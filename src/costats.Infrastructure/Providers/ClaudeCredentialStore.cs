using System.Text.Json;

namespace costats.Infrastructure.Providers;

internal interface IClaudeCredentialStore
{
    string RefreshLockKey { get; }

    Task<ClaudeCredentials?> LoadAsync(CancellationToken cancellationToken);
}

internal sealed class ClaudeCredentialStore : IClaudeCredentialStore
{
    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(50);

    private readonly string _credentialsPath;

    public ClaudeCredentialStore(string? configDir)
    {
        configDir = ResolveConfigDir(configDir);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _credentialsPath = configDir is null
            ? Path.Combine(home, ".claude", ".credentials.json")
            : Path.Combine(configDir, ".credentials.json");
    }

    public string RefreshLockKey => Path.GetFullPath(_credentialsPath);

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
            ReadNonEmptyString(oauth, "rateLimitTier"));
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
    string? RateLimitTier);
