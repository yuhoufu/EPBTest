#requires -Version 5.1
[CmdletBinding()]
param(
 [Parameter(Mandatory=$true)][string]$RegistrationPath,
 [Parameter(Mandatory=$true)][string]$ExecutorPath,
 [Parameter(Mandatory=$true)][string]$OutputDirectory
)
# Public entry points accept PowerShell 7; .NET Framework deployment work is
# executed by the Windows PowerShell host with typed, data-only arguments.
if ($PSVersionTable.PSEdition -eq 'Core') {
    $epbBridgeParameters = @{}
    foreach ($epbBridgeKey in $PSBoundParameters.Keys) {
        $epbBridgeValue = $PSBoundParameters[$epbBridgeKey]
        if ($epbBridgeValue -is [Management.Automation.SwitchParameter]) { $epbBridgeValue = [bool]$epbBridgeValue }
        $epbBridgeParameters[$epbBridgeKey] = $epbBridgeValue
    }
    $epbBridgeData = @{ Script = $PSCommandPath; Parameters = $epbBridgeParameters } | ConvertTo-Json -Depth 5 -Compress
    $epbBridgePayload = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($epbBridgeData))
    $epbBridgeCode = '$ErrorActionPreference="Stop";$ProgressPreference="SilentlyContinue";$d=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String("' + $epbBridgePayload + '"))|ConvertFrom-Json;$p=@{};foreach($v in $d.Parameters.PSObject.Properties){$p[$v.Name]=$v.Value};$global:LASTEXITCODE=0;& ([string]$d.Script) @p;exit $LASTEXITCODE'
    $epbBridgeEncoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($epbBridgeCode))
    & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -EncodedCommand $epbBridgeEncoded
    exit $LASTEXITCODE
}
$ErrorActionPreference='Stop'
$worker=$null;$failure=$null;$files=@();$omitted=@();$totalBytes=0;$clock=[Diagnostics.Stopwatch]::StartNew()
function Read-BoundedText([string]$Path){
 $reader=[IO.StreamReader]::new($Path,[Text.Encoding]::UTF8,$true)
 try{$buffer=New-Object char[] 65537;$count=0;while($count -lt $buffer.Length){$n=$reader.Read($buffer,$count,$buffer.Length-$count);if($n -eq 0){break};$count+=$n};if($count -gt 65536){throw 'Evidence source exceeds 64 Ki characters'};[string]::new($buffer,0,$count)}finally{$reader.Dispose()}
}
function Save-Evidence([string]$Name,[string]$Text){
 if($clock.ElapsedMilliseconds -gt 20000){throw 'Evidence time budget exceeded'}
 $bytes=[Text.Encoding]::UTF8.GetByteCount($Text);$script:totalBytes+=$bytes
 if($script:totalBytes -gt 2MB){throw 'Evidence size budget exceeded'}
 $path=Join-Path $OutputDirectory $Name
 $stream=[IO.File]::Open($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
 try{$writer=[IO.StreamWriter]::new($stream,[Text.UTF8Encoding]::new($false));try{$writer.Write($Text)}finally{$writer.Dispose()}}finally{$stream.Dispose()}
 $script:files+=,[ordered]@{path=$Name;bytes=$bytes;sha256=[string](Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
}
$status=& (Join-Path $PSScriptRoot 'Manage-IndependentRecovery.ps1') -Mode Status -RegistrationPath $RegistrationPath -ExecutorPath $ExecutorPath
$registration=[MTTFTest.Watchdog.Protocol.IndependentExecutorRegistration]::LoadTrusted($RegistrationPath)
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
if($OutputDirectory.Contains('"') -or $OutputDirectory.StartsWith('\\')){throw 'Evidence destination must be a local path without quotes'}
foreach($protected in @($registration.ProjectDirectory,$registration.StateDirectory,[IO.Path]::GetDirectoryName($ExecutorPath),[IO.Path]::GetDirectoryName($registration.ExecutablePath))){
 $protected=[IO.Path]::GetFullPath($protected).TrimEnd('\')
 if($OutputDirectory -eq $protected -or $OutputDirectory.StartsWith($protected+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Evidence destination must be outside project and installed components'}
}
if(Test-Path -LiteralPath $OutputDirectory){throw 'Evidence destination exists; refusing overwrite'}
$ancestor=[IO.DirectoryInfo]::new($OutputDirectory)
while($ancestor){if($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Evidence destination contains a reparse point'};$ancestor=$ancestor.Parent}
[IO.Directory]::CreateDirectory($OutputDirectory)|Out-Null
try{
 Save-Evidence 'component-status.json' ($status|ConvertTo-Json -Depth 4)
 Save-Evidence 'registration.json' (Read-BoundedText $RegistrationPath)
 $statePath=Join-Path $registration.StateDirectory 'independent-project-state.json'
 $stateText=Read-BoundedText $statePath;Save-Evidence 'independent-project-state.json' $stateText
 $state=$stateText|ConvertFrom-Json
 $observation=Join-Path $registration.StateDirectory 'executor-observation.json'
 if([IO.File]::Exists($observation)){Save-Evidence 'executor-observation.json' (Read-BoundedText $observation)}else{$omitted+='ObservationMissing'}
 if($state.Transaction){
  $request=[string]$state.Transaction.RequestId
  if($request -notmatch '^[0-9a-f]{32}$'){throw 'Invalid recovery transaction identity'}
  $baseline=Join-Path $registration.StateDirectory ('verification-'+$request+'.json')
  if([IO.File]::Exists($baseline)){Save-Evidence 'verification-baseline.json' (Read-BoundedText $baseline)}
  $enumerated=0;$matched=0
  foreach($path in [IO.Directory]::EnumerateFiles($registration.StateDirectory,'safety-*.json')){
   if(++$enumerated -gt 64){$omitted+='SafetyEnumerationTruncatedAt64';break}
   $text=Read-BoundedText $path;$item=$text|ConvertFrom-Json
   if([string]$item.RequestId -eq $request){if(++$matched -gt 16){$omitted+='SafetyFilesTruncatedAt16';break};Save-Evidence ([IO.Path]::GetFileName($path)) $text}
  }
 }
 # Query all twelve lanes for diagnosis, including excluded lanes; this does
 # not authorize any of them to run. A point-in-time report is not a DB backup.
 $output=Join-Path $OutputDirectory 'database-progress.json'
 $callback=[Action[int,long]]{param([int]$childPid,[long]$ticks);Save-Evidence 'reader-owner.json' (([ordered]@{pid=$childPid;startUtcTicks=$ticks;path=$ExecutorPath;purpose='IndependentEvidenceReadOnly'}|ConvertTo-Json -Depth 2))}
 $worker=[MTTFTest.Watchdog.Protocol.IndependentBoundedWorker]::new($ExecutorPath,('--read-recovery-database "'+$registration.DatabasePath+'" --channels 1,2,3,4,5,6,7,8,9,10,11,12 --output "'+$output+'"'),[IO.Path]::GetDirectoryName($ExecutorPath),3000,256,$callback)
 while($worker.Poll().ToString() -eq 'Running'){Start-Sleep -Milliseconds 25}
 if($worker.Poll().ToString() -ne 'Completed' -or $worker.ExitCode -ne 0){$omitted+='DatabaseReadFailed:'+ $worker.Poll().ToString()+':'+$worker.ExitCode}
 else{
  $database=Read-BoundedText $output|ConvertFrom-Json
  if([string]$database.DatabasePath -ne $registration.DatabasePath -or [long]$database.CreationUtcTicks -ne $registration.DatabaseCreationUtcTicks){throw 'Evidence database identity mismatch'}
  $files+=,[ordered]@{path='database-progress.json';bytes=[long](Get-Item $output).Length;sha256=[string](Get-FileHash $output -Algorithm SHA256).Hash}
 }
}catch{$failure=$_.Exception.Message}
finally{
 if($worker){$worker.Dispose()}
 $summary=[ordered]@{schemaVersion=1;utc=[DateTime]::UtcNow.ToString('O');installation=[string]$registration.InstallationId;failure=$failure;omitted=$omitted;files=$files;scope='Bounded independent recovery state and readonly formal-record summary; no full database or waveform backup';businessRecovery='NOT_VERIFIED';crossFileAtomic=$false}
 [IO.File]::WriteAllText((Join-Path $OutputDirectory 'evidence-manifest.json'),($summary|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
}
if($failure){throw $failure}
Write-Output ('独立恢复关键证据已保存：'+$OutputDirectory+'；缺失项='+$omitted.Count+'；未声明动作恢复或完整数据库备份。')
