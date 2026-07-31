# Build everything: publish both apps as single-file self-contained exes,
# then compile the two Inno Setup installers.
#
#   powershell -ExecutionPolicy Bypass -File build.ps1
#
# Outputs:
#   publish\client\RemoteDesktopClient.exe   publish\master\RemoteDesktopMaster.exe
#   installer\dist\RemoteDesktopClient-Setup-<ver>.exe   (+ Master)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# Make dotnet + ISCC resolvable even in a fresh shell.
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' +
            [Environment]::GetEnvironmentVariable('Path','User')

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
    '/p:EnableCompressionInSingleFile=true','-nologo'
)

Write-Host '== Publishing Client ==' -ForegroundColor Cyan
dotnet publish "$root\src\Client\Client.csproj" @publishArgs -o "$root\publish\client"

Write-Host '== Publishing Master ==' -ForegroundColor Cyan
dotnet publish "$root\src\Master\Master.csproj" @publishArgs -o "$root\publish\master"

# The unattended Windows service. Published into the SAME folder as the client so
# the supervisor finds FtdRemoteClient.exe next to itself (AppContext.BaseDirectory).
Write-Host '== Publishing Service ==' -ForegroundColor Cyan
dotnet publish "$root\src\Service\Service.csproj" @publishArgs -o "$root\publish\client"

$iscc = Find-ISCC
Write-Host "== Compiling installers ($iscc) ==" -ForegroundColor Cyan
& $iscc "$root\installer\client.iss"
& $iscc "$root\installer\master.iss"

Write-Host "`nDone. Installers:" -ForegroundColor Green
Get-ChildItem "$root\installer\dist\*.exe" | ForEach-Object {
    Write-Host ("  {0}  ({1} MB)" -f $_.Name, [math]::Round($_.Length/1MB,1))
}
