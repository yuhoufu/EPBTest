using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using MTTFTest.EngineHost;
using MTTFTest.Watchdog;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.EngineHostIntegrationTests
{
    internal static class EngineSafetyHandoffTests
    {
        internal static int RunAll()
        {
            RevocationAloneDoesNotReleaseResources();
            EveryIndependentFactIsRequired();
            ReceiptBindsEveryIdentityDimension();
            NativeEvidenceDoesNotInventDataOrPhysicalProof();
            ReleaseWaitsForFinalOffAfterOldExecutor();
            CancelledJoinNeverCallsRelease();
            Console.WriteLine("PASS 6 independent hardware handoff tests (isolated, no physical hardware)");
            return 6;
        }

        private static void RevocationAloneDoesNotReleaseResources()
        {
            var command = Command(1);
            var receipt = Receipt(command);
            receipt.HardwareHandoff = null;
            Reject(() => V3SafetyHandoffBoundary.Capture(command, receipt));
        }

        private static void EveryIndependentFactIsRequired()
        {
            var command = Command(1);
            foreach (var remove in new Action<RecoveryCommandReceipt>[]
            {
                r => r.ExecutionAuthorizationRevoked = false,
                r => r.HardwareHandoff.LogicalQuiescent = false,
                r => r.HardwareHandoff.NativeResourcesReleased = false,
                r => r.HardwareHandoff.CallbacksIsolated = false,
                r => r.HardwareHandoff.ExecutorQuiescent = false
            })
            {
                var receipt = Receipt(command); remove(receipt);
                Reject(() => V3SafetyHandoffBoundary.Capture(command, receipt));
            }
        }

        private static void ReceiptBindsEveryIdentityDimension()
        {
            var command = Command(1);
            var receipt = Receipt(command);
            var json = new JavaScriptSerializer();
            var roundTrip = json.Deserialize<RecoveryCommandReceipt>(json.Serialize(receipt));
            Assert(roundTrip.HardwareHandoff.Matches(command, receipt.HardwareHandoff.EngineInstanceId), "handoff serialization changed binding");
            Assert(!roundTrip.HardwareHandoff.Matches(command, RecoveryProtocolV7.NewId()), "different EngineHost accepted");
            foreach (var change in new Action<EngineHardwareHandoff>[]
            {
                h => h.ContractVersion++, h => h.CommandId = RecoveryProtocolV7.NewId(),
                h => h.OwnerId = RecoveryProtocolV7.NewId(), h => h.IdempotencyKey = "other",
                h => h.Identity.SessionId = RecoveryProtocolV7.NewId(), h => h.Identity.RunId = RecoveryProtocolV7.NewId(),
                h => h.Identity.RunEpoch++, h => h.Identity.IncidentId = RecoveryProtocolV7.NewId(),
                h => h.Identity.ResourceScope = "Channel:4", h => h.Identity.Generation++, h => h.Identity.Revision++,
                h => h.CapturedUtcTicks = DateTime.UtcNow.AddMinutes(1).Ticks
            })
            {
                var changed = json.Deserialize<RecoveryCommandReceipt>(json.Serialize(receipt));
                change(changed.HardwareHandoff);
                Reject(() => V3SafetyHandoffBoundary.Capture(command, changed));
            }
        }

        private static void NativeEvidenceDoesNotInventDataOrPhysicalProof()
        {
            var command = Command(1);
            var receipt = Receipt(command);
            receipt.DataBoundaryClosed = false; receipt.OutputsOff = false; receipt.PressureSafe = false;
            var boundary = V3SafetyHandoffBoundary.Capture(command, receipt);
            Assert(boundary.OldExecutionIsolated && !boundary.DataBoundaryClosed, "native closure manufactured durable data");
            // Native evidence grants only resource transfer; SafetyAgent must still
            // independently command OFF and acquire physical read-back.
            receipt.DataBoundaryClosed = true;
            var absent = V3SafetyHandoffBoundary.Capture(command, receipt, engineProcessExitConfirmed: true);
            Assert(absent.OldExecutionIsolated && !absent.DataBoundaryClosed, "OS exit promoted unsealed circle to closed boundary");
        }

        private static void ReleaseWaitsForFinalOffAfterOldExecutor()
        {
            var gate = new EngineRecoveryExecutionGate();
            using (var old = gate.Begin(Command(1, RecoveryCommandKind.RebuildResource), CancellationToken.None))
            using (var stop = gate.Begin(Command(2), CancellationToken.None))
            {
                var off = 0; var released = 0;
                var task = stop.ExecuteWithQuiescentBoundaryAsync(_ => Task.FromResult(++off), CancellationToken.None,
                    (count, _) =>
                    {
                        Assert(old.Completed.IsCompleted && count == 2, "native release preceded final OFF");
                        released++; return Task.CompletedTask;
                    });
                Assert(off == 1 && released == 0 && !task.IsCompleted, "release ran while old executor still owned hardware");
                old.Dispose();
                Assert(task.Wait(3000) && released == 1 && task.Result == 2, "missing exactly-once native release");
            }
        }

        private static void CancelledJoinNeverCallsRelease()
        {
            var gate = new EngineRecoveryExecutionGate();
            using (var old = gate.Begin(Command(1, RecoveryCommandKind.RebuildResource), CancellationToken.None))
            using (var stop = gate.Begin(Command(2), CancellationToken.None))
            using (var deadline = new CancellationTokenSource())
            {
                var released = false;
                var task = stop.ExecuteWithQuiescentBoundaryAsync(_ => Task.FromResult(1), deadline.Token,
                    (_, cancellation) => { released = true; return Task.CompletedTask; });
                deadline.Cancel();
                try { task.GetAwaiter().GetResult(); throw new Exception("cancelled join succeeded"); }
                catch (OperationCanceledException) { }
                Assert(!released && !old.Completed.IsCompleted, "timeout dropped actual resource ownership");
            }
        }

        private static RecoveryCommand Command(long sequence, RecoveryCommandKind kind = RecoveryCommandKind.DisableOutputs)
        {
            var identity = new RecoveryIdentity { SessionId = RecoveryProtocolV7.NewId(), RunId = RecoveryProtocolV7.NewId(),
                RunEpoch = 1, IncidentId = RecoveryProtocolV7.NewId(), ResourceScope = "System", Generation = 1, Revision = sequence };
            return new RecoveryCommand { Identity = identity, CommandId = RecoveryProtocolV7.NewId(), OwnerId = RecoveryProtocolV7.NewId(),
                CommandSequence = sequence, Kind = kind, TargetResource = "System", DeadlineUtcTicks = DateTime.UtcNow.AddMinutes(1).Ticks,
                IdempotencyKey = RecoveryProtocolV7.ComputeIdempotencyKey(identity, kind, sequence) };
        }

        private static RecoveryCommandReceipt Receipt(RecoveryCommand command) => new RecoveryCommandReceipt
        {
            CommandId = command.CommandId, IdempotencyKey = command.IdempotencyKey, Succeeded = true,
            ExecutionAuthorizationRevoked = true, DataBoundaryClosed = true, CompletedUtcTicks = DateTime.UtcNow.Ticks,
            HardwareHandoff = new EngineHardwareHandoff
            {
                Identity = command.Identity.Clone(), CommandId = command.CommandId, IdempotencyKey = command.IdempotencyKey,
                OwnerId = command.OwnerId, EngineInstanceId = RecoveryProtocolV7.NewId(),
                LogicalQuiescent = true, NativeResourcesReleased = true, CallbacksIsolated = true, ExecutorQuiescent = true,
                CapturedUtcTicks = DateTime.UtcNow.Ticks
            }
        };

        private static void Reject(Action action)
        {
            try { action(); }
            catch (InvalidDataException) { return; }
            catch (InvalidOperationException) { return; }
            throw new Exception("incomplete or stale hardware handoff accepted");
        }

        private static void Assert(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    }
}
