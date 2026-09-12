[CmdletBinding()]
param(
 [ValidateSet('Start','Enable','Disable','Status','Install','Uninstall','Run')][string]$Mode='Status',
 [string]$ProjectDirectory='',
 [string]$SessionId='',
 [string]$InstallRoot='C:\Program Files (x86)\MTTFTest'
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Resolve-FallbackBinding.ps1')
$manager=Join-Path $PSScriptRoot 'Manage-FallbackGuard.ps1'
$automatic=(-not $ProjectDirectory -and -not $SessionId)
if((-not $ProjectDirectory) -ne (-not $SessionId)){throw '项目目录和会话必须同时提供，或同时留空以自动识别。'}
$statePath=Get-FallbackBindingPath $InstallRoot
$previous=if($automatic -and [IO.File]::Exists($statePath)){Read-FallbackBindingJson $statePath}else{$null}
if($previous -and ($previous.SchemaVersion -ne 1 -or $previous.InstallRoot -ne [IO.Path]::GetFullPath($InstallRoot))){throw '保存的绑定结构或安装身份不匹配。'}
if($automatic){
 if($Mode -in @('Disable','Status','Uninstall','Run') -and $previous){$binding=$previous}
 else{$binding=Resolve-FallbackBinding $InstallRoot}
 $ProjectDirectory=[string]$binding.ProjectDirectory;$SessionId=[string]$binding.SessionId
}
$sessionValue=[Guid]::Empty
if(-not [Guid]::TryParseExact($SessionId,'N',[ref]$sessionValue) -or -not [IO.Directory]::Exists($ProjectDirectory)){throw '项目目录或会话无效。'}
$ProjectDirectory=[IO.Path]::GetFullPath($ProjectDirectory)
$arguments=@{ProjectDirectory=$ProjectDirectory;SessionId=$SessionId;BenchActive=$true}
if($automatic -and $Mode -in @('Start','Enable','Install')){
 if($previous -and ($previous.SessionId -ne $SessionId -or $previous.ProjectDirectory -ne $ProjectDirectory)){
  $ledgerPath=Join-Path $previous.ProjectDirectory ('fallback-'+$previous.SessionId+'.json')
  if(-not [IO.File]::Exists($ledgerPath)){throw '旧会话账本缺失，无法确认交还状态。'}
  if([IO.File]::Exists($ledgerPath)){
   $old=Read-FallbackBindingJson $ledgerPath
   if($old.SchemaVersion -ne 1 -or $old.SessionId -ne $previous.SessionId -or $old.Owner -ne 'Original' -or $old.Phase -ne 'Idle' -or $old.OutstandingLaunch){throw '旧会话尚未安全交还；保留原绑定，拒绝自动切换。'}
  }
  & $manager -Mode Uninstall -ProjectDirectory $previous.ProjectDirectory -SessionId $previous.SessionId
 }
 Save-FallbackBinding $statePath @{SchemaVersion=1;ProjectDirectory=$ProjectDirectory;SessionId=$SessionId;InstallRoot=[IO.Path]::GetFullPath($InstallRoot)}
}
Write-Output ('项目：'+$ProjectDirectory+'；会话：'+$SessionId)
if($Mode -eq 'Start'){
 & $manager -Mode Enable @arguments
 & $manager -Mode Run @arguments
}elseif($Mode -eq 'Install'){
 $name='MTTFTest-FallbackGuard-'+$SessionId
 $task=Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
 if($task){
  $description="EPB 独立兜底；项目=$ProjectDirectory；会话=$SessionId"
  $exe=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\FallbackGuard\MTTFTest.FallbackGuard.exe'))
  if($task.Description -ne $description -or @($task.Actions).Count -ne 1 -or -not ([string]$task.Actions.Arguments).Contains($exe)){throw '同名任务归属或程序路径不同；请先核对旧任务。'}
  & $manager -Mode Enable @arguments
  Start-ScheduledTask -TaskName $name
 }else{& $manager -Mode Install @arguments}
}else{& $manager -Mode $Mode @arguments}
