using System.Net;
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
    public async Task FetchAsync_CurrentLoggedOutShape_ReportsNoCredentials()
    {
        var parsed = ClaudeCredentialStore.Parse("""
            { "claudeAiOauth": {
              "accessToken": "", "refreshToken": "", "expiresAt": 0,
              "refreshTokenExpiresAt": 1786067460000,
              "scopes": ["user:profile"], "subscriptionType": "pro",
              "loginEnded": "time_limit"
            } }
            """);
        Assert.Equal("time_limit", parsed!.LoginEnded);
        var store = new FakeCredentialStore(parsed);
        var endpoint = new FakeTokenEndpoint();
        var handler = new ScriptedHandler(_ => throw new InvalidOperationException("HTTP must not be called"));
        using var fetcher = CreateFetcher(store, handler, endpoint);

        var outcome = await fetcher.FetchAsync(CancellationToken.None);

        Assert.Equal(ClaudeAuthStatus.NoCredentials, outcome.Status);
        Assert.Null(outcome.Result);
        Assert.Equal("pro", outcome.SubscriptionType);
        Assert.Equal(0, handler.CallCount);
        Assert.Equal(0, endpoint.CallCount);
    }

    [Fact]
    public async Task FetchAsync_ValidToken_DoesNotRefresh()
    {
        var store = new FakeCredentialStore(Credentials("live", "refresh", Now.AddHours(8)));
        var endpoint = new FakeTokenEndpoint();
        var handler = SuccessHandler();
        using var fetcher = CreateFetcher(store, handler, endpoint);

        var outcome = await fetcher.FetchAsync(CancellationToken.None);

        Assert.Equal(ClaudeAuthStatus.Ok, outcome.Status);
        Assert.Equal(12.5, outcome.Result!.FiveHourUsedPercent);
        Assert.Equal(0, endpoint.CallCount);
    }

    [Fact]
    public async Task FetchAsync_ExpiredToken_RefreshesUnderLockSavesAndFetches()
    {
        var store = new FakeCredentialStore(Credentials("expired-access", "old-refresh", Now.AddMinutes(-1)));
        var refreshLock = new FakeRefreshLock();
        var endpoint = new FakeTokenEndpoint(Tokens("new-access", "new-refresh"));
        var handler = SuccessHandler();
        using var fetcher = CreateFetcher(store, handler, endpoint, refreshLock);

        var outcome = await fetcher.FetchAsync(CancellationToken.None);

        Assert.Equal(ClaudeAuthStatus.Ok, outcome.Status);
        Assert.Equal(1, endpoint.CallCount);
        Assert.Equal(1, refreshLock.AcquireCount);
        Assert.True(endpoint.LockHeldDuringCall);
        Assert.Equal("new-refresh", store.Current!.RefreshToken);
        Assert.Equal(["new-access"], handler.BearerTokens);
    }

    [Fact]
    public async Task FetchAsync_RefreshesWithinFiveMinutesOfExpiry()
    {
        var store = new FakeCredentialStore(Credentials("nearly-expired", "refresh", Now.AddMinutes(4)));
        var endpoint = new FakeTokenEndpoint(Tokens("fresh", "rotated"));
        var handler = SuccessHandler();
        using var fetcher = CreateFetcher(store, handler, endpoint);

        var outcome = await fetcher.FetchAsync(CancellationToken.None);

        Assert.Equal(ClaudeAuthStatus.Ok, outcome.Status);
        Assert.Equal(1, endpoint.CallCount);
        Assert.Equal(["fresh"], handler.BearerTokens);
    }

    [Fact]
    public async Task FetchAsync_ClaudeCodeRotatedBeforeLock_AdoptsPairWithoutExchanging()
    {
        var store = new FakeCredentialStore(Credentials("expired-access", "old-refresh", Now.AddMinutes(-1)));
        var refreshLock = new FakeRefreshLock
        {
            // Claude Code finishes its own refresh just before releasing the lock to us.
            OnAcquire = () => store.Current = Credentials("claude-code-access", "claude-code-refresh", Now.AddHours(8))
        };
        var endpoint = new FakeTokenEndpoint(Tokens("unused", "unused"));
        var handler = SuccessHandler();
        using var fetcher = CreateFetcher(store, handler, endpoint, refreshLock);

        var outcome = await fetcher.FetchAsync(CancellationToken.None);

        Assert.Equal(ClaudeAuthStatus.Ok, outcome.Status);
        Assert.Equal(0, endpoint.CallCount);
        Assert.Equal(["claude-code-access"], handler.BearerTokens);
    }

    [Fact]
    public async Task FetchAsync_LockBusy_DoesNotExchangeAndReportsTransient()
    {
        var store = new FakeCredentialStore(Credentials("expired-access", "refresh", Now.AddMinutes(-1)));
        var refreshLock = new FakeRefreshLock { Busy = true };
        var endpoint = new FakeTokenEndpoint(Tokens("unused", "unused"));
        var handler = new ScriptedHandler(_ => throw new InvalidOperationException("HTTP must not be called"));
        using var fetcher = CreateFetcher(store, handler, endpoint, refreshLock);

        var outcome = await fetcher.FetchAsync(CancellationToken.None);

        Assert.Equal(ClaudeAuthStatus.Unavailable, outcome.Status);
        Assert.False(ClaudeIdentity.IsSignInRequired(outcome.Status));
        Assert.Equal(0, endpoint.CallCount);
    }

    [Fact]
    public async Task FetchAsync_DeadGrant_ReportsExpiredAndNeverRetriesThatToken()
    {
        var store = new FakeCredentialStore(Credentials("expired-access", "dead-refresh", Now.AddMinutes(-1)));
        var endpoint = new FakeTokenEndpoint(new ClaudeTokenRefreshResult(ClaudeTokenRefreshOutcome.DeadGrant));
        var handler = new ScriptedHandler(_ => throw new InvalidOperationException("HTTP must not be called"));
        using var fetcher = CreateFetcher(store, handler, endpoint);

        var first = await fetcher.FetchAsync(CancellationToken.None);
        var second = await fetcher.FetchAsync(CancellationToken.None);

        Assert.Equal(ClaudeAuthStatus.Expired, first.Status);
        Assert.Equal(ClaudeAuthStatus.Expired, second.Status);
        Assert.Equal(1, endpoint.CallCount);
    }

    [Fact]
    public async Task FetchAsync_NewLoginAfterDeadGrant_IsUsed()
    {
        var store = new FakeCredentialStore(Credentials("expired-access", "dead-refresh", Now.AddMinutes(-1)));
        var endpoint = new FakeTokenEndpoint(new ClaudeTokenRefreshResult(ClaudeTokenRefreshOutcome.DeadGrant));
        var handler = SuccessHandler();
        using var fetcher = CreateFetcher(store, handler, endpoint);

        await fetcher.FetchAsync(CancellationToken.None);
        store.Current = Credentials("after-login", "new-refresh", Now.AddHours(8));
        var outcome = await fetcher.FetchAsync(CancellationToken.None);

        Assert.Equal(ClaudeAuthStatus.Ok, outcome.Status);
        Assert.Equal(["after-login"], handler.BearerTokens);
    }

    [Fact]
    public async Task FetchAsync_TransientRefreshFailure_IsNotASignInProblem()
    {
        var store = new FakeCredentialStore(Credentials("expired-access", "refresh", Now.AddMinutes(-1)));
        var endpoint = new FakeTokenEndpoint(new ClaudeTokenRefreshResult(ClaudeTokenRefreshOutcome.Transient));
        var handler = new ScriptedHandler(_ => throw new InvalidOperationException("HTTP must not be called"));
        using var fetcher = CreateFetcher(store, handler, endpoint);

        var outcome = await fetcher.FetchAsync(CancellationToken.None);

        Assert.Equal(ClaudeAuthStatus.Unavailable, outcome.Status);
        Assert.Equal("refresh", store.Current!.RefreshToken);
    }

    [Fact]
    public async Task FetchAsync_SaveRejectedByCompareAndSwap_AdoptsPairOnDisk()
    {
        var store = new FakeCredentialStore(Credentials("expired-access", "old-refresh", Now.AddMinutes(-1)))
        {
            // Another writer replaced the file between the exchange and our save.
            OnSave = s => s.Current = Credentials("other-writer", "other-refresh", Now.AddHours(8))
        };
        var endpoint = new FakeTokenEndpoint(Tokens("ours", "ours-refresh"));
        var handler = SuccessHandler();
        using var fetcher = CreateFetcher(store, handler, endpoint);

        var outcome = await fetcher.FetchAsync(CancellationToken.None);

        Assert.Equal(ClaudeAuthStatus.Ok, outcome.Status);
        Assert.Equal("other-refresh", store.Current!.RefreshToken);
        Assert.Equal(["other-writer"], handler.BearerTokens);
    }

    [Fact]
    public async Task FetchAsync_TokenRotatedDuringRequest_RereadsFileAndRetriesOnce()
    {
        var store = new FakeCredentialStore(Credentials("superseded", "refresh", Now.AddHours(8)));
        var endpoint = new FakeTokenEndpoint();
        var handler = new ScriptedHandler(request =>
        {
            if (Bearer(request) != "superseded")
            {
                return JsonResponse(HttpStatusCode.OK, UsageJson);
            }

            store.Current = Credentials("replacement", "rotated", Now.AddHours(8));
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });
        using var fetcher = CreateFetcher(store, handler, endpoint);

        var outcome = await fetcher.FetchAsync(CancellationToken.None);

        Assert.Equal(ClaudeAuthStatus.Ok, outcome.Status);
        Assert.Equal(["superseded", "replacement"], handler.BearerTokens);
        Assert.Equal(0, endpoint.CallCount);
    }

    [Fact]
    public async Task FetchAsync_RevokedUnexpiredToken_ForcesOneRefreshAndRetries()
    {
        var store = new FakeCredentialStore(Credentials("revoked", "refresh", Now.AddHours(8)));
        var endpoint = new FakeTokenEndpoint(Tokens("replacement", "rotated"));
        var handler = new ScriptedHandler(request =>
            Bearer(request) == "revoked"
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : JsonResponse(HttpStatusCode.OK, UsageJson));
        using var fetcher = CreateFetcher(store, handler, endpoint);

        var outcome = await fetcher.FetchAsync(CancellationToken.None);

        Assert.Equal(ClaudeAuthStatus.Ok, outcome.Status);
        Assert.Equal(1, endpoint.CallCount);
        Assert.Equal(["revoked", "replacement"], handler.BearerTokens);
    }

    [Fact]
    public async Task FetchAsync_RefreshDisabled_ExpiredTokenIsTransientAndNeverExchanged()
    {
        var store = new FakeCredentialStore(Credentials("expired-access", "refresh", Now.AddMinutes(-1)));
        var refreshLock = new FakeRefreshLock();
        var endpoint = new FakeTokenEndpoint(Tokens("new-access", "new-refresh"));
        var handler = SuccessHandler();
        using var fetcher = CreateFetcher(store, handler, endpoint, refreshLock, allowRefresh: false);

        var outcome = await fetcher.FetchAsync(CancellationToken.None);

        Assert.Equal(ClaudeAuthStatus.Unavailable, outcome.Status);
        Assert.Equal(0, endpoint.CallCount);
        Assert.Equal(0, refreshLock.AcquireCount);
        Assert.Equal(0, handler.CallCount);
        Assert.Equal("refresh", store.Current!.RefreshToken);
    }

    [Fact]
    public async Task FetchAsync_RefreshDisabled_RejectedTokenAdoptsRotationByClaudeCode()
    {
        var store = new FakeCredentialStore(Credentials("stale", "refresh", Now.AddHours(8)));
        var endpoint = new FakeTokenEndpoint(Tokens("never", "never"));
        var handler = new ScriptedHandler(request =>
        {
            if (Bearer(request) == "stale")
            {
                store.Current = Credentials("rotated-by-cli", "rotated", Now.AddHours(8));
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            return JsonResponse(HttpStatusCode.OK, UsageJson);
        });
        using var fetcher = CreateFetcher(store, handler, endpoint, allowRefresh: false);

        var outcome = await fetcher.FetchAsync(CancellationToken.None);

        Assert.Equal(ClaudeAuthStatus.Ok, outcome.Status);
        Assert.Equal(0, endpoint.CallCount);
        Assert.Equal(["stale", "rotated-by-cli"], handler.BearerTokens);
    }

    [Fact]
    public async Task FetchAsync_RefreshDisabled_RejectedUnchangedTokenReportsUnauthorized()
    {
        var store = new FakeCredentialStore(Credentials("revoked", "refresh", Now.AddHours(8)));
        var endpoint = new FakeTokenEndpoint(Tokens("never", "never"));
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var fetcher = CreateFetcher(store, handler, endpoint, allowRefresh: false);

        var outcome = await fetcher.FetchAsync(CancellationToken.None);

        Assert.Equal(ClaudeAuthStatus.Unauthorized, outcome.Status);
        Assert.Equal(0, endpoint.CallCount);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task FetchAsync_RejectedAndRefreshDead_ReportsUnauthorized()
    {
        var store = new FakeCredentialStore(Credentials("revoked", "dead", Now.AddHours(8)));
        var endpoint = new FakeTokenEndpoint(new ClaudeTokenRefreshResult(ClaudeTokenRefreshOutcome.DeadGrant));
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var fetcher = CreateFetcher(store, handler, endpoint);

        var outcome = await fetcher.FetchAsync(CancellationToken.None);

        Assert.Equal(ClaudeAuthStatus.Unauthorized, outcome.Status);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task FetchAsync_RefreshTokenLifetimeExpired_ReportsExpiredWithoutExchange()
    {
        var credentials = Credentials("expired-access", "expired-refresh", Now.AddMinutes(-1)) with
        {
            RefreshTokenExpiresAt = Now.AddSeconds(-1).ToUnixTimeMilliseconds()
        };
        var store = new FakeCredentialStore(credentials);
        var endpoint = new FakeTokenEndpoint(Tokens("unused", "unused"));
        var handler = new ScriptedHandler(_ => throw new InvalidOperationException("HTTP must not be called"));
        using var fetcher = CreateFetcher(store, handler, endpoint);

        var outcome = await fetcher.FetchAsync(CancellationToken.None);

        Assert.Equal(ClaudeAuthStatus.Expired, outcome.Status);
        Assert.Equal(0, endpoint.CallCount);
    }

    [Fact]
    public async Task FetchAsync_ConcurrentFetchers_ExchangeOnce()
    {
        var store = new FakeCredentialStore(Credentials("expired", "refresh", Now.AddMinutes(-1)));
        var endpoint = new FakeTokenEndpoint(Tokens("fresh", "rotated"), delay: TimeSpan.FromMilliseconds(100));
        var handler = SuccessHandler();
        using var first = CreateFetcher(store, handler, endpoint);
        using var second = CreateFetcher(store, handler, endpoint);

        var outcomes = await Task.WhenAll(
            first.FetchAsync(CancellationToken.None),
            second.FetchAsync(CancellationToken.None));

        Assert.All(outcomes, outcome => Assert.Equal(ClaudeAuthStatus.Ok, outcome.Status));
        Assert.Equal(1, endpoint.CallCount);
    }

    [Fact]
    public async Task FetchAsync_ForbiddenIsNotMisdiagnosedAsExpiredAuth()
    {
        var store = new FakeCredentialStore(Credentials("valid", "refresh", Now.AddHours(8)));
        var endpoint = new FakeTokenEndpoint();
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var fetcher = CreateFetcher(store, handler, endpoint);

        var outcome = await fetcher.FetchAsync(CancellationToken.None);

        Assert.Equal(ClaudeAuthStatus.Unavailable, outcome.Status);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(0, endpoint.CallCount);
    }

    private static ClaudeOAuthUsageFetcher CreateFetcher(
        FakeCredentialStore store,
        ScriptedHandler handler,
        FakeTokenEndpoint endpoint,
        FakeRefreshLock? refreshLock = null,
        bool allowRefresh = true)
    {
        refreshLock ??= new FakeRefreshLock();
        endpoint.Lock = refreshLock;
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.anthropic.com") };
        return new ClaudeOAuthUsageFetcher(client, store, refreshLock, endpoint, new FixedTimeProvider(Now), allowRefresh);
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

    private static ClaudeTokenRefreshResult Tokens(string accessToken, string refreshToken)
        => new(
            ClaudeTokenRefreshOutcome.Refreshed,
            new ClaudeRefreshedTokens(
                accessToken,
                refreshToken,
                Now.AddHours(8).ToUnixTimeMilliseconds(),
                Now.AddDays(30).ToUnixTimeMilliseconds(),
                null));

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
            ConfigDirectory = Path.Combine(Path.GetTempPath(), $"costats-claude-test-{Guid.NewGuid():N}");
        }

        public string ConfigDirectory { get; }

        public Action<FakeCredentialStore>? OnSave { get; init; }

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

        public Task<bool> SaveRefreshedAsync(
            string postedRefreshToken,
            ClaudeRefreshedTokens tokens,
            CancellationToken cancellationToken)
        {
            OnSave?.Invoke(this);
            if (Current?.RefreshToken != postedRefreshToken)
            {
                return Task.FromResult(false);
            }

            Current = Current with
            {
                AccessToken = tokens.AccessToken,
                RefreshToken = tokens.RefreshToken,
                ExpiresAt = tokens.ExpiresAt,
                RefreshTokenExpiresAt = tokens.RefreshTokenExpiresAt ?? Current.RefreshTokenExpiresAt
            };
            return Task.FromResult(true);
        }
    }

    private sealed class FakeRefreshLock : IClaudeRefreshLock
    {
        private int _acquireCount;

        public bool Busy { get; init; }
        public Action? OnAcquire { get; init; }
        public bool IsHeld { get; private set; }
        public int AcquireCount => Volatile.Read(ref _acquireCount);

        public Task<IAsyncDisposable?> TryAcquireAsync(string configDirectory, CancellationToken cancellationToken)
        {
            if (Busy)
            {
                return Task.FromResult<IAsyncDisposable?>(null);
            }

            Interlocked.Increment(ref _acquireCount);
            IsHeld = true;
            OnAcquire?.Invoke();
            return Task.FromResult<IAsyncDisposable?>(new Release(this));
        }

        private sealed class Release(FakeRefreshLock owner) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                owner.IsHeld = false;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FakeTokenEndpoint : IClaudeTokenEndpoint
    {
        private readonly ClaudeTokenRefreshResult _result;
        private readonly TimeSpan _delay;
        private int _callCount;

        public FakeTokenEndpoint(ClaudeTokenRefreshResult? result = null, TimeSpan delay = default)
        {
            _result = result ?? new ClaudeTokenRefreshResult(ClaudeTokenRefreshOutcome.Transient);
            _delay = delay;
        }

        public FakeRefreshLock? Lock { get; set; }
        public bool LockHeldDuringCall { get; private set; }
        public int CallCount => Volatile.Read(ref _callCount);

        public async Task<ClaudeTokenRefreshResult> RefreshAsync(ClaudeCredentials credentials, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            LockHeldDuringCall = Lock?.IsHeld == true;
            if (_delay > TimeSpan.Zero)
            {
                await Task.Delay(_delay, cancellationToken);
            }

            return _result;
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
