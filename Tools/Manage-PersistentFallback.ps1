param([ValidateSet('Start','Enable','Disable','Status','Install','Uninstall','Run')][string]$Mode='Status',[string]$InstallRoot='C:\Program Files (x86)\MTTFTest')
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Resolve-FallbackBinding.ps1')
. (Join-Path $PSScriptRoot 'Persistent-Fallback.ps1')
$paths=Get-PersistentFallbackPaths $InstallRoot
$watcher=Join-Path $PSScriptRoot 'Watch-ActiveFallback.ps1'
if($Mode -eq 'Run'){& $watcher -InstallRoot $InstallRoot;exit $LASTEXITCODE}
if($Mode -eq 'Status'){
 if(Test-Path -LiteralPath $paths.Settings){Read-FallbackBindingJson $paths.Settings | ConvertTo-Json -Depth 3}else{Write-Output '尚未启用自动监控'}
 if(Test-Path -LiteralPath $paths.Status){Read-FallbackBindingJson $paths.Status | ConvertTo-Json -Depth 3}
 $task=Get-ScheduledTask -TaskName $paths.Task -ErrorAction SilentlyContinue
 if($task){Write-Output ('TaskState='+[string]$task.State)}
 exit 0
}
$mutex=New-Object Threading.Mutex($false,('Global\MTTF-FallbackMonitorSettings-'+$paths.Token))
$held=$false
try {
 try {$held=$mutex.WaitOne(5000)} catch [Threading.AbandonedMutexException] {$held=$true}
 if(-not $held){throw '另一项启停操作尚未完成'}
 $description='EPB 自动绑定独立兜底；安装='+[IO.Path]::GetFullPath($InstallRoot)+'；入口='+[IO.Path]::GetFullPath($watcher)
 $task=Get-ScheduledTask -TaskName $paths.Task -ErrorAction SilentlyContinue
 if($task -and $task.Description -ne $description){throw '已有监控任务属于其他候选目录，请先从原目录禁用并卸载'}
 if($Mode -in @('Disable','Uninstall')){
  Save-FallbackBinding $paths.Settings @{SchemaVersion=1;InstallRoot=[IO.Path]::GetFullPath($InstallRoot);Enabled=$false}
  if(Test-Path -LiteralPath $paths.Binding){Invoke-PersistentGuard '--disable' (Read-FallbackBindingJson $paths.Binding)}
  if($Mode -eq 'Uninstall'){
   if((Test-Path -LiteralPath $paths.Binding) -and -not (Test-FallbackDrained (Read-FallbackBindingJson $paths.Binding))){throw '已持久禁用；仍在交还，请待状态完成后再卸载'}
   if($task){Unregister-ScheduledTask -TaskName $paths.Task -Confirm:$false}
  }
  Write-Output '已持久禁用；正在接管时由原协议安全交还。'
  exit 0
 }
 # Migrate the former session task only after proving it has handed back.
 if(Test-Path -LiteralPath $paths.Legacy){
  $old=Read-FallbackBindingJson $paths.Legacy
  if($old.InstallRoot -ne [IO.Path]::GetFullPath($InstallRoot)){throw '旧绑定安装身份不匹配'}
  Invoke-PersistentGuard '--disable' $old
  if(-not (Test-FallbackDrained $old)){throw '旧会话已禁用，等待其安全交还后重试一次安装'}
  & (Join-Path $PSScriptRoot 'Manage-FallbackGuard.ps1') -Mode Uninstall -ProjectDirectory $old.ProjectDirectory -SessionId $old.SessionId
  [IO.File]::Delete($paths.Legacy)
 }
 if(-not $task){
  $account=[Security.Principal.WindowsIdentity]::GetCurrent().Name
  $action=New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -Argument ('-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}" -InstallRoot "{1}"' -f $watcher,$InstallRoot)
  $trigger=New-ScheduledTaskTrigger -AtLogOn -User $account
  $principal=New-ScheduledTaskPrincipal -UserId $account -LogonType Interactive -RunLevel Limited
  $settings=New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
  Register-ScheduledTask -TaskName $paths.Task -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description $description | Out-Null
 }
 Save-FallbackBinding $paths.Settings @{SchemaVersion=1;InstallRoot=[IO.Path]::GetFullPath($InstallRoot);Enabled=$true}
 Start-ScheduledTask -TaskName $paths.Task
 Write-Output '已启用常驻监控。无需输入项目或会话；主程序开始试验后自动绑定，登录后自动继续。'
} finally {if($held){$mutex.ReleaseMutex()};$mutex.Dispose()}
