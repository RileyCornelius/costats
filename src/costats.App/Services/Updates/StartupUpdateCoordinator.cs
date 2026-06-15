using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Reflection;

namespace costats.App.Services.Updates;

public sealed class StartupUpdateCoordinator
{
    /// <summary>
    /// Command-line flag the updater script passes to the app it relaunches after a
    /// <em>failed</em> apply. It tells the app to skip the automatic startup apply for this
    /// run so it does not immediately re-trigger the staged update and race the updater,
    /// which previously burned the whole retry budget. The next manual "Check for updates"
    /// or the next cold start retries the still-staged update.
    /// </summary>
    public const string SkipUpdateApplyFlag = "--skip-update-apply";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly Regex SemVerRegex = new(
        @"^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ShaLineRegex = new(
        @"^(?<hash>[A-Fa-f0-9]{64})\s+\*?(?<name>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly UpdateOptions _options;
    private readonly HttpClient _httpClient;
    private readonly string _appBaseDirectory;
    private readonly string _executablePath;
    private readonly string _updatesRoot;
    private readonly string _statePath;
    private readonly string _pendingPath;
    private readonly string _runtimeRid;
    private readonly Version _currentVersion;
    private readonly SemaphoreSlim _checkLock = new(1, 1);

    public StartupUpdateCoordinator(UpdateOptions options)
    {
        _options = options;
        _appBaseDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _executablePath = Environment.ProcessPath ?? Path.Combine(_appBaseDirectory, "costats.App.exe");
        _runtimeRid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        _currentVersion = ResolveCurrentVersion();

        _updatesRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "costats",
            "updates");
        _statePath = Path.Combine(_updatesRoot, "state.json");
        _pendingPath = Path.Combine(_updatesRoot, "pending.json");

        _httpClient = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("costats", "1.0"));
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public Task<bool> TryApplyPendingUpdateAsync(CancellationToken cancellationToken)
        => TryApplyPendingUpdateAsync(cancellationToken, manualTrigger: false);

