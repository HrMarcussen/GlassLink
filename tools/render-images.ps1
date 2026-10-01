# Renders the README's images: every docs\images\src\<name>.html becomes docs\images\<name>.png, at twice its CSS
# size (crisp on high-DPI screens), with headless Microsoft Edge. Each page sets its size in a meta tag:
#   <meta name="render-size" content="1280x420">
# Usage: powershell -File tools\render-images.ps1 [name ...]
param([string[]] $Names)
$ErrorActionPreference = 'Stop'
$Names = @($Names | ForEach-Object { $_ -split ',' } | Where-Object { $_ })   # "a,b" arrives as one string through -File
$root = Split-Path $PSScriptRoot -Parent
$src = Join-Path $root 'docs\images\src'
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'Microsoft Edge not found' }
$profile = Join-Path $env:TEMP 'glasslink-render-profile'     # its own profile, or a running Edge takes the job over
$pages = Get-ChildItem $src -Filter *.html | Where-Object { -not $Names -or $Names -contains $_.BaseName }
foreach ($page in $pages) {
    $size = [regex]::Match((Get-Content $page.FullName -Raw), 'name="render-size" content="(\d+)x(\d+)"')
    if (-not $size.Success) { Write-Warning "$($page.Name): no render-size meta tag"; continue }
    $out = Join-Path $root "docs\images\$($page.BaseName).png"
    $url = 'file:///' + ($page.FullName -replace '\\', '/')
    $arguments = @('--headless=new', '--disable-gpu', '--hide-scrollbars', "--user-data-dir=$profile", '--force-device-scale-factor=2',
        "--window-size=$($size.Groups[1].Value),$($size.Groups[2].Value)", '--virtual-time-budget=3000', "--screenshot=$out", $url)
    Start-Process -FilePath $edge -ArgumentList $arguments -Wait -WindowStyle Hidden
    "{0} -> {1} ({2:N0} KB)" -f $page.Name, $out, ((Get-Item $out).Length / 1KB)
}
