using System.Collections.Concurrent;
using System.Text.Json;
using costats.Application.Pricing;
using Microsoft.Extensions.Logging;

namespace costats.Infrastructure.Pricing;

public sealed class PricingCatalog : IPricingCatalog
{
    private readonly PricingDiskCache _diskCache;
    private readonly LiteLLMPricingClient _liteLlmClient;
    private readonly OpenRouterPricingClient _openRouterClient;
    private readonly EmbeddedPricingSnapshot _embeddedSnapshot;
    private readonly ModelMatcher _matcher;
    private readonly ILogger<PricingCatalog> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly ConcurrentDictionary<string, ModelPricing?> _lookupCache = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyDictionary<string, ModelPricing>? _pricing;

    public PricingCatalog(
        PricingDiskCache diskCache,
        LiteLLMPricingClient liteLlmClient,
        OpenRouterPricingClient openRouterClient,
        EmbeddedPricingSnapshot embeddedSnapshot,
        ModelMatcher matcher,
        ILogger<PricingCatalog> logger)
    {
        _diskCache = diskCache;
        _liteLlmClient = liteLlmClient;
        _openRouterClient = openRouterClient;
        _embeddedSnapshot = embeddedSnapshot;
        _matcher = matcher;
        _logger = logger;
    }

    public async ValueTask<ModelPricing?> LookupAsync(
        string modelId,
        string? providerHint = null,
        CancellationToken cancellationToken = default)
    {
        var pricing = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        var cacheKey = $"{providerHint ?? string.Empty}|{modelId}";

        return _lookupCache.GetOrAdd(cacheKey, _ =>
        {
            var match = _matcher.Match(modelId, pricing, providerHint);
            if (match is null)
            {
                _logger.LogWarning("No pricing match found for model {ModelId} with provider hint {ProviderHint}", modelId, providerHint);
            }

            return match?.Pricing;
        });
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _pricing = await LoadFreshOrFallbackAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
            _lookupCache.Clear();
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<IReadOnlyDictionary<string, ModelPricing>> EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_pricing is not null)
        {
            return _pricing;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _pricing ??= await LoadFreshOrFallbackAsync(forceRefresh: false, cancellationToken).ConfigureAwait(false);
            return _pricing;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<IReadOnlyDictionary<string, ModelPricing>> LoadFreshOrFallbackAsync(
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        if (!forceRefresh)
        {
            var fresh = await _diskCache.ReadFreshAsync(cancellationToken).ConfigureAwait(false);
            if (fresh is { Count: > 0 })
            {
                return fresh;
            }
        }

        try
        {
            var live = await _liteLlmClient.FetchAsync(cancellationToken).ConfigureAwait(false);
            if (live.Count > 0)
            {
                await _diskCache.WriteAsync(live, cancellationToken).ConfigureAwait(false);
                return live;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "LiteLLM pricing refresh failed");
        }

        try
        {
            var openRouter = await _openRouterClient.FetchAsync(cancellationToken).ConfigureAwait(false);
            if (openRouter.Count > 0)
            {
                return openRouter;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "OpenRouter pricing fallback failed");
        }

        var stale = await _diskCache.ReadAnyAgeAsync(cancellationToken).ConfigureAwait(false);
        if (stale is { Count: > 0 })
        {
            _logger.LogInformation("Using stale pricing cache");
            return stale;
        }

        _logger.LogInformation("Using embedded pricing snapshot");
        return await _embeddedSnapshot.LoadAsync(cancellationToken).ConfigureAwait(false);
    }
}
