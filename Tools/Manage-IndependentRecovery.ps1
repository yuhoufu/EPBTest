#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateSet('Install','Status','Enable','Maintenance','Uninstall')][string]$Mode,
    [Parameter(Mandatory=$true)][string]$RegistrationPath,
    [Parameter(Mandatory=$true)][string]$ExecutorPath
)
$ErrorActionPreference='Stop'
$user=[Security.Principal.WindowsIdentity]::GetCurrent()
try {
    $principal=New-Object Security.Principal.WindowsPrincipal($user)
    if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw '需要管理员权限。'}
} finally {$user.Dispose()}
$RegistrationPath=[IO.Path]::GetFullPath($RegistrationPath)
$ExecutorPath=[IO.Path]::GetFullPath($ExecutorPath)
if($RegistrationPath.StartsWith('\\') -or $ExecutorPath.StartsWith('\\')){throw '安装注册和执行器必须位于本机磁盘。'}
if($RegistrationPath.Contains('"') -or $ExecutorPath.Contains('"')){throw '路径含非法引号。'}
$assemblyPath=Join-Path ([IO.Path]::GetDirectoryName($ExecutorPath)) 'MTTFTest.Watchdog.Protocol.dll'
# The full bundle installer must protect this directory before this entry runs.
$assemblyAcl=Get-Acl -LiteralPath $assemblyPath
$trustedOwners=@('S-1-5-18','S-1-5-32-544','S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
$ownerSid=$assemblyAcl.GetOwner([Security.Principal.SecurityIdentifier]).Value
if($ownerSid -notin $trustedOwners){throw '协议程序集所有者不可信。'}
# Do not load code from an untrusted-writable file or ancestor.
$cursor=Get-Item -LiteralPath $assemblyPath
$depth=0
while($cursor){
    if(($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw '协议路径不允许重解析点。'}
    $acl=Get-Acl -LiteralPath $cursor.FullName
    if($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trustedOwners){throw '协议路径所有者不可信。'}
    $dangerous=[Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles
    if($depth -lt 2){$dangerous=$dangerous -bor [Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::Delete}
    foreach($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){
        if($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Value -notin $trustedOwners -and
           ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -eq 0 -and
           ([long]$rule.FileSystemRights -band [long]$dangerous) -ne 0){throw '协议路径可被非受信主体修改。'}
    }
    $cursor=if($cursor -is [IO.FileInfo]){$cursor.Directory}else{$cursor.Parent}
    $depth++
}
[Reflection.Assembly]::LoadFrom($assemblyPath) | Out-Null
[MTTFTest.Watchdog.Protocol.IndependentProtectedFiles]::RequireTrustedFile($ExecutorPath)
$registration=[MTTFTest.Watchdog.Protocol.IndependentExecutorRegistration]::LoadTrusted($RegistrationPath)
$store=New-Object MTTFTest.Watchdog.Protocol.IndependentProjectStateStore($registration.StateDirectory)
$serviceName='MTTFTestIndependent-'+$registration.InstallationId
$serviceCommand='"{0}" --independent-service --registration "{1}"' -f $ExecutorPath,$RegistrationPath
$taskName='Independent-'+$registration.InstallationId
$description='EPB Independent Installation '+$registration.InstallationId
$scheduler=$null;$folder=$null;$rootFolder=$null;$task=$null;$createdTask=$false;$createdService=$false
function Get-OwnedService {
    $found=Get-CimInstance Win32_Service -Filter ("Name='"+$serviceName+"'") -OperationTimeoutSec 10
    if($found -and ($found.PathName -ne $serviceCommand -or $found.StartName -notin @('LocalSystem','NT AUTHORITY\SYSTEM'))){throw '同名服务身份不匹配。'}
    return $found
}
function Set-Maintenance([bool]$value) {
    if($value){
        $mainName=[IO.Path]::GetFileName($registration.ExecutablePath).Replace("'","''")
        $candidates=@(Get-CimInstance Win32_Process -Filter ("Name='"+$mainName+"'") -OperationTimeoutSec 10)
        foreach($candidate in $candidates){
            if(-not $candidate.ExecutablePath -or $candidate.ExecutablePath -eq $registration.ExecutablePath){
                throw '旧控制程序尚未退出或其路径无法核验，拒绝安装维护。'
            }
        }
    }
    $state=$store.Read()
    $revision=if($state){$state.Revision}else{0L}
    $store.SetInstallationMaintenance($revision,$value)
}
try {
    $scheduler=New-Object -ComObject 'Schedule.Service'
    $scheduler.Connect()
    $rootFolder=$scheduler.GetFolder('\')
    [MTTFTest.Watchdog.Protocol.IndependentLaunchTaskDefinition]::ValidateSecurityDescriptor($rootFolder.GetSecurityDescriptor(7))
    try {$folder=$scheduler.GetFolder('\MTTFTest')} catch {
        if($_.Exception.HResult -ne -2147024894 -or $Mode -ne 'Install'){throw}
        $folder=$rootFolder.CreateFolder('MTTFTest','O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)')
    }
    [MTTFTest.Watchdog.Protocol.IndependentLaunchTaskDefinition]::ValidateSecurityDescriptor($folder.GetSecurityDescriptor(7))
    try {$task=$folder.GetTask($taskName)} catch {if($_.Exception.HResult -ne -2147024894){throw}}
    if($task){
        $definition=$task.Definition
        try {
            if($definition.RegistrationInfo.Description -ne $description -or $definition.Actions.Count -ne 1 -or
               $definition.Actions.Item(1).Path -ne $registration.ExecutablePath -or
               $definition.Actions.Item(1).Arguments -cne [MTTFTest.Watchdog.Protocol.IndependentLaunchTaskDefinition]::ExpectedArguments($RegistrationPath)){
                throw '同名启动任务身份不匹配。'
            }
            [MTTFTest.Watchdog.Protocol.IndependentLaunchTaskDefinition]::ValidateSecurityDescriptor($task.GetSecurityDescriptor(7))
            $taskPrincipal=$definition.Principal
            $taskSettings=$definition.Settings
            $taskAction=$definition.Actions.Item(1)
            try {
                $taskUser=[string]$taskPrincipal.UserId
                $taskSid=if($taskUser.StartsWith('S-')){$taskUser}else{(New-Object Security.Principal.NTAccount($taskUser)).Translate([Security.Principal.SecurityIdentifier]).Value}
                $actual=New-Object MTTFTest.Watchdog.Protocol.IndependentLaunchTaskDefinition
                $actual.Path=[string]$task.Path;$actual.Executable=[string]$taskAction.Path
                $actual.Arguments=[string]$taskAction.Arguments;$actual.WorkingDirectory=[string]$taskAction.WorkingDirectory
                $actual.UserSid=$taskSid;$actual.SecurityDescriptor=[string]$task.GetSecurityDescriptor(7)
                $actual.Enabled=[bool]$task.Enabled -and [bool]$taskSettings.Enabled
                $actual.AllowDemandStart=[bool]$taskSettings.AllowDemandStart;$actual.AllowHardTerminate=[bool]$taskSettings.AllowHardTerminate
                $actual.RequiresIdle=[bool]$taskSettings.RunOnlyIfIdle;$actual.RequiresNetwork=[bool]$taskSettings.RunOnlyIfNetworkAvailable
                $actual.DisallowBatteries=[bool]$taskSettings.DisallowStartIfOnBatteries;$actual.StopOnBatteries=[bool]$taskSettings.StopIfGoingOnBatteries
                $actual.ActionCount=[int]$definition.Actions.Count;$actual.ActionType=[int]$taskAction.Type
                $actual.TriggerCount=[int]$definition.Triggers.Count;$actual.RunLevel=[int]$taskPrincipal.RunLevel
                $actual.LogonType=[int]$taskPrincipal.LogonType;$actual.MultipleInstances=[int]$taskSettings.MultipleInstances
                $actual.RestartCount=[int]$taskSettings.RestartCount;$actual.ExecutionTimeLimit=[string]$taskSettings.ExecutionTimeLimit
                $actual.Validate($registration,$RegistrationPath)
            } finally {
                foreach($com in @($taskAction,$taskSettings,$taskPrincipal)){if($com){[Runtime.InteropServices.Marshal]::FinalReleaseComObject($com) | Out-Null}}
            }
        } finally {[Runtime.InteropServices.Marshal]::FinalReleaseComObject($definition) | Out-Null}
    }
    $service=Get-OwnedService
    switch($Mode){
        'Install' {
            if($service -or $task){throw '安装对象已存在；先检查，禁止猜测覆盖或迁移。'}
            Set-Maintenance $true
            $definition=$scheduler.NewTask(0)
            try {
                $definition.RegistrationInfo.Description=$description
                $definition.Principal.UserId=$registration.InteractiveUserSid
                $definition.Principal.LogonType=3
                $definition.Principal.RunLevel=1
                $settings=$definition.Settings
                $settings.Enabled=$true;$settings.AllowDemandStart=$true;$settings.AllowHardTerminate=$false
                $settings.RunOnlyIfIdle=$false;$settings.RunOnlyIfNetworkAvailable=$false
                $settings.DisallowStartIfOnBatteries=$false;$settings.StopIfGoingOnBatteries=$false
                $settings.MultipleInstances=2;$settings.RestartCount=0;$settings.ExecutionTimeLimit='PT0S'
                $action=$definition.Actions.Create(0)
                $action.Path=$registration.ExecutablePath
                $action.WorkingDirectory=[IO.Path]::GetDirectoryName($registration.ExecutablePath)
                $action.Arguments=[MTTFTest.Watchdog.Protocol.IndependentLaunchTaskDefinition]::ExpectedArguments($RegistrationPath)
                $task=$folder.RegisterTaskDefinition($taskName,$definition,2,$registration.InteractiveUserSid,$null,3,'O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)')
                $createdTask=$true
            } finally {
                foreach($com in @($action,$settings,$definition)){if($com){[Runtime.InteropServices.Marshal]::FinalReleaseComObject($com) | Out-Null}}
            }
            New-Service -Name $serviceName -BinaryPathName $serviceCommand -StartupType Automatic -Description $description | Out-Null
            $createdService=$true
            Write-Output '独立服务和交互任务已注册，保持维护模式；尚未允许恢复或启动试验。'
        }
        'Enable' {
            if(-not $service -or -not $task){throw '服务或交互任务缺失。'}
            Set-Maintenance $true
            try {
                Start-Service -Name $serviceName
                (Get-Service $serviceName).WaitForStatus('Running',[TimeSpan]::FromSeconds(10))
                Set-Maintenance $false
            } catch {Set-Maintenance $true;throw}
            Write-Output '独立执行服务已运行；试验是否恢复须另行验证动作、计数与落盘。'
        }
        'Maintenance' {Set-Maintenance $true;Write-Output '已进入维护模式，保留运行历史及停止意图。'}
        'Uninstall' {
            Set-Maintenance $true
            if($service){
                Stop-Service -Name $serviceName
                (Get-Service $serviceName).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(15))
            }
            if($task){$folder.DeleteTask($taskName,0)}
            if($service){& "$env:SystemRoot\System32\sc.exe" delete $serviceName; if($LASTEXITCODE -ne 0){throw '删除服务失败。'}}
            Write-Output '已卸载本安装的服务及启动任务；保留项目数据、持久状态与诊断。'
        }
        'Status' {
            $state=$store.Read()
            [pscustomobject]@{Installation=$registration.InstallationId;ServicePresent=[bool]$service;
                ServiceState=if($service){[string]$service.State}else{'Absent'};LaunchTaskPresent=[bool]$task;
                Maintenance=if($state){[bool]$state.Maintenance}else{$true};SafetyCleanupPending=if($state){[bool]$state.SafetyCleanupPending}else{$false}}
        }
    }
} catch {
    # Only roll back objects created by this invocation; preserve state/evidence.
    if($Mode -eq 'Install'){
        if($createdService){$owned=Get-OwnedService;if($owned){& "$env:SystemRoot\System32\sc.exe" delete $serviceName | Out-Null}}
        if($createdTask){$folder.DeleteTask($taskName,0)}
    }
    throw
} finally {
    foreach($com in @($task,$folder,$rootFolder,$scheduler)){if($com){[Runtime.InteropServices.Marshal]::FinalReleaseComObject($com) | Out-Null}}
}
