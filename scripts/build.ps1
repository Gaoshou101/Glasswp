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
        $buildInfo = Get-Content -Raw -LiteralPath artifacts\current-build.json | ConvertFrom-Json
        $candidate = Join-Path $repoRoot ('artifacts\candidates\'+$buildInfo.buildId)
        if (Test-Path -LiteralPath $candidate) { throw 'Candidate output already exists' }
        & $dotnetPath publish bridge\WallpaperBridge\WallpaperBridge.csproj -c Release -r win-x64 --self-contained true -p:WallpaperBuildId=$($buildInfo.buildId) -o $candidate
        if ($LASTEXITCODE -ne 0) { throw 'Bridge publish failed' }
        $wallpaperOutput = Join-Path $candidate 'wallpaper'
        New-Item -ItemType Directory -Path $wallpaperOutput -Force | Out-Null
        Get-ChildItem -LiteralPath (Join-Path $repoRoot 'dist') | Copy-Item -Destination $wallpaperOutput -Recurse -Force
        & node (Join-Path $PSScriptRoot 'build-info.mjs') --release $candidate
        if ($LASTEXITCODE -ne 0) { throw 'Release manifest failed' }
        . (Join-Path $PSScriptRoot 'release-common.ps1')
        Invoke-ReleaseTransaction -Source $candidate -Destination (Join-Path $repoRoot 'artifacts\staging') -BackupRoot (Join-Path $repoRoot 'artifacts\backups\staging-releases') | Out-Null
        Write-Output '候选构建完成；运行 scripts/update-bridge.ps1 安装。'
    }
} finally { Pop-Location }
