using System.Text.RegularExpressions;

namespace costats.Application.Pricing;

public sealed partial class ModelMatcher
{
    private static readonly string[] ProviderPrefixes =
    [
        "anthropic/",
        "anthropic.",
        "openai/",
        "azure/",
        "bedrock/",
        "vertex_ai/",
        "openrouter/"
    ];

    public ModelMatch? Match(
        string modelId,
        IReadOnlyDictionary<string, ModelPricing> catalog,
        string? providerHint = null)
    {
        if (string.IsNullOrWhiteSpace(modelId) || catalog.Count == 0)
        {
            return null;
        }

        var normalized = modelId.Trim().ToLowerInvariant();
        var candidates = FilterByProvider(catalog, providerHint);
        var attempts = BuildAttempts(normalized);

        foreach (var attempt in attempts)
        {
            if (candidates.TryGetValue(attempt, out var pricing))
            {
                return new ModelMatch(attempt, pricing);
            }
        }

        return LongestPrefixMatch(normalized, candidates, attempts);
    }

    private static Dictionary<string, ModelPricing> FilterByProvider(
        IReadOnlyDictionary<string, ModelPricing> catalog,
        string? providerHint)
    {
        if (string.IsNullOrWhiteSpace(providerHint))
        {
            return catalog.ToDictionary(
                pair => pair.Key.Trim().ToLowerInvariant(),
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
        }

        var normalizedProvider = providerHint.Trim().ToLowerInvariant();
        return catalog
            .Where(pair => pair.Value.LiteLlmProvider?.Equals(normalizedProvider, StringComparison.OrdinalIgnoreCase) == true)
            .ToDictionary(
                pair => pair.Key.Trim().ToLowerInvariant(),
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> BuildAttempts(string normalized)
    {
        var attempts = new List<string>();

        AddAttempt(attempts, normalized);
        foreach (var prefix in ProviderPrefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                AddAttempt(attempts, normalized[prefix.Length..]);
            }
        }

        var snapshot = attempts.ToArray();
        foreach (var attempt in snapshot)
        {
            AddAttempt(attempts, NormalizeVersionSeparator(attempt));
            AddAttempt(attempts, StripDateSuffix(attempt));
            AddAttempt(attempts, StripCodexSuffix(attempt));
        }

        snapshot = attempts.ToArray();
        foreach (var attempt in snapshot)
        {
            AddAttempt(attempts, StripDateSuffix(NormalizeVersionSeparator(attempt)));
            AddAttempt(attempts, StripCodexSuffix(NormalizeVersionSeparator(attempt)));
        }

        return attempts;
    }

    private static ModelMatch? LongestPrefixMatch(
        string modelId,
        IReadOnlyDictionary<string, ModelPricing> candidates,
        IReadOnlyList<string> attempts)
    {
        var stems = attempts.Select(FamilyStem).Where(stem => stem.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        return candidates
            .Where(pair => attempts.Any(attempt => attempt.StartsWith(pair.Key, StringComparison.OrdinalIgnoreCase)))
            .Where(pair => stems.Any(stem => pair.Key.StartsWith(stem, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(pair => pair.Key.Length)
            .Select(pair => new ModelMatch(pair.Key, pair.Value))
            .FirstOrDefault();
    }

    private static string FamilyStem(string value)
    {
        var parts = value.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && parts[0] == "claude")
        {
            return string.Join('-', parts.Take(3));
        }

        if (parts.Length >= 2 && parts[0] == "gpt")
        {
            return string.Join('-', parts.Take(2));
        }

        return parts.Length == 0 ? string.Empty : parts[0];
    }

    private static void AddAttempt(List<string> attempts, string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !attempts.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            attempts.Add(value);
        }
    }

    private static string NormalizeVersionSeparator(string value)
    {
        if (!value.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        return SemanticVersionSuffixRegex().Replace(value, "-$1.$2");
    }

    private static string StripDateSuffix(string value) => DateSuffixRegex().Replace(value, string.Empty);

    private static string StripCodexSuffix(string value) => value.EndsWith("-codex", StringComparison.OrdinalIgnoreCase)
        ? value[..^"-codex".Length]
        : value;

    [GeneratedRegex(@"-(\d+)-(\d+)(?=-|$)", RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersionSuffixRegex();

    [GeneratedRegex(@"-\d{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex DateSuffixRegex();
}

public sealed record ModelMatch(string ModelId, ModelPricing Pricing);
