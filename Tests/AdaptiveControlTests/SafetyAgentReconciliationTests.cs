using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;
using MTTFTest.RecoveryControl;
using MTTFTest.Watchdog;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    internal static class SafetyAgentReconciliationTests
    {
        internal static int RunAll()
        {
            using (var f = new Fixture())
            {
                f.Write("Started"); f.Attach();
                Check(f.AttachedProcessId == f.Identity.ProcessId, "persisted exact process is reattached");
                f.Attach();
                Check(f.AttachedProcessId == f.Identity.ProcessId, "repeat attachment retains one process");
                f.Write("Started", boot: "invalid");
                f.Reject("ProcessUnproven");
            }
            using (var f = new Fixture())
            {
                f.Write("LaunchIntent"); f.Reject("OutcomeUnproven");
                Check(f.AttachedProcessId == 0, "unconfirmed launch is not treated as a missing process");
                File.WriteAllText(f.Path, "{broken-json");
                f.Reject(null);
                f.Write("Started");
                f.Request.AuthorityId = Guid.NewGuid().ToString("N");
                f.Reject("BindingMismatch");
            }
            using (var f = new Fixture())
            {
                f.Write("LaunchIntent");
                f.RetainCreatedProcess();
                f.Attach();
                var record = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(f.Path))
                    as System.Collections.Generic.Dictionary<string, object>;
                Check((string)record["State"] == "Started" && Convert.ToInt32(record["ProcessId"]) == f.Identity.ProcessId,
                    "retained creation handle repairs lost Started write without starting again");
            }
            using (var f = new Fixture())
            {
                f.Write("Started", startTicks: f.Identity.StartUtcTicks - TimeSpan.TicksPerSecond);
                f.Attach();
                Check(f.AttachedProcessId == 0, "different process at a reused PID is never attached or terminated");
                f.Write("Started", boot: null); f.Attach();
                Check(f.AttachedProcessId == f.Identity.ProcessId, "legacy Started identity can still reattach exactly");
            }
            using (var f = new Fixture())
            {
                f.Write("Started");
                f.RejectOther("OtherAgentNotRetired");
                f.Write("LaunchIntent");
                f.RejectOther("OtherLaunchOutcomeUnproven");
                f.Write("Started", boot: "invalid");
                f.RejectOther("OtherAgentNotRetired");
                f.Write("Started", startTicks: f.Identity.StartUtcTicks - TimeSpan.TicksPerSecond);
                f.CheckOthers();
                Check(!Process.GetCurrentProcess().HasExited, "retirement checks do not terminate the unrelated current process");
            }
            SupervisedChildLifecycle();
            CreationFencePreventsStart();
            HardwareCreationGateExcludesConcurrentActor();
            ReconciliationDoesNotWaitBehindHardwareCreator();
            LegacyCreationRespectsTakeoverAndStop();
            Console.WriteLine("PASS SafetyAgent 对账 10/10 精确接续、创建互斥、锁顺序、人工停止优先及真实隔离子进程生命周期");
            return 10;
        }

        private static void ReconciliationDoesNotWaitBehindHardwareCreator()
        {
            using (var f = new Fixture())
            using (var entered = new ManualResetEventSlim())
            {
                var actorAvailable = false;
                var reachedCreationFence = false;
                Exception failure = null;
                var creator = new Thread(() =>
                {
                    entered.Set();
                    try { f.Register(() => { reachedCreationFence = true; throw new InvalidOperationException("IsolatedCreationDenied"); }); }
                    catch (InvalidOperationException ex) when (ex.Message == "IsolatedCreationDenied") { }
                    catch (Exception ex) { failure = ex; }
                });
                using (SupervisorHardwareLaunchGate.Enter(f.Request.WorkingDirectory))
                {
                    creator.Start();
                    Check(entered.Wait(1000), "creator entered isolated call");
                    Check(SpinWait.SpinUntil(() => (creator.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, 1000),
                        "creator is waiting for held hardware gate");
                    actorAvailable = Monitor.TryEnter(f.ActorGate, 500);
                    if (actorAvailable) Monitor.Exit(f.ActorGate);
                }
                Check(creator.Join(10000), "creator retires after hardware gate release");
                if (failure != null) throw failure;
                Check(actorAvailable && reachedCreationFence && !File.Exists(f.Path),
                    "hardware-gate owner can reconcile actor while creator waits, without deadlock or process creation");
            }
        }

        private static void HardwareCreationGateExcludesConcurrentActor()
        {
            using (var f = new Fixture())
            {
                var denied = false;
                Exception unexpected = null;
                using (SupervisorHardwareLaunchGate.Enter(f.Request.WorkingDirectory))
                {
                    var other = new Thread(() =>
                    {
                        try { using (SupervisorHardwareLaunchGate.Enter(f.Request.WorkingDirectory)) { } }
                        catch (IOException ex) when (ex.Message == "SupervisorSafetyLaunchBusy") { denied = true; }
                        catch (Exception ex) { unexpected = ex; }
                    });
                    other.Start();
                    Check(other.Join(10000), "contending hardware creator must return within budget");
                    if (unexpected != null) throw unexpected;
                    Check(denied, "a second actor must not enter while launch reconciliation holds the gate");
                }
                using (SupervisorHardwareLaunchGate.Enter(f.Request.WorkingDirectory)) { }
            }
        }

        private static void LegacyCreationRespectsTakeoverAndStop()
        {
            var state = new RecoveryControlState { Intent = new RecoveryRunIntent { DesiredState = RecoveryDesiredState.Run,
                    MainProcess = new RecoveryProcessIdentity { ProcessId = 1, StartUtcTicks = 2 } },
                Transaction = new RecoveryTakeoverTransaction { Stage = RecoveryStage.Launch } };
            var receipt = new WatchdogSafetyHandoffReceipt { OldProcessId = 1, OldProcessStartUtcTicks = 2,
                RelaunchDisposition = WatchdogRelaunchDisposition.PreserveApprovedPermit };
            Action deny = () =>
            {
                try { SupervisorSafetyTakeoverPolicy.AssertLegacyCreationAllowed(state, receipt); }
                catch (InvalidOperationException ex) when (ex.Message.Contains("TakeoverFenced")) { return; }
                throw new Exception("legacy automatic safety creation must be fenced");
            };
            deny();
            receipt.RelaunchDisposition = WatchdogRelaunchDisposition.Forbidden;
            deny();
            SupervisorSafetyTakeoverPolicy.AssertLegacyCreationAllowed(state, receipt, true);
            state.Intent.DesiredState = RecoveryDesiredState.Stopped;
            SupervisorSafetyTakeoverPolicy.AssertLegacyCreationAllowed(state, receipt);
            receipt.RelaunchDisposition = WatchdogRelaunchDisposition.PreserveApprovedPermit;
            deny();
            state.Transaction.OwnershipReleased = true;
            SupervisorSafetyTakeoverPolicy.AssertLegacyCreationAllowed(state, receipt);
            state.Intent.DesiredState = RecoveryDesiredState.Run;
            state.Intent.MainProcess.ProcessId = 3;
            try { SupervisorSafetyTakeoverPolicy.AssertLegacyCreationAllowed(state, receipt); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("RunSuperseded")) { return; }
            throw new Exception("completed handback must not admit the previous main's safety action");
        }

        private static void SupervisedChildLifecycle()
        {
            using (var f = new Fixture())
            {
                // No production SafetyAgent is executed.
                var eventName = "Local\\EPB-Safety-Run-" + Guid.NewGuid().ToString("N");
                using (var childRelease = new EventWaitHandle(false, EventResetMode.ManualReset, eventName))
                {
                    f.SetChildArguments(eventName);
                    Process child = null;
                    try
                    {
                        var creates = 0;
                        var first = f.Register(() => creates++);
                        child = Process.GetProcessById(first.ProcessId);
                        var second = f.Register(() => creates++);
                        Check(first.ProcessId == second.ProcessId && first.StartUtcTicks == second.StartUtcTicks && creates == 1,
                            "second request joins one real child without another creation");
                        Check(f.Status(out var alive, out var hasRecord) == ProcessObservation.ExactAlive && hasRecord &&
                            alive.ProcessId == first.ProcessId, "status returns bound live identity");
                        childRelease.Set();
                        Check(child.WaitForExit(5000), "isolated child exits after explicit release");
                        Check(f.Status(out var exited, out hasRecord) == ProcessObservation.Exited && hasRecord &&
                            exited.ProcessId == first.ProcessId, "status retains exact exited identity for SafeStop completion");
                        Check(File.ReadAllLines(f.ChildLog).Length == 1, "only one child actually ran");
                    }
                    finally
                    {
                        childRelease.Set();
                        if (child != null) { child.WaitForExit(5000); child.Dispose(); }
                    }
                }
            }
        }

        private static void CreationFencePreventsStart()
        {
            using (var f = new Fixture())
            {
                // If the fence regresses, still run only the bounded child mode,
                // never recursively execute the ordinary test suite.
                f.SetChildArguments("Local\\EPB-Absent-Child-Event-" + Guid.NewGuid().ToString("N"));
                try { f.Register(() => { throw new InvalidOperationException("operator-stopped-before-create"); }); }
                catch (InvalidOperationException ex) when (ex.Message == "operator-stopped-before-create")
                {
                    Check(!File.Exists(f.Path), "last admission rejection leaves no launch intent or process");
                    Check(f.Status(out var identity, out var hasRecord) == ProcessObservation.Unknown && !hasRecord && identity == null,
                        "no created identity is invented after denial");
                    return;
                }
                throw new Exception("Final creation admission was bypassed");
            }
        }

        private static void Check(bool value, string message)
        {
            if (!value) throw new Exception("SafetyAgent reconciliation: " + message);
        }

        private sealed class Fixture : IDisposable
        {
            private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "EPB-SafetyAttach-" + Guid.NewGuid().ToString("N"));
            private readonly Type _type = typeof(SupervisorServiceRuntime).GetNestedType("SupervisorOwnedSafetyAgent", BindingFlags.NonPublic);
            private readonly object _owned;
            private readonly string _key = Guid.NewGuid().ToString("N");
            internal readonly RecoveryProcessIdentity Identity = RecoveryProcessProbe.Current();
            internal readonly SupervisorSafetyAgentLaunchRequest Request;
            internal readonly string Path;
            internal string ChildLog => System.IO.Path.Combine(_root, "child.log");
            internal object ActorGate => _type.GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_owned);

            internal Fixture()
            {
                Directory.CreateDirectory(_root);
                _owned = Activator.CreateInstance(_type, BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new object[] { _key }, null);
                Path = (string)_type.GetMethod("SafetyRecordPath", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { _root, _key });
                Request = new SupervisorSafetyAgentLaunchRequest
                {
                    SessionId = Guid.NewGuid().ToString("N"), PermitGeneration = 1, PermitId = Guid.NewGuid().ToString("N"),
                    HandoffId = Guid.NewGuid().ToString("N"), HandoffNonceSha256 = new string('a', 64),
                    ExecutablePath = Identity.ExecutablePath, ExecutableSha256 = SupervisorProtocol.ComputeSha256(Identity.ExecutablePath),
                    AuthorityId = Guid.NewGuid().ToString("N"), AuthorityReceiptRevision = 1,
                    AuthorityReceiptCanonicalSha256 = new string('b', 64),
                    Arguments = "--isolated-attachment-proof", ArgumentsSha256 = SupervisorProtocol.ComputeTextSha256("--isolated-attachment-proof"),
                    WorkingDirectory = _root
                };
            }

            internal int AttachedProcessId => ((Process)_type.GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(_owned))?.Id ?? 0;

            internal void RetainCreatedProcess()
            {
                _type.GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_owned, Process.GetCurrentProcess());
                _type.GetField("_processStartUtcTicks", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_owned, Identity.StartUtcTicks);
            }

            internal void Write(string state, string boot = "current", long? startTicks = null)
            {
                File.WriteAllText(Path, new JavaScriptSerializer().Serialize(new
                {
                    SchemaVersion = SupervisorProtocol.SchemaVersion, Key = _key, Request.SessionId,
                    Request.PermitGeneration, Request.PermitId, Request.HandoffId, Request.HandoffNonceSha256,
                    Request.ExecutablePath, Request.ExecutableSha256, Request.AuthorityId, Request.AuthorityReceiptRevision,
                    Request.AuthorityReceiptCanonicalSha256, BaseArgumentsSha256 = Request.ArgumentsSha256,
                    Request.Arguments, Request.ArgumentsSha256, State = state,
                    ProcessId = state == "Started" ? Identity.ProcessId : 0,
                    ProcessStartUtcTicks = state == "Started" ? startTicks ?? Identity.StartUtcTicks : 0,
                    ProcessBootId = boot == "current" ? Identity.BootId : boot, Revision = 1
                }));
            }

            internal void Attach()
            {
                try { _type.GetMethod("TryAttachPersisted", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(_owned, new object[] { Request, _root }); }
                catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
            }

            internal void Reject(string code)
            {
                try { Attach(); }
                catch (Exception ex) when (code == null || ex.Message.Contains(code)) { return; }
                throw new Exception("Expected SafetyAgent attachment rejection: " + code);
            }

            internal void CheckOthers()
            {
                try { _type.GetMethod("AssertOtherSafetyRecordsRetired", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { _root, "another-handoff" }); }
                catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
            }

            internal void RejectOther(string code)
            {
                try { CheckOthers(); }
                catch (Exception ex) when (ex.Message.Contains(code)) { return; }
                throw new Exception("Expected conflicting SafetyAgent rejection: " + code);
            }

            internal void SetChildArguments(string eventName)
            {
                Request.Arguments = "--safety-supervised-child \"" + ChildLog + "\" \"" + eventName + "\"";
                Request.ArgumentsSha256 = SupervisorProtocol.ComputeTextSha256(Request.Arguments);
            }

            internal RecoveryProcessIdentity Register(Action beforeCreate)
            {
                try
                {
                    var result = _type.GetMethod("RegisterOrGet", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(_owned, new object[] { Request, new WatchdogSafetyHandoffReceipt { Revision = 1 }, _root, _root, beforeCreate });
                    return new RecoveryProcessIdentity
                    {
                        ProcessId = (int)result.GetType().GetField("ProcessId", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(result),
                        StartUtcTicks = (long)result.GetType().GetField("ProcessStartUtcTicks", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(result),
                        ExecutablePath = Request.ExecutablePath, BootId = Identity.BootId
                    };
                }
                catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
            }

            internal ProcessObservation Status(out RecoveryProcessIdentity identity, out bool hasRecord)
            {
                var args = new object[] { Request, _root, null, false };
                try
                {
                    var result = (ProcessObservation)_type.GetMethod("ObserveRecorded", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_owned, args);
                    identity = (RecoveryProcessIdentity)args[2]; hasRecord = (bool)args[3]; return result;
                }
                catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
            }

            public void Dispose()
            {
                ((IDisposable)_owned).Dispose();
                Directory.Delete(_root, true);
            }
        }
    }
}
