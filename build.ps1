# Build everything: publish both apps as single-file self-contained exes, compile the
# two Inno Setup installers, and stage the auto-update package (manifest + exes).
#
#   powershell -ExecutionPolicy Bypass -File build.ps1
#   powershell -ExecutionPolicy Bypass -File build.ps1 -PushTo sim@raspberrypi
#
# Version comes from the VERSION file (single source of truth) and is stamped into the
# assemblies, installers, and update manifest.
#
# Outputs:
#   publish\client\RemotlerAgent.exe   publish\master\RemoteControl.exe
#   publish\update\manifest.json  + the exes    (upload this dir to the Pi)
#   installer\dist\RemotlerAgent-Setup-<ver>.exe   (+ Master)
#
# -PushTo <user@host> scp's publish\update\* to the Pi so connected apps see the update.

param(
    [string]$PushTo = '',
    [string]$PiUpdateDir = '~/remote-desktop/server/update',
    [string]$Notes = '',
    # Version control. A local build keeps the current version (no update-nag churn); a
    # published build (-PushTo) bumps the patch by default so installed apps see the update.
    # Override anytime: -Version x.y.z, -Bump major|minor|patch, or -Bump none to publish
    # without bumping.
    [string]$Version = '',
    [ValidateSet('major','minor','patch','none')][string]$Bump = 'none',
    # Per-org white-label: produces a custom-named installer whose app exe carries the
    # given icon. e.g. build.ps1 -BrandName "Acme Remote" -BrandIcon C:\acme.ico
    [string]$BrandName = '',
    [string]$BrandIcon = '',
    # Bundle the LGPL FFmpeg shared libraries next to the exes (enables hardware H.264).
    # Off by default so a plain local build doesn't pull ~70 MB; CI/release passes it.
    [switch]$WithFFmpeg
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# Make dotnet + ISCC resolvable even in a fresh shell. Append (don't replace) so a
# runner/session that already put dotnet on the process PATH (e.g. GitHub Actions
# setup-dotnet) keeps working.
$env:Path = $env:Path + ';' +
            [Environment]::GetEnvironmentVariable('Path','Machine') + ';' +
            [Environment]::GetEnvironmentVariable('Path','User')

$ver = (Get-Content "$root\VERSION" -Raw).Trim()
if ($ver -notmatch '^\d+\.\d+\.\d+$') { throw "VERSION file must contain x.y.z (got '$ver')." }

# Publishing a build (-PushTo) is a release, so bump the patch by default — installed apps
# then see the update. A plain local build keeps the version (no update-nag churn). Explicit
# -Version / -Bump always win; pass -Bump none to publish without bumping.
if ($PushTo -and -not $Version -and -not $PSBoundParameters.ContainsKey('Bump')) { $Bump = 'patch' }

# Decide this build's version: explicit -Version wins; otherwise bump the requested part.
if ($Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "-Version must be x.y.z (got '$Version')." }
    $ver = $Version
} elseif ($Bump -ne 'none') {
    $p = $ver.Split('.') | ForEach-Object { [int]$_ }
    switch ($Bump) {
        'major' { $ver = "$($p[0]+1).0.0" }
        'minor' { $ver = "$($p[0]).$($p[1]+1).0" }
        'patch' { $ver = "$($p[0]).$($p[1]).$($p[2]+1)" }
    }
}
# Persist so the number is monotonic across builds (commit VERSION with each release).
# ASCII (no BOM) — a UTF-8 BOM here can corrupt the version string for downstream readers.
Set-Content "$root\VERSION" $ver -Encoding ascii -NoNewline
if (-not $Notes) { $Notes = "Remotler $ver" }
Write-Host "== Building version $ver ==" -ForegroundColor Cyan

