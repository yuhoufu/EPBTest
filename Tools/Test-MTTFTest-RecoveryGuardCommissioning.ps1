#requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path $root){throw 'TestOutputAlreadyExists'}
[void](New-Item $root -ItemType Directory)
$guard=Join-Path $root 'Guard'; $main=Join-Path $root 'Main'; $scripts=Join-Path $root 'Tools'
foreach($d in @($guard,$main,$scripts)){[void](New-Item $d -ItemType Directory)}
$testedScript=Join-Path $scripts 'Invoke-MTTFTest-RecoveryGuardCommissioning.ps1'
Copy-Item (Join-Path $PSScriptRoot 'Invoke-MTTFTest-RecoveryGuardCommissioning.ps1') $testedScript
# External verifiers and the child executable are fixtures. This verifies the
# orchestration boundaries, not real package acceptance or physical recovery.
'param($ReleaseDirectory)' | Set-Content (Join-Path $scripts 'Verify-Release.ps1')
'param($Mode,$SourceDirectory) if($Mode -ne "Validate"){throw "FixtureInstallerMutation"}' |
    Set-Content (Join-Path $guard 'Install-MTTFTest-RecoveryGuard.ps1')
@{schemaVersion=2;deliveryStage='ObserveOnlyCommissioning';builtFromVerifiedInputs=$true;
    gitDirty=$false;configuration='Release';supervisedCommissioningAvailable=$true;gitCommit='fixture-commit'} |
    ConvertTo-Json | Set-Content (Join-Path $guard 'guard-identity.json')
'{"Mode":0}' | Set-Content (Join-Path $guard 'guard-settings.json')
foreach($name in @('build-identity.json','MTTFTest.exe','MTTFTest.RecoveryControl.dll',
    'MTTFTest.Watchdog.exe','MTTFTest.SessionAgent.exe','MTTFTest.SafetyAgent.exe')) {
    'fixture' | Set-Content (Join-Path $main $name)
}
Copy-Item (Join-Path $main 'MTTFTest.RecoveryControl.dll') $guard
'{"gitCommit":"fixture-commit"}' | Set-Content (Join-Path $main 'build-identity.json') -Encoding UTF8
Add-Type -TypeDefinition @'
using System;
using System.IO;
public class CommissioningFixture {
 public static int Main(string[] args) {
  string root=AppDomain.CurrentDomain.BaseDirectory;
  if(args.Length==1 && args[0]=="--status") {
   string json=File.ReadAllText(Path.Combine(root,"state.json"));
   var portable=new System.Text.StringBuilder();
   foreach(char c in json) { if(c>127) portable.Append("\\u").Append(((int)c).ToString("x4")); else portable.Append(c); }
   Console.WriteLine(portable.ToString()); return 0;
  }
  if(args.Length>0 && args[0]=="--execute") {
   File.WriteAllLines(Path.Combine(root,"executed-args.txt"),args);
   Console.WriteLine("{\"FixtureOnly\":true,\"Decision\":\"NoHardwareExecuted\"}"); return 0;
  }
  return 2;
 }
}
'@ -OutputAssembly (Join-Path $guard 'MTTFTest.RecoveryGuard.exe') -OutputType ConsoleApplication
$state=@{InstallationId='fixture-installation';MainExecutablePath=(Join-Path $main 'MTTFTest.exe');
    Intent=@{DesiredState=0;AuthorizationId='fixture-authorization';IntentVersion=1}}
function Write-State { $state | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $guard 'state.json') -Encoding UTF8 }
Write-State
$taskState='Disabled'
function Get-ScheduledTask { param($TaskName,$ErrorAction) [pscustomobject]@{State=$taskState} }
$passed=0
function Expect-Rejected([scriptblock]$Action,[string]$Message) {
    $caught=$false
    try { & $Action | Out-Null } catch {
        if($_.Exception.Message -notlike "*$Message*"){throw}
        $caught=$true
    }
    if(-not $caught){throw "Expected rejection: $Message"}
}
function Invoke-Case([string]$Mode,[string]$Case) {
    & $testedScript -Mode $Mode -GuardPackageDirectory $guard -MainReleaseDirectory $main `
        -InstalledMainDirectory $main -EvidenceDirectory (Join-Path $root $Case)
}
Invoke-Case Plan 'valid' | Out-Null
if(Test-Path (Join-Path $guard 'executed-args.txt')){throw 'Plan dispatched child'}
$passed++
Invoke-Case Execute 'valid' | Out-Null
$arguments=Get-Content (Join-Path $guard 'executed-args.txt')
if($arguments -notcontains '--commission-until-utc' -or $arguments -notcontains '--commission-authorization') {
    throw 'Bounded scope arguments missing'
}
$passed++
Expect-Rejected { Invoke-Case Execute 'valid' } 'execution-claimed'
$passed++
Invoke-Case Plan 'changed' | Out-Null
$state.Intent.AuthorizationId='replacement'
Write-State
Expect-Rejected { Invoke-Case Execute 'changed' } 'PlanScopeMismatch'
$state.Intent.AuthorizationId='fixture-authorization'; Write-State
$passed++
Invoke-Case Plan 'expired' | Out-Null
$planPath=Join-Path $root 'expired\commissioning-plan.json'
$plan=Get-Content $planPath -Raw | ConvertFrom-Json
$plan.expiresUtc=[DateTime]::UtcNow.AddSeconds(-1).ToString('O')
$plan | ConvertTo-Json -Depth 8 | Set-Content $planPath -Encoding UTF8
Expect-Rejected { Invoke-Case Execute 'expired' } 'PlanExpiredOrInvalid'
$passed++
$state.Intent.DesiredState=2; Write-State
Expect-Rejected { Invoke-Case Plan 'stopped' } 'RequiresExistingRunIntent'
$state.Intent.DesiredState=0; Write-State
$passed++
$taskState='Ready'
Expect-Rejected { Invoke-Case Plan 'automatic-enabled' } 'AutomaticTaskDisabled'
$taskState='Disabled'
$passed++
Invoke-Case Plan 'changed-binary' | Out-Null
Add-Content (Join-Path $guard 'guard-identity.json') ' '
Expect-Rejected { Invoke-Case Execute 'changed-binary' } 'PlanBinaryChanged'
$passed++
foreach($case in @('changed','expired','changed-binary')) {
    if(Test-Path (Join-Path $root "$case\execution-claimed")){throw "Rejected case consumed plan: $case"}
}
[ordered]@{passed=$passed;testOnly=$true;physicalHardwareSafetyVerified=$false;
    scriptSha256=(Get-FileHash $testedScript).Hash; evidence=$root} |
    ConvertTo-Json | Tee-Object (Join-Path $root 'results.json')
