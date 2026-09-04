using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Web.Script.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Linq;
using MTTFTest.Watchdog.Protocol;

namespace MTEmbTest
{
    internal sealed class V3EngineIdentity
    {
        internal string SessionId { get; private set; }
        internal string RunId { get; private set; }
        internal long RunEpoch { get; private set; }

        internal static V3EngineIdentity Parse(string[] args)
        {
            var session = Read(args, SessionAgentProtocol.SessionArgument);
            var run = Read(args, "--engine-run");
            if (!long.TryParse(Read(args, "--engine-epoch"), out var epoch) ||
                !RecoveryProtocolV7.IsGuid(session) ||
                !RecoveryProtocolV7.IsGuid(run) || epoch <= 0)
                throw new InvalidDataException("V3EngineIdentityArgumentsInvalid");
            return new V3EngineIdentity
            {
                SessionId = session,
                RunId = run,
                RunEpoch = epoch
            };
        }

        private static string Read(string[] args, string name)
        {
            for (var index = 0; index + 1 < (args?.Length ?? 0); index++)
                if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
                    return args[index + 1]?.Trim('"') ?? string.Empty;
            return string.Empty;
        }
    }

    internal sealed partial class V3EngineHostClient : IEngineUiClient
    {
        private readonly V3EngineIdentity _identity;
        private readonly string _enginePipeName;
        private readonly string _supervisorPipeName;
        private readonly bool _requiresAttachment;
        private SupervisorUiAttachmentResponse _attachment;
        private long _highestEpoch;
        private string _attachedRunId;
        private readonly ConcurrentDictionary<string, OperatorCommand> _pendingCommands = new ConcurrentDictionary<string, OperatorCommand>();
        public bool HasUnresolvedCommands => !_pendingCommands.IsEmpty;

        internal V3EngineHostClient(V3EngineIdentity identity, string enginePipeName = null, string supervisorPipeName = null)
        {
            _identity = identity ?? throw new ArgumentNullException(nameof(identity));
            _enginePipeName = enginePipeName ?? EngineHostProtocol.UiPipeName;
            _supervisorPipeName = supervisorPipeName ?? SupervisorProtocol.PipeName;
            _requiresAttachment = enginePipeName == null || supervisorPipeName != null;
            _highestEpoch = identity.RunEpoch;
            _attachedRunId = identity.RunId;
        }

        public async Task<EngineUiSnapshot> ReadUiSnapshotAsync(CancellationToken token)
        {
            try
            {
                if (_requiresAttachment && _attachment == null)
                    await AttachAsync(token).ConfigureAwait(false);
                var response = await SendEngineAsync(new EngineHostRequest
                {
                    RequestId = RecoveryProtocolV7.NewId(), Kind = EngineHostRequestKind.ReadUiSnapshot
                }, token).ConfigureAwait(false);
                if (!response.Accepted || response.UiSnapshot?.IsStructurallyValid() != true)
                    throw new InvalidDataException("完整界面数据契约不兼容或数据无效：" + response.Detail);
                EnsureIdentity(response.UiSnapshot.Engine);
                if (_requiresAttachment)
                    response.UiSnapshot.Kernel = await ReadKernelStateAsync(response.UiSnapshot.Engine, token).ConfigureAwait(false);
                return response.UiSnapshot;
            }
            catch
            {
                // A replacement must be independently readmitted by Supervisor. Never attach
                // based only on a new instance/epoch announced by the data pipe itself.
                _attachment = null;
                throw;
            }
        }

