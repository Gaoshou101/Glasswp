param([Parameter(Mandatory=$true)][string]$Version, [string]$ExpectedCommit, [switch]$CheckInstaller)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw 'Invalid release version' }
$repoRoot = Split-Path -Parent $PSScriptRoot
$name = 'Glasswp-Bridge-v'+$Version+'-win-x64'
$zipPath = Join-Path $repoRoot ('artifacts\packages\'+$name+'.zip')
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksum = (Get-Content -LiteralPath ($zipPath+'.sha256') -Raw).Trim()
if ($checksum -ne ($hash+'  '+$name+'.zip')) { throw 'Archive checksum or checksum filename does not match' }
$testRoot = Join-Path $repoRoot ('artifacts\validation\release-archive-'+[Guid]::NewGuid().ToString('N'))
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $testRoot)
    $bundle = Join-Path $testRoot $name
    $payload = Join-Path $bundle 'payload'
    $info = Test-ReleasePackage $payload @('WallpaperBridge.exe','WallpaperBridge.dll','run-bridge.ps1','wallpaper/index.html','wallpaper/project.json')
    if ($info.version -ne $Version) { throw 'Archive manifest version differs from release' }
    if ($ExpectedCommit -and ($info.commit -ne $ExpectedCommit -or $info.dirty)) { throw 'Release must come from the expected clean source commit' }
    $assembly = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $payload 'WallpaperBridge.dll'))
    if ($assembly.FileVersion -ne ($Version+'.0')) { throw 'Bridge binary version differs from release' }
    foreach ($relative in @('install-bridge.cmd','scripts\install-bridge.ps1','scripts\update-bridge.ps1','scripts\release-common.ps1','scripts\install-bridge-autostart.ps1','scripts\start-bridge.ps1','scripts\stop-bridge.ps1')) {
        if ((Get-FileHash -LiteralPath (Join-Path $bundle $relative)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $repoRoot $relative)).Hash) { throw ('Archive installer differs from source: '+$relative) }
    }
    $project = Get-Content -LiteralPath (Join-Path $payload 'wallpaper\project.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($project.general.properties.bridge_token.value) { throw 'Archive contains a paired wallpaper' }
    if ($CheckInstaller) {
        # This preflight never creates the runtime or modifies scheduled tasks. Use the
        # runner's actual drive; GitHub-hosted machines do not have our local E: drive.
        $runtime = Join-Path ([IO.Path]::GetPathRoot($repoRoot)) 'GlasswpReleasePreflight'
        & (Join-Path $bundle 'install-bridge.cmd') -Check -NoAutostart -RuntimeRoot $runtime
        if ($LASTEXITCODE -ne 0) { throw 'Extracted installer preflight failed' }
    }
    @{ version=$Version; buildId=$info.buildId; commit=$info.commit; cleanSource=(-not $info.dirty); sha256=$hash; packageBytes=(Get-Item $zipPath).Length; manifestVerified=$true; binaryVersionVerified=$true; installerMatchesSource=$true; installerPreflight=[bool]$CheckInstaller } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $repoRoot 'artifacts\validation\release-artifact-check.json') -Encoding UTF8
    Write-Output ('PASS release archive '+$name+'; binary version, payload hashes and installer scripts verified')
} finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $allowed = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\validation'))+'\'
    if (-not $resolved.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe archive verification cleanup' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
