#requires -Version 5.1
$ErrorActionPreference='Stop'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Install-AutomaticRecoveryBundle.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw $errors[0]}
$fn=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Resolve-AutomaticRepairMode'},$true)
. ([scriptblock]::Create($fn.Extent.Text))
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('RepairMode-'+[guid]::NewGuid().ToString('N')+'.json')
$cases=@(
    @('Repair','RecoverExited',$false,2,'RecoverStalled'),
    @('Repair','RecoverExited',$false,'RecoverStalled','RecoverStalled'),
    @('Repair','RecoverExited',$false,1,'RecoverExited'),
    @('Repair','RecoverExited',$false,0,'RecoverExited'),
    @('Repair','RecoverExited',$true,2,'RecoverExited'),
    @('Install','RecoverExited',$false,2,'RecoverExited'))
foreach($c in $cases){
    @{Mode=$c[3]}|ConvertTo-Json|Set-Content $fixture -Encoding UTF8
    $actual=Resolve-AutomaticRepairMode $c[0] $c[1] $c[2] $fixture
    if($actual -cne $c[4]){throw 'RepairModeMismatch'}
}
'{}'|Set-Content $fixture -Encoding UTF8
try{Resolve-AutomaticRepairMode Repair RecoverExited $false $fixture;throw 'ExpectedRejection'}catch{if($_.Exception.Message -notlike 'InstalledRecoveryModeInvalid*'){throw}}
Write-Output 'PASS 7/7 repair mode selection; temporary JSON only; no installed state changed'
