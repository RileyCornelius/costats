using System.Diagnostics;
using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using costats.App.Services;
using costats.App.Services.Updates;
using costats.Application.Pulse;
using costats.Application.Security;
using costats.Application.Settings;
using costats.Core.Pulse;
using costats.Infrastructure.Providers;
using costats.Infrastructure.Providers.Cursor;
using System.Linq;

namespace costats.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly IPulseOrchestrator _pulseOrchestrator;
    private readonly ICredentialVault _credentialVault;
    private readonly CopilotUsageFetcher _copilotFetcher;
    private readonly CursorUsageFetcher _cursorFetcher;
    private readonly ThemeService _themeService;
    private readonly StartupUpdateCoordinator? _updateCoordinator;
    private readonly IMulticcDiscovery? _multiccDiscovery;

    public SettingsViewModel(
        ISettingsStore settingsStore,
        AppSettings settings,
        IPulseOrchestrator pulseOrchestrator,
        ICredentialVault credentialVault,
        ThemeService themeService,
        CopilotUsageFetcher copilotFetcher,
        CursorUsageFetcher cursorFetcher,
        StartupUpdateCoordinator? updateCoordinator = null,
        IMulticcDiscovery? multiccDiscovery = null)
    {
        _settingsStore = settingsStore;
        _settings = settings;
        _pulseOrchestrator = pulseOrchestrator;
        _credentialVault = credentialVault;
        _themeService = themeService;
        _copilotFetcher = copilotFetcher;
        _cursorFetcher = cursorFetcher;
        _updateCoordinator = updateCoordinator;
        _multiccDiscovery = multiccDiscovery;

        appThemeMode = settings.AppThemeMode;
        refreshMinutes = settings.RefreshMinutes;
        startAtLogin = StartupRegistration.IsEnabled();

        multiccDetected = _multiccDiscovery?.IsDetected ?? false;
        multiccEnabled = settings.MulticcEnabled;
        multiccSelectedProfile = settings.MulticcSelectedProfile;
        multiccProfileNames = _multiccDiscovery?.Profiles.Select(p => p.Name).ToList() ?? [];
        multiccProfileCount = multiccProfileNames.Count;

        copilotEnabled = settings.CopilotEnabled;
        _ = LoadCopilotTokenStatusAsync();

        cursorEnabled = settings.CursorEnabled;
        _ = LoadCursorTokenStatusAsync();
    }

    [ObservableProperty]
    private AppThemeMode appThemeMode;

    [ObservableProperty]
    private int refreshMinutes;

    [ObservableProperty]
    private bool startAtLogin;

    [ObservableProperty]
    private bool isCheckingForUpdates;

    [ObservableProperty]
    private string updateStatusText = string.Empty;

    [ObservableProperty]
    private string updateButtonText = "Check for updates";

    // Set once a check has found and staged an update; the next button press installs it.
    private bool _hasStagedUpdate;

    [ObservableProperty]
    private bool multiccDetected;

    [ObservableProperty]
    private bool multiccEnabled;

    [ObservableProperty]
    private string? multiccSelectedProfile;

    [ObservableProperty]
    private IReadOnlyList<string> multiccProfileNames = [];

    [ObservableProperty]
    private int multiccProfileCount;

    [ObservableProperty]
    private string multiccRestartMessage = string.Empty;

    [ObservableProperty]
    private bool copilotEnabled;

    [ObservableProperty]
    private bool hasCopilotToken;

    [ObservableProperty]
    private string copilotTokenStatus = string.Empty;

    [ObservableProperty]
    private bool isCopilotTokenBusy;

    [ObservableProperty]
    private bool cursorEnabled;

    [ObservableProperty]
    private bool hasCursorToken;

    [ObservableProperty]
    private string cursorTokenStatus = string.Empty;

    [ObservableProperty]
    private bool isCursorTokenBusy;

    public bool IsMulticcAllProfiles => MulticcSelectedProfile is null;

    public string Version { get; } =
        (Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown")
        .Split('+')[0];

    public static IReadOnlyList<RefreshOption> RefreshOptions { get; } = new[]
    {
        new RefreshOption(1, "1 minute"),
        new RefreshOption(2, "2 minutes"),
        new RefreshOption(3, "3 minutes"),
        new RefreshOption(5, "5 minutes"),
        new RefreshOption(10, "10 minutes"),
        new RefreshOption(15, "15 minutes"),
    };

    public static IReadOnlyList<ThemeOption> ThemeOptions { get; } = new[]
    {
        new ThemeOption(AppThemeMode.System, "System"),
        new ThemeOption(AppThemeMode.Light, "Light"),
        new ThemeOption(AppThemeMode.Dark, "Dark"),
    };

    public ThemeOption SelectedThemeOption
    {
        get => ThemeOptions.FirstOrDefault(option => option.Mode == AppThemeMode) ?? ThemeOptions[0];
        set
        {
            if (value is not null && AppThemeMode != value.Mode)
            {
                AppThemeMode = value.Mode;
                OnPropertyChanged();
            }
        }
    }

    public RefreshOption SelectedRefreshOption
    {
        get => RefreshOptions.FirstOrDefault(o => o.Minutes == RefreshMinutes) ?? RefreshOptions[3];
        set
        {
            if (value is not null && RefreshMinutes != value.Minutes)
            {
                RefreshMinutes = value.Minutes;
                OnPropertyChanged();
            }
        }
    }

    partial void OnAppThemeModeChanged(AppThemeMode value)
    {
        _settings.AppThemeMode = value;
        _themeService.ApplyTheme(value);
        _ = SaveSettingsAsync();
        OnPropertyChanged(nameof(SelectedThemeOption));
    }

    partial void OnRefreshMinutesChanged(int value)
    {
        _settings.RefreshMinutes = value;
        _pulseOrchestrator.UpdateRefreshInterval(TimeSpan.FromMinutes(value));
        _ = SaveSettingsAsync();
        OnPropertyChanged(nameof(SelectedRefreshOption));
    }

    partial void OnStartAtLoginChanged(bool value)
    {
        _settings.StartAtLogin = value;
        StartupRegistration.SetEnabled(value);
        _ = SaveSettingsAsync();
    }

    partial void OnMulticcEnabledChanged(bool value)
    {
        _settings.MulticcEnabled = value;
        MulticcRestartMessage = "Restart required to apply changes.";
        _ = SaveSettingsAsync();
    }

    partial void OnMulticcSelectedProfileChanged(string? value)
    {
        _settings.MulticcSelectedProfile = value;
        MulticcRestartMessage = "Restart required to apply changes.";
        OnPropertyChanged(nameof(IsMulticcAllProfiles));
        _ = SaveSettingsAsync();
    }

    partial void OnCopilotEnabledChanged(bool value)
    {
        _settings.CopilotEnabled = value;
        _ = SaveSettingsAsync();
        _ = _pulseOrchestrator.RefreshOnceAsync(RefreshTrigger.Silent, CancellationToken.None);
    }

    partial void OnCursorEnabledChanged(bool value)
    {
        _settings.CursorEnabled = value;
        _ = SaveSettingsAsync();
        _ = _pulseOrchestrator.RefreshOnceAsync(RefreshTrigger.Silent, CancellationToken.None);
    }

    public async Task SaveCursorTokenAsync(string token)
    {
        var normalized = CursorCredentialReader.NormalizeManualToken(token);
        if (normalized is null)
        {
            CursorTokenStatus = "Cursor session token is required.";
            return;
        }

        IsCursorTokenBusy = true;
        try
        {
            await _credentialVault.SaveAsync(CredentialKeys.CursorToken, normalized, CancellationToken.None);
            var validation = await _cursorFetcher.FetchAsync(normalized, CancellationToken.None);
            HasCursorToken = true;
            CursorTokenStatus = validation.Status == CursorFetchStatus.Success
                ? "Cursor session token saved."
                : validation.StatusSummary;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Cursor token save failed: {ex.Message}");
            CursorTokenStatus = "Could not save Cursor session token.";
        }
        finally
        {
            IsCursorTokenBusy = false;
        }
    }

    [RelayCommand]
    private async Task ClearCursorTokenAsync()
    {
        IsCursorTokenBusy = true;
        try
        {
            await _credentialVault.SaveAsync(CredentialKeys.CursorToken, string.Empty, CancellationToken.None);
            HasCursorToken = false;
            CursorTokenStatus = "Cursor session token cleared.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Cursor token clear failed: {ex.Message}");
            CursorTokenStatus = "Could not clear Cursor session token.";
        }
        finally
        {
            IsCursorTokenBusy = false;
        }
    }

    private async Task LoadCursorTokenStatusAsync()
    {
        try
        {
            var token = await _credentialVault.LoadAsync(CredentialKeys.CursorToken, CancellationToken.None);
            HasCursorToken = !string.IsNullOrWhiteSpace(token);
            CursorTokenStatus = string.Empty;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Cursor token load failed: {ex.Message}");
            CursorTokenStatus = "Could not load Cursor session token.";
        }
    }

    public async Task SaveCopilotTokenAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            CopilotTokenStatus = "Copilot token is required.";
            return;
        }

        IsCopilotTokenBusy = true;
        try
        {
            var trimmedToken = token.Trim();
            await _credentialVault.SaveAsync(CredentialKeys.CopilotToken, trimmedToken, CancellationToken.None);
            var validation = await _copilotFetcher.FetchAsync(trimmedToken, CancellationToken.None);
            HasCopilotToken = true;
            CopilotTokenStatus = validation.Status == CopilotFetchStatus.Success
                ? "Copilot token saved."
                : validation.StatusSummary;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Copilot token save failed: {ex.Message}");
            CopilotTokenStatus = "Could not save Copilot token.";
        }
        finally
        {
            IsCopilotTokenBusy = false;
        }
    }

    [RelayCommand]
    private async Task ClearCopilotTokenAsync()
    {
        IsCopilotTokenBusy = true;
        try
        {
            await _credentialVault.SaveAsync(CredentialKeys.CopilotToken, string.Empty, CancellationToken.None);
            HasCopilotToken = false;
            CopilotTokenStatus = "Copilot token cleared.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Copilot token clear failed: {ex.Message}");
            CopilotTokenStatus = "Could not clear Copilot token.";
        }
        finally
        {
            IsCopilotTokenBusy = false;
        }
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (_updateCoordinator is null)
        {
            UpdateStatusText = "Updates are not available.";
            return;
        }

        // Cancel any previous in-flight check before starting a new one
        _updateCheckCts?.Cancel();
        _updateCheckCts?.Dispose();
        _updateCheckCts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = _updateCheckCts.Token;

        // Second press: an update was already found — install it now.
        if (_hasStagedUpdate)
        {
            await InstallStagedUpdateAsync(ct);
            return;
        }

        IsCheckingForUpdates = true;
        UpdateStatusText = "Checking for updates...";

        try
        {
            var result = await Task.Run(() => _updateCoordinator.CheckAndStageUpdateAsync(ct, forceCheck: true), ct);

            switch (result)
            {
                case UpdateCheckResult.UpdateStaged:
                case UpdateCheckResult.UpdateAlreadyStaged:
                    var pendingVersion = await _updateCoordinator.GetPendingUpdateVersionAsync(ct);
                    _hasStagedUpdate = true;
                    UpdateButtonText = "Install update";
                    UpdateStatusText = pendingVersion is not null
                        ? $"Update v{pendingVersion} is available."
                        : "An update is available.";
                    IsCheckingForUpdates = false;
                    break;

                case UpdateCheckResult.UpToDate:
                case UpdateCheckResult.Skipped:
                    UpdateStatusText = "You're up to date.";
                    IsCheckingForUpdates = false;
                    break;

                case UpdateCheckResult.Disabled:
                    UpdateStatusText = "Updates are not available.";
                    IsCheckingForUpdates = false;
                    break;

                case UpdateCheckResult.AlreadyRunning:
                    UpdateStatusText = "Update check already in progress.";
                    IsCheckingForUpdates = false;
                    break;

                case UpdateCheckResult.CheckFailed:
                default:
                    UpdateStatusText = "Could not check for updates.";
                    IsCheckingForUpdates = false;
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            UpdateStatusText = "Update check timed out. Try again.";
            IsCheckingForUpdates = false;
        }
        catch
        {
            UpdateStatusText = "Could not check for updates.";
            IsCheckingForUpdates = false;
        }
    }

    private async Task InstallStagedUpdateAsync(CancellationToken ct)
    {
        IsCheckingForUpdates = true;
        UpdateStatusText = "Installing update. Restarting...";

        try
        {
            if (await Task.Run(() => _updateCoordinator!.TryApplyPendingUpdateAsync(ct, manualTrigger: true), ct))
            {
                // Use BeginInvoke to avoid any potential deadlock with synchronous Invoke
                _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                    System.Windows.Application.Current.Shutdown(0));
                return;
            }

            // The staged update could not be launched (e.g. files were cleaned up).
            // Fall back to check mode so the next press re-checks.
            _hasStagedUpdate = false;
            UpdateButtonText = "Check for updates";
            UpdateStatusText = "Could not install the update. Try checking again.";
            IsCheckingForUpdates = false;
        }
        catch (OperationCanceledException)
        {
            UpdateStatusText = "Update install timed out. Try again.";
            IsCheckingForUpdates = false;
        }
        catch
        {
            _hasStagedUpdate = false;
            UpdateButtonText = "Check for updates";
            UpdateStatusText = "Could not install the update. Try checking again.";
            IsCheckingForUpdates = false;
        }
    }

    private CancellationTokenSource? _updateCheckCts;

    private async Task LoadCopilotTokenStatusAsync()
    {
        try
        {
            var token = await _credentialVault.LoadAsync(CredentialKeys.CopilotToken, CancellationToken.None);
            HasCopilotToken = !string.IsNullOrWhiteSpace(token);
            CopilotTokenStatus = HasCopilotToken ? string.Empty : "Copilot token not set.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Copilot token load failed: {ex.Message}");
            CopilotTokenStatus = "Could not load Copilot token.";
        }
    }

    private async Task SaveSettingsAsync()
    {
        await _settingsStore.SaveAsync(_settings, CancellationToken.None);
    }

}

public sealed record RefreshOption(int Minutes, string Label)
{
    public override string ToString() => Label;
}

public sealed record ThemeOption(AppThemeMode Mode, string Label)
{
    public override string ToString() => Label;
}
