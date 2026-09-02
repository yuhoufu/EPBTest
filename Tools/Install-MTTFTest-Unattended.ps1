[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet('Install', 'Repair', 'Configure', 'Uninstall', 'PromoteLastKnownGood')]
    [string]$Mode = 'Install',
    [string]$SourceDirectory = (Split-Path -Parent $PSScriptRoot),
    [string]$InstallRoot = (Join-Path $env:ProgramFiles 'MTTFTest'),
    [string]$SoakEvidencePath
)

$ErrorActionPreference = 'Stop'
$serviceName = 'MTTFTestSupervisor'
$taskName = 'MTTFTestSessionAgent'
$autoStartTaskName = 'MTTFTestAutoStart'
$shortcutName = 'MT EPB 试验系统 V2.14.lnk'
$configuredMarkerName = 'MTTFTest.FirstRun.configured'
$runtimeConfigNames = @(
    'AIConfig.xml', 'AlarmConfig.xml', 'AOConfig.xml', 'DOConfig.xml',
    'PowerSupplyConfig.xml', 'TestConfig.xml', 'UnattendedAlarmConfig.xml',
    'UIConfig.xml')

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

function Assert-RequiredProgramFiles([string]$Directory) {
    foreach ($name in @(
            'MTTFTest.exe', 'MTTFTest.Watchdog.exe', 'MTTFTest.SessionAgent.exe',
            'MTTFTest.SafetyAgent.exe', 'MTTFTest.Watchdog.Protocol.dll')) {
        $path = Join-Path $Directory $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "缺少组件：$name" }
    }
}

function Stop-Supervisor {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -ne $service -and $service.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
}

function Stop-InstalledSessionAgent([string]$Root) {
    $expected = [IO.Path]::GetFullPath(
        (Join-Path $Root 'Current\MTTFTest.SessionAgent.exe'))
    $processes = Get-CimInstance Win32_Process `
        -Filter "Name='MTTFTest.SessionAgent.exe'" `
        -ErrorAction SilentlyContinue
    foreach ($process in @($processes)) {
        if ([string]::IsNullOrWhiteSpace([string]$process.ExecutablePath)) { continue }
        $actual = [IO.Path]::GetFullPath([string]$process.ExecutablePath)
        if ([string]::Equals($actual, $expected, [StringComparison]::OrdinalIgnoreCase)) {
            Stop-Process -Id ([int]$process.ProcessId) -Force -ErrorAction SilentlyContinue
        }
    }
}

