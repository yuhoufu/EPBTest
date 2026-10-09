using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace MTTFTest.Watchdog.Protocol
{
    // This journal is an authority record, not a heartbeat. Never infer operator
    // intent from the liveness of a process or from a channel's displayed state.
    public sealed class IndependentMechanicalTarget
    {
        public int Channel { get; set; }
        public long TotalCount { get; set; }
    }

    public sealed class IndependentRunIntent
    {
        public int SchemaVersion { get; set; } = 2;
        public long Revision { get; set; }
        public string ProjectDirectory { get; set; }
        public string DatabasePath { get; set; }
        public long DatabaseCreationUtcTicks { get; set; }
        public string ExecutablePath { get; set; }
        public string ConfigurationSha256 { get; set; }
        public string RunId { get; set; }
        public long RunEpoch { get; set; }
        public int[] SelectedChannels { get; set; } = Array.Empty<int>();
        public int[] PausedChannels { get; set; } = Array.Empty<int>();
        public int[] PermanentChannels { get; set; } = Array.Empty<int>();
        public int[] CompletedChannels { get; set; } = Array.Empty<int>();
        public IndependentMechanicalTarget[] MechanicalTargets { get; set; } = Array.Empty<IndependentMechanicalTarget>();
        public bool Armed { get; set; }
        public bool ManualStopped { get; set; }
        public bool ManualPaused { get; set; }
        public long PeriodMs { get; set; }
        public long StartupBudgetMs { get; set; }

        public int[] RecoveryChannels()
        {
            Validate();
            if (!Armed || ManualStopped || ManualPaused) return Array.Empty<int>();
            return SelectedChannels.Except(PausedChannels).Except(PermanentChannels)
                .Except(CompletedChannels).OrderBy(c => c).ToArray();
        }

        public void Validate()
        {
            if (SchemaVersion != 2 || Revision <= 0 || !Guid.TryParseExact(RunId, "N", out _) ||
                RunEpoch <= 0 || !Path.IsPathRooted(ProjectDirectory ?? string.Empty) ||
                !Path.IsPathRooted(DatabasePath ?? string.Empty) || DatabaseCreationUtcTicks <= 0 ||
                !Path.IsPathRooted(ExecutablePath ?? string.Empty) ||
                string.IsNullOrEmpty(ConfigurationSha256) || ConfigurationSha256.Length != 64 ||
                !ConfigurationSha256.All(Uri.IsHexDigit) || PeriodMs <= 0 || PeriodMs > 86400000 ||
                StartupBudgetMs <= 0 || StartupBudgetMs > 86400000)
                throw new InvalidDataException("IndependentIntentInvalid");
            foreach (var channels in new[] { SelectedChannels, PausedChannels, PermanentChannels, CompletedChannels })
                if (channels == null || channels.Length > 12 || channels.Any(c => c < 1 || c > 12) ||
                    channels.Distinct().Count() != channels.Length)
                    throw new InvalidDataException("IndependentIntentChannelsInvalid");
            if (MechanicalTargets == null || MechanicalTargets.Length > 12 ||
                MechanicalTargets.Any(target => target == null || target.Channel < 1 || target.Channel > 12 || target.TotalCount <= 0) ||
                MechanicalTargets.Select(target => target.Channel).Distinct().Count() != MechanicalTargets.Length ||
                (MechanicalTargets.Length > 0 && SelectedChannels.Except(MechanicalTargets.Select(target => target.Channel)).Any()))
                throw new InvalidDataException("IndependentMechanicalTargetsInvalid");
        }
    }

    public enum IndependentRecoveryPhase
    {
        Idle, CooperativeStop, PowerOff, RetireControls, OutputsSafe,
        LaunchPending, Verifying, Verified, Cancelled, NeedsAttention
    }

    public sealed class IndependentRecoveryTransaction
    {
        public int SchemaVersion { get; set; } = 2;
        public long Revision { get; set; }
        public long Generation { get; set; }
        public string RequestId { get; set; }
        public string RunId { get; set; }
        public long RunEpoch { get; set; }
        public long IntentRevision { get; set; }
        public string ExecutorIdentity { get; set; }
        public IndependentRecoveryPhase Phase { get; set; }
        public long PhaseDeadlineUtcTicks { get; set; }
        public long LastAttemptUtcTicks { get; set; }
        public long[] AttemptsUtcTicks { get; set; } = Array.Empty<long>();
        public int[] Channels { get; set; } = Array.Empty<int>();
        public int ReplacementPid { get; set; }
        public long ReplacementStartUtcTicks { get; set; }
        public string Detail { get; set; }
        public bool OperatorStopOnly { get; set; }
        public bool IsTerminal => Phase == IndependentRecoveryPhase.Idle || Phase == IndependentRecoveryPhase.Verified ||
            Phase == IndependentRecoveryPhase.Cancelled || Phase == IndependentRecoveryPhase.NeedsAttention;

        public void Validate()
        {
            if (SchemaVersion != 2 || Revision <= 0 || Generation <= 0 || IntentRevision <= 0 ||
                !Guid.TryParseExact(RequestId, "N", out _) || !Guid.TryParseExact(RunId, "N", out _) || RunEpoch <= 0 ||
                string.IsNullOrWhiteSpace(ExecutorIdentity) || !Enum.IsDefined(typeof(IndependentRecoveryPhase), Phase) ||
                PhaseDeadlineUtcTicks <= 0 || LastAttemptUtcTicks <= 0 || AttemptsUtcTicks == null ||
                (!OperatorStopOnly && AttemptsUtcTicks.Length == 0) || AttemptsUtcTicks.Length > 3 || AttemptsUtcTicks.Any(t => t <= 0) ||
                !AttemptsUtcTicks.SequenceEqual(AttemptsUtcTicks.OrderBy(t => t)) ||
                (AttemptsUtcTicks.Length > 0 && AttemptsUtcTicks.Last() != LastAttemptUtcTicks) || Channels == null || Channels.Length == 0 ||
                Channels.Length > 12 || Channels.Any(c => c < 1 || c > 12) || Channels.Distinct().Count() != Channels.Length)
                throw new InvalidDataException("IndependentTransactionInvalid");
            if (OperatorStopOnly && (Phase == IndependentRecoveryPhase.LaunchPending || Phase == IndependentRecoveryPhase.Verifying ||
                Phase == IndependentRecoveryPhase.Verified || ReplacementPid != 0 || ReplacementStartUtcTicks != 0))
                throw new InvalidDataException("IndependentOperatorStopCannotLaunch");
            if ((Phase == IndependentRecoveryPhase.Verifying || Phase == IndependentRecoveryPhase.Verified) &&
                (ReplacementPid <= 0 || ReplacementStartUtcTicks <= 0))
                throw new InvalidDataException("IndependentReplacementIdentityMissing");
        }
    }

    public static class IndependentRecoveryTransitions
    {
        public static IndependentRecoveryTransaction BeginOperatorStop(IndependentRecoveryTransaction previous,
            IndependentRunIntent intent, string executor, long now, int[] cleanupChannels)
        {
            previous?.Validate(); intent?.Validate();
            if (intent == null || !intent.ManualStopped || intent.Armed || string.IsNullOrWhiteSpace(executor) || now <= 0 ||
                cleanupChannels == null || cleanupChannels.Length == 0 || cleanupChannels.Length > 12 ||
                cleanupChannels.Any(c => c < 1 || c > 12) || cleanupChannels.Distinct().Count() != cleanupChannels.Length ||
                previous?.LastAttemptUtcTicks > now)
                throw new InvalidOperationException("IndependentOperatorStopNotAuthorized");
            // These are restart attempts, not shutdown attempts. Preserve even
            // an exhausted budget without preventing an explicit safety stop.
            var attempts = previous?.AttemptsUtcTicks.ToArray() ?? Array.Empty<long>();
            var tx = new IndependentRecoveryTransaction
            {
                Revision = checked((previous?.Revision ?? 0) + 1), Generation = checked((previous?.Generation ?? 0) + 1),
                RequestId = Guid.NewGuid().ToString("N"), RunId = intent.RunId, RunEpoch = intent.RunEpoch,
                IntentRevision = intent.Revision, ExecutorIdentity = executor, Channels = cleanupChannels,
                Phase = IndependentRecoveryPhase.CooperativeStop, PhaseDeadlineUtcTicks = checked(now + TimeSpan.FromSeconds(25).Ticks),
                LastAttemptUtcTicks = attempts.Length == 0 ? now : attempts.Last(), AttemptsUtcTicks = attempts,
                Detail = "OperatorStopSafetyCleanupRequested", OperatorStopOnly = true
            };
            tx.Validate(); return tx;
        }

        public static IndependentRecoveryTransaction Begin(IndependentRecoveryTransaction previous,
            IndependentRunIntent intent, string executor, long now)
        {
            previous?.Validate();
            var channels = intent.RecoveryChannels();
            if (channels.Length == 0 || string.IsNullOrWhiteSpace(executor) || now <= 0)
                throw new InvalidOperationException("IndependentRecoveryNotAuthorized");
            return CreateAttempt(previous, intent, channels, executor, now);
        }

        public static IndependentRecoveryTransaction RetrySafetyCleanup(IndependentRecoveryTransaction previous,
            IndependentRunIntent intent, string executor, long now)
        {
            previous?.Validate(); intent?.Validate();
            if (previous == null || previous.OperatorStopOnly || previous.Phase != IndependentRecoveryPhase.NeedsAttention || intent == null ||
                previous.ExecutorIdentity != executor || string.IsNullOrWhiteSpace(executor) || now <= 0 ||
                previous.RunId != intent.RunId || previous.RunEpoch != intent.RunEpoch)
                throw new InvalidOperationException("IndependentCleanupRetryNotOwned");
            var channels = intent.RecoveryChannels();
            // Retain cleanup responsibility after manual revocation without
            // inventing an armed intent. Final safety commit rechecks authority.
            return CreateAttempt(previous, intent, channels.Length == 0 ? previous.Channels : channels, executor, now);
        }

        private static IndependentRecoveryTransaction CreateAttempt(IndependentRecoveryTransaction previous,
            IndependentRunIntent intent, int[] channels, string executor, long now)
        {
            if (previous != null && !previous.IsTerminal)
                throw new InvalidOperationException("IndependentRecoveryAlreadyOwned");
            var attempts = (previous?.AttemptsUtcTicks ?? Array.Empty<long>()).ToList();
            if (attempts.Any(t => t > now) || previous?.LastAttemptUtcTicks > now)
                throw new InvalidOperationException("IndependentRecoveryClockRegressed");
            attempts.RemoveAll(t => now - t >= TimeSpan.FromMinutes(30).Ticks);
            var lastRestart = previous?.OperatorStopOnly == true ? previous.AttemptsUtcTicks.LastOrDefault() : previous?.LastAttemptUtcTicks ?? 0;
            if (attempts.Count >= 3 || (lastRestart > 0 && now - lastRestart < TimeSpan.FromSeconds(60).Ticks))
                throw new InvalidOperationException("IndependentRecoveryRateLimited");
            attempts.Add(now);
            return new IndependentRecoveryTransaction
            {
                Revision = checked((previous?.Revision ?? 0) + 1),
                Generation = checked((previous?.Generation ?? 0) + 1),
                RequestId = Guid.NewGuid().ToString("N"), RunId = intent.RunId, RunEpoch = intent.RunEpoch,
                IntentRevision = intent.Revision, ExecutorIdentity = executor,
                Channels = channels, Phase = IndependentRecoveryPhase.CooperativeStop,
                PhaseDeadlineUtcTicks = checked(now + TimeSpan.FromSeconds(25).Ticks),
                LastAttemptUtcTicks = now, AttemptsUtcTicks = attempts.ToArray(), Detail = "CooperativeStopRequested"
            };
        }

        public static void RequireCurrent(IndependentRecoveryTransaction tx, IndependentRunIntent intent,
            string executor, long generation)
        {
            tx?.Validate();
            intent?.Validate();
            if (tx == null || tx.OperatorStopOnly || tx.SchemaVersion != 2 || tx.IsTerminal || tx.ExecutorIdentity != executor ||
                intent == null || tx.Generation != generation || tx.IntentRevision != intent.Revision || tx.RunId != intent.RunId ||
                tx.RunEpoch != intent.RunEpoch || !(tx.Phase == IndependentRecoveryPhase.Verifying
                    ? tx.Channels.Except(intent.CompletedChannels).SequenceEqual(intent.RecoveryChannels())
                    : tx.Channels.SequenceEqual(intent.RecoveryChannels())))
                throw new InvalidOperationException("IndependentRecoveryAuthorityChanged");
        }

        public static void Advance(IndependentRecoveryTransaction tx, IndependentRunIntent intent,
            string executor, long generation, IndependentRecoveryPhase next, long now, long budgetMs,
            string detail)
        {
            RequireCurrent(tx, intent, executor, generation);
            if (now <= 0 || budgetMs <= 0 || budgetMs > 86400000)
                throw new InvalidDataException("IndependentRecoveryDeadlineInvalid");
            var expected = tx.Phase == IndependentRecoveryPhase.CooperativeStop ? IndependentRecoveryPhase.PowerOff :
                tx.Phase == IndependentRecoveryPhase.PowerOff ? IndependentRecoveryPhase.RetireControls :
                tx.Phase == IndependentRecoveryPhase.RetireControls ? IndependentRecoveryPhase.OutputsSafe :
                tx.Phase == IndependentRecoveryPhase.OutputsSafe ? IndependentRecoveryPhase.LaunchPending :
                tx.Phase == IndependentRecoveryPhase.LaunchPending ? IndependentRecoveryPhase.Verifying :
                tx.Phase == IndependentRecoveryPhase.Verifying ? IndependentRecoveryPhase.Verified :
                IndependentRecoveryPhase.NeedsAttention;
            if (next != expected) throw new InvalidOperationException("IndependentRecoveryPhaseSkipped");
            if (next == IndependentRecoveryPhase.Verifying &&
                (tx.ReplacementPid <= 0 || tx.ReplacementStartUtcTicks <= 0))
                throw new InvalidOperationException("IndependentReplacementIdentityMissing");
            tx.Phase = next;
            tx.PhaseDeadlineUtcTicks = checked(now + TimeSpan.FromMilliseconds(budgetMs).Ticks);
            tx.Revision = checked(tx.Revision + 1);
            tx.Detail = detail ?? string.Empty;
        }
    }

    // All writers, including operator intent updates, use this same mutex.
    // The caller must re-read intent inside the transaction before any irreversible action.
    public sealed class IndependentRecoveryJournal
    {
        private readonly string _directory;
        private readonly string _mutexName;
        public IndependentRecoveryJournal(string directory)
        {
            _directory = Path.GetFullPath(directory);
            _mutexName = "Global\\MTTF-IndependentJournal-" +
                SupervisorProtocol.ComputeTextSha256(_directory.ToUpperInvariant());
        }
        public T Execute<T>(Func<T> operation)
        {
            using (var mutex = new Mutex(false, _mutexName))
            {
                var owned = false;
                try
                {
                    try { owned = mutex.WaitOne(5000); }
                    catch (AbandonedMutexException) { owned = true; }
                    if (!owned) throw new TimeoutException("IndependentJournalBusy");
                    return operation();
                }
                finally { if (owned) mutex.ReleaseMutex(); }
            }
        }
        public IndependentRunIntent ReadIntent()
        {
            var value = BoundedJson.Read<IndependentRunIntent>(Path.Combine(_directory, "independent-run-intent-v2.json"));
            value.Validate();
            return value;
        }
        public IndependentRecoveryTransaction ReadTransaction()
        {
            var path = Path.Combine(_directory, "independent-recovery-v2.json");
            if (!File.Exists(path)) return null;
            var value = BoundedJson.Read<IndependentRecoveryTransaction>(path);
            value.Validate();
            return value;
        }
        public void WriteIntent(IndependentRunIntent intent) { intent.Validate(); BoundedJson.Write(Path.Combine(_directory, "independent-run-intent-v2.json"), intent); }
        public IndependentRunIntent UpdateIntent(long expectedRevision, Func<IndependentRunIntent, IndependentRunIntent> update)
        {
            return Execute(() =>
            {
                var path = Path.Combine(_directory, "independent-run-intent-v2.json");
                var current = File.Exists(path) ? ReadIntent() : null;
                if ((current?.Revision ?? 0) != expectedRevision)
                    throw new InvalidOperationException("IndependentIntentRevisionConflict");
                var value = update(current) ?? throw new InvalidDataException("IndependentIntentUpdateEmpty");
                value.Revision = checked(expectedRevision + 1);
                WriteIntent(value);
                return value;
            });
        }

        public IndependentRecoveryTransaction Begin(string executor, long now)
        {
            return Execute(() =>
            {
                var value = IndependentRecoveryTransitions.Begin(ReadTransaction(), ReadIntent(), executor, now);
                WriteTransaction(value);
                return value;
            });
        }
        public void WriteTransaction(IndependentRecoveryTransaction tx)
        {
            tx.Validate();
            BoundedJson.Write(Path.Combine(_directory, "independent-recovery-v2.json"), tx);
        }

        // The durable record, not an in-memory copy obtained on a previous tick,
        // decides whether a worker may start. Operations must remain nonblocking.
        public IndependentRecoveryTransaction Tick(string executor, long generation, long now,
            IIndependentRecoveryOperations operations, long powerBudgetMs, long outputsBudgetMs)
        {
            return Execute(() =>
            {
                var tx = ReadTransaction();
                if (tx == null || tx.IsTerminal) return tx;
                var intent = ReadIntent();
                var engine = new IndependentRecoveryExecutor(operations, WriteTransaction,
                    powerBudgetMs, outputsBudgetMs);
                engine.Tick(tx, intent, executor, generation, now);
                return tx;
            });
        }

        public void CompareExchangeTransaction(long expectedRevision, string executor, long generation,
            IndependentRecoveryTransaction replacement)
        {
            Execute(() =>
            {
                var current = ReadTransaction();
                if (current == null || current.Revision != expectedRevision ||
                    current.ExecutorIdentity != executor || current.Generation != generation ||
                    replacement == null || replacement.ExecutorIdentity != executor ||
                    replacement.Generation != generation || replacement.RequestId != current.RequestId ||
                    replacement.Revision != checked(expectedRevision + 1))
                    throw new InvalidOperationException("IndependentTransactionRevisionConflict");
                IndependentRecoveryTransitions.RequireCurrent(current, ReadIntent(), executor, generation);
                // Phase progression belongs to Tick. This path only persists a
                // newly observed worker/process identity within the same stage.
                if (replacement.Phase != current.Phase || replacement.IntentRevision != current.IntentRevision ||
                    replacement.RunId != current.RunId || replacement.RunEpoch != current.RunEpoch ||
                    replacement.PhaseDeadlineUtcTicks != current.PhaseDeadlineUtcTicks ||
                    (current.ReplacementPid > 0 && (replacement.ReplacementPid != current.ReplacementPid ||
                        replacement.ReplacementStartUtcTicks != current.ReplacementStartUtcTicks)) ||
                    !replacement.Channels.SequenceEqual(current.Channels) ||
                    !replacement.AttemptsUtcTicks.SequenceEqual(current.AttemptsUtcTicks))
                    throw new InvalidOperationException("IndependentTransactionStageChanged");
                WriteTransaction(replacement);
                return true;
            });
        }
    }
}
