# Makes the key that signs released DU firmware. Run it once, by hand (docs/releasing.md):
#
#   tools\new-firmware-key.ps1 [-Folder <a folder outside the repository>]
#
# It writes the private key to <Folder>\glasslink-firmware-key.pem without showing it, and its public half to
# firmware\signing-key.pub.pem in this repository (commit that one: the release workflow checks every signature
# against it). Then:
#   1. GitHub: Settings > Environments > release > Environment secrets > Add secret FIRMWARE_SIGNING_KEY, with the
#      whole text of the .pem file as its value.
#   2. Keep a copy of the .pem file in your password manager. It cannot be made again: a DU running released firmware
#      installs updates over USB only when they are signed with this key, for good.
#   3. Delete the .pem file and its folder.
# It needs the ESP-IDF Python environment (C:\Espressif) or `pip install esptool==4.12.0`.
param([string]$Folder = (Join-Path $env:USERPROFILE 'GlassLink-signing-key'))
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$public = Join-Path $root 'firmware\signing-key.pub.pem'
$private = Join-Path $Folder 'glasslink-firmware-key.pem'
if ((Test-Path $public) -or (Test-Path $private)) {
    throw "a key exists already ($public or $private): DUs running a release trust only the key they were signed with, so it is never replaced by accident"
}
# inside the repository = a folder above it holds this script (compared by what is there, not by how the path is spelt)
for ($dir = [IO.Path]::GetFullPath($Folder); $dir; $dir = Split-Path $dir -Parent) {
    if (Test-Path (Join-Path $dir 'tools\new-firmware-key.ps1')) {
        throw "the private key must not be made inside the repository: give -Folder a place outside $root"
    }
}

$python = @(Get-ChildItem 'C:\Espressif\python_env\*\Scripts\python.exe' -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName) + @('python') | Select-Object -First 1
New-Item -ItemType Directory -Force $Folder | Out-Null
& $python -m espsecure generate_signing_key --version 2 --scheme rsa3072 $private | Out-Null
if ($LASTEXITCODE) { throw "espsecure could not make the key (is esptool installed for $python?)" }
& $python -m espsecure extract_public_key --version 2 --keyfile $private $public | Out-Null
if ($LASTEXITCODE) { throw "espsecure could not write the public key" }

Write-Host "Private key: $private (not shown here)"
Write-Host "Public key:  $public (commit it)"
Write-Host ""
Write-Host "Next: 1. GitHub release environment secret FIRMWARE_SIGNING_KEY = the whole text of the private key file"
Write-Host "      2. a copy in your password manager"
Write-Host "      3. delete $Folder"
