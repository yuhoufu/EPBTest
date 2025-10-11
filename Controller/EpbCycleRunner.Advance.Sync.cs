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