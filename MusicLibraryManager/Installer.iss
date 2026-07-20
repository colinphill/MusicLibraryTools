#ifndef AppVersion
  #error AppVersion must be supplied with /DAppVersion=<version>
#endif
#ifndef PublishRoot
  #error PublishRoot must be supplied with /DPublishRoot=<directory>
#endif
#ifndef OutputDir
  #error OutputDir must be supplied with /DOutputDir=<directory>
#endif

[Setup]
AppId={{9EA32223-67AB-46AE-A4AC-383E4AF630FB}
AppName=Music Library Manager
AppVersion={#AppVersion}
AppPublisher=MusicLibraryTools
AppPublisherURL=https://github.com/colinphill/MusicLibraryTools
AppSupportURL=https://github.com/colinphill/MusicLibraryTools/issues
AppUpdatesURL=https://github.com/colinphill/MusicLibraryTools/releases
DefaultDirName={autopf}\Music Library Manager
DefaultGroupName=Music Library Manager
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=MusicLibraryManager-{#AppVersion}-win-x64-setup
SetupIconFile=Assets\AppIcon.ico
UninstallDisplayIcon={app}\MusicLibraryManager.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Files]
Source: "{#PublishRoot}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Icons]
Name: "{group}\Music Library Manager"; Filename: "{app}\MusicLibraryManager.exe"
Name: "{autodesktop}\Music Library Manager"; Filename: "{app}\MusicLibraryManager.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\MusicLibraryManager.exe"; Description: "Launch Music Library Manager"; Flags: nowait postinstall skipifsilent
