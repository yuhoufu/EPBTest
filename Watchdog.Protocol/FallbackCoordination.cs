using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace MTTFTest.Watchdog.Protocol
{
    public sealed class FallbackLedger
    {
        public int SchemaVersion { get; set; } = 1;
        public long Revision { get; set; }
        public string SessionId { get; set; }
        public string RunId { get; set; }
        public long RunEpoch { get; set; }
        public string Owner { get; set; } = "Original";
        public string OwnerInstanceId { get; set; }
        public long FenceGeneration { get; set; } = 1;
        public string Phase { get; set; } = "Idle";
        public string CommandId { get; set; }
        public string Requester { get; set; }
        public long StopRevision { get; set; }
        public bool ManualStopped { get; set; }
        public string OutstandingLaunch { get; set; }
        public int LaunchProcessId { get; set; }
        public long LaunchProcessStartUtcTicks { get; set; }
        public string Detail { get; set; }
        public string IndependentRecoveryRequestId { get; set; }
    }

    public sealed class FallbackObservation
    {
        public int SchemaVersion { get; set; } = 1;
        public string InstallationId { get; set; }
        public string SessionId { get; set; }
        public string SourceInstanceId { get; set; }
        public int ProcessId { get; set; }
        public long ProcessStartUtcTicks { get; set; }
        public long Sequence { get; set; }
        public string CapturedUtc { get; set; }
        public bool OriginalBusy { get; set; }
        public bool RecoveryBlocked { get; set; }
        public bool ManualStopped { get; set; }
        public string CoordinationStatus { get; set; }
        public WatchdogHeartbeat Heartbeat { get; set; }
    }

    // Single serialized authority. The file is not itself a safety proof:
    // the original executor must supply Yield at a proven quiescent boundary.
    public sealed class FallbackLedgerStore
    {
        private readonly string _path;
        private readonly string _mutex;
        public FallbackLedgerStore(string directory, string sessionId)
        {
            if (!Guid.TryParseExact(sessionId, "N", out _))
                throw new ArgumentException("FallbackSessionInvalid");
            _path = Path.Combine(Path.GetFullPath(directory), "fallback-" + sessionId + ".json");
            _mutex = "Global\\MTTF-Fallback-" + SupervisorProtocol.ComputeTextSha256(_path.ToUpperInvariant());
        }
        public FallbackLedger Read() => BoundedJson.Read<FallbackLedger>(_path);
        public bool Exists => File.Exists(_path);

        public FallbackLedger RetireForIndependentRecovery(string requestId, int pid, long start, string previousRequestId = null) =>
            Transaction(null, value =>
            {
                Require(value != null && !value.ManualStopped && value.Owner == "Original" &&
                    (string.IsNullOrEmpty(value.IndependentRecoveryRequestId) || value.IndependentRecoveryRequestId == requestId ||
                     value.IndependentRecoveryRequestId == previousRequestId) &&
                    (value.Phase == "Idle" || value.Phase == "Requested" ||
                     value.Phase == "OriginalVerifying" && value.LaunchProcessId == pid &&
                     value.LaunchProcessStartUtcTicks == start), "IndependentRetirementUnresolved");
                Require(Guid.TryParseExact(requestId, "N", out _), "IndependentRequestInvalid");
                value.IndependentRecoveryRequestId = requestId;
                value.Detail = "Original launch authority fenced for independent fresh safety request";
                return value;
            });

        public FallbackLedger RebindOriginal(long revision, string previous, string replacement) =>
            Transaction(revision, value =>
            {
                Require(value != null && value.Owner == "Original" && value.OwnerInstanceId == previous &&
                    value.Phase == "Idle" && string.IsNullOrEmpty(value.OutstandingLaunch), "OriginalRebindUnresolved");
                value.OwnerInstanceId = replacement; value.FenceGeneration = checked(value.FenceGeneration + 1);
                return value;
            });

        public FallbackLedger PrepareOriginalLaunch(string original, string intent) =>
            Transaction(null, value =>
            {
                Require(value != null && value.Owner == "Original" && value.OwnerInstanceId == original &&
                    value.Phase != "Yielded" && !value.ManualStopped &&
                    string.IsNullOrEmpty(value.IndependentRecoveryRequestId) &&
                    string.IsNullOrEmpty(value.OutstandingLaunch), "OriginalLaunchRejected");
                value.OutstandingLaunch = intent; value.Phase = "OriginalLaunchPrepared";
                return value;
            });

        // Called only by the original executor after observing the admitted
        // active run. A new run never inherits an old stop or request token.
        public FallbackLedger BindActiveRun(long revision, string original, string runId, long epoch) =>
            Transaction(revision, value =>
            {
                Require(value != null && value.Owner == "Original" && value.OwnerInstanceId == original &&
                    value.Phase == "Idle" && string.IsNullOrEmpty(value.OutstandingLaunch) &&
                    Guid.TryParse(runId, out _) && epoch > 0 &&
                    (value.RunId != runId || epoch > value.RunEpoch), "ActiveRunBindRejected");
                if (value.RunId != runId)
                { value.LaunchProcessId = 0; value.LaunchProcessStartUtcTicks = 0; }
                value.RunId = runId; value.RunEpoch = epoch;
                value.FenceGeneration = checked(value.FenceGeneration + 1);
                value.ManualStopped = false; value.CommandId = null; value.Requester = null;
                value.Detail = "Original admitted replacement active run";
                return value;
            });

        public FallbackLedger BindOriginalLaunch(string original, string intent, int pid, long start) =>
            Transaction(null, value =>
            {
                Require(value != null && value.Owner == "Original" && value.OwnerInstanceId == original &&
                    value.OutstandingLaunch == intent && pid > 0 && start > 0, "OriginalLaunchBindRejected");
                value.LaunchProcessId = pid; value.LaunchProcessStartUtcTicks = start;
                value.Phase = "OriginalVerifying"; return value;
            });

        public FallbackLedger CompleteOriginalLaunch(long revision, int pid, long start) =>
            Transaction(revision, value =>
            {
                Require(value != null && value.Owner == "Original" && value.Phase == "OriginalVerifying" &&
                    value.LaunchProcessId == pid && value.LaunchProcessStartUtcTicks == start, "OriginalCommitRejected");
                value.OutstandingLaunch = null; value.Phase = "Idle"; return value;
            });

        public FallbackLedger Initialize(string runId, long epoch, string instance)
        {
            if (!Guid.TryParse(runId, out _) || epoch <= 0 || string.IsNullOrEmpty(instance))
                throw new InvalidDataException("FallbackRunIdentityInvalid");
            return Transaction(null, value =>
            {
                if (value != null)
                {
                    if (value.RunId != runId || value.RunEpoch != epoch)
                        throw new InvalidDataException("FallbackRunChangedRequiresReconciliation");
                    return value;
                }
                return new FallbackLedger { SessionId = Path.GetFileNameWithoutExtension(_path).Substring(9),
                    RunId = runId, RunEpoch = epoch, OwnerInstanceId = instance };
            });
        }

        public FallbackLedger Request(long revision, string command, string requester) =>
            Transaction(revision, value =>
            {
                Require(value != null && !value.ManualStopped && value.Owner == "Original" &&
                    string.IsNullOrEmpty(value.IndependentRecoveryRequestId) &&
                    value.Phase == "Idle" && string.IsNullOrEmpty(value.OutstandingLaunch), "RequestRejected");
                Require(Guid.TryParseExact(command, "N", out _) && !string.IsNullOrEmpty(requester), "CommandIdentityInvalid");
                value.Phase = "Requested"; value.CommandId = command; value.Requester = requester;
                return value;
            });

        public FallbackLedger Yield(long revision, string originalInstance) =>
            Transaction(revision, value =>
            {
                Require(value != null && value.Owner == "Original" && value.OwnerInstanceId == originalInstance &&
                    value.Phase == "Requested" && !value.ManualStopped &&
                    string.IsNullOrEmpty(value.OutstandingLaunch), "YieldRejected");
                value.Phase = "Yielded"; return value;
            });

        public FallbackLedger Acquire(long revision, string requester, string command) =>
            Transaction(revision, value =>
            {
                Require(value != null && value.Phase == "Yielded" && value.Owner == "Original" &&
                    value.Requester == requester && value.CommandId == command && !value.ManualStopped &&
                    string.IsNullOrEmpty(value.OutstandingLaunch), "AcquireRejected");
                value.Owner = "Fallback"; value.OwnerInstanceId = requester;
                value.FenceGeneration = checked(value.FenceGeneration + 1); value.Phase = "Acquired";
                return value;
            });

        public FallbackLedger PrepareLaunch(long generation, string owner, string command) =>
            Transaction(null, value =>
            {
                RequireCurrent(value, generation, owner);
                Require(value.Phase == "Acquired" && value.CommandId == command &&
                    string.IsNullOrEmpty(value.OutstandingLaunch), "LaunchAlreadyPreparedOrUnknown");
                value.OutstandingLaunch = command; value.Phase = "LaunchPrepared";
                return value;
            });

        public FallbackLedger BindLaunch(long generation, string owner, string command, int pid, long start) =>
            Transaction(null, value =>
            {
                RequireCurrent(value, generation, owner);
                Require(value.OutstandingLaunch == command && value.Phase == "LaunchPrepared" && pid > 0 && start > 0,
                    "LaunchBindingRejected");
                value.LaunchProcessId = pid; value.LaunchProcessStartUtcTicks = start;
                value.Phase = "Verifying"; return value;
            });

        public FallbackLedger Return(long revision, string owner, string originalInstance, bool verified) =>
            Transaction(revision, value =>
            {
                Require(value != null && value.Owner == "Fallback" && value.OwnerInstanceId == owner &&
                    ((value.Phase == "Acquired" && string.IsNullOrEmpty(value.OutstandingLaunch)) ||
                     (value.Phase == "Verifying" && verified)), "ReturnRequiresReconciliation");
                value.Owner = "Original"; value.OwnerInstanceId = originalInstance;
                value.FenceGeneration = checked(value.FenceGeneration + 1); value.Phase = "Idle";
                value.OutstandingLaunch = null; value.CommandId = null; value.Requester = null;
                return value;
            });

        public FallbackLedger Stop() => Transaction(null, value =>
        {
            Require(value != null, "StopLedgerMissing");
            value.ManualStopped = true; value.StopRevision = checked(value.StopRevision + 1);
            value.Detail = "ManualStop; unresolved launch identities retained"; return value;
        });

        public FallbackLedger CancelRequest(long revision, string requester) => Transaction(revision, value =>
        {
            Require(value != null && value.Owner == "Original" && value.Requester == requester &&
                (value.Phase == "Requested" || value.Phase == "Yielded") &&
                string.IsNullOrEmpty(value.OutstandingLaunch), "CancelRequestRejected");
            value.Phase = "Idle"; value.CommandId = null; value.Requester = null; return value;
        });

        public static void RequireCurrent(FallbackLedger value, long generation, string owner)
        {
            Require(value != null && !value.ManualStopped && value.Owner == "Fallback" &&
                value.FenceGeneration == generation && value.OwnerInstanceId == owner, "FenceRejected");
        }

        private FallbackLedger Transaction(long? revision, Func<FallbackLedger, FallbackLedger> apply)
        {
            using (var mutex = new Mutex(false, _mutex))
            {
                var held = false;
                try
                {
                    try { held = mutex.WaitOne(1000); }
                    catch (AbandonedMutexException) { held = true; throw new InvalidDataException("FallbackAbandonedMutexReconcile"); }
                    if (!held) throw new TimeoutException("FallbackLedgerBusy");
                    var old = File.Exists(_path) ? Read() : null;
                    Require(!revision.HasValue || old?.Revision == revision.Value, "RevisionConflict");
                    var next = apply(old);
                    next.Revision = checked(next.Revision + 1);
                    BoundedJson.Write(_path, next);
                    return next;
                }
                finally { if (held) mutex.ReleaseMutex(); }
            }
        }
        private static void Require(bool condition, string reason)
        { if (!condition) throw new InvalidOperationException("Fallback:" + reason); }
    }

    // Captured by Task execution context. Original delayed operations never
    // inherit a newly acquired external generation.
    public sealed class FallbackExecutionScope : IDisposable
    {
        public static readonly AsyncLocal<FallbackLedger> Current = new AsyncLocal<FallbackLedger>();
        private readonly FallbackLedger _previous;
        public FallbackExecutionScope(FallbackLedger value) { _previous = Current.Value; Current.Value = value; }
        public void Dispose() { Current.Value = _previous; }
    }

    public static class FallbackPowerBoundary
    {
        public static void ValidateCurrentProcess()
        {
            var args = Environment.GetCommandLineArgs();
            string Read(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            var session = Read("--watchdog-recover");
            if (session == null) return;
            var store = new FallbackLedgerStore(Read("--journal-directory"), session);
            if (!store.Exists) return; // baseline sessions without coordination
            var value = store.Read();
            if (value.ManualStopped) throw new InvalidOperationException("FallbackPowerManualStop");
            if (WatchdogControlMarker.IsRevoked(Read("--journal-directory"), session) ||
                (WatchdogClosingTombstoneStore.TryRead(Read("--journal-directory"), session, out var closing) &&
                 closing.RelaunchDisposition == WatchdogRelaunchDisposition.Forbidden))
                throw new InvalidOperationException("FallbackPowerStopFence");
            using (var process = Process.GetCurrentProcess())
            {
                if (!string.IsNullOrEmpty(value.OutstandingLaunch) &&
                    (value.LaunchProcessId != process.Id ||
                     value.LaunchProcessStartUtcTicks != process.StartTime.ToUniversalTime().Ticks))
                    throw new InvalidOperationException("FallbackPowerLaunchIdentityUnconfirmed");
                if (value.Owner == "Fallback" && value.Phase != "Verifying")
                    throw new InvalidOperationException("FallbackPowerGenerationUnconfirmed");
            }
        }
    }

    public static class BoundedJson
    {
        public const int MaximumBytes = 65536;
        private static void RequireFields(System.Collections.Generic.Dictionary<string, object> fields, params string[] required)
        {
            if (fields == null || required.Any(key => !fields.ContainsKey(key)))
                throw new InvalidDataException("IndependentStateFieldsMissing");
        }
        public static T Read<T>(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length > MaximumBytes) throw new InvalidDataException("FallbackFileOversize");
                var data = new byte[checked((int)stream.Length)];
                var offset = 0;
                while (offset < data.Length)
                { var count = stream.Read(data, offset, data.Length - offset); if (count == 0) throw new EndOfStreamException(); offset += count; }
                var serializer = new JavaScriptSerializer { MaxJsonLength = MaximumBytes, RecursionLimit = 12 };
                var json = new UTF8Encoding(false, true).GetString(data);
                if (typeof(T) == typeof(IndependentProjectState))
                {
                    var fields = serializer.DeserializeObject(json) as System.Collections.Generic.Dictionary<string, object>;
                    RequireFields(fields, "SchemaVersion", "Revision", "Maintenance", "SafetyCleanupPending",
                        "Intent", "Transaction", "Controller", "Ticket", "Audit");
                    if (!(fields["Maintenance"] is bool) || !(fields["SafetyCleanupPending"] is bool))
                        throw new InvalidDataException("IndependentStateFlagsInvalid");
                    if (fields["Intent"] != null)
                    {
                        var intent = fields["Intent"] as System.Collections.Generic.Dictionary<string, object>;
                        RequireFields(intent, "SchemaVersion", "Revision", "ProjectDirectory", "DatabasePath",
                            "DatabaseCreationUtcTicks", "ExecutablePath", "ConfigurationSha256", "RunId", "RunEpoch",
                            "SelectedChannels", "PausedChannels", "PermanentChannels", "CompletedChannels", "Armed",
                            "ManualStopped", "ManualPaused", "PeriodMs", "StartupBudgetMs");
                        if (!(intent["Armed"] is bool) || !(intent["ManualStopped"] is bool) || !(intent["ManualPaused"] is bool))
                            throw new InvalidDataException("IndependentIntentFlagsInvalid");
                    }
                    if (fields["Ticket"] != null)
                    {
                        var ticket = fields["Ticket"] as System.Collections.Generic.Dictionary<string, object>;
                        RequireFields(ticket, "Nonce", "RequestId", "Generation", "IntentRevision",
                            "ExecutableSha256", "WindowsSessionId", "ExpiresUtcTicks", "Revoked", "Consumer");
                        if (!(ticket["Revoked"] is bool)) throw new InvalidDataException("IndependentTicketFlagInvalid");
                    }
                }
                if (typeof(T) == typeof(FallbackLedger))
                {
                    var fields = serializer.DeserializeObject(json) as System.Collections.Generic.Dictionary<string, object>;
                    var required = new[] { "SchemaVersion", "Revision", "SessionId", "RunId", "RunEpoch", "Owner",
                        "OwnerInstanceId", "FenceGeneration", "Phase", "StopRevision", "ManualStopped", "OutstandingLaunch",
                        "LaunchProcessId", "LaunchProcessStartUtcTicks" };
                    if (fields == null || required.Any(key => !fields.ContainsKey(key)) || !(fields["ManualStopped"] is bool))
                        throw new InvalidDataException("FallbackLedgerFieldsMissing");
                }
                var result = serializer.Deserialize<T>(json);
                if (result == null) throw new InvalidDataException("FallbackJsonNull");
                if (result is FallbackLedger ledger && (ledger.SchemaVersion != 1 || ledger.Revision <= 0 ||
                    ledger.FenceGeneration <= 0 || ledger.RunEpoch <= 0 ||
                    !Guid.TryParseExact(ledger.SessionId, "N", out _) || !Guid.TryParse(ledger.RunId, out _) ||
                    string.IsNullOrEmpty(ledger.OwnerInstanceId) ||
                    !new[] { "Idle", "Requested", "Yielded", "Acquired", "LaunchPrepared", "Verifying", "OriginalLaunchPrepared", "OriginalVerifying" }.Contains(ledger.Phase) ||
                    (ledger.Owner != "Original" && ledger.Owner != "Fallback")))
                    throw new InvalidDataException("FallbackLedgerSchemaInvalid");
                return result;
            }
        }
        public static void Write<T>(string path, T value)
        {
            var data = Encoding.UTF8.GetBytes(new JavaScriptSerializer { MaxJsonLength = MaximumBytes, RecursionLimit = 12 }.Serialize(value));
            if (data.Length > MaximumBytes) throw new InvalidDataException("FallbackFileOversize");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { file.Write(data, 0, data.Length); file.Flush(true); }
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
                        break;
                    }
                    catch (IOException)
                    {
                        // A replace response can be ambiguous. Confirm the exact
                        // bytes before retrying, retaining the same transaction.
                        try
                        {
                            using (var check = new FileStream(path, FileMode.Open, FileAccess.Read,
                                       FileShare.ReadWrite | FileShare.Delete))
                            {
                                if (check.Length == data.Length)
                                {
                                    var actual = new byte[data.Length];
                                    var offset = 0;
                                    while (offset < actual.Length)
                                    { var read = check.Read(actual, offset, actual.Length - offset); if (read == 0) break; offset += read; }
                                    if (offset == data.Length && actual.SequenceEqual(data)) break;
                                }
                            }
                        }
                        catch (IOException) { }
                        if (attempt >= 4 || !File.Exists(temp)) throw;
                        Thread.Sleep(25);
                    }
                }
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
