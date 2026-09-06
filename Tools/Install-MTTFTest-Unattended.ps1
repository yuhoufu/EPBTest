[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet('Install', 'Repair', 'Configure', 'Uninstall', 'PromoteLastKnownGood')]
    [string]$Mode = 'Install',
    [string]$SourceDirectory = (Split-Path -Parent $PSScriptRoot),
    [string]$InstallRoot = '',
    [string]$SoakEvidencePath,
    [switch]$ForceUninstall,
    [switch]$PhysicalIsolationConfirmed
)

$ErrorActionPreference = 'Stop'
$serviceName = 'MTTFTestSupervisor'
$taskName = 'MTTFTestSessionAgent'
$autoStartTaskName = 'MTTFTestAutoStart'
$healthTaskName = 'MTTFTestRecoveryHealth'
$shortcutName = 'MT EPB 试验系统 V2.17.lnk'
$configuredMarkerName = 'MTTFTest.FirstRun.configured'
$runtimeConfigNames = @(
    'AIConfig.xml', 'AlarmConfig.xml', 'AOConfig.xml', 'DOConfig.xml',
    'PowerSupplyConfig.xml', 'TestConfig.xml', 'UnattendedAlarmConfig.xml',
    'UIConfig.xml')

if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    $productProgramFiles = [Environment]::GetFolderPath('ProgramFilesX86')
    if ([string]::IsNullOrWhiteSpace($productProgramFiles)) {
        $productProgramFiles = [Environment]::GetFolderPath('ProgramFiles')
    }
    $InstallRoot = Join-Path $productProgramFiles 'MTTFTest'
}

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw '必须以管理员身份运行无人值守安装脚本。'
    }
}

function Resolve-SafeDirectory([string]$Path, [string]$Label) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw "$Label 为空。" }
    $resolved = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $root = [IO.Path]::GetPathRoot($resolved).TrimEnd('\', '/')
    if ([string]::Equals($resolved, $root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label 不得为磁盘根目录：$resolved"
    }
    return $resolved
}

function Read-Utf8JsonFile([string]$Path, [string]$Label) {
    try {
        # V2.14.x 使用无 BOM UTF-8 原子写入检查点。Windows PowerShell 5.1
        # 的 Get-Content 默认使用本机 ANSI，GBK 双字节解码可能吞掉紧邻中文的
        # JSON 引号。使用严格 UTF-8，同时仍允许 StreamReader 自动识别 BOM。
        $utf8 = New-Object Text.UTF8Encoding($false, $true)
        $text = [IO.File]::ReadAllText($Path, $utf8)
        return $text | ConvertFrom-Json
    }
    catch {
        throw "$Label 无法按 UTF-8 JSON 读取：$($_.Exception.Message)"
    }
}

function Assert-RequiredProgramFiles([string]$Directory) {
    foreach ($name in @(
            'MTTFTest.exe', 'MTTFTest.Watchdog.exe', 'MTTFTest.SessionAgent.exe',
            'MTTFTest.SafetyAgent.exe', 'MTTFTest.Watchdog.Protocol.dll')) {
        $path = Join-Path $Directory $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "缺少组件：$name" }
    }
}

function Assert-RuntimePrerequisites([string]$Source, [string]$Root) {
    if ($PSVersionTable.PSVersion.Major -lt 5) { throw '需要 Windows PowerShell 5.1 或更高版本。' }
    $framework = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue
    if ($null -eq $framework -or [int]$framework.Release -lt 528040) { throw '缺少 .NET Framework 4.8，请先安装运行环境。' }
    $nativeDirectory = if ([Environment]::Is64BitOperatingSystem) { 'SysWOW64' } else { 'System32' }
    if (-not (Test-Path -LiteralPath (Join-Path $env:SystemRoot ($nativeDirectory + '\nicaiu.dll')))) {
        throw '缺少 x86 NI-DAQmx 原生运行时 nicaiu.dll；禁止把文件复制成功当作可运行。'
    }
    $drive = New-Object IO.DriveInfo([IO.Path]::GetPathRoot($Root))
    $bytes = (Get-ChildItem -LiteralPath $Source -Recurse -File | Measure-Object Length -Sum).Sum
    if ($drive.AvailableFreeSpace -lt (3 * $bytes + 1GB)) { throw '安装卷空间不足，需要包体积三倍加 1 GiB。' }
}

