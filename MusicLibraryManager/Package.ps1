[CmdletBinding()]
param(
    [string]$Version = "0.1.0",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string[]]$Rids,
    [string]$OutputRoot,
    [string]$SyncerRuntimeRoot,
    [switch]$Installers,
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"

function Write-ArtifactChecksum([string]$Artifact) {
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $Artifact).Hash.ToLowerInvariant()
    $checksum = "$Artifact.sha256"
    Set-Content -LiteralPath $checksum -Value "$hash  $([IO.Path]::GetFileName($Artifact))" -Encoding ascii
}

function Assert-SatelliteResources([string]$PublishRoot) {
    $cultures = @(
        "de-DE", "es-ES", "fr-FR", "it-IT", "pt-BR",
        "ja-JP", "ko-KR", "zh-CN", "zh-TW"
    )
    foreach ($culture in $cultures) {
        $satellite = Join-Path $PublishRoot "$culture/MusicLibraryManager.Presentation.resources.dll"
        if (-not (Test-Path -LiteralPath $satellite -PathType Leaf)) {
            throw "The publish output is missing the $culture presentation satellite assembly: $satellite"
        }
    }
    Write-Host "Verified all $($cultures.Count) presentation satellite assemblies."
}

function Assert-ThirdPartyLicenses([string]$PublishRoot) {
    $licenses = @(
        "AvaloniaUI-12.1.0-MIT.txt",
        "SixLabors.ImageSharp-3.1.12-LICENSE.txt"
    )
    foreach ($license in $licenses) {
        $licensePath = Join-Path $PublishRoot "ThirdPartyLicenses/$license"
        if (-not (Test-Path -LiteralPath $licensePath -PathType Leaf)) {
            throw "The publish output is missing the third-party license agreement: $licensePath"
        }
    }
    Write-Host "Verified all $($licenses.Count) third-party license agreements."
}

function Resolve-InnoCompiler {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
    $localPrograms = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) "Programs"
    $candidates = @(
        (Join-Path $localPrograms "Inno Setup 7/ISCC.exe"),
        (Join-Path $localPrograms "Inno Setup 6/ISCC.exe"),
        (Join-Path $programFilesX86 "Inno Setup 7/ISCC.exe"),
        (Join-Path $programFilesX86 "Inno Setup 6/ISCC.exe")
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    throw "Inno Setup Compiler (ISCC.exe) is required to build the Windows installer."
}

function New-WindowsInstaller(
    [string]$Version,
    [string]$PublishRoot,
    [string]$OutputRoot,
    [string]$ProductName
) {
    $compiler = Resolve-InnoCompiler
    $script = Join-Path $PSScriptRoot "Installer.iss"
    & $compiler "/DAppVersion=$Version" "/DPublishRoot=$PublishRoot" "/DOutputDir=$OutputRoot" $script |
        ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE." }
    $installer = Join-Path $OutputRoot "$ProductName-setup.exe"
    if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
        throw "Inno Setup did not produce the expected installer: $installer"
    }
    return $installer
}

function New-DebianPackage(
    [string]$Version,
    [string]$PublishRoot,
    [string]$PackageRoot,
    [string]$OutputRoot,
    [string]$ProductName
) {
    if ($Version -notmatch '^[0-9][0-9A-Za-z.+:~-]*$') {
        throw "Version '$Version' is not valid for a Debian package."
    }
    if (-not (Get-Command dpkg-deb -ErrorAction SilentlyContinue)) {
        throw "dpkg-deb is required to build the Linux installer."
    }

    $debRoot = Join-Path $PackageRoot "deb"
    $controlRoot = Join-Path $debRoot "DEBIAN"
    $applicationRoot = Join-Path $debRoot "opt/musiclibrarymanager"
    $launcherRoot = Join-Path $debRoot "usr/bin"
    $desktopRoot = Join-Path $debRoot "usr/share/applications"
    $iconRoot = Join-Path $debRoot "usr/share/pixmaps"
    New-Item -ItemType Directory -Force -Path $controlRoot, $applicationRoot, $launcherRoot, $desktopRoot, $iconRoot | Out-Null
    Copy-Item -Path (Join-Path $PublishRoot "*") -Destination $applicationRoot -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Assets/AppIcon.ico") `
        -Destination (Join-Path $iconRoot "musiclibrarymanager.ico") -Force

    $executable = Join-Path $applicationRoot "MusicLibraryManager"
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "The linux-x64 publish output does not contain MusicLibraryManager."
    }
    & chmod -R "u=rwX,go=rX" $debRoot
    if ($LASTEXITCODE -ne 0) { throw "chmod failed while preparing the Debian package." }
    & chmod "755" $executable
    if ($LASTEXITCODE -ne 0) { throw "chmod failed for the MusicLibraryManager executable." }
    & ln -s "/opt/musiclibrarymanager/MusicLibraryManager" (Join-Path $launcherRoot "musiclibrarymanager")
    if ($LASTEXITCODE -ne 0) { throw "Could not create the Debian command launcher." }

    $desktop = @"
