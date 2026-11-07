param(
    [Parameter(Mandatory)][int]$RootProcessId,
    [ValidateRange(1,300)][double]$IntervalSeconds = 5,
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$StopFile,
    [ValidateRange(0,604800)][double]$DurationSeconds = 0
)
# Samples a process and all of its descendants (for example an isolated Chromium used for A/B runs)
# as JSON lines. Read-only: it never signals or changes the observed processes. Stops when the root
# exits, when -StopFile appears, or after -DurationSeconds. CPU is normalized to all logical CPUs.
$ErrorActionPreference = 'Stop'
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath) -Force | Out-Null
$logical = [int](Get-CimInstance Win32_ComputerSystem).NumberOfLogicalProcessors
$roles = @{}
function Get-Role([int]$id) {
    if ($roles.ContainsKey($id)) { return $roles[$id] }
    $commandLine = [string](Get-CimInstance Win32_Process -Filter "ProcessId=$id" -ErrorAction SilentlyContinue).CommandLine
    $role = if ($id -eq $RootProcessId) { 'browser' } elseif ($commandLine -match '--type=gpu-process') { 'gpu' } elseif ($commandLine -match '--type=renderer') { 'renderer' } elseif ($commandLine -match '--utility-sub-type=([A-Za-z]+)') { 'utility-' + $Matches[1].ToLowerInvariant() } elseif ($commandLine -match '--type=([a-z-]+)') { $Matches[1] } else { 'other' }
    $roles[$id] = $role
    return $role
}
$started = [DateTimeOffset]::UtcNow
$oldProcesses = @{}; $oldEngines = @{}
while ($true) {
    $processes = @(Get-CimInstance Win32_PerfRawData_PerfProc_Process)
    $engines = @(Get-CimInstance Win32_PerfRawData_GPUPerformanceCounters_GPUEngine)
    $memory = @(Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUProcessMemory)
    $at = [DateTimeOffset]::UtcNow
    $byId = @{}; $children = @{}
    foreach ($process in $processes) {
        $id = [int]$process.IDProcess
        if ($id -eq 0) { continue }
        $byId[$id] = $process
        $parent = [int]$process.CreatingProcessID
        if (-not $children.ContainsKey($parent)) { $children[$parent] = [Collections.Generic.List[int]]::new() }
        $children[$parent].Add($id)
    }
    if (-not $byId.ContainsKey($RootProcessId)) { break }
    $tree = [Collections.Generic.List[int]]::new(); $queue = [Collections.Generic.Queue[int]]::new(); $queue.Enqueue($RootProcessId)
    while ($queue.Count) {
        $id = $queue.Dequeue(); if ($tree.Contains($id)) { continue }
        $tree.Add($id)
        if ($children.ContainsKey($id)) { foreach ($child in $children[$id]) { if ($child -ne $id) { $queue.Enqueue($child) } } }
    }
    $metrics = @{}
    foreach ($id in $tree) {
        $process = $byId[$id]; $cpu = $null; $old = $oldProcesses[$id]
        if ($old) {
            $dt = [double]$process.Timestamp_Sys100NS - [double]$old.Timestamp_Sys100NS
            if ($dt -gt 0) { $cpu = 100.0 * ([double]$process.PercentProcessorTime - [double]$old.PercentProcessorTime) / $dt / $logical }
        }
        $metrics[$id] = [ordered]@{pid=$id;role=(Get-Role $id);cpuSystemPercent=$cpu;workingSetMiB=[math]::Round([double]$process.WorkingSet/1MB,2);privateCommitMiB=[math]::Round([double]$process.PrivateBytes/1MB,2);privateWorkingSetMiB=[math]::Round([double]$process.WorkingSetPrivate/1MB,2);gpu3dPercent=0.0;gpuDedicatedMiB=0.0;gpuSharedMiB=0.0}
    }
    foreach ($m in $memory) {
        if ($m.Name -match '^pid_(\d+)_' -and $metrics.ContainsKey([int]$Matches[1])) { $x = $metrics[[int]$Matches[1]]; $x.gpuDedicatedMiB += [math]::Round([double]$m.DedicatedUsage/1MB,2); $x.gpuSharedMiB += [math]::Round([double]$m.SharedUsage/1MB,2) }
    }
    $currentEngines = @{}
    foreach ($engine in $engines) {
        $currentEngines[$engine.Name] = $engine
        if ($engine.Name -notmatch '^pid_(\d+)_.*engtype_3D$' -or -not $metrics.ContainsKey([int]$Matches[1])) { continue }
        $old = $oldEngines[$engine.Name]
        if (-not $old) { $metrics[[int]$Matches[1]].gpu3dPercent = $null; continue }
        $dt = [double]$engine.Timestamp_Sys100NS - [double]$old.Timestamp_Sys100NS
        if ($dt -gt 0 -and $null -ne $metrics[[int]$Matches[1]].gpu3dPercent) { $metrics[[int]$Matches[1]].gpu3dPercent += 100.0 * ([double]$engine.RunningTime - [double]$old.RunningTime) / $dt }
    }
    $row = [ordered]@{at=$at.ToString('o');processes=@($tree | ForEach-Object { $x = $metrics[$_]; if ($null -ne $x.cpuSystemPercent) { $x.cpuSystemPercent = [math]::Round($x.cpuSystemPercent,4) }; if ($null -ne $x.gpu3dPercent) { $x.gpu3dPercent = [math]::Round($x.gpu3dPercent,4) }; $x })}
    $row | ConvertTo-Json -Depth 6 -Compress | Add-Content -LiteralPath $OutputPath -Encoding UTF8
    $oldProcesses = $byId; $oldEngines = $currentEngines
    if ($StopFile -and (Test-Path -LiteralPath $StopFile)) { break }
    if ($DurationSeconds -gt 0 -and ([DateTimeOffset]::UtcNow - $started).TotalSeconds -ge $DurationSeconds) { break }
    Start-Sleep -Milliseconds ([int]($IntervalSeconds * 1000))
}
