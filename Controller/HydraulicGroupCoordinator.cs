// ====================== HydraulicGroupCoordinator.cs（内联到 EpbManager.cs）======================
// 目的：统一管理“组内首个电控前建压；最后一个电压释放点后释放液压”的跨卡钳节拍。
// 依赖：TestConfig.Hydraulics[*].Members 列出该液压控制的所有卡钳通道号；
//       若 Members 为空，则回退到 DOConfig 中每个 EPB Record 的可选 HydraulicId 关联。
// 线程安全：内部用 ConcurrentDictionary + 锁；可同时被多路 EpbCycleRunner 调用。

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Config;
using IO.NI;
using IAppLogger = Config.IAppLogger;
using NullLogger = Config.NullLogger;

namespace Controller
{
    public enum FaultScope
    {
        Channel,
        ElectricalGroup,
        HydraulicGroup,
        DaqGroup,
        Global
    }

    public sealed class ControlFault
    {
        public ControlFault(string code, string reason, FaultScope scope, int[] affectedChannels,
            int? groupId, DateTime timestampUtc, Guid correlationId)
        {
            Code = code ?? string.Empty;
            Reason = reason ?? string.Empty;
            Scope = scope;
            AffectedChannels = affectedChannels ?? Array.Empty<int>();
            GroupId = groupId;
            TimestampUtc = timestampUtc;
            CorrelationId = correlationId;
        }
        public string Code { get; }
        public string Reason { get; }
        public FaultScope Scope { get; }
        public int[] AffectedChannels { get; }
        public int? GroupId { get; }
        public DateTime TimestampUtc { get; }
        public Guid CorrelationId { get; }
    }

    public enum HydraulicPhaseKind
    {
        PreRelease,
        Learning,
        Formal,
        SingleChannel,
        Recovery
    }

