#requires -Version 5.1
# Isolated launcher branch test. No installer, task, hardware or production state is invoked.
$ErrorActionPreference='Stop'
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'FieldPackage-Launcher.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'LauncherParseFailure'}
$branch=$ast.Find({param($n) $n -is [Management.Automation.Language.SwitchStatementAst]},$true).Clauses[0].Item2
$body=$branch.Extent.Text
$body=[scriptblock]::Create($body.Substring(1,$body.Length-2))
$sandbox=Join-Path $env:TEMP ('GuardMode-' + [Guid]::NewGuid().ToString('N'))
$guard=Join-Path $sandbox 'Guard'; $root=Join-Path $sandbox 'Install'; $main=Join-Path $sandbox 'Main'
[void](New-Item -ItemType Directory -Path $guard -Force)
$installer=Join-Path $main 'InstallerStub.ps1'
$savedProgramData=$env:ProgramData
$env:ProgramData=Join-Path $sandbox 'ProgramData'
$script:calls=@(); $script:taskState='Disabled'
function Invoke-Step($Name,$Script,$Parameters){$script:calls+=,$Parameters}
function Get-ScheduledTask($Name){[pscustomobject]@{State=$script:taskState}}
$Action='Install'
function Check($Name,$Mode,$Machine,$TaskState,$ExpectedFailure,$ExpectedCalls){
    $script:calls=@(); $script:taskState=$TaskState
    @{acceptanceBenchId=$env:COMPUTERNAME;acceptanceMachineName=$Machine}|ConvertTo-Json|Set-Content (Join-Path $guard 'guard-identity.json') -Encoding UTF8
    $recoveryMode=$Mode; $failed=$false
    try{. $body}catch{$failed=$true}
    if($failed -ne $ExpectedFailure -or $script:calls.Count -ne $ExpectedCalls){throw "Unexpected branch result: $Name"}
    if(-not $failed -and $script:calls[1].RecoveryMode -cne $Mode){throw 'Mode was not passed to Guard installer'}
    Write-Output "PASS $Name"
}
try {
    Check 'Observe keeps executor disabled' ObserveOnly $env:COMPUTERNAME Disabled $false 2
    Check 'Automatic mode reaches Guard installer' RecoverExited $env:COMPUTERNAME Ready $false 2
    Check 'Wrong host rejected before Main changes' RecoverExited 'SYNTHETIC-WRONG-HOST' Ready $true 0
    Check 'Automatic task unexpectedly disabled is failure' RecoverExited $env:COMPUTERNAME Disabled $true 2
    Check 'Observe task unexpectedly enabled is failure' ObserveOnly $env:COMPUTERNAME Ready $true 2
    $Action='Repair'
    Check 'Repair preserves automatic selection' RecoverExited $env:COMPUTERNAME Ready $false 2
} finally {$env:ProgramData=$savedProgramData}
Write-Output 'PASS 6/6; branch mocks only, no field acceptance or installation'
