using costats.Core.Pulse;

namespace costats.Infrastructure.Providers;

/// <summary>
/// Shared formatting for Claude identity/auth display, used by both the default
/// <see cref="ClaudeLogSource"/> and the per-profile <see cref="MulticcClaudeLogSource"/>.
/// </summary>
public static class ClaudeIdentity
{
    /// <summary>
    /// Formats the subscription type for the plan badge: "pro" -> "Pro", "max" -> "Max".
    /// Returns an empty string when the plan is genuinely unknown so the badge collapses —
    /// we never guess a plan the user doesn't have.
    /// </summary>
    public static string FormatPlan(string? subscriptionType)
    {
        if (string.IsNullOrEmpty(subscriptionType))
        {
            return string.Empty;
        }

        return char.ToUpper(subscriptionType[0]) + subscriptionType[1..].ToLower();
    }

    /// <summary>
    /// User-facing status text for a degraded authentication state. Returns an empty
    /// string for non-degraded states (<see cref="ClaudeAuthStatus.Ok"/> /
    /// <see cref="ClaudeAuthStatus.Unavailable"/>).
    /// </summary>
    public static string SignInMessage(ClaudeAuthStatus status) => status switch
    {
        ClaudeAuthStatus.Expired or ClaudeAuthStatus.Unauthorized
            => "Sign-in expired — run claude auth login",
        ClaudeAuthStatus.NoCredentials
            => "Not signed in — run claude auth login",
        _ => string.Empty
    };

    /// <summary>True when the auth status warrants a prominent sign-in warning in the UI.</summary>
    public static bool IsSignInRequired(ClaudeAuthStatus status) => status
        is ClaudeAuthStatus.Expired
        or ClaudeAuthStatus.Unauthorized
        or ClaudeAuthStatus.NoCredentials;

    /// <summary>
    /// Builds the reading returned when there is no usage data at all (no OAuth result and
    /// no local log tokens). A dead login surfaces a loud sign-in prompt with the known plan;
    /// otherwise it falls back to <paramref name="noDataMessage"/>.
    /// </summary>
    public static ProviderReading BuildEmptyReading(
        ClaudeOAuthOutcome outcome,
        string providerId,
        string displayName,
        string noDataMessage,
        DateTimeOffset now)
    {
        var signInRequired = IsSignInRequired(outcome.Status);
        var planOnly = FormatPlan(outcome.SubscriptionType);
        return new ProviderReading(
            Usage: null,
            Identity: signInRequired && planOnly.Length > 0
                ? new IdentityCard(providerId, displayName, null, null, planOnly, "OAuth")
                : null,
            StatusSummary: signInRequired ? SignInMessage(outcome.Status) : noDataMessage,
            CapturedAt: now,
            Confidence: ReadingConfidence.Low,
            Source: ReadingSource.LocalLog,
            Alert: signInRequired ? ReadingAlert.SignInRequired : ReadingAlert.None);
    }

    /// <summary>
    /// Resolves the status line, confidence, and alert for a reading that does have usage data.
    /// A dead login is called out instead of presenting stale numbers as normal; otherwise the
    /// status reflects how fresh the OAuth/log data is.
    /// </summary>
    public static (string StatusSummary, ReadingConfidence Confidence, ReadingAlert Alert) ResolveStatus(
        ClaudeOAuthOutcome outcome,
        ClaudeOAuthUsageResult? oauthResult,
        DateTimeOffset? logTimestamp,
        DateTimeOffset now)
    {
        if (IsSignInRequired(outcome.Status))
        {
            // Login is dead — make it obvious instead of showing stale log numbers as normal.
            return (SignInMessage(outcome.Status), ReadingConfidence.Low, ReadingAlert.SignInRequired);
        }

        var statusSummary = oauthResult is not null
            ? $"Updated {UsageFormatter.FormatRelativeTime(oauthResult.FetchedAt, now)}"
            : $"Updated {UsageFormatter.FormatRelativeTime(logTimestamp ?? now, now)}";
        var confidence = oauthResult is not null ? ReadingConfidence.High : ReadingConfidence.Medium;
        return (statusSummary, confidence, ReadingAlert.None);
    }
}
