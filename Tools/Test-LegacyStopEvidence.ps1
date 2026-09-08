#requires -Version 5.1
param([Parameter(Mandatory=$true)][string]$EvidenceDirectory)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'LegacyStopEvidence.ps1')
$checkpoint=Get-Content (Join-Path $EvidenceDirectory 'legacy-stopped-checkpoint.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$receipt=Get-Content (Join-Path $EvidenceDirectory 'stop-safety.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$session=Get-Content (Join-Path $EvidenceDirectory 'stopped-session.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$request=Get-Content (Join-Path $EvidenceDirectory 'stop-request.json') -Raw -Encoding UTF8 | ConvertFrom-Json
function Copy-Json($Value) { $Value | ConvertTo-Json -Depth 30 | ConvertFrom-Json }
$passed=0
function Test-Case([string]$Name,[scriptblock]$Change,[string]$ExpectedError='') {
    $candidate=@{Checkpoint=(Copy-Json $checkpoint);Receipt=(Copy-Json $receipt);Session=(Copy-Json $session);
        Request=(Copy-Json $request);ExpectedExecutablePath=$session.ExecutablePath;
        ExpectedExecutableSha256=$checkpoint.ExecutableSha256;ProcessExited=$true;
        NowUtc=([DateTime]::new(([Math]::Max([long]$receipt.UpdatedUtcTicks,(Convert-LegacyEvidenceUtc $checkpoint.UpdatedUtc).Ticks)+[TimeSpan]::FromSeconds(1).Ticks),[DateTimeKind]::Utc))}
    & $Change $candidate
    $failure=''
    try { $result=Assert-LegacyStopEvidence @candidate } catch { $failure=$_.Exception.Message }
    if (($ExpectedError -eq '' -and $failure -ne '') -or
        ($ExpectedError -ne '' -and $failure -notlike "$ExpectedError*")) {throw "$Name failed: $failure"}
    if ($ExpectedError -eq '' -and ($result.PhysicalIsolationClaimed -or $result.AuthorizationMigrated)) {
        throw 'Stop validation invented isolation or migrated authorization'
    }
    $script:passed++
    Write-Output "PASS $Name"
}
Test-Case 'Captured complete stop despite false checkpoint flags' {param($c)}
foreach($flag in @('FinalSafetyResultCommitted','MotorsOff','PowerOff','PressureSafe','PersistenceDrained','LogicalQuiescent','DataContinuityVerified')) {
    $changedFlag=$flag
    Test-Case "Incomplete $flag" {param($c) $c.Receipt.$changedFlag=$false} 'LegacyStopIncomplete'
}
Test-Case 'String truth is not proof' {param($c) $c.Receipt.MotorsOff='true'} 'LegacyStopIncomplete'
Test-Case 'Running checkpoint rejected' {param($c) $c.Checkpoint.Armed=$true} 'LegacyStopCheckpointNotStopped'
Test-Case 'Paused checkpoint rejected' {param($c) $c.Checkpoint.GracefulPaused=$true} 'LegacyStopCheckpointNotStopped'
Test-Case 'New run rejects previous proof' {param($c) $c.Checkpoint.RunId=[Guid]::NewGuid().ToString('N')} 'LegacyStopIdentityMismatch'
Test-Case 'Other process rejected' {param($c) $c.Session.CurrentPid++} 'LegacyStopIdentityMismatch'
Test-Case 'Reused PID rejected' {param($c) $c.Session.CurrentProcessStartUtcTicks++} 'LegacyStopIdentityMismatch'
Test-Case 'Other image rejected' {param($c) $c.ExpectedExecutableSha256='a'*64} 'LegacyStopExecutableMismatch'
Test-Case 'Other session rejected' {param($c) $c.Receipt.SessionId=[Guid]::NewGuid().ToString('N')} 'LegacyStopIdentityMismatch'
Test-Case 'Exit must be separately proven' {param($c) $c.ProcessExited=$false} 'LegacyStopProcessExitUnproven'
Test-Case 'Expired request rejected' {param($c) $c.NowUtc=[DateTime]::new(([long]$c.Request.RequestUtcTicks+[TimeSpan]::FromMinutes(10).Ticks+1),[DateTimeKind]::Utc)} 'LegacyStopEvidenceExpiredOrClockInvalid'
Test-Case 'Future proof rejected' {param($c) $c.NowUtc=[DateTime]::new(([long]$c.Receipt.UpdatedUtcTicks-1),[DateTimeKind]::Utc)} 'LegacyStopEvidenceExpiredOrClockInvalid'
Test-Case 'Proof before request rejected' {param($c) $c.Receipt.UpdatedUtcTicks=[long]$c.Request.RequestUtcTicks-1} 'LegacyStopEvidenceExpiredOrClockInvalid'
Test-Case 'Live relaunch permit rejected' {param($c) $c.Receipt.RelaunchPermitGeneration=1} 'LegacyStopNotTerminalOperatorStop'
Test-Case 'Nonmanual stop rejected' {param($c) $c.Receipt.CloseIntent='Shutdown'} 'LegacyStopNotTerminalOperatorStop'
Test-Case 'Checkpoint changed after receipt rejected' {param($c) $c.Checkpoint.LastReason='ManualUi'; $c.Checkpoint.UpdatedUtc=[DateTime]::new(([long]$c.Receipt.UpdatedUtcTicks+1),[DateTimeKind]::Utc).ToString('O')} 'LegacyStopEvidenceExpiredOrClockInvalid'
Test-Case 'Normal monitor close may follow terminal safety' {param($c) $c.Checkpoint.LastReason='MonitorClosing'; $c.Checkpoint.UpdatedUtc=[DateTime]::new(([long]$c.Receipt.UpdatedUtcTicks+1),[DateTimeKind]::Utc).ToString('O')}
Test-Case 'Monitor close cannot claim future timestamp' {param($c) $c.Checkpoint.LastReason='MonitorClosing'; $c.Checkpoint.UpdatedUtc=$c.NowUtc.AddSeconds(1).ToString('O')} 'LegacyStopEvidenceExpiredOrClockInvalid'
Write-Output "PASS $passed/$passed; historical fixture time only, no installation or hardware action"
