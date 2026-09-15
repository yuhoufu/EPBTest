param([string]$EvidenceRoot=$env:EPB_TEST_ARTIFACT_ROOT,[switch]$Child,[string]$FixtureRoot)
$ErrorActionPreference='Stop'
$installer=Join-Path $PSScriptRoot '..\..\Tools\Install-IndependentRecoveryBundle.ps1'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($installer,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Installer parse failed'}
foreach($name in @('Invoke-IndependentFileRepair','Restore-IndependentFileTransaction')){
    $node=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
    . ([scriptblock]::Create($node.Extent.Text))
}
if($Child){
    if(-not $FixtureRoot){throw 'Fixture required'}
    $source=Join-Path $FixtureRoot 'payload.dll'
    $files=@([pscustomobject]@{Relative='Current/main.dll';Source=$source;Sha256=(Get-FileHash $source).Hash},
        [pscustomobject]@{Relative='Current/added.dll';Source=$source;Sha256=(Get-FileHash $source).Hash})
    $retired=@([pscustomobject]@{Relative='Current/obsolete.dll';Sha256=(Get-FileHash (Join-Path $FixtureRoot 'Current\obsolete.dll')).Hash})
    $checkpoint={
        [IO.File]::WriteAllText((Join-Path $FixtureRoot 'replacement-ready.txt'),[string]$PID)
        # The parent has an independent deadline and memory limit. If the parent
        # disappears, this bounded wait ends in ordinary rollback, not a hang.
        Start-Sleep -Seconds 45
        throw 'Parent did not terminate test child within deadline'
    }.GetNewClosure()
    Invoke-IndependentFileRepair $files $FixtureRoot $retired $checkpoint|Out-Null
    throw 'Child unexpectedly completed'
}
if(-not $EvidenceRoot){throw 'Evidence root required'}
$root=Join-Path $EvidenceRoot ('file-crash-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory((Join-Path $root 'Current'))|Out-Null
[IO.File]::WriteAllText((Join-Path $root 'payload.dll'),'new')
[IO.File]::WriteAllText((Join-Path $root 'Current\main.dll'),'old')
[IO.File]::WriteAllText((Join-Path $root 'Current\obsolete.dll'),'obsolete')
$process=$null
try{
    $arguments='-NoProfile -ExecutionPolicy Bypass -File "'+$PSCommandPath+'" -Child -FixtureRoot "'+$root+'"'
    $process=Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -ArgumentList $arguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $root 'child.stdout.log') -RedirectStandardError (Join-Path $root 'child.stderr.log')
    $identity=[ordered]@{pid=$process.Id;startUtcTicks=$process.StartTime.ToUniversalTime().Ticks;fixture=$root}
    [IO.File]::WriteAllText((Join-Path $root 'child-owner.json'),($identity|ConvertTo-Json -Depth 2))
    $deadline=[Diagnostics.Stopwatch]::StartNew()
    $ready=Join-Path $root 'replacement-ready.txt'
    while(-not [IO.File]::Exists($ready)){
        $process.Refresh()
        if($process.HasExited){throw 'Child exited before crash point'}
        if($deadline.Elapsed.TotalSeconds -gt 25 -or $process.PrivateMemorySize64 -gt 256MB){throw 'Child exceeded resource or time budget'}
        Start-Sleep -Milliseconds 100
    }
    if([IO.File]::ReadAllText($ready) -ne [string]$identity.pid){throw 'Ready signal owner mismatch'}
    if([IO.File]::ReadAllText((Join-Path $root 'Current\main.dll')) -ne 'new' -or
       -not [IO.File]::Exists((Join-Path $root 'Current\added.dll')) -or
       [IO.File]::Exists((Join-Path $root 'Current\obsolete.dll'))){throw 'Crash point did not follow all file mutations'}
    $process.Refresh()
    if($process.StartTime.ToUniversalTime().Ticks -ne $identity.startUtcTicks){throw 'Process identity changed'}
    $process.Kill()
    if(-not $process.WaitForExit(5000)){throw 'Owned child did not exit'}
    $journals=@(Get-ChildItem -LiteralPath (Join-Path $root 'Repair') -Filter transaction.json -Recurse -File)
    if($journals.Count -ne 1){throw 'Expected one persisted transaction'}
    $record=[IO.File]::ReadAllText($journals[0].FullName)|ConvertFrom-Json
    if($record.phase -ne 'Prepared'){throw 'Child did not die before transaction commit'}
    Restore-IndependentFileTransaction $root $journals[0].Directory.Name|Out-Null
    if([IO.File]::ReadAllText((Join-Path $root 'Current\main.dll')) -ne 'old' -or
       [IO.File]::ReadAllText((Join-Path $root 'Current\obsolete.dll')) -ne 'obsolete' -or
       [IO.File]::Exists((Join-Path $root 'Current\added.dll'))){throw 'Crash recovery did not restore original bytes'}
    if(([IO.File]::ReadAllText($journals[0].FullName)|ConvertFrom-Json).phase -ne 'RolledBack'){throw 'Rollback not persisted'}
    [IO.File]::WriteAllText((Join-Path $root 'result.json'),([ordered]@{passed=$true;childExited=$process.HasExited;transaction=$journals[0].Directory.Name;phase='RolledBack'}|ConvertTo-Json -Depth 2))
    Write-Output ('PASS actual file-transaction child termination and persisted rollback; '+$root)
}finally{
    if($process){
        try{
            $process.Refresh()
            if(-not $process.HasExited){
                if($identity -and $process.StartTime.ToUniversalTime().Ticks -eq $identity.startUtcTicks){$process.Kill();if(-not $process.WaitForExit(5000)){throw 'Owned child cleanup failed'}}
                else{throw 'Cannot confirm child identity for cleanup'}
            }
        }finally{$process.Dispose()}
    }
}
