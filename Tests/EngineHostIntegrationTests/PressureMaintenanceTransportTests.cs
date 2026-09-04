using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.EngineHost;
using MTTFTest.Watchdog;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.EngineHostIntegrationTests
{
    internal static class PressureMaintenanceTransportTests
    {
        internal static int RunAll()
        {
            Check("MaintenanceLeaseRequiresExplicitPrepare", FenceRequiresPrepare);
            Check("MaintenanceLeaseRenewalAndRevocationAreMonotonic", FenceRenewal);
            Check("MaintenanceLeaseWallClockAndMonotonicExpiryLatch", FenceClocks);
            Check("MaintenanceLeaseOwnerCannotDisplaceUnretiredExecutor", FenceOwners);
            Check("MaintenanceHeartbeatAuthenticatesActualPipePeer", HeartbeatAuthentication);
            Check("MaintenanceHeartbeatRejectsMalformedAndStalledFrames", HeartbeatBadFrames);
            Check("MaintenanceHeartbeatClientValidatesResponseIdentity", HeartbeatClientBinding);
            Check("MaintenanceLeasePublicationAuthenticatesPeerBeforeWriting", PublicationBinding);
            return 8;
        }

        internal static PressureMaintenanceLease Lease(string session = null, string run = null, string engine = null)
        {
            var now = DateTime.UtcNow.Ticks;
            using (var process = Process.GetCurrentProcess())
                return new PressureMaintenanceLease { SessionId = session ?? RecoveryProtocolV7.NewId(), RunId = run ?? RecoveryProtocolV7.NewId(),
                    RunEpoch = 1, IncidentId = RecoveryProtocolV7.NewId(), OwnerId = RecoveryProtocolV7.NewId(),
                    EngineInstanceId = engine ?? RecoveryProtocolV7.NewId(), Generation = 1, UiProcessId = process.Id,
                    UiProcessStartUtcTicks = process.StartTime.ToUniversalTime().Ticks, HydraulicId = 1, Revision = 1,
                    CreatedUtcTicks = now, LastHeartbeatUtcTicks = now, ExpiresUtcTicks = now + TimeSpan.FromSeconds(10).Ticks,
                    AbsoluteDeadlineUtcTicks = now + TimeSpan.FromMinutes(30).Ticks };
        }

        internal static RecoveryCommand Command(PressureMaintenanceLease lease, bool prepare = true)
        {
            var op = new OperatorCommand { SessionId = lease.SessionId, RunId = lease.RunId, RunEpoch = lease.RunEpoch,
                CommandId = RecoveryProtocolV7.NewId(), BaseRevision = 1, IssuedUtcTicks = lease.CreatedUtcTicks,
                Kind = prepare ? OperatorCommandKind.BeginPressureMaintenance : OperatorCommandKind.SetMaintenancePressure,
                PressureMaintenance = new PressureMaintenanceCommand { EngineInstanceId = lease.EngineInstanceId,
                    UiProcessId = lease.UiProcessId, UiProcessStartUtcTicks = lease.UiProcessStartUtcTicks, HydraulicId = 1,
                    IncidentId = prepare ? string.Empty : lease.IncidentId, OwnerId = prepare ? string.Empty : lease.OwnerId,
                    PressureBar = prepare ? 0 : 30 } };
            op.PayloadSha256 = op.PressureMaintenance.ComputeSha256();
            var command = new RecoveryCommand { CommandId = RecoveryProtocolV7.NewId(), OwnerId = lease.OwnerId,
                Kind = prepare ? RecoveryCommandKind.PreparePressureMaintenance : RecoveryCommandKind.SetMaintenancePressure,
                CommandSequence = 1, TargetResource = "System", DeadlineUtcTicks = lease.CreatedUtcTicks + TimeSpan.FromMinutes(3).Ticks,
                PressureMaintenance = lease.Clone(), OperatorTransaction = op, Identity = new RecoveryIdentity { SessionId = lease.SessionId,
                    RunId = lease.RunId, RunEpoch = lease.RunEpoch, IncidentId = lease.IncidentId, Generation = lease.Generation, Revision = 1, ResourceScope = "System" } };
            command.IdempotencyKey = command.ExpectedIdempotencyKey();
            Assert(command.IsStructurallyValid(), "invalid test command"); return command;
        }

        private static EnginePressureMaintenanceLeaseFence Fence(PressureMaintenanceLease lease, Func<long> now = null, Func<long> mono = null) =>
            new EnginePressureMaintenanceLeaseFence(lease.SessionId, lease.RunId, lease.RunEpoch, lease.EngineInstanceId, now, mono);

        private static void FenceRequiresPrepare()
        {
            var lease = Lease(); var fence = Fence(lease);
            fence.Receive(lease);
            Assert(!fence.IsAuthorized(lease.IncidentId, lease.OwnerId), "publication energized before prepare");
            Reject(() => fence.RequireForCommand(Command(lease, false)));
            fence.RequireForCommand(Command(lease));
            Assert(fence.IsAuthorized(lease.IncidentId, lease.OwnerId), "valid prepare did not bind executor");
            Reject(() => fence.Receive(Lease(lease.SessionId, lease.RunId)));
            Reject(() => fence.RequireForCommand(Command(Lease(lease.SessionId, lease.RunId, lease.EngineInstanceId))));
            Assert(!fence.IsAuthorized(lease.IncidentId, RecoveryProtocolV7.NewId()), "foreign owner allowed");
        }

        private static void FenceRenewal()
        {
            var lease = Lease(); var now = lease.CreatedUtcTicks; long mono = 0;
            var fence = Fence(lease, () => now, () => mono); fence.Receive(lease); fence.RequireForCommand(Command(lease));
            now += TimeSpan.FromSeconds(4).Ticks; mono += TimeSpan.FromSeconds(4).Ticks;
            var renewed = lease.Clone(); renewed.Revision++; renewed.LastHeartbeatSequence = 1; renewed.LastHeartbeatUtcTicks = now;
            renewed.ExpiresUtcTicks = now + TimeSpan.FromSeconds(10).Ticks;
            Assert(fence.Receive(renewed).Revision == 2, "renewal missing");
            Reject(() => fence.Receive(lease));
            var collision = renewed.Clone(); collision.ExpiresUtcTicks--; Reject(() => fence.Receive(collision));
            now += TimeSpan.FromSeconds(5).Ticks; mono += TimeSpan.FromSeconds(5).Ticks;
            fence.Receive(renewed);
            Assert(fence.IsAuthorized(lease.IncidentId, lease.OwnerId), "duplicate revoked live authority");
            mono += TimeSpan.FromSeconds(5).Ticks;
            Assert(!fence.IsAuthorized(lease.IncidentId, lease.OwnerId), "duplicate extended monotonic deadline");
            var late = renewed.Clone(); late.Revision++; late.LastHeartbeatSequence++; late.LastHeartbeatUtcTicks = now;
            late.ExpiresUtcTicks = now + TimeSpan.FromSeconds(10).Ticks; Reject(() => fence.Receive(late));
            late.Revoked = true; fence.Receive(late);
            Reject(() => fence.RequireForCommand(Command(renewed)));
        }

        private static void FenceClocks()
        {
            foreach (var mode in new[] { 0, 1, 2 })
            {
                var lease = Lease(); var now = lease.CreatedUtcTicks; long mono = 0;
                var fence = Fence(lease, () => now, () => mono); fence.Receive(lease); fence.RequireForCommand(Command(lease));
                if (mode == 0) now--; // Wall clock adjustment cannot lengthen authority.
                if (mode == 1) now += TimeSpan.FromSeconds(11).Ticks;
                if (mode == 2) mono += TimeSpan.FromSeconds(11).Ticks;
                Assert(!fence.IsAuthorized(lease.IncidentId, lease.OwnerId), "clock expiry did not latch:" + mode);
                now = lease.CreatedUtcTicks + 1; mono = 1;
                Reject(() => fence.Receive(lease));
                Assert(!fence.IsAuthorized(lease.IncidentId, lease.OwnerId), "clock reset revived expired executor");
            }
        }

        private static void FenceOwners()
        {
            var lease = Lease(); var now = lease.CreatedUtcTicks; long mono = 0;
            var fence = Fence(lease, () => now, () => mono); fence.Receive(lease); fence.RequireForCommand(Command(lease));
            var next = lease.Clone(); next.OwnerId = RecoveryProtocolV7.NewId(); next.IncidentId = RecoveryProtocolV7.NewId();
            next.CreatedUtcTicks++; next.LastHeartbeatUtcTicks++; next.ExpiresUtcTicks++; next.AbsoluteDeadlineUtcTicks++;
            now++; Reject(() => fence.Receive(next));
            now += TimeSpan.FromSeconds(11).Ticks; mono += TimeSpan.FromSeconds(11).Ticks;
            next.LastHeartbeatUtcTicks = now; next.ExpiresUtcTicks = now + TimeSpan.FromSeconds(10).Ticks;
            Reject(() => fence.Receive(next)); // Expiry alone is not proof hardware has retired.
            fence.RetireAfterSafetyHandoff(); fence.Receive(next);
            Assert(!fence.IsAuthorized(next.IncidentId, next.OwnerId), "new owner bypassed preparation");
            Reject(() => fence.Receive(lease)); fence.RequireForCommand(Command(next));
            Assert(fence.IsAuthorized(next.IncidentId, next.OwnerId), "new owner could not prepare after release");
        }

        private static PressureMaintenanceHeartbeatRequest Request(PressureMaintenanceLease lease) => new PressureMaintenanceHeartbeatRequest
        {
            RequestId = RecoveryProtocolV7.NewId(), ChallengeNonce = RecoveryProtocolV7.NewId(),
            Heartbeat = new PressureMaintenanceHeartbeat { SessionId = lease.SessionId, RunId = lease.RunId, RunEpoch = lease.RunEpoch,
                EngineInstanceId = lease.EngineInstanceId, IncidentId = lease.IncidentId, OwnerId = lease.OwnerId, Generation = lease.Generation,
                UiProcessId = lease.UiProcessId, UiProcessStartUtcTicks = lease.UiProcessStartUtcTicks, Sequence = 1, IssuedUtcTicks = DateTime.UtcNow.Ticks }
        };

        private static void HeartbeatAuthentication()
        {
            var lease = Lease(); var pipeName = "MTTFTest.MaintenanceHeartbeat.Test." + RecoveryProtocolV7.NewId(); var calls = 0;
            using (var stop = new CancellationTokenSource())
            using (var process = Process.GetCurrentProcess())
            {
                var server = new PressureMaintenanceHeartbeatServer((pid, started, session) =>
                    pid == process.Id && started == process.StartTime.ToUniversalTime().Ticks && session == lease.SessionId,
                    heartbeat =>
                    {
                        Interlocked.Increment(ref calls); if (!heartbeat.Binds(lease)) throw new InvalidDataException("ForeignHeartbeat");
                        var reply = lease.Clone(); reply.Revision++; reply.LastHeartbeatSequence = heartbeat.Sequence;
                        reply.LastHeartbeatUtcTicks = heartbeat.IssuedUtcTicks; reply.ExpiresUtcTicks = heartbeat.IssuedUtcTicks + TimeSpan.FromSeconds(10).Ticks;
                        return reply;
                    }, pipeName);
                var running = Task.Run(() => server.RunAsync(stop.Token));
                try
                {
                    for (var mode = 0; mode < 6; mode++)
                    {
                        var request = Request(lease);
                        if (mode == 1) request.Heartbeat.UiProcessId++;
                        if (mode == 2) request.Heartbeat.UiProcessStartUtcTicks++;
                        if (mode == 3) request.Heartbeat.SessionId = RecoveryProtocolV7.NewId();
                        if (mode == 4) request.Heartbeat.OwnerId = RecoveryProtocolV7.NewId();
                        if (mode == 5) request.Heartbeat.Generation++;
                        var response = PressureMaintenanceTransport.RenewAsync(request, peer =>
                            Assert(PipePeerIdentity.ServerProcessId(peer) == process.Id, "server PID mismatch"), CancellationToken.None, pipeName).GetAwaiter().GetResult();
                        Assert(response.Accepted == (mode == 0), "heartbeat peer accepted/rejected incorrectly:" + mode);
                    }
                    Assert(calls == 3, "unauthenticated peer reached kernel");
                }
                finally { stop.Cancel(); Assert(running.Wait(4000), "heartbeat server did not stop"); }
            }
        }

        private static void HeartbeatBadFrames()
        {
            var lease = Lease(); var pipeName = "MTTFTest.MaintenanceFrames.Test." + RecoveryProtocolV7.NewId(); var calls = 0;
            using (var stop = new CancellationTokenSource())
            {
                var server = new PressureMaintenanceHeartbeatServer((p, t, s) => true, heartbeat => { calls++; return lease; }, pipeName);
                var running = Task.Run(() => server.RunAsync(stop.Token));
                try
                {
                    foreach (var size in new[] { -1, PressureMaintenanceTransport.MaximumBytes + 1, 100 })
                    using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                    {
                        client.Connect(3000); var bytes = BitConverter.GetBytes(size); client.Write(bytes, 0, bytes.Length); client.Flush();
                        if (size == 100) client.WriteByte(123); // Disconnect in the middle of a frame.
                    }
                    using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                    {
                        client.Connect(3000); var read = client.ReadAsync(new byte[1], 0, 1);
                        Assert(read.Wait(4000) && read.Result == 0, "silent peer occupied heartbeat endpoint indefinitely");
                    }
                    Assert(calls == 0, "malformed heartbeat invoked kernel");
                }
                finally { stop.Cancel(); Assert(running.Wait(4000), "malformed frame server did not stop"); }
            }
        }

        private static void HeartbeatClientBinding()
        {
            foreach (var mode in new[] { 0, 1, 2, 3 })
            {
                var pipeName = "MTTFTest.MaintenanceReply.Test." + RecoveryProtocolV7.NewId(); var lease = Lease(); var request = Request(lease);
                using (var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                {
                    var server = Task.Run(async () =>
                    {
                        await pipe.WaitForConnectionAsync();
                        await PressureMaintenanceTransport.ReadFrameAsync(pipe, CancellationToken.None);
                        if (mode == 3) { await Task.Delay(2500); return; }
                        var response = new PressureMaintenanceHeartbeatResponse { RequestId = request.RequestId, ChallengeNonce = request.ChallengeNonce,
                            Accepted = true, Lease = lease.Clone() };
                        response.Lease.LastHeartbeatSequence = request.Heartbeat.Sequence; response.Lease.LastHeartbeatUtcTicks = request.Heartbeat.IssuedUtcTicks;
                        if (mode == 0) response.RequestId = RecoveryProtocolV7.NewId();
                        if (mode == 1) response.ChallengeNonce = RecoveryProtocolV7.NewId();
                        if (mode == 2) response.Lease.RunEpoch++;
                        await PressureMaintenanceTransport.WriteFrameAsync(pipe, response, CancellationToken.None);
                    });
                    Reject(() => PressureMaintenanceTransport.RenewAsync(request, peer => { }, CancellationToken.None, pipeName).GetAwaiter().GetResult());
                    Assert(server.Wait(4000), "reply test peer did not stop");
                }
            }
        }

        private static void PublicationBinding()
        {
            foreach (var mode in new[] { 0, 1, 2 })
            {
                var pipeName = "MTTFTest.MaintenancePublish.Test." + RecoveryProtocolV7.NewId(); var lease = Lease(); var wrote = false;
                using (var process = Process.GetCurrentProcess())
                using (var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                {
                    var server = Task.Run(async () =>
                    {
                        await pipe.WaitForConnectionAsync();
                        if (mode == 1) { wrote = pipe.ReadByte() != -1; return; }
                        var request = PressureMaintenanceTransport.Decode<EngineHostRequest>(await PressureMaintenanceTransport.ReadFrameAsync(pipe, CancellationToken.None));
                        wrote = true;
                        var reply = lease.Clone(); if (mode == 2) reply.Generation++;
                        await PressureMaintenanceTransport.WriteFrameAsync(pipe, new EngineHostResponse { RequestId = request.RequestId, Accepted = true, MaintenanceLease = reply }, CancellationToken.None);
                    });
                    Action send = () => EngineHostPipeClient.SendMaintenanceLeaseAsync(lease,
                        (pid, started) => mode != 1 && pid == process.Id && started == process.StartTime.ToUniversalTime().Ticks,
                        CancellationToken.None, pipeName).GetAwaiter().GetResult();
                    if (mode == 0) send(); else Reject(send);
                    Assert(server.Wait(4000) && wrote == (mode != 1), "lease sent to unapproved EngineHost");
                }
            }
        }

        private static void Reject(Action action)
        {
            try { action(); } catch (InvalidDataException) { return; } catch (InvalidOperationException) { return; } catch (TimeoutException) { return; }
            throw new Exception("expected maintenance rejection");
        }
        private static void Assert(bool value, string detail) { if (!value) throw new Exception(detail); }
        private static void Check(string name, Action action) { action(); Console.WriteLine("PASS " + name); }
    }
}