# Download the LGPL FFmpeg 8 shared DLLs (H.264 hardware encode + native decode) once,
# cache under .ffmpeg, and copy them next to each given exe folder. LGPL (no GPL libx264),
# so it's safe to ship with a proprietary app; H.264 encode uses NVENC/QuickSync/AMF.
function Add-FFmpeg {
    param([string[]]$Dests)
    # PINNED to the FFmpeg 8.x line: SIPSorceryMedia.FFmpeg's bindings load the v8 DLLs
    # by name (avcodec-62 etc.). The old "master-latest" URL silently moved to FFmpeg 9
    # (avcodec-63), which the bindings cannot load — shipping it disables H.264 entirely
    # and every session falls back to software VP8.
    $cache  = Join-Path $root '.ffmpeg8'
    $binDir = Join-Path $cache 'bin'
    if (-not (Get-ChildItem -Path $binDir -Filter 'avcodec-62.dll' -ErrorAction SilentlyContinue)) {
        New-Item -ItemType Directory -Force -Path $cache | Out-Null
        $zip = Join-Path $cache 'ffmpeg.zip'
        $url = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip'
        Write-Host "== Downloading FFmpeg (LGPL shared) ==" -ForegroundColor Cyan
        Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
        Expand-Archive -Path $zip -DestinationPath $cache -Force
        $extractedBin = Get-ChildItem -Path $cache -Directory |
            ForEach-Object { Join-Path $_.FullName 'bin' } |
            Where-Object { Test-Path $_ } | Select-Object -First 1
        if (-not $extractedBin) { throw "FFmpeg 'bin' folder not found after extraction." }
        if (Test-Path $binDir) { Remove-Item $binDir -Recurse -Force }
        Copy-Item $extractedBin $binDir -Recurse -Force
    }
    foreach ($d in $Dests) {
        if ($d -and (Test-Path $d)) { Copy-Item (Join-Path $binDir '*.dll') $d -Force }
    }
    Write-Host "   FFmpeg DLLs staged into: $($Dests -join ', ')"
}

function Find-ISCC {
    $candidates = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw "ISCC.exe (Inno Setup) not found. Install with: winget install JRSoftware.InnoSetup"
}

