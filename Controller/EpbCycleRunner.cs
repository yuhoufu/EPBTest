using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Config;
using Controller.Adaptive;
using Controller.Alarm;
using IO.NI;
using ILogger = Config.IAppLogger;
using NLogger = Config.NullLogger;

namespace Controller
{
    public sealed partial class EpbCycleRunner
    {
        public delegate double ReadCurrentDelegate(int epbChannel);

        /// <summary>
        ///     获取“预释放”阶段默认保持时长（ms），等于原始字段 <c>_revEmptyKeepMs</c>。
        /// </summary>
        public int DefaultPreReleaseKeepMs { get; } = 500;

        /// <summary>
        /// 预释放寻找反向空行程的最长等待（ms）；与进入空行程后的保持时长相互独立。
        /// </summary>
        public int DefaultPreReleaseDetectTimeoutMs { get; } = 3000;

        private const double PlateauAboveEmptyMarginA = 0.8;
        private const int PlateauWindowMs = 150;
        private const double PlateauFlatRangeA = 0.15;

        // // === 在 EpbCycleRunner 字段区补充（若前面已经有 _currentBus 字段则保留，不要重复定义）===
        // private readonly CurrentRingBuffer[] _currentBus = Enumerable.Range(0, 16)
        //     .Select(_ => new CurrentRingBuffer(131072))
        //     .ToArray();

        // 建议容量：32768 样本（以 2ms 采样计 ≈ 65 秒窗口），够用又省内存。
        // 如仍嫌大，可进一步降到 16384（≈ 32 秒）。
        private const int CURRENT_BUF_CAP = 32768;

        /// <summary>
        ///     进程级共享的电流环形缓冲；避免每个 Runner 都各自持有 16 份副本导致内存爆炸。
        /// </summary>
        private static readonly CurrentRingBuffer[] _currentBus =
            Enumerable.Range(0, 16).Select(_ => new CurrentRingBuffer(CURRENT_BUF_CAP)).ToArray();

        private readonly int _channel;
        private readonly DoController _do;
        private readonly double _emptyBandA;
        private readonly double _ewmaAlpha;
        private readonly int _holdMs = 1000; //默认正向切换到反向的中间转换时间，单位 ms
        private readonly int _hydId;
        private readonly HydraulicController _hydraulic;
        private readonly ILogger _log;

        // 字段区 
        // ★ 新增：便于调用 EpbManager 暴露的液压钩子
        private readonly EpbManager _manager;
        private readonly int _peakIgnoreMs;
        private readonly GlobalConfig _cfg;

        private readonly SafetyMarginControlMode _safetyMarginControlMode = SafetyMarginControlMode.Legacy20251010;

        private readonly double _posThrA;
        private double _safetyMarginA; // 提前断电空间

        private readonly ReadCurrentDelegate _readCurrent; // 读取瞬时电流

        // ⑦ 反向空行程“默认保持”时长（用于“首圈已夹紧/无空行程”时的释放），单位 ms

        private readonly int _sampleMs;


        // —— 在类中加：保存最近几个采样点 —— //
        private readonly Queue<(double I, long Tick)> _slopeWindow = new();
        private readonly int _stableWinMs;

        //private HydraulicOrchestrator _hydCoordinator; // 可选的液压协调器 - 已弃用
        private double _iEmptyFwdA;
        private double _iEmptyRevA;
        private double _tClampRampMs;
        private double _tFwdEmptyMs;

        private double _tFwdPeakDecayMs;
        private double _tRevEmptyMs;
        private double _tRevPeakDecayMs;
        private readonly TwoDeviceAiAcquirer _acq; // 新增：双设备采集器引用


        private double _actualCutoffCurrent = 0; // 新增：实际断电电流值（判断时监测到的值）

        private readonly double _overshootAlarmDeltaA = 0; // 正向峰值超阈值报警增量（A）；<=0 表示禁用（由 AlarmConfig.xml 注入）
        private readonly double _adaptiveOvershootWarningDeltaA = 0.8;
        private readonly int _adaptiveOvershootConfirmCycles = 5;
        private readonly int _peakEvidenceMismatchConfirmCycles = 3;
        private readonly int _adaptiveForwardStallConfirmCycles = 5;

        /// <summary>
        ///     报警事件：由 Runner 判定“异常/过流”等场景触发。
        ///     string 为原因/摘要（供上层触发硬件报警与报警快照导出）。
        /// </summary>
        public event Action<int, string> AlarmRaised;

        /// <summary>
        /// 软预警事件：只用于黄色提示和日志，不触发停机或蜂鸣器。
        /// </summary>
        public event Action<int, string> WarningRaised;
        public event Action<AdaptiveWarningEvent> WarningEvidenceRaised;

        /// <summary>已经先断电的可恢复故障；上层可在安全释压后执行一次受控恢复。</summary>
        public event Action<int, string> RecoverableFaultRaised;


        public EpbCycleRunner(
            int channel,
            int hydId,
            ReadCurrentDelegate readCurrent,
            DoController doController,
            HydraulicController hydraulic,
            double posThresholdA,
            int holdMs,
            int sampleMs = 2,
            int peakIgnoreMs = 80,
            ILogger log = null)
        {
            _channel = channel;
            _hydId = hydId;
            _readCurrent = readCurrent ?? (_ => 0.0);
            _do = doController ?? throw new ArgumentNullException(nameof(doController));
            _hydraulic = hydraulic ?? throw new ArgumentNullException(nameof(hydraulic));
            _posThrA = posThresholdA;
            _holdMs = Math.Max(0, holdMs);
            _sampleMs = Math.Max(1, sampleMs);
            _peakIgnoreMs = Math.Max(0, peakIgnoreMs);
            _log = log ?? NLogger.Instance;
            // 所有构造路径必须具有有效的预释放/旧学习判稳参数，禁止 readonly 默认成 0。
            _emptyBandA = 0.20;
            _ewmaAlpha = 0.20;
            _stableWinMs = 50;
        }


        // 构造器 —— 在你的现有构造器参数里添加 EpbManager manager（可选）并保存
        public EpbCycleRunner(
            int channel,
            int hydId,
            ReadCurrentDelegate readCurrent,
            DoController doController,
            HydraulicController hydraulic,
            double posThresholdA,
            int holdMs,
            int sampleMs = 2,
            int peakIgnoreMs = 80,
            ILogger log = null,
            EpbManager manager = null,
            double overshootAlarmDeltaA = 0) // ★ 新增：峰值超限报警增量（A），<=0 禁用
            : this(channel, hydId, readCurrent, doController, hydraulic, posThresholdA, holdMs, sampleMs, peakIgnoreMs,
                log)
        {
            // …你原有的赋值保持不变…
            _manager = manager; // ★ 保存 manager
            _overshootAlarmDeltaA = overshootAlarmDeltaA;
        }

        public EpbCycleRunner(
            int channel,
            int hydId,
            ReadCurrentDelegate readCurrent,
            DoController doController,
            TwoDeviceAiAcquirer twoDeviceAiAcquirer,
            HydraulicController hydraulic,
            double posThresholdA,
            int holdMs,
            int sampleMs = 2,
            int peakIgnoreMs = 80,
            ILogger log = null,
            GlobalConfig cfg = null,
            EpbManager manager = null,
            double overshootAlarmDeltaA = 0, // ★ 新增：峰值超限报警增量（A），<=0 禁用
            double adaptiveOvershootWarningDeltaA = 0.8,
            int adaptiveOvershootConfirmCycles = 5,
            int adaptiveForwardStallConfirmCycles = 5,
            int peakEvidenceMismatchConfirmCycles = 3,
            SafetyMarginControlMode safetyMarginControlMode = SafetyMarginControlMode.Legacy20251010,
            EpbControlMode epbControlMode = EpbControlMode.LegacyFixedTiming,
            bool adaptiveShadowMode = true,
            EpbAdaptiveProfile adaptiveProfile = null,
            Action<EpbAdaptiveProfile> saveAdaptiveProfile = null,
            EpbProgramSafetySettings programSafetySettings = null)
            : this(channel, hydId, readCurrent, doController, hydraulic, posThresholdA, holdMs, sampleMs, peakIgnoreMs,
                log)
        {
            // …你原有的赋值保持不变…
            _manager = manager; // ★ 保存 manager
            _cfg = cfg;
            _safetyMarginA =
                _cfg?.Test.EpbCycleRunner.GetRunnerChannel(channel).SafetyMarginA ?? 2.0; // SafetyMarginA为null 则设置为2
            _acq = twoDeviceAiAcquirer;
            _overshootAlarmDeltaA = overshootAlarmDeltaA;
            _adaptiveOvershootWarningDeltaA = Math.Max(0.1, adaptiveOvershootWarningDeltaA);
            _adaptiveOvershootConfirmCycles = Math.Max(1, adaptiveOvershootConfirmCycles);
            _adaptiveForwardStallConfirmCycles = Math.Max(1, adaptiveForwardStallConfirmCycles);
            _peakEvidenceMismatchConfirmCycles = Math.Max(1, peakEvidenceMismatchConfirmCycles);
            _safetyMarginControlMode = safetyMarginControlMode;
            _epbControlMode = epbControlMode;
            _adaptiveShadowMode = adaptiveShadowMode;
            _adaptiveProfile = adaptiveProfile?.Clone() ?? new EpbAdaptiveProfile { Channel = channel };
            _saveAdaptiveProfile = saveAdaptiveProfile;
            _adaptiveStateMachine = new EpbAdaptiveCurrentStateMachine(_adaptiveProfile);
            _programSafetySettings =
                programSafetySettings ?? EpbProgramSafetySettings.Load(_log);
            _adaptiveSafetyLimits = _programSafetySettings.ToAdaptiveSafetyLimits();

            // 在此处设置epb卡钳的实际运行参数
            DefaultPreReleaseKeepMs = _cfg?.Test.EpbCycleRunner.GetRunnerChannel(channel).PreReleaseKeepMs ?? 500; // 预释放保持时长
            RevDecayRigidMaxMs = _cfg?.Test.EpbCycleRunner.GetRunnerChannel(channel).RevDecayRigidMaxMs ?? 1000; // // 建议现场可配：80~150ms 
            RevEmptyFixedMs = _cfg?.Test.EpbCycleRunner.GetRunnerChannel(channel).RevEmptyFixedMs ?? 2000; // 反向固定空行程时长
            RevDecayLimitA = _cfg?.Test.EpbCycleRunner.GetRunnerChannel(channel).RevDecayLimitA ?? 3; // 反向电流“衰减限值”
            DefaultPreReleaseDetectTimeoutMs =
                _cfg?.Test.EpbCycleRunner.GetRunnerChannel(channel).PreReleaseDetectTimeoutMs
                ?? Math.Max(3000, RevDecayRigidMaxMs);
        }


