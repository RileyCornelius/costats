using costats.Infrastructure.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace costats.Tests.Providers;

public sealed class CursorUsageFetcherTests
{
    [Fact]
    public void ParsePayload_ParsesFullSummary()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cursor-usage-summary.json"));

        var payload = CursorUsageFetcher.ParsePayload(json);

        Assert.NotNull(payload);
        Assert.Equal(42.5, payload.PlanPercentUsed);
        Assert.Equal(1234, payload.OnDemandUsedCents);
        Assert.Equal(5000, payload.OnDemandLimitCents);
        Assert.Equal(DateTimeOffset.Parse("2026-08-03T18:57:21.000Z"), payload.BillingCycleEnd);
        Assert.Equal("pro", payload.MembershipType);
        Assert.Null(payload.Email);
    }

    [Fact]
    public void ParsePayload_PrefersTotalPercentUsed_OverUsedOverLimit()
    {
        // plan.limit is often the subscription price in cents, so used/limit (10%) must lose
        // to totalPercentUsed.
        const string json = """
            {
              "individualUsage": {
                "plan": { "used": 200, "limit": 2000, "totalPercentUsed": 42.5 }
              }
            }
            """;

        var payload = CursorUsageFetcher.ParsePayload(json);

        Assert.NotNull(payload);
        Assert.Equal(42.5, payload.PlanPercentUsed);
    }

    [Fact]
    public void ParsePayload_FallsBackToAutoApiAverage_WhenTotalPercentMissing()
    {
        const string json = """
            {
              "individualUsage": {
                "plan": { "autoPercentUsed": 20.0, "apiPercentUsed": 40.0 }
              }
            }
            """;

        var payload = CursorUsageFetcher.ParsePayload(json);

        Assert.NotNull(payload);
        Assert.Equal(30.0, payload.PlanPercentUsed);
    }

    [Fact]
    public void ParsePayload_FallsBackToSinglePercent_ThenUsedOverLimit()
    {
        var single = CursorUsageFetcher.ParsePayload("""
            { "individualUsage": { "plan": { "apiPercentUsed": 12.0 } } }
            """);
        Assert.NotNull(single);
        Assert.Equal(12.0, single.PlanPercentUsed);

        var ratio = CursorUsageFetcher.ParsePayload("""
            { "individualUsage": { "plan": { "used": 500, "limit": 2000 } } }
            """);
        Assert.NotNull(ratio);
        Assert.Equal(25.0, ratio.PlanPercentUsed);
    }

    [Theory]
    [InlineData(150.0, 100.0)]
    [InlineData(-5.0, 0.0)]
    public void ParsePayload_ClampsPercentToValidRange(double raw, double expected)
    {
        var payload = CursorUsageFetcher.ParsePayload($$"""
            { "individualUsage": { "plan": { "totalPercentUsed": {{raw}} } } }
            """);

        Assert.NotNull(payload);
        Assert.Equal(expected, payload.PlanPercentUsed);
    }

    [Fact]
    public void ParsePayload_ToleratesMissingFields()
    {
        var payload = CursorUsageFetcher.ParsePayload("{}");

        Assert.NotNull(payload);
        Assert.Null(payload.PlanPercentUsed);
        Assert.Null(payload.OnDemandUsedCents);
        Assert.Null(payload.OnDemandLimitCents);
        Assert.Null(payload.BillingCycleEnd);
        Assert.Null(payload.MembershipType);
    }

    [Fact]
    public void ParsePayload_HandlesNullOnDemandLimit()
    {
        // limit: null means no on-demand spending limit is configured.
        const string json = """
            {
              "individualUsage": {
                "onDemand": { "enabled": false, "used": 0, "limit": null, "remaining": null }
              }
            }
            """;

        var payload = CursorUsageFetcher.ParsePayload(json);

        Assert.NotNull(payload);
        Assert.Equal(0, payload.OnDemandUsedCents);
        Assert.Null(payload.OnDemandLimitCents);
    }

    [Fact]
    public void ParsePayload_ReturnsNull_OnInvalidJson()
    {
        Assert.Null(CursorUsageFetcher.ParsePayload("not json"));
    }

    [Fact]
    public async Task FetchAsync_ReturnsMissingToken_WithoutCookie()
    {
        using var fetcher = new CursorUsageFetcher(NullLogger<CursorUsageFetcher>.Instance);

        var result = await fetcher.FetchAsync(null, CancellationToken.None);

        Assert.Equal(CursorFetchStatus.MissingToken, result.Status);
        Assert.Null(result.Payload);
    }
}
