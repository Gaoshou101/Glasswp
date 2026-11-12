param([switch]$Start, [switch]$NoElevation, [string]$RuntimeRoot, [string]$Source, [string]$DataDirectory)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
$repoRoot = Split-Path -Parent $PSScriptRoot
$runtimeRoot = Get-BridgeRuntimeRoot $RuntimeRoot
if (-not $Source) { $Source = Join-Path $repoRoot 'artifacts\staging' }
$source = Get-ReleasePath $Source
foreach ($path in @($source, $runtimeRoot, $DataDirectory)) { if ($path -and $path.IndexOfAny([char[]]@('"', "`r", "`n")) -ge 0) { throw 'Installation paths must not contain quotes or line breaks' } }
$destination = Join-Path $runtimeRoot 'current'
$requiredFiles = @('WallpaperBridge.exe','WallpaperBridge.dll','run-bridge.ps1','wallpaper/index.html','wallpaper/wallpaper.js','wallpaper/project.json')
$info = Test-ReleasePackage $source $requiredFiles
$stateResult = Join-Path $runtimeRoot 'state\bridge-update.json'
$repoResult = Join-Path $repoRoot 'artifacts\validation\bridge-update.json'
$explicitDataDirectory = [bool]$DataDirectory
$installContext = Get-BridgeInstallContext $runtimeRoot $DataDirectory
if (-not $installContext.ownershipNeedsElevation -or $installContext.task -or $explicitDataDirectory) { $DataDirectory = $installContext.dataDirectory }
if (-not (Test-IsAdministrator)) {
    # The runtime is writable only by administrators: install through one UAC prompt, then copy the report back.
    if ($NoElevation) { throw 'Administrator context required; candidate was not installed' }
    $arguments='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$PSCommandPath+'" -NoElevation -RuntimeRoot "'+$runtimeRoot+'" -Source "'+$source+'"'
    if ($Start) { $arguments+=' -Start' }
    if ($DataDirectory) { $arguments+=' -DataDirectory "'+$DataDirectory+'"' }
    $startedAt = [DateTime]::UtcNow
    $elevated=Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -PassThru
    $elevated.WaitForExit()
    if ((Test-Path -LiteralPath $stateResult) -and (Get-Item -LiteralPath $stateResult).LastWriteTimeUtc -ge $startedAt.AddSeconds(-2)) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $repoResult) -Force | Out-Null
        Copy-Item -LiteralPath $stateResult -Destination $repoResult -Force
    }
    if ($elevated.ExitCode -ne 0) { throw ('Elevated bridge update failed; inspect ' + $stateResult) }
    Write-Output ('Bridge build ' + $info.buildId + ' installed in ' + $destination)
    return
}
$dataDirectory = $installContext.dataDirectory
if (Test-Path -LiteralPath $runtimeRoot) { Assert-ProtectedRuntime $runtimeRoot | Out-Null } else { Initialize-ProtectedRuntime $runtimeRoot | Out-Null }
New-Item -ItemType Directory -Path (Split-Path -Parent $stateResult) -Force | Out-Null
$health = $null
try { $health = Invoke-RestMethod -Uri ($script:BridgeOrigin+'/v1/health') -TimeoutSec 2 } catch { }
$wasRunning = [bool]$health
$launcher = Get-BridgeLauncherPath $runtimeRoot
$task = Get-ScheduledTask -TaskName 'WallpaperDashboardBridge' -TaskPath '\' -ErrorAction SilentlyContinue
$ownTask = $task -and ($task.Actions.Arguments -join ' ').IndexOf(('"' + $launcher + '"'), [StringComparison]::OrdinalIgnoreCase) -ge 0
$taskRunning = $ownTask -and $task.State -eq 'Running'
$beforeConfig = $null; $privateHashes=@{}
$destinationExe = Join-Path $destination 'WallpaperBridge.exe'
if ($wasRunning) {
    if ($health.service -ne 'portrait-wallpaper-bridge') { throw 'Bridge port belongs to another service' }
    $processes = @(Get-Process WallpaperBridge -ErrorAction SilentlyContinue)
    if ($processes.Count -ne 1 -or -not $processes[0].Path) { throw 'Cannot confirm ownership of the running bridge' }
    if ([IO.Path]::GetFullPath($processes[0].Path) -ne $destinationExe) { throw ('Running bridge is not the protected runtime (' + $processes[0].Path + '); run scripts/migrate-bridge-runtime.ps1 first') }
    $beforeConfig = Read-BridgeConfig
    $command = (Get-CimInstance Win32_Process -Filter ('ProcessId=' + $processes[0].Id)).CommandLine
    if ($command -match '--data-dir\s+"([^"]+)"') { $dataDirectory=$Matches[1] }
    elseif ($command -match '--data-dir\s+(\S+)') { $dataDirectory=$Matches[1] }
    if (-not $dataDirectory) { throw 'Running bridge needs an explicit --data-dir before a safe update' }
    foreach($file in @('config.dpapi','read-token.dpapi')) {
        $privateHashes[$file]=(Get-FileHash -LiteralPath (Join-Path $dataDirectory $file) -Algorithm SHA256).Hash
    }
}
function Start-PreservedBridge {
    if ($taskRunning) { Start-ScheduledTask -TaskName 'WallpaperDashboardBridge' -TaskPath '\' }
    elseif ($wasRunning -or $Start) {
        $launch = @{ FilePath=$destinationExe; WorkingDirectory=$destination; WindowStyle='Hidden'; PassThru=$true }
        if ($dataDirectory) { $launch.ArgumentList='--data-dir "'+$dataDirectory+'"' }
        Start-Process @launch | Out-Null
    }
}
$lifecycle=@{ stopped=$false; launched=$false; recovered=$false }
try {
$result = Invoke-ReleaseTransaction -Source $source -Destination $destination -BackupRoot (Join-Path $runtimeRoot 'backups') -Prepare {
    param($candidatePath)
    # Re-check hashes and permissions on the protected copy, not on the user-writable staging folder.
    Test-ReleasePackage $candidatePath $requiredFiles | Out-Null
    Assert-ProtectedRuntime $runtimeRoot | Out-Null
} -BeforeSwitch {
    if ($wasRunning) {
        & (Join-Path $PSScriptRoot 'stop-bridge.ps1') -RuntimeRoot $runtimeRoot; $lifecycle.stopped=$true
        foreach($file in $privateHashes.Keys) {
            if ((Get-FileHash -LiteralPath (Join-Path $dataDirectory $file)).Hash -ne $privateHashes[$file]) { throw 'Private configuration changed before update; retry after inspecting settings' }
        }
    }
} -Verify {
    $lifecycle.launched=$wasRunning -or [bool]$Start
    Start-PreservedBridge
    if ($wasRunning -or $Start) { Wait-BridgeReady -BuildId $info.buildId -ExpectedConfig $beforeConfig -ExpectedPath $destinationExe | Out-Null }
} -BeforeRollback {
    if ($lifecycle.launched) { & (Join-Path $PSScriptRoot 'stop-bridge.ps1') -RuntimeRoot $runtimeRoot }
} -Recover {
    if ($lifecycle.stopped) {
        Start-PreservedBridge
        Wait-BridgeReady -ExpectedConfig $beforeConfig -ExpectedPath $destinationExe | Out-Null
        $lifecycle.recovered=$true
    }
}
$removedBackups = Remove-OldRuntimeBackups (Join-Path $runtimeRoot 'backups') 2
$result | Add-Member -NotePropertyName dataDirectory -NotePropertyValue $dataDirectory
$result | Add-Member -NotePropertyName buildId -NotePropertyValue $info.buildId
$result | Add-Member -NotePropertyName removedOldBackups -NotePropertyValue $removedBackups
$result | Add-Member -NotePropertyName verifiedAt -NotePropertyValue ([DateTimeOffset]::Now.ToString('o'))
$result | ConvertTo-Json | Set-Content -LiteralPath $stateResult -Encoding UTF8
Write-Output ('Installed bridge build ' + $info.buildId + '; backup: ' + $result.backup)
} catch {
    @{installed=$false;buildId=$info.buildId;dataDirectory=$dataDirectory;rollbackAttempted=$lifecycle.stopped;previousRuntimeRecovered=$lifecycle.recovered;error=$_.Exception.Message;verifiedAt=[DateTimeOffset]::Now.ToString('o')} | ConvertTo-Json | Set-Content -LiteralPath $stateResult -Encoding UTF8
    throw
}
