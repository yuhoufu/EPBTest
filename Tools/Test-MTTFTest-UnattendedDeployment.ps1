[CmdletBinding()]
param(
    [string]$InstallerPath = ''
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
    $InstallerPath = Join-Path $PSScriptRoot 'Install-MTTFTest-Unattended.ps1'
}
$installer = [IO.Path]::GetFullPath($InstallerPath)
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
    throw "无人值守安装脚本不存在：$installer"
}

$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    $installer,
    [ref]$tokens,
    [ref]$parseErrors)
if (@($parseErrors).Count -ne 0) {
    throw "无人值守安装脚本无法解析：$(@($parseErrors)[0].Message)"
}

$unsupportedStopTaskConfirm = @($ast.FindAll({
    param($node)
    if ($node -isnot [Management.Automation.Language.CommandAst] -or
        $node.GetCommandName() -ne 'Stop-ScheduledTask') {
        return $false
    }
    return @($node.CommandElements | Where-Object {
        $_ -is [Management.Automation.Language.CommandParameterAst] -and
        $_.ParameterName -eq 'Confirm'
    }).Count -ne 0
}, $true))
if ($unsupportedStopTaskConfirm.Count -ne 0) {
    throw 'Stop-ScheduledTask 在 Windows PowerShell 5.1 不支持 -Confirm 参数。'
}

$installerText = [IO.File]::ReadAllText($installer, [Text.Encoding]::UTF8)
foreach ($removedGate in @(
        'Read-And-VerifyPackage',
        'Protect-MachineJson',
        'Write-SlotDescriptor',
        'Assert-SlotDescriptor',
        'package-pointer.v5.json')) {
    if ($installerText.Contains($removedGate)) {
        throw "安装脚本仍包含已取消的复杂包门禁：$removedGate"
    }
}
foreach ($required in @(
        'Assert-RequiredProgramFiles',
        'Stop-InstalledSessionAgent',
        'Assert-InstalledMainStopped',
        'Initialize-RuntimeConfig',
        'Remove-InstalledProgramFiles',
        'ProgramData 配置、日志和事故证据已保留',
        'Read-Host',
        'ForceUninstall',
        'Write-OperationContext',
        'Write-OperationStep',
        'Write-DeploymentResult',
        'last-deployment-result.json',
        'RunLevel Highest',
        'shortcutBytes[21]',
        'Test-CurrentSlotReplacementRequired',
        'Read-Utf8JsonFile',
        'New-Object Text.UTF8Encoding($false, $true)',
        'Invoke-LegacyCheckpointSafeRollover',
        'Ensure-V3BaselineLastKnownGood',
        'legacySchema -notin @(5, 6)',
        'authorizationMigrated = $false',
        'permitMigrated = $false',
        'nonceMigrated = $false',
        'SafeIdleAlarmed',
        "'obj=' 'LocalSystem'",
        "-Argument '--launch-main'",
        'MTTFTestAutoStart',
        'MTTFTest.FirstRun.configured',
        "'Configure'",
        'MTTFTest.exe',
        'MTTFTest.EngineHost.exe',
        'MTTFTest.Recovery.Kernel.dll',
        'MTTFTest.Watchdog.exe',
        'MTTFTest.SessionAgent.exe')) {
    if (-not $installerText.Contains($required)) {
        throw "安装脚本缺少最小运行检查：$required"
    }
}
Write-Output 'PASS SimpleUnattendedDeploymentContract 1/1'
Write-Output 'PASS WindowsPowerShell51ScheduledTaskCompatibility 1/1'

