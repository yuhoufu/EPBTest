param([string]$EvidenceRoot=$env:EPB_TEST_ARTIFACT_ROOT)
$ErrorActionPreference='Stop'
if(-not $EvidenceRoot){throw 'Evidence root required'}
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '..\..\Tools\Install-IndependentRecoveryBundle.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Installer parse failed'}
$definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-IndependentRepairFiles'},$true)
. ([scriptblock]::Create($definition.Extent.Text))
$files=@('Current/MTTFTest.exe','Current/MTTFTest.exe.config','Current/Config/AIConfig.xml','Current/Config/Custom.dll','Current/index.db','Current/readme.md','FallbackGuard/MTTFTest.FallbackGuard.exe','Tools/Manage-IndependentRecovery.ps1')
$plan=[pscustomobject]@{Version='4.1.0.0';Destination=[IO.Path]::GetFullPath((Join-Path $EvidenceRoot 'repair-target'));Files=@($files|ForEach-Object{[pscustomobject]@{Relative=$_;Sha256=('a'*64);Source='unused'}})}
$receipt=[ordered]@{schemaVersion=1;version=$plan.Version;installRoot=$plan.Destination;files=@($plan.Files|ForEach-Object{[ordered]@{path=$_.Relative;sha256=$_.Sha256}})}|ConvertTo-Json -Depth 4
$count=0
foreach($scenario in @('valid','version','directory','digest','duplicate','missing','unknown')){
 $r=$receipt|ConvertFrom-Json
 switch($scenario){
  'version'{$r.version='4.0.0.3'}
  'directory'{$r.installRoot+='-foreign'}
  'digest'{$r.files[0].sha256='b'*64}
  'duplicate'{$r.files[1].path=$r.files[0].path}
  'missing'{$r.files=@($r.files|Select-Object -Skip 1)}
  'unknown'{$r.files[0].path='Current/foreign.exe'}
 }
 $failure=$null;$result=@()
 try{$result=@(Get-IndependentRepairFiles $plan $r)}catch{$failure=$_.Exception}
 if($scenario -eq 'valid'){
  if($failure -or $result.Count -ne 4 -or @($result|Where-Object{$_.Relative -like 'Current/Config/*' -or $_.Relative -like '*.db'}).Count){throw 'Repair selection changed configuration/data'}
 }elseif(-not $failure){throw ('Foreign repair accepted: '+$scenario)}
 $count++
}
Write-Output ('PASS repair file ownership and preservation '+$count+'/7; NO FILES MODIFIED')
