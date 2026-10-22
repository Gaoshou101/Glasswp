param([ValidateRange(1024,65535)][int]$Port=17634)
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
. (Join-Path $repoRoot 'scripts/release-common.ps1')
$source=Join-Path $repoRoot 'artifacts/staging'
$build=Test-ReleasePackage $source @('WallpaperBridge.exe','WallpaperBridge.runtimeconfig.json')
$testRoot=Join-Path $repoRoot ('artifacts/validation/bridge-release-'+[Guid]::NewGuid().ToString('N'))
$target=Join-Path $testRoot 'running'
$bad=Join-Path $testRoot 'bad-candidate'
$private=Join-Path $testRoot 'private'
$backups=Join-Path $testRoot 'backups'
$origin='http://127.0.0.1:'+$Port
if(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue){throw 'Isolated test port already in use'}
New-Item -ItemType Directory -Path $target,$bad | Out-Null
Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $target -Recurse
Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $bad -Recurse
$script:testProcess=$null
$script:launchNumber=0
function Stop-Isolated {
    if($script:testProcess -and -not $script:testProcess.HasExited){
        $processPath=$script:testProcess.Path
        if([IO.Path]::GetFullPath($processPath) -ne (Join-Path $target 'WallpaperBridge.exe')){throw 'Unexpected test process path'}
        Stop-Process -Id $script:testProcess.Id
        if(-not $script:testProcess.WaitForExit(5000)){throw 'Test process did not stop'}
    }
}
function Start-Isolated {
    $script:launchNumber++
    $script:testProcess=Start-Process -FilePath (Join-Path $target 'WallpaperBridge.exe') -ArgumentList ('--data-dir "'+$private+'" --port '+$Port) -WorkingDirectory $target -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $testRoot ('launch-'+$script:launchNumber+'.out')) -RedirectStandardError (Join-Path $testRoot ('launch-'+$script:launchNumber+'.err'))
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds(20)
    while([DateTimeOffset]::UtcNow -lt $deadline){
        if($script:testProcess.HasExited){throw 'Isolated candidate exited during startup'}
        try {
            $health=Invoke-RestMethod -Uri ($origin+'/v1/health') -TimeoutSec 1
            if($health.buildId -eq $build.buildId){return}
        }catch{}
        Start-Sleep -Milliseconds 200
    }
    throw 'Isolated candidate failed health check'
}
function Read-IsolatedConfig {
    $session=(Invoke-RestMethod -Uri ($origin+'/v1/setup/session') -Method Post -Headers @{Origin=$origin;'X-Wallpaper-Setup'='1'} -ContentType 'application/json' -Body '{}' -TimeoutSec 3).session
    Invoke-RestMethod -Uri ($origin+'/v1/setup/config/read') -Method Post -Headers @{Origin=$origin;'X-Wallpaper-Admin'=$session} -ContentType 'application/json' -Body '{}' -TimeoutSec 3
}
try {
    Start-Isolated
    $session=(Invoke-RestMethod -Uri ($origin+'/v1/setup/session') -Method Post -Headers @{Origin=$origin;'X-Wallpaper-Setup'='1'} -ContentType 'application/json' -Body '{}' -TimeoutSec 3).session
    Invoke-RestMethod -Uri ($origin+'/v1/setup/config') -Method Post -Headers @{Origin=$origin;'X-Wallpaper-Admin'=$session} -ContentType 'application/json' -Body '{"quotaRefreshEnabled":false,"quotaRefreshMinutes":17}' -TimeoutSec 3 | Out-Null
    $before=Read-IsolatedConfig
    Stop-Isolated
    $configHash=(Get-FileHash -LiteralPath (Join-Path $private 'config.dpapi')).Hash
    # An intact manifest can still describe a runtime that fails startup. Exercise that gate.
    [IO.File]::WriteAllText((Join-Path $bad 'WallpaperBridge.runtimeconfig.json'),'{"runtimeOptions":{"tfm":"invalid","framework":{"name":"Missing.Test.Runtime","version":"99.0.0"}}}')
    $manifest=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $bad 'build-info.json') | ConvertFrom-Json
    $manifest.files.'WallpaperBridge.runtimeconfig.json'=(Get-FileHash -LiteralPath (Join-Path $bad 'WallpaperBridge.runtimeconfig.json')).Hash.ToLowerInvariant()
    $manifest | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $bad 'build-info.json') -Encoding UTF8
    Test-ReleasePackage $bad @('WallpaperBridge.exe') | Out-Null
    $runtimeFailed=$false
    try {
        Invoke-ReleaseTransaction -Source $bad -Destination $target -BackupRoot $backups -Verify {Start-Isolated} -BeforeRollback {Stop-Isolated} -Recover {Start-Isolated} | Out-Null
    } catch {if($_.Exception.Message -like 'Isolated candidate*'){$runtimeFailed=$true}else{throw}}
    if(-not $runtimeFailed){throw 'Broken runtime was unexpectedly accepted'}
    $after=Read-IsolatedConfig
    $sampleDeadline=[DateTimeOffset]::UtcNow.AddSeconds(20)
    do {
        $snapshot=Invoke-RestMethod -Uri ($origin+'/v1/snapshot') -Headers @{'X-Wallpaper-Token'=$after.readToken} -TimeoutSec 3
        if($snapshot.hardware.observedAt){break}
        Start-Sleep -Milliseconds 200
    } while([DateTimeOffset]::UtcNow -lt $sampleDeadline)
    $preserved=$before.readToken -eq $after.readToken -and $after.quotaRefreshEnabled -eq $false -and $after.quotaRefreshMinutes -eq 17 -and (Get-FileHash -LiteralPath (Join-Path $private 'config.dpapi')).Hash -eq $configHash
    $restored=Test-ReleasePackage $target @('WallpaperBridge.exe')
    if(-not $preserved -or $restored.buildId -ne $build.buildId -or -not $snapshot.hardware.observedAt){throw ('Rollback verification failed: configuration='+$preserved+'; build='+($restored.buildId -eq $build.buildId)+'; sample='+[bool]$snapshot.hardware.observedAt)}
    @{startupFailureRejected=$true;previousRuntimeRecovered=$true;configAndTicketPreserved=$preserved;restoredBuildId=$restored.buildId;isolatedPort=$Port;hardwareSamplePresent=[bool]$snapshot.hardware.observedAt;productionUntouched=$true;at=[DateTimeOffset]::UtcNow.ToString('o')} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $repoRoot 'artifacts/validation/bridge-release-rollback.json') -Encoding UTF8
    Write-Output 'PASS isolated bridge startup failure restores previous files, runtime, DPAPI settings and display ticket'
} finally {Stop-Isolated}