        /// <summary>
        ///     兼容入口：执行统一启动定位（正向确认位置 → 断电确认 → 单次反向释放）。
        /// </summary>
        /// <param name="keepMs">
        ///     反向空行程保持时长（毫秒）。为 <c>null</c> 时使用 <see cref="DefaultPreReleaseKeepMs" />。
        /// </param>
        /// <param name="token">取消令牌。</param>
        /// <returns>是否完成启动定位并具备进入学习/正式阶段的条件。</returns>
        public async Task<bool> PreReleaseAsync(int? keepMs, CancellationToken token)
        {
            return await PreReleaseAsync(keepMs, null, token).ConfigureAwait(false);
        }

        /// <summary>执行一次预释放，并允许上层仅调整检测预算；所有电流硬保护保持不变。</summary>
        public async Task<bool> PreReleaseAsync(
            int? keepMs,
            int? detectTimeoutMs,
            CancellationToken token)
        {
            var result = await StartupPositioningAsync(keepMs, detectTimeoutMs, token)
                .ConfigureAwait(false);
            if (result.Succeeded)
                _log.Info(
                    $"EPB[{_channel}] 启动定位完成：{result.CompletionKind}，" +
                    $"Peak={result.PeakCurrentA:F3}A Elapsed={result.ElapsedMs}ms。",
                    "EPB");
            else
                _log.Warn(
                    $"EPB[{_channel}] 启动定位失败：Stage={result.Stage} Code={result.Code} " +
                    $"Reason={result.Reason}",
                    "EPB");
            return result.Succeeded;
        }


        /// <summary>
        ///     由采集线程调用：喂入一个“低时延电流样本”（Stopwatch Tick 与电流），并为本通道的
        ///     “夹紧阈值/平台等待器”（若已 Arm）提供事件驱动的判定输入。
        /// </summary>
        /// <param name="epbChannel">EPB 通道号（1..12）。</param>
        /// <param name="tick">该样本对应的 <see cref="Stopwatch"/> Tick（建议与采集时间戳同源转换而来）。</param>
        /// <param name="currentAmp">电流值（A）。通常来自采集回调的 fast 值。</param>
        /// <remarks>
        /// 线程模型：
        /// <list type="bullet">
        /// <item>本方法可能运行在 NI DAQ 回调线程或后台采集线程上，必须避免阻塞与重操作。</item>
        /// <item>内部仅做 O(1) 写入与“窗口扫描（几十点级）”的轻量判断，避免影响采集链路。</item>
        /// </list>
        /// 设计目的：
        /// <list type="bullet">
        /// <item>让 <see cref="WaitCurrentAboveAsync(double,double,CancellationToken,int,double,double,int)"/> 的触发时刻尽可能贴近采样到达时刻，
        /// 避免因 Task.Delay/线程调度造成“已经远超阈值才触发”。</item>
        /// </list>
        /// </remarks>
        public void FeedCurrentSample(int epbChannel, long tick, double currentAmp)
        {
            FeedCurrentSample(epbChannel, tick, currentAmp, DateTime.UtcNow);
        }

        /// <summary>
        ///     注入带采集时间戳的电流样本。时间戳仅用于诊断证据，控制判定仍使用单调时钟。
        /// </summary>
        public void FeedCurrentSample(
            int epbChannel,
            long tick,
            double currentAmp,
            DateTime sampleUtc)
        {
            if (epbChannel < 1 || epbChannel >= _currentBus.Length) return;
            _currentBus[epbChannel].Add(new CurrentSample(tick, currentAmp));

            // 事件驱动判定：仅对本 Runner 所属通道生效
            if (epbChannel != _channel) return;

            var waiter = Volatile.Read(ref _currentAboveWaiter);
            if (waiter != null)
            {
                try
                {
                    // 正向夹紧阈值判据使用电流幅值（A），避免硬件/标定导致符号翻转影响过流判断。
                    waiter.OnSample(tick, Math.Abs(currentAmp));
                }
                catch
                {
                    // 采集线程必须“永不抛出”影响上游；异常吞掉即可。
                }
            }

            ProcessAdaptiveSample(tick, currentAmp, sampleUtc);
        }

        /// <summary>
        ///     “夹紧阈值/平台等待器”：由 <see cref="WaitCurrentAboveAsync(double,double,CancellationToken,int,double,double,int)"/> Arm，
        ///     再由 <see cref="FeedCurrentSample"/> 在采集样本到达时进行事件驱动判定并完成任务。
        /// </summary>
        private sealed class CurrentAboveWaiter
        {
            private readonly long _startTick;
            private readonly double _thrA;
            private readonly double _safetyMarginA;
            private readonly double _iEmptyFwdA;

            private readonly int _winCap;
            private readonly double[] _ring;
            private int _count;
            private int _head;

            public readonly TaskCompletionSource<(bool ok, bool isPlatform, double iNow, long tick)> Tcs;

            public CurrentAboveWaiter(long startTick, double thrA, double safetyMarginA, double iEmptyFwdA, int winCap)
            {
                _startTick = startTick;
                _thrA = thrA;
                _safetyMarginA = safetyMarginA;
                _iEmptyFwdA = iEmptyFwdA;

                _winCap = Math.Max(1, winCap);
                _ring = new double[_winCap];
                _count = 0;
                _head = 0;

                Tcs = new TaskCompletionSource<(bool ok, bool isPlatform, double iNow, long tick)>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public void OnSample(long tick, double currentAbs)
            {
                if (tick < _startTick) return;
                if (Tcs.Task.IsCompleted) return;

                // ① 阈值判据：I + margin ≥ thr
                if (currentAbs + _safetyMarginA >= _thrA)
                {
                    Tcs.TrySetResult((true, false, currentAbs, tick));
                    return;
                }

                // ② 平台判据：窗口内平坦，且高于 Iempty+ + margin
                _ring[_head] = currentAbs;
                _head = (_head + 1) % _winCap;
                if (_count < _winCap) _count++;

                if (_count == _winCap && _iEmptyFwdA != 0)
                {
                    // 窗口几十点级，直接线性扫描求 min/max
                    double min = _ring[0], max = _ring[0];
                    for (var i = 1; i < _winCap; i++)
                    {
                        var x = _ring[i];
                        if (x < min) min = x;
                        if (x > max) max = x;
                    }

                    var range = max - min;
                    if (range <= PlateauFlatRangeA && currentAbs >= _iEmptyFwdA + PlateauAboveEmptyMarginA)
                    {
                        Tcs.TrySetResult((true, true, currentAbs, tick));
                    }
                }
            }
        }

        /// <summary>
        ///     当前通道“夹紧阈值/平台等待器”的实例。
        ///     仅在 <see cref="WaitCurrentAboveAsync(double,double,CancellationToken,int,double,double,int)"/> 执行期间非空。
        /// </summary>
        private CurrentAboveWaiter _currentAboveWaiter;

