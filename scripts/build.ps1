param([switch]$SkipBridge, [string]$ReleaseVersion, [switch]$HeadlessTests)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'release-common.ps1')
Push-Location $repoRoot
try {
    $packageVersion = (Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $repoRoot 'package.json') | ConvertFrom-Json).version
    [xml]$bridgeProject = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $repoRoot 'bridge\WallpaperBridge\WallpaperBridge.csproj')
    if ($bridgeProject.Project.PropertyGroup.Version -ne $packageVersion) { throw 'Frontend and bridge versions must match before building' }
    if ($ReleaseVersion) {
        if ($ReleaseVersion -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw 'ReleaseVersion must be a stable semantic version such as 1.0.0' }
        if ($ReleaseVersion -ne $packageVersion) { throw ('ReleaseVersion does not match the source version '+$packageVersion) }
        if ($SkipBridge) { throw 'A release build cannot skip the bridge' }
    }
    & npm.cmd ci
    if ($LASTEXITCODE -ne 0) { throw 'npm ci failed' }
    & npm.cmd run check
    if ($LASTEXITCODE -ne 0) { throw 'Frontend checks failed' }
    if (-not $SkipBridge) {
        $dotnetPath = Get-WallpaperDotnet
        $testArguments = @('run','--project','bridge\WallpaperBridge.Tests\WallpaperBridge.Tests.csproj')
        if ($HeadlessTests) { $testArguments += @('--','--headless') }
        & $dotnetPath @testArguments
        if ($LASTEXITCODE -ne 0) { throw 'Bridge tests failed' }
        $buildInfo = Get-Content -Raw -LiteralPath artifacts\current-build.json | ConvertFrom-Json
        $candidate = Join-Path $repoRoot ('artifacts\candidates\'+$buildInfo.buildId)
        if (Test-Path -LiteralPath $candidate) { throw 'Candidate output already exists' }
        if ($buildInfo.version -ne $packageVersion) { throw 'Build manifest version does not match the source version' }
        & $dotnetPath publish bridge\WallpaperBridge\WallpaperBridge.csproj -c Release -r win-x64 --self-contained true -p:WallpaperBuildId=$($buildInfo.buildId) -p:Version=$packageVersion -o $candidate
        if ($LASTEXITCODE -ne 0) { throw 'Bridge publish failed' }
        $wallpaperOutput = Join-Path $candidate 'wallpaper'
        New-Item -ItemType Directory -Path $wallpaperOutput -Force | Out-Null
        Get-ChildItem -LiteralPath (Join-Path $repoRoot 'dist') | Copy-Item -Destination $wallpaperOutput -Recurse -Force
        # The scheduled task runs this launcher from the protected runtime; ship it inside the hashed release.
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'bridge-launcher.ps1') -Destination (Join-Path $candidate 'run-bridge.ps1')
        & node (Join-Path $PSScriptRoot 'build-info.mjs') --release $candidate
        if ($LASTEXITCODE -ne 0) { throw 'Release manifest failed' }
        Invoke-ReleaseTransaction -Source $candidate -Destination (Join-Path $repoRoot 'artifacts\staging') -BackupRoot (Join-Path $repoRoot 'artifacts\backups\staging-releases') | Out-Null
        & (Join-Path $PSScriptRoot 'package-bridge.ps1') -ReleaseVersion $ReleaseVersion
        Write-Output '候选构建和安装包已生成；双击 install-bridge.cmd 或运行 scripts/install-bridge.ps1 安装。'
    }
} finally { Pop-Location }
