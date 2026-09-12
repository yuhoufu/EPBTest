param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Resolve-FallbackBinding.ps1')
. (Join-Path $PSScriptRoot 'Persistent-Fallback.ps1')
$root=[IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($root) | Out-Null
$install=Join-Path $root ('isolated-install-'+[Guid]::NewGuid().ToString('N'))
$paths=Get-PersistentFallbackPaths $install
$manager=Join-Path $PSScriptRoot 'Manage-PersistentFallback.ps1'
$record=@{Task=$paths.Task;InstallRoot=$install;Passed=$false;Removed=$false}
Save-FallbackBinding (Join-Path $root 'owned-monitor.json') $record
function Wait-TaskStopped {
 $deadline=[DateTime]::UtcNow.AddSeconds(8)
 do{Start-Sleep -Milliseconds 250;$task=Get-ScheduledTask -TaskName $paths.Task}while($task.State -eq 'Running' -and [DateTime]::UtcNow -lt $deadline)
 if($task.State -eq 'Running'){throw 'Monitor failed to exit after disable'}
}
try {
 & $manager -Mode Install -InstallRoot $install
 $deadline=[DateTime]::UtcNow.AddSeconds(8)
 while(-not (Test-Path -LiteralPath $paths.Status) -and [DateTime]::UtcNow -lt $deadline){Start-Sleep -Milliseconds 250}
 $status=Read-FallbackBindingJson $paths.Status
 if(-not $status.Enabled -or $status.PrivateBytes -gt 128MB){throw 'Waiting monitor state or memory failed'}
 $process=Get-Process -Id $status.ProcessId -ErrorAction Stop
 $record.PrivateBytes=[long]$process.PrivateMemorySize64
 $record.ProcessId=[int]$process.Id;$record.StartUtcTicks=[long]$process.StartTime.ToUniversalTime().Ticks
 Save-FallbackBinding (Join-Path $root 'owned-monitor.json') $record
 if($record.PrivateBytes -gt 128MB){throw 'Monitor memory budget exceeded'}
 & $manager -Mode Enable -InstallRoot $install
 Start-Sleep -Seconds 1
 $status=Read-FallbackBindingJson $paths.Status
 if($status.ProcessId -ne $record.ProcessId){throw 'Repeated enable replaced singleton'}
 & $manager -Mode Disable -InstallRoot $install
 Wait-TaskStopped
 Start-ScheduledTask -TaskName $paths.Task
 Start-Sleep -Seconds 2
 Wait-TaskStopped
 if((Get-ScheduledTaskInfo -TaskName $paths.Task).LastTaskResult -ne 0){throw 'Disabled startup failed'}
 $record.Passed=$true
} finally {
 & $manager -Mode Uninstall -InstallRoot $install
 $record.Removed=(-not (Get-ScheduledTask -TaskName $paths.Task -ErrorAction SilentlyContinue))
 $remaining=Get-Process -Id $record.ProcessId -ErrorAction SilentlyContinue
 $record.ProcessExited=(-not $remaining -or $remaining.StartTime.ToUniversalTime().Ticks -ne $record.StartUtcTicks)
 Save-FallbackBinding (Join-Path $root 'result.json') $record
}
if(-not $record.Removed -or -not $record.ProcessExited){throw 'Owned task/process cleanup not proven'}
Write-Output 'PASS persistent task lifecycle'
