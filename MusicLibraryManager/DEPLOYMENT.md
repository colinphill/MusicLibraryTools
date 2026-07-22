# Music Library Manager deployment

`MusicLibraryManager` is the native Avalonia desktop application for Windows, macOS, and Linux.

## Local publish

From the repository root, publish for the current operating system and architecture:

```powershell
pwsh MusicLibraryManager/Package.ps1 -Version 0.2.4
```

The script creates a self-contained archive and SHA-256 checksum under
`.artifacts/music-library-manager`. Supply `-Rids win-x64,linux-x64,osx-x64,osx-arm64` to select
runtime identifiers explicitly. Publishing on the matching operating system is recommended so
executable permissions and the macOS application bundle are preserved.

Pass `-Installers` on the matching host to additionally create the platform-native installer:

- `win-x64`: an Inno Setup installer (`*-setup.exe`); `ISCC.exe` must be installed.
- `linux-x64`: an `amd64.deb` package; `dpkg-deb` must be installed.
- `osx-x64` and `osx-arm64`: compressed DMGs containing the application and an Applications link.

Device synchronization runs in-process through the managed `Syncer.Client` library. Its assembly
embeds the `syncerd` daemon for `arm64-v8a`, `armeabi-v7a`, `x86_64`, and `x86`; packages do not
carry a second loose copy or include the native host `syncer` command. Packaging verifies that the
four embedded resources are non-empty and byte-for-byte identical to the native build outputs. By
default the script reads the in-tree `syncer/out/package/syncer-Release` directory. Use
`-SyncerRuntimeRoot <path>` for a release/runtime staging directory instead. A multi-RID staging
directory may contain one child directory per RID.

For unpackaged development builds, set `MLT_SYNCER_SERVER_PATH` to either the server directory or
its parent directory. Packaged builds extract the matching embedded daemon through `Syncer.Client`.

## Package shapes

- Windows x64: a self-contained ZIP and an optional Inno Setup executable installer.
- Linux x64: a self-contained `tar.gz` and an optional Debian `amd64` package. The package installs
  under `/opt/musiclibrarymanager`, adds `/usr/bin/musiclibrarymanager`, and registers a desktop
  launcher.
- macOS x64 and arm64: a self-contained `MusicLibraryManager.app` in a `tar.gz` and an optional
  drag-to-Applications DMG.

Android device synchronization additionally requires Android platform-tools (`adb`) on the target
computer. The Devices page can select an explicit `adb` executable when auto-detection is not
appropriate.

CI/CD Authenticode-signs the Windows application executable, first-party managed assemblies, the
Inno Setup uninstaller, and the installer with Azure Artifact Signing. The Windows job uses Inno's
two-pass signed-uninstaller cache: it generates the unsigned uninstaller image, signs and verifies
that image, then rebuilds the installer with the signed image embedded. The signed application
payload is verified before the ZIP and installer are rebuilt, the installer signature is verified
separately, and checksums are generated only after signing. Third-party and .NET runtime assemblies
retain their publishers' original signatures. Signing runs for pushes to `master`, manually
dispatched builds whose selected ref is `master`, and releases. Pull-request, other-branch, and local
packages remain unsigned so untrusted or pre-merge changes never receive signing credentials.

The Windows jobs use the repository secrets `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`,
`AZURE_SUBSCRIPTION_ID`, and `AZURE_TENANT_ID`. Because discovery enumerates resources with
`az resource list --subscription`, the service principal must have the **Reader** role at subscription
scope. It must also have the **Artifact Signing Certificate Profile Signer** role at the certificate
profile scope. CI discovers the single accessible Artifact Signing account and its single
`PublicTrust` certificate profile; ambiguous accounts or profiles stop the build instead of selecting
one implicitly.

The macOS bundle is currently unsigned and is not notarized. Production macOS distribution still
requires an Apple Developer ID with hardened-runtime signing and notarization.

The configured `ffmpeg` executable must exist on the target computer for audio verification and
transcoding workflows. Library configuration, cache, window state, saved grids, and split widths
use the shared application settings services.
The configured WavPack executable is additionally required for lossless DSF-to-WavPack DSD
ingest recipes.

## CI/CD

GitHub Actions checks out the `syncer` submodule, builds and tests the portable solution on Windows,
Linux, and macOS, builds the native syncer client and all four Android server ABIs, and tests the
managed syncer solution. The Android server set is then shared with the Windows, Linux, and macOS
manager packaging jobs; every package embeds all four daemon payloads in `Syncer.Client.dll`.

Every CI build produces the Windows x64 setup executable, Linux x64 Debian package, and x64 and
arm64 macOS DMGs alongside the portable archives, each with a SHA-256 file. Pushing a `v*` tag
publishes all of those artifacts to a GitHub release. The release workflow can also be run manually
with an explicit version to create downloadable workflow artifacts without publishing a GitHub
release.
