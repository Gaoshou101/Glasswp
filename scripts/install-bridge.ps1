param(
    [string]$Source,
    [string]$RuntimeRoot,
    [string]$DataDirectory,
    [switch]$NoAutostart,
    [switch]$NoOpenSettings,
    [switch]$Check,
    [switch]$NoElevation,
    [string]$ExpectedUserSid
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
$currentUserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
if ($ExpectedUserSid -and $ExpectedUserSid -ne $currentUserSid) { throw '请使用同一 Windows 用户接受 UAC；切换管理员账号会导致 DPAPI 私有配置无法读取。' }
$packageRoot = Split-Path -Parent $PSScriptRoot
if (-not $Source) { $Source = Join-Path $packageRoot 'artifacts\staging' }
$Source = Get-ReleasePath $Source
$required = @('WallpaperBridge.exe','WallpaperBridge.dll','run-bridge.ps1','wallpaper/index.html','wallpaper/wallpaper.js','wallpaper/project.json')
if (-not (Test-Path -LiteralPath (Join-Path $Source 'build-info.json'))) {
    throw '找不到桥接发布包。请解压完整安装包后运行 install-bridge.cmd；源码用户先运行 scripts/build.ps1，或用 -Source 指定已构建发布目录。'
}
$info = Test-ReleasePackage $Source $required
$explicitDataDirectory = [bool]$DataDirectory
$context = Get-BridgeInstallContext $RuntimeRoot $DataDirectory
$RuntimeRoot = $context.runtimeRoot
$DataDirectory = $context.dataDirectory
if ($Source.IndexOfAny([char[]]@('"', "`r", "`n")) -ge 0) { throw '发布包路径不得包含引号或换行。' }
foreach ($parent in @($RuntimeRoot, $Source)) {
    if ($DataDirectory -eq $parent -or $DataDirectory.StartsWith($parent + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '私有配置目录必须位于运行目录与发布包之外。' }
}
if ($Check) {
    [pscustomobject]@{
        success = $true; checkOnly = $true; buildId = $info.buildId; source = $Source
        runtimeRoot = $RuntimeRoot; dataDirectory = $DataDirectory; bridgeRunning = [bool]$context.health
        autostart = if ($NoAutostart) { 'preserve-existing' } else { 'install' }
        requiresElevation = -not (Test-IsAdministrator); ownershipNeedsElevation = $context.ownershipNeedsElevation
    }
    return
}
$stateResult = Join-Path $RuntimeRoot 'state\bridge-install.json'
if (-not (Test-IsAdministrator)) {
    if ($NoElevation) { throw '安装桥接需要管理员权限；请接受 UAC 后重试。' }
    $arguments = '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $PSCommandPath + '" -Source "' + $Source + '" -RuntimeRoot "' + $RuntimeRoot + '" -NoElevation -NoOpenSettings -ExpectedUserSid "' + $currentUserSid + '"'
    if ($explicitDataDirectory -or -not $context.ownershipNeedsElevation -or $context.task) { $arguments += ' -DataDirectory "' + $DataDirectory + '"' }
    if ($NoAutostart) { $arguments += ' -NoAutostart' }
    try {
        $elevated = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -PassThru
        $elevated.WaitForExit()
    } catch { throw ('安装未完成：无法启动管理员安装进程。请接受 UAC 后重试。' + $_.Exception.Message) }
    if ($elevated.ExitCode -ne 0) {
        $detail = ''
        if (Test-Path -LiteralPath $stateResult) { try { $detail = (Get-Content -Raw -LiteralPath $stateResult | ConvertFrom-Json).error } catch { } }
        throw ('安装未完成：' + $detail + '。诊断记录：' + $stateResult)
    }
} else {
    $phase = 'install-runtime'
    try {
        & (Join-Path $PSScriptRoot 'update-bridge.ps1') -Source $Source -RuntimeRoot $RuntimeRoot -DataDirectory $DataDirectory -Start -NoElevation
        if (-not $NoAutostart) {
            $phase = 'install-autostart'
            & (Join-Path $PSScriptRoot 'install-bridge-autostart.ps1') -RuntimeRoot $RuntimeRoot -DataDirectory $DataDirectory
        }
        $phase = 'verify'
        Wait-BridgeReady -BuildId $info.buildId -ExpectedPath (Join-Path $RuntimeRoot 'current\WallpaperBridge.exe') | Out-Null
        @{
            success = $true; phase = 'complete'; buildId = $info.buildId; runtimeRoot = $RuntimeRoot; dataDirectory = $DataDirectory
            autostart = if ($NoAutostart) { 'preserved' } else { 'installed' }; verifiedAt = [DateTimeOffset]::Now.ToString('o')
        } | ConvertTo-Json | Set-Content -LiteralPath $stateResult -Encoding UTF8
    } catch {
        if (Test-Path -LiteralPath (Split-Path -Parent $stateResult)) {
            @{success=$false;phase=$phase;buildId=$info.buildId;dataDirectory=$dataDirectory;error=$_.Exception.Message;verifiedAt=[DateTimeOffset]::Now.ToString('o')} | ConvertTo-Json | Set-Content -LiteralPath $stateResult -Encoding UTF8
        }
        throw
    }
}
Write-Output ('桥接安装完成：' + (Join-Path $RuntimeRoot 'current') + '；本机设置：http://127.0.0.1:17632/admin/')
if (-not $NoOpenSettings) { Start-Process 'http://127.0.0.1:17632/admin/' }