    public sealed class HydraulicGenerationKey : IEquatable<HydraulicGenerationKey>
    {
        public HydraulicGenerationKey(Guid testRunId, int hydraulicId, HydraulicPhaseKind phaseKind, long slot)
        {
            TestRunId = testRunId;
            HydraulicId = hydraulicId;
            PhaseKind = phaseKind;
            Slot = slot;
        }
        public Guid TestRunId { get; }
        public int HydraulicId { get; }
        public HydraulicPhaseKind PhaseKind { get; }
        public long Slot { get; }
        public bool Equals(HydraulicGenerationKey other) => other != null && TestRunId == other.TestRunId &&
            HydraulicId == other.HydraulicId && PhaseKind == other.PhaseKind && Slot == other.Slot;
        public override bool Equals(object obj) => Equals(obj as HydraulicGenerationKey);
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = TestRunId.GetHashCode();
                hash = (hash * 397) ^ HydraulicId;
                hash = (hash * 397) ^ (int)PhaseKind;
                hash = (hash * 397) ^ Slot.GetHashCode();
                return hash;
            }
        }
        public override string ToString() => $"{TestRunId:N}/{HydraulicId}/{PhaseKind}/{Slot}";
    }

    public sealed class HydraulicCycleLease
    {
        internal HydraulicCycleLease(
            HydraulicGenerationKey key,
            IReadOnlyList<int> members,
            PressureQualification qualification,
            DateTime actuationAnchorUtc)
        {
            Key = key;
            Members = members;
            Qualification = qualification;
            ActuationAnchorUtc = actuationAnchorUtc;
        }

        public HydraulicGenerationKey Key { get; }
        public IReadOnlyList<int> Members { get; }
        public PressureQualification Qualification { get; }
        /// <summary>
        /// 同一液压代次内所有电机相位共同使用的未来锚点。该值只在资格完成时生成一次，
        /// 防止多个等待者分别以自己的恢复时刻计算相位而重新聚拢。
        /// </summary>
        public DateTime ActuationAnchorUtc { get; }
    }

    internal sealed class HydraulicReleaseTimeoutException : TimeoutException
    {
        public HydraulicReleaseTimeoutException(
            int hydraulicId,
            double lastPressureBar,
            double safePressureBar,
            int timeoutMs,
            string detail = null)
            : base(
                $"HydraulicReleaseTimeout Hydraulic={hydraulicId} " +
                $"LastPressure={lastPressureBar:F3}bar SafePressure={safePressureBar:F3}bar " +
                $"Timeout={timeoutMs}ms" +
                (string.IsNullOrWhiteSpace(detail) ? string.Empty : $" Detail={detail}"))
        {
        }
    }

    public enum HydraulicPressureFailureReason
    {
        NoSample,
        InvalidValue,
        StaleSample,
        BelowMinimum
    }

    public sealed class HydraulicPressureLostException : InvalidOperationException
    {
        public HydraulicPressureLostException(
            int hydraulicId,
            long generationId,
            double actualBar,
            double minimumBar,
            HydraulicPressureFailureReason failureReason,
            double sampleAgeMs)
            : base($"HydraulicPressureLost Hydraulic={hydraulicId} Generation={generationId} " +
                   $"Reason={failureReason} Actual={actualBar:F3}bar Minimum={minimumBar:F3}bar " +
                   $"AgeMs={sampleAgeMs:F1}")
        {
            FailureReason = failureReason;
            SampleAgeMs = sampleAgeMs;
        }

        public HydraulicPressureFailureReason FailureReason { get; }
        public double SampleAgeMs { get; }
    }

    public sealed class HydraulicBarrierTimeoutException : TimeoutException
    {
        public HydraulicBarrierTimeoutException(
            HydraulicGenerationKey key,
            int timeoutMs,
            IEnumerable<int> pendingMembers)
            : base($"HydraulicBarrierTimeout Hydraulic={key.HydraulicId} Run={key.TestRunId:N} " +
                   $"Phase={key.PhaseKind} Slot={key.Slot} Timeout={timeoutMs}ms " +
                   $"Pending=[{string.Join(",", pendingMembers ?? Array.Empty<int>())}]") { }
    }

    internal sealed class HydraulicGroupCoordinator
    {
        private readonly AoController _ao;

        // channel -> hydId（优先 TestConfig.Hydraulics[*].Members；其次 DOConfig.EPB[*].HydraulicId）
        private readonly Dictionary<int, int> _channel2Hyd = new();
        private readonly DoController _do;
        private readonly DoConfig _doCfg;

        // 可选：引用已有液压控制器。若存在则优先用它的“保持-外部释放”能力。
        // 注意：我们改成使用你现有的 HydraulicController.BuildAndHoldAsync / Release / TryGetReleaseDelegate
        private readonly HydraulicController _hydCtl;

        private readonly ConcurrentDictionary<int, Latch> _latches = new();
        private readonly IAppLogger _log;
        private readonly Func<int, double> _readPressure;
        private readonly Func<int, PressureSample> _readPressureSample;
        private readonly Func<int, Task> _testReleaseAction;
        private readonly TestConfig _test;
        private readonly ConcurrentDictionary<HydraulicGenerationKey, GenerationState> _generations = new();
        private readonly ConcurrentDictionary<int, SemaphoreSlim> _generationGates = new();

        public event Action<ControlFault> FaultRaised;

        public HydraulicGroupCoordinator(TestConfig test,
            DoConfig dO,
            DoController doController,
            Func<int, double> readPressure,
            AoController aoController,
            HydraulicController hydCtl,
            IAppLogger log)
        {
            _test = test ?? throw new ArgumentNullException(nameof(test));
            _doCfg = dO ?? new DoConfig();
            _do = doController ?? throw new ArgumentNullException(nameof(doController));
            _ao = aoController; // 可空：无 AO 时仍可只用 HydraulicController
            _readPressure = readPressure; // 可空：若仅用 HydraulicController，可不需要读压
            _readPressureSample = readPressure == null
                ? null
                : id => new PressureSample(id, readPressure(id), DateTime.UtcNow, Stopwatch.GetTimestamp());
            _hydCtl = hydCtl; // 可空：无专用控制器则走 Fallback
            _log = log ?? NullLogger.Instance;

            // 1) 先用 TestConfig.Hydraulics[*].Members 做映射
            foreach (var h in _test.Hydraulics)
            {
                if (h?.Enabled != true) continue;
                if (h.Members != null && h.Members.Count > 0)
                    foreach (var ch in h.Members.Distinct())
                        _channel2Hyd[ch] = h.Id;
            }

            // 2) 对未覆盖的通道，用 DOConfig.EPB 的 HydraulicId 兜底
            // 这段代码暂时无用，doconfig中暂时没有hydraulicid字段
            if (_doCfg?.Epb != null)
                foreach (var r in _doCfg.Epb)
                    if (r.Enabled && r.Channel > 0 && r.HydraulicId.HasValue && !_channel2Hyd.ContainsKey(r.Channel))
                        _channel2Hyd[r.Channel] = r.HydraulicId.Value;
        }

        public HydraulicGroupCoordinator(TestConfig test,
            DoConfig dO,
            DoController doController,
            Func<int, PressureSample> readPressureSample,
            AoController aoController,
            HydraulicController hydCtl,
            IAppLogger log)
            : this(
                test,
                dO,
                doController,
                id => readPressureSample(id).ValueBar,
                aoController,
                hydCtl,
                log)
        {
            _readPressureSample = readPressureSample ?? throw new ArgumentNullException(nameof(readPressureSample));
        }

        internal HydraulicGroupCoordinator(
            TestConfig test,
            Func<int, double> readPressure,
            Func<int, Task> releaseAction,
            IAppLogger log = null)
        {
            _test = test ?? throw new ArgumentNullException(nameof(test));
            _doCfg = new DoConfig();
            _readPressure = readPressure;
            _readPressureSample = readPressure == null
                ? null
                : id => new PressureSample(id, readPressure(id), DateTime.UtcNow, Stopwatch.GetTimestamp());
            _testReleaseAction = releaseAction ?? throw new ArgumentNullException(nameof(releaseAction));
            _log = log ?? NullLogger.Instance;

            foreach (var h in _test.Hydraulics)
            {
                if (h?.Enabled != true || h.Members == null) continue;
                foreach (var ch in h.Members.Distinct())
                    _channel2Hyd[ch] = h.Id;
            }
        }

        internal HydraulicGroupCoordinator(
            TestConfig test,
            Func<int, PressureSample> readPressureSample,
            Func<int, Task> releaseAction,
            IAppLogger log = null)
            : this(test, sampleReader: readPressureSample, releaseAction, hydCtl: null, log)
        {
        }

        private HydraulicGroupCoordinator(
            TestConfig test,
            Func<int, PressureSample> sampleReader,
            Func<int, Task> releaseAction,
            HydraulicController hydCtl,
            IAppLogger log)
        {
            _test = test ?? throw new ArgumentNullException(nameof(test));
            _doCfg = new DoConfig();
            _readPressureSample = sampleReader ?? throw new ArgumentNullException(nameof(sampleReader));
            _readPressure = id => sampleReader(id).ValueBar;
            _testReleaseAction = releaseAction;
            _hydCtl = hydCtl;
            _log = log ?? NullLogger.Instance;
            foreach (var h in _test.Hydraulics)
            {
                if (h?.Enabled != true || h.Members == null) continue;
                foreach (var ch in h.Members.Distinct()) _channel2Hyd[ch] = h.Id;
            }
        }

        /// <summary>
        /// 以压力组代次为单位冻结成员并完成实际压力资格。相同Key重复调用只返回同一任务，
        /// 不会把已经释放的成员重新加入。
        /// </summary>
        public async Task<HydraulicCycleLease> EnterGenerationAsync(
            HydraulicGenerationKey key,
            IReadOnlyCollection<int> participants,
            CancellationToken token)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            var members = (participants ?? Array.Empty<int>())
                .Where(ch => _channel2Hyd.TryGetValue(ch, out var h) && h == key.HydraulicId)
                .Distinct()
                .OrderBy(ch => ch)
                .ToArray();
            if (members.Length == 0)
                throw new InvalidOperationException($"Hydraulic={key.HydraulicId} 代次没有有效参与成员。");

            var state = _generations.GetOrAdd(key, _ => new GenerationState(key, members));
            if (!state.Members.SequenceEqual(members))
                throw new InvalidOperationException(
                    $"Hydraulic generation members are immutable. Key={key} " +
                    $"Existing=[{string.Join(",", state.Members)}] Requested=[{string.Join(",", members)}]");

            Task<HydraulicCycleLease> initialize;
            lock (state.Gate)
            {
                initialize = state.InitializeTask;
                if (initialize == null)
                {
                    initialize = InitializeGenerationAsync(state, token);
                    state.InitializeTask = initialize;
                }
            }

            return await initialize.ConfigureAwait(false);
        }

        public async Task MarkVoltageReleaseAsync(HydraulicCycleLease lease, int epbChannel)
        {
            if (lease == null) return;
            if (!_generations.TryGetValue(lease.Key, out var state))
            {
                _log.Warn($"忽略过期液压释放：EPB={epbChannel} Key={lease.Key}", "液压协调");
                return;
            }

            var releaseNow = false;
            lock (state.Gate)
            {
                if (!state.Remaining.Remove(epbChannel))
                {
                    _log.Warn(
                        $"液压释放重复或代次不匹配：EPB={epbChannel} Key={lease.Key}",
                        "液压协调");
                }
                if (state.Remaining.Count == 0 && !state.ReleaseStarted)
                {
                    state.ReleaseStarted = true;
                    releaseNow = true;
                }
            }

            if (releaseNow)
                _ = CompleteGenerationReleaseAsync(state);

            var item = GetHydraulicItem(lease.Key.HydraulicId);
            var timeoutMs = item.BarrierTimeoutMs > 0
                ? item.BarrierTimeoutMs
                : Math.Max(1, _test.PeriodMs);
            var completed = await Task.WhenAny(
                    state.Completion.Task,
                    Task.Delay(timeoutMs))
                .ConfigureAwait(false);
            if (completed != state.Completion.Task)
            {
                int[] pending;
                lock (state.Gate) pending = state.Remaining.OrderBy(x => x).ToArray();
                var ex = new HydraulicBarrierTimeoutException(lease.Key, timeoutMs, pending);
                await FailGenerationAsync(state, ex).ConfigureAwait(false);
                throw ex;
            }

            await state.Completion.Task.ConfigureAwait(false);
        }

        public async Task ForceReleaseAsync(int hydraulicId, string reason)
        {
            var active = _generations.Values
                .Where(x => x.Key.HydraulicId == hydraulicId && !x.Completion.Task.IsCompleted)
                .ToArray();
            foreach (var state in active)
                await FailGenerationAsync(
                        state,
                        new OperationCanceledException($"HydraulicForceRelease Reason={reason}"),
                        publishFault: false)
                    .ConfigureAwait(false);

            // 即使某代次已由另一故障线程进入 FailGeneration，也再次幂等回零，
            // 并以实际压力连续安全作为 StopAll/恢复流程的完成依据。
            await ExecuteReleaseOutputAsync(hydraulicId).ConfigureAwait(false);
            await WaitForSafePressureAsync(hydraulicId).ConfigureAwait(false);
        }

        private async Task<HydraulicCycleLease> InitializeGenerationAsync(
            GenerationState state,
            CancellationToken token)
        {
            var gate = _generationGates.GetOrAdd(state.Key.HydraulicId, _ => new SemaphoreSlim(1, 1));
            var item = GetHydraulicItem(state.Key.HydraulicId);
            var timeoutMs = item.BarrierTimeoutMs > 0 ? item.BarrierTimeoutMs : Math.Max(1, _test.PeriodMs);
            if (!await gate.WaitAsync(timeoutMs, token).ConfigureAwait(false))
                throw new HydraulicBarrierTimeoutException(state.Key, timeoutMs, state.Members);
            state.GenerationGate = gate;

            try
            {
                PressureQualification qualification;
                if (_hydCtl != null)
                {
                    qualification = await _hydCtl.BuildAndQualifyAsync(
                            state.Key.HydraulicId,
                            state.Key.Slot,
                            token)
                        .ConfigureAwait(false);
                }
                else
                {
                    // 测试构造路径由测试释放委托模拟硬件；仍返回明确资格对象。
                    var sample = _readPressureSample(state.Key.HydraulicId);
                    qualification = new PressureQualification(
                        state.Key.HydraulicId,
                        state.Key.Slot,
                        item.PressureThresholdBar,
                        sample.ValueBar,
                        DateTime.UtcNow,
                        item.BuildStableMs,
                        sample.ValueBar,
                        sample.ValueBar,
                        item.PressureThresholdBar,
                        double.NaN);
                }

                state.Qualification = qualification;
                state.MonitorCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                state.MonitorTask = MonitorQualifiedPressureAsync(state, state.MonitorCts.Token);
                // 所有等待同一 InitializeTask 的通道拿到完全相同的未来执行锚点。
                // 留出极小调度裕量，保证零相位也不会以“过期任务”立即补发。
                var actuationAnchorUtc = DateTime.UtcNow.AddMilliseconds(2);
                return new HydraulicCycleLease(
                    state.Key,
                    state.Members,
                    qualification,
                    actuationAnchorUtc);
            }
            catch (Exception ex)
            {
                await FailGenerationAsync(state, ex).ConfigureAwait(false);
                throw;
            }
        }

        private async Task MonitorQualifiedPressureAsync(GenerationState state, CancellationToken token)
        {
            var item = GetHydraulicItem(state.Key.HydraulicId);
            var minimumBar = item.PressureThresholdBar - Math.Max(0, item.HoldDropToleranceBar);
            long? lowSince = null;
            var clock = Stopwatch.StartNew();
            try
            {
                while (!token.IsCancellationRequested)
                {
                    lock (state.Gate)
                    {
                        if (state.ReleaseStarted) return;
                    }

                    var sample = _readPressureSample(state.Key.HydraulicId);
                    var failureReason = HydraulicController.ClassifyPressureSampleFailure(
                        sample,
                        minimumBar,
                        item.PressureSampleMaxAgeMs);
                    var valid = !failureReason.HasValue;
                    if (!valid)
                    {
                        if (!lowSince.HasValue) lowSince = clock.ElapsedMilliseconds;
                        if (clock.ElapsedMilliseconds - lowSince.Value >= Math.Max(0, item.HoldDropConfirmMs))
                        {
                            var ex = new HydraulicPressureLostException(
                                state.Key.HydraulicId,
                                state.Key.Slot,
                                sample.ValueBar,
                                minimumBar,
                                failureReason ?? HydraulicPressureFailureReason.InvalidValue,
                                sample.AgeMs);
                            await FailGenerationAsync(state, ex).ConfigureAwait(false);
                            return;
                        }
                    }
                    else
                    {
                        lowSince = null;
                    }
                    await Task.Delay(10, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        }

        private async Task CompleteGenerationReleaseAsync(GenerationState state)
        {
            try
            {
                state.MonitorCts?.Cancel();
                await ExecuteReleaseOutputAsync(state.Key.HydraulicId).ConfigureAwait(false);
                await WaitForSafePressureAsync(state.Key.HydraulicId).ConfigureAwait(false);
                state.Completion.TrySetResult(true);
            }
            catch (Exception ex)
            {
                state.Completion.TrySetException(ex);
                PublishFault(state, ex);
            }
            finally
            {
                ReleaseGenerationGate(state);
            }
        }

        private async Task FailGenerationAsync(
            GenerationState state,
            Exception exception,
            bool publishFault = true)
        {
            if (Interlocked.Exchange(ref state.FailStarted, 1) != 0) return;
            lock (state.Gate) state.ReleaseStarted = true;
            try
            {
                state.MonitorCts?.Cancel();
                await ExecuteReleaseOutputAsync(state.Key.HydraulicId).ConfigureAwait(false);
            }
            finally
            {
                state.Completion.TrySetException(exception);
                if (publishFault)
                    PublishFault(state, exception);
                ReleaseGenerationGate(state);
            }
        }

        private async Task ExecuteReleaseOutputAsync(int hydraulicId)
        {
            if (_testReleaseAction != null)
            {
                await _testReleaseAction(hydraulicId).ConfigureAwait(false);
                return;
            }
            if (_hydCtl != null)
            {
                await _hydCtl.ForceReleaseAsync(hydraulicId).ConfigureAwait(false);
                return;
            }
            try { _do?.SetPressure(hydraulicId, false); } catch { }
            try { _ao?.WritePressure(hydraulicId == 1 ? "Cylinder1" : "Cylinder2", 0); } catch { }
        }

        private void PublishFault(GenerationState state, Exception exception)
        {
            _log.Error(exception.Message, "液压协调", exception);
            try
            {
                FaultRaised?.Invoke(new ControlFault(
                    exception is HydraulicBarrierTimeoutException ? "HydraulicBarrierTimeout" :
                    exception is HydraulicPressureLostException ? "HydraulicPressureLost" :
                    exception is HydraulicBuildTimeoutException ? "HydraulicBuildTimeout" :
                    "HydraulicFault",
                    exception.Message,
                    FaultScope.HydraulicGroup,
                    state.Members.ToArray(),
                    state.Key.HydraulicId,
                    DateTime.UtcNow,
                    state.Key.TestRunId));
            }
            catch { }
        }

        private void ReleaseGenerationGate(GenerationState state)
        {
            if (Interlocked.Exchange(ref state.GateReleased, 1) != 0) return;
            try { state.GenerationGate?.Release(); } catch { }
        }

        private HydraulicItem GetHydraulicItem(int hydraulicId)
        {
            return _test.Hydraulics.FirstOrDefault(x => x.Id == hydraulicId)
                   ?? throw new InvalidOperationException($"Hydraulic={hydraulicId} 缺少配置。");
        }

        /// <summary>
        ///     ★ 接入点（上电前调用）：声明“我这个通道要开始电控了”，若该组还没建压则先建压。
        ///     - 若有 _hydCtl：启动 BuildAndHoldAsync(hydId, token)，并把 ReleaseAction 绑定为 _hydCtl.Release(hydId)
        ///     - 否则：使用 Fallback DO/AO + 读压保持，到统一释放时撤销
        /// </summary>
        public async Task EnterElectricalPhaseAsync(int epbChannel, CancellationToken token)
        {
            if (!_channel2Hyd.TryGetValue(epbChannel, out var hydId))
            {
                _log.Warn($"EPB[{epbChannel}] 未映射到液压，跳过建压逻辑。", "液压协调");
                return;
            }

            var latch = _latches.GetOrAdd(hydId, _ => new Latch()); // 每个 hydId 一份

            while (true)
            {
                Task priorRelease = null;
                bool needBuild;
                lock (latch.Gate)
                {
                    if (latch.ReleaseStarted)
                    {
                        priorRelease = latch.ReleaseCompletion?.Task ?? Task.CompletedTask;
                        needBuild = false;
                    }
                    else
                    {
                        needBuild = !latch.PressureOn;
                        if (needBuild)
                        {
                            latch.Generation++;
                            latch.PressureOn = true;
                            latch.ReleaseStarted = false;
                            latch.ReleaseCompletion =
                                new TaskCompletionSource<bool>(
                                    TaskCreationOptions.RunContinuationsAsynchronously);
                        }

                        latch.InFlight.Add(epbChannel);
                    }
                }

                if (priorRelease == null)
                {
                    if (!needBuild) return;
                    break;
                }

                await priorRelease.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }

            try
            {
                if (_testReleaseAction != null)
                {
                    lock (latch.Gate)
                        latch.ReleaseActionAsync = () => _testReleaseAction(hydId);
                    return;
                }

                if (_hydCtl != null)
                {
                    // 方式A：优先用你已有的 HydraulicController —— BuildAndHoldAsync + Release
                    // BuildAndHoldAsync 会：DO 打开 + AO 输出到设定百分比，并保持，直到 Release(hydId) 或 token 取消。
                    // 我们在后台开一个保持任务；ReleaseAction 直接调用 _hydCtl.Release(hydId)。
                    latch.ReleaseActionAsync = async () =>
                    {
                        try
                        {
                            _hydCtl.Release(hydId);
                        }
                        catch
                        {
                            /* 忽略异常 */
                        }

                        await Task.CompletedTask;
                    };

                    // 异步起保持任务（不阻塞 EPB 的电控流程）
                    _ = Task.Run(() => _hydCtl.BuildAndHoldAsync(hydId, token), token);

                    _log.Info($"液压[{hydId}] 进入保持（HydraulicController.BuildAndHoldAsync）。", "液压协调");

                    // 兼容 RunOnceAsync(HoldUntilRelease) 的“委托释放”用法（可选）：
                    // 若你的业务在别处以 RunOnceAsync(HoldUntilRelease) 启动保持，这里也尝试获取一次释放委托。
                    if (_hydCtl.TryGetReleaseDelegate(hydId, out var rel))
                    {
                        // 如果拿到了委托，优先用控制器的委托（写入日志方便排查）
                        latch.ReleaseActionAsync = rel;
                        _log.Info($"液压[{hydId}] 获取到 TryGetReleaseDelegate 的释放委托。", "液压协调");
                    }
                }
                else
                {
                    // 方式B：回退方案 —— 直接 DO/AO + 读压保持，直到统一释放时取消
                    if (_ao == null || _readPressure == null)
                    {
                        _log.Warn($"液压[{hydId}] 无法进入保持：缺少 AO 或 读压方法。", "液压协调");
                        lock (latch.Gate)
                        {
                            latch.PressureOn = false;
                        }

                        return;
                    }

                    latch.Cts = new CancellationTokenSource();
                    _ = Task.Run(() => FallbackHoldLoopAsync(hydId, latch.Cts.Token), latch.Cts.Token);
                    latch.ReleaseActionAsync = async () =>
                    {
                        try
                        {
                            latch.Cts?.Cancel();
                        }
                        catch
                        {
                        }

                        await Task.Delay(10);
                        try
                        {
                            _do.SetPressure(hydId, false);
                        }
                        catch
                        {
                        }

                        try
                        {
                            var dev = hydId == 1 ? "Cylinder1" : "Cylinder2";
                            _ao.WritePressure(dev, 0);
                        }
                        catch
                        {
                        }
                    };
                    _log.Info($"液压[{hydId}] 进入保持（Fallback）。", "液压协调");
                }
            }
            catch (Exception ex)
            {
                lock (latch.Gate)
                {
                    latch.PressureOn = false;
                    latch.ReleaseStarted = false;
                    latch.ReleaseCompletion?.TrySetException(ex);
                }

                _log.Error($"液压[{hydId}] 建压保持失败：{ex.Message}", "液压协调", ex);
                throw;
            }
        }

        /// <summary>
        ///     ★ 接入点（单通道到达“电压释放点”时调用）：
        ///     若这是该液压组内最后一个待释放的通道，则统一释放液压（调用 ReleaseAction）。
        /// </summary>
        public async Task MarkVoltageReleaseAsync(int epbChannel)
        {
            if (!_channel2Hyd.TryGetValue(epbChannel, out var hydId)) return;
            if (!_latches.TryGetValue(hydId, out var latch)) return;

            bool needReleaseNow;
            int generation;
            Task releaseCompletion;
            Func<Task> releaseAction;
            lock (latch.Gate)
            {
                latch.InFlight.Remove(epbChannel);
                if (!latch.PressureOn || latch.ReleaseCompletion == null)
                    return;

                generation = latch.Generation;
                releaseCompletion = latch.ReleaseCompletion.Task;
                needReleaseNow = latch.InFlight.Count == 0 && !latch.ReleaseStarted;
                if (needReleaseNow)
                    latch.ReleaseStarted = true;
                releaseAction = latch.ReleaseActionAsync;
            }

            if (needReleaseNow)
            {
                try
                {
                    _log.Info(
                        $"液压[{hydId}] 本轮所有成员已到电压释放点：统一释压并等待低压确认。",
                        "液压协调");

                    if (releaseAction != null)
                    {
                        await releaseAction().ConfigureAwait(false);
                    }
                    else
                    {
                        try
                        {
                            _do?.SetPressure(hydId, false);
                        }
                        catch
                        {
                        }

                        try
                        {
                            var dev = hydId == 1 ? "Cylinder1" : "Cylinder2";
                            _ao?.WritePressure(dev, 0);
                        }
                        catch
                        {
                        }
                    }

                    await WaitForSafePressureAsync(hydId).ConfigureAwait(false);
                    latch.ReleaseCompletion.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    latch.ReleaseCompletion.TrySetException(ex);
                }
                finally
                {
                    lock (latch.Gate)
                    {
                        if (latch.Generation == generation)
                        {
                            latch.InFlight.Clear();
                            latch.PressureOn = false;
                            latch.ReleaseStarted = false;
                            latch.Cts?.Dispose();
                            latch.Cts = null;
                            latch.ReleaseActionAsync = null;
                        }
                    }
                }
            }

            await releaseCompletion.ConfigureAwait(false);
        }

        private async Task WaitForSafePressureAsync(int hydId)
        {
            var item = _test.Hydraulics.FirstOrDefault(h => h.Id == hydId);
            var safePressureBar = Math.Max(0, item?.ReleaseSafePressureBar ?? 5);
            var stableMs = Math.Max(0, item?.ReleaseStableMs ?? 100);
            var timeoutMs = Math.Max(1, item?.ReleaseTimeoutMs ?? 5000);
            if (_readPressureSample == null)
            {
                throw new HydraulicReleaseTimeoutException(
                    hydId,
                    double.NaN,
                    safePressureBar,
                    timeoutMs,
                    "PressureReaderUnavailable");
            }

            var clock = Stopwatch.StartNew();
            long? stableSinceMs = null;
            var lastPressureBar = double.NaN;
            var lastDetail = string.Empty;
            while (clock.ElapsedMilliseconds <= timeoutMs)
            {
                PressureSample sample;
                try
                {
                    sample = _readPressureSample(hydId);
                    lastPressureBar = sample.ValueBar;
                }
                catch (Exception ex)
                {
                    throw new HydraulicReleaseTimeoutException(
                        hydId,
                        double.NaN,
                        safePressureBar,
                        timeoutMs,
                        "PressureReadFailed:" + ex.Message);
                }

                var sampleFailure = HydraulicController.ClassifyPressureSampleFailure(
                    sample,
                    double.NegativeInfinity,
                    item?.PressureSampleMaxAgeMs ?? 100);
                var safe = !sampleFailure.HasValue && lastPressureBar <= safePressureBar;
                lastDetail = sampleFailure.HasValue
                    ? $"PressureSample{sampleFailure.Value} AgeMs={sample.AgeMs:F1}"
                    : string.Empty;
                if (safe)
                {
                    if (!stableSinceMs.HasValue)
                        stableSinceMs = clock.ElapsedMilliseconds;
                    if (clock.ElapsedMilliseconds - stableSinceMs.Value >= stableMs)
                    {
                        _log.Info(
                            $"液压[{hydId}] 释压确认完成：Pressure={lastPressureBar:F3}bar，" +
                            $"阈值={safePressureBar:F3}bar，稳定={stableMs}ms。",
                            "液压协调");
                        return;
                    }
                }
                else
                {
                    stableSinceMs = null;
                }

                await Task.Delay(10).ConfigureAwait(false);
            }

            throw new HydraulicReleaseTimeoutException(
                hydId,
                lastPressureBar,
                safePressureBar,
                timeoutMs,
                lastDetail);
        }

        // —— 回退保持实现：DO 打开 + AO 输出百分比，达到阈值后保持，直到外部取消 —— //
        private async Task FallbackHoldLoopAsync(int hydId, CancellationToken token)
        {
            var item = _test.Hydraulics.FirstOrDefault(h => h.Id == hydId);
            if (item == null || !item.Enabled)
            {
                _log.Warn($"液压[{hydId}] 未启用/找不到配置。", "液压协调");
                return;
            }

            var dev = hydId == 1 ? "Cylinder1" : "Cylinder2";
            if (!_do.SetPressure(hydId, true))
            {
                _log.Error($"液压[{hydId}] DO 打开失败（Fallback）。", "液压协调");
                return;
            }

            if (!_ao.WritePressure(dev, item.PressureThresholdBar))
            {
                _log.Error($"液压[{hydId}] AO 输出失败（Fallback）。", "液压协调");
                return;
            }

            if (item.Mode == HydraulicMode.ByPressure || item.Mode == HydraulicMode.Either)
                while (!token.IsCancellationRequested)
                    try
                    {
                        if (HydraulicController.IsPressureWithinTarget(
                                _readPressure(hydId),
                                item.PressureThresholdBar,
                                item.PressureToleranceBar)) break;
                        await Task.Delay(5, token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
            else if (item.Mode == HydraulicMode.ByDuration)
                try
                {
                    await Task.Delay(Math.Max(0, item.DurationMs), token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

            // 保持到取消
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                /* 正常 */
            }
        }

        // hydId 对应的一轮“闩锁”状态
        /// <summary>
        ///     记录某个液压组的当前状态。
        /// </summary>
        private sealed class Latch
        {
            public readonly object Gate = new(); // 
            public readonly HashSet<int> InFlight = new(); // 仍未到“电压释放点”的通道
            public CancellationTokenSource Cts; // Fallback 持有的 CTS（仅回退方案用）
            public bool PressureOn; // 是否已进入“建压保持”状态
            public bool ReleaseStarted;
            public int Generation;
            public TaskCompletionSource<bool> ReleaseCompletion;
            public Func<Task> ReleaseActionAsync; // 统一“释压”动作（优先使用 HydraulicController）
        }

        private sealed class GenerationState
        {
            public GenerationState(HydraulicGenerationKey key, int[] members)
            {
                Key = key;
                Members = members;
                Remaining = new HashSet<int>(members);
            }

            public readonly object Gate = new();
            public HydraulicGenerationKey Key { get; }
            public int[] Members { get; }
            public HashSet<int> Remaining { get; }
            public Task<HydraulicCycleLease> InitializeTask;
            public PressureQualification Qualification;
            public readonly TaskCompletionSource<bool> Completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public CancellationTokenSource MonitorCts;
            public Task MonitorTask;
            public SemaphoreSlim GenerationGate;
            public bool ReleaseStarted;
            public int FailStarted;
            public int GateReleased;
        }
    }
}
