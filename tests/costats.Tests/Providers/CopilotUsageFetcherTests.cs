using costats.Infrastructure.Providers;
using Xunit;

namespace costats.Tests.Providers;

public sealed class CopilotUsageFetcherTests
{
    [Fact]
    public void ParsePayload_ParsesFullProPayload()
    {
        const string json = """
            {
              "quota_snapshots": {
                "premium_interactions": {
                  "entitlement": 300,
                  "remaining": 279,
                  "percent_remaining": 93.0,
                  "unlimited": false,
                  "overage_permitted": false
                },
                "chat": {
                  "unlimited": true
                }
              }
            }
            """;

        var payload = CopilotUsageFetcher.ParsePayload(json);

        Assert.NotNull(payload);
        Assert.NotNull(payload.Premium);
        Assert.Equal(21, payload.Premium.Used);
        Assert.NotNull(payload.Chat);
        Assert.True(payload.Chat.Unlimited);
    }

    [Fact]
    public void ParsePayload_PlaceholderPremium_IsNull()
    {
        const string json = """
            {
              "quota_snapshots": {
                "premium_interactions": {},
                "chat": {
                  "entitlement": 50,
                  "remaining": 45,
                  "unlimited": false
                }
              }
            }
            """;

        var payload = CopilotUsageFetcher.ParsePayload(json);

        Assert.NotNull(payload);
        Assert.Null(payload.Premium);
        Assert.NotNull(payload.Chat);
        Assert.Equal(50, payload.Chat.Entitlement);
        Assert.Equal(45, payload.Chat.Remaining);
    }

    [Fact]
    public void ParsePayload_PlaceholderPremium_FallsBackToFreeQuotasPerLane()
    {
        const string json = """
            {
              "quota_snapshots": {
                "premium_interactions": {}
              },
              "limited_user_quotas": { "chat": 45, "completions": 1900 },
              "monthly_quotas": { "chat": 50, "completions": 2000 }
            }
            """;

        var payload = CopilotUsageFetcher.ParsePayload(json);

        Assert.NotNull(payload);
        Assert.Null(payload.Premium);

        Assert.NotNull(payload.Chat);
        Assert.Equal(50, payload.Chat.Entitlement);
        Assert.Equal(45, payload.Chat.Remaining);
        Assert.Equal(5, payload.Chat.Used);

        Assert.NotNull(payload.Completions);
        Assert.Equal(2000, payload.Completions.Entitlement);
        Assert.Equal(1900, payload.Completions.Remaining);
    }

    [Fact]
    public void ParsePayload_PartialSnapshot_MissingRemaining_IsNull()
    {
        const string json = """
            {
              "quota_snapshots": {
                "chat": { "entitlement": 120, "quota_id": "chat" }
              }
            }
            """;

        var payload = CopilotUsageFetcher.ParsePayload(json);

        Assert.NotNull(payload);
        Assert.Null(payload.Chat);
    }

    [Fact]
    public void ParsePayload_FreePlanOnly_ComputesChatAndCompletions()
    {
        const string json = """
            {
              "limited_user_quotas": { "chat": 45, "completions": 1900 },
              "monthly_quotas": { "chat": 50, "completions": 2000 }
            }
            """;

        var payload = CopilotUsageFetcher.ParsePayload(json);

        Assert.NotNull(payload);
        Assert.Null(payload.Premium);
        Assert.NotNull(payload.Chat);
        Assert.Equal(50, payload.Chat.Entitlement);
        Assert.Equal(45, payload.Chat.Remaining);
        Assert.NotNull(payload.Completions);
        Assert.Equal(2000, payload.Completions.Entitlement);
        Assert.Equal(1900, payload.Completions.Remaining);
    }

    [Fact]
    public void ParsePayload_PrefersQuotaResetDateUtc_OverQuotaResetDate()
    {
        const string json = """
            {
              "quota_reset_date_utc": "2026-08-03T18:57:21.000Z",
              "quota_reset_date": "2026-08-01"
            }
            """;

        var payload = CopilotUsageFetcher.ParsePayload(json);

        Assert.NotNull(payload);
        Assert.Equal(DateTimeOffset.Parse("2026-08-03T18:57:21.000Z"), payload.QuotaResetAt);
    }

    [Fact]
    public void ParsePayload_ReturnsNull_OnInvalidJson()
    {
        Assert.Null(CopilotUsageFetcher.ParsePayload("not json"));
    }
}
