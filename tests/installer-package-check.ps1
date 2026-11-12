$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $repoRoot 'scripts\release-common.ps1')
$testRoot = Join-Path $repoRoot ('artifacts\validation\installer-package-'+[Guid]::NewGuid().ToString('N'))
$source = Join-Path $testRoot 'source with spaces'
$output = Join-Path $testRoot '安装包 output'
function Assert-Check($Condition, [string]$Message) { if (-not $Condition) { throw $Message }; Write-Output ('PASS '+$Message) }
function Write-FixtureManifest {
    $files = @{}
    foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File) {
        if ($file.Name -eq 'build-info.json') { continue }
        $files[$file.FullName.Substring($source.Length+1).Replace('\','/')] = (Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant()
    }
    @{buildId='installer-package-test';files=$files} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $source 'build-info.json') -Encoding UTF8
}
try {
    New-Item -ItemType Directory -Path (Join-Path $source 'wallpaper') -Force | Out-Null
    foreach ($name in @('WallpaperBridge.exe','WallpaperBridge.dll','run-bridge.ps1','wallpaper/index.html','wallpaper/wallpaper.js')) {
        Set-Content -LiteralPath (Join-Path $source $name) -Value 'package fixture only' -Encoding UTF8
    }
    [IO.File]::WriteAllText((Join-Path $source 'wallpaper\project.json'), '{"title":"竖屏动态信息壁纸","general":{"properties":{"bridge_token":{"value":""}}}}', [Text.UTF8Encoding]::new($false))
    Write-FixtureManifest
    & (Join-Path $repoRoot 'scripts\package-bridge.ps1') -Source $source -OutputDirectory $output
    $zip = Join-Path $output 'Glasswp-Bridge-installer-package-test-win-x64.zip'
    $expectedHash = ((Get-Content -LiteralPath ($zip+'.sha256')).Split(' '))[0]
    Assert-Check ((Get-FileHash -LiteralPath $zip).Hash.ToLowerInvariant() -eq $expectedHash) 'archive SHA256 matches sidecar'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $extracted = Join-Path $testRoot '解压 with spaces'
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $extracted)
    $bundle = Join-Path $extracted 'Glasswp-Bridge-installer-package-test-win-x64'
    $actualNames = @(Get-ChildItem -LiteralPath $bundle -File -Recurse | ForEach-Object { $_.FullName.Substring($bundle.Length+1).Replace('\','/') })
    $expected = @('install-bridge.cmd','INSTALL.txt','THIRD-PARTY-NOTICES.md','scripts/install-bridge.ps1','scripts/update-bridge.ps1','scripts/release-common.ps1','scripts/install-bridge-autostart.ps1','scripts/start-bridge.ps1','scripts/stop-bridge.ps1','payload/build-info.json','payload/WallpaperBridge.exe','payload/WallpaperBridge.dll','payload/run-bridge.ps1','payload/wallpaper/index.html','payload/wallpaper/wallpaper.js','payload/wallpaper/project.json')
    Assert-Check (-not (Compare-Object $expected $actualNames)) 'bundle contains only payload and allowlisted installer files'
    Test-ReleasePackage (Join-Path $bundle 'payload') | Out-Null
    Assert-Check $true 'extracted payload hashes remain valid'
    # Exercise the real CMD bootstrap without running installation, including a
    # polluted parent module path (PowerShell 7 -> cmd -> Windows PowerShell 5.1).
    @'
param([string]$Source, [switch]$Check)
$ErrorActionPreference = 'Stop'
if (-not $Check -or -not (Test-Path -LiteralPath (Join-Path $Source 'build-info.json'))) { throw 'CMD did not forward payload and Check' }
if ($env:PSModulePath -like '*incompatible-parent-modules*') { throw 'Inherited module path was not isolated' }
Get-FileHash -LiteralPath (Join-Path $Source 'build-info.json') | Out-Null
'@ | Set-Content -LiteralPath (Join-Path $bundle 'scripts\install-bridge.ps1') -Encoding UTF8
    $savedModules = $env:PSModulePath
    try {
        $env:PSModulePath = Join-Path $testRoot 'incompatible-parent-modules'
        & (Join-Path $bundle 'install-bridge.cmd') -Check
        Assert-Check ($LASTEXITCODE -eq 0) 'CMD forwards spaced Unicode payload paths and isolates Windows PowerShell modules'
    } finally { $env:PSModulePath = $savedModules }
    $rejected = $false
    try { & (Join-Path $repoRoot 'scripts\package-bridge.ps1') -Source $source -OutputDirectory $output } catch { $rejected = $true }
    Assert-Check $rejected 'existing installer archive is not overwritten'
    '{"general":{"properties":{"bridge_token":{"value":"fixture-secret"}}}}' | Set-Content -LiteralPath (Join-Path $source 'wallpaper\project.json') -Encoding UTF8
    Write-FixtureManifest
    $privateOutput = Join-Path $testRoot 'private-output'
    $rejected = $false
    try { & (Join-Path $repoRoot 'scripts\package-bridge.ps1') -Source $source -OutputDirectory $privateOutput } catch { $rejected = $true }
    Assert-Check ($rejected -and -not (Test-Path -LiteralPath $privateOutput)) 'pairing token blocks packaging before output is created'
    Add-Content -LiteralPath (Join-Path $source 'WallpaperBridge.dll') -Value 'tampered'
    $rejected = $false
    try { & (Join-Path $repoRoot 'scripts\package-bridge.ps1') -Source $source -OutputDirectory $privateOutput } catch { $rejected = $true }
    Assert-Check ($rejected -and -not (Test-Path -LiteralPath $privateOutput)) 'tampered payload blocks packaging'
} finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $allowed = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\validation'))+'\'
    if (-not $resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
