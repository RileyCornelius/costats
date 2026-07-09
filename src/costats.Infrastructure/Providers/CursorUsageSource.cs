using costats.Application.Pulse;
using costats.Application.Security;
using costats.Application.Settings;
using costats.Core.Pulse;
using costats.Infrastructure.Expense;
using costats.Infrastructure.Providers.Cursor;
using Microsoft.Extensions.Logging;
using static costats.Core.Pulse.UsageFormatter;

namespace costats.Infrastructure.Providers;

public sealed class CursorUsageSource : ISignalSource
{
    private const string SignedOutSummary = "Cursor session expired — open Cursor or paste a token in Settings";
    private const int ConsumptionWindowDays = 30;

    // The paginated events call is much heavier than usage-summary, so the digest is
    // recomputed at most this often while the summary follows the normal refresh cadence.
    private static readonly TimeSpan ConsumptionCacheTtl = TimeSpan.FromMinutes(30);

    private readonly AppSettings _settings;
    private readonly ICredentialVault _credentialVault;
    private readonly CursorCredentialReader _credentialReader;
    private readonly CursorUsageFetcher _fetcher;
    private readonly ExpenseAnalyzer _expenseAnalyzer;
    private readonly ILogger<CursorUsageSource> _logger;

    private readonly object _consumptionGate = new();
    private ConsumptionDigest? _cachedConsumption;
    private DateTimeOffset _consumptionComputedAt = DateTimeOffset.MinValue;

    public CursorUsageSource(
        AppSettings settings,
        ICredentialVault credentialVault,
        CursorCredentialReader credentialReader,
        CursorUsageFetcher fetcher,
        ExpenseAnalyzer expenseAnalyzer,
        ILogger<CursorUsageSource> logger)
    {
        _settings = settings;
        _credentialVault = credentialVault;
        _credentialReader = credentialReader;
        _fetcher = fetcher;
        _expenseAnalyzer = expenseAnalyzer;
        _logger = logger;
    }

    public ProviderProfile Profile => ProviderCatalog.Cursor;

    public async Task<ProviderReading> ReadAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        if (!_settings.CursorEnabled)
        {
            return new ProviderReading(
                Usage: null,
                Identity: new IdentityCard(Profile.ProviderId, Profile.DisplayName, null, null, "Cursor", "Session"),
                StatusSummary: "Cursor disabled in Settings",
                CapturedAt: now,
                Confidence: ReadingConfidence.Low,
                Source: ReadingSource.Api);
        }

