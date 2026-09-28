# Builds a release of the .NET DMC: dist\GlassLink-<version>\ (one self-contained GlassLink.exe, the status page,
# the DU firmware image, config.example.json), the same as a zip, and - when Inno Setup 6 is installed - the
# installer dist\GlassLink-<version>-setup.exe from installer\GlassLink.iss.
#
#   tools\build-release.ps1 [-NoInstaller]
#
# The running DMC is asked to stop first (its files are in use otherwise); start it again afterwards.
param([switch]$NoInstaller)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = (Get-Content "$root\VERSION" -Raw).Trim()
$build = (git -C $root describe --always --dirty --abbrev=7 --exclude '*').Trim()
$out = "$root\dist\GlassLink-$version"

$running = "$root\dotnet\src\GlassLink.Dmc\bin\Release\net10.0-windows10.0.26100.0\GlassLink.exe"
if (Get-Process GlassLink -ErrorAction SilentlyContinue) {
    Write-Host "stopping the running DMC"
    & $running --quit
}

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish "$root\dotnet\src\GlassLink.Dmc" -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $out -nologo -v q
if ($LASTEXITCODE) { exit $LASTEXITCODE }
Remove-Item "$out\*.xml", "$out\*.pdb" -ErrorAction SilentlyContinue
# Microsoft's SimConnect.dll is never shipped (#58): a local developer copy would otherwise land here. The DMC finds it
# in the MSFS SDK or in %LOCALAPPDATA%\GlassLink (see README, "SimConnect").
Remove-Item "$out\SimConnect.dll" -ErrorAction SilentlyContinue
Set-Content "$out\BUILD" $build -Encoding ascii
Copy-Item "$root\CHANGELOG.md" $out
if (Test-Path "$root\firmware\build\glasslink_du.bin") {
    New-Item -ItemType Directory -Force "$out\firmware" | Out-Null
    Copy-Item "$root\firmware\build\glasslink_du.bin" "$out\firmware\"
} else {
    Write-Warning "firmware\build\glasslink_du.bin not found: the release cannot update DUs"
}

$zip = "$root\dist\GlassLink-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "$out\*" -DestinationPath $zip
Write-Host "built $out ($build) and $zip"

$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($NoInstaller) { return }
if ($iscc) {
    & $iscc "/DVersion=$version" "/DBuild=$build" "$root\installer\GlassLink.iss"
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
    Write-Host "built $root\dist\GlassLink-$version-setup.exe"
} else {
    Write-Host "Inno Setup 6 not found (winget install JRSoftware.InnoSetup): only the zip was built"
}
