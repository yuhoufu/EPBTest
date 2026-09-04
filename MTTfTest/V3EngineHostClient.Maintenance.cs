using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.Watchdog.Protocol;

namespace MTEmbTest
{
    internal interface IEnginePressureMaintenanceUiClient
    {
        int UiProcessId { get; }
        long UiProcessStartUtcTicks { get; }
        Task<SupervisorOperatorCommandResponse> SubmitMaintenanceAsync(EngineStateSnapshot snapshot, OperatorCommandKind kind,
            PressureMaintenanceCommand payload, CancellationToken token);
        Task<PressureMaintenanceHeartbeatResponse> RenewMaintenanceAsync(PressureMaintenanceHeartbeat heartbeat, CancellationToken token);
    }

    internal sealed partial class V3EngineHostClient : IEnginePressureMaintenanceUiClient
    {
        public int UiProcessId { get { using (var process = Process.GetCurrentProcess()) return process.Id; } }
        public long UiProcessStartUtcTicks { get { using (var process = Process.GetCurrentProcess()) return process.StartTime.ToUniversalTime().Ticks; } }
        public Task<SupervisorOperatorCommandResponse> SubmitMaintenanceAsync(EngineStateSnapshot snapshot, OperatorCommandKind kind,
            PressureMaintenanceCommand payload, CancellationToken token)
        {
            EnsureIdentity(snapshot);
            if (payload?.IsStructurallyValid(kind) != true || payload.EngineInstanceId != snapshot.EngineInstanceId ||
                payload.UiProcessId != UiProcessId || payload.UiProcessStartUtcTicks != UiProcessStartUtcTicks)
                throw new InvalidDataException("MaintenanceUiCommandBindingInvalid");
            return SubmitTransactionAsync(new OperatorCommand { CommandId = RecoveryProtocolV7.NewId(), SessionId = snapshot.SessionId,
                RunId = snapshot.RunId, RunEpoch = snapshot.RunEpoch, BaseRevision = snapshot.Revision, Kind = kind,
                IssuedUtcTicks = DateTime.UtcNow.Ticks, PressureMaintenance = payload.Clone(), PayloadSha256 = payload.ComputeSha256() }, token);
        }
        public Task<PressureMaintenanceHeartbeatResponse> RenewMaintenanceAsync(PressureMaintenanceHeartbeat heartbeat, CancellationToken token) =>
            RenewMaintenanceAsync(heartbeat, token, PressureMaintenanceTransport.SupervisorPipeName, SupervisorServicePeerIdentity.Validate);

        internal Task<PressureMaintenanceHeartbeatResponse> RenewMaintenanceAsync(PressureMaintenanceHeartbeat heartbeat,
            CancellationToken token, string pipeName, Action<NamedPipeClientStream> validateSupervisor)
        {
            if (heartbeat?.IsStructurallyValid() != true || heartbeat.SessionId != _identity.SessionId ||
                heartbeat.UiProcessId != UiProcessId || heartbeat.UiProcessStartUtcTicks != UiProcessStartUtcTicks)
                throw new InvalidDataException("MaintenanceHeartbeatUiBindingInvalid");
            return PressureMaintenanceTransport.RenewAsync(new PressureMaintenanceHeartbeatRequest
            { RequestId = RecoveryProtocolV7.NewId(), ChallengeNonce = RecoveryProtocolV7.NewId(), Heartbeat = heartbeat }, validateSupervisor, token, pipeName);
        }
    }
}
