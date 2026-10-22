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
