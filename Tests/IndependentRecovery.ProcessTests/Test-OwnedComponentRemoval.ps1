param([string]$EvidenceRoot=$env:EPB_TEST_ARTIFACT_ROOT)
$ErrorActionPreference='Stop'
if(-not $EvidenceRoot){throw 'Evidence root required'}
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '..\..\Tools\Install-IndependentRecoveryBundle.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Installer parse failed'}
$definition=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Remove-IndependentOwnedComponents'},$true)
. ([scriptblock]::Create($definition.Extent.Text))
$root=Join-Path $EvidenceRoot ('component-removal-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory((Join-Path $root 'Current\Config'))|Out-Null
$first=Join-Path $root 'Current\first.dll';$second=Join-Path $root 'Current\second.exe'
[IO.File]::WriteAllText($first,'first');[IO.File]::WriteAllText($second,'second')
$files=@([pscustomobject]@{Relative='Current/first.dll';Sha256=(Get-FileHash $first).Hash},[pscustomobject]@{Relative='Current/second.exe';Sha256=(Get-FileHash $second).Hash})
$retained=@('Current/Config/custom.dll','Current/index.db','Current/unknown.dll')
foreach($leaf in $retained){[IO.File]::WriteAllText((Join-Path $root $leaf),'retain')}
[IO.File]::AppendAllText($second,'changed')
$failure=$null;try{Remove-IndependentOwnedComponents $files $root}catch{$failure=$_.Exception}
if(-not $failure -or -not [IO.File]::Exists($first)){throw 'Changed component was not rejected before deletion'}
[IO.File]::WriteAllText($second,'second')
Remove-IndependentOwnedComponents $files $root
Remove-IndependentOwnedComponents $files $root
if([IO.File]::Exists($first) -or [IO.File]::Exists($second)){throw 'Owned components remained'}
foreach($leaf in $retained){if([IO.File]::ReadAllText((Join-Path $root $leaf)) -ne 'retain'){throw 'Unowned data/config changed'}}
foreach($relative in @('../outside.dll','Current/Config/custom.dll','Current\Config\custom.dll','Current/index.db')){
 $failure=$null;try{Remove-IndependentOwnedComponents @([pscustomobject]@{Relative=$relative;Sha256=('a'*64)}) $root}catch{$failure=$_.Exception}
 if(-not $failure){throw ('Unsafe removal accepted: '+$relative)}
}
Write-Output ('PASS owned-component deletion, idempotence, full preflight and data preservation; '+$root)
