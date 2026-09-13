param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Watch-ActiveFallback.ps1')
$root=[IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($root) | Out-Null
$selection=Join-Path $root 'selection.json'
$project=Join-Path $root 'project with spaces'
[IO.Directory]::CreateDirectory((Join-Path $project 'Config')) | Out-Null
[IO.File]::WriteAllText((Join-Path $project 'Config\TestConfig.xml'),'<TestConfig/>')
Save-FallbackBinding $selection @{schemaVersion=1;storeDir=$root;testName='project with spaces'}
$passed=0
function Check($Condition,[string]$Message){if(-not $Condition){throw $Message};$script:passed++}
Check ((Get-ConfiguredFallbackProject $root $selection) -eq (Join-Path $project 'WatchdogSessions')) 'Saved project resolution'
Save-FallbackBinding $selection @{schemaVersion=99;storeDir=$root;testName='project with spaces'}
$rejected=$false;try{Get-ConfiguredFallbackProject $root $selection | Out-Null}catch{$rejected=$true};Check $rejected 'Unknown config rejected'
[IO.File]::Delete($selection)
[IO.Directory]::CreateDirectory((Join-Path $root 'Current')) | Out-Null
[IO.File]::WriteAllText((Join-Path $root 'Current\MTTFTest.exe.config'),('<configuration><appSettings><add key="InitialProjectPath" value="'+[Security.SecurityElement]::Escape($project)+'"/></appSettings></configuration>'))
Check ((Get-ConfiguredFallbackProject $root $selection) -eq (Join-Path $project 'WatchdogSessions')) 'Bootstrap config resolution'
# Execute the production monitor loop with deterministic process/config seams.
# The real atomic settings/binding/status files and startup/drain order remain.
$script:testPaths=[pscustomobject]@{Token=[Guid]::NewGuid().ToString('N');Settings=(Join-Path $root 'settings.json');Binding=(Join-Path $root 'binding.json');Status=(Join-Path $root 'status.json')}
function Get-PersistentFallbackPaths {param($InstallRoot) return $script:testPaths}
$script:tick=0;$script:launches=0;$script:disabled=0;$script:present=$false;$script:canDrain=$true
$script:first=[pscustomobject]@{ProjectDirectory=$project;SessionId=('1'*32)}
$script:second=[pscustomobject]@{ProjectDirectory=$project;SessionId=('2'*32)}
function Resolve-ConfiguredFallbackBinding {
 param($InstallRoot)
 if($script:tick -eq 0){throw 'waiting'}
 if($script:tick -le 2){return $script:first}
 return $script:second
}
function Invoke-PersistentGuard {param($Action,$Binding) if($Action -eq '--disable'){$script:disabled++;if($script:canDrain){$script:present=$false}}}
function Test-FallbackDrained {param($Binding) return ($script:canDrain -and -not $script:present)}
function Get-BoundGuardProcesses {param($Binding) if($script:present){return [pscustomobject]@{ProcessId=123}}}
function Start-Process {
 param($FilePath,$ArgumentList,[switch]$PassThru,$WindowStyle)
 Check (Test-Path -LiteralPath $script:testPaths.Binding) 'Intent persisted before launch'
 $script:launches++;$script:present=$true
 return New-Object IO.MemoryStream
}
function Start-Sleep {
 param($Seconds)
 $script:tick++
 switch($script:tick){
  1 {Check ($script:launches -eq 0) 'Wait without app'}
  3 {$script:canDrain=$false;Check ($script:launches -eq 1) 'Same session not duplicated'}
  5 {Check ($script:launches -eq 1) 'Unreturned ownership blocks switch';$script:canDrain=$true}
  6 {Check ($script:launches -eq 2) 'New session auto-bound after drain';Save-FallbackBinding $script:testPaths.Settings @{SchemaVersion=1;InstallRoot=$root;Enabled=$false}}
 }
 if($script:tick -gt 8){throw 'Monitor did not stop'}
}
Save-FallbackBinding $script:testPaths.Settings @{SchemaVersion=1;InstallRoot=$root;Enabled=$true}
Invoke-FallbackMonitor $root
Check (-not (Test-Path -LiteralPath $script:testPaths.Binding)) 'Disabled binding drained'
Check ($script:disabled -ge 2) 'Protocol disable used'
$before=$script:launches
Invoke-FallbackMonitor $root
Check ($script:launches -eq $before) 'Persisted disable prevents revival'
# Crash with persisted intent: startup drains before any new launch.
Save-FallbackBinding $script:testPaths.Binding $script:first
Invoke-FallbackMonitor $root
Check (-not (Test-Path -LiteralPath $script:testPaths.Binding)) 'Crash intent reconciled when disabled'
# Original heartbeat/sidecar can disappear precisely when DB fallback is
# needed. A launcher restart must retain that observer, then revive only the
# observer if it exits; it must never disable it because resolution failed.
function Resolve-ConfiguredFallbackBinding {param($InstallRoot) throw 'Original snapshot unavailable'}
$script:tick=0;$script:present=$true;$script:canDrain=$true
$script:beforeDisabled=$script:disabled;$script:beforeLaunches=$script:launches
Save-FallbackBinding $script:testPaths.Binding $script:first
Save-FallbackBinding $script:testPaths.Settings @{SchemaVersion=1;InstallRoot=$root;Enabled=$true}
function Start-Sleep {
 param($Seconds)
 $script:tick++
 if($script:tick -eq 1){
  Check ($script:disabled -eq $script:beforeDisabled) 'Missing original snapshot did not disable observer'
  Check ($script:launches -eq $script:beforeLaunches) 'Launcher restart retained existing observer'
  $script:present=$false
 }elseif($script:tick -eq 2){
  Check ($script:launches -eq $script:beforeLaunches+1) 'Observer restarted without original heartbeat'
  Save-FallbackBinding $script:testPaths.Settings @{SchemaVersion=1;InstallRoot=$root;Enabled=$false}
 }
 if($script:tick -gt 4){throw 'Missing snapshot fixture did not stop'}
}
Invoke-FallbackMonitor $root
Check (-not (Test-Path -LiteralPath $script:testPaths.Binding)) 'Explicit disable still drained retained observer'
Write-Output "PASS $passed/$passed"