$utf8RegressionRoot = Join-Path ([IO.Path]::GetTempPath()) `
    ('EPBTest-PS51-Utf8-' + [Guid]::NewGuid().ToString('N'))
try {
    [void](New-Item -ItemType Directory -Path $utf8RegressionRoot)
    $utf8RegressionPath = Join-Path $utf8RegressionRoot 'checkpoint.json'
    $utf8RegressionJson = '{"SchemaVersion":6,"MotorOffConfirmed":true,"PressureSafeConfirmed":true,"PersistenceDrained":true,"LastInProcessRecoveryResult":"必须由 Watchdog 重启软件。","RemainingFormalCycles":{"4":123}}'
    [IO.File]::WriteAllText(
        $utf8RegressionPath,
        $utf8RegressionJson,
        (New-Object Text.UTF8Encoding($false)))
    $utf8ReaderScript = Join-Path $utf8RegressionRoot 'read-checkpoint.ps1'
    $utf8ReaderCommand = @'
param([Parameter(Mandatory = $true)][string]$CheckpointPath)
$ErrorActionPreference = 'Stop'
$utf8 = New-Object Text.UTF8Encoding($false, $true)
$json = [IO.File]::ReadAllText($CheckpointPath, $utf8) | ConvertFrom-Json
if ($json.SchemaVersion -ne 6 -or
    -not $json.MotorOffConfirmed -or
    $json.LastInProcessRecoveryResult -ne '必须由 Watchdog 重启软件。' -or
    $json.RemainingFormalCycles.'4' -ne 123) {
    throw 'UTF-8 checkpoint mismatch'
}
'UTF8_CHECKPOINT_PASS'
'@
    [IO.File]::WriteAllText(
        $utf8ReaderScript,
        $utf8ReaderCommand,
        (New-Object Text.UTF8Encoding($true)))
    $utf8Output = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass `
        -File $utf8ReaderScript `
        -CheckpointPath $utf8RegressionPath 2>&1)
    if ($LASTEXITCODE -ne 0 -or 'UTF8_CHECKPOINT_PASS' -notin $utf8Output) {
        throw "Windows PowerShell 5.1 无 BOM UTF-8 检查点回归失败：$($utf8Output -join ' | ')"
    }
}
finally {
    if (Test-Path -LiteralPath $utf8RegressionRoot -PathType Container) {
        Remove-Item -LiteralPath $utf8RegressionRoot -Recurse -Force
    }
}
Write-Output 'PASS WindowsPowerShell51Utf8Checkpoint 1/1'

$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifestPath = Join-Path $repo 'MTTfTest\app.manifest'
[xml]$manifest = [IO.File]::ReadAllText($manifestPath, [Text.Encoding]::UTF8)
$executionLevel = $manifest.SelectSingleNode(
    "/*[local-name()='assembly']/*[local-name()='trustInfo']/*[local-name()='security']/*[local-name()='requestedPrivileges']/*[local-name()='requestedExecutionLevel']")
if ($null -eq $executionLevel -or
    $executionLevel.GetAttribute('level') -ne 'requireAdministrator') {
    throw '主程序清单必须声明 requireAdministrator。'
}
$firstRunText = [IO.File]::ReadAllText(
    (Join-Path $repo 'MTTfTest\FirstRunBootstrap.cs'), [Text.Encoding]::UTF8)
$runtimePathsText = [IO.File]::ReadAllText(
    (Join-Path $repo 'Config\RuntimeConfigPaths.cs'), [Text.Encoding]::UTF8)
if (-not $firstRunText.Contains('ProgramFilesX86') -or
    -not $runtimePathsText.Contains('ProgramFilesX86')) {
    throw 'x86 主程序和运行配置路径必须统一使用 ProgramFilesX86。'
}
$programText = [IO.File]::ReadAllText(
    (Join-Path $repo 'MTTfTest\Program.cs'), [Text.Encoding]::UTF8)
$supervisorProtocolText = [IO.File]::ReadAllText(
    (Join-Path $repo 'Watchdog.Protocol\SupervisorProtocol.cs'), [Text.Encoding]::UTF8)
$sessionProtocolText = [IO.File]::ReadAllText(
    (Join-Path $repo 'Watchdog.Protocol\SessionAgentProtocol.cs'), [Text.Encoding]::UTF8)
$sessionAgentHostText = [IO.File]::ReadAllText(
    (Join-Path $repo 'MTTFTest.SessionAgent\SessionAgentHost.cs'), [Text.Encoding]::UTF8)
$supervisorHostText = [IO.File]::ReadAllText(
    (Join-Path $repo 'MTTFTest.Watchdog\SupervisorServiceHost.cs'), [Text.Encoding]::UTF8)
$sessionLaunchClientText = [IO.File]::ReadAllText(
    (Join-Path $repo 'MTTFTest.Watchdog\SessionAgentLaunchClient.cs'), [Text.Encoding]::UTF8)
$supervisorMainLaunchClientText = [IO.File]::ReadAllText(
    (Join-Path $repo 'MTTFTest.Watchdog\SupervisorMainLaunchClient.cs'), [Text.Encoding]::UTF8)
if (-not $programText.Contains('LaunchCapabilityGate.ValidateOrReject') -or
    -not $supervisorProtocolText.Contains('public const int SchemaVersion = 7') -or
    -not $sessionProtocolText.Contains('public const int SchemaVersion = 7') -or
    -not $sessionAgentHostText.Contains('LaunchCapabilityAlreadyConsumed') -or
    -not $supervisorHostText.Contains('MainProcessStartUtcTicks') -or
    -not $supervisorHostText.Contains('SupervisorSafetyHandoffOldProcessIdentityMismatch') -or
    -not $supervisorHostText.Contains('SupervisorSafetyHandoffRegisteredPathMismatch') -or
    -not $supervisorHostText.Contains('SupervisorSafetyHandoffRegisteredExecutableMismatch') -or
    -not $sessionProtocolText.Contains('RecoveryAuthoritySha256') -or
    -not $sessionLaunchClientText.Contains('SupervisorMainLaunchClient.Start(source)') -or
    -not $supervisorMainLaunchClientText.Contains('IsRecoveryLaunch = true') -or
    -not $supervisorHostText.Contains('SupervisorMainRecoverySessionMismatch') -or
    -not $supervisorHostText.Contains('SupervisorRecoveryCapabilitySessionAgentLaunch')) {
    throw 'schema 7 Supervisor 单次角色 LaunchCapability 生产门禁不完整。'
}
foreach ($commandName in @(
        '一键安装正式版.cmd', '一键修复.cmd', '一键卸载.cmd', '启动试验.cmd')) {
    $commandText = [IO.File]::ReadAllText(
        (Join-Path (Join-Path $PSScriptRoot 'QuickDeploy') $commandName),
        [Text.Encoding]::ASCII)
    if (-not $commandText.Contains('ProgramFiles(x86)')) {
        throw "快捷入口未统一 32/64 位安装路径：$commandName"
    }
}
$launchCommandText = [IO.File]::ReadAllText(
    (Join-Path (Join-Path $PSScriptRoot 'QuickDeploy') '启动试验.cmd'),
    [Text.Encoding]::ASCII)
if (-not $launchCommandText.Contains('MTTFTest.Watchdog.exe') -or
    -not $launchCommandText.Contains('--launch-main') -or
    $launchCommandText.Contains('start "MT EPB Test System" /d "%MTTFTEST_PROGRAM_FILES%\MTTFTest\Current" "%APP%"')) {
    throw '正式启动入口未唯一收口到 Supervisor launcher。'
}
Write-Output 'PASS RequireAdministratorLaunchContract 1/1'

$e2eBuildPath = Join-Path $PSScriptRoot 'Build-UnattendedRecoveryE2EPackage.ps1'
$e2eRunPath = Join-Path $PSScriptRoot 'Test-MTTFTest-InstalledRecoveryE2E.ps1'
$e2eMainPath = Join-Path $repo `
    'Tests\UnattendedRecoveryE2E\TestMainProgram.cs'
