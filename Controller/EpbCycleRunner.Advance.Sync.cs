using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Controller
{
    /// <summary>
    ///     EpbCycleRunner 扩展：对齐外壳（去① + ⑧扣回 + deadline 收尾）。
    ///     注意：这里不改变“电流判稳、平台判据、预测提前断电”等核心算法，只管理时序外壳。
    /// </summary>
    public partial class EpbCycleRunner : IEpbCycleRunner
    {

        #region 新增参数（建议置于类的字段区）
        /// <summary>
        /// 反向峰值“刚性衰减”最长等待（ms）。
        /// 在反向忽略涌流后，最多等待该时长以观察电流是否衰减到 <see cref="RevDecayLimitA"/> 以下；
        /// 若未达标则按超时处理并记告警。
        /// </summary>
        private int RevDecayRigidMaxMs = 1000; // 建议现场可配：80~150ms 120

        /// <summary>
        /// 反向固定空行程时长（ms）。不再用“空行程值+带宽”判据，仅做固定时间推进。
        /// </summary>
        private int RevEmptyFixedMs = 2000; // 你提出的默认值

        /// <summary>
        /// 反向电流“衰减限值”（A）。用于判定“峰值已衰减到足够低”
        /// 典型值：3A（可按被测件与电源特性微调）
        /// </summary>
        private double RevDecayLimitA = 3.0;
        #endregion

        /// <summary>
        ///     单通道完成一个循环时触发的事件。
        ///     参数依次为：EPB 通道号（1..12）、已完成总圈数。
        /// </summary>

        /// <summary>
        /// 试验前已有的累计运行次数（从 <see cref="EpbTestRecord.RunCount"/> 传入）。
        /// 初始化时赋值，之后整个周期内不再修改。
        /// </summary>
        //private readonly int _initialRunCount;

        /// <summary>
        /// 本次试验过程中新增的运行次数（从 0 开始，每完成一圈加 1）。
        /// </summary>


        /// <summary>学习期使用的“临时裕量”（仅在学习圈内滚动更新，避免直接写回 _safetyMarginA）。</summary>
        private double _learnMargin = double.NaN;

        /// <summary>学习期的“裕量轨迹”（每圈更新一次）。</summary> 
        private readonly List<double> _marginTrace = new List<double>(32);

        /// <summary>学习期的“峰值轨迹”（每圈记录，用于去异常/诊断）。</summary>
        private readonly List<double> _peakTrace = new List<double>(32);

        /// <summary>写裕量的线程安全锁（若同实例可能并发，建议保留）。</summary>
        private readonly object _marginLock = new object();

        /// <summary>
        /// 运行/学习期共用：发生过冲后，短暂抑制“向下调裕量”（避免两种动态模式间来回摆动）。
        /// 单位：剩余圈数。
        /// </summary>
        private int _downAdjustFreezeCyclesLeft = 0;


        /// <summary>安全裕量学习参数（可视需要暴露到配置）。</summary>
        private static class MarginLearnDefaults
        {
            public const double DeadbandA = 0.05; // |err| ≤ deadband 不调参

            // 非对称 + 分段：过冲更激进、欠冲更保守（避免两种模式之间来回追）
            public const double KpUp = 0.80; // err>0
            public const double KpDown = 0.40; // err<0

            public const double MaxStepUpSmallA = 0.60; // |err| < SplitErrA
            public const double MaxStepUpBigA = 1.00;   // |err| ≥ SplitErrA
            public const double MaxStepDownSmallA = 0.30;
            public const double MaxStepDownBigA = 0.40;
            public const double SplitErrA = 1.00;

            // 迟滞：过冲后冻结/衰减下调若干圈
            public const int FreezeDownCyclesSmall = 2;
            public const int FreezeDownCyclesBig = 3;
            public const double FreezeDownScale = 0.25; // 冻结期：下调的 Kp/step 缩放系数（0=完全冻结）

            public const double MinMarginA = 0.20; // 下限
            public const double MaxMarginA = 5.00; // 上限

            public const int WindowForFinalize = 5;   // 收敛统计：取“最后 N 圈”的窗口
            public const double MadK = 3.0;              // MAD 去异常阈
        }



        /// <summary>学习阶段：单圈结果样本。</summary>
        public sealed class LearnSample
        {
            public int TFwdPeakDecayMs; // ②
            public int TFwdEmptyMs; // ③ (actual)
            public int TClampRampMs; // ④
            public int TRevPeakDecayMs; // ⑥
            public int TRevEmptyMs; // ⑦ (planned)
            public double IEmptyFwdA; // I+empty
            public double IEmptyRevA; // I-empty
        }

        // —— 简单的聚合容器（一次批量学习期间使用）——
        private List<double> _aggFwdPeak, _aggFwdEmpty, _aggClamp, _aggRevPeak, _aggRevEmpty, _aggIEmptyF, _aggIEmptyR;

        /// <summary>开始一轮学习聚合（清空样本缓存）。</summary>
        public void BeginLearnAggregation()
        {
            _aggFwdPeak = new List<double>();
            _aggFwdEmpty = new List<double>();
            _aggClamp = new List<double>();
            _aggRevPeak = new List<double>();
            _aggRevEmpty = new List<double>();
            _aggIEmptyF = new List<double>();
            _aggIEmptyR = new List<double>();
        }

        /// <summary>加入一条单圈学习样本。</summary>
        public void ApplyLearnSample(LearnSample s)
        {
            if (s == null) return;
            _aggFwdPeak.Add(s.TFwdPeakDecayMs);
            _aggFwdEmpty.Add(s.TFwdEmptyMs);
            _aggClamp.Add(s.TClampRampMs);
            _aggRevPeak.Add(s.TRevPeakDecayMs);
            _aggRevEmpty.Add(s.TRevEmptyMs);
            if (s.IEmptyFwdA != 0) _aggIEmptyF.Add(s.IEmptyFwdA);
            if (s.IEmptyRevA != 0) _aggIEmptyR.Add(s.IEmptyRevA);
        }

        /// <summary>结束聚合：把中位数写回 Runner 的估计字段。</summary>
        public void FinalizeLearnAggregation()
        {
            if (_aggFwdPeak == null) return;

            _tFwdPeakDecayMs = Median(_aggFwdPeak);
            _tClampRampMs = _aggClamp.Count > 0 ? Median(_aggClamp) : 0.0;
            _tRevPeakDecayMs = Median(_aggRevPeak);
            _tFwdEmptyMs = _aggFwdEmpty.Count > 0 ? Median(_aggFwdEmpty) : 0.0;
            _tRevEmptyMs = _aggRevEmpty.Count > 0 ? Median(_aggRevEmpty) : 0.0;
            _iEmptyFwdA = _aggIEmptyF.Count > 0 ? Median(_aggIEmptyF) : _iEmptyFwdA;
            _iEmptyRevA = _aggIEmptyR.Count > 0 ? Median(_aggIEmptyR) : _iEmptyRevA;

            // 释放临时列表
            _aggFwdPeak = _aggFwdEmpty = _aggClamp = _aggRevPeak = _aggRevEmpty = _aggIEmptyF = _aggIEmptyR = null;
        }

        

        /// <inheritdoc />
        public bool UseNoHeadPhase { get; set; } = true;

        /// <inheritdoc />
        public bool EnableTailCompensation { get; set; } = true;

        /// <inheritdoc />
        public int TailMinMs { get; set; } = 150;

        /// <summary>
        ///     学习单圈（对齐外壳版）。
        ///     外部已把“每圈对齐 + 相位延时”实现为：在“锚点+相位”时刻调用本方法——
        ///     因此此处不再做①；仅在⑧中扣回 (tailBaseMs - phaseMs)，并做最小时长保护。
        /// </summary>
        /// <param name="periodMs">目标周期（ms）。</param>
        /// <param name="tailBaseMs">尾段基准（旧T8 + 旧T1）。</param>
        /// <param name="phaseMs">本通道相位：索引×Δ。</param>
        /// <param name="tailMinMs">尾段最小保护（ms）。</param>
        /// <param name="token">取消令牌。</param>
        public async Task<bool> LearnOneAlignedAsync(int periodMs, int tailBaseMs, int phaseMs, int tailMinMs,
            CancellationToken token)
        {
            // —— 1) 进入“学习一圈”的主流程 —— //
            // 说明：这里我们调用你现有的一圈学习实现（如果你只有 LearnAsync(n) 且无“单圈”入口，
            // 可重用 LearnAsync(1, token, periodMs)；若已有内部 OneCycleLearnAsync 更理想）
            var ok = await LearnAsync(1, token, periodMs).ConfigureAwait(false);

            // —— 2) ⑧尾段扣回：T8_actual = max(tailMin, tailBase - phase) —— //
            if (EnableTailCompensation)
            {
                var t8 = Math.Max(tailMinMs, tailBaseMs - phaseMs);
                if (t8 > 0)
                    try
                    {
                        await Task.Delay(t8, token).ConfigureAwait(false);
                    }
                    catch (TaskCanceledException)
                    {
                        /* ignore */
                    }
            }

            return ok;
        }



        /// <summary>
        /// 学习阶段的“单圈核心”（对齐外壳版，按你的新方案改造）：
        /// <list type="number">
        ///   <item>正向：上电→忽略涌流→直接执行“夹紧阈值判定”（合并原②~④，不再单独判“空行程”）。</item>
        ///   <item>达到阈值/平台/预测将触达→立即断电，并通知协调器标记释放。</item>
        ///   <item>保持（⑤）按配置执行（可为 0）。</item>
        ///   <item>反向：上电→忽略涌流→在“刚性衰减上限”内等待电流衰减至 <see cref="RevDecayLimitA"/> 以下，记录 TRevPeakDecayMs。</item>
        ///   <item>随后不再做“空行程值+带宽”的判据，直接执行“反向固定空行程时长” <see cref="RevEmptyFixedMs"/>（⑦）。</item>
        ///   <item>本方法不再回溯“正向空带/阈值上穿”的分界时刻，<c>TFwdEmptyMs/TClampRampMs/IEmptyFwdA/IEmptyRevA</c> 等字段置 0。</item>
        ///   <item>⑧ 收尾仍由外壳统一按 <c>(tailBaseMs - phaseMs)</c> 收口。</item>
        /// </list>
        /// </summary>
        /// <param name="periodMs">
        /// 目标周期（ms）。此处仅用于与外壳保持一致，不在内部用于①/⑧的等待（保留签名以兼容上层）。</param>
        /// <param name="tailBaseMs">尾段基准（旧 T8 + 旧 T1），仅用于外壳收尾时的计算；本方法不直接使用。</param>
        /// <param name="phaseMs">当前通道相位（索引×Δ），仅用于外壳收尾；本方法不直接使用。</param>
        /// <param name="tailMinMs">尾段最小保护（ms），仅用于外壳；本方法不直接使用。</param>
        /// <param name="token">取消令牌。</param>
        /// <returns>
        /// 若学习成功，返回 <see cref="LearnSample"/>：
        /// <list type="bullet">
        ///   <item><c>TFwdPeakDecayMs</c>：此处记录“正向忽略涌流时长”（作为峰值衰减的等效值）。</item>
        ///   <item><c>TFwdEmptyMs</c> = 0、<c>TClampRampMs</c> = 0（不再分段）。</item>
        ///   <item><c>TRevPeakDecayMs</c>：反向“刚性衰减”实测（或等于上限）。</item>
        ///   <item><c>TRevEmptyMs</c>：反向固定空行程（即 <see cref="RevEmptyFixedMs"/>）。</item>
        ///   <item><c>IEmptyFwdA</c>、<c>IEmptyRevA</c> = 0（弃用）。</item>
        /// </list>
        /// 若中途失败或取消，返回 <c>null</c>。
        /// </returns>
        public async Task<LearnSample> LearnOneAlignedCoreAsyncOld(
            int periodMs, int tailBaseMs, int phaseMs, int tailMinMs, CancellationToken token)
        {
            // —— 进入液压建压（与正式阶段保持一致；若无需求可保持幂等）——
            if (_manager != null)
                await _manager.HydraulicEnterAsync(_channel, _executionPermit, token)
                    .ConfigureAwait(false);

            // ===================== 正向阶段（②~④ 合并为“直接夹紧判据”） =====================
            // ② 正向上电并忽略涌流去抖
            RequireMotorCommandSucceeded(CommandForward(), "Forward");
            await Task.Delay(_peakIgnoreMs, token).ConfigureAwait(false);

            // 这里的“正向峰值衰减时长”按你的新方案，取为“忽略涌流时长”的等效值。
            var tFwdPeakDecayMs = Math.Max(0, _peakIgnoreMs);

            // ③+④：直接进入“夹紧阈值判定”（不再单独判空行程）
            var tFwdJudgeStart = Stopwatch.GetTimestamp();
            var okClamp = await WaitCurrentAboveAsync(_posThrA, token).ConfigureAwait(false);
            var fwdJudgeElapsedMs = (int)((Stopwatch.GetTimestamp() - tFwdJudgeStart) * 1000.0 / Stopwatch.Frequency);

            if (!okClamp)
            {
                // 未达到阈值/平台，直接断电并放弃本圈样本
                RequireMotorCommandSucceeded(
                    CommandOffHighPriority(),
                    "ForwardAbortOff");
                _log?.Warn($"EPB[{_channel}] 正向阶段未满足夹紧判据，放弃本圈样本。", "EPB");
                return null;
            }

            // —— 达到夹紧判据 → 立即断电，并通知协调器（用于统一释压）——
            RequireMotorCommandSucceeded(
                CommandOffHighPriority(),
                "ForwardTerminalOff");
            if (_manager != null)
                await _manager.HydraulicMarkReleaseAsync(_channel).ConfigureAwait(false);

            _log?.Info(
                $"EPB[{_channel}] 正向阶段完成：PeakIgnore={tFwdPeakDecayMs}ms, JudgeElapsed≈{fwdJudgeElapsedMs}ms（②~④已合并）。", "EPB");

            // ===================== ⑤ 保持 =====================
            if (_holdMs > 0)
                await Task.Delay(_holdMs, token).ConfigureAwait(false);

            // ===================== 反向阶段（⑥ 刚性衰减 + ⑦ 固定空行程） =====================
            // ⑥ 反向上电并忽略涌流去抖
            RequireMotorCommandSucceeded(CommandReverse(), "Reverse");
            await Task.Delay(_peakIgnoreMs, token).ConfigureAwait(false);

            // —— 在“刚性衰减上限”内等待电流 ≤ RevDecayLimitA —— //
            var tRevDecayStart = Stopwatch.GetTimestamp();
            var tRevPeakDecayMs = 0;
            var decayReached = false;

            // 以 _sampleMs 为节拍进行轮询（不使用 Thread.Sleep(1) 等粗粒度等待，保持与采样节拍一致）
            var tickPerMs = Stopwatch.Frequency / 1000.0;
            var nextDue = Stopwatch.GetTimestamp();
            while (true)
            {
                token.ThrowIfCancellationRequested();

                var now = await DelayUntilMonotonicAsync(nextDue, token)
                    .ConfigureAwait(false);
                nextDue += (long)(Math.Max(1, _sampleMs) * tickPerMs);

                var current = _readCurrent(_channel);
                var elapsedMs = (int)((now - tRevDecayStart) * 1000.0 / Stopwatch.Frequency);

                if (current <= RevDecayLimitA)
                {
                    decayReached = true;
                    tRevPeakDecayMs = elapsedMs;
                    break;
                }

                if (elapsedMs >= RevDecayRigidMaxMs)
                {
                    // 超过刚性上限仍未达标
                    tRevPeakDecayMs = RevDecayRigidMaxMs;
                    break;
                }
            }

            if (!decayReached)
            {
                _log?.Warn(
                    $"EPB[{_channel}] 反向峰值衰减未达标：限值={RevDecayLimitA:F2}A，上限={RevDecayRigidMaxMs}ms，" +
                    $"实测≈{tRevPeakDecayMs}ms（按上限计入）。", "EPB");
            }
            else
            {
                _log?.Info(
                    $"EPB[{_channel}] 反向峰值衰减达标：I≤{RevDecayLimitA:F2}A，TRevPeakDecay≈{tRevPeakDecayMs}ms。", "EPB");
            }

            // ⑦ 反向固定空行程：无需“空行程值 + 带宽”判据
            await Task.Delay(Math.Max(0, RevEmptyFixedMs), token).ConfigureAwait(false);

            // —— 反向断电 —— //
            RequireMotorCommandSucceeded(CommandOff(), "ReverseTerminalOff");

            // ===================== 返回单圈样本（兼容 LearnSample 结构） =====================
            // 说明：
            //  - TFwdPeakDecayMs：用忽略涌流时长作为“峰值衰减的等效值”（你给的方案）
            //  - TFwdEmptyMs / TClampRampMs：已合并为直接夹紧判据，置 0
            //  - TRevPeakDecayMs：刚性衰减实测（或等于上限）
            //  - TRevEmptyMs：固定空行程（RevEmptyFixedMs）
            //  - IEmptyFwdA / IEmptyRevA：弃用，置 0（保留字段兼容外层聚合）
            var sample = new LearnSample
            {
                TFwdPeakDecayMs = tFwdPeakDecayMs,
                TFwdEmptyMs = 0,
                TClampRampMs = 0,
                TRevPeakDecayMs = tRevPeakDecayMs,
                TRevEmptyMs = Math.Max(0, RevEmptyFixedMs),
                IEmptyFwdA = 0,
                IEmptyRevA = 0
            };

            // 可选：记录“正向合并阶段耗时”（忽略涌流后到阈值/平台判定完成的时间），便于统计（不改 LearnSample 结构）
            _log?.Info($"EPB[{_channel}] 正向合并阶段耗时（忽略后→判定完成）≈{fwdJudgeElapsedMs}ms。", "EPB");

            return sample;
        }

        /// <summary>
        /// 学习阶段的“单圈核心”（对齐外壳版，按你的新方案并加入 SafetyMargin 自学习）：
        /// <list type="number">
        ///   <item>正向：上电→忽略涌流→直接执行“夹紧阈值判定”（合并原②~④，不再单独判“空行程”）。</item>
        ///   <item>达到阈值/平台/预测将触达→立即断电，并通知协调器标记释放；同步封口获取本圈峰值 <c>peak.MaxAmp</c>。</item>
        ////  <item>用 <c>e = peak.MaxAmp - _posThrA</c> 做比例+死区+步长限幅更新 <c>_safetyMarginA</c>（峰值偏高→增大裕量；偏低→减小）。</item>
        ///   <item>保持（⑤）按配置执行（可为 0）。</item>
        ///   <item>反向：上电→忽略涌流→在“刚性衰减上限”内等待电流衰减至 <see cref="RevDecayLimitA"/> 以下，记录 TRevPeakDecayMs。</item>
        ///   <item>随后不再做“空行程值+带宽”的判据，直接执行“反向固定空行程时长” <see cref="RevEmptyFixedMs"/>（⑦）。</item>
        ///   <item>本方法不再回溯“正向空带/阈值上穿”的分界时刻，<c>TFwdEmptyMs/TClampRampMs/IEmptyFwdA/IEmptyRevA</c> 等字段置 0。</item>
        ///   <item>⑧ 收尾仍由外壳统一按 <c>(tailBaseMs - phaseMs)</c> 收口。</item>
        /// </list>
        /// <para>
        /// 【自学习策略】单圈闭环：<br/>
        /// <c>err = peak.MaxAmp - _posThrA</c>；若 <c>|err| &gt; deadband</c>，则
        /// <c>Δmargin = clamp(kp * err, ±maxStep)</c>，并将 <c>_safetyMarginA = clamp(_safetyMarginA + Δmargin, [min,max])</c>。<br/>
        /// 目的：使最终峰值尽量逼近阈值 <c>_posThrA</c>（不过冲不过早）。
        /// </para>
        /// </summary>
        /// <param name="periodMs">目标周期（ms）。仅用于与外壳保持一致，不在内部用于①/⑧的等待（保留签名以兼容上层）。</param>
        /// <param name="tailBaseMs">尾段基准（旧 T8 + 旧 T1），仅用于外壳收尾时的计算；本方法不直接使用。</param>
        /// <param name="phaseMs">当前通道相位（索引×Δ），仅用于外壳收尾；本方法不直接使用。</param>
        /// <param name="tailMinMs">尾段最小保护（ms），仅用于外壳；本方法不直接使用。</param>
        /// <param name="token">取消令牌。</param>
        /// <returns>
        /// 若学习成功，返回 <see cref="LearnSample"/>：
        /// <list type="bullet">
        ///   <item><c>TFwdPeakDecayMs</c>：此处记录“正向忽略涌流时长”（作为峰值衰减的等效值）。</item>
        ///   <item><c>TFwdEmptyMs</c> = 0、<c>TClampRampMs</c> = 0（不再分段）。</item>
        ///   <item><c>TRevPeakDecayMs</c>：反向“刚性衰减”实测（或等于上限）。</item>
        ///   <item><c>TRevEmptyMs</c>：反向固定空行程（即 <see cref="RevEmptyFixedMs"/>）。</item>
        ///   <item><c>IEmptyFwdA</c>、<c>IEmptyRevA</c> = 0（弃用）。</item>
        /// </list>
        /// 若中途失败或取消，返回 <c>null</c>。
        /// </returns>
        public async Task<LearnSample> LearnOneAlignedCoreAsync(
            int periodMs, int tailBaseMs, int phaseMs, int tailMinMs, CancellationToken token)
        {
            // —— 进入液压建压（与正式阶段保持一致；若无需求可保持幂等）——
            if (_manager != null)
                await _manager.HydraulicEnterAsync(_channel, _executionPermit, token)
                    .ConfigureAwait(false);

            // ===================== 正向阶段（②~④ 合并为“直接夹紧判据”） =====================
            // ② 正向上电并忽略涌流去抖
            RequireMotorCommandSucceeded(CommandForward(), "Forward");
            await Task.Delay(_peakIgnoreMs, token).ConfigureAwait(false);

            // 这里的“正向峰值衰减时长”按你的新方案，取为“忽略涌流时长”的等效值。
            var tFwdPeakDecayMs = Math.Max(0, _peakIgnoreMs);

            // —— 启动峰值捕获 —— 
            if (_acq != null)
                _acq.BeginEpbCurrentPeak(_channel);

            // —— 使用“临时学习裕量”调用判据（不要直接用 _safetyMarginA，避免被单圈扰动）——
            double marginNow;
            lock (_marginLock)
            {
                marginNow = double.IsNaN(_learnMargin) ? (_safetyMarginA > 0 ? _safetyMarginA : 2.0) : _learnMargin;
            }
            var tFwdJudgeStart = Stopwatch.GetTimestamp();
            var okClamp = await WaitCurrentAboveAsync(
                thrA: _posThrA,
                safetyMarginA: marginNow, // ★★ 使用学习中的临时裕量
                token: token
            ).ConfigureAwait(false);
            var fwdJudgeElapsedMs = (int)((Stopwatch.GetTimestamp() - tFwdJudgeStart) * 1000.0 / Stopwatch.Frequency);

            // —— 达成与否都立刻断电 —— 
            RequireMotorCommandSucceeded(CommandOff(), "ForwardTerminalOff");

            if (!okClamp)
            {
                _log?.Warn($"EPB[{_channel}] 正向阶段未满足夹紧判据（Margin={marginNow:F3}A），放弃本圈样本。", "EPB");
                if (_acq != null)
                {
                    try { await _acq.EndEpbCurrentPeakAsync(_channel, 500, true, token).ConfigureAwait(false); } catch { }
                }
                return null;
            }

            // —— 通知协调器（统一释压）——
            if (_manager != null)
                await _manager.HydraulicMarkReleaseAsync(_channel).ConfigureAwait(false);

            // —— 获取本圈峰值（同步版；若要“软等待/完全异步”，用我前条消息给你的两种方案 A/B）——
            double peakAmp = 0.0;
            if (_acq != null)
            {
                var peak = await _acq.EndEpbCurrentPeakAsync(
                    _channel, delayMs: 1000, cutoffAfterDelay: true, token: token
            ).ConfigureAwait(false);
                peakAmp = peak.MaxAmp;
            }
            else
            {
                peakAmp = Math.Max(0.0, _actualCutoffCurrent);
            }

            _log?.Info(
                $"EPB[{_channel}] 正向阶段完成：PeakIgnore={tFwdPeakDecayMs}ms, Judge≈{fwdJudgeElapsedMs}ms, " +
                $"I_peak={peakAmp:F3}A, Thr={_posThrA:F3}A, Margin(use)={marginNow:F3}A。",
                "EPB");

            // —— ★★ 仅更新“临时学习裕量”并记录轨迹；不直接写 _safetyMarginA —— 
            ApplySafetyMarginFromPeak(peakAmp);

            // ===================== ⑤ 保持 =====================
            if (_holdMs > 0)
                await Task.Delay(_holdMs, token).ConfigureAwait(false);

            // ===================== 反向阶段（⑥ 刚性衰减 + ⑦ 固定空行程） =====================
            // ⑥ 反向上电并忽略涌流去抖
            RequireMotorCommandSucceeded(CommandReverse(), "Reverse");
            await Task.Delay(_peakIgnoreMs, token).ConfigureAwait(false);

            // —— 在“刚性衰减上限”内等待电流 ≤ RevDecayLimitA —— //
            var tRevDecayStart = Stopwatch.GetTimestamp();
            var tRevPeakDecayMs = 0;
            var decayReached = false;

            // 以 _sampleMs 为节拍进行轮询（不使用 Thread.Sleep(1) 等粗粒度等待，保持与采样节拍一致）
            var tickPerMs = Stopwatch.Frequency / 1000.0;
            var nextDue = Stopwatch.GetTimestamp();
            while (true)
            {
                token.ThrowIfCancellationRequested();

                var now = await DelayUntilMonotonicAsync(nextDue, token)
                    .ConfigureAwait(false);
                nextDue += (long)(Math.Max(1, _sampleMs) * tickPerMs);

                var current = _readCurrent(_channel);
                var elapsedMs = (int)((now - tRevDecayStart) * 1000.0 / Stopwatch.Frequency);

                if (current <= RevDecayLimitA)
                {
                    decayReached = true;
                    tRevPeakDecayMs = elapsedMs;
                    break;
                }

                if (elapsedMs >= RevDecayRigidMaxMs)
                {
                    // 超过刚性上限仍未达标
                    tRevPeakDecayMs = RevDecayRigidMaxMs;
                    break;
                }
            }

            if (!decayReached)
            {
                _log?.Warn(
                    $"EPB[{_channel}] 反向峰值衰减未达标：限值={RevDecayLimitA:F2}A，上限={RevDecayRigidMaxMs}ms，" +
                    $"实测≈{tRevPeakDecayMs}ms（按上限计入）。", "EPB");
            }
            else
            {
                _log?.Info(
                    $"EPB[{_channel}] 反向峰值衰减达标：I≤{RevDecayLimitA:F2}A，TRevPeakDecay≈{tRevPeakDecayMs}ms。", "EPB");
            }

            // ⑦ 反向固定空行程：无需“空行程值 + 带宽”判据
            await Task.Delay(Math.Max(0, RevEmptyFixedMs), token).ConfigureAwait(false);

            // —— 反向断电 —— //
            RequireMotorCommandSucceeded(CommandOff(), "ReverseTerminalOff");

            // ===================== 返回单圈样本（兼容 LearnSample 结构） =====================
            var sample = new LearnSample
            {
                TFwdPeakDecayMs = tFwdPeakDecayMs,
                TFwdEmptyMs = 0,
                TClampRampMs = 0,
                TRevPeakDecayMs = tRevPeakDecayMs,
                TRevEmptyMs = Math.Max(0, RevEmptyFixedMs),
                IEmptyFwdA = 0,
                IEmptyRevA = 0
            };

            // 可选：记录“正向合并阶段耗时”（忽略涌流后到判定完成的时间），便于统计（不改 LearnSample 结构）
            _log?.Info($"EPB[{_channel}] 正向合并阶段耗时（忽略后→判定完成）≈{fwdJudgeElapsedMs}ms。", "EPB");

            return sample;
        }

        /// <summary>
        /// 开始一轮“安全裕量学习”聚合：复位临时容器与“临时裕量”。
        /// 建议在外层 Learn…（或开始批量学习）之前调用。
        /// </summary>
        public void BeginSafetyMarginLearning()
        {
            lock (_marginLock)
            {
                _marginTrace.Clear();
                _peakTrace.Clear();
                // 初值取当前运行裕量，保证第一圈就能使用合理值
                _learnMargin = (_safetyMarginA > 0) ? _safetyMarginA : 2.0;
            }
        }

        /// <summary>
        /// 记录一圈的峰值，并基于该峰值对“临时裕量 _learnMargin”做闭环更新；
        /// —— 仅更新 _learnMargin 与轨迹，不直接写 _safetyMarginA（避免被偶发样本污染最终结果）。
        /// </summary>
        /// <param name="peakAmp">本圈正向段的峰值（Imax）。</param>
        private void ApplySafetyMarginFromPeak(double peakAmp)
        {
            if (_safetyMarginControlMode == SafetyMarginControlMode.Legacy20251010)
                ApplySafetyMarginFromPeak_Legacy20251010(peakAmp);
            else
                ApplySafetyMarginFromPeak_FreezeA20260101(peakAmp);
        }

        private void ApplySafetyMarginFromPeak_Legacy20251010(double peakAmp)
        {
            var err = peakAmp - _posThrA;
            var absErr = Math.Abs(err);

            const double deadbandA = 0.05;
            const double kp = 0.60;
            const double maxStepA = 0.50;
            const double minMarginA = 0.20;
            const double maxMarginA = 5.00;

            double before, after;
            lock (_marginLock)
            {
                before = double.IsNaN(_learnMargin) ? (_safetyMarginA > 0 ? _safetyMarginA : 2.0) : _learnMargin;

                // legacy 模式不使用 freezeDown
                _downAdjustFreezeCyclesLeft = 0;

                if (absErr > deadbandA)
                {
                    var delta = kp * err;
                    if (delta > 0) delta = Math.Min(delta, maxStepA);
                    else delta = Math.Max(delta, -maxStepA);
                    after = before + delta;
                }
                else
                {
                    after = before;
                }

                if (after < minMarginA) after = minMarginA;
                if (after > maxMarginA) after = maxMarginA;

                _learnMargin = after;
                _peakTrace.Add(peakAmp);
                _marginTrace.Add(after);
            }

            _log?.Error(
                $"EPB[{_channel}] SafetyMargin 学习圈(legacy)：Imax={peakAmp:F3}A, err={err:+0.000;-0.000;0.000}A, " +
                $"Margin:{before:F3}→{after:F3}A（deadband={deadbandA:F2}, Kp={kp:F2}, step≤{maxStepA:F2}）",
                "EPB");
        }

        private void ApplySafetyMarginFromPeak_FreezeA20260101(double peakAmp)
        {
            // 计算误差：>0 偏高（欠切）→ 增大裕量；<0 偏低（过早）→ 减小裕量
            var err = peakAmp - _posThrA;
            var absErr = Math.Abs(err);

            double before, after;
            var freezeBefore = 0;
            var freezeAfter = 0;
            lock (_marginLock)
            {
                before = double.IsNaN(_learnMargin) ? (_safetyMarginA > 0 ? _safetyMarginA : 2.0) : _learnMargin;
                freezeBefore = _downAdjustFreezeCyclesLeft;

                if (absErr > MarginLearnDefaults.DeadbandA)
                {
                    double delta;
                    if (err > 0)
                    {
                        // 过冲：加大裕量，并启动“下调冻结”窗口
                        _downAdjustFreezeCyclesLeft = Math.Max(
                            _downAdjustFreezeCyclesLeft,
                            absErr >= MarginLearnDefaults.SplitErrA
                                ? MarginLearnDefaults.FreezeDownCyclesBig
                                : MarginLearnDefaults.FreezeDownCyclesSmall);

                        var maxStepUp = absErr >= MarginLearnDefaults.SplitErrA
                            ? MarginLearnDefaults.MaxStepUpBigA
                            : MarginLearnDefaults.MaxStepUpSmallA;
                        delta = MarginLearnDefaults.KpUp * err;
                        if (delta > maxStepUp) delta = maxStepUp;
                    }
                    else
                    {
                        var maxStepDown = absErr >= MarginLearnDefaults.SplitErrA
                            ? MarginLearnDefaults.MaxStepDownBigA
                            : MarginLearnDefaults.MaxStepDownSmallA;

                        // 欠冲：若处于“过冲后冻结窗口”，则只衰减 KpDown（不缩放 maxStepDown），
                        // 避免小幅欠冲拉回过快，同时允许大欠冲仍能较快回拉。
                        var scale = (_downAdjustFreezeCyclesLeft > 0) ? MarginLearnDefaults.FreezeDownScale : 1.0;
                        var kpDown = MarginLearnDefaults.KpDown * scale;

                        delta = kpDown * err;
                        if (delta < -maxStepDown) delta = -maxStepDown;
                    }

                    after = before + delta;
                }
                else
                {
                    after = before; // 死区内不调整
                }

                // 计数衰减：只要本圈没有发生过冲，就让冻结窗口向 0 收敛
                if (err <= MarginLearnDefaults.DeadbandA && _downAdjustFreezeCyclesLeft > 0)
                    _downAdjustFreezeCyclesLeft--;

                freezeAfter = _downAdjustFreezeCyclesLeft;

                // 夹紧边界
                if (after < MarginLearnDefaults.MinMarginA) after = MarginLearnDefaults.MinMarginA;
                if (after > MarginLearnDefaults.MaxMarginA) after = MarginLearnDefaults.MaxMarginA;

                _learnMargin = after;

                // 轨迹记录
                _peakTrace.Add(peakAmp);
                _marginTrace.Add(after);
            }

            var stepText = absErr >= MarginLearnDefaults.SplitErrA
                ? $"up≤{MarginLearnDefaults.MaxStepUpBigA:F2}/down≤{MarginLearnDefaults.MaxStepDownBigA:F2}"
                : $"up≤{MarginLearnDefaults.MaxStepUpSmallA:F2}/down≤{MarginLearnDefaults.MaxStepDownSmallA:F2}";

            var freezeText = (freezeBefore > 0 || freezeAfter > 0)
                ? $", freezeDown:{freezeBefore}→{freezeAfter}"
                : string.Empty;

            _log?.Error($"EPB[{_channel}] SafetyMargin 学习圈：Imax={peakAmp:F3}A, err={err:+0.000;-0.000;0.000}A, " +
                       $"Margin:{before:F3}→{after:F3}A（deadband={MarginLearnDefaults.DeadbandA:F2}, " +
                       $"KpUp={MarginLearnDefaults.KpUp:F2}, KpDown={MarginLearnDefaults.KpDown:F2}, {stepText}{freezeText}）", "EPB");
        }

        /// <summary>
        /// 结束一轮裕量学习：对 <see cref="_marginTrace"/> 做鲁棒统计，
        /// 以“最后 N 圈窗口 + MAD 去异常 + 中位数”得到最终裕量，并一次性写回 <see cref="_safetyMarginA"/>。
        /// 建议在批量学习结束后调用（与 FinalizeLearnAggregation 同期）。 
        /// </summary>
        public void FinalizeSafetyMarginLearning()
        {
            lock (_marginLock)
            {
                if (_marginTrace.Count == 0)
                {
                    _log?.Warn($"EPB[{_channel}] SafetyMargin 学习未产生样本，保持原值 {_safetyMarginA:F3}A。", "EPB");
                    return;
                }

                // 仅取“最后 N 圈”的窗口，避免早期粗调影响最终值
                var n = _marginTrace.Count;
                var win = Math.Max(1, Math.Min(MarginLearnDefaults.WindowForFinalize, n));
                var tail = _marginTrace.Skip(n - win).ToArray();

                // MAD 去异常（以窗口中位与绝对偏差的 1.4826*median 标准化后阈值过滤）
                var med = Median(tail);
                var absDev = tail.Select(x => Math.Abs(x - med)).ToArray();
                var mad = Median(absDev);
                var sigma = (mad <= 1e-9) ? 0 : 1.4826 * mad;
                double[] filtered;
                if (sigma <= 0)
                    filtered = tail; // 全部一致，直接用
                else
                    filtered = tail.Where(x => Math.Abs(x - med) <= MarginLearnDefaults.MadK * sigma).ToArray();

                var final = (filtered.Length > 0) ? Median(filtered) : med;

                // 写回 _safetyMarginA（一次性）
                var old = _safetyMarginA;
                _safetyMarginA = Clamp(final, MarginLearnDefaults.MinMarginA, MarginLearnDefaults.MaxMarginA);

                _log?.Info(
                    $"EPB[{_channel}] SafetyMargin 学习收敛：轨迹{_marginTrace.Count}圈，窗口后{win}圈，" +
                    $"MADσ≈{sigma:F3}，Final≈{_safetyMarginA:F3}A（原 {old:F3}A）。", "EPB");
            }
        }


        /// <summary>
        ///     正式单圈（对齐外壳版）。
        ///     外部使用 AlignToWallClock 计时器确保每圈在 (t0 + k*Period + phase) 触发；
        ///     此处负责：不做①；⑧中扣回 (tailBase - phase - lateness)；并在 deadline 前硬收尾。
        /// </summary>
        /// <param name="periodMs">目标周期（ms）。</param>
        /// <param name="tailBaseMs">尾段基准（旧T8 + 旧T1）。</param>
        /// <param name="phaseMs">本通道相位：索引×Δ。</param>
        /// <param name="tailMinMs">尾段最小保护（ms）。</param>
        /// <param name="deadlineUtc">本圈统一截止（UTC）。</param>
        /// <param name="token">取消令牌。</param>
        public async Task<bool> RunOneAlignedAsync(int periodMs, int tailBaseMs, int phaseMs, int tailMinMs,
            DateTime deadlineUtc, CancellationToken token)
        {
            var sw = Stopwatch.StartNew();

            // —— 1) 执行你现有“正式一圈” —— //
            // 如果你的旧方法是 RunOneAsync(periodMs, token[, ...])，此处直接调用即可。
            // 关键点：旧方法里若还有①/⑧的等待，不影响我们“外壳”收尾，后面的 deadline 仍会统一结束点。
            var ok = await RunOneAsync(periodMs, token).ConfigureAwait(false);

            // 正式完成事件由 EpbManager 在控制成功且圈数据可靠提交后发布。
            // 此处只能证明物理动作完成，不能提前增加 UI/检查点正式次数。

            // —— 2) 计算“迟到量”（lateness）：实际耗时 - (periodMs - tailBaseMs) —— //
            // 理解：假设旧流程内部已经用掉了 (periodMs - 旧T8) 的时间（粗略近似），我们在⑧中要扣回 phase，
            // 如果旧流程“忙得更久”，需要把迟到量计进⑧，避免超过 deadline。
            var elapsedMs = (int)sw.ElapsedMilliseconds;
            if (LastCycleOutcome != null)
                LastCycleOutcome.CallbackElapsedMs = elapsedMs;
            var expectedBeforeTail = periodMs - tailBaseMs;
            var lateness = Math.Max(0, elapsedMs - expectedBeforeTail);

            // —— 3) ⑧尾段扣回，T8_actual = max(tailMin, tailBase - phase - lateness) —— //
            var t8 = 0;
            if (EnableTailCompensation)
            {
                t8 = Math.Max(tailMinMs, tailBaseMs - phaseMs - lateness);
                if (t8 < 0) t8 = 0;
            }

            // —— 4) 以 deadline 为硬截止：若剩余时间 < t8，则压缩到“剩余时间”；若为负则直接收尾 —— //
            var remainToDeadline = (int)(deadlineUtc - DateTime.UtcNow).TotalMilliseconds;
            var sleepMs = Math.Min(t8, Math.Max(0, remainToDeadline));

            if (sleepMs > 0)
                try
                {
                    await Task.Delay(sleepMs, token).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    /* ignore */
                }

            return ok;
        }

        // ====== 说明 ======
        // 1) 此处调用的 LearnAsync / RunOneAsync 是你现有类中的方法签名。
        //    如果签名不同，请把这里改成你现有的一圈学习/一圈正式的入口。
        // 2) 我们不在这里对①做等待，因为“相位等待”由外部“锚点+相位”的启动时刻实现了；
        //    这样保证“每圈起点统一”，不会产生累积相位误差。
        // 3) ⑧尾部扣回除了 phase 还要考虑“迟到量”，并以 deadline 作为最终硬截止，确保“同圈统一收尾”。
    }
}
