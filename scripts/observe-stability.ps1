param(
    [ValidateRange(0.01,10080)][double]$DurationMinutes = 1440,
    [ValidateRange(1,300)][int]$IntervalSeconds = 60,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
if (-not $OutputDirectory) {
    $OutputDirectory=Join-Path (Split-Path -Parent $PSScriptRoot) ('artifacts/analysis/stability-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$rowsPath=Join-Path $OutputDirectory 'samples.jsonl'
if(Test-Path -LiteralPath $rowsPath){throw 'Observation output already exists; choose a fresh directory'}
$statePath=Join-Path $OutputDirectory 'status.json'
$origin='http://127.0.0.1:17632'
$started=[DateTimeOffset]::UtcNow
$deadline=$started.AddMinutes($DurationMinutes)
$readToken=$null; $nextDiagnostics=$started; $diagnostics=$null
$count=0; $unavailable=0; $gaps=0; $previousAt=$started
function Write-State([string]$Phase) {
    @{phase=$Phase;started=$started.ToString('o');expectedEnd=$deadline.ToString('o');updated=[DateTimeOffset]::UtcNow.ToString('o');samples=$count;unavailableSamples=$unavailable;longGaps=$gaps;intervalSeconds=$IntervalSeconds;
      scope='Read-only local observation, not controlled A/B. Gaps may reflect sleep or load. Physical input and injected network failures require separate evidence.'} |
        ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8
}
Write-State 'running'
try {
    do {
        $now=[DateTimeOffset]::UtcNow
        $gap=($now-$previousAt).TotalSeconds; $previousAt=$now
        if($gap -gt $IntervalSeconds*2+10){$gaps++}
        $row=[ordered]@{at=$now.ToString('o');gapSeconds=[math]::Round($gap,1);healthAvailable=$false}
        try {
            $health=Invoke-RestMethod -Uri ($origin+'/v1/health') -TimeoutSec 3
            if($health.service -ne 'portrait-wallpaper-bridge'){throw 'Unexpected service'}
            $row.healthAvailable=$true; $row.buildId=$health.buildId; $row.elevated=$health.elevated
            if(-not $readToken -or $now -ge $nextDiagnostics){
                $session=(Invoke-RestMethod -Uri ($origin+'/v1/setup/session') -Method Post -Headers @{Origin=$origin;'X-Wallpaper-Setup'='1'} -ContentType 'application/json' -Body '{}' -TimeoutSec 3).session
                $config=Invoke-RestMethod -Uri ($origin+'/v1/setup/config/read') -Method Post -Headers @{Origin=$origin;'X-Wallpaper-Admin'=$session} -ContentType 'application/json' -Body '{}' -TimeoutSec 3
                $readToken=$config.readToken
                $hostReport=$config.diagnostics.lastClient
                $diagnostics=@{cacheFaults=$config.diagnostics.cacheFaults;accountCount=@($config.accounts).Count;refreshStatus=$config.quotaRefresh.state.status;
                    client=if($hostReport){@{at=$hostReport.at;buildId=$hostReport.buildId;host=$hostReport.host;width=$hostReport.width;height=$hostReport.height;fps=$hostReport.fps;paused=$hostReport.paused}}else{$null}}
                $nextDiagnostics=$now.AddMinutes(5)
            }
            $snapshot=Invoke-RestMethod -Uri ($origin+'/v1/snapshot') -Headers @{'X-Wallpaper-Token'=$readToken} -TimeoutSec 3
            $row.data=@{hardware=$snapshot.hardware.status;hardwareObservedAt=$snapshot.hardware.observedAt;quota=$snapshot.quota.status;weather=$snapshot.weather.status;epoch=$snapshot.revisions.epoch}
            $row.diagnostics=$diagnostics
        } catch {
            $row.error='Local health or data unavailable'; $unavailable++; $readToken=$null
        }
        $row.processes=@(Get-Process -Name WallpaperBridge,webwallpaper32,webwallpaper64 -ErrorAction SilentlyContinue | ForEach-Object {
            $processItem=$_; $cpu=$null
            try {$cpu=$processItem.CPU}catch{}
            @{id=$processItem.Id;name=$processItem.ProcessName;cpuSeconds=$cpu;privateCommitMiB=[math]::Round($processItem.PrivateMemorySize64/1MB,2);workingSetMiB=[math]::Round($processItem.WorkingSet64/1MB,2)}
        })
        $row | ConvertTo-Json -Depth 10 -Compress | Add-Content -LiteralPath $rowsPath -Encoding UTF8
        $count++; Write-State 'running'
        $remaining=($deadline-[DateTimeOffset]::UtcNow).TotalSeconds
        if($remaining -le 0){break}
        # [math]::Min(int,double) binds to the Int32 overload and truncates a sub-second remainder to 0,
        # which made the 2026-10-22 run spin out 17 extra samples just before its deadline.
        Start-Sleep -Milliseconds ([int]([math]::Min([double]$IntervalSeconds,$remaining)*1000))
    } while([DateTimeOffset]::UtcNow -lt $deadline)
    Write-State 'completed'
    Write-Output ('Observation completed; inspect samples and event coverage: '+$OutputDirectory)
} catch {
    Write-State 'failed'
    throw
}
