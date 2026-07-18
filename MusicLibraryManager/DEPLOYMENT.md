# Music Library Manager deployment

`MusicLibraryManager` is the native Avalonia desktop application for Windows, macOS, and Linux.

## Local publish

From the repository root, publish for the current operating system and architecture:

```powershell
pwsh MusicLibraryManager/Package.ps1 -Version 0.1.0
```

The script creates a self-contained archive and SHA-256 checksum under
`.artifacts/music-library-manager`. Supply `-Rids win-x64,linux-x64,osx-x64,osx-arm64` to select
runtime identifiers explicitly. Publishing on the matching operating system is recommended so
executable permissions and the macOS application bundle are preserved.

## Package shapes

- Windows: a self-contained ZIP containing `MusicLibraryManager.exe`.
- Linux: a self-contained `tar.gz`; extract it and launch `MusicLibraryManager`.
- macOS: a self-contained `MusicLibraryManager.app` in a `tar.gz`.

The macOS bundle is unsigned and unnotarized. Production distribution requires an Apple Developer
ID, hardened-runtime signing, and notarization. Linux desktop integration and signed Windows
installers are also separate distribution steps.

The configured `ffmpeg` executable must exist on the target computer for audio verification and
transcoding workflows. Library configuration, cache, window state, saved grids, and split widths
use the shared application settings services.
