# Avalonia Studio deployment

`MusicLibraryManager.Studio.Avalonia` is the native, WebView-free cross-platform Studio build.
The original WPF Studio remains available during the parity-validation release.

## Local publish

From the repository root, publish the current operating system and architecture:

```powershell
pwsh MusicLibraryManager.Studio.Avalonia/Package.ps1 -Version 0.1.0
```

The script creates a self-contained archive and SHA-256 checksum under
`.artifacts/studio-avalonia`. Supply `-Rids win-x64,linux-x64,osx-x64,osx-arm64` to select
runtime identifiers explicitly. Publishing a package on its matching operating system is
recommended so executable permissions and the macOS application bundle are preserved.

## Package shapes

- Windows: a self-contained ZIP containing `MusicLibraryManager.Studio.Avalonia.exe`.
- Linux: a self-contained `tar.gz`; extract it and launch `MusicLibraryManager.Studio.Avalonia`.
- macOS: a self-contained `MusicLibraryManager Studio.app` in a `tar.gz`.

The macOS bundle is currently unsigned and unnotarized. Production distribution requires an
Apple Developer ID, hardened-runtime signing, and notarization. Linux desktop integration and
signed Windows installers are likewise post-parity release work; the archives are intended for
validation and controlled deployment.

The configured `ffmpeg` executable must exist on the target computer for audio verification and
transcoding workflows. Library configuration, cache, window state, saved grids, and split widths
use the same settings services and Studio preference keys as the Windows reference application.
