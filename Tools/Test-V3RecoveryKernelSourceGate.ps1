[CmdletBinding()]
param([string]$RepositoryRoot = '')

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
}
else {
    $RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
}

function Read-Source([string]$RelativePath) {
    $path = Join-Path $RepositoryRoot $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "V3 source gate missing file: $RelativePath"
    }
    return [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8)
}

$program = Read-Source 'MTTfTest\Program.cs'
$uiClient = (Read-Source 'MTTfTest\V3EngineHostClient.cs') +
            (Read-Source 'MTTfTest\V3EngineClientForm.cs')
$engineRuntime = Read-Source 'MTTFTest.EngineHost\EngineHostRuntime.cs'
$engineComposition = Read-Source 'MTTFTest.EngineHost\PhysicalEngineRuntime.cs'
$supervisorKernel = Read-Source 'MTTFTest.Watchdog\SupervisorRecoveryKernelService.cs'
$safetyProofExecutor = Read-Source 'MTTFTest.Watchdog\V3SafetyProofExecutor.cs'
$engineReplacer = Read-Source 'MTTFTest.Watchdog\V3EngineProcessReplacer.cs'
$watchdogProgram = Read-Source 'MTTFTest.Watchdog\Program.cs'
$legacyTestHost = Read-Source 'MTTFTest.Watchdog\LegacyWatchdogTestHost.cs'
$supervisorHost = Read-Source 'MTTFTest.Watchdog\SupervisorServiceHost.cs'

foreach ($forbidden in @(
        'new Main_Frm', 'new FrmEpbMainMonitor',
        'RecoveryProcessBootstrap.', 'UnattendedRecoveryCoordinator.RequestFatalRestart')) {
    if ($program.Contains($forbidden)) {
        throw "V3 entry still references legacy recovery: $forbidden"
    }
}
foreach ($forbidden in @(
        'using Controller', 'using IO.NI', 'EpbManager',
        'TwoDeviceAiAcquirer', 'DoController', 'AoController', 'StopAll')) {
    if ($uiClient.Contains($forbidden)) {
        throw "V3 UI still owns hardware or recovery entry: $forbidden"
    }
}
if ($engineRuntime.Contains('Process.Start(') -or
    $engineComposition.Contains('Process.Start(')) {
    throw 'EngineHost must not start or replace processes.'
}
if (-not $engineRuntime.Contains('RecoveryCommandRequiresLocalSystemSupervisor') -or
    -not $engineRuntime.Contains('OperatorCommandRequiresRecoveryKernelGate')) {
    throw 'EngineHost command plane can bypass the LocalSystem Recovery Kernel.'
}
if (-not $engineComposition.Contains('FaultObserved?.Invoke') -or
    -not $engineRuntime.Contains('It neither calls StopAll')) {
    throw 'EngineHost fact-only fault boundary is missing.'
}
if (-not $supervisorKernel.Contains('new RecoveryCoordinator') -or
    -not $supervisorKernel.Contains('new V3SafetyProofExecutor') -or
    -not $supervisorKernel.Contains('AcceptSafetyProof') -or
    -not $safetyProofExecutor.Contains('MTTFTest.SafetyAgent.exe') -or
    -not $safetyProofExecutor.Contains('SupervisorSafetyAuthorityStore')) {
    throw 'Supervisor Recovery Kernel or independent safety gate is missing.'
}
if (-not $supervisorKernel.Contains('OperatorCommandKind.Start') -or
    -not $supervisorKernel.Contains('V3EngineProcessReplacer') -or
    -not $engineReplacer.Contains('PackageSlotDescriptorStore.TryActivateLastKnownGood') -or
    -not $engineReplacer.Contains('SessionAgentLaunchClient.Start')) {
    throw 'V3 start transaction or bounded EngineHost/LKG replacement is missing.'
}
if ($watchdogProgram.Contains('WatchdogHost.Run(') -or
    -not $watchdogProgram.Contains('LegacySessionHostDisabledInV3') -or
    -not $supervisorHost.Contains('LegacySessionRecordIgnored') -or
    -not $supervisorHost.Contains('LegacySessionRegistrationRejected')) {
    throw 'V2 session-host recovery authority is still reachable in V3 production.'
}
if (-not $legacyTestHost.Contains('Tests" +') -or
    -not $legacyTestHost.Contains('AdaptiveControlTests.exe') -or
    -not $legacyTestHost.Contains('File.Exists(regressionHarness)')) {
    throw 'Legacy Watchdog regression adapter is not restricted to repository test output.'
}

Write-Output 'PASS V3RecoveryKernelSourceGate 1/1'