[Desktop Entry]
Type=Application
Name=Music Library Manager
Comment=Manage and analyze music libraries
Exec=/usr/bin/musiclibrarymanager
Icon=/usr/share/pixmaps/musiclibrarymanager.ico
Terminal=false
Categories=AudioVideo;Audio;Utility;
StartupWMClass=MusicLibraryManager
"@ -replace "`r`n", "`n"
    [IO.File]::WriteAllText((Join-Path $desktopRoot "musiclibrarymanager.desktop"),
        $desktop + "`n", [Text.UTF8Encoding]::new($false))

    $installedBytes = (Get-ChildItem -LiteralPath $debRoot -File -Recurse |
        Where-Object { $_.FullName -notlike "$controlRoot*" } |
        Measure-Object -Property Length -Sum).Sum
    $installedSize = [Math]::Max(1, [Math]::Ceiling($installedBytes / 1KB))
    $control = @"
Package: musiclibrarymanager
Version: $Version
Section: sound
Priority: optional
Architecture: amd64
Installed-Size: $installedSize
Maintainer: MusicLibraryTools <colinphill@users.noreply.github.com>
Depends: libfontconfig1, libice6, libsm6, libx11-6
Homepage: https://github.com/colinphill/MusicLibraryTools
Description: Manage, inspect, and maintain music libraries
 Music Library Manager is a native desktop application for indexing,
 analyzing, repairing, and synchronizing music collections.
"@ -replace "`r`n", "`n"
    [IO.File]::WriteAllText((Join-Path $controlRoot "control"),
        $control + "`n", [Text.UTF8Encoding]::new($false))

    $package = Join-Path $OutputRoot "$ProductName.deb"
    if (Test-Path -LiteralPath $package) { Remove-Item -LiteralPath $package -Force }
    & dpkg-deb --root-owner-group --build $debRoot $package | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "dpkg-deb failed with exit code $LASTEXITCODE." }
    & dpkg-deb --info $package | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "The generated Debian package failed validation." }
    return $package
}

