using System.Text.Json;
using costats.Application.Pricing;
using Microsoft.Extensions.Options;

namespace costats.Infrastructure.Pricing;

public sealed class PricingDiskCache
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly IOptions<PricingOptions> _options;

    public PricingDiskCache(IOptions<PricingOptions> options)
    {
        _options = options;
    }

    public string LiteLlmCachePath => Path.Combine(_options.Value.GetCacheDirectory(), "litellm.json");

    public async Task<IReadOnlyDictionary<string, ModelPricing>?> ReadFreshAsync(CancellationToken cancellationToken)
    {
        var path = LiteLlmCachePath;
        if (!File.Exists(path))
        {
            return null;
        }

        var age = DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(path);
        if (age > _options.Value.RefreshInterval)
        {
            return null;
        }

        return await ReadAnyAgeAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, ModelPricing>?> ReadAnyAgeAsync(CancellationToken cancellationToken)
    {
        var path = LiteLlmCachePath;
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await LiteLLMPricingClient.ParseAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteAsync(IReadOnlyDictionary<string, ModelPricing> pricing, CancellationToken cancellationToken)
    {
        var path = LiteLlmCachePath;
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, pricing, JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        if (File.Exists(path))
        {
            File.Replace(tempPath, path, null);
        }
        else
        {
            File.Move(tempPath, path);
        }
    }
}
