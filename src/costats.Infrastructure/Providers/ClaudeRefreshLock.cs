namespace costats.Infrastructure.Providers;

internal interface IClaudeRefreshLock
{
    /// <summary>
    /// Acquires Claude Code's cross-process OAuth refresh locks, or returns null when another
    /// process (normally Claude Code itself) is refreshing.
    /// </summary>
    Task<IAsyncDisposable?> TryAcquireAsync(string configDirectory, CancellationToken cancellationToken);
}

/// <summary>
/// Takes the same locks Claude Code takes before rotating the refresh token, so Costats and
/// Claude Code never exchange the same single-use refresh token concurrently.
/// </summary>
/// <remarks>
/// Claude Code uses <c>proper-lockfile</c> directory locks on
/// <c>&lt;configDir&gt;/.oauth_refresh.lock</c> and the legacy <c>&lt;configDir&gt;.lock</c>,
/// acquired in that order, treated as stale after 60s and touched every 5s by the holder.
/// Claude Code only takes over a live-looking lock when its own owner record proves the
/// holder dead; Costats writes no owner record, so Claude Code waits for it instead.
/// </remarks>
internal sealed class ClaudeRefreshLock : IClaudeRefreshLock
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TouchInterval = TimeSpan.FromSeconds(5);
    private const int MaxRetries = 5;

    public async Task<IAsyncDisposable?> TryAcquireAsync(string configDirectory, CancellationToken cancellationToken)
    {
        var fullDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configDirectory));
        var primary = Path.Combine(fullDir, ".oauth_refresh.lock");
        var legacy = fullDir + ".lock";

        for (var attempt = 0; ; attempt++)
        {
            if (TryCreateLockDirectory(primary))
            {
                if (TryCreateLockDirectory(legacy))
                {
                    return new Handle(primary, legacy);
                }

                TryRemove(primary);
            }

            if (attempt >= MaxRetries)
            {
                return null;
            }

            // Same 1-2s jittered retry Claude Code uses while another refresh is in flight.
            await Task.Delay(TimeSpan.FromMilliseconds(1000 + Random.Shared.Next(1000)), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Atomically creates the lock directory. On Windows, moving a fresh private directory
    /// into place fails if the target exists, which gives mkdir-style exclusivity.
    /// A lock untouched for longer than the stale window is removed and retried once, as
    /// proper-lockfile does.
    /// </summary>
    internal static bool TryCreateLockDirectory(string lockPath)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            if (!Directory.Exists(lockPath))
            {
                var staging = $"{lockPath}.costats-{Guid.NewGuid():N}";
                try
                {
                    Directory.CreateDirectory(staging);
                    Directory.Move(staging, lockPath);
                    return true;
                }
                catch (IOException)
                {
                    // Lost the race to another process; fall through to the stale check.
                }
                catch (UnauthorizedAccessException)
                {
                }
                finally
                {
                    TryRemove(staging);
                }
            }

            if (pass > 0 || !IsStale(lockPath))
            {
                return false;
            }

            TryRemove(lockPath);
        }

        return false;
    }

    private static bool IsStale(string lockPath)
    {
        try
        {
            return DateTime.UtcNow - Directory.GetLastWriteTimeUtc(lockPath) > StaleAfter;
        }
        catch
        {
            return false;
        }
    }

    private static void TryRemove(string path)
    {
        try { Directory.Delete(path); } catch { /* already gone or not ours to remove */ }
    }

    private sealed class Handle : IAsyncDisposable
    {
        private readonly string _primary;
        private readonly string _legacy;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _toucher;

        public Handle(string primary, string legacy)
        {
            _primary = primary;
            _legacy = legacy;
            _toucher = TouchAsync(_stop.Token);
        }

        // Keep the mtime fresh so neither Claude Code nor proper-lockfile treats it as stale.
        private async Task TouchAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(TouchInterval, cancellationToken).ConfigureAwait(false);
                    var now = DateTime.UtcNow;
                    try { Directory.SetLastWriteTimeUtc(_primary, now); } catch { }
                    try { Directory.SetLastWriteTimeUtc(_legacy, now); } catch { }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            await _toucher.ConfigureAwait(false);
            _stop.Dispose();

            // Release in reverse acquisition order, matching Claude Code.
            TryRemove(_legacy);
            TryRemove(_primary);
        }
    }
}
