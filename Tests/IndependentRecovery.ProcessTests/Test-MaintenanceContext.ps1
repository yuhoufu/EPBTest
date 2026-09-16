#requires -Version 5.1
param([string]$EvidenceRoot=$env:EPB_TEST_ARTIFACT_ROOT)
$ErrorActionPreference='Stop'
if(-not $EvidenceRoot){throw 'EPB_TEST_ARTIFACT_ROOT required'}
. (Join-Path $PSScriptRoot '..\..\Tools\Independent-MaintenanceContext.ps1')
$root=Join-Path $EvidenceRoot ('maintenance-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root)|Out-Null
$script:passed=0
function Check([bool]$Value,[string]$Message){if(-not $Value){throw $Message};$script:passed++}
function Reject([scriptblock]$Action,[string]$Message){$rejected=$false;try{& $Action|Out-Null}catch{$rejected=$true};Check $rejected $Message}
Check ($null -eq (Get-IndependentMaintenanceTransaction $root 'RollbackUpgrade' '4.1.0.4')) 'No transaction does not invent an ID'
$id=[Guid]::NewGuid().ToString('N');$directory=Join-Path $root ('Upgrade\'+$id)
[IO.Directory]::CreateDirectory($directory)|Out-Null
$path=Join-Path $directory 'result.json'
$record=@{toVersion='4.1.0.4';fromVersion='4.1.0.3';stage='Completed'}
[IO.File]::WriteAllText($path,($record|ConvertTo-Json))
Check ((Get-IndependentMaintenanceTransaction $root 'RollbackUpgrade' '4.1.0.4').Id -eq $id) 'Unique applicable upgrade inferred'
Check ($null -eq (Get-IndependentMaintenanceTransaction $root 'RollbackUpgrade' '4.1.0.5')) 'Other version is not selected'
Check ($null -eq (Get-IndependentMaintenanceTransaction $root 'FinalizeUpgrade' '4.1.0.4')) 'Completed upgrade needs no finalization'
$record.stage='FinalizationFailed';[IO.File]::WriteAllText($path,($record|ConvertTo-Json))
Check ((Get-IndependentMaintenanceTransaction $root 'FinalizeUpgrade' '4.1.0.4').Id -eq $id) 'Failed finalization inferred'
$second=Join-Path $root ('Upgrade\'+[Guid]::NewGuid().ToString('N'));[IO.Directory]::CreateDirectory($second)|Out-Null
[IO.File]::WriteAllText((Join-Path $second 'result.json'),($record|ConvertTo-Json))
Reject {Get-IndependentMaintenanceTransaction $root 'FinalizeUpgrade' '4.1.0.4'} 'Ambiguous transactions must not silently select one'
$repairId=[Guid]::NewGuid().ToString('N');$repair=Join-Path $root ('Repair\'+$repairId);[IO.Directory]::CreateDirectory($repair)|Out-Null
foreach($phase in @('Prepared','RollbackFailed')){
 [IO.File]::WriteAllText((Join-Path $repair 'transaction.json'),(@{schemaVersion=2;phase=$phase}|ConvertTo-Json))
 Check ((Get-IndependentMaintenanceTransaction $root 'RecoverFiles' '4.1.0.4').Id -eq $repairId) ('Interrupted phase discovered: '+$phase)
}
foreach($phase in @('Replaced','RolledBack')){
 [IO.File]::WriteAllText((Join-Path $repair 'transaction.json'),(@{schemaVersion=2;phase=$phase}|ConvertTo-Json))
 Check ($null -eq (Get-IndependentMaintenanceTransaction $root 'RecoverFiles' '4.1.0.4')) ('Completed phase ignored: '+$phase)
}
Check ((Find-IndependentPreviousBundle $root '4.1.0.3') -eq '') 'No cache does not guess another build'
$source=Join-Path $root '中文 原包';[IO.Directory]::CreateDirectory((Join-Path $source 'Tools'))|Out-Null
[IO.File]::WriteAllText((Join-Path $source 'Tools\probe.ps1'),'original verified bytes')
$manifest=@{version='4.1.0.3';gitCommit=('a'*40);files=@(@{path='Tools/probe.ps1';sha256=(Get-FileHash (Join-Path $source 'Tools\probe.ps1')).Hash})}
[IO.File]::WriteAllText((Join-Path $source 'automatic-bundle.json'),($manifest|ConvertTo-Json -Depth 4))
[IO.File]::WriteAllText((Join-Path $source 'unowned.log'),'do not cache')
$cached=Save-IndependentBundleCache $source $root
Check ((Find-IndependentPreviousBundle $root '4.1.0.3') -eq $cached) 'Prior bundle automatically rediscovered'
Check (-not [IO.File]::Exists((Join-Path $cached 'unowned.log'))) 'Cache excludes unrelated logs'
Check ((Save-IndependentBundleCache $source $root) -eq $cached) 'Cache publication is idempotent'
$manifest.files[0].sha256='0'*64
[IO.File]::WriteAllText((Join-Path $source 'automatic-bundle.json'),($manifest|ConvertTo-Json -Depth 4))
Reject {Save-IndependentBundleCache $source $root} 'Same build changed manifest rejected'
$manifest.gitCommit='b'*40
[IO.File]::WriteAllText((Join-Path $source 'automatic-bundle.json'),($manifest|ConvertTo-Json -Depth 4))
Reject {Save-IndependentBundleCache $source $root} 'Corrupt source cannot publish cache'
Check ((Find-IndependentPreviousBundle $root '4.1.0.3') -eq $cached) 'Incomplete staging is never selected'
$manifest.files[0].path='../escape.ps1'
[IO.File]::WriteAllText((Join-Path $source 'automatic-bundle.json'),($manifest|ConvertTo-Json -Depth 4))
Reject {Save-IndependentBundleCache $source $root} 'Cache path traversal rejected'
$nearby=Join-Path $root 'nearby';$current=Join-Path $nearby 'new';$old=Join-Path $nearby 'old'
[IO.Directory]::CreateDirectory($current)|Out-Null;[IO.Directory]::CreateDirectory($old)|Out-Null
$nearManifest=@{version='4.1.0.3';files=@(@{path='Base/MTTFTest.exe';sha256=('a'*64)})}
[IO.File]::WriteAllText((Join-Path $old 'automatic-bundle.json'),($nearManifest|ConvertTo-Json -Depth 4))
Check ((Find-IndependentNearbyBundle $current '4.1.0.3' ('a'*64)) -eq $old) 'Original package discovered beside new package'
Check ((Find-IndependentNearbyBundle $current '4.1.0.3' ('b'*64)) -eq '') 'Different build is not guessed'
Check ((Find-IndependentNearbyBundle $current '4.1.0.2' '') -eq '') 'Different version is not guessed'
$duplicate=Join-Path $nearby 'duplicate';[IO.Directory]::CreateDirectory($duplicate)|Out-Null
[IO.File]::Copy((Join-Path $old 'automatic-bundle.json'),(Join-Path $duplicate 'automatic-bundle.json'),$false)
Check ((Find-IndependentNearbyBundle $current '4.1.0.3' ('a'*64)) -eq '') 'Multiple original packages require selection'
Write-Output ('PASS maintenance context '+$script:passed+' checks; '+$root)
