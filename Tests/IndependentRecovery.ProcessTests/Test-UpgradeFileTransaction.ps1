param([string]$EvidenceRoot=$env:EPB_TEST_ARTIFACT_ROOT)
$ErrorActionPreference='Stop'
if(-not $EvidenceRoot){throw 'Evidence root required'}
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '..\..\Tools\Install-IndependentRecoveryBundle.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Installer parse failed'}
$definition=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Invoke-IndependentFileRepair'},$true)
. ([scriptblock]::Create($definition.Extent.Text))
$suite=Join-Path $EvidenceRoot ('upgrade-files-'+[Guid]::NewGuid().ToString('N'))
foreach($scenario in @('success','postcondition-failure','retirement-locked','changed-owner','config-excluded')){
    $root=Join-Path $suite $scenario
    [IO.Directory]::CreateDirectory((Join-Path $root 'Current'))|Out-Null
    $old=Join-Path $root 'Current\obsolete.dll'
    $target=Join-Path $root 'Current\main.dll'
    $added=Join-Path $root 'Current\added.dll'
    $source=Join-Path $root 'payload.dll'
    [IO.File]::WriteAllText($old,'old component')
    [IO.File]::WriteAllText($target,'old main')
    [IO.File]::WriteAllText($source,'new version')
    $hash=(Get-FileHash -LiteralPath $source).Hash
    $files=@([pscustomobject]@{Relative='Current/main.dll';Source=$source;Sha256=$hash},
        [pscustomobject]@{Relative='Current/added.dll';Source=$source;Sha256=$hash})
    $retired=@([pscustomobject]@{Relative='Current/obsolete.dll';Sha256=(Get-FileHash -LiteralPath $old).Hash})
    $lock=$null;$failure=$null;$verify=$null
    if($scenario -eq 'changed-owner'){$retired[0].Sha256='0'*64}
    if($scenario -eq 'config-excluded'){$retired[0].Relative='Current\Config\obsolete.dll'}
    if($scenario -eq 'retirement-locked'){$lock=[IO.File]::Open($old,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)}
    if($scenario -eq 'postcondition-failure'){
        $verify={
            if([IO.File]::Exists($old) -or -not [IO.File]::Exists($added) -or [IO.File]::ReadAllText($target) -ne 'new version'){throw 'Expected replacement state was not reached'}
            [IO.File]::WriteAllText((Join-Path $root 'postcondition-observed.txt'),'new installed, old retired')
            throw 'Injected post-replacement verification failure'
        }.GetNewClosure()
    }
    try{Invoke-IndependentFileRepair $files $root $retired $verify|Out-Null}catch{$failure=$_.Exception}finally{if($lock){$lock.Dispose()}}
    $journals=@(Get-ChildItem -LiteralPath (Join-Path $root 'Repair') -Filter transaction.json -File -Recurse)
    if($journals.Count -ne 1){throw 'Expected one transaction'}
    $record=[IO.File]::ReadAllText($journals[0].FullName)|ConvertFrom-Json
    if($scenario -eq 'success'){
        if($failure){throw $failure}
        if([IO.File]::Exists($old) -or [IO.File]::ReadAllText($target) -ne 'new version' -or [IO.File]::ReadAllText($added) -ne 'new version'){throw 'Upgrade file result invalid'}
        $entry=@($record.entries|Where-Object{$_.retired})
        if($entry.Count -ne 1 -or [IO.File]::ReadAllText($entry[0].backup) -ne 'old component' -or $record.phase -ne 'Replaced'){throw 'Retired backup missing'}
    }else{
        if(-not $failure -or $record.phase -ne 'RolledBack'){throw 'Expected rolled-back failure'}
        if([IO.File]::ReadAllText($old) -ne 'old component' -or [IO.File]::ReadAllText($target) -ne 'old main' -or [IO.File]::Exists($added)){throw 'Rollback failed to restore original installation'}
        if($scenario -eq 'postcondition-failure' -and -not [IO.File]::Exists((Join-Path $root 'postcondition-observed.txt'))){throw 'Postcondition failure did not occur after retirement'}
    }
    $probe=[IO.File]::Open((Join-Path $root 'file-repair.lock'),[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);$probe.Dispose()
}
Write-Output ('PASS upgrade replacement, retirement and rollback 5/5; '+$suite)
