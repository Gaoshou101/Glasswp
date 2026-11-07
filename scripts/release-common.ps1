Set-StrictMode -Version Latest

function Get-ReleasePath([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    if ($resolved -eq [IO.Path]::GetPathRoot($resolved).TrimEnd('\', '/')) { throw 'Release target must not be a drive root' }
    return $resolved
}
function Assert-NoReleaseAncestors([string]$Path) {
    $ancestor=[IO.Path]::GetFullPath($Path)
    while ($ancestor) {
        if ((Test-Path -LiteralPath $ancestor) -and (Get-Item -LiteralPath $ancestor -Force).LinkType -in @('SymbolicLink','Junction')) { throw 'Release ancestors must not be links' }
        $ancestor=Split-Path -Parent $ancestor
    }
}
function Assert-NoReleaseLinks([string]$Path) {
    Assert-NoReleaseAncestors $Path
    if ((Get-Item -LiteralPath $Path -Force).LinkType -in @('SymbolicLink','Junction')) { throw 'Release paths must not be links' }
    foreach ($item in Get-ChildItem -LiteralPath $Path -Recurse -Force) {
        if ($item.LinkType -in @('SymbolicLink','Junction')) { throw 'Release packages must not contain links' }
    }
}
function Test-ReleasePackage([string]$Path, [string[]]$RequiredFiles = @()) {
    $root = Get-ReleasePath $Path
    Assert-NoReleaseLinks $root
    $info = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'build-info.json') | ConvertFrom-Json
    if ($info.buildId -notmatch '^[a-zA-Z0-9.-]{1,80}$' -or -not $info.files) { throw 'Invalid release manifest' }
    foreach ($required in $RequiredFiles) {
        if (-not $info.files.PSObject.Properties[$required]) { throw ('Missing required release file: ' + $required) }
    }
    $expected = @('build-info.json')
    foreach ($file in $info.files.PSObject.Properties) {
        $target = [IO.Path]::GetFullPath((Join-Path $root $file.Name))
        if (-not $target.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Release path escapes package' }
        if ($file.Value -notmatch '^[a-f0-9]{64}$' -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $file.Value) { throw ('Release checksum mismatch: ' + $file.Name) }
        $expected += $file.Name.Replace('/', '\')
    }
    foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File -Force) {
        $relative = $file.FullName.Substring($root.Length + 1)
        if ($relative -notin $expected) { throw ('Unlisted release file: ' + $relative) }
    }
    return $info
}
function Merge-WallpaperProject($Candidate, $Previous) {
    if (-not $Previous) { return $Candidate }
    $properties = $Candidate.general.properties
    foreach ($property in $properties.PSObject.Properties) {
        $saved = $Previous.general.properties.PSObject.Properties[$property.Name]
        if ($saved -and $saved.Value.PSObject.Properties['value']) { $property.Value.value = $saved.Value.value }
    }
    foreach ($property in $Previous.general.properties.PSObject.Properties) {
        if (-not $properties.PSObject.Properties[$property.Name]) { $properties | Add-Member -NotePropertyName $property.Name -NotePropertyValue $property.Value }
    }
    $Previous.general.properties = $properties
    return $Previous
}
function Invoke-ReleaseTransaction {
    param([string]$Source, [string]$Destination, [string]$BackupRoot,
          [scriptblock]$Prepare = {}, [scriptblock]$BeforeSwitch = {}, [scriptblock]$Verify = {}, [scriptblock]$BeforeRollback = {}, [scriptblock]$Recover = {})
    $sourcePath = Get-ReleasePath $Source
    $destinationPath = Get-ReleasePath $Destination
    $backupPath = Get-ReleasePath $BackupRoot
    if ([IO.Path]::GetPathRoot($destinationPath) -ne [IO.Path]::GetPathRoot($backupPath)) { throw 'Rollback directory must be on the target volume' }
    foreach ($pair in @(@($sourcePath,$destinationPath),@($sourcePath,$backupPath),@($destinationPath,$backupPath))) {
        if ($pair[0] -eq $pair[1] -or $pair[0].StartsWith($pair[1]+'\',[StringComparison]::OrdinalIgnoreCase) -or $pair[1].StartsWith($pair[0]+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Release paths overlap' }
    }
    Assert-NoReleaseLinks $sourcePath
    Assert-NoReleaseAncestors $destinationPath
    Assert-NoReleaseAncestors $backupPath
    if (Test-Path -LiteralPath $destinationPath) { Assert-NoReleaseLinks $destinationPath }
    New-Item -ItemType Directory -Path $backupPath -Force | Out-Null
    Assert-NoReleaseLinks $backupPath
    $parent = Split-Path -Parent $destinationPath
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $stagedPath = Join-Path $parent ((Split-Path -Leaf $destinationPath) + '.candidate-' + [Guid]::NewGuid().ToString('N'))
    $oldPath = Join-Path $backupPath ('previous-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stagedPath | Out-Null
    Get-ChildItem -LiteralPath $sourcePath -Force | Copy-Item -Destination $stagedPath -Recurse -Force
    $switched = $false; $hadOld = Test-Path -LiteralPath $destinationPath; $prepared = $false
    try {
        & $Prepare $stagedPath | Out-Null
        $prepared = $true
        & $BeforeSwitch | Out-Null
        if ($hadOld) { Move-Item -LiteralPath $destinationPath -Destination $oldPath }
        Move-Item -LiteralPath $stagedPath -Destination $destinationPath
        $switched = $true
        & $Verify $destinationPath | Out-Null
        return [pscustomobject]@{ destination=$destinationPath; backup=if($hadOld){$oldPath}else{$null}; installed=$true }
    } catch {
        $failure = $_
        if ($prepared) { & $BeforeRollback | Out-Null }
        if ($switched) { Move-Item -LiteralPath $destinationPath -Destination (Join-Path $backupPath ('failed-' + [Guid]::NewGuid().ToString('N'))) }
        if (Test-Path -LiteralPath $oldPath) { Move-Item -LiteralPath $oldPath -Destination $destinationPath }
        if ($prepared) { & $Recover | Out-Null }
        throw $failure
    }
}

# ---- Elevated runtime -------------------------------------------------------------------------
# The administrator scheduled task runs the bridge from this directory. It sits directly under a
# drive root, so no user-writable ancestor can be renamed and replaced, and it is outside the
# CloudDrive2 project sync range. Only Administrators and SYSTEM may write to it.
$script:DefaultBridgeRuntimeRoot = 'E:\WallpaperDashboard'
$script:AdminEquivalentSids = @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
$script:BridgeOrigin = 'http://127.0.0.1:17632'

function Get-BridgeRuntimeRoot([string]$RuntimeRoot) {
    if (-not $RuntimeRoot) { $RuntimeRoot = $script:DefaultBridgeRuntimeRoot }
    $root = Get-ReleasePath $RuntimeRoot
    if ((Split-Path -Parent $root).TrimEnd('\') -ne [IO.Path]::GetPathRoot($root).TrimEnd('\')) { throw 'Runtime root must be a direct child of a drive root' }
    return $root
}
function Get-BridgeLauncherPath([string]$RuntimeRoot) { Join-Path (Get-BridgeRuntimeRoot $RuntimeRoot) 'current\run-bridge.ps1' }
function Test-IsAdministrator {
    [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
# Allow rules for anyone other than SYSTEM, Administrators or TrustedInstaller must be read/execute only.
function Get-RuntimeWriteGrants([string]$Path) {
    $allowed = [int64]1179817 -bor [int64]2147483648 -bor [int64]536870912  # ReadAndExecute, Synchronize, GENERIC_READ, GENERIC_EXECUTE
    $acl = Get-Acl -LiteralPath $Path
    $grants = @()
    if ($script:AdminEquivalentSids -notcontains $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value) { $grants += ('owner:' + $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value) }
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow) { continue }
        if ($script:AdminEquivalentSids -contains $rule.IdentityReference.Value) { continue }
        $rights = ([int64][int]$rule.FileSystemRights) -band [int64]4294967295
        if ($rights -band (-bnot $allowed)) { $grants += ($rule.IdentityReference.Value + ':' + $rule.FileSystemRights) }
    }
    return ,$grants
}
function Assert-ProtectedRuntime([string]$RuntimeRoot) {
    $root = Get-BridgeRuntimeRoot $RuntimeRoot
    Assert-NoReleaseAncestors $root
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw 'Runtime root does not exist' }
    if (-not (Get-Acl -LiteralPath $root).AreAccessRulesProtected) { throw 'Runtime root must not inherit permissions' }
    # Other users must not be able to delete, rename or re-permission the root through its parent.
    $parent = Split-Path -Parent $root
    $parentAcl = Get-Acl -LiteralPath $parent
    $parentRisk = [int64]0x40 -bor [int64]0x40000 -bor [int64]0x80000 -bor [int64]0x10000000  # delete child, WRITE_DAC, WRITE_OWNER, GENERIC_ALL
    if ($script:AdminEquivalentSids -notcontains $parentAcl.GetOwner([Security.Principal.SecurityIdentifier]).Value) { throw ('Runtime parent is not owned by an administrator: ' + $parent) }
    foreach ($rule in $parentAcl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow -or $script:AdminEquivalentSids -contains $rule.IdentityReference.Value) { continue }
        if ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) { continue }
        if ((([int64][int]$rule.FileSystemRights) -band [int64]4294967295) -band $parentRisk) { throw ('Runtime parent lets ' + $rule.IdentityReference.Value + ' replace the runtime root') }
    }
    $items = @(Get-Item -LiteralPath $root -Force) + @(Get-ChildItem -LiteralPath $root -Recurse -Force)
    foreach ($item in $items) {
        if ($item.LinkType) { throw ('Runtime must not contain links: ' + $item.FullName) }
        $grants = Get-RuntimeWriteGrants $item.FullName
        if ($grants.Count) { throw ('Non-administrators can modify ' + $item.FullName + ': ' + ($grants -join ', ')) }
    }
    return $root
}
# Create or repair the runtime root: owner Administrators, no inheritance, SYSTEM/Administrators
# full control and Users read/execute, inherited by everything below it. Requires elevation.
function Initialize-ProtectedRuntime([string]$RuntimeRoot) {
    if (-not (Test-IsAdministrator)) { throw 'Protecting the runtime requires an administrator context' }
    $root = Get-BridgeRuntimeRoot $RuntimeRoot
    Assert-NoReleaseAncestors $root
    if (-not (Test-Path -LiteralPath $root)) { New-Item -ItemType Directory -Path $root | Out-Null }
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetOwner([Security.Principal.SecurityIdentifier]'S-1-5-32-544')
    $acl.SetAccessRuleProtection($true, $false)
    $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    foreach ($entry in @(@('S-1-5-18', 'FullControl'), @('S-1-5-32-544', 'FullControl'), @('S-1-5-32-545', 'ReadAndExecute'))) {
        $identity = [Security.Principal.SecurityIdentifier]$entry[0]
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($identity, [Security.AccessControl.FileSystemRights]$entry[1], $inherit, [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow)))
    }
    Set-Acl -LiteralPath $root -AclObject $acl
    foreach ($name in @('backups', 'state')) { New-Item -ItemType Directory -Path (Join-Path $root $name) -Force | Out-Null }
    return (Assert-ProtectedRuntime $root)
}
# Keep the newest rollback copies; previous-* and failed-* are created by Invoke-ReleaseTransaction.
function Remove-OldRuntimeBackups([string]$BackupRoot, [int]$Keep = 2) {
    $root = Get-ReleasePath $BackupRoot
    if (-not (Test-Path -LiteralPath $root)) { return 0 }
    $old = @(Get-ChildItem -LiteralPath $root -Directory -Force | Where-Object { $_.Name -match '^(previous|failed)-[0-9a-f]{32}$' } | Sort-Object CreationTimeUtc -Descending | Select-Object -Skip $Keep)
    foreach ($directory in $old) {
        Assert-NoReleaseLinks $directory.FullName
        Remove-Item -LiteralPath $directory.FullName -Recurse -Force
    }
    return $old.Count
}
function New-BridgeTaskAction([string]$RuntimeRoot, [string]$DataDirectory) {
    $launcher = Get-BridgeLauncherPath $RuntimeRoot
    $arguments = '-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + $launcher + '" -DataDirectory "' + $DataDirectory + '"'
    New-ScheduledTaskAction -Execute (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -Argument $arguments -WorkingDirectory (Split-Path -Parent $launcher)
}
function Read-BridgeConfig {
    $session = (Invoke-RestMethod -Uri ($script:BridgeOrigin + '/v1/setup/session') -Method Post -Headers @{ Origin = $script:BridgeOrigin; 'X-Wallpaper-Setup' = '1' } -ContentType 'application/json' -Body '{}' -TimeoutSec 3).session
    Invoke-RestMethod -Uri ($script:BridgeOrigin + '/v1/setup/config/read') -Method Post -Headers @{ Origin = $script:BridgeOrigin; 'X-Wallpaper-Admin' = $session } -ContentType 'application/json' -Body '{}' -TimeoutSec 3
}
# Display ticket, accounts and their order, refresh schedule, city, sensors and CPAMP connection.
function Test-SameBridgeConfig($Before, $After) {
    $Before.readToken -eq $After.readToken -and
        (ConvertTo-Json -Depth 15 $Before.accounts -Compress) -eq (ConvertTo-Json -Depth 15 $After.accounts -Compress) -and
        $Before.quotaRefreshEnabled -eq $After.quotaRefreshEnabled -and $Before.quotaRefreshMinutes -eq $After.quotaRefreshMinutes -and
        (ConvertTo-Json $Before.city -Compress) -eq (ConvertTo-Json $After.city -Compress) -and
        (ConvertTo-Json $Before.sensors -Compress) -eq (ConvertTo-Json $After.sensors -Compress) -and
        $Before.cpampUrl -eq $After.cpampUrl -and $Before.managementConfigured -eq $After.managementConfigured
}
# Wait until the expected build answers, runs elevated, keeps its configuration and has a hardware sample.
function Wait-BridgeReady([string]$BuildId, $ExpectedConfig, [string]$ExpectedPath, [int]$TimeoutSeconds = 60) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds); $lastError = 'no response'
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        try {
            $health = Invoke-RestMethod -Uri ($script:BridgeOrigin + '/v1/health') -TimeoutSec 2
            if ($health.service -ne 'portrait-wallpaper-bridge') { throw 'Bridge port belongs to another service' }
            if ($BuildId -and $health.buildId -ne $BuildId) { $lastError = 'build ' + $health.buildId; throw 'wait' }
            if (-not $health.elevated) { $lastError = 'not elevated'; throw 'wait' }
            if ($ExpectedPath) {
                $processes = @(Get-Process WallpaperBridge -ErrorAction SilentlyContinue)
                if ($processes.Count -ne 1 -or -not $processes[0].Path -or [IO.Path]::GetFullPath($processes[0].Path) -ne $ExpectedPath) { $lastError = 'unexpected bridge process path'; throw 'wait' }
            }
            $config = Read-BridgeConfig
            if ($ExpectedConfig -and -not (Test-SameBridgeConfig $ExpectedConfig $config)) { throw 'Configuration changed' }
            $snapshot = Invoke-RestMethod -Uri ($script:BridgeOrigin + '/v1/snapshot') -Headers @{ 'X-Wallpaper-Token' = $config.readToken } -TimeoutSec 2
            if ($snapshot.hardware.observedAt) { return [pscustomobject]@{ health = $health; config = $config } }
            $lastError = 'no hardware sample'
        } catch {
            if ($_.Exception.Message -in @('Configuration changed', 'Bridge port belongs to another service')) { throw }
        }
        Start-Sleep -Milliseconds 500
    }
    throw ('Bridge did not become ready: ' + $lastError)
}
function Get-WallpaperDotnet {
    $candidates = @(Join-Path $env:LOCALAPPDATA 'WallpaperDashboard\devtools\dotnet\dotnet.exe')
    # Codex's MSIX container virtualizes LocalAppData; plain shells see the SDK under the package cache.
    $packages = Join-Path $env:LOCALAPPDATA 'Packages'
    if (Test-Path -LiteralPath $packages) {
        $candidates += @(Get-ChildItem -LiteralPath $packages -Directory -Filter 'OpenAI.Codex_*' -ErrorAction SilentlyContinue | ForEach-Object { Join-Path $_.FullName 'LocalCache\Local\WallpaperDashboard\devtools\dotnet\dotnet.exe' })
    }
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { $candidates += $onPath.Source }
    foreach ($candidate in $candidates) {
        if (-not (Test-Path -LiteralPath $candidate)) { continue }
        $sdks = & $candidate --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and $sdks) { return $candidate }
    }
    throw 'No .NET SDK found. Install the .NET 10 SDK under %LOCALAPPDATA%\WallpaperDashboard\devtools\dotnet.'
}