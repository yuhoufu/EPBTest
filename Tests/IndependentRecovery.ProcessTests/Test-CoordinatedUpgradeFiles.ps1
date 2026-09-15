param([string]$EvidenceRoot=$env:EPB_TEST_ARTIFACT_ROOT)
$ErrorActionPreference='Stop'
if(-not $EvidenceRoot){throw 'Evidence root required'}
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '..\..\Tools\Install-IndependentRecoveryBundle.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Installer parse failed'}
foreach($name in @('Invoke-IndependentFileRepair','Restore-IndependentFileTransaction')){
    $node=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
    . ([scriptblock]::Create($node.Extent.Text))
}
$suite=Join-Path $EvidenceRoot ('coordinated-upgrade-'+[Guid]::NewGuid().ToString('N'))
foreach($scenario in @('success-and-replay','metadata-locked','state-excluded')){
    $root=Join-Path $suite $scenario
    foreach($folder in @('Current','IndependentState','payloads')){[IO.Directory]::CreateDirectory((Join-Path $root $folder))|Out-Null}
    $files=@();$metadata=@();$index=0
    foreach($relative in @('Current/main.dll','IndependentState/registration.json','installed-files.json')){
        $target=Join-Path $root $relative
        $source=Join-Path $root ('payloads\'+$index)
        [IO.File]::WriteAllText($target,'original-'+$relative)
        [IO.File]::WriteAllText($source,'replacement-'+$relative)
        $entry=[pscustomobject]@{Relative=$relative;Source=$source;Sha256=(Get-FileHash $source).Hash}
        if($index++ -eq 0){$files+=,$entry}else{$metadata+=,$entry}
    }
    $lock=$null;$failure=$null;$transaction=$null
    if($scenario -eq 'metadata-locked'){$lock=[IO.File]::Open((Join-Path $root 'installed-files.json'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)}
    if($scenario -eq 'state-excluded'){$metadata[1].Relative='IndependentState/project-state.json'}
    try{$transaction=Invoke-IndependentFileRepair -Files $files -InstallDirectory $root -MetadataFiles $metadata}catch{$failure=$_.Exception}finally{if($lock){$lock.Dispose()}}
    if($scenario -eq 'success-and-replay'){
        if($failure){throw $failure}
        foreach($entry in @($files)+@($metadata)){
            if([IO.File]::ReadAllText((Join-Path $root $entry.Relative)) -ne ('replacement-'+$entry.Relative)){throw 'Coordinated replacement missing'}
        }
        $journal=Join-Path $transaction 'transaction.json';$record=[IO.File]::ReadAllText($journal)|ConvertFrom-Json
        if($record.schemaVersion -ne 2){throw 'Metadata transaction must be versioned'}
        $record.phase='Prepared';[IO.File]::WriteAllText($journal,($record|ConvertTo-Json -Depth 4))
        Restore-IndependentFileTransaction $root ([IO.Path]::GetFileName($transaction))|Out-Null
    }elseif(-not $failure){throw 'Expected metadata rejection'}
    foreach($relative in @('Current/main.dll','IndependentState/registration.json','installed-files.json')){
        if([IO.File]::ReadAllText((Join-Path $root $relative)) -ne ('original-'+$relative)){throw 'Component or metadata not restored'}
    }
    if($scenario -eq 'metadata-locked'){
        $failedCopies=@(Get-ChildItem -LiteralPath (Join-Path $root 'Repair') -Filter '*.failed' -Recurse -File)
        if($failedCopies.Count -ne 2){throw 'Failure did not follow main and registration replacement'}
    }
}
Write-Output ('PASS coordinated component/registration/receipt rollback 3/3; '+$suite)
