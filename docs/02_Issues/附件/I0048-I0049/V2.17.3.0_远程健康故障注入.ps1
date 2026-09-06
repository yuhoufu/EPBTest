param([string]$InstallRoot='C:\Program Files (x86)\MTTFTest',
    [string]$EvidenceDirectory='C:\EPB-I0049-Validation\FormalHealthEvidence')
$ErrorActionPreference='Stop'
if ($env:COMPUTERNAME -ne 'MT-20251206JXCQ') { throw 'This fault injection is restricted to the authorized test host.' }
$expected=Join-Path $InstallRoot 'Current\MTTFTest.Watchdog.exe'
$agentPath=Join-Path $InstallRoot 'Current\MTTFTest.SessionAgent.exe'
$healthTask='MTTFTestRecoveryHealth'
$inhibit=Join-Path $env:ProgramData 'MTTFTest\maintenance-inhibit.json'
$results=[Collections.Generic.List[object]]::new()
New-Item -ItemType Directory $EvidenceDirectory -Force | Out-Null
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class EpbHealthFault {
 [DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr h);
 [DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr h);
}
'@
function Record($name,$detail) {
    $results.Add([pscustomobject]@{ name=$name; passed=$true; utc=[DateTime]::UtcNow.ToString('O'); detail=$detail })
    Write-Output "PASS $name $detail"
}
function Wait-Condition([scriptblock]$condition,[int]$seconds,[string]$reason) {
    $deadline=[DateTime]::UtcNow.AddSeconds($seconds)
    do { $value=& $condition; if ($value) { return $value }; Start-Sleep -Milliseconds 250 } while ([DateTime]::UtcNow -lt $deadline)
    throw $reason
}
function Service-Identity {
    $service=Get-RecoverySupervisorIdentity
    if ($null -eq $service -or $service.PathName.Trim('"') -ne $expected) { throw 'UnexpectedServiceIdentity' }
    return $service
}
function Exact-Process([int]$processId,[string]$path) {
    $process=Get-Process -Id $processId
    [void]$process.Handle
    if ($process.Path -ne $path) { $process.Dispose(); throw 'UnexpectedProcessIdentity' }
    return $process
}
$passed=$false
$failure=$null
$createdInhibit=$false
try {
    $parseErrors=$null
    $healthAst=[Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $InstallRoot 'Current\Deployment\Test-MTTFTest-RecoveryHealth.ps1'),[ref]$null,[ref]$parseErrors)
    if ($parseErrors.Count) { throw 'InstalledHealthScriptParseError' }
    foreach ($name in @('Invoke-BoundedSupervisorSc','Get-RecoverySupervisorIdentity')) {
        $definition=$healthAst.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name },$true)
        if ($null -eq $definition) { throw "MissingInstalledQueryFunction:$name" }
        Invoke-Expression $definition.Extent.Text
    }
    if (Get-Process MTTFTest -ErrorAction SilentlyContinue) { throw 'Main must be closed before service fault injection.' }
    if (Test-Path $inhibit) { throw 'ExistingMaintenanceMustNotBeOverridden' }
    $service=Service-Identity
    if ($service.State -ne 'Running') { throw 'SupervisorNotRunningAfterInstall' }
    [xml]$definition=Export-ScheduledTask -TaskName $healthTask
    if ($definition.Task.Principals.Principal.UserId -ne 'S-1-5-18' -or
        $definition.Task.Settings.ExecutionTimeLimit -ne 'PT45S' -or
        $definition.Task.Settings.MultipleInstancesPolicy -ne 'IgnoreNew' -or
        $null -eq $definition.Task.Triggers.SelectSingleNode("*[local-name()='BootTrigger']") -or
        $definition.Task.Triggers.TimeTrigger.Repetition.Interval -ne 'PT1M') { throw 'HealthTaskDefinitionMismatch' }
    $definition.Save((Join-Path $EvidenceDirectory 'health-task.xml'))
    Record 'InstalledMinuteTaskDefinition' 'SYSTEM;Boot+PT1M;IgnoreNew;PT45S'
    & (Join-Path $InstallRoot 'Current\Deployment\Test-MTTFTest-RecoveryHealth.ps1') -InstallRoot $InstallRoot -CheckOnly
    Record 'InstalledSupervisorHealthProbe' "PID=$($service.ProcessId)"

    $previousPid=[int]$service.ProcessId
    Stop-Service MTTFTestSupervisor
    $watch=[Diagnostics.Stopwatch]::StartNew()
    $restarted=Wait-Condition { $s=Service-Identity; if ($s.State -eq 'Running' -and $s.ProcessId -gt 0 -and $s.ProcessId -ne $previousPid) { $s } } 100 'Natural minute task did not start stopped service.'
    Record 'NaturalMinuteRestartsStoppedSupervisor' "OldPID=$previousPid;NewPID=$($restarted.ProcessId);Seconds=$($watch.Elapsed.TotalSeconds)"

    Start-Sleep -Seconds 31
    $hung=Exact-Process ([int]$restarted.ProcessId) $expected
    $previousPid=$hung.Id
    if ([EpbHealthFault]::NtSuspendProcess($hung.Handle) -ne 0) { throw 'SuspendSupervisorFailed' }
    try {
        $watch.Restart()
        $replacement=Wait-Condition { $s=Service-Identity; if ($s.State -eq 'Running' -and $s.ProcessId -gt 0 -and $s.ProcessId -ne $previousPid) { $s } } 130 'Minute task did not replace unresponsive supervisor.'
        Record 'MinuteReplacesUnresponsiveSupervisor' "OldPID=$previousPid;NewPID=$($replacement.ProcessId);Seconds=$($watch.Elapsed.TotalSeconds)"
    } finally { if (-not $hung.HasExited) { [void][EpbHealthFault]::NtResumeProcess($hung.Handle) }; $hung.Dispose() }

    $agent=Wait-Condition { Get-Process MTTFTest.SessionAgent -ErrorAction SilentlyContinue | Where-Object Path -eq $agentPath | Select-Object -First 1 } 80 'Installed session agent missing.'
    Start-Sleep -Seconds 31
    [void]$agent.Handle
    $previousPid=$agent.Id
    if ([EpbHealthFault]::NtSuspendProcess($agent.Handle) -ne 0) { throw 'SuspendAgentFailed' }
    try {
        $replacement=Wait-Condition { Get-Process MTTFTest.SessionAgent -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $agentPath -and $_.Id -ne $previousPid } | Select-Object -First 1 } 90 'Installed supervisor did not restore unresponsive agent.'
        Record 'InstalledSupervisorRestoresUnresponsiveAgent' "OldPID=$previousPid;NewPID=$($replacement.Id)"
    } finally { if (-not $agent.HasExited) { [void][EpbHealthFault]::NtResumeProcess($agent.Handle) }; $agent.Dispose() }

    @{ schemaVersion=1; source='I0049 authorized isolated health test'; utc=[DateTime]::UtcNow.ToString('O') } | ConvertTo-Json | Set-Content $inhibit -Encoding UTF8
    $createdInhibit=$true
    Stop-Service MTTFTestSupervisor
    Start-ScheduledTask -TaskName $healthTask
    Start-Sleep -Seconds 6
    if ((Service-Identity).State -ne 'Stopped') { throw 'MaintenanceIgnored' }
    Record 'MaintenancePreventsServiceRestart' 'Stopped service remains stopped while persisted inhibit exists.'
    Move-Item -LiteralPath $inhibit -Destination (Join-Path $EvidenceDirectory 'test-maintenance-inhibit.json')
    $createdInhibit=$false
    Start-Service MTTFTestSupervisor
    Start-ScheduledTask -TaskName $healthTask
    Start-ScheduledTask -TaskName $healthTask
    Start-Sleep -Seconds 5
    if (Get-Process MTTFTest -ErrorAction SilentlyContinue) { throw 'HealthCheckStartedMainWithoutIntent' }
    if (@(Get-Process MTTFTest.SessionAgent -ErrorAction SilentlyContinue | Where-Object Path -eq $agentPath).Count -ne 1) { throw 'AgentNotSingleton' }
    Record 'RepeatedHealthCheckNoMainOrDuplicateAgent' 'Main=0;SessionAgent=1'
    $passed=$true
} catch { $failure=$_.Exception.Message; Write-Output "FAIL $failure" }
finally {
    if ($createdInhibit -and (Test-Path $inhibit)) {
        Move-Item -LiteralPath $inhibit -Destination (Join-Path $EvidenceDirectory 'failed-test-maintenance-inhibit.json')
        Start-Service MTTFTestSupervisor -ErrorAction SilentlyContinue
    }
    @{ schemaVersion=1; host=$env:COMPUTERNAME; passed=$passed; tests=@($results.ToArray()); failure=$failure;
       completedUtc=[DateTime]::UtcNow.ToString('O'); hardwareTestPerformed=$false } | ConvertTo-Json -Depth 8 |
       Set-Content (Join-Path $EvidenceDirectory 'health-result.json') -Encoding UTF8
}
if (-not $passed) { exit 1 }
