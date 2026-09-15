param([string]$EvidenceRoot=$env:EPB_TEST_ARTIFACT_ROOT)
$ErrorActionPreference='Stop'
if(-not $EvidenceRoot){throw 'Evidence root required'}
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '..\..\Tools\Install-IndependentRecoveryBundle.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Installer parse failed'}
$node=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Complete-IndependentUpgrade'},$true)
. ([scriptblock]::Create($node.Extent.Text))
$root=Join-Path $EvidenceRoot ('upgrade-finalize-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root)|Out-Null
$path=Join-Path $root 'result.json';$id=[Guid]::NewGuid().ToString('N')
$initial=[ordered]@{stage='FilesCommitted';installationId=$id;fromVersion='4.0.0.3';toVersion='4.1.0.0';finalizationRequired=$true;maintenance=$true;trialStarted=$false}
[IO.File]::WriteAllText($path,($initial|ConvertTo-Json))
$calls=[Collections.Generic.List[string]]::new()
$registration={
    if(([IO.File]::ReadAllText($path)|ConvertFrom-Json).stage -ne 'Registering'){throw 'Registration stage not durable'}
    $calls.Add('registration')
}
$shortcut={param($oldVersion)
    if($oldVersion -ne '4.0.0.3' -or ([IO.File]::ReadAllText($path)|ConvertFrom-Json).stage -ne 'UpdatingShortcut'){throw 'Shortcut stage or previous version invalid'}
    $calls.Add('shortcut')
}
$failed=$false
try{Complete-IndependentUpgrade $path '4.1.0.0' $id $registration {throw 'Injected shortcut failure'}}catch{$failed=$true}
$record=[IO.File]::ReadAllText($path)|ConvertFrom-Json
if(-not $failed -or $record.stage -ne 'FinalizationFailed' -or -not $record.finalizationRequired -or $record.failure -notlike '*Injected*'){throw 'Failed finalization falsely reported completion'}
Complete-IndependentUpgrade $path '4.1.0.0' $id $registration $shortcut
$record=[IO.File]::ReadAllText($path)|ConvertFrom-Json
if($record.stage -ne 'Completed' -or $record.finalizationRequired -or -not $record.maintenance -or $record.trialStarted -or ($calls -join ',') -ne 'registration,registration,shortcut'){throw 'Retry did not complete ordered stages'}
$failed=$false
try{Complete-IndependentUpgrade $path '4.1.0.0' ('0'*32) $registration $shortcut}catch{$failed=$true}
if(-not $failed -or $calls.Count -ne 3){throw 'Foreign installation finalization was executed'}
Write-Output ('PASS staged upgrade finalization, retry and foreign-identity rejection 3/3; '+$root)
