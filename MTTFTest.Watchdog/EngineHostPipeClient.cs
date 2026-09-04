using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Security.Principal;
using System.Web.Script.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Watchdog
{
    internal static class EngineHostPipeClient
    {
        internal static async Task<PressureMaintenanceLease> SendMaintenanceLeaseAsync(PressureMaintenanceLease lease,
            Func<int, long, bool> authorizeEngine, CancellationToken token, string pipeName = null)
        {
            if (lease?.IsStructurallyValid() != true || authorizeEngine == null)
                throw new InvalidDataException("MaintenanceLeasePublicationInvalid");
            var request = new EngineHostRequest { RequestId = RecoveryProtocolV7.NewId(),
                Kind = EngineHostRequestKind.UpdateMaintenanceLease, MaintenanceLease = lease.Clone() };
            var bytes = await BoundedPipeTransport.ExchangeAsync(pipeName ?? EngineHostProtocol.MaintenanceLeasePipeName,
                PressureMaintenanceTransport.Encode(request), 1000, PressureMaintenanceTransport.MaximumBytes, token, peer =>
                {
                    var pid = PipePeerIdentity.ServerProcessId(peer);
                    using (var process = Process.GetProcessById(pid))
                        if (!authorizeEngine(pid, process.StartTime.ToUniversalTime().Ticks))
                            throw new InvalidDataException("MaintenanceLeaseEnginePeerNotAuthorized");
                }).ConfigureAwait(false);
            var response = PressureMaintenanceTransport.Decode<EngineHostResponse>(bytes);
            if (response?.Accepted != true || response.SchemaVersion != EngineHostProtocol.SchemaVersion || response.RequestId != request.RequestId ||
                response.MaintenanceLease?.IsStructurallyValid() != true || response.MaintenanceLease.ComputeSha256() != lease.ComputeSha256())
                throw new InvalidDataException("MaintenanceLeaseEngineResponseInvalid");
            return response.MaintenanceLease;
        }

        internal static EngineStateSnapshot ReadBoundSnapshot(string sessionId, Func<int, long, bool> authorize,
            out int processId, out long startTicks, int timeoutMilliseconds = 3000, string pipeName = null)
        {
            var observedId = 0; long observedStart = 0;
            var request = new EngineHostRequest { RequestId = RecoveryProtocolV7.NewId(), Kind = EngineHostRequestKind.ReadLatestSnapshot };
            var json = new JavaScriptSerializer { MaxJsonLength = EngineHostProtocol.MaximumRequestBytes };
            var bytes = BoundedPipeTransport.ExchangeAsync(pipeName ?? EngineHostProtocol.SupervisorReadPipeName,
                Encoding.UTF8.GetBytes(json.Serialize(request)), timeoutMilliseconds, EngineHostProtocol.MaximumRequestBytes,
                CancellationToken.None, peer =>
                {
                    observedId = PipePeerIdentity.ServerProcessId(peer);
                    using (var process = Process.GetProcessById(observedId)) observedStart = process.StartTime.ToUniversalTime().Ticks;
                    if (authorize == null || !authorize(observedId, observedStart)) throw new InvalidDataException("EngineSnapshotPeerNotAuthorized");
                }).GetAwaiter().GetResult();
            var response = json.Deserialize<EngineHostResponse>(Encoding.UTF8.GetString(bytes));
            if (response?.Accepted != true || response.SchemaVersion != EngineHostProtocol.SchemaVersion || response.RequestId != request.RequestId ||
                response.Snapshot?.IsStructurallyValid() != true || response.Snapshot.SessionId != sessionId)
                throw new InvalidDataException("EngineSnapshotPeerResponseInvalid");
            processId = observedId; startTicks = observedStart;
            return response.Snapshot;
        }
        internal static EngineHostResponse Send(
            EngineHostRequest request,
            int timeoutMilliseconds = 3000,
            string pipeName = null)
        {
            return SendAsync(request, timeoutMilliseconds,
                pipeName ?? EngineHostProtocol.PipeName).GetAwaiter().GetResult();
        }

        private static async Task<EngineHostResponse> SendAsync(
            EngineHostRequest request, int timeoutMilliseconds, string pipeName)
        {
            if (request?.IsStructurallyValid() != true)
                throw new InvalidDataException("EngineHostRequestInvalid");
            if (timeoutMilliseconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
            var json = new JavaScriptSerializer
            {
                MaxJsonLength = EngineHostProtocol.MaximumRequestBytes
            };
            var payload = Encoding.UTF8.GetBytes(json.Serialize(request));
            if (payload.Length > EngineHostProtocol.MaximumRequestBytes)
                throw new InvalidDataException("EngineHostRequestLengthInvalid");
            using (var deadline = new CancellationTokenSource(timeoutMilliseconds))
            using (var pipe = new NamedPipeClientStream(
                       ".",
                       pipeName,
                       PipeDirection.InOut,
                       PipeOptions.Asynchronous,
                       TokenImpersonationLevel.Identification))
            // PipeStream does not support Stream.ReadTimeout/WriteTimeout on
            // .NET Framework. Closing the handle also bounds stalled peers.
            using (deadline.Token.Register(() => pipe.Dispose()))
            {
                try
                {
                    var token = deadline.Token;
                    await pipe.ConnectAsync(timeoutMilliseconds, token).ConfigureAwait(false);
                    var header = BitConverter.GetBytes(payload.Length);
                    await pipe.WriteAsync(header, 0, header.Length, token).ConfigureAwait(false);
                    await pipe.WriteAsync(payload, 0, payload.Length, token).ConfigureAwait(false);
                    await pipe.FlushAsync(token).ConfigureAwait(false);
                    await ReadExactlyAsync(pipe, header, token).ConfigureAwait(false);
                    var length = BitConverter.ToInt32(header, 0);
                    if (length <= 0 || length > EngineHostProtocol.MaximumRequestBytes)
                        throw new InvalidDataException("EngineHostResponseLengthInvalid");
                    var bytes = new byte[length];
                    await ReadExactlyAsync(pipe, bytes, token).ConfigureAwait(false);
                    var response = json.Deserialize<EngineHostResponse>(
                        Encoding.UTF8.GetString(bytes));
                    if (response == null ||
                        response.SchemaVersion != EngineHostProtocol.SchemaVersion ||
                        !string.Equals(response.RequestId, request.RequestId,
                            StringComparison.Ordinal))
                        throw new InvalidDataException("EngineHostResponseBindingInvalid");
                    return response;
                }
                catch (Exception ex) when (deadline.IsCancellationRequested)
                {
                    throw new TimeoutException("EngineHostPipeDeadlineExceeded", ex);
                }
            }
        }

        private static async Task ReadExactlyAsync(
            Stream stream, byte[] bytes, CancellationToken token)
        {
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes, offset, bytes.Length - offset, token)
                    .ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("EngineHostResponseTruncated");
                offset += read;
            }
        }

        internal static EngineStateSnapshot ReadSnapshot(int timeoutMilliseconds = 3000)
        {
            var response = Send(new EngineHostRequest
            {
                RequestId = RecoveryProtocolV7.NewId(),
                Kind = EngineHostRequestKind.ReadLatestSnapshot
            }, timeoutMilliseconds, EngineHostProtocol.SupervisorReadPipeName);
            if (!response.Accepted || response.Snapshot?.IsStructurallyValid() != true)
                throw new InvalidDataException(
                    "EngineHostSnapshotRejected:" + response.FailureCode + ":" + response.Detail);
            return response.Snapshot;
        }

        internal static FaultObservation ReadFault(int timeoutMilliseconds = 3000)
        {
            var response = Send(new EngineHostRequest
            {
                RequestId = RecoveryProtocolV7.NewId(),
                Kind = EngineHostRequestKind.ReadLatestFault
            }, timeoutMilliseconds, EngineHostProtocol.SupervisorReadPipeName);
            if (!response.Accepted)
                throw new InvalidDataException(
                    "EngineHostFaultReadRejected:" + response.FailureCode);
            return response.FaultObservation;
        }

        internal static EngineStateSnapshot ReadPanelSnapshot(int timeoutMilliseconds = 1000)
        {
            var response = Send(new EngineHostRequest { RequestId = RecoveryProtocolV7.NewId(),
                Kind = EngineHostRequestKind.ReadLatestSnapshot }, timeoutMilliseconds, EngineHostProtocol.PanelPipeName);
            if (!response.Accepted || response.Snapshot?.IsStructurallyValid() != true)
                throw new InvalidDataException("AlarmPanelEngineSnapshotRejected");
            return response.Snapshot;
        }

        internal static OperatorExecutionReceipt ExecutePanel(OperatorCommand command, int timeoutMilliseconds = 11000, string pipeName = null)
        {
            var response = Send(new EngineHostRequest { RequestId = RecoveryProtocolV7.NewId(),
                Kind = EngineHostRequestKind.ExecutePanelCommand, OperatorCommand = command },
                timeoutMilliseconds, pipeName ?? EngineHostProtocol.PanelPipeName);
            if (!response.Accepted || response.OperatorReceipt?.Matches(command) != true ||
                response.Snapshot?.IsStructurallyValid() != true || response.Snapshot.SessionId != command.SessionId ||
                response.Snapshot.RunId != command.RunId || response.Snapshot.RunEpoch != command.RunEpoch)
                throw new InvalidDataException("AlarmPanelExecutionReceiptInvalid:" + response.Detail);
            return response.OperatorReceipt;
        }

        internal static RecoveryCommandReceipt Execute(
            RecoveryCommand command,
            int timeoutMilliseconds = 5000)
        {
            var response = Send(new EngineHostRequest
            {
                RequestId = RecoveryProtocolV7.NewId(),
                Kind = EngineHostRequestKind.ExecuteRecoveryCommand,
                RecoveryCommand = command
            }, timeoutMilliseconds, EngineHostProtocol.IsPrioritySafetyCommand(command.Kind)
                ? EngineHostProtocol.SafetyPipeName : EngineHostProtocol.PipeName);
            if (!response.Accepted || response.RecoveryReceipt == null || response.RecoveryReceipt.CommandId != command.CommandId ||
                response.RecoveryReceipt.IdempotencyKey != command.IdempotencyKey ||
                !EngineUiContract.IsUtcTicks(response.RecoveryReceipt.CompletedUtcTicks))
                throw new InvalidDataException(
                    "EngineHostCommandRejected:" + response.FailureCode + ":" + response.Detail);
            if (EngineHostProtocol.RequiresIndependentSafetyHandoff(command.Kind) &&
                (response.Snapshot?.IsStructurallyValid() != true ||
                 response.RecoveryReceipt.HardwareHandoff?.Matches(command, response.Snapshot.EngineInstanceId) != true))
                throw new InvalidDataException("EngineHostHardwareHandoffBindingInvalid");
            return response.RecoveryReceipt;
        }
    }
}
