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

Device synchronization runs in-process through the managed `Syncer.Client` library. The package
includes Android servers for all supported ABIs under `tools/syncer/servers`; it does not include
or launch the native host `syncer` command. By default the script reads the in-tree
`syncer/out/package/syncer-Release` directory. Use `-SyncerRuntimeRoot <path>` for a release/runtime
staging directory instead. A multi-RID staging directory may contain one child directory per RID.

For unpackaged development builds, set `MLT_SYNCER_SERVER_PATH` to either the server directory or
its parent directory. Packaged builds discover `tools/syncer` automatically.

## Package shapes

- Windows: a self-contained ZIP containing `MusicLibraryManager.exe`.
- Linux: a self-contained `tar.gz`; extract it and launch `MusicLibraryManager`.
- macOS: a self-contained `MusicLibraryManager.app` in a `tar.gz`.

Android device synchronization additionally requires Android platform-tools (`adb`) on the target
computer. The Devices page can select an explicit `adb` executable when auto-detection is not
appropriate.

The macOS bundle is unsigned and unnotarized. Production distribution requires an Apple Developer
ID, hardened-runtime signing, and notarization. Linux desktop integration and signed Windows
installers are also separate distribution steps.

The configured `ffmpeg` executable must exist on the target computer for audio verification and
transcoding workflows. Library configuration, cache, window state, saved grids, and split widths
use the shared application settings services.

## CI/CD

GitHub Actions checks out the `syncer` submodule, builds and tests the portable solution on Windows
and Linux, builds the native syncer client and all four Android server ABIs, and tests the managed
syncer solution. The Android server set is then shared with the Windows, Linux, and macOS manager
packaging jobs; every package must contain `Syncer.Client` plus all four `syncerd` binaries.

Pushing a `v*` tag builds `win-x64`, `linux-x64`, `osx-x64`, and `osx-arm64` archives with SHA-256
files and publishes them to a GitHub release. The release workflow can also be run manually with an
explicit version to create downloadable workflow artifacts without publishing a GitHub release.
