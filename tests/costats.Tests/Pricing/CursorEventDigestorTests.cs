using costats.Application.Pricing;
using costats.Infrastructure.Expense;
using costats.Infrastructure.Providers;
using Xunit;

namespace costats.Tests.Pricing;

public sealed class CursorEventDigestorTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Now);

    [Fact]
    public async Task Digest_aggregates_by_day_and_model_with_cache_buckets()
    {
        var events = new[]
        {
            MakeEvent(Today, "gpt-5", input: 100, cacheRead: 400, cacheWrite: 50, output: 30),
            MakeEvent(Today, "gpt-5", input: 20, cacheRead: 100, cacheWrite: 0, output: 10),
            MakeEvent(Today.AddDays(-1), "gpt-5", input: 5, cacheRead: 0, cacheWrite: 0, output: 5)
        };

        var slices = await CursorEventDigestor.DigestAsync(new MatchingCatalog("gpt-5"), events, Today.AddDays(-29), Today);

        Assert.Equal(2, slices.Count);
        var todaySlice = Assert.Single(slices, s => s.Period == Today);
        Assert.Equal(120, todaySlice.Tokens.StandardInput);
        Assert.Equal(500, todaySlice.Tokens.CachedInput);
        Assert.Equal(50, todaySlice.Tokens.CacheWriteInput);
        Assert.Equal(40, todaySlice.Tokens.GeneratedOutput);
    }

    [Fact]
    public async Task Digest_prices_matched_models_at_catalog_rates()
    {
        var events = new[]
        {
            // Catalog: input 1, output 10, cache read 0.5, cache write 2 (per token).
            MakeEvent(Today, "gpt-5", input: 10, cacheRead: 4, cacheWrite: 2, output: 3, totalCents: 99999)
        };

        var slices = await CursorEventDigestor.DigestAsync(new MatchingCatalog("gpt-5"), events, Today.AddDays(-29), Today);

        var slice = Assert.Single(slices);
        // 10*1 + 3*10 + 4*0.5 + 2*2 = 46; the billed-cents fallback must not be used.
        Assert.Equal(46m, slice.ComputedCostUsd);
    }

    [Fact]
    public async Task Digest_falls_back_to_billed_cents_for_unmatched_models()
    {
        var events = new[]
        {
            MakeEvent(Today, "composer-2.5-fast", input: 537, cacheRead: 153344, cacheWrite: 0, output: 99, totalCents: 15.7274),
            MakeEvent(Today, "composer-2.5-fast", input: 100, cacheRead: 0, cacheWrite: 0, output: 10, totalCents: null)
        };

        var slices = await CursorEventDigestor.DigestAsync(new MatchingCatalog("gpt-5"), events, Today.AddDays(-29), Today);

        var slice = Assert.Single(slices);
        Assert.Equal("composer-2.5-fast", slice.ModelIdentifier);
        // 15.7274 cents => $0.157274; the second event has no cents estimate and adds $0.
        Assert.Equal(0.157274m, slice.ComputedCostUsd, precision: 6);
        Assert.Equal(637, slice.Tokens.StandardInput);
    }

    [Fact]
    public async Task Digest_skips_events_outside_window_and_zero_token_events()
    {
        var events = new[]
        {
            MakeEvent(Today.AddDays(-40), "gpt-5", input: 100, cacheRead: 0, cacheWrite: 0, output: 100),
            MakeEvent(Today, "gpt-5", input: 0, cacheRead: 0, cacheWrite: 0, output: 0)
        };

        var slices = await CursorEventDigestor.DigestAsync(new MatchingCatalog("gpt-5"), events, Today.AddDays(-29), Today);

        Assert.Empty(slices);
    }

    private static CursorUsageEvent MakeEvent(
        DateOnly day,
        string model,
        long input,
        long cacheRead,
        long cacheWrite,
        long output,
        double? totalCents = 1.0)
    {
        var timestamp = new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0)), DateTimeOffset.Now.Offset);
        return new CursorUsageEvent(timestamp, model, input, cacheRead, cacheWrite, output, totalCents);
    }

    private sealed class MatchingCatalog : IPricingCatalog
    {
        private readonly string _knownModel;

        public MatchingCatalog(string knownModel)
        {
            _knownModel = knownModel;
        }

        public ValueTask<ModelPricing?> LookupAsync(string modelId, string? providerHint = null, CancellationToken cancellationToken = default)
        {
            ModelPricing? pricing = string.Equals(modelId, _knownModel, StringComparison.OrdinalIgnoreCase)
                ? new ModelPricing
                {
                    InputCostPerToken = 1m,
                    OutputCostPerToken = 10m,
                    CacheReadInputTokenCost = 0.5m,
                    CacheCreationInputTokenCost = 2m
                }
                : null;
            return ValueTask.FromResult(pricing);
        }
    }
}
