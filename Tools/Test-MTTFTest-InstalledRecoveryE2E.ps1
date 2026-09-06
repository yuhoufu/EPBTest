#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDirectory,
    [Parameter(Mandatory = $true)]
    [switch]$ConfirmIsolatedEnvironment,
    [string]$EvidenceDirectory = ''
)

$ErrorActionPreference = 'Stop'
$serviceName = 'MTTFTestSupervisor'
$taskName = 'MTTFTestSessionAgent'
$package = [IO.Path]::GetFullPath($PackageDirectory)
$programDataRoot = Join-Path $env:ProgramData 'MTTFTest'
$e2eRoot = Join-Path $programDataRoot 'UnattendedRecoveryE2E'
$projectRoot = Join-Path $e2eRoot 'Project'
$mainEvents = Join-Path $projectRoot 'e2e-main-events.log'
$agentEvents = Join-Path $projectRoot 'e2e-safety-agent-events.log'

if (-not $ConfirmIsolatedEnvironment) {
    throw '必须显式确认在无生产服务/数据的隔离工控机或虚拟机中执行。'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '安装态 E2E 必须在管理员 PowerShell 中执行。'
}
foreach ($required in @(
        'MTTFTest.exe', 'MTTFTest.Watchdog.exe',
        'MTTFTest.SessionAgent.exe', 'MTTFTest.SafetyAgent.exe',
        'MTTFTest.Watchdog.Protocol.dll', 'MTTFTest.Watchdog.Client.dll',
        'MTTFTest.UnattendedMode.required', 'e2e-package-identity.json',
        'Config\AlarmConfig.xml')) {
    if (-not (Test-Path -LiteralPath (Join-Path $package $required) -PathType Leaf)) {
        throw "E2E 测试包缺少：$required"
    }
}
$packageIdentity = Get-Content `
    -LiteralPath (Join-Path $package 'e2e-package-identity.json') `
    -Raw | ConvertFrom-Json
if (-not $packageIdentity.testOnly -or $packageIdentity.productionRelease -or
    $packageIdentity.version -ne '2.17.3.0') {
    throw '拒绝执行未明确标识 testOnly 的 E2E 包。'
}
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    throw "隔离机已存在 $serviceName，拒绝复用或覆盖。"
}
if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    throw "隔离机已存在 $taskName，拒绝复用或覆盖。"
}
if (Test-Path -LiteralPath $programDataRoot) {
    throw "隔离机已存在 $programDataRoot，拒绝覆盖任何既有 ProgramData。"
}
foreach ($name in @(
        'MTTFTest', 'MTTFTest.Watchdog',
        'MTTFTest.SessionAgent', 'MTTFTest.SafetyAgent')) {
    if (Get-Process -Name $name -ErrorAction SilentlyContinue) {
        throw "隔离机仍有 $name 进程，拒绝开始测试。"
    }
}

if ([string]::IsNullOrWhiteSpace($EvidenceDirectory)) {
    $EvidenceDirectory = Join-Path $package (
        'E2E-Evidence-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
}
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
$createdProgramData = $false
$createdService = $false
$createdTask = $false
$startedUtc = [DateTime]::UtcNow
$result = [ordered]@{
    schemaVersion = 1
    version = '2.17.3.0'
    startedUtc = $startedUtc.ToString('O')
    account = $identity.Name
    package = $package
    tests = @()
    passed = $false
}

function Add-Result([string]$Name, [bool]$Passed, [string]$Detail) {
    $result.tests += [ordered]@{
        name = $Name
        passed = $Passed
        utc = [DateTime]::UtcNow.ToString('O')
        detail = $Detail
    }
    if (-not $Passed) { throw "$Name 失败：$Detail" }
}

function Wait-Until([scriptblock]$Condition, [int]$TimeoutSeconds,
        [string]$Failure) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $value = & $Condition
        if ($null -ne $value -and $value -ne $false) { return $value }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $Failure
}

function Get-ExactProcess([string]$Name, [string]$ExecutablePath) {
    $expected = [IO.Path]::GetFullPath($ExecutablePath)
    return @(Get-Process -Name $Name -ErrorAction SilentlyContinue |
        Where-Object {
            try {
                [IO.Path]::GetFullPath($_.MainModule.FileName) -eq $expected
            } catch { $false }
        })
}

