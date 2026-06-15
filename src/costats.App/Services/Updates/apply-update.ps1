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
