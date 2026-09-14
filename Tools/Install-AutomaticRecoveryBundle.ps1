[CmdletBinding()]
param(
    [ValidateSet('ValidatePackage','Install','Repair','Restore','Launch','Status','Stop','Evidence','Uninstall')]
    [string]$Mode = 'ValidatePackage',
    [string]$InstallRoot = '',
    [string]$ProjectDirectory = '',
    [string]$InteractiveUserSid = '',
    [string]$EvidenceDirectory = '',
    [switch]$ForceUninstall,
    [switch]$Elevated
)
$ErrorActionPreference = 'Stop'
if (-not $InstallRoot) { $InstallRoot = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'MTTFTest' }
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$base = Join-Path $PSScriptRoot 'Base'
$manifestPath = Join-Path $PSScriptRoot 'automatic-bundle.json'
function Test-Bundle {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $legacy = $manifest.schemaVersion -eq 1 -and $manifest.recoveryArchitecture -eq 'V2-Supervisor-SessionAgent-SafetyAgent'
    $independent = $manifest.schemaVersion -eq 2 -and $manifest.recoveryArchitecture -eq 'V4-Independent-SystemExecutor'
    if (-not $legacy -and -not $independent) { throw '不支持的候选包架构。' }
    foreach ($entry in $manifest.files) {
        $relative = [string]$entry.path
        if ([IO.Path]::IsPathRooted($relative) -or $relative -match '(^|[\\/])\.\.([\\/]|$)') { throw '包清单包含越界路径。' }
        $path = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $relative))
        if (-not $path.StartsWith($PSScriptRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '包清单路径越界。' }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "包文件缺失：$relative" }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256) { throw "包文件摘要不符：$relative" }
    }
    foreach ($required in @('Base/MTTFTest.exe','Base/Deployment/Install-MTTFTest-Unattended.ps1','Install-AutomaticRecoveryBundle.ps1','RecoveryGuard-Acceptance.ps1')) {
        if ($required -notin @($manifest.files.path)) { throw "包清单缺少必要文件：$required" }
    }
    if ($independent) {
        if ('Tools/Install-IndependentRecoveryBundle.ps1' -notin @($manifest.files.path)) { throw '缺少独立文件安装器。' }
        if ('Tools/Export-IndependentRecoveryEvidence.ps1' -notin @($manifest.files.path)) { throw '缺少独立关键证据采集器。' }
        & (Join-Path $PSScriptRoot 'Tools\Install-IndependentRecoveryBundle.ps1') -Mode Validate -BundleDirectory $PSScriptRoot -InstallRoot $InstallRoot | Out-Null
    }
    foreach ($file in Get-ChildItem -LiteralPath $base -File -Recurse) {
        $relative = $file.FullName.Substring($PSScriptRoot.Length).TrimStart('\').Replace('\','/')
        if ($relative -notin @($manifest.files.path)) { throw "Base 中存在未登记文件：$relative" }
    }
    return $manifest
}
function Get-ExactProcesses([string]$Name) {
    $expected = [IO.Path]::GetFullPath((Join-Path $InstallRoot ('Current\' + $Name)))
    foreach ($process in @(Get-CimInstance Win32_Process -Filter ("Name='" + $Name + "'") -ErrorAction Stop)) {
        if (-not $process.ExecutablePath) { throw "无法读取 $Name 进程路径，请使用管理员权限。" }
        if ([string]::Equals([IO.Path]::GetFullPath($process.ExecutablePath), $expected, [StringComparison]::OrdinalIgnoreCase)) { $process }
    }
}
function Get-State {
    $main = @(Get-ExactProcesses 'MTTFTest.exe')
    $observed = $null
    if ($ProjectDirectory) {
        $sessions = Join-Path $ProjectDirectory 'WatchdogSessions'
        foreach ($file in @(Get-ChildItem -LiteralPath $sessions -File -Filter 'session-*.json' -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match '^session-[0-9a-f]{32}\.json$' } | Sort-Object LastWriteTimeUtc -Descending)) {
            try { $journal = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8 | ConvertFrom-Json }
            catch { continue }
            $heartbeat = $journal.LastHeartbeat
            if (-not $heartbeat) { continue }
            $age = ([DateTime]::UtcNow.Ticks - [long]$journal.LastHeartbeatUtcTicks) / 10000000.0
            $exact = @($main | Where-Object {
                $_.ProcessId -eq $heartbeat.ProcessId -and
                [Math]::Abs($_.CreationDate.ToUniversalTime().Ticks - [long]$heartbeat.ProcessStartUtcTicks) -lt 10
            }).Count -eq 1
            $observed = [ordered]@{
                Source=$file.FullName; SessionId=$journal.SessionId; RunId=$heartbeat.RunId; RunEpoch=$heartbeat.RunEpoch
                HeartbeatAgeSeconds=$age; ExactLiveProcess=$exact
                Freshness= $(if ($exact -and $age -ge 0 -and $age -le 120) {'RECENT_OBSERVATION'} else {'STALE_OR_OTHER_PROCESS'})
                Phase=$heartbeat.Phase; EnabledChannels=@($heartbeat.EnabledChannels)
                AlarmedChannels=@($heartbeat.AlarmedChannels); CompletedChannels=@($heartbeat.CompletedChannels)
                RecoveryActive=$heartbeat.RecoveryActive; RecoveryStage=$heartbeat.RecoveryStage
                RecoveryBlocked=$journal.RecoveryBlocked; RecoveryFailureCode=$journal.RecoveryFailureCode
                ManualPauseActive=$heartbeat.ManualPauseActive; OutputsConfirmedOff=$heartbeat.OutputsConfirmedOff
            }
            break
        }
    }
    [pscustomobject]@{
        Computer = $env:COMPUTERNAME; TimeUtc = [DateTime]::UtcNow.ToString('O')
        InstallRoot = $InstallRoot; MainProcessIds = @($main.ProcessId)
        Supervisor = [string](Get-Service MTTFTestSupervisor -ErrorAction SilentlyContinue).Status
        BusinessRecovery = 'UNVERIFIED: 必须核验每路动作、计数和连续正式提交；进程存在不代表恢复'
        ProjectDirectory = $ProjectDirectory
        LastObservedRecovery = $observed
    }
}
function Save-BusinessEvidence([string]$Destination, [string]$Snapshot = '') {
    if (-not $ProjectDirectory) { throw '请通过 -ProjectDirectory 指定实际项目目录，不能猜测或读取其他项目计数。' }
    $hostPath = Join-Path $env:SystemRoot 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe'
    if (-not (Test-Path -LiteralPath $hostPath)) { $hostPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe' }
    $arguments = @('-NoProfile','-ExecutionPolicy','Bypass','-File',
        (Join-Path $base 'Deployment\Get-EpbBusinessEvidence.ps1'),
        '-ProgramDirectory',$base,'-ProjectDirectory',$ProjectDirectory,'-OutputPath',$Destination)
    if ($Snapshot) { $arguments += @('-SnapshotPath',$Snapshot) }
    & $hostPath @arguments
    if ($LASTEXITCODE -ne 0) { throw '业务计数/数据库采证失败，不能报告完整采证成功。' }
}
function Assert-ClosedProcessSafety([int]$ProcessId, [long]$StartUtcTicks) {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $base 'MTTFTest.Watchdog.Protocol.dll'))
    $control = Join-Path $env:LOCALAPPDATA 'MTTFTest\WatchdogControlV2'
    foreach ($path in @(Get-ChildItem -LiteralPath $control -Filter '*.application-exit.json' -File -ErrorAction SilentlyContinue)) {
        $session = $path.Name -replace '^session-','' -replace '\.application-exit\.json$',''
        $receipt = New-Object MTTFTest.Watchdog.Protocol.WatchdogApplicationExitReceipt
        if ([MTTFTest.Watchdog.Protocol.WatchdogApplicationExitReceiptStore]::TryRead($null,$session,[ref]$receipt) -and
            $receipt.MainProcessId -eq $ProcessId -and $receipt.MainProcessStartUtcTicks -eq $StartUtcTicks -and
            $receipt.State -eq [MTTFTest.Watchdog.Protocol.WatchdogApplicationExitState]::GracefulCompleted -and
            $receipt.MotorsOff -and $receipt.PowerOff -and $receipt.PressureSafe -and
            $receipt.PersistenceDrained -and $receipt.LogicalQuiescent -and
            $receipt.RelaunchDisposition -eq [MTTFTest.Watchdog.Protocol.WatchdogRelaunchDisposition]::Forbidden) { return }
    }
    throw '未找到与刚退出进程 PID/启动时间完全匹配的安全关闭回执；后台保护保留，不能把进程退出当作安全完成。'
}
try {
    $manifest = Test-Bundle
    Write-Host ('候选版本：' + $manifest.version + '；现场耐久验收未完成。')
    if ($Mode -eq 'ValidatePackage') { Write-Output ('PASS BundleIntegrity ' + @($manifest.files).Count); exit 0 }
    if ($Mode -in @('Install','Repair','Restore','Launch','Stop','Uninstall','Evidence')) {
        $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
        if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            if ($Elevated) { throw '提权后仍无管理员权限。' }
            # Arguments are data, quoted for the Windows command line; reject quote injection.
            foreach ($argument in @($PSScriptRoot,$InstallRoot,$ProjectDirectory,$InteractiveUserSid,$EvidenceDirectory)) { if ($argument.Contains('"')) { throw '参数不能含引号。' } }
            $arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '" -Mode ' + $Mode + ' -InstallRoot "' + $InstallRoot + '" -Elevated'
            if ($ProjectDirectory) { $arguments += ' -ProjectDirectory "' + $ProjectDirectory + '"' }
            if ($InteractiveUserSid) { $arguments += ' -InteractiveUserSid "' + $InteractiveUserSid + '"' }
            if ($EvidenceDirectory) { $arguments += ' -EvidenceDirectory "' + $EvidenceDirectory + '"' }
            if ($ForceUninstall) { $arguments += ' -ForceUninstall' }
            $child = Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -ArgumentList $arguments -Verb RunAs -Wait -PassThru
            exit $child.ExitCode
        }
    }
    if ($manifest.recoveryArchitecture -eq 'V4-Independent-SystemExecutor') {
        if ($Mode -eq 'Install') {
            & (Join-Path $PSScriptRoot 'Tools\Install-IndependentRecoveryBundle.ps1') -Mode Install `
                -BundleDirectory $PSScriptRoot -InstallRoot $InstallRoot -ProjectDirectory $ProjectDirectory -InteractiveUserSid $InteractiveUserSid
        } elseif ($Mode -in @('Repair','Uninstall')) {
            & (Join-Path $PSScriptRoot 'Tools\Install-IndependentRecoveryBundle.ps1') -Mode $Mode -BundleDirectory $PSScriptRoot -InstallRoot $InstallRoot
        } elseif ($Mode -in @('Status','Restore','Stop')) {
            $manager = Join-Path $PSScriptRoot 'Tools\Manage-IndependentRecovery.ps1'
            $managerMode = if ($Mode -eq 'Restore') { 'Enable' } else { $Mode }
            & $manager -Mode $managerMode -RegistrationPath (Join-Path $InstallRoot 'IndependentState\registration.json') `
                -ExecutorPath (Join-Path $InstallRoot 'FallbackGuard\MTTFTest.FallbackGuard.exe')
            Write-Host '组件状态不代表续测成功；动作、计数和三周期落盘须单独核验。'
        } elseif ($Mode -eq 'Evidence') {
            if (-not $EvidenceDirectory) { $EvidenceDirectory = Join-Path $PSScriptRoot ('Evidence\' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')) }
            & (Join-Path $PSScriptRoot 'Tools\Export-IndependentRecoveryEvidence.ps1') `
                -RegistrationPath (Join-Path $InstallRoot 'IndependentState\registration.json') `
                -ExecutorPath (Join-Path $InstallRoot 'FallbackGuard\MTTFTest.FallbackGuard.exe') -OutputDirectory $EvidenceDirectory
        } elseif ($Mode -eq 'Launch') {
            $registrationPath = Join-Path $InstallRoot 'IndependentState\registration.json'
            & (Join-Path $PSScriptRoot 'Tools\Manage-IndependentRecovery.ps1') -Mode Status -RegistrationPath $registrationPath `
                -ExecutorPath (Join-Path $InstallRoot 'FallbackGuard\MTTFTest.FallbackGuard.exe') | Out-Null
            $registration = [MTTFTest.Watchdog.Protocol.IndependentExecutorRegistration]::LoadTrusted($registrationPath)
            if (-not [string]::Equals($registration.ExecutablePath,(Join-Path $InstallRoot 'Current\MTTFTest.exe'),[StringComparison]::OrdinalIgnoreCase)) { throw '注册主程序路径与安装目录不一致。' }
            if (@(Get-ExactProcesses 'MTTFTest.exe').Count -gt 0) { Write-Host '本安装主程序已运行。' }
            else {
                $current = Join-Path $InstallRoot 'Current'
                Start-Process -FilePath (Join-Path $current 'MTTFTest.exe') -WorkingDirectory $current | Out-Null
                Write-Host '已请求打开本安装主程序；未声明试验已启动。'
            }
        } else { throw ('独立架构尚未实现此入口，未调用旧架构脚本：' + $Mode) }
        exit 0
    }
    $installer = Join-Path $base 'Deployment\Install-MTTFTest-Unattended.ps1'
    switch ($Mode) {
        'Install' { & $installer -Mode Install -SourceDirectory $base -InstallRoot $InstallRoot }
        'Repair' { & $installer -Mode Repair -SourceDirectory $base -InstallRoot $InstallRoot }
        'Restore' { & $installer -Mode Configure -SourceDirectory $base -InstallRoot $InstallRoot }
        'Uninstall' { & $installer -Mode Uninstall -SourceDirectory $base -InstallRoot $InstallRoot -ForceUninstall:$ForceUninstall }
        'Launch' {
            if (@(Get-ExactProcesses 'MTTFTest.exe').Count -gt 0) { Write-Host '本安装主程序已运行，请在现有窗口操作。'; break }
            if (@(Get-ExactProcesses 'MTTFTest.SafetyAgent.exe').Count -gt 0) { throw '安全接管尚未完成，拒绝手动拉起。' }
            $current = Join-Path $InstallRoot 'Current'
            Start-Process -FilePath (Join-Path $current 'MTTFTest.exe') -WorkingDirectory $current | Out-Null
            Write-Host '已请求打开程序；试验启动仍由程序统一安全准入。尚未验证续测。'
        }
        'Status' {
            Get-State | ConvertTo-Json -Depth 5
            if ($ProjectDirectory) {
                $statusDirectory = Join-Path $PSScriptRoot 'Evidence'
                [void](New-Item -ItemType Directory -Path $statusDirectory -Force)
                $statusPath = Join-Path $statusDirectory ('business-' + [Guid]::NewGuid().ToString('N') + '.json')
                Save-BusinessEvidence $statusPath
                Get-Content -LiteralPath $statusPath -Raw
            }
        }
        'Stop' {
            $pendingPath = Join-Path $InstallRoot 'stop-command-pending.json'
            if (Test-Path -LiteralPath $pendingPath) {
                $pending = Get-Content -LiteralPath $pendingPath -Raw | ConvertFrom-Json
                $sameProcess = Get-Process -Id ([int]$pending.processId) -ErrorAction SilentlyContinue
                if (-not $sameProcess -or $sameProcess.StartTime.ToUniversalTime().Ticks -ne [long]$pending.startUtcTicks) {
                    Assert-ClosedProcessSafety ([int]$pending.processId) ([long]$pending.startUtcTicks)
                    Remove-Item -LiteralPath $pendingPath
                }
            }
            $active = @(Get-ExactProcesses 'MTTFTest.exe')
            foreach ($item in $active) {
                $process = Get-Process -Id $item.ProcessId -ErrorAction Stop
                # CIM CreationDate has microsecond precision; GetProcessTimes keeps 100ns ticks.
                if ([Math]::Abs($process.StartTime.ToUniversalTime().Ticks - $item.CreationDate.ToUniversalTime().Ticks) -ge 10) { throw '进程身份已变化，停止操作取消。' }
                $started = $process.StartTime.ToUniversalTime().Ticks
                [ordered]@{processId=$process.Id;startUtcTicks=$started} | ConvertTo-Json | Set-Content -LiteralPath $pendingPath -Encoding UTF8
                if (-not $process.CloseMainWindow()) { throw '无法投递正常关闭请求，请在程序界面安全停止；不会强杀。' }
                if (-not $process.WaitForExit(60000)) { throw '正常关闭尚未完成，请检查安全和落盘状态；不会强杀。' }
                Assert-ClosedProcessSafety $item.ProcessId $started
                Remove-Item -LiteralPath $pendingPath
            }
            if (@(Get-ExactProcesses 'MTTFTest.SafetyAgent.exe').Count -gt 0) { throw '安全代理仍活动，不能停止后台保护。' }
            foreach ($taskName in @('MTTFTestAutoStart','MTTFTestSessionAgent')) {
                if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
                    Disable-ScheduledTask -TaskName $taskName | Out-Null
                    Stop-ScheduledTask -TaskName $taskName
                }
            }
            if (Get-Service MTTFTestSupervisor -ErrorAction SilentlyContinue) { Stop-Service MTTFTestSupervisor -ErrorAction Stop }
            Write-Host '后台组件已停止；恢复请使用“恢复后台服务”。未更改试验计数或恢复许可。'
        }
        'Evidence' {
            if (-not $ProjectDirectory) { $ProjectDirectory = Read-Host '输入实际试验项目目录（包含 index.db）' }
            if (-not $ProjectDirectory) { throw '未指定项目，未进行采证。' }
            if (-not $EvidenceDirectory) { $EvidenceDirectory = Join-Path $PSScriptRoot ('Evidence\' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')) }
            if (Test-Path -LiteralPath $EvidenceDirectory) { throw '采证输出已存在，请指定新目录。' }
            [void](New-Item -ItemType Directory -Path $EvidenceDirectory)
            Get-State | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'status.json') -Encoding UTF8
            Save-BusinessEvidence (Join-Path $EvidenceDirectory 'business.json') (Join-Path $EvidenceDirectory 'index.db')
            $sources = @((Join-Path $env:LOCALAPPDATA 'MTTFTest\WatchdogControlV2'), (Join-Path $env:ProgramData 'MTTFTest'), [IO.Path]::GetFullPath($ProjectDirectory).TrimEnd('\'))
            $index = 0
            foreach ($source in $sources) {
                $index++
                if (Test-Path -LiteralPath $source) {
                    $destination = Join-Path $EvidenceDirectory ('state-' + $index)
                    [void](New-Item -ItemType Directory -Path $destination)
                    Get-ChildItem -LiteralPath $source -File -Recurse | Where-Object { $_.Extension -in @('.json','.log','.txt','.xml') -and $_.Length -lt 20MB } | ForEach-Object {
                        $relative = $_.FullName.Substring($source.Length).TrimStart('\')
                        $target = Join-Path $destination $relative
                        [void](New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force)
                        Copy-Item -LiteralPath $_.FullName -Destination $target
                    }
                }
            }
            'index.db 使用 SQLite 在线一致性快照并通过 integrity_check；活动日志不是跨文件原子快照；未包含全部波形，不能作为完整增量链备份。' | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'scope.txt') -Encoding UTF8
            Get-ChildItem -LiteralPath $EvidenceDirectory -File -Recurse | ForEach-Object {
                [pscustomobject]@{path=$_.FullName.Substring($EvidenceDirectory.Length).TrimStart('\'); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
            } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'sha256.json') -Encoding UTF8
            Write-Host "日志、状态及一致性数据库快照完成：$EvidenceDirectory；实际动作仍须单独核验。"
        }
    }
    exit 0
}
catch { Write-Error -ErrorAction Continue $_; exit 1 }