function Archive-MaintenanceInhibitForInstall(
    [string]$StateRoot = (Join-Path $env:ProgramData 'MTTFTest')) {
    $maintenance = Join-Path $StateRoot 'maintenance-inhibit.json'
    if (-not (Test-Path -LiteralPath $maintenance -PathType Leaf)) { return }

    # 重新安装/修复已在前面确认主程序退出、旧会话空闲，并停用了
    # 旧服务与任务。此时旧的维护事务只是过期启动禁止，不应再阻断换包。
    # 保留原始文件供审计，再由新安装建立服务和任务。
    $archiveRoot = Join-Path $StateRoot 'MaintenanceArchive'
    [void](New-Item -ItemType Directory -Path $archiveRoot -Force)
    $archiveName = 'maintenance-inhibit.preinstall.' +
        [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '.' +
        [Guid]::NewGuid().ToString('N') + '.json'
    Move-Item -LiteralPath $maintenance -Destination (Join-Path $archiveRoot $archiveName)
    Write-Host '已封存旧清场维护状态；它不再限制重新安装或修复。'
}

function Stop-Supervisor {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -ne $service -and $service.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force -Confirm:$false
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
}

function Stop-InstalledSessionAgent([string]$Root) { Stop-InstalledProcess $Root 'MTTFTest.SessionAgent.exe' }

function Install-CurrentSlot([string]$Source, [string]$Root) {
    [void](Get-VerifiedDeploymentIdentity $Source)
    $Root = Resolve-SafeDirectory $Root 'InstallRoot'
    [void](New-Item -ItemType Directory -Path $Root -Force)
    $staging = Join-Path $Root ('.current-staging-' + [Guid]::NewGuid().ToString('N'))
    $retired = Join-Path $Root ('.retired-' + [Guid]::NewGuid().ToString('N'))
    $current = Join-Path $Root 'Current'
    $journal = Join-Path $Root 'install-transaction.json'
    if (Test-Path -LiteralPath $journal) {
        $pending = Read-Utf8JsonFile $journal '未完成换包事务'
        $saved = Resolve-SafeDirectory ([string]$pending.retired) 'Retired'
        if ([IO.Path]::GetDirectoryName($saved) -ne $Root -or
            [IO.Path]::GetFileName($saved) -notlike '.retired-*') { throw '换包恢复路径越界。' }
        if (-not (Test-Path -LiteralPath $current) -and (Test-Path -LiteralPath $saved)) {
            Move-Item -LiteralPath $saved -Destination $current
        }
    }
    [void](New-Item -ItemType Directory -Path $staging)
    try {
        Get-ChildItem -LiteralPath $Source -Force | Copy-Item -Destination $staging -Recurse -Force
        [void](Get-VerifiedDeploymentIdentity $staging)
        @{ retired = $retired; current = $current; startedUtc = [DateTime]::UtcNow.ToString('O') } |
            ConvertTo-Json | Set-Content -LiteralPath $journal -Encoding UTF8
        if (Test-Path -LiteralPath $current) { Move-Item -LiteralPath $current -Destination $retired }
        try {
            Move-Item -LiteralPath $staging -Destination $current
            [void](Assert-InstalledPackageMatchesSource $Source $Root)
        }
        catch {
            if (-not (Test-Path -LiteralPath $current) -and (Test-Path -LiteralPath $retired)) {
                Move-Item -LiteralPath $retired -Destination $current
            }
            throw
        }
        Remove-Item -LiteralPath $journal -Force
    }
    finally {
        if (Test-Path -LiteralPath $staging) {
            $checked = Resolve-SafeDirectory $staging 'Staging'
            if ([IO.Path]::GetDirectoryName($checked) -ne $Root) { throw '清理路径越界。' }
            Remove-Item -LiteralPath $checked -Recurse -Force
        }
    }
}

function Get-VerifiedDeploymentIdentity([string]$Directory) {
    Assert-RequiredProgramFiles $Directory
    $directoryFull = Resolve-SafeDirectory $Directory 'PackageDirectory'
    $identityPath = Join-Path $directoryFull 'build-identity.json'
    $identity = Read-Utf8JsonFile $identityPath '安装包身份'
    if ([string]$identity.gitCommit -notmatch '^[0-9a-fA-F]{40}$' -or
        [string]$identity.packageContentSha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        @($identity.files).Count -eq 0) {
        throw "安装包身份不完整：$directoryFull"
    }
    if ([string]$identity.recoveryArchitectureGeneration -ne 'EPB-V2.17' -or
        [int]$identity.watchdogSchema -ne 7 -or
        [string]$identity.releaseStatus -ne 'FORMAL_RELEASE' -or
        -not [bool]$identity.deploymentApproved -or
        ([Version]$identity.fileVersion).Major -ne 2 -or
        ([Version]$identity.fileVersion).Minor -ne 17 -or
        @($identity.componentIdentities).Count -ne 8) {
        throw "拒绝旧许可、V3 混装或非正式包：$directoryFull"
    }
    foreach ($component in @($identity.componentIdentities)) {
        if ([string]$component.fileVersion -ne [string]$identity.fileVersion -or
            [IO.Path]::GetFileName([string]$component.name) -ne [string]$component.name -or
            (Get-Item -LiteralPath (Join-Path $directoryFull ([string]$component.name))).VersionInfo.FileVersion -ne [string]$identity.fileVersion) {
            throw "正式组件版本混装：$($component.name)"
        }
    }
    $prefix = $directoryFull.TrimEnd('\', '/') + '\'
    $names = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $paths = New-Object 'System.Collections.Generic.SortedDictionary[string,string]' ([StringComparer]::Ordinal)
    foreach ($file in @($identity.files)) {
        $relative = [string]$file.name
        if ([string]::IsNullOrWhiteSpace($relative) -or
            [IO.Path]::IsPathRooted($relative) -or $relative -match '(^|[\\/])\.\.([\\/]|$)' -or
            -not $names.Add($relative.Replace('\','/'))) {
            throw "安装包清单路径无效：$relative"
        }
        $path = [IO.Path]::GetFullPath((Join-Path $directoryFull $relative))
        if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $path -PathType Leaf) -or
            [string]$file.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne [string]$file.sha256) {
            throw "安装包文件缺失或哈希不一致：$path"
        }
        $paths.Add($relative.Replace('\','/'), $path)
    }
    foreach ($actual in @(Get-ChildItem -LiteralPath $directoryFull -File -Recurse)) {
        $relative = $actual.FullName.Substring($prefix.Length).Replace('\','/')
        if ($actual.Name -like 'MTTFTest.EngineHost*' -or $actual.Name -like 'MTTFTest.RecoveryKernel*') {
            throw "V2.17 禁止引入 V3 运行组件：$relative"
        }
        if ($relative -in @('build-identity.json','SHA256SUMS.txt','MTTFTest.FirstRun.configured')) { continue }
        if (-not $names.Contains($relative)) { throw "发现清单外文件，拒绝混包：$relative" }
    }
    if ((Get-DeploymentAggregateHash $paths) -ne [string]$identity.packageContentSha256) {
        throw '安装包聚合哈希不一致。'
    }
    return [pscustomobject]@{
        GitCommit = [string]$identity.gitCommit
        PackageContentSha256 = [string]$identity.packageContentSha256
        IdentitySha256 = (Get-FileHash -LiteralPath $identityPath -Algorithm SHA256).Hash
        DeploymentApproved = [bool]$identity.deploymentApproved
        RecoveryArchitectureGeneration = [string]$identity.recoveryArchitectureGeneration
        WatchdogSchema = [int]$identity.watchdogSchema
    }
}

function Get-DeploymentAggregateHash($Paths) {
    $hash = [Security.Cryptography.SHA256]::Create()
    $buffer = New-Object byte[] (1MB)
    $newline = [byte[]]@(10)
    try {
        foreach ($entry in $Paths.GetEnumerator()) {
            $name = [Text.Encoding]::UTF8.GetBytes($entry.Key.ToLowerInvariant() + "`n")
            [void]$hash.TransformBlock($name, 0, $name.Length, $name, 0)
            $stream = [IO.File]::OpenRead($entry.Value)
            try {
                while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    [void]$hash.TransformBlock($buffer, 0, $read, $buffer, 0)
                }
            } finally { $stream.Dispose() }
            [void]$hash.TransformBlock($newline, 0, 1, $newline, 0)
        }
        [void]$hash.TransformFinalBlock([byte[]]@(), 0, 0)
        return ([BitConverter]::ToString($hash.Hash)).Replace('-','').ToLowerInvariant()
    } finally { $hash.Dispose() }
}

function Assert-InstalledPackageMatchesSource([string]$Source, [string]$Root) {
    $expected = Get-VerifiedDeploymentIdentity $Source
    $installed = Get-VerifiedDeploymentIdentity (Join-Path $Root 'Current')
    if ($expected.IdentitySha256 -ne $installed.IdentitySha256) {
        throw "安装未完成：Current 身份不等于来源包；Expected=$($expected.GitCommit)，Actual=$($installed.GitCommit)。禁止报告 PASS。"
    }
    return $installed
}

function Set-UnattendedAcl([string]$Root) {
    & icacls.exe $Root '/inheritance:r' `
        '/grant:r' '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' `
        '*S-1-5-32-545:(OI)(CI)RX' | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "安装目录 ACL 设置失败：$LASTEXITCODE" }
    $stateRoot = Join-Path $env:ProgramData 'MTTFTest'
    [void](New-Item -ItemType Directory -Path $stateRoot -Force)
    & icacls.exe $stateRoot '/inheritance:r' `
        '/grant:r' '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' `
        '*S-1-5-11:(OI)(CI)M' | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "运行状态目录 ACL 设置失败：$LASTEXITCODE" }
}

function Install-RecoveryHealthTask([string]$Root) {
    $current = Join-Path $Root 'Current'
    $keepaliveTrigger = New-ScheduledTaskTrigger -Once -At ((Get-Date).AddMinutes(1)) `
        -RepetitionInterval ([TimeSpan]::FromMinutes(1))
    $healthScript = Join-Path $current 'Deployment\Test-MTTFTest-RecoveryHealth.ps1'
    if (-not (Test-Path -LiteralPath $healthScript)) { throw "缺少健康检查脚本：$healthScript" }
    $healthAction = New-ScheduledTaskAction -Execute (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') `
        -Argument "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$healthScript`" -InstallRoot `"$Root`""
    $healthSettings = New-ScheduledTaskSettingsSet -StartWhenAvailable `
        -ExecutionTimeLimit ([TimeSpan]::FromSeconds(45)) -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName $healthTaskName -Action $healthAction `
        -Trigger @((New-ScheduledTaskTrigger -AtStartup), $keepaliveTrigger) `
        -Principal (New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest) `
        -Settings $healthSettings -Force | Out-Null
}

function Install-ServiceAndAgent([string]$Root) {
    $current = Join-Path $Root 'Current'
    $watchdog = Join-Path $current 'MTTFTest.Watchdog.exe'
    $sessionAgent = Join-Path $current 'MTTFTest.SessionAgent.exe'
    $mainApplication = Join-Path $current 'MTTFTest.exe'
    & sc.exe query $serviceName *> $null
    if ($LASTEXITCODE -eq 0) {
        & sc.exe config $serviceName `
            'binPath=' "`"$watchdog`"" `
            'start=' 'delayed-auto' `
            'obj=' 'LocalSystem' | Out-Host
    }
    else {
        & sc.exe create $serviceName `
            'binPath=' "`"$watchdog`"" `
            'start=' 'delayed-auto' `
            'obj=' 'LocalSystem' `
            'DisplayName=' 'MTTFTest Unattended Supervisor' | Out-Host
    }
    if ($LASTEXITCODE -ne 0) { throw "监督服务安装失败：$LASTEXITCODE" }
    & sc.exe failure $serviceName `
        'reset=' '0' `
        'actions=' 'restart/5000/restart/15000/restart/60000' | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "SCM 恢复策略设置失败：$LASTEXITCODE" }
    & sc.exe failureflag $serviceName 1 | Out-Host

    $account = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $action = New-ScheduledTaskAction -Execute $sessionAgent
    $logonTrigger = New-ScheduledTaskTrigger -AtLogOn -User $account
    $keepaliveTrigger = New-ScheduledTaskTrigger -Once `
        -At ((Get-Date).AddMinutes(1)) `
        -RepetitionInterval ([TimeSpan]::FromMinutes(1))
    $principal = New-ScheduledTaskPrincipal -UserId $account `
        -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable `
        -RestartCount 255 -RestartInterval ([TimeSpan]::FromMinutes(1)) `
        -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName $taskName -Action $action `
        -Trigger @($logonTrigger, $keepaliveTrigger) `
        -Principal $principal -Settings $settings -Force | Out-Null

    $autoStartAction = New-ScheduledTaskAction -Execute $watchdog `
        -Argument '--launch-main' `
        -WorkingDirectory $current
    $autoStartTrigger = New-ScheduledTaskTrigger -AtLogOn -User $account
    $autoStartTrigger.Delay = 'PT15S'
    $autoStartPrincipal = New-ScheduledTaskPrincipal -UserId $account `
        -LogonType Interactive -RunLevel Highest
    $autoStartSettings = New-ScheduledTaskSettingsSet -StartWhenAvailable `
        -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName $autoStartTaskName -Action $autoStartAction `
        -Trigger $autoStartTrigger -Principal $autoStartPrincipal `
        -Settings $autoStartSettings -Force | Out-Null
    Install-RecoveryHealthTask $Root
    Start-Service -Name $serviceName
    Start-ScheduledTask -TaskName $taskName
}

function Stop-InstalledProcess([string]$Root, [string]$FileName) {
    $prefix = (Resolve-SafeDirectory $Root 'InstallRoot').TrimEnd('\', '/') + '\'
    $processes = Get-CimInstance Win32_Process `
        -Filter "Name='$FileName'" `
        -ErrorAction Stop
    foreach ($process in @($processes)) {
        if ([string]::IsNullOrWhiteSpace([string]$process.ExecutablePath)) {
            throw "无法核验进程路径，停止卸载：$FileName PID=$($process.ProcessId)"
        }
        $actual = [IO.Path]::GetFullPath([string]$process.ExecutablePath)
        if (-not $actual.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { continue }
        $handle = Get-Process -Id ([int]$process.ProcessId) -ErrorAction SilentlyContinue
        if ($null -eq $handle) { continue }
        try {
            # Retain the exact process handle until exit; never hide access/stop failures.
            [void]$handle.Handle
            if (-not [string]::Equals([string]$handle.Path, $actual, [StringComparison]::OrdinalIgnoreCase)) {
                throw "进程身份已变化，停止卸载：$FileName PID=$($process.ProcessId)"
            }
            Stop-Process -InputObject $handle -Force -Confirm:$false -ErrorAction Stop
            if (-not $handle.WaitForExit(10000)) {
                throw "进程未在 10 秒内退出，未删除程序文件：$FileName PID=$($process.ProcessId)"
            }
            Write-Host "已退出：$FileName PID=$($process.ProcessId)"
        }
        finally { $handle.Dispose() }
    }
}

function Remove-InstalledProgramFiles([string]$Root) {
    $resolved = Resolve-SafeDirectory $Root 'InstallRoot'
    if ([IO.Path]::GetFileName($resolved) -ne 'MTTFTest') {
        throw "拒绝删除非 MTTFTest 安装目录：$resolved"
    }
    if (Test-Path -LiteralPath $resolved -PathType Container) {
        for ($attempt = 1; $attempt -le 3; $attempt++) {
            try {
                Remove-Item -LiteralPath $resolved -Recurse -Force -Confirm:$false -ErrorAction Stop
                return
            }
            catch {
                if (-not (Test-Path -LiteralPath $resolved)) { return }
                if ($attempt -eq 3) {
                    throw "程序目录仍被占用或无删除权限：$resolved。服务和任务可能已经移除；可使用本卸载工具重试，ProgramData 保持不变。原因：$($_.Exception.Message)"
                }
                Start-Sleep -Milliseconds 500
            }
        }
    }
}

function Test-CurrentSlotReplacementRequired([string]$Source, [string]$Root) {
    $sourceIdentity = Get-VerifiedDeploymentIdentity $Source
    $current = Join-Path $Root 'Current'
    $sourceVersion = [Reflection.AssemblyName]::GetAssemblyName(
        (Join-Path $Source 'MTTFTest.exe')).Version
    try {
        $currentVersion = [Reflection.AssemblyName]::GetAssemblyName(
            (Join-Path $current 'MTTFTest.exe')).Version
    }
    catch { return $true }
    if ($sourceVersion.CompareTo($currentVersion) -lt 0) {
        throw "拒绝静默降级：来源=$sourceVersion，已安装=$currentVersion。"
    }
    try { $currentIdentity = Get-VerifiedDeploymentIdentity $current }
    catch { return $true }
    # 产品版本不区分同版修复包。只有完整身份和清单文件都相同才允许跳过。
    return $sourceIdentity.IdentitySha256 -ne $currentIdentity.IdentitySha256
}

function Initialize-RuntimeConfig([string]$Source, [string]$Root) {
    $stateRoot = Join-Path $env:ProgramData 'MTTFTest'
    $runtimeConfig = Join-Path $stateRoot 'Config'
    [void](New-Item -ItemType Directory -Path $runtimeConfig -Force)

    $sourceConfig = Join-Path $Source 'Config'
    $previousConfig = Join-Path $Root 'Current\Config'
    foreach ($name in $runtimeConfigNames) {
        $target = Join-Path $runtimeConfig $name
        if (Test-Path -LiteralPath $target -PathType Leaf) { continue }

        $candidate = Join-Path $previousConfig $name
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            $candidate = Join-Path $sourceConfig $name
        }
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            throw "运行配置模板缺失：$name"
        }
        Copy-Item -LiteralPath $candidate -Destination $target -Force
    }
}

function Assert-InstalledMainStopped([string]$Root) {
    $expected = [IO.Path]::GetFullPath(
        (Join-Path $Root 'Current\MTTFTest.exe'))
    $processes = Get-CimInstance Win32_Process `
        -Filter "Name='MTTFTest.exe'" `
        -ErrorAction SilentlyContinue
    foreach ($process in @($processes)) {
        if ([string]::IsNullOrWhiteSpace([string]$process.ExecutablePath)) { throw '无法核验主程序路径，拒绝换包。' }
        $actual = [IO.Path]::GetFullPath([string]$process.ExecutablePath)
        if ($actual.StartsWith(([IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)) {
            throw '检测到已安装的旧版主程序仍在运行。请先在程序中安全停止试验并完全退出，再双击新版本 MTTFTest.exe。'
        }
    }
}

function Stop-InstalledRuntimeTasks([string]$Root) {
    foreach ($name in @($autoStartTaskName, $taskName, $healthTaskName)) {
        $task = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
        if ($null -ne $task) {
            Disable-ScheduledTask -TaskName $name | Out-Null
            Stop-ScheduledTask -TaskName $name -ErrorAction Stop
        }
    }
    Stop-InstalledSessionAgent $Root
}

function Assert-RuntimeIdle([string]$Root) {
    $prefix = (Resolve-SafeDirectory $Root 'InstallRoot').TrimEnd('\') + '\'
    foreach ($name in @('MTTFTest.SafetyAgent.exe','MTTFTest.EngineHost.exe','MTTFTest.Watchdog.exe')) {
        foreach ($proc in @(Get-CimInstance Win32_Process -Filter "Name='$name'" -ErrorAction Stop)) {
            if ([string]::IsNullOrWhiteSpace([string]$proc.ExecutablePath)) { throw '后台进程路径不可核验。' }
            if (([IO.Path]::GetFullPath([string]$proc.ExecutablePath)).StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -and
                ($name -ne 'MTTFTest.Watchdog.exe' -or [string]$proc.CommandLine -match '--session-host|--parent-pid')) {
                throw '仍有安全接管或旧会话，先执行一键停止全部相关进程，再安装。'
            }
        }
    }
}


function Invoke-LegacyCheckpointSafeRollover([string]$Root) {
    $stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
    $stateRoot = [IO.Path]::GetFullPath((Join-Path $env:ProgramData 'MTTFTest'))
    $archiveRoot = [IO.Path]::GetFullPath(
        (Join-Path $stateRoot ("MigrationArchive\legacy-checkpoint-$stamp")))
    [void](New-Item -ItemType Directory -Path $archiveRoot -Force)

    $checkpoint = [IO.Path]::GetFullPath(
        (Join-Path $env:LOCALAPPDATA 'MTTFTest\unattended-run-checkpoint.json'))
    $legacy = $null
    if (Test-Path -LiteralPath $checkpoint -PathType Leaf) {
        try { $legacy = Read-Utf8JsonFile $checkpoint '旧运行检查点' }
        catch { throw "旧检查点无法读取，拒绝换代：$($_.Exception.Message)" }
    }

    if ($null -eq $legacy -and (Test-Path -LiteralPath (Join-Path $stateRoot 'Migration\schema5-remaining-cycles-migration.json'))) { return }

    $migration = [ordered]@{
        migrationSchemaVersion = 7
        sourceSchemaVersion = if ($null -eq $legacy) { 0 } else { [int]$legacy.SchemaVersion }
        migratedUtc = [DateTime]::UtcNow.ToString('O')
        safetyState = 'SafeIdleAlarmed'
        authorizationMigrated = $false
        permitMigrated = $false
        nonceMigrated = $false
        operatorPhysicalIsolationConfirmed = [bool]$PhysicalIsolationConfirmed
        storeDir = if ($null -eq $legacy) { '' } else { [string]$legacy.StoreDir }
        testName = if ($null -eq $legacy) { '' } else { [string]$legacy.TestName }
        selectedChannels = if ($null -eq $legacy) { @() } else { @($legacy.SelectedChannels) }
        remainingFormalCycles = if ($null -eq $legacy) { @{} } else { $legacy.RemainingFormalCycles }
        archiveRoot = $archiveRoot
    }

    if ($null -ne $legacy) {
        $legacySchema = [int]$legacy.SchemaVersion
        $legacyCycleKeyCount = 0
        if ($null -ne $legacy.RemainingFormalCycles) {
            $legacyCycleKeyCount = @($legacy.RemainingFormalCycles.PSObject.Properties).Count
        }
        $legacyHasPayload =
            (-not [string]::IsNullOrWhiteSpace([string]$legacy.RunId)) -or
            (-not [string]::IsNullOrWhiteSpace([string]$legacy.RootRunId)) -or
            (-not [string]::IsNullOrWhiteSpace([string]$legacy.StoreDir)) -or
            (-not [string]::IsNullOrWhiteSpace([string]$legacy.TestName)) -or
            ($legacyCycleKeyCount -gt 0)
        $isEmptyDisarmed = (-not [bool]$legacy.Armed) -and
            (-not [bool]$legacy.GracefulPaused) -and
            (-not $legacyHasPayload)

        if ($isEmptyDisarmed) {
            # 空的未授权检查点不携带授权、许可或剩余圈数，归档留证后直接换代；
            # 不得把无负载空壳当作不支持的旧格式拒绝安装。
            $migration['legacyDisposition'] = 'EmptyDisarmedDiscarded'
            foreach ($path in @($checkpoint, "$checkpoint.bak")) {
                if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
                Move-Item -LiteralPath $path -Destination (Join-Path $archiveRoot ([IO.Path]::GetFileName($path)))
            }
        }
        else {
            if ($legacySchema -notin @(5, 6)) {
                throw "旧运行检查点 SchemaVersion=$legacySchema 不受支持；保持 SafeIdleAlarmed，拒绝安装新授权。"
            }
            if (-not $PhysicalIsolationConfirmed -and (-not [bool]$legacy.MotorOffConfirmed -or
                -not [bool]$legacy.PressureSafeConfirmed -or
                -not [bool]$legacy.PersistenceDrained)) {
                throw "schema $legacySchema 会话缺少 MotorOff/PressureSafe/PersistenceDrained 三项安全证明；保持 SafeIdleAlarmed，拒绝安装新授权。"
            }

            foreach ($path in @($checkpoint, "$checkpoint.bak")) {
                if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
                $destination = Join-Path $archiveRoot ([IO.Path]::GetFileName($path))
                Move-Item -LiteralPath $path -Destination $destination
            }

            if (-not [string]::IsNullOrWhiteSpace([string]$legacy.StoreDir) -and
                -not [string]::IsNullOrWhiteSpace([string]$legacy.TestName)) {
                $projectRoot = [IO.Path]::GetFullPath(
                    (Join-Path ([string]$legacy.StoreDir) ([string]$legacy.TestName)))
                $storeRoot = (Resolve-SafeDirectory ([string]$legacy.StoreDir) 'StoreDir').TrimEnd('\') + '\'
                if (-not $projectRoot.StartsWith($storeRoot, [StringComparison]::OrdinalIgnoreCase)) { throw '项目检查点路径越界。' }
                $projectCheckpoint = Join-Path $projectRoot 'Recovery\unattended-run-checkpoint.json'
                foreach ($path in @($projectCheckpoint, "$projectCheckpoint.bak")) {
                    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
                    $name = 'project-' + [IO.Path]::GetFileName($path)
                    Move-Item -LiteralPath $path -Destination (Join-Path $archiveRoot $name)
                }
                $sessions = Join-Path $projectRoot 'WatchdogSessions'
                if (Test-Path -LiteralPath $sessions -PathType Container) {
                    $sealed = Join-Path $projectRoot "WatchdogSessions.LegacySealed-$stamp"
                    Move-Item -LiteralPath $sessions -Destination $sealed
                    $migration['projectSessionArchive'] = $sealed
                }
            }
        }
    }

    foreach ($supervisorRoot in @(
            (Join-Path $stateRoot 'Supervisor\sessions'),
            (Join-Path $env:LOCALAPPDATA 'MTTFTest\SupervisorConsole\sessions'))) {
        if (-not (Test-Path -LiteralPath $supervisorRoot -PathType Container)) { continue }
        foreach ($file in @(Get-ChildItem -LiteralPath $supervisorRoot -File -Filter '*.json')) {
            $isSchema5 = $false
            try {
                $json = Read-Utf8JsonFile $file.FullName 'Supervisor 旧会话'
                $isSchema5 = [int]$json.SchemaVersion -in @(5, 6)
            }
            catch { }
            if (-not $isSchema5) { continue }
            Move-Item -LiteralPath $file.FullName -Destination `
                (Join-Path $archiveRoot ("supervisor-" + [Guid]::NewGuid().ToString('N') + '-' + $file.Name))
        }
    }

    $migrationRoot = Join-Path $stateRoot 'Migration'
    [void](New-Item -ItemType Directory -Path $migrationRoot -Force)
    $migrationPath = Join-Path $migrationRoot 'schema5-remaining-cycles-migration.json'
    $migration | ConvertTo-Json -Depth 8 | Set-Content `
        -LiteralPath $migrationPath -Encoding UTF8
    Write-Host "旧 schema 5/6 检查点已安全封存；仅迁移剩余圈数：$migrationPath"
}

function Assert-Health([string]$Root) {
    $current = Join-Path $Root 'Current'
    Assert-RequiredProgramFiles $current
    $service = Get-Service -Name $serviceName -ErrorAction Stop
    if ($service.Status -ne 'Running') { throw "监督服务未运行：$($service.Status)" }
    $task = Get-ScheduledTask -TaskName $taskName -ErrorAction Stop
    if ($task.Settings.RestartCount -lt 1) {
        throw 'SessionAgent 登录任务缺少崩溃自动重启策略。'
    }
    $keepaliveTriggers = @($task.Triggers | Where-Object {
        $null -ne $_.Repetition -and $_.Repetition.Interval -eq 'PT1M'
    })
    if ($task.Triggers.Count -lt 2 -or $keepaliveTriggers.Count -ne 1) {
        throw 'SessionAgent 登录任务缺少每分钟存活触发器。'
    }
    [void](Get-ScheduledTask -TaskName $autoStartTaskName -ErrorAction Stop)
    $runtimeConfig = Join-Path (Join-Path $env:ProgramData 'MTTFTest') 'Config'
    foreach ($name in $runtimeConfigNames) {
        if (-not (Test-Path -LiteralPath (Join-Path $runtimeConfig $name) -PathType Leaf)) {
            throw "运行配置缺失：$name"
        }
    }
}

function Write-ConfiguredMarker([string]$Root) {
    $path = Join-Path (Join-Path $Root 'Current') $configuredMarkerName
    [IO.File]::WriteAllText(
        $path,
        "ConfiguredUtc=$([DateTime]::UtcNow.ToString('O'))`r`n",
        (New-Object Text.UTF8Encoding($false)))
}

function Get-ShortcutPaths([string]$Name = $shortcutName) {
    $paths = @()
    $desktop = [Environment]::GetFolderPath('DesktopDirectory')
    $programs = [Environment]::GetFolderPath('Programs')
    if (-not [string]::IsNullOrWhiteSpace($desktop)) {
        $paths += (Join-Path $desktop $Name)
    }
    if (-not [string]::IsNullOrWhiteSpace($programs)) {
        $paths += (Join-Path $programs $Name)
    }
    return @($paths)
}

function Install-Shortcuts([string]$Root) {
    $current = Join-Path $Root 'Current'
    $target = Join-Path $current 'MTTFTest.exe'
    $launcher = Join-Path $current 'MTTFTest.Watchdog.exe'
    if (-not (Test-Path -LiteralPath $target -PathType Leaf) -or
        -not (Test-Path -LiteralPath $launcher -PathType Leaf)) {
        throw "快捷方式主程序或 Supervisor 启动器不存在：$current"
    }
    $targetVersion = (Get-Item -LiteralPath $target).VersionInfo.FileVersion
    if ([string]::IsNullOrWhiteSpace($targetVersion)) { $targetVersion = '未知版本' }
    foreach ($legacy in @(Get-ShortcutPaths 'MT EPB 试验系统 V2.14.lnk')) {
        if (Test-Path -LiteralPath $legacy -PathType Leaf) {
            Remove-Item -LiteralPath $legacy -Force -Confirm:$false
        }
    }
    $shell = New-Object -ComObject WScript.Shell
    foreach ($path in @(Get-ShortcutPaths)) {
        $parent = Split-Path -Parent $path
        if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
            [void](New-Item -ItemType Directory -Path $parent -Force)
        }
        $shortcut = $shell.CreateShortcut($path)
        $shortcut.TargetPath = $launcher
        $shortcut.Arguments = "--launch-main --main-executable `"$target`""
        $shortcut.WorkingDirectory = $current
        $shortcut.IconLocation = "$target,0"
        $shortcut.Description = "MT EPB 试验系统 V$targetVersion（Supervisor 无人值守正式版）"
        $shortcut.Save()
        $shortcutBytes = [IO.File]::ReadAllBytes($path)
        if ($shortcutBytes.Length -lt 22) {
            throw "快捷方式格式无效，无法设置管理员运行标记：$path"
        }
        # Shell Link Header 的 LinkFlags 第 2 个字节置 0x20，即 SLDF_RUNAS_USER。
        $shortcutBytes[21] = $shortcutBytes[21] -bor 0x20
        [IO.File]::WriteAllBytes($path, $shortcutBytes)
    }
}

