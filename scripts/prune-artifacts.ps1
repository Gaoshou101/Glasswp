param([switch]$Apply, [int]$KeepCandidates = 2, [int]$KeepStagingBackups = 1, [int]$BackupRetentionDays = 14, [string]$RuntimeRoot)
# Retention for artifacts/. Dry run by default: lists what would be removed. Never touches staging,
# validation, analysis, baselines or the protected runtime; the running release lives in E:\WallpaperDashboard.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifacts = Get-ReleasePath (Join-Path $repoRoot 'artifacts')
$runtimeRoot = Get-BridgeRuntimeRoot $RuntimeRoot
$installedBuild = $null
$installedInfo = Join-Path $runtimeRoot 'current\build-info.json'
if (Test-Path -LiteralPath $installedInfo) { $installedBuild = (Get-Content -Raw -LiteralPath $installedInfo | ConvertFrom-Json).buildId }
$remove = [Collections.Generic.List[object]]::new()
function Add-Removal([IO.DirectoryInfo]$Directory, [string]$Reason) { $remove.Add([pscustomobject]@{ Path = $Directory.FullName; Reason = $Reason }) }

# Candidates: newest builds (IDs start with a UTC timestamp) and whatever is installed.
$candidates = @(Get-ChildItem -LiteralPath (Join-Path $artifacts 'candidates') -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending)
foreach ($candidate in ($candidates | Select-Object -Skip $KeepCandidates)) {
    if ($installedBuild -and $candidate.Name -eq $installedBuild) { continue }
    Add-Removal $candidate 'older candidate build'
}
# Staging rollback copies created by build.ps1.
foreach ($copy in @(Get-ChildItem -LiteralPath (Join-Path $artifacts 'backups\staging-releases') -Directory -ErrorAction SilentlyContinue | Sort-Object CreationTimeUtc -Descending | Select-Object -Skip $KeepStagingBackups)) {
    Add-Removal $copy 'older staging rollback copy'
}
# Rollback copies of the repository runtime become obsolete only once the task starts the protected
# launcher and the single running bridge is the process that launcher recorded.
$task = Get-ScheduledTask -TaskName 'WallpaperDashboardBridge' -TaskPath '\' -ErrorAction SilentlyContinue
$taskUsesRuntime = $task -and ($task.Actions.Arguments -join ' ').IndexOf(('"' + (Get-BridgeLauncherPath $runtimeRoot) + '"'), [StringComparison]::OrdinalIgnoreCase) -ge 0
$running = @(Get-Process WallpaperBridge -ErrorAction SilentlyContinue)
$launcherState = Join-Path $runtimeRoot 'state\autostart-runtime.json'
$recordedId = $null
if (Test-Path -LiteralPath $launcherState) { $state = Get-Content -Raw -LiteralPath $launcherState | ConvertFrom-Json; if ($state.PSObject.Properties['processId']) { $recordedId = $state.processId } }
$runtimeOwnsBridge = $running.Count -eq 0 -or ($running.Count -eq 1 -and $recordedId -eq $running[0].Id)
$repoRuntimeInUse = -not ($taskUsesRuntime -and $runtimeOwnsBridge)
if (-not $repoRuntimeInUse) {
    foreach ($name in @('bridge-releases')) { $path = Join-Path $artifacts ('backups\' + $name); if (Test-Path -LiteralPath $path) { Add-Removal (Get-Item -LiteralPath $path) 'rollback copies of the retired repository runtime' } }
    foreach ($legacy in @(Get-ChildItem -LiteralPath (Join-Path $artifacts 'backups') -Directory -Filter 'bridge-migration-*' -ErrorAction SilentlyContinue)) { Add-Removal $legacy 'rollback copy from the 2026-11-04 repository migration' }
    foreach ($legacy in @(Get-ChildItem -LiteralPath $artifacts -Directory -Filter 'bridge-before-*')) { Add-Removal $legacy 'pre-change copy of the retired repository runtime' }
    $legacyRuntime = Join-Path $artifacts 'bridge'
    if (Test-Path -LiteralPath $legacyRuntime) { Add-Removal (Get-Item -LiteralPath $legacyRuntime) 'retired repository runtime (reproducible from its commit)' }
}
# Dated one-off rollback folders (name ends in -yyyyMMdd-HHmmss) older than the retention window.
$cutoff = [DateTime]::Now.AddDays(-$BackupRetentionDays)
foreach ($dated in @(Get-ChildItem -LiteralPath (Join-Path $artifacts 'backups') -Directory -ErrorAction SilentlyContinue)) {
    if ($dated.Name -match '-(\d{8})-(\d{6})$') {
        $at = [DateTime]::ParseExact($Matches[1] + $Matches[2], 'yyyyMMddHHmmss', [Globalization.CultureInfo]::InvariantCulture)
        if ($at -lt $cutoff) { Add-Removal $dated ('dated rollback copy older than ' + $BackupRetentionDays + ' days') }
    }
}

$total = 0
$report = foreach ($item in $remove) {
    $resolved = [IO.Path]::GetFullPath($item.Path)
    if (-not $resolved.StartsWith($artifacts + '\', [StringComparison]::OrdinalIgnoreCase)) { throw ('Refusing to touch ' + $resolved) }
    $bytes = (Get-ChildItem -LiteralPath $resolved -Recurse -File -Force | Measure-Object Length -Sum).Sum
    if (-not $bytes) { $bytes = 0 }
    $total += $bytes
    [pscustomobject]@{ MiB = [math]::Round($bytes / 1MB, 1); Path = $resolved.Substring($artifacts.Length + 1); Reason = $item.Reason }
}
$report | Format-Table -AutoSize | Out-String -Width 220
Write-Output ('{0} folders, {1:N1} MiB{2}' -f $remove.Count, ($total / 1MB), $(if ($Apply) { '' } else { ' would be removed (dry run; add -Apply to delete)' }))
if ($Apply) {
    foreach ($item in $remove) {
        Assert-NoReleaseLinks $item.Path
        Remove-Item -LiteralPath $item.Path -Recurse -Force
    }
    Write-Output 'Removed.'
}