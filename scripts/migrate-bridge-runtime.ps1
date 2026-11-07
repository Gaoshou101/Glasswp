param([string]$RuntimeRoot, [switch]$NoElevation)
# One-shot move of the administrator bridge from the repository (artifacts\bridge, started by the
# repository script) to the protected runtime. On any failure the original task definition is
# re-registered and the previous bridge is started again; the private DPAPI directory never moves.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
$repoRoot = Split-Path -Parent $PSScriptRoot
$runtimeRoot = Get-BridgeRuntimeRoot $RuntimeRoot
$taskName = 'WallpaperDashboardBridge'
$source = Join-Path $repoRoot 'artifacts\staging'
$legacyExe = Join-Path $repoRoot 'artifacts\bridge\WallpaperBridge.exe'
$legacyLauncher = Join-Path $PSScriptRoot 'install-bridge-autostart.ps1'
$destination = Join-Path $runtimeRoot 'current'
$runtimeExe = Join-Path $destination 'WallpaperBridge.exe'
$launcher = Get-BridgeLauncherPath $runtimeRoot
$stateResult = Join-Path $runtimeRoot 'state\migration.json'
$repoResult = Join-Path $repoRoot 'artifacts\validation\bridge-runtime-migration.json'
$requiredFiles = @('WallpaperBridge.exe','WallpaperBridge.dll','run-bridge.ps1','wallpaper/index.html','wallpaper/wallpaper.js','wallpaper/project.json')
$info = Test-ReleasePackage $source $requiredFiles
if (-not (Test-IsAdministrator)) {
    if ($NoElevation) { throw 'Administrator context required; nothing was changed' }
    $arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $PSCommandPath + '" -NoElevation -RuntimeRoot "' + $runtimeRoot + '"'
    $startedAt = [DateTime]::UtcNow
    $elevated = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -PassThru
    $elevated.WaitForExit()
    if ((Test-Path -LiteralPath $stateResult) -and (Get-Item -LiteralPath $stateResult).LastWriteTimeUtc -ge $startedAt.AddSeconds(-2)) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $repoResult) -Force | Out-Null
        Copy-Item -LiteralPath $stateResult -Destination $repoResult -Force
        Get-Content -Raw -LiteralPath $repoResult
    }
    if ($elevated.ExitCode -ne 0) { throw ('Runtime migration failed; inspect ' + $stateResult) }
    return
}

