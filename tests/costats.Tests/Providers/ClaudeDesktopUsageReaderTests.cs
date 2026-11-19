using costats.Infrastructure.Providers;
using Xunit;

namespace costats.Tests.Providers;

public sealed class ClaudeDesktopUsageReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ParseLatest_PicksNewestSample_AndSkipsMalformedOnes()
    {
        var json = $$"""
            { "version": 2, "samples": [
              { "t": {{Now.AddMinutes(-30).ToUnixTimeMilliseconds()}}, "org": "a", "u": { "fh": 10, "sd": 20 } },
              { "t": {{Now.AddMinutes(-15).ToUnixTimeMilliseconds()}}, "org": "a", "u": { "fh": 12, "sd": 21, "xu": 69.59 } },
              { "t": {{Now.ToUnixTimeMilliseconds()}}, "org": "a", "u": { "fh": "bad" } },
              { "t": {{Now.ToUnixTimeMilliseconds()}}, "org": "a", "u": 5 },
              42
            ] }
            """;

        var sample = ClaudeDesktopUsageReader.ParseLatest(json);

        Assert.NotNull(sample);
        Assert.Equal(Now.AddMinutes(-15), sample.SampledAt);
        Assert.Equal(12, sample.FiveHourPercent);
        Assert.Equal(21, sample.SevenDayPercent);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "version": 3 }""")]
    [InlineData("""{ "samples": [] }""")]
    [InlineData("""[]""")]
    public void ParseLatest_UnexpectedShape_ReturnsNull(string json)
    {
        Assert.Null(ClaudeDesktopUsageReader.ParseLatest(json));
    }

    [Fact]
    public async Task ReadLatestAsync_MissingFile_ReturnsNull()
    {
        var reader = new ClaudeDesktopUsageReader(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));

        Assert.Null(await reader.ReadLatestAsync(CancellationToken.None));
    }

    [Fact]
    public void Merge_FreshDesktopSample_FillsUsageButKeepsCliSignInAlert()
    {
        var cliOutcome = new ClaudeOAuthOutcome(null, ClaudeAuthStatus.Expired, "max", "tier");
        var sample = new ClaudeDesktopUsageSample(Now.AddMinutes(-10), 42, 17);

        var merged = ClaudeDesktopUsageReader.Merge(cliOutcome, sample, lastKnownApiResult: null, localActivityAt: null, Now);

        // A dead CLI login still needs claude /login, or the numbers stop updating.
        Assert.Equal(ClaudeAuthStatus.Expired, merged.Status);
        Assert.Equal(42, merged.Result!.FiveHourUsedPercent);
        Assert.Equal(17, merged.Result.SevenDayUsedPercent);
        Assert.Equal(sample.SampledAt, merged.Result.FetchedAt);
        Assert.Equal("max", merged.Result.SubscriptionType);
    }

    [Fact]
    public void Merge_NewerApiResult_Wins()
    {
        var api = ApiResult(fetchedAt: Now, sevenDayResetsAt: Now.AddDays(2));
        var cliOutcome = new ClaudeOAuthOutcome(api, ClaudeAuthStatus.Ok, "pro", null);
        var sample = new ClaudeDesktopUsageSample(Now.AddMinutes(-10), 99, 99);

        var merged = ClaudeDesktopUsageReader.Merge(cliOutcome, sample, api, localActivityAt: null, Now);

        Assert.Same(cliOutcome, merged);
    }

    [Fact]
    public void Merge_StaleDesktopSample_IsIgnored()
    {
        var cliOutcome = new ClaudeOAuthOutcome(null, ClaudeAuthStatus.Expired, "pro", null);
        var sample = new ClaudeDesktopUsageSample(Now - ClaudeDesktopUsageReader.MaxSampleAge - TimeSpan.FromMinutes(1), 5, 5);

        var merged = ClaudeDesktopUsageReader.Merge(cliOutcome, sample, lastKnownApiResult: null, localActivityAt: null, Now);

        Assert.Same(cliOutcome, merged);
    }

    [Fact]
    public void Merge_NoDesktopApp_KeepsCliSignInAlert()
    {
        var cliOutcome = new ClaudeOAuthOutcome(null, ClaudeAuthStatus.Expired, "pro", null);

        var merged = ClaudeDesktopUsageReader.Merge(cliOutcome, sample: null, lastKnownApiResult: null, localActivityAt: null, Now);

        Assert.Same(cliOutcome, merged);
    }

    [Fact]
    public void Merge_LoggedUsageLongAfterSample_DropsStaleSample()
    {
        // Desktop polling paused after its startup fetch while Claude Code kept working.
        var cliOutcome = new ClaudeOAuthOutcome(null, ClaudeAuthStatus.NoCredentials, null, null);
        var sample = new ClaudeDesktopUsageSample(Now.AddHours(-1), 0, 3);

        var merged = ClaudeDesktopUsageReader.Merge(
            cliOutcome, sample, lastKnownApiResult: null, localActivityAt: Now.AddMinutes(-2), Now);

        Assert.Same(cliOutcome, merged);
    }

    [Fact]
    public void Merge_LoggedUsageWithinPollInterval_KeepsSample()
    {
        var cliOutcome = new ClaudeOAuthOutcome(null, ClaudeAuthStatus.NoCredentials, null, null);
        var sample = new ClaudeDesktopUsageSample(Now.AddMinutes(-12), 16, 5);

        var merged = ClaudeDesktopUsageReader.Merge(
            cliOutcome, sample, lastKnownApiResult: null, localActivityAt: Now.AddMinutes(-1), Now);

        Assert.Equal(16, merged.Result!.FiveHourUsedPercent);
    }

    [Fact]
    public void Merge_StaleSampleButFreshApiResult_KeepsApiResult()
    {
        var api = ApiResult(fetchedAt: Now, sevenDayResetsAt: Now.AddDays(2));
        var cliOutcome = new ClaudeOAuthOutcome(api, ClaudeAuthStatus.Ok, "pro", null);
        var sample = new ClaudeDesktopUsageSample(Now.AddHours(-1), 0, 3);

        var merged = ClaudeDesktopUsageReader.Merge(
            cliOutcome, sample, api, localActivityAt: Now.AddMinutes(-2), Now);

        Assert.Same(cliOutcome, merged);
    }

    [Fact]
    public void ToUsageResult_ProjectsWeeklyResetForward_AndZeroesWeekAfterReset()
    {
        // The last exact API value is three weeks old: project it forward whole weeks. The
        // sample predates the five-hour window but not the last weekly boundary.
        var lastKnown = ApiResult(fetchedAt: Now.AddDays(-22), sevenDayResetsAt: Now.AddDays(-20).AddHours(1));
        var sample = new ClaudeDesktopUsageSample(Now.AddHours(-5.5), 30, 60);

        var result = ClaudeDesktopUsageReader.ToUsageResult(sample, lastKnown, "pro", null, Now);

        Assert.Equal(Now.AddDays(1).AddHours(1), result.SevenDayResetsAt);
        Assert.Equal(0, result.FiveHourUsedPercent);
        Assert.Equal(60, result.SevenDayUsedPercent);
    }

    [Fact]
    public void ToUsageResult_WeeklyResetAfterSample_ZeroesWeeklyPercent()
    {
        var lastKnown = ApiResult(fetchedAt: Now.AddDays(-8), sevenDayResetsAt: Now.AddDays(-1));
        var sample = new ClaudeDesktopUsageSample(Now.AddDays(-1).AddMinutes(-30), 0, 80);

        var result = ClaudeDesktopUsageReader.ToUsageResult(sample, lastKnown, "pro", null, Now);

        Assert.Equal(Now.AddDays(6), result.SevenDayResetsAt);
        Assert.Equal(0, result.SevenDayUsedPercent);
    }

    private static ClaudeOAuthUsageResult ApiResult(DateTimeOffset fetchedAt, DateTimeOffset sevenDayResetsAt)
        => new(10, fetchedAt.AddHours(2), 20, sevenDayResetsAt, false, null, null, "pro", null, fetchedAt);
}
