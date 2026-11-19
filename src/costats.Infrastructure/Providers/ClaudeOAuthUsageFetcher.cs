using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace costats.Infrastructure.Providers;

/// <summary>
/// Fetches Claude usage data via the Anthropic OAuth API.
/// </summary>
/// <remarks>
/// Uses the terminal CLI's login (<c>~/.claude/.credentials.json</c>). By default Costats only
/// reads it and leaves rotation to Claude Code, so it can never be the process that burns a
/// single-use refresh token. Setting <c>COSTATS_CLAUDE_REFRESH=1</c> lets Costats keep the
/// login alive for people who never run the CLI. When enabled, Costats refreshes
/// exactly the way Claude Code does: only near expiry, only while holding Claude Code's own
/// cross-process refresh locks, re-reading the file under the lock to adopt a pair another
/// process already rotated, and saving with compare-and-swap. The token exchange and save
/// never observe the caller's cancellation, because abandoning them after the server
/// rotates the pair would burn the only refresh token.
/// </remarks>
public sealed class ClaudeOAuthUsageFetcher : IDisposable
{
    private const string BaseUrl = "https://api.anthropic.com";
    private const string UsagePath = "/api/oauth/usage";
    private const string BetaHeader = "oauth-2025-04-20";

    private static readonly TimeSpan BackoffBase = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan BackoffCap = TimeSpan.FromHours(6);

    private static readonly TimeSpan MaxCacheAge = TimeSpan.FromHours(6);
    private static readonly TimeSpan MemoryCacheTtl = TimeSpan.FromMinutes(30);

    // Claude Code refreshes within five minutes of expiry; match it so both agree on timing.
    private static readonly TimeSpan RefreshWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RefreshFailureCooldown = TimeSpan.FromMinutes(1);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RefreshGates = new(StringComparer.OrdinalIgnoreCase);

    private readonly HttpClient _httpClient;
    private readonly HttpClient? _tokenHttpClient;
    private readonly bool _disposeHttpClient;
    private readonly bool _useDiskCache;
    private readonly string? _configDir;
    private readonly IClaudeCredentialStore _credentialStore;
    private readonly IClaudeRefreshLock _refreshLock;
    private readonly IClaudeTokenEndpoint _tokenEndpoint;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly bool _allowRefresh;

    private int _consecutiveFailures;
    private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _refreshBlockedUntil = DateTimeOffset.MinValue;
    private string? _deadRefreshTokenFingerprint;
    private string? _lastCredentialFingerprint;
    private ClaudeOAuthUsageResult? _memoryCache;
    private DateTimeOffset _memoryCacheWrittenAt = DateTimeOffset.MinValue;
    private ClaudeAuthStatus? _lastLoggedStatus;
    private long? _observedExpiresAt;
    private long? _observedRefreshTokenExpiresAt;

