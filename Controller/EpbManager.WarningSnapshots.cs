using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Config;
using Controller.Alarm;
using DataOperation;
using IO.NI;

namespace Controller
{
    internal sealed class DaqIncidentEvidenceSubmission
    {
        internal string ContextKey;
        internal Guid RunId;
        internal Guid CorrelationId;
        internal string Device;
        internal DateTime StartedUtc;
        internal string PhaseKey;
        internal string PhaseDirectoryName;
        internal string IncidentJson;
        internal bool IsTrigger;
        internal bool IsTerminal;
        internal Action<string> WriteHeavyEvidence;
    }

    internal sealed class DaqIncidentEvidenceBatch
    {
        internal DaqIncidentEvidenceSubmission Trigger;
        internal DaqIncidentEvidenceSubmission[] Derived = Array.Empty<DaqIncidentEvidenceSubmission>();
        internal DaqIncidentEvidenceSubmission Terminal;

        internal DaqIncidentEvidenceSubmission[] OrderedSubmissions()
        {
            return (Trigger == null
                    ? Enumerable.Empty<DaqIncidentEvidenceSubmission>()
                    : new[] { Trigger })
                .Concat(Derived ?? Array.Empty<DaqIncidentEvidenceSubmission>())
                .Concat(Terminal == null
                    ? Enumerable.Empty<DaqIncidentEvidenceSubmission>()
                    : new[] { Terminal })
                .ToArray();
        }
    }

    /// <summary>
    /// DAQ 事故取证的根上下文聚合队列。容量核复用 WarningSnapshotWorkGate：
    /// 全局最多一个运行任务和一个等待任务；同一根事故的派生症状只合并状态，
    /// 不为每个 phase 创建 Task。trigger/terminal 各自至多导出一次。
    /// </summary>
    internal sealed class DaqIncidentEvidenceQueue
    {
        private sealed class RootState
        {
            private readonly object _gate = new();
            private readonly HashSet<string> _phases = new(StringComparer.OrdinalIgnoreCase);
            private readonly List<DaqIncidentEvidenceSubmission> _derived = new();
            private int _exportedDerivedCount;
            private DaqIncidentEvidenceSubmission _trigger;
            private DaqIncidentEvidenceSubmission _terminal;
            private bool _triggerExported;
            private bool _terminalExported;

            internal RootState(string key) { Key = key; }
            internal string Key { get; }
            internal int QueuedOrRunning;

            internal bool Merge(DaqIncidentEvidenceSubmission submission)
            {
                lock (_gate)
                {
                    if (_terminalExported) return false;
                    var phase = (submission.PhaseKey ?? string.Empty).Trim();
                    if (phase.Length == 0) return false;
                    if (_phases.Contains(phase)) return true;
                    // terminal 注册后根事故已经冻结；迟到派生症状不得写在终态之后。
                    if (_terminal != null && !submission.IsTerminal) return false;

                    if (submission.IsTrigger)
                    {
                        if (_trigger != null) return true;
                        _trigger = submission;
                    }
                    else if (submission.IsTerminal)
                    {
                        if (_terminal != null) return true;
                        _terminal = submission;
                    }
                    else
                    {
                        _derived.Add(submission);
                    }
                    _phases.Add(phase);
                    return true;
                }
            }

            internal bool HasQueueableWork
            {
                get
                {
                    lock (_gate)
                        return (!_triggerExported && _trigger != null) ||
                               (!_terminalExported && _terminal != null);
                }
            }

            internal bool IsComplete
            {
                get { lock (_gate) return _terminalExported; }
            }

            internal DaqIncidentEvidenceBatch Snapshot()
            {
                lock (_gate)
                {
                    var trigger = _triggerExported ? null : _trigger;
                    var terminal = _terminalExported ? null : _terminal;
                    var derived = _derived.Skip(_exportedDerivedCount).ToArray();
                    if (trigger == null && terminal == null && derived.Length == 0) return null;
                    return new DaqIncidentEvidenceBatch
                    {
                        Trigger = trigger,
                        Derived = derived,
                        Terminal = terminal
                    };
                }
            }

            internal void Commit(DaqIncidentEvidenceBatch batch)
            {
                if (batch == null) return;
                lock (_gate)
                {
                    if (batch.Trigger != null && ReferenceEquals(batch.Trigger, _trigger))
                        _triggerExported = true;
                    _exportedDerivedCount = Math.Min(
                        _derived.Count,
                        _exportedDerivedCount + (batch.Derived?.Length ?? 0));
                    if (batch.Terminal != null && ReferenceEquals(batch.Terminal, _terminal))
                        _terminalExported = true;
                }
            }
        }

        private readonly WarningSnapshotWorkGate _capacity = new();
        private readonly ConcurrentDictionary<string, RootState> _contexts =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<RootState> _queue = new();
        private readonly Action<DaqIncidentEvidenceBatch> _export;
        private readonly Action<Task> _observeWorker;
        private readonly Action<Exception> _reportFailure;
        private int _workerRunning;
        private int _workerStartCount;

        internal DaqIncidentEvidenceQueue(
            Action<DaqIncidentEvidenceBatch> export,
            Action<Task> observeWorker = null,
            Action<Exception> reportFailure = null)
        {
            _export = export ?? throw new ArgumentNullException(nameof(export));
            _observeWorker = observeWorker;
            _reportFailure = reportFailure;
        }

        internal int RunningCount => _capacity.RunningCount;
        internal int PendingCount => _capacity.PendingCount;
        internal int ActiveJobCount => _capacity.ActiveJobCount;
        internal int ContextCount => _contexts.Count;
        internal int WorkerStartCount => Volatile.Read(ref _workerStartCount);

        internal bool Submit(DaqIncidentEvidenceSubmission submission)
        {
            if (submission == null || string.IsNullOrWhiteSpace(submission.ContextKey)) return false;
            var created = false;
            RootState state;
            while (!_contexts.TryGetValue(submission.ContextKey, out state))
            {
                if (!submission.IsTrigger) return false;
                var candidate = new RootState(submission.ContextKey);
                if (!_contexts.TryAdd(submission.ContextKey, candidate)) continue;
                state = candidate;
                created = true;
                break;
            }

            if (!state.Merge(submission))
            {
                if (created) _contexts.TryRemove(submission.ContextKey, out _);
                return false;
            }
            if (!submission.IsTrigger && !submission.IsTerminal) return true;
            if (EnsureQueued(state)) return true;
            if (created)
            {
                _contexts.TryRemove(submission.ContextKey, out _);
                return false;
            }
            // 已获准的根事故绝不能因全局 pending 槽短暂占满而丢 terminal；
            // 唯一 worker 在当前工作完成后会扫描并补排，不创建额外 Task。
            return true;
        }

        internal async Task<bool> DrainAsync(int timeoutMs)
        {
            var deadline = Stopwatch.GetTimestamp() +
                           (long)(Math.Max(1, timeoutMs) / 1000d * Stopwatch.Frequency);
            while (true)
            {
                // 未提交 terminal 的根事故也属于未收口状态；关闭 drain 必须在时限内
                // 明确返回 false，不能把仍仅含 trigger/derived 的 manifest 当作已完成。
                if (_contexts.IsEmpty && _queue.IsEmpty &&
                    Volatile.Read(ref _workerRunning) == 0 &&
                    _capacity.ActiveJobCount == 0)
                    return true;
                var remainingMs = (deadline - Stopwatch.GetTimestamp()) * 1000d /
                                  Stopwatch.Frequency;
                if (remainingMs <= 0) return false;
                await Task.Delay((int)Math.Min(10d, Math.Max(1d, remainingMs)))
                    .ConfigureAwait(false);
            }
        }

        private bool EnsureQueued(RootState state)
        {
            if (state == null || !state.HasQueueableWork) return true;
            if (Interlocked.CompareExchange(ref state.QueuedOrRunning, 1, 0) != 0) return true;
            if (!_capacity.TryQueue(
                    state.Key,
                    state.Key,
                    DateTime.UtcNow,
                    minimumIntervalSeconds: 0,
                    pendingCapacity: 1))
            {
                Interlocked.Exchange(ref state.QueuedOrRunning, 0);
                return false;
            }
            _queue.Enqueue(state);
            StartWorker();
            return true;
        }

        private void StartWorker()
        {
            if (Interlocked.CompareExchange(ref _workerRunning, 1, 0) != 0) return;
            Interlocked.Increment(ref _workerStartCount);
            var worker = Task.Factory.StartNew(
                ProcessQueue,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            try { _observeWorker?.Invoke(worker); }
            catch { /* 观察增强不得破坏有界 worker。 */ }
        }

        private void ProcessQueue()
        {
            try
            {
                while (_queue.TryDequeue(out var state))
                {
                    if (!_capacity.TryStart(state.Key))
                    {
                        _queue.Enqueue(state);
                        Thread.Yield();
                        continue;
                    }

                    var exported = false;
                    try
                    {
                        var batch = state.Snapshot();
                        if (batch != null) _export(batch);
                        state.Commit(batch);
                        exported = true;
                    }
                    catch (Exception ex)
                    {
                        try { _reportFailure?.Invoke(ex); } catch { }
                    }
                    finally
                    {
                        _capacity.Complete(state.Key);
                        Interlocked.Exchange(ref state.QueuedOrRunning, 0);
                    }

                    if (state.IsComplete)
                        _contexts.TryRemove(state.Key, out _);
                    else if (exported && state.HasQueueableWork)
                        EnsureQueued(state);
                    ScheduleWaitingContexts(exported ? null : state);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _workerRunning, 0);
                if (!_queue.IsEmpty) StartWorker();
            }
        }

        private void ScheduleWaitingContexts(RootState excluded)
        {
            foreach (var state in _contexts.Values)
            {
                if (_capacity.PendingCount >= 1) return;
                if (ReferenceEquals(state, excluded)) continue;
                if (state.HasQueueableWork) EnsureQueued(state);
            }
        }
    }

    public sealed partial class EpbManager
    {
        public event Action<AdaptiveWarningEvent> ChannelWarningEvidenceRaised;
        public event Action<string> SnapshotExportFailed;
        public event Action<WarningSnapshotStorageStatus> WarningSnapshotStorageChanged;

