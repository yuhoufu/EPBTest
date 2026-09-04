using System;
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
            }, timeoutMilliseconds);
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
            }, timeoutMilliseconds);
            if (!response.Accepted)
                throw new InvalidDataException(
                    "EngineHostFaultReadRejected:" + response.FailureCode);
            return response.FaultObservation;
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
            }, timeoutMilliseconds);
            if (!response.Accepted || response.RecoveryReceipt == null)
                throw new InvalidDataException(
                    "EngineHostCommandRejected:" + response.FailureCode + ":" + response.Detail);
            return response.RecoveryReceipt;
        }
    }
}
