param([switch]$Browser, [string]$Python, [switch]$ReleaseRuntime)
$ErrorActionPreference='Stop'
$repoRoot=Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    & npm.cmd run check
    if($LASTEXITCODE -ne 0){throw 'Frontend verification failed'}
    $dotnetPath=Join-Path $env:LOCALAPPDATA 'WallpaperDashboard\devtools\dotnet\dotnet.exe'
    if(-not(Test-Path -LiteralPath $dotnetPath)){$dotnetPath=(Get-Command dotnet).Source}
    & $dotnetPath run --project bridge\WallpaperBridge.Tests\WallpaperBridge.Tests.csproj
    if($LASTEXITCODE -ne 0){throw 'Bridge verification failed'}
    & (Join-Path $repoRoot 'tests\release-check.ps1')
    if($ReleaseRuntime){& (Join-Path $repoRoot 'tests\bridge-release-check.ps1')}
    if($Browser){
        if(-not $Python){$Python=(Get-Command python -ErrorAction Stop).Source}
        foreach($test in @('snapshot_lifecycle_check.py','desktop_input_check.py','quota_ui_check.py','liquid_glass_check.py')){
            & $Python (Join-Path $repoRoot ('tests\'+$test))
            if($LASTEXITCODE -ne 0){throw ('Browser verification failed: '+$test)}
        }
    }
    Write-Output 'Verification complete; no installation performed.'
}finally{Pop-Location}
