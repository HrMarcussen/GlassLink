# Turns the aircraft in X-Plane 12 through its web API, so the PFD heading tape and the ND compass change on every
# frame: a frame-rate test for the displays on the DUs (watch them on the status page or with GlassLink.Bench run).
# The aircraft's own position is overridden while it runs (sim/operation/override/override_planepath; the ToLiss
# A339 ignores heading writes otherwise) and heading and override are put back at the end. Its IRS must be aligned for
# the displays to show a heading (ToLiss: ADIRUs to NAV, then the command AirbusFBW/ADIRU_fast_align).
# Usage: powershell -File tools\xplane-turn.ps1 [-Seconds 25] [-Step 0.6] [-Rate 30]
param([double]$Seconds = 25, [double]$Step = 0.6, [double]$Rate = 30)
$ErrorActionPreference = 'Stop'
$api = 'http://127.0.0.1:8086/api/v3'
$culture = [Globalization.CultureInfo]::InvariantCulture

function Get-DatarefId([string]$name) { (Invoke-RestMethod "$api/datarefs?filter[name]=$name").data[0].id }
function Get-Dataref([long]$id) { (Invoke-RestMethod "$api/datarefs/$id/value").data }
function Set-Dataref([long]$id, [string]$json) { Invoke-RestMethod -Method Patch "$api/datarefs/$id/value" -ContentType 'application/json' -Body ('{"data":' + $json + '}') | Out-Null }

$psi = Get-DatarefId 'sim/flightmodel/position/psi'
$override = Get-DatarefId 'sim/operation/override/override_planepath'
$heading = [double](Get-Dataref $psi)
$flags = @(Get-Dataref $override)
$on = $flags.Clone(); $on[0] = 1
Set-Dataref $override (ConvertTo-Json @($on) -Compress)
try {
    $clock = [Diagnostics.Stopwatch]::StartNew(); $n = 0
    while ($clock.Elapsed.TotalSeconds -lt $Seconds) {
        $n++
        Set-Dataref $psi ((($heading + $n * $Step) % 360).ToString($culture))
        $wait = $n / $Rate * 1000 - $clock.ElapsedMilliseconds
        if ($wait -gt 0) { Start-Sleep -Milliseconds $wait }
    }

    "{0} headings in {1:0.0} s ({2:0.0} a second)" -f $n, $clock.Elapsed.TotalSeconds, ($n / $clock.Elapsed.TotalSeconds)
}
finally {
    Set-Dataref $psi $heading.ToString($culture)
    Set-Dataref $override (ConvertTo-Json @($flags) -Compress)
    "heading back to {0:0.0}, override as before" -f $heading
}
