param([switch]$Start)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$staging = Join-Path $repoRoot 'artifacts\staging'
$destination = Join-Path $repoRoot 'artifacts\bridge'
if (-not (Test-Path -LiteralPath (Join-Path $staging 'WallpaperBridge.exe'))) { throw '没有可安装的 staging 发布程序' }
& (Join-Path $PSScriptRoot 'stop-bridge.ps1')
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Get-ChildItem -LiteralPath $staging | Copy-Item -Destination $destination -Recurse -Force
New-Item -ItemType Directory -Path (Join-Path $destination 'wallpaper') -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $repoRoot 'dist') | Copy-Item -Destination (Join-Path $destination 'wallpaper') -Recurse -Force
if ($Start) { & (Join-Path $PSScriptRoot 'start-bridge.ps1') }
