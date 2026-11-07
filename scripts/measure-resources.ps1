param([ValidateRange(1,172800)][int]$Samples = 120, [ValidateRange(1,300)][double]$IntervalSeconds = 5, [string]$OutputPath, [string]$Label = 'current-use')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputPath) { $OutputPath = Join-Path $repoRoot ('artifacts/analysis/resources-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.json') }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath) -Force | Out-Null
$logical = [int](Get-CimInstance Win32_ComputerSystem).NumberOfLogicalProcessors
$initial = @(Get-CimInstance Win32_Process)
$groups = [ordered]@{
    portraitWeb = @($initial | Where-Object { $_.Name -match '^webwallpaper(32|64)\.exe$' } | ForEach-Object { [int]$_.ProcessId })
    bridge = @($initial | Where-Object { $_.Name -eq 'WallpaperBridge.exe' } | ForEach-Object { [int]$_.ProcessId })
    wallpaperHostOtherScreens = @($initial | Where-Object { $_.Name -match '^(wallpaper(32|64)|wallpaperservice32|wallpaperui)\.exe$' } | ForEach-Object { [int]$_.ProcessId })
    desktopCompositor = @($initial | Where-Object { $_.Name -eq 'dwm.exe' } | ForEach-Object { [int]$_.ProcessId })
}
# Label each Web process by its CEF role so a change can be attributed to the GPU process, renderer or browser.
$roles = @{}
foreach ($process in @($initial | Where-Object { $_.Name -match '^webwallpaper(32|64)\.exe$' })) {
    $commandLine = [string]$process.CommandLine
    $roles[[int]$process.ProcessId] = if ($commandLine -match '--type=gpu-process') { 'gpu' } elseif ($commandLine -match '--type=renderer') { 'renderer' } elseif ($commandLine -match '--utility-sub-type=([A-Za-z]+)') { 'utility-' + $Matches[1].ToLowerInvariant() } elseif ($commandLine -match '--type=([a-z-]+)') { $Matches[1] } else { 'browser' }
}
$roleNames = [ordered]@{}
foreach ($id in @($roles.Keys | Sort-Object)) { $roleNames[[string]$id] = $roles[$id] }
$o = 'http://127.0.0.1:17632'
$session = (Invoke-RestMethod -Uri ($o+'/v1/setup/session') -Method Post -Headers @{Origin=$o;'X-Wallpaper-Setup'='1'} -ContentType 'application/json' -Body '{}').session
$cfg = Invoke-RestMethod -Uri ($o+'/v1/setup/config/read') -Method Post -Headers @{Origin=$o;'X-Wallpaper-Admin'=$session} -ContentType 'application/json' -Body '{}'
function HostInfo($client) { return [ordered]@{at=$client.at;buildId=$client.buildId;host=$client.host;width=$client.width;height=$client.height;fps=$client.fps;paused=$client.paused;materials=$client.materials} }
$hostBefore = HostInfo $cfg.diagnostics.lastClient
$started = (Get-Date).ToString('o')
$rows = [Collections.Generic.List[object]]::new()
$oldProcesses = @{}
$oldEngines = @{}
$firstCpu = @{}
$lastProcesses = @{}
$errorsSeen = [Collections.Generic.List[string]]::new()
function Format-Metrics($items, [int]$count) {
    [ordered]@{processCount=$count;cpuSystemPercent=[math]::Round(($items | ForEach-Object { $_.cpu } | Measure-Object -Sum).Sum,4);workingSetMiB=[math]::Round(($items | ForEach-Object { $_.working } | Measure-Object -Sum).Sum/1MB,2);privateCommitMiB=[math]::Round(($items | ForEach-Object { $_.private } | Measure-Object -Sum).Sum/1MB,2);privateWorkingSetMiB=[math]::Round(($items | ForEach-Object { $_.workingPrivate } | Measure-Object -Sum).Sum/1MB,2);gpu3dPercent=[math]::Round(($items | ForEach-Object { $_.gpu3d } | Measure-Object -Sum).Sum,4);gpuDedicatedMiB=[math]::Round(($items | ForEach-Object { $_.dedicated } | Measure-Object -Sum).Sum/1MB,2);gpuSharedMiB=[math]::Round(($items | ForEach-Object { $_.shared } | Measure-Object -Sum).Sum/1MB,2)}
}
for ($index = 0; $index -le $Samples; $index++) {
    if ($index) { Start-Sleep -Milliseconds ([int]($IntervalSeconds * 1000)) }
    $processes = @(Get-CimInstance Win32_PerfRawData_PerfProc_Process)
    $engines = @(Get-CimInstance Win32_PerfRawData_GPUPerformanceCounters_GPUEngine)
    $memory = @(Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUProcessMemory)
    $systemCpu = (Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'").PercentProcessorTime
    $currentProcesses = @{}
    foreach ($process in $processes) {
        if ($process.IDProcess -eq 0) { continue }
        $key = [int]$process.IDProcess
        $currentProcesses[$key] = $process
        if (-not $firstCpu.ContainsKey($key)) { $firstCpu[$key] = $process }
    }
    $currentEngines = @{}
    foreach ($engine in $engines) { $currentEngines[$engine.Name] = $engine }
    if ($index) {
        $row = [ordered]@{at=(Get-Date).ToString('o');systemCpuPercent=[double]$systemCpu}
        # Per-process metrics first; groups and Web roles are sums of the same values.
        $metrics = @{}
        foreach ($id in @($groups.Values | ForEach-Object { $_ })) {
            $process = $currentProcesses[$id]
            if (-not $process -or $metrics.ContainsKey($id)) { continue }
            $cpu = 0.0; $old = $oldProcesses[$id]
            if ($old) {
                $dt = [double]$process.Timestamp_Sys100NS - [double]$old.Timestamp_Sys100NS
                if ($dt -gt 0) { $cpu = 100.0 * ([double]$process.PercentProcessorTime - [double]$old.PercentProcessorTime) / $dt / $logical }
            }
            $metrics[$id] = @{cpu=$cpu;working=[double]$process.WorkingSet;private=[double]$process.PrivateBytes;workingPrivate=[double]$process.WorkingSetPrivate;gpu3d=0.0;dedicated=0.0;shared=0.0}
        }
        foreach ($m in $memory) {
            if ($m.Name -match '^pid_(\d+)_' -and $metrics.ContainsKey([int]$Matches[1])) { $x = $metrics[[int]$Matches[1]]; $x.dedicated += [double]$m.DedicatedUsage; $x.shared += [double]$m.SharedUsage }
        }
        foreach ($engine in $engines) {
            if ($engine.Name -notmatch '^pid_(\d+)_.*engtype_3D$' -or -not $metrics.ContainsKey([int]$Matches[1])) { continue }
            $id = [int]$Matches[1]; $old = $oldEngines[$engine.Name]
            if (-not $old) { continue }
            $dt = [double]$engine.Timestamp_Sys100NS - [double]$old.Timestamp_Sys100NS
            if ($dt -gt 0) { $metrics[$id].gpu3d += 100.0 * ([double]$engine.RunningTime - [double]$old.RunningTime) / $dt }
        }
        foreach ($group in $groups.GetEnumerator()) {
            $items = @($group.Value | Where-Object { $metrics.ContainsKey($_) } | ForEach-Object { $metrics[$_] })
            $row[$group.Key] = Format-Metrics $items $items.Count
        }
        $row.webRoles = [ordered]@{}
        foreach ($id in @($groups.portraitWeb | Sort-Object)) {
            if (-not $metrics.ContainsKey($id)) { continue }
            $role = $roles[$id]; if ($row.webRoles.Contains($role)) { $role = $role + '-' + $id }
            $row.webRoles[$role] = Format-Metrics @($metrics[$id]) 1
            $row.webRoles[$role].pid = $id
        }
        try {
            $snapshot = Invoke-RestMethod -Uri ($o+'/v1/snapshot') -Headers @{'X-Wallpaper-Token'=$cfg.readToken} -TimeoutSec 2
            $row.hardware = [ordered]@{cpuPercent=$snapshot.hardware.cpu.load.value;cpuTemperature=$snapshot.hardware.cpu.temperature.value;gpuPercent=$snapshot.hardware.gpu.load.value;gpuTemperature=$snapshot.hardware.gpu.temperature.value;vramUsedGiB=[math]::Round($snapshot.hardware.vram.usedBytes/1GB,3);ramUsedGiB=[math]::Round($snapshot.hardware.ram.usedBytes/1GB,3)}
        } catch { if (-not $errorsSeen.Count) { $errorsSeen.Add('Hardware snapshot unavailable for some samples') } }
        $rows.Add($row)
        $row | ConvertTo-Json -Depth 8 -Compress | Add-Content -LiteralPath ($OutputPath+'.jsonl') -Encoding UTF8
        if ($index % 10 -eq 0) { Write-Output "Resource samples: $index/$Samples" }
    }
    $oldProcesses = $currentProcesses; $oldEngines = $currentEngines; $lastProcesses = $currentProcesses
}
function Get-Statistics([double[]]$values) {
    $m = $values | Measure-Object -Minimum -Maximum -Average
    $sorted = [double[]]@($values | Sort-Object)
    $middle = [int][math]::Floor($sorted.Count / 2)
    $median = if ($sorted.Count % 2) { $sorted[$middle] } else { ($sorted[$middle - 1] + $sorted[$middle]) / 2 }
    $p95 = $sorted[[int][math]::Max(0.0, [math]::Ceiling($sorted.Count * .95) - 1)]
    [ordered]@{mean=[math]::Round($m.Average,3);min=[math]::Round($m.Minimum,3);max=[math]::Round($m.Maximum,3);p95=[math]::Round($p95,3);median=[math]::Round($median,3);first=$values[0];last=$values[-1]}
}
$metricNames = @('cpuSystemPercent','workingSetMiB','privateCommitMiB','privateWorkingSetMiB','gpu3dPercent','gpuDedicatedMiB','gpuSharedMiB')
$stats = [ordered]@{}
foreach ($group in $groups.Keys) {
    $stats[$group] = [ordered]@{}
    foreach ($metric in $metricNames) { $stats[$group][$metric] = Get-Statistics @($rows | ForEach-Object { [double]$_[$group][$metric] }) }
}
$roleStats = [ordered]@{}
foreach ($role in @($rows | ForEach-Object { $_.webRoles.Keys } | Sort-Object -Unique)) {
    $roleRows = @($rows | Where-Object { $_.webRoles.Contains($role) } | ForEach-Object { $_.webRoles[$role] })
    $roleStats[$role] = [ordered]@{pid=$roleRows[0].pid;samples=$roleRows.Count}
    foreach ($metric in $metricNames) { $roleStats[$role][$metric] = Get-Statistics @($roleRows | ForEach-Object { [double]$_[$metric] }) }
}
$top = @($lastProcesses.GetEnumerator() | ForEach-Object {
    $process = $_.Value; $old = $firstCpu[$_.Key]; $dt = [double]$process.Timestamp_Sys100NS-[double]$old.Timestamp_Sys100NS
    if ($dt -gt 0 -and $process.Name -notmatch '^_Total$') { [pscustomobject]@{pid=$_.Key;name=$process.Name;cpuSystemPercent=[math]::Round(100*([double]$process.PercentProcessorTime-[double]$old.PercentProcessorTime)/$dt/$logical,3);privateCommitMiB=[math]::Round($process.PrivateBytes/1MB,1)} }
} | Sort-Object cpuSystemPercent -Descending | Select-Object -First 10)
$session = (Invoke-RestMethod -Uri ($o+'/v1/setup/session') -Method Post -Headers @{Origin=$o;'X-Wallpaper-Setup'='1'} -ContentType 'application/json' -Body '{}').session
$clientNow = Invoke-RestMethod -Uri ($o+'/v1/setup/config/read') -Method Post -Headers @{Origin=$o;'X-Wallpaper-Admin'=$session} -ContentType 'application/json' -Body '{}'
$report = [ordered]@{started=$started;ended=(Get-Date).ToString('o');logicalProcessors=$logical;label=$Label;scope='Read-only current-use observation. All webwallpaper processes present at start are grouped; verify that only the target Web wallpaper is running; other-screen scene rendering and DWM recorded separately. No settings, wallpaper, input or workload changes. Not a controlled pre/post A/B.';hostBefore=$hostBefore;hostAfter=(HostInfo $clientNow.diagnostics.lastClient);processGroups=$groups;webRoles=$roleNames;statistics=$stats;webRoleStatistics=$roleStats;topCpuProcesses=$top;errors=$errorsSeen.ToArray();samples=$rows.ToArray()}
$out = $OutputPath
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $out -Encoding utf8
Write-Output ('Saved current resource observation: '+$out)
$stats | ConvertTo-Json -Depth 5