        public async Task<bool> LearnAsync(int nCycles, CancellationToken token, int? targetPeriodMs)
        {
            if (targetPeriodMs == null)
            {
                targetPeriodMs = 5000;
                _log.Warn($"EPB[{_channel}] LearnAsync 未显式指定目标周期，默认按 {targetPeriodMs}ms 计算柔性分配。", "EPB");
            }

            static long NowTicks()
            {
                return Stopwatch.GetTimestamp();
            }

            static int MsBetween(long t0, long t1)
            {
                return (int)((t1 - t0) * 1000.0 / Stopwatch.Frequency);
            }

            const double R_HEAD = 0.15;
            const double R_FWD_EMPTY = 0.35;
            const double R_REV_EMPTY = 0.35;

            _log.Info(
                $"EPB[{_channel}] 学习开始，次数={nCycles}；采样={_sampleMs}ms，忽略涌流={_peakIgnoreMs}ms，" +
                $"emptyBand={_emptyBandA:F2}A，stableWin={_stableWinMs}ms，posThr={_posThrA:F2}A，" +
                $"plateau(win={PlateauWindowMs}ms, flat≤{PlateauFlatRangeA:F2}A, +emptyMargin≥{PlateauAboveEmptyMarginA:F2}A)。",
                "EPB");

            // 所有学习入口共用同一启动定位，禁止旧逻辑直接反向顶住机械端。
            var startup = await StartupPositioningAsync(
                    DefaultPreReleaseKeepMs,
                    DefaultPreReleaseDetectTimeoutMs,
                    token)
                .ConfigureAwait(false);
            if (!startup.Succeeded)
            {
                if (_manager != null)
                    await _manager.PublishStartupPositioningFailureAsync(startup).ConfigureAwait(false);
                return false;
            }

            // 采样统计容器
            var fwdPeakList = new List<double>();
            var fwdEmptyList = new List<double>();
            var clampList = new List<double>();
            var revPeakList = new List<double>();
            var revEmptyList = new List<double>();
            var iEmptyFList = new List<double>();
            var iEmptyRList = new List<double>();

            for (var k = 0; k < nCycles; k++)
            {
                token.ThrowIfCancellationRequested();

                var swHyd = Stopwatch.StartNew();
                var hydUsed = (int)swHyd.ElapsedMilliseconds;
                var elecBudgetMs = Math.Max(0, (int)targetPeriodMs - hydUsed);

                // ① 头部未上电（第二圈起）
                var headPlan = 0;
                if (k >= 1 && fwdPeakList.Count > 0 && clampList.Count > 0 && revPeakList.Count > 0)
                {
                    var rigidEst =
                        (int)Math.Round(Median(fwdPeakList) + Median(clampList) + Median(revPeakList));
                    var flexEst = Math.Max(0, elecBudgetMs - rigidEst - Math.Max(0, _holdMs));
                    headPlan = (int)Math.Floor(flexEst * R_HEAD);
                }

                if (headPlan > 0)
                {
                    _log.Info($"EPB[{_channel}] 学习{k + 1}：①头部未上电 {headPlan}ms（按比例 {R_HEAD:P0} 计划）。", "EPB");
                    await Task.Delay(headPlan, token).ConfigureAwait(false);
                }
                else if (k == 0)
                {
                    _log.Info($"EPB[{_channel}] 学习{k + 1}：①头部未上电跳过（首圈不延时）。", "EPB");
                }

                var tElecStart = NowTicks();

                // ② 正向
                CommandForward();
                _log.Info($"EPB[{_channel}] 学习{k + 1}：②正向上电，忽略涌流 {_peakIgnoreMs}ms…", "EPB");
                await Task.Delay(_peakIgnoreMs, token).ConfigureAwait(false);

                var (okEmptyFwd, tEnterEmptyFwd_ms, iEmptyFwd) =
                    await WaitStableAroundAsync(+0.5, +1, _emptyBandA, _stableWinMs, token).ConfigureAwait(false);
                if (!okEmptyFwd)
                {
                    CommandOff();
                    _log.Warn($"EPB[{_channel}] 学习{k + 1}：②未判定到正向空行程，放弃本轮。", "EPB");
                    continue;
                }

                var tFwdPeakDecay = Math.Max(0, tEnterEmptyFwd_ms);
                _log.Info($"EPB[{_channel}] 学习{k + 1}：②FwdPeakDecay={tFwdPeakDecay}ms，Iempty+≈{iEmptyFwd:F2}A。", "EPB");

                // ③+④ 协作式等待（不卡 UI）
                var (okClamp, clampReachTick, clampCause) =
                    await WaitClampCoopAsync(iEmptyFwd, token).ConfigureAwait(false);

                if (!okClamp)
                {
                    CommandOffHighPriority();
                    _log.Warn($"EPB[{_channel}] 学习{k + 1}：④未达到阈值/平台（阈 {_posThrA:F2}A），放弃本轮。", "EPB");
                    continue;
                }

                // 达到判据 → 立即断电
                CommandOffHighPriority();
                _log.Info($"EPB[{_channel}] 学习{k + 1}：已达到夹紧条件（{clampCause}），立即正向断电。", "EPB");

                // ③ 回溯“离开空带上边界”的起点
                var enterTick = tElecStart + (long)(tEnterEmptyFwd_ms * (Stopwatch.Frequency / 1000.0));
                var emptyLeave = iEmptyFwd + _emptyBandA;
                var rampStartTick = _currentBus[_channel].FindFirstUpCrossing(emptyLeave, enterTick);
                if (rampStartTick is null || rampStartTick.Value > clampReachTick)
                    rampStartTick = clampReachTick; // 保守兜底

                var tFwdEmpty = Math.Max(0, MsBetween(enterTick, rampStartTick.Value));
                var tClampRamp = Math.Max(0, MsBetween(rampStartTick.Value, clampReachTick));
                _log.Info($"EPB[{_channel}] 学习{k + 1}：③FwdEmpty(actual)={tFwdEmpty}ms，④ClampRamp={tClampRamp}ms。",
                    "EPB");

                // ⑤ 保持
                var holdUsed = Math.Max(0, _holdMs);
                if (holdUsed > 0)
                {
                    _log.Info($"EPB[{_channel}] 学习{k + 1}：⑤保持 {holdUsed}ms。", "EPB");
                    await Task.Delay(holdUsed, token).ConfigureAwait(false);
                }

                // ⑥ + ⑦ 反向
                CommandReverse();
                _log.Info($"EPB[{_channel}] 学习{k + 1}：⑥反向上电，忽略涌流 {_peakIgnoreMs}ms…", "EPB");
                await Task.Delay(_peakIgnoreMs, token).ConfigureAwait(false);

                var (okEmptyRev, tEnterEmptyRev_ms, iEmptyRev) =
                    await WaitStableAroundAsync(-0.5, -1, _emptyBandA, _stableWinMs, token).ConfigureAwait(false);
                if (!okEmptyRev)
                {
                    CommandOff();
                    _log.Warn($"EPB[{_channel}] 学习{k + 1}：⑥未判定到反向空行程，放弃本轮。", "EPB");
                    continue;
                }

                var tRevPeakDecay = Math.Max(0, tEnterEmptyRev_ms);
                _log.Info($"EPB[{_channel}] 学习{k + 1}：⑥RevPeakDecay={tRevPeakDecay}ms，Iempty-≈{iEmptyRev:F2}A。", "EPB");

                var rigidThis = (int)Math.Round((double)(tFwdPeakDecay + tClampRamp + tRevPeakDecay));
                var flexBudget = elecBudgetMs - headPlan - rigidThis - holdUsed;
                if (flexBudget < 0)
                {
                    _log.Warn(
                        $"EPB[{_channel}] 学习{k + 1}：柔性预算为负（预算{elecBudgetMs}ms，①{headPlan}ms，②④⑥合计{rigidThis}ms，⑤{holdUsed}ms）。" +
                        "将 ③/⑦/⑧ 置 0，仅做 ⑦=0 定时与⑧收口。", "EPB");
                    flexBudget = 0;
                }

                var plan3 = (int)Math.Floor(flexBudget * R_FWD_EMPTY);
                var plan7 = (int)Math.Floor(flexBudget * R_REV_EMPTY);
                var plan8 = Math.Max(0, flexBudget - plan3 - plan7);

                _log.Info(
                    $"EPB[{_channel}] 学习{k + 1}：柔性预算={flexBudget}ms -> ③(计划)={plan3}ms，⑦(计划)={plan7}ms，⑧(计划)≈{plan8}ms。",
                    "EPB");

                var run7 = Math.Max(10, plan7);
                _log.Info($"EPB[{_channel}] 学习{k + 1}：⑦反向空行程定时 {run7}ms…", "EPB");
                await Task.Delay(run7, token).ConfigureAwait(false);
                var tRevEmpty = run7;

                CommandOff();

                var elecElapsed = MsBetween(tElecStart, NowTicks());
                var tailRemain = Math.Max(0, elecBudgetMs - elecElapsed);
                if (tailRemain > 0)
                {
                    _log.Info($"EPB[{_channel}] 学习{k + 1}：⑧尾段收口 {tailRemain}ms（计划≈{plan8}ms）。", "EPB");
                    await Task.Delay(tailRemain, token).ConfigureAwait(false);
                }

                if (plan3 > 0 || tFwdEmpty > 0)
                {
                    var diff = tFwdEmpty - plan3;
                    _log.Info($"EPB[{_channel}] 学习{k + 1}：③实测={tFwdEmpty}ms vs ③计划={plan3}ms，差值={diff}ms。", "EPB");
                }

                // 累积样本
                fwdPeakList.Add(tFwdPeakDecay);
                fwdEmptyList.Add(tFwdEmpty);
                clampList.Add(tClampRamp);
                revPeakList.Add(tRevPeakDecay);
                revEmptyList.Add(tRevEmpty);
                iEmptyFList.Add(iEmptyFwd);
                iEmptyRList.Add(iEmptyRev);

                _log.Info(
                    $"EPB[{_channel}] 学习{k + 1} 汇总：②={tFwdPeakDecay}ms, ③(actual)={tFwdEmpty}ms, ④={tClampRamp}ms, " +
                    $"⑥={tRevPeakDecay}ms, ⑦(planned)={tRevEmpty}ms, I±empty≈({iEmptyFwd:F2},{iEmptyRev:F2})A。", "EPB");

                // 阶段统计
                var tHead = headPlan;
                var t2 = (int)Math.Round((decimal)tFwdPeakDecay);
                var t3 = (int)Math.Round((decimal)tFwdEmpty);
                var t4 = (int)Math.Round((decimal)tClampRamp);
                var t5 = Math.Max(0, _holdMs);
                var t6 = (int)Math.Round((decimal)tRevPeakDecay);
                var t7 = (int)Math.Round((decimal)tRevEmpty);
                var t8 = tailRemain;
                var total = tHead + t2 + t3 + t4 + t5 + t6 + t7 + t8;

                _log.Info(
                    $"EPB[{_channel}] 学习{k + 1}阶段统计：" +
                    $"①Head={tHead}ms, ②FwdPeak={t2}ms, ③FwdEmpty={t3}ms, ④ClampRamp={t4}ms, " +
                    $"⑤Hold={t5}ms, ⑥RevPeak={t6}ms, ⑦RevEmpty={t7}ms, ⑧Tail={t8}ms | 合计={total}ms (目标≈{targetPeriodMs}ms)",
                    "EPB");
            }

            if (!fwdPeakList.Any() || !revPeakList.Any())
            {
                _log.Warn($"EPB[{_channel}] 学习失败：有效样本不足（至少应包含 ② 与 ⑥）。", "EPB");
                return false;
            }

            _tFwdPeakDecayMs = Median(fwdPeakList);
            _tClampRampMs = clampList.Any() ? Median(clampList) : 0.0;
            _tRevPeakDecayMs = Median(revPeakList);
            _tFwdEmptyMs = fwdEmptyList.Any() ? Median(fwdEmptyList) : 0.0;
            _tRevEmptyMs = revEmptyList.Any() ? Median(revEmptyList) : 0.0;
            _iEmptyFwdA = iEmptyFList.Any() ? Median(iEmptyFList) : 0.0;
            _iEmptyRevA = iEmptyRList.Any() ? Median(iEmptyRList) : 0.0;

            _log.Info(
                $"EPB[{_channel}] 学习完成(中位数)：②={_tFwdPeakDecayMs}ms，④={_tClampRampMs}ms，⑥={_tRevPeakDecayMs}ms；" +
                $"③(actual)={_tFwdEmptyMs}ms，⑦(planned)={_tRevEmptyMs}ms；I±empty≈({_iEmptyFwdA:F2},{_iEmptyRevA:F2})A。",
                "EPB");

            return true;
        }


