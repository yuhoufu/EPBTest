[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet('Install', 'Repair', 'Uninstall', 'PromoteLastKnownGood')]
    [string]$Mode = 'Install',
    [string]$SourceDirectory = (Split-Path -Parent $PSScriptRoot),
    [string]$InstallRoot = (Join-Path $env:ProgramFiles 'MTTFTest'),
    [string]$SoakEvidencePath
)

$ErrorActionPreference = 'Stop'
$serviceName = 'MTTFTestSupervisor'
$taskName = 'MTTFTestSessionAgent'
$expectedVersion = '2.14.2.0'
$shortcutName = 'MT EPB 试验系统 V2.14.lnk'

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

function Read-And-VerifyPackage([string]$Directory) {
    $identityPath = Join-Path $Directory 'build-identity.json'
    if (-not (Test-Path -LiteralPath $identityPath -PathType Leaf)) {
        throw "缺少发布身份：$identityPath"
    }
    $identity = Get-Content -LiteralPath $identityPath -Raw | ConvertFrom-Json
    if ([string]$identity.fileVersion -ne $expectedVersion -or
        [string]$identity.productVersion -ne ('V' + $expectedVersion)) {
        throw "包版本不一致：期望 $expectedVersion，实际 $($identity.fileVersion)。"
    }
    foreach ($entry in @($identity.files)) {
        $relative = ([string]$entry.name).Replace('/', '\')
        if ([IO.Path]::IsPathRooted($relative) -or $relative.Contains('..')) {
            throw "清单相对路径非法：$relative"
        }
        $path = [IO.Path]::GetFullPath((Join-Path $Directory $relative))
        $prefix = $Directory.TrimEnd('\') + '\'
        if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "清单文件缺失或越界：$relative"
        }
        $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne ([string]$entry.sha256).ToLowerInvariant()) {
            throw "清单哈希不一致：$relative"
        }
    }
    foreach ($name in @(
            'MTTFTest.exe', 'MTTFTest.Watchdog.exe', 'MTTFTest.SessionAgent.exe',
            'MTTFTest.SafetyAgent.exe', 'MTTFTest.Watchdog.Protocol.dll')) {
        $path = Join-Path $Directory $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "缺少组件：$name" }
        if ((Get-Item -LiteralPath $path).VersionInfo.FileVersion -ne $expectedVersion) {
            throw "组件版本不一致：$name"
        }
    }
    return $identity
}

function Protect-MachineJson([object]$Value) {
    Add-Type -AssemblyName System.Security
    $json = $Value | ConvertTo-Json -Depth 8 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $protected = [Security.Cryptography.ProtectedData]::Protect(
        $bytes,
        [Text.Encoding]::UTF8.GetBytes('MTTFTest.PackageSlot.Schema5'),
        [Security.Cryptography.DataProtectionScope]::LocalMachine)
    return [Convert]::ToBase64String($protected)
}

function Unprotect-MachineJson([string]$ProtectedPayloadBase64) {
    Add-Type -AssemblyName System.Security
    if ([string]::IsNullOrWhiteSpace($ProtectedPayloadBase64)) {
        throw 'schema 5 封印载荷为空。'
    }
    $clear = [Security.Cryptography.ProtectedData]::Unprotect(
        [Convert]::FromBase64String($ProtectedPayloadBase64),
        [Text.Encoding]::UTF8.GetBytes('MTTFTest.PackageSlot.Schema5'),
        [Security.Cryptography.DataProtectionScope]::LocalMachine)
    return ([Text.Encoding]::UTF8.GetString($clear) | ConvertFrom-Json)
}

