param([string]$InstallRoot='C:\Program Files (x86)\MTTFTest',
      [string]$EvidenceDirectory='C:\EPB-I0049-Validation\FinalUiEvidence')
$ErrorActionPreference='Stop'
if ($env:COMPUTERNAME -ne 'MT-20251206JXCQ') { throw 'Wrong test host' }
New-Item -ItemType Directory $EvidenceDirectory -Force | Out-Null
$expected=Join-Path $InstallRoot 'Current\MTTFTest.exe'
if (Get-Process MTTFTest -ErrorAction SilentlyContinue) { throw 'Unexpected existing main process' }
$result=[ordered]@{ startedUtc=[DateTime]::UtcNow.ToString('O'); installedMain=$expected;
    version=(Get-Item $expected).VersionInfo.FileVersion; hardwareTestPerformed=$false;
    trialStarted=$false; launchPassed=$false; idleClosePassed=$false }
$main=$null
try {
    $launcher=Start-Process -FilePath (Join-Path $InstallRoot 'Current\MTTFTest.Watchdog.exe') `
        -ArgumentList '--launch-main' -WindowStyle Hidden -PassThru
    try { if (-not $launcher.WaitForExit(15000)) { throw 'LauncherTimeout' }; if ($launcher.ExitCode -ne 0) { throw "LauncherExit=$($launcher.ExitCode)" } }
    finally { $launcher.Dispose() }
    $until=[DateTime]::UtcNow.AddSeconds(30)
    do {
        $main=Get-Process MTTFTest -ErrorAction SilentlyContinue | Where-Object Path -eq $expected | Select-Object -First 1
        if ($main -and $main.MainWindowHandle -ne [IntPtr]::Zero) { break }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $until)
    if (-not $main -or $main.MainWindowHandle -eq [IntPtr]::Zero) { throw 'MainWindowNotVisible' }
    Start-Sleep -Seconds 3
    $main.Refresh()
    $result.processId=$main.Id
    $result.processStartTicks=$main.StartTime.ToUniversalTime().Ticks
    $result.windowTitle=$main.MainWindowTitle
    $result.launchPassed=$true
    try {
        Add-Type -AssemblyName System.Drawing,System.Windows.Forms
        $bounds=[Windows.Forms.Screen]::PrimaryScreen.Bounds
        $bitmap=New-Object Drawing.Bitmap($bounds.Width,$bounds.Height)
        $graphics=[Drawing.Graphics]::FromImage($bitmap)
        try { $graphics.CopyFromScreen($bounds.Location,[Drawing.Point]::Empty,$bounds.Size); $bitmap.Save((Join-Path $EvidenceDirectory 'idle-main.png')) }
        finally { $graphics.Dispose(); $bitmap.Dispose() }
    } catch { $result.screenshotUnavailable=$_.Exception.Message }
    $clock=[Diagnostics.Stopwatch]::StartNew()
    if (-not $main.CloseMainWindow()) { throw 'NormalWindowCloseNotAccepted' }
    $result.idleClosePassed=$main.WaitForExit(15000)
    $result.idleCloseMilliseconds=$clock.ElapsedMilliseconds
    if (-not $result.idleClosePassed) { throw 'IdleMainDidNotExitNormallyWithin15Seconds' }
    $result.mainExitCode=$main.ExitCode
} catch { $result.failure=$_.Exception.Message }
finally {
    $result.completedUtc=[DateTime]::UtcNow.ToString('O')
    $result | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $EvidenceDirectory 'ui-result.json') -Encoding UTF8
    if ($main) { $main.Dispose() }
}
# This checks a cold idle main window only. It does not claim that a running
# monitor's physical shutdown or data drain was exercised without hardware.
if (-not $result.launchPassed -or -not $result.idleClosePassed) { exit 1 }
