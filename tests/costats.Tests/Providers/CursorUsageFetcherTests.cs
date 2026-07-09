using System.Net;
using System.Text;
using System.Text.Json;
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
        Assert.Equal(30.5, payload.FirstPartyPercentUsed);
        Assert.Equal(55.0, payload.ApiPercentUsed);
        Assert.Equal(1234, payload.OnDemandUsedCents);
        Assert.Equal(5000, payload.OnDemandLimitCents);
        Assert.Equal(DateTimeOffset.Parse("2026-08-03T18:57:21.000Z"), payload.BillingCycleEnd);
        Assert.Equal("pro", payload.MembershipType);
        Assert.Null(payload.Email);
    }

    [Fact]
    public void ParsePayload_FirstPartyFallsBackToTotalPercent_WhenAutoMissing()
    {
        const string json = """
            {
              "individualUsage": {
                "plan": { "used": 200, "limit": 2000, "totalPercentUsed": 42.5 }
              }
            }
            """;

        var payload = CursorUsageFetcher.ParsePayload(json);

        Assert.NotNull(payload);
        Assert.Equal(42.5, payload.FirstPartyPercentUsed);
        Assert.Null(payload.ApiPercentUsed);
    }

    [Fact]
    public void ParsePayload_FirstPartyFallsBackToUsedOverLimit_WhenPercentagesMissing()
    {
        var payload = CursorUsageFetcher.ParsePayload("""
            { "individualUsage": { "plan": { "used": 500, "limit": 2000 } } }
            """);

        Assert.NotNull(payload);
        Assert.Equal(25.0, payload.FirstPartyPercentUsed);
        Assert.Null(payload.ApiPercentUsed);
    }

    [Fact]
    public void ParsePayload_KeepsFirstPartyAndApiIndependent()
    {
        // autoPercentUsed must win over totalPercentUsed for the first-party bar, and the
        // API bar must not inherit any fallback.
        const string json = """
            {
              "individualUsage": {
                "plan": { "autoPercentUsed": 2.0, "apiPercentUsed": 12.0, "totalPercentUsed": 4.0 }
              }
            }
            """;

        var payload = CursorUsageFetcher.ParsePayload(json);

        Assert.NotNull(payload);
        Assert.Equal(2.0, payload.FirstPartyPercentUsed);
        Assert.Equal(12.0, payload.ApiPercentUsed);
    }

    [Theory]
    [InlineData(150.0, 100.0)]
    [InlineData(-5.0, 0.0)]
    public void ParsePayload_ClampsPercentagesToValidRange(double raw, double expected)
    {
        var payload = CursorUsageFetcher.ParsePayload($$"""
            { "individualUsage": { "plan": { "autoPercentUsed": {{raw}}, "apiPercentUsed": {{raw}} } } }
            """);

        Assert.NotNull(payload);
        Assert.Equal(expected, payload.FirstPartyPercentUsed);
        Assert.Equal(expected, payload.ApiPercentUsed);
    }

    [Fact]
    public void ParsePayload_ToleratesMissingFields()
    {
        var payload = CursorUsageFetcher.ParsePayload("{}");

        Assert.NotNull(payload);
        Assert.Null(payload.FirstPartyPercentUsed);
        Assert.Null(payload.ApiPercentUsed);
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
    public void ParseUsageEventsPage_ParsesVerifiedResponseShape()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cursor-usage-events.json"));

        var page = CursorUsageFetcher.ParseUsageEventsPage(json);

        Assert.NotNull(page);
        Assert.Equal(34, page.TotalCount);
        Assert.Equal(3, page.Events.Count);

        var first = page.Events[0];
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1783635383682), first.Timestamp);
        Assert.Equal("gpt-5.6-sol-high", first.Model);
        Assert.Equal(124067, first.InputTokens);
        Assert.Equal(2636480, first.CacheReadTokens);
        Assert.Equal(0, first.CacheWriteTokens);
        Assert.Equal(17787, first.OutputTokens);
        Assert.NotNull(first.TotalCents);
        Assert.Equal(247.218505859375, first.TotalCents.Value, precision: 6);

        // Second event carries cacheWriteTokens and string-encoded numbers.
        var second = page.Events[1];
        Assert.Equal("claude-4.5-sonnet", second.Model);
        Assert.Equal(5000, second.InputTokens);
        Assert.Equal(1200, second.CacheWriteTokens);
    }

    [Fact]
    public void ParseUsageEventsPage_SkipsEventsWithoutTimestamp_AndToleratesMissingTokenUsage()
    {
        const string json = """
            {
              "totalUsageEventsCount": 3,
              "usageEventsDisplay": [
                { "model": "no-timestamp", "tokenUsage": { "inputTokens": 10 } },
                { "timestamp": "1783635383682" },
                { "timestamp": "1783635383683", "model": "gpt-5", "tokenUsage": { "inputTokens": 7 } }
              ]
            }
            """;

        var page = CursorUsageFetcher.ParseUsageEventsPage(json);

        Assert.NotNull(page);
        Assert.Equal(2, page.Events.Count);
        Assert.Equal("unknown", page.Events[0].Model);
        Assert.Equal(0, page.Events[0].InputTokens);
        Assert.Null(page.Events[0].TotalCents);
        Assert.Equal(7, page.Events[1].InputTokens);
    }

    [Fact]
    public void ParseUsageEventsPage_ReturnsEmpty_WhenNoEventsArray()
    {
        var page = CursorUsageFetcher.ParseUsageEventsPage("""{ "totalUsageEventsCount": 0 }""");

        Assert.NotNull(page);
        Assert.Equal(0, page.TotalCount);
        Assert.Empty(page.Events);
    }

    [Fact]
    public void ParseUsageEventsPage_ReturnsNull_OnInvalidJson()
    {
        Assert.Null(CursorUsageFetcher.ParseUsageEventsPage("not json"));
    }

    [Fact]
    public async Task FetchAsync_ReturnsMissingToken_WithoutCookie()
    {
        using var fetcher = new CursorUsageFetcher(NullLogger<CursorUsageFetcher>.Instance);

        var result = await fetcher.FetchAsync(null, CancellationToken.None);

        Assert.Equal(CursorFetchStatus.MissingToken, result.Status);
        Assert.Null(result.Payload);
    }

    [Fact]
    public async Task FetchUsageEventsAsync_ReturnsMissingToken_WithoutCookie()
    {
        using var fetcher = new CursorUsageFetcher(NullLogger<CursorUsageFetcher>.Instance);

        var result = await fetcher.FetchUsageEventsAsync(null, DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(CursorFetchStatus.MissingToken, result.Status);
        Assert.Empty(result.Events);
    }

    [Fact]
    public async Task FetchUsageEventsAsync_PaginatesUntilTotalCountReached()
    {
        // Page size is 100: serve one full page then a partial one and expect both requests.
        var handler = new StubHandler(request =>
        {
            var body = request.Content!.ReadAsStringAsync().Result;
            using var doc = JsonDocument.Parse(body);
            var page = doc.RootElement.GetProperty("page").GetInt32();
            var eventCount = page == 1 ? 100 : 1;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(BuildEventsJson(totalCount: 101, count: eventCount, startTimestamp: page * 1_000_000), Encoding.UTF8, "application/json")
            };
        });
        using var fetcher = new CursorUsageFetcher(NullLogger<CursorUsageFetcher>.Instance, handler);

        var result = await fetcher.FetchUsageEventsAsync("WorkosCursorSessionToken=x", DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(CursorFetchStatus.Success, result.Status);
        Assert.Equal(101, result.Events.Count);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task FetchUsageEventsAsync_StopsAfterPartialPage()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(BuildEventsJson(totalCount: 34, count: 34, startTimestamp: 1_000_000), Encoding.UTF8, "application/json")
        });
        using var fetcher = new CursorUsageFetcher(NullLogger<CursorUsageFetcher>.Instance, handler);

        var result = await fetcher.FetchUsageEventsAsync("WorkosCursorSessionToken=x", DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(CursorFetchStatus.Success, result.Status);
        Assert.Equal(34, result.Events.Count);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task FetchUsageEventsAsync_ReturnsInvalidToken_On401()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var fetcher = new CursorUsageFetcher(NullLogger<CursorUsageFetcher>.Instance, handler);

        var result = await fetcher.FetchUsageEventsAsync("WorkosCursorSessionToken=x", DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(CursorFetchStatus.InvalidToken, result.Status);
        Assert.Empty(result.Events);
    }

    private static string BuildEventsJson(int totalCount, int count, long startTimestamp)
    {
        var events = Enumerable.Range(0, count).Select(i =>
            $$"""{ "timestamp": "{{startTimestamp + i}}", "model": "gpt-5", "tokenUsage": { "inputTokens": 1, "outputTokens": 1, "cacheReadTokens": 0, "totalCents": 0.5 } }""");
        return $$"""{ "totalUsageEventsCount": {{totalCount}}, "usageEventsDisplay": [{{string.Join(",", events)}}] }""";
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(_responder(request));
        }
    }
}
