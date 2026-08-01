; Inno Setup script — Remote Desktop CLIENT (the PC being controlled)
; Build: publish first (see build.ps1), then compile this with ISCC.exe.

#define AppName "Hangar Agent"
; Version can be overridden from build.ps1 via ISCC /DAppVersion=x.y.z
#ifndef AppVersion
  #define AppVersion "0.2.0"
#endif
#define AppPublisher "FTD.aero"
#define AppExe "FtdRemoteClient.exe"
#define SvcExe "FtdRemoteService.exe"
#define SvcName "FtdRemoteService"

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
OutputBaseFilename=HangarAgent-Setup-{#AppVersion}
SetupIconFile=..\assets\hangar.ico
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
; Unattended access installs a LocalSystem service that keeps this PC reachable with a
; fixed ID/password even while logged out or at the UAC/lock secure desktop. Opt-in.
Name: "unattended"; Description: "Enable unattended access (installs a background service)"; GroupDescription: "Unattended access:"; Flags: unchecked

[Files]
Source: "..\publish\client\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
; The service exe is always laid down (harmless if the task is unchecked); it looks for
; the worker exe next to itself, so both live in {app}.
Source: "..\publish\client\{#SvcExe}"; DestDir: "{app}"; Flags: ignoreversion

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
; Register + start the unattended service only if the user opted in. `binPath= ` needs the
; trailing space; the doubled quotes wrap the path (may contain spaces).
Filename: "{sys}\sc.exe"; \
    Parameters: "create {#SvcName} binPath= ""{app}\{#SvcExe}"" start= auto DisplayName= ""FTD Remote Service"""; \
    Flags: runhidden; Tasks: unattended
Filename: "{sys}\sc.exe"; \
    Parameters: "description {#SvcName} ""Unattended remote access for FTD Remote (LocalSystem)."""; \
    Flags: runhidden; Tasks: unattended
Filename: "{sys}\sc.exe"; Parameters: "start {#SvcName}"; Flags: runhidden; Tasks: unattended
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName} now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Runs before files are removed, so stopping releases the exe lock. Harmless if absent.
Filename: "{sys}\sc.exe"; Parameters: "stop {#SvcName}"; Flags: runhidden; RunOnceId: "StopFtdSvc"
Filename: "{sys}\sc.exe"; Parameters: "delete {#SvcName}"; Flags: runhidden; RunOnceId: "DelFtdSvc"

[Code]
// On upgrade the service may be running and holding FtdRemoteService.exe open, which would
// block [Files] from overwriting it. Stop it (best-effort) before files are copied.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  code: Integer;
begin
  Result := '';
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#SvcName}', '', SW_HIDE, ewWaitUntilTerminated, code);
end;
