#requires -Version 5.1
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'RecoveryGuard-Acceptance.ps1')
$passed = 0
$root = Join-Path ([IO.Path]::GetTempPath()) ('GuardAcceptanceFixture-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $root)
# Synthetic in-memory parser fixtures only: never a field acceptance or package approval.
[IO.File]::WriteAllText((Join-Path $root 'fixture.txt'), 'Synthetic validator test fixture; not field evidence.')
$evidenceHash = (Get-FileHash (Join-Path $root 'fixture.txt')).Hash
$ids = @('independence','oldState','primaryBackupConflict','snapshotFreshness','operatorPriority',
    'persistenceFailure','singleInstance','sessionRollover','repeatedFailures','cooldownAndExpiry',
    'powerLossBoundary','panelStop','transactionInterruption','coordinatorLoss','maintenanceAndClock',
    'persistentBudget','dataReconciliation','businessRecovery','exitedRecovery','physicalSafety')
$base = [ordered]@{schemaVersion=1;kind='RecoveryGuardFieldAcceptance';testOnly=$false;passed=$true;
    physicalHardwareSafetyVerified=$true;productVersion='3.0.0.0';benchId='SYNTHETIC-FIXTURE';
    machineName='SYNTHETIC-FIXTURE';operatorName='SYNTHETIC-FIXTURE';completedUtc='2026-01-01T00:00:00Z';
    mainIdentitySha256=('a'*64);guardExecutableSha256=('b'*64);recoveryControlSha256=('c'*64);
    guardPackageIdentitySha256=('e'*64);
    approvedModes=@('RecoverExited');checks=@($ids | ForEach-Object {
        @{id=$_;passed=$true;evidencePath='fixture.txt';evidenceSha256=$evidenceHash}
    })}
function New-Fixture { return ($base | ConvertTo-Json -Depth 6 | ConvertFrom-Json) }
function Validate($report, [string]$mode='RecoverExited') {
    [void](Assert-GuardAcceptanceReport $report $mode ('a'*64) ('b'*64) ('c'*64) ('e'*64) ([datetime]'2026-01-02T00:00:00Z'))
    Assert-GuardAcceptanceEvidenceFiles $report $root
}
function Reject([string]$name, [scriptblock]$action, [string]$reason) {
    try { & $action; throw 'EXPECTED_REJECTION_MISSING' }
    catch { if ($_.Exception.Message -notlike ($reason + '*')) { throw } }
    $script:passed++; Write-Output "PASS $name"
}
try {
    Validate (New-Fixture); $passed++
    Reject 'simulation report' { $r=New-Fixture; $r.testOnly=$true; Validate $r } 'AcceptanceRequiresPassedPhysicalFieldReport'
    Reject 'missing hardware proof' { $r=New-Fixture; $r.physicalHardwareSafetyVerified=$false; Validate $r } 'AcceptanceRequiresPassedPhysicalFieldReport'
    Reject 'string truth value' { $r=New-Fixture; $r.passed='true'; Validate $r } 'AcceptanceRequiresPassedPhysicalFieldReport'
    Reject 'different main package' { $r=New-Fixture; $r.mainIdentitySha256='d'*64; Validate $r } 'AcceptanceBindingMismatch'
    Reject 'different Guard' { $r=New-Fixture; $r.guardExecutableSha256='d'*64; Validate $r } 'AcceptanceBindingMismatch'
    Reject 'different shared core' { $r=New-Fixture; $r.recoveryControlSha256='d'*64; Validate $r } 'AcceptanceBindingMismatch'
    Reject 'different Guard package scripts or configuration' { $r=New-Fixture; $r.guardPackageIdentitySha256='d'*64; Validate $r } 'AcceptanceBindingMismatch'
    Reject 'unapproved stalled mode' { Validate (New-Fixture) 'RecoverStalled' } 'AcceptanceModeNotApproved'
    Reject 'missing scenario' { $r=New-Fixture; $r.checks=@($r.checks | Select-Object -Skip 1); Validate $r } 'AcceptanceChecksIncomplete'
    Reject 'duplicate scenario' { $r=New-Fixture; $r.checks[1]=$r.checks[0]; Validate $r } 'AcceptanceCheckInvalid'
    Reject 'failed scenario' { $r=New-Fixture; $r.checks[0].passed=$false; Validate $r } 'AcceptanceCheckInvalid'
    Reject 'future completion' { $r=New-Fixture; $r.completedUtc='2026-01-03T00:00:00Z'; Validate $r } 'AcceptanceTimestampInvalid'
    Reject 'missing UTC offset' { $r=New-Fixture; $r.completedUtc='2026-01-01T00:00:00'; Validate $r } 'AcceptanceTimestampMustBeUtc'
    Reject 'evidence path escape' { $r=New-Fixture; $r.checks[0].evidencePath='../outside.txt'; Validate $r } 'AcceptanceEvidencePathInvalid'
    Reject 'missing evidence file' { $r=New-Fixture; $r.checks[0].evidencePath='missing.txt'; Validate $r } 'AcceptanceEvidenceMissing'
    Reject 'tampered evidence' { $r=New-Fixture; $r.checks[0].evidenceSha256='d'*64; Validate $r } 'AcceptanceEvidenceHashMismatch'
    $r=New-Fixture; $r.approvedModes=@('RecoverExited','RecoverStalled')
    Reject 'stalled mode without scenario' { Validate $r 'RecoverStalled' } 'AcceptanceChecksIncomplete'
    $r.checks+=@{id='stalledRecovery';passed=$true;evidencePath='fixture.txt';evidenceSha256=$evidenceHash}
    Validate $r 'RecoverStalled'; $passed++
    Write-Output "PASS GuardAcceptance $passed/$passed; synthetic parser fixtures only; no installation or hardware"
} finally {
    # Retain the small synthetic file for troubleshooting; no field report is written.
    Write-Output "FixtureDirectory=$root"
}
