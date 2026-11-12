param([string]$Source, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Source) { $Source = Join-Path $repoRoot 'artifacts\staging' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'artifacts\packages' }
$sourcePath = Get-ReleasePath $Source
$outputPath = Get-ReleasePath $OutputDirectory
Assert-NoReleaseAncestors $outputPath
if ($outputPath -eq $sourcePath -or $outputPath.StartsWith($sourcePath+'\', [StringComparison]::OrdinalIgnoreCase) -or $sourcePath.StartsWith($outputPath+'\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Package output and source must not overlap' }
$info = Test-ReleasePackage $sourcePath @('WallpaperBridge.exe','WallpaperBridge.dll','run-bridge.ps1','wallpaper/index.html','wallpaper/wallpaper.js','wallpaper/project.json')
# Fail before creating output if a checkout is missing any installer dependency.
$scripts = @('install-bridge.ps1','update-bridge.ps1','release-common.ps1','install-bridge-autostart.ps1','start-bridge.ps1','stop-bridge.ps1')
foreach ($name in $scripts) {
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $name) -PathType Leaf)) { throw ('Missing installer script: '+$name) }
}
$project = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $sourcePath 'wallpaper\project.json') | ConvertFrom-Json
if ($project.general.properties.bridge_token.value) { throw 'Release wallpaper must not contain a pairing token' }
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$name = 'Glasswp-Bridge-'+$info.buildId+'-win-x64'
$zipPath = Join-Path $outputPath ($name+'.zip')
if ((Test-Path -LiteralPath $zipPath) -or (Test-Path -LiteralPath ($zipPath+'.sha256'))) { throw ('Package already exists: '+$zipPath) }
$workspace = Join-Path $outputPath ('.package-'+[Guid]::NewGuid().ToString('N'))
$temporaryZip = Join-Path $outputPath ('.package-'+[Guid]::NewGuid().ToString('N')+'.zip')
try {
    $bundle = Join-Path $workspace $name
    $payload = Join-Path $bundle 'payload'
    $scriptOutput = Join-Path $bundle 'scripts'
    New-Item -ItemType Directory -Path $payload,$scriptOutput -Force | Out-Null
    Get-ChildItem -LiteralPath $sourcePath -Force | Copy-Item -Destination $payload -Recurse -Force
    Test-ReleasePackage $payload | Out-Null
    foreach ($script in $scripts) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $script) -Destination $scriptOutput }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'install-bridge.cmd') -Destination $bundle
    Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\THIRD-PARTY-NOTICES.md') -Destination $bundle
    @'
# Glasswp Bridge for Windows x64

Extract the complete archive, then double-click install-bridge.cmd.
Installation requests administrator access once. It installs and starts the bridge,
enables login autostart, and opens http://127.0.0.1:17632/admin/.
Node.js and the .NET SDK/runtime are not required for this self-contained package.

Default runtime: E:\WallpaperDashboard. To select another drive, run:
  .\install-bridge.cmd -RuntimeRoot D:\WallpaperDashboard
The runtime must be directly beneath a drive root on a local NTFS volume.

Check without installation:
  .\install-bridge.cmd -Check
Skip creating/changing login autostart:
  .\install-bridge.cmd -NoAutostart
Reuse an existing private configuration directory:
  .\install-bridge.cmd -DataDirectory "D:\ExistingPrivateDirectory"

Keep the existing Windows user when upgrading: credentials use CurrentUser DPAPI.
The installer preserves configuration and verifies the candidate before switching.
See the console and <RuntimeRoot>\state for installation diagnostics.

Wallpaper Engine and a wallpaper are installed separately. payload\wallpaper
contains the generic web wallpaper and a blank pairing token. In the bridge's
settings page, copy the display token to the wallpaper's Wallpaper Engine property.
Do not share your private configuration or a wallpaper containing your display token.
'@ | Set-Content -LiteralPath (Join-Path $bundle 'INSTALL.txt') -Encoding UTF8
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($workspace, $temporaryZip, [IO.Compression.CompressionLevel]::Optimal, $false)
    Move-Item -LiteralPath $temporaryZip -Destination $zipPath
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    ($hash+'  '+[IO.Path]::GetFileName($zipPath)) | Set-Content -LiteralPath ($zipPath+'.sha256') -Encoding ASCII
    Write-Output ('Installer package: '+$zipPath)
} finally {
    # Only remove the exact temporary paths created by this invocation.
    foreach ($temporary in @($workspace,$temporaryZip)) {
        $resolved = [IO.Path]::GetFullPath($temporary)
        if (-not $resolved.StartsWith($outputPath+'\', [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike '.package-*') { throw 'Unsafe package cleanup path' }
        if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    }
}
