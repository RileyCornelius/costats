using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace costats.Infrastructure.Providers.Cursor;

/// <summary>
/// Reads the Cursor session token from the local Cursor install (state.vscdb) and builds the
/// WorkosCursorSessionToken cookie used by cursor.com/api endpoints.
/// </summary>
public sealed class CursorCredentialReader
{
    private const string AccessTokenKey = "cursorAuth/accessToken";
    private const string CachedEmailKey = "cursorAuth/cachedEmail";
    private const string MembershipTypeKey = "cursorAuth/stripeMembershipType";
    private const string StatsigBootstrapKey = "workbench.experiments.statsigBootstrap";
    private const string SessionCookieName = "WorkosCursorSessionToken";

    private readonly ILogger<CursorCredentialReader> _logger;

    public CursorCredentialReader(ILogger<CursorCredentialReader> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Reads credentials from the local Cursor install. Returns null when Cursor is not
    /// installed, the user is signed out, or the database cannot be read.
    /// </summary>
    public CursorCredentials? ReadLocalCredentials()
    {
        var dbPath = ResolveStateDbPath();
        if (dbPath is null || !File.Exists(dbPath))
        {
            return null;
        }

        try
        {
            var values = ReadItemTableValues(dbPath, AccessTokenKey, CachedEmailKey, MembershipTypeKey, StatsigBootstrapKey);

            if (!values.TryGetValue(AccessTokenKey, out var accessToken) || string.IsNullOrWhiteSpace(accessToken))
            {
                return null;
            }

            var userId = DeriveUserId(accessToken, values.GetValueOrDefault(StatsigBootstrapKey));
            if (string.IsNullOrWhiteSpace(userId))
            {
                return null;
            }

            return new CursorCredentials(
                CookieHeader: BuildCookieHeader(userId, accessToken),
                Email: values.GetValueOrDefault(CachedEmailKey),
                MembershipType: values.GetValueOrDefault(MembershipTypeKey),
                Source: "state.vscdb");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cursor state.vscdb read failed");
            return null;
        }
    }

    public static string BuildCookieHeader(string userId, string accessToken)
        => $"{SessionCookieName}={Uri.EscapeDataString($"{userId}::{accessToken}")}";

    /// <summary>
    /// Normalizes a manually pasted value — a bare cookie value, a name=value pair, or a full
    /// "Cookie:" header — into the cookie header form. Returns null for blank input.
    /// </summary>
    public static string? NormalizeManualToken(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var value = input.Trim();

        if (value.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
        {
            value = value["Cookie:".Length..].Trim();
        }

        // A full cookie header may contain several pairs; keep only the session cookie.
        if (value.Contains(';'))
        {
            var sessionPair = value
                .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(pair => pair.StartsWith(SessionCookieName + "=", StringComparison.OrdinalIgnoreCase));
            if (sessionPair is not null)
            {
                value = sessionPair;
            }
        }

        if (value.StartsWith(SessionCookieName + "=", StringComparison.OrdinalIgnoreCase))
        {
            value = value[(SessionCookieName.Length + 1)..].Trim();
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // Bare values copied from DevTools are usually already URL-encoded ("%3A%3A"); raw
        // "userId::token" values are not. Encode only when the raw separator is present.
        if (value.Contains("::", StringComparison.Ordinal))
        {
            value = Uri.EscapeDataString(value);
        }

        return $"{SessionCookieName}={value}";
    }

    /// <summary>
    /// Resolves the state.vscdb path: CURSOR_DATA_DIR override, then %APPDATA%\Cursor.
    /// </summary>
    private static string? ResolveStateDbPath()
    {
        var overrideDir = Environment.GetEnvironmentVariable("CURSOR_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            return Path.Combine(overrideDir, "User", "globalStorage", "state.vscdb");
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appData))
        {
            return null;
        }

        return Path.Combine(appData, "Cursor", "User", "globalStorage", "state.vscdb");
    }

    private Dictionary<string, string> ReadItemTableValues(string dbPath, params string[] keys)
    {
        try
        {
            return QueryItemTable(dbPath, keys);
        }
        catch (SqliteException ex)
        {
            // Cursor may hold a lock on the live database; retry against a temp copy.
            _logger.LogDebug(ex, "Direct read-only open of state.vscdb failed; retrying with a temp copy");
            return ReadFromTempCopy(dbPath, keys);
        }
    }

    private static Dictionary<string, string> ReadFromTempCopy(string dbPath, string[] keys)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "costats", "cursor");
        Directory.CreateDirectory(tempDir);
        var tempDb = Path.Combine(tempDir, "state.vscdb");

        try
        {
            File.Copy(dbPath, tempDb, overwrite: true);
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                var sidecar = dbPath + suffix;
                var tempSidecar = tempDb + suffix;
                if (File.Exists(sidecar))
                {
                    File.Copy(sidecar, tempSidecar, overwrite: true);
                }
                else if (File.Exists(tempSidecar))
                {
                    File.Delete(tempSidecar);
                }
            }

            return QueryItemTable(tempDb, keys);
        }
        finally
        {
            TryDeleteTempFiles(tempDb);
        }
    }

    private static Dictionary<string, string> QueryItemTable(string dbPath, string[] keys)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        var parameterNames = keys.Select((_, i) => $"@k{i}").ToArray();
        command.CommandText = $"SELECT key, value FROM ItemTable WHERE key IN ({string.Join(", ", parameterNames)})";
        for (var i = 0; i < keys.Length; i++)
        {
            command.Parameters.AddWithValue(parameterNames[i], keys[i]);
        }

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(1))
            {
                values[reader.GetString(0)] = reader.GetString(1);
            }
        }

        return values;
    }

    /// <summary>
    /// Derives the user id from the access token's JWT "sub" claim (e.g. "github|54592152" →
    /// "54592152"), falling back to statsigBootstrap's user.userID. The API validates the token
    /// part of the cookie, so an imprecise user id does not break usage calls.
    /// </summary>
    internal static string? DeriveUserId(string accessToken, string? statsigBootstrapJson)
    {
        var sub = ReadJwtSubClaim(accessToken);
        if (!string.IsNullOrWhiteSpace(sub))
        {
            var separatorIndex = sub.LastIndexOf('|');
            return separatorIndex >= 0 ? sub[(separatorIndex + 1)..] : sub;
        }

        if (string.IsNullOrWhiteSpace(statsigBootstrapJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(statsigBootstrapJson);
            if (doc.RootElement.TryGetProperty("user", out var user)
                && user.ValueKind == JsonValueKind.Object
                && user.TryGetProperty("userID", out var userId)
                && userId.ValueKind == JsonValueKind.String)
            {
                var value = userId.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    var separatorIndex = value.LastIndexOf('|');
                    return separatorIndex >= 0 ? value[(separatorIndex + 1)..] : value;
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    internal static string? ReadJwtSubClaim(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload += (payload.Length % 4) switch
            {
                2 => "==",
                3 => "=",
                _ => string.Empty
            };

            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.TryGetProperty("sub", out var sub) && sub.ValueKind == JsonValueKind.String
                ? sub.GetString()
                : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    private static void TryDeleteTempFiles(string tempDb)
    {
        foreach (var path in new[] { tempDb, tempDb + "-wal", tempDb + "-shm" })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

public sealed record CursorCredentials(
    string CookieHeader,
    string? Email,
    string? MembershipType,
    string Source);
