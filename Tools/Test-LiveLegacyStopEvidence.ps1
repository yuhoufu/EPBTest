#requires -Version 5.1
param([Parameter(Mandatory=$true)][string]$EvidenceDirectory)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'LegacyStopEvidence.ps1')
function Read-Utf8JsonFile([string]$Path,[string]$Label) { Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json }
function Get-CimInstance { param($ClassName,$Filter,$ErrorAction) if ($script:processPresent) { [pscustomobject]@{ProcessId=123} } }
$sandbox=Join-Path $env:TEMP ('LegacyStopEvidence-' + [Guid]::NewGuid().ToString('N'))
$root=Join-Path $sandbox 'Chinese space 测试\Install'
$store=Join-Path $sandbox 'Data'
$project=Join-Path $store 'Trial'
$journal=Join-Path $project 'WatchdogSessions'
$archive=Join-Path $sandbox 'Archive'
foreach($dir in @((Join-Path $root 'Current'),$journal,$archive)) { [void](New-Item -ItemType Directory -Path $dir -Force) }
$exe=Join-Path $root 'Current\MTTFTest.exe'
[IO.File]::WriteAllText($exe,'Not executable: fixture only')
$c=Read-Utf8JsonFile (Join-Path $EvidenceDirectory 'legacy-stopped-checkpoint.json') ''
$r=Read-Utf8JsonFile (Join-Path $EvidenceDirectory 'stop-safety.json') ''
$s=Read-Utf8JsonFile (Join-Path $EvidenceDirectory 'stopped-session.json') ''
$shift=[DateTime]::UtcNow.AddSeconds(-20).Ticks-[Math]::Max([long]$r.UpdatedUtcTicks,(Convert-LegacyEvidenceUtc $c.UpdatedUtc).Ticks)
$c.UpdatedUtc=[DateTime]::new(((Convert-LegacyEvidenceUtc $c.UpdatedUtc).Ticks+$shift),[DateTimeKind]::Utc).ToString('O')
$r.UpdatedUtcTicks=[long]$r.UpdatedUtcTicks+$shift
$s.LastVerifiedActiveRun.CapturedUtc=[DateTime]::new(((Convert-LegacyEvidenceUtc $s.LastVerifiedActiveRun.CapturedUtc).Ticks+$shift),[DateTimeKind]::Utc).ToString('O')
$c.StoreDir=$store; $c.TestName='Trial'; $c.ExecutableSha256=(Get-FileHash $exe).Hash
$s.ExecutablePath=$exe
$cp=Join-Path $sandbox 'checkpoint.json'
$sp=Join-Path $journal ('session-'+$s.SessionId+'.json')
$rp=Join-Path $journal ('session-'+$s.SessionId+'.closing.json')
function Save-Fixture { $c|ConvertTo-Json -Depth 32|Set-Content $cp -Encoding UTF8; $s|ConvertTo-Json -Depth 32|Set-Content $sp -Encoding UTF8; $r|ConvertTo-Json -Depth 32|Set-Content $rp -Encoding UTF8 }
function Check([string]$Name,[string]$ErrorPrefix='') {
    $failure=''
    try { $null=Get-LiveLegacyStopEvidence -Root $root -CheckpointPath $cp -Checkpoint $c -ArchiveRoot $archive }
    catch { $failure=$_.Exception.Message }
    if (($ErrorPrefix -eq '' -and $failure) -or ($ErrorPrefix -ne '' -and $failure -notlike "$ErrorPrefix*")) {throw "$Name failed: $failure"}
    Write-Output "PASS $Name"
}
Save-Fixture
Check 'Live journal accepts complete fixture'
foreach($name in @('stop-session.json','stop-terminal-receipt.json','stop-validation.json')) { if(-not(Test-Path (Join-Path $archive $name))) {throw 'Missing archived proof'} }
$script:processPresent=$true
Check 'Any Main process blocks migration' 'LegacyStopProcessStillPresent'
$script:processPresent=$false
$saved=$c.RunId; $c.RunId=[Guid]::NewGuid().ToString('N')
Check 'New run cannot reuse old journal' 'LegacyStopSessionMissingOrAmbiguous'
$c.RunId=$saved
$saved=$c.Revision; $c.Revision++
Check 'Changed checkpoint blocks migration' 'LegacyStopCheckpointChanged'
$c.Revision=$saved
$r.PowerOff=$false; Save-Fixture
Check 'Incomplete actual receipt blocks migration' 'LegacyStopIncomplete'
$r.PowerOff=$true; Save-Fixture
$savedCapture=$s.LastVerifiedActiveRun.CapturedUtc
$s.LastVerifiedActiveRun.CapturedUtc=[DateTime]::UtcNow.AddHours(-1).ToString('O'); Save-Fixture
Check 'Session update cannot renew old active observation' 'LegacyStopEvidenceExpiredOrClockInvalid'
$s.LastVerifiedActiveRun.CapturedUtc=$savedCapture
$oldLocal=$env:LOCALAPPDATA; $oldData=$env:ProgramData
try {
    $env:LOCALAPPDATA=Join-Path $sandbox 'Local'
    $env:ProgramData=Join-Path $sandbox 'ProgramData'
    [void](New-Item -ItemType Directory -Path (Join-Path $env:LOCALAPPDATA 'MTTFTest') -Force)
    $cp=Join-Path $env:LOCALAPPDATA 'MTTFTest\unattended-run-checkpoint.json'
    Save-Fixture
    $PhysicalIsolationConfirmed=$false; $maintenanceHeld=$true
    function Resolve-SafeDirectory([string]$Path,[string]$Label) { [IO.Path]::GetFullPath($Path) }
    $source=Get-Content (Join-Path $PSScriptRoot 'Install-MTTFTest-Unattended.ps1') -Raw -Encoding UTF8
    $match=[regex]::Match($source,'(?s)function Invoke-LegacyCheckpointSafeRollover\b.*?\r?\n\}')
    if(-not $match.Success){throw 'Installer migration function missing'}
    # Extracted function has no script origin; bind only its helper directory.
    $functionText=$match.Value.Replace('$PSScriptRoot',("'"+$PSScriptRoot.Replace("'","''")+"'"))
    Invoke-Expression $functionText
    Invoke-LegacyCheckpointSafeRollover $root
    $migration=Read-Utf8JsonFile (Join-Path $env:ProgramData 'MTTFTest\Migration\schema5-remaining-cycles-migration.json') ''
    if(Test-Path $cp){throw 'Original checkpoint not archived'}
    if($migration.operatorPhysicalIsolationConfirmed -or $migration.authorizationMigrated -or
        $migration.terminalStopEvidence.RunId -cne $c.RunId){throw 'Incorrect migration disposition'}
    $archived=Read-Utf8JsonFile (Join-Path $migration.archiveRoot 'unattended-run-checkpoint.json') ''
    if($archived.MotorOffConfirmed -or $archived.PressureSafeConfirmed -or $archived.PersistenceDrained){throw 'Historical checkpoint flags mutated'}
    Write-Output 'PASS Installer migration consumes live evidence without changing safety flags or authorization'
} finally { $env:LOCALAPPDATA=$oldLocal; $env:ProgramData=$oldData }
Write-Output "PASS 7/7; simulated journal and process query only; sandbox=$sandbox"
