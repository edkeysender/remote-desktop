; Inno Setup script — FTD Remote (unified app: host + viewer in one).
; Build: publish first (see build.ps1), then compile this with ISCC.exe.

#define AppName "FTD Remote"
; Version can be overridden from build.ps1 via ISCC /DAppVersion=x.y.z
#ifndef AppVersion
  #define AppVersion "0.3.0"
#endif
#define AppPublisher "FTD.aero"
#define AppExe "FtdRemote.exe"

[Setup]
AppId={{9E3AC14D-5F61-4C77-BE33-C3D4E5F60718}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\FTD Remote
DefaultGroupName=FTD Remote
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
OutputDir=dist
OutputBaseFilename=FTDRemote-Setup-{#AppVersion}
SetupIconFile=..\assets\ftd.ico
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
; The unified app hosts + views as the normal user (asInvoker) — install per-user, no UAC.
PrivilegesRequired=lowest

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"
Name: "autostart"; Description: "Start automatically when I sign in (stay available)"; GroupDescription: "Startup:"

[Files]
Source: "..\publish\app\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "FtdRemote"; ValueData: """{app}\{#AppExe}"""; \
    Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName} now"; Flags: nowait postinstall skipifsilent
