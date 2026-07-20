[CmdletBinding()]
param(
    [string]$Version = "0.1.0",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string[]]$Rids,
    [string]$OutputRoot,
    [string]$SyncerRuntimeRoot,
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $PSScriptRoot "MusicLibraryManager.csproj"
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

    $arguments = @(
        "publish", $project, "--configuration", $Configuration, "--runtime", $rid,
        "--self-contained", "true", "--output", $publishRoot, "/p:Version=$Version",
        "/p:DebugType=None", "/p:DebugSymbols=false"
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

    $ridSyncerRoot = Join-Path $SyncerRuntimeRoot $rid
    if (-not (Test-Path -LiteralPath $ridSyncerRoot -PathType Container)) {
        $ridSyncerRoot = $SyncerRuntimeRoot
    }
    $syncerServers = Join-Path $ridSyncerRoot "servers"
    if (-not (Test-Path -LiteralPath $syncerServers -PathType Container)) {
        throw "Syncer Android servers for $rid were not found under $ridSyncerRoot. Build syncer first or pass -SyncerRuntimeRoot."
    }
    foreach ($abi in @('arm64-v8a', 'armeabi-v7a', 'x86_64', 'x86')) {
        $server = Join-Path $syncerServers "$abi/syncerd"
        if (-not (Test-Path -LiteralPath $server -PathType Leaf)) {
            throw "The Syncer Android server set is incomplete. Missing: $server"
        }
    }
    $syncerPublishRoot = Join-Path $publishRoot "tools/syncer"
    New-Item -ItemType Directory -Force -Path $syncerPublishRoot | Out-Null
    Copy-Item -LiteralPath $syncerServers -Destination $syncerPublishRoot -Recurse -Force

    $productName = "MusicLibraryManager-$Version-$rid"
    if ($rid.StartsWith("win-", [StringComparison]::OrdinalIgnoreCase)) {
        $archive = Join-Path $resolvedOutputRoot "$productName.zip"
        if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
        Compress-Archive -Path (Join-Path $publishRoot "*") -DestinationPath $archive -CompressionLevel Optimal
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
        $archive = Join-Path $resolvedOutputRoot "$productName.tar.gz"
        if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
        & tar -czf $archive -C $packageRoot "MusicLibraryManager.app"
        if ($LASTEXITCODE -ne 0) { throw "tar failed for $rid" }
    }
    else {
        $archive = Join-Path $resolvedOutputRoot "$productName.tar.gz"
        if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
        & tar -czf $archive -C $publishRoot .
        if ($LASTEXITCODE -ne 0) { throw "tar failed for $rid" }
    }

    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $archive).Hash.ToLowerInvariant()
    Set-Content -LiteralPath "$archive.sha256" -Value "$hash  $([IO.Path]::GetFileName($archive))" -Encoding ascii
    Write-Host "Created $archive"
}
