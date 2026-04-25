using costats.Application.Pricing;
using Xunit;

namespace costats.Tests.Pricing;

public sealed class ModelMatcherTests
{
    private static readonly IReadOnlyDictionary<string, ModelPricing> Catalog = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-opus-4-7"] = Anthropic(0.000015m),
        ["claude-opus-4-6"] = Anthropic(0.000015m),
        ["claude-opus-4"] = Anthropic(0.000015m),
        ["claude-sonnet-4-6"] = Anthropic(0.000003m),
        ["claude-sonnet-4"] = Anthropic(0.000003m),
        ["claude-haiku-4-5"] = Anthropic(0.000001m),
        ["claude-opus-4-5"] = Anthropic(0.000005m),
        ["gpt-5-codex"] = OpenAi(0.00000125m),
        ["gpt-5"] = OpenAi(0.00000125m),
        ["gpt-5.3-codex"] = OpenAi(0.000002m)
    };

    [Theory]
    [InlineData("claude-opus-4-7", "claude-opus-4-7")]
    [InlineData("claude-opus-4-6", "claude-opus-4-6")]
    [InlineData("claude-sonnet-4-6", "claude-sonnet-4-6")]
    [InlineData("claude-haiku-4-5-20251001", "claude-haiku-4-5")]
    [InlineData("anthropic/claude-opus-4-5", "claude-opus-4-5")]
    [InlineData("gpt-5-codex", "gpt-5-codex")]
    [InlineData("openai/gpt-5", "gpt-5")]
    [InlineData("gpt-5-3-codex", "gpt-5.3-codex")]
    public void Match_resolves_expected_model(string input, string expected)
    {
        var match = new ModelMatcher().Match(input, Catalog);

        Assert.NotNull(match);
        Assert.Equal(expected, match.ModelId);
    }

    [Fact]
    public void Match_restricts_candidates_by_provider_hint()
    {
        var catalog = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase)
        {
            ["vendor/model"] = OpenAi(1m),
            ["model"] = Anthropic(2m)
        };

        var match = new ModelMatcher().Match("model", catalog, "anthropic");

        Assert.NotNull(match);
        Assert.Equal("model", match.ModelId);
        Assert.Equal(2m, match.Pricing.InputCostPerToken);
    }

    private static ModelPricing Anthropic(decimal input) => new()
    {
        LiteLlmProvider = "anthropic",
        InputCostPerToken = input,
        OutputCostPerToken = input * 5
    };

    private static ModelPricing OpenAi(decimal input) => new()
    {
        LiteLlmProvider = "openai",
        InputCostPerToken = input,
        OutputCostPerToken = input * 8
    };
}
