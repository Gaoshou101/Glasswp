$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $repoRoot 'scripts\release-common.ps1')
$testRoot = Join-Path $repoRoot ('artifacts\validation\installer-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
function Check($Condition, [string]$Message) { if (-not $Condition) { throw $Message }; Write-Output ('PASS ' + $Message) }
# The real context resolver runs against in-process substitutes for Windows resources.
$script:fakeTask = $null; $script:fakeHealth = $null; $script:fakeListener = $false; $script:fakeProcess = @(); $script:fakeCommand = ''
function Get-ScheduledTask { param($TaskName, $TaskPath, $ErrorAction) $script:fakeTask }
function Invoke-RestMethod { param($Uri, $TimeoutSec) $script:fakeHealth }
function Get-NetTCPConnection { param($LocalPort, $State, $ErrorAction) if ($script:fakeListener) { [pscustomobject]@{OwningProcess=99} } }
function Get-Process { param($Name, $ErrorAction) $script:fakeProcess }
function Get-CimInstance { param($ClassName, $Filter) [pscustomobject]@{CommandLine=$script:fakeCommand} }
function Test-IsAdministrator { $true }
function Assert-ProtectedRuntime { param($RuntimeRoot) $RuntimeRoot }
$probeRuntime = Join-Path ([IO.Path]::GetPathRoot($repoRoot)) ('WallpaperInstallerProbe-' + [Guid]::NewGuid().ToString('N'))
$private = Join-Path $testRoot 'old private'
try {
    $script:fakeTask = [pscustomobject]@{Actions=[pscustomobject]@{Arguments='-File "' + (Join-Path $probeRuntime 'current\run-bridge.ps1') + '" -DataDirectory "' + $private + '"'}}
    Check ((Get-BridgeInstallContext $probeRuntime).dataDirectory -eq $private) 'stopped bridge preserves the scheduled task private directory'
    $conflict = $false
    try { Get-BridgeInstallContext $probeRuntime (Join-Path $testRoot 'different') | Out-Null } catch { $conflict = $true }
    Check $conflict 'a different explicit private directory is rejected'
    $script:fakeTask.Actions.Arguments = '-File "Z:\Other\run.ps1"'
    $conflict = $false
    try { Get-BridgeInstallContext $probeRuntime | Out-Null } catch { $conflict = $true }
    Check $conflict 'a same-name task from another installation is rejected'
    $script:fakeTask = $null; $script:fakeListener = $true
    $conflict = $false
    try { Get-BridgeInstallContext $probeRuntime | Out-Null } catch { $conflict = $true }
    Check $conflict 'an occupied port without a healthy bridge is rejected'
    $script:fakeListener = $false
    $script:fakeHealth = [pscustomobject]@{service='portrait-wallpaper-bridge'}
    $script:fakeProcess = @([pscustomobject]@{Id=99;Path=(Join-Path $probeRuntime 'current\WallpaperBridge.exe')})
    $script:fakeCommand = '--data-dir "' + $private + '"'
    Check ((Get-BridgeInstallContext $probeRuntime).dataDirectory -eq $private) 'a manually running bridge preserves its explicit private directory'
    $script:fakeProcess[0].Path = $null
    function Test-IsAdministrator { $false }
    Check ((Get-BridgeInstallContext $probeRuntime).ownershipNeedsElevation) 'a normal-user check reports the remaining elevated ownership check'
    $script:fakeHealth = $null
    $recordRuntime = Join-Path ([IO.Path]::GetPathRoot($repoRoot)) ('WallpaperInstallerState-' + [Guid]::NewGuid().ToString('N'))
    # Avoid writing to the drive root: replace only the runtime path restriction for this state lookup.
    function Get-BridgeRuntimeRoot { param($RuntimeRoot) $RuntimeRoot }
    $recordRuntime = Join-Path $testRoot 'saved-runtime'
    New-Item -ItemType Directory -Path (Join-Path $recordRuntime 'state') | Out-Null
    @{success=$true;dataDirectory=$private} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $recordRuntime 'state\bridge-install.json') -Encoding UTF8
    Check ((Get-BridgeInstallContext $recordRuntime).dataDirectory -eq $private) 'a stopped manual installation preserves its saved private directory'

    # Run the real installer against a synthetic package, replacing its child scripts only.
    $harness = Join-Path $testRoot 'scripts'; $payload = Join-Path $testRoot 'payload'; $runtime = Join-Path $testRoot 'runtime'
    New-Item -ItemType Directory -Path $harness,$payload,(Join-Path $runtime 'state') | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts\install-bridge.ps1') -Destination $harness
    $files = @{}
    foreach ($name in @('WallpaperBridge.exe','WallpaperBridge.dll','run-bridge.ps1','wallpaper/index.html','wallpaper/wallpaper.js','wallpaper/project.json')) {
        $path = Join-Path $payload $name
        New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
        Set-Content -LiteralPath $path -Value 'test fixture'
        $files[$name] = (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant()
    }
    @{buildId='installer-fixture';files=$files} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $payload 'build-info.json') -Encoding UTF8
    $sharedSource = (Join-Path $repoRoot 'scripts\release-common.ps1').Replace("'", "''")
    $stub = ". '$sharedSource'`r`n" + @'
function Test-IsAdministrator { $global:installerAdmin }
function Get-BridgeInstallContext {
    param($RuntimeRoot, $DataDirectory)
    [pscustomobject]@{runtimeRoot=$RuntimeRoot;dataDirectory=$DataDirectory;health=$null;task=$null;ownershipNeedsElevation=$false}
}
function Wait-BridgeReady { param($BuildId, $ExpectedPath) $global:installerCalls.Add('verify') }
function Start-Process {
    param($FilePath, $ArgumentList, $Verb, $WindowStyle, [switch]$PassThru)
    if ($Verb -ne 'RunAs') { throw 'Unexpected process call in isolated installer test' }
    $global:installerCalls.Add('uac')
    $global:installerUacArguments = $ArgumentList
    $process = [pscustomobject]@{ExitCode=$global:installerExitCode}
    $process | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value { }
    $process
}
'@
    Set-Content -LiteralPath (Join-Path $harness 'release-common.ps1') -Value $stub -Encoding UTF8
    @'
param($Source, $RuntimeRoot, $DataDirectory, [switch]$Start, [switch]$NoElevation)
if (-not $Start -or -not $NoElevation) { throw 'Installer must request startup without another elevation' }
$global:installerCalls.Add('update')
$global:installerPrivate = $DataDirectory
'@ | Set-Content -LiteralPath (Join-Path $harness 'update-bridge.ps1') -Encoding UTF8
    @'
param($RuntimeRoot, $DataDirectory)
$global:installerCalls.Add('autostart')
if ($global:installerAutostartFail) { throw 'simulated task registration failure' }
'@ | Set-Content -LiteralPath (Join-Path $harness 'install-bridge-autostart.ps1') -Encoding UTF8
    $installer = Join-Path $harness 'install-bridge.ps1'
    $global:installerCalls = [Collections.Generic.List[string]]::new(); $global:installerAdmin = $false; $global:installerExitCode = 0; $global:installerAutostartFail = $false
    $plan = & $installer -Source $payload -RuntimeRoot $runtime -DataDirectory $private -Check
    Check ($plan.checkOnly -and $plan.buildId -eq 'installer-fixture' -and $global:installerCalls.Count -eq 0) 'check validates hashes and plan without starting a process or changing tasks'
    & $installer -Source $payload -RuntimeRoot $runtime -DataDirectory $private -NoOpenSettings | Out-Null
    Check (($global:installerCalls -join ',') -eq 'uac' -and $global:installerUacArguments.Contains('-NoElevation') -and $global:installerUacArguments.Contains('-ExpectedUserSid')) 'ordinary installation uses one UAC and binds the initiating Windows user'
    $invalidPlan = $false
    try { & $installer -Source $payload -RuntimeRoot $runtime -DataDirectory (Join-Path $runtime 'private') -Check | Out-Null } catch { $invalidPlan = $true }
    Check $invalidPlan 'check rejects private directories inside the runtime before any installation'
    $global:installerCalls.Clear(); $global:installerAdmin = $true
    & $installer -Source $payload -RuntimeRoot $runtime -DataDirectory $private -NoOpenSettings | Out-Null
    $result = Get-Content -Raw -LiteralPath (Join-Path $runtime 'state\bridge-install.json') | ConvertFrom-Json
    Check (($global:installerCalls -join ',') -eq 'update,autostart,verify' -and $result.success -and $global:installerPrivate -eq $private) 'elevated install starts the runtime, registers autostart, verifies, and preserves private path'
    $global:installerCalls.Clear()
    & $installer -Source $payload -RuntimeRoot $runtime -DataDirectory $private -NoAutostart -NoOpenSettings | Out-Null
    Check (($global:installerCalls -join ',') -eq 'update,verify') 'NoAutostart skips task changes while installing and starting the runtime'
    $global:installerAutostartFail = $true; $failed = $false
    try { & $installer -Source $payload -RuntimeRoot $runtime -DataDirectory $private -NoOpenSettings | Out-Null } catch { $failed = $true }
    $result = Get-Content -Raw -LiteralPath (Join-Path $runtime 'state\bridge-install.json') | ConvertFrom-Json
    Check ($failed -and -not $result.success -and $result.phase -eq 'install-autostart') 'task registration failure propagates and records incomplete installation'
    # Start with a completed manual install, then let the real updater fail its transaction.
    # Both failure reports replace the successful report; a stopped retry must still find private.
    $global:installerAutostartFail = $false
    & $installer -Source $payload -RuntimeRoot $runtime -DataDirectory $private -NoAutostart -NoOpenSettings | Out-Null
    Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts\update-bridge.ps1') -Destination (Join-Path $harness 'update-bridge.ps1') -Force
    @'
function Get-BridgeRuntimeRoot { param($RuntimeRoot) $RuntimeRoot }
function Assert-ProtectedRuntime { param($RuntimeRoot) $RuntimeRoot }
function Get-ScheduledTask { param($TaskName, $TaskPath, $ErrorAction) $null }
function Invoke-RestMethod { param($Uri, $TimeoutSec) $null }
function Invoke-ReleaseTransaction { throw 'simulated stopped upgrade transaction failure' }
'@ | Add-Content -LiteralPath (Join-Path $harness 'release-common.ps1') -Encoding UTF8
    $failed = $false
    try { & $installer -Source $payload -RuntimeRoot $runtime -DataDirectory $private -NoAutostart -NoOpenSettings | Out-Null } catch { $failed = $true }
    $installFailure = Get-Content -Raw -LiteralPath (Join-Path $runtime 'state\bridge-install.json') | ConvertFrom-Json
    $updateFailure = Get-Content -Raw -LiteralPath (Join-Path $runtime 'state\bridge-update.json') | ConvertFrom-Json
    Check ($failed -and -not $installFailure.success -and -not $updateFailure.installed -and $installFailure.dataDirectory -eq $private -and $updateFailure.dataDirectory -eq $private) 'failed stopped upgrade preserves private in both overwritten failure reports'
    Check ((Get-BridgeInstallContext $runtime).dataDirectory -eq $private) 'retry resolves the stopped custom private directory after installation and update failures'
    Add-Content -LiteralPath (Join-Path $payload 'WallpaperBridge.dll') -Value 'tampered'
    $global:installerCalls.Clear(); $failed = $false
    try { & $installer -Source $payload -RuntimeRoot $runtime -DataDirectory $private -Check | Out-Null } catch { $failed = $true }
    Check ($failed -and $global:installerCalls.Count -eq 0) 'tampered payload is rejected before elevation or installation'
    Write-Output 'Installer checks complete; production runtime and scheduled tasks untouched.'
} finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $validation = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\validation')).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($validation, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
