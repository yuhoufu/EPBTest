using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IO.NI;
using ILogger = Config.IAppLogger;
using NLogger = Config.NullLogger;

namespace Controller
{
    /// <summary>
    /// EPB 单卡钳 自学习 + 周期控制
    /// ① 未上电 ② 正向上电涌流 ③ 正向空行程 ④ 夹紧拉升 ⑤ 断电保持 ⑥ 反向上电涌流 ⑦ 反向空行程 ⑧ 未上电
    /// </summary>
    public sealed class EpbCycleRunner
    {
        // 由上层提供：读电流 / DO 控制 / 液压控制
        public delegate double ReadCurrentDelegate(int epbChannel);

        private readonly int _channel;
        private readonly int _hydId;
        private readonly ReadCurrentDelegate _readCurrent;
        private readonly DoController _do;
        private readonly HydraulicController _hydraulic;
        private readonly ILogger _log;

        // —— 参数 —— //
        private readonly double _posThrA;      // ④ 夹紧判据（正向电流阈值）
        private readonly int _holdMs;          // ⑤ 断电保持
        private readonly int _sampleMs;        // 采样周期
        private readonly int _peakIgnoreMs;    // ②/⑥ 上电涌流忽略窗口
        private readonly double _ewmaAlpha;    // EWMA 系数
        private readonly double _emptyBandA;   // “空行程带”电流宽度
        private readonly int _stableWinMs;     // 进入“空行程带”需持续的最短时间

        // —— 学习得到的特征 —— //
        private double _tFwdPeakDecayMs;   // ②→③ 衰减时间
        private double _tFwdEmptyMs;       // ③ 正向空行程时间
        private double _tClampRampMs;      // ④ 拉升到目标阈值
        private double _tRevPeakDecayMs;   // ⑥→⑦ 衰减时间
        private double _tRevEmptyMs;       // ⑦ 反向空行程（用作释放定时）
        private double _iEmptyFwdA;        // +空行程电流（经验≈+0.5A）
        private double _iEmptyRevA;        // -空行程电流（经验≈-0.5A）

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