        private readonly ConcurrentDictionary<string, WarningSnapshotRequest> _pendingWarningSnapshots = new();
        private readonly ConcurrentQueue<WarningSnapshotRequest> _warningSnapshotQueue = new();
        private readonly WarningSnapshotWorkGate _warningSnapshotWorkGate = new();
        private readonly ConcurrentDictionary<string, ConcurrentQueue<WarningSnapshotLink>> _warningChains = new();
        private readonly ConcurrentDictionary<string, object> _warningSnapshotCategoryGates =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<WarningScalarEvidence> _warningScalarQueue = new();
        private readonly ConcurrentDictionary<Guid, string> _daqIncidentDirectories = new();
        private readonly ConcurrentDictionary<int, int> _formalPersistenceRecoveryAttempts = new();
        private readonly ConcurrentDictionary<int, int> _formalControlRecoveryAttempts = new();
        private readonly ConcurrentDictionary<int, int> _formalPersistenceRecoveryPendingCycles = new();
        private readonly object _daqIncidentEvidenceQueueInitGate = new();
        private DaqIncidentEvidenceQueue _daqIncidentEvidenceQueue;
        private int _warningSnapshotFreeSpaceWarningActive;
        private int _warningSnapshotWorkerRunning;
        private int _warningScalarWorkerRunning;
        private int _warningScalarQueueCount;
        private int _warningScalarDropped;
        private RuntimeBuildIdentity _warningScalarBuildIdentity;
        private RuntimeBuildIdentity _daqIncidentBuildIdentity;
        private readonly object _warningSnapshotStorageCacheGate = new();
        private WarningSnapshotStorageStatus _warningSnapshotStorageCache;
        private DateTime _warningSnapshotStorageCacheUtc = DateTime.MinValue;
        private string _warningSnapshotTrackedRoot = string.Empty;
        private long _warningSnapshotTrackedUsedBytes;
        private long _warningSnapshotTrackedCount;
        private bool _warningSnapshotStorageCounterInitialized;
        private static readonly Regex WarningEventDirectoryPattern = new(
            @"^\d{8}_\d{9}-Cycle-?\d+-Streak\d+of\d+$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        internal sealed class WarningScalarEvidence
        {
            public Guid RunId;
            public int Channel;
            public int CycleNumber;
            public long AttemptId;
            public DateTime OccurredUtc;
            public string Code;
            public double EvidenceLagMs;
            public int PersistenceQueueDepth;
            public double PeakCurrentA;
            public double TargetCurrentA;
            public double PeakErrorA;
            public int Streak;
            public int ConfirmThreshold;
            public string Reason;
        }

        /// <summary>
        /// 软件自愈只能在本通道输出已可靠关闭后继续。若高优先级关闭命令失败，
        /// 立即进入同电源组失效安全自恢复，保持断电重试；只有确认断电后才允许自动续测。
        /// </summary>
        private bool TryEnsureSoftwareRecoveryOutputOff(int channel, string context)
        {
            var offSucceeded = false;
            try { offSucceeded = CommandEpbOffSafetyImmediate(channel); } catch { }
            if (offSucceeded) return true;

            RequestElectricalGroupEmergencyShutdown(
                channel,
                $"{context} OutputOffCommandFailed");
            _log?.Error(
                $"EPB[{channel}] 软件自愈前无法确认电机断电，已进入电源组失效安全自恢复。" +
                $"Context={context}",
                "EPB");
            return false;
        }

        private void RequireSoftwareRecoveryOutputOff(int channel, string context)
        {
            if (!TryEnsureSoftwareRecoveryOutputOff(channel, context))
                throw new EpbOutputCommandException(channel, context + "Off");
        }

        private void OnRunnerWarningEvidenceRaised(AdaptiveWarningEvent warning)
        {
            if (warning == null) return;
            if (warning.OccurredUtc == default) warning.OccurredUtc = DateTime.UtcNow;
            if (warning.AttemptId <= 0 &&
                _currentAttemptIdByChannel.TryGetValue(warning.Channel, out var attemptId))
                warning.AttemptId = attemptId;
            if (string.IsNullOrWhiteSpace(warning.ScopeKey))
                warning.ScopeKey = $"Channel:{warning.Channel}";
            NonCriticalObserver.Invoke(
                ChannelWarningEvidenceRaised,
                warning,
                ex => _log?.Warn($"预警证据观察者异常，已隔离：{ex.Message}", "EPB"));
            var cfg = AlarmConfig?.WarningSnapshots ?? new WarningSnapshotConfig();
            if (!cfg.Enabled) return;
            // 正式圈为正数，学习圈为负数；只有 0/不存在才表示尚未进入任何圈。
            var hasCycle = _currentCycleNumberByChannel.TryGetValue(
                warning.Channel,
                out var cycleNumber) && cycleNumber != 0;
            QueueWarningScalarEvidence(warning, hasCycle ? cycleNumber : 0, cfg);
            if (!cfg.FullEvidenceEnabled) return;

            if (!hasCycle)
            {
                ReportSnapshotFailure($"EPB[{warning.Channel}] 预警发生时没有有效圈号，未保存软预警快照。");
                return;
            }
            if (!(Recorder is ICycleEvidenceExporter))
            {
                ReportSnapshotFailure("当前圈记录器不支持 ICycleEvidenceExporter，已拒绝静默丢失预警证据。");
                return;
            }

            var request = new WarningSnapshotRequest
            {
                TestRunId = _activeBatchId,
                Channel = warning.Channel,
                CycleNumber = cycleNumber,
                Warning = warning
            };
            if (_pendingWarningSnapshots.TryAdd(request.IdempotencyKey, request))
            {
                // 在控制回调只登记不可变请求与确定性路径；实际文件导出仍在封圈后的后台线程。
                // 这样硬报警紧随其后时 warning-chain 不依赖后台IO完成时序。
                var chainKey = GetWarningChainKey(request.Channel, warning.NormalizedCode);
                var queue = _warningChains.GetOrAdd(chainKey, _ => new ConcurrentQueue<WarningSnapshotLink>());
                queue.Enqueue(new WarningSnapshotLink
                {
                    CycleNumber = request.CycleNumber,
                    Streak = warning.Streak,
                    ConfirmThreshold = warning.ConfirmThreshold,
                    RelativePath = MakeRelativePath(
                        Path.Combine(_cfg.Test.StoreDir, _cfg.Test.TestName),
                        GetWarningSnapshotDirectory(request, cfg))
                });
            }
        }

        private bool CompleteCycleAndScheduleEvidence(
            IEpbCycleRecorder recorder,
            int channel,
            int cycleNumber,
            int finalSampleCount,
            DateTime endUtc)
        {
            if (recorder == null) return true;
            // DAQ恢复入口会在硬件安全动作前锁存事故圈。任何迟到的旧Runner/兼容
            // 回调都只能重复确认 AbortedBySoftwareRecovery，不能把同一圈改写为
            // 正式 completed；持久化作废由当前 attempt 或恢复 Finalizer 的唯一所有者负责。
            if (IsDaqClockCycleAborted(
                    _activeBatchId,
                    Interlocked.Read(ref _runEpoch),
                    channel,
                    cycleNumber))
            {
                if (_cycleAttempts.TryGetCurrent(channel, out var recoveryAttempt) &&
                    recoveryAttempt.Cycle == cycleNumber)
                {
                    AbortFormalCycleAttempt(
                        recoveryAttempt,
                        recorder,
                        endUtc,
                        "AbortedBySoftwareRecovery");
                }
                _log.Warn(
                    $"EPB[{channel}] 迟到圈完成回调被DAQ恢复事故标记拦截。" +
                    $"Cycle={cycleNumber} Status=AbortedBySoftwareRecovery；跳过正式完成。",
                    "落盘");
                return false;
            }
            try
            {
                finalSampleCount = FinalizeCyclePersistence(
                    recorder,
                    channel,
                    cycleNumber,
                    endUtc,
                    finalSampleCount);
                recorder.CompleteCycle(channel, cycleNumber, finalSampleCount, endUtc);
            }
            catch (Exception ex)
            {
                PreserveFormalCycleForPersistenceRecovery(
                    channel,
                    cycleNumber,
                    "CompleteCycle",
                    ex);
                return false;
            }

            try
            {
                QueuePendingWarningSnapshotsForCycle(channel, cycleNumber);
            }
            catch (Exception ex)
            {
                _log.Warn(
                    $"EPB[{channel}] 正式圈已可靠封存，但软预警证据调度失败：{ex.Message}",
                    "落盘");
            }

            try { QueueRollingHistoricalSnapshot(channel, cycleNumber); }
            catch (Exception ex)
            {
                _log.Warn(
                    $"EPB[{channel}] 正式圈已可靠封存，但滚动历史快照调度失败：{ex.Message}",
                    "落盘");
            }
            return true;
        }

        private void QueuePendingWarningSnapshotsForCycle(int channel, int cycleNumber)
        {
            foreach (var item in _pendingWarningSnapshots.ToArray())
            {
                var request = item.Value;
                if (request.Channel != channel || request.CycleNumber != cycleNumber) continue;
                if (!_pendingWarningSnapshots.TryRemove(item.Key, out request)) continue;
                QueueWarningSnapshot(request);
            }
        }

        private void QueueWarningScalarEvidence(
            AdaptiveWarningEvent warning,
            int cycleNumber,
            WarningSnapshotConfig cfg)
        {
            var capacity = Math.Max(128, cfg?.ScalarEvidenceQueueCapacity ?? 4096);
            var count = Interlocked.Increment(ref _warningScalarQueueCount);
            if (count > capacity)
            {
                Interlocked.Decrement(ref _warningScalarQueueCount);
                if (Interlocked.Increment(ref _warningScalarDropped) == 1)
                    _log.Warn(
                        $"WarningScalarQueueDropped=1 Capacity={capacity}；" +
                        "软预警标量证据队列达到硬容量；" +
                        "已锁存聚合丢弃计数，不在控制线程执行文件IO。",
                        "落盘");
                return;
            }

            var queueDepth = 0;
            try
            {
                var device = _acq.GetDeviceForEpbChannel(warning.Channel);
                queueDepth = _persistence?.GetSnapshot(device)?.QueueDepth ?? 0;
            }
            catch { }
            _warningScalarQueue.Enqueue(new WarningScalarEvidence
            {
                RunId = _activeBatchId,
                Channel = warning.Channel,
                CycleNumber = cycleNumber,
                AttemptId = warning.AttemptId,
                OccurredUtc = warning.OccurredUtc,
                Code = warning.NormalizedCode,
                EvidenceLagMs = warning.EvidenceLagMs,
                PersistenceQueueDepth = queueDepth,
                PeakCurrentA = warning.PeakCurrentA,
                TargetCurrentA = warning.TargetCurrentA,
                PeakErrorA = warning.PeakErrorA,
                Streak = warning.Streak,
                ConfirmThreshold = warning.ConfirmThreshold,
                Reason = warning.Reason ?? string.Empty
            });
            StartWarningScalarWorker();
        }

        private void StartWarningScalarWorker()
        {
            if (Interlocked.CompareExchange(ref _warningScalarWorkerRunning, 1, 0) != 0)
                return;
            var worker = Task.Factory.StartNew(
                ProcessWarningScalarQueue,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            ObserveBackgroundTask(worker, "WarningScalarJournal");
        }

        private void ProcessWarningScalarQueue()
        {
            var failureAttempt = 0;
            try
            {
                while (!_warningScalarQueue.IsEmpty)
                {
                    try
                    {
                        var batch = _warningScalarQueue.ToArray().Take(128).ToArray();
                        if (batch.Length == 0) break;
                        var cfg = AlarmConfig?.WarningSnapshots ?? new WarningSnapshotConfig();
                        var root = Path.Combine(
                            _cfg.Test.StoreDir,
                            _cfg.Test.TestName,
                            string.IsNullOrWhiteSpace(cfg.RootDirectory)
                                ? "WarningSnapshots"
                                : cfg.RootDirectory.Trim());
                        EnsureWarningSnapshotStorageCounter(root);
                        Directory.CreateDirectory(root);
                        var identity = _warningScalarBuildIdentity ??
                                       (_warningScalarBuildIdentity = RuntimeBuildIdentity.Capture());
                        var path = Path.Combine(
                            root,
                            $"warning-events-{batch[0].OccurredUtc:yyyyMMdd}.jsonl");
                        var previousBytes = File.Exists(path) ? new FileInfo(path).Length : 0L;
                        File.AppendAllLines(
                            path,
                            batch.Select(item => BuildWarningScalarJson(item, identity)),
                            new UTF8Encoding(false));
                        for (var i = 0; i < batch.Length; i++)
                        {
                            if (!_warningScalarQueue.TryDequeue(out _)) break;
                            Interlocked.Decrement(ref _warningScalarQueueCount);
                        }
                        var dropped = Interlocked.Exchange(ref _warningScalarDropped, 0);
                        if (dropped > 0)
                            File.AppendAllText(
                                path,
                                BuildWarningScalarDropJson(dropped, identity) + Environment.NewLine,
                                new UTF8Encoding(false));
                        var currentBytes = File.Exists(path) ? new FileInfo(path).Length : previousBytes;
                        TrackWarningSnapshotBytes(Math.Max(0L, currentBytes - previousBytes), 0);
                        if (failureAttempt > 0)
                            _log.Info(
                                $"软预警标量证据写入已恢复，重试次数={failureAttempt}。",
                                "落盘");
                        failureAttempt = 0;
                    }
                    catch (Exception ex)
                    {
                        failureAttempt++;
                        if (failureAttempt == 1 || failureAttempt % 10 == 0)
                            _log.Warn(
                                $"软预警标量证据批量写入失败，保留队列并退避重试。" +
                                $"Attempt={failureAttempt} Error={ex.Message}",
                                "落盘");
                        Thread.Sleep(Math.Min(30000, 1000 * failureAttempt));
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _warningScalarWorkerRunning, 0);
                if (!_warningScalarQueue.IsEmpty) StartWarningScalarWorker();
            }
        }

        internal static string BuildWarningScalarJson(
            WarningScalarEvidence item,
            RuntimeBuildIdentity identity)
        {
            return "{" +
                   $"\"occurredUtc\":\"{item.OccurredUtc:O}\"," +
                   $"\"runId\":\"{item.RunId:N}\"," +
                   $"\"channel\":{item.Channel},\"cycle\":{item.CycleNumber}," +
                   $"\"attemptId\":{item.AttemptId},\"code\":\"{JsonEscape(item.Code)}\"," +
                   $"\"lagMs\":{JsonNumberOrNull(item.EvidenceLagMs)}," +
                   $"\"persistenceQueueDepth\":{item.PersistenceQueueDepth}," +
                   $"\"peakA\":{JsonNumberOrNull(item.PeakCurrentA)}," +
                   $"\"targetA\":{JsonNumberOrNull(item.TargetCurrentA)}," +
                   $"\"errorA\":{JsonNumberOrNull(item.PeakErrorA)}," +
                   $"\"streak\":{item.Streak},\"confirmThreshold\":{item.ConfirmThreshold}," +
                   $"\"reason\":\"{JsonEscape(item.Reason)}\"," +
                   $"\"productVersion\":\"{JsonEscape(identity.ProductVersion)}\"," +
                   $"\"processId\":{identity.ProcessId}," +
                   $"\"executablePath\":\"{JsonEscape(identity.ExecutablePath)}\"," +
                   $"\"executableSha256\":\"{JsonEscape(identity.ExecutableSha256)}\"," +
                   $"\"gitCommit\":\"{JsonEscape(identity.GitCommit)}\"," +
                   $"\"buildUtc\":\"{JsonEscape(identity.BuildUtc)}\"," +
                   $"\"releaseConfigSha256\":\"{JsonEscape(identity.ReleaseConfigSha256)}\"" +
                   "}";
        }

        private static string BuildWarningScalarDropJson(
            int dropped,
            RuntimeBuildIdentity identity)
        {
            return "{" +
                   $"\"occurredUtc\":\"{DateTime.UtcNow:O}\"," +
                   "\"code\":\"WarningScalarQueueDropped\"," +
                   $"\"dropped\":{Math.Max(0, dropped)}," +
                   $"\"productVersion\":\"{JsonEscape(identity.ProductVersion)}\"," +
                   $"\"processId\":{identity.ProcessId}" +
                   "}";
        }

        private static string JsonNumberOrNull(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value)
                ? "null"
                : value.ToString("R", CultureInfo.InvariantCulture);
        }

        private int FinalizeCyclePersistence(
            IEpbCycleRecorder recorder,
            int channel,
            int cycleNumber,
            DateTime endUtc,
            int fallbackCount)
        {
            if (recorder == null) return fallbackCount;
            var cycles = new System.Collections.Generic.Dictionary<int, int>
            {
                [channel] = cycleNumber
            };
            var durable = TryWaitForCycleDurableCutoffAsync(
                    cycles,
                    endUtc,
                    $"CycleFinalize:{cycleNumber}",
                    _daqPersistenceRecoveryTimeoutMs,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            if (!durable)
                throw new TimeoutException(
                    $"圈收尾 Raw/耐久前缀未闭合。EPB={channel} Cycle={cycleNumber}");
            return recorder.GetCurrentCycleSampleCount(channel);
        }

        private void AbortCycleAfterPersistence(
            IEpbCycleRecorder recorder,
            int channel,
            int cycleNumber,
            DateTime endUtc,
            string status)
        {
            if (recorder == null) return;
            var finalN = recorder.GetCurrentCycleSampleCount(channel);
            finalN = FinalizeCyclePersistence(
                recorder,
                channel,
                cycleNumber,
                endUtc,
                finalN);
            recorder.AbortCycle(channel, cycleNumber, finalN, endUtc, status);
            QueuePendingWarningSnapshotsForCycle(channel, cycleNumber);
        }

        /// <summary>
        /// 调用方已经针对事故首次冻结的唯一前缀完成 Raw/SQLite 耐久证明时提交圈终态。
        /// 此路径严禁再次读取 LastDiskPublishedSequence，否则 SuppressAfter 之后的新鲜度
        /// 验证批次会被错误扩大为新的正式落盘义务。
        /// </summary>
        private void AbortCycleAtConfirmedDurableBoundary(
            IEpbCycleRecorder recorder,
            int channel,
            int cycleNumber,
            DateTime endUtc,
            string status)
        {
            if (recorder == null) return;
            var finalN = recorder.GetCurrentCycleSampleCount(channel);
            recorder.AbortCycle(channel, cycleNumber, finalN, endUtc, status);
            QueuePendingWarningSnapshotsForCycle(channel, cycleNumber);
        }

        private bool TryBeginFormalCycle(
            IEpbCycleRecorder recorder,
            int channel,
            int cycleNumber,
            DateTime beginUtc)
        {
            if (recorder == null)
            {
                MarkCurrentCycleNumber(channel, cycleNumber);
                return true;
            }
            try
            {
                recorder.BeginCycle(channel, cycleNumber, beginUtc);
                MarkCurrentCycleNumber(channel, cycleNumber);
                return true;
            }
            catch (Exception ex)
            {
                _currentCycleNumberByChannel.TryAdd(channel, cycleNumber);
                ReportFormalPersistenceRecovery(channel, cycleNumber, "BeginCycle", ex);
                return false;
            }
        }

        private void PreserveFormalCycleForPersistenceRecovery(
            int channel,
            int cycleNumber,
            string stage,
            Exception cause)
        {
            _currentCycleNumberByChannel.TryAdd(channel, cycleNumber);
            ReportFormalPersistenceRecovery(channel, cycleNumber, stage, cause);
        }

        private void ReportFormalPersistenceRecovery(
            int channel,
            int cycleNumber,
            string stage,
            Exception cause)
        {
            var attempt = _formalPersistenceRecoveryAttempts.AddOrUpdate(channel, 1, (_, old) => old + 1);
            _formalPersistenceRecoveryPendingCycles[channel] = cycleNumber;
            _currentCycleNumberByChannel.TryAdd(channel, cycleNumber);
            if (!TryEnsureSoftwareRecoveryOutputOff(channel, "FormalPersistenceSelfHealing"))
            {
                return;
            }
            if (_timers.TryGetValue(channel, out var timer))
            {
                try { timer.Pause("FormalPersistenceSelfHealing"); } catch { }
            }
            try { CancelCyclePauseCts(channel); } catch { }
            UnmarkHydraulicParticipant(channel);
            try { ObserveSafetyTask(HydraulicMarkReleaseAsync(channel), "FormalPersistenceRecovery", channel); }
            catch { }
            PublishChannelRuntimeState(
                channel,
                ChannelRuntimeState.Recovering,
                "FormalPersistenceSelfHealing",
                $"正式圈落盘软件自愈第{attempt}次；当前尝试已作废且不计数，未来完整圈自动重试。",
                affectedChannels: new[] { channel },
                correlationId: _activeBatchId,
                allowTerminalReset: false);
            _log.Warn(
                $"EPB[{channel}] 正式圈落盘软件异常；保留圈事务并等待真实耐久收口。" +
                $"Cycle={cycleNumber} Stage={stage} Attempt={attempt} Error={cause?.Message}",
                "落盘");
            ScheduleIsolatedInfrastructureRecovery(
                new[] { channel },
                $"FormalPersistence:{stage}:Cycle={cycleNumber}",
                _activeBatchId);
        }

        private void ReportFormalControlSoftwareRecovery(
            int channel,
            int cycleNumber,
            string reason)
        {
            var attempt = _formalControlRecoveryAttempts.AddOrUpdate(channel, 1, (_, old) => old + 1);
            if (!TryEnsureSoftwareRecoveryOutputOff(channel, "FormalControlSelfHealing"))
            {
                return;
            }
            try { ObserveSafetyTask(HydraulicMarkReleaseAsync(channel), "FormalControlRecovery", channel); }
            catch { }
            PublishChannelRuntimeState(
                channel,
                ChannelRuntimeState.Recovering,
                "FormalControlSelfHealing",
                $"正式圈控制软件自愈第{attempt}次；当前圈已作废且不计数，未来完整圈自动重试。",
                affectedChannels: new[] { channel },
                correlationId: _activeBatchId,
                allowTerminalReset: false);
            _log.Warn(
                $"EPB[{channel}] 正式圈控制软件异常已安全断电并作废。" +
                $"Cycle={cycleNumber} Attempt={attempt} Reason={reason}",
                "EPB");
        }

        private void CompleteFormalSoftwareRecoveryAfterCommit(int channel)
        {
            var hadPersistence = _formalPersistenceRecoveryAttempts.TryRemove(
                channel,
                out var persistenceAttempts);
            _formalPersistenceRecoveryPendingCycles.TryRemove(channel, out _);
            var hadControl = _formalControlRecoveryAttempts.TryRemove(
                channel,
                out var controlAttempts);
            if (!hadPersistence && !hadControl) return;

            PublishChannelRuntimeState(
                channel,
                ChannelRuntimeState.Running,
                "FormalSoftwareSelfHealed",
                $"正式圈软件自愈完成；此前控制作废{controlAttempts}次、落盘作废{persistenceAttempts}次，" +
                "本圈已可靠完成。",
                affectedChannels: new[] { channel },
                correlationId: _activeBatchId,
                allowTerminalReset: false);
            _log.Info(
                $"EPB[{channel}] 正式圈软件自愈完成。" +
                $"ControlDiscarded={controlAttempts} PersistenceDiscarded={persistenceAttempts}。",
                "EPB");
        }

        internal static bool IsFormalCycleCountable(
            bool controlSucceeded,
            bool persistenceCommitted)
        {
            return controlSucceeded && persistenceCommitted;
        }

        internal static bool IsFormalControlSucceeded(
            bool workReturnedSuccess,
            bool currentOutcomeSucceeded)
        {
            // LastCycleOutcome 是 Runner 上的可观察快照。若本次调用在更新它之前抛异常，
            // 该快照可能仍是上一圈的 Success，因此必须同时要求本次调用正常返回 true。
            return workReturnedSuccess && currentOutcomeSucceeded;
        }

        private void SubmitDaqIncidentSnapshot(
            DaqAutoRecoveryContext context,
            string reason,
            string result)
        {
            try
            {
                SubmitDaqIncidentSnapshotCore(context, reason, result);
            }
            catch (Exception ex)
            {
                _log.Error(
                    $"DAQ事故取证提交失败：Phase={result} Device={context?.Device} " +
                    $"CorrelationId={context?.CorrelationId:N} Error={ex.Message}",
                    "落盘",
                    ex);
            }
        }

        private void SubmitDaqIncidentSnapshotCore(
            DaqAutoRecoveryContext context,
            string reason,
            string result)
        {
            if (context == null) return;
            var phaseKey = string.IsNullOrWhiteSpace(result) ? "phase" : result.Trim();
            if (!context.SnapshotPhases.TryAdd(phaseKey, 0)) return;
            // Only cheap scalar state is frozen on the caller. Full diagnostic/cycle evidence
            // is captured by the bounded incident worker; recovery only submits immutable data.
            var capturedUtc = DateTime.UtcNow;
            var includeFullEvidence = ShouldIncludeFullDaqIncidentEvidence(result);
            var includeTimingEvidence = ShouldIncludeDaqTimingEvidence(result);
            // 恢复终态默认只保留紧凑诊断。完整单圈数据已经由正式圈文件保存，
            // 再复制每通道最近 10 圈会把一次恢复放大到数百 MB，并直接拖慢试验。
            var includeRecentCycleCopies = includeFullEvidence &&
                                           ReadBooleanAppSetting("DaqIncidentCopyRecentCycles", false);
            var queue = _persistence.GetSnapshot(context.Device);
            var runEpoch = context.RunEpoch;
            var recoveryEpoch = context.RecoveryEpoch;
            var runId = context.RunId == Guid.Empty ? _activeBatchId : context.RunId;
            var beforeClock = context.BeforeClock ?? new DaqFreshnessSnapshot();
            var afterClock = context.AfterClock ?? _acq.GetDaqFreshnessSnapshot(context.Device, 100);
            var generation = _acq.GetCurrentGeneration(context.Device);
            var previousGeneration = context.PreviousGeneration;
            var recoveredGeneration = context.RecoveredGeneration;
            var firstVerifiedSequence = context.FirstVerifiedSequence;
            var lastVerifiedSequence = context.LastVerifiedSequence;
            var recoveryAttempt = context.RecoveryAttempt;
            var affectedChannels = (context.AffectedChannels ?? Array.Empty<int>()).ToArray();
            var powerStates = _powerSupply == null
                ? Array.Empty<PowerSupplyRuntimeState>()
                : _cfg.Test.Groups
                    .Where(group => group.Members.Any(affectedChannels.Contains))
                    .Select(group => _powerSupply.GetRuntimeState(group.Id))
                    .ToArray();
            var powerStatesJson = string.Join(",", powerStates.Select(state => "{" +
                $"\"group\":{state.ElectricalGroupId}," +
                $"\"operationEpoch\":{state.OperationEpoch}," +
                $"\"expectedOn\":{state.ExpectedOutputEnabled.ToString().ToLowerInvariant()}," +
                $"\"planned\":{state.PlannedTransition.ToString().ToLowerInvariant()}," +
                $"\"active\":{state.Active.ToString().ToLowerInvariant()}," +
                $"\"telemetryOn\":{state.TelemetryOutputEnabled.ToString().ToLowerInvariant()}," +
                $"\"protection\":{state.ProtectionTripped.ToString().ToLowerInvariant()}," +
                $"\"telemetryUtc\":\"{state.TelemetryUtc:O}\"" + "}"));
            var sequence = Interlocked.Increment(ref context.SnapshotSequence);
            var requiredFresh = string.Equals(
                context.TriggerCode,
                "DaqClockModelInvalid",
                StringComparison.OrdinalIgnoreCase)
                ? _daqClockRecoveryFreshBatches
                : _daqPersistenceRequiredFreshBatches;
            var incidentJson =
                "{\n" +
                $"  \"device\": \"{JsonEscape(context.Device)}\",\n" +
                $"  \"correlationId\": \"{context.CorrelationId:N}\",\n" +
                $"  \"runId\": \"{runId:N}\",\n" +
                $"  \"runEpoch\": {runEpoch},\n" +
                $"  \"recoveryEpoch\": {recoveryEpoch},\n" +
                $"  \"faultCode\": \"{JsonEscape(context.TriggerCode)}\",\n" +
                $"  \"reason\": \"{JsonEscape(reason)}\",\n" +
                $"  \"generation\": {generation},\n" +
                $"  \"previousGeneration\": {previousGeneration},\n" +
                $"  \"recoveredGeneration\": {recoveredGeneration},\n" +
                $"  \"firstVerifiedSequence\": {firstVerifiedSequence},\n" +
                $"  \"lastVerifiedSequence\": {lastVerifiedSequence},\n" +
                $"  \"recoveryAttempt\": {recoveryAttempt},\n" +
                $"  \"clockRecoveryMaxAttempts\": {_daqClockRecoveryMaxAttempts},\n" +
                $"  \"clockRecoveryWindowMinutes\": {_daqClockRecoveryWindowMinutes},\n" +
                $"  \"beforeClockState\": \"{beforeClock.ClockState}\",\n" +
                $"  \"beforeSampleRateHz\": {beforeClock.EffectiveSampleRateHz.ToString("F6", CultureInfo.InvariantCulture)},\n" +
                $"  \"beforeSkewPpm\": {beforeClock.EstimatedSkewPpm.ToString("F3", CultureInfo.InvariantCulture)},\n" +
                $"  \"beforeResidualMs\": {beforeClock.ClockResidualMs.ToString("F3", CultureInfo.InvariantCulture)},\n" +
                $"  \"beforeCallbackAgeMs\": {beforeClock.CallbackAgeMs.ToString("F3", CultureInfo.InvariantCulture)},\n" +
                $"  \"beforeCallbackGapEvents\": {beforeClock.CallbackGapEventCount},\n" +
                $"  \"beforeLastCallbackGapMs\": {beforeClock.LastCallbackGapIntervalMs.ToString("F3", CultureInfo.InvariantCulture)},\n" +
                $"  \"beforeControlEnqueueAgeMs\": {beforeClock.ControlEnqueueAgeMs.ToString("F3", CultureInfo.InvariantCulture)},\n" +
                $"  \"beforeControlProcessedAgeMs\": {beforeClock.ControlProcessedAgeMs.ToString("F3", CultureInfo.InvariantCulture)},\n" +
                $"  \"beforeSampleAgeMs\": {beforeClock.AgeMs.ToString("F3", CultureInfo.InvariantCulture)},\n" +
                $"  \"beforeProcessedSampleUtc\": \"{beforeClock.ProcessedSampleUtc:O}\",\n" +
                $"  \"afterClockState\": \"{afterClock.ClockState}\",\n" +
                $"  \"afterSampleRateHz\": {afterClock.EffectiveSampleRateHz.ToString("F6", CultureInfo.InvariantCulture)},\n" +
                $"  \"afterSkewPpm\": {afterClock.EstimatedSkewPpm.ToString("F3", CultureInfo.InvariantCulture)},\n" +
                $"  \"afterResidualMs\": {afterClock.ClockResidualMs.ToString("F3", CultureInfo.InvariantCulture)},\n" +
                $"  \"afterCallbackAgeMs\": {afterClock.CallbackAgeMs.ToString("F3", CultureInfo.InvariantCulture)},\n" +
                $"  \"afterCallbackGapEvents\": {afterClock.CallbackGapEventCount},\n" +
                $"  \"afterLastCallbackGapMs\": {afterClock.LastCallbackGapIntervalMs.ToString("F3", CultureInfo.InvariantCulture)},\n" +
                $"  \"afterControlEnqueueAgeMs\": {afterClock.ControlEnqueueAgeMs.ToString("F3", CultureInfo.InvariantCulture)},\n" +
                $"  \"afterControlProcessedAgeMs\": {afterClock.ControlProcessedAgeMs.ToString("F3", CultureInfo.InvariantCulture)},\n" +
                $"  \"afterSampleAgeMs\": {afterClock.AgeMs.ToString("F3", CultureInfo.InvariantCulture)},\n" +
                $"  \"afterProcessedSampleUtc\": \"{afterClock.ProcessedSampleUtc:O}\",\n" +
                $"  \"queueDepth\": {queue.QueueDepth},\n" +
                $"  \"oldestBatchAgeMs\": {queue.OldestBatchAgeMs.ToString("F3", CultureInfo.InvariantCulture)},\n" +
                $"  \"affectedChannels\": [{string.Join(",", affectedChannels)}],\n" +
                $"  \"powerStates\": [{powerStatesJson}],\n" +
                $"  \"processingQueueCapacity\": {_acq.ProcessingQueueCapacity},\n" +
                $"  \"persistenceQueueCapacity\": {_daqPersistenceQueueCapacity},\n" +
                $"  \"pauseDepth\": {_daqPersistencePauseDepth},\n" +
                $"  \"resumeDepth\": {_daqPersistenceResumeDepth},\n" +
                $"  \"pauseAgeMs\": {_daqPersistencePauseAgeMs.ToString("F0", CultureInfo.InvariantCulture)},\n" +
                $"  \"resumeAgeMs\": {_daqPersistenceResumeAgeMs.ToString("F0", CultureInfo.InvariantCulture)},\n" +
                $"  \"recoveryTimeoutMs\": {_daqPersistenceRecoveryTimeoutMs},\n" +
                $"  \"requiredFreshBatches\": {requiredFresh},\n" +
                $"  \"suppressedBatches\": {queue.SuppressedBatchCount},\n" +
                $"  \"cumulativeSuppressedBatches\": {queue.CumulativeSuppressedBatchCount},\n" +
                $"  \"lastTerminallyHandledSequence\": {queue.LastTerminallyHandledSequence},\n" +
                $"  \"suppressAfterSequence\": {queue.SuppressAfterSequence},\n" +
                $"  \"suppressThroughSequence\": {queue.SuppressThroughSequence},\n" +
                $"  \"firstSuppressedSequence\": {queue.FirstSuppressedSequence},\n" +
                $"  \"lastSuppressedSequence\": {queue.LastSuppressedSequence},\n" +
                $"  \"suppressedRangeCount\": {queue.SuppressedRangeCount},\n" +
                $"  \"discardedGenerationBatches\": {queue.DiscardedGenerationBatchCount},\n" +
                $"  \"validationPhase\": \"{JsonEscape(context.ValidationPhase)}\",\n" +
                $"  \"timingEvidenceIncluded\": {includeTimingEvidence.ToString().ToLowerInvariant()},\n" +
                $"  \"fullEvidenceIncluded\": {includeFullEvidence.ToString().ToLowerInvariant()},\n" +
                $"  \"recentCycleCopiesIncluded\": {includeRecentCycleCopies.ToString().ToLowerInvariant()},\n" +
                "  \"validBatchesDroppedByClockModel\": 0,\n" +
                $"  \"result\": \"{JsonEscape(result)}\",\n" +
                $"  \"capturedUtc\": \"{capturedUtc:O}\"\n" +
                "}\n";
            var isTrigger = phaseKey.StartsWith("00-trigger", StringComparison.OrdinalIgnoreCase);
            var isTerminal = phaseKey.StartsWith("90-", StringComparison.OrdinalIgnoreCase);
            var safePhase = string.Concat(phaseKey
                .Select(ch => char.IsLetterOrDigit(ch) || ch == '-' ? ch : '_'));
            var recorder = includeRecentCycleCopies ? Recorder : null;
            // CaptureDiagnostics 只做有界 ring 的内存快照，不触碰文件。必须在 Submit
            // 时冻结，否则前一份证据写盘稍慢就会让 trigger 前 10 秒记录被 ring 覆盖。
            var frozenDiagnostics = includeTimingEvidence
                ? _acq.CaptureDiagnostics(
                    new[] { context.Device },
                    includeFullEvidence ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(10))
                : null;
            Action<string> writeHeavyEvidence = null;
            if (includeTimingEvidence)
            {
                writeHeavyEvidence = phaseDirectory =>
                {
                    // worker 只序列化已经冻结的证据，绝不重新读取实时 ring。
                    frozenDiagnostics?.WriteTo(phaseDirectory);
                    if (recorder != null)
                    {
                        foreach (var channel in affectedChannels)
                        {
                            var sub = Path.Combine(phaseDirectory, $"EPB{channel:D2}");
                            Directory.CreateDirectory(sub);
                            try { recorder.FlushRecentTo(channel, 10, sub, includeRunningCycle: true); }
                            catch (Exception ex) { _log.Warn($"Incident EPB[{channel}] 证据导出失败：{ex.Message}", "落盘"); }
                        }
                    }
                };
            }

            var submission = new DaqIncidentEvidenceSubmission
            {
                ContextKey = DaqIncidentEvidenceContextKey(runId, context.CorrelationId),
                RunId = runId,
                CorrelationId = context.CorrelationId,
                Device = context.Device,
                StartedUtc = context.StartedUtc,
                PhaseKey = phaseKey,
                PhaseDirectoryName = isTrigger || isTerminal
                    ? $"{safePhase}-{sequence:D3}-{capturedUtc:HHmmss_fff}"
                    : null,
                IncidentJson = incidentJson,
                IsTrigger = isTrigger,
                IsTerminal = isTerminal,
                WriteHeavyEvidence = writeHeavyEvidence
            };
            if (!GetDaqIncidentEvidenceQueue().Submit(submission))
                _log.Error(
                    $"DAQ事故取证有界门拒绝提交：Phase={phaseKey} Device={context.Device} " +
                    $"RunId={runId:N} CorrelationId={context.CorrelationId:N}",
                    "落盘");
        }

        internal static bool ShouldIncludeFullDaqIncidentEvidence(string phase)
        {
            return (phase ?? string.Empty).StartsWith("90-", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool ShouldIncludeDaqTimingEvidence(string phase)
        {
            var value = phase ?? string.Empty;
            return value.StartsWith("00-trigger", StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("90-", StringComparison.OrdinalIgnoreCase);
        }

        private void SubmitDaqHardFaultIncidentSnapshot(
            DaqIncidentContext initialContext,
            DaqDeviceFault evidence)
        {
            try
            {
                SubmitDaqHardFaultIncidentSnapshotCore(initialContext, evidence);
            }
            catch (Exception ex)
            {
                _log.Error(
                    $"DAQ硬故障事故取证提交失败：Device={initialContext?.Device} " +
                    $"CorrelationId={initialContext?.CorrelationId:N} Error={ex.Message}",
                    "落盘",
                    ex);
            }
        }

        private void SubmitDaqHardFaultIncidentSnapshotCore(
            DaqIncidentContext initialContext,
            DaqDeviceFault evidence)
        {
            if (initialContext == null) return;
            // 触发线程只冻结标量；硬件确认的 trigger/terminal 和重证据同样经过全局门。
            var context = initialContext.Clone();
            var capturedUtc = DateTime.UtcNow;
            var control = _acq.GetControlSnapshot(context.Device);
            var runEpoch = Interlocked.Read(ref _runEpoch);
            var powerStates = _powerSupply == null
                ? Array.Empty<PowerSupplyRuntimeState>()
                : _cfg.Test.Groups
                    .Where(group => group.Members.Any((context.AffectedChannels ?? Array.Empty<int>()).Contains))
                    .Select(group => _powerSupply.GetRuntimeState(group.Id))
                    .ToArray();
            var powerStatesJson = string.Join(",", powerStates.Select(state => "{" +
                $"\"group\":{state.ElectricalGroupId}," +
                $"\"operationEpoch\":{state.OperationEpoch}," +
                $"\"expectedOn\":{state.ExpectedOutputEnabled.ToString().ToLowerInvariant()}," +
                $"\"planned\":{state.PlannedTransition.ToString().ToLowerInvariant()}," +
                $"\"telemetryOn\":{state.TelemetryOutputEnabled.ToString().ToLowerInvariant()}," +
                $"\"protection\":{state.ProtectionTripped.ToString().ToLowerInvariant()}" + "}"));
            var affectedChannels = context.AffectedChannels ?? Array.Empty<int>();
            var derivedCodes = context.DerivedCodes
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var derivedCodesJson = derivedCodes.Select(code => $"\"{JsonEscape(code)}\"");
            var contextKey = DaqIncidentEvidenceContextKey(context.RunId, context.CorrelationId);
            var triggerJson = "{\n" +
                $"  \"runId\": \"{context.RunId:N}\",\n" +
                $"  \"device\": \"{JsonEscape(context.Device)}\",\n" +
                $"  \"generation\": {context.Generation},\n" +
                $"  \"correlationId\": \"{context.CorrelationId:N}\",\n" +
                $"  \"runEpoch\": {runEpoch},\n" +
                $"  \"faultCode\": \"{JsonEscape(context.PrimaryCode)}\",\n" +
                $"  \"reason\": \"{JsonEscape(context.PrimaryReason)}\",\n" +
                $"  \"affectedChannels\": [{string.Join(",", affectedChannels)}],\n" +
                $"  \"timingEvidenceIncluded\": true,\n" +
                $"  \"fullEvidenceIncluded\": false,\n" +
                $"  \"recentCycleCopiesIncluded\": false,\n" +
                $"  \"result\": \"00-trigger\",\n" +
                $"  \"capturedUtc\": \"{capturedUtc:O}\"\n" +
                "}\n";
            var terminalJson = "{\n" +
                $"  \"runId\": \"{context.RunId:N}\",\n" +
                $"  \"device\": \"{JsonEscape(context.Device)}\",\n" +
                $"  \"generation\": {context.Generation},\n" +
                $"  \"correlationId\": \"{context.CorrelationId:N}\",\n" +
                $"  \"runEpoch\": {runEpoch},\n" +
                $"  \"primaryFault\": \"{JsonEscape(context.PrimaryCode)}\",\n" +
                $"  \"primaryReason\": \"{JsonEscape(context.PrimaryReason)}\",\n" +
                $"  \"primaryChannel\": {context.PrimaryChannel},\n" +
                $"  \"affectedChannels\": [{string.Join(",", affectedChannels)}],\n" +
                $"  \"powerStates\": [{powerStatesJson}],\n" +
                $"  \"derivedActions\": [{string.Join(",", derivedCodesJson)}],\n" +
                $"  \"firstSeenUtc\": \"{context.FirstSeenUtc:O}\",\n" +
                $"  \"lastSeenUtc\": \"{context.LastSeenUtc:O}\",\n" +
                $"  \"controlQueueType\": \"{JsonEscape(control?.QueueType ?? evidence?.QueueType ?? "PreallocatedSpscRing")}\",\n" +
                $"  \"controlQueueCapacity\": {control?.QueueCapacity ?? evidence?.QueueCapacity ?? 0},\n" +
                $"  \"controlQueueDepth\": {control?.QueueDepth ?? evidence?.QueueDepth ?? 0},\n" +
                $"  \"oldestControlBatchAgeMs\": {(control?.OldestBatchAgeMs ?? evidence?.OldestBatchAgeMs ?? 0).ToString("F3", CultureInfo.InvariantCulture)},\n" +
                $"  \"lastControlProcessingMs\": {(control?.LastBatchProcessMs ?? 0).ToString("F3", CultureInfo.InvariantCulture)},\n" +
                $"  \"subscriberMaxMs\": {(control?.SubscriberMaxMs ?? 0).ToString("F3", CultureInfo.InvariantCulture)},\n" +
                $"  \"lastProcessedSampleUtc\": \"{(control?.ProcessedSampleUtc ?? evidence?.LastProcessedSampleUtc ?? default):O}\",\n" +
                $"  \"capturedUtc\": \"{capturedUtc:O}\"\n" +
                "}\n";

            var incidentQueue = GetDaqIncidentEvidenceQueue();
            var frozenTriggerDiagnostics = _acq.CaptureDiagnostics(
                new[] { context.Device },
                TimeSpan.FromSeconds(10));
            var triggerAccepted = incidentQueue.Submit(new DaqIncidentEvidenceSubmission
            {
                ContextKey = contextKey,
                RunId = context.RunId,
                CorrelationId = context.CorrelationId,
                Device = context.Device,
                StartedUtc = context.FirstSeenUtc,
                PhaseKey = "00-trigger",
                PhaseDirectoryName = $"00-trigger-001-{capturedUtc:HHmmss_fff}",
                IncidentJson = triggerJson,
                IsTrigger = true,
                WriteHeavyEvidence = phaseDirectory =>
                    frozenTriggerDiagnostics?.WriteTo(phaseDirectory)
            });
            if (!triggerAccepted)
            {
                _log.Error(
                    $"DAQ硬故障事故取证有界门拒绝 trigger：Device={context.Device} " +
                    $"RunId={context.RunId:N} CorrelationId={context.CorrelationId:N}",
                    "落盘");
                return;
            }

            foreach (var code in derivedCodes)
            {
                var symptomJson = "{" +
                    $"\"runId\":\"{context.RunId:N}\"," +
                    $"\"device\":\"{JsonEscape(context.Device)}\"," +
                    $"\"correlationId\":\"{context.CorrelationId:N}\"," +
                    $"\"symptomCode\":\"{JsonEscape(code)}\"," +
                    $"\"capturedUtc\":\"{capturedUtc:O}\"" +
                    "}\n";
                incidentQueue.Submit(new DaqIncidentEvidenceSubmission
                {
                    ContextKey = contextKey,
                    RunId = context.RunId,
                    CorrelationId = context.CorrelationId,
                    Device = context.Device,
                    StartedUtc = context.FirstSeenUtc,
                    PhaseKey = "symptom-" + code,
                    IncidentJson = symptomJson
                });
            }

            var frozenTerminalDiagnostics = _acq.CaptureDiagnostics(
                new[] { context.Device },
                TimeSpan.FromSeconds(60));
            if (!incidentQueue.Submit(new DaqIncidentEvidenceSubmission
                {
                    ContextKey = contextKey,
                    RunId = context.RunId,
                    CorrelationId = context.CorrelationId,
                    Device = context.Device,
                    StartedUtc = context.FirstSeenUtc,
                    PhaseKey = "90-hardware-confirmed",
                    PhaseDirectoryName = $"90-terminal-{capturedUtc:HHmmss_fff}-{Guid.NewGuid():N}",
                    IncidentJson = terminalJson,
                    IsTerminal = true,
                    WriteHeavyEvidence = phaseDirectory =>
                        frozenTerminalDiagnostics?.WriteTo(phaseDirectory)
                }))
                _log.Error(
                    $"DAQ硬故障事故取证终态提交失败：Device={context.Device} " +
                    $"RunId={context.RunId:N} CorrelationId={context.CorrelationId:N}",
                    "落盘");
        }

        private static string DaqIncidentEvidenceContextKey(Guid runId, Guid correlationId)
        {
            return $"{runId:N}:{correlationId:N}";
        }

        private DaqIncidentEvidenceQueue GetDaqIncidentEvidenceQueue()
        {
            var current = Volatile.Read(ref _daqIncidentEvidenceQueue);
            if (current != null) return current;
            lock (_daqIncidentEvidenceQueueInitGate)
            {
                return _daqIncidentEvidenceQueue ??=
                    new DaqIncidentEvidenceQueue(
                        ExportDaqIncidentEvidenceBatch,
                        worker => ObserveBackgroundTask(worker, "DaqIncidentEvidenceWorker"),
                        ex => _log.Error(
                            $"DAQ事故取证worker失败：{ex.Message}",
                            "落盘",
                            ex));
            }
        }

        internal Task<bool> DrainDaqIncidentEvidenceAsync(int timeoutMs)
        {
            var queue = Volatile.Read(ref _daqIncidentEvidenceQueue);
            return queue == null
                ? Task.FromResult(true)
                : queue.DrainAsync(Math.Max(1, timeoutMs));
        }

        private void ExportDaqIncidentEvidenceBatch(DaqIncidentEvidenceBatch batch)
        {
            var submissions = batch?.OrderedSubmissions() ??
                              Array.Empty<DaqIncidentEvidenceSubmission>();
            if (submissions.Length == 0) return;
            var anchor = submissions[0];
            var root = Path.Combine(
                _cfg.Test.StoreDir,
                _cfg.Test.TestName,
                "IncidentSnapshots");
            Directory.CreateDirectory(root);
            var incidentDirectory = _daqIncidentDirectories.GetOrAdd(
                anchor.CorrelationId,
                _ => Path.Combine(
                    root,
                    $"{anchor.StartedUtc.ToLocalTime():yyyyMMdd_HHmmss_fff}-" +
                    $"{anchor.Device}-{anchor.CorrelationId:N}"));
            Directory.CreateDirectory(incidentDirectory);
            var buildIdentity = _daqIncidentBuildIdentity ??
                                (_daqIncidentBuildIdentity = RuntimeBuildIdentity.Capture());
            var rootIdentityPath = Path.Combine(incidentDirectory, "build-identity.json");
            if (!File.Exists(rootIdentityPath)) buildIdentity.WriteJson(rootIdentityPath);

            foreach (var submission in submissions)
            {
                if (submission == null) continue;
                if (!string.IsNullOrWhiteSpace(submission.PhaseDirectoryName))
                {
                    var phaseDirectory = Path.Combine(
                        incidentDirectory,
                        submission.PhaseDirectoryName);
                    try
                    {
                        Directory.CreateDirectory(phaseDirectory);
                        var incidentPath = Path.Combine(phaseDirectory, "incident.json");
                        if (!File.Exists(incidentPath))
                        {
                            using var stream = new FileStream(
                                incidentPath,
                                FileMode.CreateNew,
                                FileAccess.Write,
                                FileShare.Read);
                            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                            writer.Write(submission.IncidentJson ?? "{}\n");
                        }
                        var identityPath = Path.Combine(phaseDirectory, "build-identity.json");
                        if (!File.Exists(identityPath)) buildIdentity.WriteJson(identityPath);
                        try
                        {
                            submission.WriteHeavyEvidence?.Invoke(phaseDirectory);
                        }
                        catch (Exception ex)
                        {
                            _log.Error(
                                $"DAQ事故重证据导出失败：Phase={submission.PhaseKey} " +
                                $"Directory={phaseDirectory} Error={ex.Message}",
                                "落盘",
                                ex);
                            TryWriteIncidentEvidenceError(phaseDirectory, submission, ex);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Error(
                            $"DAQ事故phase导出失败：Phase={submission.PhaseKey} " +
                            $"Directory={phaseDirectory} Error={ex.Message}",
                            "落盘",
                            ex);
                    }
                }

                try
                {
                    AppendDaqIncidentManifest(incidentDirectory, submission);
                }
                catch (Exception ex)
                {
                    _log.Error(
                        $"DAQ事故manifest追加失败：Phase={submission.PhaseKey} " +
                        $"Directory={incidentDirectory} Error={ex.Message}",
                        "落盘",
                        ex);
                }
            }

            if (batch.Terminal != null)
            {
                _daqIncidentDirectories.TryRemove(anchor.CorrelationId, out _);
                _log.Info(
                    $"DAQ事故取证已收口：{incidentDirectory} " +
                    $"CorrelationId={anchor.CorrelationId:N}",
                    "落盘");
            }
        }

        private static void AppendDaqIncidentManifest(
            string incidentDirectory,
            DaqIncidentEvidenceSubmission submission)
        {
            var kind = submission.IsTrigger
                ? "trigger"
                : submission.IsTerminal ? "terminal" : "symptom";
            var payload = (submission.IncidentJson ?? "{}")
                .Replace("\r", string.Empty)
                .Replace("\n", string.Empty)
                .Trim();
            if (payload.Length == 0) payload = "{}";
            var line = "{" +
                       $"\"phase\":\"{JsonEscape(submission.PhaseKey)}\"," +
                       $"\"kind\":\"{kind}\"," +
                       $"\"payload\":{payload}" +
                       "}";
            using var stream = new FileStream(
                Path.Combine(incidentDirectory, "manifest.jsonl"),
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.WriteLine(line);
        }

        private static void TryWriteIncidentEvidenceError(
            string phaseDirectory,
            DaqIncidentEvidenceSubmission submission,
            Exception error)
        {
            try
            {
                var path = Path.Combine(phaseDirectory, "evidence-error.txt");
                File.WriteAllText(
                    path,
                    $"Phase={submission?.PhaseKey}\r\n" +
                    $"CapturedUtc={DateTime.UtcNow:O}\r\n" +
                    $"Error={error}\r\n",
                    new UTF8Encoding(false));
            }
            catch
            {
                // 错误旁证是尽力而为，主 manifest 仍必须继续追加。
            }
        }

        private string TryWriteDaqSnapshotFailure(
            DaqIncidentContext context,
            string intendedDirectory,
            Exception error)
        {
            var candidates = new[]
            {
                intendedDirectory,
                Path.Combine(
                    Path.GetTempPath(),
                    "EPBTest",
                    "IncidentSnapshots",
                    $"{DateTime.Now:yyyyMMdd_HHmmss_fff}-{context.Device}-{context.CorrelationId:N}"),
                Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "IncidentSnapshots-Fallback",
                    $"{DateTime.Now:yyyyMMdd_HHmmss_fff}-{context.Device}-{context.CorrelationId:N}")
            };
            foreach (var directory in candidates.Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                try
                {
                    Directory.CreateDirectory(directory);
                    var failureJson = "{\n" +
                        $"  \"device\": \"{JsonEscape(context.Device)}\",\n" +
                        $"  \"runId\": \"{context.RunId:N}\",\n" +
                        $"  \"generation\": {context.Generation},\n" +
                        $"  \"correlationId\": \"{context.CorrelationId:N}\",\n" +
                        $"  \"intendedDirectory\": \"{JsonEscape(intendedDirectory)}\",\n" +
                        $"  \"fallbackDirectory\": \"{JsonEscape(directory)}\",\n" +
                        $"  \"error\": \"{JsonEscape(error?.ToString())}\",\n" +
                        $"  \"timestampUtc\": \"{DateTime.UtcNow:O}\"\n" +
                        "}\n";
                    using (var stream = new FileStream(
                               Path.Combine(directory, $"snapshot-failure-{Guid.NewGuid():N}.json"),
                               FileMode.CreateNew,
                               FileAccess.Write,
                               FileShare.Read))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                        writer.Write(failureJson);
                    RuntimeBuildIdentity.Capture().WriteJson(
                        Path.Combine(directory, "build-identity.json"));
                    return directory;
                }
                catch { }
            }
            return "unavailable";
        }

        private void QueueWarningSnapshot(WarningSnapshotRequest request)
        {
            if (request == null) return;
            var cfg = AlarmConfig?.WarningSnapshots ?? new WarningSnapshotConfig();
            var categoryKey = GetWarningSnapshotRateLimitKey(
                request.TestRunId,
                request.Channel,
                request.Warning?.NormalizedCode ?? "Unknown");
            if (!_warningSnapshotWorkGate.TryQueue(
                    request.IdempotencyKey,
                    categoryKey,
                    DateTime.UtcNow,
                    cfg.FullEvidenceMinimumIntervalSeconds,
                    cfg.FullEvidenceQueueCapacity))
                return;

            _warningSnapshotQueue.Enqueue(request);
            StartWarningSnapshotWorker();
        }

        internal static bool IsWarningSnapshotIntervalElapsed(
            DateTime previousUtc,
            DateTime nowUtc,
            int minimumIntervalSeconds)
        {
            return WarningSnapshotWorkGate.IsIntervalElapsed(
                previousUtc,
                nowUtc,
                minimumIntervalSeconds);
        }

        private void StartWarningSnapshotWorker()
        {
            if (Interlocked.CompareExchange(ref _warningSnapshotWorkerRunning, 1, 0) != 0) return;
            var worker = Task.Factory.StartNew(
                ProcessWarningSnapshotQueue,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            ObserveBackgroundTask(worker, "WarningSnapshotWorker");
        }

        private void ProcessWarningSnapshotQueue()
        {
            try
            {
                while (_warningSnapshotQueue.TryDequeue(out var request))
                {
                    if (!_warningSnapshotWorkGate.TryStart(request.IdempotencyKey))
                        continue;
                    try { ExportWarningSnapshot(request); }
                    finally { _warningSnapshotWorkGate.Complete(request.IdempotencyKey); }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _warningSnapshotWorkerRunning, 0);
                if (!_warningSnapshotQueue.IsEmpty) StartWarningSnapshotWorker();
            }
        }

        private void ExportWarningSnapshot(WarningSnapshotRequest request)
        {
            try
            {
                var cfg = AlarmConfig?.WarningSnapshots ?? new WarningSnapshotConfig();
                var warning = request.Warning;
                var directory = GetWarningSnapshotDirectory(request, cfg);
                var categoryDirectory = Path.GetDirectoryName(directory);
                EnsureWarningSnapshotStorageCounter(GetWarningSnapshotRoot(cfg));
                var categoryGate = _warningSnapshotCategoryGates.GetOrAdd(
                    GetWarningChainKey(request.Channel, warning.NormalizedCode),
                    _ => new object());
                lock (categoryGate)
                {
                Directory.CreateDirectory(directory);
                InvalidateWarningSnapshotStorageCache();
                PublishWarningSnapshotStorageStatus();

                CycleSnapshotEvidence evidence;
                if (Recorder is ICycleAttemptEvidenceExporter attemptExporter)
                {
                    evidence = ExportWarningCycleAfterFinalization(
                        attemptExporter,
                        request,
                        directory,
                        cfg);
                }
                else if (Recorder is ICycleEvidenceExporter completedExporter)
                {
                    evidence = completedExporter.ExportCompletedCycleTo(
                        request.Channel,
                        request.CycleNumber,
                        directory,
                        cfg.SaveCsv,
                        cfg.SaveBin);
                }
                else
                {
                    throw new InvalidOperationException(
                        "Recorder does not implement a cycle evidence exporter.");
                }
                var hashes = new ConcurrentDictionary<string, string>();
                foreach (var path in new[] { evidence.CsvPath, evidence.BinPath }.Where(File.Exists))
                    hashes[Path.GetFileName(path)] = ComputeSha256(path);

                var chainKey = GetWarningChainKey(request.Channel, warning.NormalizedCode);
                var linkedAlarmPath = _warningChains.TryGetValue(chainKey, out var registeredLinks)
                    ? registeredLinks.FirstOrDefault(x =>
                        x.CycleNumber == request.CycleNumber && x.Streak == warning.Streak)
                        ?.LinkedAlarmRelativePath ?? string.Empty
                    : string.Empty;

                File.WriteAllText(
                    Path.Combine(directory, "warning-metadata.json"),
                    BuildWarningMetadataJson(request, evidence, hashes, linkedAlarmPath),
                    new UTF8Encoding(false));
                File.WriteAllLines(
                    Path.Combine(directory, "checksums.sha256"),
                    hashes.OrderBy(x => x.Key).Select(x => $"{x.Value}  {x.Key}"),
                    new UTF8Encoding(false));
                TrackWarningSnapshotCreated(directory);

                _log.Info($"软预警完整单圈快照已保存：{directory}", "落盘");
                try
                {
                    EnforceSoftWarningCountRetentionCore(
                        categoryDirectory,
                        cfg,
                        message => _log.Warn(message, "落盘"),
                        (ignored, bytes) => TrackWarningSnapshotDeleted(bytes, 1));
                }
                catch (Exception ex)
                {
                    _log.Warn(
                        $"软预警计数清理失败但新快照已保留：Directory={categoryDirectory} Error={ex.Message}",
                        "落盘");
                }
                EnforceSoftWarningQuota(request.TestRunId, cfg);
                InvalidateWarningSnapshotStorageCache();
                PublishWarningSnapshotStorageStatus();
                }
            }
            catch (Exception ex)
            {
                ReportSnapshotFailure(
                    $"WarningSnapshotExportFailed Key={request.IdempotencyKey} Error={ex.Message}",
                    ex);
            }
        }

        private CycleSnapshotEvidence ExportWarningCycleAfterFinalization(
            ICycleAttemptEvidenceExporter exporter,
            WarningSnapshotRequest request,
            string directory,
            WarningSnapshotConfig cfg)
        {
            Exception last = null;
            // 软预警在控制判定点发布，而正式圈/作废圈的数据库终态在 Runner 返回后
            // 才提交。V2.12.0.2 的后台导出常常领先收尾 0.2~0.8s，因而误报“没有可
            // 导出的样本”。只在独立长线程等待，绝不阻塞控制/DAQ；终态完成后再从
            // 当前圈或历史 BIN 导出。
            var delaysMs = new[] { 0, 100, 200, 400, 800, 1200 };
            for (var attempt = 0; attempt < delaysMs.Length; attempt++)
            {
                if (delaysMs[attempt] > 0) Thread.Sleep(delaysMs[attempt]);
                try
                {
                    return exporter.ExportCycleAttemptTo(
                        request.Channel,
                        request.CycleNumber,
                        directory,
                        cfg.SaveCsv,
                        cfg.SaveBin);
                }
                catch (Exception ex) when (
                    ex is InvalidDataException ||
                    ex is InvalidOperationException ||
                    ex is IOException)
                {
                    last = ex;
                }
            }

            throw new InvalidDataException(
                $"EPB[{request.Channel}] Cycle={request.CycleNumber} 等待圈终态后仍无法导出软预警证据。",
                last);
        }

        public static void EnforceSoftWarningCountRetention(
            string categoryDirectory,
            WarningSnapshotConfig cfg,
            Action<string> warningSink = null)
        {
            EnforceSoftWarningCountRetentionCore(
                categoryDirectory,
                cfg,
                warningSink,
                null);
        }

        private static void EnforceSoftWarningCountRetentionCore(
            string categoryDirectory,
            WarningSnapshotConfig cfg,
            Action<string> warningSink,
            Action<string, long> deleted)
        {
            if (cfg == null ||
                cfg.SoftWarningRetentionMode == StorageRetentionMode.Unlimited ||
                string.IsNullOrWhiteSpace(categoryDirectory) ||
                !Directory.Exists(categoryDirectory))
                return;

            var keep = cfg.SoftWarningRetainCountPerChannelCode;
            if (keep < 1) keep = 30;
            var valid = new System.Collections.Generic.List<DirectoryInfo>();
            foreach (var directory in new DirectoryInfo(categoryDirectory)
                         .EnumerateDirectories("*", SearchOption.TopDirectoryOnly))
            {
                if (string.Equals(directory.Name, "Archive", StringComparison.OrdinalIgnoreCase) ||
                    directory.Name.StartsWith(".", StringComparison.Ordinal) ||
                    directory.Name.IndexOf(".tmp-", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                if (!WarningEventDirectoryPattern.IsMatch(directory.Name) ||
                    !IsCompleteWarningEvent(directory.FullName, cfg))
                {
                    warningSink?.Invoke($"软预警计数清理跳过未知或不完整目录：{directory.FullName}");
                    continue;
                }
                valid.Add(directory);
            }

            foreach (var directory in valid
                         .OrderByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase)
                         .ThenByDescending(x => x.LastWriteTimeUtc)
                         .Skip(keep)
                         .Reverse())
            {
                try
                {
                    var bytes = GetDirectoryBytes(directory.FullName);
                    directory.Delete(true);
                    deleted?.Invoke(directory.FullName, bytes);
                }
                catch (Exception ex)
                {
                    warningSink?.Invoke(
                        $"软预警旧事件删除失败：{directory.FullName} Error={ex.Message}");
                }
            }
        }

        private static bool IsCompleteWarningEvent(string directory, WarningSnapshotConfig cfg)
        {
            if (!File.Exists(Path.Combine(directory, "warning-metadata.json")) ||
                !File.Exists(Path.Combine(directory, "checksums.sha256")))
                return false;
            if (cfg.SaveCsv && !Directory.EnumerateFiles(directory, "*.csv", SearchOption.TopDirectoryOnly).Any())
                return false;
            if (cfg.SaveBin && !Directory.EnumerateFiles(directory, "*.bin", SearchOption.TopDirectoryOnly).Any())
                return false;
            return true;
        }

        private void QueueRollingHistoricalSnapshot(int channel, int cycleNumber)
        {
            if (!_historicalStorageEnabled) return;
            if (!(Recorder is ICycleEvidenceExporter exporter)) return;
            ObserveBackgroundTask(Task.Run(() =>
            {
                try
                {
                    var root = Path.Combine(
                        _cfg.Test.StoreDir,
                        _cfg.Test.TestName,
                        "HistoricalSnapshots",
                        $"EPB{channel:D2}");
                    var dir = Path.Combine(root, $"Cycle_{cycleNumber:D6}");
                    exporter.ExportCompletedCycleTo(channel, cycleNumber, dir, false, true);
                    var keep = Math.Max(1, _historicalRetainCyclesPerChannel);
                    foreach (var old in new DirectoryInfo(root).EnumerateDirectories("Cycle_*")
                                 .OrderByDescending(x => x.Name).Skip(keep))
                    {
                        try { old.Delete(true); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    ReportSnapshotFailure(
                        $"HistoricalSnapshotExportFailed EPB={channel} Cycle={cycleNumber} Error={ex.Message}",
                        ex);
                }
            }), "RollingHistoricalSnapshot", channel);
        }

        private string GetWarningSnapshotDirectory(
            WarningSnapshotRequest request,
            WarningSnapshotConfig cfg)
        {
            var rootName = string.IsNullOrWhiteSpace(cfg?.RootDirectory)
                ? "WarningSnapshots"
                : cfg.RootDirectory.Trim();
            var warning = request.Warning;
            var folder =
                $"{warning.OccurredUtc.ToLocalTime():yyyyMMdd_HHmmssfff}-" +
                $"Cycle{request.CycleNumber:D6}-Streak{warning.Streak}of{warning.ConfirmThreshold}";
            return Path.Combine(
                _cfg.Test.StoreDir,
                _cfg.Test.TestName,
                rootName,
                $"EPB{request.Channel:D2}",
                SanitizePathSegment(warning.NormalizedCode),
                folder);
        }

        private static string SanitizePathSegment(string value)
        {
            var safe = string.Concat((value ?? "ControlWarning")
                .Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            return string.IsNullOrWhiteSpace(safe) ? "ControlWarning" : safe;
        }

        private void WriteWarningChain(string alarmSnapshotDirectory, int channel, string reason, int terminalCycle)
        {
            string code = null;
            if (reason?.IndexOf("ForwardPeakOvershoot", StringComparison.OrdinalIgnoreCase) >= 0)
                code = AdaptiveWarningCode.ForwardPeakOvershootWarning.ToString();
            else if (reason?.IndexOf("PeakEvidenceMismatch", StringComparison.OrdinalIgnoreCase) >= 0)
                code = AdaptiveWarningCode.PeakEvidenceMismatchWarning.ToString();
            else if (reason?.IndexOf("PeakEvidenceLag", StringComparison.OrdinalIgnoreCase) >= 0)
                code = AdaptiveWarningCode.PeakEvidenceLagWarning.ToString();
            else if (reason?.IndexOf("ForwardCurrentRiseStall", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     reason?.IndexOf("ForwardCurrentRiseStalled", StringComparison.OrdinalIgnoreCase) >= 0)
                code = AdaptiveWarningCode.ForwardCurrentRiseStallWarning.ToString();
            else
                code = ExtractFaultCode(reason);
            if (string.IsNullOrWhiteSpace(code)) return;

            var links = Array.Empty<WarningSnapshotLink>();
            if (_warningChains.TryGetValue(GetWarningChainKey(channel, code), out var queue))
            {
                var candidates = queue.Where(x => x.CycleNumber < terminalCycle)
                    .OrderByDescending(x => x.CycleNumber)
                    .ToArray();
                var threshold = candidates.FirstOrDefault()?.ConfirmThreshold ?? 0;
                var selected = new System.Collections.Generic.List<WarningSnapshotLink>();
                for (var expected = threshold - 1; expected >= 1; expected--)
                {
                    var match = candidates.FirstOrDefault(x => x.Streak == expected);
                    if (match == null) break;
                    selected.Add(match);
                    candidates = candidates.Where(x => x.CycleNumber < match.CycleNumber).ToArray();
                }
                if (selected.Count == Math.Max(0, threshold - 1))
                    links = selected.OrderBy(x => x.Streak).ToArray();
            }
            if (links.Length == 0) return;
            var testRoot = Path.Combine(_cfg.Test.StoreDir, _cfg.Test.TestName);
            var linkedAlarmRelativePath = MakeRelativePath(testRoot, alarmSnapshotDirectory);
            foreach (var link in links)
            {
                link.LinkedAlarmRelativePath = linkedAlarmRelativePath;
                TryUpdateWarningMetadataLink(testRoot, link);
            }
            var sb = new StringBuilder();
            sb.Append("{\n  \"WarningCode\": \"").Append(code).Append("\",")
                .Append("\n  \"HardAlarmTerminalCycle\": ").Append(terminalCycle).Append(',')
                .Append("\n  \"Links\": [");
            for (var i = 0; i < links.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("\n    {\"CycleNumber\": ").Append(links[i].CycleNumber)
                    .Append(", \"Streak\": ").Append(links[i].Streak)
                    .Append(", \"ConfirmThreshold\": ").Append(links[i].ConfirmThreshold)
                    .Append(", \"Path\": \"").Append(JsonEscape(links[i].RelativePath)).Append("\"}");
            }
            sb.Append("\n  ]\n}\n");
            File.WriteAllText(
                Path.Combine(alarmSnapshotDirectory, "warning-chain.json"),
                sb.ToString(),
                new UTF8Encoding(false));
        }

        private static void WriteAlarmSnapshotManifest(
            string snapshotDirectory,
            int alarmChannel,
            int alarmCycle,
            int requestedCycles,
            string reason,
            int[] affectedChannels,
            int[] requestedCycleEvidenceChannels,
            int[] exportedCycleEvidenceChannels,
            int[] skippedCycleEvidenceChannels,
            bool includeSameElectricalGroup,
            int sameGroupRequestedCycles)
        {
            var files = Directory.EnumerateFiles(snapshotDirectory, "*.*", SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path)
                .ToArray();
            var alarmFolder = $"EPB{alarmChannel:D2}_ALARM";
            var alarmCycleToken = $"_{alarmCycle:D6}.";
            var alarmCycles = files
                .Where(path => path.IndexOf(alarmFolder, StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(Path.GetFileNameWithoutExtension)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var sb = new StringBuilder();
            sb.Append("{\n  \"AlarmChannel\": ").Append(alarmChannel).Append(',')
                .Append("\n  \"AlarmCycle\": ").Append(alarmCycle).Append(',')
                .Append("\n  \"Reason\": \"").Append(JsonEscape(reason)).Append("\",")
                .Append("\n  \"AffectedChannels\": [")
                .Append(string.Join(",", affectedChannels ?? new[] { alarmChannel })).Append("],")
                .Append("\n  \"RequestedCycleEvidenceChannels\": [")
                .Append(string.Join(",", requestedCycleEvidenceChannels ?? new[] { alarmChannel })).Append("],")
                .Append("\n  \"ExportedCycleEvidenceChannels\": [")
                .Append(string.Join(",", exportedCycleEvidenceChannels ?? Array.Empty<int>())).Append("],")
                .Append("\n  \"SkippedCycleEvidenceChannels\": [")
                .Append(string.Join(",", skippedCycleEvidenceChannels ?? Array.Empty<int>())).Append("],")
                .Append("\n  \"IncludeSameElectricalGroup\": ")
                .Append(includeSameElectricalGroup ? "true" : "false").Append(',')
                .Append("\n  \"SameGroupRequestedCycles\": ").Append(sameGroupRequestedCycles).Append(',')
                .Append("\n  \"GroupCoverage\": \"Cycle evidence is limited to the alarm channel")
                .Append(includeSameElectricalGroup ? " and its electrical-group siblings\"," : "\",")
                .Append("\n  \"RequestedCycles\": ").Append(requestedCycles).Append(',')
                .Append("\n  \"ExportedCycles\": ").Append(alarmCycles).Append(',')
                .Append("\n  \"ShortfallReason\": \"")
                .Append(alarmCycles < requestedCycles
                    ? $"Only {alarmCycles} completed/alarm cycles were available"
                    : string.Empty)
                .Append("\",")
                .Append("\n  \"Files\": [");
            for (var i = 0; i < files.Length; i++)
            {
                var relative = MakeRelativePath(snapshotDirectory, files[i]);
                var inAlarmFolder = relative.IndexOf(alarmFolder, StringComparison.OrdinalIgnoreCase) >= 0;
                var role = inAlarmFolder
                    ? relative.IndexOf(alarmCycleToken, StringComparison.OrdinalIgnoreCase) >= 0
                        ? alarmCycle < 0 ? "LearningCycle" : "AlarmCycle"
                        : "HistoricalCompletedCycle"
                    : "AuxiliaryRunningPartial";
                if (i > 0) sb.Append(',');
                sb.Append("\n    {\"Path\": \"").Append(JsonEscape(relative))
                    .Append("\", \"Role\": \"").Append(role)
                    .Append("\", \"IsComplete\": ").Append(role == "AuxiliaryRunningPartial" ? "false" : "true")
                    .Append(", \"Sha256\": \"").Append(ComputeSha256(files[i])).Append("\"}");
            }
            sb.Append("\n  ]\n}\n");
            File.WriteAllText(
                Path.Combine(snapshotDirectory, "snapshot-manifest.json"),
                sb.ToString(),
                new UTF8Encoding(false));
        }

        private static string BuildWarningMetadataJson(
            WarningSnapshotRequest request,
            CycleSnapshotEvidence evidence,
            ConcurrentDictionary<string, string> hashes,
            string linkedAlarmSnapshotPath)
        {
            var w = request.Warning;
            var files = string.Join(",\n", hashes.OrderBy(x => x.Key).Select(x =>
                $"    \"{JsonEscape(x.Key)}\": \"{x.Value}\""));
            return "{\n" +
                   $"  \"TestRunId\": \"{request.TestRunId:N}\",\n" +
                   $"  \"Channel\": {request.Channel},\n" +
                   $"  \"CycleNumber\": {request.CycleNumber},\n" +
                   $"  \"WarningCode\": \"{w.Code}\",\n" +
                   $"  \"FaultCode\": \"{JsonEscape(w.NormalizedCode)}\",\n" +
                   $"  \"ScopeKey\": \"{JsonEscape(w.ScopeKey)}\",\n" +
                   $"  \"CorrelationId\": \"{w.CorrelationId:N}\",\n" +
                   $"  \"AttemptId\": {w.AttemptId},\n" +
                   $"  \"OccurredUtc\": \"{w.OccurredUtc:O}\",\n" +
                   $"  \"PeakCurrentA\": {w.PeakCurrentA.ToString("R", CultureInfo.InvariantCulture)},\n" +
                   $"  \"TargetCurrentA\": {w.TargetCurrentA.ToString("R", CultureInfo.InvariantCulture)},\n" +
                   $"  \"PeakErrorA\": {w.PeakErrorA.ToString("R", CultureInfo.InvariantCulture)},\n" +
                   $"  \"SlopeAperMs\": {w.SlopeAperMs.ToString("R", CultureInfo.InvariantCulture)},\n" +
                   $"  \"WindowSpanMs\": {w.WindowSpanMs},\n" +
                   $"  \"Streak\": {w.Streak},\n" +
                   $"  \"ConfirmThreshold\": {w.ConfirmThreshold},\n" +
                   $"  \"FirstSampleUtc\": \"{evidence.FirstSampleUtc:O}\",\n" +
                   $"  \"LastSampleUtc\": \"{evidence.LastSampleUtc:O}\",\n" +
                   $"  \"SampleCount\": {evidence.SampleCount},\n" +
                   $"  \"IsCompleteCycle\": {(evidence.IsCompleteCycle ? "true" : "false")},\n" +
                   $"  \"LinkedAlarmSnapshotPath\": \"{JsonEscape(linkedAlarmSnapshotPath)}\",\n" +
                   "  \"FileSha256\": {\n" + files + "\n  }\n}\n";
        }

        private static void TryUpdateWarningMetadataLink(string testRoot, WarningSnapshotLink link)
        {
            try
            {
                var metadataPath = Path.Combine(testRoot, link.RelativePath, "warning-metadata.json");
                if (!File.Exists(metadataPath)) return;
                var json = File.ReadAllText(metadataPath);
                const string marker = "\"LinkedAlarmSnapshotPath\": \"\"";
                if (json.IndexOf(marker, StringComparison.Ordinal) < 0) return;
                json = json.Replace(
                    marker,
                    $"\"LinkedAlarmSnapshotPath\": \"{JsonEscape(link.LinkedAlarmRelativePath)}\"");
                var temporary = metadataPath + ".tmp." + Guid.NewGuid().ToString("N");
                File.WriteAllText(temporary, json, new UTF8Encoding(false));
                File.Replace(temporary, metadataPath, null);
            }
            catch
            {
                // 链文件本身仍是主关联证据；元数据补链失败由后续清单校验发现。
            }
        }

        private void ReportSnapshotFailure(string message, Exception exception = null)
        {
            _log.Error(message, "落盘", exception);
            try
            {
                var path = Path.Combine(
                    _cfg.Test.StoreDir,
                    _cfg.Test.TestName,
                    "snapshot-export-errors.log");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(
                    path,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}",
                    new UTF8Encoding(false));
            }
            catch { }
            NonCriticalObserver.Invoke(
                SnapshotExportFailed,
                message,
                ex => _log?.Warn($"快照失败观察者异常，已隔离：{ex.Message}", "落盘"));
        }

        public WarningSnapshotStorageStatus GetWarningSnapshotStorageStatus()
        {
            return GetWarningSnapshotStorageStatus(false);
        }

        private WarningSnapshotStorageStatus GetWarningSnapshotStorageStatus(bool forceRefresh)
        {
            lock (_warningSnapshotStorageCacheGate)
            {
                if (!forceRefresh &&
                    _warningSnapshotStorageCache != null &&
                    DateTime.UtcNow - _warningSnapshotStorageCacheUtc < TimeSpan.FromSeconds(30))
                    return _warningSnapshotStorageCache;
            }
            var cfg = AlarmConfig?.WarningSnapshots ?? new WarningSnapshotConfig();
            var root = GetWarningSnapshotRoot(cfg);
            EnsureWarningSnapshotStorageCounter(root);
            long used;
            long snapshots;
            lock (_warningSnapshotStorageCacheGate)
            {
                used = Math.Max(0L, _warningSnapshotTrackedUsedBytes);
                snapshots = Math.Max(0L, _warningSnapshotTrackedCount);
            }
            long free = 0;
            try { free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))).AvailableFreeSpace; } catch { }
            var average = snapshots > 0 ? Math.Max(1L, used / snapshots) : 0L;
            var status = new WarningSnapshotStorageStatus
            {
                RootDirectory = root,
                UsedBytes = used,
                FreeBytes = free,
                EstimatedAdditionalCycles = average > 0 ? free / average : 0,
                IsBelowFreeSpaceWarning = free > 0 &&
                    free < Math.Max(0L, cfg.DiskFreeWarningMb) * 1024L * 1024L
            };
            lock (_warningSnapshotStorageCacheGate)
            {
                _warningSnapshotStorageCache = status;
                _warningSnapshotStorageCacheUtc = DateTime.UtcNow;
            }
            return status;
        }

        private string GetWarningSnapshotRoot(WarningSnapshotConfig cfg)
        {
            return Path.Combine(
                _cfg.Test.StoreDir,
                _cfg.Test.TestName,
                string.IsNullOrWhiteSpace(cfg?.RootDirectory)
                    ? "WarningSnapshots"
                    : cfg.RootDirectory.Trim());
        }

        private void EnsureWarningSnapshotStorageCounter(string root)
        {
            lock (_warningSnapshotStorageCacheGate)
            {
                if (_warningSnapshotStorageCounterInitialized &&
                    string.Equals(
                        _warningSnapshotTrackedRoot,
                        root,
                        StringComparison.OrdinalIgnoreCase))
                    return;

                long used = 0;
                long snapshots = 0;
                if (Directory.Exists(root))
                {
                    foreach (var path in Directory.EnumerateFiles(
                                 root,
                                 "*",
                                 SearchOption.AllDirectories))
                    {
                        if (path.IndexOf(
                                Path.DirectorySeparatorChar + "Archive" + Path.DirectorySeparatorChar,
                                StringComparison.OrdinalIgnoreCase) >= 0)
                            continue;
                        try { used += new FileInfo(path).Length; } catch { }
                        if (string.Equals(
                                Path.GetFileName(path),
                                "warning-metadata.json",
                                StringComparison.OrdinalIgnoreCase))
                            snapshots++;
                    }
                }
                _warningSnapshotTrackedRoot = root;
                _warningSnapshotTrackedUsedBytes = Math.Max(0L, used);
                _warningSnapshotTrackedCount = Math.Max(0L, snapshots);
                _warningSnapshotStorageCounterInitialized = true;
            }
        }

        private void TrackWarningSnapshotCreated(string directory)
        {
            TrackWarningSnapshotBytes(GetDirectoryBytes(directory), 1);
        }

        private void TrackWarningSnapshotDeleted(long bytes, long snapshots)
        {
            TrackWarningSnapshotBytes(-Math.Max(0L, bytes), -Math.Max(0L, snapshots));
        }

        private void TrackWarningSnapshotBytes(long bytesDelta, long snapshotDelta)
        {
            lock (_warningSnapshotStorageCacheGate)
            {
                _warningSnapshotTrackedUsedBytes = Math.Max(
                    0L,
                    _warningSnapshotTrackedUsedBytes + bytesDelta);
                _warningSnapshotTrackedCount = Math.Max(
                    0L,
                    _warningSnapshotTrackedCount + snapshotDelta);
                _warningSnapshotStorageCacheUtc = DateTime.MinValue;
            }
        }

        private static long GetDirectoryBytes(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return 0L;
            long bytes = 0;
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                try { bytes += new FileInfo(path).Length; } catch { }
            return Math.Max(0L, bytes);
        }

        private void InvalidateWarningSnapshotStorageCache()
        {
            lock (_warningSnapshotStorageCacheGate)
                _warningSnapshotStorageCacheUtc = DateTime.MinValue;
        }

        private void PublishWarningSnapshotStorageStatus()
        {
            try
            {
                var status = GetWarningSnapshotStorageStatus();
                NonCriticalObserver.Invoke(
                    WarningSnapshotStorageChanged,
                    status,
                    ex => _log?.Warn($"预警快照存储观察者异常，已隔离：{ex.Message}", "落盘"));
                if (status.IsBelowFreeSpaceWarning)
                {
                    if (System.Threading.Interlocked.Exchange(
                            ref _warningSnapshotFreeSpaceWarningActive, 1) == 0)
                        _log.Warn(
                            $"WarningSnapshots 磁盘余量低：Free={status.FreeBytes / 1024d / 1024d:F0}MB " +
                            $"Root={status.RootDirectory}",
                            "落盘");
                }
                else
                {
                    System.Threading.Interlocked.Exchange(
                        ref _warningSnapshotFreeSpaceWarningActive, 0);
                }
            }
            catch { }
        }

        private void EnforceSoftWarningQuota(Guid currentRunId, WarningSnapshotConfig cfg)
        {
            if (cfg == null || cfg.SoftWarningQuotaMb <= 0 || currentRunId == Guid.Empty) return;
            var root = Path.Combine(
                _cfg.Test.StoreDir,
                _cfg.Test.TestName,
                string.IsNullOrWhiteSpace(cfg.RootDirectory) ? "WarningSnapshots" : cfg.RootDirectory.Trim());
            if (!Directory.Exists(root)) return;
            var quotaBytes = cfg.SoftWarningQuotaMb * 1024L * 1024L;
            var currentToken = $"\"TestRunId\": \"{currentRunId:N}\"";
            var candidates = Directory.EnumerateFiles(root, "warning-metadata.json", SearchOption.AllDirectories)
                .Where(x => x.IndexOf(Path.DirectorySeparatorChar + "Archive" + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) < 0)
                .Select(Path.GetDirectoryName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(dir =>
                {
                    try { return !File.ReadAllText(Path.Combine(dir, "warning-metadata.json")).Contains(currentToken); }
                    catch { return false; }
                })
                .OrderBy(dir => new DirectoryInfo(dir).CreationTimeUtc)
                .ToArray();

            foreach (var directory in candidates)
            {
                if (GetWarningSnapshotStorageStatus(true).UsedBytes <= quotaBytes) break;
                ArchiveVerifiedSoftWarning(directory, root);
            }
        }

        private void ArchiveVerifiedSoftWarning(string directory, string root)
        {
            var sourceBytes = GetDirectoryBytes(directory);
            var archiveRoot = Path.Combine(root, "Archive");
            Directory.CreateDirectory(archiveRoot);
            var baseName = Path.GetFileName(directory) + "-" + Guid.NewGuid().ToString("N");
            var temporary = Path.Combine(archiveRoot, baseName + ".zip.tmp");
            var completed = Path.Combine(archiveRoot, baseName + ".zip");
            ZipFile.CreateFromDirectory(directory, temporary, CompressionLevel.Optimal, false);
            using (var archive = ZipFile.OpenRead(temporary))
            {
                if (archive.Entries.Count == 0)
                    throw new InvalidDataException("软预警归档为空，拒绝删除源目录。");
            }
            File.Move(temporary, completed);
            File.WriteAllText(completed + ".sha256", ComputeSha256(completed), new UTF8Encoding(false));
            Directory.Delete(directory, true);
            TrackWarningSnapshotDeleted(sourceBytes, 1);
            _log.Info($"旧批次软预警已校验归档：{completed}", "落盘");
        }

        private static string ComputeSha256(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static string GetWarningChainKey(int channel, AdaptiveWarningCode code) =>
            GetWarningChainKey(channel, code.ToString());

        private static string GetWarningChainKey(int channel, string code) => $"{channel}:{code}";

        internal static string GetWarningSnapshotRateLimitKey(
            Guid runId,
            int channel,
            string code)
        {
            // 完整证据的 10 分钟限频只能在同一次运行内合并。同一进程开始新 Run 后，
            // 即使 EPB 与告警类别相同，首个事故也必须获得独立的完整证据。
            var runKey = runId == Guid.Empty ? "NoRun" : runId.ToString("N");
            return $"{runKey}:{GetWarningChainKey(channel, code ?? "Unknown")}";
        }
        private static string JsonEscape(string value) => (value ?? string.Empty)
            .Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        private static string MakeRelativePath(string root, string path)
        {
            var rootUri = new Uri(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            var pathUri = new Uri(Path.GetFullPath(path));
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(pathUri).ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        private sealed class WarningSnapshotLink
        {
            public int CycleNumber;
            public int Streak;
            public int ConfirmThreshold;
            public string RelativePath;
            public string LinkedAlarmRelativePath;
        }
    }
}