function Stop-ExactProcess([Diagnostics.Process]$Process) {
    if ($null -eq $Process) { throw '精确进程缺失。' }
    $pidValue = $Process.Id
    $startTicks = $Process.StartTime.ToUniversalTime().Ticks
    $current = [Diagnostics.Process]::GetProcessById($pidValue)
    try {
        if ($current.StartTime.ToUniversalTime().Ticks -ne $startTicks) {
            throw "PID $pidValue 已复用，拒绝终止。"
        }
        $current.Kill()
        if (-not $current.WaitForExit(10000)) {
            throw "PID $pidValue 未在10秒内退出。"
        }
    } finally { $current.Dispose() }
    return "PID=$pidValue;StartUtcTicks=$startTicks"
}

# Suspending the exact retained handle injects a live-but-unresponsive process,
# unlike Kill, and exercises the production health probe and replacement path.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class EpbRecoveryE2EProcessFault
{
    [DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr handle);
    [DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr handle);
}
'@

function Wait-ReplacementAfterHang([Diagnostics.Process]$Process,
        [string]$Name, [string]$Path, [int]$TimeoutSeconds) {
    [void]$Process.Handle
    $previousPid = $Process.Id
    if ([EpbRecoveryE2EProcessFault]::NtSuspendProcess($Process.Handle) -ne 0) {
        throw "无法注入无响应故障 PID=$previousPid"
    }
    try {
        return Wait-Until {
            @(Get-ExactProcess $Name $Path | Where-Object { $_.Id -ne $previousPid }) |
                Select-Object -First 1
        } $TimeoutSeconds "$Name 无响应后未替换 PID=$previousPid"
    }
    finally {
        if (-not $Process.HasExited) {
            [void][EpbRecoveryE2EProcessFault]::NtResumeProcess($Process.Handle)
        }
    }
}

