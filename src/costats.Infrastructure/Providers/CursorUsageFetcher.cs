using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace costats.Infrastructure.Providers;

public sealed class CursorUsageFetcher : IDisposable
{
    private const string BaseUrl = "https://cursor.com/";
    private const string UsageSummaryPath = "api/usage-summary";
    private const string AuthMePath = "api/auth/me";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) costats";
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(800)
    ];

    private readonly HttpClient _httpClient;
    private readonly ILogger<CursorUsageFetcher> _logger;

    public CursorUsageFetcher(ILogger<CursorUsageFetcher> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(10)
        };
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

            double? planPercentUsed = null;
            if (hasPlan)
            {
                // Prefer totalPercentUsed: plan.limit is often the subscription price in cents,
                // so used/limit can diverge from the dashboard usage bars.
                var totalPercent = ReadDouble(plan, "totalPercentUsed");
                var autoPercent = ReadDouble(plan, "autoPercentUsed");
                var apiPercent = ReadDouble(plan, "apiPercentUsed");
                var used = ReadLong(plan, "used");
                var limit = ReadLong(plan, "limit");

                if (totalPercent is not null)
                {
                    planPercentUsed = totalPercent;
                }
                else if (autoPercent is not null && apiPercent is not null)
                {
                    planPercentUsed = (autoPercent + apiPercent) / 2.0;
                }
                else if (apiPercent is not null || autoPercent is not null)
                {
                    planPercentUsed = apiPercent ?? autoPercent;
                }
                else if (used is not null && limit is > 0)
                {
                    planPercentUsed = (double)used.Value / limit.Value * 100.0;
                }

                planPercentUsed = planPercentUsed is null ? null : Math.Clamp(planPercentUsed.Value, 0.0, 100.0);
            }

            long? onDemandUsedCents = null;
            long? onDemandLimitCents = null;
            if (hasOnDemand)
            {
                onDemandUsedCents = ReadLong(onDemand, "used");
                onDemandLimitCents = ReadLong(onDemand, "limit");
            }

            return new CursorUsagePayload(
                PlanPercentUsed: planPercentUsed,
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
    double? PlanPercentUsed,
    long? OnDemandUsedCents,
    long? OnDemandLimitCents,
    DateTimeOffset? BillingCycleEnd,
    string? MembershipType,
    string? Email,
    DateTimeOffset FetchedAt);

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
