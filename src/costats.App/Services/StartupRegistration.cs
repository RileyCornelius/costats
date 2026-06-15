using Microsoft.Win32;

namespace costats.App.Services;

/// <summary>
/// Manages the Windows "start at login" entry under HKCU ...\CurrentVersion\Run.
/// The auto-start command carries <see cref="AutoStartFlag"/> so the app can tell a
/// login-triggered launch from a manual one and start hidden in the tray accordingly.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "costats";

    /// <summary>Command-line flag appended to the auto-start entry to mark login launches.</summary>
    public const string AutoStartFlag = "--autostart";

    /// <summary>True when the app is registered to start at login.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(AppName) is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Adds or removes the start-at-login entry (with the auto-start flag when enabling).</summary>
    public static void SetEnabled(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key is null) return;

            if (enable)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                {
                    key.SetValue(AppName, $"\"{exePath}\" {AutoStartFlag}");
                }
            }
            else
            {
                key.DeleteValue(AppName, false);
            }
        }
        catch
        {
            // Silently ignore registry errors
        }
    }

    /// <summary>
    /// When start-at-login is enabled, ensures the registry command points at the current
    /// executable and carries <see cref="AutoStartFlag"/>. Heals entries written by older
    /// versions (which had no flag, so the widget always popped up at login) and stale paths
    /// left behind after an update.
    /// </summary>
    public static void SyncIfEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key is null) return;
            if (key.GetValue(AppName) is not string) return; // not enabled — nothing to heal

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            var desired = $"\"{exePath}\" {AutoStartFlag}";
            if (key.GetValue(AppName) is string current
                && !string.Equals(current, desired, StringComparison.Ordinal))
            {
                key.SetValue(AppName, desired);
            }
        }
        catch
        {
            // Silently ignore registry errors
        }
    }
}
