using costats.Application.Pulse;
using costats.Core.Pulse;
using costats.Infrastructure.Expense;
using costats.Infrastructure.Usage;
using static costats.Core.Pulse.UsageFormatter;

namespace costats.Infrastructure.Providers;

public sealed class CodexLogSource : ISignalSource
{
    private static readonly TimeSpan DefaultSessionDuration = TimeSpan.FromHours(3);
    private static readonly TimeSpan DefaultWeekDuration = TimeSpan.FromDays(7);

    private readonly UsageLogScanner _scanner = new();
    private readonly CodexOAuthUsageFetcher _oauthFetcher = new();
    private readonly ExpenseAnalyzer _expenseAnalyzer;

    public CodexLogSource(ExpenseAnalyzer expenseAnalyzer)
    {
        _expenseAnalyzer = expenseAnalyzer;
    }

    public ProviderProfile Profile => ProviderCatalog.Codex;

    public async Task<ProviderReading> ReadAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        // OAuth is a network call - run in parallel with file I/O
        var oauthTask = _oauthFetcher.FetchAsync(cancellationToken);

        // Log scan and expense analysis both read the same files - run sequentially to halve peak memory
        var logResult = await _scanner.ScanCodexAsync(cancellationToken).ConfigureAwait(false);
        var consumption = await SafeAnalyzeExpenseAsync(cancellationToken).ConfigureAwait(false);

        var oauthResult = await oauthTask.ConfigureAwait(false);

        if (oauthResult is null && logResult.SessionTokens == 0 && logResult.WeekTokens == 0)
        {
            return new ProviderReading(
                Usage: null,
                Identity: null,
                StatusSummary: "No Codex usage data available",
                CapturedAt: now,
                Confidence: ReadingConfidence.Low,
                Source: ReadingSource.LocalLog);
        }

        // Prefer OAuth data for percentages
        var sessionUsedPercent = oauthResult?.SessionUsedPercent;
        var weeklyUsedPercent = oauthResult?.WeeklyUsedPercent;

        // When the API responds but reports no session window, OpenAI has
        // suspended the session limit — surface that as "no limit" instead of
        // fabricating a local countdown from log data.
        var sessionLimitSuspended = oauthResult is not null && sessionUsedPercent is null;

        // Get window durations from API or use defaults
        var sessionDuration = oauthResult?.SessionWindowSeconds is not null
            ? TimeSpan.FromSeconds(oauthResult.SessionWindowSeconds.Value)
            : DefaultSessionDuration;

        var weekDuration = oauthResult?.WeeklyWindowSeconds is not null
            ? TimeSpan.FromSeconds(oauthResult.WeeklyWindowSeconds.Value)
            : DefaultWeekDuration;

        var weeklyResetsAt = oauthResult?.WeeklyResetsAt ?? CalculateWeeklyReset(now);

        QuotaWindow? sessionWindow = null;
        if (!sessionLimitSuspended)
        {
            var sessionResetsAt = oauthResult?.SessionResetsAt ?? CalculateSessionReset(logResult.SessionStart, now, sessionDuration);
            sessionWindow = new QuotaWindow(sessionDuration, sessionResetsAt);
        }

        var weekWindow = new QuotaWindow(weekDuration, weeklyResetsAt);

        // Use percentage data directly when available
        long? sessionUsed;
        long? sessionLimit;
        long? weekUsed;
        long? weekLimit;

        if (sessionUsedPercent is not null)
        {
            // Store percentage directly: used=percentage, limit=100
            sessionUsed = (long)Math.Round(sessionUsedPercent.Value);
            sessionLimit = 100;
        }
        else
        {
            sessionUsed = !sessionLimitSuspended && logResult.SessionTokens > 0 ? logResult.SessionTokens : null;
            sessionLimit = null;
        }

        if (weeklyUsedPercent is not null)
        {
            weekUsed = (long)Math.Round(weeklyUsedPercent.Value);
            weekLimit = 100;
        }
        else
        {
            weekUsed = logResult.WeekTokens > 0 ? logResult.WeekTokens : null;
            weekLimit = null;
        }

        // Build prepaid balance bucket when credits are available
        MonetaryBucket? spendingBucket = null;
        if (oauthResult is { HasCredits: true, CreditBalance: not null } && oauthResult.CreditBalance.Value > 0)
        {
            spendingBucket = MonetaryBucket.ForPrepaidBalance((decimal)oauthResult.CreditBalance.Value);
        }

        var usage = new UsagePulse(
            ProviderId: Profile.ProviderId,
            CapturedAt: oauthResult?.FetchedAt ?? logResult.LatestTimestamp ?? now,
            SessionUsed: sessionUsed,
            SessionLimit: sessionLimit,
            WeekUsed: weekUsed,
            WeekLimit: weekLimit,
            SpendingBucket: spendingBucket,
            Consumption: consumption,
            SessionWindow: sessionWindow,
            WeekWindow: weekWindow);

        var planText = FormatPlanText(oauthResult?.PlanType);
        var statusSummary = oauthResult is not null
            ? $"Updated {FormatRelativeTime(oauthResult.FetchedAt, now)}"
            : $"Updated {FormatRelativeTime(logResult.LatestTimestamp ?? now, now)}";

        var confidence = oauthResult is not null ? ReadingConfidence.High : ReadingConfidence.Medium;
        var source = oauthResult is not null ? ReadingSource.Api : ReadingSource.LocalLog;

        return new ProviderReading(
            Usage: usage,
            Identity: new IdentityCard(Profile.ProviderId, Profile.DisplayName, null, null, planText, "OAuth"),
            StatusSummary: statusSummary,
            CapturedAt: usage.CapturedAt,
            Confidence: confidence,
            Source: source);
    }

    private static string FormatPlanText(string? planType)
    {
        if (string.IsNullOrEmpty(planType))
        {
            return "Pro";
        }

        // Convert "pro" to "Pro", "plus" to "Plus", etc.
        return char.ToUpper(planType[0]) + planType[1..].ToLower();
    }

    private static DateTimeOffset? CalculateSessionReset(DateTimeOffset? sessionStart, DateTimeOffset now, TimeSpan sessionDuration)
    {
        if (sessionStart is null)
        {
            return now + sessionDuration;
        }

        var elapsed = now - sessionStart.Value;
        if (elapsed >= sessionDuration)
        {
            return now + sessionDuration;
        }

        return sessionStart.Value + sessionDuration;
    }

    private static DateTimeOffset CalculateWeeklyReset(DateTimeOffset now)
    {
        var daysUntilMonday = ((int)DayOfWeek.Monday - (int)now.DayOfWeek + 7) % 7;
        if (daysUntilMonday == 0 && now.TimeOfDay > TimeSpan.Zero)
        {
            daysUntilMonday = 7;
        }

        var nextMonday = now.Date.AddDays(daysUntilMonday);
        return new DateTimeOffset(nextMonday, TimeSpan.Zero);
    }

    private async Task<ConsumptionDigest?> SafeAnalyzeExpenseAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _expenseAnalyzer.AnalyzeCodexAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Cost analysis failure should not break usage display
            return null;
        }
    }
}
