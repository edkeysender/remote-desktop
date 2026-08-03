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
#   publish\client\FtdRemoteClient.exe   publish\master\FtdRemoteMaster.exe
#   publish\update\manifest.json  + the exes    (upload this dir to the Pi)
#   installer\dist\FTDRemoteClient-Setup-<ver>.exe   (+ Master)
#
# -PushTo <user@host> scp's publish\update\* to the Pi so connected apps see the update.

param(
    [string]$PushTo = '',
    [string]$PiUpdateDir = '~/remote-desktop/server/update',
    [string]$Notes = '',
    # Version control. By default every build bumps the patch number and writes it back to
    # VERSION, so each build is a strictly newer version and installed apps always see the
    # update. Override with -Version x.y.z, change the part with -Bump major|minor|patch,
    # or keep the current number with -Bump none.
    [string]$Version = '',
    [ValidateSet('major','minor','patch','none')][string]$Bump = 'patch',
    # Per-org white-label: produces a custom-named installer whose app exe carries the
    # given icon. e.g. build.ps1 -BrandName "Acme Remote" -BrandIcon C:\acme.ico
    [string]$BrandName = '',
    [string]$BrandIcon = ''
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# Make dotnet + ISCC resolvable even in a fresh shell.
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' +
            [Environment]::GetEnvironmentVariable('Path','User')

$ver = (Get-Content "$root\VERSION" -Raw).Trim()
if ($ver -notmatch '^\d+\.\d+\.\d+$') { throw "VERSION file must contain x.y.z (got '$ver')." }

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
if (-not $Notes) { $Notes = "FTD Remote $ver" }
Write-Host "== Building version $ver ==" -ForegroundColor Cyan

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
Get-Process RemoteControl,FtdRemote,FtdRemoteClient,FtdRemoteMaster,FtdRemoteService -ErrorAction SilentlyContinue |
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

# The unified attended app (host + viewer in one), FtdRemote.exe. A branded build bakes
# the org's icon (Explorer/taskbar) into the exe; name/colour/logo also apply at runtime.
$brandIconPath = if ($BrandName -and $BrandIcon -and (Test-Path $BrandIcon)) { (Resolve-Path $BrandIcon).Path } else { "$root\assets\hangar.ico" }
$appPubArgs = $publishArgs
if ($BrandName) { $appPubArgs = $publishArgs + @("/p:ApplicationIcon=$brandIconPath", "/p:Product=$BrandName") }
$brandLabel = if ($BrandName) { " (branded: $BrandName)" } else { "" }
Write-Host "== Publishing App (unified)$brandLabel ==" -ForegroundColor Cyan
dotnet publish "$root\src\Master\Master.csproj" @appPubArgs -o "$root\publish\app"

# The unattended host worker + Windows service (headless). The service finds the worker
# exe (FtdRemoteClient.exe) next to itself, so both live in publish\client.
Write-Host '== Publishing Client worker ==' -ForegroundColor Cyan
dotnet publish "$root\src\Client\Client.csproj" @publishArgs -o "$root\publish\client"
Write-Host '== Publishing Service ==' -ForegroundColor Cyan
dotnet publish "$root\src\Service\Service.csproj" @publishArgs -o "$root\publish\client"

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
$appOut   = if ($BrandName) { (($BrandName -replace '[^\w\-]', '_')) + "-Setup-$ver" } else { "Hangar-Setup-$ver" }
$agentOut = "HangarAgent-Setup-$ver"

# @(...) keeps these as JSON arrays; the app parser also tolerates a bare object.
# Components: "app" = the unified attended app; "client" = the unattended worker+service.
$manifest = [ordered]@{
    version        = $ver
    notes          = $Notes
    app            = @( New-Entry "$root\publish\app\RemoteControl.exe" )
    client         = @(
        New-Entry "$root\publish\client\FtdRemoteClient.exe"
        New-Entry "$root\publish\client\FtdRemoteService.exe"
    )
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
    $env:HANGAR_BRAND_NAME = $BrandName
    $env:HANGAR_BRAND_ICON = $brandIconPath
    $env:HANGAR_OUTFILE = "$safe-Setup-$ver"
} else {
    Remove-Item Env:HANGAR_BRAND_NAME, Env:HANGAR_BRAND_ICON, Env:HANGAR_OUTFILE -ErrorAction SilentlyContinue
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
    Write-Host "`nTo publish the update to the Pi:" -ForegroundColor Yellow
    Write-Host "  scp publish\update\* sim@raspberrypi:~/remote-desktop/server/update/"
    Write-Host "  (or re-run:  build.ps1 -PushTo sim@raspberrypi )"
}
