using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Web.Script.Serialization;
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

    internal sealed class V3EngineHostClient
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer
        {
            MaxJsonLength = int.MaxValue
        };
        private readonly V3EngineIdentity _identity;

        internal V3EngineHostClient(V3EngineIdentity identity)
        {
            _identity = identity ?? throw new ArgumentNullException(nameof(identity));
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
            EnsureIdentity(snapshot);
            var command = new OperatorCommand
            {
                CommandId = RecoveryProtocolV7.NewId(),
                SessionId = _identity.SessionId,
                RunId = _identity.RunId,
                RunEpoch = _identity.RunEpoch,
                BaseRevision = snapshot.Revision,
                PayloadSha256 = SupervisorProtocol.ComputeTextSha256(payload),
                Kind = kind,
                IssuedUtcTicks = DateTime.UtcNow.Ticks
            };
            using (var current = Process.GetCurrentProcess())
            {
                var request = new SupervisorOperatorCommandRequest
                {
                    RequestId = RecoveryProtocolV7.NewId(),
                    ChallengeNonce = RecoveryProtocolV7.NewId(),
                    RequesterProcessId = current.Id,
                    RequesterProcessStartUtcTicks =
                        current.StartTime.ToUniversalTime().Ticks,
                    Command = command
                };
                using (var pipe = new NamedPipeClientStream(
                           ".", SupervisorProtocol.PipeName,
                           PipeDirection.InOut, PipeOptions.None))
                {
                    pipe.Connect(5000);
                    using (var writer = new BinaryWriter(pipe, new UTF8Encoding(false), true))
                    using (var reader = new BinaryReader(pipe, new UTF8Encoding(false), true))
                    {
                        request.WriteTo(writer);
                        var response = SupervisorOperatorCommandResponse.ReadFrom(reader);
                        if (response.SchemaVersion != SupervisorProtocol.SchemaVersion ||
                            !string.Equals(response.RequestId, request.RequestId,
                                StringComparison.Ordinal) ||
                            !string.Equals(response.ChallengeNonce, request.ChallengeNonce,
                                StringComparison.Ordinal))
                            throw new InvalidDataException(
                                "SupervisorOperatorResponseBindingInvalid");
                        return response;
                    }
                }
            }
        }

        private void EnsureIdentity(EngineStateSnapshot snapshot)
        {
            if (!string.Equals(snapshot.SessionId, _identity.SessionId,
                    StringComparison.Ordinal) ||
                !string.Equals(snapshot.RunId, _identity.RunId,
                    StringComparison.Ordinal) ||
                snapshot.RunEpoch != _identity.RunEpoch)
                throw new InvalidDataException("EngineSnapshotIdentityMismatch");
        }

        private static EngineHostResponse SendEngine(EngineHostRequest request)
        {
            using (var pipe = new NamedPipeClientStream(
                       ".", EngineHostProtocol.PipeName,
                       PipeDirection.InOut, PipeOptions.None))
            {
                pipe.Connect(3000);
                using (var writer = new BinaryWriter(pipe, new UTF8Encoding(false), true))
                using (var reader = new BinaryReader(pipe, new UTF8Encoding(false), true))
                {
                    var payload = Encoding.UTF8.GetBytes(Json.Serialize(request));
                    writer.Write(payload.Length);
                    writer.Write(payload);
                    writer.Flush();
                    var length = reader.ReadInt32();
                    if (length <= 0 || length > EngineHostProtocol.MaximumRequestBytes)
                        throw new InvalidDataException("EngineResponseLengthInvalid");
                    var bytes = reader.ReadBytes(length);
                    if (bytes.Length != length)
                        throw new EndOfStreamException("EngineResponseTruncated");
                    return Json.Deserialize<EngineHostResponse>(
                        Encoding.UTF8.GetString(bytes));
                }
            }
        }
    }
}
