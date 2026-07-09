using costats.Infrastructure.Providers;
using Xunit;

namespace costats.Tests.Providers;

public sealed class ClaudeIdentityTests
{
    [Theory]
    [InlineData("pro", "Pro")]
    [InlineData("max", "Max")]
    [InlineData("PRO", "Pro")]
    [InlineData("Max", "Max")]
    [InlineData("enterprise", "Enterprise")]
    public void FormatPlan_CapitalizesKnownPlans(string input, string expected)
    {
        Assert.Equal(expected, ClaudeIdentity.FormatPlan(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void FormatPlan_ReturnsEmpty_WhenUnknown_NeverGuessesMax(string? input)
    {
        // Regression: previously this defaulted to "Max", mislabeling Pro users on fetch failure.
        Assert.Equal(string.Empty, ClaudeIdentity.FormatPlan(input));
    }

    [Theory]
    [InlineData(ClaudeAuthStatus.Expired)]
    [InlineData(ClaudeAuthStatus.Unauthorized)]
    public void SignInMessage_PromptsLogin_WhenTokenDead(ClaudeAuthStatus status)
    {
        Assert.Equal("Sign-in expired — run claude auth login", ClaudeIdentity.SignInMessage(status));
    }

    [Fact]
    public void SignInMessage_PromptsLogin_WhenNoCredentials()
    {
        Assert.Equal("Not signed in — run claude auth login", ClaudeIdentity.SignInMessage(ClaudeAuthStatus.NoCredentials));
    }

    [Theory]
    [InlineData(ClaudeAuthStatus.Ok)]
    [InlineData(ClaudeAuthStatus.Unavailable)]
    public void SignInMessage_IsEmpty_ForNonDegradedStates(ClaudeAuthStatus status)
    {
        Assert.Equal(string.Empty, ClaudeIdentity.SignInMessage(status));
    }

    [Theory]
    [InlineData(ClaudeAuthStatus.Expired, true)]
    [InlineData(ClaudeAuthStatus.Unauthorized, true)]
    [InlineData(ClaudeAuthStatus.NoCredentials, true)]
    [InlineData(ClaudeAuthStatus.Ok, false)]
    [InlineData(ClaudeAuthStatus.Unavailable, false)]
    public void IsSignInRequired_FlagsOnlyDeadLoginStates(ClaudeAuthStatus status, bool expected)
    {
        Assert.Equal(expected, ClaudeIdentity.IsSignInRequired(status));
    }
}
