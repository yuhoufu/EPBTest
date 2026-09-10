#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$LogPath,
    [Parameter(Mandatory = $true)][datetime]$From,
    [Parameter(Mandatory = $true)][datetime]$Until,
    [Parameter(Mandatory = $true)][ValidateSet('Learning','Formal','Mixed','Unknown')][string]$Phase,
    [string[]]$Devices = @('Dev1','Dev2')
)
$ErrorActionPreference = 'Stop'
if ($Until -le $From) { throw 'Until must be after From.' }
$events = @{}
$first = $null
$last = $null
foreach ($line in [IO.File]::ReadLines((Resolve-Path -LiteralPath $LogPath).Path)) {
    if ($line.Length -lt 23) { continue }
    $time = [datetime]::MinValue
    if (-not [datetime]::TryParseExact($line.Substring(0,23), 'yyyy-MM-dd HH:mm:ss.fff',
        [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$time)) { continue }
    if ($null -eq $first) { $first = $time }
    $last = $time
    if ($time -lt $From -or $time -ge $Until) { continue }
    if ($line -notmatch 'FieldMetric DAQ_LIVENESS .*Device=(\S+).*GapEvent=(\d+) GapIntervalMs=([\d.]+) Generation=(\d+).*RunId=(\S+) RunEpoch=(\d+)') { continue }
    $device = $Matches[1]
    $key = '{0}|{1}|{2}|{3}|{4}' -f $device,$Matches[5],$Matches[6],$Matches[4],$Matches[2]
    $events[$key] = [pscustomobject]@{ Device=$device; Time=$time; GapMs=[double]::Parse($Matches[3], [Globalization.CultureInfo]::InvariantCulture); Identity=$key }
}
if ($null -eq $first -or $From -lt $first -or $Until -gt $last) { throw 'Requested window is not covered by log timestamps.' }
$hours = ($Until - $From).TotalHours
foreach ($device in $Devices) {
    $selected = @($events.Values | Where-Object Device -eq $device)
    $count250 = @($selected | Where-Object GapMs -gt 250).Count
    [pscustomobject]@{
        Device=$device; Phase=$Phase; From=$From.ToString('o'); Until=$Until.ToString('o')
        ObservationHours=$hours; Over250ms=$count250
        Over1000ms=@($selected | Where-Object GapMs -gt 1000).Count
        Over5000ms=@($selected | Where-Object GapMs -gt 5000).Count
        EventsPerObservationHour=$count250/$hours
        Events=@($selected | Sort-Object Time)
        Caveat='Observation window, not proven energized exposure; phase supplied from incident timeline. Missing metrics do not prove zero physical gaps.'
    }
}
