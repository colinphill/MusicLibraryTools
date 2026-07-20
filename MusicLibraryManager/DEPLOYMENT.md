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

Device synchronization runs in-process through the managed `Syncer.Client` library. The package
includes Android servers for all supported ABIs under `tools/syncer/servers`; it does not include
or launch the native host `syncer` command. By default the script reads the in-tree
`syncer/out/package/syncer-Release` directory. Use `-SyncerRuntimeRoot <path>` for a release/runtime
staging directory instead. A multi-RID staging directory may contain one child directory per RID.

For unpackaged development builds, set `MLT_SYNCER_SERVER_PATH` to either the server directory or
its parent directory. Packaged builds discover `tools/syncer` automatically.

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

The Windows installer and macOS bundle are currently unsigned, and the macOS bundle is not
notarized. Production distribution requires a Windows code-signing certificate and an Apple
Developer ID with hardened-runtime signing and notarization.

The configured `ffmpeg` executable must exist on the target computer for audio verification and
transcoding workflows. Library configuration, cache, window state, saved grids, and split widths
use the shared application settings services.

## CI/CD

GitHub Actions checks out the `syncer` submodule, builds and tests the portable solution on Windows
and Linux, builds the native syncer client and all four Android server ABIs, and tests the managed
syncer solution. The Android server set is then shared with the Windows, Linux, and macOS manager
packaging jobs; every package must contain `Syncer.Client` plus all four `syncerd` binaries.

Every CI build produces the Windows x64 setup executable, Linux x64 Debian package, and x64 and
arm64 macOS DMGs alongside the portable archives, each with a SHA-256 file. Pushing a `v*` tag
publishes all of those artifacts to a GitHub release. The release workflow can also be run manually
with an explicit version to create downloadable workflow artifacts without publishing a GitHub
release.
