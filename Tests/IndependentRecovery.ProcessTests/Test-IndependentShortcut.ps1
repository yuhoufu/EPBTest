param([string]$EvidenceRoot=$env:EPB_TEST_ARTIFACT_ROOT)
$ErrorActionPreference='Stop'
if(-not $EvidenceRoot){throw 'EPB_TEST_ARTIFACT_ROOT required'}
$root=Join-Path $EvidenceRoot ('shortcut-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root)|Out-Null
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '..\..\Tools\Manage-IndependentRecovery.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw ($errors|Out-String)}
$definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Set-IndependentShortcut'},$true)
. ([scriptblock]::Create($definition.Extent.Text))
$main=Join-Path $root 'MTTFTest.exe';[IO.File]::WriteAllText($main,'NOT_EXECUTABLE')
$id=[Guid]::NewGuid().ToString('N');$version='4.1.0.0'
$path=Join-Path $root ('MT EPB V'+$version+' ['+$id+'].lnk')
Set-IndependentShortcut $main $version $id $root $false
if(-not [IO.File]::Exists($path)){throw 'Versioned shortcut absent'}
$before=(Get-FileHash -LiteralPath $path).Hash
Set-IndependentShortcut $main $version $id $root $false
if((Get-FileHash -LiteralPath $path).Hash -ne $before){throw 'Idempotent creation rewrote shortcut'}
$shell=New-Object -ComObject WScript.Shell;$link=$null
try{
 $link=$shell.CreateShortcut($path)
 if($link.TargetPath -ne $main -or $link.WorkingDirectory -ne $root -or $link.Arguments -ne ''){throw 'Shortcut target mismatch'}
 $link.Arguments='--foreign';$link.Save()
}finally{foreach($com in @($link,$shell)){if($com){[Runtime.InteropServices.Marshal]::FinalReleaseComObject($com)|Out-Null}}}
foreach($remove in @($false,$true)){
 $failure=$null;try{Set-IndependentShortcut $main $version $id $root $remove}catch{$failure=$_.Exception}
 if(-not $failure -or -not [IO.File]::Exists($path)){throw 'Foreign shortcut changed or removed'}
}
$shell=New-Object -ComObject WScript.Shell;$link=$null
try{$link=$shell.CreateShortcut($path);$link.Arguments='';$link.Save()}
finally{foreach($com in @($link,$shell)){if($com){[Runtime.InteropServices.Marshal]::FinalReleaseComObject($com)|Out-Null}}}
Set-IndependentShortcut $main $version $id $root $true
if([IO.File]::Exists($path)){throw 'Owned shortcut not removed'}
Set-IndependentShortcut $main $version $id $root $true
$failure=$null;try{Set-IndependentShortcut $main '../invalid' $id $root $false}catch{$failure=$_.Exception}
if(-not $failure){throw 'Invalid version accepted'}
Write-Output ('PASS shortcut create/target/idempotence/foreign-preservation/removal/invalid-version; '+$root)