        private async Task<EngineUiKernelState> ReadKernelStateAsync(EngineStateSnapshot engine, CancellationToken token)
        {
            SupervisorUiAttachmentRequest request;
            using (var current = Process.GetCurrentProcess())
                request = new SupervisorUiAttachmentRequest
                {
                    RequestId = RecoveryProtocolV7.NewId(), ChallengeNonce = RecoveryProtocolV7.NewId(), QueryKernelOnly = true,
                    SessionId = _identity.SessionId, RequesterProcessId = current.Id,
                    RequesterProcessStartUtcTicks = current.StartTime.ToUniversalTime().Ticks
                };
            var response = await BoundedPipeTransport.ExchangeBinaryAsync(_supervisorPipeName, request.WriteTo,
                SupervisorUiStateResponse.ReadFrom, 3000, token).ConfigureAwait(false);
            if (response.RequestId != request.RequestId || response.ChallengeNonce != request.ChallengeNonce || !response.Accepted ||
                response.State?.IsStructurallyValid() != true)
                throw new InvalidDataException("Supervisor 状态未通过校验：" + response.Detail);
            if (response.State.Available && (response.State.SessionId != engine.SessionId || response.State.RunId != engine.RunId ||
                response.State.RunEpoch != engine.RunEpoch)) return new EngineUiKernelState();
            return response.State;
        }

        public async Task<EngineUiLogPage> ReadLogsAsync(EngineUiLogQuery query, EngineStateSnapshot expected, CancellationToken token)
        {
            EnsureIdentity(expected);
            var response = await SendEngineAsync(new EngineHostRequest { RequestId = RecoveryProtocolV7.NewId(),
                Kind = EngineHostRequestKind.ReadUiLogs, UiLogQuery = query }, token).ConfigureAwait(false);
            if (!response.Accepted || response.UiLogPage?.IsStructurallyValid() != true ||
                response.UiLogPage.Level != query.Level || response.UiLogPage.BeforeSequence != query.BeforeSequence ||
                response.Snapshot?.IsStructurallyValid() != true || response.Snapshot.EngineInstanceId != expected.EngineInstanceId ||
                response.UiLogPage.Entries.Length > query.PageSize)
                throw new InvalidDataException("日志分页回执或后台身份无效");
            EnsureIdentity(response.Snapshot);
            return response.UiLogPage;
        }

        private async Task AttachAsync(CancellationToken token)
        {
            SupervisorUiAttachmentRequest request;
            using (var current = Process.GetCurrentProcess())
                request = new SupervisorUiAttachmentRequest
                {
                    RequestId = RecoveryProtocolV7.NewId(), ChallengeNonce = RecoveryProtocolV7.NewId(),
                    SessionId = _identity.SessionId, RequesterProcessId = current.Id,
                    RequesterProcessStartUtcTicks = current.StartTime.ToUniversalTime().Ticks
                };
            var response = await BoundedPipeTransport.ExchangeBinaryAsync(_supervisorPipeName,
                request.WriteTo, SupervisorUiAttachmentResponse.ReadFrom, 5000, token).ConfigureAwait(false);
            if (response.RequestId != request.RequestId || response.ChallengeNonce != request.ChallengeNonce ||
                !response.ApprovesRun(_identity.SessionId, _attachedRunId, _highestEpoch, DateTime.UtcNow.Ticks))
                throw new InvalidDataException("Supervisor 未批准后台重新附着：" + response.Detail);
            _highestEpoch = response.Engine.RunEpoch;
            _attachedRunId = response.Engine.RunId;
            _attachment = response;
        }

