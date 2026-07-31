; Inno Setup script — Remote Desktop MASTER (the controlling PC)
; Build: publish first (see build.ps1), then compile this with ISCC.exe.

#define AppName "FTD Remote Master"
; Version can be overridden from build.ps1 via ISCC /DAppVersion=x.y.z
#ifndef AppVersion
  #define AppVersion "0.2.0"
#endif
#define AppPublisher "FTD.aero"
#define AppExe "FtdRemoteMaster.exe"

[Setup]
AppId={{8D2FAB3C-4E50-4B66-AD22-B2C3D4E5F607}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\FTD Remote\Master
DefaultGroupName=FTD Remote
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
OutputDir=dist
OutputBaseFilename=FTDRemoteMaster-Setup-{#AppVersion}
SetupIconFile=..\assets\ftd.ico
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
; The master doesn't inject input or need admin — install per-user, no UAC.
PrivilegesRequired=lowest

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"

[Files]
Source: "..\publish\master\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName} now"; Flags: nowait postinstall skipifsilent
