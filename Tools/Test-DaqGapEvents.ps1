$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('daq-gap-test-' + [guid]::NewGuid().ToString('N') + '.log')
try {
    $lines = @('2026-09-10 00:00:00.000 start')
    foreach ($event in @(@(1,250),@(2,251),@(2,251),@(3,1001),@(4,5001))) {
        $lines += '2026-09-10 00:00:01.000 FIELD FieldMetric DAQ_LIVENESS Device=Dev1 GapEvent={0} GapIntervalMs={1} Generation=2 RunId=fixture RunEpoch=1' -f $event[0],$event[1]
    }
    $lines += '2026-09-10 00:00:02.000 FIELD FieldMetric DAQ_LIVENESS Device=Dev1 GapEvent=5 GapIntervalMs=6000 Generation=2 RunId=fixture RunEpoch=1'
    [IO.File]::WriteAllLines($fixture, $lines)
    $result = @(& (Join-Path $PSScriptRoot 'Measure-DaqGapEvents.ps1') -LogPath $fixture -From '2026-09-10 00:00:00' -Until '2026-09-10 00:00:02' -Phase Learning)
    if ($result.Count -ne 2 -or $result[0].Over250ms -ne 3 -or $result[0].Over1000ms -ne 2 -or
        $result[0].Over5000ms -ne 1 -or $result[1].Over250ms -ne 0) { throw 'Gap deduplication, threshold or half-open window failed.' }
    $rejected = $false
    try { & (Join-Path $PSScriptRoot 'Measure-DaqGapEvents.ps1') -LogPath $fixture -From '2026-09-09' -Until '2026-09-10' -Phase Unknown | Out-Null }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'Uncovered window accepted.' }
    'PASS DaqGapEvents 2/2'
}
finally { if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture } }
