using costats.Infrastructure.Providers;
using Xunit;

namespace costats.Tests.Providers;

public sealed class ClaudeOAuthUsageFetcherTests
{
    [Fact]
    public void CreateDelegatedRefreshStartInfo_StartsSessionInitializationInsteadOfAuthStatus()
    {
        var startInfo = ClaudeOAuthUsageFetcher.CreateDelegatedRefreshStartInfo(
            @"C:\Users\test\.local\bin\claude.exe",
            configDir: null);

        Assert.Equal(@"C:\Users\test\.local\bin\claude.exe", startInfo.FileName);
        Assert.Equal(["/status"], startInfo.ArgumentList);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
    }

    [Fact]
    public void CreateDelegatedRefreshStartInfo_UsesRequestedClaudeProfile()
    {
        var startInfo = ClaudeOAuthUsageFetcher.CreateDelegatedRefreshStartInfo(
            "claude",
            @"C:\Users\test\.claude-profile");

        Assert.Equal(@"C:\Users\test\.claude-profile", startInfo.Environment["CLAUDE_CONFIG_DIR"]);
    }
}
