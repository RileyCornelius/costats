using System.Text;
using System.Text.Json;
using costats.Infrastructure.Providers.Cursor;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace costats.Tests.Providers;

public sealed class CursorCredentialReaderTests
{
    [Fact]
    public void BuildCookieHeader_UrlEncodesTheSeparator()
    {
        var header = CursorCredentialReader.BuildCookieHeader("54592152", "token123");

        Assert.Equal("WorkosCursorSessionToken=54592152%3A%3Atoken123", header);
    }

    [Fact]
    public void NormalizeManualToken_ReturnsNull_ForBlankInput()
    {
        Assert.Null(CursorCredentialReader.NormalizeManualToken(null));
        Assert.Null(CursorCredentialReader.NormalizeManualToken("  "));
        Assert.Null(CursorCredentialReader.NormalizeManualToken("WorkosCursorSessionToken="));
    }

    [Fact]
    public void NormalizeManualToken_AcceptsBareEncodedValue()
    {
        var result = CursorCredentialReader.NormalizeManualToken("54592152%3A%3Atoken123");

        Assert.Equal("WorkosCursorSessionToken=54592152%3A%3Atoken123", result);
    }

    [Fact]
    public void NormalizeManualToken_EncodesRawSeparator()
    {
        var result = CursorCredentialReader.NormalizeManualToken("54592152::token123");

        Assert.Equal("WorkosCursorSessionToken=54592152%3A%3Atoken123", result);
    }

    [Fact]
    public void NormalizeManualToken_AcceptsNameValuePair()
    {
        var result = CursorCredentialReader.NormalizeManualToken("WorkosCursorSessionToken=54592152%3A%3Atoken123");

        Assert.Equal("WorkosCursorSessionToken=54592152%3A%3Atoken123", result);
    }

    [Fact]
    public void NormalizeManualToken_ExtractsSessionCookie_FromFullHeader()
    {
        var result = CursorCredentialReader.NormalizeManualToken(
            "Cookie: other=1; WorkosCursorSessionToken=54592152%3A%3Atoken123; trailing=2");

        Assert.Equal("WorkosCursorSessionToken=54592152%3A%3Atoken123", result);
    }

    [Fact]
    public void ReadLocalCredentials_ReadsTokenAndIdentity_FromStateDb()
    {
        var accessToken = CreateFakeJwt("github|54592152");
        var dataDir = Path.Combine(Path.GetTempPath(), "costats-tests", Path.GetRandomFileName());
        var previousOverride = Environment.GetEnvironmentVariable("CURSOR_DATA_DIR");

        try
        {
            CreateStateDb(dataDir, new Dictionary<string, string>
            {
                ["cursorAuth/accessToken"] = accessToken,
                ["cursorAuth/cachedEmail"] = "user@example.com",
                ["cursorAuth/stripeMembershipType"] = "pro",
            });
            Environment.SetEnvironmentVariable("CURSOR_DATA_DIR", dataDir);

            var reader = new CursorCredentialReader(NullLogger<CursorCredentialReader>.Instance);
            var credentials = reader.ReadLocalCredentials();

            Assert.NotNull(credentials);
            Assert.Equal(CursorCredentialReader.BuildCookieHeader("54592152", accessToken), credentials.CookieHeader);
            Assert.Equal("user@example.com", credentials.Email);
            Assert.Equal("pro", credentials.MembershipType);
            Assert.Equal("state.vscdb", credentials.Source);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CURSOR_DATA_DIR", previousOverride);
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(dataDir))
            {
                Directory.Delete(dataDir, recursive: true);
            }
        }
    }

    [Fact]
    public void ReadLocalCredentials_ReturnsNull_WhenSignedOut()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "costats-tests", Path.GetRandomFileName());
        var previousOverride = Environment.GetEnvironmentVariable("CURSOR_DATA_DIR");

        try
        {
            CreateStateDb(dataDir, new Dictionary<string, string>
            {
                ["cursorAuth/cachedEmail"] = "user@example.com",
            });
            Environment.SetEnvironmentVariable("CURSOR_DATA_DIR", dataDir);

            var reader = new CursorCredentialReader(NullLogger<CursorCredentialReader>.Instance);

            Assert.Null(reader.ReadLocalCredentials());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CURSOR_DATA_DIR", previousOverride);
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(dataDir))
            {
                Directory.Delete(dataDir, recursive: true);
            }
        }
    }

    private static void CreateStateDb(string dataDir, IReadOnlyDictionary<string, string> values)
    {
        var globalStorage = Path.Combine(dataDir, "User", "globalStorage");
        Directory.CreateDirectory(globalStorage);
        var dbPath = Path.Combine(globalStorage, "state.vscdb");

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Pooling = false
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE ItemTable (key TEXT PRIMARY KEY, value BLOB)";
            create.ExecuteNonQuery();
        }

        foreach (var (key, value) in values)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO ItemTable (key, value) VALUES (@key, @value)";
            insert.Parameters.AddWithValue("@key", key);
            insert.Parameters.AddWithValue("@value", value);
            insert.ExecuteNonQuery();
        }
    }

    private static string CreateFakeJwt(string sub)
    {
        static string Encode(object value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        var header = Encode(new { alg = "HS256", typ = "JWT" });
        var payload = Encode(new { sub, time = "1", type = "session" });
        return $"{header}.{payload}.signature";
    }
}