$e2eAgentPath = Join-Path $repo `
    'Tests\UnattendedRecoveryE2E\NoHardwareSafetyAgentProgram.cs'
foreach ($path in @($e2eBuildPath, $e2eRunPath, $e2eMainPath, $e2eAgentPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "安装态 E2E 测试宿主缺失：$path"
    }
}
$e2eRunText = [IO.File]::ReadAllText($e2eRunPath, [Text.Encoding]::UTF8)
$e2eMainText = [IO.File]::ReadAllText($e2eMainPath, [Text.Encoding]::UTF8)
$e2eAgentText = [IO.File]::ReadAllText($e2eAgentPath, [Text.Encoding]::UTF8)
foreach ($required in @(
        'ConfirmIsolatedEnvironment',
        'WindowsBuiltInRole]::Administrator',
        "'obj=' 'LocalSystem'",
        "'reset=' '0'",
        'sc.exe qfailure',
        '配置 Supervisor SCM failure actions 失败',
        'keepaliveTrigger',
        'e2e-session-agent-task.xml',
        'KillSupervisorAndRecover',
        'KillSessionAgentAndRecover',
        'KillSafetyAgentAndResumeAuthority',
        'MotorOffWithin250ms',
        'IndependentSafetyProofWithin30Seconds',
        'RecoveryFirstCycleCommitted',
        'ExactlyOneMainProcess',
        'ExactlyOneEffectivePermit')) {
    if (-not $e2eRunText.Contains($required)) {
        throw "安装态 E2E 驱动缺少门禁或指标：$required"
    }
}
if (-not $installerText.Contains('keepaliveTrigger') -or
    -not $installerText.Contains('SessionAgent 登录任务缺少每分钟存活触发器')) {
    throw '正式 SessionAgent 任务缺少登录与周期存活双触发门禁。'
}
if (-not $e2eMainText.Contains('LaunchCapabilityGate.TryValidate') -or
    -not $e2eMainText.Contains('SupervisorSidecarProcessLauncher') -or
    -not $e2eAgentText.Contains('E2ENoHardwareMotorOffConfirmed') -or
    -not $e2eAgentText.Contains('SupervisorSafetyAuthorityStore.Advance')) {
    throw '独立 E2E 主进程/无硬件 SafetyAgent 未复用正式授权链。'
}
Write-Output 'PASS InstalledRecoveryE2ETestHostContract 1/1'

$releaseScripts = @(
    (Join-Path $PSScriptRoot 'Build-Release.ps1'),
    (Join-Path $PSScriptRoot 'Verify-Release.ps1'),
    (Join-Path $PSScriptRoot 'New-FormalRelease7z.ps1'),
    (Join-Path $PSScriptRoot 'Build-V3Installer.ps1'))
foreach ($releaseScript in $releaseScripts) {
    $releaseTokens = $null
    $releaseParseErrors = $null
    [void][Management.Automation.Language.Parser]::ParseFile(
        $releaseScript,
        [ref]$releaseTokens,
        [ref]$releaseParseErrors)
    if (@($releaseParseErrors).Count -ne 0) {
        throw "发布脚本无法解析：$releaseScript；$(@($releaseParseErrors)[0].Message)"
    }
    $releaseText = [IO.File]::ReadAllText($releaseScript, [Text.Encoding]::UTF8)
    foreach ($removedSoakGate in @(
            'PersistenceSoakSeconds',
            'persistenceSoak',
            'longSoakGate',
            '--persistence-soak')) {
        if ($releaseText.Contains($removedSoakGate)) {
            throw "正式发布流程仍包含强制长稳门禁：$removedSoakGate；File=$releaseScript"
        }
    }
}
Write-Output 'PASS ReleaseBuildNoMandatorySoak 1/1'

$formalReleaseText = [IO.File]::ReadAllText(
    (Join-Path $PSScriptRoot 'Build-Release.ps1'),
    [Text.Encoding]::UTF8)
foreach ($requiredNativeCaptureGuard in @(
        'RedirectStandardOutput = $stdoutPath',
        'RedirectStandardError = $stderrPath',
        '$processHandle = $process.Handle',
        '$process.WaitForExit($TimeoutSeconds * 1000)',
        '$process.Kill()',
        '$exitCode = $process.ExitCode')) {
    if (-not $formalReleaseText.Contains($requiredNativeCaptureGuard)) {
        throw "正式发布流程缺少 Windows PowerShell 5.1 原生 stderr/退出码兼容门禁：$requiredNativeCaptureGuard"
    }
}
Write-Output 'PASS ReleaseNativeStderrExitCodeContract 1/1'

$v3InstallerText = [IO.File]::ReadAllText(
    (Join-Path $PSScriptRoot 'Build-V3Installer.ps1'),
    [Text.Encoding]::UTF8)
foreach ($requiredV3PackageContract in @(
        'FieldValidationCandidate',
        'AllowFieldValidationCandidate',
        'New-QuickDeployBundle.ps1',
        'FormalApprovalEvidencePath',
        'installedCrashMatrixPassed',
        'hardwareMatrixPassed',
        'soak168HoursPassed',
        'QUICKDEPLOY_R22',
        'archiveSha256')) {
    if (-not $v3InstallerText.Contains($requiredV3PackageContract)) {
        throw "V3 一键安装包脚本缺少契约：$requiredV3PackageContract"
    }
}
Write-Output 'PASS V3OperatorPackageBuilderContract 1/1'

foreach ($windowsPowerShellEntry in @(
        (Join-Path $PSScriptRoot 'Build-V3Installer.ps1'),
        (Join-Path $PSScriptRoot 'New-QuickDeployBundle.ps1'))) {
    $entryBytes = [IO.File]::ReadAllBytes($windowsPowerShellEntry)
    if ($entryBytes.Length -lt 3 -or
        $entryBytes[0] -ne 0xEF -or
        $entryBytes[1] -ne 0xBB -or
        $entryBytes[2] -ne 0xBF) {
        throw "Windows PowerShell 5.1 中文入口缺少 UTF-8 BOM：$windowsPowerShellEntry"
    }
}
Write-Output 'PASS V3PackageWindowsPowerShell51Encoding 1/1'

$simplePackageScript = Join-Path $PSScriptRoot 'New-FormalRelease7z.ps1'
$simplePackageText = [IO.File]::ReadAllText($simplePackageScript, [Text.Encoding]::UTF8)
foreach ($removedComplexStep in @(
        'Build-Release.ps1',
        'Verify-Release.ps1',
        'New-QuickDeployBundle.ps1',
        'AllowDirtyCandidate')) {
    if ($simplePackageText.Contains($removedComplexStep)) {
        throw "简易打包仍依赖复杂发布步骤：$removedComplexStep"
    }
}
foreach ($requiredSimpleStep in @(
        'MTTfTest\MTTfTest.csproj',
        '/t:Rebuild',
        '/p:Configuration=Release',
        'QuickDeploy-Installer.ps1',
        'MTTFTest.UnattendedMode.required',
        '一键卸载.cmd',
        '7-Zip 完整性测试失败',
        'Get-FileHash')) {
    if (-not $simplePackageText.Contains($requiredSimpleStep)) {
        throw "简易打包缺少必要步骤：$requiredSimpleStep"
    }
}
Write-Output 'PASS SimpleVsReleasePackaging 1/1'

$testRoot = Join-Path ([IO.Path]::GetTempPath()) `
    ('EPBTest QuickDeploy 远程机-' + [Guid]::NewGuid().ToString('N'))
