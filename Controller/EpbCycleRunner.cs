using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IO.NI;
using ILogger = Config.IAppLogger;
using NLogger = Config.NullLogger;

namespace Controller
{
    public sealed partial class EpbCycleRunner
    {
        public delegate double ReadCurrentDelegate(int epbChannel);

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

        private readonly double _posThrA;
        private readonly ReadCurrentDelegate _readCurrent;

        // ⑦ 反向空行程“默认保持”时长（用于“首圈已夹紧/无空行程”时的释放），单位 ms
        private readonly int _revEmptyKeepMs = 1500; // 可按需要改成 200~500ms
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
            double ewmaAlpha = 0.2,
            double emptyBandA = 0.2,
            int stableWinMs = 50,
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
            _ewmaAlpha = Clamp(ewmaAlpha, 0.01, 0.9);
            _emptyBandA = Math.Max(0.02, emptyBandA);
            _stableWinMs = Math.Max(10, stableWinMs);
            _log = log ?? NLogger.Instance;
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
            double ewmaAlpha = 0.2,
            double emptyBandA = 0.2,
            int stableWinMs = 50,
            ILogger log = null,
            EpbManager manager = null) // ★ 新增（可选，保持兼容）
            : this(channel, hydId, readCurrent, doController, hydraulic, posThresholdA, holdMs, sampleMs, peakIgnoreMs,
                ewmaAlpha, emptyBandA, stableWinMs, log)
        {
            // …你原有的赋值保持不变…
            _manager = manager; // ★ 保存 manager
        }


        /// <summary>
        ///     由采集线程调用：喂入一个“低时延电流样本”（Stopwatch Tick 与电流）。
        /// </summary>
        public void FeedCurrentSample(int epbChannel, long tick, double currentAmp)
        {
            if (epbChannel < 1 || epbChannel >= _currentBus.Length) return;
            _currentBus[epbChannel].Add(new CurrentSample(tick, currentAmp));
        }

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
            const double R_TAIL = 0.15;

            _log.Info(
                $"EPB[{_channel}] 学习开始，次数={nCycles}；采样={_sampleMs}ms，忽略涌流={_peakIgnoreMs}ms，" +
                $"emptyBand={_emptyBandA:F2}A，stableWin={_stableWinMs}ms，posThr={_posThrA:F2}A，" +
                $"plateau(win={PlateauWindowMs}ms, flat≤{PlateauFlatRangeA:F2}A, +emptyMargin≥{PlateauAboveEmptyMarginA:F2}A)。",
                "EPB");

