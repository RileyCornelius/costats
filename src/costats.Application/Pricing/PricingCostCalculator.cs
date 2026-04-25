using costats.Core.Pulse;

namespace costats.Application.Pricing;

public static class PricingCostCalculator
{
    public static decimal ComputeCost(ModelPricing pricing, TokenLedger ledger)
    {
        return RateCardMath.ComputeTieredCost(ledger.StandardInput, pricing.InputCostPerToken, pricing.InputCostPerTokenAbove200k)
            + RateCardMath.ComputeTieredCost(ledger.GeneratedOutput, pricing.OutputCostPerToken, pricing.OutputCostPerTokenAbove200k)
            + RateCardMath.ComputeTieredCost(ledger.CachedInput, pricing.CacheReadInputTokenCost, pricing.CacheReadInputTokenCostAbove200k)
            + RateCardMath.ComputeTieredCost(ledger.CacheWriteInput, pricing.CacheCreationInputTokenCost, pricing.CacheCreationInputTokenCostAbove200k);
    }
}