function Remove-Shortcuts {
    foreach ($path in @(
            @(Get-ShortcutPaths) +
            @(Get-ShortcutPaths 'MT EPB 试验系统 V2.14.lnk'))) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            Remove-Item -LiteralPath $path -Force -Confirm:$false
        }
    }
}

function Write-OperationContext(
    [string]$Operation,
    [string]$Root,
    [string]$Source = '') {
    Write-Host ''
    Write-Host "=== MTTFTest $Operation ===" -ForegroundColor Cyan
    Write-Host "时间：$([DateTime]::Now.ToString('yyyy-MM-dd HH:mm:ss'))"
    Write-Host "计算机：$env:COMPUTERNAME"
    Write-Host "账号：$([Security.Principal.WindowsIdentity]::GetCurrent().Name)"
    Write-Host "PowerShell：$($PSVersionTable.PSVersion)"
    if (-not [string]::IsNullOrWhiteSpace($Source)) {
        Write-Host "来源目录：$Source"
    }
    Write-Host "安装目录：$Root"
    Write-Host "保留数据：$(Join-Path $env:ProgramData 'MTTFTest')"
}

function Write-OperationStep([int]$Index, [int]$Total, [string]$Message) {
    Write-Host "[$Index/$Total] $Message" -ForegroundColor Cyan
}

