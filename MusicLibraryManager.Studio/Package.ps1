[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string] $Version = '1.0.0',

    [ValidateSet('win-x64')]
    [string] $RuntimeIdentifier = 'win-x64',

    [string] $OutputDirectory,

    [switch] $NoRestore
)

$ErrorActionPreference = 'Stop'

$projectDirectory = $PSScriptRoot
$repositoryDirectory = Split-Path -Parent $projectDirectory
$projectPath = Join-Path $projectDirectory 'MusicLibraryManager.Studio.csproj'

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryDirectory '.artifacts\studio'
}
elseif (-not [IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryDirectory $OutputDirectory
}

$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$bundleName = "MusicLibraryManager.Studio-$RuntimeIdentifier"
$bundleDirectory = Join-Path $OutputDirectory $bundleName
$archivePath = Join-Path $OutputDirectory "$bundleName-$Version.zip"
$checksumPath = "$archivePath.sha256"

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

# Publishing does not remove stale files. Only clean the script-owned bundle and artifacts.
if (Test-Path -LiteralPath $bundleDirectory) {
    Remove-Item -LiteralPath $bundleDirectory -Recurse -Force
}
foreach ($artifactPath in @($archivePath, $checksumPath)) {
    if (Test-Path -LiteralPath $artifactPath) {
        Remove-Item -LiteralPath $artifactPath -Force
    }
}

$publishArguments = @(
    'publish',
    $projectPath,
    '--configuration', 'Release',
    '--runtime', $RuntimeIdentifier,
    '--self-contained', 'true',
    '--output', $bundleDirectory,
    "-p:Version=$Version",
    '-p:PublishSingleFile=false',
    '-p:PublishTrimmed=false',
    '-p:DebugType=None',
    '-p:DebugSymbols=false'
)
if ($NoRestore) {
    $publishArguments += '--no-restore'
}

& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$executablePath = Join-Path $bundleDirectory 'MusicLibraryManager.Studio.exe'
if (-not (Test-Path -LiteralPath $executablePath)) {
    throw "The published bundle does not contain $executablePath."
}

Compress-Archive -LiteralPath $bundleDirectory -DestinationPath $archivePath -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash *$(Split-Path -Leaf $archivePath)" | Set-Content -LiteralPath $checksumPath -Encoding ascii

Write-Host ''
Write-Host 'Studio package created:'
Write-Host "  Bundle:   $bundleDirectory"
Write-Host "  Archive:  $archivePath"
Write-Host "  SHA-256:  $checksumPath"
