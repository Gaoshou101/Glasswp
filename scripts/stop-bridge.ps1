param([string]$RuntimeRoot)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\') + '\'
$runtimeRoot = (Get-BridgeRuntimeRoot $RuntimeRoot) + '\'
# The protected runtime launcher, plus the repository launcher used before the runtime migration.
$launchers = @((Get-BridgeLauncherPath $RuntimeRoot), (Join-Path $PSScriptRoot 'install-bridge-autostart.ps1'))
$startupTask = Get-ScheduledTask -TaskName 'WallpaperDashboardBridge' -TaskPath '\' -ErrorAction SilentlyContinue
if ($startupTask) {
    $taskArguments = $startupTask.Actions.Arguments -join ' '
    if (@($launchers | Where-Object { $taskArguments.IndexOf(('"' + $_ + '"'), [StringComparison]::OrdinalIgnoreCase) -ge 0 }).Count) {
        # An intentional stop must not be treated as a crash and restarted after a minute.
        Stop-ScheduledTask -TaskName 'WallpaperDashboardBridge' -TaskPath '\'
    }
}
foreach ($bridgeProcess in @(Get-Process WallpaperBridge -ErrorAction SilentlyContinue)) {
    $bridgePath = $bridgeProcess.Path
    if (-not $bridgePath) { throw '无法确认桥接进程路径。若桥接以管理员运行，请用管理员终端停止。' }
    $resolved = [IO.Path]::GetFullPath($bridgePath)
    if ($resolved.StartsWith($runtimeRoot, [StringComparison]::OrdinalIgnoreCase) -or $resolved.StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Stop-Process -Id $bridgeProcess.Id; $bridgeProcess.WaitForExit(5000) | Out-Null
    }
}
Write-Output '项目桥接已停止'