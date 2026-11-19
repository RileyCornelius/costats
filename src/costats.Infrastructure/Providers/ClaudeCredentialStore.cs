using System.Text.Json;
using System.Text.Json.Nodes;

namespace costats.Infrastructure.Providers;

internal interface IClaudeCredentialStore
{
    /// <summary>Claude Code's config directory, which also holds its refresh locks.</summary>
    string ConfigDirectory { get; }

    Task<ClaudeCredentials?> LoadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Writes a rotated token pair, but only if the file still holds
    /// <paramref name="postedRefreshToken"/> (compare-and-swap, as Claude Code does).
    /// Returns false when another writer got there first.
    /// </summary>
    Task<bool> SaveRefreshedAsync(
        string postedRefreshToken,
        ClaudeRefreshedTokens tokens,
        CancellationToken cancellationToken);
}

/// <summary>
/// Claude Code's credential file (<c>~/.claude/.credentials.json</c> on Windows and Linux).
/// Costats writes it only to persist a pair it rotated while holding Claude Code's refresh
/// locks, and preserves every field it does not own.
/// </summary>
internal sealed class ClaudeCredentialStore : IClaudeCredentialStore
{
    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(50);

    private readonly string _credentialsPath;

    public ClaudeCredentialStore(string? configDir)
    {
        configDir = ResolveConfigDir(configDir);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        ConfigDirectory = configDir ?? Path.Combine(home, ".claude");
        _credentialsPath = Path.Combine(ConfigDirectory, ".credentials.json");
    }

    public string ConfigDirectory { get; }

    internal static string? ResolveConfigDir(string? configDir)
    {
        if (!string.IsNullOrWhiteSpace(configDir))
        {
            return configDir;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        return string.IsNullOrWhiteSpace(fromEnvironment) ? null : fromEnvironment;
    }

    public async Task<ClaudeCredentials?> LoadAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (!File.Exists(_credentialsPath))
                {
                    return null;
                }

                var json = await File.ReadAllTextAsync(_credentialsPath, cancellationToken).ConfigureAwait(false);
                return Parse(json);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (IOException) when (attempt < 2)
            {
                await Task.Delay(ReadRetryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException) when (attempt < 2)
            {
                // Claude may be between replace/write operations. A short bounded retry
                // avoids briefly presenting an active login as missing credentials.
                await Task.Delay(ReadRetryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    public async Task<bool> SaveRefreshedAsync(
        string postedRefreshToken,
        ClaudeRefreshedTokens tokens,
        CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(_credentialsPath, cancellationToken).ConfigureAwait(false);
        var updated = ApplyRefresh(json, postedRefreshToken, tokens);
        if (updated is null)
        {
            return false;
        }

        // Write a sibling temp file and swap it in so readers never see a partial file. The
        // server has already rotated the pair, so losing this write means a forced /login:
        // retry through transient sharing violations, then fall back to an in-place write.
        var tempPath = $"{_credentialsPath}.costats-{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(tempPath, updated, CancellationToken.None).ConfigureAwait(false);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tempPath, _credentialsPath, overwrite: true);
                    return true;
                }
                catch (IOException) when (attempt < SaveRetryCount)
                {
                    await Task.Delay(SaveRetryDelay, CancellationToken.None).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException) when (attempt < SaveRetryCount)
                {
                    await Task.Delay(SaveRetryDelay, CancellationToken.None).ConfigureAwait(false);
                }
                catch when (attempt >= SaveRetryCount)
                {
                    await File.WriteAllTextAsync(_credentialsPath, updated, CancellationToken.None).ConfigureAwait(false);
                    return true;
                }
            }
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* best effort */ }
        }
    }

    private const int SaveRetryCount = 20;
    private static readonly TimeSpan SaveRetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Returns the file content with the rotated pair applied, or null when the stored
    /// refresh token no longer matches the one that was exchanged.
    /// </summary>
    internal static string? ApplyRefresh(string json, string postedRefreshToken, ClaudeRefreshedTokens tokens)
    {
        var root = JsonNode.Parse(json)?.AsObject();
        if (root?["claudeAiOauth"] is not JsonObject oauth
            || oauth["refreshToken"]?.GetValueKind() != JsonValueKind.String
            || oauth["refreshToken"]!.GetValue<string>() != postedRefreshToken)
        {
            return null;
        }

        oauth["accessToken"] = tokens.AccessToken;
        oauth["refreshToken"] = tokens.RefreshToken;
        oauth["expiresAt"] = tokens.ExpiresAt;
        if (tokens.RefreshTokenExpiresAt is { } refreshExpiresAt)
        {
            oauth["refreshTokenExpiresAt"] = refreshExpiresAt;
        }

        if (tokens.Scopes is { Count: > 0 } scopes)
        {
            oauth["scopes"] = new JsonArray(scopes.Select(scope => (JsonNode?)JsonValue.Create(scope)).ToArray());
        }

        return root.ToJsonString();
    }

    internal static ClaudeCredentials? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth))
        {
            return null;
        }

        var scopes = Array.Empty<string>();
        if (oauth.TryGetProperty("scopes", out var scopesElement)
            && scopesElement.ValueKind == JsonValueKind.Array)
        {
            scopes = scopesElement.EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.String)
                .Select(element => element.GetString())
                .Where(scope => !string.IsNullOrWhiteSpace(scope))
                .Cast<string>()
                .ToArray();
        }

        return new ClaudeCredentials(
            ReadNonEmptyString(oauth, "accessToken"),
            ReadNonEmptyString(oauth, "refreshToken"),
            ReadInt64(oauth, "expiresAt"),
            ReadInt64(oauth, "refreshTokenExpiresAt"),
            scopes,
            ReadNonEmptyString(oauth, "subscriptionType"),
            ReadNonEmptyString(oauth, "rateLimitTier"),
            ReadNonEmptyString(oauth, "clientId"),
            ReadNonEmptyString(oauth, "loginEnded"));
    }

    private static string? ReadNonEmptyString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static long? ReadInt64(JsonElement element, string name)
        => element.TryGetProperty(name, out var property)
           && property.ValueKind == JsonValueKind.Number
           && property.TryGetInt64(out var value)
            ? value
            : null;
}

internal sealed record ClaudeCredentials(
    string? AccessToken,
    string? RefreshToken,
    long? ExpiresAt,
    long? RefreshTokenExpiresAt,
    IReadOnlyList<string> Scopes,
    string? SubscriptionType,
    string? RateLimitTier,
    string? ClientId = null,
    // Set by Claude Code when it clears a login the server rejected; "time_limit" means the
    // server reported the refresh token itself as expired.
    string? LoginEnded = null);

/// <summary>A rotated token pair from the OAuth token endpoint, with absolute expiries.</summary>
internal sealed record ClaudeRefreshedTokens(
    string AccessToken,
    string RefreshToken,
    long ExpiresAt,
    long? RefreshTokenExpiresAt,
    IReadOnlyList<string>? Scopes);
