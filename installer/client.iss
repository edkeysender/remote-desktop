; Inno Setup script — Remote Desktop CLIENT (the PC being controlled)
; Build: publish first (see build.ps1), then compile this with ISCC.exe.

#define AppName "FTD Remote Client"
#define AppVersion "0.1.0"
#define AppPublisher "FTD.aero"
#define AppExe "FtdRemoteClient.exe"

[Setup]
; A stable, unique GUID keeps upgrades/uninstall coherent across versions.
AppId={{7C1E9A2B-3D4F-4A55-9C11-A1B2C3D4E5F6}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\FTD Remote\Client
DefaultGroupName=FTD Remote
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
OutputDir=dist
OutputBaseFilename=FTDRemoteClient-Setup-{#AppVersion}
SetupIconFile=..\assets\ftd.ico
Compression=lzma2/max
SolidCompression=yes
; Self-contained x64 build → require 64-bit Windows.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
PrivilegesRequired=admin

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"
Name: "autostart"; Description: "Start automatically when I sign in"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "..\publish\client\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Optional per-user autostart (only if the task is selected).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "FtdRemoteClient"; ValueData: """{app}\{#AppExe}"""; \
    Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName} now"; Flags: nowait postinstall skipifsilent