function Write-SlotDescriptor(
    [string]$SlotPath,
    [string]$SlotName,
    [object]$Identity,
    [string]$CanonicalRootPath = '') {
    $sealedRootPath = if ([string]::IsNullOrWhiteSpace($CanonicalRootPath)) {
        [IO.Path]::GetFullPath($SlotPath)
    } else {
        [IO.Path]::GetFullPath($CanonicalRootPath)
    }
    $payload = [ordered]@{
        SchemaVersion = 5
        SlotName = $SlotName
        ProductVersion = $expectedVersion
        ReleaseStatus = if ($SlotName -eq 'LastKnownGood') {
            'OPERATOR_PROMOTED_LAST_KNOWN_GOOD'
        } else {
            [string]$Identity.releaseStatus
        }
        # 文件仍在 staging 中生成，但安全证明必须绑定原子切换后的正式槽路径。
        RootPath = $sealedRootPath
        ManifestSha256 = (Get-FileHash -LiteralPath (Join-Path $SlotPath 'build-identity.json') -Algorithm SHA256).Hash
        ConfigSha256 = [string]$Identity.configSha256
        CreatedUtc = [DateTime]::UtcNow.ToString('O')
        Files = @($Identity.files)
    }
    $envelope = [ordered]@{
        SchemaVersion = 5
        Protection = 'DPAPI-LocalMachine'
        ProtectedPayloadBase64 = Protect-MachineJson $payload
    }
    $path = Join-Path $SlotPath 'package-slot.v5.json'
    [IO.File]::WriteAllText(
        $path,
        ($envelope | ConvertTo-Json -Depth 4),
        (New-Object Text.UTF8Encoding($false)))
}

