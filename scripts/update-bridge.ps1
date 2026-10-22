param([switch]$Start, [switch]$NoElevation)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
$repoRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repoRoot 'artifacts\staging'
$destination = Join-Path $repoRoot 'artifacts\bridge'
$info = Test-ReleasePackage $source @('WallpaperBridge.exe','WallpaperBridge.dll','wallpaper/index.html','wallpaper/wallpaper.js','wallpaper/project.json')
$origin = 'http://127.0.0.1:17632'
$health = $null
try { $health = Invoke-RestMethod -Uri ($origin+'/v1/health') -TimeoutSec 2 } catch { }
$wasRunning = [bool]$health
$admin = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($wasRunning -and $health.elevated -and -not $admin) {
    if ($NoElevation) { throw 'Administrator context required; candidate was not installed' }
    $arguments='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$PSCommandPath+'" -NoElevation'
    if ($Start) { $arguments+=' -Start' }
    $elevated=Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -PassThru
    $elevated.WaitForExit()
    if ($elevated.ExitCode -ne 0) { throw 'Elevated bridge update failed; inspect artifacts/validation/bridge-update.json' }
    Write-Output 'Bridge updated in its administrator context'
    return
}
$task = Get-ScheduledTask -TaskName 'WallpaperDashboardBridge' -TaskPath '\' -ErrorAction SilentlyContinue
$ownTask = $task -and ($task.Actions.Arguments -join ' ').Contains(('"' + (Join-Path $PSScriptRoot 'install-bridge-autostart.ps1') + '"'))
$taskRunning = $ownTask -and $task.State -eq 'Running'
$dataDirectory = $null; $beforeConfig = $null; $privateHashes=@{}
if ($wasRunning) {
    if ($health.service -ne 'portrait-wallpaper-bridge') { throw 'Bridge port belongs to another service' }
    $processes = @(Get-Process WallpaperBridge -ErrorAction SilentlyContinue)
    if ($processes.Count -ne 1 -or -not $processes[0].Path -or [IO.Path]::GetFullPath($processes[0].Path) -ne (Join-Path $destination 'WallpaperBridge.exe')) { throw 'Cannot confirm ownership of running bridge; use its administrator context' }
    if ($health.elevated -and -not $admin) { throw 'Run the update in an administrator terminal to preserve the running bridge' }
    $session = (Invoke-RestMethod -Uri ($origin+'/v1/setup/session') -Method Post -Headers @{Origin=$origin;'X-Wallpaper-Setup'='1'} -ContentType 'application/json' -Body '{}').session
    $beforeConfig = Invoke-RestMethod -Uri ($origin+'/v1/setup/config/read') -Method Post -Headers @{Origin=$origin;'X-Wallpaper-Admin'=$session} -ContentType 'application/json' -Body '{}'
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
        $launch = @{ FilePath=(Join-Path $destination 'WallpaperBridge.exe'); WorkingDirectory=$destination; WindowStyle='Hidden'; PassThru=$true }
        if ($dataDirectory) { $launch.ArgumentList='--data-dir "'+$dataDirectory+'"' }
        Start-Process @launch | Out-Null
    }
}
$lifecycle=@{ stopped=$false; launched=$false; recovered=$false }
$resultPath=Join-Path $repoRoot 'artifacts\validation\bridge-update.json'
New-Item -ItemType Directory -Path (Split-Path -Parent $resultPath) -Force | Out-Null
try {
$result = Invoke-ReleaseTransaction -Source $source -Destination $destination -BackupRoot (Join-Path $repoRoot 'artifacts\backups\bridge-releases') -Prepare {
    param($candidatePath)
    Test-ReleasePackage $candidatePath @('WallpaperBridge.exe','WallpaperBridge.dll','wallpaper/index.html') | Out-Null
} -BeforeSwitch {
    if ($wasRunning) {
        & (Join-Path $PSScriptRoot 'stop-bridge.ps1'); $lifecycle.stopped=$true
        foreach($file in $privateHashes.Keys) {
            if ((Get-FileHash -LiteralPath (Join-Path $dataDirectory $file)).Hash -ne $privateHashes[$file]) { throw 'Private configuration changed before update; retry after inspecting settings' }
        }
    }
} -Verify {
    $lifecycle.launched=$wasRunning -or [bool]$Start
    Start-PreservedBridge
    if ($wasRunning -or $Start) {
        $deadline=[DateTimeOffset]::UtcNow.AddSeconds(45); $ready=$false
        while ([DateTimeOffset]::UtcNow -lt $deadline) {
            try {
                $current=Invoke-RestMethod -Uri ($origin+'/v1/health') -TimeoutSec 2
                if ($current.buildId -eq $info.buildId) {
                    $session=(Invoke-RestMethod -Uri ($origin+'/v1/setup/session') -Method Post -Headers @{Origin=$origin;'X-Wallpaper-Setup'='1'} -ContentType 'application/json' -Body '{}').session
                    $config=Invoke-RestMethod -Uri ($origin+'/v1/setup/config/read') -Method Post -Headers @{Origin=$origin;'X-Wallpaper-Admin'=$session} -ContentType 'application/json' -Body '{}'
                    if ($beforeConfig -and ($config.readToken -ne $beforeConfig.readToken -or (ConvertTo-Json -Depth 15 $config.accounts -Compress) -ne (ConvertTo-Json -Depth 15 $beforeConfig.accounts -Compress) -or $config.quotaRefreshEnabled -ne $beforeConfig.quotaRefreshEnabled -or $config.quotaRefreshMinutes -ne $beforeConfig.quotaRefreshMinutes -or (ConvertTo-Json $config.city -Compress) -ne (ConvertTo-Json $beforeConfig.city -Compress) -or (ConvertTo-Json $config.sensors -Compress) -ne (ConvertTo-Json $beforeConfig.sensors -Compress))) { throw 'Configuration changed during update' }
                    $snapshot=Invoke-RestMethod -Uri ($origin+'/v1/snapshot') -Headers @{'X-Wallpaper-Token'=$config.readToken} -TimeoutSec 2
                    if ($snapshot.hardware.observedAt) {
                        if($beforeConfig -and ($config.cpampUrl -ne $beforeConfig.cpampUrl -or $config.managementConfigured -ne $beforeConfig.managementConfigured)) { throw 'Configuration changed during update' }
                        $ready=$true; break
                    }
                }
            } catch { if ($_.Exception.Message -eq 'Configuration changed during update') { throw } }
            Start-Sleep -Milliseconds 500
        }
        if (-not $ready) { throw 'Candidate bridge failed health/sample checks' }
    }
} -BeforeRollback {
    if ($lifecycle.launched) { & (Join-Path $PSScriptRoot 'stop-bridge.ps1') }
} -Recover {
    if ($lifecycle.stopped) {
        Start-PreservedBridge
        $deadline=[DateTimeOffset]::UtcNow.AddSeconds(45)
        while ([DateTimeOffset]::UtcNow -lt $deadline) {
            try {
                $restored=Invoke-RestMethod -Uri ($origin+'/v1/health') -TimeoutSec 2
                if ($restored.service -eq 'portrait-wallpaper-bridge') { $lifecycle.recovered=$true; break }
            } catch { }
            Start-Sleep -Milliseconds 500
        }
        if (-not $lifecycle.recovered) { throw 'Previous release restored on disk but runtime recovery failed' }
    }
}
$result | Add-Member -NotePropertyName buildId -NotePropertyValue $info.buildId
$result | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding UTF8
Write-Output ('Installed bridge build ' + $info.buildId + '; backup: ' + $result.backup)
} catch {
    @{installed=$false;buildId=$info.buildId;rollbackAttempted=$lifecycle.stopped;previousRuntimeRecovered=$lifecycle.recovered;error='Update failed; inspect running state and backup before retrying'} | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding UTF8
    throw
}
