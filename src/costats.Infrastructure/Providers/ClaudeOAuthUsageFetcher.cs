using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace costats.Infrastructure.Providers;

/// <summary>
/// Fetches Claude usage data via the Anthropic OAuth API.
/// </summary>
public sealed class ClaudeOAuthUsageFetcher : IDisposable
{
    private const string BaseUrl = "https://api.anthropic.com";
    private const string UsagePath = "/api/oauth/usage";
    private const string BetaHeader = "oauth-2025-04-20";

    private static readonly TimeSpan BackoffBase = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan BackoffCap = TimeSpan.FromHours(6);

    private static readonly TimeSpan MaxCacheAge = TimeSpan.FromHours(6);
    private static readonly TimeSpan MemoryCacheTtl = TimeSpan.FromMinutes(30);

    // Match Claude Code's own five-minute early-refresh window. This keeps Costats
    // from being the process that discovers an expired access token after a day idle.
    private static readonly TimeSpan ProactiveRefreshWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RefreshFailureCooldown = TimeSpan.FromSeconds(20);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RefreshGates = new(StringComparer.OrdinalIgnoreCase);

    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;
    private readonly bool _useDiskCache;
    private readonly string? _configDir;
    private readonly IClaudeCredentialStore _credentialStore;
    private readonly IClaudeTokenRefresher _tokenRefresher;
    private readonly TimeProvider _timeProvider;

    private int _consecutiveFailures;
    private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;
    private string? _lastCredentialFingerprint;
    private ClaudeOAuthUsageResult? _memoryCache;
    private DateTimeOffset _memoryCacheWrittenAt = DateTimeOffset.MinValue;
    private DateTimeOffset _refreshBlockedUntil = DateTimeOffset.MinValue;

    public ClaudeOAuthUsageFetcher() : this(configDir: null)
    {
    }

