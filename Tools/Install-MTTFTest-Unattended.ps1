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
$shortcutName = 'MT EPB 试验系统.lnk'
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
    [IO.File]::Move($maintenance, (Join-Path $archiveRoot $archiveName))
    Write-Host '已封存旧清场维护状态；它不再限制重新安装或修复。'
}

function Stop-Supervisor {
    try { $service = Get-Service -Name $serviceName -ErrorAction Stop }
    catch {
        if ($_.FullyQualifiedErrorId.Split(',')[0] -eq 'NoServiceFoundForGivenName') { return }
        throw
    }
    if ($null -ne $service -and $service.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force -Confirm:$false -ErrorAction Stop
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
}

function Stop-InstalledSessionAgent([string]$Root) { Stop-InstalledProcess $Root 'MTTFTest.SessionAgent.exe' }

function Backup-PendingInstallJournal([string]$Root, [string]$Journal) {
    $item = Get-Item -LiteralPath $Journal -Force -ErrorAction Stop
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        [IO.Path]::GetDirectoryName($item.FullName) -ine $Root -or $item.Name -cne 'install-transaction.json') {
        throw 'InstallJournalBackupTargetInvalid'
    }
    $backup = Join-Path $Root ('.install-transaction-history-' + [Guid]::NewGuid().ToString('N') + '.json')
    $sourceStream = [IO.File]::Open($item.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $backupStream = [IO.File]::Open($backup, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $sourceStream.CopyTo($backupStream); $backupStream.Flush($true) }
        finally { $backupStream.Dispose() }
        if ((Get-DeploymentFileSha256 $item.FullName) -cne (Get-DeploymentFileSha256 $backup)) {
            throw 'InstallJournalBackupHashMismatch'
        }
    }
    finally { $sourceStream.Dispose() }
    return $backup
}

