$ErrorActionPreference='Stop'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '..\..\Tools\Install-IndependentRecoveryBundle.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Installer parse failed'}
foreach($name in @('Get-IndependentRepairFiles','Get-IndependentUpgradePlan')){
 $definition=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
 . ([scriptblock]::Create($definition.Extent.Text))
}
$oldFiles=@('Current/app.exe','Current/obsolete.dll','Current/Config/custom.dll','Current/Config/TestConfig.xml','Current/index.db')|ForEach-Object{[pscustomobject]@{Relative=$_;Sha256=('a'*64)}}
$newFiles=@('Current/app.exe','Current/new.dll','Current/Config/TestConfig.xml')|ForEach-Object{[pscustomobject]@{Relative=$_;Sha256=('b'*64)}}
$old=[pscustomobject]@{Version='4.0.0.3';Destination='D:\IsolatedInstallation';Files=@($oldFiles)}
$next=[pscustomobject]@{Version='4.1.0.0';Destination=$old.Destination;Files=@($newFiles)}
$receipt=[pscustomobject]@{schemaVersion=1;version=$old.Version;installRoot=$old.Destination;files=@($oldFiles|ForEach-Object{[pscustomobject]@{path=$_.Relative;sha256=$_.Sha256}})}
$plan=Get-IndependentUpgradePlan $old $next $receipt
if($plan.ReplacementFiles.Count -ne 2 -or $plan.ObsoleteFiles.Count -ne 1 -or $plan.ObsoleteFiles[0].Relative -ne 'Current/obsolete.dll' -or $plan.PreservedFiles.Count -ne 3){throw 'Upgrade lost obsolete ownership or configuration preservation'}
foreach($version in @('4.0.0.3','2.14.2.12','invalid')){
 $next.Version=$version;$rejected=$false
 try{Get-IndependentUpgradePlan $old $next $receipt|Out-Null}catch{$rejected=$true}
 if(-not $rejected){throw 'Non-upgrade version accepted'}
}
$next.Version='4.1.0.0';$next.Destination='D:\OtherInstallation';$rejected=$false
try{Get-IndependentUpgradePlan $old $next $receipt|Out-Null}catch{$rejected=$true}
if(-not $rejected){throw 'Foreign installation accepted'}
$next.Destination=$old.Destination;$receipt.files[0].sha256='c'*64;$rejected=$false
try{Get-IndependentUpgradePlan $old $next $receipt|Out-Null}catch{$rejected=$true}
if(-not $rejected){throw 'Unverified previous build accepted'}
Write-Output 'PASS upgrade plan 6/6; NO FILE OR INSTALLATION CHANGES'
