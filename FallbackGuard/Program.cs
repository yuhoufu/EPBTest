using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.FallbackGuard
{
    public sealed class FallbackSettings
    {
        public int SchemaVersion { get; set; } = 1;
        public bool Enabled { get; set; }
        public bool Active { get; set; }
    }

    internal static class Program
    {
        private static int Main(string[] args)
        {
            try { return Run(args); }
            catch (Exception error) { Console.Error.WriteLine("FallbackGuard: " + error.Message); return 1; }
        }
        private static int Run(string[] args)
        {
            string Read(string key) { var index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
            if (args.Contains("--independent-service"))
                return IndependentExecutorService.Run(Read("--registration") ??
                    throw new ArgumentException("--registration required for independent service"));
            if (args.Contains("--read-recovery-database"))
            {
                var result = RecoveryDatabaseEvidence.Read(Read("--read-recovery-database"),
                    (Read("--channels") ?? "").Split(',').Select(int.Parse).ToArray());
                Console.WriteLine(new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(result));
                return 0;
            }
            if (args.Contains("--read-database"))
            {
                var result = DatabaseProgressReader.Read(Read("--read-database"),
                    (Read("--channels") ?? "").Split(',').Select(int.Parse).ToArray());
                Console.WriteLine(new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(result));
                return 0;
            }
            var directory = Path.GetFullPath(Read("--project-directory") ?? throw new ArgumentException("--project-directory required"));
            var session = Read("--session-id");
            if (!Guid.TryParseExact(session, "N", out _)) throw new ArgumentException("--session-id requires exact GUID N");
            var settingsPath = Path.Combine(directory, "fallback-settings-" + session + ".json");
            var statePath = Path.Combine(directory, "fallback-observation-" + session + ".json");
            var settings = File.Exists(settingsPath) ? BoundedJson.Read<FallbackSettings>(settingsPath) : new FallbackSettings();
            if (settings.SchemaVersion != 1) throw new InvalidDataException("UnknownSettingsSchema");
            if (args.Contains("--enable") || args.Contains("--disable"))
            {
                settings.Enabled = args.Contains("--enable");
                if (settings.Enabled) settings.Active = args.Contains("--active");
                BoundedJson.Write(settingsPath, settings);
                Console.WriteLine(settings.Enabled ? (settings.Active ? "Enabled: bench active" : "Enabled: observation") : "Disabled; pending ownership must be returned");
                return 0;
            }
            if (args.Contains("--status"))
            {
                Console.WriteLine("Enabled=" + settings.Enabled + ";Active=" + settings.Active);
                if (File.Exists(statePath))
                { var state = BoundedJson.Read<FallbackObservation>(statePath); Console.WriteLine(state.CapturedUtc + ";" + state.CoordinationStatus); }
                return 0;
            }
            if (!args.Contains("--run")) throw new ArgumentException("Use --enable, --disable, --status or --run");
            if (!settings.Enabled) return 0;
            using (var singleton = new Mutex(false, "Global\\MTTF-FallbackGuard-" + session))
            {
                var held = false;
                try
                {
                    try { held = singleton.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
                    if (!held) return 0;
                    using (var current = Process.GetCurrentProcess())
                        return Observe(directory, session, settingsPath, statePath, current.Id + ":" + current.StartTime.ToUniversalTime().Ticks);
                }
                finally { if (held) singleton.ReleaseMutex(); }
            }
        }

        private static int Observe(string directory, string session, string settingsPath, string statePath, string instance)
        {
            using var independentRecovery = new IndependentRecovery();
            var store = new FallbackLedgerStore(directory, session);
            var databaseMonitor = new DatabaseStallMonitor();
            var intentPath = Path.Combine(directory, "fallback-database-intent-" + session + ".json");
            var intent = File.Exists(intentPath) ? BoundedJson.Read<DatabaseWatchIntent>(intentPath) : null;
            long lastDatabaseRead = -5000;
            var databaseStalled = false;
            var databaseStatus = "DatabaseAwaitingAuthorizedRun";
            DatabaseProgressSnapshot databaseSnapshot = null;
            var installationConflict = false;
            var verifier = new FallbackProgressVerifier();
            FallbackObservation observation = null;
            long lastRequest = -5000;
            var stopping = false;
            Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stopping = true; };
            string lastStatus = null, source = null, installation = null;
            long lastSequence = 0;
            long received = Stopwatch.GetTimestamp();
            while (true)
            {
                string status;
                try
                {
                    var settings = BoundedJson.Read<FallbackSettings>(settingsPath);
                    if (settings.SchemaVersion != 1) throw new InvalidDataException("UnknownSettingsSchema");
                    stopping |= !settings.Enabled;
                    var ledger = store.Exists ? store.Read() : null;
                    var owned = ledger?.Owner == "Fallback" && ledger.OwnerInstanceId == instance;
                    if (stopping && !owned && !independentRecovery.IsPending)
                    {
                        if (ledger?.Requester == instance && (ledger.Phase == "Requested" || ledger.Phase == "Yielded"))
                            Send("Cancel", ledger, session, instance);
                        return 0;
                    }
                    if (stopping && owned && ledger.Phase == "Acquired")
                    { Send("Return", ledger, session, instance); continue; }
                    var now = Stopwatch.GetTimestamp();
                    var tickMs = (long)(now * 1000d / Stopwatch.Frequency);
                    var sourceFresh = false;
                    try
                    {
                        var candidate = BoundedJson.Read<FallbackObservation>(statePath);
                        if (candidate.SchemaVersion != 1 || candidate.SessionId != session || candidate.Heartbeat == null ||
                            candidate.Heartbeat.ChannelProgress == null || candidate.Heartbeat.ChannelProgress.Length > 12 ||
                            string.IsNullOrEmpty(candidate.InstallationId)) throw new InvalidDataException("ObservationIdentityUnknown");
                        if (installation != null && installation != candidate.InstallationId)
                        { installationConflict = true; throw new InvalidDataException("InstallationChanged"); }
                        installation = candidate.InstallationId;
                        observation = candidate;
                        if (source != observation.SourceInstanceId)
                        { source = observation.SourceInstanceId; received = now; lastSequence = 0; }
                        if (observation.Sequence > lastSequence)
                        { received = now; lastSequence = observation.Sequence; }
                        sourceFresh = DateTime.TryParse(observation.CapturedUtc, null,
                            System.Globalization.DateTimeStyles.RoundtripKind, out var captured) &&
                            Math.Abs((DateTime.UtcNow - captured.ToUniversalTime()).TotalSeconds) <= 5 &&
                            (now - received) / (double)Stopwatch.Frequency <= 5;
                    }
                    catch (Exception) { /* DB observation survives an unavailable original endpoint. */ }
                    var heartbeat = observation?.Heartbeat;
                    var recoveryVerified = false;
                    // A recovered main process owns a new run identity.  The
                    // database observer must follow that admitted identity;
                    // retaining the crashed process intent makes a live guard
                    // report a stall forever without being able to act.
                    if (intent != null && ledger != null && sourceFresh && heartbeat.RunActive &&
                        !ledger.ManualStopped && !heartbeat.ManualStopRequested &&
                        ledger.RunId == heartbeat.RunId && ledger.RunEpoch == heartbeat.RunEpoch &&
                        (intent.RunId != ledger.RunId || intent.RunEpoch != ledger.RunEpoch))
                    {
                        intent = null;
                        databaseMonitor = new DatabaseStallMonitor();
                        databaseStalled = false;
                    }
                    // Bind only to a verified formal run. Once bound, the
                    // database clock continues even when RunActive disappears.
                    if (intent == null && sourceFresh && heartbeat.RunActive &&
                        heartbeat.Phase == "Formal" && ledger != null && ledger.RunId == heartbeat.RunId &&
                        !ledger.ManualStopped && !heartbeat.ManualStopRequested)
                    {
                        var channels = (heartbeat.RecoveryEligibleChannels ?? Array.Empty<int>())
                            .Except(heartbeat.CompletedChannels ?? Array.Empty<int>())
                            .Except(heartbeat.PermanentAlarmedChannels ?? Array.Empty<int>())
                            .Except(heartbeat.ManuallyDisabledChannels ?? Array.Empty<int>()).ToArray();
                        if (channels.Length == 0) throw new InvalidDataException("NoAuthorizedDatabaseChannels");
                        var path = Path.Combine(Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar)), "index.db");
                        var baseline = DatabaseProgressReader.ReadIsolated(path, channels);
                        intent = new DatabaseWatchIntent { SessionId = session, RunId = ledger.RunId,
                            RunEpoch = ledger.RunEpoch, ProcessId = heartbeat.ProcessId,
                            ProcessStartUtcTicks = heartbeat.ProcessStartUtcTicks, Channels = channels,
                            DatabasePath = baseline.DatabasePath, DatabaseCreationUtcTicks = baseline.CreationUtcTicks,
                            PeriodMs = Math.Max(1, heartbeat.ExpectedCyclePeriodMs) };
                        BoundedJson.Write(intentPath, intent);
                    }
                    if (intent != null)
                    {
                        if (intent.SchemaVersion != 1 || intent.SessionId != session || ledger == null ||
                            ledger.RunId != intent.RunId || ledger.RunEpoch != intent.RunEpoch)
                            throw new InvalidDataException("DatabaseIntentIdentityMismatch");
                        if (ledger.ManualStopped || observation?.ManualStopped == true || heartbeat?.ManualStopRequested == true)
                        { intent.ManualStopped = true; BoundedJson.Write(intentPath, intent); }
                        if (sourceFresh && heartbeat.RunId == intent.RunId && heartbeat.RunEpoch == intent.RunEpoch)
                        {
                            var remaining = intent.Channels.Except(heartbeat.CompletedChannels ?? Array.Empty<int>())
                                .Except(heartbeat.PermanentAlarmedChannels ?? Array.Empty<int>())
                                .Except(heartbeat.ManuallyDisabledChannels ?? Array.Empty<int>()).ToArray();
                            if (!remaining.SequenceEqual(intent.Channels))
                            { intent.Channels = remaining; BoundedJson.Write(intentPath, intent); }
                        }
                        if (!intent.ManualStopped && intent.Channels.Length > 0 && tickMs - lastDatabaseRead >= 5000)
                        {
                            lastDatabaseRead = tickMs;
                            try
                            {
                                databaseMonitor.BeginVerification(ledger.LaunchProcessId, ledger.LaunchProcessStartUtcTicks);
                                databaseSnapshot = DatabaseProgressReader.ReadIsolated(intent.DatabasePath, intent.Channels);
                                databaseStalled = databaseMonitor.Observe(intent, databaseSnapshot, tickMs);
                                databaseStatus = databaseStalled ? "DatabaseStalled:" + string.Join(",", databaseMonitor.StalledChannels) : "DatabaseObserving";
                            }
                            catch (Exception readError)
                            { databaseMonitor.Unreadable(); databaseStalled = false; databaseStatus = "DatabaseUnreadable:" + readError.GetBaseException().Message; }
                        }
                        if (intent.ManualStopped || intent.Channels.Length == 0)
                        { databaseStalled = false; databaseStatus = intent.ManualStopped ? "DatabaseManualStopped" : "DatabaseAllRequiredChannelsCompleted"; }
                        recoveryVerified = sourceFresh && !databaseStalled && databaseMonitor.RecoveryVerified &&
                            verifier.Observe(heartbeat, ledger, now);
                        if (recoveryVerified)
                            databaseStatus = "DatabaseAndActionRecoveryVerified";
                        BoundedJson.Write(Path.Combine(directory, "fallback-database-status-" + session + ".json"),
                            new { CapturedUtc = DateTime.UtcNow.ToString("O"), Status = databaseStatus,
                                RunId = intent.RunId, ManualStopped = intent.ManualStopped,
                                Channels = intent.Channels, OriginalResponsive = sourceFresh });
                    }
                    independentRecovery.Observe(directory, settings.Active && !stopping, databaseStalled);
                    if (independentRecovery.IsPending)
                    {
                        Thread.Sleep(1000);
                        continue;
                    }
                    if (ledger != null && !string.IsNullOrEmpty(heartbeat?.RunId) && ledger.RunId != heartbeat.RunId)
                        throw new InvalidDataException("RunMismatch; reconciliation required");
                    status = databaseStatus + "; original recovery has priority";
                    if (owned)
                    {
                        status = "Owned:" + ledger.Phase + "; awaiting per-channel commits or reconciliation";
                        if (ledger.Phase == "Verifying" && recoveryVerified)
                        { Send("Return", ledger, session, instance); status = "Verified and returned"; }
                    }
                    else if (ledger?.Owner == "Fallback") status = "ReconciliationRequired; prior external owner remains authoritative";
                    else if (settings.Active && !stopping && ledger != null &&
                        intent != null && !installationConflict && !intent.ManualStopped && !ledger.ManualStopped)
                    {
                        if (!databaseStalled && ledger.Requester == instance &&
                            (ledger.Phase == "Requested" || ledger.Phase == "Yielded"))
                            Send("Cancel", ledger, session, instance);
                        else if (databaseStalled && ledger.Phase == "Yielded" && ledger.Requester == instance)
                            Send("Acquire", ledger, session, instance);
                        else if (databaseStalled && tickMs - lastRequest >= 5000 &&
                            (ledger.Phase == "Idle" || ledger.Phase == "Requested" && ledger.Requester == instance))
                        {
                            lastRequest = tickMs;
                            BoundedJson.Write(Path.Combine(directory, "fallback-last-database-request-" + session + ".json"),
                                new { CapturedUtc = DateTime.UtcNow.ToString("O"), RunId = intent.RunId,
                                    RunEpoch = intent.RunEpoch, StalledChannels = databaseMonitor.StalledChannels,
                                    Database = databaseSnapshot, Requester = instance, OriginalResponsive = sourceFresh });
                            Send("Request", ledger, session, instance);
                        }
                    }
                    using (var process = Process.GetCurrentProcess())
                        if (process.PrivateMemorySize64 > 128L * 1024 * 1024)
                        { stopping = true; status = "MemoryBudgetExceeded; draining ownership"; }
                }
                catch (Exception error) { status = "Unknown:" + error.GetBaseException().Message; }
                if (status != lastStatus) { Console.WriteLine(DateTime.UtcNow.ToString("O") + " " + status); lastStatus = status; }
                Thread.Sleep(1000);
            }
        }
        private static void Send(string action, FallbackLedger ledger, string session, string requester)
        {
            var command = new FallbackCommand { Action = action, SessionId = session, RunId = ledger.RunId,
                RunEpoch = ledger.RunEpoch, Revision = ledger.Revision, Requester = requester,
                FenceGeneration = ledger.FenceGeneration, CommandId = action == "Request" && ledger.Phase == "Idle" ? Guid.NewGuid().ToString("N") : ledger.CommandId };
            var result = FallbackControlPipe.Send(command);
            if (result?.Status == "Rejected") throw new InvalidOperationException(result.Detail);
            // A lost response is never interpreted as cancellation. The next
            // iteration reads durable ownership before issuing another action.
        }
    }
}