function Assert-SlotDescriptor([string]$SlotPath, [string]$SlotName) {
    $root = [IO.Path]::GetFullPath($SlotPath).TrimEnd('\', '/')
    $descriptorPath = Join-Path $root 'package-slot.v5.json'
    if (-not (Test-Path -LiteralPath $descriptorPath -PathType Leaf)) {
        throw "$SlotName 缺少 schema 5 封印描述：$descriptorPath"
    }
    $envelope = Get-Content -LiteralPath $descriptorPath -Raw -Encoding UTF8 |
        ConvertFrom-Json
    if ([int]$envelope.SchemaVersion -ne 5 -or
        [string]$envelope.Protection -ne 'DPAPI-LocalMachine') {
        throw "$SlotName 封印信封非法。"
    }
    $payload = Unprotect-MachineJson ([string]$envelope.ProtectedPayloadBase64)
    $sealedRoot = [IO.Path]::GetFullPath([string]$payload.RootPath).TrimEnd('\', '/')
    if ([int]$payload.SchemaVersion -ne 5 -or
        [string]$payload.SlotName -ne $SlotName -or
        [string]$payload.ProductVersion -ne $expectedVersion -or
        -not [string]::Equals($sealedRoot, $root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$SlotName 封印身份不一致：Root=$sealedRoot Expected=$root Version=$($payload.ProductVersion)"
    }
    $manifestHash = (Get-FileHash -LiteralPath `
        (Join-Path $root 'build-identity.json') -Algorithm SHA256).Hash
    if (-not [string]::Equals(
            $manifestHash,
            [string]$payload.ManifestSha256,
            [StringComparison]::OrdinalIgnoreCase) -or
        @($payload.Files).Count -lt 1) {
        throw "$SlotName 封印文件身份不完整或 manifest 已变化。"
    }
}

function Write-SlotPointer([string]$Root, [string]$ActiveSlot) {
    $current = Join-Path $Root 'Current'
    $lastKnownGood = Join-Path $Root 'LastKnownGood'
    $payload = [ordered]@{
        SchemaVersion = 5
        ActiveSlot = $ActiveSlot
        CurrentPath = $current
        LastKnownGoodPath = $lastKnownGood
        RevisionUtcTicks = [DateTime]::UtcNow.Ticks
    }
    $envelope = [ordered]@{
        SchemaVersion = 5
        Protection = 'DPAPI-LocalMachine'
        ProtectedPayloadBase64 = Protect-MachineJson $payload
    }
    [IO.File]::WriteAllText(
        (Join-Path $Root 'package-pointer.v5.json'),
        ($envelope | ConvertTo-Json -Depth 4),
        (New-Object Text.UTF8Encoding($false)))
}

function Stop-Supervisor {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -ne $service -and $service.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
}

function Install-CurrentSlot([string]$Source, [string]$Root) {
    $sourceIdentity = Read-And-VerifyPackage $Source
    [void](New-Item -ItemType Directory -Path $Root -Force)
    $staging = Join-Path $Root ('.current-staging-' + [Guid]::NewGuid().ToString('N'))
    [void](New-Item -ItemType Directory -Path $staging)
    try {
        $current = Join-Path $Root 'Current'
        Get-ChildItem -LiteralPath $Source -Force | Copy-Item -Destination $staging -Recurse -Force
        [void](New-Item -ItemType File -Path (Join-Path $staging 'MTTFTest.UnattendedMode.required') -Force)
        $stagingIdentity = Read-And-VerifyPackage $staging
        Write-SlotDescriptor $staging 'Current' $stagingIdentity $current
        $retired = Join-Path $Root ('.retired-' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))
        if (Test-Path -LiteralPath $current) { Move-Item -LiteralPath $current -Destination $retired }
        Move-Item -LiteralPath $staging -Destination $current
        Assert-SlotDescriptor $current 'Current'
        if (Test-Path -LiteralPath $retired) {
            Write-Warning "旧 Current 已保留用于人工审计：$retired"
        }
        Write-SlotPointer $Root 'Current'
        return $sourceIdentity
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
    Start-Service -Name $serviceName
    Start-ScheduledTask -TaskName $taskName
}

function Assert-Health([string]$Root) {
    $current = Join-Path $Root 'Current'
    [void](Read-And-VerifyPackage $current)
    Assert-SlotDescriptor $current 'Current'
    if (-not (Test-Path -LiteralPath (Join-Path $Root 'package-pointer.v5.json'))) {
        throw '缺少 schema 5 双槽指针。'
    }
    $service = Get-Service -Name $serviceName -ErrorAction Stop
    if ($service.Status -ne 'Running') { throw "监督服务未运行：$($service.Status)" }
    $task = Get-ScheduledTask -TaskName $taskName -ErrorAction Stop
    if ($task.Settings.RestartCount -lt 1) {
        throw 'SessionAgent 登录任务缺少崩溃自动重启策略。'
    }
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
        $shortcut.Description = 'MT EPB 试验系统 V2.14.2.0（正式包，运行状态由操作人员负责）'
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
    if ($PSCmdlet.ShouldProcess($root, '卸载服务和登录任务（保留程序槽及事故证据）')) {
        Stop-Supervisor
        & sc.exe delete $serviceName | Out-Host
        & schtasks.exe /Delete /TN $taskName /F | Out-Host
        Remove-Shortcuts
        Write-Host '已卸载监督服务和 SessionAgent 任务；程序槽、ProgramData 日志及事故证据已保留。'
    }
    return
}

if ($Mode -eq 'PromoteLastKnownGood') {
    Stop-Supervisor
    $current = Join-Path $root 'Current'
    $identity = Read-And-VerifyPackage $current
    $staging = Join-Path $root ('.lkg-staging-' + [Guid]::NewGuid().ToString('N'))
    [void](New-Item -ItemType Directory -Path $staging)
    try {
        $lkg = Join-Path $root 'LastKnownGood'
        Get-ChildItem -LiteralPath $current -Force | Copy-Item -Destination $staging -Recurse -Force
        Write-SlotDescriptor $staging 'LastKnownGood' $identity $lkg
        $retired = Join-Path $root ('.lkg-retired-' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss'))
        if (Test-Path -LiteralPath $lkg) { Move-Item -LiteralPath $lkg -Destination $retired }
        Move-Item -LiteralPath $staging -Destination $lkg
        Assert-SlotDescriptor $lkg 'LastKnownGood'
        Write-SlotPointer $root 'Current'
    }
    finally {
        if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    }
    Start-Service -Name $serviceName
    Write-Host "LastKnownGood 已由操作人员显式晋升：$(Join-Path $root 'LastKnownGood')"
    return
}

if ($PSCmdlet.ShouldProcess($root, "$Mode V2.14.2.0 无人值守运行环境")) {
    Stop-Supervisor
    [void](Install-CurrentSlot $source $root)
    Set-UnattendedAcl $root
    Install-ServiceAndAgent $root
    Assert-Health $root
    Install-Shortcuts $root
    Write-Host "V2.14.2.0 正式包已完成 $Mode；发布与现场运行状态由操作人员负责。"
}