            // 预释放
            if (_revEmptyKeepMs > 0)
                try
                {
                    _log.Info($"EPB[{_channel}] 自学习预处理：先反向释放，进入反向空行程后保持 {_revEmptyKeepMs}ms。", "EPB");
                    _do.SetEpbReverse(_channel);
                    await Task.Delay(_peakIgnoreMs, token).ConfigureAwait(false);

                    var (okRel, _, iEmptyRel) =
                        await WaitStableAroundAsync(-0.5, -1, _emptyBandA, _stableWinMs, token).ConfigureAwait(false);
                    if (okRel)
                    {
                        _log.Info($"EPB[{_channel}] 预释放：已进入反向空行程，Iempty-≈{iEmptyRel:F2}A。保持 {_revEmptyKeepMs}ms。",
                            "EPB");
                        if (_iEmptyRevA == 0) _iEmptyRevA = iEmptyRel;
                    }
                    else
                    {
                        _log.Warn($"EPB[{_channel}] 预释放：未稳定判定到反向空行程，仍按 {_revEmptyKeepMs}ms 定时保持。", "EPB");
                    }

                    await Task.Delay(_revEmptyKeepMs, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.Warn($"EPB[{_channel}] 预释放阶段异常：{ex.Message}（忽略继续）。", "EPB");
                }
                finally
                {
                    _do.SetEpbOff(_channel);
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
                _do.SetEpbForward(_channel);
                _log.Info($"EPB[{_channel}] 学习{k + 1}：②正向上电，忽略涌流 {_peakIgnoreMs}ms…", "EPB");
                await Task.Delay(_peakIgnoreMs, token).ConfigureAwait(false);

                var (okEmptyFwd, tEnterEmptyFwd_ms, iEmptyFwd) =
                    await WaitStableAroundAsync(+0.5, +1, _emptyBandA, _stableWinMs, token).ConfigureAwait(false);
                if (!okEmptyFwd)
                {
                    _do.SetEpbOff(_channel);
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
                    _do.SetEpbOff(_channel);
                    _log.Warn($"EPB[{_channel}] 学习{k + 1}：④未达到阈值/平台（阈 {_posThrA:F2}A），放弃本轮。", "EPB");
                    continue;
                }

                // 达到判据 → 立即断电
                _do.SetEpbOff(_channel);
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
                _do.SetEpbReverse(_channel);
                _log.Info($"EPB[{_channel}] 学习{k + 1}：⑥反向上电，忽略涌流 {_peakIgnoreMs}ms…", "EPB");
                await Task.Delay(_peakIgnoreMs, token).ConfigureAwait(false);

                var (okEmptyRev, tEnterEmptyRev_ms, iEmptyRev) =
                    await WaitStableAroundAsync(-0.5, -1, _emptyBandA, _stableWinMs, token).ConfigureAwait(false);
                if (!okEmptyRev)
                {
                    _do.SetEpbOff(_channel);
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

                _do.SetEpbOff(_channel);

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


        public async Task<bool> RunOneAsync(int targetPeriodMs, CancellationToken token, bool? preRelease = false)
        {
            try
            {
                // —— 本地工具：计时 & 比例系数（与 LearnAsync 保持一致）——
                static long NowTicks()
                {
                    return Stopwatch.GetTimestamp();
                }

                static int MsBetween(long t0, long t1)
                {
                    return (int)((t1 - t0) * 1000.0 / Stopwatch.Frequency);
                }

                // 柔性分配的比例（①③⑦⑧）；⑤为 _holdMs
                const double R_HEAD = 0.15; // ① 头部未上电（腾出组内错峰/等待液压稳定等）
                const double R_FWD_EMPTY = 0.35; // ③ 正向空行程（计划值，仅用于分配，不改变判稳逻辑）
                const double R_REV_EMPTY = 0.35; // ⑦ 反向空行程（计划值，定时控制）
                const double R_TAIL = 0.15; // ⑧ 尾段收口（把误差吸收，使电控段≈预算）

                // —— 关键变化：液压建/释压改由“协调器”统一控制。
                // 这里的电控预算直接按整个周期 targetPeriodMs 来分配；
                // 若需要与 Learn 的“刚性时间”联动，可继续使用下方的刚性/柔性估算。
                var elecBudgetMs = Math.Max(0, targetPeriodMs);

                // 用“已学习的估计值”计算刚性时间与柔性时间（便于给①③⑦⑧分配计划）
                // 刚性：②FwdPeakDecay + ④ClampRamp + ⑥RevPeakDecay（⑤hold 单独占用）
                var rigidEst = (int)Math.Round(
                    Math.Max(0, _tFwdPeakDecayMs) +
                    Math.Max(0, _tClampRampMs) +
                    Math.Max(0, _tRevPeakDecayMs));

                var flexEst = Math.Max(0, elecBudgetMs - rigidEst - Math.Max(0, _holdMs));
                var plan1 = (int)Math.Floor(flexEst * R_HEAD);
                var plan3 = (int)Math.Floor(flexEst * R_FWD_EMPTY);
                var plan7 = (int)Math.Floor(flexEst * R_REV_EMPTY);
                var plan8 = Math.Max(0, flexEst - plan1 - plan3 - plan7); // 剩余给尾段收口


                // —— 接入点 #1：上电之前 —— //
                await _manager?.HydraulicEnterAsync(_channel, token);


                // ===================== ① 头部未上电 =====================
                // （真正上电前调用“液压协调器：进入电控阶段”）
                if (plan1 > 0)
                {
                    _log.Info($"EPB[{_channel}] ①头部未上电 {plan1}ms（按比例 {R_HEAD:P0} 计划）。", "EPB");
                    await Task.Delay(plan1, token);
                }


                // 延时4s后开始上电
                //await Task.Delay(4000 - plan1, token); // 延时上电注释掉 2025.09.19

                var tElecStart = NowTicks(); // 用于⑧尾段收口

                // ===================== ② + ③ + ④：正向 =====================
                // 上电（正向）
                _do.SetEpbForward(_channel);
                _log.Info($"EPB[{_channel}] ②正向上电，忽略涌流 {_peakIgnoreMs}ms…", "EPB");
                await Task.Delay(_peakIgnoreMs, token); // 忽略上电涌流（去抖）

                // ②：进入正向“空行程电流”小带宽（Ewma 判稳）
                var (okEmptyFwd, tEnterEmptyFwd_ms, iEmptyFwd) =
                    await WaitStableAroundAsync(
                        _iEmptyFwdA != 0 ? _iEmptyFwdA : +0.5, // 目标均值；若学习未得出，用 +0.5A 兜底
                        +1, // 正向
                        _emptyBandA, // 带宽
                        _stableWinMs, // 稳定窗口
                        token);

                if (!okEmptyFwd)
                {
                    _log.Warn($"EPB[{_channel}] 正向未判定到空行程（②失败），本轮终止。", "EPB");
                    _do.SetEpbOff(_channel);
                    return false;
                }

                Console.WriteLine($"EPB[{_channel}], 空行程阶段，等待夹紧");
                // ③+④：从空行程向“夹紧升坡/限流平台”爬升，直到达到阈值/平台
                // WaitCurrentAboveAsync 内部具备平台判据（见你现有实现）
                var okClamp = await WaitCurrentAboveAsync(_posThrA, token);
                if (!okClamp)
                {
                    _log.Warn($"EPB[{_channel}] 正向未达到阈值/平台（阈 {_posThrA}A），本轮终止。", "EPB");
                    _do.SetEpbOff(_channel);

                    // —— 接入点 #2：到达本卡钳“电压释放点”的瞬间 ——
                    // 若该液压组内这是最后一个未释放成员，协调器会统一释压
                    await _manager?.HydraulicMarkReleaseAsync(_channel);
                    return false;
                }

                // --- 关键：达到阈值后**立刻断电**（避免超调） ----------
                _do.SetEpbOff(_channel);
                _log.Info($"EPB[{_channel}] 已达到夹紧阈值 {_posThrA}A，已断电并标记释放。", "EPB");

                // 如果有“液压组统一释压”逻辑，告诉协调器本卡钳已到达释放点
                // （先断电再标记，确保断电动作不会被等待标记的异步延时影响）
                await _manager?.HydraulicMarkReleaseAsync(_channel);


                // ===================== ⑤ 保持 =====================
                if (_holdMs > 0)
                {
                    _log.Info($"EPB[{_channel}] ⑤保持 {_holdMs}ms。", "EPB");
                    await Task.Delay(_holdMs, token);
                }

                // ===================== ⑥ + ⑦：反向 =====================
                // 反向上电
                _do.SetEpbReverse(_channel);
                _log.Info($"EPB[{_channel}] ⑥反向上电，忽略涌流 {_peakIgnoreMs}ms…", "EPB");
                await Task.Delay(_peakIgnoreMs, token);

                // ⑥：进入反向空行程小带
                var (okEmptyRev, _, iEmptyRev) =
                    await WaitStableAroundAsync(
                        _iEmptyRevA != 0 ? _iEmptyRevA : -0.5, // 目标均值；若学习未得出，用 -0.5A 兜底
                        -1, // 反向
                        _emptyBandA,
                        _stableWinMs,
                        token);

                if (!okEmptyRev)
                {
                    _log.Warn($"EPB[{_channel}] 反向未判定到空行程（⑥失败），本轮终止。", "EPB");
                    _do.SetEpbOff(_channel);
                    return false;
                }

                // ⑦：反向按“计划时间”定时（不观测退出）
                var run7 = Math.Max(10, plan7);
                _log.Info($"EPB[{_channel}] ⑦反向空行程定时 {run7}ms。", "EPB");
                await Task.Delay(run7, token);

                // 反向断电
                _do.SetEpbOff(_channel);

                // ===================== ⑧ 尾段收口：把误差吸收 =====================
                // 目标：电控段总时长 ≈ elecBudgetMs
                var elecElapsed = MsBetween(tElecStart, NowTicks());
                var tailRemain = Math.Max(0, elecBudgetMs - elecElapsed);
                if (tailRemain > 0)
                {
                    _log.Info($"EPB[{_channel}] ⑧尾段收口 {tailRemain}ms（计划 {plan8}ms，实际剩余 {tailRemain}ms）。", "EPB");
                    await Task.Delay(tailRemain, token);
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                _log.Warn($"EPB[{_channel}] 本轮被取消。", "EPB");
                _do.SetEpbOff(_channel);
                return false;
            }
            catch (Exception ex)
            {
                _log.Error($"EPB[{_channel}] 运行异常：{ex.Message}", "EPB", ex);
                _do.SetEpbOff(_channel);
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

        private async Task<bool> WaitCurrentAboveAsyncOld(double thrA, CancellationToken token)
        {
            var ewma = ReadEwma(_readCurrent(_channel), _readCurrent(_channel));
            var tBegin = Stopwatch.GetTimestamp();

            // —— 平台检测窗口（环形缓冲）——
            var winCap = Math.Max(1, PlateauWindowMs / Math.Max(1, _sampleMs));
            var ring = new double[winCap];
            int count = 0, head = 0;

            while (true)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(_sampleMs, token);

                var raw = _readCurrent(_channel);
                ewma = ReadEwma(ewma, raw);

                // 1) 达阈值：立即返回（上层会立刻断电）
                if (ewma >= thrA) return true;

                // 2) 限流平台：150ms 内几乎一条直线，且显著高于空行程
                ring[head] = ewma;
                head = (head + 1) % winCap;
                if (count < winCap) count++;
                if (count == winCap && _iEmptyFwdA != 0)
                {
                    double min = ring[0], max = ring[0];
                    for (var i = 1; i < winCap; i++)
                    {
                        var v = ring[i];
                        if (v < min) min = v;
                        if (v > max) max = v;
                    }

                    var range = max - min;
                    if (range <= PlateauFlatRangeA && ewma >= _iEmptyFwdA + PlateauAboveEmptyMarginA)
                    {
                        _log.Warn($"EPB[{_channel}] 检测到疑似电源限流平台，{PlateauWindowMs}ms 内波动≤{range:F2}A，" +
                                  $"EWMA≈{ewma:F2}A（空行程≈{_iEmptyFwdA:F2}A）。提前断电。", "EPB");
                        return true; // 视同到达“应断电”条件
                    }
                }

                // 3) 超时保护
                if (ElapsedMs(tBegin) > 10_000) return false;
            }
        }

        /// <summary>
        ///     等待 EPB 通道电流达到或超过指定阈值（A）。
        ///     <para>改动要点：直接使用瞬时电流值参与判断（不做平滑），并将采样读取放到循环起始处以减少首判延迟。</para>
        /// </summary>
        /// <param name="thrA">电流阈值（安培）。达到或超过则立即返回 <c>true</c>。</param>
        /// <param name="token">取消令牌。</param>
        /// <returns>
        ///     若在超时时间（固定 10 s）内达到阈值或识别到“限流平台”，返回 <c>true</c>；
        ///     超时未达到则返回 <c>false</c>。
        /// </returns>
        /// <remarks>
        ///     其他逻辑保持与原方法一致：
        ///     1) 达阈值立即返回；
        ///     2) “限流平台”检测：在 <see cref="PlateauWindowMs" /> 毫秒窗口内波动近似恒定，且显著高于空行程电流 <c>_iEmptyFwdA</c>；
        ///     3) 超时保护：超过 10 s 返回 <c>false</c>；
        ///     4) 采样节拍仍基于 <c>_sampleMs</c> 的 <see cref="Task.Delay(int, CancellationToken)" />。
        /// </remarks>
        private async Task<bool> WaitCurrentAboveAsyncOld2(double thrA, CancellationToken token)
        {
            // 记录起始时间用于超时判断（需配合现有的 ElapsedMs(tBegin) 辅助函数）
            var tBegin = Stopwatch.GetTimestamp();

            // —— 平台检测窗口（环形缓冲，单位：样本数）——
            // 与原逻辑一致：窗口大小 = PlateauWindowMs / _sampleMs（至少为 1）
            var winCap = Math.Max(1, PlateauWindowMs / Math.Max(1, _sampleMs));
            var ring = new double[winCap];
            int count = 0, head = 0;

            while (true)
            {
                token.ThrowIfCancellationRequested();

                // ① 先读取当前瞬时电流（避免先 Delay 带来的首判滞后）
                var current = _readCurrent(_channel);

                // ② 达阈值：立即返回（上层会立刻断电）
                if (current >= thrA) return true;

                // ③ 限流平台检测（逻辑保持不变，只是用 raw 电流而非 EWMA）
                ring[head] = current;
                head = (head + 1) % winCap;
                if (count < winCap) count++;

                if (count == winCap && _iEmptyFwdA != 0)
                {
                    // 在窗口内求极差
                    double min = ring[0], max = ring[0];
                    for (var i = 1; i < winCap; i++)
                    {
                        var v = ring[i];
                        if (v < min) min = v;
                        if (v > max) max = v;
                    }

                    var range = max - min;

                    // 平台判据：波动很小，且电流显著高于空行程
                    if (range <= PlateauFlatRangeA && current >= _iEmptyFwdA + PlateauAboveEmptyMarginA)
                    {
                        _log.Warn(
                            $"EPB[{_channel}] 检测到疑似电源限流平台，{PlateauWindowMs}ms 内波动≤{range:F2}A，" +
                            $"I≈{current:F2}A（空行程≈{_iEmptyFwdA:F2}A）。提前断电。",
                            "EPB");
                        return true; // 视同到达“应断电”条件
                    }
                }

                // ④ 超时保护（与原逻辑一致）
                if (ElapsedMs(tBegin) > 10_000) return false;

                // ⑤ 节拍等待（放在读取之后，保证首判即时）
                await Task.Delay(_sampleMs, token);
            }
        }

        /// <summary>
        ///     等待 EPB 通道电流达到或超过指定阈值（A），并使用“斜率预测 + 提前量”实现更精准的断电控制。
        ///     带详细日志输出以便调试。
        /// </summary>
        private async Task<bool> WaitCurrentAboveAsyncOld3(
            double thrA,
            CancellationToken token,
            int predictiveCutMs = 2,
            double minSlopeAperMs = 0.02,
            double safetyMarginA = 0.0)
        {
            var tBegin = Stopwatch.GetTimestamp();

            // —— 平台检测窗口 —— //
            var winCap = Math.Max(1, PlateauWindowMs / Math.Max(1, _sampleMs));
            var ring = new double[winCap];
            int count = 0, head = 0;

            // —— 斜率计算需要上一帧 —— //
            var hasLast = false;
            var lastI = 0.0;
            long lastTick = 0;

            // —— Tick 相关 —— //
            var tickPerMs = Stopwatch.Frequency / 1000;
            var nextDue = Stopwatch.GetTimestamp();

            // —— 打印一次方法启动参数 —— //
            _log.Info(
                $"WaitCurrentAboveAsync 启动: Thr={thrA:F2}A, PredictiveCut={predictiveCutMs}ms, " +
                $"MinSlope={minSlopeAperMs:F3}A/ms, Margin={safetyMarginA:F2}A, SampleMs={_sampleMs}ms",
                "EPB");

            var loopCounter = 0; // 控制调试日志频率

            while (true)
            {
                token.ThrowIfCancellationRequested();

                // —— ① 对齐节拍 —— //
                {
                    var now = Stopwatch.GetTimestamp();
                    if (now < nextDue)
                    {
                        var ms = (int)Math.Max(0, (nextDue - now) / tickPerMs - 1);
                        if (ms > 0) await Task.Delay(ms, token);
                        while ((now = Stopwatch.GetTimestamp()) < nextDue)
                        {
                            /* 自旋 */
                        }
                    }
                }
                nextDue += _sampleMs * tickPerMs;

                // —— ② 读取瞬时电流 —— //
                var current = _readCurrent(_channel);
                var nowTick = Stopwatch.GetTimestamp();

                // —— ③ 达阈值 —— //
                if (current + safetyMarginA >= thrA)
                {
                    _log.Info($"EPB[{_channel}] 达到阈值: I={current:F2}A ≥ Thr={thrA:F2}A (Margin={safetyMarginA:F2}A)",
                        "EPB");
                    return true;
                }

                // —— ④ 斜率预测 —— //
                if (predictiveCutMs > 0 && hasLast)
                {
                    var dtMs = (nowTick - lastTick) * 1000.0 / Stopwatch.Frequency;
                    if (dtMs > 0.05)
                    {
                        var slope = (current - lastI) / dtMs;
                        if (slope >= minSlopeAperMs)
                        {
                            var remainA = thrA - safetyMarginA - current;
                            var tToThrMs = remainA / slope;

                            if (tToThrMs <= predictiveCutMs && tToThrMs >= 0)
                            {
                                _log.Warn(
                                    $"EPB[{_channel}] 预测提前断电: I={current:F2}A, slope={slope:F3}A/ms, " +
                                    $"remain={remainA:F2}A, tToThr={tToThrMs:F1}ms ≤ Cut={predictiveCutMs}ms",
                                    "EPB");
                                return true;
                            }
                        }
                    }
                }

                // —— ⑤ 平台检测 —— //
                ring[head] = current;
                head = (head + 1) % winCap;
                if (count < winCap) count++;

                if (count == winCap && _iEmptyFwdA != 0)
                {
                    double min = ring[0], max = ring[0];
                    for (var i = 1; i < winCap; i++)
                    {
                        var v = ring[i];
                        if (v < min) min = v;
                        if (v > max) max = v;
                    }

                    var range = max - min;

                    if (range <= PlateauFlatRangeA && current >= _iEmptyFwdA + PlateauAboveEmptyMarginA)
                    {
                        _log.Warn(
                            $"EPB[{_channel}] 检测到疑似限流平台: {PlateauWindowMs}ms 内波动≤{range:F2}A, " +
                            $"I≈{current:F2}A（空行程≈{_iEmptyFwdA:F2}A）",
                            "EPB");
                        return true;
                    }
                }

                // —— ⑥ 超时保护 —— //
                if (ElapsedMs(tBegin) > 10_000)
                {
                    _log.Warn($"EPB[{_channel}] 超时: 10s 内未达到阈值 Thr={thrA:F2}A", "EPB");
                    return false;
                }

                // —— ⑦ 更新上一帧 —— //
                lastI = current;
                lastTick = nowTick;
                hasLast = true;

                loopCounter++;
            }
        }

        private async Task<bool> WaitCurrentAboveAsync(
            double thrA,
            CancellationToken token,
            int predictiveCutMs = 5,
            double minSlopeAperMs = 0.02,
            double maxSlopeAperMs = 1.0, // 斜率物理上限（A/ms）
            double safetyMarginA = 2,
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
        }
    }
}