using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MTTFTest.Watchdog.Client
{
    public enum WatchdogRuntimeCallbackLane
    {
        Safety = 0,
        Diagnostic = 1
    }

    public enum WatchdogRuntimeCallbackOfferStatus
    {
        Accepted = 0,
        Coalesced = 1,
        RejectedClosed = 2,
        RejectedSaturated = 3,
        RejectedInvalid = 4
    }

    public enum WatchdogCallbackCompletionStatus
    {
        Posted = 0,
        Completed = 1,
        HandlerFaulted = 2,
        Canceled = 3,
        PostRejected = 4
    }

    public sealed class WatchdogRuntimeCallbackIdentity
    {
        public string SessionId { get; }
        public long SessionGeneration { get; }
        public long SessionLease { get; }
        public long AuthorityGeneration { get; }
        public long ScopeGeneration { get; }
        public Guid ScopeId { get; }

        public WatchdogRuntimeCallbackIdentity(
            string sessionId,
            long sessionGeneration,
            long sessionLease,
            long authorityGeneration,
            long scopeGeneration,
            Guid scopeId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("SessionId不能为空。", nameof(sessionId));
            if (sessionLease <= 0) throw new ArgumentOutOfRangeException(nameof(sessionLease));
            if (scopeGeneration <= 0) throw new ArgumentOutOfRangeException(nameof(scopeGeneration));
            if (scopeId == Guid.Empty) throw new ArgumentException("ScopeId不能为空。", nameof(scopeId));
            SessionId = sessionId;
            SessionGeneration = sessionGeneration;
            SessionLease = sessionLease;
            AuthorityGeneration = authorityGeneration;
            ScopeGeneration = scopeGeneration;
            ScopeId = scopeId;
        }

        public string ExactKey => string.Join("|", new[]
        {
            SessionId,
            SessionGeneration.ToString(),
            SessionLease.ToString(),
            AuthorityGeneration.ToString(),
            ScopeGeneration.ToString(),
            ScopeId.ToString("N")
        });

        public override string ToString() => ExactKey;
    }

    public sealed class WatchdogRuntimeCallbackOffer
    {
        public WatchdogRuntimeCallbackIdentity Identity { get; }
        public WatchdogRuntimeCallbackLane Lane { get; }
        public string Code { get; }
        public string EventType => Code;
        public string Reason { get; }
        public string Detail { get; }
        public string CoalesceKey { get; }
        /// <summary>
        /// Native asynchronous callback.  The post target receives the task
        /// returned by this delegate and its completion is therefore the
        /// handler completion, not merely the scheduler-post completion.
        /// </summary>
        public Func<Task> CallbackAsync { get; }

        /// <summary>
        /// Source-compatible synchronous callback view.  New production code
        /// must use <see cref="CallbackAsync"/>; this property exists only
        /// for older callers while they migrate to the task-shaped contract.
        /// </summary>
        public Action Callback { get; }

        public WatchdogRuntimeCallbackOffer(
            WatchdogRuntimeCallbackIdentity identity,
            WatchdogRuntimeCallbackLane lane,
            string code,
            Func<Task> callback,
            string detail = null,
            string coalesceKey = null,
            string reason = null)
        {
            Identity = identity;
            Lane = lane;
            Code = code ?? string.Empty;
            Reason = reason ?? string.Empty;
            Detail = detail ?? string.Empty;
            CoalesceKey = coalesceKey ?? string.Empty;
            CallbackAsync = callback;
            Callback = null;
        }

        [Obsolete("Use the Func<Task> constructor for asynchronous delivery.")]
        public WatchdogRuntimeCallbackOffer(
            WatchdogRuntimeCallbackIdentity identity,
            WatchdogRuntimeCallbackLane lane,
            string code,
            Action callback,
            string detail = null,
            string coalesceKey = null,
            string reason = null)
            : this(identity, lane, code,
                callback == null ? (Func<Task>)null : WrapCompatibilityCallback(callback),
                detail, coalesceKey, reason)
        {
            Callback = callback;
        }

        public WatchdogRuntimeCallbackOffer(
            WatchdogRuntimeCallbackIdentity identity,
            WatchdogRuntimeCallbackLane lane,
            string code,
            string reason,
            string detail,
            Func<Task> callback)
            : this(identity, lane, code, callback, detail, null, reason)
        {
        }

        [Obsolete("Use the Func<Task> constructor for asynchronous delivery.")]
        public WatchdogRuntimeCallbackOffer(
            WatchdogRuntimeCallbackIdentity identity,
            WatchdogRuntimeCallbackLane lane,
            string code,
            string reason,
            string detail,
            Action callback)
            : this(identity, lane, code, callback, detail, null, reason)
        {
        }

        private static Func<Task> WrapCompatibilityCallback(Action callback)
        {
            return () =>
            {
                callback();
                return Task.CompletedTask;
            };
        }
    }

    public interface IWatchdogCallbackPostTarget
    {
        WatchdogPostReceipt TryPost(Func<Task> callback);
    }

    public sealed class WatchdogPostReceipt
    {
        public bool Accepted { get; }
        public Task Completion { get; }
        public string Reason { get; }
        public long Sequence { get; }

        public WatchdogPostReceipt(bool accepted, Task completion, string reason, long sequence = 0)
        {
            Accepted = accepted;
            Completion = completion ?? Task.CompletedTask;
            Reason = reason ?? string.Empty;
            Sequence = sequence;
        }

        public static WatchdogPostReceipt Rejected(string reason) =>
            new WatchdogPostReceipt(false, Task.CompletedTask, reason);
    }

    /// <summary>
    /// The task returned to an accepted offer completes once that offer has a
    /// terminal outcome.  Posted is retained as the post-stage value through
    /// PostedUtcTicks/WasPosted; Status is always terminal when the task runs.
    /// </summary>
    public sealed class WatchdogCallbackCompletionReceipt
    {
        public WatchdogCallbackCompletionStatus Status { get; }
        public WatchdogCallbackCompletionStatus CompletionStatus => Status;
        public bool Accepted { get; }
        public bool WasPosted { get; }
        public long Sequence { get; }
        public long AdmissionSequence { get; }
        public long CanonicalSequence { get; }
        public WatchdogRuntimeCallbackLane Lane { get; }
        public string Reason { get; }
        public string FailureKind => Status == WatchdogCallbackCompletionStatus.HandlerFaulted
            ? "HandlerFaulted"
            : Status == WatchdogCallbackCompletionStatus.Canceled
                ? "Canceled"
                : Status == WatchdogCallbackCompletionStatus.PostRejected
                    ? "PostRejected"
                    : string.Empty;
        public string ExceptionType { get; }
        public long PostedUtcTicks { get; }
        public long CompletedUtcTicks { get; }
        public long ElapsedMilliseconds { get; }

        internal WatchdogCallbackCompletionReceipt(
            WatchdogCallbackCompletionStatus status,
            bool accepted,
            bool wasPosted,
            long admissionSequence,
            long canonicalSequence,
            WatchdogRuntimeCallbackLane lane,
            string reason,
            string exceptionType,
            long postedUtcTicks,
            long completedUtcTicks,
            long elapsedMilliseconds)
        {
            Status = status;
            Accepted = accepted;
            WasPosted = wasPosted;
            Sequence = admissionSequence;
            AdmissionSequence = admissionSequence;
            CanonicalSequence = canonicalSequence;
            Lane = lane;
            Reason = reason ?? string.Empty;
            ExceptionType = exceptionType ?? string.Empty;
            PostedUtcTicks = postedUtcTicks;
            CompletedUtcTicks = completedUtcTicks;
            ElapsedMilliseconds = elapsedMilliseconds;
        }

        internal static WatchdogCallbackCompletionReceipt Rejected(
            WatchdogRuntimeCallbackOffer offer,
            long admissionSequence,
            long canonicalSequence,
            string reason)
        {
            return Rejected(offer,
                offer == null ? WatchdogRuntimeCallbackLane.Safety : offer.Lane,
                admissionSequence, canonicalSequence, reason);
        }

        internal static WatchdogCallbackCompletionReceipt Rejected(
            WatchdogRuntimeCallbackOffer offer,
            WatchdogRuntimeCallbackLane lane,
            long admissionSequence,
            long canonicalSequence,
            string reason)
        {
            var now = DateTime.UtcNow.Ticks;
            return new WatchdogCallbackCompletionReceipt(
                WatchdogCallbackCompletionStatus.PostRejected,
                false,
                false,
                admissionSequence,
                canonicalSequence,
                lane,
                reason,
                string.Empty,
                0,
                now,
                0);
        }

        internal WatchdogCallbackCompletionReceipt WithSequences(
            long admissionSequence,
            long canonicalSequence)
        {
            return new WatchdogCallbackCompletionReceipt(
                Status,
                Accepted,
                WasPosted,
                admissionSequence,
                canonicalSequence,
                Lane,
                Reason,
                ExceptionType,
                PostedUtcTicks,
                CompletedUtcTicks,
                ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// New immutable admission surface.  The legacy OfferResult derives from
    /// this type only as a source-compatible name; no Coordinator API depends
    /// on it.
    /// </summary>
    public class WatchdogCallbackAdmission
    {
        public WatchdogRuntimeCallbackOfferStatus Status { get; }
        public long Sequence => AdmissionSequence;
        public long AdmissionSequence { get; }
        public long CanonicalSequence { get; }
        public Task<WatchdogCallbackCompletionReceipt> Completion { get; }
        public Task<WatchdogCallbackCompletionReceipt> CompletionReceipt => Completion;
        public bool Accepted => Status == WatchdogRuntimeCallbackOfferStatus.Accepted ||
                                 Status == WatchdogRuntimeCallbackOfferStatus.Coalesced;
        public bool Coalesced => Status == WatchdogRuntimeCallbackOfferStatus.Coalesced;
        public string RejectionReason { get; }
        public int SafetyQueueCount { get; }
        public int DiagnosticQueueCount { get; }
        public int InFlightCount { get; }
        public long DroppedCount { get; }

        protected WatchdogCallbackAdmission(
            WatchdogRuntimeCallbackOfferStatus status,
            long admissionSequence,
            long canonicalSequence,
            Task<WatchdogCallbackCompletionReceipt> completion,
            string rejectionReason,
            int safetyQueueCount,
            int diagnosticQueueCount,
            int inFlightCount,
            long droppedCount)
        {
            Status = status;
            AdmissionSequence = admissionSequence;
            CanonicalSequence = canonicalSequence;
            Completion = completion ?? Task.FromResult<WatchdogCallbackCompletionReceipt>(null);
            RejectionReason = rejectionReason ?? string.Empty;
            SafetyQueueCount = safetyQueueCount;
            DiagnosticQueueCount = diagnosticQueueCount;
            InFlightCount = inFlightCount;
            DroppedCount = droppedCount;
        }
    }

    public sealed class WatchdogRuntimeCallbackOfferResult : WatchdogCallbackAdmission
    {
        internal WatchdogRuntimeCallbackOfferResult(
            WatchdogRuntimeCallbackOfferStatus status,
            long admissionSequence,
            long canonicalSequence,
            Task<WatchdogCallbackCompletionReceipt> completion,
            string rejectionReason,
            int safetyQueueCount,
            int diagnosticQueueCount,
            int inFlightCount,
            long droppedCount)
            : base(status, admissionSequence, canonicalSequence, completion, rejectionReason,
                   safetyQueueCount, diagnosticQueueCount, inFlightCount, droppedCount)
        {
        }
    }

    public sealed class WatchdogRuntimeCallbackDispatchOptions
    {
        public Action<WatchdogRuntimeCallbackAudit> AuditSink { get; set; }
        public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public IWatchdogCallbackPostTarget SafetyPostTarget { get; set; }
        public IWatchdogCallbackPostTarget DiagnosticPostTarget { get; set; }
        public int SlowCallbackMilliseconds { get; set; } = 1000;
    }

    public sealed class WatchdogRuntimeCallbackAudit
    {
        public string Reason { get; }
        public long Count { get; }
        public WatchdogRuntimeCallbackLane Lane { get; }
        public long Sequence { get; }
        public string Detail { get; }

        internal WatchdogRuntimeCallbackAudit(
            string reason,
            long count,
            WatchdogRuntimeCallbackLane lane,
            long sequence,
            string detail)
        {
            Reason = reason ?? string.Empty;
            Count = count;
            Lane = lane;
            Sequence = sequence;
            Detail = detail ?? string.Empty;
        }
    }

    /// <summary>
    /// Production scheduler target.  Once scheduling succeeds it always
    /// returns Accepted; a concurrent Close only rejects later posts.
    /// </summary>
    public sealed class WatchdogThreadPoolPostTarget : IWatchdogCallbackPostTarget, IDisposable
    {
        private int _closed;
        private long _sequence;

        public WatchdogPostReceipt TryPost(Func<Task> callback)
        {
            if (callback == null) return WatchdogPostReceipt.Rejected("CallbackMissing");
            if (Volatile.Read(ref _closed) != 0)
                return WatchdogPostReceipt.Rejected("PostTargetClosed");
            try
            {
                var sequence = Interlocked.Increment(ref _sequence);
                var completion = Task.Factory.StartNew(
                    async () => await callback().ConfigureAwait(false),
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default).Unwrap();
                return new WatchdogPostReceipt(true, completion, string.Empty, sequence);
            }
            catch (Exception ex)
            {
                return WatchdogPostReceipt.Rejected("PostInfrastructureFault:" + ex.GetType().Name);
            }
        }

        public void Dispose() => Interlocked.Exchange(ref _closed, 1);
    }

    public sealed class WatchdogRuntimeCallbackDrainReceipt
    {
        public bool AcceptingClosed { get; }
        public bool IsTerminal { get; }
        public bool TimedOut { get; }
        public bool AllWorkersTerminal { get; }
        public bool AllObserversTerminal { get; }
        public bool AllResourcesReleased { get; }
        public TimeSpan DrainBudget { get; }
        public TimeSpan Elapsed { get; }
        public long CompletedUtcTicks { get; }

        public long SafetyAccepted { get; }
        public long SafetyRejected { get; }
        public long SafetyExecuted { get; }
        public long SafetySkipped { get; }
        public long SafetyCallbackFailures { get; }
        public long SafetyPostAccepted { get; }
        public long SafetyPostRejected { get; }
        public long SafetyPostFaults { get; }
        public long SafetyHandlerFaults { get; }
        public long SafetyPostCanceled { get; }
        public long SafetyPostSlow { get; }
        public long SafetyCompletionPosted { get; }
        public long SafetyCompletionCompleted { get; }

        public long DiagnosticAccepted { get; }
        public long DiagnosticRejected { get; }
        public long DiagnosticCoalesced { get; }
        public long DiagnosticDropped { get; }
        public long DiagnosticExecuted { get; }
        public long DiagnosticCallbackFailures { get; }
        public long DiagnosticPostAccepted { get; }
        public long DiagnosticPostRejected { get; }
        public long DiagnosticPostFaults { get; }
        public long DiagnosticHandlerFaults { get; }
        public long DiagnosticPostCanceled { get; }
        public long DiagnosticPostSlow { get; }
        public long DiagnosticCompletionPosted { get; }
        public long DiagnosticCompletionCompleted { get; }

        public long SafetyPostFaultCount => SafetyPostFaults;
        public long SafetyHandlerFaultCount => SafetyHandlerFaults;
        public long SafetyPostCanceledCount => SafetyPostCanceled;
        public long SafetyPostSlowCount => SafetyPostSlow;
        public long DiagnosticPostFaultCount => DiagnosticPostFaults;
        public long DiagnosticHandlerFaultCount => DiagnosticHandlerFaults;
        public long DiagnosticPostCanceledCount => DiagnosticPostCanceled;
        public long DiagnosticPostSlowCount => DiagnosticPostSlow;

        public int SafetyQueueRemaining { get; }
        public int SafetyPostingRemaining { get; }
        public int SafetyActiveRemaining { get; }
        public int SafetyInFlightRemaining => SafetyPostingRemaining + SafetyActiveRemaining;
        public int DiagnosticQueueRemaining { get; }
        public int DiagnosticPostingRemaining { get; }
        public int DiagnosticActiveRemaining { get; }
        public int DiagnosticInFlightRemaining => DiagnosticPostingRemaining + DiagnosticActiveRemaining;
        public int InFlightRemaining => SafetyInFlightRemaining + DiagnosticInFlightRemaining;
        public int SafetyWorkerThreadId { get; }
        public int DiagnosticWorkerThreadId { get; }
        public ThreadPriority SafetyWorkerPriority { get; }
        public ThreadPriority DiagnosticWorkerPriority { get; }

        public long Sequence { get; }
        public long WorkerExceptionCount { get; }
        public long AuditFailureCount { get; }
        public long SpaceAvailableFailureCount { get; }
        public string LastFailureKind { get; }
        public string LastFailureReason { get; }
        public long LastFailureSequence { get; }
        public string LastSpaceAvailableFailure { get; }
        public string LastAuditFailure { get; }

        internal WatchdogRuntimeCallbackDrainReceipt(
            bool acceptingClosed,
            bool terminal,
            bool timedOut,
            bool workersTerminal,
            bool observersTerminal,
            bool resourcesReleased,
            TimeSpan budget,
            TimeSpan elapsed,
            long safetyAccepted,
            long safetyRejected,
            long safetyExecuted,
            long safetySkipped,
            long safetyCallbackFailures,
            long safetyPostAccepted,
            long safetyPostRejected,
            long safetyPostFaults,
            long safetyHandlerFaults,
            long safetyPostCanceled,
            long safetyPostSlow,
            long safetyCompletionPosted,
            long safetyCompletionCompleted,
            long diagnosticAccepted,
            long diagnosticRejected,
            long diagnosticCoalesced,
            long diagnosticDropped,
            long diagnosticExecuted,
            long diagnosticCallbackFailures,
            long diagnosticPostAccepted,
            long diagnosticPostRejected,
            long diagnosticPostFaults,
            long diagnosticHandlerFaults,
            long diagnosticPostCanceled,
            long diagnosticPostSlow,
            long diagnosticCompletionPosted,
            long diagnosticCompletionCompleted,
            int safetyQueue,
            int safetyPosting,
            int safetyActive,
            int diagnosticQueue,
            int diagnosticPosting,
            int diagnosticActive,
            int safetyThread,
            int diagnosticThread,
            ThreadPriority safetyPriority,
            ThreadPriority diagnosticPriority,
            long sequence,
            long workerExceptions,
            long auditFailures,
            long spaceFailures,
            string lastFailureKind,
            string lastFailureReason,
            long lastFailureSequence,
            string lastSpaceFailure,
            string lastAuditFailure,
            long completedUtcTicks)
        {
            AcceptingClosed = acceptingClosed;
            IsTerminal = terminal;
            TimedOut = timedOut;
            AllWorkersTerminal = workersTerminal;
            AllObserversTerminal = observersTerminal;
            AllResourcesReleased = resourcesReleased;
            DrainBudget = budget;
            Elapsed = elapsed;
            SafetyAccepted = safetyAccepted;
            SafetyRejected = safetyRejected;
            SafetyExecuted = safetyExecuted;
            SafetySkipped = safetySkipped;
            SafetyCallbackFailures = safetyCallbackFailures;
            SafetyPostAccepted = safetyPostAccepted;
            SafetyPostRejected = safetyPostRejected;
            SafetyPostFaults = safetyPostFaults;
            SafetyHandlerFaults = safetyHandlerFaults;
            SafetyPostCanceled = safetyPostCanceled;
            SafetyPostSlow = safetyPostSlow;
            SafetyCompletionPosted = safetyCompletionPosted;
            SafetyCompletionCompleted = safetyCompletionCompleted;
            DiagnosticAccepted = diagnosticAccepted;
            DiagnosticRejected = diagnosticRejected;
            DiagnosticCoalesced = diagnosticCoalesced;
            DiagnosticDropped = diagnosticDropped;
            DiagnosticExecuted = diagnosticExecuted;
            DiagnosticCallbackFailures = diagnosticCallbackFailures;
            DiagnosticPostAccepted = diagnosticPostAccepted;
            DiagnosticPostRejected = diagnosticPostRejected;
            DiagnosticPostFaults = diagnosticPostFaults;
            DiagnosticHandlerFaults = diagnosticHandlerFaults;
            DiagnosticPostCanceled = diagnosticPostCanceled;
            DiagnosticPostSlow = diagnosticPostSlow;
            DiagnosticCompletionPosted = diagnosticCompletionPosted;
            DiagnosticCompletionCompleted = diagnosticCompletionCompleted;
            SafetyQueueRemaining = safetyQueue;
            SafetyPostingRemaining = safetyPosting;
            SafetyActiveRemaining = safetyActive;
            DiagnosticQueueRemaining = diagnosticQueue;
            DiagnosticPostingRemaining = diagnosticPosting;
            DiagnosticActiveRemaining = diagnosticActive;
            SafetyWorkerThreadId = safetyThread;
            DiagnosticWorkerThreadId = diagnosticThread;
            SafetyWorkerPriority = safetyPriority;
            DiagnosticWorkerPriority = diagnosticPriority;
            Sequence = sequence;
            WorkerExceptionCount = workerExceptions;
            AuditFailureCount = auditFailures;
            SpaceAvailableFailureCount = spaceFailures;
            LastFailureKind = lastFailureKind ?? string.Empty;
            LastFailureReason = lastFailureReason ?? string.Empty;
            LastFailureSequence = lastFailureSequence;
            LastSpaceAvailableFailure = lastSpaceFailure ?? string.Empty;
            LastAuditFailure = lastAuditFailure ?? string.Empty;
            CompletedUtcTicks = completedUtcTicks;
        }
    }

    /// <summary>
    /// Exactly two execution cores.  The workers only call post targets and
    /// observe completion tasks; handler code runs on the target scheduler.
    /// </summary>
    public sealed class WatchdogRuntimeCallbackDispatch : IDisposable
    {
        public const int SafetyCapacity = 16;
        public const int DiagnosticCapacity = 256;
        public const int DiagnosticMaxInflight = 32;

        private sealed class WorkItem
        {
            internal long Sequence;
            internal long CanonicalSequence;
            internal WatchdogRuntimeCallbackOffer Offer;
            internal Func<Task> Callback;
            internal string Key;
            internal TaskCompletionSource<WatchdogCallbackCompletionReceipt> Completion;
        }

        private sealed class Pending
        {
            internal WorkItem Item;
            internal Task Completion;
            internal long PostedUtcTicks;
            internal Stopwatch Stopwatch;
        }

        private sealed class FailureSnapshot
        {
            internal readonly string Kind;
            internal readonly string Reason;
            internal readonly long Sequence;

            internal FailureSnapshot(string kind, string reason, long sequence)
            {
                Kind = kind ?? string.Empty;
                Reason = reason ?? string.Empty;
                Sequence = sequence;
            }
        }

        private readonly object _safetyGate = new object();
        private readonly object _diagnosticGate = new object();
        private readonly object _observerGate = new object();
        private readonly Queue<WorkItem> _safetyQueue = new Queue<WorkItem>();
        private readonly Queue<WorkItem> _diagnosticQueue = new Queue<WorkItem>();
        private readonly List<Pending> _safetyPosting = new List<Pending>();
        private readonly List<Pending> _safetyActive = new List<Pending>();
        private readonly List<Pending> _diagnosticPosting = new List<Pending>();
        private readonly List<Pending> _diagnosticActive = new List<Pending>();
        private readonly Dictionary<string, WorkItem> _diagnosticKeys =
            new Dictionary<string, WorkItem>(StringComparer.Ordinal);
        private readonly List<Task> _observerTasks = new List<Task>();
        private readonly AutoResetEvent _safetyWake = new AutoResetEvent(false);
        private readonly AutoResetEvent _diagnosticWake = new AutoResetEvent(false);
        private readonly WatchdogRuntimeCallbackDispatchOptions _options;
        private readonly IWatchdogCallbackPostTarget _safetyTarget;
        private readonly IWatchdogCallbackPostTarget _diagnosticTarget;
        private readonly bool _ownsSafetyTarget;
        private readonly bool _ownsDiagnosticTarget;
        private readonly Thread _safetyWorker;
        private readonly Thread _diagnosticWorker;
        private readonly ThreadPriority _safetyPriority;
        private readonly ThreadPriority _diagnosticPriority;
        private readonly object _offerProducerGate = new object();
        private long _sequence;
        private int _activeOfferProducers;
        private int _safetyQueueCount;
        private int _safetyPostingCount;
        private int _safetyActiveCount;
        private int _diagnosticQueueCount;
        private int _diagnosticPostingCount;
        private int _diagnosticActiveCount;
        private long _safetyAccepted;
        private long _safetyRejected;
        private long _safetyExecuted;
        private long _safetySkipped;
        private long _safetyCallbackFailures;
        private long _safetyPostAccepted;
        private long _safetyPostRejected;
        private long _safetyPostFaults;
        private long _safetyHandlerFaults;
        private long _safetyPostCanceled;
        private long _safetyPostSlow;
        private long _safetyCompletionPosted;
        private long _safetyCompletionCompleted;
        private long _diagnosticAccepted;
        private long _diagnosticRejected;
        private long _diagnosticCoalesced;
        private long _diagnosticDropped;
        private long _diagnosticExecuted;
        private long _diagnosticCallbackFailures;
        private long _diagnosticPostAccepted;
        private long _diagnosticPostRejected;
        private long _diagnosticPostFaults;
        private long _diagnosticHandlerFaults;
        private long _diagnosticPostCanceled;
        private long _diagnosticPostSlow;
        private long _diagnosticCompletionPosted;
        private long _diagnosticCompletionCompleted;
        private long _workerExceptionCount;
        private long _auditFailureCount;
        private long _spaceAvailableFailureCount;
        private string _lastSpaceAvailableFailure = string.Empty;
        private string _lastAuditFailure = string.Empty;
        private FailureSnapshot _lastFailure;
        private int _closing;
        private int _disposeRequested;
        private int _safetyExited;
        private int _diagnosticExited;
        private int _resourcesReleased;
        private int _resourcesFinalizing;
        private WatchdogRuntimeCallbackDrainReceipt _terminalReceipt;

        public event Action SpaceAvailable;
        public event Action<WatchdogRuntimeCallbackAudit> Audit;

        public WatchdogRuntimeCallbackDispatch(WatchdogRuntimeCallbackDispatchOptions options = null)
        {
            _options = options ?? new WatchdogRuntimeCallbackDispatchOptions();
            if (_options.SlowCallbackMilliseconds < 0) _options.SlowCallbackMilliseconds = 0;
            if (_options.SafetyPostTarget == null)
            {
                _safetyTarget = new WatchdogThreadPoolPostTarget();
                _ownsSafetyTarget = true;
            }
            else _safetyTarget = _options.SafetyPostTarget;
            if (_options.DiagnosticPostTarget == null)
            {
                _diagnosticTarget = new WatchdogThreadPoolPostTarget();
                _ownsDiagnosticTarget = true;
            }
            else _diagnosticTarget = _options.DiagnosticPostTarget;

            _safetyWorker = new Thread(SafetyWorkerLoop)
            {
                IsBackground = true,
                Name = "Watchdog.Callback.Safety",
                Priority = ThreadPriority.AboveNormal
            };
            _diagnosticWorker = new Thread(DiagnosticWorkerLoop)
            {
                IsBackground = true,
                Name = "Watchdog.Callback.Diagnostic",
                Priority = ThreadPriority.BelowNormal
            };
            _safetyPriority = _safetyWorker.Priority;
            _diagnosticPriority = _diagnosticWorker.Priority;
            _safetyWorker.Start();
            _diagnosticWorker.Start();
        }

        public bool IsClosing => Volatile.Read(ref _closing) != 0;
        public bool IsWorkerAlive => _safetyWorker.IsAlive || _diagnosticWorker.IsAlive;
        public int SafetyWorkerThreadId => _safetyWorker.ManagedThreadId;
        public int DiagnosticWorkerThreadId => _diagnosticWorker.ManagedThreadId;
        public ThreadPriority SafetyWorkerPriority => _safetyPriority;
        public ThreadPriority DiagnosticWorkerPriority => _diagnosticPriority;
        public int SafetyQueueCount => Volatile.Read(ref _safetyQueueCount);
        public int SafetyPostingCount => Volatile.Read(ref _safetyPostingCount);
        public int SafetyActiveCount => Volatile.Read(ref _safetyActiveCount);
        public int DiagnosticQueueCount => Volatile.Read(ref _diagnosticQueueCount);
        public int DiagnosticPostingCount => Volatile.Read(ref _diagnosticPostingCount);
        public int DiagnosticActiveCount => Volatile.Read(ref _diagnosticActiveCount);
        public int InFlightCount =>
            Volatile.Read(ref _safetyPostingCount) + Volatile.Read(ref _safetyActiveCount) +
            Volatile.Read(ref _diagnosticPostingCount) + Volatile.Read(ref _diagnosticActiveCount);
        public long DiagnosticDroppedCount => Interlocked.Read(ref _diagnosticDropped);
        public long DiagnosticCoalescedCount => Interlocked.Read(ref _diagnosticCoalesced);
        public long DiagnosticRejectedCount => Interlocked.Read(ref _diagnosticRejected);
        public int ActiveOfferProducerCount => Volatile.Read(ref _activeOfferProducers);
        public long SpaceAvailableFailureCount => Interlocked.Read(ref _spaceAvailableFailureCount);
        public long AuditFailureCount => Interlocked.Read(ref _auditFailureCount);
        public string LastSpaceAvailableFailure => Volatile.Read(ref _lastSpaceAvailableFailure) ?? string.Empty;
        public string LastAuditFailure => Volatile.Read(ref _lastAuditFailure) ?? string.Empty;

        private bool TryBeginOfferProducer(out string reason)
        {
            lock (_offerProducerGate)
            {
                if (Volatile.Read(ref _closing) != 0)
                {
                    reason = "DispatcherClosing";
                    return false;
                }
                _activeOfferProducers++;
                reason = string.Empty;
                return true;
            }
        }

        private void EndOfferProducer()
        {
            lock (_offerProducerGate)
            {
                if (_activeOfferProducers > 0) _activeOfferProducers--;
                if (_activeOfferProducers == 0) Monitor.PulseAll(_offerProducerGate);
            }
        }

        private bool CloseAndWaitOfferProducers(TimeSpan budget, Stopwatch elapsed)
        {
            lock (_offerProducerGate)
            {
                Interlocked.Exchange(ref _closing, 1);
                while (_activeOfferProducers != 0)
                {
                    var remaining = budget - elapsed.Elapsed;
                    if (remaining <= TimeSpan.Zero) return false;
                    var milliseconds = (int)Math.Min(int.MaxValue,
                        Math.Max(1, remaining.TotalMilliseconds));
                    Monitor.Wait(_offerProducerGate, milliseconds);
                }
                return true;
            }
        }

        public WatchdogRuntimeCallbackOfferResult OfferSafety(WatchdogRuntimeCallbackOffer offer)
        {
            string reason;
            if (!TryBeginOfferProducer(out reason))
                return Rejected(offer, WatchdogRuntimeCallbackLane.Safety,
                    WatchdogRuntimeCallbackOfferStatus.RejectedClosed, 0, reason);
            try
            {
                return EnqueueSafety(offer);
            }
            finally
            {
                EndOfferProducer();
            }
        }

        public WatchdogRuntimeCallbackOfferResult OfferDiagnostic(WatchdogRuntimeCallbackOffer offer)
        {
            string producerReason;
            if (!TryBeginOfferProducer(out producerReason))
                return Rejected(offer, WatchdogRuntimeCallbackLane.Diagnostic,
                    WatchdogRuntimeCallbackOfferStatus.RejectedClosed, 0, producerReason);
            try
            {
                return OfferDiagnosticCore(offer);
            }
            finally
            {
                EndOfferProducer();
            }
        }

        private WatchdogRuntimeCallbackOfferResult OfferDiagnosticCore(WatchdogRuntimeCallbackOffer offer)
        {
            if (!IsValid(offer) || offer.Lane != WatchdogRuntimeCallbackLane.Diagnostic)
                return Rejected(offer, WatchdogRuntimeCallbackLane.Diagnostic,
                    WatchdogRuntimeCallbackOfferStatus.RejectedInvalid, 0, "InvalidOffer");
            var sequence = Interlocked.Increment(ref _sequence);
            var key = BuildDiagnosticKey(offer);
            var auditCount = 0L;
            WatchdogRuntimeCallbackOfferResult result;
            if (!Monitor.TryEnter(_diagnosticGate, 0))
                return Rejected(offer, WatchdogRuntimeCallbackLane.Diagnostic,
                    WatchdogRuntimeCallbackOfferStatus.RejectedSaturated,
                    sequence, "DiagnosticGateBusy");
            try
            {
                if (Volatile.Read(ref _closing) != 0)
                    return RejectedLocked(offer, WatchdogRuntimeCallbackLane.Diagnostic,
                        WatchdogRuntimeCallbackOfferStatus.RejectedClosed,
                        sequence, "DispatcherClosing");
                WorkItem existing;
                if (_diagnosticKeys.TryGetValue(key, out existing))
                {
                    Interlocked.Increment(ref _diagnosticCoalesced);
                    return BuildAdmissionLocked(
                        WatchdogRuntimeCallbackOfferStatus.Coalesced,
                        sequence,
                        DeriveCompletion(existing, sequence),
                        existing.CanonicalSequence,
                        string.Empty);
                }
                if (_diagnosticQueue.Count >= DiagnosticCapacity)
                {
                    auditCount = Interlocked.Increment(ref _diagnosticDropped);
                    Interlocked.Increment(ref _diagnosticRejected);
                    result = BuildAdmissionLocked(
                        WatchdogRuntimeCallbackOfferStatus.RejectedSaturated,
                        sequence,
                        Task.FromResult(WatchdogCallbackCompletionReceipt.Rejected(
                            offer, WatchdogRuntimeCallbackLane.Diagnostic,
                            sequence, sequence, "DiagnosticQueueFull")),
                        sequence,
                        "DiagnosticQueueFull");
                }
                else
                {
                    var item = CreateWorkItem(offer, sequence, key);
                    _diagnosticQueue.Enqueue(item);
                    Interlocked.Increment(ref _diagnosticQueueCount);
                    _diagnosticKeys.Add(key, item);
                    Interlocked.Increment(ref _diagnosticAccepted);
                    result = BuildAdmissionLocked(
                        WatchdogRuntimeCallbackOfferStatus.Accepted,
                        sequence,
                        item.Completion.Task,
                        item.CanonicalSequence,
                        string.Empty);
                }
            }
            finally
            {
                Monitor.Exit(_diagnosticGate);
            }
            if (auditCount > 0 && IsPowerOfTwo(auditCount))
            {
                EmitAudit(new WatchdogRuntimeCallbackAudit(
                    "DiagnosticQueueDropPowerOfTwo",
                    auditCount,
                    WatchdogRuntimeCallbackLane.Diagnostic,
                    sequence,
                    "QueueCapacity=" + DiagnosticCapacity));
            }
            TrySignal(_diagnosticWake);
            return result;
        }

        private WatchdogRuntimeCallbackOfferResult EnqueueSafety(WatchdogRuntimeCallbackOffer offer)
        {
            if (!IsValid(offer) || offer.Lane != WatchdogRuntimeCallbackLane.Safety)
                return Rejected(offer, WatchdogRuntimeCallbackLane.Safety,
                    WatchdogRuntimeCallbackOfferStatus.RejectedInvalid, 0, "InvalidOffer");
            var sequence = Interlocked.Increment(ref _sequence);
            if (!Monitor.TryEnter(_safetyGate, 0))
                return Rejected(offer, WatchdogRuntimeCallbackLane.Safety,
                    WatchdogRuntimeCallbackOfferStatus.RejectedSaturated,
                    sequence, "SafetyGateBusy");
            try
            {
                if (Volatile.Read(ref _closing) != 0)
                    return RejectedLocked(offer, WatchdogRuntimeCallbackLane.Safety,
                        WatchdogRuntimeCallbackOfferStatus.RejectedClosed,
                        sequence, "DispatcherClosing");
                if (_safetyQueue.Count + _safetyPosting.Count + _safetyActive.Count >= SafetyCapacity)
                    return RejectedLocked(offer, WatchdogRuntimeCallbackLane.Safety,
                        WatchdogRuntimeCallbackOfferStatus.RejectedSaturated,
                        sequence, "SafetyCapacityFull");
                var item = CreateWorkItem(offer, sequence, null);
                _safetyQueue.Enqueue(item);
                Interlocked.Increment(ref _safetyQueueCount);
                Interlocked.Increment(ref _safetyAccepted);
                var result = BuildAdmissionLocked(
                    WatchdogRuntimeCallbackOfferStatus.Accepted,
                    sequence,
                    item.Completion.Task,
                    item.CanonicalSequence,
                    string.Empty);
                TrySignal(_safetyWake);
                return result;
            }
            finally
            {
                Monitor.Exit(_safetyGate);
            }
        }

        private static bool IsValid(WatchdogRuntimeCallbackOffer offer)
        {
            return offer != null && offer.Identity != null && offer.CallbackAsync != null;
        }

        private WorkItem CreateWorkItem(
            WatchdogRuntimeCallbackOffer offer,
            long sequence,
            string key)
        {
            var item = new WorkItem
            {
                Sequence = sequence,
                CanonicalSequence = sequence,
                Offer = offer,
                Key = key,
                Completion = new TaskCompletionSource<WatchdogCallbackCompletionReceipt>(
                    TaskCreationOptions.RunContinuationsAsynchronously)
            };
            item.Callback = offer.CallbackAsync;
            return item;
        }

        private static string BuildDiagnosticKey(WatchdogRuntimeCallbackOffer offer)
        {
            return offer.Identity.ExactKey + "|" +
                   offer.Code + "|" + offer.Reason + "|" + offer.Detail + "|" + offer.CoalesceKey;
        }

        private static Task<WatchdogCallbackCompletionReceipt> DeriveCompletion(
            WorkItem canonical,
            long admissionSequence)
        {
            return canonical.Completion.Task.ContinueWith(
                completed =>
                {
                    if (completed.Status == TaskStatus.RanToCompletion && completed.Result != null)
                        return completed.Result.WithSequences(
                            admissionSequence,
                            canonical.CanonicalSequence);
                    return WatchdogCallbackCompletionReceipt.Rejected(
                        canonical.Offer,
                        admissionSequence,
                        canonical.CanonicalSequence,
                        "CanonicalCompletionUnavailable");
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private WatchdogRuntimeCallbackOfferResult Rejected(
            WatchdogRuntimeCallbackOffer offer,
            WatchdogRuntimeCallbackLane lane,
            WatchdogRuntimeCallbackOfferStatus status,
            long sequence,
            string reason)
        {
            if (lane == WatchdogRuntimeCallbackLane.Safety)
                Interlocked.Increment(ref _safetyRejected);
            else
                Interlocked.Increment(ref _diagnosticRejected);
            var receipt = WatchdogCallbackCompletionReceipt.Rejected(
                offer, lane, sequence, sequence, reason);
            return new WatchdogRuntimeCallbackOfferResult(
                status,
                sequence,
                sequence,
                Task.FromResult(receipt),
                reason,
                SafetyQueueCount,
                DiagnosticQueueCount,
                InFlightCount,
                Interlocked.Read(ref _diagnosticDropped));
        }

        private WatchdogRuntimeCallbackOfferResult RejectedLocked(
            WatchdogRuntimeCallbackOffer offer,
            WatchdogRuntimeCallbackLane lane,
            WatchdogRuntimeCallbackOfferStatus status,
            long sequence,
            string reason)
        {
            if (lane == WatchdogRuntimeCallbackLane.Safety)
                Interlocked.Increment(ref _safetyRejected);
            else
                Interlocked.Increment(ref _diagnosticRejected);
            var receipt = WatchdogCallbackCompletionReceipt.Rejected(
                offer, lane, sequence, sequence, reason);
            return BuildAdmissionLocked(
                status,
                sequence,
                Task.FromResult(receipt),
                sequence,
                reason);
        }

        private WatchdogRuntimeCallbackOfferResult BuildAdmissionLocked(
            WatchdogRuntimeCallbackOfferStatus status,
            long admissionSequence,
            Task<WatchdogCallbackCompletionReceipt> completion,
            long canonicalSequence,
            string reason)
        {
            return new WatchdogRuntimeCallbackOfferResult(
                status,
                admissionSequence,
                canonicalSequence,
                completion,
                reason,
                Volatile.Read(ref _safetyQueueCount) +
                Volatile.Read(ref _safetyPostingCount) +
                Volatile.Read(ref _safetyActiveCount),
                Volatile.Read(ref _diagnosticQueueCount),
                Volatile.Read(ref _safetyPostingCount) +
                Volatile.Read(ref _safetyActiveCount) +
                Volatile.Read(ref _diagnosticPostingCount) +
                Volatile.Read(ref _diagnosticActiveCount),
                Interlocked.Read(ref _diagnosticDropped));
        }

        private void SafetyWorkerLoop()
        {
            try
            {
                while (true)
                {
                    var toPost = new List<Pending>();
                    lock (_safetyGate)
                    {
                        while (_safetyQueue.Count > 0)
                        {
                            var item = _safetyQueue.Dequeue();
                            Interlocked.Decrement(ref _safetyQueueCount);
                            var pending = NewPending(item);
                            _safetyPosting.Add(pending);
                            Interlocked.Increment(ref _safetyPostingCount);
                            toPost.Add(pending);
                        }
                        if (Volatile.Read(ref _closing) != 0 &&
                            _safetyQueue.Count == 0 &&
                            _safetyPosting.Count == 0 &&
                            _safetyActive.Count == 0)
                            break;
                    }
                    foreach (var pending in toPost)
                        PostSafety(pending);
                    CompleteReadySafety();
                    if (toPost.Count == 0)
                        WaitWake(_safetyWake, 10);
                }
            }
            catch (Exception ex)
            {
                RecordFailure("SafetyWorkerException", ex.Message, 0, true);
                FailSafetyOutstanding(ex);
            }
            finally
            {
                Volatile.Write(ref _safetyExited, 1);
                TryFinalizeResources();
            }
        }

        private void PostSafety(Pending pending)
        {
            WatchdogPostReceipt posted = null;
            try
            {
                posted = _safetyTarget.TryPost(pending.Item.Callback);
            }
            catch (Exception ex)
            {
                RemoveSafetyPosting(pending);
                Interlocked.Increment(ref _safetyPostRejected);
                Interlocked.Increment(ref _safetyPostFaults);
                CompletePostRejected(pending.Item, "PostInfrastructureFault:" + ex.GetType().Name);
                RecordFailure("SafetyPostInfrastructureFault", ex.Message, pending.Item.Sequence);
                NotifySpaceAvailableAsync();
                return;
            }
            if (posted == null || !posted.Accepted || posted.Completion == null)
            {
                RemoveSafetyPosting(pending);
                Interlocked.Increment(ref _safetyPostRejected);
                CompletePostRejected(pending.Item, posted == null ? "PostRejected" : posted.Reason);
                RecordFailure("SafetyPostRejected",
                    posted == null ? "PostRejected" : posted.Reason,
                    pending.Item.Sequence);
                NotifySpaceAvailableAsync();
                return;
            }
            pending.Completion = posted.Completion;
            pending.PostedUtcTicks = DateTime.UtcNow.Ticks;
            lock (_safetyGate)
            {
                if (_safetyPosting.Remove(pending))
                    Interlocked.Decrement(ref _safetyPostingCount);
                _safetyActive.Add(pending);
                Interlocked.Increment(ref _safetyActiveCount);
                Interlocked.Increment(ref _safetyPostAccepted);
                Interlocked.Increment(ref _safetyCompletionPosted);
            }
        }

        private void CompleteReadySafety()
        {
            List<Pending> ready;
            lock (_safetyGate)
            {
                ready = _safetyActive.Where(item => item.Completion != null && item.Completion.IsCompleted).ToList();
                foreach (var pending in ready)
                {
                    if (_safetyActive.Remove(pending))
                        Interlocked.Decrement(ref _safetyActiveCount);
                }
            }
            foreach (var pending in ready)
            {
                FinalizeCompletion(pending, false);
                NotifySpaceAvailableAsync();
            }
        }

        private void FailSafetyOutstanding(Exception exception)
        {
            List<Pending> pending;
            List<WorkItem> queued;
            lock (_safetyGate)
            {
                pending = _safetyPosting.Concat(_safetyActive).ToList();
                queued = _safetyQueue.ToList();
                _safetyPosting.Clear();
                _safetyActive.Clear();
                _safetyQueue.Clear();
                Volatile.Write(ref _safetyQueueCount, 0);
                Volatile.Write(ref _safetyPostingCount, 0);
                Volatile.Write(ref _safetyActiveCount, 0);
            }
            foreach (var item in queued)
                CompletePostRejected(item, "SafetyWorkerException");
            foreach (var item in pending)
                CompletePostRejected(item.Item, "SafetyWorkerException");
        }

        private void DiagnosticWorkerLoop()
        {
            try
            {
                while (true)
                {
                    var toPost = new List<Pending>();
                    lock (_diagnosticGate)
                    {
                        while (_diagnosticPosting.Count + _diagnosticActive.Count < DiagnosticMaxInflight &&
                               _diagnosticQueue.Count > 0)
                        {
                            var item = _diagnosticQueue.Dequeue();
                            Interlocked.Decrement(ref _diagnosticQueueCount);
                            var pending = NewPending(item);
                            _diagnosticPosting.Add(pending);
                            Interlocked.Increment(ref _diagnosticPostingCount);
                            toPost.Add(pending);
                        }
                        if (Volatile.Read(ref _closing) != 0 &&
                            _diagnosticQueue.Count == 0 &&
                            _diagnosticPosting.Count == 0 &&
                            _diagnosticActive.Count == 0)
                            break;
                    }
                    foreach (var pending in toPost)
                        PostDiagnostic(pending);
                    CompleteReadyDiagnostic();
                    if (toPost.Count == 0)
                        WaitWake(_diagnosticWake, 10);
                }
            }
            catch (Exception ex)
            {
                RecordFailure("DiagnosticWorkerException", ex.Message, 0, true);
                FailDiagnosticOutstanding(ex);
            }
            finally
            {
                Volatile.Write(ref _diagnosticExited, 1);
                TryFinalizeResources();
            }
        }

        private void PostDiagnostic(Pending pending)
        {
            WatchdogPostReceipt posted = null;
            try
            {
                posted = _diagnosticTarget.TryPost(pending.Item.Callback);
            }
            catch (Exception ex)
            {
                RemoveDiagnosticPosting(pending);
                Interlocked.Increment(ref _diagnosticPostRejected);
                Interlocked.Increment(ref _diagnosticPostFaults);
                RemoveDiagnosticKey(pending.Item.Key);
                CompletePostRejected(pending.Item, "PostInfrastructureFault:" + ex.GetType().Name);
                RecordFailure("DiagnosticPostInfrastructureFault", ex.Message, pending.Item.Sequence);
                NotifySpaceAvailableAsync();
                return;
            }
            if (posted == null || !posted.Accepted || posted.Completion == null)
            {
                RemoveDiagnosticPosting(pending);
                Interlocked.Increment(ref _diagnosticPostRejected);
                RemoveDiagnosticKey(pending.Item.Key);
                CompletePostRejected(pending.Item, posted == null ? "PostRejected" : posted.Reason);
                RecordFailure("DiagnosticPostRejected",
                    posted == null ? "PostRejected" : posted.Reason,
                    pending.Item.Sequence);
                NotifySpaceAvailableAsync();
                return;
            }
            pending.Completion = posted.Completion;
            pending.PostedUtcTicks = DateTime.UtcNow.Ticks;
            lock (_diagnosticGate)
            {
                if (_diagnosticPosting.Remove(pending))
                    Interlocked.Decrement(ref _diagnosticPostingCount);
                _diagnosticActive.Add(pending);
                Interlocked.Increment(ref _diagnosticActiveCount);
                Interlocked.Increment(ref _diagnosticPostAccepted);
                Interlocked.Increment(ref _diagnosticCompletionPosted);
            }
        }

        private void CompleteReadyDiagnostic()
        {
            List<Pending> ready;
            lock (_diagnosticGate)
            {
                ready = _diagnosticActive.Where(item => item.Completion != null && item.Completion.IsCompleted).ToList();
                foreach (var pending in ready)
                {
                    if (_diagnosticActive.Remove(pending))
                        Interlocked.Decrement(ref _diagnosticActiveCount);
                    _diagnosticKeys.Remove(pending.Item.Key);
                }
            }
            foreach (var pending in ready)
            {
                FinalizeCompletion(pending, true);
                NotifySpaceAvailableAsync();
            }
        }

        private void FailDiagnosticOutstanding(Exception exception)
        {
            List<Pending> pending;
            List<WorkItem> queued;
            lock (_diagnosticGate)
            {
                pending = _diagnosticPosting.Concat(_diagnosticActive).ToList();
                queued = _diagnosticQueue.ToList();
                _diagnosticPosting.Clear();
                _diagnosticActive.Clear();
                _diagnosticQueue.Clear();
                _diagnosticKeys.Clear();
                Volatile.Write(ref _diagnosticQueueCount, 0);
                Volatile.Write(ref _diagnosticPostingCount, 0);
                Volatile.Write(ref _diagnosticActiveCount, 0);
            }
            foreach (var item in queued)
                CompletePostRejected(item, "DiagnosticWorkerException");
            foreach (var item in pending)
                CompletePostRejected(item.Item, "DiagnosticWorkerException");
            if (queued.Count > 0 || pending.Count > 0)
                NotifySpaceAvailableAsync();
        }

        private Pending NewPending(WorkItem item)
        {
            return new Pending
            {
                Item = item,
                Stopwatch = Stopwatch.StartNew()
            };
        }

        private void RemoveSafetyPosting(Pending pending)
        {
            lock (_safetyGate)
            {
                if (_safetyPosting.Remove(pending))
                    Interlocked.Decrement(ref _safetyPostingCount);
            }
        }

        private void RemoveDiagnosticPosting(Pending pending)
        {
            lock (_diagnosticGate)
            {
                if (_diagnosticPosting.Remove(pending))
                    Interlocked.Decrement(ref _diagnosticPostingCount);
            }
        }

        private void RemoveDiagnosticKey(string key)
        {
            if (key == null) return;
            lock (_diagnosticGate) _diagnosticKeys.Remove(key);
        }

        private void CompletePostRejected(WorkItem item, string reason)
        {
            item.Completion.TrySetResult(new WatchdogCallbackCompletionReceipt(
                WatchdogCallbackCompletionStatus.PostRejected,
                true,
                false,
                item.Sequence,
                item.CanonicalSequence,
                item.Offer.Lane,
                reason,
                string.Empty,
                0,
                DateTime.UtcNow.Ticks,
                0));
        }

        private void FinalizeCompletion(Pending pending, bool diagnostic)
        {
            pending.Stopwatch.Stop();
            var status = WatchdogCallbackCompletionStatus.Completed;
            var reason = string.Empty;
            var exceptionType = string.Empty;
            if (pending.Completion.IsCanceled)
            {
                status = WatchdogCallbackCompletionStatus.Canceled;
                reason = "TaskCanceled";
                exceptionType = typeof(OperationCanceledException).Name;
                if (diagnostic) Interlocked.Increment(ref _diagnosticPostCanceled);
                else Interlocked.Increment(ref _safetyPostCanceled);
            }
            else if (pending.Completion.IsFaulted)
            {
                status = WatchdogCallbackCompletionStatus.HandlerFaulted;
                var exception = pending.Completion.Exception?.GetBaseException();
                reason = exception?.Message ?? "HandlerFaulted";
                exceptionType = exception?.GetType().Name ?? typeof(Exception).Name;
                if (diagnostic)
                {
                    Interlocked.Increment(ref _diagnosticHandlerFaults);
                    Interlocked.Increment(ref _diagnosticCallbackFailures);
                }
                else
                {
                    Interlocked.Increment(ref _safetyHandlerFaults);
                    Interlocked.Increment(ref _safetyCallbackFailures);
                }
                RecordFailure("HandlerFaulted", reason, pending.Item.Sequence);
            }
            else
            {
                if (diagnostic) Interlocked.Increment(ref _diagnosticExecuted);
                else Interlocked.Increment(ref _safetyExecuted);
            }
            var slow = _options.SlowCallbackMilliseconds > 0 &&
                       pending.Stopwatch.ElapsedMilliseconds >= _options.SlowCallbackMilliseconds;
            if (slow)
            {
                if (diagnostic) Interlocked.Increment(ref _diagnosticPostSlow);
                else Interlocked.Increment(ref _safetyPostSlow);
            }
            if (diagnostic) Interlocked.Increment(ref _diagnosticCompletionCompleted);
            else Interlocked.Increment(ref _safetyCompletionCompleted);
            pending.Item.Completion.TrySetResult(new WatchdogCallbackCompletionReceipt(
                status,
                true,
                pending.PostedUtcTicks > 0,
                pending.Item.Sequence,
                pending.Item.CanonicalSequence,
                pending.Item.Offer.Lane,
                reason,
                exceptionType,
                pending.PostedUtcTicks,
                DateTime.UtcNow.Ticks,
                pending.Stopwatch.ElapsedMilliseconds));
        }

        private void RecordFailure(string kind, string reason, long sequence, bool workerException = false)
        {
            if (workerException) Interlocked.Increment(ref _workerExceptionCount);
            Volatile.Write(ref _lastFailure, new FailureSnapshot(kind, reason, sequence));
        }

        private void NotifySpaceAvailableAsync()
        {
            var handlers = SpaceAvailable?.GetInvocationList();
            if (handlers == null) return;
            foreach (var entry in handlers)
            {
                var handler = entry as Action;
                if (handler == null) continue;
                try
                {
                    var task = Task.Factory.StartNew(() =>
                    {
                        try { handler(); }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref _spaceAvailableFailureCount);
                            Volatile.Write(ref _lastSpaceAvailableFailure, ex.GetType().Name + ":" + ex.Message);
                        }
                    }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
                    TrackObserverTask(task);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _spaceAvailableFailureCount);
                    Volatile.Write(ref _lastSpaceAvailableFailure, ex.GetType().Name + ":" + ex.Message);
                }
            }
        }

        private void EmitAudit(WatchdogRuntimeCallbackAudit audit)
        {
            var handlers = new List<Action<WatchdogRuntimeCallbackAudit>>();
            if (_options.AuditSink != null) handlers.Add(_options.AuditSink);
            var eventHandlers = Audit?.GetInvocationList();
            if (eventHandlers != null)
            {
                foreach (var entry in eventHandlers)
                    if (entry is Action<WatchdogRuntimeCallbackAudit> handler) handlers.Add(handler);
            }
            foreach (var handler in handlers)
            {
                try
                {
                    var task = Task.Factory.StartNew(() =>
                    {
                        try { handler(audit); }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref _auditFailureCount);
                            Volatile.Write(ref _lastAuditFailure, ex.GetType().Name + ":" + ex.Message);
                        }
                    }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
                    TrackObserverTask(task);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _auditFailureCount);
                    Volatile.Write(ref _lastAuditFailure, ex.GetType().Name + ":" + ex.Message);
                }
            }
        }

        private void TrackObserverTask(Task task)
        {
            if (task == null) return;
            lock (_observerGate) _observerTasks.Add(task);
            task.ContinueWith(completed =>
            {
                lock (_observerGate) _observerTasks.Remove(completed);
                TryFinalizeResources();
            }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static bool IsPowerOfTwo(long value) => value > 0 && (value & (value - 1)) == 0;

        private static void TrySignal(EventWaitHandle handle)
        {
            try { handle.Set(); } catch (ObjectDisposedException) { }
        }

        private static void WaitWake(EventWaitHandle handle, int milliseconds)
        {
            try { handle.WaitOne(milliseconds); } catch (ObjectDisposedException) { }
        }

        public WatchdogRuntimeCallbackDrainReceipt CompleteAndDrain() =>
            CompleteAndDrain(_options.DrainTimeout);

        public WatchdogRuntimeCallbackDrainReceipt CompleteAndDrain(TimeSpan timeout)
        {
            if (timeout < TimeSpan.Zero) timeout = TimeSpan.Zero;
            var cached = Volatile.Read(ref _terminalReceipt);
            if (cached != null) return cached;
            var stopwatch = Stopwatch.StartNew();
            var producersClosed = CloseAndWaitOfferProducers(timeout, stopwatch);
            TrySignal(_safetyWake);
            TrySignal(_diagnosticWake);
            if (!producersClosed)
            {
                TryFinalizeResources();
                var producerSnapshot = CaptureSnapshot();
                return BuildReceipt(producerSnapshot, false, true, timeout, stopwatch.Elapsed);
            }
            JoinWithBudget(_safetyWorker, timeout, stopwatch);
            JoinWithBudget(_diagnosticWorker, timeout, stopwatch);
            WaitObserverTasks(timeout, stopwatch);
            TryFinalizeResources();
            var snapshot = CaptureSnapshot();
            var terminal = snapshot.WorkersTerminal && snapshot.AllQueuesEmpty &&
                           snapshot.AllObserversComplete && Volatile.Read(ref _resourcesReleased) != 0;
            var receipt = BuildReceipt(snapshot, terminal, !terminal, timeout, stopwatch.Elapsed);
            if (terminal)
            {
                Interlocked.CompareExchange(ref _terminalReceipt, receipt, null);
                return Volatile.Read(ref _terminalReceipt);
            }
            return receipt;
        }

        private void WaitObserverTasks(TimeSpan budget, Stopwatch elapsed)
        {
            while (true)
            {
                Task[] tasks;
                lock (_observerGate) tasks = _observerTasks.Where(item => !item.IsCompleted).ToArray();
                if (tasks.Length == 0) return;
                var remaining = budget - elapsed.Elapsed;
                if (remaining <= TimeSpan.Zero) return;
                var milliseconds = (int)Math.Min(int.MaxValue, Math.Max(1, remaining.TotalMilliseconds));
                try { Task.WaitAll(tasks, milliseconds); } catch { }
                if (elapsed.Elapsed >= budget) return;
            }
        }

        private static void JoinWithBudget(Thread thread, TimeSpan budget, Stopwatch elapsed)
        {
            if (thread == null || !thread.IsAlive) return;
            var remaining = budget - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero) return;
            var milliseconds = (int)Math.Min(int.MaxValue, Math.Max(1, remaining.TotalMilliseconds));
            try { thread.Join(milliseconds); } catch (ThreadStateException) { }
        }

        private sealed class DispatchSnapshot
        {
            internal int SafetyQueue;
            internal int SafetyPosting;
            internal int SafetyActive;
            internal int DiagnosticQueue;
            internal int DiagnosticPosting;
            internal int DiagnosticActive;
            internal bool WorkersTerminal;
            internal bool AllQueuesEmpty;
            internal bool AllObserversComplete;
        }

        private DispatchSnapshot CaptureSnapshot()
        {
            var snapshot = new DispatchSnapshot();
            lock (_safetyGate)
            {
                snapshot.SafetyQueue = _safetyQueue.Count;
                snapshot.SafetyPosting = _safetyPosting.Count;
                snapshot.SafetyActive = _safetyActive.Count;
                lock (_diagnosticGate)
                {
                    snapshot.DiagnosticQueue = _diagnosticQueue.Count;
                    snapshot.DiagnosticPosting = _diagnosticPosting.Count;
                    snapshot.DiagnosticActive = _diagnosticActive.Count;
                }
            }
            lock (_observerGate)
                snapshot.AllObserversComplete = !_observerTasks.Any(item => !item.IsCompleted);
            snapshot.WorkersTerminal = !_safetyWorker.IsAlive && !_diagnosticWorker.IsAlive;
            snapshot.AllQueuesEmpty = snapshot.SafetyQueue == 0 && snapshot.SafetyPosting == 0 &&
                                      snapshot.SafetyActive == 0 && snapshot.DiagnosticQueue == 0 &&
                                      snapshot.DiagnosticPosting == 0 && snapshot.DiagnosticActive == 0;
            return snapshot;
        }

        private WatchdogRuntimeCallbackDrainReceipt BuildReceipt(
            DispatchSnapshot snapshot,
            bool terminal,
            bool timedOut,
            TimeSpan budget,
            TimeSpan elapsed)
        {
            var failure = Volatile.Read(ref _lastFailure);
            return new WatchdogRuntimeCallbackDrainReceipt(
                Volatile.Read(ref _closing) != 0,
                terminal,
                timedOut,
                snapshot.WorkersTerminal,
                snapshot.AllObserversComplete,
                Volatile.Read(ref _resourcesReleased) != 0,
                budget,
                elapsed,
                Interlocked.Read(ref _safetyAccepted),
                Interlocked.Read(ref _safetyRejected),
                Interlocked.Read(ref _safetyExecuted),
                Interlocked.Read(ref _safetySkipped),
                Interlocked.Read(ref _safetyCallbackFailures),
                Interlocked.Read(ref _safetyPostAccepted),
                Interlocked.Read(ref _safetyPostRejected),
                Interlocked.Read(ref _safetyPostFaults),
                Interlocked.Read(ref _safetyHandlerFaults),
                Interlocked.Read(ref _safetyPostCanceled),
                Interlocked.Read(ref _safetyPostSlow),
                Interlocked.Read(ref _safetyCompletionPosted),
                Interlocked.Read(ref _safetyCompletionCompleted),
                Interlocked.Read(ref _diagnosticAccepted),
                Interlocked.Read(ref _diagnosticRejected),
                Interlocked.Read(ref _diagnosticCoalesced),
                Interlocked.Read(ref _diagnosticDropped),
                Interlocked.Read(ref _diagnosticExecuted),
                Interlocked.Read(ref _diagnosticCallbackFailures),
                Interlocked.Read(ref _diagnosticPostAccepted),
                Interlocked.Read(ref _diagnosticPostRejected),
                Interlocked.Read(ref _diagnosticPostFaults),
                Interlocked.Read(ref _diagnosticHandlerFaults),
                Interlocked.Read(ref _diagnosticPostCanceled),
                Interlocked.Read(ref _diagnosticPostSlow),
                Interlocked.Read(ref _diagnosticCompletionPosted),
                Interlocked.Read(ref _diagnosticCompletionCompleted),
                snapshot.SafetyQueue,
                snapshot.SafetyPosting,
                snapshot.SafetyActive,
                snapshot.DiagnosticQueue,
                snapshot.DiagnosticPosting,
                snapshot.DiagnosticActive,
                _safetyWorker.ManagedThreadId,
                _diagnosticWorker.ManagedThreadId,
                _safetyPriority,
                _diagnosticPriority,
                Interlocked.Read(ref _sequence),
                Interlocked.Read(ref _workerExceptionCount),
                Interlocked.Read(ref _auditFailureCount),
                Interlocked.Read(ref _spaceAvailableFailureCount),
                failure?.Kind,
                failure?.Reason,
                failure?.Sequence ?? 0,
                LastSpaceAvailableFailure,
                LastAuditFailure,
                DateTime.UtcNow.Ticks);
        }

        private void TryFinalizeResources()
        {
            if (Volatile.Read(ref _closing) == 0 ||
                Volatile.Read(ref _safetyExited) == 0 ||
                Volatile.Read(ref _diagnosticExited) == 0 ||
                Interlocked.CompareExchange(ref _resourcesFinalizing, 1, 0) != 0)
                return;
            var snapshot = CaptureSnapshot();
            if (!snapshot.WorkersTerminal || !snapshot.AllQueuesEmpty ||
                !snapshot.AllObserversComplete || Volatile.Read(ref _activeOfferProducers) != 0)
            {
                Volatile.Write(ref _resourcesFinalizing, 0);
                return;
            }
            try
            {
                if (_ownsSafetyTarget) (_safetyTarget as IDisposable)?.Dispose();
                if (_ownsDiagnosticTarget && !ReferenceEquals(_diagnosticTarget, _safetyTarget))
                    (_diagnosticTarget as IDisposable)?.Dispose();
                _safetyWake.Dispose();
                _diagnosticWake.Dispose();
                Volatile.Write(ref _resourcesReleased, 1);
            }
            catch (Exception ex)
            {
                RecordFailure("ResourceReleaseFault", ex.Message, 0);
                Volatile.Write(ref _resourcesFinalizing, 0);
            }
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposeRequested, 1);
            CompleteAndDrain(_options.DrainTimeout);
            GC.SuppressFinalize(this);
        }
    }
}
