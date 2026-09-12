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
            var store = new FallbackLedgerStore(directory, session);
            var verifier = new FallbackProgressVerifier();
            var laneProgress = new System.Collections.Generic.Dictionary<int, Tuple<string, long>>();
            var stopping = false;
            Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stopping = true; };
            string lastStatus = null, source = null, installation = null, progress = null;
            long changed = Stopwatch.GetTimestamp(), lastSequence = 0, lastHeartbeat = 0;
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
                    if (stopping && !owned)
                    {
                        if (ledger?.Requester == instance && (ledger.Phase == "Requested" || ledger.Phase == "Yielded"))
                            Send("Cancel", ledger, session, instance);
                        return 0;
                    }
                    if (stopping && owned && ledger.Phase == "Acquired")
                    { Send("Return", ledger, session, instance); continue; }
                    var observation = BoundedJson.Read<FallbackObservation>(statePath);
                    if (observation.SchemaVersion != 1 || observation.SessionId != session || observation.Heartbeat == null ||
                        observation.Heartbeat.ChannelProgress.Length > 12 || string.IsNullOrEmpty(observation.InstallationId))
                        throw new InvalidDataException("ObservationIdentityUnknown");
                    if (installation != null && installation != observation.InstallationId)
                        throw new InvalidDataException("InstallationChanged");
                    installation = observation.InstallationId;
                    var now = Stopwatch.GetTimestamp();
                    if (source != observation.SourceInstanceId)
                    { source = observation.SourceInstanceId; changed = received = now; lastSequence = lastHeartbeat = 0; progress = null; laneProgress.Clear(); }
                    if (observation.Sequence > lastSequence)
                    { received = now; lastSequence = observation.Sequence; }
                    if ((now - received) / (double)Stopwatch.Frequency > 5)
                        throw new InvalidOperationException("OriginalUnresponsive; isolation unproven, alert only");
                    var heartbeat = observation.Heartbeat;
                    var signature = heartbeat.RunId + ":" + heartbeat.RunEpoch + ":" + heartbeat.RecoveryProgressVersion + ":" +
                        string.Join(";", heartbeat.ChannelProgress.Select(p => p.Channel + ":" + p.DoCommandSequence + ":" + p.FormalCommitSequence));
                    if (heartbeat.Sequence > lastHeartbeat && signature != progress)
                    { changed = now; progress = signature; }
                    lastHeartbeat = Math.Max(lastHeartbeat, heartbeat.Sequence);
                    var budgetMs = Math.Max(60000d, Math.Max(1d, heartbeat.ExpectedCyclePeriodMs) * 3);
                    var eligible = (heartbeat.RecoveryEligibleChannels ?? Array.Empty<int>())
                        .Except(heartbeat.CompletedChannels ?? Array.Empty<int>())
                        .Except(heartbeat.ManuallyDisabledChannels ?? Array.Empty<int>())
                        .Except(heartbeat.PermanentAlarmedChannels ?? Array.Empty<int>()).ToArray();
                    var laneStalled = false;
                    foreach (var channel in eligible)
                    {
                        var lane = heartbeat.ChannelProgress.SingleOrDefault(p => p.Channel == channel);
                        if (lane == null) throw new InvalidDataException("MissingChannelProgress");
                        var laneSignature = heartbeat.RunId + ":" + heartbeat.RunEpoch + ":" + lane.State + ":" +
                            lane.MechanicalCompletedCount + ":" + lane.FormalCommitSequence;
                        if (!laneProgress.TryGetValue(channel, out var previous) || previous.Item1 != laneSignature)
                            laneProgress[channel] = Tuple.Create(laneSignature, now);
                        else if ((now - previous.Item2) * 1000d / Stopwatch.Frequency > budgetMs)
                            laneStalled = true;
                    }
                    if (ledger != null && ledger.RunId != heartbeat.RunId)
                        throw new InvalidDataException("RunMismatch; reconciliation required");
                    status = "Observing; original recovery has priority";
                    if (owned)
                    {
                        status = "Owned:" + ledger.Phase + "; awaiting per-channel commits or reconciliation";
                        if (ledger.Phase == "Verifying" && verifier.Observe(heartbeat, ledger, now))
                        { Send("Return", ledger, session, instance); status = "Verified and returned"; }
                    }
                    else if (ledger?.Owner == "Fallback") status = "ReconciliationRequired; prior external owner remains authoritative";
                    else if (settings.Active && !stopping && ledger != null &&
                        !observation.RecoveryBlocked && !observation.ManualStopped && !ledger.ManualStopped)
                    {
                        if (!laneStalled && ledger.Requester == instance &&
                            (ledger.Phase == "Requested" || ledger.Phase == "Yielded"))
                            Send("Cancel", ledger, session, instance);
                        else if (laneStalled && ledger.Phase == "Yielded" && ledger.Requester == instance)
                            Send("Acquire", ledger, session, instance);
                        else if (laneStalled && ledger.Phase == "Idle")
                            Send("Request", ledger, session, instance);
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
                FenceGeneration = ledger.FenceGeneration, CommandId = action == "Request" ? Guid.NewGuid().ToString("N") : ledger.CommandId };
            var result = FallbackControlPipe.Send(command);
            if (result?.Status == "Rejected") throw new InvalidOperationException(result.Detail);
            // A lost response is never interpreted as cancellation. The next
            // iteration reads durable ownership before issuing another action.
        }
    }
}
