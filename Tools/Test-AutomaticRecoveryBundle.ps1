#requires -Version 5.1
param([Parameter(Mandatory=$true)][string]$BundleDirectory)
$ErrorActionPreference='Stop'
$bundle=[IO.Path]::GetFullPath($BundleDirectory)
$entry=Join-Path $bundle 'Install-AutomaticRecoveryBundle.ps1'
function Register-ScheduledTask {throw 'TEST MUST NOT INSTALL TASKS'}
& $entry -Mode ValidatePackage|Out-Null
Write-Output 'PASS complete automatic candidate verifies without mutation'
try{& $entry -Mode Validate;throw 'ExpectedMissingReport'}catch{if($_.Exception.Message -notlike '尚未提供现场验收报告*'){throw}}
Write-Output 'PASS missing report is not downgraded to ObserveOnly'
. (Join-Path $PSScriptRoot 'Test-RecoveryGuard-Acceptance.ps1')|Out-Null
$fixtureRoot=$root
$guard=Join-Path $bundle 'Base\Guard'
$main=Join-Path $bundle 'Base\Package'
$report=New-Fixture
$mainHash=(Get-FileHash (Join-Path $main 'build-identity.json')).Hash
$report.mainIdentitySha256=$mainHash
$report.guardExecutableSha256=(Get-FileHash (Join-Path $guard 'MTTFTest.RecoveryGuard.exe')).Hash
$report.recoveryControlSha256=(Get-FileHash (Join-Path $guard 'MTTFTest.RecoveryControl.dll')).Hash
$report.guardPackageIdentitySha256=(Get-FileHash (Join-Path $guard 'guard-identity.json')).Hash
$reportPath=Join-Path $fixtureRoot 'SYNTHETIC-NOT-FOR-INSTALLATION.json'
$report|ConvertTo-Json -Depth 10|Set-Content $reportPath -Encoding UTF8
try{& $entry -Mode Validate -AcceptanceReportPath $reportPath;throw 'ExpectedWrongHost'}catch{if($_.Exception.Message -ne 'AcceptanceInstallTargetMismatch'){throw}}
try{& $entry -Mode Validate -RecoveryMode RecoverStalled -AcceptanceReportPath $reportPath;throw 'ExpectedUnapprovedMode'}catch{if($_.Exception.Message -notlike 'AcceptanceModeNotApproved*'){throw}}
Write-Output 'PASS exited-only report cannot authorize stalled recovery'
Write-Output 'PASS synthetic approved shape cannot authorize this machine'
# Exercise only materialization + real read-only installer validation. Never run
# the following Main installation statements. All writes are in TEMP.
$text=Get-Content $entry -Raw -Encoding UTF8
$start=$text.IndexOf('$stage=Join-Path')
$end=$text.IndexOf('$installRoot=Join-Path',$start)
if($start -lt 0 -or $end -le $start){throw 'MaterializationBoundariesMissing'}
$materialization=$text.Substring($start,$end-$start).Replace('$PSCommandPath',("'"+$entry.Replace("'","''")+"'"))
$code=[scriptblock]::Create($materialization)
$oldProgramData=$env:ProgramData
try{
    $env:ProgramData=Join-Path $fixtureRoot 'ProgramData'
    $identity=Get-Content (Join-Path $guard 'guard-identity.json') -Raw -Encoding UTF8|ConvertFrom-Json
    $reportBytes=[IO.File]::ReadAllBytes($reportPath)
    $evidenceRoot=$fixtureRoot
    $bench='SYNTHETIC-FIXTURE'
    $RecoveryMode='RecoverExited'
    . $code
    $accepted=Get-Content (Join-Path $stage 'guard-identity.json') -Raw -Encoding UTF8|ConvertFrom-Json
    if($accepted.deliveryStage -ne 'AutomaticRecovery' -or $accepted.acceptanceMachineName -ne 'SYNTHETIC-FIXTURE'){throw 'MaterializationInvalid'}
    Write-Output 'PASS materialized synthetic automatic package passes real Guard validation, never installed'
    $RecoveryMode='RecoverStalled'
    $report.approvedModes=@('RecoverExited','RecoverStalled')
    $report.checks+=@{id='stalledRecovery';passed=$true;evidencePath='fixture.txt';evidenceSha256=$evidenceHash}
    $reportBytes=[Text.Encoding]::UTF8.GetBytes(($report|ConvertTo-Json -Depth 10))
    $identity=Get-Content (Join-Path $guard 'guard-identity.json') -Raw -Encoding UTF8|ConvertFrom-Json
    . $code
    Write-Output 'PASS stalled materialization with additional synthetic scenario passes real Guard validation, never installed'
}finally{$env:ProgramData=$oldProgramData}
Write-Output 'PASS 6/6; synthetic non-field validation only; no tasks, Main or hardware actions'