        /// <summary>
        ///     新的 RunOneAsync：与新的 LearnOneAlignedCoreAsync 统一流程/判据。
        ///     <list type="number">
        ///         <item>① 头部：若由“外壳相位”承担，则内部跳过（<see cref="UseNoHeadPhase" />）。</item>
        ///         <item>
        ///             ②~④ 正向：上电→忽略涌流→直接执行“夹紧阈值判定”（
        ///             <see
        ///                 cref="WaitCurrentAboveAsync(double, System.Threading.CancellationToken, int, double, double, double, int)" />
        ///             ），不再判空行程/带宽。
        ///         </item>
        ///         <item>⑤ 保持：按 <c>_holdMs</c> 执行（可为 0）。</item>
        ///         <item>
        ///             ⑥~⑦ 反向：上电→忽略涌流→刚性衰减等待（≤<see cref="RevDecayRigidMaxMs" /> 且 I≤<see cref="RevDecayLimitA" />）→固定空行程
        ///             <see cref="RevEmptyFixedMs" />，不做空行程带宽判据。
        ///         </item>
        ///         <item>⑧ 尾段：若启用“外壳收尾”（<see cref="EnableTailCompensation" />），内部仅计算剩余并不等待。</item>
        ///     </list>
        /// </summary>
        /// <param name="targetPeriodMs">目标周期（ms）。用于尾段预算/日志；①/⑧若交由外壳承担，内部不直接用。</param>
        /// <param name="token">取消令牌。</param>
        /// <param name="preRelease">预释放标记（保留签名兼容；当前逻辑不直接使用）。</param>
        /// <returns>本轮是否成功完成（true/false）。</returns>
        public async Task<bool> RunOneAsync(int targetPeriodMs, CancellationToken token, bool? preRelease = false)
        {
            if (_epbControlMode == EpbControlMode.AdaptiveCurrent)
            {
                LastCycleOutcome = await RunOneAdaptiveAsync(targetPeriodMs, token).ConfigureAwait(false);
                return LastCycleOutcome.IsSuccess;
            }

            LastCycleOutcome = EpbCycleOutcome.Canceled(EpbCurrentStage.Idle, "LegacyCycleRunning");

            try
            {
                // —— 本地计时工具（与 Learn… 保持一致）——
                // ★注意：C# 7.3 对本地静态函数的 captures 有限制，这里仅使用无 captures 的形式。
                static long NowTicks()
                {
                    return Stopwatch.GetTimestamp();
                }

                static int MsBetween(long t0, long t1)
                {
                    return (int)((t1 - t0) * 1000.0 / Stopwatch.Frequency);
                }

                // —— 预算 ——（用于日志与⑧尾段估算）
                var elecBudgetMs = Math.Max(0, targetPeriodMs);

                // —— 接入点：上电前的液压进入（与 Learn… 一致）——
                if (_manager != null)
                    await _manager.HydraulicEnterAsync(_channel, token).ConfigureAwait(false);

                // ===================== ① 头部未上电（可交给外壳相位） =====================
                // 旧版本中 ① 按比例分配；现在若 UseNoHeadPhase=true，则完全由外壳承担并在此跳过。
                // 这里仅记录“外壳承担”日志，不再实际等待。
                if (UseNoHeadPhase) _log?.Info($"EPB[{_channel}] ①头部未上电由外壳相位承担，内部跳过。", "EPB");

                // 若你仍希望在无外壳相位时保留一个固定头部等待，可在此加入：
                // int headMs = 0; if (headMs > 0) await Task.Delay(headMs, token);
                var tElecStart = NowTicks(); // 用于⑧尾段收口计算

                // ===================== ② + ③ + ④：正向（合并为直接夹紧判据） =====================
                BeginAdaptiveForwardMonitoring(targetPeriodMs);
                CommandForward();
                _log?.Info($"EPB[{_channel}] ②正向上电，忽略涌流 {_peakIgnoreMs}ms…", "EPB");
                await Task.Delay(_peakIgnoreMs, token).ConfigureAwait(false);

                // 控制台输出
                Console.WriteLine("正向峰值捕获开始前，，，");
                // ——（新增）正向“有效区间”开始：启动全数据峰值捕获 —— //
                if (_acq != null)
                {
                    _acq.BeginEpbCurrentPeak(_channel);
                    _log?.Error($"EPB[{_channel}] 正向峰值捕获（全数据）已开始。", "EPB");
                    Console.WriteLine("正向峰值捕获进行中...");
                }


                // —— 直接进入“夹紧阈值/平台/预测”判据 —— //
                var tFwdJudgeStart = NowTicks();
                var okClamp = await WaitCurrentAboveAsync(_posThrA, token).ConfigureAwait(false);
                var fwdJudgeElapsedMs = MsBetween(tFwdJudgeStart, NowTicks());

                if (!okClamp)
                {
                    _log?.Warn($"EPB[{_channel}] 正向未达到阈值/平台（Thr={_posThrA:F2}A），本轮终止。", "EPB");
                    CommandOffHighPriority();

                    try
                    {
                        AlarmRaised?.Invoke(_channel, $"ClampTimeout Thr={_posThrA:F2}A");
                    }
                    catch
                    {
                        // ignore
                    }

                    // ——（新增）断电后，先结束峰值捕获并以【警告】输出 —— //
                    if (_acq != null)
                    {
                        var peak = _acq.EndEpbCurrentPeak(_channel);
                        _log?.Warn(
                            $"EPB[{_channel}] 正向未达阈值/平台（Thr={_posThrA:F2}A）。本段峰值 Imax={peak.MaxAmp:F3}A @ {peak.MaxAt:HH:mm:ss.fff}，Samples={peak.SampleCount}。",
                            "EPB");
                    }

                    if (_manager != null) await _manager.HydraulicMarkReleaseAsync(_channel).ConfigureAwait(false);
                    DisarmAdaptiveMonitoring();
                    LastCycleOutcome = EpbCycleOutcome.HardFault(
                        EpbCurrentStage.LoadRise,
                        $"ClampTimeout Thr={_posThrA:F2}A");
                    return false;
                }


                // 达到夹紧判据 → 立即断电并标记释放（与 Learn… 一致）
                CommandOffHighPriority();
                CompleteAdaptiveForwardMonitoring(fwdJudgeElapsedMs);

                // —— 达到夹紧判据 → 断电前，安排异步封口（延时 1000ms），完成后回调日志 —— //
                if (_acq != null)
                {
                    // fire-and-forget：不 await，不阻塞当前 async；回调里写日志
                    var _ = _acq.EndEpbCurrentPeakAsync(
                        _channel,
                        1000, // 延时 1s：通常 ≥ 一批长度，保障后台管线 flush
                        cutoffAfterDelay: true,
                        token,
                        peak =>
                        {
                            // 回调在后台线程，如需触发 UI 请自行 Invoke

                            var cutoffTimingText = string.Empty;
                            try
                            {
                                if (_acq.TryGetLastDaqCallbackTimingForEpbChannel(
                                        _channel,
                                        out var cbIntervalMs,
                                        out var arrivalDelayMs,
                                        out var dev,
                                        out var batchN,
                                        out var fs))
                                {
                                    cutoffTimingText =
                                        $"，触发时回调间隔≈{cbIntervalMs:F2}ms 到达延迟(尾)≈{arrivalDelayMs:F2}ms ({dev} N={batchN} Fs={fs}Hz)";
                                }

                                if (_acq.TryGetLastDaqCallbackTimingForEpbChannelEx(
                                        _channel,
                                        out var cbIntervalMsEx,
                                        out var arrivalDelayToEndMsEx,
                                        out var arrivalDelayToStartMsEx,
                                        out var devEx,
                                        out var batchNEx,
                                        out var fsEx))
                                {
                                    cutoffTimingText =
                                        $"，触发时回调间隔≈{cbIntervalMsEx:F2}ms 到达延迟(尾)≈{arrivalDelayToEndMsEx:F2}ms 到达延迟(首)≈{arrivalDelayToStartMsEx:F2}ms ({devEx} N={batchNEx} Fs={fsEx}Hz)";
                                }
                            }
                            catch
                            {
                                // ignore
                            }

                            // —— 自适应调整 SafetyMargin ——
                            double oldMargin, newMargin;
                            int freezeBefore = 0, freezeAfter = 0;
                            var modeText = string.Empty;
                            lock (_marginLock)
                            {
                                oldMargin = _safetyMarginA;
                                newMargin = oldMargin;
                                try
                                {
                                    var err = peak.MaxAmp - _posThrA; // >0: Overshoot, <0: Undershoot
                                    var absErr = Math.Abs(err);

                                    if (_safetyMarginControlMode == SafetyMarginControlMode.Legacy20251010)
                                    {
                                        modeText = "Legacy20251010";

                                        // 重要：与 feature/epb-cycle-sync-20251010 保持一致，legacy 模式在“正式阶段”不做比例自调。
                                        // SafetyMargin 的闭环更新仅发生在学习阶段（FinalizeSafetyMarginLearning 一次性写回）。
                                        newMargin = oldMargin;

                                        // legacy 模式不使用 freezeDown
                                        _downAdjustFreezeCyclesLeft = 0;
                                        freezeBefore = 0;
                                        freezeAfter = 0;
                                    }
                                    else
                                    {
                                        modeText = "FreezeA20260101";
                                        freezeBefore = _downAdjustFreezeCyclesLeft;

                                        const double deadbandA = 0.05;
                                        const double kpUp = 0.80;
                                        const double kpDown = 0.40;
                                        const double minMarginA = 0.20;
                                        const double maxMarginA = 5.00;

                                        const int freezeDownCyclesSmall = 2;
                                        const int freezeDownCyclesBig = 3;
                                        const double freezeDownScale = 0.25; // 冻结期：下调力度缩放（0=完全冻结）

                                        double maxStepUp = absErr >= 1.0 ? 1.00 : 0.60;
                                        double maxStepDown = absErr >= 1.0 ? 0.40 : 0.30;

                                        if (err > deadbandA)
                                        {
                                            _downAdjustFreezeCyclesLeft = Math.Max(
                                                _downAdjustFreezeCyclesLeft,
                                                absErr >= 1.0 ? freezeDownCyclesBig : freezeDownCyclesSmall);

                                            var delta = kpUp * err;
                                            if (delta > maxStepUp) delta = maxStepUp;
                                            newMargin = oldMargin + delta;
                                        }
                                        else if (err < -deadbandA)
                                        {
                                            var scale = (_downAdjustFreezeCyclesLeft > 0) ? freezeDownScale : 1.0;
                                            var delta = (kpDown * scale) * err;
                                            if (delta < -maxStepDown) delta = -maxStepDown;
                                            newMargin = oldMargin + delta;
                                        }

                                        if (newMargin < minMarginA) newMargin = minMarginA;
                                        if (newMargin > maxMarginA) newMargin = maxMarginA;

                                        _safetyMarginA = newMargin;

                                        if (err <= deadbandA && _downAdjustFreezeCyclesLeft > 0)
                                            _downAdjustFreezeCyclesLeft--;

                                        freezeAfter = _downAdjustFreezeCyclesLeft;
                                    }
                                }
                                catch {}
                            }

                            var freezeSuffix = _safetyMarginControlMode == SafetyMarginControlMode.FreezeA20260101
                                ? $"(freezeDown:{freezeBefore}->{freezeAfter})"
                                : string.Empty;

                            // Legacy20251010：保持与 feature/epb-cycle-sync-20251010 完全一致的日志格式（不打印 old->new / mode）。
                            if (_safetyMarginControlMode == SafetyMarginControlMode.Legacy20251010)
                            {
                                _log?.Error(
                                    $"EPB[{_channel}]，阈值：{_posThrA}A,差值：{(_posThrA - peak.MaxAmp):F3}|{(peak.MaxAmp - _actualCutoffCurrent):F3}|{(peak.MaxAmp - (_posThrA - oldMargin)):F3}, 截断值：{_actualCutoffCurrent:F3}|[{oldMargin:F3}]A, 断电触发点：{_actualCutoffCurrent:F3}A{cutoffTimingText}, 正向段峰值：Imax={peak.MaxAmp:F3}A @ {peak.MaxAt:HH:mm:ss.fff}，Samples={peak.SampleCount}。",
                                    "EPB");
                            }
                            else
                            {
                                _log?.Error(
                                    $"EPB[{_channel}]，阈值：{_posThrA}A,差值：{(_posThrA - peak.MaxAmp):F3}|{(peak.MaxAmp - _actualCutoffCurrent):F3}|{(peak.MaxAmp - (_posThrA - oldMargin)):F3}, 截断值：{_actualCutoffCurrent:F3}|[{oldMargin:F3}->{newMargin:F3}]A{freezeSuffix}(mode:{modeText}), 断电触发点：{_actualCutoffCurrent:F3}A{cutoffTimingText}, 正向段峰值：Imax={peak.MaxAmp:F3}A @ {peak.MaxAt:HH:mm:ss.fff}，Samples={peak.SampleCount}。",
                                    "EPB");
                            }

                            // —— 报警判据：峰值超阈值增量 ——
                            try
                            {
                                if (_overshootAlarmDeltaA > 0)
                                {
                                    var overshoot = peak.MaxAmp - _posThrA;
                                    if (overshoot >= _overshootAlarmDeltaA)
                                    {
                                        AlarmRaised?.Invoke(
                                            _channel,
                                            $"OverCurrent Imax={peak.MaxAmp:F3}A Thr={_posThrA:F2}A Δ={overshoot:F3}A (LimitΔ={_overshootAlarmDeltaA:F3}A)");
                                    }
                                }
                            }
                            catch
                            {
                                // ignore
                            }
                        });
                }

                _log?.Info($"EPB[{_channel}] 达到夹紧阈值 {_posThrA:F2}A，已断电并标记释放。", "EPB");
                if (_manager != null) await _manager.HydraulicMarkReleaseAsync(_channel).ConfigureAwait(false);

                // ===================== ⑤ 保持 =====================
                if (_holdMs > 0)
                {
                    _log?.Info($"EPB[{_channel}] ⑤保持 {_holdMs}ms。", "EPB");
                    await Task.Delay(_holdMs, token).ConfigureAwait(false);
                }

                // ===================== ⑥ + ⑦：反向（刚性衰减 + 固定空行程） =====================
                BeginAdaptiveReverseMonitoring(targetPeriodMs);
                CommandReverse();
                _log?.Info($"EPB[{_channel}] ⑥反向上电，忽略涌流 {_peakIgnoreMs}ms…", "EPB");
                await Task.Delay(_peakIgnoreMs, token).ConfigureAwait(false);

                // —— 刚性衰减等待：≤ RevDecayRigidMaxMs && I <= RevDecayLimitA —— //
                var tRevDecayStart = NowTicks();
                var tRevPeakDecayMs = 0;
                var decayReached = false;

                // 以采样节拍对齐（与 Learn… 保持一致）
                var tickPerMs = Stopwatch.Frequency / 1000.0;
                var nextDue = Stopwatch.GetTimestamp();
                //double lastCurrentAbs = 0;
                double lastCurrent = 0;

                while (true)
                {
                    token.ThrowIfCancellationRequested();

                    var now = Stopwatch.GetTimestamp();
                    if (now < nextDue)
                    {
                        var ms = (int)Math.Max(0, (nextDue - now) / tickPerMs - 1);
                        if (ms > 0) await Task.Delay(ms, token).ConfigureAwait(false);
                        while ((now = Stopwatch.GetTimestamp()) < nextDue)
                        {
                            /* busy wait to align */
                        }
                    }

                    nextDue += (long)(Math.Max(1, _sampleMs) * tickPerMs);


                    var current = _readCurrent(_channel); // 需要取绝对值 😒
                    var elapsedMs = MsBetween(tRevDecayStart, NowTicks());

                    lastCurrent = current;
                    current = Math.Abs(current);


                    if (current <= RevDecayLimitA)
                    {
                        decayReached = true;
                        tRevPeakDecayMs = elapsedMs;
                        break;
                    }

                    if (elapsedMs >= RevDecayRigidMaxMs)
                    {
                        tRevPeakDecayMs = RevDecayRigidMaxMs;
                        break;
                    }
                }

                if (!decayReached)
                    _log?.Info(
                        $"EPB[{_channel}] 反向峰值衰减未达标：当前值={lastCurrent:F3},限值={RevDecayLimitA:F2}A，上限={RevDecayRigidMaxMs}ms，实测≈{tRevPeakDecayMs}ms（按上限计入）。",
                        "EPB");
                else
                    _log?.Info(
                        $"EPB[{_channel}] 反向峰值衰减达标：当前值={lastCurrent:F3},I≤{RevDecayLimitA:F2}A，TRevPeakDecay≈{tRevPeakDecayMs}ms。",
                        "EPB");

                // —— 反向固定空行程（不再做带宽判据） —— //
                var run7 = Math.Max(0, RevEmptyFixedMs);
                if (run7 > 0)
                {
                    _log?.Info($"EPB[{_channel}] ⑦反向固定空行程 {run7}ms。", "EPB");
                    await Task.Delay(run7, token).ConfigureAwait(false);
                }

                // 反向断电
                CommandOff();
                CompleteAdaptiveShadowCycle();

                // ===================== ⑧ 尾段收口（可交由外壳） =====================
                var elecElapsed = MsBetween(tElecStart, NowTicks());
                var tailRemain = Math.Max(0, elecBudgetMs - elecElapsed);

                if (EnableTailCompensation)
                {
                    if (tailRemain > 0)
                        _log?.Info($"EPB[{_channel}] ⑧尾段交由外壳统一扣回（内部测得剩余 {tailRemain}ms）。", "EPB");
                    // 内部不等待，由外壳负责收口
                }
                else
                {
                    if (tailRemain > 0)
                    {
                        _log?.Info($"EPB[{_channel}] ⑧尾段收口 {tailRemain}ms（内部执行）。", "EPB");
                        await Task.Delay(tailRemain, token).ConfigureAwait(false);
                    }
                }

                // —— 成功 —— //
                _log?.Info(
                    $"EPB[{_channel}] 单圈完成：FwdJudge≈{fwdJudgeElapsedMs}ms, RevPeakDecay≈{tRevPeakDecayMs}ms, TailRemain≈{tailRemain}ms。",
                    "EPB");


                LastCycleOutcome = new EpbCycleOutcome
                {
                    Kind = _adaptiveSoftWarningSeen
                        ? EpbCycleOutcomeKind.SuccessWithWarning
                        : EpbCycleOutcomeKind.Success,
                    Stage = EpbCurrentStage.Released,
                    Reason = _adaptiveSoftWarningSeen ? "LegacyCompletedWithAdaptiveShadowWarning" : "LegacyCompleted",
                    ForwardElapsedMs = fwdJudgeElapsedMs,
                    ReverseElapsedMs = tRevPeakDecayMs,
                    PeakCurrentA = _adaptiveForwardPeakA,
                    ControlPeakCurrentA = _adaptiveForwardControlPeakA
                };
                return true;
            }
            catch (OperationCanceledException)
            {
                _log?.Warn($"EPB[{_channel}] 本轮被取消。", "EPB");
                CommandOff();
                DisarmAdaptiveMonitoring();
                LastCycleOutcome = EpbCycleOutcome.Canceled(_adaptiveStateMachine?.Stage ?? EpbCurrentStage.Idle, "Canceled");
                return false;
            }
            catch (Exception ex)
            {
                _log?.Error($"EPB[{_channel}] 运行异常：{ex.Message}", "EPB", ex);
                CommandOff();
                DisarmAdaptiveMonitoring();
                LastCycleOutcome = EpbCycleOutcome.HardFault(
                    _adaptiveStateMachine?.Stage ?? EpbCurrentStage.Faulted,
                    "UnhandledException: " + ex.Message);
                return false;
            }
        }