function Install-CurrentSlot([string]$Source, [string]$Root) {
    Assert-RequiredProgramFiles $Source
    [void](New-Item -ItemType Directory -Path $Root -Force)
    $staging = Join-Path $Root ('.current-staging-' + [Guid]::NewGuid().ToString('N'))
    [void](New-Item -ItemType Directory -Path $staging)
    try {
        $current = Join-Path $Root 'Current'
        Get-ChildItem -LiteralPath $Source -Force | Copy-Item -Destination $staging -Recurse -Force
        [void](New-Item -ItemType File -Path (Join-Path $staging 'MTTFTest.UnattendedMode.required') -Force)
        $retired = Join-Path $Root ('.retired-' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))
        if (Test-Path -LiteralPath $current) { Move-Item -LiteralPath $current -Destination $retired }
        Move-Item -LiteralPath $staging -Destination $current
        Assert-RequiredProgramFiles $current
        if (Test-Path -LiteralPath $retired) {
            Write-Warning "旧 Current 已保留用于人工审计：$retired"
        }
    }
    finally {
        if (Test-Path -LiteralPath $staging) {
            Remove-Item -LiteralPath $staging -Recurse -Force
        }
    }
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
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $account
    $principal = New-ScheduledTaskPrincipal -UserId $account `
        -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable `
        -RestartCount 255 -RestartInterval ([TimeSpan]::FromMinutes(1)) `
        -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger `
        -Principal $principal -Settings $settings -Force | Out-Null

    $autoStartAction = New-ScheduledTaskAction -Execute $mainApplication `
        -WorkingDirectory $current
    $autoStartTrigger = New-ScheduledTaskTrigger -AtLogOn -User $account
    $autoStartTrigger.Delay = 'PT15S'
    $autoStartPrincipal = New-ScheduledTaskPrincipal -UserId $account `
        -LogonType Interactive -RunLevel Limited
    $autoStartSettings = New-ScheduledTaskSettingsSet -StartWhenAvailable `
        -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName $autoStartTaskName -Action $autoStartAction `
        -Trigger $autoStartTrigger -Principal $autoStartPrincipal `
        -Settings $autoStartSettings -Force | Out-Null
    Start-Service -Name $serviceName
    Start-ScheduledTask -TaskName $taskName
}

function Stop-InstalledProcess([string]$Root, [string]$FileName) {
    $expected = [IO.Path]::GetFullPath(
        (Join-Path (Join-Path $Root 'Current') $FileName))
    $processes = Get-CimInstance Win32_Process `
        -Filter "Name='$FileName'" `
        -ErrorAction SilentlyContinue
    foreach ($process in @($processes)) {
        if ([string]::IsNullOrWhiteSpace([string]$process.ExecutablePath)) { continue }
        $actual = [IO.Path]::GetFullPath([string]$process.ExecutablePath)
        if ([string]::Equals($actual, $expected, [StringComparison]::OrdinalIgnoreCase)) {
            Stop-Process -Id ([int]$process.ProcessId) -Force -ErrorAction SilentlyContinue
        }
    }
}

function Remove-InstalledProgramFiles([string]$Root) {
    $resolved = Resolve-SafeDirectory $Root 'InstallRoot'
    if ([IO.Path]::GetFileName($resolved) -ne 'MTTFTest') {
        throw "拒绝删除非 MTTFTest 安装目录：$resolved"
    }
    if (Test-Path -LiteralPath $resolved -PathType Container) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

function Test-CurrentSlotReplacementRequired([string]$Source, [string]$Root) {
    $current = Join-Path $Root 'Current'
    try {
        Assert-RequiredProgramFiles $current
    }
    catch {
        return $true
    }

    try {
        $sourceExecutable = Join-Path $Source 'MTTFTest.exe'
        $currentExecutable = Join-Path $current 'MTTFTest.exe'
        $sourceVersion = [Reflection.AssemblyName]::GetAssemblyName($sourceExecutable).Version
        $currentVersion = [Reflection.AssemblyName]::GetAssemblyName($currentExecutable).Version
        return $sourceVersion.CompareTo($currentVersion) -gt 0
    }
    catch {
        # 无法可靠比较版本时采用保守升级，避免留下不完整程序槽。
        return $true
    }
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
        if ([string]::IsNullOrWhiteSpace([string]$process.ExecutablePath)) { continue }
        $actual = [IO.Path]::GetFullPath([string]$process.ExecutablePath)
        if ([string]::Equals($actual, $expected, [StringComparison]::OrdinalIgnoreCase)) {
            throw '检测到已安装的旧版主程序仍在运行。请先在程序中安全停止试验并完全退出，再双击新版本 MTTFTest.exe。'
        }
    }
}

function Stop-InstalledRuntimeTasks([string]$Root) {
    foreach ($name in @($autoStartTaskName, $taskName)) {
        $task = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
        if ($null -ne $task) {
            Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
        }
    }
    Stop-InstalledSessionAgent $Root
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

function Get-ShortcutPaths {
    $paths = @()
    $desktop = [Environment]::GetFolderPath('DesktopDirectory')
    $programs = [Environment]::GetFolderPath('Programs')
    if (-not [string]::IsNullOrWhiteSpace($desktop)) {
        $paths += (Join-Path $desktop $shortcutName)
    }
    if (-not [string]::IsNullOrWhiteSpace($programs)) {
        $paths += (Join-Path $programs $shortcutName)
    }
    return @($paths)
}

function Install-Shortcuts([string]$Root) {
    $current = Join-Path $Root 'Current'
    $target = Join-Path $current 'MTTFTest.exe'
    if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
        throw "快捷方式目标不存在：$target"
    }
    $targetVersion = (Get-Item -LiteralPath $target).VersionInfo.FileVersion
    if ([string]::IsNullOrWhiteSpace($targetVersion)) { $targetVersion = '未知版本' }
    $shell = New-Object -ComObject WScript.Shell
    foreach ($path in @(Get-ShortcutPaths)) {
        $parent = Split-Path -Parent $path
        if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
            [void](New-Item -ItemType Directory -Path $parent -Force)
        }
        $shortcut = $shell.CreateShortcut($path)
        $shortcut.TargetPath = $target
        $shortcut.WorkingDirectory = $current
        $shortcut.IconLocation = "$target,0"
        $shortcut.Description = "MT EPB 试验系统 V$targetVersion（正式包，运行状态由操作人员负责）"
        $shortcut.Save()
    }
}

function Remove-Shortcuts {
    foreach ($path in @(Get-ShortcutPaths)) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            Remove-Item -LiteralPath $path -Force
        }
    }
}

Assert-Administrator
$root = Resolve-SafeDirectory $InstallRoot 'InstallRoot'
$source = Resolve-SafeDirectory $SourceDirectory 'SourceDirectory'

if ($Mode -eq 'Uninstall') {
    if ($PSCmdlet.ShouldProcess($root, '卸载程序、服务、登录任务和快捷方式（保留 ProgramData）')) {
        Stop-Supervisor
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
        Stop-InstalledSessionAgent $root
        Stop-InstalledProcess $root 'MTTFTest.exe'
        Stop-InstalledProcess $root 'MTTFTest.SafetyAgent.exe'
        Stop-InstalledProcess $root 'MTTFTest.Watchdog.exe'
        & sc.exe delete $serviceName | Out-Host
        Remove-Shortcuts
        Remove-InstalledProgramFiles $root
        Write-Host '卸载完成：程序、服务、计划任务和快捷方式已删除；ProgramData 配置、日志和事故证据已保留。'
    }
    return
}

if ($Mode -eq 'Configure') {
    $current = Join-Path $root 'Current'
    Assert-RequiredProgramFiles $current
    Initialize-RuntimeConfig $current $root
    Set-UnattendedAcl $root
    Install-ServiceAndAgent $root
    Assert-Health $root
    Install-Shortcuts $root
    Write-ConfiguredMarker $root
    Write-Host '首次运行环境、登录自启动和快捷方式已配置。'
    return
}

if ($Mode -eq 'PromoteLastKnownGood') {
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
    Write-Host "LastKnownGood 已由操作人员显式晋升：$(Join-Path $root 'LastKnownGood')"
    return
}

$sourceVersion = (Get-Item -LiteralPath (Join-Path $source 'MTTFTest.exe')).VersionInfo.FileVersion
if ([string]::IsNullOrWhiteSpace($sourceVersion)) {
    throw '无法从来源目录的 MTTFTest.exe 读取版本号。'
}
if ($PSCmdlet.ShouldProcess($root, "$Mode V$sourceVersion 无人值守运行环境")) {
    Assert-InstalledMainStopped $root
    Stop-Supervisor
    Stop-InstalledRuntimeTasks $root
    Initialize-RuntimeConfig $source $root
    if (Test-CurrentSlotReplacementRequired $source $root) {
        [void](Install-CurrentSlot $source $root)
    }
    else {
        Write-Host '已安装版本不低于来源版本，保留 Current，不创建重复 retired 目录。'
    }
    Set-UnattendedAcl $root
    Install-ServiceAndAgent $root
    Assert-Health $root
    Install-Shortcuts $root
    Write-ConfiguredMarker $root
    Write-Host "V$sourceVersion 正式包已完成 $Mode；发布与现场运行状态由操作人员负责。"
}
