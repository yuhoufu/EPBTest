using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace MTTFTest.Watchdog.Protocol
{
    public sealed class PressureMaintenanceHeartbeatRequest
    {
        public int ContractVersion { get; set; } = PressureMaintenanceProtocol.Version;
        public string RequestId { get; set; } = string.Empty;
        public string ChallengeNonce { get; set; } = string.Empty;
        public PressureMaintenanceHeartbeat Heartbeat { get; set; }
        public bool IsStructurallyValid() => ContractVersion == PressureMaintenanceProtocol.Version &&
            RecoveryProtocolV7.IsGuid(RequestId) && WatchdogProcessIdentityPolicy.IsValidChallengeNonce(ChallengeNonce) &&
            Heartbeat?.IsStructurallyValid() == true;
    }

    public sealed class PressureMaintenanceHeartbeatResponse
    {
        public int ContractVersion { get; set; } = PressureMaintenanceProtocol.Version;
        public string RequestId { get; set; } = string.Empty;
        public string ChallengeNonce { get; set; } = string.Empty;
        public bool Accepted { get; set; }
        public string Detail { get; set; } = string.Empty;
        public PressureMaintenanceLease Lease { get; set; }
        // Accepted means durable renewal, not an output command or physical safety proof.
        public bool Matches(PressureMaintenanceHeartbeatRequest request, long now) => request?.IsStructurallyValid() == true &&
            ContractVersion == PressureMaintenanceProtocol.Version && RequestId == request.RequestId && ChallengeNonce == request.ChallengeNonce &&
            Detail != null && Detail.Length <= 2048 && (!Accepted ? Lease == null : Lease?.IsStructurallyValid() == true &&
            request.Heartbeat.Binds(Lease) && Lease.IsLive(now) && Lease.LastHeartbeatSequence == request.Heartbeat.Sequence &&
            Lease.LastHeartbeatUtcTicks == request.Heartbeat.IssuedUtcTicks);
    }

    public static class PressureMaintenanceTransport
    {
        public const string SupervisorPipeName = SupervisorProtocol.PipeName + ".pressure-heartbeat.v1";
        public const int MaximumBytes = 16384;
        public const int TimeoutMilliseconds = 2000;
        public static async Task<PressureMaintenanceHeartbeatResponse> RenewAsync(PressureMaintenanceHeartbeatRequest request,
            Action<NamedPipeClientStream> validateSupervisor, CancellationToken token, string pipeName = null)
        {
            if (request?.IsStructurallyValid() != true) throw new InvalidDataException("MaintenanceHeartbeatRequestInvalid");
            if (validateSupervisor == null) throw new ArgumentNullException(nameof(validateSupervisor));
            var bytes = await BoundedPipeTransport.ExchangeAsync(pipeName ?? SupervisorPipeName, Encode(request),
                TimeoutMilliseconds, MaximumBytes, token, validateSupervisor).ConfigureAwait(false);
            var response = Decode<PressureMaintenanceHeartbeatResponse>(bytes);
            if (response?.Matches(request, DateTime.UtcNow.Ticks) != true)
                throw new InvalidDataException("MaintenanceHeartbeatResponseBindingInvalid");
            return response;
        }
        public static byte[] Encode(object value)
        {
            var bytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer { MaxJsonLength = MaximumBytes }.Serialize(value));
            if (bytes.Length == 0 || bytes.Length > MaximumBytes) throw new InvalidDataException("MaintenanceFrameLengthInvalid");
            return bytes;
        }
        public static T Decode<T>(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaximumBytes) throw new InvalidDataException("MaintenanceFrameLengthInvalid");
            return new JavaScriptSerializer { MaxJsonLength = MaximumBytes, RecursionLimit = 24 }
                .Deserialize<T>(new UTF8Encoding(false, true).GetString(bytes));
        }
        public static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken token)
        {
            var header = new byte[4];
            await ReadExactlyAsync(stream, header, token).ConfigureAwait(false);
            var length = BitConverter.ToInt32(header, 0);
            if (length <= 0 || length > MaximumBytes) throw new InvalidDataException("MaintenanceFrameLengthInvalid");
            var bytes = new byte[length];
            await ReadExactlyAsync(stream, bytes, token).ConfigureAwait(false);
            return bytes;
        }
        public static async Task WriteFrameAsync(Stream stream, object value, CancellationToken token)
        {
            var bytes = Encode(value); var header = BitConverter.GetBytes(bytes.Length);
            await stream.WriteAsync(header, 0, 4, token).ConfigureAwait(false);
            await stream.WriteAsync(bytes, 0, bytes.Length, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }
        private static async Task ReadExactlyAsync(Stream stream, byte[] bytes, CancellationToken token)
        {
            var offset = 0;
            while (offset < bytes.Length)
            {
                var count = await stream.ReadAsync(bytes, offset, bytes.Length - offset, token).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException("MaintenanceFrameTruncated");
                offset += count;
            }
        }
    }
}
