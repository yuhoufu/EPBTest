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
        private int RevDecayRigidMaxMs = 120; // 建议现场可配：80~150ms

        /// <summary>
        /// 反向固定空行程时长（ms）。不再用“空行程值+带宽”判据，仅做固定时间推进。
        /// </summary>
        private int RevEmptyFixedMs = 1000; // 你提出的默认值

        /// <summary>
        /// 反向电流“衰减限值”（A）。用于判定“峰值已衰减到足够低”
        /// 典型值：3A（可按被测件与电源特性微调）
        /// </summary>
        private double RevDecayLimitA = 3.0;
        #endregion


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
        public async Task<LearnSample> LearnOneAlignedCoreAsync(
            int periodMs, int tailBaseMs, int phaseMs, int tailMinMs, CancellationToken token)
        {
            // —— 进入液压建压（与正式阶段保持一致；若无需求可保持幂等）——
            if (_manager != null)
                await _manager.HydraulicEnterAsync(_channel, token).ConfigureAwait(false);

            // ===================== 正向阶段（②~④ 合并为“直接夹紧判据”） =====================
            // ② 正向上电并忽略涌流去抖
            _do.SetEpbForward(_channel);
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
                _do.SetEpbOff(_channel);
                _log?.Warn($"EPB[{_channel}] 正向阶段未满足夹紧判据，放弃本圈样本。", "EPB");
                return null;
            }

            // —— 达到夹紧判据 → 立即断电，并通知协调器（用于统一释压）——
            _do.SetEpbOff(_channel);
            if (_manager != null)
                await _manager.HydraulicMarkReleaseAsync(_channel).ConfigureAwait(false);

            _log?.Info(
                $"EPB[{_channel}] 正向阶段完成：PeakIgnore={tFwdPeakDecayMs}ms, JudgeElapsed≈{fwdJudgeElapsedMs}ms（②~④已合并）。", "EPB");

            // ===================== ⑤ 保持 =====================
            if (_holdMs > 0)
                await Task.Delay(_holdMs, token).ConfigureAwait(false);

            // ===================== 反向阶段（⑥ 刚性衰减 + ⑦ 固定空行程） =====================
            // ⑥ 反向上电并忽略涌流去抖
            _do.SetEpbReverse(_channel);
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

                var now = Stopwatch.GetTimestamp();
                if (now < nextDue)
                {
                    // 紧凑对齐采样节拍：先轻量延时（毫秒级），再自旋等待最后几个微小 tick
                    var ms = (int)Math.Max(0, (nextDue - now) / tickPerMs - 1);
                    if (ms > 0) await Task.Delay(ms, token).ConfigureAwait(false);
                    while ((now = Stopwatch.GetTimestamp()) < nextDue) { /* busy wait to align */ }
                }
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
            _do.SetEpbOff(_channel);

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

            // —— 2) 计算“迟到量”（lateness）：实际耗时 - (periodMs - tailBaseMs) —— //
            // 理解：假设旧流程内部已经用掉了 (periodMs - 旧T8) 的时间（粗略近似），我们在⑧中要扣回 phase，
            // 如果旧流程“忙得更久”，需要把迟到量计进⑧，避免超过 deadline。
            var elapsedMs = (int)sw.ElapsedMilliseconds;
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