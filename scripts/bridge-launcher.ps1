param([Parameter(Mandatory = $true)][string]$DataDirectory)
# 管理员计划任务从受保护的运行目录执行本脚本（发布包内名为 run-bridge.ps1）。在调用任何 cmdlet 之前，
# 把模块自动加载限制在系统目录，避免以管理员身份加载普通权限即可写入的“文档\WindowsPowerShell\Modules”。
$env:PSModulePath = [IO.Path]::Combine($PSHOME, 'Modules')
$ErrorActionPreference = 'Stop'
$releaseRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$runtimeRoot = [IO.Path]::GetDirectoryName($releaseRoot)
$exePath = [IO.Path]::Combine($releaseRoot, 'WallpaperBridge.exe')
$stateDirectory = [IO.Path]::Combine($runtimeRoot, 'state')
$statePath = [IO.Path]::Combine($stateDirectory, 'autostart-runtime.json')
$origin = 'http://127.0.0.1:17632'
$DataDirectory = [IO.Path]::GetFullPath($DataDirectory)
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw '自动启动桥接需要管理员权限' }
if (-not [IO.File]::Exists($exePath)) { throw ('找不到桥接程序：' + $exePath) }
[IO.Directory]::CreateDirectory($stateDirectory) | Out-Null
$bridgeProcess = $null
try {
    # Validate in the task's own context, outside any Codex MSIX AppData overlay.
    Add-Type -AssemblyName System.Security
    $configBytes = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes([IO.Path]::Combine($DataDirectory, 'config.dpapi')), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $savedConfig = [Text.Encoding]::UTF8.GetString($configBytes) | ConvertFrom-Json
    $tokenBytes = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes([IO.Path]::Combine($DataDirectory, 'read-token.dpapi')), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $savedToken = [Text.Encoding]::UTF8.GetString($tokenBytes) | ConvertFrom-Json
    $bridgeProcess = Start-Process -FilePath $exePath -ArgumentList ('--data-dir "' + $DataDirectory + '"') -WorkingDirectory $releaseRoot -WindowStyle Hidden -PassThru
    $health = $null
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        Start-Sleep -Milliseconds 500
        if ($bridgeProcess.HasExited) { throw '桥接进程在启动时退出' }
        try { $health = Invoke-RestMethod -Uri ($origin + '/v1/health') -TimeoutSec 1; break } catch { }
    }
    if (-not $health -or $health.service -ne 'portrait-wallpaper-bridge' -or -not $health.elevated) { throw '管理员桥接未正常启动' }
    $session = (Invoke-RestMethod -Uri ($origin + '/v1/setup/session') -Method Post -Headers @{ Origin = $origin; 'X-Wallpaper-Setup' = '1' } -ContentType 'application/json' -Body '{}' -TimeoutSec 3).session
    $runningConfig = Invoke-RestMethod -Uri ($origin + '/v1/setup/config/read') -Method Post -Headers @{ Origin = $origin; 'X-Wallpaper-Admin' = $session } -ContentType 'application/json' -Body '{}' -TimeoutSec 3
    $accountsPreserved = ($runningConfig.accounts.id -join ',') -eq ($savedConfig.accounts.id -join ',')
    if ($runningConfig.readToken -ne $savedToken -or -not $accountsPreserved -or $runningConfig.managementConfigured -ne [bool]$savedConfig.managementKey) {
        throw '原有配置或展示票据未正确加载'
    }
    @{
        success = $true; buildId = $health.buildId; elevated = $health.elevated; releaseRoot = $releaseRoot; dataDirectory = $DataDirectory
        accountCount = @($runningConfig.accounts).Count; managementConfigured = $runningConfig.managementConfigured
        displayTokenPreserved = ($runningConfig.readToken -eq $savedToken); accountsPreserved = $accountsPreserved
        processId = $bridgeProcess.Id; verifiedAt = [DateTimeOffset]::Now.ToString('o')
    } | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8
    # Keep the task alive so the scheduler observes the bridge's actual exit code.
    $bridgeProcess.WaitForExit()
    exit $bridgeProcess.ExitCode
} catch {
    if ($bridgeProcess -and -not $bridgeProcess.HasExited) { Stop-Process -Id $bridgeProcess.Id -ErrorAction SilentlyContinue }
    @{ success = $false; releaseRoot = $releaseRoot; error = $_.Exception.Message; verifiedAt = [DateTimeOffset]::Now.ToString('o') } | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8
    throw
}