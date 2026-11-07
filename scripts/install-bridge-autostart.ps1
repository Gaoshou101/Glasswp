param(
    [string]$DataDirectory,
    [string]$RuntimeRoot,
    [switch]$Remove,
    [switch]$Run
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$taskName = 'WallpaperDashboardBridge'
if (-not $DataDirectory) {
    $DataDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'WallpaperDashboard\private'
}
$DataDirectory = [IO.Path]::GetFullPath($DataDirectory)

# Legacy launcher: before the runtime migration the scheduled task ran this repository script with
# -Run. It stays only until scripts/migrate-bridge-runtime.ps1 has moved the task; it is never registered again.
$isAdministrator = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$exePath = Join-Path $repoRoot 'artifacts\bridge\WallpaperBridge.exe'
$origin = 'http://127.0.0.1:17632'
$runtimeResultPath = Join-Path $repoRoot 'artifacts\validation\bridge-autostart-runtime.json'
if ($Run) {
    if (-not $isAdministrator) { throw '自动启动桥接需要管理员权限' }
    if (-not (Test-Path -LiteralPath $exePath)) { throw '请先运行 scripts/build.ps1' }
    New-Item -ItemType Directory -Path (Split-Path -Parent $runtimeResultPath) -Force | Out-Null
    $bridgeProcess = $null
    try {
        # Validate in the task's own context, outside any Codex MSIX AppData overlay.
        Add-Type -AssemblyName System.Security
        $configBytes = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes((Join-Path $DataDirectory 'config.dpapi')), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
        $savedConfig = [Text.Encoding]::UTF8.GetString($configBytes) | ConvertFrom-Json
        $tokenBytes = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes((Join-Path $DataDirectory 'read-token.dpapi')), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
        $savedToken = [Text.Encoding]::UTF8.GetString($tokenBytes) | ConvertFrom-Json
        $bridgeProcess = Start-Process -FilePath $exePath -ArgumentList ('--data-dir "' + $DataDirectory + '"') -WorkingDirectory (Split-Path -Parent $exePath) -WindowStyle Hidden -PassThru
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
            success = $true; dataDirectory = $DataDirectory; elevated = $health.elevated
            accountCount = $runningConfig.accounts.Count; managementConfigured = $runningConfig.managementConfigured
            displayTokenPreserved = ($runningConfig.readToken -eq $savedToken); accountsPreserved = $accountsPreserved
            processId = $bridgeProcess.Id; verifiedAt = [DateTimeOffset]::Now.ToString('o')
        } | ConvertTo-Json | Set-Content -LiteralPath $runtimeResultPath -Encoding UTF8
        # Keep the task alive so the scheduler observes the bridge's actual exit code.
        $bridgeProcess.WaitForExit()
        exit $bridgeProcess.ExitCode
    } catch {
        if ($bridgeProcess -and -not $bridgeProcess.HasExited) { Stop-Process -Id $bridgeProcess.Id -ErrorAction SilentlyContinue }
        @{ success = $false; error = $_.Exception.Message; verifiedAt = [DateTimeOffset]::Now.ToString('o') } | ConvertTo-Json | Set-Content -LiteralPath $runtimeResultPath -Encoding UTF8
        throw
    }
}

. (Join-Path $PSScriptRoot 'release-common.ps1')
$runtimeRoot = Get-BridgeRuntimeRoot $RuntimeRoot
$launcher = Get-BridgeLauncherPath $runtimeRoot
$runtimeExe = Join-Path $runtimeRoot 'current\WallpaperBridge.exe'
$stateResult = Join-Path $runtimeRoot 'state\autostart-install.json'
$repoResult = Join-Path $repoRoot 'artifacts\validation\bridge-autostart.json'

if (-not (Test-IsAdministrator)) {
    $installerArguments = '-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + $PSCommandPath + '" -DataDirectory "' + $DataDirectory + '" -RuntimeRoot "' + $runtimeRoot + '"'
    if ($Remove) { $installerArguments += ' -Remove' }
    $startedAt = [DateTime]::UtcNow
    $installerProcess = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -ArgumentList $installerArguments -Verb RunAs -WindowStyle Hidden -PassThru
    $installerProcess.WaitForExit()
    if ((Test-Path -LiteralPath $stateResult) -and (Get-Item -LiteralPath $stateResult).LastWriteTimeUtc -ge $startedAt.AddSeconds(-2)) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $repoResult) -Force | Out-Null
        Copy-Item -LiteralPath $stateResult -Destination $repoResult -Force
    }
    if ($installerProcess.ExitCode -ne 0) { throw ('自动启动任务安装失败，请查看 ' + $stateResult) }
    Write-Output '管理员自动启动任务已更新'
    return
}