        /// 从 startTick 到当前的经过时间，单位毫秒。
        /// </summary>
        /// <param name="startTick"></param>
        /// <returns></returns>
        private static long ElapsedMs(long startTick)
        {
            return (long)((Stopwatch.GetTimestamp() - startTick) * 1000.0 / Stopwatch.Frequency);
        }


        /// <summary>
        ///     将指定值限制在指定的范围内。
        /// </summary>
        /// <param name="v">要限制的值。</param>
        /// <param name="lo">范围的下限（包含）。</param>
        /// <param name="hi">范围的上限（包含）。</param>
        /// <returns>
        ///     如果 <paramref name="v" /> 小于 <paramref name="lo" />，返回 <paramref name="lo" />；
        ///     如果 <paramref name="v" /> 大于 <paramref name="hi" />，返回 <paramref name="hi" />；
        ///     否则返回 <paramref name="v" />。
        /// </returns>
        /// <remarks>
        ///     此方法确保返回值始终在 [<paramref name="lo" />, <paramref name="hi" />] 区间内。
        ///     如果 <paramref name="lo" /> 大于 <paramref name="hi" />，所有输入值都将被限制为 <paramref name="lo" />。
        ///     对于 NaN 值，将直接返回 NaN。
        /// </remarks>
        /// <example>
        ///     以下示例演示如何使用 Clamp 方法：
        ///     <code>
        /// double value = 150;
        /// double clamped = Clamp(value, 0, 100); // 返回 100
        /// 
        /// double negative = -5;
        /// clamped = Clamp(negative, 0, 100); // 返回 0
        /// 
        /// double normal = 50;
        /// clamped = Clamp(normal, 0, 100); // 返回 50
        /// </code>
        /// </example>
        private static double Clamp(double v, double lo, double hi)
        {
            return v < lo ? lo : v > hi ? hi : v;
        }