    public ClaudeOAuthUsageFetcher(string? configDir, ILogger? logger = null)
    {
        configDir = ClaudeCredentialStore.ResolveConfigDir(configDir);
        _configDir = configDir;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.Add("anthropic-beta", BetaHeader);
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "claude-code/2.1.70");
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _tokenHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _disposeHttpClient = true;
        _useDiskCache = true;
        _credentialStore = new ClaudeCredentialStore(configDir);
        _refreshLock = new ClaudeRefreshLock();
        _timeProvider = TimeProvider.System;
        _tokenEndpoint = new ClaudeTokenEndpoint(_tokenHttpClient, _timeProvider);
        _logger = logger ?? NullLogger.Instance;
        _allowRefresh = Environment.GetEnvironmentVariable("COSTATS_CLAUDE_REFRESH") == "1";
        _logger.LogInformation(
            "Claude token refresh by Costats is {Mode} (profile {Profile})",
            _allowRefresh ? "enabled" : "disabled; Claude Code owns rotation",
            _configDir ?? "default");
    }

    internal ClaudeOAuthUsageFetcher(
        HttpClient httpClient,
        IClaudeCredentialStore credentialStore,
        IClaudeRefreshLock refreshLock,
        IClaudeTokenEndpoint tokenEndpoint,
        TimeProvider? timeProvider = null,
        bool allowRefresh = true)
    {
        _allowRefresh = allowRefresh;
        _httpClient = httpClient;
        _configDir = null;
        _useDiskCache = false;
        _credentialStore = credentialStore;
        _refreshLock = refreshLock;
        _tokenEndpoint = tokenEndpoint;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = NullLogger.Instance;
    }

    //  Public API
    public async Task<ClaudeOAuthOutcome> FetchAsync(CancellationToken cancellationToken)
    {
        var outcome = await FetchCoreAsync(cancellationToken).ConfigureAwait(false);
        LogStatusTransition(outcome.Status);
        return outcome;
    }

    private async Task<ClaudeOAuthOutcome> FetchCoreAsync(CancellationToken cancellationToken)
    {
        var credentials = await _credentialStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        ObserveCredentialChange(credentials);

        var subType = credentials?.SubscriptionType;
        var tier = credentials?.RateLimitTier;

        if (credentials is null || (credentials.AccessToken is null && credentials.RefreshToken is null))
        {
            return new ClaudeOAuthOutcome(GetCachedResult(), ClaudeAuthStatus.NoCredentials, subType, tier);
        }

        var refreshedThisCall = false;
        if (NeedsRefresh(credentials, force: false))
        {
            credentials = await RefreshAsync(credentials, force: false, cancellationToken).ConfigureAwait(false);
            refreshedThisCall = true;
            subType = credentials.SubscriptionType ?? subType;
            tier = credentials.RateLimitTier ?? tier;
        }

        if (credentials.AccessToken is null || IsTokenExpired(credentials))
        {
            return new ClaudeOAuthOutcome(GetCachedResult(), ClassifyUnusableAccessToken(credentials), subType, tier);
        }

        if (UtcNow < _blockedUntil)
        {
            return new ClaudeOAuthOutcome(GetCachedResult(), ClaudeAuthStatus.Unavailable, subType, tier);
        }

        var (fresh, fetchStatus) = await TryFetchAsync(credentials, cancellationToken).ConfigureAwait(false);

        // A rejected unexpired token was either rotated by another process since we read it,
        // or revoked server-side. Adopt a newer pair from disk, otherwise force one refresh.
        if (fresh is null && fetchStatus == ClaudeAuthStatus.Unauthorized)
        {
            var rejectedFingerprint = ComputeFingerprint(credentials);
            var reloaded = await _credentialStore.LoadAsync(cancellationToken).ConfigureAwait(false) ?? credentials;
            ObserveCredentialChange(reloaded);

            if (ComputeFingerprint(reloaded) == rejectedFingerprint && !refreshedThisCall)
            {
                reloaded = await RefreshAsync(reloaded, force: true, cancellationToken).ConfigureAwait(false);
            }

            subType = reloaded.SubscriptionType ?? subType;
            tier = reloaded.RateLimitTier ?? tier;

            if (reloaded.AccessToken is null || IsTokenExpired(reloaded))
            {
                fetchStatus = ClassifyUnusableAccessToken(reloaded);
            }
            else if (ComputeFingerprint(reloaded) != rejectedFingerprint)
            {
                (fresh, fetchStatus) = await TryFetchAsync(reloaded, cancellationToken).ConfigureAwait(false);
            }
        }

        if (fresh is not null)
        {
            _consecutiveFailures = 0;
            _blockedUntil = DateTimeOffset.MinValue;
            SetMemoryCache(fresh);
            if (_useDiskCache)
            {
                _ = WriteDiskCacheAsync(fresh);
            }
            return new ClaudeOAuthOutcome(fresh, ClaudeAuthStatus.Ok, subType, tier);
        }

        _consecutiveFailures++;
        _blockedUntil = UtcNow + ComputeBackoff(_consecutiveFailures);

        return new ClaudeOAuthOutcome(GetCachedResult(), fetchStatus, subType, tier);
    }

    /// <summary>
    /// A dead login needs <c>claude /login</c>. Anything else (refresh lock busy or a
    /// network failure) is transient and retried on the next poll.
    /// </summary>
    private ClaudeAuthStatus ClassifyUnusableAccessToken(ClaudeCredentials credentials)
        => HasUsableRefreshToken(credentials)
            ? ClaudeAuthStatus.Unavailable
            : ClaudeAuthStatus.Expired;

    /// <summary>
    /// Rotates the pair the way Claude Code does and returns the credentials now on disk.
    /// Returns the input unchanged when no refresh happened.
    /// </summary>
    private async Task<ClaudeCredentials> RefreshAsync(
        ClaudeCredentials observed,
        bool force,
        CancellationToken cancellationToken)
    {
        if (!_allowRefresh || !HasUsableRefreshToken(observed) || UtcNow < _refreshBlockedUntil)
        {
            return observed;
        }

        var gate = RefreshGates.GetOrAdd(_credentialStore.ConfigDirectory, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another fetcher in this process may have refreshed while we waited.
            var current = await _credentialStore.LoadAsync(cancellationToken).ConfigureAwait(false) ?? observed;
            if (ComputeFingerprint(current) != ComputeFingerprint(observed) || !NeedsRefresh(current, force))
            {
                return current;
            }

            await using var held = await _refreshLock
                .TryAcquireAsync(_credentialStore.ConfigDirectory, cancellationToken)
                .ConfigureAwait(false);
            if (held is null)
            {
                // Claude Code is refreshing right now; its new pair shows up on disk shortly.
                _logger.LogInformation("Claude token refresh skipped: another process holds the refresh lock");
                _refreshBlockedUntil = UtcNow + RefreshFailureCooldown;
                return await _credentialStore.LoadAsync(cancellationToken).ConfigureAwait(false) ?? current;
            }

            // Re-read under the lock, exactly like Claude Code, and adopt any pair rotated
            // by a process that released the lock just before we took it.
            var locked = await _credentialStore.LoadAsync(CancellationToken.None).ConfigureAwait(false) ?? current;
            if (ComputeFingerprint(locked) != ComputeFingerprint(observed)
                || !NeedsRefresh(locked, force)
                || !HasUsableRefreshToken(locked))
            {
                return locked;
            }

            // From here on, never cancel: the server may rotate the pair at any moment.
            var result = await _tokenEndpoint.RefreshAsync(locked, CancellationToken.None).ConfigureAwait(false);
            switch (result.Outcome)
            {
                case ClaudeTokenRefreshOutcome.DeadGrant:
                    _deadRefreshTokenFingerprint = Fingerprint(locked.RefreshToken);
                    _logger.LogWarning("Claude refresh token was rejected (invalid_grant); run claude /login");
                    return locked;

                case ClaudeTokenRefreshOutcome.Transient:
                    _refreshBlockedUntil = UtcNow + RefreshFailureCooldown;
                    _logger.LogWarning("Claude token refresh failed transiently; retrying after cooldown");
                    return locked;
            }

            var saved = await _credentialStore
                .SaveRefreshedAsync(locked.RefreshToken!, result.Tokens!, CancellationToken.None)
                .ConfigureAwait(false);
            if (saved)
            {
                _logger.LogInformation(
                    "Refreshed Claude login; access token expires {AccessTokenExpiresAt}",
                    FormatEpochMs(result.Tokens!.ExpiresAt));
            }
            else
            {
                _logger.LogWarning("Claude credentials changed during refresh; adopting the newer pair on disk");
            }

            var reloaded = await _credentialStore.LoadAsync(CancellationToken.None).ConfigureAwait(false) ?? locked;
            ObserveCredentialChange(reloaded);
            return reloaded;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _refreshBlockedUntil = UtcNow + RefreshFailureCooldown;
            _logger.LogError(ex, "Claude token refresh failed");
            return await _credentialStore.LoadAsync(CancellationToken.None).ConfigureAwait(false) ?? observed;
        }
        finally
        {
            gate.Release();
        }
    }

    private bool NeedsRefresh(ClaudeCredentials credentials, bool force)
        => force || credentials.AccessToken is null || IsNearExpiry(credentials);

    //  HTTP
    private async Task<(ClaudeOAuthUsageResult? Result, ClaudeAuthStatus Status)> TryFetchAsync(
        ClaudeCredentials credentials, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var request = new HttpRequestMessage(HttpMethod.Get, UsagePath);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var parsed = ParseResponse(content, credentials.SubscriptionType, credentials.RateLimitTier);
                return (parsed, parsed is not null ? ClaudeAuthStatus.Ok : ClaudeAuthStatus.Unavailable);
            }

            // Distinguish a dead/rejected token from a transient server/network failure.
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return (null, ClaudeAuthStatus.Unauthorized);
            }

            return (null, ClaudeAuthStatus.Unavailable);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return (null, ClaudeAuthStatus.Unavailable);
        }
    }

    private void ObserveCredentialChange(ClaudeCredentials? credentials)
    {
        _observedExpiresAt = credentials?.ExpiresAt;
        _observedRefreshTokenExpiresAt = credentials?.RefreshTokenExpiresAt;

        var fingerprint = ComputeFingerprint(credentials);
        if (fingerprint == _lastCredentialFingerprint)
        {
            return;
        }

        if (_lastCredentialFingerprint is not null)
        {
            // Claude Code rotated or replaced the pair. Timestamps only, never token material.
            _logger.LogInformation(
                "Claude credentials changed on disk; access token expires {AccessTokenExpiresAt}",
                FormatEpochMs(credentials?.ExpiresAt));

            if (credentials is not null && credentials.AccessToken is null && credentials.RefreshToken is null)
            {
                // Claude Code blanks the pair after the server answers a refresh with
                // invalid_grant. loginEnded "time_limit" means the server called the refresh
                // token expired; absent means it was rejected for another reason (e.g. reuse).
                _logger.LogWarning(
                    "Claude Code cleared the login after a rejected refresh (loginEnded {LoginEnded}); costats refresh {Mode}",
                    credentials.LoginEnded ?? "not set",
                    _allowRefresh ? "enabled" : "disabled");
            }
        }

        _lastCredentialFingerprint = fingerprint;
        _consecutiveFailures = 0;
        _blockedUntil = DateTimeOffset.MinValue;
    }

    /// <summary>
    /// Logs auth state changes so a forced re-login can be traced to Claude Code rather
    /// than Costats. Logs once per transition, with timestamps only.
    /// </summary>
    private void LogStatusTransition(ClaudeAuthStatus status)
    {
        if (status == _lastLoggedStatus)
        {
            return;
        }

        var previous = _lastLoggedStatus;
        _lastLoggedStatus = status;
        var level = ClaudeIdentity.IsSignInRequired(status) ? LogLevel.Warning : LogLevel.Information;
        _logger.Log(
            level,
            "Claude auth status {PreviousStatus} -> {Status} (profile {Profile}); access token expires {AccessTokenExpiresAt}, refresh token expires {RefreshTokenExpiresAt}",
            previous?.ToString() ?? "(start)",
            status,
            _configDir ?? "default",
            FormatEpochMs(_observedExpiresAt),
            FormatEpochMs(_observedRefreshTokenExpiresAt));
    }

    private static string FormatEpochMs(long? epochMs)
        => epochMs is { } value
            ? DateTimeOffset.FromUnixTimeMilliseconds(value).ToString("u")
            : "unknown";

    //  Backoff
    private static TimeSpan ComputeBackoff(int failures)
    {
        var multiplier = Math.Pow(2, Math.Max(failures - 1, 0));
        var seconds = BackoffBase.TotalSeconds * multiplier;
        return seconds >= BackoffCap.TotalSeconds
            ? BackoffCap
            : TimeSpan.FromSeconds(seconds);
    }

    //  Two-tier cache (memory + disk)
    private void SetMemoryCache(ClaudeOAuthUsageResult result)
    {
        _memoryCache = result;
        _memoryCacheWrittenAt = UtcNow;
    }

    /// <summary>
    /// Returns the best available cached result: memory first, then disk.
    /// The result is validated against age and quota window freshness.
    /// </summary>
    private ClaudeOAuthUsageResult? GetCachedResult()
    {
        // Try memory cache (30 min TTL)
        if (_memoryCache is not null
            && UtcNow - _memoryCacheWrittenAt <= MemoryCacheTtl
            && IsCacheValid(_memoryCache))
        {
            return _memoryCache;
        }

        if (!_useDiskCache)
        {
            return null;
        }

        // Try disk cache
        var fromDisk = ReadDiskCache();
        if (fromDisk is not null && IsCacheValid(fromDisk))
        {
            SetMemoryCache(fromDisk);
            return fromDisk;
        }

        return null;
    }

    /// <summary>
    /// The most recent API result regardless of age. Only its reset timestamps are reused,
    /// to project reset times for sources that lack them.
    /// </summary>
    public ClaudeOAuthUsageResult? GetLastKnownResult()
        => _memoryCache ?? (_useDiskCache ? ReadDiskCache() : null);

    private bool IsCacheValid(ClaudeOAuthUsageResult cached)
    {
        var now = UtcNow;

        if (now - cached.FetchedAt > MaxCacheAge)
        {
            return false;
        }

        var sessionExpired = cached.FiveHourResetsAt.HasValue && now > cached.FiveHourResetsAt.Value;
        var weekExpired = cached.SevenDayResetsAt.HasValue && now > cached.SevenDayResetsAt.Value;
        return !(sessionExpired && weekExpired);
    }

    // Disk cache I/O
    private string GetDiskCachePath()
    {
        var basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profileSuffix = _configDir is not null
            ? "_" + Path.GetFileName(_configDir)
            : "";
        return Path.Combine(basePath, "costats", "cache", $"claude-oauth{profileSuffix}.json");
    }

    private async Task WriteDiskCacheAsync(ClaudeOAuthUsageResult result)
    {
        try
        {
            var path = GetDiskCachePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(result, DiskCacheJsonOptions);
            await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
        }
        catch
        {
            // Non-critical — memory cache still works
        }
    }

    private ClaudeOAuthUsageResult? ReadDiskCache()
    {
        try
        {
            var path = GetDiskCachePath();
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ClaudeOAuthUsageResult>(json, DiskCacheJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions DiskCacheJsonOptions = new(JsonSerializerDefaults.Web);

    //  Credential helpers
    private static string? ComputeFingerprint(ClaudeCredentials? credentials)
        => Fingerprint(credentials?.AccessToken);

    private static string? Fingerprint(string? token)
    {
        if (token is null)
        {
            return null;
        }

        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(hash, 0, 8);
    }

    private bool IsTokenExpired(ClaudeCredentials credentials)
        => credentials.ExpiresAt.HasValue
           && UtcNow.ToUnixTimeMilliseconds() >= credentials.ExpiresAt.Value;

    private bool IsNearExpiry(ClaudeCredentials credentials)
        => credentials.ExpiresAt.HasValue
           && (UtcNow + RefreshWindow).ToUnixTimeMilliseconds() >= credentials.ExpiresAt.Value;

    /// <summary>
    /// False once the server rejected this refresh token, so a dead login is never retried
    /// (Claude Code keeps the same "known dead" list). A new login clears it.
    /// </summary>
    private bool HasUsableRefreshToken(ClaudeCredentials? credentials)
        => credentials?.RefreshToken is not null
           && Fingerprint(credentials.RefreshToken) != _deadRefreshTokenFingerprint
           && (!credentials.RefreshTokenExpiresAt.HasValue
               || UtcNow.ToUnixTimeMilliseconds() < credentials.RefreshTokenExpiresAt.Value);

    //  Response parsing
    private ClaudeOAuthUsageResult? ParseResponse(string json, string? subscriptionType, string? rateLimitTier)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            double? fiveHourPercent = null;
            DateTimeOffset? fiveHourResetsAt = null;
            double? sevenDayPercent = null;
            DateTimeOffset? sevenDayResetsAt = null;

            if (root.TryGetProperty("five_hour", out var fiveHour))
            {
                if (fiveHour.TryGetProperty("utilization", out var util))
                {
                    fiveHourPercent = util.ValueKind == JsonValueKind.Number ? util.GetDouble() : null;
                }
                if (fiveHour.TryGetProperty("resets_at", out var resets) && resets.ValueKind == JsonValueKind.String)
                {
                    if (DateTimeOffset.TryParse(resets.GetString(), out var resetsTime))
                    {
                        fiveHourResetsAt = resetsTime;
                    }
                }
            }

            if (root.TryGetProperty("seven_day", out var sevenDay))
            {
                if (sevenDay.TryGetProperty("utilization", out var util))
                {
                    sevenDayPercent = util.ValueKind == JsonValueKind.Number ? util.GetDouble() : null;
                }
                if (sevenDay.TryGetProperty("resets_at", out var resets) && resets.ValueKind == JsonValueKind.String)
                {
                    if (DateTimeOffset.TryParse(resets.GetString(), out var resetsTime))
                    {
                        sevenDayResetsAt = resetsTime;
                    }
                }
            }

            double? extraUsed = null;
            double? extraLimit = null;
            bool overageEnabled = false;
            if (root.TryGetProperty("extra_usage", out var extra))
            {
                if (extra.TryGetProperty("is_enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True)
                {
                    overageEnabled = true;
                }
                if (extra.TryGetProperty("used_credits", out var used) && used.ValueKind == JsonValueKind.Number)
                {
                    extraUsed = used.GetDouble();
                }
                if (extra.TryGetProperty("monthly_limit", out var limit) && limit.ValueKind == JsonValueKind.Number)
                {
                    extraLimit = limit.GetDouble();
                }

                if (extraUsed.HasValue && extraLimit.HasValue)
                {
                    (extraUsed, extraLimit) = NormalizeMonetaryValues(extraUsed.Value, extraLimit.Value, subscriptionType);
                }
            }

            return new ClaudeOAuthUsageResult(
                fiveHourPercent,
                fiveHourResetsAt,
                sevenDayPercent,
                sevenDayResetsAt,
                overageEnabled,
                extraUsed,
                extraLimit,
                subscriptionType,
                rateLimitTier,
                UtcNow);
        }
        catch
        {
            return null;
        }
    }

    private static (double used, double limit) NormalizeMonetaryValues(double rawUsed, double rawLimit, string? tier)
    {
        var usedDollars = rawUsed / 100.0;
        var limitDollars = rawLimit / 100.0;

        const double PlausibilityThreshold = 500.0;
        var isEnterpriseTier = tier?.Contains("enterprise", StringComparison.OrdinalIgnoreCase) == true;

        if (!isEnterpriseTier && limitDollars > PlausibilityThreshold)
        {
            usedDollars /= 100.0;
            limitDollars /= 100.0;
        }

        return (usedDollars, limitDollars);
    }

    public void Dispose()
    {
        if (_disposeHttpClient)
        {
            _httpClient.Dispose();
            _tokenHttpClient?.Dispose();
        }
    }

    private DateTimeOffset UtcNow => _timeProvider.GetUtcNow();
}

public sealed record ClaudeOAuthUsageResult(
    double? FiveHourUsedPercent,
    DateTimeOffset? FiveHourResetsAt,
    double? SevenDayUsedPercent,
    DateTimeOffset? SevenDayResetsAt,
    bool OverageEnabled,
    double? OverageSpentUsd,
    double? OverageCeilingUsd,
    string? SubscriptionType,
    string? RateLimitTier,
    DateTimeOffset FetchedAt);

/// <summary>
/// The authentication state of a Claude OAuth fetch attempt.
/// </summary>
public enum ClaudeAuthStatus
{
    /// <summary>Token is valid and usage was fetched.</summary>
    Ok,

    /// <summary>
    /// Access token is expired and the refresh token is gone, expired or rejected by the
    /// server. Re-login required.
    /// </summary>
    Expired,

    /// <summary>The API rejected the token even after a refresh (401). Re-login required.</summary>
    Unauthorized,

    /// <summary>No credentials file / no access token present — sign-in required.</summary>
    NoCredentials,

    /// <summary>A transient network/server failure; the token itself looks fine.</summary>
    Unavailable
}

/// <summary>
/// The result of a Claude OAuth fetch. Carries usage data when available plus the
/// authentication status and the credential-sourced plan info, which remain meaningful
/// even when the usage API call itself failed.
/// </summary>
public sealed record ClaudeOAuthOutcome(
    ClaudeOAuthUsageResult? Result,
    ClaudeAuthStatus Status,
    string? SubscriptionType,
    string? RateLimitTier);
