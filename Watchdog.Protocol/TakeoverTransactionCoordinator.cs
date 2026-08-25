using System;
using System.Threading;

namespace MTTFTest.Watchdog.Protocol
{
    /// <summary>
    /// Generation-scoped automatic takeover transaction.  The coordinator is
    /// the linearization point shared by SafeIdle cancellation and irreversible
    /// process termination/relaunch authorization.
    /// </summary>
    public sealed class TakeoverTransactionCoordinator
    {
        private readonly object _gate = new object();
        private long _generation;
        private TakeoverTransactionLease _active;

        public bool TryBegin(
            string correlationId,
            string authorityIdentity,
            out TakeoverTransactionLease lease)
        {
            lock (_gate)
            {
                // Even a cancelled lease remains the current generation until
                // its worker reaches finally/Complete.  Replacing it early
                // would let a monitor tick start a second takeover while the
                // cancelled dump/wait continuation is still unwinding.
                if (_active != null)
                {
                    lease = null;
                    return false;
                }

                unchecked { _generation++; }
                if (_generation <= 0) _generation = 1;
                lease = new TakeoverTransactionLease(
                    _generation,
                    correlationId,
                    authorityIdentity);
                _active = lease;
                return true;
            }
        }

        public bool IsAuthorized(TakeoverTransactionLease lease)
        {
            lock (_gate)
                return IsCurrentCancelableLocked(lease);
        }

        public bool TryAdvance(
            TakeoverTransactionLease lease,
            TakeoverTransactionStage stage)
        {
            lock (_gate)
            {
                if (!IsCurrentCancelableLocked(lease) || stage <= lease.Stage)
                    return false;
                lease.SetStage(stage);
                return true;
            }
        }

        /// <summary>
        /// Executes a point-of-no-return action while holding the same gate
        /// used by TryCancel.  If cancellation linearizes first, action is not
        /// invoked; if this method linearizes first, cancellation truthfully
        /// reports that termination authorization has already been consumed.
        /// </summary>
        public bool TryExecute<T>(
            TakeoverTransactionLease lease,
            TakeoverTransactionStage stage,
            Func<T> action,
            out T result)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            lock (_gate)
            {
                if (!IsCurrentCancelableLocked(lease) || stage <= lease.Stage)
                {
                    result = default(T);
                    return false;
                }
                lease.SetStage(stage);
                result = action();
                return true;
            }
        }

        public bool TryCancel(
            string reason,
            out TakeoverTransactionLease cancelled)
        {
            CancellationTokenSource cancellation = null;
            lock (_gate)
            {
                cancelled = _active;
                if (!IsCurrentCancelableLocked(cancelled) ||
                    cancelled.Stage >= TakeoverTransactionStage.ProcessTermination)
                {
                    cancelled = null;
                    return false;
                }

                cancelled.SetCancellation(reason);
                cancellation = cancelled.CancellationSource;
            }

            try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
            return true;
        }

        public void Complete(TakeoverTransactionLease lease)
        {
            if (lease == null) return;
            lock (_gate)
            {
                if (!ReferenceEquals(_active, lease)) return;
                if (!lease.IsCancelled)
                    lease.SetStage(TakeoverTransactionStage.Completed);
                _active = null;
            }
        }

        private bool IsCurrentCancelableLocked(TakeoverTransactionLease lease)
        {
            return lease != null &&
                   ReferenceEquals(_active, lease) &&
                   !lease.IsTerminal &&
                   !lease.IsCancelled;
        }
    }

    public sealed class TakeoverTransactionLease
    {
        private int _stage;
        private int _cancelled;
        private int _cancelledFromStage;
        private string _cancellationReason = string.Empty;

        internal TakeoverTransactionLease(
            long generation,
            string correlationId,
            string authorityIdentity)
        {
            Generation = generation;
            CorrelationId = correlationId ?? string.Empty;
            AuthorityIdentity = authorityIdentity ?? string.Empty;
            CancellationSource = new CancellationTokenSource();
            _stage = (int)TakeoverTransactionStage.StopRequested;
        }

        public long Generation { get; }
        public string CorrelationId { get; }
        public string AuthorityIdentity { get; }
        public TakeoverTransactionStage Stage =>
            (TakeoverTransactionStage)Volatile.Read(ref _stage);
        public bool IsCancelled => Volatile.Read(ref _cancelled) != 0;
        public TakeoverTransactionStage CancelledFromStage =>
            (TakeoverTransactionStage)Volatile.Read(ref _cancelledFromStage);
        public bool IsTerminal =>
            IsCancelled || Stage == TakeoverTransactionStage.Completed;
        public string CancellationReason =>
            Volatile.Read(ref _cancellationReason) ?? string.Empty;
        public CancellationToken CancellationToken => CancellationSource.Token;

        internal CancellationTokenSource CancellationSource { get; }

        internal void SetStage(TakeoverTransactionStage stage) =>
            Volatile.Write(ref _stage, (int)stage);

        internal void SetCancellation(string reason)
        {
            Volatile.Write(ref _cancellationReason, reason ?? string.Empty);
            Volatile.Write(ref _cancelledFromStage, Volatile.Read(ref _stage));
            Volatile.Write(ref _cancelled, 1);
            Volatile.Write(ref _stage, (int)TakeoverTransactionStage.Cancelled);
        }
    }

    public enum TakeoverTransactionStage
    {
        StopRequested = 10,
        DumpCapture = 20,
        ProcessTermination = 30,
        RelaunchPermit = 40,
        Relaunching = 50,
        Cancelled = 90,
        Completed = 100
    }
}