        private static double Median(IEnumerable<double> seq)
        {
            var arr = seq.OrderBy(x => x).ToArray();
            if (arr.Length == 0) return 0;
            var mid = arr.Length / 2;
            return arr.Length % 2 == 1 ? arr[mid] : 0.5 * (arr[mid - 1] + arr[mid]);
        }

        /// <summary>
        ///     计算指数加权移动平均（EWMA）值。
        /// </summary>
        /// <param name="prev">上一个EWMA值（历史平均值）。</param>
        /// <param name="cur">当前的新观测值。</param>
        /// <returns>更新后的EWMA值。</returns>
        /// <remarks>
        ///     此方法使用公式: EWMA = prev + α × (cur - prev)
        ///     其中 α 是平滑因子，控制新观测值对平均值的影响程度。
        ///     α 值越大，新观测值的权重越高，平均值对近期变化越敏感；
        ///     α 值越小，历史数据的权重越高，平均值越平滑。
        ///     典型的 α 值范围是 0.01 到 0.3，具体取决于应用场景。
        /// </remarks>
        /// <example>
        ///     以下示例演示如何使用 ReadEwma 方法：
        ///     <code>
        /// double previousEwma = 50.0;
        /// double currentValue = 55.0;
        /// double newEwma = ReadEwma(previousEwma, currentValue);
        /// Console.WriteLine($"新的EWMA值: {newEwma}");
        /// </code>
        /// </example>
        private double ReadEwma(double prev, double cur)
        {
            return prev + _ewmaAlpha * (cur - prev);
        }

        /// <summary>
        /// 使用与正式自适应反向释放相同的分位数窗口判定预释放空行程。
        /// 有稳定模型时使用通道历史基线；无模型时使用 RevDecayLimitA，
        /// 因而可在首次学习前工作，不依赖固定的 -0.5A 假设。
        /// </summary>
        private async Task<(bool ok, long tEnter, double iAvg, string reason)>
            WaitForReverseEmptyPlateauAsync(int maxWaitMs, CancellationToken token)
        {
            var waitMs = Math.Max(500, maxWaitMs);
            var detector = new EpbAdaptiveCurrentStateMachine(
                _adaptiveProfile?.Clone() ?? new EpbAdaptiveProfile { Channel = _channel });
            var startTick = Stopwatch.GetTimestamp();
            detector.ArmReverse(
                startTick,
                0,
                waitMs,
                Math.Max(0.1, RevDecayLimitA),
                0,
                _posThrA,
                _adaptiveSafetyLimits,
                RevDecayRigidMaxMs);

            while (true)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(_sampleMs, token).ConfigureAwait(false);

                var nowTick = Stopwatch.GetTimestamp();
                var decision = detector.OnSample(nowTick, _readCurrent(_channel));
                if (decision.ReleaseCompleted)
                {
                    var elapsedMs = (long)((nowTick - startTick) * 1000.0 / Stopwatch.Frequency);
                    return (
                        true,
                        elapsedMs,
                        -Math.Abs(detector.ObservedReverseEmptyA),
                        decision.Reason ?? "Released");
                }

                if (decision.HardFault)
                    return (false, 0, 0, decision.Reason ?? "DetectionHardFault");
            }
        }

        /// <summary>
        ///     - 用实际时间戳累积稳定窗口(避免 Task.Delay 抖动带来的累计误差)；
        ///     - 使用 sign 参数：+1 仅接受正向；-1 仅接受反向；0 不限定；
        ///     - tEnter 返回为“首次进入稳定带的时刻(相对本次调用起点)”。
        ///     - 可传入 maxWaitMs(默认 10_000)；
        /// </summary>
        private async Task<(bool ok, long tEnter, double iAvg)> WaitStableAroundAsync(
            double targetA,
            int sign, // +1=只接受正号；-1=只接受负号；0=不限制
            double bandA,
            int stableWinMs,
            CancellationToken token,
            int maxWaitMs = 10_000)
        {
            // 本地时间换算工具
            static long NowTicks()
            {
                return Stopwatch.GetTimestamp();
            }

            static int MsBetween(long t0, long t1)
            {
                return (int)((t1 - t0) * 1000.0 / Stopwatch.Frequency);
            }

            var wndRequiredMs = Math.Max(_sampleMs, stableWinMs);

            var tStart = NowTicks();
            var last = NowTicks();

            // 首次读数作为 ewma 起点(避免重复读取硬件)
            var first = _readCurrent(_channel);
            var ewma = first;

            var inBandMs = 0;
            double sum = 0;
            var n = 0;
            long? tEnterRelMs = null; // 记录“首次进入稳定带”的相对时间(毫秒)

            // 本地函数：根据 sign 判方向
            static bool DirectionOk(double value, int sign, double target)
            {
                if (sign == 0) return true; // 不限制方向
                var vSign = Math.Sign(value);
                // 若 target=0，按 sign 来要求方向；若 target!=0，既可按 sign 也可按与 target 同号
                return vSign == sign || (target != 0 && vSign == Math.Sign(target));
            }

            while (true)
            {
                token.ThrowIfCancellationRequested();

                await Task.Delay(_sampleMs, token);
                var now = NowTicks();
                var dtMs = MsBetween(last, now); // 实际经过的毫秒数
                last = now;

                var raw = _readCurrent(_channel);
                ewma = ReadEwma(ewma, raw);

                var diff = Math.Abs(ewma - targetA);
                var inBand = diff <= bandA && DirectionOk(ewma, sign, targetA);

                if (inBand)
                {
                    // 第一次进入稳定带：记录进入时刻
                    tEnterRelMs ??= MsBetween(tStart, now);

                    // 用“实际经过时间”来累计稳定窗口，避免节拍抖动累计误差
                    inBandMs += dtMs;
                    sum += ewma;
                    n++;

                    if (inBandMs >= wndRequiredMs)
                    {
                        // 返回进入稳定带的时刻(若未记录则用当前)
                        var tEnter = tEnterRelMs ?? MsBetween(tStart, now);
                        var avg = n > 0 ? sum / n : ewma;
                        return (true, tEnter, avg);
                    }
                }
                else
                {
                    // 离开稳定带：清空窗口累计与均值，进入时刻也清空
                    inBandMs = 0;
                    sum = 0;
                    n = 0;
                    tEnterRelMs = null;
                }

                // 超时保护(与实际时间挂钩)
                if (MsBetween(tStart, now) > maxWaitMs)
                    return (false, 0, 0);
            }
        }

