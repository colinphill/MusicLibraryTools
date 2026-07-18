# Music Library Manager Studio deployment

This is a self-contained Windows x64 build. The target computer does not need the .NET SDK or
.NET Desktop Runtime.

## Launch

1. Extract the entire ZIP to a local folder.
2. Run `MusicLibraryManager.Studio.exe`.
3. Keep the executable, `wwwroot` directory, and supporting files together.

Studio uses the Microsoft Edge WebView2 Runtime to host its interface. Windows 11 normally includes
it. If Studio reports that WebView2 is unavailable, install the current Evergreen WebView2 Runtime
from Microsoft and launch Studio again.

The build is not code-signed. Windows may display an unknown-publisher warning when the package was
downloaded from another computer.

## Build a package

From the repository root, run:

```powershell
.\MusicLibraryManager.Studio\Package.cmd
```

The script publishes a self-contained folder and creates a versioned ZIP plus a SHA-256 checksum in
`.artifacts\studio`. Supply `-Version 1.2.3` to set the application/package version or
`-OutputDirectory C:\Packages` to place the results elsewhere.
