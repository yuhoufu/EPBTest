$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $repo 'Tools\Install-IndependentRecoveryBundle.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Installer parse failed'}
$definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Assert-IndependentComponentVersions'},$true)
. ([scriptblock]::Create($definition.Extent.Text))
$main=Join-Path $repo 'MTTfTest\bin\Debug\MTTFTest.exe'
$files=@(
 [pscustomobject]@{Relative='Current/MTTFTest.exe';Source=$main},
 [pscustomobject]@{Relative='Current/MTTFTest.SafetyAgent.exe';Source=(Join-Path $repo 'MTTfTest\bin\Debug\MTTFTest.SafetyAgent.exe')},
 [pscustomobject]@{Relative='FallbackGuard/MTTFTest.FallbackGuard.exe';Source=(Join-Path $repo 'FallbackGuard\bin\Debug\MTTFTest.FallbackGuard.exe')}
)
$plan=[pscustomobject]@{Version=[Diagnostics.FileVersionInfo]::GetVersionInfo($main).FileVersion;Files=$files}
Assert-IndependentComponentVersions $plan
$passed=1
foreach($index in 0..2){
 $source=$files[$index].Source
 try{
  $files[$index].Source=Join-Path $repo 'Tests\IndependentRecovery.ProcessTests\bin\Debug\IndependentRecovery.ProcessTests.exe'
  $rejected=$false;try{Assert-IndependentComponentVersions $plan}catch{$rejected=$true}
  if(-not $rejected){throw 'Mixed component version accepted'}
  $passed++
 }finally{$files[$index].Source=$source}
}
$plan.Version='99.99.99.99';$rejected=$false
try{Assert-IndependentComponentVersions $plan}catch{$rejected=$true}
if(-not $rejected){throw 'Manifest version mismatch accepted'}
$passed++
Write-Output ('PASS actual component version validation '+$passed+'/5; NO EXECUTABLE STARTED')