        private async Task<bool> WaitCurrentAboveAsyncOld(
            double thrA,
            double safetyMarginA,
            CancellationToken token,
            int predictiveCutMs = 5,
            double minSlopeAperMs = 0.02,
            double maxSlopeAperMs = 1.0, // 斜率物理上限（A/ms）
            int slopeWinSize = 10) // 滑动窗口大小
        {
            var tBegin = Stopwatch.GetTimestamp();

            var winCap = Math.Max(1, PlateauWindowMs / Math.Max(1, _sampleMs));
            var ring = new double[winCap];
            int count = 0, head = 0;

            var tickPerMs = Stopwatch.Frequency / 1000;
            var nextDue = Stopwatch.GetTimestamp();

            _slopeWindow.Clear();

            _log.Info(
                $"WaitCurrentAboveAsync 启动: Thr={thrA:F2}A, PredictiveCut={predictiveCutMs}ms, " +
                $"MinSlope={minSlopeAperMs:F3}A/ms, MaxSlope={maxSlopeAperMs:F2}A/ms, " +
                $"Margin={safetyMarginA:F2}A, SampleMs={_sampleMs}ms",
                "EPB");

            while (true)
            {
                token.ThrowIfCancellationRequested();

                // 对齐采样节拍
                {
                    var now = Stopwatch.GetTimestamp();
                    if (now < nextDue)
                    {
                        var ms = (int)Math.Max(0, (nextDue - now) / tickPerMs - 1);
                        if (ms > 0) await Task.Delay(ms, token);
                        while ((now = Stopwatch.GetTimestamp()) < nextDue)
                        {
                        }
                    }
                }
                nextDue += _sampleMs * tickPerMs;

                // 读取瞬时电流
                var current = _readCurrent(_channel);
                var nowTick = Stopwatch.GetTimestamp();

                // 直接判定
                if (current + safetyMarginA >= thrA)
                {
                    _log.Warn($"EPB[{_channel}] 达到阈值: I={current:F2}A ≥ Thr={thrA:F2}A", "EPB");
                    return true;
                }

                // —— 更新斜率窗口 —— //
                _slopeWindow.Enqueue((current, nowTick));
                if (_slopeWindow.Count > slopeWinSize) _slopeWindow.Dequeue();

                // —— 足够数据时计算平均斜率 —— //
                if (predictiveCutMs > 0 && _slopeWindow.Count >= 2)
                {
                    var first = _slopeWindow.Peek();
                    var last = _slopeWindow.Last();

                    var dI = last.I - first.I;
                    var dtMs = (last.Tick - first.Tick) * 1000.0 / Stopwatch.Frequency;

                    if (dtMs > 0.5) // 至少 0.5ms 间隔
                    {
                        var slope = dI / dtMs;
                        slope = Math.Min(slope, maxSlopeAperMs); // 限幅

                        var remainA = thrA /* - safetyMarginA*/ - current;

                        // 只在接近阈值时才启用预测
                        if (slope >= minSlopeAperMs && remainA > 0 && remainA < 3) // 接近阈值3A内
                        {
                            var tToThrMs = remainA / slope;
                            if (tToThrMs <= predictiveCutMs && tToThrMs >= 0)
                            {
                                _log.Warn(
                                    $"EPB[{_channel}] 预测提前断电: I={current:F2}A, slope≈{slope:F3}A/ms, " +
                                    $"remain={remainA:F2}A, tToThr={tToThrMs:F1}ms ≤ Cut={predictiveCutMs}ms",
                                    "EPB");
                                return true;
                            }
                        }
                    }
                }

                // 平台检测（不变）
                ring[head] = current;
                head = (head + 1) % winCap;
                if (count < winCap) count++;
                if (count == winCap && _iEmptyFwdA != 0)
                {
                    double min = ring.Min(), max = ring.Max();
                    var range = max - min;

                    if (range <= PlateauFlatRangeA && current >= _iEmptyFwdA + PlateauAboveEmptyMarginA)
                    {
                        _log.Warn(
                            $"EPB[{_channel}] 检测到疑似限流平台: {PlateauWindowMs}ms 内波动≤{range:F2}A, I≈{current:F2}A",
                            "EPB");
                        return true;
                    }
                }

                if (ElapsedMs(tBegin) > 10_000)
                {
                    _log.Warn($"EPB[{_channel}] 超时: 10s 内未达到 Thr={thrA:F2}A", "EPB");
                    return false;
                }
            }
        }


