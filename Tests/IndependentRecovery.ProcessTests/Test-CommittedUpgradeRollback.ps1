param([string]$EvidenceRoot=$env:EPB_TEST_ARTIFACT_ROOT)
$ErrorActionPreference='Stop'
if(-not $EvidenceRoot){throw 'Evidence root required'}
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '..\..\Tools\Install-IndependentRecoveryBundle.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Installer parse failed'}
foreach($name in @('Get-IndependentRepairFiles','Get-IndependentUpgradePlan','Get-IndependentRollbackPlan','Invoke-IndependentFileRepair','Restore-IndependentFileTransaction')){
    $node=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
    . ([scriptblock]::Create($node.Extent.Text))
}
$root=Join-Path $EvidenceRoot ('committed-rollback-'+[Guid]::NewGuid().ToString('N'))
foreach($dir in @('Current','IndependentState','payloads')){[IO.Directory]::CreateDirectory((Join-Path $root $dir))|Out-Null}
$oldFiles=@();$newFiles=@()
foreach($name in @('main.dll','obsolete.dll')){
    $target=Join-Path $root ('Current\'+$name);[IO.File]::WriteAllText($target,'original-'+$name)
    $oldFiles+=,[pscustomobject]@{Relative=('Current/'+$name);Sha256=(Get-FileHash $target).Hash}
}
foreach($name in @('main.dll','added.dll')){
    $source=Join-Path $root ('payloads\'+$name);[IO.File]::WriteAllText($source,'new-'+$name)
    $newFiles+=,[pscustomobject]@{Relative=('Current/'+$name);Source=$source;Sha256=(Get-FileHash $source).Hash}
}
$old=[pscustomobject]@{Version='4.0.0.3';Destination=$root;Files=$oldFiles}
$next=[pscustomobject]@{Version='4.1.0.0';Destination=$root;Files=$newFiles}
$receipt=[pscustomobject]@{schemaVersion=1;version=$old.Version;installRoot=$root;files=@($oldFiles|ForEach-Object{[pscustomobject]@{path=$_.Relative;sha256=$_.Sha256}})}
[IO.File]::WriteAllText((Join-Path $root 'installed-files.json'),($receipt|ConvertTo-Json -Depth 4))
$nextReceipt=Join-Path $root 'payloads\receipt.json';[IO.File]::WriteAllText($nextReceipt,'new receipt')
$metadata=@([pscustomobject]@{Relative='installed-files.json';Source=$nextReceipt;Sha256=(Get-FileHash $nextReceipt).Hash})
$transaction=Invoke-IndependentFileRepair -Files $newFiles -InstallDirectory $root -RetiredFiles @($oldFiles[1]) -MetadataFiles $metadata
$journalPath=Join-Path $transaction 'transaction.json';$journal=[IO.File]::ReadAllText($journalPath)|ConvertFrom-Json
$id=[Guid]::NewGuid().ToString('N')
$outcome=[pscustomobject]@{installationId=$id;stage='Completed';fromVersion=$old.Version;toVersion=$next.Version;transaction=$transaction}
$rollback=Get-IndependentRollbackPlan $next $old $outcome $journal $receipt $id
$failed=$false
try{Restore-IndependentFileTransaction $root $rollback.TransactionId|Out-Null}catch{$failed=$true}
if(-not $failed){throw 'Ordinary interrupted recovery rolled back a committed upgrade'}
foreach($scenario in @('foreign-id','changed-original','missing-original')){
    $copy=[IO.File]::ReadAllText($journalPath)|ConvertFrom-Json
    $owner=$id
    if($scenario -eq 'foreign-id'){$owner='0'*32}
    if($scenario -eq 'changed-original'){$copy.entries[0].originalSha256='0'*64}
    if($scenario -eq 'missing-original'){$copy.entries=@($copy.entries|Where-Object{$_.target -notlike '*obsolete.dll'})}
    $failed=$false
    try{Get-IndependentRollbackPlan $next $old $outcome $copy $receipt $owner|Out-Null}catch{$failed=$true}
    if(-not $failed){throw ('Rollback admitted '+$scenario)}
}
Restore-IndependentFileTransaction $root $rollback.TransactionId -AllowCommitted|Out-Null
Restore-IndependentFileTransaction $root $rollback.TransactionId -AllowCommitted|Out-Null
foreach($file in $oldFiles){if((Get-FileHash (Join-Path $root $file.Relative)).Hash -ne $file.Sha256){throw 'Original component not restored'}}
if([IO.File]::Exists((Join-Path $root 'Current\added.dll')) -or ([IO.File]::ReadAllText((Join-Path $root 'installed-files.json'))|ConvertFrom-Json).version -ne $old.Version){throw 'New component or receipt remained'}
Write-Output ('PASS committed upgrade rollback, explicit authorization, old-package identity and repeat 5/5; '+$root)
