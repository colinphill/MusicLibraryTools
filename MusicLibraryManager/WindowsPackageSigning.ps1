[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet("CreateCatalog", "PrepareSignedUninstaller", "RebuildArtifacts", "Verify")]
    [string]$Operation,
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:[-.][0-9A-Za-z.-]+)?$')]
    [string]$Version,
    [string]$OutputRoot,
    [switch]$IncludeUninstaller,
    [switch]$IncludeInstaller,
    [switch]$WriteChecksums
)

$ErrorActionPreference = "Stop"

$firstPartyPayload = @(
    "MusicLibraryManager.exe",
    "MusicLibraryManager.dll",
    "MusicLibraryManager.Presentation.dll",
    "MusicLibrary.Core.dll",
    "MusicFileUtilities.dll",
    "MetadataCaching.dll",
    "ITLTools.dll",
    "Syncer.Client.dll"
)
$satelliteCultures = @(
    "de-DE", "es-ES", "fr-FR", "it-IT", "pt-BR",
    "ja-JP", "ko-KR", "zh-CN", "zh-TW"
)

function Get-RelativeFirstPartyPayload {
    $relativePayload = [Collections.Generic.List[string]]::new()
    foreach ($fileName in $firstPartyPayload) {
        [void]$relativePayload.Add($fileName)
    }
    foreach ($culture in $satelliteCultures) {
        [void]$relativePayload.Add(
            (Join-Path $culture "MusicLibraryManager.Presentation.resources.dll"))
    }
    return $relativePayload.ToArray()
}

function Assert-CatalogCoverage([string[]]$Entries) {
    $expectedEntries = @(
        Get-RelativeFirstPartyPayload |
            ForEach-Object { Join-Path "publish" $_ }
    )
    $expectedSet = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $expectedEntries) {
        [void]$expectedSet.Add($entry)
    }

    $actualSet = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $duplicates = [Collections.Generic.List[string]]::new()
    foreach ($entry in $Entries) {
        if (-not $actualSet.Add($entry)) {
            [void]$duplicates.Add($entry)
        }
    }

    $missing = @($expectedEntries | Where-Object { -not $actualSet.Contains($_) })
    $unexpected = @($Entries | Where-Object { -not $expectedSet.Contains($_) })
    if ($duplicates.Count -gt 0 -or $missing.Count -gt 0 -or $unexpected.Count -gt 0) {
        $details = [Collections.Generic.List[string]]::new()
        if ($missing.Count -gt 0) {
            [void]$details.Add("missing: $($missing -join ', ')")
        }
        if ($unexpected.Count -gt 0) {
            [void]$details.Add("unexpected: $($unexpected -join ', ')")
        }
        if ($duplicates.Count -gt 0) {
            [void]$details.Add("duplicate: $($duplicates -join ', ')")
        }
        throw "The Authenticode catalog does not exactly cover the current first-party payload ($($details -join '; ')). Recreate it before signing."
    }
}

function Resolve-InnoCompiler {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
    $localPrograms = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) "Programs"
    foreach ($candidate in @(
        (Join-Path $localPrograms "Inno Setup 7/ISCC.exe"),
        (Join-Path $localPrograms "Inno Setup 6/ISCC.exe"),
        (Join-Path $programFilesX86 "Inno Setup 7/ISCC.exe"),
        (Join-Path $programFilesX86 "Inno Setup 6/ISCC.exe")
    )) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    throw "Inno Setup Compiler (ISCC.exe) is required to rebuild the signed Windows installer."
}

function Write-ArtifactChecksum([string]$Artifact) {
    if (-not (Test-Path -LiteralPath $Artifact -PathType Leaf)) {
        throw "Cannot checksum missing artifact: $Artifact"
    }
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $Artifact).Hash.ToLowerInvariant()
    $checksum = "$Artifact.sha256"
    Set-Content -LiteralPath $checksum -Value "$hash  $([IO.Path]::GetFileName($Artifact))" -Encoding ascii
}

function Assert-ValidSignature([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Cannot verify missing signed file: $Path"
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid) {
        throw "Invalid Authenticode signature on '$Path': $($signature.Status) $($signature.StatusMessage)"
    }
    if (-not $signature.SignerCertificate) {
        throw "The signed file does not contain a signer certificate: $Path"
    }
    if (-not $signature.TimeStamperCertificate) {
        throw "The signed file does not contain an RFC 3161 timestamp: $Path"
    }
    Write-Host "Verified Authenticode signature: $Path"
}

$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $projectRoot ".artifacts/music-library-manager"
}
$resolvedOutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$resolvedProjectRoot = [IO.Path]::GetFullPath($projectRoot).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar)
$repositoryPrefix = $resolvedProjectRoot + [IO.Path]::DirectorySeparatorChar
if (-not $resolvedOutputRoot.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot must stay inside the repository: $resolvedOutputRoot"
}

$productName = "MusicLibraryManager-$Version-win-x64"
$packageRoot = Join-Path $resolvedOutputRoot "$Version/win-x64"
$publishRoot = Join-Path $packageRoot "publish"
$catalog = Join-Path $packageRoot "authenticode-payload.txt"
$signedUninstallerRoot = Join-Path $packageRoot "signed-uninstaller"
$signedUninstallerCatalog = Join-Path $packageRoot "authenticode-uninstaller-payload.txt"
$archive = Join-Path $resolvedOutputRoot "$productName.zip"
$installer = Join-Path $resolvedOutputRoot "$productName-setup.exe"

if (-not (Test-Path -LiteralPath $publishRoot -PathType Container)) {
    throw "The Windows publish output does not exist: $publishRoot"
}