        try
        {
            var localCredentials = _credentialReader.ReadLocalCredentials();

            CursorUsageFetchResult? result = null;
            string? activeCookie = null;
            if (localCredentials is not null)
            {
                activeCookie = localCredentials.CookieHeader;
                result = await _fetcher.FetchAsync(activeCookie, cancellationToken).ConfigureAwait(false);
            }

            // Fall back to the manually pasted token when the local install has no usable session.
            if (result is null || result.Status is CursorFetchStatus.MissingToken or CursorFetchStatus.InvalidToken)
            {
                var manualToken = await _credentialVault.LoadAsync(CredentialKeys.CursorToken, cancellationToken).ConfigureAwait(false);
                var manualCookie = CursorCredentialReader.NormalizeManualToken(manualToken);
                if (manualCookie is not null)
                {
                    activeCookie = manualCookie;
                    result = await _fetcher.FetchAsync(manualCookie, cancellationToken).ConfigureAwait(false);
                }
            }

            var identity = new IdentityCard(
                Profile.ProviderId,
                Profile.DisplayName,
                result?.Payload?.Email ?? localCredentials?.Email,
                null,
                FormatPlanText(result?.Payload?.MembershipType ?? localCredentials?.MembershipType),
                "Session");

            if (result is null)
            {
                return new ProviderReading(
                    Usage: null,
                    Identity: identity,
                    StatusSummary: "Cursor session not found — open Cursor or paste a token in Settings",
                    CapturedAt: now,
                    Confidence: ReadingConfidence.Low,
                    Source: ReadingSource.Api);
            }

            if (result.Status != CursorFetchStatus.Success || result.Payload is null)
            {
                var summary = result.Status is CursorFetchStatus.MissingToken or CursorFetchStatus.InvalidToken
                    ? SignedOutSummary
                    : result.StatusSummary;

                return new ProviderReading(
                    Usage: null,
                    Identity: identity,
                    StatusSummary: summary,
                    CapturedAt: now,
                    Confidence: ReadingConfidence.Low,
                    Source: ReadingSource.Api);
            }

            var payload = result.Payload;

            // Session slot carries the dashboard's "First-party models" bar and the week
            // slot its "API" bar, both as 0-100 percentages of the included plan.
            long? sessionUsed = null;
            long? sessionLimit = null;
            if (payload.FirstPartyPercentUsed is not null)
            {
                sessionUsed = (long)Math.Round(payload.FirstPartyPercentUsed.Value);
                sessionLimit = 100;
            }

            long? weekUsed = null;
            long? weekLimit = null;
            if (payload.ApiPercentUsed is not null)
            {
                weekUsed = (long)Math.Round(payload.ApiPercentUsed.Value);
                weekLimit = 100;
            }

            var consumption = await GetConsumptionAsync(activeCookie!, cancellationToken).ConfigureAwait(false);

            if (sessionUsed is null && weekUsed is null && consumption is null)
            {
                return new ProviderReading(
                    Usage: null,
                    Identity: identity,
                    StatusSummary: "No Cursor usage data available",
                    CapturedAt: now,
                    Confidence: ReadingConfidence.Low,
                    Source: ReadingSource.Api);
            }

            // Cursor quotas reset with the monthly billing cycle.
            var resetAt = payload.BillingCycleEnd;
            var monthlyDuration = CalculateMonthlyDuration(resetAt);
            QuotaWindow? sessionWindow = sessionUsed is not null
                ? new QuotaWindow(monthlyDuration, resetAt)
                : null;
            QuotaWindow? weekWindow = weekUsed is not null
                ? new QuotaWindow(monthlyDuration, resetAt)
                : null;

            var usage = new UsagePulse(
                ProviderId: Profile.ProviderId,
                CapturedAt: payload.FetchedAt,
                SessionUsed: sessionUsed,
                SessionLimit: sessionLimit,
                WeekUsed: weekUsed,
                WeekLimit: weekLimit,
                SpendingBucket: null,
                Consumption: consumption,
                SessionWindow: sessionWindow,
                WeekWindow: weekWindow);

            var statusSummary = $"Updated {FormatRelativeTime(payload.FetchedAt, now)}";

            return new ProviderReading(
                Usage: usage,
                Identity: identity,
                StatusSummary: statusSummary,
                CapturedAt: usage.CapturedAt,
                Confidence: ReadingConfidence.Medium,
                Source: ReadingSource.Api);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cursor usage read failed");
            return new ProviderReading(
                Usage: null,
                Identity: new IdentityCard(Profile.ProviderId, Profile.DisplayName, null, null, "Cursor", "Session"),
                StatusSummary: "Cursor usage unavailable",
                CapturedAt: now,
                Confidence: ReadingConfidence.Low,
                Source: ReadingSource.Api);
        }
    }

    /// <summary>
    /// Returns the token-consumption digest built from dashboard usage events, recomputing
    /// at most every <see cref="ConsumptionCacheTtl"/>. Failures fall back to the last good
    /// digest (or null on the first run) so cost display degrades without affecting quota data.
    /// </summary>
    private async Task<ConsumptionDigest?> GetConsumptionAsync(string cookieHeader, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_consumptionGate)
        {
            if (_cachedConsumption is not null && now - _consumptionComputedAt < ConsumptionCacheTtl)
            {
                return _cachedConsumption;
            }
        }

        try
        {
            var eventsResult = await _fetcher.FetchUsageEventsAsync(
                cookieHeader,
                now.AddDays(-ConsumptionWindowDays),
                now,
                cancellationToken).ConfigureAwait(false);

            if (eventsResult.Status != CursorFetchStatus.Success)
            {
                _logger.LogDebug("Cursor usage events fetch skipped: {Summary}", eventsResult.StatusSummary);
                return _cachedConsumption;
            }

            var digest = await _expenseAnalyzer.AnalyzeCursorAsync(eventsResult.Events, cancellationToken).ConfigureAwait(false);
            lock (_consumptionGate)
            {
                _cachedConsumption = digest;
                _consumptionComputedAt = now;
            }

            return digest;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cursor consumption digest failed");
            return _cachedConsumption;
        }
    }

    /// <summary>
    /// Calculates the monthly window duration based on the billing-cycle reset date.
    /// Falls back to ~30 days if no reset date is available.
    /// </summary>
    private static TimeSpan CalculateMonthlyDuration(DateTimeOffset? resetAt)
    {
        if (resetAt is null)
        {
            return TimeSpan.FromDays(30);
        }

        var resetDate = resetAt.Value;
        var windowStart = resetDate.AddMonths(-1);
        return resetDate - windowStart;
    }

    private static string FormatPlanText(string? membershipType)
    {
        if (string.IsNullOrWhiteSpace(membershipType))
        {
            return "Cursor";
        }

        // Handle values like "pro" → "Pro", "free_trial" → "Free Trial"
        return string.Join(' ', membershipType.Split('_')
            .Select(word => word.Length > 0
                ? char.ToUpper(word[0]) + word[1..].ToLower()
                : word));
    }
}
