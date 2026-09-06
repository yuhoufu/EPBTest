[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$InstallRoot, [switch]$CheckOnly)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($InstallRoot)
$stateRoot = Join-Path $env:ProgramData 'MTTFTest'
$inhibit = Join-Path $stateRoot 'maintenance-inhibit.json'
$expected = Join-Path $root 'Current\MTTFTest.Watchdog.exe'
$mutex = New-Object Threading.Mutex($false, 'Global\MTTFTest.MaintenanceHealth.V1')
$held = $false
try {
    try { $held = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $held = $true }
    if (-not $held -or (Test-Path -LiteralPath $inhibit)) { Write-Output 'MaintenanceOrCheckInProgress'; return }
    Add-Type -Path (Join-Path $root 'Current\MTTFTest.Watchdog.Protocol.dll')
    $service = Get-CimInstance Win32_Service -Filter "Name='MTTFTestSupervisor'"
    if ($null -eq $service) { throw 'SupervisorNotInstalled' }
    if ($service.PathName.Trim('"') -ne $expected) { throw 'SupervisorServicePathMismatch' }
    if ($service.State -eq 'Stopped') {
        if (-not $CheckOnly) { Start-Service MTTFTestSupervisor }
        Write-Output 'SupervisorStartRequested'; return
    }
    if ([int]$service.ProcessId -le 0) { throw 'SupervisorTransitionPending' }
    $owner = Get-Process -Id ([int]$service.ProcessId)
    try {
        [void]$owner.Handle
        if ($owner.Path -ne $expected) { throw 'SupervisorProcessPathMismatch' }
        $started = $owner.StartTime.ToUniversalTime().Ticks
        if ([DateTime]::UtcNow.Ticks - $started -lt [TimeSpan]::FromSeconds(30).Ticks) {
            Write-Output 'SupervisorStarting'; return
        }
        $lastReason = ''
        for ($attempt = 0; $attempt -lt 2; $attempt++) {
            try {
                $snapshot = [MTTFTest.Watchdog.Protocol.RecoveryHealthEndpoint]::Probe(
                    [MTTFTest.Watchdog.Protocol.RecoveryHealthEndpoint]::SupervisorPipe, 2000)
                if ($snapshot.Matches($owner.Id, $started, [DateTime]::UtcNow, 15)) {
                    $snapshot | ConvertTo-Json -Compress; return
                }
                $lastReason = 'SupervisorProgressStalled'
            } catch { $lastReason = $_.Exception.GetBaseException().Message }
            if ($attempt -eq 0) { Start-Sleep -Seconds 3 }
        }
        if ($CheckOnly) { throw $lastReason }
        if (Test-Path -LiteralPath $inhibit) { return }
        $current = Get-CimInstance Win32_Service -Filter "Name='MTTFTestSupervisor'"
        if ($current.ProcessId -ne $owner.Id -or $owner.HasExited -or
            $owner.StartTime.ToUniversalTime().Ticks -ne $started) { throw 'SupervisorIdentityChanged' }
        # Only the exact SCM-owned supervisor is replaced. Hardware workers/main are untouched.
        $owner.Kill()
        if (-not $owner.WaitForExit(5000)) { throw 'SupervisorExitPending' }
        Start-Service MTTFTestSupervisor -ErrorAction SilentlyContinue
        [void](New-Item -ItemType Directory -Path $stateRoot -Force)
        @{ Utc=[DateTime]::UtcNow.ToString('O'); Action='SupervisorRestartRequested';
            ProcessId=$owner.Id; StartTicks=$started; Reason=$lastReason } |
            ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $stateRoot 'recovery-health.log') -Encoding UTF8
        Write-Output 'SupervisorRestartRequested'
    } finally { $owner.Dispose() }
} finally {
    if ($held) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