try {
    [void](New-Item -ItemType Directory -Path $testRoot)
    foreach ($quickDeployFile in @(
            '一键安装正式版.cmd', '一键修复.cmd', '一键卸载.cmd',
            '检查运行状态.cmd', '启动试验.cmd', '快捷部署说明.md')) {
        $quickDeployPath = Join-Path (Join-Path $PSScriptRoot 'QuickDeploy') $quickDeployFile
        if (-not (Test-Path -LiteralPath $quickDeployPath -PathType Leaf)) {
            throw "快捷部署目录缺少文件：$quickDeployFile"
        }
        if ([IO.Path]::GetExtension($quickDeployPath) -eq '.cmd') {
            $nonAsciiBytes = @([IO.File]::ReadAllBytes($quickDeployPath) |
                Where-Object { $_ -gt 127 })
            if ($nonAsciiBytes.Count -ne 0) {
                throw "快捷部署批处理必须保持纯 ASCII，中文提示应由 PowerShell 输出：$quickDeployFile"
            }
        }
    }
    Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'QuickDeploy') -File |
        Copy-Item -Destination $testRoot -Force
    [IO.File]::WriteAllText(
        (Join-Path $testRoot 'QuickDeploy-Installer.ps1'),
        [IO.File]::ReadAllText($installer, [Text.Encoding]::UTF8),
        (New-Object Text.UTF8Encoding($true)))
    [IO.File]::WriteAllText(
        (Join-Path $testRoot 'MTTFTest.exe'),
        'parse-only',
        (New-Object Text.UTF8Encoding($false)))

    $previousParseOnly = $env:MTTFTEST_QUICKDEPLOY_PARSE_ONLY
    try {
        $env:MTTFTEST_QUICKDEPLOY_PARSE_ONLY = '1'
        $commandCases = @(
            @('一键安装正式版.cmd', 'QUICKDEPLOY_INSTALL_PARSE_PASS'),
            @('一键修复.cmd', 'QUICKDEPLOY_REPAIR_PARSE_PASS'),
            @('一键卸载.cmd', 'QUICKDEPLOY_UNINSTALL_PARSE_PASS'),
            @('检查运行状态.cmd', 'QUICKDEPLOY_STATUS_PARSE_PASS'),
            @('启动试验.cmd', 'QUICKDEPLOY_LAUNCH_PARSE_PASS'))
        foreach ($case in $commandCases) {
            $commandPath = Join-Path $testRoot $case[0]
            $output = @(& $env:ComSpec /d /c "`"$commandPath`"" 2>&1)
            if ($LASTEXITCODE -ne 0 -or
                (@($output | Where-Object { [string]$_ -eq $case[1] }).Count -ne 1)) {
                throw "快捷部署批处理解析失败：$($case[0]); Exit=$LASTEXITCODE; Output=$($output -join ' | ')"
            }
        }
    }
    finally {
        $env:MTTFTEST_QUICKDEPLOY_PARSE_ONLY = $previousParseOnly
    }
    Write-Output 'PASS QuickDeployCommandParse 5/5'

    $previousArgumentProbe = $env:MTTFTEST_QUICKDEPLOY_ARGUMENT_PROBE
    try {
        $env:MTTFTEST_QUICKDEPLOY_ARGUMENT_PROBE = '1'
        $programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
        $machineProgramFiles = if ([string]::IsNullOrWhiteSpace($programFilesX86)) {
            $env:ProgramFiles
        }
        else {
            $programFilesX86
        }
        $expectedInstallRoot = Join-Path $machineProgramFiles 'MTTFTest'
        foreach ($case in @(
                @('一键安装正式版.cmd', 'Install'),
                @('一键修复.cmd', 'Repair'),
                @('一键卸载.cmd', 'Uninstall'))) {
            $commandPath = Join-Path $testRoot $case[0]
            $output = @(& $env:ComSpec /d /c "`"$commandPath`"" 2>&1)
            $expected = "QUICKDEPLOY_ARGUMENT_PROBE_PASS Mode=$($case[1]) Source=$testRoot Root=$expectedInstallRoot"
            if ($LASTEXITCODE -ne 0 -or
                (@($output | Where-Object { [string]$_ -eq $expected }).Count -ne 1)) {
                throw "快捷部署参数绑定失败：$($case[0]); Expected=$expected; Exit=$LASTEXITCODE; Output=$($output -join ' | ')"
            }
        }
    }
    finally {
        $env:MTTFTEST_QUICKDEPLOY_ARGUMENT_PROBE = $previousArgumentProbe
    }
    Write-Output 'PASS QuickDeployArgumentBinding 3/3'

    $nestedPackage = Join-Path $testRoot 'Package'
    [void](New-Item -ItemType Directory -Path $nestedPackage)
    [IO.File]::WriteAllText(
        (Join-Path $nestedPackage 'MTTFTest.exe'),
        'nested-parse-only',
        (New-Object Text.UTF8Encoding($false)))
    try {
        $env:MTTFTEST_QUICKDEPLOY_ARGUMENT_PROBE = '1'
        $commandPath = Join-Path $testRoot '一键安装正式版.cmd'
        $output = @(& $env:ComSpec /d /c "`"$commandPath`"" 2>&1)
        $expected = "QUICKDEPLOY_ARGUMENT_PROBE_PASS Mode=Install Source=$nestedPackage Root=$expectedInstallRoot"
        if ($LASTEXITCODE -ne 0 -or
            (@($output | Where-Object { [string]$_ -eq $expected }).Count -ne 1)) {
            throw "快捷部署没有优先使用嵌套正式包：Expected=$expected; Exit=$LASTEXITCODE; Output=$($output -join ' | ')"
        }
    }
    finally {
        $env:MTTFTEST_QUICKDEPLOY_ARGUMENT_PROBE = $previousArgumentProbe
    }
    Write-Output 'PASS QuickDeployNestedFormalPackage 1/1'

    $uninstallCommandText = [IO.File]::ReadAllText(
        (Join-Path (Join-Path $PSScriptRoot 'QuickDeploy') '一键卸载.cmd'),
        [Text.Encoding]::ASCII)
    $interactiveUninstallLines = @($uninstallCommandText -split "`r?`n" |
        Where-Object {
            $_.Contains('-Mode Uninstall') -and
            -not $_.Contains('-ForceUninstall')
        })
    if ($interactiveUninstallLines.Count -ne 2 -or
        @($interactiveUninstallLines | Where-Object {
            $_.Contains('-Confirm')
        }).Count -ne 0) {
        throw '交互卸载入口不得传递全局 -Confirm，否则内部命令会重复询问。'
    }
    if (-not $uninstallCommandText.Contains('-ForceUninstall -Confirm:$false')) {
        throw '非交互卸载入口缺少明确的免确认参数。'
    }
    if ([regex]::Matches($installerText, 'Read-Host').Count -ne 1 -or
        $installerText.Contains('PromptForChoice')) {
        throw '安装器必须且只能包含一次交互式卸载确认。'
    }
    Write-Output 'PASS QuickDeploySingleUninstallPrompt 1/1'

    $previousConfirmationProbe = $env:MTTFTEST_QUICKDEPLOY_CONFIRMATION_PROBE
    try {
        $env:MTTFTEST_QUICKDEPLOY_CONFIRMATION_PROBE = '1'
        $confirmationCaseIndex = 0
        foreach ($case in @(
                @('Y', 'True'),
                @('A', 'True'),
                @('N', 'False'),
                @('', 'False'))) {
            $confirmationCaseIndex++
            $confirmationInput = Join-Path $testRoot `
                "confirmation-$confirmationCaseIndex-input.txt"
            $confirmationStdOut = Join-Path $testRoot `
                "confirmation-$confirmationCaseIndex-stdout.txt"
            $confirmationStdErr = Join-Path $testRoot `
                "confirmation-$confirmationCaseIndex-stderr.txt"
            [IO.File]::WriteAllText(
                $confirmationInput,
                [string]$case[0] + [Environment]::NewLine,
                [Text.Encoding]::ASCII)
            $confirmationInstaller = Join-Path $testRoot 'QuickDeploy-Installer.ps1'
            $confirmationInstallRoot = Join-Path $env:ProgramFiles 'MTTFTest'
            $confirmationArguments =
                "-NoProfile -ExecutionPolicy Bypass -File `"$confirmationInstaller`" " +
                "-Mode Uninstall -SourceDirectory `"$testRoot`" " +
                "-InstallRoot `"$confirmationInstallRoot`""
            $confirmationProcess = Start-Process `
                -FilePath 'powershell.exe' `
                -ArgumentList $confirmationArguments `
                -RedirectStandardInput $confirmationInput `
                -RedirectStandardOutput $confirmationStdOut `
                -RedirectStandardError $confirmationStdErr `
                -WindowStyle Hidden `
                -Wait `
                -PassThru
            $output = @(
                @([IO.File]::ReadAllLines(
                    $confirmationStdOut,
                    [Text.Encoding]::Default)) +
                @([IO.File]::ReadAllLines(
                    $confirmationStdErr,
                    [Text.Encoding]::Default)))
            $expected = "QUICKDEPLOY_CONFIRMATION_PROBE_PASS Confirmed=$($case[1])"
            if ($confirmationProcess.ExitCode -ne 0 -or
                -not (($output -join [Environment]::NewLine).Contains($expected))) {
                throw "卸载确认输入校验失败：Input='$($case[0])'; Expected=$expected; Exit=$($confirmationProcess.ExitCode); Output=$($output -join ' | ')"
            }
        }
    }
    finally {
        $env:MTTFTEST_QUICKDEPLOY_CONFIRMATION_PROBE = $previousConfirmationProbe
    }
    Write-Output 'PASS QuickDeployConfirmationInput 4/4'
}
finally {
    if (Test-Path -LiteralPath $testRoot -PathType Container) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