$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$steps = [Collections.Generic.List[string]]::new()
$result = [ordered]@{ success = $false; buildId = $info.buildId; commit = $info.commit; dirty = $info.dirty; runtimeRoot = $runtimeRoot; startedAt = [DateTimeOffset]::Now.ToString('o') }
# Compare what matters for the logon start: account, elevation, triggers and restart/time-limit policy.
function Get-TaskShape([string]$Name) {
    $current = Get-ScheduledTask -TaskName $Name -TaskPath '\'
    [ordered]@{
        user = $current.Principal.UserId; logon = [string]$current.Principal.LogonType; runLevel = [string]$current.Principal.RunLevel
        triggers = @($current.Triggers | ForEach-Object { $_.CimClass.CimClassName + '|' + $_.Delay + '|' + $_.UserId + '|' + $_.Enabled })
        executionTimeLimit = $current.Settings.ExecutionTimeLimit; restartCount = $current.Settings.RestartCount; restartInterval = $current.Settings.RestartInterval
        startWhenAvailable = $current.Settings.StartWhenAvailable; multipleInstances = [string]$current.Settings.MultipleInstances
        batteries = [string]$current.Settings.DisallowStartIfOnBatteries + '|' + [string]$current.Settings.StopIfGoingOnBatteries
    } | ConvertTo-Json -Compress
}
function Wait-BridgeStopped {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        $running = $true
        try { Invoke-RestMethod -Uri ($script:BridgeOrigin + '/v1/health') -TimeoutSec 1 | Out-Null } catch { $running = $false }
        if (-not $running -and -not @(Get-Process WallpaperBridge -ErrorAction SilentlyContinue).Count) { return }
        Start-Sleep -Milliseconds 300
    }
    throw 'Bridge did not stop'
}
$originalXml = $null; $beforeConfig = $null; $previousBuild = $null
try {
    # 1. Confirm the current legacy layout and capture everything needed to restore it.
    $task = Get-ScheduledTask -TaskName $taskName -TaskPath '\'
    $taskArguments = $task.Actions.Arguments -join ' '
    if ($taskArguments.IndexOf(('"' + $launcher + '"'), [StringComparison]::OrdinalIgnoreCase) -ge 0) { throw 'The task already starts the protected runtime' }
    if ($taskArguments.IndexOf(('"' + $legacyLauncher + '"'), [StringComparison]::OrdinalIgnoreCase) -lt 0) { throw 'The task does not start the repository launcher; inspect it before migrating' }
    $originalXml = Export-ScheduledTask -TaskName $taskName -TaskPath '\'
    $originalShape = Get-TaskShape $taskName
    $health = Invoke-RestMethod -Uri ($script:BridgeOrigin + '/v1/health') -TimeoutSec 2
    if ($health.service -ne 'portrait-wallpaper-bridge' -or -not $health.elevated) { throw 'The running bridge is not the elevated wallpaper bridge' }
    $previousBuild = $health.buildId
    $processes = @(Get-Process WallpaperBridge -ErrorAction SilentlyContinue)
    if ($processes.Count -ne 1 -or [IO.Path]::GetFullPath($processes[0].Path) -ne $legacyExe) { throw 'Cannot confirm the running bridge is the repository runtime' }
    $command = (Get-CimInstance Win32_Process -Filter ('ProcessId=' + $processes[0].Id)).CommandLine
    if ($command -match '--data-dir\s+"([^"]+)"') { $dataDirectory = $Matches[1] } else { throw 'The running bridge has no explicit --data-dir' }
    $beforeConfig = Read-BridgeConfig
    $privateHashes = @{}
    foreach ($file in @('config.dpapi', 'read-token.dpapi')) { $privateHashes[$file] = (Get-FileHash -LiteralPath (Join-Path $dataDirectory $file) -Algorithm SHA256).Hash }
    $result.previousBuildId = $previousBuild; $result.dataDirectory = $dataDirectory; $result.accountCount = @($beforeConfig.accounts).Count
    $steps.Add('captured legacy task, process, configuration and private file hashes')

    # 2. Create the protected runtime and install the verified release into it.
    Initialize-ProtectedRuntime $runtimeRoot | Out-Null
    $originalXml | Set-Content -LiteralPath (Join-Path $runtimeRoot ('state\task-before-' + $stamp + '.xml')) -Encoding Unicode
    $steps.Add('protected runtime root initialized and verified')
    $install = Invoke-ReleaseTransaction -Source $source -Destination $destination -BackupRoot (Join-Path $runtimeRoot 'backups') -Prepare {
        param($candidatePath)
        Test-ReleasePackage $candidatePath $requiredFiles | Out-Null
        Assert-ProtectedRuntime $runtimeRoot | Out-Null
    }
    $result.installedBackup = $install.backup
    $steps.Add('release copied into the runtime and re-verified there')

    # 3. Switch only the task action; restore the exported definition on any failure.
    $switched = $false
    try {
        & (Join-Path $PSScriptRoot 'stop-bridge.ps1') -RuntimeRoot $runtimeRoot | Out-Null
        Wait-BridgeStopped
        foreach ($file in $privateHashes.Keys) {
            if ((Get-FileHash -LiteralPath (Join-Path $dataDirectory $file) -Algorithm SHA256).Hash -ne $privateHashes[$file]) { throw 'Private configuration changed during migration' }
        }
        $launcherState = Join-Path $runtimeRoot 'state\autostart-runtime.json'
        if (Test-Path -LiteralPath $launcherState) { Remove-Item -LiteralPath $launcherState }
        Set-ScheduledTask -TaskName $taskName -TaskPath '\' -Action (New-BridgeTaskAction $runtimeRoot $dataDirectory) | Out-Null
        $switched = $true
        $steps.Add('task action switched to the protected launcher')
        Start-ScheduledTask -TaskName $taskName -TaskPath '\'
        $ready = Wait-BridgeReady -BuildId $info.buildId -ExpectedConfig $beforeConfig -ExpectedPath $runtimeExe -TimeoutSeconds 90
        $launcherResult = $null
        for ($attempt = 0; $attempt -lt 20 -and -not $launcherResult; $attempt++) {
            if (Test-Path -LiteralPath $launcherState) { try { $launcherResult = Get-Content -Raw -LiteralPath $launcherState | ConvertFrom-Json } catch { } }
            if (-not $launcherResult) { Start-Sleep -Milliseconds 500 }
        }
        if (-not $launcherResult -or -not $launcherResult.success -or $launcherResult.buildId -ne $info.buildId) { throw 'The protected launcher did not confirm configuration and display ticket' }
        $afterXml = Export-ScheduledTask -TaskName $taskName -TaskPath '\'
        if ((Get-TaskShape $taskName) -ne $originalShape) { throw 'Task principal, trigger or settings changed during migration' }
        $afterXml | Set-Content -LiteralPath (Join-Path $runtimeRoot ('state\task-after-' + $stamp + '.xml')) -Encoding Unicode
        foreach ($file in $privateHashes.Keys) {
            if ((Get-FileHash -LiteralPath (Join-Path $dataDirectory $file) -Algorithm SHA256).Hash -ne $privateHashes[$file]) { $result.privateFileRewritten = $file }
        }
        $steps.Add('new bridge healthy: build, elevation, process path, configuration, display ticket and hardware sample verified')
    } catch {
        $failure = $_
        $result.rollback = 'started'
        try {
            & (Join-Path $PSScriptRoot 'stop-bridge.ps1') -RuntimeRoot $runtimeRoot | Out-Null
            Wait-BridgeStopped
            if ($switched) { Register-ScheduledTask -TaskName $taskName -TaskPath '\' -Xml $originalXml -Force | Out-Null }
            Start-ScheduledTask -TaskName $taskName -TaskPath '\'
            Wait-BridgeReady -BuildId $previousBuild -ExpectedConfig $beforeConfig -ExpectedPath $legacyExe -TimeoutSeconds 90 | Out-Null
            $result.rollback = 'previous task and bridge restored'
        } catch { $result.rollback = 'FAILED: ' + $_.Exception.Message }
        throw $failure
    }
    Assert-ProtectedRuntime $runtimeRoot | Out-Null
    $result.success = $true
    $result.task = [ordered]@{ action = (Get-ScheduledTask -TaskName $taskName -TaskPath '\').Actions[0].Arguments; principalsTriggersSettingsUnchanged = $true }
    $result.runtimeAcl = (Get-Acl -LiteralPath $runtimeRoot).Sddl
} catch {
    $result.error = $_.Exception.Message
    throw
} finally {
    $result.steps = @($steps)
    $result.finishedAt = [DateTimeOffset]::Now.ToString('o')
    if (Test-Path -LiteralPath (Join-Path $runtimeRoot 'state')) {
        $json = $result | ConvertTo-Json -Depth 6
        $json | Set-Content -LiteralPath $stateResult -Encoding UTF8
        $json | Set-Content -LiteralPath (Join-Path $runtimeRoot ('state\migration-' + $stamp + '.json')) -Encoding UTF8
    }
}