# Builds a release of the .NET DMC: dist\GlassLink-<version>\ (one self-contained GlassLink.exe, the status page,
# the DU firmware image, config.example.json), the same as a zip, and - when Inno Setup 6 is installed - the
# installer dist\GlassLink-<version>-setup.exe from installer\GlassLink.iss.
#
#   tools\build-release.ps1 [-NoInstaller] [-Stage all|publish|package] [-Strict]
#
# -Stage publish builds only the folder, -Stage package makes the zip and the installer from it: the release workflow
# signs GlassLink.exe in between (.github/workflows/release.yml, docs/releasing.md). The running DMC is asked to stop
# first (its files are in use otherwise); start it again afterwards. -Strict (the release workflow on a tag) stops
# instead of warning when SimConnect.dll, the DU firmware image or Inno Setup is missing.
param([switch]$NoInstaller, [ValidateSet('all', 'publish', 'package')] [string]$Stage = 'all', [switch]$Strict)
$ErrorActionPreference = 'Stop'
function Missing([string]$what) { if ($Strict) { throw $what } else { Write-Warning $what } }
$root = Split-Path $PSScriptRoot -Parent
$version = (Get-Content "$root\VERSION" -Raw).Trim()
$build = (git -C $root describe --always --dirty --abbrev=7 --exclude '*').Trim()
$out = "$root\dist\GlassLink-$version"

$running = "$root\dotnet\src\GlassLink.Dmc\bin\Release\net10.0-windows10.0.26100.0\GlassLink.exe"
if ($Stage -ne 'package') {
if (Get-Process GlassLink -ErrorAction SilentlyContinue) {
    Write-Host "stopping the running DMC"
    & $running --quit
}

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish "$root\dotnet\src\GlassLink.Dmc" -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $out -nologo -v q
if ($LASTEXITCODE) { exit $LASTEXITCODE }
Remove-Item "$out\*.xml", "$out\*.pdb" -ErrorAction SilentlyContinue
# Microsoft's SimConnect.dll goes into the release, unmodified, as most MSFS add-ons ship it; it is never in the
# repository (#58, THIRD-PARTY-NOTICES.md). Taken from the developer's local copy (dotnet\lib, which the build already
# put next to the exe), a clone of the private build-deps repository next to this checkout, or an installed MSFS SDK.
if (-not (Test-Path "$out\SimConnect.dll")) {
    $sdkDll = @("$root\dotnet\lib\SimConnect.dll", "$root\..\build-deps\GlassLink\SimConnect.dll") + (@($env:MSFS2024_SDK, $env:MSFS_SDK) | Where-Object { $_ } | ForEach-Object { Join-Path $_ "SimConnect SDK\lib\SimConnect.dll" }) |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($sdkDll) { Copy-Item $sdkDll $out }
    else { Missing "no SimConnect.dll (dotnet\lib or the MSFS SDK): clone build-deps next to this checkout, or users of this release must install the SDK" }
}
Set-Content "$out\BUILD" $build -Encoding ascii
Copy-Item "$root\CHANGELOG.md", "$root\LICENSE", "$root\THIRD-PARTY-NOTICES.md" $out
if (Test-Path "$root\firmware\build\glasslink_du.bin") {
    New-Item -ItemType Directory -Force "$out\firmware" | Out-Null
    Copy-Item "$root\firmware\build\glasslink_du.bin" "$out\firmware\"
} else {
    Missing "firmware\build\glasslink_du.bin not found: the release cannot update DUs"
}
Write-Host "built $out ($build)"
}
if ($Stage -eq 'publish') { return }
if (-not (Test-Path "$out\GlassLink.exe")) { throw "$out\GlassLink.exe not found: run -Stage publish first" }

$zip = "$root\dist\GlassLink-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "$out\*" -DestinationPath $zip
Write-Host "built $zip"

$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($NoInstaller) { return }
if ($iscc) {
    & $iscc "/DVersion=$version" "/DBuild=$build" "$root\installer\GlassLink.iss"
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
    Write-Host "built $root\dist\GlassLink-$version-setup.exe"
} else {
    Missing "Inno Setup 6 not found (winget install JRSoftware.InnoSetup): only the zip was built"
}
