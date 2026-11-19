using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace costats.Infrastructure.Providers;

internal interface IClaudeTokenEndpoint
{
    Task<ClaudeTokenRefreshResult> RefreshAsync(ClaudeCredentials credentials, CancellationToken cancellationToken);
}

internal enum ClaudeTokenRefreshOutcome
{
    Refreshed,

    /// <summary>The server rejected the refresh token (invalid_grant); only /login recovers.</summary>
    DeadGrant,

    /// <summary>Network or server trouble; the refresh token may still be good.</summary>
    Transient
}

internal sealed record ClaudeTokenRefreshResult(ClaudeTokenRefreshOutcome Outcome, ClaudeRefreshedTokens? Tokens = null);

/// <summary>
/// Exchanges a refresh token at Claude Code's OAuth token endpoint, sending the same request
/// Claude Code sends (JSON body with grant type, refresh token, client id and scopes).
/// </summary>
internal sealed class ClaudeTokenEndpoint : IClaudeTokenEndpoint
{
    internal const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
    internal const string ClaudeCodeClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;

    public ClaudeTokenEndpoint(HttpClient httpClient, TimeProvider timeProvider)
    {
        _httpClient = httpClient;
        _timeProvider = timeProvider;
    }

    public async Task<ClaudeTokenRefreshResult> RefreshAsync(ClaudeCredentials credentials, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = credentials.RefreshToken!,
            ["client_id"] = credentials.ClientId ?? ClaudeCodeClientId
        };
        if (credentials.Scopes.Count > 0)
        {
            body["scope"] = string.Join(' ', credentials.Scopes);
        }

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(TokenUrl, body, cancellationToken).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var tokens = ParseTokens(content, credentials, _timeProvider.GetUtcNow());
                return tokens is null
                    ? new ClaudeTokenRefreshResult(ClaudeTokenRefreshOutcome.Transient)
                    : new ClaudeTokenRefreshResult(ClaudeTokenRefreshOutcome.Refreshed, tokens);
            }

            return IsDeadGrant(response.StatusCode, content)
                ? new ClaudeTokenRefreshResult(ClaudeTokenRefreshOutcome.DeadGrant)
                : new ClaudeTokenRefreshResult(ClaudeTokenRefreshOutcome.Transient);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new ClaudeTokenRefreshResult(ClaudeTokenRefreshOutcome.Transient);
        }
    }

    internal static ClaudeRefreshedTokens? ParseTokens(string json, ClaudeCredentials previous, DateTimeOffset now)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!TryGetString(root, "access_token", out var accessToken)
                || !root.TryGetProperty("expires_in", out var expiresIn)
                || !expiresIn.TryGetInt64(out var expiresInSeconds))
            {
                return null;
            }

            // The server may keep the refresh token unrotated; Claude Code then reuses it.
            var refreshToken = TryGetString(root, "refresh_token", out var rotated) ? rotated : previous.RefreshToken!;

            long? refreshExpiresAt = root.TryGetProperty("refresh_token_expires_in", out var refreshExpiresIn)
                                     && refreshExpiresIn.TryGetInt64(out var refreshSeconds)
                ? now.AddSeconds(refreshSeconds).ToUnixTimeMilliseconds()
                : null;

            IReadOnlyList<string>? scopes = TryGetString(root, "scope", out var scopeText)
                ? scopeText.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                : null;

            return new ClaudeRefreshedTokens(
                accessToken,
                refreshToken,
                now.AddSeconds(expiresInSeconds).ToUnixTimeMilliseconds(),
                refreshExpiresAt,
                scopes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static bool IsDeadGrant(HttpStatusCode status, string content)
    {
        if (status is not (HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(content);
            return TryGetString(doc.RootElement, "error", out var error) && error == "invalid_grant";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            return false;
        }

        value = property.GetString()!;
        return true;
    }
}
