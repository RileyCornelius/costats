using System.Net;
using System.Net.Http.Headers;
using System.Text;
using costats.Infrastructure.Providers;
using Xunit;

namespace costats.Tests.Providers;

public sealed class ClaudeOAuthUsageFetcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 17, 1, 0, 0, TimeSpan.Zero);
    private const string UsageJson = """
        {
          "five_hour": { "utilization": 12.5, "resets_at": "2026-07-17T05:00:00Z" },
          "seven_day": { "utilization": 34.5, "resets_at": "2026-07-20T00:00:00Z" }
        }
        """;

    [Fact]
    public void CreateRefreshStartInfo_UsesLockSafeClaudeStartup_WithoutSecretArguments()
    {
        var startInfo = ClaudeCliTokenRefresher.CreateRefreshStartInfo(
            @"C:\Users\test\.local\bin\claude.exe",
            @"C:\Users\test\Claude Profile");

        Assert.Equal(["--print", "--no-session-persistence", "--tools", "", "/usage"], startInfo.ArgumentList);
        Assert.Equal(@"C:\Users\test\Claude Profile", startInfo.Environment["CLAUDE_CONFIG_DIR"]);
        Assert.DoesNotContain("CLAUDE_CODE_OAUTH_REFRESH_TOKEN", startInfo.Environment.Keys);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
    }

    [Fact]
    public async Task FetchAsync_CurrentLoggedOutShape_DoesNotAttemptImpossibleRefresh()
    {
        var parsed = ClaudeCredentialStore.Parse("""
            { "claudeAiOauth": {
              "accessToken": "", "refreshToken": "", "expiresAt": 0,
              "refreshTokenExpiresAt": 1786067460000,
              "scopes": ["user:profile"], "subscriptionType": "pro"
            } }
            """);
        var store = new FakeCredentialStore(parsed);
        var refresher = new FakeRefresher(store, refreshed: null);
        var handler = new ScriptedHandler(_ => throw new InvalidOperationException("HTTP must not be called"));
        using var fetcher = CreateFetcher(store, refresher, handler);

        var outcome = await fetcher.FetchAsync(CancellationToken.None);

        Assert.Equal(ClaudeAuthStatus.NoCredentials, outcome.Status);
        Assert.Null(outcome.Result);
        Assert.Equal("pro", outcome.SubscriptionType);
        Assert.Equal(0, refresher.CallCount);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task FetchAsync_ExpiredAccessToken_RefreshesReloadsAndFetches()
    {
        var oldCredentials = Credentials("expired-access", "valid-refresh", Now.AddMinutes(-1));
        var newCredentials = Credentials("new-access", "rotated-refresh", Now.AddHours(8));
        var store = new FakeCredentialStore(oldCredentials);
        var refresher = new FakeRefresher(store, newCredentials);
        var handler = SuccessHandler();
        using var fetcher = CreateFetcher(store, refresher, handler);

        var outcome = await fetcher.FetchAsync(CancellationToken.None);

        Assert.Equal(ClaudeAuthStatus.Ok, outcome.Status);
        Assert.Equal(1, refresher.CallCount);
        Assert.Equal(["new-access"], handler.BearerTokens);
    }

    [Fact]
    public async Task FetchAsync_RefreshesFiveMinutesBeforeExpiry()
    {
        var store = new FakeCredentialStore(Credentials("nearly-expired", "refresh", Now.AddMinutes(4)));
        var refresher = new FakeRefresher(store, Credentials("fresh", "rotated", Now.AddHours(8)));
        var handler = SuccessHandler();
        using var fetcher = CreateFetcher(store, refresher, handler);

        var outcome = await fetcher.FetchAsync(CancellationToken.None);

        Assert.Equal(ClaudeAuthStatus.Ok, outcome.Status);
        Assert.Equal(1, refresher.CallCount);
        Assert.Equal(["fresh"], handler.BearerTokens);
    }

    [Fact]
    public async Task FetchAsync_ServerRejectsUnexpiredToken_RefreshesAndRetriesOnce()
    {
        var store = new FakeCredentialStore(Credentials("server-rejected", "refresh", Now.AddHours(8)));
        var refresher = new FakeRefresher(store, Credentials("replacement", "rotated", Now.AddHours(8)));
        var handler = new ScriptedHandler(request =>
            Bearer(request) == "server-rejected"
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : JsonResponse(HttpStatusCode.OK, UsageJson));
        using var fetcher = CreateFetcher(store, refresher, handler);

        var outcome = await fetcher.FetchAsync(CancellationToken.None);

        Assert.Equal(ClaudeAuthStatus.Ok, outcome.Status);
        Assert.Equal(1, refresher.CallCount);
        Assert.Equal(["server-rejected", "replacement"], handler.BearerTokens);
    }

    [Fact]
    public async Task FetchAsync_ReplacementIsAlsoRejected_DoesNotLoop()
    {
        var store = new FakeCredentialStore(Credentials("bad-one", "refresh", Now.AddHours(8)));
        var refresher = new FakeRefresher(store, Credentials("bad-two", "rotated", Now.AddHours(8)));
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var fetcher = CreateFetcher(store, refresher, handler);

        var outcome = await fetcher.FetchAsync(CancellationToken.None);

        Assert.Equal(ClaudeAuthStatus.Unauthorized, outcome.Status);
        Assert.Equal(1, refresher.CallCount);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task FetchAsync_ClaudeDoesNotRotateRejectedToken_DoesNotRetrySameBearer()
    {
        var unchanged = Credentials("server-rejected", "refresh", Now.AddHours(8));
        var store = new FakeCredentialStore(unchanged);
        var refresher = new FakeRefresher(store, unchanged);
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var fetcher = CreateFetcher(store, refresher, handler);

        var outcome = await fetcher.FetchAsync(CancellationToken.None);

        Assert.Equal(ClaudeAuthStatus.Unauthorized, outcome.Status);
        Assert.Equal(1, refresher.CallCount);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task FetchAsync_RefreshTokenLifetimeExpired_DoesNotLaunchClaude()
    {
        var credentials = Credentials("expired-access", "expired-refresh", Now.AddMinutes(-1)) with
        {
            RefreshTokenExpiresAt = Now.AddSeconds(-1).ToUnixTimeMilliseconds()
        };
        var store = new FakeCredentialStore(credentials);
        var refresher = new FakeRefresher(store, Credentials("unused", "unused", Now.AddHours(8)));
        var handler = new ScriptedHandler(_ => throw new InvalidOperationException("HTTP must not be called"));
        using var fetcher = CreateFetcher(store, refresher, handler);

        var outcome = await fetcher.FetchAsync(CancellationToken.None);

        Assert.Equal(ClaudeAuthStatus.Expired, outcome.Status);
        Assert.Equal(0, refresher.CallCount);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task FetchAsync_ForbiddenIsNotMisdiagnosedAsExpiredAuth()
    {
        var store = new FakeCredentialStore(Credentials("valid", "refresh", Now.AddHours(8)));
        var refresher = new FakeRefresher(store, Credentials("unused", "unused", Now.AddHours(8)));
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var fetcher = CreateFetcher(store, refresher, handler);

        var outcome = await fetcher.FetchAsync(CancellationToken.None);

        Assert.Equal(ClaudeAuthStatus.Unavailable, outcome.Status);
        Assert.Equal(0, refresher.CallCount);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task FetchAsync_ConcurrentExpiredRequests_CoalesceRefresh()
    {
        var store = new FakeCredentialStore(Credentials("expired", "refresh", Now.AddMinutes(-1)));
        var refresher = new FakeRefresher(
            store,
            Credentials("fresh", "rotated", Now.AddHours(8)),
            delay: TimeSpan.FromMilliseconds(100));
        var handler = SuccessHandler();
        using var first = CreateFetcher(store, refresher, handler);
        using var second = CreateFetcher(store, refresher, handler);

        var outcomes = await Task.WhenAll(
            first.FetchAsync(CancellationToken.None),
            second.FetchAsync(CancellationToken.None));

        Assert.All(outcomes, outcome => Assert.Equal(ClaudeAuthStatus.Ok, outcome.Status));
        Assert.Equal(1, refresher.CallCount);
        Assert.Equal(2, handler.CallCount);
    }

    private static ClaudeOAuthUsageFetcher CreateFetcher(
        FakeCredentialStore store,
        FakeRefresher refresher,
        ScriptedHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.com") };
        return new ClaudeOAuthUsageFetcher(client, store, refresher, new FixedTimeProvider(Now));
    }

    private static ScriptedHandler SuccessHandler()
        => new(_ => JsonResponse(HttpStatusCode.OK, UsageJson));

    private static ClaudeCredentials Credentials(
        string accessToken,
        string refreshToken,
        DateTimeOffset expiresAt)
        => new(
            accessToken,
            refreshToken,
            expiresAt.ToUnixTimeMilliseconds(),
            Now.AddDays(30).ToUnixTimeMilliseconds(),
            ["user:profile", "user:inference"],
            "pro",
            "default_claude_ai");

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json)
        => new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static string? Bearer(HttpRequestMessage request)
        => request.Headers.Authorization?.Scheme == "Bearer"
            ? request.Headers.Authorization.Parameter
            : null;

    private sealed class FakeCredentialStore : IClaudeCredentialStore
    {
        private ClaudeCredentials? _current;

        public FakeCredentialStore(ClaudeCredentials? current)
        {
            _current = current;
            RefreshLockKey = $"test-{Guid.NewGuid():N}";
        }

        public string RefreshLockKey { get; }

        public ClaudeCredentials? Current
        {
            get => Volatile.Read(ref _current);
            set => Volatile.Write(ref _current, value);
        }

        public Task<ClaudeCredentials?> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Current);
        }
    }

    private sealed class FakeRefresher : IClaudeTokenRefresher
    {
        private readonly FakeCredentialStore _store;
        private readonly ClaudeCredentials? _refreshed;
        private readonly TimeSpan _delay;
        private int _callCount;

        public FakeRefresher(
            FakeCredentialStore store,
            ClaudeCredentials? refreshed,
            TimeSpan delay = default)
        {
            _store = store;
            _refreshed = refreshed;
            _delay = delay;
        }

        public int CallCount => Volatile.Read(ref _callCount);

        public async Task<bool> RefreshAsync(ClaudeCredentials credentials, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            if (_delay > TimeSpan.Zero)
            {
                await Task.Delay(_delay, cancellationToken);
            }

            if (_refreshed is null)
            {
                return false;
            }

            _store.Current = _refreshed;
            return true;
        }
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        private readonly List<string?> _bearerTokens = [];
        private int _callCount;

        public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public int CallCount => Volatile.Read(ref _callCount);
        public IReadOnlyList<string?> BearerTokens => _bearerTokens;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            lock (_bearerTokens)
            {
                _bearerTokens.Add(Bearer(request));
            }

            return Task.FromResult(_responder(request));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