try {
    New-Item -ItemType Directory -Path $projectRoot -Force | Out-Null
    $createdProgramData = $true
    New-Item -ItemType Directory `
        -Path (Join-Path $programDataRoot 'Config') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $package 'Config\AlarmConfig.xml') `
        -Destination (Join-Path $programDataRoot 'Config\AlarmConfig.xml') -Force

    $watchdog = Join-Path $package 'MTTFTest.Watchdog.exe'
    & sc.exe create $serviceName `
        'binPath=' "`"$watchdog`"" `
        'start=' 'auto' 'obj=' 'LocalSystem' | Out-Host
    if ($LASTEXITCODE -ne 0) { throw '创建 LocalSystem Supervisor 失败。' }
    $createdService = $true
    & sc.exe failure $serviceName 'reset=' '0' `
        'actions=' 'restart/5000/restart/15000/restart/60000' | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw '配置 Supervisor SCM failure actions 失败。'
    }
    & sc.exe failureflag $serviceName 1 | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw '配置 Supervisor SCM failureflag 失败。'
    }
    $failurePolicy = @(& sc.exe qfailure $serviceName 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw '读取 Supervisor SCM failure actions 失败。'
    }
    $failurePolicy | Out-Host
    $failurePolicy | Set-Content `
        -LiteralPath (Join-Path $projectRoot 'e2e-scm-policy.txt') `
        -Encoding UTF8
    & sc.exe start $serviceName | Out-Host
    if ($LASTEXITCODE -ne 0) { throw '启动 LocalSystem Supervisor 失败。' }
    Wait-Until { (Get-Service -Name $serviceName).Status -eq 'Running' } `
        20 'Supervisor 未进入 Running。' | Out-Null

    $sessionAgent = Join-Path $package 'MTTFTest.SessionAgent.exe'
    $action = New-ScheduledTaskAction -Execute $sessionAgent
    $logonTrigger = New-ScheduledTaskTrigger -AtLogOn -User $identity.Name
    $keepaliveTrigger = New-ScheduledTaskTrigger -Once `
        -At ((Get-Date).AddMinutes(1)) `
        -RepetitionInterval ([TimeSpan]::FromMinutes(1))
    $settings = New-ScheduledTaskSettingsSet -RestartCount 255 `
        -RestartInterval ([TimeSpan]::FromMinutes(1)) `
        -ExecutionTimeLimit ([TimeSpan]::Zero) `
        -MultipleInstances IgnoreNew
    $taskPrincipal = New-ScheduledTaskPrincipal -UserId $identity.Name `
        -LogonType Interactive -RunLevel Highest
    Register-ScheduledTask -TaskName $taskName -Action $action `
        -Trigger @($logonTrigger, $keepaliveTrigger) `
        -Settings $settings -Principal $taskPrincipal `
        -Force | Out-Null
    $createdTask = $true
    Export-ScheduledTask -TaskName $taskName | Set-Content `
        -LiteralPath (Join-Path $projectRoot 'e2e-session-agent-task.xml') `
        -Encoding UTF8
    Start-ScheduledTask -TaskName $taskName
    $agent = Wait-Until {
        @(Get-ExactProcess 'MTTFTest.SessionAgent' $sessionAgent) |
            Select-Object -First 1
    } 20 'SessionAgent 未启动。'

    $launcher = Start-Process -FilePath $watchdog `
        -ArgumentList @('--launch-main') -WindowStyle Hidden -PassThru -Wait
    if ($launcher.ExitCode -ne 0) {
        throw "Supervisor launcher 失败：Exit=$($launcher.ExitCode)"
    }
    $mainPath = Join-Path $package 'MTTFTest.exe'
    $initialMain = Wait-Until {
        @(Get-ExactProcess 'MTTFTest' $mainPath) | Select-Object -First 1
    } 30 '初始测试主进程未启动。'
    Wait-Until {
        (Test-Path -LiteralPath $mainEvents) -and
        (Get-Content -LiteralPath $mainEvents -Raw).Contains('InitialMainAttached')
    } 30 '初始主进程未完成 Watchdog Attached。' | Out-Null
    Add-Result 'InitialSupervisorLaunch' $true (
        "PID=$($initialMain.Id);StartUtcTicks=" +
        $initialMain.StartTime.ToUniversalTime().Ticks)

    $supervisorPid = [int](Get-CimInstance Win32_Service -Filter "Name='$serviceName'").ProcessId
    $hostProcess = @(Get-ExactProcess 'MTTFTest.Watchdog' $watchdog |
        Where-Object { $_.Id -ne $supervisorPid }) | Select-Object -First 1
    $oldHostPid = $hostProcess.Id
    Stop-ExactProcess $hostProcess | Out-Null
    $hostProcess = Wait-Until {
        @(Get-ExactProcess 'MTTFTest.Watchdog' $watchdog |
            Where-Object { $_.Id -ne $supervisorPid -and $_.Id -ne $oldHostPid }) |
            Select-Object -First 1
    } 30 'sidecar 退出后未恢复持久化监督身份。'
    Add-Result 'KillSidecarAndRestoreOwnership' $true "OldPID=$oldHostPid;NewPID=$($hostProcess.Id)"
    $oldHostPid = $hostProcess.Id
    # Exclude the running service when waiting for its sidecar replacement.
    [void]$hostProcess.Handle
    if ([EpbRecoveryE2EProcessFault]::NtSuspendProcess($hostProcess.Handle) -ne 0) {
        throw '无法注入 sidecar 无响应故障。'
    }
    try {
        $healthyHost = Wait-Until {
            @(Get-ExactProcess 'MTTFTest.Watchdog' $watchdog |
                Where-Object { $_.Id -ne $supervisorPid -and $_.Id -ne $oldHostPid }) |
                Select-Object -First 1
        } 90 'sidecar 无响应后未由 Supervisor 恢复。'
        Add-Result 'HangSidecarAndRestoreOwnership' $true "OldPID=$oldHostPid;NewPID=$($healthyHost.Id)"
    }
    finally {
        if (-not $hostProcess.HasExited) {
            [void][EpbRecoveryE2EProcessFault]::NtResumeProcess($hostProcess.Handle)
        }
    }

    $service = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    $oldServicePid = [int]$service.ProcessId
    $oldService = [Diagnostics.Process]::GetProcessById($oldServicePid)
    $serviceIdentity = Stop-ExactProcess $oldService
    $newService = Wait-Until {
        $candidate = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
        if ($candidate.State -eq 'Running' -and
            [int]$candidate.ProcessId -gt 0 -and
            [int]$candidate.ProcessId -ne $oldServicePid) { $candidate }
    } 30 'Supervisor 被强杀后未由 SCM 自动恢复。'
    Add-Result 'KillSupervisorAndRecover' $true (
        "$serviceIdentity;NewPID=$($newService.ProcessId)")

    $oldAgentPid = $agent.Id
    $agentIdentity = Stop-ExactProcess $agent
    $newAgent = Wait-Until {
        @(Get-ExactProcess 'MTTFTest.SessionAgent' $sessionAgent |
            Where-Object { $_.Id -ne $oldAgentPid }) | Select-Object -First 1
    } 90 'SessionAgent 被强杀后未由登录任务自动恢复。'
    Get-ScheduledTaskInfo -TaskName $taskName | ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath (
            Join-Path $projectRoot 'e2e-session-agent-task-after-kill.json') `
            -Encoding UTF8
    Add-Result 'KillSessionAgentAndRecover' $true (
        "$agentIdentity;NewPID=$($newAgent.Id)")

    $hungAgentPid = $newAgent.Id
    $healthyAgent = Wait-ReplacementAfterHang $newAgent 'MTTFTest.SessionAgent' $sessionAgent 90
    Add-Result 'HangSessionAgentAndRecover' $true "OldPID=$hungAgentPid;NewPID=$($healthyAgent.Id)"

    $mainKilledUtc = [DateTime]::UtcNow
    $oldMainPid = $initialMain.Id
    $mainIdentity = Stop-ExactProcess $initialMain
    $safetyPath = Join-Path $package 'MTTFTest.SafetyAgent.exe'
    $safetyAgent = Wait-Until {
        @(Get-ExactProcess 'MTTFTest.SafetyAgent' $safetyPath) |
            Select-Object -First 1
    } 30 '主进程强杀后 SafetyAgent 未启动。'
    Wait-Until {
        (Test-Path -LiteralPath $agentEvents) -and
        (Get-Content -LiteralPath $agentEvents -Raw).Contains('MotorOffConfirmed')
    } 5 '无硬件后端未形成 MotorOff 证明。' | Out-Null
    $agentTimeline = @(Get-Content -LiteralPath $agentEvents | ForEach-Object {
        $parts = $_.Split('|')
        if ($parts.Count -ge 2) {
            [pscustomobject]@{
                Utc = [DateTime]::Parse(
                    $parts[0], [Globalization.CultureInfo]::InvariantCulture,
                    [Globalization.DateTimeStyles]::RoundtripKind)
                Event = $parts[1]
            }
        }
    })
    $agentStarted = $agentTimeline |
        Where-Object Event -eq 'SafetyAgentStarted' | Select-Object -First 1
    $motorOff = $agentTimeline |
        Where-Object Event -eq 'MotorOffConfirmed' | Select-Object -First 1
    $motorOffMs = if ($null -ne $agentStarted -and $null -ne $motorOff) {
        ($motorOff.Utc - $agentStarted.Utc).TotalMilliseconds
    } else { [double]::PositiveInfinity }
    Add-Result 'MotorOffWithin250ms' ($motorOffMs -le 250) (
        'ElapsedMs=' + [Math]::Round($motorOffMs, 1))
    $safetyStartUtc = [DateTime]::UtcNow
    $safetyIdentity = Stop-ExactProcess $safetyAgent
    Wait-Until {
        (Test-Path -LiteralPath $agentEvents) -and
        (Get-Content -LiteralPath $agentEvents -Raw).Contains('SafetyProofCompleted')
    } 30 'SafetyAgent 强杀后未幂等续跑完成安全证明。' | Out-Null
    $proofUtc = [DateTime]::UtcNow
    Add-Result 'KillSafetyAgentAndResumeAuthority' $true (
        "$safetyIdentity;ProofElapsedMs=" +
        [Math]::Round(($proofUtc - $safetyStartUtc).TotalMilliseconds))
    $proofFromFailureMs = ($proofUtc - $mainKilledUtc).TotalMilliseconds
    Add-Result 'IndependentSafetyProofWithin30Seconds' `
        ($proofFromFailureMs -le 30000) `
        ('ElapsedMs=' + [Math]::Round($proofFromFailureMs))

    $recoveredMain = Wait-Until {
        @(Get-ExactProcess 'MTTFTest' $mainPath |
            Where-Object { $_.Id -ne $oldMainPid }) | Select-Object -First 1
    } 180 '180秒内未自动拉起新的主进程。'
    Wait-Until {
        (Get-Content -LiteralPath $mainEvents -Raw).Contains(
            'RecoveryMainAttached')
    } 30 '恢复主进程未完成精确 Attached。' | Out-Null
    $commitDeadline = [DateTime]::UtcNow.AddSeconds(30)
    $committed = $false
    do {
        $authorityFiles = @(Get-ChildItem `
            -LiteralPath (Join-Path $projectRoot 'WatchdogSessions') `
            -Filter 'session-*.relaunch.json' -File -ErrorAction SilentlyContinue)
        foreach ($file in $authorityFiles) {
            try {
                $record = Get-Content -LiteralPath $file.FullName -Raw |
                    ConvertFrom-Json
                if ($record.State -eq 'Committed' -or
                    [int]$record.State -eq 5) { $committed = $true }
            } catch { }
        }
        if (-not $committed) { Start-Sleep -Milliseconds 100 }
    } while (-not $committed -and [DateTime]::UtcNow -lt $commitDeadline)
    Add-Result 'RecoveryFirstCycleCommitted' $committed (
        "NewPID=$($recoveredMain.Id);ElapsedMs=" +
        [Math]::Round(([DateTime]::UtcNow - $mainKilledUtc).TotalMilliseconds))

    $mainCount = @(Get-ExactProcess 'MTTFTest' $mainPath).Count
    Add-Result 'ExactlyOneMainProcess' ($mainCount -eq 1) "Count=$mainCount"
    $activePermits = 0
    foreach ($file in $authorityFiles) {
        try {
            $record = Get-Content -LiteralPath $file.FullName -Raw |
                ConvertFrom-Json
            if ($record.State -in @('Approved', 'LaunchIntent', 'Started',
                    'Attached', 'Committed', 1, 2, 3, 4, 5)) {
                $activePermits++
            }
        } catch { }
    }
    Add-Result 'ExactlyOneEffectivePermit' ($activePermits -eq 1) `
        "Count=$activePermits"
    $result.passed = $true
}
finally {
    try {
        New-Item -ItemType Directory -Path $evidence -Force | Out-Null
        $result.completedUtc = [DateTime]::UtcNow.ToString('O')
        $result | ConvertTo-Json -Depth 10 | Set-Content `
            -LiteralPath (Join-Path $evidence 'e2e-result.json') -Encoding UTF8
        if (Test-Path -LiteralPath $programDataRoot) {
            Copy-Item -LiteralPath $programDataRoot `
                -Destination (Join-Path $evidence 'ProgramData-MTTFTest') `
                -Recurse -Force
        }
    }
    finally {
        if ($createdTask) {
        try { Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue }
        catch { }
        try { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false }
        catch { }
    }
    foreach ($entry in @(
            @{ Name = 'MTTFTest'; Path = (Join-Path $package 'MTTFTest.exe') },
            @{ Name = 'MTTFTest.Watchdog'; Path = (Join-Path $package 'MTTFTest.Watchdog.exe') },
            @{ Name = 'MTTFTest.SessionAgent'; Path = (Join-Path $package 'MTTFTest.SessionAgent.exe') },
            @{ Name = 'MTTFTest.SafetyAgent'; Path = (Join-Path $package 'MTTFTest.SafetyAgent.exe') })) {
        foreach ($process in @(Get-ExactProcess $entry.Name $entry.Path)) {
            try { Stop-ExactProcess $process | Out-Null } catch { }
        }
    }
    if ($createdService) {
        try { & sc.exe stop $serviceName | Out-Null } catch { }
        try { & sc.exe delete $serviceName | Out-Null } catch { }
    }
        if ($createdProgramData -and (Test-Path -LiteralPath $programDataRoot)) {
            $resolvedProgramData = [IO.Path]::GetFullPath($programDataRoot)
            $expectedProgramData = [IO.Path]::GetFullPath(
                (Join-Path $env:ProgramData 'MTTFTest'))
            if ($resolvedProgramData -eq $expectedProgramData -and
                $resolvedProgramData -ne [IO.Path]::GetPathRoot($resolvedProgramData)) {
                Remove-Item -LiteralPath $resolvedProgramData -Recurse -Force
            }
        }
    }
}

if (-not $result.passed) { throw '安装态 E2E 未通过。' }
Write-Output "PASS InstalledLocalSystemRecoveryE2E Evidence=$evidence"
