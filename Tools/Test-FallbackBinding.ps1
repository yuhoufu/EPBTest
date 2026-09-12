param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Resolve-FallbackBinding.ps1')
$root=[IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($root) | Out-Null
$install=Join-Path $root 'install';$project=Join-Path $root 'project with spaces'
[IO.Directory]::CreateDirectory($project) | Out-Null
$session=[Guid]::NewGuid().ToString('N');$run=[Guid]::NewGuid().ToString('N');$created=[DateTime]::UtcNow.AddMinutes(-1)
$process=[pscustomobject]@{ExecutablePath=(Join-Path $install 'Current\MTTFTest.Watchdog.exe');CommandLine=('--session-id "{0}" --journal-directory "{1}"' -f $session,$project);ProcessId=1234;CreationDate=$created}
$script:fixture=@($process)
function Get-CimInstance {param($ClassName,$Filter) return $script:fixture}
$snapshotPath=Join-Path $project ('fallback-observation-'+$session+'.json')
$ledgerPath=Join-Path $project ('fallback-'+$session+'.json')
function Reset-Snapshot {
 Save-FallbackBinding $snapshotPath @{SchemaVersion=1;SessionId=$session;CapturedUtc=[DateTime]::UtcNow.ToString('O');ProcessId=1234;ProcessStartUtcTicks=$created.Ticks;Heartbeat=@{RunId=$run}}
 Save-FallbackBinding $ledgerPath @{SchemaVersion=1;SessionId=$session;RunId=$run}
}
$passed=0
function Check-Rejected([scriptblock]$Action){$rejected=$false;try{& $Action | Out-Null}catch{$rejected=$true};if(-not $rejected){throw 'Expected rejection'};$script:passed++}
Reset-Snapshot
$value=Resolve-FallbackBinding $install
if($value.ProjectDirectory -ne $project -or $value.SessionId -ne $session){throw 'Quoted command binding failed'};$passed++
$script:fixture=@();Check-Rejected {Resolve-FallbackBinding $install}
$script:fixture=@($process,$process);Check-Rejected {Resolve-FallbackBinding $install}
$script:fixture=@($process)
$bad=Read-FallbackBindingJson $snapshotPath;$bad.CapturedUtc=[DateTime]::UtcNow.AddMinutes(-1).ToString('O');Save-FallbackBinding $snapshotPath $bad;Check-Rejected {Resolve-FallbackBinding $install}
Reset-Snapshot;$bad=Read-FallbackBindingJson $snapshotPath;$bad.ProcessStartUtcTicks++;Save-FallbackBinding $snapshotPath $bad;Check-Rejected {Resolve-FallbackBinding $install}
Reset-Snapshot;$bad=Read-FallbackBindingJson $snapshotPath;$bad.ProcessId=5678;Save-FallbackBinding $snapshotPath $bad;Check-Rejected {Resolve-FallbackBinding $install}
Reset-Snapshot;$bad=Read-FallbackBindingJson $ledgerPath;$bad.RunId='other';Save-FallbackBinding $ledgerPath $bad;Check-Rejected {Resolve-FallbackBinding $install}
Reset-Snapshot;$bad=Read-FallbackBindingJson $ledgerPath;$bad.SchemaVersion=99;Save-FallbackBinding $ledgerPath $bad;Check-Rejected {Resolve-FallbackBinding $install}
[IO.File]::WriteAllText($snapshotPath,('x'*65537));Check-Rejected {Read-FallbackBindingJson $snapshotPath}
$cached=Join-Path $root 'cache.json';Save-FallbackBinding $cached @{SchemaVersion=1;SessionId=$session};Save-FallbackBinding $cached @{SchemaVersion=1;SessionId='changed'}
if((Read-FallbackBindingJson $cached).SessionId -ne 'changed'){throw 'Atomic binding update failed'};$passed++
Write-Output "PASS $passed/$passed"