        /// <summary>
        /// （异步，方案C：高速轮询）等待通道电流达到阈值 ——
        /// 仅依据安全裕量 <paramref name="safetyMarginA"/> 提前判定，
        /// 不进行斜率预测，且不再对齐采样节拍；
        /// 采用“轻量自旋 + 主动让出时间片”的高速轮询策略以降低触发延迟。
        /// </summary>
        /// <param name="thrA">
        /// 触发阈值电流（A）。当 <c>current + safetyMarginA ≥ thrA</c> 时立即返回 true。
        /// </param>
        /// <param name="safetyMarginA">
        /// 安全裕量（A）。用于“提前触发”判定：<c>current + safetyMarginA ≥ thrA</c>。
        /// </param>
        /// <param name="token">取消令牌，支持外部取消。</param>
        /// <param name="predictiveCutMs">
        /// 预测提前断电窗口（毫秒）。<b>方案C不使用</b>，仅为保持与旧签名一致，避免修改调用方。
        /// </param>
        /// <param name="minSlopeAperMs">
        /// 最小有效斜率（A/ms）。<b>方案C不使用</b>，兼容占位。
        /// </param>
        /// <param name="maxSlopeAperMs">
        /// 最大物理斜率上限（A/ms）。<b>方案C不使用</b>，兼容占位。
        /// </param>
        /// <param name="slopeWinSize">
        /// 斜率滑动窗口大小。<b>方案C不使用</b>，兼容占位。
        /// </param>
        /// <returns>
        /// 当在超时时间（固定 10s）内达到 <c>current + safetyMarginA ≥ thrA</c> 或检测到疑似限流平台时返回 <c>true</c>；
        /// 否则返回 <c>false</c>。
        /// </returns>
        /// <remarks>
        /// • 与“对齐采样节拍”的版本相比，本方法优先“反应速度”，触发时机不再受 `_sampleMs` 量化；
        /// • 使用轻量自旋（<see cref="System.Threading.SpinWait"/>）+ 周期性 <c>Thread.Sleep(0)</c> 让出时间片，
        ///   以减少 CPU 占用同时保持低延迟；
        /// • 平台检测（环形缓冲、空载电流阈值）逻辑与原方法保持一致；
        /// • 依赖字段/方法：<c>_readCurrent</c>、<c>_channel</c>、<c>PlateauWindowMs</c>、<c>_sampleMs</c>、
        ///   <c>PlateauFlatRangeA</c>、<c>_iEmptyFwdA</c>、<c>PlateauAboveEmptyMarginA</c>、<c>ElapsedMs(long)</c>、<c>_log</c>。
        /// </remarks>
        // private async Task<bool> WaitCurrentAboveByMarginOnlyAsync(
        private async Task<bool> WaitCurrentAboveAsync(
            double thrA,
            double safetyMarginA,
            CancellationToken token,
            int predictiveCutMs = 5,
            double minSlopeAperMs = 0.02,
            double maxSlopeAperMs = 1.0, // 斜率物理上限（A/ms）
            int slopeWinSize = 10
            ) // 滑动窗口大小
        {
            // —— 为保持签名一致，这些参数在方案C中不使用 —— //
            _ = predictiveCutMs;
            _ = minSlopeAperMs;
            _ = maxSlopeAperMs;
            _ = slopeWinSize;

            // 平台检测窗口（样本数）
            var winCap = Math.Max(1, PlateauWindowMs / Math.Max(1, _sampleMs));
            var startTick = Stopwatch.GetTimestamp();
            var waiter = new CurrentAboveWaiter(startTick, thrA, safetyMarginA, _iEmptyFwdA, winCap);

            // Arm（事件驱动）：由 FeedCurrentSample 在样本到达时完成该 TCS
            Volatile.Write(ref _currentAboveWaiter, waiter);

            _log.Info(
                $"WaitCurrentAboveAsync[C] 启动: Thr={thrA:F2}A, Margin={safetyMarginA:F2}A, Mode=EventDriven(FastSample)",
                "EPB");

            int maxWaitMs = _cfg?.Test.EpbCycleRunner.GetRunnerChannel(_channel).FwdOnLimitMs ?? 5_000;

            try
            {
                using var _ = token.Register(() => waiter.Tcs.TrySetCanceled());

                // 注意：这里不主动轮询 _readCurrent；判据完全由采集样本驱动。
                var done = await Task.WhenAny(
                        waiter.Tcs.Task,
                        Task.Delay(maxWaitMs, token))
                    .ConfigureAwait(false);

                token.ThrowIfCancellationRequested();

                if (done != waiter.Tcs.Task)
                {
                    _log.Warn($"EPB[{_channel}] 超时(方案C/事件驱动): {maxWaitMs / 1000}s 内未达到 Thr={thrA:F2}A", "EPB");
                    return false;
                }

                var result = await waiter.Tcs.Task.ConfigureAwait(false);

                _actualCutoffCurrent = result.iNow;

                if (!result.isPlatform)
                {
                    _log.Warn(
                        $"EPB[{_channel}] 达到阈值(方案C/事件驱动): I={result.iNow:F2}A + Margin={safetyMarginA:F2}A ≥ Thr={thrA:F2}A",
                        "EPB");
                }
                else
                {
                    _log.Warn(
                        $"EPB[{_channel}] 疑似限流平台(方案C/事件驱动): {PlateauWindowMs}ms 内波动≤{PlateauFlatRangeA:F2}A, I≈{result.iNow:F2}A",
                        "EPB");
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            finally
            {
                // 只清理自己 Arm 的 waiter，避免并发覆盖
                if (ReferenceEquals(Volatile.Read(ref _currentAboveWaiter), waiter))
                    Volatile.Write(ref _currentAboveWaiter, null);
            }
        }


        private async Task<bool> WaitCurrentAboveAsync(
            double thrA,
            CancellationToken token,
            int predictiveCutMs = 5,
            double minSlopeAperMs = 0.02,
            double maxSlopeAperMs = 1.0,
            int slopeWinSize = 10)
        {
            double margin;
            lock (_marginLock)
            {
                margin = _safetyMarginA;
            }

            // 直接调用原方法，使用字段 _safetyMarginA 作为参数
            return await WaitCurrentAboveAsync(
                thrA,
                margin, // 使用字段值
                token,
                predictiveCutMs,
                minSlopeAperMs,
                maxSlopeAperMs,
                slopeWinSize);
        }

      

        /// <summary>
        ///     以与 <see cref="WaitCurrentAboveAsync" /> 相同的“对齐采样节拍”方式，
        ///     等待电流第一次**上穿**给定的离开阈值（例如 Iempty+_ + band）。
        /// </summary>
        /// <param name="leaveThrA">离开空行程的上边界阈值（安培）。</param>
        /// <param name="token">取消令牌。</param>
        /// <param name="maxWaitMs">最大等待时长，毫秒，默认 2000ms。</param>
        /// <returns>
        ///     成功时返回“首次上穿阈值的 Stopwatch Tick”；超时返回 <c>null</c>。
        /// </returns>
        private async Task<long?> WaitLeaveBandUpAsync(double leaveThrA, CancellationToken token, int maxWaitMs = 2000)
        {
            var tickPerMs = Stopwatch.Frequency / 1000;
            var nextDue = Stopwatch.GetTimestamp();
            var tBegin = Stopwatch.GetTimestamp();

            while (true)
            {
                token.ThrowIfCancellationRequested();

                // —— 与 WaitCurrentAboveAsync 一致的对齐节拍 —— //
                // （避免 Task.Delay 抖动，让采样/判断更稳定）
                var now = Stopwatch.GetTimestamp();
                if (now < nextDue)
                {
                    var ms = (int)Math.Max(0, (nextDue - now) / tickPerMs - 1);
                    if (ms > 0) await Task.Delay(ms, token);
                    while ((now = Stopwatch.GetTimestamp()) < nextDue)
                    {
                        /* 自旋对齐 */
                    }
                }

                nextDue += _sampleMs * tickPerMs; // 与 _sampleMs 对齐的节拍
                var current = _readCurrent(_channel);

                if (current >= leaveThrA)
                    return Stopwatch.GetTimestamp();

                // 超时保护
                var elapsedMs = (int)((Stopwatch.GetTimestamp() - tBegin) * 1000.0 / Stopwatch.Frequency);
                if (elapsedMs > maxWaitMs) return null;
            }
        }


        /// <summary>
        ///     协作式等待“达到夹紧判据”（阈值 或 平台）；
        ///     - 仅用 Task.Delay 对齐采样节拍，绝不自旋；
        ///     - 返回是否达成、达成时刻 Tick 以及原因字符串("threshold"/"platform"/"timeout")。
        /// </summary>
        private async Task<(bool ok, long tick, string cause)> WaitClampCoopAsync(
            double iEmptyFwdA, CancellationToken token)
        {
            // 平台窗口长度（样本数）
            var winCap = Math.Max(1, PlateauWindowMs / Math.Max(1, _sampleMs));
            var win = new double[winCap];
            int rc = 0, rh = 0;

            var tStart = Stopwatch.GetTimestamp();
            var cause = "timeout";

            while (true)
            {
                token.ThrowIfCancellationRequested();

                // 协作式节拍等待（不回 UI）
                await Task.Delay(_sampleMs, token).ConfigureAwait(false);

                // 读当前（如需去抖可自行做 EWMA，这里直接用工程值）
                var v = _readCurrent(_channel);

                // ① 阈值判据
                if (v >= _posThrA)
                {
                    cause = "threshold";
                    return (true, Stopwatch.GetTimestamp(), cause);
                }

                // ② 平台判据（窗口内平坦，且高于 Iempty+ + margin）
                win[rh] = v;
                rh = (rh + 1) % winCap;
                if (rc < winCap) rc++;
                if (rc == winCap && iEmptyFwdA != 0)
                {
                    double min = win[0], max = win[0];
                    for (var i = 1; i < winCap; i++)
                    {
                        var x = win[i];
                        if (x < min) min = x;
                        if (x > max) max = x;
                    }

                    var range = max - min;
                    if (range <= PlateauFlatRangeA && v >= iEmptyFwdA + PlateauAboveEmptyMarginA)
                    {
                        cause = "platform";
                        return (true, Stopwatch.GetTimestamp(), cause);
                    }
                }

                // ③ 超时保护（给一个宽松上限，避免异常工况卡住）
                if ((int)((Stopwatch.GetTimestamp() - tStart) * 1000.0 / Stopwatch.Frequency) > 10_000)
                    return (false, Stopwatch.GetTimestamp(), cause);
            }
        }

        /// <summary>
        ///     单个电流采样点（Stopwatch Ticks + 电流 A）。
        /// </summary>
        private readonly struct CurrentSample
        {
            public readonly long Tick;
            public readonly double I;

            public CurrentSample(long tick, double i)
            {
                Tick = tick;
                I = i;
            }
        }


        /// <summary>
        ///     线程安全环形缓冲，存放电流采样点；支持按时间区间搜索“第一次上穿阈值”的时刻。
        /// </summary>
        private sealed class CurrentRingBuffer
        {
            private readonly CurrentSample[] _buf;
            private readonly object _lock = new();
            private int _count; // 实际有效元素数（<= _buf.Length）
            private int _head; // 指向“下一个写入位置”

            public CurrentRingBuffer(int capacity = 131072)
            {
                if (capacity < 1024) capacity = 1024;
                _buf = new CurrentSample[capacity];
            }

            /// <summary>加入一个样本（线程安全）。</summary>
            public void Add(CurrentSample s)
            {
                lock (_lock)
                {
                    _buf[_head] = s;
                    _head = (_head + 1) % _buf.Length;
                    if (_count < _buf.Length) _count++;
                }
            }

            /// <summary>
            ///     在给定“起始 Tick 之后”的样本中，返回“首次上穿阈值 thrA 的样本 Tick”。找不到返回 null。
            /// </summary>
            public long? FindFirstUpCrossing(double thrA, long startTick)
            {
                lock (_lock)
                {
                    if (_count == 0) return null;

                    // 从最老元素开始线性扫描（容量很大但单圈样本量有限，且只在 Learn 的统计阶段调用，不影响实时）
                    var idx = (_head - _count + _buf.Length) % _buf.Length;
                    var left = _count;

                    // 跳过 startTick 之前的数据
                    while (left > 0 && _buf[idx].Tick < startTick)
                    {
                        idx = (idx + 1) % _buf.Length;
                        left--;
                    }

                    // 找首次上穿
                    while (left > 0)
                    {
                        var s = _buf[idx];
                        if (s.I >= thrA) return s.Tick;
                        idx = (idx + 1) % _buf.Length;
                        left--;
                    }

                    return null;
                }
            }

            /// <summary>
            /// 返回时间窗口内电流幅值的中位数；没有有效样本时返回 NaN。
            /// </summary>
            public double MedianAbsolute(long startTick, long endTick)
            {
                lock (_lock)
                {
                    if (_count == 0 || endTick < startTick) return double.NaN;

                    var values = new List<double>();
                    // 从最新样本反向扫描，越过窗口起点立即结束，避免每圈遍历整个大环形缓冲。
                    var idx = (_head - 1 + _buf.Length) % _buf.Length;
                    for (var left = _count; left > 0; left--)
                    {
                        var sample = _buf[idx];
                        if (sample.Tick < startTick) break;
                        if (sample.Tick >= startTick &&
                            sample.Tick <= endTick &&
                            !double.IsNaN(sample.I) &&
                            !double.IsInfinity(sample.I))
                            values.Add(Math.Abs(sample.I));
                        idx = (idx - 1 + _buf.Length) % _buf.Length;
                    }

                    if (values.Count == 0) return double.NaN;
                    values.Sort();
                    var middle = values.Count / 2;
                    return values.Count % 2 == 0
                        ? (values[middle - 1] + values[middle]) / 2.0
                        : values[middle];
                }
            }
        }
    }
}
