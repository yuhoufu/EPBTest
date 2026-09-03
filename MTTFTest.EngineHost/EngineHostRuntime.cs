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
        private EngineStateSnapshot _snapshot;
        private EngineTelemetryFrame _telemetry;
        private Task _pipeLoop;
        private Task _pulseLoop;
        private NamedPipeServerStream _activePipe;
        private Mutex _singleton;
        private long _snapshotRevision;
        private long _pulseSequence;
        private long _telemetrySequence;
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
            _hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
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
                _pipeLoop = Task.Run(() => PipeLoopAsync(_stop.Token));
                _pulseLoop = Task.Run(() => PulseLoopAsync(_stop.Token));
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                {
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
                    var initialized = _hardware.InitializeSafeIdleAsync(identity, timeout.Token)
                        .GetAwaiter().GetResult();
                    PublishSnapshot(
                        SystemTerminalState.SafeIdleAlarmed,
                        initialized.Succeeded,
                        initialized.Detail,
                        string.Empty,
                        string.Empty);
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
            bool trustedCommandClient)
        {
            if (request?.IsStructurallyValid() != true)
                throw new InvalidDataException("EngineHostRequestInvalid");
            switch (request.Kind)
            {
                case EngineHostRequestKind.Ping:
                case EngineHostRequestKind.ReadLatestSnapshot:
                    return Success(request.RequestId, snapshot: CaptureSnapshot());
                case EngineHostRequestKind.ReadLatestTelemetry:
                    return Success(request.RequestId, telemetry: CaptureTelemetry());
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
            if (_receipts.TryGetValue(command.IdempotencyKey, out var existing))
                return Success(requestId, receipt: existing);
            if (EngineHostCommandReceiptStore.TryRead(
                    command.IdempotencyKey,
                    out existing,
                    _receiptRootDirectory))
            {
                _receipts[command.IdempotencyKey] = existing;
                return Success(requestId, receipt: existing);
            }
            if (command.DeadlineUtcTicks < DateTime.UtcNow.Ticks)
                throw new TimeoutException("RecoveryCommandExpired");
            var result = await _hardware.ExecuteAsync(command, token).ConfigureAwait(false);
            var receipt = new RecoveryCommandReceipt
            {
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
            EngineHostCommandReceiptStore.Write(receipt, _receiptRootDirectory);
            _receipts[command.IdempotencyKey] = receipt;
            TrimReceipts();
            if (command.Kind == RecoveryCommandKind.StopByOperator && result.Succeeded)
                PublishSnapshot(SystemTerminalState.StoppedByOperator,
                    _hardware.Initialized, result.Detail, string.Empty, string.Empty);
            else if (command.Kind == RecoveryCommandKind.ResumeFormalRun && result.Succeeded)
                PublishSnapshot(SystemTerminalState.Running,
                    _hardware.Initialized, result.Detail, string.Empty, string.Empty);
            else if (command.Kind == RecoveryCommandKind.IsolateResource && result.Succeeded)
                PublishSnapshot(
                    result.Detail.IndexOf("RunningDegraded",
                        StringComparison.Ordinal) >= 0
                        ? SystemTerminalState.RunningDegraded
                        : SystemTerminalState.SafeIdleAlarmed,
                    _hardware.Initialized,
                    result.Detail,
                    string.Empty,
                    string.Empty);
            else
                PublishSnapshot(CaptureSnapshot().State,
                    _hardware.Initialized, result.Detail,
                    command.Identity.IncidentId, command.OwnerId);
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

        private void PublishSnapshot(
            SystemTerminalState state,
            bool hardwareInitialized,
            string detail,
            string incidentId,
            string ownerId)
        {
            lock (_stateGate)
            {
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
                    OutputsEnergized = state == SystemTerminalState.Running ||
                                       state == SystemTerminalState.RunningDegraded,
                    QualificationCyclesCompleted =
                        _telemetry?.QualificationCyclesCompleted ?? 0,
                    FormalCyclesSinceRecovery =
                        _telemetry?.FormalCyclesSinceRecovery ?? 0,
                    StableSinceUtcTicks = _telemetry?.StableSinceUtcTicks ?? 0,
                    CapturedUtcTicks = DateTime.UtcNow.Ticks,
                    IsolatedResources = Array.Empty<string>()
                };
                EngineHostLog.Info("EngineState=" + state + ";Detail=" + detail);
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
                    OutputsEnergized = _snapshot.OutputsEnergized,
                    QualificationCyclesCompleted =
                        _snapshot.QualificationCyclesCompleted,
                    FormalCyclesSinceRecovery =
                        _snapshot.FormalCyclesSinceRecovery,
                    StableSinceUtcTicks = _snapshot.StableSinceUtcTicks,
                    CapturedUtcTicks = _snapshot.CapturedUtcTicks,
                    IsolatedResources = (_snapshot.IsolatedResources ?? Array.Empty<string>()).ToArray()
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

        private NamedPipeServerStream CreatePipe()
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
                _pipeName,
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
                _activePipe = null;
            }
            try { _hardware.FaultObserved -= OnFaultObserved; } catch { }
            try { _hardware.Dispose(); } catch { }
            try { Task.WaitAll(
                new[] { _pipeLoop, _pulseLoop }.Where(task => task != null).ToArray(),
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
