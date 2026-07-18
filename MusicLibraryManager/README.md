# MusicLibraryManager

MusicLibraryManager is the Windows 11 Fluent desktop experience for MusicLibraryTools. It is a
self-contained WPF x64 application targeting `net10.0-windows10.0.26100.0` and Windows 11 build
22000 or newer. It directly consumes `MusicLibrary.Core`; no Avalonia controls or presentation
types are referenced. WPF's native desktop rendering path keeps the real layout active throughout
a live window resize.

## Current release

- Windows 11 Fluent shell with native window chrome, global search (`Ctrl+K`), active
  configuration, indexing state, and activity center.
- Home dashboard with cached collection, artwork, scan-root, and indexing health.
- Library workspace using WPF's recycling, virtualized `DataGrid`, advanced Core filtering, typed
  sorting, configurable columns, saved views, and a responsive selection inspector.
- Cache-first offline browsing and lazy artwork loading for the current selection.
- Single- and multi-file tag editing, artwork replacement/removal/scrubbing, confirmation prompts,
  activity reporting, and targeted cache refresh.
- Configuration creation/editing, scan roots, recent configurations, appearance selection, and
  versioned `manager.*` workspace settings.
- Platform-neutral presentation ViewModels with WPF adapters for pickers, dialogs, clipboard,
  Explorer, thumbnails, navigation, theme, and window state.

Health, Ingest, Organize, and Operations are present in the information architecture and explicitly
identify their staged migration status. `MusicLibrary.App` remains the supported route for those
workflows until their preview/apply and recovery behavior reaches parity.

## Build and run

```powershell
dotnet restore MusicLibraryManager\MusicLibraryManager.csproj --runtime win-x64
dotnet build MusicLibraryManager\MusicLibraryManager.csproj -c Debug -r win-x64
MusicLibraryManager\bin\Debug\net10.0-windows10.0.26100.0\win-x64\MusicLibraryManager.exe
```

## Publish

```powershell
dotnet publish MusicLibraryManager\MusicLibraryManager.csproj -c Release -r win-x64
```

The publish result is a self-contained directory rather than a single-file executable. The icon
PNGs and multi-resolution ICO are generated from the canonical brand mark with
`Assets/Generate-Icons.ps1`.
