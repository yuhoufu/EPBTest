using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.EngineHost
{
    internal sealed class EngineHostRuntime : IDisposable
    {
        private readonly string _sessionId;
        private readonly string _runId;
        private readonly long _runEpoch;
        private readonly string _engineInstanceId = RecoveryProtocolV7.NewId();
        private SystemTerminalState _manualPauseReturnState = SystemTerminalState.Running;
        private readonly IEngineHardwareRuntime _hardware;
        private readonly bool _allowSimulationCommandClient;
        private readonly string _pipeName;
        private readonly string _singletonName;
        private readonly string _receiptRootDirectory;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly ManualResetEventSlim _stopped = new ManualResetEventSlim(false);
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer
        {
            MaxJsonLength = int.MaxValue
        };
        private readonly ConcurrentDictionary<string, RecoveryCommandReceipt> _receipts =
            new ConcurrentDictionary<string, RecoveryCommandReceipt>(StringComparer.Ordinal);
        private readonly ConcurrentQueue<FaultObservation> _faultObservations =
            new ConcurrentQueue<FaultObservation>();
        private readonly object _stateGate = new object();
        private readonly EngineRecoveryExecutionGate _executionGate = new EngineRecoveryExecutionGate();
        private readonly EnginePressureMaintenanceLeaseFence _maintenanceLeaseFence;
        private EngineStateSnapshot _snapshot;
        private EngineTelemetryFrame _telemetry;
        private Task _pipeLoop;
        private Task _uiPipeLoop;
        private Task _panelPipeLoop;
        private Task _safetyPipeLoop;
        private Task _supervisorReadPipeLoop;
        private Task _maintenanceLeasePipeLoop;
        private Task _pulseLoop;
        private NamedPipeServerStream _activePipe;
        private NamedPipeServerStream _activeUiPipe;
        private NamedPipeServerStream _activePanelPipe;
        private NamedPipeServerStream _activeSafetyPipe;
        private NamedPipeServerStream _activeSupervisorReadPipe;
        private NamedPipeServerStream _activeMaintenanceLeasePipe;
        private Mutex _singleton;
        private long _snapshotRevision;
        private long _pulseSequence;
        private long _telemetrySequence;
        private long _uiSequence;
        private string _statusDetail = string.Empty;
        private int _faultOverflowPublished;
        private bool _ownsSingleton;
        private int _disposed;

        internal EngineHostRuntime(
            string sessionId,
            string runId,
            long runEpoch,
            IEngineHardwareRuntime hardware,
            bool allowSimulationCommandClient = false,
            string pipeName = null,
            string singletonName = null,
            string receiptRootDirectory = null)
        {
            if (!RecoveryProtocolV7.IsGuid(sessionId) ||
                !RecoveryProtocolV7.IsGuid(runId) || runEpoch <= 0)
                throw new ArgumentException("EngineHostIdentityInvalid");
            _sessionId = sessionId;
            _runId = runId;
            _runEpoch = runEpoch;
            _maintenanceLeaseFence = new EnginePressureMaintenanceLeaseFence(sessionId, runId, runEpoch, _engineInstanceId);
            _hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
            (_hardware as IEnginePressureMaintenanceRuntime)?.BindMaintenanceAuthority(_maintenanceLeaseFence);
            _allowSimulationCommandClient = allowSimulationCommandClient;
            _pipeName = string.IsNullOrWhiteSpace(pipeName)
                ? EngineHostProtocol.PipeName
                : pipeName;
            _singletonName = singletonName;
            _receiptRootDirectory = receiptRootDirectory;
            _hardware.FaultObserved += OnFaultObserved;
            PublishSnapshot(SystemTerminalState.SafeIdleAlarmed, false,
                "EngineHostStarting", string.Empty, string.Empty);
        }

        internal int Run()
        {
            _singleton = CreateSingletonMutex();
            try { _ownsSingleton = _singleton.WaitOne(0, false); }
            catch (AbandonedMutexException) { _ownsSingleton = true; }
            if (!_ownsSingleton)
            {
                EngineHostLog.Error("EngineHostDuplicateInstanceRejected", null);
                return 3;
            }
            ConsoleCancelEventHandler cancel = (sender, args) =>
            {
                args.Cancel = true;
                _stopped.Set();
            };
            Console.CancelKeyPress += cancel;
            try
            {
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                using (var initialization = _executionGate.BeginInitialization(timeout.Token))
                {
                    _pipeLoop = Task.Run(() => PipeLoopAsync(_stop.Token));
                    _uiPipeLoop = Task.Run(() => UiPipeLoopAsync(_stop.Token));
                    _panelPipeLoop = Task.Run(() => UiPipeLoopAsync(_stop.Token, EngineEndpoint.Panel));
                    _safetyPipeLoop = Task.Run(() => UiPipeLoopAsync(_stop.Token, EngineEndpoint.Safety));
                    _supervisorReadPipeLoop = Task.Run(() => UiPipeLoopAsync(_stop.Token, EngineEndpoint.SupervisorRead));
                    _maintenanceLeasePipeLoop = Task.Run(() => UiPipeLoopAsync(_stop.Token, EngineEndpoint.MaintenanceLease));
                    _pulseLoop = Task.Run(() => PulseLoopAsync(_stop.Token));
                    var identity = new RecoveryIdentity
                    {
                        SessionId = _sessionId,
                        RunId = _runId,
                        RunEpoch = _runEpoch,
                        IncidentId = RecoveryProtocolV7.NewId(),
                        ResourceScope = "System",
                        Generation = 1,
                        Revision = 1
                    };
                    var initialized = _hardware.InitializeSafeIdleAsync(identity, initialization.Token)
                        .GetAwaiter().GetResult();
                    initialization.PublishIfCurrent(() => PublishSnapshot(
                        SystemTerminalState.SafeIdleAlarmed,
                        initialized.Succeeded,
                        initialized.Detail,
                        string.Empty,
                        string.Empty));
                }
                _stopped.Wait();
                return 0;
            }
            finally
            {
                Console.CancelKeyPress -= cancel;
                Dispose();
            }
        }

        private async Task PipeLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = CreatePipe();
                    lock (_stateGate) _activePipe = pipe;
                    await Task.Factory.FromAsync(
                            pipe.BeginWaitForConnection,
                            pipe.EndWaitForConnection,
                            null)
                        .ConfigureAwait(false);
                    if (token.IsCancellationRequested) break;
                    await HandlePipeAsync(pipe, token).ConfigureAwait(false);
                }
                catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
                catch (EndOfStreamException)
                {
                    // A readiness probe may connect and close without a request.
                }
                catch (IOException ex)
                {
                    EngineHostLog.Error("EngineHostPipeIoFailure", ex);
                }
                catch (Exception ex)
                {
                    EngineHostLog.Error("EngineHostPipeFailure", ex);
                }
                finally
                {
                    lock (_stateGate)
                    {
                        if (ReferenceEquals(_activePipe, pipe)) _activePipe = null;
                    }
                    try { pipe?.Dispose(); } catch { }
                }
            }
        }

        private enum EngineEndpoint { Ui, Panel, Safety, SupervisorRead, MaintenanceLease }

        private async Task UiPipeLoopAsync(CancellationToken token, EngineEndpoint endpoint = EngineEndpoint.Ui)
        {
            // A blocked/slow UI owns only this bounded read-only pipe, never the recovery
            // command endpoint. One display frame is built at a time; no request backlog.
            while (!token.IsCancellationRequested)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    var suffix = endpoint == EngineEndpoint.Panel ? EngineHostProtocol.PanelPipeSuffix :
                        endpoint == EngineEndpoint.MaintenanceLease ? EngineHostProtocol.MaintenanceLeasePipeSuffix :
                        endpoint == EngineEndpoint.Safety ? EngineHostProtocol.SafetyPipeSuffix :
                        endpoint == EngineEndpoint.SupervisorRead ? EngineHostProtocol.SupervisorReadPipeSuffix : EngineHostProtocol.UiPipeSuffix;
                    pipe = CreatePipe(_pipeName + suffix);
                    lock (_stateGate)
                    {
                        if (endpoint == EngineEndpoint.Panel) _activePanelPipe = pipe;
                        else if (endpoint == EngineEndpoint.MaintenanceLease) _activeMaintenanceLeasePipe = pipe;
                        else if (endpoint == EngineEndpoint.Safety) _activeSafetyPipe = pipe;
                        else if (endpoint == EngineEndpoint.SupervisorRead) _activeSupervisorReadPipe = pipe;
                        else _activeUiPipe = pipe;
                    }
                    await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                    using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        deadline.CancelAfter(endpoint == EngineEndpoint.Safety ? 30000 : endpoint == EngineEndpoint.Panel ? 10000 : 3000);
                        using (deadline.Token.Register(() => { try { pipe.Dispose(); } catch { } }))
                        {
                            var header = new byte[4];
                            await ReadUiBytesAsync(pipe, header, deadline.Token).ConfigureAwait(false);
                            var length = BitConverter.ToInt32(header, 0);
                            // Display requests carry no payload; reject oversized input before allocating.
                            if (length <= 0 || length > 16384) throw new InvalidDataException("UiRequestTooLarge");
                            var bytes = new byte[length];
                            await ReadUiBytesAsync(pipe, bytes, deadline.Token).ConfigureAwait(false);
                            var json = new JavaScriptSerializer { MaxJsonLength = EngineHostProtocol.MaximumRequestBytes };
                            var request = json.Deserialize<EngineHostRequest>(Encoding.UTF8.GetString(bytes));
                            EngineHostResponse response;
                            var kindAllowed = request != null && (endpoint == EngineEndpoint.Panel
                                ? request.Kind == EngineHostRequestKind.ExecutePanelCommand || request.Kind == EngineHostRequestKind.ReadLatestSnapshot
                                : endpoint == EngineEndpoint.MaintenanceLease ? request.Kind == EngineHostRequestKind.UpdateMaintenanceLease
                                : endpoint == EngineEndpoint.Safety ? request.Kind == EngineHostRequestKind.ExecuteRecoveryCommand &&
                                    request.RecoveryCommand != null && EngineHostProtocol.IsPrioritySafetyCommand(request.RecoveryCommand.Kind)
                                : endpoint == EngineEndpoint.SupervisorRead ? request.Kind == EngineHostRequestKind.ReadLatestSnapshot ||
                                    request.Kind == EngineHostRequestKind.ReadLatestFault || request.Kind == EngineHostRequestKind.ReadLatestTelemetry
                                : request.Kind == EngineHostRequestKind.ReadUiSnapshot || request.Kind == EngineHostRequestKind.ReadUiLogs);
                            if (request?.IsStructurallyValid() != true || !kindAllowed || endpoint != EngineEndpoint.Ui && !IsTrustedCommandClient(pipe))
                                response = new EngineHostResponse
                                {
                                    RequestId = request?.RequestId ?? string.Empty,
                                    FailureCode = endpoint == EngineEndpoint.Ui ? "UiPipeReadOnly" : "EngineEndpointRequiresSupervisorAndAllowedKind",
                                    Detail = "该通道不接受当前命令或调用身份。"
                                };
                            else
                            {
                                try { response = await DispatchAsync(request, deadline.Token, endpoint != EngineEndpoint.Ui,
                                    endpoint == EngineEndpoint.Panel, endpoint == EngineEndpoint.MaintenanceLease).ConfigureAwait(false); }
                                catch (Exception ex) { response = new EngineHostResponse { RequestId = request.RequestId,
                                    FailureCode = "EngineHostRequestRejected", Detail = ex.GetBaseException().Message }; }
                            }
                            var payload = Encoding.UTF8.GetBytes(json.Serialize(response));
                            if (payload.Length > EngineHostProtocol.MaximumRequestBytes)
                                throw new InvalidDataException("UiResponseTooLarge");
                            header = BitConverter.GetBytes(payload.Length);
                            await pipe.WriteAsync(header, 0, 4, deadline.Token).ConfigureAwait(false);
                            await pipe.WriteAsync(payload, 0, payload.Length, deadline.Token).ConfigureAwait(false);
                            await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
                catch (IOException) { } // Abandoned display frames are normal latest-only loss, not a log storm.
                catch (Exception ex)
                {
                    EngineHostLog.Error("EngineUiFrameRejected", ex);
                    try { await Task.Delay(1000, token).ConfigureAwait(false); } catch (OperationCanceledException) { }
                }
                finally
                {
                    lock (_stateGate)
                    {
                        if (ReferenceEquals(_activeUiPipe, pipe)) _activeUiPipe = null;
                        if (ReferenceEquals(_activePanelPipe, pipe)) _activePanelPipe = null;
                        if (ReferenceEquals(_activeSafetyPipe, pipe)) _activeSafetyPipe = null;
                        if (ReferenceEquals(_activeSupervisorReadPipe, pipe)) _activeSupervisorReadPipe = null;
                        if (ReferenceEquals(_activeMaintenanceLeasePipe, pipe)) _activeMaintenanceLeasePipe = null;
                    }
                    try { pipe?.Dispose(); } catch { }
                }
            }
        }

        private static async Task ReadUiBytesAsync(Stream pipe, byte[] bytes, CancellationToken token)
        {
            var offset = 0;
            while (offset < bytes.Length)
            {
                var count = await pipe.ReadAsync(bytes, offset, bytes.Length - offset, token).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException("UiFrameTruncated");
                offset += count;
            }
        }

        private async Task HandlePipeAsync(
            NamedPipeServerStream pipe,
            CancellationToken token)
        {
            using (var reader = new BinaryReader(pipe, new UTF8Encoding(false), true))
            {
                var writer = new BinaryWriter(pipe, new UTF8Encoding(false), true);
                try
                {
                    var length = reader.ReadInt32();
                    if (length <= 0 || length > EngineHostProtocol.MaximumRequestBytes)
                        throw new InvalidDataException("EngineHostRequestLengthInvalid");
                    var bytes = reader.ReadBytes(length);
                    if (bytes.Length != length)
                        throw new EndOfStreamException("EngineHostRequestTruncated");
                    var request = _json.Deserialize<EngineHostRequest>(
                        Encoding.UTF8.GetString(bytes));
                    EngineHostResponse response;
                    try
                    {
                        response = await DispatchAsync(
                            request,
                            token,
                            IsTrustedCommandClient(pipe)).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        response = new EngineHostResponse
                        {
                            RequestId = request?.RequestId ?? string.Empty,
                            Accepted = false,
                            FailureCode = "EngineHostRequestRejected",
                            Detail = ex.GetBaseException().Message
                        };
                    }
                    var payload = Encoding.UTF8.GetBytes(_json.Serialize(response));
                    writer.Write(payload.Length);
                    writer.Write(payload);
                    writer.Flush();
                }
                finally
                {
                    // The client closes immediately after reading the response.
                    // BinaryWriter.Dispose flushes again and can otherwise turn
                    // each successful request into a broken-pipe error/log storm.
                    try { writer.Dispose(); } catch (IOException) { }
                }
            }
        }

        private async Task<EngineHostResponse> DispatchAsync(
            EngineHostRequest request,
            CancellationToken token,
            bool trustedCommandClient,
            bool panelCommandEndpoint = false,
            bool maintenanceLeaseEndpoint = false)
        {
            if (request?.IsStructurallyValid() != true)
                throw new InvalidDataException("EngineHostRequestInvalid");
            switch (request.Kind)
            {
                case EngineHostRequestKind.UpdateMaintenanceLease:
                    if (!trustedCommandClient || !maintenanceLeaseEndpoint)
                        throw new UnauthorizedAccessException("MaintenanceLeaseRequiresSupervisorDedicatedEndpoint");
                    return new EngineHostResponse { RequestId = request.RequestId, Accepted = true,
                        MaintenanceLease = _maintenanceLeaseFence.Receive(request.MaintenanceLease) };
                case EngineHostRequestKind.Ping:
                case EngineHostRequestKind.ReadLatestSnapshot:
                    return Success(request.RequestId, snapshot: CaptureSnapshot());
                case EngineHostRequestKind.ReadLatestTelemetry:
                    return Success(request.RequestId, telemetry: CaptureTelemetry());
                case EngineHostRequestKind.ReadUiSnapshot:
                    var ui = (_hardware as IEngineUiSource)?.CaptureUiSnapshot() ??
                        EngineUiSnapshotFactory.Empty("EngineHost 未提供监控数据");
                    ui.Engine = CaptureSnapshot();
                    ui.PressureMaintenance = (_hardware as IEnginePressureMaintenanceRuntime)?.CaptureMaintenanceDisplay();
                    ui.Sequence = Interlocked.Increment(ref _uiSequence);
                    ui.CapturedUtcTicks = DateTime.UtcNow.Ticks;
                    ui.StatusDetail = _statusDetail + "; " + ui.StatusDetail;
                    ui.Logs = EngineHostLog.ReadUiEntries(out var truncated);
                    ui.LogsTruncated = truncated;
                    return new EngineHostResponse { RequestId = request.RequestId, Accepted = true, UiSnapshot = ui };
                case EngineHostRequestKind.ReadUiLogs:
                    return new EngineHostResponse { RequestId = request.RequestId, Accepted = true,
                        Snapshot = CaptureSnapshot(), UiLogPage = EngineHostLog.ReadUiPage(request.UiLogQuery) };
                case EngineHostRequestKind.ReadLatestFault:
                    return Success(request.RequestId, fault: CaptureFault());
                case EngineHostRequestKind.ExecuteRecoveryCommand:
                    if (!trustedCommandClient)
                        throw new UnauthorizedAccessException(
                            "RecoveryCommandRequiresLocalSystemSupervisor");
                    return await ExecuteRecoveryAsync(
                        request.RequestId,
                        request.RecoveryCommand,
                        token).ConfigureAwait(false);
                case EngineHostRequestKind.ExecuteOperatorCommand:
                    throw new InvalidOperationException(
                        "OperatorCommandRequiresRecoveryKernelGate");
                case EngineHostRequestKind.ExecutePanelCommand:
                    if (!trustedCommandClient || !panelCommandEndpoint) throw new UnauthorizedAccessException("PanelCommandRequiresLocalSystemSupervisorPanelEndpoint");
                    return await ExecutePanelAsync(request, token).ConfigureAwait(false);
                default:
                    throw new InvalidDataException("EngineHostRequestKindUnsupported");
            }
        }

        private bool IsTrustedCommandClient(NamedPipeServerStream pipe)
        {
            if (_allowSimulationCommandClient) return true;
            SecurityIdentifier clientSid = null;
            try
            {
                pipe.RunAsClient(() =>
                {
                    using (var identity = WindowsIdentity.GetCurrent(true))
                        clientSid = identity.User;
                });
            }
            catch
            {
                return false;
            }
            return clientSid != null && clientSid.Equals(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        }

        private async Task<EngineHostResponse> ExecutePanelAsync(EngineHostRequest request, CancellationToken token)
        {
            var command = request.OperatorCommand;
            if (command.SessionId != _sessionId || command.RunId != _runId || command.RunEpoch != _runEpoch)
                throw new InvalidDataException("AlarmPanelRunIdentityMismatch");
            var fingerprint = OperatorCommandAdmission.GetFingerprint(command);
            var key = SupervisorProtocol.ComputeTextSha256("AlarmPanelReceipt/v1|" + fingerprint);
            if (!EngineHostCommandReceiptStore.TryRead(key, out var stored, _receiptRootDirectory))
            {
                var succeeded = false;
                string detail;
                try
                {
                    if (command.IssuedUtcTicks > DateTime.UtcNow.AddSeconds(5).Ticks ||
                        command.IssuedUtcTicks < DateTime.UtcNow.AddSeconds(-15).Ticks)
                        throw new InvalidOperationException("AlarmPanelCommandExpired");
                    if (!(_hardware is IEngineAlarmPanel panel)) throw new InvalidOperationException("AlarmPanelUnavailable");
                    await panel.ExecutePanelCommandAsync(command, token).ConfigureAwait(false);
                    succeeded = true;
                    detail = _allowSimulationCommandClient ? "隔离模拟报警操作已执行；未访问现场串口。" :
                        "报警操作已执行；仅串口写入，不是断能证明；隔离状态未改变。";
                }
                catch (Exception ex) { detail = ex.GetBaseException().Message; }
                stored = new RecoveryCommandReceipt { CommandId = command.CommandId, IdempotencyKey = key,
                    Succeeded = succeeded, Detail = detail, CompletedUtcTicks = DateTime.UtcNow.Ticks };
                EngineHostCommandReceiptStore.Write(stored, _receiptRootDirectory);
            }
            if (stored.CommandId != command.CommandId) throw new InvalidDataException("AlarmPanelReceiptBindingMismatch");
            return new EngineHostResponse { RequestId = request.RequestId, Accepted = true, Snapshot = CaptureSnapshot(),
                OperatorReceipt = new OperatorExecutionReceipt { CommandId = command.CommandId, Fingerprint = fingerprint,
                    Succeeded = stored.Succeeded, Detail = stored.Detail, CompletedUtcTicks = stored.CompletedUtcTicks } };
        }

        private async Task<EngineHostResponse> ExecuteRecoveryAsync(
            string requestId,
            RecoveryCommand command,
            CancellationToken token)
        {
            if (!string.Equals(command.Identity.SessionId, _sessionId,
                    StringComparison.Ordinal) ||
                !string.Equals(command.Identity.RunId, _runId,
                    StringComparison.Ordinal) ||
                command.Identity.RunEpoch != _runEpoch)
                throw new InvalidDataException(
                    "RecoveryCommandRunIdentityMismatch");
            if (command.OperatorTransaction?.ManualBatch != null && command.OperatorTransaction.ManualBatch.EngineInstanceId != _engineInstanceId)
                throw new InvalidDataException("ManualBatchEngineInstanceMismatch");
            if (command.PressureMaintenance != null && command.PressureMaintenance.EngineInstanceId != _engineInstanceId)
                throw new InvalidDataException("MaintenanceCommandEngineInstanceMismatch");
            if ((command.Kind == RecoveryCommandKind.PrepareProjectSwitch || command.Kind == RecoveryCommandKind.AbortProjectSwitch) &&
                command.ProjectSwitch.SourceEngineInstanceId != _engineInstanceId)
                throw new InvalidDataException("ProjectSwitchSourceEngineInstanceMismatch");
            if (_receipts.TryGetValue(command.IdempotencyKey, out var existing))
            {
                ValidateHandoffReplay(command, existing);
                return Success(requestId, receipt: existing);
            }
            if (EngineHostCommandReceiptStore.TryRead(
                    command.IdempotencyKey,
                    out existing,
                    _receiptRootDirectory))
            {
                ValidateHandoffReplay(command, existing);
                _receipts[command.IdempotencyKey] = existing;
                return Success(requestId, receipt: existing);
            }
            if (command.DeadlineUtcTicks < DateTime.UtcNow.Ticks)
                throw new TimeoutException("RecoveryCommandExpired");
            using var execution = _executionGate.Begin(command, token);
            if ((command.Kind == RecoveryCommandKind.PauseBatchGracefully || command.Kind == RecoveryCommandKind.PauseChannelGracefully) &&
                string.IsNullOrEmpty(CaptureSnapshot().RecoveryOwnerId))
                _manualPauseReturnState = CaptureSnapshot().State == SystemTerminalState.RunningDegraded ? SystemTerminalState.RunningDegraded : SystemTerminalState.Running;
            execution.PublishIfCurrent(() => PublishSnapshot(CaptureSnapshot().State, _hardware.Initialized,
                "Executing:" + command.Kind, command.Identity.IncidentId, command.OwnerId));
            EngineHardwareCommandResult result;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(execution.Token))
            {
                deadline.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, Math.Min(command.Kind == RecoveryCommandKind.PauseBatchGracefully || command.Kind == RecoveryCommandKind.PauseChannelGracefully ? 300000 : 180000,
                    TimeSpan.FromTicks(command.DeadlineUtcTicks - DateTime.UtcNow.Ticks).TotalMilliseconds))));
                try
                {
                    result = await execution.ExecuteWithQuiescentBoundaryAsync(
                        cancellation => _hardware.ExecuteAsync(command, cancellation), deadline.Token, async (completed, cancellation) =>
                        {
                            if (!EngineHostProtocol.RequiresIndependentSafetyHandoff(command.Kind)) return;
                            if (!(_hardware is IEngineSafetyHandoff handoff))
                                throw new InvalidOperationException("EngineSafetyHandoffUnsupported");
                            await handoff.PrepareSafetyHandoffAsync(command, completed, cancellation).ConfigureAwait(false);
                            completed.ExecutorQuiescent = true;
                            if (completed.NativeResourcesReleased && completed.CallbacksIsolated && completed.LogicalQuiescent && completed.DataBoundaryClosed)
                                _maintenanceLeaseFence.CompleteSafetyHandoff(command);
                        }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    result = new EngineHardwareCommandResult { Detail = "RecoveryExecutionFailed:" + ex.GetBaseException().Message };
                }
            }
            if (!execution.IsCurrent)
                result = new EngineHardwareCommandResult { Detail = "RecoveryExecutionSupersededBySafety" };
            var receipt = new RecoveryCommandReceipt
            {
                PressureMaintenance = result.PressureMaintenance,
                ProjectSwitch = result.ProjectSwitch,
                HardwareHandoff = EngineHostProtocol.RequiresIndependentSafetyHandoff(command.Kind) ? new EngineHardwareHandoff
                {
                    Identity = command.Identity.Clone(), CommandId = command.CommandId,
                    IdempotencyKey = command.IdempotencyKey, EngineInstanceId = _engineInstanceId, OwnerId = command.OwnerId,
                    LogicalQuiescent = result.LogicalQuiescent, NativeResourcesReleased = result.NativeResourcesReleased,
                    CallbacksIsolated = result.CallbacksIsolated, ExecutorQuiescent = result.ExecutorQuiescent,
                    CapturedUtcTicks = DateTime.UtcNow.Ticks
                } : null,
                CommandId = command.CommandId,
                IdempotencyKey = command.IdempotencyKey,
                Succeeded = result.Succeeded,
                Detail = result.Detail,
                OutputsOff = result.OutputsOff,
                PressureSafe = result.PressureSafe,
                DataBoundaryClosed = result.DataBoundaryClosed,
                ExecutionAuthorizationRevoked =
                    result.ExecutionAuthorizationRevoked,
                QualificationCyclesCompleted =
                    result.QualificationCyclesCompleted,
                FormalCyclesCompleted = result.FormalCyclesCompleted,
                StableSinceUtcTicks = result.StableSinceUtcTicks,
                InterruptedCycleCounted = result.InterruptedCycleCounted,
                CompletedUtcTicks = DateTime.UtcNow.Ticks
            };
            if (ManualBatchCommand.IsChannelOperation(command.OperatorTransaction?.Kind ?? OperatorCommandKind.None) &&
                _hardware is IEngineManualChannelState channelState)
                receipt.ManualChannels = new ManualChannelState { EngineInstanceId = _engineInstanceId,
                    PauseMask = channelState.ChannelPauseMask, ResumeMask = channelState.ChannelResumeMask };
            EngineHostCommandReceiptStore.Write(receipt, _receiptRootDirectory);
            _receipts[command.IdempotencyKey] = receipt;
            TrimReceipts();
            execution.PublishIfCurrent(() => {
            if ((command.Kind == RecoveryCommandKind.PauseChannelGracefully || command.Kind == RecoveryCommandKind.ResumePausedChannel) && result.Succeeded)
            {
                var held = receipt.ManualChannels.ResumeMask != 0;
                PublishSnapshot(held ? receipt.ManualChannels.PauseMask == 0 ? SystemTerminalState.StoppedByOperator : SystemTerminalState.RunningDegraded : _manualPauseReturnState,
                    _hardware.Initialized, result.Detail, held ? command.Identity.IncidentId : string.Empty, held ? command.OwnerId : string.Empty);
                // A channel hold is not a global OFF proof (shared supplies may remain on).
                lock (_stateGate) _snapshot.OutputsEnergized = true;
            }
            else if (command.Kind == RecoveryCommandKind.DisableOutputs && command.PressureMaintenance?.Revoked == true && result.Succeeded)
                PublishSnapshot(command.PressureMaintenance.ExitState, _hardware.Initialized, result.Detail, string.Empty, string.Empty);
            else if ((command.Kind == RecoveryCommandKind.StopByOperator || command.Kind == RecoveryCommandKind.CommitTestConfiguration) && result.Succeeded)
                PublishSnapshot(SystemTerminalState.StoppedByOperator,
                    _hardware.Initialized, result.Detail, string.Empty, string.Empty);
            else if ((command.Kind == RecoveryCommandKind.ResumeFormalRun || command.Kind == RecoveryCommandKind.ResumePausedBatch) && result.Succeeded)
                PublishSnapshot(command.Kind == RecoveryCommandKind.ResumePausedBatch ? _manualPauseReturnState :
                    result.RecoveredState == SystemTerminalState.RunningDegraded ? SystemTerminalState.RunningDegraded : SystemTerminalState.Running,
                    _hardware.Initialized, result.Detail, string.Empty, string.Empty);
            else if (command.Kind == RecoveryCommandKind.PauseBatchGracefully && result.Succeeded)
                PublishSnapshot(SystemTerminalState.StoppedByOperator, _hardware.Initialized, result.Detail,
                    command.Identity.IncidentId, command.OwnerId);
            else if (command.Kind == RecoveryCommandKind.RunQualificationCycle && result.Succeeded)
                PublishSnapshot(SystemTerminalState.SafeIdleAlarmed, _hardware.Initialized, result.Detail,
                    command.Identity.IncidentId, command.OwnerId);
            else if (command.Kind == RecoveryCommandKind.IsolateResource && result.Succeeded)
                PublishSnapshot(SystemTerminalState.SafeIdleAlarmed, _hardware.Initialized, result.Detail,
                    command.Identity.IncidentId, command.OwnerId);
            else
                PublishSnapshot(CaptureSnapshot().State,
                    _hardware.Initialized, result.Detail,
                    command.Identity.IncidentId, command.OwnerId);
            });
            return Success(requestId, receipt: receipt, snapshot: CaptureSnapshot());
        }

        private async Task PulseLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(500, token).ConfigureAwait(false);
                    lock (_stateGate)
                    {
                        _pulseSequence++;
                        _snapshot.PulseSequence = _pulseSequence;
                        _snapshot.CapturedUtcTicks = DateTime.UtcNow.Ticks;
                        _telemetry = _hardware.CaptureTelemetry(
                            Interlocked.Increment(ref _telemetrySequence));
                        if (_telemetry != null)
                        {
                            _snapshot.QualificationCyclesCompleted =
                                _telemetry.QualificationCyclesCompleted;
                            _snapshot.FormalCyclesSinceRecovery =
                                _telemetry.FormalCyclesSinceRecovery;
                            _snapshot.StableSinceUtcTicks =
                                _telemetry.StableSinceUtcTicks;
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    EngineHostLog.Error("EngineHostPulseFailure", ex);
                }
            }
        }

        private void OnFaultObserved(FaultObservation observation)
        {
            if (observation?.IsStructurallyValid() != true) return;
            _faultObservations.Enqueue(observation);
            var overflowed = false;
            while (_faultObservations.Count > 4095)
            {
                _faultObservations.TryDequeue(out _);
                overflowed = true;
            }
            if (overflowed &&
                Interlocked.Exchange(ref _faultOverflowPublished, 1) == 0)
            {
                var identity = observation.Identity.Clone();
                identity.IncidentId = RecoveryProtocolV7.NewId();
                identity.ResourceScope = "System";
                identity.Generation++;
                _faultObservations.Enqueue(new FaultObservation
                {
                    ObservationId = RecoveryProtocolV7.NewId(),
                    Identity = identity,
                    ResourceKind = ResourceKind.System,
                    ResourceId = "System",
                    FaultCode = "FaultObservationOverflow",
                    ObservableProperty = "FaultQueueCapacityExceeded",
                    Detail = "At least one fault observation was dropped; minimal scope is no longer provable.",
                    Severity = FaultSeverity.SafetyCritical,
                    ScopeProven = false,
                    SafetyChainHealthy = false,
                    ObservedUtcTicks = DateTime.UtcNow.Ticks
                });
            }
            // The EngineHost reports facts only.  It neither calls StopAll nor
            // starts a recovery worker from this callback.
        }

        private void ValidateHandoffReplay(RecoveryCommand command, RecoveryCommandReceipt receipt)
        {
            if (PressureMaintenanceProtocol.IsExecution(command.Kind) && receipt.Succeeded)
            {
                // A persisted receipt is not permission to reuse expired maintenance
                // or move a prepared hardware context into a replacement process.
                if (receipt.PressureMaintenance?.EngineInstanceId != _engineInstanceId ||
                    !_maintenanceLeaseFence.IsAuthorized(command.Identity.IncidentId, command.OwnerId))
                    throw new InvalidDataException("MaintenanceReceiptAuthorityRetired");
            }
            if (EngineHostProtocol.RequiresIndependentSafetyHandoff(command.Kind) &&
                receipt.HardwareHandoff?.Matches(command, _engineInstanceId) != true)
                throw new InvalidDataException("HardwareHandoffBelongsToDifferentEngineOrCommand");
            if (receipt.HardwareHandoff?.ResourcesTransferable == true &&
                (_hardware as IEngineSafetyHandoff)?.HardwareRecompositionReady != true)
                throw new InvalidDataException("HardwareHandoffRetiredByRecomposition");
        }

        private void PublishSnapshot(
            SystemTerminalState state,
            bool hardwareInitialized,
            string detail,
            string incidentId,
            string ownerId)
        {
            lock (_stateGate)
            {
                _statusDetail = detail ?? string.Empty;
                _snapshotRevision++;
                _pulseSequence++;
                _snapshot = new EngineStateSnapshot
                {
                    EngineInstanceId = _engineInstanceId,
                    SessionId = _sessionId,
                    RunId = _runId,
                    RunEpoch = _runEpoch,
                    Revision = _snapshotRevision,
                    PulseSequence = _pulseSequence,
                    State = state,
                    RecoveryIncidentId = incidentId ?? string.Empty,
                    RecoveryOwnerId = ownerId ?? string.Empty,
                    HardwareInitialized = hardwareInitialized,
                    HardwareRecompositionReady = (_hardware as IEngineSafetyHandoff)?.HardwareRecompositionReady == true,
                    OutputsEnergized = state == SystemTerminalState.Running ||
                                       state == SystemTerminalState.RunningDegraded ||
                                       (_hardware as IEnginePressureMaintenanceRuntime)?.MaintenanceMayBeEnergized == true,
                    QualificationCyclesCompleted =
                        _telemetry?.QualificationCyclesCompleted ?? 0,
                    FormalCyclesSinceRecovery =
                        _telemetry?.FormalCyclesSinceRecovery ?? 0,
                    StableSinceUtcTicks = _telemetry?.StableSinceUtcTicks ?? 0,
                    CapturedUtcTicks = DateTime.UtcNow.Ticks,
                    IsolatedResources = Array.Empty<string>()
                };
                // Memory-only: the execution fence may call this while admitting OFF.
                // Supervisor already audits command outcomes; no synchronous disk log here.
            }
        }

        private Mutex CreateSingletonMutex()
        {
            if (!string.IsNullOrWhiteSpace(_singletonName))
                return new Mutex(false, _singletonName);
            try
            {
                return new Mutex(false, "Global\\MTTFTest.EngineHost.V3");
            }
            catch (UnauthorizedAccessException)
            {
                // Developer workstations and integration-test accounts may not
                // have SeCreateGlobalPrivilege. Installed service hosts use Global.
                return new Mutex(false, "Local\\MTTFTest.EngineHost.V3");
            }
        }

        private EngineStateSnapshot CaptureSnapshot()
        {
            lock (_stateGate)
                return new EngineStateSnapshot
                {
                    ProjectActivation = (_hardware as IEngineProjectState)?.ProjectActivation,
                    SchemaVersion = _snapshot.SchemaVersion,
                    EngineInstanceId = _snapshot.EngineInstanceId,
                    SessionId = _snapshot.SessionId,
                    RunId = _snapshot.RunId,
                    RunEpoch = _snapshot.RunEpoch,
                    Revision = _snapshot.Revision,
                    PulseSequence = _snapshot.PulseSequence,
                    State = _snapshot.State,
                    RecoveryIncidentId = _snapshot.RecoveryIncidentId,
                    RecoveryOwnerId = _snapshot.RecoveryOwnerId,
                    HardwareInitialized = _snapshot.HardwareInitialized,
                    HardwareRecompositionReady = _snapshot.HardwareRecompositionReady,
                    OutputsEnergized = _snapshot.OutputsEnergized || (_hardware as IEnginePressureMaintenanceRuntime)?.MaintenanceMayBeEnergized == true,
                    ChannelPauseMask = (_hardware as IEngineManualChannelState)?.ChannelPauseMask ?? 0,
                    ChannelResumeMask = (_hardware as IEngineManualChannelState)?.ChannelResumeMask ?? 0,
                    QualificationCyclesCompleted =
                        _snapshot.QualificationCyclesCompleted,
                    FormalCyclesSinceRecovery =
                        _snapshot.FormalCyclesSinceRecovery,
                    StableSinceUtcTicks = _snapshot.StableSinceUtcTicks,
                    CapturedUtcTicks = _snapshot.CapturedUtcTicks,
                    IsolatedResources = (_snapshot.IsolatedResources ?? Array.Empty<string>())
                        .Concat((_hardware as IEngineProjectState)?.ProjectIsolatedResources ?? Array.Empty<string>())
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                };
        }

        private EngineTelemetryFrame CaptureTelemetry()
        {
            lock (_stateGate)
                return _telemetry == null
                    ? null
                    : new EngineTelemetryFrame
                    {
                        SchemaVersion = _telemetry.SchemaVersion,
                        Sequence = _telemetry.Sequence,
                        CapturedUtcTicks = _telemetry.CapturedUtcTicks,
                        Currents = (_telemetry.Currents ?? Array.Empty<double>()).ToArray(),
                        Pressures = (_telemetry.Pressures ?? Array.Empty<double>()).ToArray(),
                        FormalCycleCounts =
                            (_telemetry.FormalCycleCounts ?? Array.Empty<int>()).ToArray(),
                        QualificationCyclesCompleted =
                            _telemetry.QualificationCyclesCompleted,
                        FormalCyclesSinceRecovery =
                            _telemetry.FormalCyclesSinceRecovery,
                        StableSinceUtcTicks = _telemetry.StableSinceUtcTicks
                    };
        }

        private FaultObservation CaptureFault()
        {
            _faultObservations.TryDequeue(out var observation);
            return observation;
        }

        private static EngineHostResponse Success(
            string requestId,
            EngineStateSnapshot snapshot = null,
            EngineTelemetryFrame telemetry = null,
            RecoveryCommandReceipt receipt = null,
            FaultObservation fault = null)
        {
            return new EngineHostResponse
            {
                RequestId = requestId,
                Accepted = true,
                Snapshot = snapshot,
                Telemetry = telemetry,
                RecoveryReceipt = receipt,
                FaultObservation = fault,
                Detail = "Accepted"
            };
        }

        private NamedPipeServerStream CreatePipe(string pipeName = null)
        {
            var security = new PipeSecurity();
            var user = WindowsIdentity.GetCurrent().User;
            if (user != null)
                security.AddAccessRule(new PipeAccessRule(
                    user, PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
            return new NamedPipeServerStream(
                pipeName ?? _pipeName,
                PipeDirection.InOut,
                4,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                64 * 1024,
                64 * 1024,
                security);
        }

        private void TrimReceipts()
        {
            if (_receipts.Count <= 4096) return;
            foreach (var key in _receipts.Keys.Take(_receipts.Count - 4096))
                _receipts.TryRemove(key, out _);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stop.Cancel();
            _stopped.Set();
            lock (_stateGate)
            {
                try { _activePipe?.Dispose(); } catch { }
                try { _activeUiPipe?.Dispose(); } catch { }
                try { _activePanelPipe?.Dispose(); } catch { }
                try { _activeSafetyPipe?.Dispose(); } catch { }
                try { _activeSupervisorReadPipe?.Dispose(); } catch { }
                try { _activeMaintenanceLeasePipe?.Dispose(); } catch { }
                _activePipe = null;
                _activeUiPipe = null;
                _activePanelPipe = null;
                _activeSafetyPipe = null;
                _activeSupervisorReadPipe = null;
                _activeMaintenanceLeasePipe = null;
            }
            try { _hardware.FaultObserved -= OnFaultObserved; } catch { }
            try { _hardware.Dispose(); } catch { }
            try { Task.WaitAll(
                new[] { _pipeLoop, _uiPipeLoop, _panelPipeLoop, _safetyPipeLoop, _supervisorReadPipeLoop, _maintenanceLeasePipeLoop, _pulseLoop }.Where(task => task != null).ToArray(),
                TimeSpan.FromSeconds(5)); } catch { }
            if (_ownsSingleton)
            {
                try { _singleton?.ReleaseMutex(); } catch { }
            }
            try { _singleton?.Dispose(); } catch { }
            _stop.Dispose();
            _stopped.Dispose();
        }
    }
}