    public async Task<bool> TryApplyPendingUpdateAsync(CancellationToken cancellationToken, bool manualTrigger)
    {
        if (!_options.Enabled || !CanSelfUpdate())
        {
            return false;
        }

        // Only gate on ApplyStagedUpdateOnStartup for automatic (non-manual) triggers
        if (!manualTrigger && !_options.ApplyStagedUpdateOnStartup)
        {
            return false;
        }

        try
        {
            var pending = await ReadJsonAsync<PendingUpdate>(_pendingPath, cancellationToken).ConfigureAwait(false);
            if (pending is null)
            {
                return false;
            }

            const int maxApplyAttempts = 3;
            if (pending.FailedAttempts >= maxApplyAttempts)
            {
                Trace.WriteLine($"[costats-update] pending update {pending.Version} failed {pending.FailedAttempts} times, giving up");
                SafeDeleteFile(_pendingPath);
                SafeDeleteDirectory(pending.StagingDirectory);
                return false;
            }

            if (!TryParseSemVer(pending.Version, out var pendingVersion) || pendingVersion <= _currentVersion)
            {
                SafeDeleteFile(_pendingPath);
                SafeDeleteDirectory(pending.StagingDirectory);
                return false;
            }

            if (!TryResolvePendingExecutable(pending, out var stagedExe, out var executableRelativePath))
            {
                SafeDeleteFile(_pendingPath);
                return false;
            }

            Directory.CreateDirectory(_updatesRoot);
            var scriptPath = Path.Combine(_updatesRoot, "apply-update.ps1");

            // Prefer the script shipped with the staged update (from the new version's ZIP).
            // This prevents a chicken-and-egg problem where the running version's embedded
            // script has a bug that can only be fixed by the version being installed.
            var stagedScript = Path.Combine(pending.StagingDirectory, "apply-update.ps1");
            if (File.Exists(stagedScript))
            {
                File.Copy(stagedScript, scriptPath, overwrite: true);
            }
            else
            {
                await File.WriteAllTextAsync(scriptPath, UpdaterScriptContents, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
                    .ConfigureAwait(false);
            }

            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(scriptPath);
            psi.ArgumentList.Add("-TargetPid");
            psi.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-InstallDir");
            psi.ArgumentList.Add(_appBaseDirectory);
            psi.ArgumentList.Add("-StagingDir");
            psi.ArgumentList.Add(pending.StagingDirectory);
            psi.ArgumentList.Add("-ExecutableRelativePath");
            psi.ArgumentList.Add(executableRelativePath);
            psi.ArgumentList.Add("-PendingFilePath");
            psi.ArgumentList.Add(_pendingPath);

            var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            Trace.WriteLine($"[costats-update] launching updater for version {pending.Version} from {stagedExe}");
            return true;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[costats-update] apply staged update failed: {ex}");
            return false;
        }
    }

    public async Task<UpdateCheckResult> CheckAndStageUpdateAsync(CancellationToken cancellationToken, bool forceCheck = false)
    {
        if (!_options.Enabled || !CanSelfUpdate())
        {
            return UpdateCheckResult.Disabled;
        }

        if (!await _checkLock.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false))
        {
            return UpdateCheckResult.AlreadyRunning;
        }

        try
        {
            Directory.CreateDirectory(_updatesRoot);
            var pending = await ReadJsonAsync<PendingUpdate>(_pendingPath, cancellationToken).ConfigureAwait(false);
            if (pending is not null && IsPendingValidAndNewer(pending))
            {
                return UpdateCheckResult.UpdateAlreadyStaged;
            }

            var state = await ReadJsonAsync<UpdateState>(_statePath, cancellationToken).ConfigureAwait(false) ?? new UpdateState();
            var now = DateTimeOffset.UtcNow;
            var interval = TimeSpan.FromHours(_options.CheckIntervalHours);
            if (!forceCheck && state.LastCheckedUtc.HasValue && now - state.LastCheckedUtc.Value < interval)
            {
                return UpdateCheckResult.Skipped;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, BuildLatestReleaseUri(_options.Repository));
            if (!string.IsNullOrWhiteSpace(state.ETag) &&
                EntityTagHeaderValue.TryParse(state.ETag, out var eTagHeader))
            {
                request.Headers.IfNoneMatch.Add(eTagHeader);
            }

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            state.LastCheckedUtc = now;
            state.ETag = response.Headers.ETag?.Tag ?? state.ETag;

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                await WriteJsonAsync(_statePath, state, cancellationToken).ConfigureAwait(false);
                return UpdateCheckResult.UpToDate;
            }

            if (!response.IsSuccessStatusCode)
            {
                await WriteJsonAsync(_statePath, state, cancellationToken).ConfigureAwait(false);
                return UpdateCheckResult.CheckFailed;
            }

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var release = await ParseReleaseAsync(contentStream, cancellationToken).ConfigureAwait(false);
            if (release is null)
            {
                await WriteJsonAsync(_statePath, state, cancellationToken).ConfigureAwait(false);
                return UpdateCheckResult.CheckFailed;
            }

            if (release.Prerelease && !_options.AllowPrerelease)
            {
                await WriteJsonAsync(_statePath, state, cancellationToken).ConfigureAwait(false);
                return UpdateCheckResult.UpToDate;
            }

            if (!TryGetBestAsset(release, out var zipAsset, out var releaseVersion))
            {
                await WriteJsonAsync(_statePath, state, cancellationToken).ConfigureAwait(false);
                return UpdateCheckResult.UpToDate;
            }

            if (releaseVersion <= _currentVersion)
            {
                state.LastSeenVersion = releaseVersion.ToString(3);
                await WriteJsonAsync(_statePath, state, cancellationToken).ConfigureAwait(false);
                return UpdateCheckResult.UpToDate;
            }

            var downloadsDir = Path.Combine(_updatesRoot, "downloads");
            Directory.CreateDirectory(downloadsDir);
            var zipPath = Path.Combine(downloadsDir, zipAsset.Name);
            await DownloadToFileAsync(zipAsset.DownloadUrl, zipPath, cancellationToken).ConfigureAwait(false);

            var expectedHash = await TryResolveChecksumAsync(release, zipAsset, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(expectedHash))
            {
                var actualHash = await ComputeSha256Async(zipPath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Downloaded update checksum does not match release checksum.");
                }
            }

            var stageDir = Path.Combine(
                _updatesRoot,
                "staging",
                $"{releaseVersion.Major}.{releaseVersion.Minor}.{releaseVersion.Build}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}");
            if (Directory.Exists(stageDir))
            {
                Directory.Delete(stageDir, recursive: true);
            }

            Directory.CreateDirectory(stageDir);
            ZipFile.ExtractToDirectory(zipPath, stageDir, overwriteFiles: true);

            if (!TryFindStagedExecutable(stageDir, out var stagedExecutablePath))
            {
                throw new FileNotFoundException("Staged update did not contain costats.App.exe.");
            }

            var executableRelativePath = Path.GetRelativePath(stageDir, stagedExecutablePath);
            var pendingUpdate = new PendingUpdate
            {
                Version = releaseVersion.ToString(3),
                CreatedUtc = DateTimeOffset.UtcNow,
                StagingDirectory = stageDir,
                ExecutableRelativePath = executableRelativePath
            };

            await WriteJsonAsync(_pendingPath, pendingUpdate, cancellationToken).ConfigureAwait(false);

            state.LastSeenVersion = releaseVersion.ToString(3);
            await WriteJsonAsync(_statePath, state, cancellationToken).ConfigureAwait(false);

            SafeDeleteFile(zipPath);
            CleanupOldStagingDirectories(stageDir);

            return UpdateCheckResult.UpdateStaged;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[costats-update] check/stage failed: {ex}");
            return UpdateCheckResult.CheckFailed;
        }
        finally
        {
            _checkLock.Release();
        }
    }

    private static string BuildLatestReleaseUri(string repository)
    {
        return $"https://api.github.com/repos/{repository}/releases/latest";
    }

    private bool CanSelfUpdate()
    {
        if (!File.Exists(_executablePath))
        {
            return false;
        }

        if (_appBaseDirectory.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
        {
            // MSIX/AppInstaller installs are updated by App Installer.
            return false;
        }

        if (_appBaseDirectory.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) &&
            _appBaseDirectory.Contains(@"\src\", StringComparison.OrdinalIgnoreCase))
        {
            // Development runs should not self-update.
            return false;
        }

        if (!HasWriteAccess(_appBaseDirectory))
        {
            return false;
        }

        return true;
    }

    private static bool HasWriteAccess(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var testPath = Path.Combine(directory, $".write-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(testPath, "ok");
            File.Delete(testPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool IsPendingValidAndNewer(PendingUpdate pending)
    {
        if (!TryResolvePendingExecutable(pending, out _, out _))
        {
            SafeDeleteFile(_pendingPath);
            return false;
        }

        return TryParseSemVer(pending.Version, out var pendingVersion) && pendingVersion > _currentVersion;
    }

    private static bool TryResolvePendingExecutable(PendingUpdate pending, out string stagedExePath, out string executableRelativePath)
    {
        stagedExePath = string.Empty;
        executableRelativePath = "costats.App.exe";

        if (string.IsNullOrWhiteSpace(pending.StagingDirectory) || !Directory.Exists(pending.StagingDirectory))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(pending.ExecutableRelativePath))
        {
            var candidate = Path.Combine(pending.StagingDirectory, pending.ExecutableRelativePath);
            if (File.Exists(candidate))
            {
                stagedExePath = candidate;
                executableRelativePath = pending.ExecutableRelativePath;
                return true;
            }
        }

        if (!TryFindStagedExecutable(pending.StagingDirectory, out var discoveredExecutable))
        {
            return false;
        }

        stagedExePath = discoveredExecutable;
        executableRelativePath = Path.GetRelativePath(pending.StagingDirectory, discoveredExecutable);
        return true;
    }

    private static bool TryFindStagedExecutable(string stageDirectory, out string executablePath)
    {
        executablePath = Path.Combine(stageDirectory, "costats.App.exe");
        if (File.Exists(executablePath))
        {
            return true;
        }

        var discovered = Directory
            .EnumerateFiles(stageDirectory, "costats.App.exe", SearchOption.AllDirectories)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(discovered))
        {
            executablePath = string.Empty;
            return false;
        }

        executablePath = discovered;
        return true;
    }

    private static void CleanupOldStagingDirectories(string keepPath)
    {
        var parent = Path.GetDirectoryName(keepPath);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
        {
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(parent))
        {
            if (string.Equals(dir, keepPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }

    private static Version ResolveCurrentVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var informational = assembly
            .GetCustomAttributes<AssemblyInformationalVersionAttribute>()
            .Select(attribute => attribute.InformationalVersion)
            .FirstOrDefault();

        if (TryParseSemVer(informational, out var informationalVersion))
        {
            return informationalVersion;
        }

        var assemblyVersion = assembly.GetName().Version;
        if (assemblyVersion is not null && assemblyVersion.Major >= 0 && assemblyVersion.Minor >= 0 && assemblyVersion.Build >= 0)
        {
            return new Version(assemblyVersion.Major, assemblyVersion.Minor, assemblyVersion.Build);
        }

        return new Version(0, 0, 0);
    }

    private bool TryGetBestAsset(ReleaseDocument release, out ReleaseAsset selectedAsset, out Version selectedVersion)
    {
        selectedAsset = default!;
        selectedVersion = new Version(0, 0, 0);

        var candidates = new List<(ReleaseAsset Asset, Version Version)>();
        foreach (var asset in release.Assets)
        {
            if (!TryExtractVersionFromAssetName(asset.Name, out var assetRid, out var parsedVersion))
            {
                continue;
            }

            candidates.Add((asset with { RuntimeIdentifier = assetRid }, parsedVersion));
        }

        var best = candidates
            .Where(candidate => string.Equals(candidate.Asset.RuntimeIdentifier, _runtimeRid, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candidate => candidate.Version)
            .FirstOrDefault();

        if (best.Asset is null || string.IsNullOrWhiteSpace(best.Asset.Name))
        {
            return false;
        }

        selectedAsset = best.Asset;
        selectedVersion = best.Version;
        return true;
    }

    private static bool TryExtractVersionFromAssetName(string assetName, out string runtimeIdentifier, out Version version)
    {
        runtimeIdentifier = string.Empty;
        version = new Version(0, 0, 0);

        if (!assetName.StartsWith("costats-win-", StringComparison.OrdinalIgnoreCase) ||
            !assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var markerIndex = assetName.LastIndexOf("-v", StringComparison.OrdinalIgnoreCase);
        if (markerIndex <= 0)
        {
            return false;
        }

        runtimeIdentifier = assetName["costats-".Length..markerIndex];
        var versionText = assetName[(markerIndex + 2)..^4];
        return TryParseSemVer(versionText, out version);
    }

    private static bool TryParseSemVer(string? value, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var match = SemVerRegex.Match(value.TrimStart('v', 'V').Trim());
        if (!match.Success)
        {
            return false;
        }

        if (!int.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !int.TryParse(match.Groups["patch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        version = new Version(major, minor, patch);
        return true;
    }

    private async Task<string?> TryResolveChecksumAsync(ReleaseDocument release, ReleaseAsset packageAsset, CancellationToken cancellationToken)
    {
        var directChecksumAsset = release.Assets
            .FirstOrDefault(asset => string.Equals(asset.Name, $"{packageAsset.Name}.sha256", StringComparison.OrdinalIgnoreCase));
        if (directChecksumAsset is not null && !string.IsNullOrWhiteSpace(directChecksumAsset.Name))
        {
            var checksumText = await DownloadAsStringAsync(directChecksumAsset.DownloadUrl, cancellationToken).ConfigureAwait(false);
            return ExtractChecksum(checksumText, packageAsset.Name);
        }

        var checksumsAsset = release.Assets
            .FirstOrDefault(asset => string.Equals(asset.Name, "checksums.txt", StringComparison.OrdinalIgnoreCase));
        if (checksumsAsset is not null && !string.IsNullOrWhiteSpace(checksumsAsset.Name))
        {
            var checksumText = await DownloadAsStringAsync(checksumsAsset.DownloadUrl, cancellationToken).ConfigureAwait(false);
            return ExtractChecksum(checksumText, packageAsset.Name);
        }

        return null;
    }

    private static string? ExtractChecksum(string checksumText, string packageName)
    {
        if (string.IsNullOrWhiteSpace(checksumText))
        {
            return null;
        }

        foreach (var line in checksumText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Regex.IsMatch(line, "^[A-Fa-f0-9]{64}$"))
            {
                return line.Trim();
            }

            var match = ShaLineRegex.Match(line.Trim());
            if (!match.Success)
            {
                continue;
            }

            var candidateName = match.Groups["name"].Value.Trim();
            if (string.Equals(candidateName, packageName, StringComparison.OrdinalIgnoreCase))
            {
                return match.Groups["hash"].Value.Trim();
            }
        }

        return null;
    }

    private async Task<string> DownloadAsStringAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DownloadToFileAsync(string url, string destinationPath, CancellationToken cancellationToken)
    {
        var tempPath = $"{destinationPath}.part";
        SafeDeleteFile(tempPath);

        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        // HttpClient.Timeout only covers headers with ResponseHeadersRead.
        // Add an explicit timeout for the body download to prevent indefinite hangs.
        using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        downloadCts.CancelAfter(TimeSpan.FromMinutes(3));

        await using (var source = await response.Content.ReadAsStreamAsync(downloadCts.Token).ConfigureAwait(false))
        await using (var destination = File.Create(tempPath))
        {
            await source.CopyToAsync(destination, downloadCts.Token).ConfigureAwait(false);
        }

        SafeDeleteFile(destinationPath);
        File.Move(tempPath, destinationPath);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<ReleaseDocument?> ParseReleaseAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;

        if (!root.TryGetProperty("assets", out var assetsElement) || assetsElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var assets = new List<ReleaseAsset>();
        foreach (var assetElement in assetsElement.EnumerateArray())
        {
            var name = assetElement.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            var downloadUrl = assetElement.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() : null;

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(downloadUrl))
            {
                continue;
            }

            assets.Add(new ReleaseAsset(name, downloadUrl));
        }

        var prerelease = root.TryGetProperty("prerelease", out var prereleaseElement) && prereleaseElement.GetBoolean();
        return new ReleaseDocument(prerelease, assets);
    }

    private static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return default;
        }
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static void SafeDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static void SafeDeleteDirectory(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private sealed record ReleaseDocument(bool Prerelease, IReadOnlyList<ReleaseAsset> Assets);

    private sealed record ReleaseAsset(string Name, string DownloadUrl, string RuntimeIdentifier = "");

    private sealed class UpdateState
    {
        public DateTimeOffset? LastCheckedUtc { get; set; }
        public string? ETag { get; set; }
        public string? LastSeenVersion { get; set; }
    }

    private sealed class PendingUpdate
    {
        public string Version { get; set; } = "0.0.0";
        public DateTimeOffset CreatedUtc { get; set; }
        public string StagingDirectory { get; set; } = string.Empty;
        public string ExecutableRelativePath { get; set; } = "costats.App.exe";
        public int FailedAttempts { get; set; }
    }

    private const string UpdaterScriptContents = """
param(
    [Parameter(Mandatory = $true)][int]$TargetPid,
    [Parameter(Mandatory = $true)][string]$InstallDir,
    [Parameter(Mandatory = $true)][string]$StagingDir,
    [Parameter(Mandatory = $true)][string]$ExecutableRelativePath,
    [Parameter(Mandatory = $true)][string]$PendingFilePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$logDir = Join-Path $env:LOCALAPPDATA "costats\updates"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$logPath = Join-Path $logDir "apply-update.log"

$InstallDir = $InstallDir.TrimEnd('\', '/')
$StagingDir = $StagingDir.TrimEnd('\', '/')

# Flag passed to the relaunched app after a FAILED apply so it does not immediately
# retry the staged update (which would race this script and burn the attempt budget).
# Must match StartupUpdateCoordinator.SkipUpdateApplyFlag and the App.xaml.cs handling.
$SkipUpdateApplyFlag = "--skip-update-apply"

# Path to the executable inside the install dir; only valid once the swap succeeds.
$newExePath = $null

function Write-Log {
    param([string]$Message)
    $stamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
    Add-Content -Path $logPath -Value "[$stamp] $Message"
}

function Wait-ForProcessExit {
    param([int]$ProcessId, [int]$TimeoutSeconds = 60)
    for ($i = 0; $i -lt ($TimeoutSeconds * 2); $i++) {
        if (-not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) {
            return $true
        }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function Stop-CostatsProcessesUnder {
    # Stop any costats process whose image lives under the install dir. This covers a
    # prior failed relaunch that came back up and re-locked the folder — the original
    # bug burned all retries because only $TargetPid was awaited.
    param([string]$Directory)
    try {
        $procs = @(Get-Process -Name "costats*" -ErrorAction SilentlyContinue | Where-Object {
            $path = $null
            try { $path = $_.Path } catch { $path = $null }
            $path -and $path.StartsWith($Directory, [System.StringComparison]::OrdinalIgnoreCase)
        })
        foreach ($p in $procs) {
            Write-Log "Stopping lingering costats process PID $($p.Id) ($($p.Path))."
            try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { }
        }
        if ($procs.Count -gt 0) { Start-Sleep -Milliseconds 1000 }
    } catch {
        Write-Log "Could not enumerate costats processes: $($_.Exception.Message)"
    }
}

function Relaunch-App {
    # Relaunch the app. On a failed apply pass -SkipUpdateApply so the app does NOT
    # immediately re-trigger the staged update (breaking the unattended retry loop).
    param([switch]$SkipUpdateApply)

    $candidates = @()
    if ($newExePath -and (Test-Path -LiteralPath $newExePath)) { $candidates += $newExePath }
    $currentExe = Join-Path $InstallDir $ExecutableRelativePath
    if ((Test-Path -LiteralPath $currentExe) -and ($candidates -notcontains $currentExe)) { $candidates += $currentExe }
    $stagedExe = Join-Path $StagingDir $ExecutableRelativePath
    if ((Test-Path -LiteralPath $stagedExe) -and ($candidates -notcontains $stagedExe)) { $candidates += $stagedExe }

    foreach ($exe in $candidates) {
        try {
            if ($SkipUpdateApply) {
                Start-Process -FilePath $exe -ArgumentList $SkipUpdateApplyFlag | Out-Null
            } else {
                Start-Process -FilePath $exe | Out-Null
            }
            Write-Log "Launched app: $exe (skipApply=$($SkipUpdateApply.IsPresent))"
            return
        } catch {
            Write-Log "Failed to launch $exe : $($_.Exception.Message)"
        }
    }
    Write-Log "CRITICAL: Could not launch any executable. Candidates: $($candidates -join ', ')"
}

function Increment-FailedAttempts {
    try {
        if (Test-Path -LiteralPath $PendingFilePath) {
            $json = Get-Content -Raw -Path $PendingFilePath | ConvertFrom-Json
            if (-not (Get-Member -InputObject $json -Name "failedAttempts" -MemberType NoteProperty)) {
                $json | Add-Member -NotePropertyName "failedAttempts" -NotePropertyValue 0
            }
            $json.failedAttempts = $json.failedAttempts + 1
            $json | ConvertTo-Json -Depth 10 | Set-Content -Path $PendingFilePath -Encoding UTF8
            Write-Log "Incremented failedAttempts to $($json.failedAttempts)."
        }
    } catch {
        Write-Log "Failed to increment failedAttempts: $($_.Exception.Message)"
    }
}

function Remove-AsideBackups {
    # Best-effort sweep of *.old-* files left by a previous (possibly interrupted) run.
    param([string]$Directory)
    try {
        if (-not (Test-Path -LiteralPath $Directory)) { return }
        Get-ChildItem -LiteralPath $Directory -Recurse -File -Force -Filter "*.old-*" -ErrorAction SilentlyContinue |
            ForEach-Object {
                try { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue } catch { }
            }
    } catch { }
}

function Copy-OneFile {
    # Replace a single file with backoff. The existing target is ALWAYS moved aside
    # before the new file is copied in, so every replacement is reversible: a later
    # file failing mid-run can roll the whole set back to the prior version instead of
    # leaving a half-updated (mixed OLD/NEW) install. Renaming aside also succeeds on a
    # loaded/running image where an in-place overwrite would be blocked.
    param(
        [string]$SourceFile,
        [string]$TargetFile,
        [System.Collections.ArrayList]$RenamedAside,
        [int]$Attempts = 12
    )
    $delay = 500
    for ($i = 1; $i -le $Attempts; $i++) {
        try {
            if (Test-Path -LiteralPath $TargetFile) {
                $aside = "$TargetFile.old-$([Guid]::NewGuid().ToString('N'))"
                Move-Item -LiteralPath $TargetFile -Destination $aside -Force
                [void]$RenamedAside.Add([PSCustomObject]@{ Original = $TargetFile; Aside = $aside })
            }
            Copy-Item -LiteralPath $SourceFile -Destination $TargetFile -Force
            return
        } catch {
            if ($i -ge $Attempts) { throw }
            Start-Sleep -Milliseconds $delay
            $delay = [Math]::Min($delay * 2, 5000)
        }
    }
}

function Replace-Files {
    # Copy every staged file into the live install dir in place — no whole-directory
    # rename, so a single locked file no longer blocks the entire update.
    param(
        [string]$Source,
        [string]$Destination,
        [System.Collections.ArrayList]$RenamedAside
    )
    $sourceRoot = (Resolve-Path -LiteralPath $Source).Path.TrimEnd('\', '/')
    foreach ($file in (Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -Force)) {
        $relative = $file.FullName.Substring($sourceRoot.Length).TrimStart('\', '/')
        $target = Join-Path $Destination $relative
        $targetDir = Split-Path -Parent $target
        if ($targetDir -and -not (Test-Path -LiteralPath $targetDir)) {
            New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
        }
        Copy-OneFile -SourceFile $file.FullName -TargetFile $target -RenamedAside $RenamedAside
    }
}

function Restore-AsideBackups {
    # Roll back a partial replace by restoring each renamed-aside original.
    param([System.Collections.ArrayList]$RenamedAside)
    foreach ($entry in $RenamedAside) {
        try {
            if (Test-Path -LiteralPath $entry.Original) {
                Remove-Item -LiteralPath $entry.Original -Force -ErrorAction SilentlyContinue
            }
            if (Test-Path -LiteralPath $entry.Aside) {
                Move-Item -LiteralPath $entry.Aside -Destination $entry.Original -Force
            }
            Write-Log "Rolled back: $($entry.Original)"
        } catch {
            Write-Log "Rollback failed for $($entry.Original): $($_.Exception.Message)"
        }
    }
}

function Remove-Orphans {
    # Delete install-dir files that no longer exist in the new version, keeping parity
    # with the old clean-swap behavior. Never touches our aside backups or log files.
    param([string]$Source, [string]$Destination)
    $sourceRoot = (Resolve-Path -LiteralPath $Source).Path.TrimEnd('\', '/')
    $destRoot = (Resolve-Path -LiteralPath $Destination).Path.TrimEnd('\', '/')
    foreach ($file in (Get-ChildItem -LiteralPath $destRoot -Recurse -File -Force)) {
        if ($file.Name -like "*.old-*" -or $file.Extension -eq ".log") { continue }
        $relative = $file.FullName.Substring($destRoot.Length).TrimStart('\', '/')
        $sourceEquivalent = Join-Path $sourceRoot $relative
        if (-not (Test-Path -LiteralPath $sourceEquivalent)) {
            try {
                Remove-Item -LiteralPath $file.FullName -Force -ErrorAction Stop
                Write-Log "Removed orphan: $relative"
            } catch {
                Write-Log "Could not remove orphan $relative : $($_.Exception.Message)"
            }
        }
    }
}

Write-Log "Starting staged update."
Write-Log "InstallDir=$InstallDir"
Write-Log "StagingDir=$StagingDir"

# --- Circuit breaker: abort if too many failed attempts ---
$maxAttempts = 3
try {
    if (Test-Path -LiteralPath $PendingFilePath) {
        $pendingJson = Get-Content -Raw -Path $PendingFilePath | ConvertFrom-Json
        $currentAttempts = 0
        if (Get-Member -InputObject $pendingJson -Name "failedAttempts" -MemberType NoteProperty) {
            $currentAttempts = $pendingJson.failedAttempts
        }
        if ($currentAttempts -ge $maxAttempts) {
            Write-Log "Update has failed $currentAttempts times (max $maxAttempts). Giving up and removing pending update."
            Remove-Item -Force $PendingFilePath -ErrorAction SilentlyContinue
            Relaunch-App
            return
        }
    }
} catch {
    Write-Log "Failed to read failedAttempts: $($_.Exception.Message)"
}

$renamed = New-Object System.Collections.ArrayList

try {
    # --- Wait for the target process to exit ---
    Write-Log "Waiting for process $TargetPid to exit..."
    if (Wait-ForProcessExit -ProcessId $TargetPid -TimeoutSeconds 60) {
        Write-Log "Process exited."
    } else {
        Write-Log "Target process still running after 60s. Stopping forcefully."
        Stop-Process -Id $TargetPid -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
    }

    # Stop any other costats instance still holding the install dir (e.g. a prior relaunch).
    Stop-CostatsProcessesUnder -Directory $InstallDir

    # Let antivirus / Search indexer / single-file extraction handles release.
    Write-Log "Waiting for file handles to release..."
    Start-Sleep -Seconds 3

    # --- Validate staging ---
    if (-not (Test-Path -LiteralPath $StagingDir)) {
        Write-Log "Staging directory not found: $StagingDir"
        Relaunch-App
        return
    }

    $stagedExeCheck = Join-Path $StagingDir $ExecutableRelativePath
    if (-not (Test-Path -LiteralPath $stagedExeCheck)) {
        Write-Log "Staged executable not found: $stagedExeCheck"
        Relaunch-App
        return
    }

    if (-not (Test-Path -LiteralPath $InstallDir)) {
        New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    }

    # Clear stale aside-backups from any previous interrupted run.
    Remove-AsideBackups -Directory $InstallDir

    # --- Per-file in-place replace ---
    try {
        Replace-Files -Source $StagingDir -Destination $InstallDir -RenamedAside $renamed
        Write-Log "Copied staged files in place ($($renamed.Count) existing file(s) replaced)."
    } catch {
        Write-Log "File replacement failed: $($_.Exception.Message). Rolling back."
        Restore-AsideBackups -RenamedAside $renamed
        Increment-FailedAttempts
        Relaunch-App -SkipUpdateApply
        return
    }

    # --- Verify new executable ---
    $newExePath = Join-Path $InstallDir $ExecutableRelativePath
    if (-not (Test-Path -LiteralPath $newExePath)) {
        Write-Log "New executable not found after replace: $newExePath. Rolling back."
        Restore-AsideBackups -RenamedAside $renamed
        $newExePath = $null
        Increment-FailedAttempts
        Relaunch-App -SkipUpdateApply
        return
    }

    # Remove files dropped in the new version.
    Remove-Orphans -Source $StagingDir -Destination $InstallDir

    Write-Log "Replace completed successfully."

    # --- Cleanup ---
    if (Test-Path -LiteralPath $PendingFilePath) {
        Remove-Item -Force $PendingFilePath -ErrorAction SilentlyContinue
    }
    Remove-AsideBackups -Directory $InstallDir
    try {
        if (Test-Path -LiteralPath $StagingDir) {
            Remove-Item -LiteralPath $StagingDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    } catch {
        Write-Log "Staging cleanup failed (non-fatal): $($_.Exception.Message)"
    }

    # --- Launch updated app ---
    Relaunch-App
    Write-Log "Update finished successfully."

} catch {
    Write-Log "Unexpected error: $($_.Exception.Message)"
    Restore-AsideBackups -RenamedAside $renamed
    Increment-FailedAttempts
    Relaunch-App -SkipUpdateApply
}
""";
}