function New-MacDiskImage(
    [string]$Bundle,
    [string]$PackageRoot,
    [string]$OutputRoot,
    [string]$ProductName
) {
    if (-not (Get-Command hdiutil -ErrorAction SilentlyContinue)) {
        throw "hdiutil is required to build the macOS installer."
    }
    $staging = Join-Path $PackageRoot "dmg"
    New-Item -ItemType Directory -Force -Path $staging | Out-Null
    Copy-Item -LiteralPath $Bundle -Destination (Join-Path $staging "MusicLibraryManager.app") -Recurse -Force
    & ln -s "/Applications" (Join-Path $staging "Applications")
    if ($LASTEXITCODE -ne 0) { throw "Could not create the Applications link for the macOS disk image." }

    $image = Join-Path $OutputRoot "$ProductName.dmg"
    if (Test-Path -LiteralPath $image) { Remove-Item -LiteralPath $image -Force }
    & hdiutil create -volname "Music Library Manager" -srcfolder $staging -anyowners -ov -format UDZO $image |
        ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "hdiutil failed with exit code $LASTEXITCODE." }
    & hdiutil verify $image | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "The generated macOS disk image failed validation." }
    return $image
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $PSScriptRoot "MusicLibraryManager.csproj"
$syncerVerifierProject = Join-Path $projectRoot "BuildTools/SyncerResourceVerifier/SyncerResourceVerifier.csproj"
$localizationCatalogProject = Join-Path $projectRoot "BuildTools/LocalizationCatalogGenerator/LocalizationCatalogGenerator.csproj"
& dotnet build $localizationCatalogProject --configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw "Localization catalog validator build failed with exit code $LASTEXITCODE." }
$localizationCatalogValidator = Join-Path $projectRoot "BuildTools/LocalizationCatalogGenerator/bin/$Configuration/net10.0/LocalizationCatalogGenerator.dll"
& dotnet $localizationCatalogValidator --check
if ($LASTEXITCODE -ne 0) { throw "Shipping localization catalogs failed deterministic validation." }
& dotnet build $syncerVerifierProject --configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw "Syncer resource verifier build failed with exit code $LASTEXITCODE." }
$syncerVerifier = Join-Path $projectRoot "BuildTools/SyncerResourceVerifier/bin/$Configuration/net10.0/SyncerResourceVerifier.dll"
if ([string]::IsNullOrWhiteSpace($SyncerRuntimeRoot)) {
    $SyncerRuntimeRoot = Join-Path $projectRoot "syncer/out/package/syncer-Release"
}
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $projectRoot ".artifacts/music-library-manager"
}
$isWindowsPlatform = $PSVersionTable.PSEdition -eq "Desktop" -or $IsWindows
$isMacPlatform = -not $isWindowsPlatform -and $IsMacOS
if (-not $Rids -or $Rids.Count -eq 0) {
    $architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    $Rids = if ($isWindowsPlatform) { @("win-$architecture") } elseif ($isMacPlatform) { @("osx-$architecture") } else { @("linux-$architecture") }
}
$Rids = @($Rids | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })

$resolvedOutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$resolvedProjectRoot = [System.IO.Path]::GetFullPath($projectRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
$pathComparison = if ($isWindowsPlatform) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
$repositoryPrefix = $resolvedProjectRoot + [IO.Path]::DirectorySeparatorChar
if (-not $resolvedOutputRoot.StartsWith($repositoryPrefix, $pathComparison)) {
    throw "OutputRoot must stay inside the repository: $resolvedOutputRoot"
}
New-Item -ItemType Directory -Force -Path $resolvedOutputRoot | Out-Null

foreach ($rid in $Rids) {
    $packageRoot = Join-Path $resolvedOutputRoot "$Version/$rid"
    $publishRoot = Join-Path $packageRoot "publish"
    if (Test-Path -LiteralPath $packageRoot) {
        Remove-Item -LiteralPath $packageRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null

    $ridSyncerRoot = Join-Path $SyncerRuntimeRoot $rid
    if (-not (Test-Path -LiteralPath $ridSyncerRoot -PathType Container)) {
        $ridSyncerRoot = $SyncerRuntimeRoot
    }
    $syncerServers = [IO.Path]::GetFullPath((Join-Path $ridSyncerRoot "servers"))
    if (-not (Test-Path -LiteralPath $syncerServers -PathType Container)) {
        throw "Syncer Android servers for $rid were not found under $ridSyncerRoot. Build syncer first or pass -SyncerRuntimeRoot."
    }
    foreach ($abi in @('arm64-v8a', 'armeabi-v7a', 'x86_64', 'x86')) {
        $server = Join-Path $syncerServers "$abi/syncerd"
        if (-not (Test-Path -LiteralPath $server -PathType Leaf)) {
            throw "The Syncer Android server set is incomplete. Missing: $server"
        }
    }

    $arguments = @(
        "publish", $project, "--configuration", $Configuration, "--runtime", $rid,
        "--self-contained", "true", "--output", $publishRoot, "/p:Version=$Version",
        "/p:DebugType=None", "/p:DebugSymbols=false", "/p:SyncerServerRoot=$syncerServers"
    )
    if ($NoRestore) { $arguments += "--no-restore" }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid" }

    $syncerAssembly = Join-Path $publishRoot "Syncer.Client.dll"
    $dependencyManifest = Join-Path $publishRoot "MusicLibraryManager.deps.json"
    if (-not (Test-Path -LiteralPath $syncerAssembly -PathType Leaf) -or
        -not (Test-Path -LiteralPath $dependencyManifest -PathType Leaf) -or
        -not (Select-String -LiteralPath $dependencyManifest -SimpleMatch 'Syncer.Client/' -Quiet)) {
        throw "The published app does not contain a resolvable Syncer.Client dependency. Restore the current project graph and publish again."
    }
    & dotnet $syncerVerifier $syncerAssembly $syncerServers
    if ($LASTEXITCODE -ne 0) { throw "The published Syncer.Client Android resources failed validation." }
    Assert-SatelliteResources $publishRoot
    Assert-ThirdPartyLicenses $publishRoot

    $productName = "MusicLibraryManager-$Version-$rid"
    $artifacts = [Collections.Generic.List[string]]::new()
    if ($rid.StartsWith("win-", [StringComparison]::OrdinalIgnoreCase)) {
        $archive = Join-Path $resolvedOutputRoot "$productName.zip"
        if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
        Compress-Archive -Path (Join-Path $publishRoot "*") -DestinationPath $archive -CompressionLevel Optimal
        [void]$artifacts.Add($archive)
        if ($Installers) {
            if (-not $isWindowsPlatform -or $rid -ne "win-x64") {
                throw "Windows installer generation supports win-x64 on Windows only."
            }
            [void]$artifacts.Add((New-WindowsInstaller $Version $publishRoot $resolvedOutputRoot $productName))
        }
    }
    elseif ($rid.StartsWith("osx-", [StringComparison]::OrdinalIgnoreCase)) {
        $bundle = Join-Path $packageRoot "MusicLibraryManager.app"
        $macOs = Join-Path $bundle "Contents/MacOS"
        New-Item -ItemType Directory -Force -Path $macOs | Out-Null
        Copy-Item -Path (Join-Path $publishRoot "*") -Destination $macOs -Recurse -Force
        $bundleVersion = [regex]::Match($Version, '\d+(\.\d+){0,2}').Value
        if ([string]::IsNullOrWhiteSpace($bundleVersion)) { $bundleVersion = "1.0.0" }
        $plist = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "https://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleExecutable</key><string>MusicLibraryManager</string>
  <key>CFBundleIdentifier</key><string>com.musiclibrarytools.manager</string>
  <key>CFBundleName</key><string>Music Library Manager</string>
  <key>CFBundleDisplayName</key><string>Music Library Manager</string>
  <key>CFBundleVersion</key><string>$bundleVersion</string>
  <key>CFBundleShortVersionString</key><string>$bundleVersion</string>
  <key>NSHighResolutionCapable</key><true/>
</dict></plist>
"@
        [IO.File]::WriteAllText((Join-Path $bundle "Contents/Info.plist"), $plist, [Text.UTF8Encoding]::new($false))
        & chmod "755" (Join-Path $macOs "MusicLibraryManager")
        if ($LASTEXITCODE -ne 0) { throw "chmod failed for the macOS application executable." }
        $archive = Join-Path $resolvedOutputRoot "$productName.tar.gz"
        if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
        & tar -czf $archive -C $packageRoot "MusicLibraryManager.app"
        if ($LASTEXITCODE -ne 0) { throw "tar failed for $rid" }
        [void]$artifacts.Add($archive)
        if ($Installers) {
            if (-not $isMacPlatform) { throw "macOS disk images can only be built on macOS." }
            [void]$artifacts.Add((New-MacDiskImage $bundle $packageRoot $resolvedOutputRoot $productName))
        }
    }
    else {
        $archive = Join-Path $resolvedOutputRoot "$productName.tar.gz"
        if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
        & tar -czf $archive -C $publishRoot .
        if ($LASTEXITCODE -ne 0) { throw "tar failed for $rid" }
        [void]$artifacts.Add($archive)
        if ($Installers) {
            if ($isWindowsPlatform -or $isMacPlatform -or $rid -ne "linux-x64") {
                throw "Debian package generation supports linux-x64 on Linux only."
            }
            [void]$artifacts.Add((New-DebianPackage $Version $publishRoot $packageRoot $resolvedOutputRoot $productName))
        }
    }

    foreach ($artifact in $artifacts) {
        Write-ArtifactChecksum $artifact
        Write-Host "Created $artifact"
    }
}