        public async Task<SupervisorOperatorCommandResponse> SubmitAsync(EngineStateSnapshot snapshot,
            OperatorCommandKind kind, CancellationToken token)
        {
            if (kind == OperatorCommandKind.Stop && _requiresAttachment && _attachment == null)
            {
                if (snapshot?.IsStructurallyValid() != true || snapshot.SessionId != _identity.SessionId ||
                    snapshot.RunId != _attachedRunId || snapshot.RunEpoch != _highestEpoch)
                    throw new InvalidDataException("OperatorStopLastApprovedIdentityInvalid");
            }
            else EnsureIdentity(snapshot);
            var command = new OperatorCommand
            {
                CommandId = RecoveryProtocolV7.NewId(), SessionId = snapshot.SessionId,
                RunId = snapshot.RunId, RunEpoch = snapshot.RunEpoch, BaseRevision = snapshot.Revision,
                PayloadSha256 = SupervisorProtocol.ComputeTextSha256("Operator" + kind),
                Kind = kind, IssuedUtcTicks = DateTime.UtcNow.Ticks
            };
            if (ManualBatchCommand.IsOperation(kind))
            {
                command.ManualBatch = new ManualBatchCommand { EngineInstanceId = snapshot.EngineInstanceId,
                    PauseIncidentId = snapshot.RecoveryIncidentId, PauseOwnerId = snapshot.RecoveryOwnerId };
                command.PayloadSha256 = command.ManualBatch.ComputeSha256();
            }
            return await SubmitTransactionAsync(command, token).ConfigureAwait(false);
        }

        public Task<SupervisorOperatorCommandResponse> SubmitChannelAsync(EngineStateSnapshot snapshot,
            OperatorCommandKind kind, int channel, CancellationToken token)
        {
            EnsureIdentity(snapshot);
            if (!ManualBatchCommand.IsChannelOperation(kind)) throw new InvalidDataException("ManualChannelOperationInvalid");
            var payload = new ManualBatchCommand { EngineInstanceId = snapshot.EngineInstanceId, Channel = channel,
                PauseIncidentId = kind == OperatorCommandKind.RetryQualification ? string.Empty : snapshot.RecoveryIncidentId,
                PauseOwnerId = kind == OperatorCommandKind.RetryQualification ? string.Empty : snapshot.RecoveryOwnerId };
            return SubmitTransactionAsync(new OperatorCommand { CommandId = RecoveryProtocolV7.NewId(), SessionId = snapshot.SessionId,
                RunId = snapshot.RunId, RunEpoch = snapshot.RunEpoch, BaseRevision = snapshot.Revision, Kind = kind,
                IssuedUtcTicks = DateTime.UtcNow.Ticks, ManualBatch = payload, PayloadSha256 = payload.ComputeSha256() }, token);
        }

        internal async Task<SupervisorOperatorCommandResponse> SubmitTransactionAsync(OperatorCommand command, CancellationToken token)
        {
            if (command?.IsStructurallyValid() != true) throw new InvalidDataException("OperatorCommandInvalid");
            _pendingCommands.TryAdd(command.CommandId, command);
            return await ExchangePendingAsync(command, false, token).ConfigureAwait(false);
        }

        public Task<SupervisorOperatorCommandResponse> SubmitConfigurationAsync(EngineStateSnapshot snapshot,
            TestConfigurationCommit configuration, CancellationToken token)
        {
            EnsureIdentity(snapshot);
            if (configuration?.IsStructurallyValid() != true) throw new InvalidDataException("ConfigurationPayloadInvalid");
            var command = new OperatorCommand
            {
                CommandId = RecoveryProtocolV7.NewId(), SessionId = snapshot.SessionId, RunId = snapshot.RunId,
                RunEpoch = snapshot.RunEpoch, BaseRevision = snapshot.Revision, Kind = OperatorCommandKind.CommitConfiguration,
                TestConfiguration = configuration.Clone(), PayloadSha256 = configuration.ComputeSha256(), IssuedUtcTicks = DateTime.UtcNow.Ticks
            };
            return SubmitTransactionAsync(command, token);
        }

        public Task<SupervisorOperatorCommandResponse> SubmitProjectSwitchAsync(EngineStateSnapshot snapshot,
            ProjectSwitchRequest project, CancellationToken token)
        {
            EnsureIdentity(snapshot);
            if (project?.IsStructurallyValid() != true || project.EngineInstanceId != snapshot.EngineInstanceId)
                throw new InvalidDataException("ProjectSwitchPayloadInvalid");
            return SubmitTransactionAsync(new OperatorCommand
            {
                CommandId = RecoveryProtocolV7.NewId(), SessionId = snapshot.SessionId, RunId = snapshot.RunId,
                RunEpoch = snapshot.RunEpoch, BaseRevision = snapshot.Revision, Kind = OperatorCommandKind.SwitchProject,
                ProjectSwitch = project.Clone(), PayloadSha256 = project.ComputeSha256(), IssuedUtcTicks = DateTime.UtcNow.Ticks
            }, token);
        }

