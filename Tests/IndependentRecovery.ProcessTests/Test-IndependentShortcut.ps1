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
$id=[Guid]::NewGuid().ToString('N')
[xml]$versionDocument=[IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\..\ProductVersion.props'))
$version=[string]$versionDocument.Project.PropertyGroup.EpbProductVersion
$path=Join-Path $root ('MT EPB 试验系统 V'+$version+'.lnk')
Set-IndependentShortcut $main $version $id $root $false
if(-not [IO.File]::Exists($path)){throw 'Versioned shortcut absent'}
$before=(Get-FileHash -LiteralPath $path).Hash
Set-IndependentShortcut $main $version $id $root $false
if((Get-FileHash -LiteralPath $path).Hash -ne $before){throw 'Idempotent creation rewrote shortcut'}
$shell=New-Object -ComObject WScript.Shell;$link=$null
try{
 $link=$shell.CreateShortcut($path)
 if($link.TargetPath -ne $main -or $link.WorkingDirectory -ne $root -or $link.Arguments -ne '' -or $link.IconLocation -ne $main+',0'){throw 'Shortcut target or logo mismatch'}
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
$legacy=Join-Path $root ('MT EPB V'+$version+' ['+$id+'].lnk')
Set-IndependentShortcut $main $version $id $root $false
[IO.File]::Move($path,$legacy)
Set-IndependentShortcut $main $version $id $root $false
if([IO.File]::Exists($legacy) -or -not [IO.File]::Exists($path)){throw 'Legacy name not migrated'}
$shell=New-Object -ComObject WScript.Shell;$link=$null
try{$link=$shell.CreateShortcut($path);$link.IconLocation='shell32.dll,0';$link.Save()}
finally{foreach($com in @($link,$shell)){if($com){[Runtime.InteropServices.Marshal]::FinalReleaseComObject($com)|Out-Null}}}
Set-IndependentShortcut $main $version $id $root $false
$shell=New-Object -ComObject WScript.Shell;$link=$null
try{$link=$shell.CreateShortcut($path);if($link.IconLocation -ne $main+',0'){throw 'Existing wrong icon not repaired'}}
finally{foreach($com in @($link,$shell)){if($com){[Runtime.InteropServices.Marshal]::FinalReleaseComObject($com)|Out-Null}}}
$otherId=[Guid]::NewGuid().ToString('N');$before=(Get-FileHash -LiteralPath $path).Hash
$failure=$null;try{Set-IndependentShortcut $main $version $otherId $root $false}catch{$failure=$_.Exception}
if(-not $failure -or (Get-FileHash -LiteralPath $path).Hash -ne $before){throw 'Another installation short-name collision overwritten'}
Set-IndependentShortcut $main $version $id $root $true
[IO.File]::Copy($main,(Join-Path $root 'other.exe'))
Set-IndependentShortcut $main $version $id $root $false
[IO.File]::Move($path,$legacy)
$shell=New-Object -ComObject WScript.Shell;$link=$null
try{$link=$shell.CreateShortcut($legacy);$link.TargetPath=Join-Path $root 'other.exe';$link.Save()}
finally{foreach($com in @($link,$shell)){if($com){[Runtime.InteropServices.Marshal]::FinalReleaseComObject($com)|Out-Null}}}
$failure=$null;try{Set-IndependentShortcut $main $version $id $root $false}catch{$failure=$_.Exception}
if(-not $failure -or -not [IO.File]::Exists($legacy) -or [IO.File]::Exists($path)){throw 'Foreign legacy shortcut migrated'}
if([IO.File]::Exists($path)){throw 'Owned shortcut not removed'}
$failure=$null;try{Set-IndependentShortcut $main $version $id $root $true}catch{$failure=$_.Exception}
if(-not $failure -or -not [IO.File]::Exists($legacy)){throw 'Foreign legacy shortcut removed'}
$failure=$null;try{Set-IndependentShortcut $main '../invalid' $id $root $false}catch{$failure=$_.Exception}
if(-not $failure){throw 'Invalid version accepted'}
Write-Output ('PASS shortcut create/logo/idempotence/migration/icon-repair/collision/foreign-preservation/removal/invalid-version; '+$root)
