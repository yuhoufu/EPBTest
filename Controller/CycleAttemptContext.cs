using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Controller
{
    /// <summary>一次圈控制尝试的来源；用于区分正常批次、单通道和恢复重入。</summary>
    public enum CycleAttemptKind
    {
        FormalBatch = 1,
        FormalSingle = 2,
        FormalRecovery = 3,
        Learning = 4,
        Qualification = 5
    }

    /// <summary>圈尝试的唯一终态。持久化成功前只是原子声明，失败后回到 Active 供恢复封圈。</summary>
    public enum CycleAttemptTerminalState
    {
        Active = 0,
        Completed = 1,
        Aborted = 2,
        Alarmed = 3
    }

    public enum CycleAttemptBeginState
    {
        Registered = 0,
        Begun = 1,
        Failed = 2
    }

    /// <summary>
    /// 一次圈尝试的不可变身份和终态门闩。终态先锁存，再执行耐久提交；只有提交成功后
    /// 才从 registry 精确移除。提交失败会回到 Active，但不会丢失身份，恢复路径可再次封圈。
    /// </summary>
    public sealed class CycleAttemptContext : IDisposable
    {
        private int _terminalState;
        private readonly object _terminalGate = new object();
        private int _terminalActionOwned;
        private int _durablyCommitted;
        private int _terminalCleanupOwned;
        private int _terminalCleanupComplete;
        private int _cancelRequested;
        private int _disposed;
        private int _beginState;
        private Exception _beginFailure;
        private int _executionStarted;
        private int _executionCompleted;
        private int _registryRemoved;
        private readonly TaskCompletionSource<bool> _executionCompletion =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _durableCompletion =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public CycleAttemptContext(
            Guid runId,
            long runEpoch,
            string device,
            int channel,
            long attemptId,
            CycleAttemptKind kind,
            int cycle,
            CancellationTokenSource attemptCts = null)
            : this(
                runId,
                runEpoch,
                device,
                0,
                0,
                channel,
                attemptId,
                kind,
                cycle,
                attemptCts)
        {
        }

        public CycleAttemptContext(
            Guid runId,
            long runEpoch,
            string device,
            long daqGeneration,
            long daqBeginSequence,
            int channel,
            long attemptId,
            CycleAttemptKind kind,
            int cycle,
            CancellationTokenSource attemptCts = null)
        {
            if (channel <= 0) throw new ArgumentOutOfRangeException(nameof(channel));
            if (attemptId <= 0) throw new ArgumentOutOfRangeException(nameof(attemptId));

            RunId = runId;
            RunEpoch = runEpoch;
            Device = device ?? string.Empty;
            DaqGeneration = Math.Max(0, daqGeneration);
            DaqBeginSequence = Math.Max(0, daqBeginSequence);
            Channel = channel;
            AttemptId = attemptId;
            Kind = kind;
            Cycle = cycle;
            AttemptCts = attemptCts ?? new CancellationTokenSource();
        }

        public Guid RunId { get; }
        public long RunEpoch { get; }
        public string Device { get; }
        /// <summary>
        /// DAQ identity frozen before Recorder.BeginCycle.  Recovery finalizers must use
        /// this identity instead of a later replacement generation; otherwise a local
        /// equipment fault can manufacture a cross-generation seal failure and escalate
        /// an otherwise safe group isolation into a global stop.
        /// </summary>
        public long DaqGeneration { get; }
        public long DaqBeginSequence { get; }
        public int Channel { get; }
        public long AttemptId { get; }
        public CycleAttemptKind Kind { get; }
        public int Cycle { get; }
        public CancellationTokenSource AttemptCts { get; }
        public CycleAttemptBeginState BeginState =>
            (CycleAttemptBeginState)Volatile.Read(ref _beginState);
        public Exception BeginFailure => Volatile.Read(ref _beginFailure);
        public CycleAttemptTerminalState TerminalState =>
            (CycleAttemptTerminalState)Volatile.Read(ref _terminalState);
        public bool IsDurablyCommitted => Volatile.Read(ref _durablyCommitted) != 0;
        public bool IsExecutionStarted => Volatile.Read(ref _executionStarted) != 0;
        public bool IsExecutionCompleted => Volatile.Read(ref _executionCompleted) != 0;
        public Task ExecutionCompletion => _executionCompletion.Task;
        public Task DurableCompletion => _durableCompletion.Task;

        public CycleAttemptClosureReceipt CaptureClosureReceipt()
        {
            var terminal = TerminalState;
            return new CycleAttemptClosureReceipt
            {
                RunId = RunId,
                RunEpoch = RunEpoch,
                Device = Device,
                Channel = Channel,
                AttemptId = AttemptId,
                Cycle = Cycle,
                Durable = IsDurablyCommitted,
                DurabilityEvidence = IsDurablyCommitted
                    ? "CycleAttemptTerminalDurablyCommitted"
                    : "CycleAttemptTerminalNotDurable",
                CapturedUtc = DateTime.UtcNow,
                Disposition = terminal == CycleAttemptTerminalState.Completed
                    ? CycleAttemptClosureDisposition.Committed
                    : terminal == CycleAttemptTerminalState.Aborted
                        ? CycleAttemptClosureDisposition.Aborted
                        : terminal == CycleAttemptTerminalState.Alarmed
                            ? CycleAttemptClosureDisposition.Alarmed
                            : CycleAttemptClosureDisposition.Unknown
            };
        }

        public void MarkBeginSucceeded()
        {
            Interlocked.CompareExchange(
                ref _beginState,
                (int)CycleAttemptBeginState.Begun,
                (int)CycleAttemptBeginState.Registered);
        }

        public void MarkBeginFailed(Exception error)
        {
            Volatile.Write(ref _beginFailure, error);
            Interlocked.CompareExchange(
                ref _beginState,
                (int)CycleAttemptBeginState.Failed,
                (int)CycleAttemptBeginState.Registered);
        }

        /// <summary>
        /// 在真正调用 Runner 前取得本 attempt 的执行所有权。Begin 未成功、已被撤销或
        /// 已经结束的 attempt 均不得进入硬件执行区。
        /// </summary>
        public bool MarkExecutionStarted()
        {
            lock (_terminalGate)
            {
                if (BeginState != CycleAttemptBeginState.Begun ||
                    AttemptCts.IsCancellationRequested ||
                    _terminalState != (int)CycleAttemptTerminalState.Active ||
                    _durablyCommitted != 0 ||
                    IsExecutionCompleted ||
                    _executionStarted != 0)
                    return false;

                Volatile.Write(ref _executionStarted, 1);
                return true;
            }
        }

        /// <summary>幂等发布 Runner 已彻底退出；允许下一 attempt 复用该通道 Runner。</summary>
        public bool MarkExecutionCompleted()
        {
            if (Interlocked.Exchange(ref _executionCompleted, 1) != 0) return false;
            _executionCompletion.TrySetResult(true);
            TryDisposeAfterLifecycleComplete();
            return true;
        }

        internal void MarkRegistryRemoved()
        {
            Volatile.Write(ref _registryRemoved, 1);
            TryDisposeAfterLifecycleComplete();
        }

        private void TryDisposeAfterLifecycleComplete()
        {
            if (Volatile.Read(ref _registryRemoved) != 0 && IsExecutionCompleted)
                Dispose();
        }

        /// <summary>竞争锁存唯一终态；只允许 Active 到某一个终态的一次转换。</summary>
        public bool TryLatchTerminal(CycleAttemptTerminalState terminal)
        {
            if (terminal == CycleAttemptTerminalState.Active)
                throw new ArgumentOutOfRangeException(nameof(terminal));
            lock (_terminalGate)
            {
                if (_terminalState != (int)CycleAttemptTerminalState.Active) return false;
                Volatile.Write(ref _terminalState, (int)terminal);
                return true;
            }
        }

        public bool CompleteOnce(Func<bool> persist, Action<CycleAttemptContext> onDurableCommit)
        {
            return CommitOnce(CycleAttemptTerminalState.Completed, persist, onDurableCommit);
        }

        public bool AbortOnce(Func<bool> persist, Action<CycleAttemptContext> onDurableCommit)
        {
            return CommitOnce(CycleAttemptTerminalState.Aborted, persist, onDurableCommit);
        }

        public bool AlarmOnce(Func<bool> persist, Action<CycleAttemptContext> onDurableCommit)
        {
            return CommitOnce(CycleAttemptTerminalState.Alarmed, persist, onDurableCommit);
        }

        /// <summary>只有 Begin 已提交后才允许 Complete 触碰 Recorder。</summary>
        public bool CompleteRecorderOnce(
            Func<bool> persist,
            Action<CycleAttemptContext> onDurableCommit)
        {
            var begin = BeginState;
            if (begin == CycleAttemptBeginState.Registered)
            {
                CancelAttempt();
                return false;
            }
            if (begin == CycleAttemptBeginState.Failed) return false;
            return CompleteOnce(persist, onDurableCommit);
        }

        /// <summary>
        /// Registered 只撤销且不触碰 Recorder；Failed 确认没有 Recorder 圈，直接精确作废；
        /// 只有 Begun 才执行传入的 Recorder 终态动作。
        /// </summary>
        public bool AbortRecorderOnce(
            Func<bool> persist,
            Action<CycleAttemptContext> onDurableCommit)
        {
            var begin = BeginState;
            if (begin == CycleAttemptBeginState.Registered)
            {
                CancelAttempt();
                return false;
            }
            return begin == CycleAttemptBeginState.Failed
                ? AbortOnce(() => true, onDurableCommit)
                : AbortOnce(persist, onDurableCommit);
        }

        public bool AlarmRecorderOnce(
            Func<bool> persist,
            Action<CycleAttemptContext> onDurableCommit)
        {
            var begin = BeginState;
            if (begin == CycleAttemptBeginState.Registered)
            {
                CancelAttempt();
                return false;
            }
            return begin == CycleAttemptBeginState.Failed
                ? AbortOnce(() => true, onDurableCommit)
                : AlarmOnce(persist, onDurableCommit);
        }

        public void CancelAttempt()
        {
            if (Interlocked.Exchange(ref _cancelRequested, 1) != 0) return;
            try { AttemptCts.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        private bool CommitOnce(
            CycleAttemptTerminalState terminal,
            Func<bool> persist,
            Action<CycleAttemptContext> onDurableCommit)
        {
            lock (_terminalGate)
            {
                var current = (CycleAttemptTerminalState)_terminalState;
                if (_durablyCommitted != 0)
                    return current == terminal && RetryTerminalCleanup(onDurableCommit);
                if (current == CycleAttemptTerminalState.Active)
                    Volatile.Write(ref _terminalState, (int)terminal);
                else if (current != terminal)
                    return false;
                if (_terminalActionOwned != 0) return false;
                _terminalActionOwned = 1;
            }

            bool committed;
            try
            {
                committed = persist?.Invoke() != false;
            }
            catch
            {
                ReleaseFailedTerminalClaim(terminal);
                throw;
            }

            if (!committed)
            {
                ReleaseFailedTerminalClaim(terminal);
                return false;
            }

            lock (_terminalGate)
            {
                Volatile.Write(ref _durablyCommitted, 1);
                _terminalActionOwned = 0;
            }
            _durableCompletion.TrySetResult(true);
            return RetryTerminalCleanup(onDurableCommit);
        }

        private void ReleaseFailedTerminalClaim(CycleAttemptTerminalState terminal)
        {
            lock (_terminalGate)
            {
                _terminalActionOwned = 0;
                if (_durablyCommitted == 0 &&
                    _terminalState == (int)terminal)
                    Volatile.Write(
                        ref _terminalState,
                        (int)CycleAttemptTerminalState.Active);
            }
        }

        private bool RetryTerminalCleanup(Action<CycleAttemptContext> onDurableCommit)
        {
            if (Volatile.Read(ref _terminalCleanupComplete) != 0) return false;
            if (Interlocked.CompareExchange(ref _terminalCleanupOwned, 1, 0) != 0) return false;
            try
            {
                onDurableCommit?.Invoke(this);
                Volatile.Write(ref _terminalCleanupComplete, 1);
                return true;
            }
            catch
            {
                Volatile.Write(ref _terminalCleanupOwned, 0);
                throw;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            AttemptCts.Dispose();
        }
    }

    /// <summary>每通道最多一个活动尝试，并用完整对象引用执行精确移除。</summary>
    public sealed class CycleAttemptRegistry
    {
        private readonly ConcurrentDictionary<int, CycleAttemptContext> _current =
            new ConcurrentDictionary<int, CycleAttemptContext>();
        private readonly ConcurrentDictionary<int, CycleAttemptContext> _lastExecution =
            new ConcurrentDictionary<int, CycleAttemptContext>();
        private readonly ConcurrentDictionary<int, object> _channelGates =
            new ConcurrentDictionary<int, object>();

        public int Count => _current.Count;

        public bool TryRegister(CycleAttemptContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            lock (GetChannelGate(context.Channel))
            {
                if (_current.ContainsKey(context.Channel)) return false;
                if (_lastExecution.TryGetValue(context.Channel, out var previous) &&
                    !previous.IsExecutionCompleted)
                    return false;

                if (!_current.TryAdd(context.Channel, context)) return false;
                _lastExecution[context.Channel] = context;
                return true;
            }
        }

        /// <summary>
        /// 先把身份放入 registry，再调用同步 Begin。Begin 阻塞或抛出时身份始终可观察且不移除。
        /// </summary>
        public bool TryRegisterBeforeBegin(CycleAttemptContext context, Action begin)
        {
            if (!TryRegister(context)) return false;
            begin?.Invoke();
            return true;
        }

        public bool TryGetCurrent(int channel, out CycleAttemptContext context)
        {
            return _current.TryGetValue(channel, out context);
        }

        public bool IsCurrent(CycleAttemptContext context)
        {
            return context != null &&
                   _current.TryGetValue(context.Channel, out var current) &&
                   ReferenceEquals(current, context);
        }

        public bool TryRemoveExact(CycleAttemptContext context)
        {
            if (context == null) return false;
            lock (GetChannelGate(context.Channel))
            {
                var removed =
                    ((ICollection<KeyValuePair<int, CycleAttemptContext>>)_current)
                    .Remove(new KeyValuePair<int, CycleAttemptContext>(context.Channel, context));
                if (removed) context.MarkRegistryRemoved();
                return removed;
            }
        }

        public bool TryGetLastExecution(int channel, out CycleAttemptContext context)
        {
            return _lastExecution.TryGetValue(channel, out context);
        }

        /// <summary>
        /// 发布执行退出并精确清理 tombstone。旧 attempt 的迟到完成永远不会清除新 attempt。
        /// </summary>
        public bool MarkExecutionCompleted(CycleAttemptContext context)
        {
            if (context == null) return false;
            context.MarkExecutionCompleted();
            return TryClearExecutionTombstoneExact(context);
        }

        public bool TryClearExecutionTombstoneExact(CycleAttemptContext context)
        {
            if (context == null) return false;
            lock (GetChannelGate(context.Channel))
            {
                return ((ICollection<KeyValuePair<int, CycleAttemptContext>>)_lastExecution)
                    .Remove(new KeyValuePair<int, CycleAttemptContext>(context.Channel, context));
            }
        }

        public async Task<bool> WaitForPreviousExecutionAsync(
            int channel,
            int timeoutMs,
            CancellationToken token)
        {
            if (!_lastExecution.TryGetValue(channel, out var previous) ||
                previous.IsExecutionCompleted)
                return true;

            var completion = previous.ExecutionCompletion;
            var timeout = Task.Delay(Math.Max(1, timeoutMs), token);
            var winner = await Task.WhenAny(completion, timeout).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return winner == completion;
        }

        private object GetChannelGate(int channel)
        {
            return _channelGates.GetOrAdd(channel, _ => new object());
        }
    }

    /// <summary>
    /// 同步硬件 Begin 期间可撤销的精确令牌租约。撤销发生在令牌返回前时，令牌一旦发布
    /// 便只按自身身份清理；旧租约永远不会按通道误取消后来启动的新租约。
    /// </summary>
    internal sealed class ExactTokenLease<TToken> where TToken : class
    {
        private readonly object _gate = new object();
        private TToken _token;
        private bool _detached;
        private Action<TToken> _cleanupAfterPublish;

        internal bool TryPublish(TToken token, Action<TToken> cleanupIfDetached)
        {
            if (token == null) throw new ArgumentNullException(nameof(token));
            Action<TToken> cleanup = null;
            lock (_gate)
            {
                if (!_detached)
                {
                    _token = token;
                    return true;
                }
                cleanup = _cleanupAfterPublish ?? cleanupIfDetached;
            }
            cleanup?.Invoke(token);
            return false;
        }

        internal bool TryPeek(out TToken token)
        {
            lock (_gate)
            {
                token = _detached ? null : _token;
                return token != null;
            }
        }

        internal bool TryTakePublished(out TToken token)
        {
            lock (_gate)
            {
                _detached = true;
                token = _token;
                _token = null;
                return token != null;
            }
        }

        internal void CancelWhenPublished(Action<TToken> cleanup)
        {
            TToken token = null;
            lock (_gate)
            {
                _detached = true;
                if (_token != null)
                {
                    token = _token;
                    _token = null;
                }
                else
                {
                    _cleanupAfterPublish = cleanup;
                }
            }
            if (token != null) cleanup?.Invoke(token);
        }
    }

    internal sealed class ExactTokenLeaseOwner<TToken> where TToken : class
    {
        private ExactTokenLease<TToken> _current;

        internal ExactTokenLease<TToken> TryReserve()
        {
            var candidate = new ExactTokenLease<TToken>();
            return Interlocked.CompareExchange(ref _current, candidate, null) == null
                ? candidate
                : null;
        }

        internal ExactTokenLease<TToken> DetachCurrent()
        {
            return Interlocked.Exchange(ref _current, null);
        }

        internal bool TryDetachExact(ExactTokenLease<TToken> lease)
        {
            return lease != null &&
                   ReferenceEquals(Interlocked.CompareExchange(ref _current, null, lease), lease);
        }

        internal bool TryPeek(out TToken token)
        {
            var lease = Volatile.Read(ref _current);
            if (lease != null && lease.TryPeek(out token)) return true;
            token = null;
            return false;
        }
    }
}
