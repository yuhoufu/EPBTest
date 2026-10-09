param([string]$EvidenceRoot=$env:EPB_TEST_ARTIFACT_ROOT)
$ErrorActionPreference='Stop'
if(-not $EvidenceRoot){throw 'Evidence root required'}
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '..\..\Tools\Install-IndependentRecoveryBundle.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Installer parse failed'}
$definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-IndependentFileRepair'},$true)
. ([scriptblock]::Create($definition.Extent.Text))
$suite=Join-Path $EvidenceRoot ('file-repair-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($suite)|Out-Null
foreach($scenario in @('success','sharing-failure','bad-source','config-excluded')){
 $root=Join-Path $suite $scenario
 [IO.Directory]::CreateDirectory((Join-Path $root 'Current'))|Out-Null
 [IO.Directory]::CreateDirectory((Join-Path $root 'sources'))|Out-Null
 $files=@();$lock=$null
 foreach($name in @('first.dll','missing.dll','last.dll')){
  $source=Join-Path $root ('sources\'+$name);[IO.File]::WriteAllText($source,'new-'+$name)
  $target=Join-Path $root ('Current\'+$name)
  if($name -ne 'missing.dll'){[IO.File]::WriteAllText($target,'original-'+$name)}
  $files+=,[pscustomobject]@{Relative=('Current/'+$name);Source=$source;Sha256=(Get-FileHash -LiteralPath $source).Hash}
 }
 if($scenario -eq 'sharing-failure'){$lock=[IO.File]::Open((Join-Path $root 'Current\last.dll'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)}
 if($scenario -eq 'bad-source'){$files[2].Sha256='0'*64}
 if($scenario -eq 'config-excluded'){$files[2].Relative='Current/Config/Custom.dll'}
 $failure=$null
 try{Invoke-IndependentFileRepair $files $root|Out-Null}catch{$failure=$_.Exception}finally{if($lock){$lock.Dispose()}}
 if($scenario -eq 'success'){
  if($failure){throw $failure}
  foreach($file in $files){if((Get-FileHash -LiteralPath (Join-Path $root $file.Relative)).Hash -ne $file.Sha256){throw 'Successful replacement hash mismatch'}}
 }else{
  if(-not $failure){throw 'Expected repair rejection/failure'}
  foreach($name in @('first.dll','last.dll')){if([IO.File]::ReadAllText((Join-Path $root ('Current\'+$name))) -ne ('original-'+$name)){throw 'Original bytes not restored'}}
  if([IO.File]::Exists((Join-Path $root 'Current\missing.dll'))){throw 'New file remained after rollback'}
 }
 $journals=@(Get-ChildItem -LiteralPath (Join-Path $root 'Repair') -Filter 'transaction.json' -File -Recurse)
 if($journals.Count -ne 1){throw 'Repair journal absent'}
 $receipt=[IO.File]::ReadAllText($journals[0].FullName)|ConvertFrom-Json
 $expected=if($scenario -eq 'success'){'Replaced'}else{'RolledBack'}
 if($receipt.phase -ne $expected){throw 'Journal does not describe outcome'}
 if($scenario -eq 'sharing-failure' -and -not [IO.File]::Exists((Join-Path $journals[0].DirectoryName '0.new.failed'))){throw 'Failure did not exercise rollback after replacement'}
}
foreach($scenario in @('concurrent-owner','unfinished','missing-journal')){
 $root=Join-Path $suite $scenario
 [IO.Directory]::CreateDirectory((Join-Path $root 'Current'))|Out-Null
 $source=Join-Path $root 'source.dll';[IO.File]::WriteAllText($source,'new')
 $target=Join-Path $root 'Current\target.dll';[IO.File]::WriteAllText($target,'original')
 $files=@([pscustomobject]@{Relative='Current/target.dll';Source=$source;Sha256=(Get-FileHash -LiteralPath $source).Hash})
 $lock=$null
 if($scenario -eq 'concurrent-owner'){$lock=[IO.File]::Open((Join-Path $root 'file-repair.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)}
 else{
  $history=Join-Path $root 'Repair\interrupted';[IO.Directory]::CreateDirectory($history)|Out-Null
  if($scenario -eq 'unfinished'){[IO.File]::WriteAllText((Join-Path $history 'transaction.json'),'{"schemaVersion":1,"phase":"Prepared"}')}
 }
 $failure=$null
 try{Invoke-IndependentFileRepair $files $root|Out-Null}catch{$failure=$_.Exception}finally{if($lock){$lock.Dispose()}}
 if(-not $failure -or [IO.File]::ReadAllText($target) -ne 'original'){throw 'Concurrent/interrupted repair was not rejected before mutation'}
 # A rejection must release its own lease, allowing subsequent recovery.
 $probe=[IO.File]::Open((Join-Path $root 'file-repair.lock'),[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);$probe.Dispose()
}
Write-Output ('PASS real file replacement, rollback and interrupted/concurrent rejection 7/7; isolated path '+$suite)
