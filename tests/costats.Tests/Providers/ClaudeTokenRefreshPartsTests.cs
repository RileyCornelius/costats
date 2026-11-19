using System.Net;
using System.Text.Json.Nodes;
using costats.Infrastructure.Providers;
using Xunit;

namespace costats.Tests.Providers;

/// <summary>
/// Covers the pieces that touch Claude Code's shared state: the credential file rewrite,
/// the token response parsing, and the cross-process lock directories.
/// </summary>
public sealed class ClaudeTokenRefreshPartsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 2, 0, 0, TimeSpan.Zero);

    private static readonly ClaudeRefreshedTokens Rotated = new(
        "new-access", "new-refresh", 1_800_000_000_000, 1_900_000_000_000, ["user:profile", "user:inference"]);

    [Fact]
    public void ApplyRefresh_UpdatesTokensAndPreservesEverythingElse()
    {
        const string json = """
            {
              "claudeAiOauth": {
                "accessToken": "old-access", "refreshToken": "old-refresh", "expiresAt": 1,
                "scopes": ["user:profile"], "subscriptionType": "pro", "rateLimitTier": "default"
              },
              "mcpOAuth": { "server": { "token": "keep-me" } }
            }
            """;

        var updated = JsonNode.Parse(ClaudeCredentialStore.ApplyRefresh(json, "old-refresh", Rotated)!)!;

        var oauth = updated["claudeAiOauth"]!;
        Assert.Equal("new-access", oauth["accessToken"]!.GetValue<string>());
        Assert.Equal("new-refresh", oauth["refreshToken"]!.GetValue<string>());
        Assert.Equal(1_800_000_000_000, oauth["expiresAt"]!.GetValue<long>());
        Assert.Equal(1_900_000_000_000, oauth["refreshTokenExpiresAt"]!.GetValue<long>());
        Assert.Equal(2, oauth["scopes"]!.AsArray().Count);
        Assert.Equal("pro", oauth["subscriptionType"]!.GetValue<string>());
        Assert.Equal("default", oauth["rateLimitTier"]!.GetValue<string>());
        Assert.Equal("keep-me", updated["mcpOAuth"]!["server"]!["token"]!.GetValue<string>());
    }

    [Fact]
    public void ApplyRefresh_StoredTokenChanged_RefusesToOverwrite()
    {
        const string json = """{ "claudeAiOauth": { "accessToken": "a", "refreshToken": "someone-elses" } }""";

        Assert.Null(ClaudeCredentialStore.ApplyRefresh(json, "old-refresh", Rotated));
    }

    [Fact]
    public async Task SaveRefreshedAsync_WritesFileAndLeavesNoTempFiles()
    {
        var dir = Directory.CreateTempSubdirectory("costats-claude-store-").FullName;
        try
        {
            var path = Path.Combine(dir, ".credentials.json");
            await File.WriteAllTextAsync(path, """{ "claudeAiOauth": { "accessToken": "a", "refreshToken": "old-refresh" } }""");
            var store = new ClaudeCredentialStore(dir);

            var saved = await store.SaveRefreshedAsync("old-refresh", Rotated, CancellationToken.None);
            var reloaded = await store.LoadAsync(CancellationToken.None);

            Assert.True(saved);
            Assert.Equal("new-access", reloaded!.AccessToken);
            Assert.Equal("new-refresh", reloaded.RefreshToken);
            Assert.Single(Directory.GetFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ParseTokens_ComputesAbsoluteExpiries()
    {
        const string json = """
            { "access_token": "acc", "refresh_token": "ref", "expires_in": 28800,
              "refresh_token_expires_in": 2592000, "scope": "user:profile user:inference" }
            """;

        var tokens = ClaudeTokenEndpoint.ParseTokens(json, Previous(), Now)!;

        Assert.Equal("acc", tokens.AccessToken);
        Assert.Equal("ref", tokens.RefreshToken);
        Assert.Equal(Now.AddHours(8).ToUnixTimeMilliseconds(), tokens.ExpiresAt);
        Assert.Equal(Now.AddDays(30).ToUnixTimeMilliseconds(), tokens.RefreshTokenExpiresAt);
        Assert.Equal(["user:profile", "user:inference"], tokens.Scopes);
    }

    [Fact]
    public void ParseTokens_UnrotatedRefreshToken_KeepsPreviousOne()
    {
        var tokens = ClaudeTokenEndpoint.ParseTokens("""{ "access_token": "acc", "expires_in": 60 }""", Previous(), Now)!;

        Assert.Equal("previous-refresh", tokens.RefreshToken);
        Assert.Null(tokens.RefreshTokenExpiresAt);
        Assert.Null(tokens.Scopes);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "access_token": "acc" }""")]
    [InlineData("not json")]
    public void ParseTokens_IncompleteResponse_ReturnsNull(string json)
    {
        Assert.Null(ClaudeTokenEndpoint.ParseTokens(json, Previous(), Now));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{ "error": "invalid_grant" }""", true)]
    [InlineData(HttpStatusCode.BadRequest, """{ "error": "invalid_scope" }""", false)]
    [InlineData(HttpStatusCode.InternalServerError, """{ "error": "invalid_grant" }""", false)]
    [InlineData(HttpStatusCode.BadRequest, "<html>", false)]
    public void IsDeadGrant_OnlyForInvalidGrant(HttpStatusCode status, string body, bool expected)
    {
        Assert.Equal(expected, ClaudeTokenEndpoint.IsDeadGrant(status, body));
    }

    [Fact]
    public void TryCreateLockDirectory_IsExclusiveUntilReleased()
    {
        var dir = Directory.CreateTempSubdirectory("costats-claude-lock-").FullName;
        try
        {
            var lockPath = Path.Combine(dir, ".oauth_refresh.lock");

            Assert.True(ClaudeRefreshLock.TryCreateLockDirectory(lockPath));
            Assert.False(ClaudeRefreshLock.TryCreateLockDirectory(lockPath));

            Directory.Delete(lockPath);
            Assert.True(ClaudeRefreshLock.TryCreateLockDirectory(lockPath));
            Assert.Single(Directory.GetDirectories(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TryCreateLockDirectory_TakesOverStaleLock()
    {
        var dir = Directory.CreateTempSubdirectory("costats-claude-lock-").FullName;
        try
        {
            var lockPath = Path.Combine(dir, ".oauth_refresh.lock");
            Directory.CreateDirectory(lockPath);
            Directory.SetLastWriteTimeUtc(lockPath, DateTime.UtcNow.AddMinutes(-2));

            Assert.True(ClaudeRefreshLock.TryCreateLockDirectory(lockPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TryAcquireAsync_TakesBothClaudeCodeLocksAndReleasesThem()
    {
        var root = Directory.CreateTempSubdirectory("costats-claude-cfg-").FullName;
        var configDir = Path.Combine(root, ".claude");
        Directory.CreateDirectory(configDir);
        try
        {
            var refreshLock = new ClaudeRefreshLock();

            await using (var held = await refreshLock.TryAcquireAsync(configDir, CancellationToken.None))
            {
                Assert.NotNull(held);
                Assert.True(Directory.Exists(Path.Combine(configDir, ".oauth_refresh.lock")));
                Assert.True(Directory.Exists(configDir + ".lock"));
            }

            Assert.False(Directory.Exists(Path.Combine(configDir, ".oauth_refresh.lock")));
            Assert.False(Directory.Exists(configDir + ".lock"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ClaudeCredentials Previous()
        => new("previous-access", "previous-refresh", null, null, ["user:profile"], "pro", null);
}
