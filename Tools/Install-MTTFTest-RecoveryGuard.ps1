#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Validate', 'Install', 'Uninstall')][string]$Mode = 'Validate',
    [string]$SourceDirectory = '',
    [string]$InstallRoot = '',
    [string]$BenchId = '',
    [string]$MainExecutable = '',
    [ValidateSet('ObserveOnly', 'RecoverExited', 'RecoverStalled')][string]$RecoveryMode
)
$ErrorActionPreference = 'Stop'
$SourceDirectory = if ([string]::IsNullOrWhiteSpace($SourceDirectory)) { $PSScriptRoot } else { $SourceDirectory }
$taskName = 'MTTFTestRecoveryGuard'
$executionTaskName = 'MTTFTestRecoveryGuardExecution'
if ([string]::IsNullOrWhiteSpace($InstallRoot)) { $InstallRoot = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'MTTFTestRecoveryGuard' }
$install = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$guardState = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'MTTFTestRecoveryGuard'
$control = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'MTTFTest\RecoveryControl'
$registrationPath = Join-Path $guardState 'installation.json'

function Read-VerifiedPackage([string]$Root) {
    $identity = Get-Content -LiteralPath (Join-Path $Root 'guard-identity.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $commissioning = $identity.schemaVersion -in @(1,2) -and $identity.deliveryStage -eq 'ObserveOnlyCommissioning' -and $identity.automaticExecutionReady -eq $false
    $automatic = $identity.schemaVersion -eq 3 -and $identity.deliveryStage -eq 'AutomaticRecovery' -and
        $identity.automaticExecutionReady -eq $true -and $identity.configuration -eq 'Release' -and $identity.builtFromVerifiedInputs -eq $true -and
        $identity.gitDirty -eq $false
    if ($identity.schemaVersion -notin @(1, 2, 3) -or $identity.product -ne 'MTTFTest.RecoveryGuard' -or
        $identity.version -notmatch '^\d+\.\d+\.\d+\.\d+$' -or
        (-not $commissioning -and -not $automatic)) { throw '独立包身份或交付阶段不受支持。' }
    $required = @('MTTFTest.RecoveryGuard.exe', 'MTTFTest.RecoveryControl.dll', 'guard-settings.json', 'Install-MTTFTest-RecoveryGuard.ps1')
    if ($identity.schemaVersion -ge 2) { $required += @('MTTFTest.RecoveryGuard.pdb', 'MTTFTest.RecoveryControl.pdb', 'README.md') }
    if ($identity.supervisedCommissioningAvailable -eq $true) {
        $required += @('Invoke-MTTFTest-RecoveryGuardCommissioning.ps1','Verify-Release.ps1')
    }
    $observeRequired = @($required)
    if ($automatic) {
        $required += @('RecoveryGuard-Acceptance.ps1', 'acceptance-report.json', 'observe-base-identity.json')
        $report = Get-Content -LiteralPath (Join-Path $Root 'acceptance-report.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($relative in @($report.checks.evidencePath | Sort-Object -Unique)) {
            if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or
                $relative.Contains(':') -or $relative -match '(^|[\\/])\.\.([\\/]|$)') { throw 'AcceptanceEvidencePathInvalid' }
            $required += ('Acceptance/' + $relative.Replace('\','/'))
        }
    }
    if (@($identity.files).Count -ne $required.Count) { throw '独立包文件清单不完整。' }
    $seen = @{}
    foreach ($file in $identity.files) {
        if ($file.name -notin $required -or $seen.ContainsKey([string]$file.name)) { throw '独立包包含重复或非预期文件。' }
        $seen[[string]$file.name] = $true
        $path = Join-Path $Root $file.name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "独立包文件缺失：$($file.name)" }
        if ((Get-Item -LiteralPath $path).Length -ne $file.bytes -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) { throw "独立包文件损坏：$($file.name)" }
    }
    foreach ($name in $required[0..1]) {
        if ((Get-Item -LiteralPath (Join-Path $Root $name)).VersionInfo.FileVersion -ne $identity.version) { throw "独立组件版本不一致：$name" }
    }
    if ($automatic) {
        . (Join-Path $Root 'RecoveryGuard-Acceptance.ps1')
        [void](Assert-GuardAcceptanceReport $report 'RecoverExited' $identity.mainIdentitySha256 `
            (Get-FileHash (Join-Path $Root 'MTTFTest.RecoveryGuard.exe')).Hash `
            (Get-FileHash (Join-Path $Root 'MTTFTest.RecoveryControl.dll')).Hash `
            (Get-FileHash (Join-Path $Root 'observe-base-identity.json')).Hash)
        $observedBase=Get-Content (Join-Path $Root 'observe-base-identity.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        if($observedBase.schemaVersion -ne 2 -or $observedBase.deliveryStage -ne 'ObserveOnlyCommissioning' -or
            $observedBase.version -cne $identity.version -or
            $observedBase.gitCommit -cne $identity.gitCommit -or $observedBase.gitDirty -ne $false -or
            $observedBase.builtFromVerifiedInputs -ne $true -or $observedBase.configuration -ne 'Release' -or
            [bool]$observedBase.supervisedCommissioningAvailable -ne [bool]$identity.supervisedCommissioningAvailable -or
            @($observedBase.files).Count -ne $observeRequired.Count){throw 'AcceptanceObserveBaseInvalid'}
        $baseNames=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach($file in $observedBase.files){
            if($file.name -notin $observeRequired -or -not $baseNames.Add([string]$file.name) -or
                (Get-FileHash -LiteralPath (Join-Path $Root $file.name)).Hash -ine $file.sha256){throw 'AcceptanceObserveBaseContentMismatch'}
        }
        Assert-GuardAcceptanceEvidenceFiles $report (Join-Path $Root 'Acceptance')
        if (($report.approvedModes -join ',') -cne ($identity.approvedModes -join ',') -or
            $report.benchId -cne $identity.acceptanceBenchId -or $report.machineName -cne $identity.acceptanceMachineName) {
            throw 'AcceptanceManifestScopeMismatch'
        }
    }
    $settings = Get-Content -LiteralPath (Join-Path $Root 'guard-settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($settings.SchemaVersion -ne 1 -or $settings.Mode -ne 0 -or $settings.SupervisionExpirySeconds -ne 3600) { throw '当前独立包必须为 ObserveOnly，监督过期阈值必须为 60 分钟。' }
    $validation = @(& (Join-Path $Root 'MTTFTest.RecoveryGuard.exe') --validate-settings --settings (Join-Path $Root 'guard-settings.json'))
    if ($LASTEXITCODE -ne 0) { throw ('独立包配置校验失败：' + ($validation -join '')) }
    return $identity
}

function Assert-NoTakeover([string]$Exe) {
    if (-not (Test-Path -LiteralPath $control)) { return }
    $raw = @(& $Exe --status --root $control)
    if ($LASTEXITCODE -ne 0) { throw ('共享权威不可读，不能切换 Guard：' + ($raw -join '')) }
    $state = ($raw -join '') | ConvertFrom-Json
    if ($null -ne $state.Transaction -and -not $state.Transaction.OwnershipReleased) { throw '接管所有权尚未收口，不能安装或卸载 Guard。' }
}

function Set-ProductDirectoryAcl([string]$Path, [Security.AccessControl.FileSystemRights]$UserRights) {
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule(
            (New-Object Security.Principal.SecurityIdentifier($sid)), [Security.AccessControl.FileSystemRights]::FullControl,
            $inherit, [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow)
        $acl.AddAccessRule($rule)
    }
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
        (New-Object Security.Principal.SecurityIdentifier('S-1-5-11')), $UserRights,
        $inherit, [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow)))
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function New-GuardTaskXml([string]$Command, [string]$Directory, [string]$SettingsPath, [string]$JournalPath,
    [bool]$Execution, [bool]$Enabled) {
    foreach ($value in @($Command, $Directory, $SettingsPath, $JournalPath)) {
        if ($value.IndexOfAny([char[]]@('"', "`r", "`n")) -ge 0 -or $value.EndsWith('\') -or -not [IO.Path]::IsPathRooted($value)) {
            throw 'Guard 任务路径无效。'
        }
    }
    $verb = if ($Execution) { '--execute' } else { '--check' }
    $arguments = $verb + ' --settings "' + $SettingsPath + '" --journal "' + $JournalPath + '"'
    $limit = if ($Execution) { 'PT0S' } else { 'PT45S' }
    $hardTerminate = if ($Execution) { 'false' } else { 'true' }
    $enabledText = $Enabled.ToString().ToLowerInvariant()
    $start = (Get-Date).AddMinutes(1).ToString('yyyy-MM-ddTHH:mm:ss')
    $xmlCommand = [Security.SecurityElement]::Escape($Command)
    $xmlArguments = [Security.SecurityElement]::Escape($arguments)
    $xmlDirectory = [Security.SecurityElement]::Escape($Directory)
    # A separate recurring task starts/rejoins the persistent executor. The
    # scanner's 45-second limit never becomes a safety-action timeout.
    return @"
<Task version="1.3" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <Triggers>
    <BootTrigger><Enabled>true</Enabled></BootTrigger>
    <TimeTrigger><Repetition><Interval>PT1M</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition><StartBoundary>$start</StartBoundary><Enabled>true</Enabled></TimeTrigger>
  </Triggers>
  <Principals><Principal id="System"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>$hardTerminate</AllowHardTerminate><StartWhenAvailable>true</StartWhenAvailable><Enabled>$enabledText</Enabled><Hidden>true</Hidden><ExecutionTimeLimit>$limit</ExecutionTimeLimit></Settings>
  <Actions Context="System"><Exec><Command>$xmlCommand</Command><Arguments>$xmlArguments</Arguments><WorkingDirectory>$xmlDirectory</WorkingDirectory></Exec></Actions>
</Task>
"@
}

function Write-GuardAtomicBytes([string]$Path, [byte[]]$Bytes) {
    $temporary = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        $stream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stream.Write($Bytes, 0, $Bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
        if ([IO.File]::Exists($Path)) { [IO.File]::Replace($temporary, $Path, $temporary + '.previous') }
        else { [IO.File]::Move($temporary, $Path) }
    } finally {
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
        if ([IO.File]::Exists($temporary + '.previous')) { [IO.File]::Delete($temporary + '.previous') }
    }
}

function Invoke-GuardTaskTransaction([Collections.IDictionary]$Desired, [Collections.IDictionary]$Previous,
    [string]$RegistrationPath, [string]$NewRegistration, [string]$EvidenceDirectory, [ref]$KeepMaintenance,
    [string]$SettingsPath = '', [byte[]]$NewSettingsBytes = $null) {
    $allowed = @('MTTFTestRecoveryGuardExecution', 'MTTFTestRecoveryGuard')
    if ($Desired.Count -ne 2 -or @($Desired.Keys | Where-Object { $_ -notin $allowed }).Count -ne 0 -or
        @($Previous.Keys | Where-Object { $_ -notin $allowed }).Count -ne 0) { throw 'Guard 任务事务目标无效。' }
    $hadRegistration = [IO.File]::Exists($RegistrationPath)
    [byte[]]$oldBytes = @()
    if ($hadRegistration) { $oldBytes = [IO.File]::ReadAllBytes($RegistrationPath) }
    $changeSettings = -not [string]::IsNullOrEmpty($SettingsPath)
    $hadSettings = $changeSettings -and [IO.File]::Exists($SettingsPath)
    [byte[]]$oldSettingsBytes = @()
    if ($hadSettings) { $oldSettingsBytes = [IO.File]::ReadAllBytes($SettingsPath) }
    if ($changeSettings -and $null -eq $NewSettingsBytes) { throw 'Guard 配置事务内容缺失。' }
    $evidence = Join-Path $EvidenceDirectory ([Guid]::NewGuid().ToString('N') + '.json')
    [void][IO.Directory]::CreateDirectory($EvidenceDirectory)
    $record = [ordered]@{ schemaVersion = 1; state = 'Prepared'; registrationPath = $RegistrationPath;
        hadRegistration = $hadRegistration; previousRegistrationBase64 = [Convert]::ToBase64String([byte[]]$oldBytes);
        previousTasks = $Previous; desiredTasks = $Desired; startedUtc = [DateTime]::UtcNow.ToString('O') }
    $record['settingsPath'] = $SettingsPath
    $record['hadSettings'] = $hadSettings
    $record['previousSettingsBase64'] = [Convert]::ToBase64String($oldSettingsBytes)
    Write-GuardAtomicBytes $evidence ([Text.Encoding]::UTF8.GetBytes(($record | ConvertTo-Json -Depth 8)))
    $attempted = New-Object 'System.Collections.Generic.List[string]'
    try {
        if ($changeSettings) { Write-GuardAtomicBytes $SettingsPath $NewSettingsBytes }
        foreach ($name in $allowed) {
            $attempted.Add($name) # A failed response may follow an applied mutation.
            if ($null -eq $Desired[$name]) {
                if ($Previous.Contains($name)) { Unregister-ScheduledTask -TaskName $name -TaskPath '\' -Confirm:$false -ErrorAction Stop }
            } else { Register-ScheduledTask -TaskName $name -TaskPath '\' -Xml $Desired[$name] -Force -ErrorAction Stop | Out-Null }
        }
        Write-GuardAtomicBytes $RegistrationPath ([Text.Encoding]::UTF8.GetBytes($NewRegistration))
        $record.state = 'Committed'
        Write-GuardAtomicBytes $evidence ([Text.Encoding]::UTF8.GetBytes(($record | ConvertTo-Json -Depth 8)))
    }
    catch {
        $failure = $_
        $rollbackFailures = New-Object 'System.Collections.Generic.List[string]'
        for ($index = $attempted.Count - 1; $index -ge 0; $index--) {
            $name = $attempted[$index]
            try {
                if ($Previous.Contains($name)) {
                    Register-ScheduledTask -TaskName $name -TaskPath '\' -Xml $Previous[$name] -Force -ErrorAction Stop | Out-Null
                } else {
                    $present = @(Get-ScheduledTask -TaskPath '\' -ErrorAction Stop | Where-Object { $_.TaskName -eq $name })
                    if ($present.Count -gt 0) { Unregister-ScheduledTask -TaskName $name -TaskPath '\' -Confirm:$false -ErrorAction Stop }
                }
            } catch { $rollbackFailures.Add($name + ': ' + $_.Exception.Message) }
        }
        try {
            if ($hadRegistration) { Write-GuardAtomicBytes $RegistrationPath ([byte[]]$oldBytes) }
            elseif ([IO.File]::Exists($RegistrationPath)) { [IO.File]::Delete($RegistrationPath) }
        } catch { $rollbackFailures.Add('Registration: ' + $_.Exception.Message) }
        if ($changeSettings) {
            try {
                if ($hadSettings) { Write-GuardAtomicBytes $SettingsPath $oldSettingsBytes }
                elseif ([IO.File]::Exists($SettingsPath)) { [IO.File]::Delete($SettingsPath) }
            } catch { $rollbackFailures.Add('Settings: ' + $_.Exception.Message) }
        }
        $record.state = if ($rollbackFailures.Count -eq 0) { 'RolledBack' } else { 'RollbackFailed' }
        $record['failure'] = $failure.Exception.Message
        $record['rollbackFailures'] = @($rollbackFailures)
        if ($rollbackFailures.Count -gt 0) { $KeepMaintenance.Value = $true }
        try { Write-GuardAtomicBytes $evidence ([Text.Encoding]::UTF8.GetBytes(($record | ConvertTo-Json -Depth 8))) }
        catch { $KeepMaintenance.Value = $true; throw ('Guard 回退记录写入失败，保留维护阻断及事务证据：' + $_.Exception.Message) }
        if ($rollbackFailures.Count -gt 0) { throw ('Guard 回退未完成，保留维护阻断：' + ($rollbackFailures -join '; ')) }
        throw $failure
    }
}

function Assert-GuardMainComponents([string]$MainPath, [string]$GuardDirectory, [string]$Version) {
    $main = Get-Item -LiteralPath $MainPath -ErrorAction Stop
    $directory = $main.DirectoryName
    $identity = Get-Content -LiteralPath (Join-Path $directory 'build-identity.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($identity.deploymentApproved -ne $true -or $identity.gitDirty -ne $false -or $identity.releaseStatus -ne 'FORMAL_RELEASE' -or
        $identity.fileVersion -ne $Version -or $identity.assemblyName -ne 'MTTFTest' -or
        $identity.watchdogSchema -ne 7 -or $identity.sessionAgentSchema -ne 8) { throw '主程序包未批准部署或版本/协议不匹配。' }
    if ((Get-FileHash -LiteralPath $main.FullName -Algorithm SHA256).Hash -ine $identity.mainExecutableSha256) {
        throw '主程序文件与发布身份哈希不匹配。'
    }
    $required = @($main.Name, 'MTTFTest.Watchdog.exe', 'MTTFTest.SessionAgent.exe', 'MTTFTest.SafetyAgent.exe',
        'MTTFTest.SafetyHardware.dll', 'MTTFTest.Watchdog.Protocol.dll', 'MTTFTest.Watchdog.Client.dll',
        'MTTFTest.RecoveryControl.dll', 'Controller.dll')
    foreach ($name in $required) {
        $path = Join-Path $directory $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "恢复组件缺失：$name" }
        $entries = @($identity.componentIdentities | Where-Object { $_.name -ceq $name })
        if ($entries.Count -ne 1 -or $entries[0].fileVersion -ne $Version -or
            (Get-Item -LiteralPath $path).VersionInfo.FileVersion -ne $Version) { throw "恢复组件版本或清单不匹配：$name" }
        $assembly = [Reflection.AssemblyName]::GetAssemblyName($path)
        $expectedName = if ($name -eq $main.Name) { 'MTTFTest' } else { [IO.Path]::GetFileNameWithoutExtension($name) }
        if ($assembly.Name -ne $expectedName -or $assembly.Version.ToString() -ne $Version -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $entries[0].sha256) { throw "恢复组件内容或程序集身份不匹配：$name" }
    }
    $mainCore = (Get-FileHash -LiteralPath (Join-Path $directory 'MTTFTest.RecoveryControl.dll') -Algorithm SHA256).Hash
    $guardCore = (Get-FileHash -LiteralPath (Join-Path $GuardDirectory 'MTTFTest.RecoveryControl.dll') -Algorithm SHA256).Hash
    if ($mainCore -cne $guardCore) { throw '主程序与独立 Guard 的 RecoveryControl 内容不一致。' }
    if (-not (Test-Path -LiteralPath (Join-Path $directory 'MTTFTest.UnattendedMode.required') -PathType Leaf)) {
        throw '主程序缺少正式无人值守启动标记。'
    }
}

function Resolve-GuardRecoveryMode($Identity, $Settings, [string]$RequestedMode) {
    $modes = @('ObserveOnly', 'RecoverExited', 'RecoverStalled')
    $mode = $Settings.Mode
    if (-not [string]::IsNullOrEmpty($RequestedMode)) { $mode = [Array]::IndexOf($modes, $RequestedMode) }
    if ($mode -notin @(0, 1, 2) -or $Settings.SupervisionExpirySeconds -ne 3600) { throw 'Guard 恢复模式或 60 分钟阈值无效。' }
    if ($mode -ne 0 -and ($Identity.deliveryStage -ne 'AutomaticRecovery' -or
        $Identity.automaticExecutionReady -ne $true -or $Identity.configuration -ne 'Release' -or
        $Identity.builtFromVerifiedInputs -ne $true -or $Identity.schemaVersion -ne 3 -or
        $Identity.gitDirty -ne $false -or $modes[$mode] -cnotin @($Identity.approvedModes))) {
        throw '当前包未开放自动恢复，不能启用执行任务。'
    }
    return [int]$mode
}

function Assert-GuardAcceptanceTarget($Identity, [string]$Executable, [string]$TargetBench, [string]$TargetMachine) {
    if ($Identity.schemaVersion -ne 3 -or $TargetBench -cne $Identity.acceptanceBenchId -or
        $TargetMachine -ine $Identity.acceptanceMachineName -or
        (Get-FileHash -LiteralPath (Join-Path (Split-Path $Executable -Parent) 'build-identity.json')).Hash -ine $Identity.mainIdentitySha256) {
        throw 'AcceptanceInstallTargetMismatch'
    }
}

function Assert-GuardTaskOwnership([string]$ActualXml, [string]$Directory, [string]$StateDirectory, [bool]$Execution) {
    if ([string]::IsNullOrWhiteSpace($ActualXml) -or $ActualXml.Length -gt 65536) { throw 'Guard 任务归属无法确认。' }
    $readerSettings = New-Object Xml.XmlReaderSettings
    $readerSettings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $readerSettings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create((New-Object IO.StringReader($ActualXml)), $readerSettings)
    try {
        $actual = New-Object Xml.XmlDocument
        $actual.XmlResolver = $null
        $actual.Load($reader)
    } finally { $reader.Dispose() }
    $ns = New-Object Xml.XmlNamespaceManager($actual.NameTable)
    $ns.AddNamespace('t', 'http://schemas.microsoft.com/windows/2004/02/mit/task')
    $actions = @($actual.SelectNodes('/t:Task/t:Actions/*', $ns))
    $principals = @($actual.SelectNodes('/t:Task/t:Principals/t:Principal', $ns))
    $verb = if ($Execution) { '--execute' } else { '--check' }
    $journalName = if ($Execution) { 'execution-journal' } else { 'journal' }
    $expectedArguments = $verb + ' --settings "' + (Join-Path $StateDirectory 'guard-settings.json') +
        '" --journal "' + (Join-Path $StateDirectory $journalName) + '"'
    if ($actions.Count -ne 1 -or $actions[0].LocalName -ne 'Exec' -or $principals.Count -ne 1 -or
        $principals[0].UserId -ne 'S-1-5-18' -or
        (-not [string]::IsNullOrEmpty([string]$principals[0].LogonType) -and
            $principals[0].LogonType -ne 'ServiceAccount') -or
        $principals[0].RunLevel -ne 'HighestAvailable' -or
        $actual.Task.Actions.Context -ne $principals[0].id -or
        $actions[0].Command -ine (Join-Path $Directory 'MTTFTest.RecoveryGuard.exe') -or
        $actions[0].WorkingDirectory -ine $Directory -or $actions[0].Arguments -cne $expectedArguments) {
        throw 'Guard 同名任务不属于已登记安装，拒绝覆盖或卸载。'
    }
}

$source = [IO.Path]::GetFullPath($SourceDirectory)
if ($Mode -ne 'Uninstall') {
    $identity = Read-VerifiedPackage $source
    if ($PSBoundParameters.ContainsKey('RecoveryMode')) {
        $packageSettings = Get-Content -LiteralPath (Join-Path $source 'guard-settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $null = Resolve-GuardRecoveryMode $identity $packageSettings $RecoveryMode
    }
    if ($Mode -eq 'Validate') {
        $packageSettings = Get-Content -LiteralPath (Join-Path $source 'guard-settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $validatedMode = Resolve-GuardRecoveryMode $identity $packageSettings $RecoveryMode
        [ordered]@{ validated = $true; version = $identity.version; mode = @('ObserveOnly','RecoverExited','RecoverStalled')[$validatedMode]; mutations = $false } | ConvertTo-Json
        return
    }
}
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw '安装或卸载需要管理员 PowerShell；Validate 无需管理员。' }
if ($Mode -eq 'Install' -and ([string]::IsNullOrWhiteSpace($BenchId) -or
    [string]::IsNullOrWhiteSpace($MainExecutable) -or -not [IO.Path]::IsPathRooted($MainExecutable))) { throw '安装需要 BenchId 和主程序绝对路径。' }

[void](New-Item -ItemType Directory -Path $guardState -Force)
Set-ProductDirectoryAcl $guardState ([Security.AccessControl.FileSystemRights]::ReadAndExecute)
$maintenance = Join-Path $guardState 'maintenance-inhibit.json'
$maintenanceHandle = [IO.File]::Open($maintenance, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
$executionGate = $null
$executionGateHeld = $false
$keepMaintenance = $false
try {
    # Match the executable's process mutex. Holding it across the ownership
    # check and task replacement closes a claim-after-check maintenance race.
    $executionGate = New-Object Threading.Mutex($false, 'Global\MTTFTest.RecoveryGuard.Execution')
    try { $executionGateHeld = $executionGate.WaitOne(3000) }
    catch [Threading.AbandonedMutexException] { $executionGateHeld = $true }
    if (-not $executionGateHeld) { throw 'Guard 执行者尚未退出，保留任务及接管记录，不能切换安装。' }
    $registered = $null
    if (Test-Path -LiteralPath $registrationPath -PathType Leaf) {
        $registered = Get-Content -LiteralPath $registrationPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $installedDirectory = [IO.Path]::GetFullPath([string]$registered.directory)
        if ($registered.schemaVersion -ne 2 -or $registered.task -ne $taskName -or
            $registered.executionTask -ne $executionTaskName -or
            -not $installedDirectory.StartsWith($install + '\versions\', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Guard 已登记安装身份不匹配。'
        }
        $null = Read-VerifiedPackage $installedDirectory
    }
    # Enumerate the explicit root folder; query failures must not be treated
    # as absence and then overwritten with -Force.
    $existingTasks = @(Get-ScheduledTask -TaskPath '\' -ErrorAction Stop |
        Where-Object { $_.TaskName -in @($taskName, $executionTaskName) })
    $previousTaskXml = [ordered]@{}
    foreach ($task in $existingTasks) {
        if ($null -eq $registered) { throw 'Guard 同名任务缺少安装登记，拒绝覆盖或卸载。' }
        $xml = Export-ScheduledTask -TaskName $task.TaskName -TaskPath '\' -ErrorAction Stop
        Assert-GuardTaskOwnership $xml $installedDirectory $guardState ($task.TaskName -eq $executionTaskName)
        $previousTaskXml[$task.TaskName] = [string]$xml
    }
    if ($Mode -eq 'Uninstall') {
        if ($null -eq $registered) { throw 'Guard 安装登记不存在，无法确认卸载目标。' }
        $registered = Get-Content -LiteralPath $registrationPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $installedDirectory = [IO.Path]::GetFullPath([string]$registered.directory)
        if (-not $installedDirectory.StartsWith($install + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '已安装 Guard 路径不在指定根目录。' }
        $null = Read-VerifiedPackage $installedDirectory
        Assert-NoTakeover (Join-Path $installedDirectory 'MTTFTest.RecoveryGuard.exe')
        $desiredTasks = [ordered]@{ MTTFTestRecoveryGuardExecution = $null; MTTFTestRecoveryGuard = $null }
        Invoke-GuardTaskTransaction $desiredTasks $previousTaskXml $registrationPath (Get-Content -LiteralPath $registrationPath -Raw -Encoding UTF8) (Join-Path $guardState 'install-transactions') ([ref]$keepMaintenance)
        [ordered]@{ uninstalled = $true; sharedAuthorityPreserved = $true; installedFilesPreserved = $true } | ConvertTo-Json
        return
    }
    Assert-NoTakeover (Join-Path $source 'MTTFTest.RecoveryGuard.exe')
    if (Test-Path -LiteralPath $MainExecutable -PathType Leaf) {
        if ((Get-Item -LiteralPath $MainExecutable).VersionInfo.FileVersion -ne $identity.version) { throw '主程序版本与本 Guard 包不匹配；主程序组件升级需先完成。' }
    }
    if (-not (Test-Path -LiteralPath $control)) {
        $mainName = [IO.Path]::GetFileNameWithoutExtension($MainExecutable)
        $runningMain = [Diagnostics.Process]::GetProcessesByName($mainName)
        try {
            if ($runningMain.Length -gt 0) { throw '首次注册必须在主程序维护窗口执行：主程序需退出，旧恢复会话需先收口。' }
        } finally { foreach ($process in $runningMain) { $process.Dispose() } }
        $oldHosts = @(Get-CimInstance Win32_Process -Filter "Name='MTTFTest.Watchdog.exe'" |
            Where-Object { $_.CommandLine -match '(?i)--session-host(?:\s|$)' })
        if ($oldHosts.Count -gt 0) { throw '仍有原看门狗会话运行，不能首次注册新的外部授权。' }
    }
    if ($install -eq [IO.Path]::GetPathRoot($install).TrimEnd('\') -or
        $install -eq [Environment]::GetFolderPath('ProgramFiles') -or
        $install -eq [Environment]::GetFolderPath('CommonApplicationData')) { throw '安装根目录必须是 Guard 专用子目录。' }
    if (Test-Path -LiteralPath $install) {
        if (@(Get-ChildItem -LiteralPath $install -Force | Where-Object { $_.Name -ne 'versions' }).Count -gt 0) { throw '安装根目录包含非 Guard 内容，拒绝修改其权限。' }
    }
    [void](New-Item -ItemType Directory -Path $install -Force)
    Set-ProductDirectoryAcl $install ([Security.AccessControl.FileSystemRights]::ReadAndExecute)
    $digest = (Get-FileHash -LiteralPath (Join-Path $source 'guard-identity.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    $destination = [IO.Path]::GetFullPath((Join-Path $install ('versions\' + $identity.version + '-' + $digest.Substring(0, 12) + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))))
    if (-not $destination.StartsWith($install + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '安装目标越界。' }
    if (-not (Test-Path -LiteralPath $destination)) {
        [void](New-Item -ItemType Directory -Path $destination -Force)
        foreach ($name in @($identity.files | ForEach-Object { $_.name }) + @('guard-identity.json')) {
            $targetFile = Join-Path $destination $name
            [void](New-Item -ItemType Directory -Path (Split-Path $targetFile -Parent) -Force)
            Copy-Item -LiteralPath (Join-Path $source $name) -Destination $targetFile
        }
    }
    $null = Read-VerifiedPackage $destination
    $exe = Join-Path $destination 'MTTFTest.RecoveryGuard.exe'
    $settingsPath = Join-Path $guardState 'guard-settings.json'
    $settingsInput = if (Test-Path -LiteralPath $settingsPath -PathType Leaf) { $settingsPath } else { Join-Path $destination 'guard-settings.json' }
    $settings = Get-Content -LiteralPath $settingsInput -Raw -Encoding UTF8 | ConvertFrom-Json
    $settings.Mode = Resolve-GuardRecoveryMode $identity $settings $RecoveryMode
    if ($settings.Mode -ne 0) {
        Assert-GuardAcceptanceTarget $identity $MainExecutable $BenchId $env:COMPUTERNAME
        Assert-GuardMainComponents $MainExecutable $destination $identity.version
    }
    $validationPath = Join-Path $guardState ('settings-validation-' + [Guid]::NewGuid().ToString('N') + '.json')
    try {
        Write-GuardAtomicBytes $validationPath ([Text.Encoding]::UTF8.GetBytes(($settings | ConvertTo-Json -Depth 8)))
        $validation = @(& $exe --validate-settings --settings $validationPath)
        if ($LASTEXITCODE -ne 0) { throw ('拟应用配置未通过参数关系校验：' + ($validation -join '')) }
    } finally { if ([IO.File]::Exists($validationPath)) { [IO.File]::Delete($validationPath) } }
    $registration = @(& $exe --register --bench $BenchId --main ([IO.Path]::GetFullPath($MainExecutable)) --root $control)
    if ($LASTEXITCODE -ne 0) { throw ('Guard 注册失败：' + ($registration -join '')) }
    Set-ProductDirectoryAcl $control ([Security.AccessControl.FileSystemRights]::Modify)
    $journal = Join-Path $guardState 'journal'
    $scanXml = New-GuardTaskXml $exe $destination $settingsPath $journal $false $true
    # Separate journals avoid concurrent rotation by scanner and executor.
    $executionEnabled = $settings.Mode -ne 0
    $executionXml = New-GuardTaskXml $exe $destination $settingsPath (Join-Path $guardState 'execution-journal') $true $executionEnabled
    # The selected mode has passed package readiness and executable validation.
    # Configuration, tasks and registration switch within one rollback boundary.
    $newRegistration = [ordered]@{ schemaVersion = 2; directory = $destination; version = $identity.version; task = $taskName;
        executionTask = $executionTaskName; executionEnabled = $executionEnabled; installedUtc = [DateTime]::UtcNow.ToString('O') } | ConvertTo-Json
    $desiredTasks = [ordered]@{ MTTFTestRecoveryGuardExecution = $executionXml; MTTFTestRecoveryGuard = $scanXml }
    Invoke-GuardTaskTransaction $desiredTasks $previousTaskXml $registrationPath $newRegistration (Join-Path $guardState 'install-transactions') ([ref]$keepMaintenance) $settingsPath ([Text.Encoding]::UTF8.GetBytes(($settings | ConvertTo-Json -Depth 8)))
    [ordered]@{ installed = $true; directory = $destination; mode = @('ObserveOnly','RecoverExited','RecoverStalled')[$settings.Mode]; task = $taskName } | ConvertTo-Json
}
finally {
    if ($executionGateHeld) { $executionGate.ReleaseMutex() }
    if ($null -ne $executionGate) { $executionGate.Dispose() }
    $maintenanceHandle.Dispose()
    if (-not $keepMaintenance) { Remove-Item -LiteralPath $maintenance }
}
