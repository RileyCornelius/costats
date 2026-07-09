using costats.Application.Pricing;
using costats.Core.Pulse;
using costats.Infrastructure.Providers;

namespace costats.Infrastructure.Expense;

/// <summary>
/// Aggregates Cursor dashboard usage events into daily per-model consumption slices.
/// Models with a pricing-catalog entry are priced at provider list rates (the same
/// semantics as the Codex/Claude digests); Cursor-proprietary models with no catalog
/// entry fall back to Cursor's own API-value estimate (tokenUsage.totalCents) instead
/// of contributing $0.
/// </summary>
public static class CursorEventDigestor
{
    public static async Task<IReadOnlyList<ConsumptionSlice>> DigestAsync(
        IPricingCatalog pricingCatalog,
        IReadOnlyList<CursorUsageEvent> events,
        DateOnly since,
        DateOnly until,
        CancellationToken cancellationToken = default)
    {
        var aggregates = new Dictionary<DateOnly, Dictionary<string, SliceAccumulator>>();

        foreach (var usageEvent in events)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var period = DateOnly.FromDateTime(usageEvent.Timestamp.LocalDateTime);
            if (period < since || period > until)
            {
                continue;
            }

            var ledger = new TokenLedger
            {
                StandardInput = ClampToInt(usageEvent.InputTokens),
                CachedInput = ClampToInt(usageEvent.CacheReadTokens),
                CacheWriteInput = ClampToInt(usageEvent.CacheWriteTokens),
                GeneratedOutput = ClampToInt(usageEvent.OutputTokens),
                ReasoningOutput = 0
            };

            if (ledger.TotalConsumed == 0)
            {
                continue;
            }

            var pricing = await pricingCatalog.LookupAsync(usageEvent.Model, null, cancellationToken).ConfigureAwait(false);
            var cost = pricing is not null
                ? PricingCostCalculator.ComputeCost(pricing, ledger)
                : (decimal)(usageEvent.TotalCents ?? 0) / 100m;

            ConsumptionSliceAggregator.Add(aggregates, period, usageEvent.Model, ledger, cost);
        }

        return ConsumptionSliceAggregator.Build(aggregates);
    }

    private static int ClampToInt(long value) => value > int.MaxValue ? int.MaxValue : (int)Math.Max(0, value);
}
