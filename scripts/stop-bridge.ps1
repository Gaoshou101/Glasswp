$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\') + '\'
$startupScript = Join-Path $PSScriptRoot 'install-bridge-autostart.ps1'
$startupTask = Get-ScheduledTask -TaskName 'WallpaperDashboardBridge' -TaskPath '\' -ErrorAction SilentlyContinue
if ($startupTask -and ($startupTask.Actions.Arguments -join ' ').IndexOf(('"' + $startupScript + '"'), [StringComparison]::OrdinalIgnoreCase) -ge 0) {
    # An intentional stop must not be treated as a crash and restarted after a minute.
    Stop-ScheduledTask -TaskName 'WallpaperDashboardBridge' -TaskPath '\'
}
$bridgeProcesses = Get-Process WallpaperBridge -ErrorAction SilentlyContinue
foreach ($bridgeProcess in $bridgeProcesses) {
    $bridgePath = $bridgeProcess.Path
    if (-not $bridgePath) { throw '无法确认桥接进程路径。若桥接以管理员运行，请用管理员终端停止。' }
    $resolved = [IO.Path]::GetFullPath($bridgePath)
    if ($resolved.StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase)) { Stop-Process -Id $bridgeProcess.Id; $bridgeProcess.WaitForExit(5000) | Out-Null }
}
Write-Output '项目桥接已停止'
