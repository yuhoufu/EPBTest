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
$suite=Join-Path $EvidenceRoot ('file-replay-'+[Guid]::NewGuid().ToString('N'))
foreach($scenario in @('replay','changed-target','damaged-backup')){
    $root=Join-Path $suite $scenario
    [IO.Directory]::CreateDirectory((Join-Path $root 'Current'))|Out-Null
    $source=Join-Path $root 'payload.dll';[IO.File]::WriteAllText($source,'new')
    $main=Join-Path $root 'Current\main.dll';[IO.File]::WriteAllText($main,'old')
    $old=Join-Path $root 'Current\obsolete.dll';[IO.File]::WriteAllText($old,'retired')
    $added=Join-Path $root 'Current\added.dll'
    $files=@([pscustomobject]@{Relative='Current/main.dll';Source=$source;Sha256=(Get-FileHash $source).Hash},
        [pscustomobject]@{Relative='Current/added.dll';Source=$source;Sha256=(Get-FileHash $source).Hash})
    $retired=@([pscustomobject]@{Relative='Current/obsolete.dll';Sha256=(Get-FileHash $old).Hash})
    $transaction=Invoke-IndependentFileRepair $files $root $retired
    $journal=Join-Path $transaction 'transaction.json'
    $record=[IO.File]::ReadAllText($journal)|ConvertFrom-Json
    # Model interruption after all file operations but before success commit.
    # This fixture is not an actual process-kill acceptance test.
    $record.phase='Prepared'
    [IO.File]::WriteAllText($journal,($record|ConvertTo-Json -Depth 4))
    if($scenario -eq 'changed-target'){[IO.File]::WriteAllText($main,'foreign')}
    if($scenario -eq 'damaged-backup'){[IO.File]::WriteAllText($record.entries[0].backup,'corrupt')}
    $failure=$null
    try{Restore-IndependentFileTransaction $root ([IO.Path]::GetFileName($transaction))|Out-Null}catch{$failure=$_.Exception}
    if($scenario -eq 'replay'){
        if($failure){throw $failure}
        # Replay again to prove already-restored entries are idempotent.
        Restore-IndependentFileTransaction $root ([IO.Path]::GetFileName($transaction))|Out-Null
        if([IO.File]::ReadAllText($main) -ne 'old' -or [IO.File]::ReadAllText($old) -ne 'retired' -or [IO.File]::Exists($added)){throw 'Original installation was not restored'}
        if(-not [IO.File]::Exists($record.entries[0].backup)){throw 'Replay consumed backup'}
        if(([IO.File]::ReadAllText($journal)|ConvertFrom-Json).phase -ne 'RolledBack'){throw 'Replay completion not persisted'}
    }else{
        if(-not $failure -or -not [IO.File]::Exists($added) -or [IO.File]::Exists($old)){throw 'Invalid state not rejected before any restoration'}
        $expected=if($scenario -eq 'changed-target'){'foreign'}else{'new'}
        if([IO.File]::ReadAllText($main) -ne $expected){throw 'Rejected replay changed main'}
    }
    $probe=[IO.File]::Open((Join-Path $root 'file-repair.lock'),[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);$probe.Dispose()
}
Write-Output ('PASS interrupted file state replay and ownership rejection 3/3; '+$suite)
