param([switch]$SkipBridge)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    & npm.cmd ci
    if ($LASTEXITCODE -ne 0) { throw 'npm ci failed' }
    & npm.cmd run check
    if ($LASTEXITCODE -ne 0) { throw 'Frontend checks failed' }
    if (-not $SkipBridge) {
        $dotnetPath = Join-Path $env:LOCALAPPDATA 'WallpaperDashboard\devtools\dotnet\dotnet.exe'
        if (-not (Test-Path -LiteralPath $dotnetPath)) { $dotnetPath = (Get-Command dotnet).Source }
        & $dotnetPath run --project bridge\WallpaperBridge.Tests\WallpaperBridge.Tests.csproj
        if ($LASTEXITCODE -ne 0) { throw 'Bridge tests failed' }
        & $dotnetPath publish bridge\WallpaperBridge\WallpaperBridge.csproj -c Release -r win-x64 --self-contained true -o artifacts\staging
        if ($LASTEXITCODE -ne 0) { throw 'Bridge publish failed' }
        & (Join-Path $PSScriptRoot 'update-bridge.ps1')
    }
} finally { Pop-Location }
