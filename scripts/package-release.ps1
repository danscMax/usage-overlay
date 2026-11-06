param(
    [string]$Version = (Get-Content -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'VERSION') -Raw).Trim(),
    [string]$ArtifactsRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts')
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

if ($Version -notmatch '^\d+\.\d+\.\d+(?:[-.][0-9A-Za-z.-]+)?$') {
    throw "Invalid semantic version: $Version"
}

$projectRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifactsRoot = [System.IO.Path]::GetFullPath($ArtifactsRoot)
$allowedRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
if ($artifactsRoot -ne $allowedRoot -and -not $artifactsRoot.StartsWith($allowedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Release output must remain inside this checkout artifacts directory.'
}
$workRoot = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot 'release-work'))
$publishDirectory = Join-Path $workRoot 'publish'
$packageDirectory = Join-Path $workRoot 'UsageOverlay'
$releaseDirectory = Join-Path $artifactsRoot 'release'
$overlayProject = Join-Path $projectRoot 'src\UsageOverlay\UsageOverlay.csproj'

function Reset-ReleaseDirectory {
    param([Parameter(Mandatory)][string]$Path)

    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    $requiredPrefix = $artifactsRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPath.StartsWith($requiredPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to reset a directory outside artifacts: $resolvedPath"
    }

    if (Test-Path -LiteralPath $resolvedPath) {
        Remove-Item -LiteralPath $resolvedPath -Recurse -Force
    }
    [void](New-Item -ItemType Directory -Path $resolvedPath -Force)
}

& (Join-Path $PSScriptRoot 'build.ps1')
Reset-ReleaseDirectory -Path $workRoot
Reset-ReleaseDirectory -Path $releaseDirectory
[void](New-Item -ItemType Directory -Path $publishDirectory -Force)
[void](New-Item -ItemType Directory -Path $packageDirectory -Force)
[void](New-Item -ItemType Directory -Path (Join-Path $packageDirectory 'scripts') -Force)

dotnet publish $overlayProject `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $publishDirectory `
    "-p:Version=$Version" `
    '-p:PublishSingleFile=true' `
    '-p:PublishTrimmed=false' `
    '-p:IncludeNativeLibrariesForSelfExtract=true' `
    '-p:DebugType=None' `
    '-p:DebugSymbols=false'

Copy-Item -LiteralPath (Join-Path $publishDirectory 'UsageOverlay.exe') -Destination $packageDirectory
foreach ($document in @(
    'README.md',
    'LICENSE',
    'THIRD-PARTY-NOTICES.md',
    'RELEASE_NOTES.md',
    'CHANGELOG.md',
    'SECURITY.md',
    'CONTRIBUTING.md',
    'CODE_OF_CONDUCT.md',
    'SUPPORT.md',
    'VERSION'
)) {
    Copy-Item -LiteralPath (Join-Path $projectRoot $document) -Destination $packageDirectory
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'assets') -Destination $packageDirectory -Recurse
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $packageDirectory -Recurse
Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses') -Destination $packageDirectory -Recurse
$assets = Get-Content -LiteralPath (Join-Path $projectRoot 'src\UsageOverlay\obj\project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
$packagesRoot = $assets.packageFolders.Keys | Select-Object -First 1
$runtimeNotices = Join-Path $packageDirectory 'licenses\runtime'
[void](New-Item -ItemType Directory -Path $runtimeNotices -Force)
$packagePaths = @($assets.libraries.Values | Where-Object type -eq 'package' | ForEach-Object path)
# Runtime packs are restored separately from project library references.
foreach ($pack in @('microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64')) {
    $download = $assets.project.frameworks.Values | ForEach-Object { $_.downloadDependencies } | Where-Object name -eq $pack | Select-Object -First 1
    if (-not $download) { throw "Restored runtime pack was not found: $pack" }
    $runtimeVersion = $download.version.TrimStart('[').Split(',')[0].Trim()
    $packagePaths += "$pack/$runtimeVersion"
}
foreach ($packagePath in $packagePaths) {
    $source = Join-Path $packagesRoot $packagePath
    $notices = @(Get-ChildItem -LiteralPath $source -File | Where-Object Name -Match '^(LICENSE|NOTICE|THIRD.PARTY)|\.nuspec$')
    if ($notices.Count -gt 0) {
        $destination = Join-Path $runtimeNotices ($packagePath -replace '[/\\]', '-')
        [void](New-Item -ItemType Directory -Path $destination -Force)
        $notices | Copy-Item -Destination $destination
    }
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'src\UsageOverlay\Assets\UsageOverlay.ico') `
    -Destination (Join-Path $packageDirectory 'assets\UsageOverlay.ico')
foreach ($script in @(
    'install-start-menu.ps1',
    'uninstall-start-menu.ps1',
    'install-startup.ps1',
    'uninstall-startup.ps1'
)) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $script) -Destination (Join-Path $packageDirectory 'scripts')
}

$archiveName = "usage-overlay-v$Version-win-x64.zip"
$archivePath = Join-Path $releaseDirectory $archiveName
if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}
Compress-Archive -Path $packageDirectory -DestinationPath $archivePath -CompressionLevel Optimal

& (Join-Path $PSScriptRoot 'build-installer.ps1') `
    -Version $Version `
    -PackageDirectory $packageDirectory `
    -OutputDirectory $releaseDirectory

$installerName = "usage-overlay-v$Version-win-x64-setup.exe"
$installerPath = Join-Path $releaseDirectory $installerName
$archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
$installerHash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumPath = Join-Path $releaseDirectory 'SHA256SUMS.txt'
@(
    "$installerHash  $installerName",
    "$archiveHash  $archiveName"
) | Set-Content -LiteralPath $checksumPath -Encoding ascii

$manifest = [ordered]@{
    product = 'Usage Overlay'
    publisher = 'danscMax'
    repository = 'https://github.com/danscMax/usage-overlay'
    version = $Version
    runtime = 'win-x64'
    selfContained = $true
    installer = $installerName
    installerSha256 = $installerHash
    portableArchive = $archiveName
    portableArchiveSha256 = $archiveHash
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $releaseDirectory 'release-manifest.json') -Encoding utf8

Write-Host "Release package: $archivePath"
Write-Host "Installer: $installerPath"
Write-Host "Installer SHA-256: $installerHash"
Write-Host "Portable SHA-256: $archiveHash"
