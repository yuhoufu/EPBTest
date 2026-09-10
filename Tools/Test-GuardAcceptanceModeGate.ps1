# Pure in-memory validator regression. No report is written or used for publication.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'RecoveryGuard-Acceptance.ps1')
$hash = 'a' * 64
$report = [pscustomobject]@{
    schemaVersion=1; kind='RecoveryGuardFieldAcceptance'; testOnly=$false
    passed=$true; physicalHardwareSafetyVerified=$true; productVersion='3.0.0.0'
    benchId='SYNTHETIC-UNIT-TEST'; machineName='SYNTHETIC'; operatorName='SYNTHETIC'
    completedUtc='2026-01-01T00:00:00Z'; mainIdentitySha256=$hash
    guardExecutableSha256=$hash; recoveryControlSha256=$hash; guardPackageIdentitySha256=$hash
    approvedModes=@('RecoverExited'); checks=@()
}
function Expect-Rejection([string]$mode, [string]$expected) {
    try {
        [void](Assert-GuardAcceptanceReport $report $mode $hash $hash $hash $hash)
    } catch {
        if ($_.Exception.Message -cne $expected) { throw }
        Write-Output "PASS $expected"
        return
    }
    throw "UnexpectedAcceptance:$mode/$expected"
}
Expect-Rejection RecoverStalled AcceptanceModeNotApproved
$ids = @('independence','oldState','primaryBackupConflict','snapshotFreshness',
    'operatorPriority','persistenceFailure','singleInstance','sessionRollover',
    'repeatedFailures','cooldownAndExpiry','powerLossBoundary','panelStop',
    'transactionInterruption','coordinatorLoss','maintenanceAndClock','persistentBudget',
    'dataReconciliation','businessRecovery','exitedRecovery','physicalSafety')
$report.checks = @($ids | ForEach-Object {
    [pscustomobject]@{ id=$_; passed=$true; evidencePath='synthetic-never-written.log'; evidenceSha256=$hash }
})
$report.approvedModes = @('RecoverExited','RecoverStalled')
# Even requesting Exited must validate every mode advertised by the report.
Expect-Rejection RecoverExited AcceptanceChecksIncomplete
Expect-Rejection RecoverStalled AcceptanceChecksIncomplete
$report.checks += [pscustomobject]@{ id='stalledRecovery'; passed=$true; evidencePath='synthetic-never-written.log'; evidenceSha256=$hash }
Expect-Rejection RecoverExited AcceptanceChecksIncomplete
Expect-Rejection RecoverStalled AcceptanceChecksIncomplete
$report.checks += [pscustomobject]@{ id='forcedStalledRecovery'; passed=$true; evidencePath='synthetic-never-written.log'; evidenceSha256=$hash }
Expect-Rejection RecoverStalled AcceptanceChecksIncomplete
$report.testOnly = $true
Expect-Rejection RecoverExited AcceptanceRequiresPassedPhysicalFieldReport
$report.testOnly = $false
$report.physicalHardwareSafetyVerified = $false
Expect-Rejection RecoverStalled AcceptanceRequiresPassedPhysicalFieldReport
Write-Output 'PASS 8/8 (synthetic rejection tests; no field acceptance)'
