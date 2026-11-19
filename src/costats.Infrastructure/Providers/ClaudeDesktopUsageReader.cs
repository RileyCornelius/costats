using System.Text.Json;

namespace costats.Infrastructure.Providers;

/// <summary>
/// Reads the plan-usage samples the Claude desktop app records in
/// <c>%APPDATA%\Claude\plan-usage-history.json</c> (about every 15 minutes while it runs).
/// </summary>
/// <remarks>
/// This source needs no credentials. It stays current for people who use the desktop app and
/// rarely run the CLI, whose <c>.credentials.json</c> goes stale. The file format is undocumented,
/// so any read or shape failure returns null and callers fall back to OAuth and logs.
/// </remarks>
public sealed class ClaudeDesktopUsageReader
{
    /// <summary>
    /// Samples older than one weekly window are ignored. <see cref="ToUsageResult"/> zeroes
    /// figures from younger samples whose windows have rolled over, and the UI shows their age.
    /// </summary>
    internal static readonly TimeSpan MaxSampleAge = TimeSpan.FromDays(7);

    private static readonly TimeSpan FiveHourWindow = TimeSpan.FromHours(5);
    private static readonly TimeSpan WeekDuration = TimeSpan.FromDays(7);

    private readonly string _historyPath;

    public ClaudeDesktopUsageReader()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Claude",
            "plan-usage-history.json"))
    {
    }

    internal ClaudeDesktopUsageReader(string historyPath)
    {
        _historyPath = historyPath;
    }

    public async Task<ClaudeDesktopUsageSample?> ReadLatestAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_historyPath))
            {
                return null;
            }

            // The desktop app may be rewriting the file. Share access and let the next poll retry.
            await using var stream = new FileStream(
                _historyPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            return ParseLatest(json);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    internal static ClaudeDesktopUsageSample? ParseLatest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("samples", out var samples)
                || samples.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            ClaudeDesktopUsageSample? latest = null;
            foreach (var sample in samples.EnumerateArray())
            {
                if (!TryReadInt64(sample, "t", out var timestamp)
                    || !sample.TryGetProperty("u", out var usage)
                    || !TryReadDouble(usage, "fh", out var fiveHour)
                    || !TryReadDouble(usage, "sd", out var sevenDay))
                {
                    continue;
                }

                var sampledAt = DateTimeOffset.FromUnixTimeMilliseconds(timestamp);
                if (latest is null || sampledAt > latest.SampledAt)
                {
                    latest = new ClaudeDesktopUsageSample(sampledAt, fiveHour, sevenDay);
                }
            }

            return latest;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// While the desktop app polls, it samples every 15 minutes at most. Logged Claude usage
    /// further past the latest sample than this means polling has paused (the desktop stops
    /// when its tray usage panel has not been opened recently) and the sample understates usage.
    /// </summary>
    internal static readonly TimeSpan SupersededByActivityAfter = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Fallback for when the OAuth API has no fresher result: uses the desktop sample when it
    /// is newer and no logged Claude usage has superseded it. The auth status is left alone,
    /// so a dead CLI login still prompts for <c>claude /login</c>.
    /// </summary>
    /// <param name="localActivityAt">Time of the latest Claude usage in the local logs.</param>
    internal static ClaudeOAuthOutcome Merge(
        ClaudeOAuthOutcome outcome,
        ClaudeDesktopUsageSample? sample,
        ClaudeOAuthUsageResult? lastKnownApiResult,
        DateTimeOffset? localActivityAt,
        DateTimeOffset now)
    {
        if (sample is null)
        {
            return outcome;
        }

        var supersededByActivity = localActivityAt is { } activity
            && activity - sample.SampledAt > SupersededByActivityAfter;

        if (!supersededByActivity
            && now - sample.SampledAt <= MaxSampleAge
            && (outcome.Result is null || outcome.Result.FetchedAt < sample.SampledAt))
        {
            var desktopResult = ToUsageResult(
                sample, lastKnownApiResult, outcome.SubscriptionType, outcome.RateLimitTier, now);
            return outcome with { Result = desktopResult };
        }

        // A stale sample is dropped rather than shown as current.
        return outcome;
    }

    /// <summary>
    /// Converts a desktop sample into the usage shape the UI reads. The desktop file has no
    /// reset times, so the weekly reset is projected from the last OAuth result. If a weekly
    /// reset happened after the sample, the weekly figure is zeroed.
    /// </summary>
    internal static ClaudeOAuthUsageResult ToUsageResult(
        ClaudeDesktopUsageSample sample,
        ClaudeOAuthUsageResult? lastKnownApiResult,
        string? subscriptionType,
        string? rateLimitTier,
        DateTimeOffset now)
    {
        var weeklyResetsAt = ProjectWeeklyReset(lastKnownApiResult?.SevenDayResetsAt, now);

        // The five-hour window containing the sample started before it, so it has ended by now.
        var fiveHour = now - sample.SampledAt >= FiveHourWindow ? 0 : sample.FiveHourPercent;
        var sevenDay = sample.SevenDayPercent;
        if (weeklyResetsAt is { } nextReset && nextReset - WeekDuration > sample.SampledAt)
        {
            sevenDay = 0;
        }

        return new ClaudeOAuthUsageResult(
            fiveHour,
            FiveHourResetsAt: null,
            sevenDay,
            weeklyResetsAt,
            OverageEnabled: false,
            OverageSpentUsd: null,
            OverageCeilingUsd: null,
            subscriptionType,
            rateLimitTier,
            sample.SampledAt);
    }

    internal static DateTimeOffset? ProjectWeeklyReset(DateTimeOffset? knownReset, DateTimeOffset now)
    {
        if (knownReset is not { } reset)
        {
            return null;
        }

        if (reset > now)
        {
            return reset;
        }

        var weeksBehind = (now - reset).Ticks / WeekDuration.Ticks + 1;
        return reset + TimeSpan.FromTicks(WeekDuration.Ticks * weeksBehind);
    }

    private static bool TryReadInt64(JsonElement element, string name, out long value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(name, out var property)
               && property.ValueKind == JsonValueKind.Number
               && property.TryGetInt64(out value);
    }

    private static bool TryReadDouble(JsonElement element, string name, out double value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(name, out var property)
               && property.ValueKind == JsonValueKind.Number
               && property.TryGetDouble(out value);
    }
}

public sealed record ClaudeDesktopUsageSample(
    DateTimeOffset SampledAt,
    double FiveHourPercent,
    double SevenDayPercent);
