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
    [string]$Notes = ''
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# Make dotnet + ISCC resolvable even in a fresh shell.
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' +
            [Environment]::GetEnvironmentVariable('Path','User')

$ver = (Get-Content "$root\VERSION" -Raw).Trim()
if ($ver -notmatch '^\d+\.\d+\.\d+$') { throw "VERSION file must contain x.y.z (got '$ver')." }
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

$publishArgs = @(
    '-c','Release','-r','win-x64','--self-contained','true',
    '/p:PublishSingleFile=true','/p:IncludeNativeLibrariesForSelfExtract=true',
    '/p:EnableCompressionInSingleFile=true',"/p:Version=$ver",'-nologo'
)

Write-Host '== Publishing Client ==' -ForegroundColor Cyan
dotnet publish "$root\src\Client\Client.csproj" @publishArgs -o "$root\publish\client"

Write-Host '== Publishing Master ==' -ForegroundColor Cyan
dotnet publish "$root\src\Master\Master.csproj" @publishArgs -o "$root\publish\master"

# The unattended Windows service. Published into the SAME folder as the client so
# the supervisor finds FtdRemoteClient.exe next to itself (AppContext.BaseDirectory).
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

# @(...) keeps these as JSON arrays; the client parser also tolerates a bare object.
$manifest = [ordered]@{
    version = $ver
    notes   = $Notes
    master  = @( New-Entry "$root\publish\master\FtdRemoteMaster.exe" )
    client  = @(
        New-Entry "$root\publish\client\FtdRemoteClient.exe"
        New-Entry "$root\publish\client\FtdRemoteService.exe"
    )
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content "$upd\manifest.json" -Encoding utf8
Write-Host "   manifest.json + $((Get-ChildItem $upd -Filter *.exe).Count) exe(s) staged in $upd"

# ---- installers --------------------------------------------------------------------
$iscc = Find-ISCC
Write-Host "== Compiling installers ($iscc) ==" -ForegroundColor Cyan
& $iscc "/DAppVersion=$ver" "$root\installer\client.iss"
& $iscc "/DAppVersion=$ver" "$root\installer\master.iss"

Write-Host "`nInstallers:" -ForegroundColor Green
Get-ChildItem "$root\installer\dist\*.exe" | ForEach-Object {
    Write-Host ("  {0}  ({1} MB)" -f $_.Name, [math]::Round($_.Length/1MB,1))
}

# ---- optional: push the update package to the Pi -----------------------------------
if ($PushTo) {
    Write-Host "`n== Pushing update to $PushTo:$PiUpdateDir ==" -ForegroundColor Cyan
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
