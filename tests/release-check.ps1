$ErrorActionPreference='Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts\release-common.ps1')
$testRoot=Join-Path ([IO.Path]::GetTempPath()) ('wallpaper-release-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
function Check($Condition,[string]$Message) { if(-not $Condition){throw $Message}; Write-Output ('PASS '+$Message) }
try {
    $source=Join-Path $testRoot 'source'; $target=Join-Path $testRoot 'target'; $backups=Join-Path $testRoot 'backups'
    New-Item -ItemType Directory -Path $source,$target | Out-Null
    Set-Content -LiteralPath (Join-Path $source 'app.txt') -Value 'new'
    Set-Content -LiteralPath (Join-Path $target 'app.txt') -Value 'old'
    $oldHash=(Get-FileHash -LiteralPath (Join-Path $target 'app.txt')).Hash
    $info=@{buildId='test-build';files=@{'app.txt'=(Get-FileHash -LiteralPath (Join-Path $source 'app.txt')).Hash.ToLowerInvariant()}}
    $info | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $source 'build-info.json')
    Check ((Test-ReleasePackage $source @('app.txt')).buildId -eq 'test-build') 'candidate manifest and hashes validate'
    $failed=$false
    try { Invoke-ReleaseTransaction $source $target $backups -Verify {throw 'simulated failed startup'} | Out-Null } catch {$failed=$true}
    Check ($failed -and (Get-FileHash -LiteralPath (Join-Path $target 'app.txt')).Hash -eq $oldHash) 'failed startup restores exact previous release'
    $hooks=[Collections.Generic.List[string]]::new()
    try {
        Invoke-ReleaseTransaction $source $target $backups -BeforeSwitch {$hooks.Add('stop'); throw 'pre-switch failure'} -Recover {$hooks.Add('recover')} | Out-Null
    } catch { }
    Check (($hooks -join ',') -eq 'stop,recover' -and (Get-FileHash -LiteralPath (Join-Path $target 'app.txt')).Hash -eq $oldHash) 'pre-switch failure runs recovery without replacing the old release'
    $result=Invoke-ReleaseTransaction $source $target $backups -Verify {param($path) Test-ReleasePackage $path @('app.txt') | Out-Null}
    Check ($result.installed -and (Test-Path -LiteralPath (Join-Path $result.backup 'app.txt'))) 'normal install keeps complete rollback directory'
    $candidate='{"general":{"properties":{"old":{"value":1},"new":{"value":false}}}}' | ConvertFrom-Json
    $previous='{"workshopid":"private-id","title":"Existing","general":{"properties":{"old":{"value":83},"custom":{"value":"keep"}}}}' | ConvertFrom-Json
    $merged=Merge-WallpaperProject $candidate $previous
    Check ($merged.general.properties.old.value -eq 83 -and $merged.general.properties.new.value -eq $false -and $merged.general.properties.custom.value -eq 'keep' -and $merged.workshopid -eq 'private-id') 'project identity and properties survive migration while new defaults are added'
    Add-Content -LiteralPath (Join-Path $source 'app.txt') -Value 'tampered'
    $rejected=$false; try {Test-ReleasePackage $source | Out-Null}catch{$rejected=$true}
    Check $rejected 'tampered package is rejected before installation'
    $overlap=$false; try {Invoke-ReleaseTransaction $source (Join-Path $source 'nested') $backups | Out-Null}catch{$overlap=$true}
    Check $overlap 'overlapping release directories are rejected'
    $outside=Join-Path $testRoot 'outside'; $linked=Join-Path $testRoot 'linked'
    New-Item -ItemType Directory -Path $outside | Out-Null
    New-Item -ItemType Junction -Path $linked -Target $outside | Out-Null
    $linkedRejected=$false
    try { Invoke-ReleaseTransaction $source (Join-Path $linked 'not-created') $backups | Out-Null } catch { $linkedRejected=$true }
    Check ($linkedRejected -and -not (Test-Path -LiteralPath (Join-Path $outside 'not-created'))) 'new targets under linked parents are rejected before writing'
    # Remove the link itself before recursive cleanup, preserving its target.
    [IO.Directory]::Delete($linked)
    # Runtime protection helpers. The elevated path (owner Administrators, protected ACL) is verified by installation.
    $nestedRejected=$false; try { Get-BridgeRuntimeRoot 'E:\Codex\wallpaper-runtime' | Out-Null } catch { $nestedRejected=$true }
    $driveRejected=$false; try { Get-BridgeRuntimeRoot 'E:\' | Out-Null } catch { $driveRejected=$true }
    Check ($nestedRejected -and $driveRejected -and (Get-BridgeRuntimeRoot 'E:\WallpaperDashboard') -eq 'E:\WallpaperDashboard') 'runtime root must sit directly under a drive root'
    $userDir=Join-Path $testRoot 'user-owned'
    New-Item -ItemType Directory -Path $userDir | Out-Null
    Check ((Get-RuntimeWriteGrants $userDir).Count -gt 0) 'user-writable directories are reported'
    $locked=Join-Path $testRoot 'locked'
    New-Item -ItemType Directory -Path $locked | Out-Null
    $acl=New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true,$false)
    foreach($entry in @(@('S-1-5-18','FullControl'),@('S-1-5-32-544','FullControl'),@('S-1-5-32-545','ReadAndExecute'))){
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(([Security.Principal.SecurityIdentifier]$entry[0]),[Security.AccessControl.FileSystemRights]$entry[1],[Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit',[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow)))
    }
    Set-Acl -LiteralPath $locked -AclObject $acl
    $lockedGrants=Get-RuntimeWriteGrants $locked
    # Restore inheritance (the owner keeps WRITE_DAC) so the temporary folder can be removed.
    & icacls.exe $locked /inheritance:e /q | Out-Null
    if($LASTEXITCODE -ne 0){throw 'Could not restore test folder permissions'}
    Check ($lockedGrants.Count -eq 1 -and $lockedGrants[0] -like 'owner:*') 'read-only grants pass while a non-administrator owner is still rejected'
    $backupTest=Join-Path $testRoot 'runtime-backups'
    foreach($minutes in 1..4){
        $directory=New-Item -ItemType Directory -Path (Join-Path $backupTest ('previous-'+[Guid]::NewGuid().ToString('N')))
        Set-Content -LiteralPath (Join-Path $directory.FullName 'app.txt') -Value $minutes
        $directory.CreationTimeUtc=[DateTime]::UtcNow.AddMinutes(-$minutes)
    }
    New-Item -ItemType Directory -Path (Join-Path $backupTest 'keep-me') | Out-Null
    $removed=Remove-OldRuntimeBackups $backupTest 2
    $left=@(Get-ChildItem -LiteralPath $backupTest -Directory | ForEach-Object { if($_.Name -eq 'keep-me'){'keep-me'}else{Get-Content -LiteralPath (Join-Path $_.FullName 'app.txt')} })
    Check ($removed -eq 2 -and $left.Count -eq 3 -and $left -contains 'keep-me' -and $left -contains '1' -and $left -contains '2') 'runtime keeps the two newest rollback copies and ignores unrelated folders'
} finally {
    $resolved=[IO.Path]::GetFullPath($testRoot)
    if(-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe test cleanup'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
