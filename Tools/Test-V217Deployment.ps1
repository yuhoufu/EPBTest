[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$temp = Join-Path ([IO.Path]::GetTempPath()) ('EPB V217 中文 & () ! 测试-' + [Guid]::NewGuid().ToString('N'))
$passed = 0
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:passed++; Write-Output "PASS V217Deployment $Message"
}
function Expect-Failure([scriptblock]$Action, [string]$Message) {
    $failed = $false
    try { & $Action | Out-Null } catch { $failed = $true }
    Check $failed $Message
}
function Import-Functions([string]$Path) {
    $tokens=$null; $errors=$null
    $ast = [Management.Automation.Language.Parser]::ParseFile($Path, [ref]$tokens, [ref]$errors)
    if (@($errors).Count -gt 0) { throw "$Path : $($errors[0].Message)" }
    foreach ($node in $ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst]}, $false)) {
        . ([scriptblock]::Create(($node.Extent.Text -replace '^function ', 'function script:')))
    }
}
function Refresh-FixtureIdentity([string]$Path) {
    $files = @(Get-ChildItem -LiteralPath $Path -File | Where-Object { $_.Name -ne 'build-identity.json' } | ForEach-Object {
        @{ name=$_.Name; bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName).Hash }
    })
    $version = (Get-Item -LiteralPath (Join-Path $Path 'MTTFTest.exe')).VersionInfo.FileVersion
    $components = @($files | Where-Object { $_.name -match '\.(exe|dll)$' } | ForEach-Object { @{name=$_.name;fileVersion=$version} })
    $map = New-Object 'System.Collections.Generic.SortedDictionary[string,string]' ([StringComparer]::Ordinal)
    foreach ($file in $files) { $map.Add($file.name, (Join-Path $Path $file.name)) }
    @{ gitCommit=('a'*40); packageContentSha256=(Get-DeploymentAggregateHash $map); files=$files; componentIdentities=$components;
        fileVersion=$version; recoveryArchitectureGeneration='EPB-V2.17'; watchdogSchema=7; sessionAgentSchema=8;
        releaseStatus='FORMAL_RELEASE'; deploymentApproved=$true } | ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath (Join-Path $Path 'build-identity.json') -Encoding UTF8
}
try {
    [void](New-Item -ItemType Directory -Path $temp)
    Import-Functions (Join-Path $PSScriptRoot 'Install-MTTFTest-Unattended.ps1')
    Import-Functions (Join-Path $PSScriptRoot 'Stop-RelatedProcesses.ps1')
    Import-Functions (Join-Path $PSScriptRoot 'Export-StabilityEvidence.ps1')
    $evidenceSource = Join-Path $temp 'evidence-source'
    $deniedDirectory = Join-Path $evidenceSource 'denied'
    [void](New-Item -ItemType Directory -Path $deniedDirectory -Force)
    'readable' | Set-Content -LiteralPath (Join-Path $evidenceSource 'readable.txt')
    $originalAcl = Get-Acl -LiteralPath $deniedDirectory
    $restrictedAcl = Get-Acl -LiteralPath $deniedDirectory
    $deny = New-Object Security.AccessControl.FileSystemAccessRule(
        [Security.Principal.WindowsIdentity]::GetCurrent().User,
        [Security.AccessControl.FileSystemRights]::ListDirectory,
        [Security.AccessControl.AccessControlType]::Deny)
    $restrictedAcl.AddAccessRule($deny)
    try {
        Set-Acl -LiteralPath $deniedDirectory -AclObject $restrictedAcl
        $scan = Get-EvidenceSourceScan $evidenceSource
        Check (@($scan.Files | Where-Object { $_.Name -eq 'readable.txt' }).Count -eq 1) '采证遇受保护目录仍取得可读文件'
        Check (@($scan.Errors | Where-Object { $_.Kind -eq 'Enumeration' -and $_.Reason }).Count -gt 0) '采证明确记录目录权限拒绝而非伪报完整'
    } finally { Set-Acl -LiteralPath $deniedDirectory -AclObject $originalAcl }
    $source = Join-Path $temp 'Package'
    $root = Join-Path $temp 'MTTFTest'
    [void](New-Item -ItemType Directory -Path $source)
    $names = @('MTTFTest.exe','Controller.dll','MTTFTest.Watchdog.exe','MTTFTest.SafetyAgent.exe',
        'MTTFTest.SessionAgent.exe','MTTFTest.SafetyHardware.dll','MTTFTest.Watchdog.Protocol.dll','MTTFTest.Watchdog.Client.dll',
        'MTTFTest.RecoveryControl.dll')
    foreach ($name in $names) { Copy-Item -LiteralPath (Join-Path $repo ('MTTfTest\bin\' + $Configuration + '\' + $name)) -Destination (Join-Path $source $name) }
    'fixture-A' | Set-Content -LiteralPath (Join-Path $source 'fixture.txt')
    Refresh-FixtureIdentity $source
    Install-CurrentSlot $source $root
    Check (-not (Test-CurrentSlotReplacementRequired $source $root)) '首次安装完整文件及身份一致'
    Check (@(Get-ChildItem -LiteralPath $root -Directory -Filter '.retired-*').Count -eq 0) '重复安装不产生重复旧槽'
    'fixture-B' | Set-Content -LiteralPath (Join-Path $source 'fixture.txt')
    Refresh-FixtureIdentity $source
    Check (Test-CurrentSlotReplacementRequired $source $root) '同版本不同哈希必须换包'
    Install-CurrentSlot $source $root
    Check (-not (Test-CurrentSlotReplacementRequired $source $root)) '完整换包之后文件一致'
    'corrupt' | Set-Content -LiteralPath (Join-Path $source 'fixture.txt')
    Expect-Failure { Install-CurrentSlot $source $root } '损坏包在修改Current前拒绝'
    Check ((Get-Content -LiteralPath (Join-Path $root 'Current\fixture.txt')) -eq 'fixture-B') '损坏包不影响已有版本'
    Refresh-FixtureIdentity $source
    $locked = [IO.File]::Open((Join-Path $root 'Current\MTTFTest.exe'), 'Open', 'Read', 'None')
    try { Expect-Failure { Install-CurrentSlot $source $root } '文件独占占用时换包明确失败' }
    finally { $locked.Dispose() }
    Check (Test-Path -LiteralPath (Join-Path $root 'Current\MTTFTest.exe')) '文件占用失败保留当前程序'
    Install-CurrentSlot $source $root
    Check (-not (Test-CurrentSlotReplacementRequired $source $root)) '占用解除后重试成功'
    $interrupted = Join-Path $root '.retired-injected-interruption'
    Move-Item -LiteralPath (Join-Path $root 'Current') -Destination $interrupted
    @{ retired=$interrupted } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'install-transaction.json') -Encoding UTF8
    Install-CurrentSlot $source $root
    Check (-not (Test-CurrentSlotReplacementRequired $source $root)) '中断于Current移走后可恢复并重新安装'
    $identityPath = Join-Path $source 'build-identity.json'
    $valid = [IO.File]::ReadAllText($identityPath)
    & {
        $WhatIfPreference = $true
        $verified = Get-VerifiedDeploymentIdentity $source
        Check ($null -ne $verified) 'WhatIf预演仍真实校验包身份'
        Check ($WhatIfPreference -eq $true) '只读校验不修改调用者WhatIf偏好'
    }
    $identity = $valid | ConvertFrom-Json
    $identity.sessionAgentSchema = 7
    $identity | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $identityPath -Encoding UTF8
    Expect-Failure { Get-VerifiedDeploymentIdentity $source } '3.0拒绝旧SessionAgent协议'
    [IO.File]::WriteAllText($identityPath, $valid, (New-Object Text.UTF8Encoding($true)))
    $identity = $valid | ConvertFrom-Json
    $identity.componentIdentities = @($identity.componentIdentities | Where-Object { $_.name -ne 'MTTFTest.RecoveryControl.dll' })
    $identity | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $identityPath -Encoding UTF8
    Expect-Failure { Get-VerifiedDeploymentIdentity $source } '3.0缺少RecoveryControl组件拒绝'
    [IO.File]::WriteAllText($identityPath, $valid, (New-Object Text.UTF8Encoding($true)))
    $identity = $valid | ConvertFrom-Json
    $identity.componentIdentities[1] = $identity.componentIdentities[0]
    $identity | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $identityPath -Encoding UTF8
    Expect-Failure { Get-VerifiedDeploymentIdentity $source } '重复组件不能代替完整组件集合'
    [IO.File]::WriteAllText($identityPath, $valid, (New-Object Text.UTF8Encoding($true)))
    $identity = $valid | ConvertFrom-Json
    $identity.recoveryArchitectureGeneration = 'EPB-V3'
    $identity | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $identityPath -Encoding UTF8
    Expect-Failure { Get-VerifiedDeploymentIdentity $source } 'V3组件身份拒绝混装'
    [IO.File]::WriteAllText($identityPath, $valid, (New-Object Text.UTF8Encoding($true)))
    $identity = $valid | ConvertFrom-Json
    $identity.files[0].name = '..\outside.exe'
    $identity | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $identityPath -Encoding UTF8
    Expect-Failure { Get-VerifiedDeploymentIdentity $source } '清单路径穿越拒绝'
    [IO.File]::WriteAllText($identityPath, $valid, (New-Object Text.UTF8Encoding($true)))
    $extra = Join-Path $source 'unexpected.dll'
    'old-dll' | Set-Content -LiteralPath $extra
    Expect-Failure { Get-VerifiedDeploymentIdentity $source } '清单外旧DLL拒绝混包'
    Remove-Item -LiteralPath $extra
    $identity = $valid | ConvertFrom-Json
    $identity.packageContentSha256 = 'f' * 64
    $identity | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $identityPath -Encoding UTF8
    Expect-Failure { Get-VerifiedDeploymentIdentity $source } '聚合哈希错误拒绝'
    Expect-Failure { Resolve-SafeDirectory 'D:\' 'UnsafeRoot' } '磁盘根目录拒绝'
    $main = @{ Id=123; StartTicks=100 }
    $receipt = [pscustomobject]@{ SchemaVersion=2; MainProcessId=123; MainProcessStartUtcTicks=100;
        UpdatedUtcTicks=300; RequestedUtcTicks=201; SessionId='session'; State=3; RelaunchDisposition=1;
        MotorsOff=$true; PowerOff=$true; PressureSafe=$true; PersistenceDrained=$true; LogicalQuiescent=$true }
    Check (Test-StopReceipt $receipt $main 200) '清场接受本次准确身份的完整安全退出回执'
    $receipt.MainProcessStartUtcTicks=101
    Check (-not (Test-StopReceipt $receipt $main 200)) '清场拒绝PID复用回执'
    $receipt.MainProcessStartUtcTicks=100; $receipt.PowerOff=$false
    Check (-not (Test-StopReceipt $receipt $main 200)) '通信失败不能伪报断能'
    $receipt.PowerOff=$true; $receipt.RequestedUtcTicks=199
    Check (-not (Test-StopReceipt $receipt $main 200)) '历史停止回执不能授权本次清场'
    $receipt.RequestedUtcTicks=201; $receipt.RelaunchDisposition=2
    Check (-not (Test-StopReceipt $receipt $main 200)) '自动续跑许可不能授权人工清场'
    $jsonPath = Join-Path $temp 'maintenance.json'
    Write-MaintenanceJson $jsonPath @{TransactionId='same';CleanupCompleted=$false}
    Write-MaintenanceJson $jsonPath @{TransactionId='same';CleanupCompleted=$true}
    Check ((Get-Content -LiteralPath $jsonPath -Raw | ConvertFrom-Json).CleanupCompleted) '维护事务原子更新'
    $maintenanceRoot = Join-Path $temp 'ProgramData\MTTFTest'
    [void](New-Item -ItemType Directory -Path $maintenanceRoot -Force)
    $maintenancePath = Join-Path $maintenanceRoot 'maintenance-inhibit.json'
    $unfinishedState = '{"CleanupCompleted":false,"InstallRoot":"D:\\OldInstall\\MTTFTest"}'
    [IO.File]::WriteAllText($maintenancePath, $unfinishedState, (New-Object Text.UTF8Encoding($true)))
    Archive-MaintenanceInhibitForInstall $maintenanceRoot
    Check (-not (Test-Path -LiteralPath $maintenancePath)) '未完成或异目录清场状态不阻断重新安装'
    $maintenanceArchives = @(Get-ChildItem -LiteralPath (Join-Path $maintenanceRoot 'MaintenanceArchive') -File)
    Check ($maintenanceArchives.Count -eq 1 -and
        [IO.File]::ReadAllText($maintenanceArchives[0].FullName, [Text.Encoding]::UTF8).TrimStart([char]0xFEFF) -eq $unfinishedState) `
        '重新安装前封存旧清场状态供审计'
    foreach ($name in @('Stop-RelatedProcesses.ps1','Export-StabilityEvidence.ps1')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $temp }
    $saved = $env:MTTFTEST_QUICKDEPLOY_PARSE_ONLY
    try {
        $env:MTTFTEST_QUICKDEPLOY_PARSE_ONLY='1'
        foreach ($name in @('一键停止全部相关进程.cmd','恢复后台服务.cmd','一键故障采证.cmd')) {
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('QuickDeploy\' + $name)) -Destination $temp
            $commandPath = Join-Path $temp $name
            $info = New-Object Diagnostics.ProcessStartInfo
            $info.FileName = $env:ComSpec
            $info.Arguments = '/d /s /v:off /c ""' + $commandPath + '""'
            $info.UseShellExecute=$false; $info.CreateNoWindow=$true
            $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
            $process = [Diagnostics.Process]::Start($info)
            try {
                $out = $process.StandardOutput.ReadToEnd()
                $errorText = $process.StandardError.ReadToEnd()
                $process.WaitForExit()
                Check ($process.ExitCode -eq 0 -and $out.Contains('QUICKDEPLOY_MAINTENANCE_PARSE_PASS')) "中文及特殊字符路径入口：$name $errorText"
            } finally { $process.Dispose() }
        }
    } finally { $env:MTTFTEST_QUICKDEPLOY_PARSE_ONLY=$saved }
    # Materialize actual task definitions, replacing only the OS registration mutation.
    $healthTaskName = 'MTTFTestRecoveryHealth'
    $healthRoot = Join-Path $temp 'HealthTaskFixture'
    $healthDeployment = Join-Path $healthRoot 'Current\Deployment'
    [void](New-Item -ItemType Directory -Path $healthDeployment -Force)
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Test-MTTFTest-RecoveryHealth.ps1') -Destination $healthDeployment
    Import-Module ScheduledTasks -ErrorAction Stop
    function script:Register-ScheduledTask {
        param($TaskName, $Action, $Trigger, $Principal, $Settings, [switch]$Force)
        $script:capturedHealthTask = @{Name=$TaskName; Action=$Action; Trigger=$Trigger;
            Principal=$Principal; Settings=$Settings}
    }
    try {
        if ((Get-Command Register-ScheduledTask).Definition -notmatch 'capturedHealthTask') {
            throw '健康任务测试未接管注册边界，拒绝调用真实注册器。'
        }
        Install-RecoveryHealthTask $healthRoot
        $definition = $script:capturedHealthTask
        Check ($definition.Name -eq $healthTaskName -and $definition.Principal.UserId -in @('SYSTEM','S-1-5-18')) '分钟健康任务使用SYSTEM身份'
        Check ($definition.Settings.ExecutionTimeLimit -eq 'PT45S' -and
            [int]$definition.Settings.MultipleInstances -eq 2) '健康任务45秒退出且IgnoreNew禁止并发'
        Check (@($definition.Trigger | Where-Object { $_.Repetition.Interval -eq 'PT1M' }).Count -eq 1 -and
            @($definition.Trigger | Where-Object { $_.CimClass.CimClassName -eq 'MSFT_TaskBootTrigger' }).Count -eq 1) '健康任务同时包含开机与每分钟触发'
        Check ($definition.Action.Arguments.Contains('Test-MTTFTest-RecoveryHealth.ps1') -and
            -not $definition.Action.Arguments.Contains('--launch-main')) '健康任务检查监督服务而不直接拉起试验'
    } finally { Remove-Item Function:\Register-ScheduledTask }
    $healthAst = [Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $PSScriptRoot 'Test-MTTFTest-RecoveryHealth.ps1'), [ref]$null, [ref]$null)
    $queryFunction = $healthAst.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Get-RecoverySupervisorIdentity'
    }, $true)
    Invoke-Expression $queryFunction.Extent.Text
    function script:Invoke-BoundedSupervisorSc { param($Verb) return $script:fakeScStatus }
    function script:Get-ItemProperty { param($LiteralPath, $Name)
        if ($LiteralPath -ne 'HKLM:\SYSTEM\CurrentControlSet\Services\MTTFTestSupervisor' -or $Name -ne 'ImagePath') {
            throw 'Unexpected registry query in health fixture'
        }
        return @{ ImagePath='"C:\Test\Current\MTTFTest.Watchdog.exe"' }
    }
    try {
        $script:fakeScStatus = "SERVICE_NAME: MTTFTestSupervisor`n STATE : 4 RUNNING`n PID : 12345`n"
        $queried = Get-RecoverySupervisorIdentity
        Check ($queried.ProcessId -eq 12345 -and $queried.State -eq 'Running') '有界SCM查询保留准确运行PID'
        Check ($queried.PathName -eq '"C:\Test\Current\MTTFTest.Watchdog.exe"') '有界SCM查询保留配置可执行路径'
        $script:fakeScStatus = " STATE : 1 STOPPED`n PID : 0`n"
        $queried = Get-RecoverySupervisorIdentity
        Check ($queried.ProcessId -eq 0 -and $queried.State -eq 'Stopped') 'SCM停止状态不复用旧PID'
        $script:fakeScStatus = 'unparseable service query'
        Expect-Failure { Get-RecoverySupervisorIdentity } 'SCM身份无法解析时禁止猜测进程'
    } finally {
        Remove-Item Function:\Invoke-BoundedSupervisorSc
        Remove-Item Function:\Get-ItemProperty
    }
    Write-Output "PASS V217Deployment $passed/$passed (isolated filesystem; no SCM or hardware mutation)"
}
finally {
    $resolved = [IO.Path]::GetFullPath($temp)
    $prefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw '测试清理路径越界。' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
