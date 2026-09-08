using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Controller;
using DataOperation;
using MTEmbTest;
using MTTFTest.RecoveryControl;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    internal static class RecoveryGuardRuntimeTests
    {
        private const BindingFlags Static = BindingFlags.NonPublic | BindingFlags.Static;
        private static readonly Type Runtime = typeof(RecoveryGuardRuntime);

        internal static int RunAll()
        {
            PauseContinueAndCheckpointConsumption();
            StopDuringPauseCannotContinue();
            OldQueuedIntentCannotRevokeNewContinue();
            LaunchFenceSealAndWire();
            TakeoverFencesOnlyItsOldLease();
            CompletedStartupWithoutRunLease();
            BudgetCooldownPreservesIntent();
            AdmissionWaitsForVerify(false);
            AdmissionWaitsForVerify(true);
            Console.WriteLine("PASS RecoveryGuard 主程序桥接 9/9 暂停、继续、启动身份、完成发布、预算冷却及Verify等待/停止");
            return 9;
        }

        private static void BudgetCooldownPreservesIntent()
        {
            using (var scope = new Scope())
            {
                Admit(scope.Run, RunAdmissionOrigin.ManualStart);
                var before = scope.Store.Read().Token();
                var nonce = Guid.NewGuid().ToString("N");
                var now = DateTime.UtcNow.ToString("O");
                File.WriteAllText(scope.Checkpoint, new JavaScriptSerializer().Serialize(new UnattendedRunCheckpoint
                {
                    SchemaVersion = 6, RunId = scope.Run.ToString("N"), RootRunId = scope.Run.ToString("N"),
                    Armed = true, RestartPending = true, RecoveryNonce = nonce, UpdatedUtc = now,
                    RestartHistoryUtc = new List<string> { now, now, now }
                }));
                Check(UnattendedRunCheckpointStore.ReleasePendingRestartForRetry(nonce, scope.Run.ToString("N"),
                    "LaunchFailed", out var attempts, out var releaseError) && attempts == 3 && string.IsNullOrEmpty(releaseError),
                    "three attempts release nonce for cooldown without disarming");
                Check(!UnattendedRunCheckpointStore.TryRegisterRestart(Guid.NewGuid().ToString("N"), scope.Run.ToString("N"),
                    out _, out var reason) && reason.StartsWith("RecoveryCoolingDown:"), "next registration must enforce the same budget");
                var checkpoint = UnattendedRunCheckpointStore.Load();
                Check(checkpoint.Armed && !checkpoint.RestartPending && !string.IsNullOrEmpty(checkpoint.NextRetryUtc),
                    "cooldown keeps authorization and a concrete retry time");
                Check(scope.Store.Read().Matches(before) && !RecoveryRevocationSignal.IsStopped(before),
                    "budget exhaustion must neither advance operator intent nor emit Stop");
                UnattendedRunCheckpointStore.Disarm("ManualStop");
                Check(scope.Store.Read().Intent.DesiredState == RecoveryDesiredState.Stopped,
                    "manual stop during cooldown must still revoke external recovery");
            }
        }

        private static void AdmissionWaitsForVerify(bool stop)
        {
            using (var scope = new Scope())
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            {
                var owner = RecoveryProcessProbe.Current();
                var transaction = scope.PrepareGuardLaunch(owner);
                var nextRun = Guid.NewGuid();
                var request = (RunAdmissionRequest)Activator.CreateInstance(typeof(RunAdmissionRequest),
                    BindingFlags.NonPublic | BindingFlags.Instance, null,
                    new object[] { new RunChainIdentity(nextRun, scope.Run, scope.Run, 1, 2), new[] { 1 }, RunAdmissionOrigin.AutomaticRecovery }, null);
                var admission = (Task)Call("AdmitAsync", request, null, cancellation.Token);
                try
                {
                    Check(SpinWait.SpinUntil(() => scope.Store.Read().Intent.RunId == nextRun.ToString("N"), 5000),
                        "main must reach binding before the test advances its Guard");
                    Check(Task.WhenAny(admission, Task.Delay(200)).GetAwaiter().GetResult() != admission,
                        "new main must remain pending while Guard is in Launch");
                    if (stop)
                    {
                        var intent = scope.Store.Read().Intent;
                        scope.Store.SetOperatorIntent(intent.AuthorizationId, intent.IntentVersion, RecoveryDesiredState.Stopped, "test stop while waiting");
                    }
                    else
                        scope.Store.Advance(transaction.TransactionId, transaction.Epoch, owner, RecoveryStage.Launch,
                            RecoveryStage.Verify, "created process confirmed", DateTime.UtcNow, new RecoveryGuardSettings());
                    Check(Task.WhenAny(admission, Task.Delay(5000)).GetAwaiter().GetResult() == admission,
                        "admission must resolve after persisted Verify or Stop");
                    if (stop)
                    {
                        try { admission.GetAwaiter().GetResult(); }
                        catch (InvalidOperationException ex) when (ex.Message.Contains("Revoked")) { return; }
                        throw new Exception("stopped waiting main was admitted");
                    }
                    admission.GetAwaiter().GetResult();
                    Check(scope.Store.Read().Intent.DesiredState == RecoveryDesiredState.Run, "Verify retains the run intent");
                }
                finally
                {
                    cancellation.Cancel();
                    try { admission.GetAwaiter().GetResult(); } catch { }
                }
            }
        }

        private static void CompletedStartupWithoutRunLease()
        {
            using (var scope = new Scope())
            {
                var now = DateTime.UtcNow.AddSeconds(-60);
                var current = RecoveryProcessProbe.Current();
                var previous = new JavaScriptSerializer().Deserialize<RecoveryProcessIdentity>(
                    new JavaScriptSerializer().Serialize(current));
                previous.ProcessId++;
                var token = scope.Store.BeginManualRun(scope.Run.ToString("N"), scope.Run.ToString("N"),
                    UnattendedRunCheckpointStore.ComputeConfigurationHash(null), previous, now);
                var snapshot = new RecoveryObservationSnapshot
                {
                    Authorization = token, RunId = scope.Run.ToString("N"), MainProcess = previous,
                    ConfigurationIdentity = UnattendedRunCheckpointStore.ComputeConfigurationHash(null),
                    Sequence = 1, SourceVersion = 1, SourceAvailable = true,
                    PublishedUtcTicks = now.Ticks, SourceUtcTicks = now.Ticks,
                    Channels = new[] { new RecoveryChannelProgress
                    {
                        Channel = 1, Eligible = true, Stage = "Formal", SampleSequence = 1,
                        ControlSequence = 1, PersistedSequence = 1
                    } }
                };
                var settings = new RecoveryGuardSettings();
                scope.Store.Observe(snapshot, ProcessObservation.ExactAlive, current.BootId, now, settings, false);
                now = DateTime.UtcNow;
                snapshot.Sequence++; snapshot.SourceVersion++;
                snapshot.PublishedUtcTicks = snapshot.SourceUtcTicks = now.Ticks;
                snapshot.Channels[0].SampleSequence++;
                snapshot.Channels[0].ControlSequence++;
                snapshot.Channels[0].PersistedSequence++;
                scope.Store.Observe(snapshot, ProcessObservation.ExactAlive, current.BootId, now, settings, false);
                var operation = Guid.NewGuid().ToString("N");
                scope.Store.ReserveLaunch(token, operation, now, now.AddMinutes(1));
                scope.Store.ConsumeLaunch(token, operation, now);
                scope.Store.RecordLaunchResult(operation, current, false);
                Check(Runtime.GetField("_lease", Static).GetValue(null) == null, "startup has not admitted a trial");
                RecoveryGuardRuntime.CompleteRecoveredRunBeforeAdmission(new UnattendedRunCheckpoint
                {
                    RootRunId = scope.Run.ToString("D"), RunId = scope.Run.ToString("D")
                }, null);
                Check(scope.Store.Read().Intent.DesiredState == RecoveryDesiredState.Completed,
                    "durably complete startup must cancel auto-resume even without a run lease");
            }
        }

        private static void TakeoverFencesOnlyItsOldLease()
        {
            using (var scope = new Scope())
            {
                Admit(scope.Run, RunAdmissionOrigin.ManualStart);
                var state = scope.Store.Read();
                var calls = 0;
                Runtime.GetField("_externalFence", Static).SetValue(null, new Action<string, string>((run, reason) =>
                {
                    Check(run == scope.Run.ToString("N"), "fence must target the superseded run");
                    calls++;
                }));
                state.Transaction = new RecoveryTakeoverTransaction
                {
                    TransactionId = Guid.NewGuid().ToString("N"), AuthorizationId = Guid.NewGuid().ToString("N"),
                    IntentVersion = state.Intent.IntentVersion, Epoch = state.LastTakeoverEpoch + 1
                };
                RecoveryGuardRuntime.ApplyTakeoverFence(state);
                Check(calls == 0, "other authorization must not fence this run");
                state.Transaction.AuthorizationId = state.Intent.AuthorizationId;
                state.Transaction.Epoch = state.LastTakeoverEpoch;
                RecoveryGuardRuntime.ApplyTakeoverFence(state);
                Check(calls == 0, "current launch epoch must remain enabled");
                state.Transaction.Epoch++;
                RecoveryGuardRuntime.ApplyTakeoverFence(state);
                RecoveryGuardRuntime.ApplyTakeoverFence(state);
                Check(calls == 1, "superseded main must be fenced once");
                Check(scope.Store.Read().Intent.DesiredState == RecoveryDesiredState.Run,
                    "automatic takeover must not be converted into a user stop");
            }
        }

        private static void LaunchFenceSealAndWire()
        {
            var process = RecoveryProcessProbe.Current();
            var now = DateTime.UtcNow;
            var capability = new SessionLaunchCapability
            {
                CapabilityId = Guid.NewGuid().ToString("N"), SessionId = Guid.NewGuid().ToString("N"),
                PermitId = Guid.NewGuid().ToString("N"), PermitGeneration = 1, DesktopSessionId = 1,
                ExecutablePath = process.ExecutablePath, ExecutableSha256 = new string('a', 64),
                ArgumentsSha256 = SupervisorProtocol.ComputeTextSha256(string.Empty),
                WorkingDirectory = Path.GetDirectoryName(process.ExecutablePath), LaunchNonce = Guid.NewGuid().ToString("N"),
                IssuedUtcTicks = now.Ticks, ExpiresUtcTicks = now.AddMinutes(1).Ticks,
                IssuerProcessId = process.ProcessId, IssuerProcessStartUtcTicks = process.StartUtcTicks,
                IsRecoveryLaunch = true,
                RecoveryFenceJson = new RecoveryLaunchFence
                {
                    IsRecovery = true, Authorization = new RecoveryAuthorizationToken
                    {
                        InstallationId = Guid.NewGuid().ToString("N"), AuthorizationId = Guid.NewGuid().ToString("N"),
                        IntentVersion = 5, TakeoverEpoch = 2
                    }
                }.Serialize()
            };
            var seal = SessionAgentProtocol.Seal(capability);
            using (var stream = new MemoryStream())
            {
                capability.WriteTo(new BinaryWriter(stream), seal);
                stream.Position = 0;
                var read = SessionLaunchCapability.ReadFrom(new BinaryReader(stream), out var readSeal);
                Check(SessionAgentProtocol.VerifySeal(read, readSeal), "wire roundtrip must retain signed fence");
                var modified = RecoveryLaunchFence.Parse(read.RecoveryFenceJson, true);
                modified.Authorization.IntentVersion++;
                read.RecoveryFenceJson = modified.Serialize();
                Check(!SessionAgentProtocol.VerifySeal(read, readSeal), "changing intent version must invalidate seal");
                read.RecoveryFenceJson = string.Empty;
                Check(!SessionAgentProtocol.VerifySeal(read, readSeal), "removing external fence must invalidate seal");
            }
        }

        private static void Check(bool condition, string reason)
        {
            if (!condition) throw new Exception("RecoveryGuard runtime: " + reason);
        }

        private static object Call(string method, params object[] args) => Runtime.GetMethod(method, Static).Invoke(null, args);

        private static void Admit(Guid run, RunAdmissionOrigin origin)
        {
            var request = (RunAdmissionRequest)Activator.CreateInstance(typeof(RunAdmissionRequest),
                BindingFlags.NonPublic | BindingFlags.Instance, null,
                new object[] { new RunChainIdentity(run, run, Guid.Empty, 0, 1), new[] { 1 }, origin }, null);
            ((Task)Call("AdmitAsync", request, null, CancellationToken.None)).GetAwaiter().GetResult();
        }

        private static void PauseContinueAndCheckpointConsumption()
        {
            using (var scope = new Scope())
            {
                Admit(scope.Run, RunAdmissionOrigin.ManualStart);
                var first = scope.Store.Read().Token();
                Call("RequestManualPause", scope.Run);
                Check(RecoveryRevocationSignal.IsPaused(first), "pause must notify before checkpoint I/O");
                Admit(scope.Run, RunAdmissionOrigin.ManualContinue);
                var resumed = scope.Store.Read();
                Check(resumed.Intent.DesiredState == RecoveryDesiredState.Run && resumed.Intent.IntentVersion > first.IntentVersion,
                    "same-process continue must durably advance authorization");

                File.WriteAllText(scope.Checkpoint, new JavaScriptSerializer().Serialize(new UnattendedRunCheckpoint
                {
                    RunId = scope.Run.ToString("N"), RootRunId = scope.Run.ToString("N"),
                    Armed = true, GracefulPaused = true, UpdatedUtc = DateTime.UtcNow.ToString("O")
                }));
                UnattendedRunCheckpointStore.ClearGracefulPause("SameProcessResumed");
                Check(scope.Store.Read().Matches(resumed.Token()), "consuming pause checkpoint must not emit a stop");
                Check(scope.Store.Read().Intent.DesiredState == RecoveryDesiredState.Run, "continued run must remain authorized");
            }
        }

        private static void StopDuringPauseCannotContinue()
        {
            using (var scope = new Scope())
            {
                Admit(scope.Run, RunAdmissionOrigin.ManualStart);
                Call("RequestManualPause", scope.Run);
                RecoveryGuardRuntime.CheckpointTerminal(new UnattendedRunCheckpoint
                {
                    RunId = scope.Run.ToString("N"), GracefulPaused = true
                });
                RecoveryGuardRuntime.CheckpointTerminal(new UnattendedRunCheckpoint
                {
                    RunId = scope.Run.ToString("N"), Armed = false, LastReason = "ManualStop"
                });
                Check(scope.Store.Read().Intent.DesiredState == RecoveryDesiredState.Stopped, "stop must escalate persisted pause");
                try { Admit(scope.Run, RunAdmissionOrigin.ManualContinue); }
                catch (InvalidOperationException) { return; }
                throw new Exception("stopped authorization was manually continued as a pause");
            }
        }

        private static void OldQueuedIntentCannotRevokeNewContinue()
        {
            using (var scope = new Scope())
            {
                Admit(scope.Run, RunAdmissionOrigin.ManualStart);
                var oldLease = Runtime.GetField("_lease", Static).GetValue(null);
                Call("RequestManualPause", scope.Run);
                Admit(scope.Run, RunAdmissionOrigin.ManualContinue);
                Call("RequestManualPause", scope.Run);
                RecoveryGuardRuntime.CheckpointTerminal(new UnattendedRunCheckpoint
                {
                    RunId = scope.Run.ToString("N"), GracefulPaused = true
                });
                var current = scope.Store.Read();
                var requestType = Runtime.GetNestedType("TerminalRequest", BindingFlags.NonPublic);
                var request = Activator.CreateInstance(requestType, true);
                var fields = BindingFlags.NonPublic | BindingFlags.Instance;
                requestType.GetField("Lease", fields).SetValue(request, oldLease);
                requestType.GetField("State", fields).SetValue(request, RecoveryDesiredState.Stopped);
                requestType.GetField("Reason", fields).SetValue(request, "DelayedOldStop");
                Call("CommitTerminal", request);
                Check(scope.Store.Read().Matches(current.Token()), "old stop cannot overwrite a newer paused intent");
            }
        }

        // Redirect only test-process statics, before any publisher is started.
        // Every durable write stays below a unique temporary directory.
        private sealed class Scope : IDisposable
        {
            internal readonly Guid Run = Guid.NewGuid();
            internal readonly string Root = Path.Combine(Path.GetTempPath(), "EPB-GuardRuntime-" + Guid.NewGuid().ToString("N"));
            internal readonly RecoveryControlStore Store;
            internal readonly string Checkpoint;
            private readonly Dictionary<FieldInfo, object> _saved = new Dictionary<FieldInfo, object>();

            internal Scope()
            {
                Check(Runtime.GetField("_worker", Static).GetValue(null) == null, "test cannot redirect a running publisher");
                Directory.CreateDirectory(Root);
                Store = new RecoveryControlStore(Path.Combine(Root, "authority"));
                Store.Register("runtime-test", RecoveryProcessProbe.Current().ExecutablePath);
                Redirect(Runtime, "Store", Store);
                Redirect(Runtime, "_lease", null);
                Redirect(Runtime, "_externalFence", null);
                Redirect(Runtime, "_lastObservedFence", null);
                Checkpoint = Path.Combine(Root, "checkpoint.json");
                Redirect(typeof(UnattendedRunCheckpointStore), "CheckpointPath", Checkpoint);
                Redirect(typeof(UnattendedRunCheckpointStore), "CheckpointBackupPath", Checkpoint + ".bak");
                Redirect(typeof(UnattendedRunCheckpointStore), "CheckpointAuditPath", Path.Combine(Root, "audit.jsonl"));
            }

            internal RecoveryTakeoverTransaction PrepareGuardLaunch(RecoveryProcessIdentity owner)
            {
                var now = DateTime.UtcNow;
                var old = new JavaScriptSerializer().Deserialize<RecoveryProcessIdentity>(new JavaScriptSerializer().Serialize(owner));
                old.ProcessId++; old.StartUtcTicks = now.AddMinutes(-10).Ticks;
                var config = UnattendedRunCheckpointStore.ComputeConfigurationHash(null);
                var token = Store.BeginManualRun(Run.ToString("N"), Run.ToString("N"), config, old, now.AddSeconds(-180));
                var snapshot = new RecoveryObservationSnapshot
                {
                    Authorization = token, RunId = Run.ToString("N"), ConfigurationIdentity = config, MainProcess = old,
                    Sequence = 1, SourceVersion = 1, SourceAvailable = true,
                    PublishedUtcTicks = now.AddSeconds(-180).Ticks, SourceUtcTicks = now.AddSeconds(-180).Ticks,
                    Channels = new[] { new RecoveryChannelProgress
                    {
                        Channel = 1, Eligible = true, Stage = "Formal", SampleSequence = 1, ControlSequence = 1, PersistedSequence = 1
                    } }
                };
                var settings = new RecoveryGuardSettings { Mode = RecoveryGuardMode.RecoverExited };
                for (var i = 0; i < 4; i++)
                {
                    var at = now.AddSeconds(-180 + i * 60);
                    snapshot.Sequence++; snapshot.SourceVersion++;
                    snapshot.PublishedUtcTicks = snapshot.SourceUtcTicks = at.Ticks;
                    if (i == 1)
                    {
                        snapshot.Channels[0].SampleSequence++; snapshot.Channels[0].ControlSequence++;
                        snapshot.Channels[0].PersistedSequence++;
                    }
                    Store.Observe(snapshot, i < 2 ? ProcessObservation.ExactAlive : ProcessObservation.Exited, owner.BootId, at, settings, false);
                }
                var transaction = Store.Claim(snapshot, ProcessObservation.Exited, owner, now, settings, false);
                var stage = RecoveryStage.Claim;
                foreach (var next in new[] { RecoveryStage.SafeStop, RecoveryStage.Retire, RecoveryStage.Launch })
                {
                    Store.Advance(transaction.TransactionId, transaction.Epoch, owner, stage, next, "isolated test evidence", now, settings);
                    stage = next;
                }
                token = Store.Read().Token();
                var operation = Guid.NewGuid().ToString("N");
                Store.ReserveLaunch(token, operation, now, now.AddMinutes(1));
                Store.ConsumeLaunch(token, operation, now);
                Store.RecordLaunchResult(operation, owner, false);
                return transaction;
            }

            private void Redirect(Type type, string name, object value)
            {
                var field = type.GetField(name, Static);
                _saved.Add(field, field.GetValue(null));
                field.SetValue(null, value);
            }

            public void Dispose()
            {
                var lease = Runtime.GetField("_lease", Static).GetValue(null);
                if (lease != null)
                    ((IDisposable)lease.GetType().GetField("Signal", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(lease)).Dispose();
                var queue = Runtime.GetField("Terminals", Static).GetValue(null);
                var dequeue = queue.GetType().GetMethod("TryDequeue");
                while ((bool)dequeue.Invoke(queue, new object[] { null })) { }
                foreach (var item in _saved) item.Key.SetValue(null, item.Value);
                Directory.Delete(Root, true);
            }
        }
    }
}
