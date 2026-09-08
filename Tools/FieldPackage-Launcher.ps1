#requires -Version 5.1
[CmdletBinding()]
param([ValidateSet('Install','Repair','Launch','Status','Restore','Evidence','Stop','Uninstall','Validate')][string]$Action='Status')
$ErrorActionPreference='Stop'
$bundle=$PSScriptRoot
$root=Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'MTTFTest'
if ([string]::IsNullOrWhiteSpace([Environment]::GetFolderPath('ProgramFilesX86'))) { $root=Join-Path $env:ProgramFiles 'MTTFTest' }
$main=Join-Path $bundle 'Package'
$guard=Join-Path $bundle 'Guard'
$stage='初始化'
try {
    $principal=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if ($Action -ne 'Validate' -and -not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        $scriptPath=$PSCommandPath.Replace("'","''")
        $command="& '$scriptPath' -Action '$Action'; exit `$LASTEXITCODE"
        $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
        $child=Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -EncodedCommand $encoded" -Wait -PassThru
        exit $child.ExitCode
    }
    $stage='校验完整包'
    & (Join-Path $bundle 'Verify-FieldPackage.ps1')
    if ($Action -eq 'Validate') { exit 0 }
    $logRoot=Join-Path $env:ProgramData 'MTTFTest\DeploymentLogs'
    [void](New-Item -ItemType Directory $logRoot -Force)
    $log=Join-Path $logRoot ('QuickDeploy-'+$Action+'-'+(Get-Date -Format 'yyyyMMdd-HHmmssfff')+'.log')
    Start-Transcript -LiteralPath $log | Out-Null
    $installer=Join-Path $main 'Deployment\Install-MTTFTest-Unattended.ps1'
    $stopper=Join-Path $main 'Deployment\Stop-RelatedProcesses.ps1'
    function Invoke-Step([string]$Name,[string]$Script,[hashtable]$Parameters) {
        $script:stage=$Name
        Write-Host "正在执行：$Name"
        $global:LASTEXITCODE=0
        & $Script @Parameters
        if ($LASTEXITCODE -ne 0) {throw "步骤返回失败退出码：$LASTEXITCODE"}
    }
    switch ($Action) {
        {$_ -in @('Install','Repair')} {
            # The original installers retain their maintenance, identity and safety gates.
            Invoke-Step '安装主程序与监督组件' $installer @{Mode=$Action;SourceDirectory=$main;InstallRoot=$root}
            $stage='读取台架注册身份'
            $raw=& (Join-Path $guard 'MTTFTest.RecoveryGuard.exe') --status
            if ($LASTEXITCODE -ne 0) { throw '无法读取独立恢复注册状态；主程序步骤已完成，Guard未安装。' }
            $state=($raw -join "`n") | ConvertFrom-Json
            $bench=if ([string]::IsNullOrWhiteSpace([string]$state.BenchId)) {$env:COMPUTERNAME} else {[string]$state.BenchId}
            Write-Host "Guard台架标识：$bench；模式：ObserveOnly"
            Invoke-Step '安装独立观察Guard' (Join-Path $guard 'Install-MTTFTest-RecoveryGuard.ps1') @{Mode='Install';SourceDirectory=$guard;BenchId=$bench;MainExecutable=(Join-Path $root 'Current\MTTFTest.exe');RecoveryMode='ObserveOnly'}
            if ((Get-ScheduledTask 'MTTFTestRecoveryGuardExecution').State -ne 'Disabled') { throw 'Guard执行任务没有禁用。' }
            Write-Host '安装完成。请使用“启动试验.cmd”；安装不会自动开始试验。'
        }
        'Launch' {
            $stage='请求监督启动'
            $launcher=Join-Path $root 'Current\MTTFTest.Watchdog.exe'
            if (-not (Test-Path $launcher)) {throw '尚未安装，请先运行一键安装正式版.cmd。'}
            & $launcher --launch-main --main-executable (Join-Path $root 'Current\MTTFTest.exe')
            if ($LASTEXITCODE -ne 0) {throw "监督启动请求失败：$LASTEXITCODE"}
            Write-Host '监督启动请求已提交；请核对主窗口与运行状态，尚不代表试验已经运行。'
        }
        'Status' {
            $stage='读取运行状态'
            $exe=Join-Path $root 'Current\MTTFTest.exe'
            if(Test-Path $exe){Write-Host ('安装版本：'+(Get-Item $exe).VersionInfo.FileVersion)}
            Get-Service MTTFTestSupervisor -ErrorAction SilentlyContinue | Format-Table Name,Status
            Get-ScheduledTask | Where-Object TaskName -match '^MTTFTest' | Format-Table TaskName,State
            Get-CimInstance Win32_Process -Filter "Name LIKE 'MTTFTest%'" | Format-Table Name,ProcessId,ExecutablePath
            Write-Host '进程和任务状态不等于采样、控制或数据保存健康。'
        }
        'Restore' {Invoke-Step '恢复监督组件' $stopper @{Mode='Restore';InstallRoot=$root}}
        'Stop' {Invoke-Step '安全停止相关组件' $stopper @{Mode='Stop';InstallRoot=$root}}
        'Evidence' {Invoke-Step '导出故障证据' (Join-Path $main 'Deployment\Export-StabilityEvidence.ps1') @{InstallRoot=$root}}
        'Uninstall' {
            # Never remove the Guard before confirming the business has stopped.
            if (@(Get-CimInstance Win32_Process -Filter "Name='MTTFTest.exe'").Count) {throw '请正常停止并退出主程序后再卸载。'}
            Invoke-Step '卸载独立Guard（保留事务和停止记录）' (Join-Path $guard 'Install-MTTFTest-RecoveryGuard.ps1') @{Mode='Uninstall'}
            Invoke-Step '卸载主程序（保留数据）' $installer @{Mode='Uninstall';InstallRoot=$root;ForceUninstall=$true}
        }
    }
    Write-Host "操作完成：$Action。日志：$log"
    exit 0
} catch {
    Write-Host "操作失败，步骤：$stage" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host '已完成的步骤不会被当作整体成功；保留数据与日志，排除原因后再修复。'
    exit 1
} finally {try {Stop-Transcript | Out-Null} catch {}}