function Install-CurrentSlot([string]$Source, [string]$Root, [switch]$DeferCommit) {
    [void](Get-VerifiedDeploymentIdentity $Source)
    $Root = Resolve-SafeDirectory $Root 'InstallRoot'
    [void](New-Item -ItemType Directory -Path $Root -Force)
    $staging = Join-Path $Root ('.current-staging-' + [Guid]::NewGuid().ToString('N'))
    $retired = Join-Path $Root ('.retired-' + [Guid]::NewGuid().ToString('N'))
    $current = Join-Path $Root 'Current'
    $journal = Join-Path $Root 'install-transaction.json'
    $previousJournal = $null
    if (Test-Path -LiteralPath $journal) {
        $pending = Read-Utf8JsonFile $journal '未完成换包事务'
        if ($pending.schema -eq 2 -and $pending.phase -cin @(
                'OldCurrentRetired', 'OldCurrentAbsent',
                'CurrentPublishedPendingInstallCommit')) {
            Restore-CurrentSlotTransaction ([pscustomobject]@{
                Journal = $journal
                TransactionId = [string]$pending.transactionId
                Current = [string]$pending.current
                Retired = [string]$pending.retired
                OldCurrentExisted = [bool]$pending.oldCurrentExisted
            }) $Root
            $pending = $null
        }
        elseif ($pending.schema -eq 2 -and $pending.phase -ceq 'CurrentReplacementPrepared') {
            $preparedRetired = [IO.Path]::GetFullPath([string]$pending.retired)
            $preparedCurrent = [IO.Path]::GetFullPath([string]$pending.current)
            $retiredExists = Test-Path -LiteralPath $preparedRetired
            $currentExists = Test-Path -LiteralPath $preparedCurrent
            if ([bool]$pending.oldCurrentExisted -and $currentExists -and -not $retiredExists) {
                $abandoned = Join-Path $Root ('.install-transaction-prepared-aborted-' +
                    [string]$pending.transactionId + '.json')
                [IO.File]::Move($journal, $abandoned)
            }
            elseif (-not [bool]$pending.oldCurrentExisted -and -not $currentExists -and -not $retiredExists) {
                $abandoned = Join-Path $Root ('.install-transaction-prepared-aborted-' +
                    [string]$pending.transactionId + '.json')
                [IO.File]::Move($journal, $abandoned)
            }
            else {
                throw 'CurrentPreparedJournalStateAmbiguous'
            }
            $pending = $null
        }
    }
    if (Test-Path -LiteralPath $journal) {
        $pending = Read-Utf8JsonFile $journal '未完成换包事务'
        $saved = Resolve-SafeDirectory ([string]$pending.retired) 'Retired'
        if ([IO.Path]::GetDirectoryName($saved) -ne $Root -or
            [IO.Path]::GetFileName($saved) -notlike '.retired-*' -or
            [string]::IsNullOrWhiteSpace([string]$pending.current) -or
            [IO.Path]::GetFullPath([string]$pending.current) -ine $current) { throw '换包恢复路径越界。' }
        if (Test-Path -LiteralPath $saved) {
            $savedItem = Get-Item -LiteralPath $saved -Force
            if (-not $savedItem.PSIsContainer -or
                ($savedItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'RetiredRecoveryTargetInvalid' }
        }
        $previousJournal = Backup-PendingInstallJournal $Root $journal
        if (-not (Test-Path -LiteralPath $current) -and (Test-Path -LiteralPath $saved)) {
            [IO.Directory]::Move($saved, $current)
        }
    }
    [void](New-Item -ItemType Directory -Path $staging)
    try {
        Get-ChildItem -LiteralPath $Source -Force | Copy-Item -Destination $staging -Recurse -Force
        [void](Get-VerifiedDeploymentIdentity $staging)
        $transactionId = [Guid]::NewGuid().ToString('N')
        $oldCurrentExisted = Test-Path -LiteralPath $current -PathType Container
        $journalText = @{ schema = 2; phase = 'CurrentReplacementPrepared';
            transactionId = $transactionId; retired = $retired; current = $current;
            oldCurrentExisted = [bool]$oldCurrentExisted; previousJournal = $previousJournal;
            startedUtc = [DateTime]::UtcNow.ToString('O') } |
            ConvertTo-Json
        $journalPending = Join-Path $Root ('.install-journal-pending-' + [Guid]::NewGuid().ToString('N'))
        $journalBytes = [Text.Encoding]::UTF8.GetBytes($journalText)
        $journalStream = [IO.File]::Open($journalPending, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $journalStream.Write($journalBytes, 0, $journalBytes.Length); $journalStream.Flush($true) }
        finally { $journalStream.Dispose() }
        if (Test-Path -LiteralPath $journal) {
            $replacedJournal = Join-Path $Root ('.install-journal-replaced-' + [Guid]::NewGuid().ToString('N') + '.json')
            [IO.File]::Replace($journalPending, $journal, $replacedJournal)
        } else {
            [IO.File]::Move($journalPending, $journal)
        }
        $oldCurrentRetired = $false
        if (Test-Path -LiteralPath $current) {
            $oldCurrentItem = Get-Item -LiteralPath $current -Force
            if (-not $oldCurrentItem.PSIsContainer -or
                ($oldCurrentItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
                [IO.Path]::GetDirectoryName($oldCurrentItem.FullName) -ine $Root) {
                throw 'CurrentOriginalTargetInvalid'
            }
            [IO.Directory]::Move($oldCurrentItem.FullName, $retired)
            $oldCurrentRetired = $true
        }
        Set-CurrentSlotJournalPhase $journal $transactionId `
            $(if ($oldCurrentRetired) { 'OldCurrentRetired' } else { 'OldCurrentAbsent' })
        $published = $false
        try {
            [IO.Directory]::Move($staging, $current)
            $published = $true
            [void](Assert-InstalledPackageMatchesSource $Source $Root)
            Set-CurrentSlotJournalPhase $journal $transactionId `
                'CurrentPublishedPendingInstallCommit'
        }
        catch {
            $slotFailure = $_
            try {
                if ($published -and (Test-Path -LiteralPath $current)) {
                    $failed = [IO.Path]::GetFullPath((Join-Path $Root ('.failed-current-' + [Guid]::NewGuid().ToString('N'))))
                    $currentItem = Get-Item -LiteralPath $current -Force
                    if (-not $currentItem.PSIsContainer -or
                        ($currentItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
                        [IO.Path]::GetDirectoryName($currentItem.FullName) -ine $Root -or
                        [IO.Path]::GetDirectoryName($failed) -ine $Root -or
                        (Test-Path -LiteralPath $failed)) { throw 'CurrentRollbackTargetInvalid' }
                    # 保存失败槽及其全部内容，绝不覆盖或递归删除，供故障审计恢复。
                    [IO.Directory]::Move($currentItem.FullName, $failed)
                    Write-Warning "新程序槽校验失败，已保留：$failed"
                }
                if (Test-Path -LiteralPath $current) { throw 'CurrentRollbackDestinationOccupied' }
                if ($oldCurrentRetired -and -not (Test-Path -LiteralPath $retired)) {
                    throw 'CurrentRetiredMissing'
                }
                if ($oldCurrentRetired) {
                    $retiredItem = Get-Item -LiteralPath $retired -Force
                    if (-not $retiredItem.PSIsContainer -or
                        ($retiredItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
                        [IO.Path]::GetDirectoryName($retiredItem.FullName) -ine $Root) { throw 'RetiredRollbackTargetInvalid' }
                    [IO.Directory]::Move($retiredItem.FullName, $current)
                }
                Mark-CurrentSlotPublicationRolledBack $journal $transactionId
            }
            catch {
                throw "CurrentPublicationAndRollbackFailed: Journal=$journal; Publication=$($slotFailure.Exception.Message); Rollback=$($_.Exception.Message)"
            }
            throw $slotFailure
        }
        $reference = [pscustomobject]@{
            Journal = $journal
            TransactionId = $transactionId
            Current = $current
            Retired = $retired
            OldCurrentExisted = [bool]$oldCurrentExisted
        }
        if ($DeferCommit) { return $reference }
        [void](Complete-CurrentSlotTransaction $reference $Root)
    }
    finally {
        if (Test-Path -LiteralPath $staging) {
            $checked = Resolve-SafeDirectory $staging 'Staging'
            if ([IO.Path]::GetDirectoryName($checked) -ne $Root) { throw '候选路径越界。' }
            Write-Warning "Current 发布未完成，保留候选副本供核验：$checked"
        }
    }
}

function Get-DeploymentFileSha256([string]$Path) {
    # 使用只读流，避免 Windows PowerShell 5.1 Get-FileHash 的内部
    # ProviderPath 查询受脚本 -WhatIf 影响而不返回摘要。
    $stream = [IO.File]::OpenRead($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '') }
    finally { $algorithm.Dispose(); $stream.Dispose() }
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
    $packageVersion = [Version]$identity.fileVersion
    $isGuardRelease = $packageVersion -eq [Version]'3.0.0.0'
    $isLegacyRelease = $packageVersion.Major -eq 2 -and $packageVersion.Minor -eq 17
    $expectedComponents = @('MTTFTest.exe', 'Controller.dll', 'MTTFTest.Watchdog.exe',
        'MTTFTest.SafetyAgent.exe', 'MTTFTest.SessionAgent.exe', 'MTTFTest.SafetyHardware.dll',
        'MTTFTest.Watchdog.Protocol.dll', 'MTTFTest.Watchdog.Client.dll')
    if ($isGuardRelease) { $expectedComponents += 'MTTFTest.RecoveryControl.dll' }
    if ([string]$identity.recoveryArchitectureGeneration -ne 'EPB-V2.17' -or
        [int]$identity.watchdogSchema -ne 7 -or
        [string]$identity.releaseStatus -ne 'FORMAL_RELEASE' -or
        -not [bool]$identity.deploymentApproved -or
        (-not $isGuardRelease -and -not $isLegacyRelease) -or
        ($isGuardRelease -and [int]$identity.sessionAgentSchema -ne 8) -or
        @($identity.componentIdentities).Count -ne $expectedComponents.Count) {
        throw "拒绝不兼容协议、组件架构混装或非正式包：$directoryFull"
    }
    $componentNames = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($component in @($identity.componentIdentities)) {
        if ([string]$component.name -notin $expectedComponents -or
            -not $componentNames.Add([string]$component.name) -or
            [string]$component.fileVersion -ne [string]$identity.fileVersion -or
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
            (Get-DeploymentFileSha256 $path) -ne [string]$file.sha256) {
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
        IdentitySha256 = Get-DeploymentFileSha256 $identityPath
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

function Get-DeploymentAclSnapshot([string]$Root) {
    $entries = @()
    foreach ($entry in @(
            [pscustomobject]@{ Name='InstallRoot'; Path=[IO.Path]::GetFullPath($Root) },
            [pscustomobject]@{ Name='StateRoot'; Path=[IO.Path]::GetFullPath((Join-Path $env:ProgramData 'MTTFTest')) })) {
        $exists = Test-Path -LiteralPath $entry.Path -PathType Container
        if (-not $exists -and (Test-Path -LiteralPath $entry.Path)) { throw "AclSnapshotTargetInvalid:$($entry.Name)" }
        $sddl = $null
        if ($exists) {
            $item = Get-Item -LiteralPath $entry.Path -Force -ErrorAction Stop
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "AclSnapshotReparsePointRejected:$($entry.Name)"
            }
            $sddl = (Get-Acl -LiteralPath $entry.Path -ErrorAction Stop).GetSecurityDescriptorSddlForm(14)
            [void][Security.AccessControl.RawSecurityDescriptor]::new($sddl)
        }
        $entries += [ordered]@{ Name=$entry.Name; Path=$entry.Path; Existed=[bool]$exists; Sddl=$sddl }
    }
    return [pscustomobject]@{ Entries=$entries }
}

function Restore-DeploymentAclSnapshot([string]$Root, $Snapshot) {
    $expected = @{
        InstallRoot = [IO.Path]::GetFullPath($Root)
        StateRoot = [IO.Path]::GetFullPath((Join-Path $env:ProgramData 'MTTFTest'))
    }
    $entries = @($Snapshot.Entries)
    if ($entries.Count -ne 2 -or @($entries.Name | Sort-Object -Unique).Count -ne 2) {
        throw 'AclRollbackSnapshotInvalid'
    }
    foreach ($entry in $entries) {
        if (-not $expected.ContainsKey([string]$entry.Name) -or
            [IO.Path]::GetFullPath([string]$entry.Path) -ine $expected[[string]$entry.Name] -or
            $entry.Existed -isnot [bool]) { throw 'AclRollbackIdentityInvalid' }
        if (-not [bool]$entry.Existed) { continue }
        if ([string]::IsNullOrWhiteSpace([string]$entry.Sddl)) { throw 'AclRollbackDescriptorMissing' }
        $security = Get-Acl -LiteralPath ([string]$entry.Path) -ErrorAction Stop
        $security.SetSecurityDescriptorSddlForm([string]$entry.Sddl, 14)
        $directoryInfo = [IO.DirectoryInfo]::new([string]$entry.Path)
        if ($PSVersionTable.PSVersion.Major -le 5) {
            $directoryInfo.SetAccessControl($security)
        } else {
            [IO.FileSystemAclExtensions]::SetAccessControl($directoryInfo, $security)
        }
        $actual = (Get-Acl -LiteralPath ([string]$entry.Path) -ErrorAction Stop).GetSecurityDescriptorSddlForm(14)
        $expectedDescriptor = [Security.AccessControl.RawSecurityDescriptor]::new([string]$entry.Sddl)
        $actualDescriptor = [Security.AccessControl.RawSecurityDescriptor]::new($actual)
        if ($actualDescriptor.GetSddlForm(14) -cne $expectedDescriptor.GetSddlForm(14)) {
            throw "AclRollbackVerificationFailed:$($entry.Name)"
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
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne 1060) {
        throw "无法确认监督服务是否存在，拒绝修改服务：$LASTEXITCODE"
    }
    if ($LASTEXITCODE -eq 0) {
        # 更新既有服务不改写账户或凭据；否则失败回退无法恢复原账户密码。
        & sc.exe config $serviceName `
            'binPath=' "`"$watchdog`"" `
            'start=' 'delayed-auto' | Out-Host
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
    if ($LASTEXITCODE -ne 0) { throw "SCM 非崩溃失败恢复策略设置失败：$LASTEXITCODE" }

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

function Publish-MissingRuntimeConfig([string]$Source, [string]$Target) {
    $targetPath = [IO.Path]::GetFullPath($Target)
    $stage = Join-Path ([IO.Path]::GetDirectoryName($targetPath)) ('.config-pending-' + [Guid]::NewGuid().ToString('N'))
    try {
        $inputStream = [IO.File]::Open($Source, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            $outputStream = [IO.File]::Open($stage, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $inputStream.CopyTo($outputStream); $outputStream.Flush($true) }
            finally { $outputStream.Dispose() }
        }
        finally { $inputStream.Dispose() }
        $publishedHash = Get-DeploymentFileSha256 $stage
        # 同目录发布完整文件；目标已出现时 Move 拒绝覆盖。
        [IO.File]::Move($stage, $targetPath)
        return [pscustomobject]@{ Target = $targetPath; Sha256 = $publishedHash }
    }
    catch {
        if (Test-Path -LiteralPath $stage) { Write-Warning "配置发布未完成，保留临时证据：$stage" }
        throw
    }
}

function Mark-CurrentSlotPublicationRolledBack([string]$Journal, [string]$TransactionId) {
    Set-CurrentSlotJournalPhase $Journal $TransactionId 'CurrentPublicationRolledBack'
}

function Set-CurrentSlotJournalPhase(
    [string]$Journal,
    [string]$TransactionId,
    [string]$Phase) {
    $saved = Read-Utf8JsonFile $Journal 'Current换包事务'
    $allowedCurrent = @('CurrentReplacementPrepared', 'OldCurrentRetired',
        'OldCurrentAbsent', 'CurrentPublishedPendingInstallCommit')
    $allowedNext = @('OldCurrentRetired', 'OldCurrentAbsent',
        'CurrentPublishedPendingInstallCommit', 'CurrentPublicationRolledBack')
    if ($saved.schema -ne 2 -or [string]$saved.phase -cnotin $allowedCurrent -or
        $Phase -cnotin $allowedNext -or
        [string]$saved.transactionId -cne $TransactionId) {
        throw 'CurrentSlotJournalPhaseIdentityMismatch'
    }
    $saved.phase = $Phase
    $saved | Add-Member -NotePropertyName phaseUpdatedUtc `
        -NotePropertyValue ([DateTime]::UtcNow.ToString('O')) -Force
    $pending = $Journal + '.phase-pending-' + [Guid]::NewGuid().ToString('N')
    $replaced = $Journal + '.before-phase-' + [Guid]::NewGuid().ToString('N')
    $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes(($saved | ConvertTo-Json -Depth 6))
    $stream = [IO.File]::Open($pending, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
    finally { $stream.Dispose() }
    [IO.File]::Replace($pending, $Journal, $replaced)
}

function Assert-CurrentSlotTransactionReference($Reference, [string]$Root) {
    if ($null -eq $Reference -or [string]$Reference.TransactionId -notmatch '^[a-f0-9]{32}$') {
        throw 'CurrentSlotTransactionReferenceMissing'
    }
    $resolvedRoot = Resolve-SafeDirectory $Root 'InstallRoot'
    $journal = [IO.Path]::GetFullPath([string]$Reference.Journal)
    if ($journal -ine (Join-Path $resolvedRoot 'install-transaction.json')) {
        throw 'CurrentSlotTransactionJournalMismatch'
    }
    $saved = Read-Utf8JsonFile $journal 'Current换包事务'
    if ($saved.schema -ne 2 -or $saved.phase -cnotin @(
            'OldCurrentRetired', 'OldCurrentAbsent',
            'CurrentPublishedPendingInstallCommit') -or
        [string]$saved.transactionId -cne [string]$Reference.TransactionId -or
        [IO.Path]::GetFullPath([string]$saved.current) -ine (Join-Path $resolvedRoot 'Current') -or
        [IO.Path]::GetFullPath([string]$saved.retired) -ine [IO.Path]::GetFullPath([string]$Reference.Retired) -or
        [bool]$saved.oldCurrentExisted -ne [bool]$Reference.OldCurrentExisted) {
        throw 'CurrentSlotTransactionIdentityMismatch'
    }
    return $saved
}

function Complete-CurrentSlotTransaction($Reference, [string]$Root) {
    $saved = Assert-CurrentSlotTransactionReference $Reference $Root
    if ($saved.phase -cne 'CurrentPublishedPendingInstallCommit') {
        throw 'CurrentSlotCompletionPhaseInvalid'
    }
    $completed = Join-Path (Resolve-SafeDirectory $Root 'InstallRoot') `
        ('.install-transaction-completed-' + $Reference.TransactionId + '.json')
    [IO.File]::Move([string]$Reference.Journal, $completed)
    return $completed
}

function Restore-CurrentSlotTransaction($Reference, [string]$Root) {
    [void](Assert-CurrentSlotTransactionReference $Reference $Root)
    $resolvedRoot = Resolve-SafeDirectory $Root 'InstallRoot'
    $current = Join-Path $resolvedRoot 'Current'
    $retired = [IO.Path]::GetFullPath([string]$Reference.Retired)
    if ([IO.Path]::GetDirectoryName($retired) -ine $resolvedRoot -or
        [IO.Path]::GetFileName($retired) -notlike '.retired-*') {
        throw 'CurrentSlotRollbackRetiredPathInvalid'
    }
    if (Test-Path -LiteralPath $current) {
        $currentItem = Get-Item -LiteralPath $current -Force
        if (-not $currentItem.PSIsContainer -or
            ($currentItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'CurrentSlotRollbackCurrentInvalid'
        }
        $failed = Join-Path $resolvedRoot ('.failed-current-' + [Guid]::NewGuid().ToString('N'))
        [IO.Directory]::Move($currentItem.FullName, $failed)
        Write-Warning "未完成安装的新Current已保留：$failed"
    }
    if ([bool]$Reference.OldCurrentExisted) {
        $retiredItem = Get-Item -LiteralPath $retired -Force -ErrorAction Stop
        if (-not $retiredItem.PSIsContainer -or
            ($retiredItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'CurrentSlotRollbackRetiredInvalid'
        }
        [IO.Directory]::Move($retiredItem.FullName, $current)
    } elseif (Test-Path -LiteralPath $retired) {
        throw 'CurrentSlotRollbackUnexpectedRetired'
    }
    $rolledBack = Join-Path $resolvedRoot `
        ('.install-transaction-rolledback-' + $Reference.TransactionId + '.json')
    [IO.File]::Move([string]$Reference.Journal, $rolledBack)
    return $rolledBack
}

function Undo-InitializedRuntimeConfigs([string]$ConfigRoot, $Published) {
    $expectedRoot = [IO.Path]::GetFullPath($ConfigRoot).TrimEnd('\')
    $failures = New-Object 'System.Collections.Generic.List[string]'
    foreach ($entry in @($Published | Sort-Object Target -Descending)) {
        try {
            $target = [IO.Path]::GetFullPath([string]$entry.Target)
            if ([IO.Path]::GetDirectoryName($target) -ine $expectedRoot) { throw 'ConfigRollbackPathInvalid' }
            if (-not (Test-Path -LiteralPath $target)) { continue }
            $item = Get-Item -LiteralPath $target -Force
            if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
                (Get-DeploymentFileSha256 $target) -cne [string]$entry.Sha256) { throw 'ConfigRollbackContentChanged' }
            $saved = Join-Path $expectedRoot ('.config-rollback-' + [Guid]::NewGuid().ToString('N'))
            [IO.File]::Move($target, $saved)
            Write-Warning "已撤回本次新增配置，副本保留：$saved"
        }
        catch { $failures.Add($_.Exception.Message) }
    }
    if ($failures.Count) { throw ('ConfigRollbackIncomplete: ' + ($failures -join ' | ')) }
}

function Initialize-RuntimeConfig(
    [string]$Source,
    [string]$Root,
    [string]$InstallTransactionPath = '') {
    $stateRoot = Join-Path $env:ProgramData 'MTTFTest'
    $runtimeConfig = Join-Path $stateRoot 'Config'

    $sourceConfig = Join-Path $Source 'Config'
    $previousConfig = Join-Path $Root 'Current\Config'
    $copies = New-Object 'System.Collections.Generic.List[object]'
    foreach ($name in $runtimeConfigNames) {
        $target = Join-Path $runtimeConfig $name
        if (Test-Path -LiteralPath $target -PathType Leaf) { continue }
        if (Test-Path -LiteralPath $target) { throw "运行配置目标不是文件：$target" }

        $candidate = Join-Path $previousConfig $name
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            $candidate = Join-Path $sourceConfig $name
        }
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            throw "运行配置模板缺失：$name"
        }
        $copies.Add([pscustomobject]@{ Source = $candidate; Target = $target })
    }
    # 全部模板验证后再开始写入，避免后续缺项留下前半套配置。
    # 先把所有“原先不存在且允许撤回”的目标及预期字节绑定到总事务，
    # 因而即使进程在 Move 后立即崩溃，下一次启动仍可精确判定所有权。
    $planned = @($copies | ForEach-Object {
        [pscustomobject]@{
            Target = [IO.Path]::GetFullPath([string]$_.Target)
            Sha256 = Get-DeploymentFileSha256 ([string]$_.Source)
        }
    })
    if (-not [string]::IsNullOrWhiteSpace($InstallTransactionPath)) {
        $installTransaction = Read-DeploymentInstallTransaction $InstallTransactionPath $Root
        [void](Set-DeploymentInstallTransactionPhase $InstallTransactionPath $Root `
            @([string]$installTransaction.phase) ([string]$installTransaction.phase) `
            $null $null $null ([pscustomobject]@{ Entries = $planned }))
    }
    [void](New-Item -ItemType Directory -Path $runtimeConfig -Force)
    $published = New-Object 'System.Collections.Generic.List[object]'
    try {
        foreach ($copy in $copies) {
            # 检查后出现的新配置不得被 Force 覆盖。
            $published.Add((Publish-MissingRuntimeConfig $copy.Source $copy.Target))
        }
    }
    catch {
        $publicationFailure = $_
        try { Undo-InitializedRuntimeConfigs $runtimeConfig $published }
        catch { throw "ConfigPublicationAndRollbackFailed: Publication=$($publicationFailure.Exception.Message); Rollback=$($_.Exception.Message)" }
        throw $publicationFailure
    }
    return ,$published.ToArray()
}

function Assert-InstalledMainStopped([string]$Root) {
    $expected = [IO.Path]::GetFullPath(
        (Join-Path $Root 'Current\MTTFTest.exe'))
    $processes = Get-CimInstance Win32_Process `
        -Filter "Name='MTTFTest.exe'" `
        -ErrorAction Stop
    foreach ($process in @($processes)) {
        if ([string]::IsNullOrWhiteSpace([string]$process.ExecutablePath)) { throw '无法核验主程序路径，拒绝换包。' }
        $actual = [IO.Path]::GetFullPath([string]$process.ExecutablePath)
        if ($actual.StartsWith(([IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)) {
            throw '检测到已安装的旧版主程序仍在运行。请先在程序中安全停止试验并完全退出，再双击新版本 MTTFTest.exe。'
        }
    }
}

function Wait-InstalledTaskStopped([string]$Name, [int]$TimeoutMilliseconds = 30000) {
    if ($TimeoutMilliseconds -lt 0) { throw 'TaskStopTimeoutInvalid' }
    $scheduler = $folder = $registered = $instances = $null
    try {
        $scheduler = New-Object -ComObject 'Schedule.Service'
        $scheduler.Connect()
        $folder = $scheduler.GetFolder('\')
        $timer = [Diagnostics.Stopwatch]::StartNew()
        do {
            try {
                $registered = $folder.GetTask($Name)
                if ($registered.Enabled) { throw "InstalledTaskReenabled: $Name" }
                # Disabled 状态不证明已经退出；直接核对运行实例集合。
                $instances = $registered.GetInstances(0)
                if ($null -eq $instances) { throw "InstalledTaskInstancesUnavailable: $Name" }
                if ($instances.Count -eq 0) { return }
            }
            finally {
                foreach ($value in @($instances, $registered)) {
                    if ($null -ne $value -and [Runtime.InteropServices.Marshal]::IsComObject($value)) {
                        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($value)
                    }
                }
                $instances = $registered = $null
            }
            if ($timer.ElapsedMilliseconds -ge $TimeoutMilliseconds) { throw "InstalledTaskStopTimeout: $Name" }
            Start-Sleep -Milliseconds 100
        } while ($true)
    }
    finally {
        foreach ($value in @($folder, $scheduler)) {
            if ($null -ne $value -and [Runtime.InteropServices.Marshal]::IsComObject($value)) {
                [void][Runtime.InteropServices.Marshal]::ReleaseComObject($value)
            }
        }
    }
}

function Get-InstalledTaskSecurityDescriptor([string]$Name) {
    $scheduler = $folder = $registered = $null
    try {
        $scheduler = New-Object -ComObject 'Schedule.Service'
        $scheduler.Connect()
        $folder = $scheduler.GetFolder('\')
        $registered = $folder.GetTask($Name)
        # OWNER | GROUP | DACL；不申请读取 SACL 所需的额外系统权限。
        $sddl = [string]$registered.GetSecurityDescriptor(7)
        if ([string]::IsNullOrWhiteSpace($sddl)) { throw "TaskBackupSecurityDescriptorMissing: $Name" }
        [void][Security.AccessControl.RawSecurityDescriptor]::new($sddl)
        return $sddl
    }
    finally {
        foreach ($value in @($registered, $folder, $scheduler)) {
            if ($null -ne $value -and [Runtime.InteropServices.Marshal]::IsComObject($value)) {
                [void][Runtime.InteropServices.Marshal]::ReleaseComObject($value)
            }
        }
    }
}

function Get-InstalledTaskRunningState([string]$Name) {
    $scheduler = $null
    $folder = $null
    $registered = $null
    $instances = $null
    try {
        $scheduler = New-Object -ComObject 'Schedule.Service'
        $scheduler.Connect()
        $folder = $scheduler.GetFolder('\')
        $registered = $folder.GetTask($Name)
        $instances = $registered.GetInstances(0)
        if ($null -eq $instances) { throw "TaskBackupRunningStateMissing: $Name" }
        return [int]$instances.Count -gt 0
    }
    finally {
        foreach ($value in @($instances, $registered, $folder, $scheduler)) {
            if ($null -ne $value -and [Runtime.InteropServices.Marshal]::IsComObject($value)) {
                [void][Runtime.InteropServices.Marshal]::ReleaseComObject($value)
            }
        }
    }
}

function Set-InstalledTaskSecurityDescriptor([string]$Name, [string]$SecurityDescriptor) {
    [void][Security.AccessControl.RawSecurityDescriptor]::new($SecurityDescriptor)
    $scheduler = $null
    $folder = $null
    $registered = $null
    try {
        $scheduler = New-Object -ComObject 'Schedule.Service'
        $scheduler.Connect()
        $folder = $scheduler.GetFolder('\')
        $registered = $folder.GetTask($Name)
        # TASK_DONT_ADD_PRINCIPAL_ACE (0x10) 禁止 Task Scheduler 在提供的
        # OWNER/GROUP/DACL 之外再次追加 principal ACE。
        $registered.SetSecurityDescriptor($SecurityDescriptor, 0x10)
    }
    finally {
        foreach ($value in @($registered, $folder, $scheduler)) {
            if ($null -ne $value -and [Runtime.InteropServices.Marshal]::IsComObject($value)) {
                [void][Runtime.InteropServices.Marshal]::ReleaseComObject($value)
            }
        }
    }
}

function Assert-DeploymentEvidenceDirectory([string]$Directory) {
    $cursor = [IO.Path]::GetFullPath($Directory)
    while (-not [string]::IsNullOrWhiteSpace($cursor)) {
        if (Test-Path -LiteralPath $cursor -ErrorAction Stop) {
            $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
            if (-not $item.PSIsContainer -or
                ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "DeploymentEvidencePathRedirectedOrNotDirectory: $cursor"
            }
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}

function Initialize-ProtectedDeploymentDirectory([string]$Directory) {
    $path = [IO.Path]::GetFullPath($Directory)
    Assert-DeploymentEvidenceDirectory $path
    $parent = Get-Item -LiteralPath ([IO.Path]::GetDirectoryName($path)) -Force -ErrorAction Stop
    if (-not $parent.PSIsContainer) { throw 'ProtectedEvidenceParentMissing' }
    if (-not (Test-Path -LiteralPath $path -ErrorAction Stop)) {
        $security = [Security.AccessControl.DirectorySecurity]::new()
        $security.SetSecurityDescriptorSddlForm('O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)')
        $directoryInfo = [IO.DirectoryInfo]::new($path)
        if ($PSVersionTable.PSVersion.Major -le 5) { $directoryInfo.Create($security) }
        else { [IO.FileSystemAclExtensions]::Create($directoryInfo, $security) }
    }
    Assert-DeploymentEvidenceDirectory $path
    $actual = Get-Acl -LiteralPath $path -ErrorAction Stop
    $rules = @($actual.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
    if (-not $actual.AreAccessRulesProtected -or
        $actual.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne 'S-1-5-32-544' -or
        $rules.Count -ne 2 -or @($rules.IdentityReference.Value | Sort-Object -Unique).Count -ne 2) {
        throw 'ProtectedEvidenceAclMismatch'
    }
    foreach ($rule in $rules) {
        if ($rule.IdentityReference.Value -notin @('S-1-5-18', 'S-1-5-32-544') -or $rule.IsInherited -or
            $rule.AccessControlType -ne 'Allow' -or $rule.FileSystemRights -ne 'FullControl' -or
            $rule.InheritanceFlags -ne 'ContainerInherit, ObjectInherit' -or $rule.PropagationFlags -ne 'None') {
            throw 'ProtectedEvidenceAclMismatch'
        }
    }
}

function Assert-DeploymentTaskBackupEntries($Saved) {
    $names = @($autoStartTaskName, $taskName, $healthTaskName)
    $entries = @($Saved.tasks)
    if ($entries.Count -ne 3 -or @($entries.name | Sort-Object -Unique).Count -ne 3) { throw 'TaskBackupEntriesInvalid' }
    foreach ($entry in $entries) {
        if ($entry.name -cnotin $names -or $entry.path -cne '\' -or
            $entry.existed -isnot [bool] -or $entry.wasRunning -isnot [bool] -or
            $entry.securityInformation -ne 7) { throw 'TaskBackupEntryIdentityInvalid' }
        if (-not $entry.existed) {
            if ($entry.wasRunning -or $null -ne $entry.xml -or
                $null -ne $entry.securityDescriptor) { throw 'TaskBackupAbsenceInvalid' }
            continue
        }
        if ([string]::IsNullOrWhiteSpace([string]$entry.xml) -or
            [string]::IsNullOrWhiteSpace([string]$entry.securityDescriptor)) { throw 'TaskBackupDefinitionMissing' }
        [void][Security.AccessControl.RawSecurityDescriptor]::new([string]$entry.securityDescriptor)
        $settings = [Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $textReader = [IO.StringReader]::new([string]$entry.xml)
        $reader = $null
        try {
            $reader = [Xml.XmlReader]::Create($textReader, $settings)
            $document = [Xml.XmlDocument]::new()
            $document.XmlResolver = $null
            $document.Load($reader)
            if ($document.DocumentElement.LocalName -cne 'Task' -or
                $document.DocumentElement.NamespaceURI -cne 'http://schemas.microsoft.com/windows/2004/02/mit/task') {
                throw 'TaskBackupXmlInvalid'
            }
        } finally {
            if ($null -ne $reader) { $reader.Dispose() }
            $textReader.Dispose()
        }
    }
}

function Get-NormalizedScheduledTaskXml([string]$Xml) {
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $textReader = [IO.StringReader]::new($Xml)
    $reader = $null
    try {
        $reader = [Xml.XmlReader]::Create($textReader, $settings)
        $document = [Xml.XmlDocument]::new()
        $document.PreserveWhitespace = $false
        $document.XmlResolver = $null
        $document.Load($reader)
        return $document.OuterXml
    }
    finally {
        if ($null -ne $reader) { $reader.Dispose() }
        $textReader.Dispose()
    }
}

function Read-DeploymentTaskBackup([string]$Path, [string]$Root, [string]$ExpectedSha256) {
    if ($ExpectedSha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'TaskBackupExpectedHashMissing' }
    $expectedDirectory = [IO.Path]::GetFullPath((Join-Path $env:ProgramData 'MTTFTestDeploymentEvidence\TaskBackups'))
    $fullPath = [IO.Path]::GetFullPath($Path)
    if ([IO.Path]::GetDirectoryName($fullPath) -ine $expectedDirectory) { throw 'TaskBackupLocationMismatch' }
    Assert-DeploymentEvidenceDirectory $expectedDirectory
    $item = Get-Item -LiteralPath $fullPath -Force -ErrorAction Stop
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'TaskBackupFileInvalid' }
    $bytes = [IO.File]::ReadAllBytes($fullPath)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $actualHash = [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '') }
    finally { $sha.Dispose() }
    if ($actualHash -ine $ExpectedSha256) { throw 'TaskBackupHashMismatch' }
    $saved = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json -ErrorAction Stop
    if ($saved.schema -ne 4 -or $saved.machineName -ine $env:COMPUTERNAME -or
        [string]::IsNullOrWhiteSpace([string]$saved.machineName) -or
        $saved.transactionId -notmatch '^[a-f0-9]{32}$' -or
        [IO.Path]::GetFileName($fullPath) -cne ($saved.transactionId + '.json') -or
        [string]$saved.installRoot -ine [IO.Path]::GetFullPath($Root)) { throw 'TaskBackupIdentityMismatch' }
    Assert-DeploymentTaskBackupEntries $saved
    return $saved
}

function Backup-InstalledRuntimeTasks([string]$Root, [object[]]$Tasks) {
    $entries = @()
    foreach ($name in @($autoStartTaskName, $taskName, $healthTaskName)) {
        $matches = @($Tasks | Where-Object { $_.TaskName -eq $name -and $_.TaskPath -eq '\' })
        if ($matches.Count -gt 1) { throw "TaskBackupIdentityAmbiguous: $name" }
        $xml = $null
        $sddl = $null
        if ($matches.Count -eq 1) {
            $xml = [string](Export-ScheduledTask -TaskName $name -TaskPath '\' -ErrorAction Stop)
            if ([string]::IsNullOrWhiteSpace($xml)) { throw "TaskBackupXmlMissing: $name" }
            $sddl = Get-InstalledTaskSecurityDescriptor $name
        }
        $wasRunning = if ($matches.Count -eq 1) {
            Get-InstalledTaskRunningState $name
        } else { $false }
        $entries += [ordered]@{ name = $name; path = '\'; existed = ($matches.Count -eq 1);
            wasRunning = [bool]$wasRunning; xml = $xml; securityDescriptor = $sddl; securityInformation = 7 }
    }
    Assert-DeploymentTaskBackupEntries ([pscustomobject]@{ tasks = $entries })
    $evidenceRoot = Join-Path $env:ProgramData 'MTTFTestDeploymentEvidence'
    Initialize-ProtectedDeploymentDirectory $evidenceRoot
    $directory = Join-Path $evidenceRoot 'TaskBackups'
    Initialize-ProtectedDeploymentDirectory $directory
    $id = [Guid]::NewGuid().ToString('N')
    $path = Join-Path $directory ($id + '.json')
    $pending = $path + '.pending'
    $bytes = [Text.Encoding]::UTF8.GetBytes(([ordered]@{
        schema = 4; machineName = $env:COMPUTERNAME; transactionId = $id; installRoot = [IO.Path]::GetFullPath($Root)
        capturedUtc = [DateTime]::UtcNow.ToString('O'); tasks = $entries
    } | ConvertTo-Json -Depth 8))
    $stream = [IO.File]::Open($pending, 'CreateNew', 'Write', 'None')
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
    finally { $stream.Dispose() }
    [IO.File]::Move($pending, $path)
    $publishedBytes = [IO.File]::ReadAllBytes($path)
    if ([Convert]::ToBase64String($publishedBytes) -cne [Convert]::ToBase64String($bytes)) {
        throw "TaskBackupReadbackMismatch: $path"
    }
    Write-Host "任务修改前原始 XML/不存在状态已保留：$path"
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '') }
    finally { $sha.Dispose() }
    return [pscustomobject]@{ Path = $path; Sha256 = $hash; TransactionId = $id;
        InstallRoot = [IO.Path]::GetFullPath($Root); MachineName = $env:COMPUTERNAME }
}

function Restore-InstalledRuntimeTasks($Reference, [string]$Root, [switch]$DeferRunningStart) {
    if ($null -eq $Reference) { throw 'TaskRestoreReferenceMissing' }
    $saved = Read-DeploymentTaskBackup $Reference.Path $Root $Reference.Sha256
    if ($saved.transactionId -cne $Reference.TransactionId -or
        $Reference.InstallRoot -ine $saved.installRoot -or
        $Reference.MachineName -ine $saved.machineName) {
        throw 'TaskRestoreReferenceMismatch'
    }

    $runningTasks = New-Object 'System.Collections.Generic.List[string]'
    foreach ($entry in @($saved.tasks)) {
        $existing = @(Get-ScheduledTask -TaskName ([string]$entry.name) -TaskPath '\' -ErrorAction SilentlyContinue)
        if ($existing.Count -gt 1) { throw "TaskRestoreIdentityAmbiguous: $($entry.name)" }
        if ($existing.Count -eq 1) {
            Stop-ScheduledTask -TaskName ([string]$entry.name) -TaskPath '\' -ErrorAction SilentlyContinue
            Unregister-ScheduledTask -TaskName ([string]$entry.name) -TaskPath '\' -Confirm:$false -ErrorAction Stop
        }
        if (-not [bool]$entry.existed) { continue }

        Register-ScheduledTask -TaskName ([string]$entry.name) -TaskPath '\' `
            -Xml ([string]$entry.xml) -Force -ErrorAction Stop | Out-Null
        $restoredXml = [string](Export-ScheduledTask -TaskName ([string]$entry.name) -TaskPath '\' -ErrorAction Stop)
        if ((Get-NormalizedScheduledTaskXml $restoredXml) -cne
            (Get-NormalizedScheduledTaskXml ([string]$entry.xml))) {
            throw "TaskRestoreXmlMismatch: $($entry.name)"
        }
        $expectedDescriptor = [Security.AccessControl.RawSecurityDescriptor]::new(
            [string]$entry.securityDescriptor)
        $actualSddl = Get-InstalledTaskSecurityDescriptor ([string]$entry.name)
        $actualDescriptor = [Security.AccessControl.RawSecurityDescriptor]::new($actualSddl)
        if ($actualDescriptor.GetSddlForm(14) -cne $expectedDescriptor.GetSddlForm(14)) {
            Set-InstalledTaskSecurityDescriptor ([string]$entry.name) ([string]$entry.securityDescriptor)
            $actualSddl = Get-InstalledTaskSecurityDescriptor ([string]$entry.name)
            $actualDescriptor = [Security.AccessControl.RawSecurityDescriptor]::new($actualSddl)
        }
        if ($actualDescriptor.GetSddlForm(14) -cne $expectedDescriptor.GetSddlForm(14)) {
            throw "TaskRestoreSecurityDescriptorMismatch: $($entry.name)"
        }
        if ([bool]$entry.wasRunning) {
            if ($DeferRunningStart) { $runningTasks.Add([string]$entry.name) }
            else { Start-ScheduledTask -TaskName ([string]$entry.name) -TaskPath '\' -ErrorAction Stop }
        }
    }
    return $runningTasks.ToArray()
}

function Start-RestoredRuntimeTasks([string[]]$Names) {
    foreach ($name in @($Names)) {
        if ([string]::IsNullOrWhiteSpace($name) -or
            $name -cnotin @($autoStartTaskName, $taskName, $healthTaskName)) {
            throw 'RestoredTaskStartIdentityInvalid'
        }
        Start-ScheduledTask -TaskName $name -TaskPath '\' -ErrorAction Stop
    }
}

function Write-DeploymentTaskPreparation(
    $Reference,
    [string]$Root,
    [bool]$ServiceExistedBefore = $false,
    [bool]$ServiceWasRunning = $false,
    [string]$Operation = 'Install',
    $AclSnapshot = $null,
    $ServiceRegistrySnapshot = $null) {
    if ($null -eq $Reference) { throw 'TaskPreparationReferenceMissing' }
    $saved = Read-DeploymentTaskBackup $Reference.Path $Root $Reference.Sha256
    if ($saved.transactionId -cne $Reference.TransactionId -or
        $Reference.InstallRoot -ine $saved.installRoot -or $Reference.MachineName -ine $saved.machineName) {
        throw 'TaskPreparationReferenceMismatch'
    }
    $directory = Join-Path $env:ProgramData 'MTTFTestDeploymentEvidence\TaskPreparations'
    Initialize-ProtectedDeploymentDirectory $directory
    $path = Join-Path $directory ($saved.transactionId + '.json')
    $pending = $path + '.pending-' + [Guid]::NewGuid().ToString('N')
    if ($ServiceWasRunning -and -not $ServiceExistedBefore) {
        throw 'InstallTransactionServiceStateInvalid'
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes(([ordered]@{
        schema = 2; phase = 'TaskBackupPrepared'; transactionId = $saved.transactionId
        machineName = $saved.machineName; installRoot = $saved.installRoot
        backupPath = $Reference.Path; backupSha256 = $Reference.Sha256
        operation = $Operation
        serviceExistedBefore = $ServiceExistedBefore
        serviceWasRunning = $ServiceWasRunning
        serviceRegistrySnapshot = $ServiceRegistrySnapshot
        currentTransaction = $null
        shortcutTransaction = $null
        markerTransaction = $null
        configTransaction = $null
        aclSnapshot = $AclSnapshot
        preparedUtc = [DateTime]::UtcNow.ToString('O')
        updatedUtc = [DateTime]::UtcNow.ToString('O')
    } | ConvertTo-Json -Depth 4))
    $stream = [IO.File]::Open($pending, 'CreateNew', 'Write', 'None')
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
    finally { $stream.Dispose() }
    # 不覆盖同 ID 的既有准备记录；冲突文件和 pending 均保留供审计。
    [IO.File]::Move($pending, $path)
    if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) -cne [Convert]::ToBase64String($bytes)) {
        throw 'TaskPreparationReadbackMismatch'
    }
    return $path
}

function Read-DeploymentInstallTransaction([string]$Path, [string]$Root) {
    $directory = [IO.Path]::GetFullPath((Join-Path $env:ProgramData 'MTTFTestDeploymentEvidence\TaskPreparations'))
    $fullPath = [IO.Path]::GetFullPath($Path)
    if ([IO.Path]::GetDirectoryName($fullPath) -ine $directory) {
        throw 'InstallTransactionLocationMismatch'
    }
    Assert-DeploymentEvidenceDirectory $directory
    $item = Get-Item -LiteralPath $fullPath -Force -ErrorAction Stop
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'InstallTransactionFileInvalid'
    }
    $saved = Read-Utf8JsonFile $fullPath '安装事务记录'
    if ($saved.schema -ne 2 -or
        [string]$saved.transactionId -notmatch '^[a-f0-9]{32}$' -or
        [IO.Path]::GetFileName($fullPath) -cne ([string]$saved.transactionId + '.json') -or
        [string]$saved.machineName -ine $env:COMPUTERNAME -or
        [IO.Path]::GetFullPath([string]$saved.installRoot) -ine [IO.Path]::GetFullPath($Root) -or
        [string]$saved.phase -cnotin @(
            'TaskBackupPrepared', 'CurrentPublished', 'CommitAuthorized', 'Completed', 'RolledBack') -or
        $saved.serviceExistedBefore -isnot [bool] -or $saved.serviceWasRunning -isnot [bool] -or
        ([bool]$saved.serviceWasRunning -and -not [bool]$saved.serviceExistedBefore) -or
        [string]$saved.backupSha256 -notmatch '^[a-fA-F0-9]{64}$') {
        throw 'InstallTransactionIdentityInvalid'
    }
    $backupReference = [pscustomobject]@{
        Path = [string]$saved.backupPath; Sha256 = [string]$saved.backupSha256
        TransactionId = [string]$saved.transactionId; InstallRoot = [string]$saved.installRoot
        MachineName = [string]$saved.machineName
    }
    [void](Read-DeploymentTaskBackup $backupReference.Path $Root $backupReference.Sha256)
    return $saved
}

function Set-DeploymentInstallTransactionPhase(
    [string]$Path,
    [string]$Root,
    [string[]]$ExpectedPhase,
    [string]$NewPhase,
    $CurrentTransaction = $null,
    $ShortcutTransaction = $null,
    $MarkerTransaction = $null,
    $ConfigTransaction = $null) {
    $saved = Read-DeploymentInstallTransaction $Path $Root
    if ([string]$saved.phase -cnotin @($ExpectedPhase)) { throw 'InstallTransactionPhaseMismatch' }
    if ($NewPhase -cnotin @('TaskBackupPrepared', 'CurrentPublished', 'CommitAuthorized', 'Completed', 'RolledBack')) {
        throw 'InstallTransactionNewPhaseInvalid'
    }
    if ($null -ne $CurrentTransaction) {
        [void](Assert-CurrentSlotTransactionReference $CurrentTransaction $Root)
        $saved.currentTransaction = [ordered]@{
            Journal = [string]$CurrentTransaction.Journal
            TransactionId = [string]$CurrentTransaction.TransactionId
            Retired = [string]$CurrentTransaction.Retired
            OldCurrentExisted = [bool]$CurrentTransaction.OldCurrentExisted
        }
    }
    if ($null -ne $ShortcutTransaction) {
        $saved.shortcutTransaction = [ordered]@{
            PlanPath = [string]$ShortcutTransaction.PlanPath
            AllowedPaths = @($ShortcutTransaction.AllowedPaths | ForEach-Object { [IO.Path]::GetFullPath([string]$_) })
        }
    }
    if ($null -ne $MarkerTransaction) {
        $saved.markerTransaction = [ordered]@{ PlanPath = [string]$MarkerTransaction.PlanPath }
    }
    if ($null -ne $ConfigTransaction) {
        $saved.configTransaction = [ordered]@{
            Entries = @($ConfigTransaction.Entries | ForEach-Object {
                [ordered]@{ Target = [IO.Path]::GetFullPath([string]$_.Target); Sha256 = [string]$_.Sha256 }
            })
        }
    }
    $saved.phase = $NewPhase
    $saved.updatedUtc = [DateTime]::UtcNow.ToString('O')
    $pending = $Path + '.pending-' + [Guid]::NewGuid().ToString('N')
    $bytes = [Text.Encoding]::UTF8.GetBytes(($saved | ConvertTo-Json -Depth 8))
    $stream = [IO.File]::Open($pending, 'CreateNew', 'Write', 'None')
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
    finally { $stream.Dispose() }
    [IO.File]::Replace($pending, $Path, $Path + '.previous-' + [Guid]::NewGuid().ToString('N'))
    $published = Read-DeploymentInstallTransaction $Path $Root
    if ([string]$published.phase -cne $NewPhase) { throw 'InstallTransactionPhaseReadbackMismatch' }
    return $published
}

function Get-InstalledServiceRegistrySnapshot(
    [Microsoft.Win32.RegistryKey]$BaseKey = [Microsoft.Win32.Registry]::LocalMachine,
    [string]$KeyPath = ('SYSTEM\CurrentControlSet\Services\' + $serviceName)) {
    $keyPath = $KeyPath
    $key = $BaseKey.OpenSubKey($keyPath, $false)
    if ($null -eq $key) { throw 'SupervisorServiceRegistryKeyMissing' }
    try {
        $entries = @()
        foreach ($name in @('ImagePath', 'Start', 'DelayedAutoStart', 'FailureActions', 'FailureActionsOnNonCrashFailures')) {
            $exists = $name -cin @($key.GetValueNames())
            $kind = $null
            $value = $null
            if ($exists) {
                $kind = [string]$key.GetValueKind($name)
                $raw = $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                $value = if ($kind -eq 'Binary') { [Convert]::ToBase64String([byte[]]$raw) } else { $raw }
            }
            $entries += [ordered]@{ Name=$name; Existed=[bool]$exists; Kind=$kind; Value=$value }
        }
        return [pscustomobject]@{ KeyPath=$keyPath; Entries=$entries }
    }
    finally { $key.Dispose() }
}

function Restore-InstalledServiceRegistrySnapshot(
    $Snapshot,
    [Microsoft.Win32.RegistryKey]$BaseKey = [Microsoft.Win32.Registry]::LocalMachine) {
    if ($null -eq $Snapshot -or [string]$Snapshot.KeyPath -cne ('SYSTEM\CurrentControlSet\Services\' + $serviceName)) {
        throw 'SupervisorServiceSnapshotIdentityInvalid'
    }
    $expectedNames = @('ImagePath', 'Start', 'DelayedAutoStart', 'FailureActions', 'FailureActionsOnNonCrashFailures')
    $entries = @($Snapshot.Entries)
    if ($entries.Count -ne $expectedNames.Count -or @($entries.Name | Sort-Object -Unique).Count -ne $expectedNames.Count) {
        throw 'SupervisorServiceSnapshotEntriesInvalid'
    }
    $key = $BaseKey.OpenSubKey([string]$Snapshot.KeyPath, $true)
    if ($null -eq $key) { throw 'SupervisorServiceRestoreKeyMissing' }
    try {
        foreach ($entry in $entries) {
            if ([string]$entry.Name -cnotin $expectedNames -or $entry.Existed -isnot [bool]) {
                throw 'SupervisorServiceSnapshotEntryInvalid'
            }
            if (-not [bool]$entry.Existed) {
                $key.DeleteValue([string]$entry.Name, $false)
                continue
            }
            $kind = [Microsoft.Win32.RegistryValueKind][Enum]::Parse(
                [Microsoft.Win32.RegistryValueKind], [string]$entry.Kind, $false)
            if ($kind -eq [Microsoft.Win32.RegistryValueKind]::Binary) {
                $value = [Convert]::FromBase64String([string]$entry.Value)
            } elseif ($kind -eq [Microsoft.Win32.RegistryValueKind]::DWord) {
                $value = [int]$entry.Value
            } elseif ($kind -eq [Microsoft.Win32.RegistryValueKind]::QWord) {
                $value = [long]$entry.Value
            } elseif ($kind -eq [Microsoft.Win32.RegistryValueKind]::MultiString) {
                $value = [string[]]@($entry.Value)
            } else {
                $value = [string]$entry.Value
            }
            $key.SetValue([string]$entry.Name, $value, $kind)
        }
        $key.Flush()
    }
    finally { $key.Dispose() }
    $actual = Get-InstalledServiceRegistrySnapshot $BaseKey ([string]$Snapshot.KeyPath)
    if (($actual | ConvertTo-Json -Depth 6 -Compress) -cne ($Snapshot | ConvertTo-Json -Depth 6 -Compress)) {
        throw 'SupervisorServiceRestoreReadbackMismatch'
    }
}

function Get-InstalledServiceStateStrict {
    $escapedName = $serviceName.Replace("'", "''")
    $matches = @(Get-CimInstance Win32_Service -Filter "Name='$escapedName'" -ErrorAction Stop)
    if ($matches.Count -gt 1) { throw 'SupervisorServiceIdentityAmbiguous' }
    return [pscustomobject]@{
        Existed = ($matches.Count -eq 1)
        WasRunning = ($matches.Count -eq 1 -and [string]$matches[0].State -eq 'Running')
        RegistrySnapshot = if ($matches.Count -eq 1) { Get-InstalledServiceRegistrySnapshot } else { $null }
    }
}

function Stop-InstalledRuntimeTasks(
    [string]$Root,
    [bool]$ServiceExistedBefore = $false,
    [bool]$ServiceWasRunning = $false,
    [string]$Operation = 'Install',
    $AclSnapshot = $null,
    $ServiceRegistrySnapshot = $null) {
    $script:deploymentTaskBackup = $null
    $script:deploymentTaskPreparation = $null
    # 枚举失败必须中止；成功枚举但没有目标才表示任务未安装。
    $installedTasks = @(Get-ScheduledTask -ErrorAction Stop)
    $script:deploymentTaskBackup = Backup-InstalledRuntimeTasks $Root $installedTasks
    $script:deploymentTaskPreparation = Write-DeploymentTaskPreparation `
        $script:deploymentTaskBackup $Root $ServiceExistedBefore $ServiceWasRunning `
        $Operation $AclSnapshot $ServiceRegistrySnapshot
    $targets = @()
    foreach ($name in @($autoStartTaskName, $taskName, $healthTaskName)) {
        $matches = @($installedTasks | Where-Object { $_.TaskName -eq $name -and $_.TaskPath -eq '\' })
        if ($matches.Count -gt 0) {
            $targets += $name
            Disable-ScheduledTask -TaskName $name -TaskPath '\' -ErrorAction Stop | Out-Null
        }
    }
    # 先封住全部自动入口，再停止全部实例，最后等待；不能让后续任务在等待期间继续拉起。
    foreach ($name in $targets) {
        Stop-ScheduledTask -TaskName $name -TaskPath '\' -ErrorAction Stop
    }
    foreach ($name in $targets) {
        Wait-InstalledTaskStopped $name
    }
    Stop-InstalledSessionAgent $Root
    # 首次预检与任务停稳之间可能已有进程启动；不能只依赖任务实例退出。
    Assert-InstalledMainStopped $Root
    Assert-RuntimeIdle $Root
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
                if (-not $maintenanceHeld) { throw 'LegacyStopMaintenanceNotHeld' }
                . (Join-Path $PSScriptRoot 'LegacyStopEvidence.ps1')
                $migration['terminalStopEvidence'] = Get-LiveLegacyStopEvidence -Root $Root `
                    -CheckpointPath $checkpoint -Checkpoint $legacy -ArchiveRoot $archiveRoot
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

function Assert-InstalledTaskAction($Task, [string]$ExpectedExecutable, [string]$ExpectedArguments = '',
    [string]$ExpectedWorkingDirectory = '') {
    if ($Task.Settings.Enabled -ne $true -or [string]$Task.State -eq 'Disabled') {
        throw 'InstalledTaskNotEnabled'
    }
    $actions = @($Task.Actions)
    if ($actions.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$actions[0].Execute)) {
        throw 'InstalledTaskActionInvalid'
    }
    $actual = [string]$actions[0].Execute
    if (-not [IO.Path]::IsPathRooted($actual) -or
        [IO.Path]::GetFullPath($actual) -ine [IO.Path]::GetFullPath($ExpectedExecutable) -or
        ([string]$actions[0].Arguments).Trim() -cne $ExpectedArguments) {
        throw 'InstalledTaskActionMismatch'
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedWorkingDirectory)) {
        $workingDirectory = [string]$actions[0].WorkingDirectory
        if ([string]::IsNullOrWhiteSpace($workingDirectory) -or
            -not [IO.Path]::IsPathRooted($workingDirectory) -or
            [IO.Path]::GetFullPath($workingDirectory).TrimEnd('\') -ine
                [IO.Path]::GetFullPath($ExpectedWorkingDirectory).TrimEnd('\')) {
            throw 'InstalledTaskWorkingDirectoryMismatch'
        }
    }
}

function Assert-Health([string]$Root) {
    $current = Join-Path $Root 'Current'
    Assert-RequiredProgramFiles $current
    $service = Get-Service -Name $serviceName -ErrorAction Stop
    if ($service.Status -ne 'Running') { throw "监督服务未运行：$($service.Status)" }
    $serviceConfiguration = @(Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction Stop)
    if ($serviceConfiguration.Count -ne 1) { throw 'InstalledSupervisorConfigurationMissing' }
    $expectedServicePath = Join-Path $current 'MTTFTest.Watchdog.exe'
    $configuredServicePath = ([string]$serviceConfiguration[0].PathName).Trim()
    if ($configuredServicePath -ine $expectedServicePath -and
        $configuredServicePath -ine ('"' + $expectedServicePath + '"')) {
        throw 'InstalledSupervisorPathMismatch'
    }
    $supervisorPid = [int]$serviceConfiguration[0].ProcessId
    if ($supervisorPid -le 0 -or [string]$serviceConfiguration[0].State -ne 'Running') {
        throw 'InstalledSupervisorProcessMissing'
    }
    $supervisorProcess = Get-Process -Id $supervisorPid -ErrorAction Stop
    try {
        [void]$supervisorProcess.Handle
        if ($supervisorProcess.HasExited -or
            [string]$supervisorProcess.Path -ine $expectedServicePath) {
            throw 'InstalledSupervisorProcessMismatch'
        }
        $recheckedService = @(Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction Stop)
        if ($recheckedService.Count -ne 1 -or
            [int]$recheckedService[0].ProcessId -ne $supervisorPid -or
            [string]$recheckedService[0].State -ne 'Running' -or
            ([string]$recheckedService[0].PathName).Trim() -ine $configuredServicePath -or
            $supervisorProcess.HasExited) {
            throw 'InstalledSupervisorProcessChanged'
        }
    }
    finally { $supervisorProcess.Dispose() }
    $task = Get-ScheduledTask -TaskName $taskName -TaskPath '\' -ErrorAction Stop
    Assert-InstalledTaskAction $task (Join-Path $current 'MTTFTest.SessionAgent.exe')
    if ($task.Settings.RestartCount -lt 1) {
        throw 'SessionAgent 登录任务缺少崩溃自动重启策略。'
    }
    $keepaliveTriggers = @($task.Triggers | Where-Object {
        $null -ne $_.Repetition -and $_.Repetition.Interval -eq 'PT1M'
    })
    if ($task.Triggers.Count -lt 2 -or $keepaliveTriggers.Count -ne 1) {
        throw 'SessionAgent 登录任务缺少每分钟存活触发器。'
    }
    $autoTask = Get-ScheduledTask -TaskName $autoStartTaskName -TaskPath '\' -ErrorAction Stop
    Assert-InstalledTaskAction $autoTask (Join-Path $current 'MTTFTest.Watchdog.exe') '--launch-main' $current
    $healthTask = Get-ScheduledTask -TaskName $healthTaskName -TaskPath '\' -ErrorAction Stop
    $healthScript = Join-Path $current 'Deployment\Test-MTTFTest-RecoveryHealth.ps1'
    if (-not (Test-Path -LiteralPath $healthScript -PathType Leaf)) { throw 'InstalledHealthScriptMissing' }
    Assert-InstalledTaskAction $healthTask (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') `
        "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$healthScript`" -InstallRoot `"$Root`""
    $healthKeepalive = @($healthTask.Triggers | Where-Object {
        $null -ne $_.Repetition -and $_.Repetition.Interval -eq 'PT1M'
    })
    if ($healthTask.Triggers.Count -lt 2 -or $healthKeepalive.Count -ne 1) {
        throw 'InstalledHealthKeepaliveMissing'
    }
    $runtimeConfig = Join-Path (Join-Path $env:ProgramData 'MTTFTest') 'Config'
    foreach ($name in $runtimeConfigNames) {
        if (-not (Test-Path -LiteralPath (Join-Path $runtimeConfig $name) -PathType Leaf)) {
            throw "运行配置缺失：$name"
        }
    }
}

function Write-ConfiguredMarker([string]$Root, [string]$InstallTransactionPath = '') {
    $path = Join-Path (Join-Path $Root 'Current') $configuredMarkerName
    $stagedPath = $path + '.pending-' + [Guid]::NewGuid().ToString('N')
    $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes(
        "ConfiguredUtc=$([DateTime]::UtcNow.ToString('O'))`r`n")
    $stream = [IO.File]::Open($stagedPath, [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
    finally { $stream.Dispose() }
    $transactionId = [Guid]::NewGuid().ToString('N')
    $transactionDirectory = Join-Path $Root ('DeploymentMarkerTransactions\' + $transactionId)
    [void](New-Item -ItemType Directory -Path $transactionDirectory)
    $originalExisted = Test-Path -LiteralPath $path -PathType Leaf
    $originalPath = $null
    $originalSha256 = $null
    if ($originalExisted) {
        $originalPath = Join-Path $transactionDirectory 'original.marker'
        [IO.File]::Copy($path, $originalPath, $false)
        $originalSha256 = (Get-FileHash -LiteralPath $originalPath -Algorithm SHA256).Hash
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $originalSha256) {
            throw 'ConfiguredMarkerBackupChanged'
        }
    } elseif (Test-Path -LiteralPath $path) { throw 'ConfiguredMarkerTargetInvalid' }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $publishedSha256 = [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '') }
    finally { $sha.Dispose() }
    $planPath = Join-Path $transactionDirectory 'plan.json'
    $planBytes = [Text.Encoding]::UTF8.GetBytes(([ordered]@{
        schema=1; transactionId=$transactionId; installationRoot=[IO.Path]::GetFullPath($Root)
        targetPath=[IO.Path]::GetFullPath($path); originalExisted=[bool]$originalExisted
        originalPath=$originalPath; originalSha256=$originalSha256; publishedSha256=$publishedSha256
        preparedUtc=[DateTime]::UtcNow.ToString('O')
    } | ConvertTo-Json -Depth 4))
    $planStream = [IO.File]::Open($planPath, 'CreateNew', 'Write', 'None')
    try { $planStream.Write($planBytes, 0, $planBytes.Length); $planStream.Flush($true) }
    finally { $planStream.Dispose() }
    $markerTransaction = [pscustomobject]@{ PlanPath = $planPath }
    if (-not [string]::IsNullOrWhiteSpace($InstallTransactionPath)) {
        $installTransaction = Read-DeploymentInstallTransaction $InstallTransactionPath $Root
        [void](Set-DeploymentInstallTransactionPhase $InstallTransactionPath $Root `
            @([string]$installTransaction.phase) ([string]$installTransaction.phase) `
            $null $null $markerTransaction)
    }
    # 发布失败保留临时文件用于审计，绝不退化为截断/覆盖原标记。
    if ([IO.File]::Exists($path)) {
        [IO.File]::Replace($stagedPath, $path, $path + '.previous-' + [Guid]::NewGuid().ToString('N'))
    } else {
        [IO.File]::Move($stagedPath, $path)
    }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $publishedSha256) {
        throw 'ConfiguredMarkerPublishedHashMismatch'
    }
    return $markerTransaction
}

function Restore-ConfiguredMarkerTransaction([string]$Root, $Reference) {
    if ($null -eq $Reference -or [string]::IsNullOrWhiteSpace([string]$Reference.PlanPath)) {
        throw 'ConfiguredMarkerRollbackReferenceMissing'
    }
    $resolvedRoot = [IO.Path]::GetFullPath($Root)
    $planPath = [IO.Path]::GetFullPath([string]$Reference.PlanPath)
    $plan = Read-Utf8JsonFile $planPath '配置标记事务'
    $expectedPlan = Join-Path $resolvedRoot ('DeploymentMarkerTransactions\' + [string]$plan.transactionId + '\plan.json')
    $expectedTarget = Join-Path (Join-Path $resolvedRoot 'Current') $configuredMarkerName
    if ($plan.schema -ne 1 -or [string]$plan.transactionId -notmatch '^[a-f0-9]{32}$' -or
        $planPath -ine $expectedPlan -or [IO.Path]::GetFullPath([string]$plan.installationRoot) -ine $resolvedRoot -or
        [IO.Path]::GetFullPath([string]$plan.targetPath) -ine $expectedTarget -or
        $plan.originalExisted -isnot [bool] -or [string]$plan.publishedSha256 -notmatch '^[A-Fa-f0-9]{64}$') {
        throw 'ConfiguredMarkerRollbackPlanInvalid'
    }
    $currentHash = if (Test-Path -LiteralPath $expectedTarget -PathType Leaf) {
        (Get-FileHash -LiteralPath $expectedTarget -Algorithm SHA256).Hash
    } elseif (Test-Path -LiteralPath $expectedTarget) { throw 'ConfiguredMarkerRollbackTargetInvalid' } else { $null }
    if ([bool]$plan.originalExisted) {
        $originalPath = [IO.Path]::GetFullPath([string]$plan.originalPath)
        if ([IO.Path]::GetDirectoryName($originalPath) -ine [IO.Path]::GetDirectoryName($planPath) -or
            [IO.Path]::GetFileName($originalPath) -cne 'original.marker' -or
            [string]$plan.originalSha256 -notmatch '^[A-Fa-f0-9]{64}$' -or
            -not (Test-Path -LiteralPath $originalPath -PathType Leaf) -or
            (Get-FileHash -LiteralPath $originalPath -Algorithm SHA256).Hash -cne [string]$plan.originalSha256) {
            throw 'ConfiguredMarkerRollbackBackupInvalid'
        }
        if ($currentHash -ceq [string]$plan.originalSha256) { return }
        if ($currentHash -cne [string]$plan.publishedSha256) { throw 'ConfiguredMarkerRollbackConflict' }
        $staged = $expectedTarget + '.restore-' + [Guid]::NewGuid().ToString('N')
        $quarantine = $expectedTarget + '.rollback-' + [Guid]::NewGuid().ToString('N')
        [IO.File]::Copy($originalPath, $staged, $false)
        [IO.File]::Replace($staged, $expectedTarget, $quarantine)
        if ((Get-FileHash -LiteralPath $expectedTarget -Algorithm SHA256).Hash -cne [string]$plan.originalSha256) {
            throw 'ConfiguredMarkerRestoreVerificationFailed'
        }
    }
    else {
        if ($null -eq $currentHash) { return }
        if ($null -ne $plan.originalPath -or $null -ne $plan.originalSha256 -or
            $currentHash -cne [string]$plan.publishedSha256) { throw 'ConfiguredMarkerRollbackConflict' }
        $quarantine = $expectedTarget + '.rollback-' + [Guid]::NewGuid().ToString('N')
        [IO.File]::Move($expectedTarget, $quarantine)
    }
}

function Get-ShortcutPaths([string]$Name = $shortcutName) {
    if ($Name -eq $shortcutName -and -not [string]::IsNullOrWhiteSpace($root)) {
        $installedExe = Join-Path $root 'Current\MTTFTest.exe'
        if (Test-Path -LiteralPath $installedExe -PathType Leaf) {
            $installedVersion = (Get-Item -LiteralPath $installedExe).VersionInfo.FileVersion
            if (-not [string]::IsNullOrWhiteSpace($installedVersion)) {
                $Name = "MT EPB 试验系统 V$installedVersion.lnk"
            }
        }
    }
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

function Test-ShortcutOwnedByInstallation([string]$Path, [string]$Root, $Shell) {
    $link = $Shell.CreateShortcut($Path)
    $current = [IO.Path]::GetFullPath((Join-Path $Root 'Current'))
    $main = Join-Path $current 'MTTFTest.exe'
    if ([string]::Equals($link.TargetPath, $main, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    # A shared launcher path alone is not ownership: it may launch another Main
    # or perform a different operation. Accept only our exact generated contract.
    return [string]::Equals($link.TargetPath, (Join-Path $current 'MTTFTest.Watchdog.exe'), [StringComparison]::OrdinalIgnoreCase) -and
        [string]::Equals($link.Arguments, "--launch-main --main-executable `"$main`"", [StringComparison]::OrdinalIgnoreCase)
}

function Backup-ShortcutBeforeChange([string]$Path, [string]$Root, [string]$TransactionId = '') {
    $originalExists = Test-Path -LiteralPath $Path -PathType Leaf
    if ((Test-Path -LiteralPath $Path) -and -not $originalExists) { throw "ShortcutPathNotFile:$Path" }
    $source = [IO.Path]::GetFullPath($Path)
    $directory = Join-Path $Root ('DeploymentShortcutBackups\' + [Guid]::NewGuid().ToString('N'))
    [void](New-Item -ItemType Directory -Path $directory)
    $backup = Join-Path $directory 'original.lnk'
    $hash = $null
    if ($originalExists) {
        $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
        Copy-Item -LiteralPath $source -Destination $backup
        if ((Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash -cne $hash -or
            (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -cne $hash) {
            throw "ShortcutBackupChanged:$source"
        }
    }
    # Preserve the entire link, including arguments, icon, working directory,
    # elevation flags and fields that this installer does not understand.
    $record = [ordered]@{ schemaVersion=2; originalPath=$source; originalExists=[bool]$originalExists;
        transactionId=$TransactionId;
        backupFile=$(if ($originalExists) { 'original.lnk' } else { $null });
        sha256=$hash; capturedUtc=[DateTime]::UtcNow.ToString('O'); installationRoot=[IO.Path]::GetFullPath($Root) }
    $manifest = Join-Path $directory 'shortcut-backup.json'
    $record | ConvertTo-Json | Set-Content -LiteralPath $manifest -Encoding UTF8
    $durableFiles = @($manifest)
    if ($originalExists) { $durableFiles += $backup }
    foreach ($file in $durableFiles) {
        $stream = [IO.File]::Open($file, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::Read)
        try { $stream.Flush($true) } finally { $stream.Dispose() }
    }
    $verified = Get-Content -LiteralPath $manifest -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($verified.originalPath -cne $source -or $verified.sha256 -cne $hash -or
        $verified.originalExists -ne $originalExists) { throw 'ShortcutBackupManifestInvalid' }
    Write-Host "快捷方式原文件备份：$manifest"
    return $manifest
}

function Assert-ShortcutUnchangedSinceBackup([string]$Path, [string]$Manifest) {
    $record = Get-Content -LiteralPath $Manifest -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($record.schemaVersion -ne 2 -or $record.originalPath -cne [IO.Path]::GetFullPath($Path)) {
        throw 'ShortcutBackupIdentityMismatch'
    }
    if ($record.originalExists) {
        if (-not (Test-Path -LiteralPath $Path -PathType Leaf) -or
            (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -cne $record.sha256) {
            throw "ShortcutChangedAfterBackup:$Path"
        }
    } elseif (Test-Path -LiteralPath $Path) {
        throw "ShortcutAppearedAfterBackup:$Path"
    }
}

function Write-PreparedShortcutReceipt([string]$Path, [string]$StagedPath, [string]$Manifest) {
    $record = Get-Content -LiteralPath $Manifest -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($record.schemaVersion -ne 2 -or $record.originalPath -cne [IO.Path]::GetFullPath($Path) -or
        $record.transactionId -notmatch '^[a-f0-9]{32}$') { throw 'ShortcutPreparedIdentityInvalid' }
    $hash = (Get-FileHash -LiteralPath $StagedPath -Algorithm SHA256).Hash
    $receipt = [ordered]@{ schemaVersion=1; state='Prepared'; path=$record.originalPath;
        transactionId=$record.transactionId; sha256=$hash; capturedUtc=[DateTime]::UtcNow.ToString('O') }
    $receiptPath = Join-Path (Split-Path -Parent $Manifest) 'prepared-link.json'
    $bytes = [Text.Encoding]::UTF8.GetBytes(($receipt | ConvertTo-Json))
    $stream = [IO.File]::Open($receiptPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    $verified = Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($verified.path -cne $record.originalPath -or $verified.transactionId -cne $record.transactionId -or
        $verified.sha256 -cne $hash -or (Get-FileHash -LiteralPath $StagedPath -Algorithm SHA256).Hash -cne $hash) {
        throw 'ShortcutPreparedReceiptInvalid'
    }
}

function Write-CreatedShortcutReceipt([string]$Path, [string]$Manifest) {
    $record = Get-Content -LiteralPath $Manifest -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($record.originalPath -cne [IO.Path]::GetFullPath($Path)) { throw 'ShortcutReceiptPathMismatch' }
    $linkStream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::Read)
    try { $linkStream.Flush($true) } finally { $linkStream.Dispose() }
    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    $receipt = [ordered]@{ schemaVersion=1; path=$record.originalPath; sha256=$hash;
        capturedUtc=[DateTime]::UtcNow.ToString('O') }
    $receiptPath = Join-Path (Split-Path -Parent $Manifest) 'created-link.json'
    $bytes = [Text.Encoding]::UTF8.GetBytes(($receipt | ConvertTo-Json))
    $stream = [IO.File]::Open($receiptPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    $verified = Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($verified.path -cne $record.originalPath -or $verified.sha256 -cne $hash -or
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -cne $hash) { throw 'ShortcutCreatedReceiptInvalid' }
}

function Write-ShortcutTransactionPlan([string]$Root, [string]$TransactionId, $Records) {
    $directory = Join-Path $Root ('DeploymentShortcutTransactions\' + $TransactionId)
    [void](New-Item -ItemType Directory -Path $directory)
    $plan = [ordered]@{ schemaVersion=1; transactionId=$TransactionId;
        installationRoot=[IO.Path]::GetFullPath($Root); preparedUtc=[DateTime]::UtcNow.ToString('O');
        account=[Security.Principal.WindowsIdentity]::GetCurrent().Name; state='Prepared'; records=@($Records) }
    $path = Join-Path $directory 'plan.json'
    $bytes = [Text.Encoding]::UTF8.GetBytes(($plan | ConvertTo-Json -Depth 6))
    $stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    $verified = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($verified.transactionId -cne $TransactionId -or @($verified.records).Count -ne @($Records).Count) {
        throw 'ShortcutTransactionPlanInvalid'
    }
    Write-Host "快捷方式事务计划：$path"
    return $path
}

function Complete-ShortcutTransaction([string]$Root, [string]$PlanPath) {
    $planBytes = [IO.File]::ReadAllBytes($PlanPath)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $planHash = [BitConverter]::ToString($sha.ComputeHash($planBytes)).Replace('-', '') }
    finally { $sha.Dispose() }
    $plan = [Text.Encoding]::UTF8.GetString($planBytes).TrimStart([char]0xFEFF) | ConvertFrom-Json
    if ($plan.schemaVersion -ne 1 -or $plan.installationRoot -cne [IO.Path]::GetFullPath($Root) -or
        $plan.state -cne 'Prepared') { throw 'ShortcutCompletionPlanInvalid' }
    foreach ($item in $plan.records) {
        $original = Get-Content -LiteralPath $item.manifest -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($original.schemaVersion -ne 2 -or $original.originalExists -isnot [bool] -or
            $original.installationRoot -cne $plan.installationRoot -or
            $original.transactionId -cne $plan.transactionId -or $original.originalPath -cne $item.path) {
            throw 'ShortcutCompletionBindingInvalid'
        }
        if ($original.originalExists) {
            $backupPath = Join-Path (Split-Path -Parent $item.manifest) 'original.lnk'
            if ($original.backupFile -cne 'original.lnk' -or $original.sha256 -notmatch '^[A-Fa-f0-9]{64}$' -or
                -not (Test-Path -LiteralPath $backupPath -PathType Leaf) -or
                (Get-FileHash -LiteralPath $backupPath -Algorithm SHA256).Hash -cne $original.sha256) {
                throw "ShortcutRollbackBackupInvalid:$($item.path)"
            }
        } elseif ($null -ne $original.backupFile -or $null -ne $original.sha256) {
            throw 'ShortcutAbsenceEvidenceInvalid'
        }
        if ($item.action -ceq 'Retire') {
            if (Test-Path -LiteralPath $item.path) { throw "ShortcutRetirementIncomplete:$($item.path)" }
        } elseif ($item.action -ceq 'Publish') {
            $created = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $item.manifest) 'created-link.json') -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($created.path -cne $item.path -or -not (Test-Path -LiteralPath $item.path -PathType Leaf) -or
                (Get-FileHash -LiteralPath $item.path -Algorithm SHA256).Hash -cne $created.sha256) {
                throw "ShortcutPublicationChanged:$($item.path)"
            }
        } else { throw 'ShortcutCompletionActionInvalid' }
    }
    if ((Get-FileHash -LiteralPath $PlanPath -Algorithm SHA256).Hash -cne $planHash) { throw 'ShortcutPlanChangedDuringCompletion' }
    $completion = [ordered]@{ schemaVersion=1; transactionId=$plan.transactionId;
        state='ShortcutsPublished'; planSha256=$planHash;
        completedUtc=[DateTime]::UtcNow.ToString('O'); recordCount=@($plan.records).Count }
    $path = Join-Path (Split-Path -Parent $PlanPath) 'published.json'
    $bytes = [Text.Encoding]::UTF8.GetBytes(($completion | ConvertTo-Json))
    $stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    $verified = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($verified.transactionId -cne $plan.transactionId -or $verified.planSha256 -cne $completion.planSha256 -or
        $verified.state -cne 'ShortcutsPublished') { throw 'ShortcutCompletionReceiptInvalid' }
}

function Restore-ShortcutTransaction([string]$Root, [string]$PlanPath, [string[]]$AllowedPaths) {
    if (-not $AllowedPaths -or $AllowedPaths.Count -eq 0) { throw 'ShortcutRollbackAllowedPathsMissing' }
    $allowed = @($AllowedPaths | ForEach-Object { [IO.Path]::GetFullPath($_) })
    $installationRoot = [IO.Path]::GetFullPath($Root)
    $plan = Get-Content -LiteralPath $PlanPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($plan.schemaVersion -ne 1 -or $plan.installationRoot -cne $installationRoot -or
        $plan.transactionId -notmatch '^[a-f0-9]{32}$') { throw 'ShortcutRollbackPlanInvalid' }
    $expectedPlan = Join-Path $installationRoot ('DeploymentShortcutTransactions\' + $plan.transactionId + '\plan.json')
    if ([IO.Path]::GetFullPath($PlanPath) -ine $expectedPlan) { throw 'ShortcutRollbackPlanLocationInvalid' }
    $backupRoot = [IO.Path]::GetFullPath((Join-Path $installationRoot 'DeploymentShortcutBackups')) + [IO.Path]::DirectorySeparatorChar
    $actions = @()
    $seen = @{}
    foreach ($item in $plan.records) {
        $target = [IO.Path]::GetFullPath($item.path)
        if ($target -notin $allowed -or [IO.Path]::GetExtension($target) -ine '.lnk' -or $seen.ContainsKey($target)) {
            throw 'ShortcutRollbackTargetNotAllowed'
        }
        $seen[$target] = $true
        $manifest = [IO.Path]::GetFullPath($item.manifest)
        if (-not $manifest.StartsWith($backupRoot, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($manifest) -cne 'shortcut-backup.json') { throw 'ShortcutRollbackManifestLocationInvalid' }
        $original = Get-Content -LiteralPath $manifest -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($original.schemaVersion -ne 2 -or $original.originalExists -isnot [bool] -or
            $original.transactionId -cne $plan.transactionId -or $original.originalPath -cne $target -or
            $original.installationRoot -cne $installationRoot -or $item.action -cnotin @('Publish', 'Retire')) {
            throw 'ShortcutRollbackBindingInvalid'
        }
        $backup = Join-Path (Split-Path -Parent $manifest) 'original.lnk'
        if ($original.originalExists) {
            if ($original.backupFile -cne 'original.lnk' -or -not (Test-Path -LiteralPath $backup -PathType Leaf) -or
                (Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash -cne $original.sha256) { throw 'ShortcutRollbackBackupInvalid' }
        } elseif ($item.action -ceq 'Retire' -or $null -ne $original.backupFile -or $null -ne $original.sha256) {
            throw 'ShortcutRollbackAbsenceInvalid'
        }
        $currentHash = $null
        if (Test-Path -LiteralPath $target) {
            if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { throw 'ShortcutRollbackTargetNotFile' }
            $currentHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
        }
        if (($original.originalExists -and $currentHash -ceq $original.sha256) -or
            (-not $original.originalExists -and $null -eq $currentHash)) { continue }
        if ($item.action -ceq 'Retire' -and $null -eq $currentHash) {
            $actions += @{ target=$target; backup=$backup; restore=$true; expected=$null; originalHash=$original.sha256 }
            continue
        }
        $receiptPath = Join-Path (Split-Path -Parent $manifest) 'created-link.json'
        $usingPrepared = -not (Test-Path -LiteralPath $receiptPath -PathType Leaf)
        if ($usingPrepared) { $receiptPath = Join-Path (Split-Path -Parent $manifest) 'prepared-link.json' }
        if ($item.action -cne 'Publish' -or -not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) { throw 'ShortcutRollbackStateConflict' }
        $created = Get-Content -LiteralPath $receiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($created.schemaVersion -ne 1 -or
            ($usingPrepared -and ($created.state -cne 'Prepared' -or $created.transactionId -cne $plan.transactionId)) -or
            $created.path -cne $target -or $null -eq $currentHash -or $currentHash -cne $created.sha256) {
            throw 'ShortcutRollbackStateConflict'
        }
        $actions += @{ target=$target; backup=$backup; restore=[bool]$original.originalExists;
            expected=$currentHash; originalHash=$original.sha256 }
    }
    # Preflight the entire group before the first mutation. Never delete evidence.
    foreach ($action in $actions) {
        $target = $action.target
        $actual = if (Test-Path -LiteralPath $target -PathType Leaf) { (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash } else { $null }
        if ($actual -cne $action.expected) { throw 'ShortcutRollbackChangedAfterPreflight' }
        $quarantine = Join-Path (Split-Path -Parent $target) ('.epb-shortcut-rollback-' + [Guid]::NewGuid().ToString('N') + '.lnk')
        if ($action.restore) {
            $staged = Join-Path (Split-Path -Parent $target) ('.epb-shortcut-restore-' + [Guid]::NewGuid().ToString('N') + '.lnk')
            [IO.File]::Copy($action.backup, $staged, $false)
            if ((Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash -cne $action.originalHash) { throw 'ShortcutRestoreStageInvalid' }
            if ($null -eq $action.expected) { [IO.File]::Move($staged, $target) }
            else { [IO.File]::Replace($staged, $target, $quarantine) }
        } else { [IO.File]::Move($target, $quarantine) }
        if (Test-Path -LiteralPath $quarantine) {
            Write-Host "快捷方式回退保留文件：$quarantine"
            if ((Get-FileHash -LiteralPath $quarantine -Algorithm SHA256).Hash -cne $action.expected) { throw 'ShortcutRollbackPublicationConflict' }
        }
        if ($action.restore -and (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -cne $action.originalHash) { throw 'ShortcutRestoreVerificationFailed' }
    }
}

function Install-Shortcuts([string]$Root, [string]$InstallTransactionPath = '') {
    $current = Join-Path $Root 'Current'
    $target = Join-Path $current 'MTTFTest.exe'
    $launcher = Join-Path $current 'MTTFTest.Watchdog.exe'
    if (-not (Test-Path -LiteralPath $target -PathType Leaf) -or
        -not (Test-Path -LiteralPath $launcher -PathType Leaf)) {
        throw "快捷方式主程序或 Supervisor 启动器不存在：$current"
    }
    $targetVersion = (Get-Item -LiteralPath $target).VersionInfo.FileVersion
    if ([string]::IsNullOrWhiteSpace($targetVersion)) { $targetVersion = '未知版本' }
    $shell = New-Object -ComObject WScript.Shell
    $shortcutManifests = @{}
    $transactionId = [Guid]::NewGuid().ToString('N')
    $transactionRecords = @()
    $destinationPaths = @(Get-ShortcutPaths)
    $legacyPaths = @()
    # A matching display name alone does not establish ownership.
    foreach ($path in $destinationPaths) {
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            if (-not (Test-ShortcutOwnedByInstallation $path $Root $shell)) {
                throw "ShortcutNameOwnedByAnotherTarget:$path"
            }
        }
        $shortcutManifests[$path] = Backup-ShortcutBeforeChange $path $Root $transactionId
        $transactionRecords += [ordered]@{ path=[IO.Path]::GetFullPath($path); action='Publish'; manifest=$shortcutManifests[$path] }
    }
    foreach ($legacy in @(@(Get-ShortcutPaths 'MT EPB 试验系统 V2.14.lnk') + @(Get-ShortcutPaths 'MT EPB 试验系统 V2.17.lnk'))) {
        if ($shortcutManifests.ContainsKey($legacy)) { continue }
        if ((Test-Path -LiteralPath $legacy -PathType Leaf) -and
            (Test-ShortcutOwnedByInstallation $legacy $Root $shell)) {
            $shortcutManifests[$legacy] = Backup-ShortcutBeforeChange $legacy $Root $transactionId
            $legacyPaths += $legacy
            $transactionRecords += [ordered]@{ path=[IO.Path]::GetFullPath($legacy); action='Retire'; manifest=$shortcutManifests[$legacy] }
        }
    }
    $planPath = Write-ShortcutTransactionPlan $Root $transactionId $transactionRecords
    $shortcutTransaction = [pscustomobject]@{
        PlanPath = $planPath
        AllowedPaths = @($transactionRecords | ForEach-Object { $_.path })
    }
    if (-not [string]::IsNullOrWhiteSpace($InstallTransactionPath)) {
        $installTransaction = Read-DeploymentInstallTransaction $InstallTransactionPath $Root
        [void](Set-DeploymentInstallTransactionPhase $InstallTransactionPath $Root `
            @([string]$installTransaction.phase) ([string]$installTransaction.phase) `
            $null $shortcutTransaction)
    }
    try {
    foreach ($legacy in $legacyPaths) {
        Assert-ShortcutUnchangedSinceBackup $legacy $shortcutManifests[$legacy]
        Remove-Item -LiteralPath $legacy -Force -Confirm:$false
    }
    foreach ($path in $destinationPaths) {
        $parent = Split-Path -Parent $path
        if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
            [void](New-Item -ItemType Directory -Path $parent -Force)
        }
        Assert-ShortcutUnchangedSinceBackup $path $shortcutManifests[$path]
        $stagedPath = Join-Path $parent ('.epb-shortcut-stage-' + [Guid]::NewGuid().ToString('N') + '.lnk')
        $shortcut = $shell.CreateShortcut($stagedPath)
        $shortcut.TargetPath = $launcher
        $shortcut.Arguments = "--launch-main --main-executable `"$target`""
        $shortcut.WorkingDirectory = $current
        $shortcut.IconLocation = "$target,0"
        $shortcut.Description = "MT EPB 试验系统 V$targetVersion（Supervisor 无人值守正式版）"
        $shortcut.Save()
        $shortcutBytes = [IO.File]::ReadAllBytes($stagedPath)
        if ($shortcutBytes.Length -lt 22) {
            throw "快捷方式格式无效，无法设置管理员运行标记：$path"
        }
        # Shell Link Header 的 LinkFlags 第 2 个字节置 0x20，即 SLDF_RUNAS_USER。
        $shortcutBytes[21] = $shortcutBytes[21] -bor 0x20
        [IO.File]::WriteAllBytes($stagedPath, $shortcutBytes)
        $stagedStream = [IO.File]::Open($stagedPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::Read)
        try { $stagedStream.Flush($true) } finally { $stagedStream.Dispose() }
        if (-not (Test-ShortcutOwnedByInstallation $stagedPath $Root $shell)) { throw 'StagedShortcutTargetInvalid' }
        $stagedHash = (Get-FileHash -LiteralPath $stagedPath -Algorithm SHA256).Hash
        Write-PreparedShortcutReceipt $path $stagedPath $shortcutManifests[$path]
        Assert-ShortcutUnchangedSinceBackup $path $shortcutManifests[$path]
        $original = Get-Content -LiteralPath $shortcutManifests[$path] -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($original.originalExists) {
            [IO.File]::Replace($stagedPath, $path, [NullString]::Value)
        } else {
            # Two-argument Move never replaces an entry that appeared meanwhile.
            [IO.File]::Move($stagedPath, $path)
        }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $stagedHash) { throw 'PublishedShortcutBytesChanged' }
        Write-CreatedShortcutReceipt $path $shortcutManifests[$path]
    }
    Complete-ShortcutTransaction $Root $planPath
    }
    catch {
        $publicationFailure = $_
        try {
            Restore-ShortcutTransaction $Root $planPath @($transactionRecords | ForEach-Object { $_.path })
        }
        catch {
            throw "ShortcutPublicationAndRollbackFailed: Plan=$planPath; Publication=$($publicationFailure.Exception.Message); Rollback=$($_.Exception.Message)"
        }
        throw $publicationFailure
    }
    return $shortcutTransaction
}

function Remove-Shortcuts([string]$Root) {
    $shell = New-Object -ComObject WScript.Shell
    foreach ($path in @(
            @(Get-ShortcutPaths) +
            @(Get-ShortcutPaths 'MT EPB 试验系统 V2.17.lnk') +
            @(Get-ShortcutPaths 'MT EPB 试验系统 V2.14.lnk'))) {
        if ((Test-Path -LiteralPath $path -PathType Leaf) -and
            (Test-ShortcutOwnedByInstallation $path $Root $shell)) {
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
    $pendingResult = $resultPath + '.pending-' + [Guid]::NewGuid().ToString('N')
    $resultBytes = (New-Object Text.UTF8Encoding($false)).GetBytes(($result | ConvertTo-Json -Depth 3))
    $resultStream = [IO.File]::Open($pendingResult, [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $resultStream.Write($resultBytes, 0, $resultBytes.Length); $resultStream.Flush($true) }
    finally { $resultStream.Dispose() }
    if ([IO.File]::Exists($resultPath)) {
        [IO.File]::Replace($pendingResult, $resultPath, $resultPath + '.previous-' + [Guid]::NewGuid().ToString('N'))
    } else {
        [IO.File]::Move($pendingResult, $resultPath)
    }
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

function Restore-FailedInstallTransaction(
    [string]$Root,
    $TaskBackup,
    $CurrentTransaction,
    [bool]$ServiceExistedBefore,
    [bool]$ServiceWasRunning,
    $PublishedConfigs = @(),
    $ShortcutTransaction = $null,
    $MarkerTransaction = $null,
    $AclSnapshot = $null,
    $ServiceRegistrySnapshot = $null) {
    $rollbackFailures = New-Object 'System.Collections.Generic.List[string]'
    $runningTasksToRestore = @()
    try { Stop-Supervisor }
    catch { $rollbackFailures.Add('StopSupervisor=' + $_.Exception.Message) }
    if ($null -ne $ShortcutTransaction) {
        try { Restore-ShortcutTransaction $Root ([string]$ShortcutTransaction.PlanPath) @($ShortcutTransaction.AllowedPaths) }
        catch { $rollbackFailures.Add('Shortcuts=' + $_.Exception.Message) }
    }
    if ($null -ne $MarkerTransaction) {
        try { Restore-ConfiguredMarkerTransaction $Root $MarkerTransaction }
        catch { $rollbackFailures.Add('ConfiguredMarker=' + $_.Exception.Message) }
    }
    if ($null -ne $TaskBackup) {
        try {
            $runningTasksToRestore = @(Restore-InstalledRuntimeTasks `
                $TaskBackup $Root -DeferRunningStart)
        }
        catch { $rollbackFailures.Add('Tasks=' + $_.Exception.Message) }
    }
    if ($null -ne $CurrentTransaction) {
        try { [void](Restore-CurrentSlotTransaction $CurrentTransaction $Root) }
        catch { $rollbackFailures.Add('Current=' + $_.Exception.Message) }
    }
    if (@($PublishedConfigs).Count -gt 0) {
        try { Undo-InitializedRuntimeConfigs (Join-Path $env:ProgramData 'MTTFTest\Config') $PublishedConfigs }
        catch { $rollbackFailures.Add('Config=' + $_.Exception.Message) }
    }
    if ($null -ne $AclSnapshot) {
        try { Restore-DeploymentAclSnapshot $Root $AclSnapshot }
        catch { $rollbackFailures.Add('Acl=' + $_.Exception.Message) }
    }
    if ($ServiceExistedBefore -and $null -ne $ServiceRegistrySnapshot) {
        try { Restore-InstalledServiceRegistrySnapshot $ServiceRegistrySnapshot }
        catch { $rollbackFailures.Add('ServiceConfiguration=' + $_.Exception.Message) }
    }
    if ($rollbackFailures.Count -eq 0) {
        try {
            if ($ServiceExistedBefore -and $ServiceWasRunning) {
                Start-Service -Name $serviceName -ErrorAction Stop
            }
            elseif (-not $ServiceExistedBefore -and
                $null -ne (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) {
                & sc.exe delete $serviceName | Out-Host
                if ($LASTEXITCODE -ne 0) { throw "ExitCode=$LASTEXITCODE" }
            }
        }
        catch { $rollbackFailures.Add('ServiceRunningState=' + $_.Exception.Message) }
        if ($rollbackFailures.Count -eq 0) {
            try { Start-RestoredRuntimeTasks $runningTasksToRestore }
            catch { $rollbackFailures.Add('TaskRunningState=' + $_.Exception.Message) }
        }
    }
    if ($rollbackFailures.Count -gt 0) {
        throw 'InstallRollbackFailed: ' + ($rollbackFailures -join ' | ')
    }
}

function ConvertFrom-SavedCurrentTransaction($Saved) {
    if ($null -eq $Saved) { return $null }
    return [pscustomobject]@{
        Journal = [string]$Saved.Journal
        TransactionId = [string]$Saved.TransactionId
        Retired = [string]$Saved.Retired
        OldCurrentExisted = [bool]$Saved.OldCurrentExisted
    }
}

function Resolve-PendingInstallTransaction([string]$Root) {
    $directory = Join-Path $env:ProgramData 'MTTFTestDeploymentEvidence\TaskPreparations'
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) { return }
    Assert-DeploymentEvidenceDirectory $directory
    $pending = New-Object 'System.Collections.Generic.List[object]'
    foreach ($item in @(Get-ChildItem -LiteralPath $directory -Filter '*.json' -File -Force -ErrorAction Stop)) {
        $candidate = $null
        try { $candidate = Read-Utf8JsonFile $item.FullName '安装事务候选记录' }
        catch { throw "InstallTransactionCandidateUnreadable: $($item.FullName): $($_.Exception.Message)" }
        # schema 1 是旧版仅审计记录，没有足够状态支持自动恢复。
        if ($candidate.schema -ne 2) { continue }
        if ([string]$candidate.machineName -ine $env:COMPUTERNAME -or
            [IO.Path]::GetFullPath([string]$candidate.installRoot) -ine [IO.Path]::GetFullPath($Root)) { continue }
        $saved = Read-DeploymentInstallTransaction $item.FullName $Root
        if ([string]$saved.phase -notin @('Completed', 'RolledBack')) {
            $pending.Add([pscustomobject]@{ Path = $item.FullName; Saved = $saved })
        }
    }
    if ($pending.Count -gt 1) { throw 'InstallTransactionRecoveryAmbiguous' }
    if ($pending.Count -eq 0) { return }

    $entry = $pending[0]
    $saved = $entry.Saved
    $current = ConvertFrom-SavedCurrentTransaction $saved.currentTransaction
    if ([string]$saved.phase -eq 'CommitAuthorized') {
        if ($null -ne $current) {
            if (Test-Path -LiteralPath ([string]$current.Journal) -PathType Leaf) {
                [void](Complete-CurrentSlotTransaction $current $Root)
            }
            else {
                $completed = Join-Path (Resolve-SafeDirectory $Root 'InstallRoot') `
                    ('.install-transaction-completed-' + $current.TransactionId + '.json')
                if (-not (Test-Path -LiteralPath $completed -PathType Leaf)) {
                    throw 'InstallTransactionAuthorizedCurrentEvidenceMissing'
                }
            }
        }
        [void](Set-DeploymentInstallTransactionPhase $entry.Path $Root `
            @('CommitAuthorized') 'Completed')
        Archive-MaintenanceInhibitForInstall
        Write-Warning "检测到已授权提交的中断安装事务，已完成提交：$($saved.transactionId)"
        return
    }

    if ($null -ne $current -and -not (Test-Path -LiteralPath ([string]$current.Journal) -PathType Leaf)) {
        $rolledBack = Join-Path (Resolve-SafeDirectory $Root 'InstallRoot') `
            ('.install-transaction-rolledback-' + $current.TransactionId + '.json')
        if (Test-Path -LiteralPath $rolledBack -PathType Leaf) { $current = $null }
        else { throw 'InstallTransactionRollbackCurrentEvidenceMissing' }
    }
    $backup = [pscustomobject]@{
        Path = [string]$saved.backupPath; Sha256 = [string]$saved.backupSha256
        TransactionId = [string]$saved.transactionId; InstallRoot = [string]$saved.installRoot
        MachineName = [string]$saved.machineName
    }
    Restore-FailedInstallTransaction $Root $backup $current `
        ([bool]$saved.serviceExistedBefore) ([bool]$saved.serviceWasRunning) `
        @($saved.configTransaction.Entries) $saved.shortcutTransaction `
        $saved.markerTransaction $saved.aclSnapshot $saved.serviceRegistrySnapshot
    [void](Set-DeploymentInstallTransactionPhase $entry.Path $Root `
        @('TaskBackupPrepared', 'CurrentPublished') 'RolledBack')
    Archive-MaintenanceInhibitForInstall
    Write-Warning "检测到未提交的中断安装事务，已恢复安装前状态：$($saved.transactionId)"
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
Resolve-PendingInstallTransaction $root
function Enter-DeploymentMaintenance {
    $state = Join-Path $env:ProgramData 'MTTFTest'
    [void](New-Item -ItemType Directory -Path $state -Force)
    $marker = Join-Path $state 'maintenance-inhibit.json'
    if (-not (Test-Path -LiteralPath $marker)) {
        $json = @{ Mode=$Mode; StartedUtc=[DateTime]::UtcNow.ToString('O'); InstallRoot=$root } |
            ConvertTo-Json
        $stagedMarker = $marker + '.pending-' + [Guid]::NewGuid().ToString('N')
        $bytes = (New-Object Text.UTF8Encoding($false)).GetBytes($json)
        $stream = [IO.File]::Open($stagedMarker, [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
        finally { $stream.Dispose() }
        # 不覆盖已有维护所有者；冲突或发布失败保留 pending 并停止安装。
        [IO.File]::Move($stagedMarker, $marker)
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
        Remove-Shortcuts $root
        Write-OperationStep 6 6 '删除程序安装目录。'
        Remove-InstalledProgramFiles $root
        Write-DeploymentResult 'Uninstall' $installedVersion $root
        Write-Host '卸载完成：程序、服务、计划任务和快捷方式已删除；ProgramData 配置、日志和事故证据已保留。'
    }
    return
}

$source = Resolve-SafeDirectory $SourceDirectory 'SourceDirectory'

if ($Mode -eq 'Configure') {
    if (-not $PSCmdlet.ShouldProcess($root, '配置运行环境、监督服务、任务和快捷方式')) { return }
    Assert-InstalledMainStopped $root
    Assert-RuntimeIdle $root

    $configureServiceBefore = Get-InstalledServiceStateStrict
    $configureAclSnapshot = Get-DeploymentAclSnapshot $root
    $configurePublishedConfigs = @()
    $configureShortcutTransaction = $null
    $configureMarkerTransaction = $null
    $configureCommitAuthorized = $false
    try {
        Enter-DeploymentMaintenance
        Stop-Supervisor
        Stop-InstalledRuntimeTasks $root ([bool]$configureServiceBefore.Existed) `
            ([bool]$configureServiceBefore.WasRunning) 'Configure' $configureAclSnapshot `
            $configureServiceBefore.RegistrySnapshot
        $current = Join-Path $root 'Current'
        Assert-RequiredProgramFiles $current
        $configurePublishedConfigs = @(Initialize-RuntimeConfig `
            $current $root $script:deploymentTaskPreparation)
        Set-UnattendedAcl $root
        Install-ServiceAndAgent $root
        Assert-Health $root
        $configureShortcutTransaction = Install-Shortcuts $root $script:deploymentTaskPreparation
        $configureMarkerTransaction = Write-ConfiguredMarker $root $script:deploymentTaskPreparation
        [void](Set-DeploymentInstallTransactionPhase $script:deploymentTaskPreparation $root `
            @('TaskBackupPrepared') 'CommitAuthorized')
        $configureCommitAuthorized = $true
        [void](Set-DeploymentInstallTransactionPhase $script:deploymentTaskPreparation $root `
            @('CommitAuthorized') 'Completed')
        Archive-MaintenanceInhibitForInstall
        Write-Host '首次运行环境、登录自启动和快捷方式已配置。'
    }
    catch {
        $configureFailure = $_
        if ($configureCommitAuthorized) {
            throw "ConfigureCommitRecoveryRequired: $($configureFailure.Exception.Message)"
        }
        try {
            Restore-FailedInstallTransaction $root $script:deploymentTaskBackup $null `
                ([bool]$configureServiceBefore.Existed) ([bool]$configureServiceBefore.WasRunning) `
                $configurePublishedConfigs $configureShortcutTransaction $configureMarkerTransaction `
                $configureAclSnapshot $configureServiceBefore.RegistrySnapshot
            if ($null -ne $script:deploymentTaskPreparation) {
                [void](Set-DeploymentInstallTransactionPhase $script:deploymentTaskPreparation $root `
                    @('TaskBackupPrepared') 'RolledBack')
            }
            Archive-MaintenanceInhibitForInstall
        }
        catch {
            throw "ConfigureAndRollbackFailed: Configure=$($configureFailure.Exception.Message); " +
                "Rollback=$($_.Exception.Message)"
        }
        throw $configureFailure
    }
    return
}

if ($Mode -eq 'PromoteLastKnownGood') {
    if (-not $PSCmdlet.ShouldProcess($root, '将 Current 完整包晋升为 LastKnownGood')) { return }
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
        # 回退基准必须是完整正式包；只有文件存在不足以证明副本可信。
        [void](Get-VerifiedDeploymentIdentity $staging)
        $retired = Join-Path $root ('.lkg-retired-' + [Guid]::NewGuid().ToString('N'))
        $oldLkgRetired = $false
        if (Test-Path -LiteralPath $lkg) {
            $oldLkgItem = Get-Item -LiteralPath $lkg -Force
            if (-not $oldLkgItem.PSIsContainer -or
                ($oldLkgItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
                [IO.Path]::GetDirectoryName($oldLkgItem.FullName) -ine $root) {
                throw 'LastKnownGoodOriginalTargetInvalid'
            }
            [IO.Directory]::Move($oldLkgItem.FullName, $retired)
            $oldLkgRetired = $true
        }
        $lkgPublished = $false
        try {
            [IO.Directory]::Move($staging, $lkg)
            $lkgPublished = $true
            [void](Get-VerifiedDeploymentIdentity $lkg)
        }
        catch {
            $promotionFailure = $_
            try {
                if ($lkgPublished -and (Test-Path -LiteralPath $lkg)) {
                    $failedLkg = [IO.Path]::GetFullPath((Join-Path $root ('.lkg-failed-' + [Guid]::NewGuid().ToString('N'))))
                    $lkgItem = Get-Item -LiteralPath $lkg -Force
                    if (-not $lkgItem.PSIsContainer -or
                        ($lkgItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
                        [IO.Path]::GetDirectoryName($lkgItem.FullName) -ine $root -or
                        [IO.Path]::GetDirectoryName($failedLkg) -ine $root -or
                        (Test-Path -LiteralPath $failedLkg)) { throw 'LastKnownGoodRollbackTargetInvalid' }
                    [IO.Directory]::Move($lkgItem.FullName, $failedLkg)
                    Write-Warning "晋升候选复核失败，已保留：$failedLkg"
                }
                if (Test-Path -LiteralPath $lkg) { throw 'LastKnownGoodRollbackDestinationOccupied' }
                if ($oldLkgRetired -and -not (Test-Path -LiteralPath $retired)) {
                    throw 'LastKnownGoodRetiredMissing'
                }
                if ($oldLkgRetired) {
                    $retiredItem = Get-Item -LiteralPath $retired -Force
                    if (-not $retiredItem.PSIsContainer -or
                        ($retiredItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
                        [IO.Path]::GetDirectoryName($retiredItem.FullName) -ine $root) {
                        throw 'LastKnownGoodRetiredTargetInvalid'
                    }
                    [IO.Directory]::Move($retiredItem.FullName, $lkg)
                }
            }
            catch {
                throw "LastKnownGoodPublicationAndRollbackFailed: Publication=$($promotionFailure.Exception.Message); Rollback=$($_.Exception.Message)"
            }
            throw $promotionFailure
        }
    }
    finally {
        if (Test-Path -LiteralPath $staging) {
            Write-Warning "晋升未完成；保留候选副本供核验：$staging"
        }
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
    $serviceBefore = Get-InstalledServiceStateStrict
    $aclSnapshot = Get-DeploymentAclSnapshot $root
    $serviceExistedBefore = [bool]$serviceBefore.Existed
    $serviceWasRunning = [bool]$serviceBefore.WasRunning
    $tasksPrepared = $false
    $currentTransaction = $null
    $publishedConfigs = @()
    $shortcutTransaction = $null
    $markerTransaction = $null
    $commitAuthorized = $false
    try {
        Write-OperationStep 2 9 '停止旧监督服务和运行任务。'
        Enter-DeploymentMaintenance
        Stop-Supervisor
        Stop-InstalledRuntimeTasks $root $serviceExistedBefore $serviceWasRunning $Mode `
            $aclSnapshot $serviceBefore.RegistrySnapshot
        $tasksPrepared = $true
        Write-OperationStep 3 9 '确认断能证明、封存旧 schema 5/6 检查点并仅迁移剩余圈数。'
        Invoke-LegacyCheckpointSafeRollover $root
        Write-OperationStep 4 9 '初始化并保留现场运行配置。'
        $publishedConfigs = @(Initialize-RuntimeConfig `
            $source $root $script:deploymentTaskPreparation)
        Write-OperationStep 5 9 '安装或更新程序文件。'
        if (Test-CurrentSlotReplacementRequired $source $root) {
            $currentTransaction = Install-CurrentSlot $source $root -DeferCommit
            [void](Set-DeploymentInstallTransactionPhase $script:deploymentTaskPreparation $root `
                @('TaskBackupPrepared') 'CurrentPublished' $currentTransaction)
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
        $shortcutTransaction = Install-Shortcuts $root $script:deploymentTaskPreparation
        $markerTransaction = Write-ConfiguredMarker $root $script:deploymentTaskPreparation
        Write-DeploymentResult $Mode $sourceVersion $root $source
        [void](Set-DeploymentInstallTransactionPhase $script:deploymentTaskPreparation $root `
            @('TaskBackupPrepared', 'CurrentPublished') 'CommitAuthorized')
        $commitAuthorized = $true
        if ($null -ne $currentTransaction) {
            [void](Complete-CurrentSlotTransaction $currentTransaction $root)
            $currentTransaction = $null
        }
        [void](Set-DeploymentInstallTransactionPhase $script:deploymentTaskPreparation $root `
            @('CommitAuthorized') 'Completed')
        Archive-MaintenanceInhibitForInstall
        Write-Host "V$sourceVersion 正式包已完成 $Mode；发布与现场运行状态由操作人员负责。"
    }
    catch {
        $installFailure = $_
        if ($commitAuthorized) {
            throw "InstallCommitRecoveryRequired: $($installFailure.Exception.Message)"
        }
        try {
            Restore-FailedInstallTransaction $root `
                $script:deploymentTaskBackup $currentTransaction `
                $serviceExistedBefore $serviceWasRunning $publishedConfigs `
                $shortcutTransaction $markerTransaction $aclSnapshot $serviceBefore.RegistrySnapshot
            if ($null -ne $script:deploymentTaskPreparation) {
                [void](Set-DeploymentInstallTransactionPhase $script:deploymentTaskPreparation $root `
                    @('TaskBackupPrepared', 'CurrentPublished') 'RolledBack')
            }
            Archive-MaintenanceInhibitForInstall
        }
        catch {
            throw "InstallAndRollbackFailed: Install=$($installFailure.Exception.Message); " +
                "Rollback=$($_.Exception.Message)"
        }
        throw $installFailure
    }
}
} finally {
    if ($maintenanceHeld) { $maintenanceMutex.ReleaseMutex() }
    $maintenanceMutex.Dispose()
}