        // ===================  自 学 习  ===================
        /// <summary>
        /// 学习若干圈，估计②/③/④/⑥/⑦以及空行程电流，用中位数抑制离群。
        /// </summary>
        public async Task<bool> LearnAsync(int nCycles, CancellationToken token)
        {
            _log.Info($"EPB[{_channel}] 学习开始，次数={nCycles}", "EPB");
            var fwdPeakList = new List<double>();
            var fwdEmptyList = new List<double>();
            var clampRampList = new List<double>();
            var revPeakList = new List<double>();
            var revEmptyList = new List<double>();
            var iEmptyFList = new List<double>();
            var iEmptyRList = new List<double>();

            for (int k = 0; k < nCycles; k++)
            {
                token.ThrowIfCancellationRequested();

                // 0) 液压（可选）
                var hydOk = await _hydraulic.RunOnceAsync(_hydId, token);
                if (!hydOk)
                {
                    _log.Warn($"EPB[{_channel}] 学习第{k + 1}轮：液压未达成，跳过。", "EPB");
                    continue;
                }

                // ============ 正向 ============
                _do.SetEpbForward(_channel);
                _log.Info($"EPB[{_channel}] 学习{k + 1}：正向上电（忽略涌流{_peakIgnoreMs}ms）", "EPB");

                // 忽略涌流窗口 + 建EWMA
                await Task.Delay(_peakIgnoreMs, token);
                var t0 = Environment.TickCount;

                // 等待进入“空行程带”，并保持 stableWinMs
                var tPeakBegin = t0;
                var (okEmptyFwd, tEnterEmptyFwd, iEmptyFwd) =
                    await WaitStableAroundAsync(+0.5, +1, _emptyBandA, _stableWinMs, token);
                if (!okEmptyFwd)
                {
                    _do.SetEpbOff(_channel);
                    _log.Warn($"EPB[{_channel}] 学习{k + 1}：未判定到正向空行程，放弃本轮。", "EPB");
                    continue;
                }
                var tFwdPeakDecay = tEnterEmptyFwd - tPeakBegin;

                // 继续前进，直到电流 ≥ 阈值（记录空行程与拉升）
                var tEmptyStart = Environment.TickCount;
                var okClamp = await WaitCurrentAboveAsync(_posThrA, token);
                var tClampReach = Environment.TickCount;
                var tFwdEmpty = tClampReach - tEmptyStart;
                var tClampRamp = Math.Max(0, tClampReach - tEmptyStart);
                _do.SetEpbOff(_channel);

                // ⑤ 保持
                if (_holdMs > 0) await Task.Delay(_holdMs, token);

                // ============ 反向 ============
                _do.SetEpbReverse(_channel);
                _log.Info($"EPB[{_channel}] 学习{k + 1}：反向上电（忽略涌流{_peakIgnoreMs}ms）", "EPB");
                await Task.Delay(_peakIgnoreMs, token);
                var tR0 = Environment.TickCount;

                var (okEmptyRev, tEnterEmptyRev, iEmptyRev) =
                    await WaitStableAroundAsync(-0.5, -1, _emptyBandA, _stableWinMs, token);
                if (!okEmptyRev)
                {
                    _do.SetEpbOff(_channel);
                    _log.Warn($"EPB[{_channel}] 学习{k + 1}：未判定到反向空行程，放弃本轮。", "EPB");
                    continue;
                }

                var tRevPeakDecay = tEnterEmptyRev - tR0;
                var tRevEmpty = tEnterEmptyRev - tR0;

                _do.SetEpbOff(_channel);

                // 记录
                fwdPeakList.Add(tFwdPeakDecay);
                fwdEmptyList.Add(tFwdEmpty);
                clampRampList.Add(tClampRamp);
                revPeakList.Add(tRevPeakDecay);
                revEmptyList.Add(tRevEmpty);
                iEmptyFList.Add(iEmptyFwd);
                iEmptyRList.Add(iEmptyRev);

                _log.Info(
                    $"EPB[{_channel}] 学习{k + 1}：FwdPeak={tFwdPeakDecay}ms, FwdEmpty={tFwdEmpty}ms, " +
                    $"ClampRamp={tClampRamp}ms, RevPeak={tRevPeakDecay}ms, RevEmpty={tRevEmpty}ms, " +
                    $"I±empty≈({iEmptyFwd:F2},{iEmptyRev:F2})A", "EPB");
            }

            if (!fwdEmptyList.Any() || !revEmptyList.Any())
            {
                _log.Warn($"EPB[{_channel}] 学习失败：有效样本不足。", "EPB");
                return false;
            }

            // 中位数
            _tFwdPeakDecayMs = Median(fwdPeakList);
            _tFwdEmptyMs = Median(fwdEmptyList);
            _tClampRampMs = Median(clampRampList);
            _tRevPeakDecayMs = Median(revPeakList);
            _tRevEmptyMs = Median(revEmptyList);
            _iEmptyFwdA = Median(iEmptyFList);
            _iEmptyRevA = Median(iEmptyRList);

            _log.Info(
                $"EPB[{_channel}] 学习完成：FwdPeak={_tFwdPeakDecayMs}ms, FwdEmpty={_tFwdEmptyMs}ms, " +
                $"ClampRamp={_tClampRampMs}ms, RevPeak={_tRevPeakDecayMs}ms, RevEmpty={_tRevEmptyMs}ms, " +
                $"I±empty≈({_iEmptyFwdA:F2},{_iEmptyRevA:F2})A", "EPB");
            return true;
        }

