#requires -Version 5.1
[CmdletBinding()]
param([ValidateSet('Complete','Launch')][string]$Mode='Complete',
    [Parameter(Mandatory=$true)][string]$InstallRoot,
    [int]$ParentProcessId=0,[long]$ParentStartUtcTicks=0)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Independent-InstallSetup.ps1')
$root=[IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$lease=$null
try{
    $main=Join-Path $root 'Current\MTTFTest.exe'
    # The bootstrap must exit before registration seals a controller binding.
    if($ParentProcessId){
        $parent=Get-Process -Id $ParentProcessId -ErrorAction SilentlyContinue
        if($parent){try{
            if($parent.StartTime.ToUniversalTime().Ticks -ne $ParentStartUtcTicks -or
                -not [string]::Equals($parent.MainModule.FileName,$main,[StringComparison]::OrdinalIgnoreCase)){throw '项目设置父进程身份不一致。'}
            if(-not $parent.WaitForExit(15000)){throw '主程序尚未退出，未开始项目绑定。'}
        }finally{$parent.Dispose()}}
    }
    [Reflection.Assembly]::LoadFrom((Join-Path $root 'Current\MTTFTest.Watchdog.Protocol.dll'))|Out-Null
    [MTTFTest.Watchdog.Protocol.IndependentProtectedFiles]::RequireTrustedDirectory($root)
    [MTTFTest.Watchdog.Protocol.IndependentProtectedFiles]::RequireTrustedFile((Join-Path $root 'installed-files.json'))
    $receipt=Read-IndependentSetupJson (Join-Path $root 'installed-files.json')
    if($receipt.schemaVersion -ne 1 -or $receipt.installRoot -ne $root -or @($receipt.files).Count -gt 10000){throw '安装文件记录无效。'}
    foreach($entry in $receipt.files){
        $path=[IO.Path]::GetFullPath((Join-Path $root ([string]$entry.path)))
        if(-not $path.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase) -or
            (Get-FileHash -LiteralPath $path).Hash -ne $entry.sha256){throw '安装文件已变化，未执行项目绑定。'}
    }
    $lease=[IO.File]::Open((Join-Path $root 'install-setup.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $state=Read-IndependentSetupJson (Join-Path $root 'install-setup.json')
    if($state.schemaVersion -ne 1 -or $state.version -ne $receipt.version -or $state.stage -notin @('AwaitingProject','Binding','Ready')){throw '项目设置状态无效。'}
    if($Mode -eq 'Launch'){
        $user=[Security.Principal.WindowsIdentity]::GetCurrent()
        try{if($user.User.Value -ne $state.interactiveUserSid){throw '请使用安装时的试验账户启动程序；不能用其他管理员账户代替。'}}finally{$user.Dispose()}
    }
    $registration=Join-Path $root 'IndependentState\registration.json'
    if($state.stage -ne 'Ready'){
        [MTTFTest.Watchdog.Protocol.IndependentInstallationBinding]::RequireControllerAbsent($main)
        if(-not (Test-IndependentExistingProject ([string]$state.projectDirectory))){
            if($state.stage -eq 'Binding'){throw '绑定中的项目已变化，保留现场状态，未切换项目。'}
            if($Mode -eq 'Complete'){Write-Output '安装完成：等待首次创建或打开项目；恢复尚未绑定，未启动试验。';return}
            $ui=Start-Process -FilePath $main -ArgumentList '--independent-setup-project' -WorkingDirectory (Split-Path $main) -WindowStyle Normal -PassThru -Wait
            try{if($ui.ExitCode -ne 0){Write-Output '项目设置已取消或未完成；程序保持等待绑定。';return}}finally{$ui.Dispose()}
            $request=Read-IndependentSetupJson (Join-Path $root 'setup-project.json')
            if($request.interactiveUserSid -ne $state.interactiveUserSid -or -not (Test-IndependentExistingProject ([string]$request.projectDirectory))){throw '项目设置结果无效。'}
            $state.projectDirectory=[string]$request.projectDirectory
        }
        $manager=Join-Path $root 'Tools\Manage-IndependentRecovery.ps1'
        $executor=Join-Path $root 'FallbackGuard\MTTFTest.FallbackGuard.exe'
        Invoke-IndependentSetupStages $root $state {
            param($phase)
            switch($phase){
                'Recovery' {
                    if([IO.File]::Exists($registration)){
                        $registered=[MTTFTest.Watchdog.Protocol.IndependentExecutorRegistration]::LoadTrusted($registration)
                        if($registered.ProjectDirectory -ne $state.projectDirectory -or $registered.InteractiveUserSid -ne $state.interactiveUserSid){throw '已有注册与待绑定项目或账户不一致。'}
                        & $manager -Mode Repair -RegistrationPath $registration -ExecutorPath $executor
                        & $manager -Mode Enable -RegistrationPath $registration -ExecutorPath $executor
                    }else{
                        & $manager -Mode Provision -RegistrationPath $registration -ExecutorPath $executor -MainExecutablePath $main `
                            -ProjectDirectory $state.projectDirectory -InteractiveUserSid $state.interactiveUserSid
                    }
                }
                'SessionHost' { & (Join-Path $root 'Tools\Manage-SessionHost.ps1') -Mode Install -InstallRoot $root -InteractiveUserSid $state.interactiveUserSid }
                'Shortcut' { & $manager -Mode Shortcut -RegistrationPath $registration -ExecutorPath $executor }
                'Verify' { if(-not [MTTFTest.Watchdog.Protocol.IndependentInstallationBinding]::Resolve($main)){throw '恢复绑定校验失败。'} }
                'RemoveSetupShortcut' { Set-IndependentSetupShortcut $root $state.version $true }
            }
        }
        Write-Output '项目恢复绑定完成；未启动试验。'
    }
    $binding=[MTTFTest.Watchdog.Protocol.IndependentInstallationBinding]::Resolve($main)
    if(-not $binding){throw '安装尚未完成恢复绑定。'}
    if($Mode -eq 'Launch'){Start-Process -FilePath $main -WorkingDirectory (Split-Path $main) -WindowStyle Normal|Out-Null}
}catch{
    if($Mode -eq 'Launch'){
        Add-Type -AssemblyName System.Windows.Forms
        [Windows.Forms.MessageBox]::Show(('项目恢复绑定未完成：'+$_.Exception.Message),'EPB 项目设置')|Out-Null
    }
    throw
}finally{if($lease){$lease.Dispose()}}
