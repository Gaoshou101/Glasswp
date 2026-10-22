param(
    [string]$WallpaperEnginePath = 'F:\SteamLibrary\steamapps\common\wallpaper_engine',
    [switch]$Apply,
    [int]$Monitor = -1,
    [switch]$PreviewWindow
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repoRoot 'dist'
if (-not (Test-Path -LiteralPath (Join-Path $source 'project.json'))) { throw '请先运行 npm run build' }
$weExe = Join-Path $WallpaperEnginePath 'wallpaper32.exe'
if (-not (Test-Path -LiteralPath $weExe)) { throw 'Wallpaper Engine 路径无效' }
$destination = Join-Path $WallpaperEnginePath 'projects\myprojects\portrait-dashboard'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
if (Test-Path -LiteralPath (Join-Path $destination 'project.json')) {
    $backup = Join-Path $env:LOCALAPPDATA ('WallpaperDashboard\backups\project-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
    New-Item -ItemType Directory -Path (Split-Path -Parent $backup) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $destination 'project.json') -Destination $backup
}
Get-ChildItem -LiteralPath $source | Copy-Item -Destination $destination -Recurse -Force
$tokenPath = Join-Path $env:LOCALAPPDATA 'WallpaperDashboard\private\read-token.txt'
$displayToken = $null
try {
    # A packaged Codex process and an external terminal can see different AppData overlays.
    # Pair to the running bridge rather than selecting an old file from either overlay.
    $bridgeOrigin = 'http://127.0.0.1:17632'
    $setupSession = (Invoke-RestMethod -Uri ($bridgeOrigin + '/v1/setup/session') -Method Post -Headers @{ Origin = $bridgeOrigin; 'X-Wallpaper-Setup' = '1' } -ContentType 'application/json' -Body '{}' -TimeoutSec 3).session
    $bridgeConfig = Invoke-RestMethod -Uri ($bridgeOrigin + '/v1/setup/config/read') -Method Post -Headers @{ Origin = $bridgeOrigin; 'X-Wallpaper-Admin' = $setupSession } -ContentType 'application/json' -Body '{}' -TimeoutSec 3
    $displayToken = $bridgeConfig.readToken
} catch { }
if (-not $displayToken -and (Test-Path -LiteralPath $tokenPath)) { $displayToken = (Get-Content -Raw -LiteralPath $tokenPath).Trim() }
if (-not $displayToken) { throw '请先运行本机桥接，再安装壁纸' }
$project = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $destination 'project.json') | ConvertFrom-Json
$project.general.properties.bridge_token.value = $displayToken
$project | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $destination 'project.json') -Encoding UTF8
Write-Output ('已安装：' + $destination)
if ($Apply) {
    if ($Monitor -lt 0) { throw '应用到桌面时需要指定经过确认的 -Monitor 索引' }
    & $weExe -control openWallpaper -file (Join-Path $destination 'project.json') -monitor $Monitor
}
if ($PreviewWindow) { & $weExe -control openWallpaper -file (Join-Path $destination 'project.json') -playInWindow 'Portrait Dashboard Preview' -width 800 -height 1280 }
