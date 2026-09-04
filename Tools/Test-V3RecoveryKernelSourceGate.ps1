[CmdletBinding()]
param([string]$RepositoryRoot = '', [string]$CompiledSourcesPath = '')

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
if (-not [string]::IsNullOrWhiteSpace($CompiledSourcesPath)) {
    $uiPaths = @(Get-Content -LiteralPath $CompiledSourcesPath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}
else {
    [xml]$uiProject = Read-Source 'MTTfTest\MTTfTest.csproj'
    $removed = @($uiProject.Project.ItemGroup.Compile | Where-Object Remove |
        ForEach-Object { ([string]$_.Remove).Split(';') })
    $uiPaths = @($uiProject.Project.ItemGroup.Compile | Where-Object Include | ForEach-Object {
        $relative = [string]$_.Include
        if (-not $relative.Contains('$(')) {
            $excluded = $false
            foreach ($pattern in $removed) { if ($relative -like $pattern) { $excluded = $true; break } }
            if (-not $excluded) { Join-Path (Join-Path $RepositoryRoot 'MTTfTest') $relative }
        }
    })
}
$uiClient = ($uiPaths | ForEach-Object { [IO.File]::ReadAllText($_, [Text.Encoding]::UTF8) }) -join "`n"
foreach ($legacyName in @('WatchdogRuntime.cs', 'FrmEpbMainMonitor.UnattendedRecovery.cs',
        'Main_Frm.UnattendedRecovery.cs', 'PressureCalibrationHardware.cs')) {
    if (@($uiPaths | Where-Object { [IO.Path]::GetFileName($_) -eq $legacyName }).Count -gt 0) {
        throw "V3 production UI compiles a legacy ownership module: $legacyName"
    }
}
$engineRuntime = Read-Source 'MTTFTest.EngineHost\EngineHostRuntime.cs'
$engineProgram = Read-Source 'MTTFTest.EngineHost\Program.cs'
$engineIntegration = Read-Source 'Tests\EngineHostIntegrationTests\EngineHostIntegrationTests.cs'
$engineComposition = Read-Source 'MTTFTest.EngineHost\PhysicalEngineRuntime.cs'
$supervisedQualification = Read-Source 'Controller\EpbManager.SupervisedQualification.cs'
$batchStart = Read-Source 'Controller\EpbManager.BatchStart.cs'
if ($engineComposition.Contains('StartBatchFromGracefulCheckpointAsync(') -or
    $engineComposition.Contains('QualifiedFormalStarted') -or
    -not $engineComposition.Contains('_manager.PrepareBatchQualificationAsync(') -or
    -not $engineComposition.Contains('_manager.CommitPreparedFormalRunAsync(') -or
    -not $batchStart.Contains('supervisedQualification != null') -or
    -not $batchStart.Contains('CompletePauseSafetyBoundaryAsync(activeChannels, true, sessionToken)') -or
    -not $supervisedQualification.Contains('_supervisedFormal.CommitAsync(command, token)') -or
    $supervisedQualification.Contains('StartBatchFromGracefulCheckpointAsync(') -or
    $supervisedQualification.Contains('EnsureChannelsReadyAsync(')) {
    throw 'V3 qualification must wait for a separately bound Supervisor formal-run command; preflight cannot rebuild DAQ.'
}
$operatorCoordinator = Read-Source 'Recovery.Kernel\RecoveryCoordinator.OperatorCommands.cs'
$recoveryCoordinator = Read-Source 'Recovery.Kernel\RecoveryCoordinator.cs'
$monitorSession = Read-Source 'MTTfTest\V3\V3MonitorSession.cs'
$monitorForm = Read-Source 'MTTfTest\V3\FrmEpbMainMonitor.Client.cs'
$checkpointStore = Read-Source 'MTTFTest.EngineHost\EngineRunCheckpointStore.cs'
if (-not $operatorCoordinator.Contains('AdmitQualificationRetry(command, engine)') -or
    -not $operatorCoordinator.Contains('QualificationRetryRequiresExactChannelIsolation') -or
    -not $operatorCoordinator.Contains('retryBudget.ActiveQualificationAttempts++') -or
    -not $operatorCoordinator.Contains('RecoveryCommandKind.DisableOutputs') -or
    -not $operatorCoordinator.Contains('QualificationRetryEligibleScopes()') -or
    -not $recoveryCoordinator.Contains('MaximumConfirmedHardwareQualificationAttempts { get; set; } = 1') -or
    -not $recoveryCoordinator.Contains('RecoveryCommandKind.RunPassivePreflight') -or
    -not $recoveryCoordinator.Contains('OperatorQualificationRetryFailed:') -or
    -not $engineComposition.Contains('RecordQualificationRetryReintegrated') -or
    -not $engineComposition.Contains('InheritedProjectIsolationRequiresProjectTransaction') -or
    -not $checkpointStore.Contains('QualificationRetryCheckpointNotQualifiedOrIsolated') -or
    -not $monitorSession.Contains('EngineUiContract.ChannelQualificationRecovery') -or
    -not $monitorSession.Contains('OperatorCommandKind.RetryQualification') -or
    -not $monitorForm.Contains('ConfirmQualificationRetry')) {
    throw 'V3 isolated-channel qualification retry lost its one-shot safety proof, exact-scope or UI confirmation fence.'
}
$engineProgress = Read-Source 'MTTFTest.EngineHost\PhysicalEngineRuntime.Progress.cs'
$supervisorKernel = Read-Source 'MTTFTest.Watchdog\SupervisorRecoveryKernelService.cs'
$safetyProofExecutor = Read-Source 'MTTFTest.Watchdog\V3SafetyProofExecutor.cs'
$engineReplacer = Read-Source 'MTTFTest.Watchdog\V3EngineProcessReplacer.cs'
$watchdogProgram = Read-Source 'MTTFTest.Watchdog\Program.cs'
$legacyTestHost = Read-Source 'MTTFTest.Watchdog\LegacyWatchdogTestHost.cs'
$supervisorHost = Read-Source 'MTTFTest.Watchdog\SupervisorServiceHost.cs'
$enginePipeClient = Read-Source 'MTTFTest.Watchdog\EngineHostPipeClient.cs'
if ($enginePipeClient -match 'pipe\.(ReadTimeout|WriteTimeout)\s*=' -or
    -not $enginePipeClient.Contains('CancellationTokenSource(timeoutMilliseconds)') -or
    -not $enginePipeClient.Contains('PipeOptions.Asynchronous') -or
    -not $engineIntegration.Contains('ProductionSupervisorReadsEngineHostSnapshot')) {
    throw 'Supervisor EngineHost pipe must use a tested bounded asynchronous transport.'
}

foreach ($forbidden in @(
        'RecoveryProcessBootstrap.', 'UnattendedRecoveryCoordinator.RequestFatalRestart', 'FirstRunBootstrap.')) {
    if ($program.Contains($forbidden)) {
        throw "V3 entry still references legacy recovery: $forbidden"
    }
}
foreach ($forbidden in @(
        'using Controller', 'using IO.NI', 'EpbManager',
        'using PowerSupply.Core', 'using ZlgCanComm', 'NationalInstruments.DAQmx',
        'TwoDeviceAiAcquirer', 'DoController', 'AoController', 'StopAll', 'WatchdogRuntime.',
        'ResetPermanentAlarm(')) {
    if ($uiClient.Contains($forbidden)) {
        throw "V3 UI still owns hardware or recovery entry: $forbidden"
    }
}
$featurePlayback = Read-Source 'MTTfTest\FrmPlayBack.cs'
$rawPlayback = Read-Source 'MTTfTest\FrmRawPlayBack.cs'
$historyReader = Read-Source 'MTTfTest\Playback\HistoryFileReader.cs'
$historyLayout = Read-Source 'MTTfTest\Playback\HistoryRawLayout.cs'
$featurePlaybackClient = Read-Source 'MTTfTest\FrmPlayBack.HistoryClient.cs'
$rawPlaybackClient = Read-Source 'MTTfTest\FrmRawPlayBack.HistoryClient.cs'
$playbackCompileNames = @($uiPaths | ForEach-Object { [IO.Path]::GetFileName($_) })
foreach ($required in @('HistoryFileReader.cs', 'HistoryRawLayout.cs', 'HistoryDirectoryReader.cs',
        'FrmPlayBack.HistoryClient.cs', 'FrmRawPlayBack.HistoryClient.cs')) {
    if ($playbackCompileNames -notcontains $required) { throw "V3 production UI omits the bounded playback component: $required" }
}
$playbackChecks = [ordered]@{
    'feature-no-int-file-length' = -not $featurePlayback.Contains('checked((int)fs.Length)')
    'raw-no-int-file-length' = -not $rawPlayback.Contains('checked((int)fs.Length)')
    'raw-no-fixed-device-width' = -not $rawPlayback.Contains('daqRawLogRecordLens = selectedChannel <= 8')
    'feature-bounded-folder' = $featurePlayback.Contains('await ChooseHistoryDirectoryAsync()')
    'feature-bounded-reader' = $featurePlayback.Contains('ReadFeatureHistory((string)e.Argument)')
    'raw-bounded-folder' = $rawPlayback.Contains('await ChooseHistoryDirectoryAsync()')
    'raw-bounded-reader' = $rawPlayback.Contains('ReadRawHistory((string)e.Argument)')
    'display-bound' = $historyReader.Contains('MaximumDisplayPoints = 40000')
    'zoom-detail-reader' = $historyReader.Contains('ReadWindow(HistoryReadResult source')
    'source-identity' = $historyReader.Contains('SourceFileIdentity')
    'owned-temporary-export' = $historyReader.Contains('.epb-export-')
    'atomic-overwrite' = $historyReader.Contains('File.Replace(temporary, target, null)')
    'feature-full-export' = $featurePlaybackClient.Contains('HistoryFileReader.Export(source')
    'raw-full-export' = $rawPlaybackClient.Contains('HistoryFileReader.Export(source')
    'frozen-ai-config' = $historyLayout.Contains('"Config", "AIConfig.xml"')
    'ordered-variable-layout' = $historyLayout.Contains('OrderBy(r => r.') -and
        $historyLayout.Contains('ChannelCount = group.Length') -and
        $historyLayout.Contains('ChannelIndex = Array.IndexOf(group, selected[0])')
}
foreach ($playbackCheck in $playbackChecks.GetEnumerator()) {
    if (-not $playbackCheck.Value) { throw "V3 original playback gate failed: $($playbackCheck.Key)" }
}
if ($engineRuntime.Contains('Process.Start(') -or
    $engineComposition.Contains('Process.Start(')) {
    throw 'EngineHost must not start or replace processes.'
}
if (-not $engineRuntime.Contains('RecoveryCommandRequiresLocalSystemSupervisor') -or
    -not $engineRuntime.Contains('OperatorCommandRequiresRecoveryKernelGate')) {
    throw 'EngineHost command plane can bypass the LocalSystem Recovery Kernel.'
}
if (-not $engineRuntime.Contains('PanelCommandRequiresLocalSystemSupervisorPanelEndpoint') -or
    -not $supervisorKernel.Contains('RunPanelCommandsAsync') -or
    -not $supervisorKernel.Contains('AlarmPanelCommandDispatcher') -or
    -not $engineComposition.Contains('new AlarmManager')) {
    throw 'Alarm annunciation must be EngineHost-owned and separately admitted by Supervisor.'
}
if (-not $engineComposition.Contains('EngineProjectPersistence.Open') -or
    -not $engineComposition.Contains('_manager.Recorder = _projectPersistence.Recorder') -or
    -not $engineComposition.Contains('RegisterPausePersistenceFlush') -or
    -not $engineComposition.Contains('ReleasePersistenceForHostAsync')) {
    throw 'EngineHost must own the real recorder, acquisition drain and writer shutdown boundary.'
}
if (-not $engineComposition.Contains('ChannelCycleCompleted += OnProgressFormalCompleted') -or
    -not $engineComposition.Contains('ChannelMechanicalCycleCompleted += OnProgressMechanicalCompleted') -or
    -not $engineProgress.Contains('new EngineProgressPublisher') -or
    -not $engineProgress.Contains('configuration.SaveProgress') -or
    $engineComposition.Contains('_checkpointStore.RefreshFormalProgress')) {
    throw 'EngineHost progress must be durably projected off the pulse/control observers.'
}
if (-not $engineComposition.Contains('new EngineRawPersistence') -or
    -not $engineComposition.Contains('OwnedRawBatchReady += _rawPersistence.Enqueue') -or
    -not $engineComposition.Contains('StopAfterAcquisitionAsync') -or
    $engineComposition.Contains('ContinuousRawHostExecutorNotYetAvailable')) {
    throw 'Optional continuous Raw must have an EngineHost-owned consumer and shutdown boundary.'
}
if (-not $engineComposition.Contains('HardwareReleaseBarrier.RequireReleasedAsync') -or
    -not $engineComposition.Contains('_manager.CaptureHardwareReleaseForHost()')) {
    throw 'EngineHost recomposition must retain old owners until NI, power and Controller retirement is confirmed.'
}
if (-not $engineProgram.Contains('MTTFTEST_ENGINEHOST_TEST_INSTANCE') -or
    -not $engineProgram.Contains('MTTFTEST_ENGINEHOST_TEST_ROOT') -or
    -not $engineRuntime.Contains('_receiptRootDirectory') -or
    -not $engineIntegration.Contains('EngineHostProtocol.PipeName + ".test."')) {
    throw 'EngineHost integration tests are not isolated from installed production state.'
}
if (-not $engineRuntime.Contains('try { writer.Dispose(); } catch (IOException)')) {
    throw 'EngineHost successful pipe responses can still produce a disconnect log storm.'
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

if (-not $safetyProofExecutor.Contains('V3SafetyHandoffBoundary.Capture') -or
    -not $safetyProofExecutor.Contains('process.WaitForExit(remaining)') -or
    $safetyProofExecutor -match '(HardwareResourcesReleased|CallbacksIsolated|LogicalQuiescent)\s*=\s*engineReceipt\?\.ExecutionAuthorizationRevoked') {
    throw 'Native handoff still conflates revocation with resource/callback exit or misses SafetyAgent exit.'
}
if (-not $engineRuntime.Contains('PrepareSafetyHandoffAsync') -or
    -not $engineRuntime.Contains('HardwareHandoffRetiredByRecomposition')) {
    throw 'EngineHost post-executor native handoff or stale receipt fence is missing.'
}
if (-not $engineRuntime.Contains('_executionGate.BeginInitialization') -or
    -not $engineRuntime.Contains('initialization.PublishIfCurrent') -or
    -not $supervisorHost.Contains('ResolveUserInterfaceEnvironment') -or
    -not $supervisorKernel.Contains('_nativeProcessFence.AcquireUntil') -or
    -not (Read-Source 'Recovery.Kernel\RecoveryCoordinator.cs').Contains('RequireDesiredRun')) {
    throw 'Initialization, durable run identity or external native transfer fence is missing.'
}
$physicalEngine = Read-Source 'MTTFTest.EngineHost\PhysicalEngineRuntime.cs'
$physicalHandoff = Read-Source 'MTTFTest.EngineHost\PhysicalEngineRuntime.SafetyHandoff.cs'
if (-not $physicalEngine.Contains('_projectSelectionStore.ReadActive') -or
    -not $physicalEngine.Contains('ConfigLoader.LoadAllForEngine') -or
    $physicalEngine.Contains('ConfigLoader.UpdateDefaultTestFromProject') -or
    $physicalHandoff.Contains('ConfigLoader.UpdateDefaultTestFromProject') -or
    -not (Read-Source 'MTTFTest.EngineHost\EngineRunCheckpointStore.cs').Contains('isolatedDirectory == null && runId == null')) {
    throw 'V3 project selection must be run-bound, preserve templates and isolate new-run checkpoints from legacy migration.'
}
if (-not $physicalEngine.Contains('new AoController(config.AO, _log, initializeWithZeroVoltage: true)') -or
    -not $physicalEngine.Contains('EngineAoConfigurationStore.ValidateOperatingPressures') -or
    -not $physicalHandoff.Contains('_aoConfigurationStore.Commit') -or
    -not (Read-Source 'IO.NI\AoController.cs').Contains('TryResolveSupervisedPressure')) {
    throw 'V3 AO calibration requires physical zero-voltage OFF and released-hardware transactional configuration.'
}
Write-Output 'PASS V3RecoveryKernelSourceGate 1/1'
