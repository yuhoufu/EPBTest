using System;
using System.Threading;
using System.Threading.Tasks;
using Config;
using IO.NI;
// Epb/EpbCycleRunner.cs
using IAppLogger = Config.IAppLogger;
using NullLogger = Config.NullLogger; // DoController

namespace Controller
{
    /// <summary>
    /// 单卡钳完整控制循环：先液压（可选、按液压编号整体控制），再电控。
    /// 电控逻辑：DO 正向→监测电流≥正阈→DO 反向→监测电流≥反阈→双关。
    /// </summary>
    public sealed class EpbCycleRunner
    {
        public delegate double ReadCurrentDelegate(int epbChannel); // 读取实时电流（A）

        private readonly int _channel;
        private readonly int _hydraulicId; // 1: EPB 1-6；2: EPB 7-12
        private readonly EpbLimit _limit;
        private readonly DoController _do;
        private readonly HydraulicController _hydraulic;
        private readonly ReadCurrentDelegate _readCurrent;
        private readonly IAppLogger _log;

        public EpbCycleRunner(int epbChannel,
            int hydraulicId,
            EpbLimit limit,
            DoController doController,
            HydraulicController hydraulic,
            ReadCurrentDelegate readCurrent,
            IAppLogger log = null)
        {
            _channel = epbChannel;
            _hydraulicId = hydraulicId;
            _limit = limit;
            _do = doController;
            _hydraulic = hydraulic;
            _readCurrent = readCurrent;
            _log = log ?? NullLogger.Instance;
        }

        /// <summary>
        /// 运行一次“先液压后电控”的完整循环。
        /// </summary>
        public async Task<bool> RunOneAsync(CancellationToken token)
        {
            try
            {
                // Step A) 液压（按编号整体开关；如果禁用则 RunOnceAsync 直接返回 true）
                if (!await _hydraulic.RunOnceAsync(_hydraulicId, token))
                {
                    _log.Error($"EPB[{_channel}] 液压步骤失败。", "EPB");
                    return false;
                }

                // Step B) 电控 - 正向
                if (!_do.SetEpb(_channel, true))
                {
                    _log.Error($"EPB[{_channel}] 正向 DO 写入失败。", "EPB");
                    return false;
                }

                _log.Info($"EPB[{_channel}] 正向启动。阈值={_limit.ForwardA} A", "EPB");

                // 即时监控电流到正向阈值
                while (!token.IsCancellationRequested)
                {
                    var a = _readCurrent(_channel);
                    if (a >= _limit.ForwardA)
                    {
                        _log.Info($"EPB[{_channel}] 正向到达阈值：{a:F2} A", "EPB");
                        break;
                    }

                    await Task.Delay(1, token); // 尽量小的轮询间隔
                }

                // Step C) 电控 - 反向
                if (!_do.SetEpb(_channel, false))
                {
                    _log.Error($"EPB[{_channel}] 反向 DO 写入失败。", "EPB");
                    return false;
                }

                _log.Info($"EPB[{_channel}] 切换反向。阈值={_limit.ReverseA} A", "EPB");

                while (!token.IsCancellationRequested)
                {
                    var a = _readCurrent(_channel);
                    if (a >= _limit.ReverseA)
                    {
                        _log.Info($"EPB[{_channel}] 反向到达阈值：{a:F2} A", "EPB");
                        break;
                    }

                    await Task.Delay(1, token);
                }

                // Step D) 关闭（正/反同时关闭）
                if (!_do.SetEpbOff(_channel))
                {
                    _log.Error($"EPB[{_channel}] 关闭 DO 失败。", "EPB");
                    return false;
                }

                _log.Info($"EPB[{_channel}] 单循环完成（正/反全关）。", "EPB");

                return true;
            }
            catch (OperationCanceledException)
            {
                _log.Warn($"EPB[{_channel}] 循环被取消。", "EPB");
                return false;
            }
            catch (Exception ex)
            {
                _log.Error($"EPB[{_channel}] 循环异常：{ex.Message}", "EPB", ex);
                return false;
            }
        }
    }
}