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
            => "Sign-in expired — run claude /login",
        ClaudeAuthStatus.NoCredentials
            => "Not signed in — run claude /login",
        _ => string.Empty
    };

    /// <summary>True when the auth status warrants a prominent sign-in warning in the UI.</summary>
    public static bool IsSignInRequired(ClaudeAuthStatus status) => status
        is ClaudeAuthStatus.Expired
        or ClaudeAuthStatus.Unauthorized
        or ClaudeAuthStatus.NoCredentials;
}
