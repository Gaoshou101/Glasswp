param([switch]$Browser, [string]$Python, [switch]$ReleaseRuntime)
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'release-common.ps1')
Push-Location $repoRoot
try {
    & npm.cmd run check
    if($LASTEXITCODE -ne 0){throw 'Frontend verification failed'}
    $dotnetPath=Get-WallpaperDotnet
    & $dotnetPath run --project bridge\WallpaperBridge.Tests\WallpaperBridge.Tests.csproj
    if($LASTEXITCODE -ne 0){throw 'Bridge verification failed'}
    & (Join-Path $repoRoot 'tests\release-check.ps1')
    & (Join-Path $repoRoot 'tests\installer-check.ps1')
    & (Join-Path $repoRoot 'tests\installer-package-check.ps1')
    if($ReleaseRuntime){& (Join-Path $repoRoot 'tests\bridge-release-check.ps1')}
    if($Browser){
        if(-not $Python){$Python=(Get-Command python -ErrorAction Stop).Source}
        foreach($test in @('snapshot_lifecycle_check.py','desktop_input_check.py','quota_ui_check.py','quota_exhausted_check.py','liquid_glass_check.py','visual_polish_check.py','admin_settings_check.py','world_clock_check.py','top_details_check.py','clock_weather_admin_check.py')){
            & $Python (Join-Path $repoRoot ('tests\'+$test))
            if($LASTEXITCODE -ne 0){throw ('Browser verification failed: '+$test)}
        }
        & $Python (Join-Path $repoRoot 'tests/ai_sources_admin_check.py')
        if($LASTEXITCODE -ne 0){throw 'AI source settings verification failed'}
        & $Python (Join-Path $repoRoot 'tests/ai_quota_wallet_check.py')
        if($LASTEXITCODE -ne 0){throw 'AI wallet display verification failed'}
        & $Python (Join-Path $repoRoot 'tests/ai_sources_api_check.py') $dotnetPath
        if($LASTEXITCODE -ne 0){throw 'AI source API verification failed'}
        & $Python (Join-Path $repoRoot 'tests/display_settings_api_check.py') $dotnetPath
        if($LASTEXITCODE -ne 0){throw 'Display settings API verification failed'}
    }
    Write-Output 'Verification complete; no installation performed.'
}finally{Pop-Location}