# A test build launched from publish\ locks its exe and breaks the next publish.
# Stop only processes running from THIS repo's publish folder — never the installed
# apps or the SYSTEM service.
$pubRoot = Join-Path $root 'publish'
Get-Process RemoteControl,RemotlerAgent,RemotlerService -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($pubRoot, [StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object {
        Write-Host "   stopping running test build: $($_.ProcessName) (pid $($_.Id))" -ForegroundColor DarkYellow
        Stop-Process -Id $_.Id -Force
    }

$publishArgs = @(
    '-c','Release','-r','win-x64','--self-contained','true',
    '/p:PublishSingleFile=true','/p:IncludeNativeLibrariesForSelfExtract=true',
    '/p:EnableCompressionInSingleFile=true',"/p:Version=$ver",'-nologo'
)

# The unified attended app (host + viewer in one), RemoteControl.exe. A branded build bakes
# the org's icon (Explorer/taskbar) into the exe; name/colour/logo also apply at runtime.
$brandIconPath = if ($BrandName -and $BrandIcon -and (Test-Path $BrandIcon)) { (Resolve-Path $BrandIcon).Path } else { "$root\assets\remotler.ico" }
$appPubArgs = $publishArgs
if ($BrandName) { $appPubArgs = $publishArgs + @("/p:ApplicationIcon=$brandIconPath", "/p:Product=$BrandName") }
$brandLabel = if ($BrandName) { " (branded: $BrandName)" } else { "" }
Write-Host "== Publishing App (unified)$brandLabel ==" -ForegroundColor Cyan
dotnet publish "$root\src\Master\Master.csproj" @appPubArgs -o "$root\publish\app"

# The unattended host worker + Windows service (headless). The service finds the worker
# exe (RemotlerAgent.exe) next to itself, so both live in publish\client.
Write-Host '== Publishing Client worker ==' -ForegroundColor Cyan
dotnet publish "$root\src\Client\Client.csproj" @publishArgs -o "$root\publish\client"
Write-Host '== Publishing Service ==' -ForegroundColor Cyan
dotnet publish "$root\src\Service\Service.csproj" @publishArgs -o "$root\publish\client"

# Bundle the FFmpeg runtime next to both exes so hardware H.264 is available (else VP8).
if ($WithFFmpeg) { Add-FFmpeg -Dests @("$root\publish\app", "$root\publish\client") }

# ---- stage the auto-update package -------------------------------------------------
Write-Host '== Staging update package ==' -ForegroundColor Cyan
$upd = "$root\publish\update"
if (Test-Path $upd) { Remove-Item $upd -Recurse -Force }
New-Item -ItemType Directory -Force -Path $upd | Out-Null

function New-Entry([string]$path) {
    Copy-Item $path $upd -Force
    [ordered]@{
        name   = (Split-Path $path -Leaf)
        sha256 = (Get-FileHash $path -Algorithm SHA256).Hash
        size   = (Get-Item $path).Length
    }
}

# Installer filenames (also staged into the update dir so the dashboard can offer them).
$appOut   = if ($BrandName) { (($BrandName -replace '[^\w\-]', '_')) + "-Setup-$ver" } else { "Remotler-Setup-$ver" }
$agentOut = "RemotlerAgent-Setup-$ver"

# The FFmpeg runtime DLLs (hardware H.264) are part of both components so auto-update
# delivers them too, not just the exe. They're identical across app/client; the updater
# skips any file already installed with a matching hash, so they download only when changed.
$ffEntries = @()
if ($WithFFmpeg) {
    foreach ($dll in Get-ChildItem "$root\publish\app\*.dll") { $ffEntries += New-Entry $dll.FullName }
}

# @(...) keeps these as JSON arrays; the app parser also tolerates a bare object.
# Components: "app" = the unified attended app; "client" = the unattended worker+service.
$manifest = [ordered]@{
    version        = $ver
    notes          = $Notes
    app            = @( New-Entry "$root\publish\app\RemoteControl.exe" ) + $ffEntries
    client         = @(
        New-Entry "$root\publish\client\RemotlerAgent.exe"
        New-Entry "$root\publish\client\RemotlerService.exe"
    ) + $ffEntries
    appInstaller   = "$appOut.exe"      # attended app installer (dashboard Download tab)
    agentInstaller = "$agentOut.exe"    # unattended agent + service installer
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content "$upd\manifest.json" -Encoding utf8
Write-Host "   manifest.json + $((Get-ChildItem $upd -Filter *.exe).Count) exe(s) staged in $upd"

# ---- installers --------------------------------------------------------------------
$iscc = Find-ISCC
Write-Host "== Compiling installers ($iscc) ==" -ForegroundColor Cyan
# Brand values go through env vars (Inno reads them via GetEnv) to dodge CLI quoting.
if ($BrandName) {
    $safe = ($BrandName -replace '[^\w\-]', '_')
    $env:REMOTLER_BRAND_NAME = $BrandName
    $env:REMOTLER_BRAND_ICON = $brandIconPath
    $env:REMOTLER_OUTFILE = "$safe-Setup-$ver"
} else {
    Remove-Item Env:REMOTLER_BRAND_NAME, Env:REMOTLER_BRAND_ICON, Env:REMOTLER_OUTFILE -ErrorAction SilentlyContinue
}
& $iscc "/DAppVersion=$ver" "$root\installer\app.iss"
& $iscc "/DAppVersion=$ver" "$root\installer\client.iss"

# Stage the compiled installers into the update dir so `-PushTo` ships them to the Pi,
# where the dashboard's Download tab links to /update/<installer>.
Copy-Item "$root\installer\dist\$appOut.exe"   $upd -Force -ErrorAction SilentlyContinue
Copy-Item "$root\installer\dist\$agentOut.exe" $upd -Force -ErrorAction SilentlyContinue

Write-Host "`nInstallers:" -ForegroundColor Green
Get-ChildItem "$root\installer\dist\*.exe" | ForEach-Object {
    Write-Host ("  {0}  ({1} MB)" -f $_.Name, [math]::Round($_.Length/1MB,1))
}

# ---- optional: push the update package to the Pi -----------------------------------
if ($PushTo) {
    Write-Host "`n== Pushing update to ${PushTo}:$PiUpdateDir ==" -ForegroundColor Cyan
    & ssh $PushTo "mkdir -p $PiUpdateDir"
    if ($LASTEXITCODE -ne 0) { throw "ssh mkdir failed (exit $LASTEXITCODE)." }
    & scp "$upd\*" "${PushTo}:$PiUpdateDir/"
    if ($LASTEXITCODE -ne 0) { throw "scp failed (exit $LASTEXITCODE)." }
    Write-Host "   Update v$ver is live. Connected apps will offer it on next launch." -ForegroundColor Green
}
else {
    Write-Host "`nTo publish the update to the server:" -ForegroundColor Yellow
    Write-Host "  build.ps1 -PushTo root@remotler.com -PiUpdateDir /opt/remotler/server/update"
    Write-Host "  (then on the server:  chown -R remotler:remotler /opt/remotler/server/update )"
}
