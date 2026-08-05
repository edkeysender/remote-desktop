; Inno Setup script — Remotler (unified app: host + viewer in one).
; Build: publish first (see build.ps1), then compile this with ISCC.exe.
; Per-org white-label overrides (build.ps1 -BrandName …): /DAppName, /DBrandIcon, /DOutFile.

; Version via ISCC /DAppVersion=x.y.z. Per-org white-label via env vars set by build.ps1
; (REMOTLER_BRAND_NAME / REMOTLER_BRAND_ICON / REMOTLER_OUTFILE) — avoids command-line quoting.
#ifndef AppVersion
  #define AppVersion "0.3.0"
#endif
#define BrandNameEnv GetEnv("REMOTLER_BRAND_NAME")
#if BrandNameEnv != ""
  #define AppName BrandNameEnv
#else
  #define AppName "Remotler"
#endif
#define BrandIconEnv GetEnv("REMOTLER_BRAND_ICON")
#if BrandIconEnv != ""
  #define BrandIcon BrandIconEnv
#else
  #define BrandIcon "..\assets\remotler.ico"
#endif
#define OutFileEnv GetEnv("REMOTLER_OUTFILE")
#if OutFileEnv != ""
  #define OutFile OutFileEnv
#else
  #define OutFile "Remotler-Setup-" + AppVersion
#endif
#define AppPublisher "Remotler"
#define AppExe "RemoteControl.exe"

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
; Install into Program Files by default (requires admin). {autopf} resolves to
; Program Files in administrative install mode. The app itself still runs as the normal
; user (asInvoker); updates auto-elevate the file swap since Program Files needs admin.
PrivilegesRequired=admin

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"
Name: "autostart"; Description: "Start automatically when I sign in (stay available)"; GroupDescription: "Startup:"

[Files]
Source: "..\publish\app\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
; LGPL FFmpeg runtime (hardware H.264). Present only when built with -WithFFmpeg.
Source: "..\publish\app\*.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "RemoteControl"; ValueData: """{app}\{#AppExe}"""; \
    Flags: uninsdeletevalue; Tasks: autostart

[Run]
; Allow inbound LAN direct connections (serverless connect-by-IP) through the firewall.
Filename: "{sys}\netsh.exe"; \
    Parameters: "advfirewall firewall add rule name=""Remotler Direct LAN"" dir=in action=allow program=""{app}\{#AppExe}"" enable=yes profile=private,domain"; \
    Flags: runhidden
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName} now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Remotler Direct LAN"""; Flags: runhidden; RunOnceId: "DelRemotlerFw"

[Code]
{ ---- Microsoft Edge WebView2 Runtime -----------------------------------------
  Remotler renders its whole UI in WebView2. Windows 11 ships the Evergreen
  runtime; some Windows 10 machines do not have it. Detect it via the EdgeUpdate
  registry keys and, if absent, download + run the official Evergreen bootstrapper
  before the app is installed. }

const
  WV2_CLIENT = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WV2_URL    = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';

function WebView2Missing: Boolean;
var
  v: String;
begin
  Result := True;
  { Per-machine (64-bit OS stores under WOW6432Node) }
  if RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\' + WV2_CLIENT, 'pv', v)
     and (v <> '') and (v <> '0.0.0.0') then Result := False;
  if RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\' + WV2_CLIENT, 'pv', v)
     and (v <> '') and (v <> '0.0.0.0') then Result := False;
  { Per-user install }
  if RegQueryStringValue(HKCU, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\' + WV2_CLIENT, 'pv', v)
     and (v <> '') and (v <> '0.0.0.0') then Result := False;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  code: Integer;
begin
  Result := '';
  if not WebView2Missing then Exit;

  try
    DownloadTemporaryFile(WV2_URL, 'MicrosoftEdgeWebview2Setup.exe', '', nil);
  except
    Result := 'Remotler needs the Microsoft Edge WebView2 Runtime, which is not installed ' +
              'on this PC and could not be downloaded automatically.' + #13#10#13#10 +
              'Please install it from:' + #13#10 + WV2_URL + #13#10#13#10 +
              'then run this installer again.';
    Exit;
  end;

  if not Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'), '/silent /install',
              '', SW_SHOW, ewWaitUntilTerminated, code) then
    Result := 'Failed to run the WebView2 Runtime installer (error ' + IntToStr(code) + ').';
end;
