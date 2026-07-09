using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace costats.Infrastructure.Providers;

public sealed class CursorUsageFetcher : IDisposable
{
    private const string BaseUrl = "https://cursor.com/";
    private const string UsageSummaryPath = "api/usage-summary";
    private const string AuthMePath = "api/auth/me";
    private const string UsageEventsPath = "api/dashboard/get-filtered-usage-events";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) costats";
    private const int EventsPageSize = 100;
    private const int EventsMaxPages = 50;
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(800)
    ];

    private readonly HttpClient _httpClient;
    private readonly ILogger<CursorUsageFetcher> _logger;

    public CursorUsageFetcher(ILogger<CursorUsageFetcher> logger, HttpMessageHandler? httpMessageHandler = null)
    {
        _logger = logger;
        _httpClient = httpMessageHandler is null ? new HttpClient() : new HttpClient(httpMessageHandler);
        _httpClient.BaseAddress = new Uri(BaseUrl);
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public async Task<CursorUsageFetchResult> FetchAsync(string? cookieHeader, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            return CursorUsageFetchResult.MissingToken();
        }

        var trimmedCookie = cookieHeader.Trim();

        for (var attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, UsageSummaryPath);
                request.Headers.Add("Cookie", trimmedCookie);

                var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    return CursorUsageFetchResult.InvalidToken();
                }

                if ((int)response.StatusCode == 429)
                {
                    return CursorUsageFetchResult.RateLimited();
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Cursor usage request failed with status {StatusCode}", response.StatusCode);
                    return CursorUsageFetchResult.Failed("Cursor usage request failed.");
                }

                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var payload = ParsePayload(content);

                if (payload is null)
                {
                    return CursorUsageFetchResult.Failed("Cursor usage response could not be parsed.");
                }

                // Identity is best-effort: auth/me can 404 for some account types even when
                // usage-summary succeeds.
                var email = await FetchEmailAsync(trimmedCookie, cancellationToken).ConfigureAwait(false);
                if (email is not null)
                {
                    payload = payload with { Email = email };
                }

                return CursorUsageFetchResult.Success(payload);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < RetryDelays.Length)
            {
                _logger.LogWarning(ex, "Cursor usage fetch attempt {Attempt} failed", attempt + 1);
            }

            if (attempt < RetryDelays.Length)
            {
                await Task.Delay(RetryDelays[attempt], cancellationToken).ConfigureAwait(false);
            }
        }

        return CursorUsageFetchResult.Failed("Cursor usage request failed.");
    }

    private async Task<string?> FetchEmailAsync(string cookieHeader, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, AuthMePath);
            request.Headers.Add("Cookie", cookieHeader);

            var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(content);
            return ReadString(doc.RootElement, "email");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cursor identity fetch failed");
            return null;
        }
    }

    /// <summary>
    /// Parses an api/usage-summary response. All monetary values are in cents; the
    /// *PercentUsed fields are already 0-100 percentages.
    /// </summary>
    public static CursorUsagePayload? ParsePayload(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var now = DateTimeOffset.UtcNow;

            JsonElement plan = default;
            JsonElement onDemand = default;
            var hasPlan = false;
            var hasOnDemand = false;
            if (root.TryGetProperty("individualUsage", out var individual) && individual.ValueKind == JsonValueKind.Object)
            {
                hasPlan = individual.TryGetProperty("plan", out plan) && plan.ValueKind == JsonValueKind.Object;
                hasOnDemand = individual.TryGetProperty("onDemand", out onDemand) && onDemand.ValueKind == JsonValueKind.Object;
            }

            double? firstPartyPercentUsed = null;
            double? apiPercentUsed = null;
            if (hasPlan)
            {
                // autoPercentUsed is the dashboard's "First-party models" bar and apiPercentUsed
                // its "API" bar. The payload shape varies by account type, so the first-party bar
                // falls back to the blended totalPercentUsed, then used/limit (plan.limit is often
                // the subscription price in cents, so used/limit can diverge from the dashboard).
                var autoPercent = ReadDouble(plan, "autoPercentUsed");
                var totalPercent = ReadDouble(plan, "totalPercentUsed");
                var apiPercent = ReadDouble(plan, "apiPercentUsed");
                var used = ReadLong(plan, "used");
                var limit = ReadLong(plan, "limit");

                firstPartyPercentUsed = autoPercent ?? totalPercent;
                if (firstPartyPercentUsed is null && used is not null && limit is > 0)
                {
                    firstPartyPercentUsed = (double)used.Value / limit.Value * 100.0;
                }

                firstPartyPercentUsed = ClampPercent(firstPartyPercentUsed);
                apiPercentUsed = ClampPercent(apiPercent);
            }

            long? onDemandUsedCents = null;
            long? onDemandLimitCents = null;
            if (hasOnDemand)
            {
                onDemandUsedCents = ReadLong(onDemand, "used");
                onDemandLimitCents = ReadLong(onDemand, "limit");
            }

            return new CursorUsagePayload(
                FirstPartyPercentUsed: firstPartyPercentUsed,
                ApiPercentUsed: apiPercentUsed,
                OnDemandUsedCents: onDemandUsedCents,
                OnDemandLimitCents: onDemandLimitCents,
                BillingCycleEnd: ReadDateTime(root, "billingCycleEnd"),
                MembershipType: ReadString(root, "membershipType"),
                Email: null,
                FetchedAt: now);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Fetches individual usage events from the dashboard API for the given time range,
    /// paginating until all events are collected. Unlike the summary endpoint this is a
    /// heavier call, so callers are expected to cache the result rather than invoke it on
    /// every refresh.
    /// </summary>
    public async Task<CursorUsageEventsResult> FetchUsageEventsAsync(
        string? cookieHeader,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            return CursorUsageEventsResult.MissingToken();
        }

        var trimmedCookie = cookieHeader.Trim();
        var events = new List<CursorUsageEvent>();
        long? totalCount = null;

        try
        {
            for (var page = 1; page <= EventsMaxPages; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var request = new HttpRequestMessage(HttpMethod.Post, UsageEventsPath);
                request.Headers.Add("Cookie", trimmedCookie);
                request.Headers.Add("Origin", "https://cursor.com");
                request.Headers.Add("Referer", "https://cursor.com/dashboard");
                var body = JsonSerializer.Serialize(new
                {
                    teamId = 0,
                    startDate = start.ToUnixTimeMilliseconds().ToString(),
                    endDate = end.ToUnixTimeMilliseconds().ToString(),
                    page,
                    pageSize = EventsPageSize
                });
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

                var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    return CursorUsageEventsResult.InvalidToken();
                }

                if ((int)response.StatusCode == 429)
                {
                    return CursorUsageEventsResult.RateLimited();
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Cursor usage events request failed with status {StatusCode}", response.StatusCode);
                    return CursorUsageEventsResult.Failed("Cursor usage events request failed.");
                }

                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var parsed = ParseUsageEventsPage(content);
                if (parsed is null)
                {
                    return CursorUsageEventsResult.Failed("Cursor usage events response could not be parsed.");
                }

                totalCount ??= parsed.TotalCount;
                events.AddRange(parsed.Events);

                if (parsed.Events.Count < EventsPageSize || (totalCount is not null && events.Count >= totalCount))
                {
                    break;
                }
            }

            return CursorUsageEventsResult.Success(events);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cursor usage events fetch failed");
            return CursorUsageEventsResult.Failed("Cursor usage events request failed.");
        }
    }

    /// <summary>
    /// Parses one page of an api/dashboard/get-filtered-usage-events response. Verified shape:
    /// { "totalUsageEventsCount": N, "usageEventsDisplay": [ { "timestamp": "&lt;epoch ms&gt;",
    /// "model": "...", "tokenUsage": { "inputTokens", "outputTokens", "cacheReadTokens",
    /// "cacheWriteTokens", "totalCents" } } ] }. Numeric fields tolerate string encoding;
    /// events without a timestamp are skipped.
    /// </summary>
    public static CursorUsageEventsPage? ParseUsageEventsPage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var totalCount = ReadLong(root, "totalUsageEventsCount");

            if (!root.TryGetProperty("usageEventsDisplay", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                if (!root.TryGetProperty("usageEvents", out items) || items.ValueKind != JsonValueKind.Array)
                {
                    return new CursorUsageEventsPage(totalCount, []);
                }
            }

            var events = new List<CursorUsageEvent>(items.GetArrayLength());
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var timestampMs = ReadLong(item, "timestamp");
                if (timestampMs is null)
                {
                    continue;
                }

                long inputTokens = 0, outputTokens = 0, cacheReadTokens = 0, cacheWriteTokens = 0;
                double? totalCents = null;
                if (item.TryGetProperty("tokenUsage", out var tokenUsage) && tokenUsage.ValueKind == JsonValueKind.Object)
                {
                    inputTokens = ReadLong(tokenUsage, "inputTokens") ?? 0;
                    outputTokens = ReadLong(tokenUsage, "outputTokens") ?? 0;
                    cacheReadTokens = ReadLong(tokenUsage, "cacheReadTokens") ?? 0;
                    cacheWriteTokens = ReadLong(tokenUsage, "cacheWriteTokens", "cacheCreationTokens") ?? 0;
                    totalCents = ReadDouble(tokenUsage, "totalCents");
                }

                events.Add(new CursorUsageEvent(
                    Timestamp: DateTimeOffset.FromUnixTimeMilliseconds(timestampMs.Value),
                    Model: ReadString(item, "model") ?? "unknown",
                    InputTokens: Math.Max(0, inputTokens),
                    CacheReadTokens: Math.Max(0, cacheReadTokens),
                    CacheWriteTokens: Math.Max(0, cacheWriteTokens),
                    OutputTokens: Math.Max(0, outputTokens),
                    TotalCents: totalCents));
            }

            return new CursorUsageEventsPage(totalCount, events);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double? ClampPercent(double? value)
        => value is null ? null : Math.Clamp(value.Value, 0.0, 100.0);

    private static long? ReadLong(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static double? ReadDouble(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var text = value.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static DateTimeOffset? ReadDateTime(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (DateTimeOffset.TryParse(value.GetString(), out var timestamp))
            {
                return timestamp;
            }
        }

        return null;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

public sealed record CursorUsagePayload(
    double? FirstPartyPercentUsed,
    double? ApiPercentUsed,
    long? OnDemandUsedCents,
    long? OnDemandLimitCents,
    DateTimeOffset? BillingCycleEnd,
    string? MembershipType,
    string? Email,
    DateTimeOffset FetchedAt);

/// <summary>
/// One dashboard usage event. Token counts are raw per-request values; TotalCents is
/// Cursor's own pre-discount API-value estimate for the call, used as the cost fallback
/// for models with no pricing-catalog entry.
/// </summary>
public sealed record CursorUsageEvent(
    DateTimeOffset Timestamp,
    string Model,
    long InputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long OutputTokens,
    double? TotalCents);

public sealed record CursorUsageEventsPage(
    long? TotalCount,
    IReadOnlyList<CursorUsageEvent> Events);

public sealed record CursorUsageEventsResult(
    CursorFetchStatus Status,
    IReadOnlyList<CursorUsageEvent> Events,
    string StatusSummary)
{
    public static CursorUsageEventsResult Success(IReadOnlyList<CursorUsageEvent> events)
        => new(CursorFetchStatus.Success, events, "Cursor usage events loaded.");

    public static CursorUsageEventsResult MissingToken()
        => new(CursorFetchStatus.MissingToken, [], "Cursor session not found.");

    public static CursorUsageEventsResult InvalidToken()
        => new(CursorFetchStatus.InvalidToken, [], "Cursor session rejected.");

    public static CursorUsageEventsResult RateLimited()
        => new(CursorFetchStatus.RateLimited, [], "Cursor usage events rate limited.");

    public static CursorUsageEventsResult Failed(string message)
        => new(CursorFetchStatus.Failed, [], message);
}

public enum CursorFetchStatus
{
    Success = 0,
    MissingToken = 1,
    InvalidToken = 2,
    RateLimited = 3,
    Failed = 4
}

public sealed record CursorUsageFetchResult(
    CursorFetchStatus Status,
    CursorUsagePayload? Payload,
    string StatusSummary)
{
    public static CursorUsageFetchResult Success(CursorUsagePayload payload)
        => new(CursorFetchStatus.Success, payload, "Cursor usage loaded.");

    public static CursorUsageFetchResult MissingToken()
        => new(CursorFetchStatus.MissingToken, null, "Cursor session not found.");

    public static CursorUsageFetchResult InvalidToken()
        => new(CursorFetchStatus.InvalidToken, null, "Cursor session rejected.");

    public static CursorUsageFetchResult RateLimited()
        => new(CursorFetchStatus.RateLimited, null, "Cursor usage rate limited.");

    public static CursorUsageFetchResult Failed(string message)
        => new(CursorFetchStatus.Failed, null, message);
}
