using System;
using System.Collections.Generic;
using System.Diagnostics;
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

        /// <summary>学习阶段：单圈结果样本。</summary>
        public sealed class LearnSample
        {
            public int TFwdPeakDecayMs;   // ②
            public int TFwdEmptyMs;       // ③ (actual)
            public int TClampRampMs;      // ④
            public int TRevPeakDecayMs;   // ⑥
            public int TRevEmptyMs;       // ⑦ (planned)
            public double IEmptyFwdA;     // I+empty
            public double IEmptyRevA;     // I-empty
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

        /// <summary>
        /// 学习单圈（对齐版核心）：不做①；②~⑦按旧逻辑测量；⑧不等待（交外壳）。
        /// 返回本圈测得的阶段耗时与空行程电流，用于跨圈聚合。
        /// </summary>
        public async Task<LearnSample> LearnOneAlignedCoreAsync(
            int periodMs, int tailBaseMs, int phaseMs, int tailMinMs, CancellationToken token)
        {
            // —— 进入建压（幂等） —— 
            if (_manager != null)
                await _manager.HydraulicEnterAsync(_channel, token).ConfigureAwait(false);

            // ② 正向
            _do.SetEpbForward(_channel);
            await Task.Delay(_peakIgnoreMs, token).ConfigureAwait(false);

            var r2 = await WaitStableAroundAsync(
                _iEmptyFwdA != 0 ? _iEmptyFwdA : +0.5, +1, _emptyBandA, _stableWinMs, token).ConfigureAwait(false);
            if (!r2.ok)
            {
                _do.SetEpbOff(_channel);
                return null; // 本圈失败，返回 null，不计入聚合
            }

            var tFwdPeakDecay = Math.Max(0, (int)r2.tEnter);

            // ③+④ 等待“夹紧判据”
            var coop = await WaitClampCoopAsync(r2.iAvg, token).ConfigureAwait(false);
            if (!coop.ok)
            {
                _do.SetEpbOff(_channel);
                return null;
            }

            // 正向达到判据 → 立即断电并标记释放
            _do.SetEpbOff(_channel);
            if (_manager != null)
                await _manager.HydraulicMarkReleaseAsync(_channel).ConfigureAwait(false);

            // 回溯 ③、④ 分界：离开空带上边界
            var tickPerMs = Stopwatch.Frequency / 1000.0;
            var enterTick = Stopwatch.GetTimestamp() - (long)((/*当前时刻*/0 + 0) /*无用占位*/);
            // 简化：复用旧实现的工具
            var emptyLeave = r2.iAvg + _emptyBandA;
            var rampStartTick = _currentBus[_channel].FindFirstUpCrossing(emptyLeave,
                Stopwatch.GetTimestamp() - (long)(r2.tEnter * tickPerMs));
            if (rampStartTick == null || rampStartTick.Value > coop.tick) rampStartTick = coop.tick;

            var tFwdEmpty = Math.Max(0, (int)((rampStartTick.Value - (Stopwatch.GetTimestamp() - (long)(r2.tEnter * tickPerMs))) * 1000.0 / Stopwatch.Frequency));
            var tClampRamp = Math.Max(0, (int)((coop.tick - rampStartTick.Value) * 1000.0 / Stopwatch.Frequency));

            // ⑤ 保持
            if (_holdMs > 0) await Task.Delay(_holdMs, token).ConfigureAwait(false);

            // ⑥ + ⑦ 反向
            _do.SetEpbReverse(_channel);
            await Task.Delay(_peakIgnoreMs, token).ConfigureAwait(false);

            var r6 = await WaitStableAroundAsync(
                _iEmptyRevA != 0 ? _iEmptyRevA : -0.5, -1, _emptyBandA, _stableWinMs, token).ConfigureAwait(false);
            if (!r6.ok)
            {
                _do.SetEpbOff(_channel);
                return null;
            }
            var tRevPeakDecay = Math.Max(0, (int)r6.tEnter);

            // ⑦：学习阶段我们按“计划定时”执行（与旧逻辑一致）
            // 计划 = 按 period 的柔性预算分配，但这里不再做①，且⑧由外壳收尾，
            // 因此我们仅给一个温和的定时（避免侵占过多尾段）
            var plan7 = Math.Max(10, (int)Math.Floor(0.3 * Math.Max(0, tailBaseMs)));
            await Task.Delay(plan7, token).ConfigureAwait(false);
            _do.SetEpbOff(_channel);

            // ⑧：学习单圈不等待（交给外壳对齐），故不做任何 Delay

            return new LearnSample
            {
                TFwdPeakDecayMs = tFwdPeakDecay,
                TFwdEmptyMs = tFwdEmpty,
                TClampRampMs = tClampRamp,
                TRevPeakDecayMs = tRevPeakDecay,
                TRevEmptyMs = plan7,
                IEmptyFwdA = r2.iAvg,
                IEmptyRevA = r6.iAvg
            };
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