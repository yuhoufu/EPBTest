using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using MTTFTest.RecoveryControl;
using MTTFTest.Watchdog;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    internal static class RecoveryLaunchReconciliationTests
    {
        internal static int RunAll()
        {
            using (var f = new Fixture())
            {
                f.Store.SetOperatorIntent(f.Token.AuthorizationId, f.Token.IntentVersion, RecoveryDesiredState.Stopped, "OperatorStop");
                if (!RecoveryLaunchReconciler.TryReconcile(f.Store, f.Operation, f.Path)) throw new Exception("Created process was not reconciled");
                var state = f.Store.Read();
                if (state.Intent.DesiredState != RecoveryDesiredState.Stopped || state.Launches.Single().State != "Started")
                    throw new Exception("Reconciliation must record creation without reversing stop");
                if (!RecoveryLaunchReconciler.TryReconcile(f.Store, f.Operation, f.Path)) throw new Exception("Reconciliation was not idempotent");
            }
            using (var f = new Fixture())
            {
                var json = new JavaScriptSerializer();
                var record = json.Deserialize<SessionLaunchConsumptionRecord>(File.ReadAllText(f.Path));
                record.LaunchNonce = Guid.NewGuid().ToString("N");
                File.WriteAllText(f.Path, json.Serialize(record));
                try { RecoveryLaunchReconciler.TryReconcile(f.Store, f.Operation, f.Path); throw new Exception("Tampered receipt accepted"); }
                catch (InvalidDataException ex) when (ex.Message == "RecoveryReconcileBindingMismatch") { }
                if (f.Store.Read().Launches.Single().State != "Consumed") throw new Exception("Invalid receipt changed reservation");
            }
            using (var f = new Fixture())
            {
                File.Delete(f.Path);
                if (RecoveryLaunchReconciler.TryReconcile(f.Store, f.Operation, f.Path)) throw new Exception("Missing evidence became a definite result");
                if (f.Store.Read().Launches.Single().State != "Consumed") throw new Exception("Ambiguous consumption was released");
            }
            using (var f = new Fixture())
            {
                RecoveryLaunchReconciler.TryReconcile(f.Store, f.Operation, f.Path);
                var next = Guid.NewGuid().ToString("N");
                var now = DateTime.UtcNow;
                try { f.Store.ReserveLaunch(f.Token, next, now, now.AddMinutes(1)); throw new Exception("Unretired creation allowed another operation"); }
                catch (InvalidOperationException ex) when (ex.Message == "RecoveryLaunchInFlight") { }
                RecoveryLaunchReconciler.ReconcileOutstanding(f.Store);
                if (f.Store.Read().Launches.Single().State != "Exited") throw new Exception("Exact exited child was not retired");
                f.Store.ReserveLaunch(f.Token, next, now, now.AddMinutes(1));
            }
            Console.WriteLine("PASS RecoveryGuard 创建回执对账 4/4 真实子进程、停止优先、篡改、缺失证据及退出前启动互斥");
            return 4;
        }

        private sealed class Fixture : IDisposable
        {
            private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "EPB-LaunchReconcile-" + Guid.NewGuid().ToString("N"));
            internal readonly RecoveryControlStore Store;
            internal readonly RecoveryAuthorizationToken Token;
            internal readonly string Operation = Guid.NewGuid().ToString("N");
            internal readonly string Path;

            internal Fixture()
            {
                Directory.CreateDirectory(_root);
                Path = System.IO.Path.Combine(_root, "receipt.json");
                var main = RecoveryProcessProbe.Current();
                var start = DateTime.UtcNow.AddSeconds(-2);
                Store = new RecoveryControlStore(System.IO.Path.Combine(_root, "authority"));
                Store.Register("reconcile-test", main.ExecutablePath);
                var run = Guid.NewGuid().ToString("N");
                Token = Store.BeginManualRun(run, run, "config", main, start);
                for (var i = 1; i <= 2; i++)
                {
                    var now = start.AddSeconds(i - 1);
                    Store.Observe(new RecoveryObservationSnapshot
                    {
                        Authorization = Token, MainProcess = main, RunId = run, ConfigurationIdentity = "config",
                        Sequence = i, SourceVersion = i, SourceAvailable = true, PublishedUtcTicks = now.Ticks, SourceUtcTicks = now.Ticks,
                        Channels = new[] { new RecoveryChannelProgress
                        { Channel = 1, Eligible = true, SampleSequence = i, ControlSequence = i, PersistedSequence = i, Stage = "Formal" } }
                    }, ProcessObservation.ExactAlive, main.BootId, now, new RecoveryGuardSettings(), false);
                }
                var issued = DateTime.UtcNow;
                Store.ReserveLaunch(Token, Operation, issued, issued.AddMinutes(1));
                Store.ConsumeLaunch(Token, Operation, issued);
                var args = "--watchdog-guarded-launch-child \"" + System.IO.Path.Combine(_root, "child.log") + "\"";
                var capability = new SessionLaunchCapability
                {
                    CapabilityId = Operation, SessionId = Guid.NewGuid().ToString("N"), PermitId = Guid.NewGuid().ToString("N"),
                    PermitGeneration = 1, DesktopSessionId = 1, ExecutablePath = main.ExecutablePath,
                    ExecutableSha256 = SupervisorProtocol.ComputeSha256(main.ExecutablePath), Arguments = args,
                    ArgumentsSha256 = SupervisorProtocol.ComputeTextSha256(args), WorkingDirectory = _root,
                    LaunchNonce = Guid.NewGuid().ToString("N"), IssuedUtcTicks = issued.Ticks, ExpiresUtcTicks = issued.AddMinutes(1).Ticks,
                    IssuerProcessId = main.ProcessId, IssuerProcessStartUtcTicks = main.StartUtcTicks, IsRecoveryLaunch = true,
                    RecoveryFenceJson = new RecoveryLaunchFence { IsRecovery = true, Authorization = Token }.Serialize()
                };
                using (var child = Process.Start(new ProcessStartInfo(main.ExecutablePath, args)
                { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }))
                {
                    var record = new SessionLaunchConsumptionRecord
                    {
                        SchemaVersion = SessionAgentProtocol.SchemaVersion, CapabilityId = Operation,
                        SessionId = capability.SessionId, PermitId = capability.PermitId, PermitGeneration = capability.PermitGeneration,
                        LaunchNonce = capability.LaunchNonce, ExecutablePath = capability.ExecutablePath,
                        ExecutableSha256 = capability.ExecutableSha256, ArgumentsSha256 = capability.ArgumentsSha256,
                        CapabilitySealBase64 = Convert.ToBase64String(SessionAgentProtocol.Seal(capability)),
                        State = "Started", ConsumedUtcTicks = issued.Ticks, ProcessId = child.Id,
                        ProcessStartUtcTicks = child.StartTime.ToUniversalTime().Ticks, ProcessBootId = main.BootId
                    };
                    var json = new JavaScriptSerializer();
                    File.WriteAllText(Path + ".capability", json.Serialize(capability));
                    File.WriteAllText(Path, json.Serialize(record));
                    if (!child.WaitForExit(10000))
                    {
                        child.Kill();
                        throw new Exception("Isolated receipt child timed out");
                    }
                    if (child.ExitCode != 0) throw new Exception("Isolated child failed");
                }
            }

            public void Dispose() => Directory.Delete(_root, true);
        }
    }
}
