param(
    [string]$WallpaperEnginePath = 'F:\SteamLibrary\steamapps\common\wallpaper_engine',
    [string]$Destination,
    [switch]$Apply,
    [int]$Monitor = -1,
    [string]$Location,
    [switch]$PreviewWindow
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
$repoRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repoRoot 'dist'
$info = Test-ReleasePackage $source @('index.html','wallpaper.js','wallpaper.css','project.json')
$weExe = Join-Path $WallpaperEnginePath 'wallpaper32.exe'
if (-not (Test-Path -LiteralPath $weExe)) { throw 'Wallpaper Engine executable not found' }
if ($Apply -and $Monitor -lt 0 -and -not $Location) { throw 'Applying a wallpaper requires a verified -Monitor or -Location' }
if ($Location -and $Monitor -ge 0) { throw 'Choose one verified display selector' }
$selector=if($Location){@('-location',$Location)}else{@('-monitor',[string]$Monitor)}
if (-not $Destination) { $Destination=Join-Path $WallpaperEnginePath 'projects\myprojects\portrait-dashboard' }
$Destination=Get-ReleasePath $Destination
$allowedRoot=(Get-ReleasePath (Join-Path $WallpaperEnginePath 'projects\myprojects'))+'\'
if (-not $Destination.StartsWith($allowedRoot,[StringComparison]::OrdinalIgnoreCase)) { throw 'Destination must be a local Wallpaper Engine project under myprojects' }
$previous=$null
if (Test-Path -LiteralPath (Join-Path $Destination 'project.json')) { $previous=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $Destination 'project.json') | ConvertFrom-Json }
$displayToken=$null
try {
    $origin='http://127.0.0.1:17632'
    $session=(Invoke-RestMethod -Uri ($origin+'/v1/setup/session') -Method Post -Headers @{Origin=$origin;'X-Wallpaper-Setup'='1'} -ContentType 'application/json' -Body '{}' -TimeoutSec 3).session
    $cfg=Invoke-RestMethod -Uri ($origin+'/v1/setup/config/read') -Method Post -Headers @{Origin=$origin;'X-Wallpaper-Admin'=$session} -ContentType 'application/json' -Body '{}' -TimeoutSec 3
    $displayToken=$cfg.readToken
} catch { }
if (-not $displayToken -and $previous) { $displayToken=$previous.general.properties.bridge_token.value }
if (-not $displayToken) { throw 'Start the local bridge before installing a paired wallpaper' }
$result=Invoke-ReleaseTransaction -Source $source -Destination $Destination -BackupRoot (Join-Path $WallpaperEnginePath 'projects\dashboard-backups') -Prepare {
    param($candidatePath)
    $candidate=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $candidatePath 'project.json') | ConvertFrom-Json
    $merged=Merge-WallpaperProject $candidate $previous
    $merged.general.properties.bridge_token.value=$displayToken
    $merged | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $candidatePath 'project.json') -Encoding UTF8
    # Local pairing intentionally changes project.json; regenerate only this installed copy's checksum.
    $localInfo=Get-Content -Raw -LiteralPath (Join-Path $candidatePath 'build-info.json') | ConvertFrom-Json
    $localInfo.files.'project.json'=(Get-FileHash -LiteralPath (Join-Path $candidatePath 'project.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    $localInfo | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $candidatePath 'build-info.json') -Encoding UTF8
    Test-ReleasePackage $candidatePath @('project.json') | Out-Null
} -BeforeSwitch {
    if ($Apply) { & $weExe -control closeWallpaper @selector; if ($LASTEXITCODE -ne 0) { throw 'Wallpaper close failed' } }
} -Verify {
    param($installed)
    Test-ReleasePackage $installed @('index.html','wallpaper.js','project.json') | Out-Null
    if ($Apply) { & $weExe -control openWallpaper -file (Join-Path $installed 'index.html') @selector; if ($LASTEXITCODE -ne 0) { throw 'Wallpaper open failed' } }
} -BeforeRollback {
    if ($Apply) { & $weExe -control closeWallpaper @selector }
} -Recover {
    if ($Apply -and $previous) { & $weExe -control openWallpaper -file (Join-Path $Destination 'index.html') @selector }
}
Write-Output ('Installed wallpaper build '+$info.buildId+'; backup: '+$result.backup)
$result | Add-Member -NotePropertyName buildId -NotePropertyValue $info.buildId
$result | Add-Member -NotePropertyName physicalInputVerified -NotePropertyValue $false
$reportPath=Join-Path $repoRoot 'artifacts\validation\wallpaper-update.json'
New-Item -ItemType Directory -Path (Split-Path -Parent $reportPath) -Force | Out-Null
$result | ConvertTo-Json | Set-Content -LiteralPath $reportPath -Encoding UTF8
if ($PreviewWindow) { & $weExe -control openWallpaper -file (Join-Path $Destination 'project.json') -playInWindow 'Portrait Dashboard Preview' -width 800 -height 1280 }