function Request-UninstallConfirmation {
    $answer = [string](Read-Host `
        '只询问一次：输入 Y 或 A 确认卸载；输入 N 或直接回车取消')
    return $answer.Trim().ToUpperInvariant() -in @('Y', 'A')
}

function Write-DeploymentResult(
    [string]$Operation,
    [string]$Version,
    [string]$Root,
    [string]$Source = '') {
    $stateRoot = Join-Path $env:ProgramData 'MTTFTest'
    $resultRoot = Join-Path $stateRoot 'DeploymentLogs'
    [void](New-Item -ItemType Directory -Path $resultRoot -Force)
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    $sessionTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    $autoStartTask = Get-ScheduledTask -TaskName $autoStartTaskName -ErrorAction SilentlyContinue
    $result = [ordered]@{
        result = 'PASS'
        operation = $Operation
        version = $Version
        computerName = $env:COMPUTERNAME
        userName = [Security.Principal.WindowsIdentity]::GetCurrent().Name
        powershellVersion = [string]$PSVersionTable.PSVersion
        sourceDirectory = $Source
        installRoot = $Root
        installRootExists = Test-Path -LiteralPath $Root -PathType Container
        serviceState = if ($null -eq $service) { 'NotInstalled' } else { [string]$service.Status }
        sessionAgentTaskState = if ($null -eq $sessionTask) { 'NotInstalled' } else { [string]$sessionTask.State }
        autoStartTaskState = if ($null -eq $autoStartTask) { 'NotInstalled' } else { [string]$autoStartTask.State }
        programDataRetained = Test-Path -LiteralPath $stateRoot -PathType Container
        completedLocal = [DateTime]::Now.ToString('yyyy-MM-dd HH:mm:ss')
        completedUtc = [DateTime]::UtcNow.ToString('O')
    }
    $resultPath = Join-Path $resultRoot 'last-deployment-result.json'
    $result | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $resultPath -Encoding UTF8
    Write-Host ''
    Write-Host '=== 操作成功 ===' -ForegroundColor Green
    Write-Host "操作：$Operation"
    if (-not [string]::IsNullOrWhiteSpace($Version)) { Write-Host "版本：V$Version" }
    Write-Host "安装目录存在：$($result.installRootExists)"
    Write-Host "服务状态：$($result.serviceState)"
    Write-Host "登录代理任务：$($result.sessionAgentTaskState)"
    Write-Host "主程序自启动任务：$($result.autoStartTaskState)"
    Write-Host "结果文件：$resultPath" -ForegroundColor Green
}

$root = Resolve-SafeDirectory $InstallRoot 'InstallRoot'

if ($env:MTTFTEST_QUICKDEPLOY_CONFIRMATION_PROBE -eq '1') {
    $probeConfirmed = Request-UninstallConfirmation
    Write-Output "QUICKDEPLOY_CONFIRMATION_PROBE_PASS Confirmed=$probeConfirmed"
    return
}

if ($env:MTTFTEST_QUICKDEPLOY_ARGUMENT_PROBE -eq '1') {
    $probeSource = Resolve-SafeDirectory $SourceDirectory 'SourceDirectory'
    Write-Output "QUICKDEPLOY_ARGUMENT_PROBE_PASS Mode=$Mode Source=$probeSource Root=$root"
    return
}

Assert-Administrator

# Serialize installers with the short health task. Failed maintenance remains inhibited until repair.
$maintenanceMutex = New-Object Threading.Mutex($false, 'Global\MTTFTest.MaintenanceHealth.V1')
$maintenanceHeld = $false
try {
try { $maintenanceHeld = $maintenanceMutex.WaitOne(45000) }
catch [Threading.AbandonedMutexException] { $maintenanceHeld = $true }
if (-not $maintenanceHeld) { throw '健康检查/另一安装事务尚未结束，请稍后重试。' }
function Enter-DeploymentMaintenance {
    $state = Join-Path $env:ProgramData 'MTTFTest'
    [void](New-Item -ItemType Directory -Path $state -Force)
    $marker = Join-Path $state 'maintenance-inhibit.json'
    if (-not (Test-Path -LiteralPath $marker)) {
        @{ Mode=$Mode; StartedUtc=[DateTime]::UtcNow.ToString('O'); InstallRoot=$root } |
            ConvertTo-Json | Set-Content -LiteralPath $marker -Encoding UTF8
    }
}

if ($Mode -eq 'Uninstall') {
    $installedExecutable = Join-Path $root 'Current\MTTFTest.exe'
    $installedVersion = ''
    if (Test-Path -LiteralPath $installedExecutable -PathType Leaf) {
        $installedVersion = (Get-Item -LiteralPath $installedExecutable).VersionInfo.FileVersion
    }
    Write-OperationContext '卸载' $root
    Write-Warning "即将删除 $root 下的程序、服务、计划任务和快捷方式。"
    Write-Host '卸载前必须先安全停止试验并完全退出主程序。'
    Write-Host 'ProgramData 中的现场配置、日志和事故证据不会删除。'
    if (-not $ForceUninstall -and -not (Request-UninstallConfirmation)) {
        Write-Host '已取消卸载，未做任何修改。'
        return
    }
    if ($PSCmdlet.ShouldProcess($root, '卸载程序、服务、登录任务和快捷方式（保留 ProgramData）')) {
        Assert-InstalledMainStopped $root
        foreach ($critical in @('MTTFTest.SafetyAgent.exe', 'MTTFTest.EngineHost.exe')) {
            if (@(Get-CimInstance Win32_Process -Filter "Name='$critical'" -ErrorAction Stop).Count -gt 0) { throw '请先运行一键停止全部相关进程，完成安全清场后卸载。' }
        }
        Write-OperationStep 1 6 '停止监督服务。'
        Enter-DeploymentMaintenance
        Stop-Supervisor
        Write-OperationStep 2 6 '停止并删除登录代理和主程序自启动任务。'
        $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        if ($null -ne $task) {
            Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
            Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
        }
        $autoStartTask = Get-ScheduledTask -TaskName $autoStartTaskName -ErrorAction SilentlyContinue
        if ($null -ne $autoStartTask) {
            Stop-ScheduledTask -TaskName $autoStartTaskName -ErrorAction SilentlyContinue
            Unregister-ScheduledTask -TaskName $autoStartTaskName -Confirm:$false
        }
        Write-OperationStep 3 6 '停止安装目录中的主程序和后台组件。'
        if (Get-ScheduledTask -TaskName $healthTaskName -ErrorAction SilentlyContinue) {
            Stop-ScheduledTask -TaskName $healthTaskName -ErrorAction SilentlyContinue
            Unregister-ScheduledTask -TaskName $healthTaskName -Confirm:$false
        }
        Stop-InstalledSessionAgent $root
        Stop-InstalledProcess $root 'MTTFTest.exe'
        Stop-InstalledProcess $root 'MTTFTest.SafetyAgent.exe'
        Stop-InstalledProcess $root 'MTTFTest.Watchdog.exe'
        Write-OperationStep 4 6 '删除监督服务。'
        if ($null -ne (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) {
            & sc.exe delete $serviceName | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "监督服务删除失败：ExitCode=$LASTEXITCODE" }
        }
        else {
            Write-Host '监督服务未安装，跳过。'
        }
        Write-OperationStep 5 6 '删除桌面和开始菜单快捷方式。'
        Remove-Shortcuts
        Write-OperationStep 6 6 '删除程序安装目录。'
        Remove-InstalledProgramFiles $root
        Write-Host '卸载完成：程序、服务、计划任务和快捷方式已删除；ProgramData 配置、日志和事故证据已保留。'
        Write-DeploymentResult 'Uninstall' $installedVersion $root
    }
    return
}

$source = Resolve-SafeDirectory $SourceDirectory 'SourceDirectory'

if ($Mode -eq 'Configure') {
    Assert-InstalledMainStopped $root
    Enter-DeploymentMaintenance
    $current = Join-Path $root 'Current'
    Assert-RequiredProgramFiles $current
    Initialize-RuntimeConfig $current $root
    Set-UnattendedAcl $root
    Install-ServiceAndAgent $root
    Assert-Health $root
    Install-Shortcuts $root
    Write-ConfiguredMarker $root
    Archive-MaintenanceInhibitForInstall
    Write-Host '首次运行环境、登录自启动和快捷方式已配置。'
    return
}

if ($Mode -eq 'PromoteLastKnownGood') {
    Assert-InstalledMainStopped $root
    Enter-DeploymentMaintenance
    Stop-Supervisor
    $current = Join-Path $root 'Current'
    Assert-RequiredProgramFiles $current
    $staging = Join-Path $root ('.lkg-staging-' + [Guid]::NewGuid().ToString('N'))
    [void](New-Item -ItemType Directory -Path $staging)
    try {
        $lkg = Join-Path $root 'LastKnownGood'
        Get-ChildItem -LiteralPath $current -Force | Copy-Item -Destination $staging -Recurse -Force
        $retired = Join-Path $root ('.lkg-retired-' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))
        if (Test-Path -LiteralPath $lkg) { Move-Item -LiteralPath $lkg -Destination $retired }
        Move-Item -LiteralPath $staging -Destination $lkg
        Assert-RequiredProgramFiles $lkg
    }
    finally {
        if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    }
    Start-Service -Name $serviceName
    Archive-MaintenanceInhibitForInstall
    Write-Host "LastKnownGood 已由操作人员显式晋升：$(Join-Path $root 'LastKnownGood')"
    return
}

[void](Get-VerifiedDeploymentIdentity $source)
Assert-RuntimePrerequisites $source $root
$sourceVersion = (Get-Item -LiteralPath (Join-Path $source 'MTTFTest.exe')).VersionInfo.FileVersion
if ([string]::IsNullOrWhiteSpace($sourceVersion)) {
    throw '无法从来源目录的 MTTFTest.exe 读取版本号。'
}
if ($PSCmdlet.ShouldProcess($root, "$Mode V$sourceVersion 无人值守运行环境")) {
    Write-OperationContext "$Mode V$sourceVersion" $root $source
    Write-OperationStep 1 9 '确认已安装的主程序没有运行。'
    Assert-InstalledMainStopped $root
    Assert-RuntimeIdle $root
    Write-OperationStep 2 9 '停止旧监督服务和运行任务。'
    Enter-DeploymentMaintenance
    Stop-Supervisor
    Stop-InstalledRuntimeTasks $root
    Write-OperationStep 3 9 '确认断能证明、封存旧 schema 5/6 检查点并仅迁移剩余圈数。'
    Invoke-LegacyCheckpointSafeRollover $root
    Write-OperationStep 4 9 '初始化并保留现场运行配置。'
    Initialize-RuntimeConfig $source $root
    Write-OperationStep 5 9 '安装或更新程序文件。'
    if (Test-CurrentSlotReplacementRequired $source $root) {
        [void](Install-CurrentSlot $source $root)
    }
    else {
        Write-Host 'Current 完整身份及所有清单文件一致，保留现有程序。'
    }
    [void](Assert-InstalledPackageMatchesSource $source $root)
    Write-OperationStep 6 9 '配置程序目录和 ProgramData 权限。'
    Set-UnattendedAcl $root
    Write-OperationStep 7 9 '安装 schema 7 监督服务、登录代理和自启动任务。'
    Install-ServiceAndAgent $root
    Write-OperationStep 8 9 '检查程序文件、服务和任务状态。'
    Assert-Health $root
    Write-OperationStep 9 9 '创建 Supervisor 启动快捷方式并写入配置标记。'
    Install-Shortcuts $root
    Write-ConfiguredMarker $root
    Archive-MaintenanceInhibitForInstall
    Write-Host "V$sourceVersion 正式包已完成 $Mode；发布与现场运行状态由操作人员负责。"
    Write-DeploymentResult $Mode $sourceVersion $root $source
}
} finally {
    if ($maintenanceHeld) { $maintenanceMutex.ReleaseMutex() }
    $maintenanceMutex.Dispose()
}
