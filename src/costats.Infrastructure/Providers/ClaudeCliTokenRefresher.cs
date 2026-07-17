using System.Diagnostics;

namespace costats.Infrastructure.Providers;

internal interface IClaudeTokenRefresher
{
    Task<bool> RefreshAsync(ClaudeCredentials credentials, CancellationToken cancellationToken);
}

internal sealed class ClaudeCliTokenRefresher : IClaudeTokenRefresher
{
    // Claude's own token request may legitimately run for 30 seconds. The old 15s
    // timeout could kill it after server-side rotation but before the new pair was
    // saved, permanently consuming the only refresh token.
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(90);

    private readonly string? _configDir;

    public ClaudeCliTokenRefresher(string? configDir)
    {
        _configDir = configDir;
    }

    public async Task<bool> RefreshAsync(ClaudeCredentials credentials, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credentials.RefreshToken))
        {
            return false;
        }

        var claudePath = FindClaudeCli();
        if (claudePath is null)
        {
            return false;
        }

        try
        {
            using var process = new Process
            {
                StartInfo = CreateRefreshStartInfo(claudePath, _configDir)
            };

            if (!process.Start())
            {
                return false;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RefreshTimeout);

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
                return process.ExitCode == 0;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }

                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                return false;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    internal static ProcessStartInfo CreateRefreshStartInfo(string claudePath, string? configDir)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = claudePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        // A normal Claude startup owns the refresh-token exchange, cross-process lock,
        // compare/reload, and atomic credential write. `/usage` makes no model request;
        // its startup path refreshes OAuth within Claude's five-minute safety window.
        startInfo.ArgumentList.Add("--print");
        startInfo.ArgumentList.Add("--no-session-persistence");
        startInfo.ArgumentList.Add("--tools");
        startInfo.ArgumentList.Add(string.Empty);
        startInfo.ArgumentList.Add("/usage");

        if (configDir is not null)
        {
            startInfo.Environment["CLAUDE_CONFIG_DIR"] = configDir;
        }

        return startInfo;
    }

    private static string? FindClaudeCli()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(home, ".local", "bin", "claude.exe"),
            Path.Combine(home, ".local", "bin", "claude")
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = OperatingSystem.IsWindows() ? "where" : "which",
                    Arguments = "claude",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                }
            };

            process.Start();
            var output = process.StandardOutput.ReadLine();
            process.WaitForExit(2000);
            return !string.IsNullOrWhiteSpace(output) && File.Exists(output) ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
