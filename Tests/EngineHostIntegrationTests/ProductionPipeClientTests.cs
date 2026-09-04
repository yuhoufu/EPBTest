using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using MTTFTest.Watchdog;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.EngineHostIntegrationTests
{
    internal static class ProductionPipeClientTests
    {
        internal static int RunAll()
        {
            CheckReply("ProductionPipeClientReadsRealNamedPipe", (pipe, request) =>
                WriteResponse(pipe, request.RequestId), null);
            CheckReply("ProductionPipeClientRejectsForeignResponse", (pipe, request) =>
                WriteResponse(pipe, RecoveryProtocolV7.NewId()), typeof(InvalidDataException));
            CheckReply("ProductionPipeClientRejectsTruncatedFrame", (pipe, request) =>
            {
                var header = BitConverter.GetBytes(200);
                pipe.Write(header, 0, header.Length);
            }, typeof(EndOfStreamException));
            CheckReply("ProductionPipeClientBoundsConnectedSilentPeer", (pipe, request) =>
                Task.Delay(1500).GetAwaiter().GetResult(), typeof(TimeoutException), 400);
            BoundProjectSnapshot(false, false);
            BoundProjectSnapshot(true, false);
            BoundProjectSnapshot(false, true);
            return 7;
        }

        private static void BoundProjectSnapshot(bool denyPeer, bool foreignRunSession)
        {
            var pipeName = "MTTFTest.ProjectPeer." + RecoveryProtocolV7.NewId();
            var sessionId = RecoveryProtocolV7.NewId(); var sentBytes = false;
            using (var process = Process.GetCurrentProcess())
            using (var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
            {
                var task = Task.Run(async () =>
                {
                    await pipe.WaitForConnectionAsync();
                    using (var reader = new BinaryReader(pipe, Encoding.UTF8, true))
                    {
                        if (denyPeer)
                        {
                            try { sentBytes = reader.ReadBytes(1).Length != 0; } catch (IOException) { }
                            return;
                        }
                        var json = new JavaScriptSerializer();
                        var request = json.Deserialize<EngineHostRequest>(Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32())));
                        sentBytes = true;
                        var snapshot = new EngineStateSnapshot { SessionId = foreignRunSession ? RecoveryProtocolV7.NewId() : sessionId,
                            RunId = RecoveryProtocolV7.NewId(), RunEpoch = 1, EngineInstanceId = RecoveryProtocolV7.NewId(), Revision = 1,
                            PulseSequence = 1, CapturedUtcTicks = DateTime.UtcNow.Ticks, State = SystemTerminalState.SafeIdleAlarmed };
                        var bytes = Encoding.UTF8.GetBytes(json.Serialize(new EngineHostResponse { Accepted = true, RequestId = request.RequestId, Snapshot = snapshot }));
                        using (var writer = new BinaryWriter(pipe, Encoding.UTF8, true)) { writer.Write(bytes.Length); writer.Write(bytes); writer.Flush(); }
                    }
                });
                Exception failure = null;
                try
                {
                    var snapshot = EngineHostPipeClient.ReadBoundSnapshot(sessionId,
                        (id, started) => !denyPeer && id == process.Id && started == process.StartTime.ToUniversalTime().Ticks,
                        out var serverId, out var serverStart, 3000, pipeName);
                    if (snapshot.SessionId != sessionId || serverId != process.Id || serverStart != process.StartTime.ToUniversalTime().Ticks)
                        throw new Exception("Snapshot identity was not tied to actual pipe process");
                }
                catch (Exception ex) { failure = ex; }
                if (!task.Wait(4000)) throw new Exception("Bound project peer did not finish");
                if (denyPeer && sentBytes || (denyPeer || foreignRunSession) != (failure is InvalidDataException) ||
                    !denyPeer && !foreignRunSession && failure != null)
                    throw new Exception("Bound snapshot peer authorization/response fence failed", failure);
                Console.WriteLine("PASS ProjectSnapshotPeerBinding deny=" + denyPeer + " foreignSession=" + foreignRunSession);
            }
        }

        private static void CheckReply(string name,
            Action<NamedPipeServerStream, EngineHostRequest> respond,
            Type expectedException, int timeout = 3000)
        {
            var pipeName = "MTTFTest.ProductionPipeTest." + RecoveryProtocolV7.NewId();
            using (var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut,
                       1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
            {
                var peer = Task.Run(async () =>
                {
                    await server.WaitForConnectionAsync().ConfigureAwait(false);
                    using (var reader = new BinaryReader(server, Encoding.UTF8, true))
                    {
                        var payload = reader.ReadBytes(reader.ReadInt32());
                        var request = new JavaScriptSerializer().Deserialize<EngineHostRequest>(
                            Encoding.UTF8.GetString(payload));
                        respond(server, request);
                    }
                    server.Disconnect();
                });
                Exception failure = null;
                var elapsed = Stopwatch.StartNew();
                try
                {
                    var response = EngineHostPipeClient.Send(new EngineHostRequest
                    {
                        RequestId = RecoveryProtocolV7.NewId(),
                        Kind = EngineHostRequestKind.Ping
                    }, timeout, pipeName);
                    if (!response.Accepted) throw new Exception("UnexpectedRejectedResponse");
                }
                catch (Exception ex) { failure = ex; }
                if (expectedException == null && failure != null) throw failure;
                if (expectedException != null &&
                    (failure == null || !expectedException.IsInstanceOfType(failure)))
                    throw new Exception(name + ":UnexpectedException", failure);
                if (elapsed.ElapsedMilliseconds > timeout + 1500)
                    throw new Exception(name + ":DeadlineNotBounded");
                if (!peer.Wait(4000)) throw new Exception(name + ":PeerDidNotExit");
                Console.WriteLine("PASS " + name);
            }
        }

        private static void WriteResponse(NamedPipeServerStream pipe, string requestId)
        {
            var payload = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(
                new EngineHostResponse { RequestId = requestId, Accepted = true }));
            var header = BitConverter.GetBytes(payload.Length);
            pipe.Write(header, 0, header.Length);
            pipe.Write(payload, 0, payload.Length);
            pipe.Flush();
        }
    }
}
