param([switch]$OpenSettings, [switch]$Elevated, [string]$RuntimeRoot, [string]$DataDirectory)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
$runtimeRoot = Get-BridgeRuntimeRoot $RuntimeRoot
$exePath = Join-Path $runtimeRoot 'current\WallpaperBridge.exe'
if (-not (Test-Path -LiteralPath $exePath)) { throw '受保护运行目录中没有桥接；请先运行 install-bridge.cmd，或 scripts/install-bridge.ps1。' }
$context = Get-BridgeInstallContext $runtimeRoot $DataDirectory
$dataDirectory = $context.dataDirectory
try { $health = Invoke-RestMethod -Uri 'http://127.0.0.1:17632/v1/health' -TimeoutSec 2 } catch { $health = $null }
if ($health -and $health.service -ne 'portrait-wallpaper-bridge') { throw '端口 17632 已被其他程序占用' }
if ($health -and $Elevated -and -not $health.elevated) {
    & (Join-Path $PSScriptRoot 'stop-bridge.ps1') -RuntimeRoot $runtimeRoot
    $health = $null
}
if (-not $health) {
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