        public Task<SupervisorOperatorCommandResponse> SubmitAlarmPanelAsync(EngineStateSnapshot snapshot,
            OperatorCommandKind kind, AlarmPanelCommand panel, CancellationToken token)
        {
            EnsureIdentity(snapshot);
            if (!AlarmPanelCommand.IsPanelOperation(kind) || panel?.IsStructurallyValid() != true)
                throw new InvalidDataException("AlarmPanelPayloadInvalid");
            return SubmitTransactionAsync(new OperatorCommand
            {
                CommandId = RecoveryProtocolV7.NewId(), SessionId = snapshot.SessionId, RunId = snapshot.RunId,
                RunEpoch = snapshot.RunEpoch, BaseRevision = snapshot.Revision, Kind = kind,
                AlarmPanel = panel.Clone(), PayloadSha256 = panel.ComputeSha256(), IssuedUtcTicks = DateTime.UtcNow.Ticks
            }, token);
        }

        public async Task<SupervisorOperatorCommandResponse> ResolvePendingAsync(CancellationToken token)
        {
            var command = _pendingCommands.Values.OrderBy(value => value.Kind == OperatorCommandKind.Stop ? 0 : 1)
                .ThenBy(value => value.IssuedUtcTicks).FirstOrDefault();
            if (command == null) return null;
            var response = await ExchangePendingAsync(command, true, token).ConfigureAwait(false);
            if (response.FailureCode == "OperatorCommandNotFound")
                response = await ExchangePendingAsync(command, false, token).ConfigureAwait(false);
            return response;
        }

        private async Task<SupervisorOperatorCommandResponse> ExchangePendingAsync(OperatorCommand command, bool queryOnly, CancellationToken token)
        {
            var response = await SubmitCommandAsync(command, token, queryOnly).ConfigureAwait(false);
            if (response.FailureCode != "OperatorCommandNotFound" && response.FailureCode != "SupervisorOperatorCommandRejected" &&
                (!(AlarmPanelCommand.IsPanelOperation(command.Kind) || command.Kind == OperatorCommandKind.SwitchProject ||
                    command.Kind == OperatorCommandKind.CommitConfiguration || PressureMaintenanceProtocol.IsOperation(command.Kind)) ||
                    !response.Accepted || response.ExecutionCompleted))
                _pendingCommands.TryRemove(command.CommandId, out _);
            return response;
        }

        internal async Task<SupervisorOperatorCommandResponse> SubmitCommandAsync(OperatorCommand command, CancellationToken token, bool queryOnly = false)
        {
            SupervisorOperatorCommandRequest request;
            using (var current = Process.GetCurrentProcess())
                request = new SupervisorOperatorCommandRequest
                {
                    RequestId = RecoveryProtocolV7.NewId(), ChallengeNonce = RecoveryProtocolV7.NewId(),
                    RequesterProcessId = current.Id, RequesterProcessStartUtcTicks = current.StartTime.ToUniversalTime().Ticks,
                    Command = command, QueryOnly = queryOnly
                };
            var response = await BoundedPipeTransport.ExchangeBinaryAsync(_supervisorPipeName,
                request.WriteTo, SupervisorOperatorCommandResponse.ReadFrom, queryOnly ? 3000 : 5000, token).ConfigureAwait(false);
            if (response.SchemaVersion != SupervisorProtocol.SchemaVersion ||
                response.RequestId != request.RequestId || response.ChallengeNonce != request.ChallengeNonce)
                throw new InvalidDataException("SupervisorOperatorResponseBindingInvalid");
            return response;
        }

