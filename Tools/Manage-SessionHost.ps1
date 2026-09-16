#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('ValidateOwnership','Install','Stop','Uninstall','Status')][string]$Mode='Status',
    [Parameter(Mandatory=$true)][string]$InstallRoot,
    [string]$InteractiveUserSid
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
. (Join-Path $PSScriptRoot 'Service-Lifecycle.ps1')
$root=[IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
if($root.StartsWith('\\') -or $root.Contains('"') -or $root -eq [IO.Path]::GetPathRoot($root).TrimEnd('\')){throw '监督组件安装目录无效。'}
$main=Join-Path $root 'Current\MTTFTest.exe'
$watchdog=Join-Path $root 'Current\MTTFTest.Watchdog.exe'
$agent=Join-Path $root 'Current\MTTFTest.SessionAgent.exe'
$serviceName='MTTFTestSupervisor'
$taskName='MTTFTestSessionAgent'
$service=Get-CimInstance Win32_Service -Filter "Name='MTTFTestSupervisor'"
$task=Get-ScheduledTask -TaskPath '\' -TaskName $taskName -ErrorAction SilentlyContinue
$auto=Get-ScheduledTask -TaskPath '\' -TaskName 'MTTFTestAutoStart' -ErrorAction SilentlyContinue
function Assert-TaskOwner($Task,[string]$Expected){
    if($Task -and (@($Task.Actions).Count -ne 1 -or
        -not [string]::Equals([string]$Task.Actions[0].Execute,$Expected,[StringComparison]::OrdinalIgnoreCase))){
        throw '登录任务属于其他安装或动作身份不明，拒绝修改。'
    }
}
if($service -and (-not [string]::Equals(([string]$service.PathName).Trim('"'),$watchdog,[StringComparison]::OrdinalIgnoreCase) -or
    $service.StartName -notin @('LocalSystem','NT AUTHORITY\SYSTEM'))){throw 'Supervisor 属于其他安装或权限身份不符，拒绝修改。'}
Assert-TaskOwner $task $agent
Assert-TaskOwner $auto $main
if($Mode -eq 'ValidateOwnership'){Write-Output 'PASS SessionHostOwnership';return}
if($Mode -eq 'Status'){
    [pscustomobject]@{SupervisorPresent=[bool]$service;SupervisorRunning=($service -and $service.State -eq 'Running');
        SessionAgentTaskPresent=[bool]$task;SessionAgentTaskEnabled=($task -and $task.Settings.Enabled);
        SupervisorPath=$watchdog;SessionAgentPath=$agent;TrialVerified=$false}
    return
}
$principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw '监督组件维护需要管理员权限。'}
foreach($record in @(Get-CimInstance Win32_Process -Filter "Name='MTTFTest.exe' OR Name='MTTFTest.SafetyAgent.exe' OR Name='MTTFTest.Watchdog.exe'")){
    $path=[string]$record.ExecutablePath
    if($path -and $path.StartsWith((Join-Path $root 'Current')+'\',[StringComparison]::OrdinalIgnoreCase) -and
        (-not $service -or $record.ProcessId -ne $service.ProcessId)){
        throw '本安装控制程序或会话辅助进程仍在运行，拒绝维护监督组件。'
    }
}
if($Mode -eq 'Install'){
    if($InteractiveUserSid -notmatch '^S-1-5-21-\d+-\d+-\d+-\d+$'){throw '必须指定有效交互用户 SID。'}
    foreach($path in @($main,$watchdog,$agent)){if(-not [IO.File]::Exists($path)){throw ('监督组件文件缺失：'+$path)}}
}
# Maintenance does not rewrite project/global configuration or create a main
# auto-start task. Stop only this installation's background host and agent.
if($auto){Disable-ScheduledTask -TaskPath '\' -TaskName 'MTTFTestAutoStart'|Out-Null;Stop-ScheduledTask -TaskPath '\' -TaskName 'MTTFTestAutoStart'}
if($task){Disable-ScheduledTask -TaskPath '\' -TaskName $taskName|Out-Null;Stop-ScheduledTask -TaskPath '\' -TaskName $taskName}
foreach($record in @(Get-CimInstance Win32_Process -Filter "Name='MTTFTest.SessionAgent.exe'")){
    if(-not [string]::Equals([string]$record.ExecutablePath,$agent,[StringComparison]::OrdinalIgnoreCase)){continue}
    $process=Get-Process -Id $record.ProcessId -ErrorAction SilentlyContinue
    if($process){try{
        if([Math]::Abs($process.StartTime.ToUniversalTime().Ticks-$record.CreationDate.ToUniversalTime().Ticks) -lt 10 -and
            [string]::Equals($process.MainModule.FileName,$agent,[StringComparison]::OrdinalIgnoreCase)){
            $process.Kill();if(-not $process.WaitForExit(5000)){throw '登录代理尚未退出。'}
        }
    }finally{$process.Dispose()}}
}
# Do not proceed to replace binaries if an agent changed identity during cleanup.
foreach($remaining in @(Get-CimInstance Win32_Process -Filter "Name='MTTFTest.SessionAgent.exe'")){
    if([string]::Equals([string]$remaining.ExecutablePath,$agent,[StringComparison]::OrdinalIgnoreCase)){
        throw '本安装登录代理仍存活，拒绝替换文件。'
    }
}
if($service -and $service.State -ne 'Stopped'){
    $controller=Get-Service $serviceName
    try{Set-ServiceControllerStateBounded -Controller $controller -Target Stopped}finally{$controller.Dispose()}
}
if($Mode -eq 'Stop'){Write-Output '本安装 Supervisor/SessionAgent 已停止，未启动试验。';return}
if($Mode -eq 'Uninstall'){
    if($task){Unregister-ScheduledTask -TaskPath '\' -TaskName $taskName -Confirm:$false}
    if($auto){Stop-ScheduledTask -TaskPath '\' -TaskName 'MTTFTestAutoStart';Unregister-ScheduledTask -TaskPath '\' -TaskName 'MTTFTestAutoStart' -Confirm:$false}
    if($service){& sc.exe delete $serviceName|Out-Null;if($LASTEXITCODE -ne 0){throw 'Supervisor 删除失败。'}}
    if(Get-CimInstance Win32_Service -Filter "Name='MTTFTestSupervisor'"){throw 'Supervisor 删除尚未完成。'}
    Write-Output '本安装 Supervisor/SessionAgent 注册已移除。';return
}
if(-not $service){New-Service -Name $serviceName -BinaryPathName ('"'+$watchdog+'"') -StartupType Automatic -DisplayName 'MTTFTest Unattended Supervisor'|Out-Null}
$service=Get-CimInstance Win32_Service -Filter "Name='MTTFTestSupervisor'"
$changed=Invoke-CimMethod -InputObject $service -MethodName Change -Arguments @{PathName=('"'+$watchdog+'"');StartMode='Automatic'}
if($changed.ReturnValue -ne 0){throw 'Supervisor 路径配置失败。'}
& sc.exe config $serviceName 'start=' 'delayed-auto'|Out-Null
if($LASTEXITCODE -ne 0){throw 'Supervisor 启动策略配置失败。'}
& sc.exe failure $serviceName 'reset=' '0' 'actions=' 'restart/5000/restart/15000/restart/60000'|Out-Null
if($LASTEXITCODE -ne 0){throw 'Supervisor 故障重启策略配置失败。'}
& sc.exe failureflag $serviceName 1|Out-Null
if($LASTEXITCODE -ne 0){throw 'Supervisor 故障恢复标志配置失败。'}
$action=New-ScheduledTaskAction -Execute $agent -WorkingDirectory (Split-Path -Parent $agent)
$trigger=New-ScheduledTaskTrigger -AtLogOn -User $InteractiveUserSid
$taskPrincipal=New-ScheduledTaskPrincipal -UserId $InteractiveUserSid -LogonType Interactive -RunLevel Highest
$settings=New-ScheduledTaskSettingsSet -StartWhenAvailable -RestartCount 255 -RestartInterval ([TimeSpan]::FromMinutes(1)) -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskPath '\' -TaskName $taskName -Action $action -Trigger $trigger -Principal $taskPrincipal -Settings $settings -Force|Out-Null
$controller=Get-Service $serviceName
try{Set-ServiceControllerStateBounded -Controller $controller -Target Running}finally{$controller.Dispose()}
Start-ScheduledTask -TaskPath '\' -TaskName $taskName
Write-Output '本安装 Supervisor 与交互登录代理已配置；未启动试验。'