function Get-SignedUninstaller {
    $files = @(
        Get-ChildItem -LiteralPath $signedUninstallerRoot -Filter "*.e32" -File -ErrorAction SilentlyContinue
    )
    if ($files.Count -ne 1) {
        throw "Expected exactly one cached Inno signed-uninstaller image under '$signedUninstallerRoot'; found $($files.Count)."
    }
    return $files[0].FullName
}

function Invoke-InnoCompiler([switch]$UseSignedUninstaller) {
    $compiler = Resolve-InnoCompiler
    $script = Join-Path $PSScriptRoot "Installer.iss"
    $arguments = @(
        "/DAppVersion=$Version",
        "/DPublishRoot=$publishRoot",
        "/DOutputDir=$resolvedOutputRoot"
    )
    if ($UseSignedUninstaller) {
        $arguments += "/DSignedUninstallerDir=$signedUninstallerRoot"
    }
    $arguments += $script
    & $compiler @arguments | ForEach-Object { Write-Host $_ }
    $exitCode = $LASTEXITCODE

    # Callers handle the compiler result explicitly. Do not let an expected first-pass
    # failure remain as the exit status of the PowerShell process running this script.
    $global:LASTEXITCODE = 0
    return $exitCode
}

switch ($Operation) {
    "CreateCatalog" {
        $entries = foreach ($fileName in (Get-RelativeFirstPartyPayload)) {
            $path = Join-Path $publishRoot $fileName
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                throw "The Windows publish output is missing a first-party binary: $path"
            }
            Join-Path "publish" $fileName
        }
        [IO.File]::WriteAllLines($catalog, $entries, [Text.UTF8Encoding]::new($false))
        Write-Host "Created Authenticode catalog $catalog"
    }

    "PrepareSignedUninstaller" {
        if (Test-Path -LiteralPath $signedUninstallerRoot -PathType Container) {
            Remove-Item -LiteralPath $signedUninstallerRoot -Recurse -Force
        }
        New-Item -ItemType Directory -Force -Path $signedUninstallerRoot | Out-Null
        if (Test-Path -LiteralPath $signedUninstallerCatalog -PathType Leaf) {
            Remove-Item -LiteralPath $signedUninstallerCatalog -Force
        }

        $exitCode = Invoke-InnoCompiler -UseSignedUninstaller
        if ($exitCode -eq 0) {
            throw "Inno Setup unexpectedly completed before the cached uninstaller was signed."
        }
        if ($exitCode -ne 2) {
            throw "Inno Setup failed unexpectedly with exit code $exitCode while preparing the signed uninstaller."
        }

        $uninstaller = Get-SignedUninstaller
        $signature = Get-AuthenticodeSignature -LiteralPath $uninstaller
        if ($signature.Status -ne [Management.Automation.SignatureStatus]::NotSigned) {
            throw "The newly generated Inno uninstaller cache was expected to be unsigned, but its status is $($signature.Status): $uninstaller"
        }
        $packagePrefix = $packageRoot.TrimEnd(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $uninstaller.StartsWith($packagePrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "The generated Inno uninstaller cache escaped the package root: $uninstaller"
        }
        $relativeUninstaller = $uninstaller.Substring($packagePrefix.Length)
        [IO.File]::WriteAllLines(
            $signedUninstallerCatalog,
            @($relativeUninstaller),
            [Text.UTF8Encoding]::new($false))
        Write-Host "Created unsigned Inno uninstaller cache and signing catalog $signedUninstallerCatalog"
    }

    "RebuildArtifacts" {
        $uninstaller = Get-SignedUninstaller
        Assert-ValidSignature $uninstaller

        foreach ($staleChecksum in @("$archive.sha256", "$installer.sha256")) {
            if (Test-Path -LiteralPath $staleChecksum -PathType Leaf) {
                Remove-Item -LiteralPath $staleChecksum -Force
            }
        }

        if (Test-Path -LiteralPath $archive -PathType Leaf) {
            Remove-Item -LiteralPath $archive -Force
        }
        Compress-Archive -Path (Join-Path $publishRoot "*") -DestinationPath $archive -CompressionLevel Optimal

        $exitCode = Invoke-InnoCompiler -UseSignedUninstaller
        if ($exitCode -ne 0) { throw "Inno Setup failed with exit code $exitCode." }
        if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
            throw "Inno Setup did not produce the expected installer: $installer"
        }
        Write-Host "Rebuilt Windows artifacts from the signed publish payload and signed uninstaller cache."
    }

    "Verify" {
        if (-not (Test-Path -LiteralPath $catalog -PathType Leaf)) {
            throw "The Authenticode catalog does not exist: $catalog"
        }
        $catalogRoot = Split-Path -Parent $catalog
        $catalogEntries = @(
            Get-Content -LiteralPath $catalog |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                ForEach-Object { $_.Trim() }
        )
        Assert-CatalogCoverage $catalogEntries
        $paths = @(
            $catalogEntries |
                ForEach-Object { [IO.Path]::GetFullPath((Join-Path $catalogRoot $_)) }
        )
        if ($IncludeUninstaller) { $paths += Get-SignedUninstaller }
        if ($IncludeInstaller) { $paths += $installer }

        foreach ($path in $paths) {
            Assert-ValidSignature $path
        }

        if ($WriteChecksums) {
            if (-not $IncludeInstaller) {
                throw "-WriteChecksums requires -IncludeInstaller after the installer has been signed."
            }
            Write-ArtifactChecksum $archive
            Write-ArtifactChecksum $installer
            Write-Host "Refreshed checksums for the signed Windows artifacts."
        }
    }
}
