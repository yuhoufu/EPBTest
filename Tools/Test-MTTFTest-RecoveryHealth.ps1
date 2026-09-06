[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$InstallRoot, [switch]$CheckOnly)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($InstallRoot)
$stateRoot = Join-Path $env:ProgramData 'MTTFTest'
$inhibit = Join-Path $stateRoot 'maintenance-inhibit.json'
$expected = Join-Path $root 'Current\MTTFTest.Watchdog.exe'

function Invoke-BoundedSupervisorSc([ValidateSet('queryex','start')][string]$Verb) {
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = Join-Path $env:SystemRoot 'System32\sc.exe'
    $info.Arguments = "$Verb MTTFTestSupervisor"
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $probe = [Diagnostics.Process]::Start($info)
    try {
        if (-not $probe.WaitForExit(5000)) {
            try { $probe.Kill() } catch { }
            throw "SupervisorScTimeout:$Verb"
        }
        $output = $probe.StandardOutput.ReadToEnd()
        $errorText = $probe.StandardError.ReadToEnd()
        if ($probe.ExitCode -ne 0 -and -not ($Verb -eq 'start' -and $probe.ExitCode -eq 1056)) {
            throw "SupervisorScFailed:$Verb;Exit=$($probe.ExitCode);$output;$errorText"
        }
        return $output
    } finally { $probe.Dispose() }
}

function Get-RecoverySupervisorIdentity {
    # Win32_Service can block behind a suspended service and outlive the task's
    # 45-second budget. Query the SCM through an independently bounded helper.
    $status = Invoke-BoundedSupervisorSc 'queryex'
    $pidMatch = [regex]::Match($status, '(?m)^\s*PID\s*:\s*(\d+)')
    $stateMatch = [regex]::Match($status, '(?m)^\s*STATE\s*:\s*(\d+)')
    if (-not $pidMatch.Success -or -not $stateMatch.Success) { throw 'SupervisorScIdentityUnparseable' }
    $imagePath = (Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\MTTFTestSupervisor' -Name ImagePath).ImagePath
    return [pscustomobject]@{
        ProcessId = [int]$pidMatch.Groups[1].Value
        State = if ($stateMatch.Groups[1].Value -eq '1') { 'Stopped' } elseif ($stateMatch.Groups[1].Value -eq '4') { 'Running' } else { 'Transition' }
        PathName = [Environment]::ExpandEnvironmentVariables([string]$imagePath)
    }
}

$mutex = New-Object Threading.Mutex($false, 'Global\MTTFTest.MaintenanceHealth.V1')
$held = $false
try {
    try { $held = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $held = $true }
    if (-not $held -or (Test-Path -LiteralPath $inhibit)) { Write-Output 'MaintenanceOrCheckInProgress'; return }
    Add-Type -Path (Join-Path $root 'Current\MTTFTest.Watchdog.Protocol.dll')
    $service = Get-RecoverySupervisorIdentity
    if ($service.PathName.Trim('"') -ne $expected) { throw 'SupervisorServicePathMismatch' }
    if ($service.State -eq 'Stopped') {
        if (-not $CheckOnly) { [void](Invoke-BoundedSupervisorSc 'start') }
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
        $current = Get-RecoverySupervisorIdentity
        if ($current.ProcessId -ne $owner.Id -or $owner.HasExited -or
            $owner.StartTime.ToUniversalTime().Ticks -ne $started) { throw 'SupervisorIdentityChanged' }
        # Only the exact SCM-owned supervisor is replaced. Hardware workers/main are untouched.
        $owner.Kill()
        if (-not $owner.WaitForExit(5000)) { throw 'SupervisorExitPending' }
        [void](Invoke-BoundedSupervisorSc 'start')
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
