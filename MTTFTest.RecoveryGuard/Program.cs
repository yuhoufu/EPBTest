using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Web.Script.Serialization;
using MTTFTest.RecoveryControl;

namespace MTTFTest.RecoveryGuard
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Dictionary<string, string> options = null;
            var dispatchAttempted = false;
            try
            {
                options = Parse(args);
                var store = new RecoveryControlStore(Get(options, "root", RecoveryControlStore.DefaultRoot));
                if (options.ContainsKey("register"))
                {
                    Print(store.Register(Required(options, "bench"), Required(options, "main")));
                    return 0;
                }
                if (options.ContainsKey("status"))
                {
                    Print(store.Read());
                    return 0;
                }
                if (options.ContainsKey("stop"))
                {
                    Print(store.SetOperatorIntent(Required(options, "authorization"),
                        long.Parse(Required(options, "intent-version"), CultureInfo.InvariantCulture),
                        RecoveryDesiredState.Stopped, "RecoveryGuardOperatorStop"));
                    return 0;
                }
                if (options.ContainsKey("validate-settings"))
                {
                    Print(ReadSettings(options));
                    return 0;
                }
                if (options.ContainsKey("execute"))
                    return Execute(store, options);
                if (!options.ContainsKey("check"))
                    throw new ArgumentException("Expected --check, --status, --stop, --register or --validate-settings");
                var settings = ReadSettings(options);
                var current = store.Read();
                var bootId = RecoveryProcessProbe.ReadBootId();
                var process = current.Intent == null ? ProcessObservation.Unknown :
                    RecoveryProcessProbe.Observe(current.Intent.MainProcess, bootId);
                var snapshot = store.ReadSnapshot();
                var maintenance = File.Exists(Path.Combine(Path.GetDirectoryName(store.Root), "maintenance-inhibit.json"));
                maintenance |= File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "MTTFTestRecoveryGuard", "maintenance-inhibit.json"));
                var decision = store.Observe(snapshot, process, bootId, DateTime.UtcNow, settings, maintenance);
                var worker = RecoveryWorkerDispatch.Coordinate(store.Read(), decision, settings, maintenance, DateTime.UtcNow,
                    identity => RecoveryProcessProbe.Observe(identity, bootId),
                    () =>
                    {
                        dispatchAttempted = true;
                        RecoveryWorkerDispatch.RequestInstalledTask(typeof(Program).Assembly.Location,
                            Get(options, "settings", string.Empty), store.Root);
                    },
                    transaction =>
                    {
                        dispatchAttempted = true;
                        var executable = typeof(Program).Assembly.Location;
                        RecoveryWorkerDispatch.ValidateInstalledTask(executable, Get(options, "settings", string.Empty), store.Root);
                        return RecoveryWorkerRetirement.Retire(store, transaction, executable, () =>
                        {
                            if (File.Exists(Path.Combine(Path.GetDirectoryName(store.Root), "maintenance-inhibit.json")) ||
                                File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                    "MTTFTestRecoveryGuard", "maintenance-inhibit.json")))
                                throw new InvalidOperationException("RecoveryGuardMaintenanceInhibited");
                        });
                    },
                    idleWorker =>
                    {
                        dispatchAttempted = true;
                        var executable = typeof(Program).Assembly.Location;
                        RecoveryWorkerDispatch.ValidateInstalledTask(executable, Get(options, "settings", string.Empty), store.Root);
                        return RecoveryWorkerRetirement.RetireIdle(store, idleWorker, executable, () =>
                        {
                            if (File.Exists(Path.Combine(Path.GetDirectoryName(store.Root), "maintenance-inhibit.json")) ||
                                File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                    "MTTFTestRecoveryGuard", "maintenance-inhibit.json")))
                                throw new InvalidOperationException("RecoveryGuardMaintenanceInhibited");
                        });
                    });
                var observationReport = new
                {
                    SchemaVersion = 1,
                    ObservedUtc = DateTime.UtcNow.ToString("O"),
                    Mode = settings.Mode.ToString(),
                    Process = process.ToString(),
                    Decision = decision,
                    Worker = worker,
                    Revision = store.Read().Revision
                };
                WriteJournal(options, observationReport);
                Print(observationReport);
                return worker.NeedsAttention ? 2 : 0;
            }
            catch (Exception ex)
            {
                var failure = new { SchemaVersion = 1, ObservedUtc = DateTime.UtcNow.ToString("O"), Error = ex.GetBaseException().Message,
                    AutomaticActionPerformed = options?.ContainsKey("execute") == true || dispatchAttempted ? (bool?)null : false,
                    AutomaticActionOutcome = options?.ContainsKey("execute") == true || dispatchAttempted ? "ReconcileRequired" : "NoActionDispatched" };
                try { WriteJournal(options, failure); } catch { }
                Print(failure);
                return 2;
            }
        }

        private static int Execute(RecoveryControlStore store, Dictionary<string, string> options)
        {
            var settings = ReadSettings(options);
            RecoveryCommissioningScope commissioning = null;
            if (options.ContainsKey("commission-until-utc"))
            {
                var expiry = DateTime.ParseExact(Required(options, "commission-until-utc"), "O",
                    CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                commissioning = new RecoveryCommissioningScope(Required(options, "commission-installation"),
                    Required(options, "commission-authorization"), long.Parse(Required(options, "commission-intent-version"),
                        CultureInfo.InvariantCulture), DateTime.UtcNow, expiry, settings.Mode);
                commissioning.Demand(store.Read(), DateTime.UtcNow);
            }
            // The production service uses the installation authority. Never
            // pair an arbitrary test root with the real Supervisor transport.
            if (settings.Mode != RecoveryGuardMode.ObserveOnly && !string.Equals(store.Root.TrimEnd('\\'),
                Path.GetFullPath(RecoveryControlStore.DefaultRoot).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("RecoveryExecutionRequiresInstallationRoot");
            using (var gate = new Mutex(false, "Global\\MTTFTest.RecoveryGuard.Execution"))
            {
                var held = false;
                try
                {
                    try { held = gate.WaitOne(0); }
                    catch (AbandonedMutexException) { held = true; }
                    if (!held) { Print(new { Decision = "ExecutionWorkerAlreadyRunning" }); return 0; }
                    var owner = RecoveryProcessProbe.Current();
                    Func<RecoveryProcessIdentity, ProcessObservation> probe = identity => RecoveryProcessProbe.Observe(identity, owner.BootId);
                    if (settings.Mode != RecoveryGuardMode.ObserveOnly)
                    {
                        var previous = store.Read().ExecutionWorker?.Owner;
                        store.PulseExecutionWorker(owner, previous, previous == null ? ProcessObservation.Exited : probe(previous),
                            DateTime.UtcNow, settings.OwnerLeaseSeconds);
                    }
                    var actions = new SupervisorRecoveryActions(store, new RecoverySupervisorTransport(), () => DateTime.UtcNow, probe);
                    var engine = new RecoveryExecutionEngine(store, settings, owner, actions, () => DateTime.UtcNow, probe,
                        () => File.Exists(Path.Combine(Path.GetDirectoryName(store.Root), "maintenance-inhibit.json")) ||
                            File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                "MTTFTestRecoveryGuard", "maintenance-inhibit.json")), commissioning);
                    using (var cancellation = new CancellationTokenSource())
                    {
                        if (commissioning != null)
                        {
                            commissioning.Demand(store.Read(), DateTime.UtcNow);
                            cancellation.CancelAfter(commissioning.ExpiresUtc - DateTime.UtcNow);
                            var started = new { SchemaVersion = 1, Commissioning = true,
                                ProductionAcceptance = false, ExpiresUtc = commissioning.ExpiresUtc.ToString("O") };
                            WriteJournal(options, started); Print(started);
                        }
                        ConsoleCancelEventHandler cancel = (sender, args) => { args.Cancel = true; cancellation.Cancel(); };
                        Console.CancelKeyPress += cancel;
                        try
                        {
                            engine.RunAsync(step =>
                            {
                                if (settings.Mode != RecoveryGuardMode.ObserveOnly)
                                    store.PulseExecutionWorker(owner, owner, ProcessObservation.ExactAlive,
                                        DateTime.UtcNow, settings.OwnerLeaseSeconds);
                                var report = new { SchemaVersion = 1, ObservedUtc = DateTime.UtcNow.ToString("O"),
                                    Decision = step.Code, step.StopWorker, step.ActionDispatched, step.NextDelaySeconds };
                                WriteJournal(options, report); Print(report);
                            }, cancellation.Token).GetAwaiter().GetResult();
                        }
                        finally { Console.CancelKeyPress -= cancel; }
                    }
                    return 0;
                }
                finally { if (held) gate.ReleaseMutex(); }
            }
        }

        private static Dictionary<string, string> Parse(string[] args)
        {
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            var allowed = new HashSet<string>(new[]
            {
                "check", "execute", "status", "stop", "register", "validate-settings", "root", "settings", "journal", "bench", "main", "authorization", "intent-version",
                "commission-until-utc", "commission-installation", "commission-authorization", "commission-intent-version"
            }, StringComparer.Ordinal);
            var commands = 0;
            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("InvalidArgument");
                var key = args[i].Substring(2);
                if (!allowed.Contains(key) || options.ContainsKey(key)) throw new ArgumentException("UnknownOrDuplicateOption:" + key);
                if (key == "check" || key == "execute" || key == "status" || key == "stop" || key == "register" || key == "validate-settings")
                {
                    options.Add(key, "true");
                    commands++;
                }
                else
                {
                    if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException("MissingOptionValue:" + key);
                    options.Add(key, args[i]);
                }
            }
            if (commands != 1) throw new ArgumentException("ExactlyOneCommandRequired");
            var commissioningOptions = 0;
            foreach (var key in options.Keys)
                if (key.StartsWith("commission-", StringComparison.Ordinal)) commissioningOptions++;
            if (commissioningOptions != 0 && (commissioningOptions != 4 || !options.ContainsKey("execute")))
                throw new ArgumentException("CommissioningRequiresExecuteAndCompleteScope");
            return options;
        }

        private static void WriteJournal(Dictionary<string, string> options, object record)
        {
            // Explicit path keeps isolated CLI tests and ad-hoc status reads
            // from mutating the real installation's journal.
            if (options == null || !options.TryGetValue("journal", out var directory)) return;
            directory = Path.GetFullPath(directory);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "guard-events.jsonl");
            if (File.Exists(path) && new FileInfo(path).Length >= 8 * 1024 * 1024)
            {
                var previous = Path.Combine(directory, "guard-events.previous.jsonl");
                if (File.Exists(previous)) File.Delete(previous);
                File.Move(path, previous);
            }
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            {
                writer.WriteLine(new JavaScriptSerializer().Serialize(record));
                writer.Flush();
                stream.Flush(true);
            }
        }

        private static RecoveryGuardSettings ReadSettings(Dictionary<string, string> options)
        {
            var path = Get(options, "settings", Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "guard-settings.json"));
            if (!File.Exists(path) && options.ContainsKey("settings")) throw new FileNotFoundException("RecoveryGuardSettingsMissing", path);
            if (File.Exists(path) && new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("RecoveryGuardSettingsOversized");
            var settings = File.Exists(path)
                ? new JavaScriptSerializer().Deserialize<RecoveryGuardSettings>(File.ReadAllText(path))
                : new RecoveryGuardSettings();
            if (settings == null) throw new InvalidDataException("RecoveryGuardSettingsMissing");
            settings.Validate();
            return settings;
        }

        private static string Get(Dictionary<string, string> options, string key, string fallback) =>
            options.TryGetValue(key, out var value) ? value : fallback;
        private static string Required(Dictionary<string, string> options, string key) =>
            options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value : throw new ArgumentException("MissingOption:" + key);
        private static void Print(object value)
        {
            // JSON is a machine interface. Escaping non-ASCII characters avoids
            // Windows PowerShell/OEM code-page conversion corrupting paths.
            var json = new JavaScriptSerializer().Serialize(value);
            var portable = new System.Text.StringBuilder(json.Length);
            foreach (var character in json)
            {
                if (character > 127) portable.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                else portable.Append(character);
            }
            Console.WriteLine(portable.ToString());
        }
    }
}
