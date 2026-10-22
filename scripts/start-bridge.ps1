param([switch]$OpenSettings, [switch]$Elevated)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$exePath = Join-Path $repoRoot 'artifacts\bridge\WallpaperBridge.exe'
if (-not (Test-Path -LiteralPath $exePath)) { throw '请先运行 scripts/build.ps1' }
try { $health = Invoke-RestMethod -Uri 'http://127.0.0.1:17632/v1/health' -TimeoutSec 2 } catch { $health = $null }
if ($health -and $health.service -ne 'portrait-wallpaper-bridge') { throw '端口 17632 已被其他程序占用' }
if ($health -and $Elevated -and -not $health.elevated) {
    & (Join-Path $PSScriptRoot 'stop-bridge.ps1')
    $health = $null
}
if (-not $health) {
    $dataDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'WallpaperDashboard\private'
    $argsList = @{ FilePath = $exePath; ArgumentList = ('--data-dir "'+$dataDirectory+'"'); WorkingDirectory = (Split-Path -Parent $exePath); WindowStyle = 'Hidden'; PassThru = $true }
    if ($Elevated) { $argsList.Verb = 'RunAs' }
    $bridgeProcess = Start-Process @argsList
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        Start-Sleep -Milliseconds 500
        try { $health = Invoke-RestMethod -Uri 'http://127.0.0.1:17632/v1/health' -TimeoutSec 1; break } catch { }
    }
    if (-not $health) { throw '桥接启动失败，请检查端口或传感器依赖' }
}
Write-Output '本机桥接已运行：http://127.0.0.1:17632/admin/'
if ($OpenSettings) { Start-Process 'http://127.0.0.1:17632/admin/' }