        internal EngineStateSnapshot ReadSnapshot()
        {
            var response = SendEngine(new EngineHostRequest
            {
                RequestId = RecoveryProtocolV7.NewId(),
                Kind = EngineHostRequestKind.ReadLatestSnapshot
            });
            if (!response.Accepted || response.Snapshot?.IsStructurallyValid() != true)
                throw new InvalidDataException("EngineSnapshotUnavailable:" + response.Detail);
            EnsureIdentity(response.Snapshot);
            return response.Snapshot;
        }

        internal EngineTelemetryFrame ReadTelemetry()
        {
            var response = SendEngine(new EngineHostRequest
            {
                RequestId = RecoveryProtocolV7.NewId(),
                Kind = EngineHostRequestKind.ReadLatestTelemetry
            });
            if (!response.Accepted)
                throw new InvalidDataException("EngineTelemetryUnavailable:" + response.Detail);
            return response.Telemetry;
        }

        internal SupervisorOperatorCommandResponse Stop(EngineStateSnapshot snapshot)
        {
            return Submit(snapshot, OperatorCommandKind.Stop, "OperatorStop");
        }

        internal SupervisorOperatorCommandResponse Start(EngineStateSnapshot snapshot)
        {
            return Submit(snapshot, OperatorCommandKind.Start, "OperatorStart");
        }

        private SupervisorOperatorCommandResponse Submit(
            EngineStateSnapshot snapshot,
            OperatorCommandKind kind,
            string payload)
        {
            return SubmitAsync(snapshot, kind, CancellationToken.None).GetAwaiter().GetResult();
        }

        private void EnsureIdentity(EngineStateSnapshot snapshot)
        {
            var expected = _attachment?.Engine;
            if (snapshot?.IsStructurallyValid() != true || !string.Equals(snapshot.SessionId, _identity.SessionId,
                    StringComparison.Ordinal) ||
                !string.Equals(snapshot.RunId, expected?.RunId ?? _identity.RunId,
                    StringComparison.Ordinal) ||
                snapshot.RunEpoch != (expected?.RunEpoch ?? _identity.RunEpoch) ||
                (expected != null && snapshot.EngineInstanceId != expected.EngineInstanceId) ||
                (_requiresAttachment && expected == null))
                throw new InvalidDataException("EngineSnapshotIdentityMismatch");
        }

        private EngineHostResponse SendEngine(EngineHostRequest request)
        {
            return SendEngineAsync(request, CancellationToken.None).GetAwaiter().GetResult();
        }

        private async Task<EngineHostResponse> SendEngineAsync(EngineHostRequest request, CancellationToken token)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = EngineHostProtocol.MaximumRequestBytes };
            var responseBytes = await BoundedPipeTransport.ExchangeAsync(_enginePipeName,
                Encoding.UTF8.GetBytes(json.Serialize(request)), 3000, EngineHostProtocol.MaximumRequestBytes, token,
                pipe =>
                {
                    if (!_requiresAttachment) return;
                    var attachment = _attachment;
                    if (attachment == null || PipePeerIdentity.ServerProcessId(pipe) != attachment.EngineProcessId)
                        throw new InvalidDataException("EnginePipeProcessChanged");
                    using (var process = Process.GetProcessById(attachment.EngineProcessId))
                        if (process.StartTime.ToUniversalTime().Ticks != attachment.EngineProcessStartUtcTicks)
                            throw new InvalidDataException("EnginePipeProcessReused");
                })
                .ConfigureAwait(false);
            var response = json.Deserialize<EngineHostResponse>(Encoding.UTF8.GetString(responseBytes));
            if (response == null || response.SchemaVersion != EngineHostProtocol.SchemaVersion || response.RequestId != request.RequestId)
                throw new InvalidDataException("EngineResponseBindingInvalid");
            return response;
        }
    }
}
