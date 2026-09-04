using System;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.EngineHost;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.EngineHostIntegrationTests
{
    internal static class PressureMaintenanceExecutorTests
    {
        internal static int RunAll()
        {
            Check("PressureExecutorPreparesOutputsAndStopsWithoutNewOwner", RoundTrip);
            Check("PressureExecutorRejectsInvalidCalibrationBeforeEnable", InvalidCalibration);
            Check("PressureExecutorRechecksLeaseAfterEveryEnergizingCall", RecheckCalls);
            Check("PressureExecutorTripsOnStaleNonfiniteAndOverpressureSamples", SampleTrips);
            Check("PressureExecutorLeaseExpiryStopsAndPublishesOneFact", LeaseExpiry);
            Check("PressureExecutorStopFencesBlockedOutputAndDoesNotBlockRenewal", BlockedOutput);
            Check("PressureExecutorCancellationAndOffFailuresCannotClaimSuccess", CancellationAndOff);
            Check("PressureExecutorHandoffRetiresOldOwnerButKeepsNewPrepareLease", HandoffLease);
            Check("PressureExecutorDisplayBindsLiveAuthorityAndNeverRevivesRetiredOutput", DisplayEvidence);
            return 9;
        }

        private sealed class Hardware : IPressureMaintenanceExecutorHardware
        {
            internal volatile bool Enabled, RejectEvaluation, RejectZeroPressureEvaluation, OffFails;
            internal string LastOffThreadName;
            internal int EnableCalls, WriteCalls, OffCalls, SampleMode;
            internal Action OnEnable, OnWrite;
            public double MaximumPressureBar => 120;
            public double ReleaseSafePressureBar => 5;
            public int SampleMaximumAgeMilliseconds => 100;
            public PressureMaintenanceWriteResult Evaluate(double value) => new PressureMaintenanceWriteResult
            { Succeeded = !RejectEvaluation && !(RejectZeroPressureEvaluation && value == 0) && value >= 0 && value <= 120, PressureBar = value, Voltage = value / 20 };
            public UiMeasurement ReadPressure() => new UiMeasurement { Valid = SampleMode != 1,
                Value = SampleMode == 3 ? 130 : SampleMode == 4 ? double.NaN : Enabled ? 30 : 0,
                CapturedUtcTicks = SampleMode == 2 ? DateTime.UtcNow.AddSeconds(-1).Ticks : DateTime.UtcNow.Ticks };
            public bool EnablePressure() { Interlocked.Increment(ref EnableCalls); Enabled = true; OnEnable?.Invoke(); return true; }
            public PressureMaintenanceWriteResult WritePressure(double value)
            { Interlocked.Increment(ref WriteCalls); OnWrite?.Invoke(); Enabled = true; return Evaluate(value); }
            public bool AllOff() { Volatile.Write(ref LastOffThreadName, Thread.CurrentThread.Name); Interlocked.Increment(ref OffCalls); if (OffFails) return false; Enabled = false; return true; }
        }

        private sealed class Fixture : IDisposable
        {
            internal readonly PressureMaintenanceLease Lease = PressureMaintenanceTransportTests.Lease();
            internal readonly Hardware Hardware = new Hardware();
            internal readonly EnginePressureMaintenanceLeaseFence Authority;
            internal readonly EnginePressureMaintenanceExecutor Executor;
            internal readonly ManualResetEventSlim Fault = new ManualResetEventSlim();
            internal long Now;
            internal int Faults;
            internal Fixture(bool zeroPressureNotRepresentable = false)
            {
                Hardware.RejectZeroPressureEvaluation = zeroPressureNotRepresentable;
                Now = Lease.CreatedUtcTicks;
                Authority = new EnginePressureMaintenanceLeaseFence(Lease.SessionId, Lease.RunId, Lease.RunEpoch, Lease.EngineInstanceId,
                    () => Interlocked.Read(ref Now));
                Authority.Receive(Lease);
                Executor = new EnginePressureMaintenanceExecutor(Authority, Hardware, PressureMaintenanceTransportTests.Command(Lease),
                    reason => { Interlocked.Increment(ref Faults); Fault.Set(); });
                Executor.PrepareAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            public void Dispose() { Hardware.OffFails = false; Executor.Dispose(); Fault.Dispose(); }
        }

        internal static RecoveryCommand Operation(PressureMaintenanceLease lease, RecoveryCommandKind kind, long sequence)
        {
            var templateLease = lease.Clone(); templateLease.Revoked = false;
            var command = PressureMaintenanceTransportTests.Command(templateLease, kind == RecoveryCommandKind.PreparePressureMaintenance ||
                kind == RecoveryCommandKind.DisableOutputs && !lease.Revoked);
            command.PressureMaintenance = lease.Clone();
            command.Kind = kind; command.CommandSequence = sequence;
            if (kind == RecoveryCommandKind.StopMaintenanceOutput || kind == RecoveryCommandKind.DisableOutputs && lease.Revoked)
            {
                command.OperatorTransaction.Kind = kind == RecoveryCommandKind.StopMaintenanceOutput
                    ? OperatorCommandKind.StopMaintenanceOutput : OperatorCommandKind.EndPressureMaintenance;
                command.OperatorTransaction.PressureMaintenance.PressureBar = 0;
                command.OperatorTransaction.PayloadSha256 = command.OperatorTransaction.PressureMaintenance.ComputeSha256();
            }
            command.IdempotencyKey = command.ExpectedIdempotencyKey();
            Assert(command.IsStructurallyValid(), "bad fixture operation"); return command;
        }

        private static void RoundTrip()
        {
            using (var f = new Fixture(zeroPressureNotRepresentable: true))
            {
                Assert(!f.Hardware.Enabled, "prepare energized");
                var output = f.Executor.OutputAsync(Operation(f.Lease, RecoveryCommandKind.SetMaintenancePressure, 2), CancellationToken.None).GetAwaiter().GetResult();
                Assert(output.OutputActive && output.CommandPressureBar == 30 && output.Voltage == 1.5 && f.Hardware.Enabled, "output not executed");
                var stopped = f.Executor.StopOutputAsync(Operation(f.Lease, RecoveryCommandKind.StopMaintenanceOutput, 3), CancellationToken.None).GetAwaiter().GetResult();
                Assert(!stopped.OutputActive && !f.Hardware.Enabled && f.Authority.IsAuthorized(f.Lease.IncidentId, f.Lease.OwnerId), "stop output ended session");
                Assert(f.Executor.StopForHandoffAsync().GetAwaiter().GetResult() && !f.Authority.IsAuthorized(f.Lease.IncidentId, f.Lease.OwnerId), "handoff reused authority");
            }
        }
        private static void InvalidCalibration()
        {
            using (var f = new Fixture())
            {
                f.Hardware.RejectEvaluation = true;
                Reject(() => f.Executor.OutputAsync(Operation(f.Lease, RecoveryCommandKind.SetMaintenancePressure, 2), CancellationToken.None).GetAwaiter().GetResult());
                Assert(f.Hardware.EnableCalls == 0 && f.Hardware.WriteCalls == 0 && !f.Hardware.Enabled, "invalid calibration reached hardware");
            }
        }
        private static void DisplayEvidence()
        {
            using (var f = new Fixture())
            {
                var display = f.Executor.CaptureDisplay();
                Assert(display.IsStructurallyValid() && display.Binds(f.Lease) && display.Prepared && display.AuthorityValid &&
                    !display.OutputActive && !display.MayBeEnergized && display.Pressure.IsUsable(DateTime.UtcNow.Ticks), "prepared display incomplete");
                var output = Operation(f.Lease, RecoveryCommandKind.SetMaintenancePressure, 2);
                f.Executor.OutputAsync(output, CancellationToken.None).GetAwaiter().GetResult(); display = f.Executor.CaptureDisplay();
                var generation = display.OutputGeneration;
                Assert(display.Binds(f.Lease) && display.OutputActive && display.MayBeEnergized && display.OutputCommandId == output.CommandId &&
                    display.CommandPressureBar == 30 && display.Voltage == 1.5 && display.PressureSampleMaximumAgeMilliseconds == 100,
                    "output display lost command or sample age binding");
                f.Executor.StopOutputAsync(Operation(f.Lease, RecoveryCommandKind.StopMaintenanceOutput, 3), CancellationToken.None).GetAwaiter().GetResult();
                display = f.Executor.CaptureDisplay();
                Assert(!display.OutputActive && !display.MayBeEnergized && display.OutputCommandId == "" && display.OutputGeneration > generation,
                    "stopped display reused output receipt");
                f.Authority.Invalidate(); f.Hardware.SampleMode = 1; display = f.Executor.CaptureDisplay();
                Assert(display.IsStructurallyValid() && !display.Prepared && !display.AuthorityValid && !display.OutputActive &&
                    !display.Pressure.IsUsable(DateTime.UtcNow.Ticks), "invalid lease/sample displayed as healthy");
            }
        }
        private static void RecheckCalls()
        {
            foreach (var afterAo in new[] { false, true }) using (var f = new Fixture())
            {
                if (afterAo) f.Hardware.OnWrite = f.Authority.Invalidate; else f.Hardware.OnEnable = f.Authority.Invalidate;
                Reject(() => f.Executor.OutputAsync(Operation(f.Lease, RecoveryCommandKind.SetMaintenancePressure, 2), CancellationToken.None).GetAwaiter().GetResult());
                Assert(!f.Hardware.Enabled && !f.Executor.MayBeEnergized && f.Hardware.WriteCalls == (afterAo ? 1 : 0), "lease lost during SDK call was ignored");
            }
        }
        private static void SampleTrips()
        {
            foreach (var mode in new[] { 1, 2, 3, 4 }) using (var f = new Fixture())
            {
                f.Executor.OutputAsync(Operation(f.Lease, RecoveryCommandKind.SetMaintenancePressure, 2), CancellationToken.None).GetAwaiter().GetResult();
                Volatile.Write(ref f.Hardware.SampleMode, mode);
                Assert(f.Fault.Wait(2000) && SpinWait.SpinUntil(() => !f.Hardware.Enabled, 2000), "unsafe pressure did not stop");
                Assert(!f.Authority.IsAuthorized(f.Lease.IncidentId, f.Lease.OwnerId), "fault retained output authority");
                Thread.Sleep(150); Assert(f.Faults == 1, "watchdog fact storm");
            }
        }
        private static void LeaseExpiry()
        {
            using (var f = new Fixture())
            {
                f.Executor.OutputAsync(Operation(f.Lease, RecoveryCommandKind.SetMaintenancePressure, 2), CancellationToken.None).GetAwaiter().GetResult();
                Interlocked.Exchange(ref f.Now, f.Lease.ExpiresUtcTicks + 1);
                Assert(f.Fault.Wait(2000) && SpinWait.SpinUntil(() => !f.Hardware.Enabled, 2000), "expired output did not turn off");
                Assert(Volatile.Read(ref f.Hardware.LastOffThreadName) == "EPB pressure maintenance emergency off", "emergency OFF depended on the normal thread pool");
                var late = f.Lease.Clone(); late.Revision++; late.LastHeartbeatSequence++; late.LastHeartbeatUtcTicks = f.Now;
                late.ExpiresUtcTicks = f.Now + TimeSpan.FromSeconds(10).Ticks; Reject(() => f.Authority.Receive(late));
                Thread.Sleep(150); Assert(f.Faults == 1, "expiry repeated fault");
            }
        }
        private static void BlockedOutput()
        {
            using (var f = new Fixture()) using (var entered = new ManualResetEventSlim()) using (var release = new ManualResetEventSlim())
            {
                f.Hardware.OnWrite = () => { entered.Set(); if (!release.Wait(3000)) throw new TimeoutException("fixture write stalled"); };
                var output = f.Executor.OutputAsync(Operation(f.Lease, RecoveryCommandKind.SetMaintenancePressure, 2), CancellationToken.None);
                try
                {
                    Assert(entered.Wait(2000), "output did not reach fake SDK");
                    var renewed = f.Lease.Clone(); renewed.Revision++; renewed.LastHeartbeatSequence++;
                    Interlocked.Add(ref f.Now, TimeSpan.FromSeconds(1).Ticks); renewed.LastHeartbeatUtcTicks = f.Now;
                    renewed.ExpiresUtcTicks = f.Now + TimeSpan.FromSeconds(10).Ticks;
                    Assert(f.Authority.Receive(renewed).Revision == 2, "blocked SDK blocked heartbeat");
                    var stop = f.Executor.StopOutputAsync(Operation(renewed, RecoveryCommandKind.StopMaintenanceOutput, 3), CancellationToken.None);
                    Assert(stop.Wait(2000) && !stop.Result.OutputActive && !f.Hardware.Enabled, "stop waited for pending AO before first OFF");
                }
                finally { release.Set(); }
                Reject(() => output.GetAwaiter().GetResult());
                Assert(!f.Hardware.Enabled && f.Authority.IsAuthorized(f.Lease.IncidentId, f.Lease.OwnerId), "late output resumed or normal stop revoked session");
            }
        }
        private static void CancellationAndOff()
        {
            using (var f = new Fixture()) using (var cancel = new CancellationTokenSource())
            {
                f.Hardware.OnEnable = cancel.Cancel;
                Reject(() => f.Executor.OutputAsync(Operation(f.Lease, RecoveryCommandKind.SetMaintenancePressure, 2), cancel.Token).GetAwaiter().GetResult());
                Assert(!f.Hardware.Enabled && f.Hardware.WriteCalls == 0, "cancelled command wrote AO");
                f.Hardware.OffFails = true;
                Reject(() => f.Executor.StopOutputAsync(Operation(f.Lease, RecoveryCommandKind.StopMaintenanceOutput, 3), CancellationToken.None).GetAwaiter().GetResult());
                Assert(f.Executor.MayBeEnergized && f.Fault.Wait(2000), "failed OFF claimed output closed");
            }
        }
        private static void HandoffLease()
        {
            var lease = PressureMaintenanceTransportTests.Lease(); var authority = new EnginePressureMaintenanceLeaseFence(lease.SessionId, lease.RunId, 1, lease.EngineInstanceId);
            authority.Receive(lease);
            authority.CompleteSafetyHandoff(Operation(lease, RecoveryCommandKind.DisableOutputs, 1));
            authority.RequireForCommand(Operation(lease, RecoveryCommandKind.PreparePressureMaintenance, 2));
            var revoked = lease.Clone(); revoked.Revoked = true; revoked.Revision++;
            authority.Receive(revoked); authority.CompleteSafetyHandoff(Operation(revoked, RecoveryCommandKind.DisableOutputs, 3));
            var next = PressureMaintenanceTransportTests.Lease(lease.SessionId, lease.RunId, lease.EngineInstanceId);
            authority.Receive(next); authority.CompleteSafetyHandoff(Operation(next, RecoveryCommandKind.DisableOutputs, 4));
            authority.RequireForCommand(Operation(next, RecoveryCommandKind.PreparePressureMaintenance, 5));
            Assert(authority.IsAuthorized(next.IncidentId, next.OwnerId), "fresh owner could not prepare immediately after full stop");
            Reject(() => authority.Receive(lease));
        }
        private static void Reject(Action action)
        {
            try { action(); } catch (InvalidOperationException) { return; } catch (System.IO.InvalidDataException) { return; }
            catch (OperationCanceledException) { return; }
            throw new Exception("expected executor rejection");
        }
        private static void Assert(bool value, string detail) { if (!value) throw new Exception(detail); }
        private static void Check(string name, Action action) { action(); Console.WriteLine("PASS " + name); }
    }
}
