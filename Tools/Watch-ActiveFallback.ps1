param([string]$InstallRoot='C:\Program Files (x86)\MTTFTest')
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Resolve-FallbackBinding.ps1')
. (Join-Path $PSScriptRoot 'Persistent-Fallback.ps1')
function Invoke-FallbackMonitor([string]$InstallRoot) {
$paths=Get-PersistentFallbackPaths $InstallRoot
$mutex=New-Object Threading.Mutex($false,('Global\MTTF-FallbackMonitor-'+$paths.Token))
$held=$false
try {
 try {$held=$mutex.WaitOne(0)} catch [Threading.AbandonedMutexException] {$held=$true}
 if(-not $held){return}
 $binding=if(Test-Path -LiteralPath $paths.Binding){Read-FallbackBindingJson $paths.Binding}else{$null}
 # A restarted monitor first drains the durable previous binding. Never infer
 # successful handback from a missing launcher process or a lost launch reply.
 $draining=($null -ne $binding)
 while($true){
  $enabled=$false
  try {
   $settings=Read-FallbackBindingJson $paths.Settings
   if($settings.SchemaVersion -ne 1 -or $settings.InstallRoot -ne [IO.Path]::GetFullPath($InstallRoot)){throw '监控配置身份无效'}
   $enabled=($settings.Enabled -eq $true)
   $next=$null;$reason='等待主程序开始试验'
   if($enabled){try {$next=Resolve-ConfiguredFallbackBinding $InstallRoot} catch {$reason=$_.Exception.Message}}
   if($binding -and (-not $enabled -or -not $next -or $next.SessionId -ne $binding.SessionId -or $next.ProjectDirectory -ne $binding.ProjectDirectory)){$draining=$true}
   if($binding -and $draining){
    Invoke-PersistentGuard '--disable' $binding
    if(Test-FallbackDrained $binding){
     [IO.File]::Delete($paths.Binding);$binding=$null;$draining=$false
    }else{$reason='等待旧会话安全交还；禁止绑定新会话'}
   }
   if(-not $binding -and $enabled -and $next){
    $binding=$next
    # Persist the intent before enable/launch; a crash at any following step
    # is recovered through the drain path above.
    Save-FallbackBinding $paths.Binding $binding
    Invoke-PersistentGuard '--enable' $binding
    $exe=Get-PersistentGuardExecutable
    $child=Start-Process -FilePath $exe -ArgumentList ('--run --project-directory "{0}" --session-id {1}' -f $binding.ProjectDirectory,$binding.SessionId) -WindowStyle Hidden -PassThru
    $child.Dispose()
   }
   if($binding -and -not $draining){
    $reason='已自动绑定：'+$binding.ProjectDirectory+'；'+$binding.SessionId
    if(@(Get-BoundGuardProcesses $binding).Count -ne 1){$draining=$true;$reason='外部进程退出或身份冲突，先调和再重试'}
   }
   Save-FallbackBinding $paths.Status @{SchemaVersion=1;Enabled=$enabled;CapturedUtc=[DateTime]::UtcNow.ToString('O');Message=$reason;ProcessId=$PID;PrivateBytes=[Diagnostics.Process]::GetCurrentProcess().PrivateMemorySize64}
   if(-not $enabled -and -not $binding){return}
  } catch {
   # Do not launch after corrupt settings/configuration. An existing child is
   # disabled through its original protocol, never killed by this launcher.
   $draining=$true
   if($binding){try {Invoke-PersistentGuard '--disable' $binding} catch {}}
   Save-FallbackBinding $paths.Status @{SchemaVersion=1;Enabled=$false;CapturedUtc=[DateTime]::UtcNow.ToString('O');Message=('告警禁启：'+$_.Exception.Message);ProcessId=$PID}
  }
  Start-Sleep -Seconds 1
 }
} finally {if($held){$mutex.ReleaseMutex()};$mutex.Dispose()}

}
if($MyInvocation.InvocationName -ne '.'){Invoke-FallbackMonitor $InstallRoot}
