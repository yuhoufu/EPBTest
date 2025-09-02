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
    public sealed class EpbCycleRunner
    {
        public delegate double ReadCurrentDelegate(int epbChannel);

        private const double PlateauAboveEmptyMarginA = 0.8;
        private const int PlateauWindowMs = 150;
        private const double PlateauFlatRangeA = 0.15;

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


        public async Task<bool> LearnAsync(int nCycles, CancellationToken token, int? targetPeriodMs)
        {
            // —— 若未显式传入周期（旧调用），用 5000ms 并给出日志 —— //
            if (targetPeriodMs == null)
            {
                targetPeriodMs = 5000;
                _log.Warn($"EPB[{_channel}] LearnAsync 未显式指定目标周期，默认按 {targetPeriodMs}ms 计算柔性分配。", "EPB");
            }

            // —— 本地时间工具（仅本方法内使用） —— //
            static long NowTicks()
            {
                return Stopwatch.GetTimestamp();
            }

            static int MsBetween(long t0, long t1)
            {
                return (int)((t1 - t0) * 1000.0 / Stopwatch.Frequency);
            }

            // —— 比例（①③⑦⑧），⑤单独使用 _holdMs —— //
            const double R_HEAD = 0.15; // ①
            const double R_FWD_EMPTY = 0.35; // ③（计划值）
            const double R_REV_EMPTY = 0.35; // ⑦（计划值，定时控制）
            const double R_TAIL = 0.15; // ⑧


            _log.Info(
                $"EPB[{_channel}] 学习开始，次数={nCycles}；采样={_sampleMs}ms，忽略涌流={_peakIgnoreMs}ms，" +
                $"emptyBand={_emptyBandA:F2}A，stableWin={_stableWinMs}ms，posThr={_posThrA:F2}A，" +
                $"plateau(win={PlateauWindowMs}ms, flat≤{PlateauFlatRangeA:F2}A, +emptyMargin≥{PlateauAboveEmptyMarginA:F2}A)。",
                "EPB");

            // ========== 预释放：在正式学习循环前，确保卡钳处于“已释放”状态 ==========
            if (_revEmptyKeepMs > 0)
                try
                {
                    _log.Info($"EPB[{_channel}] 自学习预处理：先反向释放，进入反向空行程后保持 {_revEmptyKeepMs}ms。", "EPB");
                    _do.SetEpbReverse(_channel);
                    await Task.Delay(_peakIgnoreMs, token); // 忽略涌流

                    // 尝试判定进入反向空行程（若失败也不阻塞流程，仍按定时保持）
                    var (okRel, _, iEmptyRel) = await WaitStableAroundAsync(-0.5, -1, _emptyBandA, _stableWinMs, token);
                    if (okRel)
                    {
                        _log.Info($"EPB[{_channel}] 预释放：已进入反向空行程，Iempty-≈{iEmptyRel:F2}A。保持 {_revEmptyKeepMs}ms。",
                            "EPB");
                        // 记录一次估计值（可作为后续观测的初值，非必须）
                        if (_iEmptyRevA == 0) _iEmptyRevA = iEmptyRel;
                    }
                    else
                    {
                        _log.Warn($"EPB[{_channel}] 预释放：未稳定判定到反向空行程，仍按 {_revEmptyKeepMs}ms 定时保持。", "EPB");
                    }

                    await Task.Delay(_revEmptyKeepMs, token);
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
                    _do.SetEpbOff(_channel); // 反向断电，准备进入学习循环
                }

            // —— 累积样本（中位数抑制离群） —— //
            var fwdPeakList = new List<double>(); // ②
            var fwdEmptyList = new List<double>(); // ③（实测，用于统计与对比）
            var clampList = new List<double>(); // ④
            var revPeakList = new List<double>(); // ⑥
            var revEmptyList = new List<double>(); // ⑦（按计划定时）
            var iEmptyFList = new List<double>();
            var iEmptyRList = new List<double>();

            for (var k = 0; k < nCycles; k++)
            {
                token.ThrowIfCancellationRequested();

                // 0) 液压（可选），其用时从总周期中扣除
                var swHyd = Stopwatch.StartNew();
                //var hydOk = await _hydraulic.RunOnceAsync(_hydId, token); // 自学习暂时不做液压
                var hydUsed = (int)swHyd.ElapsedMilliseconds; // 可能为 0（未启用）
                /*if (!hydOk) // 自学习暂时不做液压
                {
                    _log.Warn($"EPB[{_channel}] 学习第{k + 1}轮：液压未达成，跳过。", "EPB");
                    continue;
                }*/

                // 分配给电控段的预算
                var elecBudgetMs = Math.Max(0, (int)targetPeriodMs - hydUsed);

                // —— 先给 ①“计划值”（从第二圈起才真正执行），第一圈置 0 —— //
                var headPlan = 0;
                if (k >= 1 && fwdPeakList.Count > 0 && clampList.Count > 0 && revPeakList.Count > 0)
                {
                    // 用“已学中位数”预估刚性，提前算出 ① 计划
                    var rigidEst =
                        (int)Math.Round(Median(fwdPeakList) + Median(clampList) + Median(revPeakList)); // 刚性时间
                    var flexEst = Math.Max(0, elecBudgetMs - rigidEst - Math.Max(0, _holdMs)); // 柔性时间
                    headPlan = (int)Math.Floor(flexEst * R_HEAD);
                }

                // ① 头部未上电（仅第二圈起执行）
                if (headPlan > 0)
                {
                    _log.Info($"EPB[{_channel}] 学习{k + 1}：①头部未上电 {headPlan}ms（按比例 {R_HEAD:P0} 计划）。", "EPB");
                    await Task.Delay(headPlan, token);
                }
                else if (k == 0)
                {
                    _log.Info($"EPB[{_channel}] 学习{k + 1}：①头部未上电跳过（首圈不延时）。", "EPB");
                }

                var tElecStart = NowTicks(); // 电控段起点（用于最终⑧收口）

                // ========== ② + ③ + ④：正向 ==========
                _do.SetEpbForward(_channel);
                _log.Info($"EPB[{_channel}] 学习{k + 1}：②正向上电，忽略涌流 {_peakIgnoreMs}ms…", "EPB");
                await Task.Delay(_peakIgnoreMs, token);

                // ②：判入正向空行程（小电流带）
                var (okEmptyFwd, tEnterEmptyFwd_ms, iEmptyFwd) =
                    await WaitStableAroundAsync(+0.5, +1, _emptyBandA, _stableWinMs, token);
                if (!okEmptyFwd)
                {
                    _do.SetEpbOff(_channel);
                    _log.Warn($"EPB[{_channel}] 学习{k + 1}：②未判定到正向空行程，放弃本轮。", "EPB");
                    continue;
                }

                var tFwdPeakDecay = Math.Max(0, tEnterEmptyFwd_ms); // ②
                _log.Info($"EPB[{_channel}] 学习{k + 1}：②FwdPeakDecay={tFwdPeakDecay}ms，Iempty+≈{iEmptyFwd:F2}A。", "EPB");

                // —— ③/④ 分离 —— //
                var emptyLeaveThr = iEmptyFwd + _emptyBandA; // ③ 离开空行程阈值（向上）
                var tEmptyEnterTick = NowTicks();
                var rampStartTick = tEmptyEnterTick; // ③ 结束 / ④ 起点
                var clampReachTick = tEmptyEnterTick; // ④ 结束

                var rampStarted = false; // 是否离开空行程带
                var clampReached = false; // 是否到达阈值/平台
                var clampCause = "timeout";

                // 平台检测窗口
                var winCap = Math.Max(1, PlateauWindowMs / Math.Max(1, _sampleMs));
                var ring = new double[winCap];
                int rc = 0, rh = 0;

                var ewma = ReadEwma(_readCurrent(_channel), _readCurrent(_channel));
                var tLoopBegin = NowTicks();
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    await Task.Delay(_sampleMs, token);

                    var raw = _readCurrent(_channel);
                    ewma = ReadEwma(ewma, raw);

                    // ③：第一次离开空行程带（向上）→ 开始上升
                    if (!rampStarted && ewma >= emptyLeaveThr)
                    {
                        rampStarted = true;
                        rampStartTick = NowTicks();
                    }

                    // ④：到达夹紧阈值
                    if (ewma >= _posThrA)
                    {
                        clampReached = true;
                        clampReachTick = NowTicks();
                        clampCause = "threshold";
                        break;
                    }

                    // ④：限流平台（窗口近似直线且显著高于空行程）
                    ring[rh] = ewma;
                    rh = (rh + 1) % winCap;
                    if (rc < winCap) rc++;
                    if (rc == winCap && iEmptyFwd != 0)
                    {
                        double min = ring[0], max = ring[0];
                        for (var i = 1; i < winCap; i++)
                        {
                            var v = ring[i];
                            if (v < min) min = v;
                            if (v > max) max = v;
                        }

                        var range = max - min;
                        if (range <= PlateauFlatRangeA && ewma >= iEmptyFwd + PlateauAboveEmptyMarginA)
                        {
                            clampReached = true;
                            clampReachTick = NowTicks();
                            clampCause = "platform";
                            _log.Warn(
                                $"EPB[{_channel}] 学习{k + 1}：④检测到“限流平台”，{PlateauWindowMs}ms内波动≤{range:F2}A，" +
                                $"EWMA≈{ewma:F2}A（Iempty+≈{iEmptyFwd:F2}A，阈=Iempty+{PlateauAboveEmptyMarginA:F2}）。",
                                "EPB");
                            break;
                        }
                    }

                    if (MsBetween(tLoopBegin, NowTicks()) > 10_000) break; // 学习阶段 2s 超时
                }

                // 正向断电
                _do.SetEpbOff(_channel);

                // ③ 实测（未离开则记 0）
                if (!rampStarted) rampStartTick = clampReachTick;
                var tFwdEmpty = Math.Max(0, MsBetween(tEmptyEnterTick, rampStartTick)); // ③（实测）
                var tClampRamp = Math.Max(0, MsBetween(rampStartTick, clampReachTick)); // ④

                _log.Info(
                    $"EPB[{_channel}] 学习{k + 1}：③FwdEmpty(actual)={tFwdEmpty}ms，④ClampRamp={tClampRamp}ms（{clampCause}）。",
                    "EPB");

                // ⑤ 保持
                var holdUsed = Math.Max(0, _holdMs);
                Console.WriteLine($"_holdMs:{_holdMs}");

                if (holdUsed > 0)
                {
                    _log.Info($"EPB[{_channel}] 学习{k + 1}：⑤保持 {holdUsed}ms。", "EPB");
                    Console.WriteLine($"EPB[{_channel}] 学习{k + 1}：⑤保持 {holdUsed}ms。", "EPB");
                    await Task.Delay(holdUsed, token);
                }

                // ========== ⑥ + ⑦：反向 ==========
                _do.SetEpbReverse(_channel);
                _log.Info($"EPB[{_channel}] 学习{k + 1}：⑥反向上电，忽略涌流 {_peakIgnoreMs}ms…", "EPB");
                await Task.Delay(_peakIgnoreMs, token);

                // ⑥：入反向空行程
                var (okEmptyRev, tEnterEmptyRev_ms, iEmptyRev) =
                    await WaitStableAroundAsync(-0.5, -1, _emptyBandA, _stableWinMs, token);
                if (!okEmptyRev)
                {
                    _do.SetEpbOff(_channel);
                    _log.Warn($"EPB[{_channel}] 学习{k + 1}：⑥未判定到反向空行程，放弃本轮。", "EPB");
                    continue;
                }

                var tRevPeakDecay = Math.Max(0, tEnterEmptyRev_ms); // ⑥
                _log.Info($"EPB[{_channel}] 学习{k + 1}：⑥RevPeakDecay={tRevPeakDecay}ms，Iempty-≈{iEmptyRev:F2}A。", "EPB");

                // —— 用“本圈刚性 ②+④+⑥ + ⑤ + ①(已执行) ”从电控预算中扣除，得到柔性预算 —— //
                var rigidThis = (int)Math.Round((double)(tFwdPeakDecay + tClampRamp + tRevPeakDecay));
                var flexBudget = elecBudgetMs - headPlan - rigidThis - holdUsed;
                if (flexBudget < 0)
                {
                    _log.Warn(
                        $"EPB[{_channel}] 学习{k + 1}：柔性预算为负（预算{elecBudgetMs}ms，①{headPlan}ms，②④⑥合计{rigidThis}ms，⑤{holdUsed}ms）。" +
                        "将 ③/⑦/⑧ 置 0，仅做 ⑦=0 定时与⑧收口。", "EPB");
                    flexBudget = 0;
                }

                // 按比例得到计划 ①③⑦⑧（注意①已执行，这里计算用于日志与⑧参考）
                var plan3 = (int)Math.Floor(flexBudget * R_FWD_EMPTY); // ③（计划，仅对比）
                var plan7 = (int)Math.Floor(flexBudget * R_REV_EMPTY); // ⑦（计划，用于定时控制）
                var plan8 = Math.Max(0, flexBudget - plan3 - plan7); // ⑧（计划，尾段实际用收口完成）

                _log.Info(
                    $"EPB[{_channel}] 学习{k + 1}：柔性预算={flexBudget}ms -> ③(计划)={plan3}ms，⑦(计划)={plan7}ms，⑧(计划)≈{plan8}ms。",
                    "EPB");

                // ⑦：按“计划时间”定时控制（不观测退出）
                var run7 = Math.Max(10, plan7); // 给个最小 10ms
                _log.Info($"EPB[{_channel}] 学习{k + 1}：⑦反向空行程定时 {run7}ms…", "EPB");
                await Task.Delay(run7, token);
                var tRevEmpty = run7;

                // 反向断电
                _do.SetEpbOff(_channel);

                // ⑧：尾段收口 —— 把实际误差全吸收，使电控段总时长 ≈ elecBudgetMs
                var elecElapsed = MsBetween(tElecStart, NowTicks());
                var tailRemain = Math.Max(0, elecBudgetMs - elecElapsed);
                if (tailRemain > 0)
                {
                    _log.Info($"EPB[{_channel}] 学习{k + 1}：⑧尾段收口 {tailRemain}ms（计划≈{plan8}ms）。", "EPB");
                    await Task.Delay(tailRemain, token);
                }

                // —— 记录“③ 计划 vs 实测”的对比 —— //
                if (plan3 > 0 || tFwdEmpty > 0)
                {
                    var diff = tFwdEmpty - plan3;
                    _log.Info(
                        $"EPB[{_channel}] 学习{k + 1}：③实测={tFwdEmpty}ms vs ③计划={plan3}ms，差值={diff}ms。", "EPB");
                }

                // —— 累积样本 —— //
                fwdPeakList.Add(tFwdPeakDecay); // ②
                fwdEmptyList.Add(tFwdEmpty); // ③（实测）
                clampList.Add(tClampRamp); // ④
                revPeakList.Add(tRevPeakDecay); // ⑥
                revEmptyList.Add(tRevEmpty); // ⑦（按计划定时）
                iEmptyFList.Add(iEmptyFwd);
                iEmptyRList.Add(iEmptyRev);

                _log.Info(
                    $"EPB[{_channel}] 学习{k + 1} 汇总：②={tFwdPeakDecay}ms, ③(actual)={tFwdEmpty}ms, " +
                    $"④={tClampRamp}ms, ⑥={tRevPeakDecay}ms, ⑦(planned)={tRevEmpty}ms, " +
                    $"I±empty≈({iEmptyFwd:F2},{iEmptyRev:F2})A。", "EPB");

                // ========= 阶段统计日志 =========
                var tHead = headPlan;
                var t2 = (int)Math.Round((decimal)tFwdPeakDecay);
                var t3 = (int)Math.Round((decimal)tFwdEmpty);
                var t4 = (int)Math.Round((decimal)tClampRamp);
                var t5 = holdUsed;
                var t6 = (int)Math.Round((decimal)tRevPeakDecay);
                var t7 = (int)Math.Round((decimal)tRevEmpty);
                var t8 = tailRemain;

                var total = tHead + t2 + t3 + t4 + t5 + t6 + t7 + t8;

                _log.Info(
                    $"EPB[{_channel}] 学习{k + 1}阶段统计：" +
                    $"①Head={tHead}ms, ②FwdPeak={t2}ms, ③FwdEmpty={t3}ms, ④ClampRamp={t4}ms, " +
                    $"⑤Hold={t5}ms, ⑥RevPeak={t6}ms, ⑦RevEmpty={t7}ms, ⑧Tail={t8}ms | " +
                    $"合计={total}ms (目标≈{targetPeriodMs}ms)", "EPB");
            }


            if (!fwdPeakList.Any() || !revPeakList.Any())
            {
                _log.Warn($"EPB[{_channel}] 学习失败：有效样本不足（至少应包含 ② 与 ⑥）。", "EPB");
                return false;
            }

            // —— 中位数抑制离群 —— //
            _tFwdPeakDecayMs = Median(fwdPeakList); // ②
            _tClampRampMs = clampList.Any() ? Median(clampList) : 0.0; // ④
            _tRevPeakDecayMs = Median(revPeakList); // ⑥

            _tFwdEmptyMs = fwdEmptyList.Any() ? Median(fwdEmptyList) : 0.0; // ③（参考）
            _tRevEmptyMs = revEmptyList.Any() ? Median(revEmptyList) : 0.0; // ⑦（定时）

            _iEmptyFwdA = iEmptyFList.Any() ? Median(iEmptyFList) : 0.0;
            _iEmptyRevA = iEmptyRList.Any() ? Median(iEmptyRList) : 0.0;

            _log.Info(
                $"EPB[{_channel}] 学习完成(中位数)：②={_tFwdPeakDecayMs}ms，④={_tClampRampMs}ms，⑥={_tRevPeakDecayMs}ms；" +
                $"③(actual)={_tFwdEmptyMs}ms（参考），⑦(planned)={_tRevEmptyMs}ms；" +
                $"I±empty≈({_iEmptyFwdA:F2},{_iEmptyRevA:F2})A。", "EPB");

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
                await Task.Delay(4000- plan1, token);

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

        private double ReadEwma(double prev, double cur)
        {
            return prev + _ewmaAlpha * (cur - prev);
        }

        private async Task<(bool ok, long tEnter, double iAvg)> WaitStableAroundAsyncOld(
            double targetA, int sign, double bandA, int stableWinMs, CancellationToken token)
        {
            var wnd = Math.Max(_sampleMs, stableWinMs);
            var tBegin = Stopwatch.GetTimestamp();
            var ewma = ReadEwma(_readCurrent(_channel), _readCurrent(_channel));
            var inBandMs = 0;
            double sum = 0;
            var n = 0;

            while (true)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(_sampleMs, token);
                var raw = _readCurrent(_channel);
                ewma = ReadEwma(ewma, raw);
                var diff = Math.Abs(ewma - targetA);
                if (diff <= bandA && Math.Sign(ewma) == Math.Sign(targetA))
                {
                    inBandMs += _sampleMs;
                    sum += ewma;
                    n++;
                    if (inBandMs >= wnd) return (true, ElapsedMs(tBegin), n > 0 ? sum / n : ewma);
                }
                else
                {
                    inBandMs = 0;
                    sum = 0;
                    n = 0;
                }

                if (ElapsedMs(tBegin) > 10_000) // 超时保护,超出 10s
                    return (false, 0, 0);
            }
        }

        /// <summary>
        ///     改进版：
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

        private async Task<bool> WaitCurrentAboveAsync(double thrA, CancellationToken token)
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
    }
}