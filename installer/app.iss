; Inno Setup script — Hangar (unified app: host + viewer in one).
; Build: publish first (see build.ps1), then compile this with ISCC.exe.
; Per-org white-label overrides (build.ps1 -BrandName …): /DAppName, /DBrandIcon, /DOutFile.

; Version via ISCC /DAppVersion=x.y.z. Per-org white-label via env vars set by build.ps1
; (HANGAR_BRAND_NAME / HANGAR_BRAND_ICON / HANGAR_OUTFILE) — avoids command-line quoting.
#ifndef AppVersion
  #define AppVersion "0.3.0"
#endif
#define BrandNameEnv GetEnv("HANGAR_BRAND_NAME")
#if BrandNameEnv != ""
  #define AppName BrandNameEnv
#else
  #define AppName "Hangar"
#endif
#define BrandIconEnv GetEnv("HANGAR_BRAND_ICON")
#if BrandIconEnv != ""
  #define BrandIcon BrandIconEnv
#else
  #define BrandIcon "..\assets\hangar.ico"
#endif
#define OutFileEnv GetEnv("HANGAR_OUTFILE")
#if OutFileEnv != ""
  #define OutFile OutFileEnv
#else
  #define OutFile "Hangar-Setup-" + AppVersion
#endif
#define AppPublisher "Hangar"
#define AppExe "FtdRemote.exe"

[Setup]
AppId={{9E3AC14D-5F61-4C77-BE33-C3D4E5F60718}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
OutputDir=dist
OutputBaseFilename={#OutFile}
SetupIconFile={#BrandIcon}
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