        // ===================  正 式 运 行  ===================
        /// <summary>
        /// 运行一圈，力求贴合目标周期 targetPeriodMs。
        /// 液控时间会被测量并从目标周期中扣除。
        /// </summary>
        public async Task<bool> RunOneAsync(int targetPeriodMs, CancellationToken token)
        {
            try
            {
                // 0) 液压（可选）——测量用时
                var th0 = Environment.TickCount;
                var hydOk = await _hydraulic.RunOnceAsync(_hydId, token);
                var hydUsed = Environment.TickCount - th0; // 可能为0（液控关闭）
                if (!hydOk)
                {
                    _log.Warn($"EPB[{_channel}] 液压阶段未达成，跳过本轮。", "EPB");
                    return false;
                }

                // 分配给电控的预算
                var elecBudgetMs = Math.Max(0, targetPeriodMs - hydUsed);

                var tStart = Environment.TickCount;

                // —— 统计“刚性时长” —— （②③④ + ⑤ + ⑥⑦）
                var rigid =
                    _tFwdPeakDecayMs + _tFwdEmptyMs + _tClampRampMs +
                    _holdMs +
                    _tRevPeakDecayMs + _tRevEmptyMs;

                // ①/⑧用来凑整
                int tIdleHead = 0, tIdleTail = 0;
                if (elecBudgetMs > 0 && rigid < elecBudgetMs)
                {
                    var spare = elecBudgetMs - (int)rigid;
                    tIdleHead = spare / 2;
                    tIdleTail = spare - tIdleHead;
                }
                if (tIdleHead > 0) await Task.Delay(tIdleHead, token);

                // 2) 正向：上电→忽略涌流→判空行程→拉升到阈值→断电
                _do.SetEpbForward(_channel);
                _log.Info($"EPB[{_channel}] 正向开始。忽略涌流 {_peakIgnoreMs}ms", "EPB");
                await Task.Delay(_peakIgnoreMs, token);

                _ = await WaitStableAroundAsync(_iEmptyFwdA, +1, _emptyBandA, _stableWinMs, token);

                var okClamp = await WaitCurrentAboveAsync(_posThrA, token);
                if (!okClamp)
                {
                    _log.Warn($"EPB[{_channel}] 正向未达到阈值 {_posThrA}A。", "EPB");
                    _do.SetEpbOff(_channel);
                    return false;
                }
                _do.SetEpbOff(_channel);

                // 3) ⑤保持
                if (_holdMs > 0)
                {
                    _log.Info($"EPB[{_channel}] 保持 {_holdMs}ms。", "EPB");
                    await Task.Delay(_holdMs, token);
                }

                // 4) 反向：上电→忽略涌流→按“反向空行程时长”定时→断电
                _do.SetEpbReverse(_channel);
                _log.Info($"EPB[{_channel}] 反向开始。忽略涌流 {_peakIgnoreMs}ms", "EPB");
                await Task.Delay(_peakIgnoreMs, token);

                var tRevRun = Math.Max(10, (int)_tRevEmptyMs);
                _log.Info($"EPB[{_channel}] 反向按空行程时长运行 {tRevRun}ms 后断电。", "EPB");
                await Task.Delay(tRevRun, token);
                _do.SetEpbOff(_channel);

                // 5) ⑧ 未上电（尾部静默），使“电控阶段”总时长≈elecBudgetMs
                if (tIdleTail > 0)
                {
                    var elapsed = Environment.TickCount - tStart;
                    var remain = Math.Max(0, tIdleTail - Math.Max(0, elapsed - elecBudgetMs + tIdleTail));
                    if (remain > 0) await Task.Delay(remain, token);
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

        // ===================  工 具  ===================
        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

        private static double Median(IEnumerable<double> seq)
        {
            var arr = seq.OrderBy(x => x).ToArray();
            if (arr.Length == 0) return 0;
            int mid = arr.Length / 2;
            return (arr.Length % 2 == 1) ? arr[mid] : 0.5 * (arr[mid - 1] + arr[mid]);
        }

        private double ReadEwma(double prev, double cur) => prev + _ewmaAlpha * (cur - prev);

        /// <summary>
        /// 在目标电流 targetA 附近（±bandA）保持 stableWinMs 视为“进入空行程带”。
        /// sign=+1 表示正向（期待正小电流），sign=-1 表示反向（期待负小电流）。
        /// 返回：(ok, tEnterTick, 平均电流)
        /// </summary>
        private async Task<(bool ok, int tEnter, double iAvg)> WaitStableAroundAsync(
            double targetA, int sign, double bandA, int stableWinMs, CancellationToken token)
        {
            var wnd = Math.Max(_sampleMs, stableWinMs);
            var tBegin = Environment.TickCount;
            double ewma = ReadEwma(_readCurrent(_channel), _readCurrent(_channel));
            int inBandMs = 0;
            double sum = 0; int n = 0;

            while (true)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(_sampleMs, token);
                var raw = _readCurrent(_channel); // 保留符号
                ewma = ReadEwma(ewma, raw);
                var diff = Math.Abs(ewma - targetA);
                if (diff <= bandA && Math.Sign(ewma) == Math.Sign(targetA))
                {
                    inBandMs += _sampleMs;
                    sum += ewma; n++;
                    if (inBandMs >= wnd)
                        return (true, Environment.TickCount, (n > 0 ? sum / n : ewma));
                }
                else
                {
                    inBandMs = 0; sum = 0; n = 0;
                }
                if (Environment.TickCount - tBegin > 10_000)
                    return (false, 0, 0);
            }
        }

        /// <summary>等待电流（EWMA）≥指定阈值（④夹紧判据）。只用于正向。</summary>
        private async Task<bool> WaitCurrentAboveAsync(double thrA, CancellationToken token)
        {
            var ewma = ReadEwma(_readCurrent(_channel), _readCurrent(_channel));
            var tBegin = Environment.TickCount;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(_sampleMs, token);
                var raw = _readCurrent(_channel);
                ewma = ReadEwma(ewma, raw);
                if (ewma >= thrA) return true;
                if (Environment.TickCount - tBegin > 10_000) return false;
            }
        }
    }
}
