using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Security.Principal;
using System.Web.Script.Serialization;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Watchdog
{
    internal static class EngineHostPipeClient
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer
        {
            MaxJsonLength = int.MaxValue
        };

        internal static EngineHostResponse Send(
            EngineHostRequest request,
            int timeoutMilliseconds = 3000)
        {
            if (request?.IsStructurallyValid() != true)
                throw new InvalidDataException("EngineHostRequestInvalid");
            using (var pipe = new NamedPipeClientStream(
                       ".",
                       EngineHostProtocol.PipeName,
                       PipeDirection.InOut,
                       PipeOptions.None,
                       TokenImpersonationLevel.Identification))
            {
                pipe.Connect(timeoutMilliseconds);
                pipe.ReadTimeout = timeoutMilliseconds;
                pipe.WriteTimeout = timeoutMilliseconds;
                using (var writer = new BinaryWriter(pipe, new UTF8Encoding(false), true))
                using (var reader = new BinaryReader(pipe, new UTF8Encoding(false), true))
                {
                    var payload = Encoding.UTF8.GetBytes(Json.Serialize(request));
                    writer.Write(payload.Length);
                    writer.Write(payload);
                    writer.Flush();
                    var length = reader.ReadInt32();
                    if (length <= 0 || length > EngineHostProtocol.MaximumRequestBytes)
                        throw new InvalidDataException("EngineHostResponseLengthInvalid");
                    var bytes = reader.ReadBytes(length);
                    if (bytes.Length != length)
                        throw new EndOfStreamException("EngineHostResponseTruncated");
                    var response = Json.Deserialize<EngineHostResponse>(
                        Encoding.UTF8.GetString(bytes));
                    if (response == null ||
                        response.SchemaVersion != EngineHostProtocol.SchemaVersion ||
                        !string.Equals(response.RequestId, request.RequestId,
                            StringComparison.Ordinal))
                        throw new InvalidDataException("EngineHostResponseBindingInvalid");
                    return response;
                }
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