    public ClaudeOAuthUsageFetcher(string? configDir)
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
        _disposeHttpClient = true;
        _useDiskCache = true;
        _credentialStore = new ClaudeCredentialStore(configDir);
        _tokenRefresher = new ClaudeCliTokenRefresher(configDir);
        _timeProvider = TimeProvider.System;
    }

    internal ClaudeOAuthUsageFetcher(
        HttpClient httpClient,
        IClaudeCredentialStore credentialStore,
        IClaudeTokenRefresher tokenRefresher,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient;
        _configDir = null;
        _useDiskCache = false;
        _credentialStore = credentialStore;
        _tokenRefresher = tokenRefresher;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    //  Public API
    public async Task<ClaudeOAuthOutcome> FetchAsync(CancellationToken cancellationToken)
    {
        var credentials = await _credentialStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        ObserveCredentialChange(credentials);

        var subType = credentials?.SubscriptionType;
        var tier = credentials?.RateLimitTier;
        var hadRefreshToken = HasUsableRefreshToken(credentials);

        // Refresh a missing access token when a recoverable refresh credential remains,
        // and refresh five minutes early to avoid the normal one-day expiry gap.
        if (credentials is not null
            && HasUsableRefreshToken(credentials)
            && (credentials.AccessToken is null || IsNearExpiry(credentials)))
        {
            credentials = await RefreshAndReloadAsync(credentials, force: false, cancellationToken).ConfigureAwait(false);
            ObserveCredentialChange(credentials);
            subType = credentials?.SubscriptionType ?? subType;
            tier = credentials?.RateLimitTier ?? tier;
        }

        if (credentials?.AccessToken is null)
        {
            var status = hadRefreshToken ? ClaudeAuthStatus.Expired : ClaudeAuthStatus.NoCredentials;
            return new ClaudeOAuthOutcome(GetCachedResult(), status, subType, tier);
        }

        if (IsTokenExpired(credentials))
        {
            return new ClaudeOAuthOutcome(GetCachedResult(), ClaudeAuthStatus.Expired, subType, tier);
        }

        if (UtcNow < _blockedUntil)
        {
            return new ClaudeOAuthOutcome(GetCachedResult(), ClaudeAuthStatus.Unavailable, subType, tier);
        }

        var (fresh, fetchStatus) = await TryFetchAsync(credentials, cancellationToken).ConfigureAwait(false);

        // A server can revoke an access token before its local expiresAt. Delegate one
        // forced refresh to Claude Code, reload its rotated pair, and retry exactly once.
        if (fresh is null
            && fetchStatus == ClaudeAuthStatus.Unauthorized
            && HasUsableRefreshToken(credentials))
        {
            var rejectedFingerprint = ComputeFingerprint(credentials);
            credentials = await RefreshAndReloadAsync(credentials, force: true, cancellationToken).ConfigureAwait(false);
            ObserveCredentialChange(credentials);
            subType = credentials?.SubscriptionType ?? subType;
            tier = credentials?.RateLimitTier ?? tier;

            if (credentials?.AccessToken is not null
                && ComputeFingerprint(credentials) != rejectedFingerprint
                && !IsTokenExpired(credentials))
            {
                (fresh, fetchStatus) = await TryFetchAsync(credentials, cancellationToken).ConfigureAwait(false);
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

    //  HTTP
    private async Task<(ClaudeOAuthUsageResult? Result, ClaudeAuthStatus Status)> TryFetchAsync(
        ClaudeCredentials? credentials, CancellationToken cancellationToken)
    {
        try
        {
            if (credentials?.AccessToken is null || IsTokenExpired(credentials))
            {
                return (null, ClaudeAuthStatus.Expired);
            }

            cancellationToken.ThrowIfCancellationRequested();

            using var request = new HttpRequestMessage(HttpMethod.Get, UsagePath);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);

            var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

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

    private async Task<ClaudeCredentials?> RefreshAndReloadAsync(
        ClaudeCredentials observedCredentials,
        bool force,
        CancellationToken cancellationToken)
    {
        var gate = RefreshGates.GetOrAdd(_credentialStore.RefreshLockKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var current = await _credentialStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var observedFingerprint = ComputeFingerprint(observedCredentials);
            var currentFingerprint = ComputeFingerprint(current);

            // Another Costats/Claude process may have rotated the token while this call
            // waited. Always consume that new pair instead of exchanging the old refresh token.
            if (currentFingerprint != observedFingerprint
                && current?.AccessToken is not null
                && !IsTokenExpired(current))
            {
                return current;
            }

            if (current is null || !HasUsableRefreshToken(current) || UtcNow < _refreshBlockedUntil)
            {
                return current;
            }

            if (!force && current.AccessToken is not null && !IsNearExpiry(current))
            {
                return current;
            }

            var processSucceeded = await _tokenRefresher.RefreshAsync(current, cancellationToken).ConfigureAwait(false);
            var reloaded = await _credentialStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var refreshSucceeded = processSucceeded
                && reloaded?.AccessToken is not null
                && !IsTokenExpired(reloaded)
                && (ComputeFingerprint(reloaded) != currentFingerprint || !IsNearExpiry(reloaded));

            if (!refreshSucceeded)
            {
                _refreshBlockedUntil = UtcNow + RefreshFailureCooldown;
            }

            // Reload even after a nonzero exit in case a concurrent CLI process won.
            return reloaded;
        }
        finally
        {
            gate.Release();
        }
    }

    private void ObserveCredentialChange(ClaudeCredentials? credentials)
    {
        var fingerprint = ComputeFingerprint(credentials);
        if (fingerprint == _lastCredentialFingerprint)
        {
            return;
        }

        _lastCredentialFingerprint = fingerprint;
        _consecutiveFailures = 0;
        _blockedUntil = DateTimeOffset.MinValue;
        _refreshBlockedUntil = DateTimeOffset.MinValue;
    }

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
    {
        if (credentials?.AccessToken is null)
        {
            return null;
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(credentials.AccessToken);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 8);
    }

    private bool IsTokenExpired(ClaudeCredentials credentials)
        => credentials.ExpiresAt.HasValue
           && UtcNow.ToUnixTimeMilliseconds() >= credentials.ExpiresAt.Value;

    private bool IsNearExpiry(ClaudeCredentials credentials)
        => credentials.ExpiresAt.HasValue
           && UtcNow.Add(ProactiveRefreshWindow).ToUnixTimeMilliseconds() >= credentials.ExpiresAt.Value;

    private bool HasUsableRefreshToken(ClaudeCredentials? credentials)
        => credentials?.RefreshToken is not null
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

    /// <summary>Access token is expired and could not be refreshed (e.g. no refresh token) — re-login required.</summary>
    Expired,

    /// <summary>The API rejected the token (401/403) — re-login required.</summary>
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
