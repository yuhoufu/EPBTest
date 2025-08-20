using System;
using System.Threading;
using System.Threading.Tasks;
using IO.NI;
using ILogger = Config.IAppLogger;
using NLogger = Config.NullLogger;

namespace Controller
{
    /// <summary>
    /// 单卡钳完整控制循环：
    /// 1) 可选先跑液压（按 hydId 维度整体控制，外部保证同 hydId 的通道同策略）；
    /// 2) 然后执行电控：正向→等电流≥正阈→反向→等电流≥反阈→双关→保持。
    /// </summary>
    public sealed class EpbCycleRunner
    {
        // —— 对接 AI 读取 —— //
        public delegate double ReadCurrentDelegate(int epbChannel);

        private readonly int _channel;
        private readonly int _hydId;
        private readonly ReadCurrentDelegate _readCurrent;
        private readonly DoController _do;
        private readonly HydraulicController _hydraulic;
        private readonly ILogger _log;

        // —— 参数化的阈值与时序 —— //
        private readonly double _posThrA;
        private readonly double _negThrA;
        private readonly int _fwdTimeoutMs;
        private readonly int _revTimeoutMs;
        private readonly int _holdMs;

        public EpbCycleRunner(
            int channel,
            int hydId,
            ReadCurrentDelegate readCurrent,
            DoController doController,
            HydraulicController hydraulic,
            double posThresholdA,
            double negThresholdA,
            int forwardTimeoutMs,
            int reverseTimeoutMs,
            int holdMs,
            ILogger log = null)
        {
            _channel = channel;
            _hydId = hydId;
            _readCurrent = readCurrent ?? (_ => 0.0);
            _do = doController ?? throw new ArgumentNullException(nameof(doController));
            _hydraulic = hydraulic ?? throw new ArgumentNullException(nameof(hydraulic));
            _posThrA = posThresholdA;
            _negThrA = negThresholdA;
            _fwdTimeoutMs = forwardTimeoutMs;
            _revTimeoutMs = reverseTimeoutMs;
            _holdMs = holdMs;
            _log = log ?? NLogger.Instance;
        }

        public async Task<bool> RunOneAsync(CancellationToken token)
        {
            try
            {
                // Step 0: 液压（由 HydraulicController 自己决定按压力/时长/择一）
                var hydOk = await _hydraulic.RunOnceAsync(_hydId, token);
                if (!hydOk)
                {
                    _log.Warn($"EPB[{_channel}] 液压阶段未达成，跳过电控本轮。", "EPB");
                    return false;
                }

                // Step 1: 正向
                _do.SetEpbForward(_channel);
                _log.Info($"EPB[{_channel}] 正向开始，等待电流≥{_posThrA}A。", "EPB");
                if (!await WaitCurrentAsync(_channel, _posThrA, _fwdTimeoutMs, token))
                {
                    _log.Warn($"EPB[{_channel}] 正向未在 {_fwdTimeoutMs}ms 内达阈值。", "EPB");
                    _do.SetEpbOff(_channel);
                    return false;
                }

                // Step 2: 反向
                _do.SetEpbReverse(_channel);
                _log.Info($"EPB[{_channel}] 反向开始，等待电流≥{_negThrA}A。", "EPB");
                if (!await WaitCurrentAsync(_channel, _negThrA, _revTimeoutMs, token))
                {
                    _log.Warn($"EPB[{_channel}] 反向未在 {_revTimeoutMs}ms 内达阈值。", "EPB");
                    _do.SetEpbOff(_channel);
                    return false;
                }

                // Step 3: 双关 → 保持
                _do.SetEpbOff(_channel);
                if (_holdMs > 0)
                {
                    _log.Info($"EPB[{_channel}] 进入保持 {_holdMs}ms。", "EPB");
                    await Task.Delay(_holdMs, token);
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                _log.Warn($"EPB[{_channel}] 循环被取消。", "EPB");
                _do.SetEpbOff(_channel);
                return false;
            }
            catch (Exception ex)
            {
                _log.Error($"EPB[{_channel}] 循环异常：{ex.Message}", "EPB", ex);
                _do.SetEpbOff(_channel);
                return false;
            }
        }

        private async Task<bool> WaitCurrentAsync(int channel, double thrA, int timeoutMs, CancellationToken token)
        {
            var start = Environment.TickCount;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var i = _readCurrent(channel);
                if (i >= thrA) return true;

                if (Environment.TickCount - start > timeoutMs) return false;
                await Task.Delay(2, token); // 2ms 轮询
            }
        }
    }
}