function Write-InstallResult($Value) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $stateResult) -Force | Out-Null
    $Value | ConvertTo-Json | Set-Content -LiteralPath $stateResult -Encoding UTF8
}
try {
    $existingTask = Get-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction SilentlyContinue
    if ($existingTask) {
        $taskArguments = $existingTask.Actions.Arguments -join ' '
        $known = @(@($launcher, $PSCommandPath) | Where-Object { $taskArguments.IndexOf(('"' + $_ + '"'), [StringComparison]::OrdinalIgnoreCase) -ge 0 })
        if (-not $known.Count) { throw '同名任务属于另一位置，请先检查现有任务' }
    }
    if ($Remove) {
        if ($existingTask) { Unregister-ScheduledTask -TaskName $taskName -TaskPath '\' -Confirm:$false }
        Write-InstallResult @{ success = $true; removed = $true; taskName = $taskName; verifiedAt = [DateTimeOffset]::Now.ToString('o') }
        Write-Output '已取消登录自动启动；正在运行的桥接可用 stop-bridge.ps1 停止'
        return
    }
    Assert-ProtectedRuntime $runtimeRoot | Out-Null
    $info = Test-ReleasePackage (Join-Path $runtimeRoot 'current') @('WallpaperBridge.exe','run-bridge.ps1')
    $configPath = Join-Path $DataDirectory 'config.dpapi'
    if (-not (Test-Path -LiteralPath $configPath)) { throw '找不到已有配置，请用 -DataDirectory 指定原桥接的 private 目录' }
    Add-Type -AssemblyName System.Security
    $configBytes = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($configPath), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $savedConfig = [Text.Encoding]::UTF8.GetString($configBytes) | ConvertFrom-Json
    if ($savedConfig.port -ne 17632) { throw '便捷启动脚本要求桥接使用默认端口 17632' }

    $health = $null
    try { $health = Invoke-RestMethod -Uri ($script:BridgeOrigin + '/v1/health') -TimeoutSec 2 } catch { }
    if ($health) {
        if ($health.service -ne 'portrait-wallpaper-bridge') { throw '端口 17632 已被其他程序占用' }
        # Stop only a bridge from this checkout or the protected runtime before handing it to the task.
        & (Join-Path $PSScriptRoot 'stop-bridge.ps1') -RuntimeRoot $runtimeRoot
        try { $stillRunning = Invoke-RestMethod -Uri ($script:BridgeOrigin + '/v1/health') -TimeoutSec 1 } catch { $stillRunning = $null }
        if ($stillRunning) { throw '端口上的桥接属于另一位置，请检查后再安装自动启动' }
    }
    if ($existingTask -and $existingTask.State -eq 'Running') { Stop-ScheduledTask -TaskName $taskName -TaskPath '\' }
    $launcherState = Join-Path $runtimeRoot 'state\autostart-runtime.json'
    if (Test-Path -LiteralPath $launcherState) { Remove-Item -LiteralPath $launcherState }
    $userSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $action = New-BridgeTaskAction $runtimeRoot $DataDirectory
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $userSid
    $trigger.Delay = 'PT15S'
    $principal = New-ScheduledTaskPrincipal -UserId $userSid -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName $taskName -TaskPath '\' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description ('登录后以当前用户的管理员权限，从受保护目录 ' + $runtimeRoot + ' 启动本机壁纸数据桥接；使用固定 DPAPI 配置目录。') -Force | Out-Null
    Start-ScheduledTask -TaskName $taskName -TaskPath '\'
    $ready = Wait-BridgeReady -BuildId $info.buildId -ExpectedPath $runtimeExe
    $runtimeResult = $null
    for ($attempt = 0; $attempt -lt 10; $attempt++) {
        if (Test-Path -LiteralPath $launcherState) {
            try { $runtimeResult = Get-Content -Raw -LiteralPath $launcherState | ConvertFrom-Json; break } catch { }
        }
        Start-Sleep -Milliseconds 500
    }
    if (-not $runtimeResult -or -not $runtimeResult.success) { throw '桥接已启动，但任务环境中的配置核验未通过' }
    $task = Get-ScheduledTask -TaskName $taskName -TaskPath '\'
    Write-InstallResult @{
        success = $true; taskName = $taskName; state = $task.State.ToString(); elevated = $ready.health.elevated; buildId = $info.buildId
        runtimeRoot = $runtimeRoot; dataDirectory = $DataDirectory; accountCount = $runtimeResult.accountCount; managementConfigured = $runtimeResult.managementConfigured
        displayTokenPreserved = $runtimeResult.displayTokenPreserved; accountsPreserved = $runtimeResult.accountsPreserved; logonDelaySeconds = 15
        restartCount = 3; verifiedAt = [DateTimeOffset]::Now.ToString('o')
    }
    Write-Output '已注册管理员登录自动启动任务，本机桥接已运行：http://127.0.0.1:17632/admin/'
} catch {
    Write-InstallResult @{ success = $false; taskName = $taskName; error = $_.Exception.Message; verifiedAt = [DateTimeOffset]::Now.ToString('o') }
    throw
}