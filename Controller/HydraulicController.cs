using System;
using System.Threading;
using System.Threading.Tasks;
using Config;
using IO.NI;
using IAppLogger = Config.IAppLogger;
using NullLogger = Config.NullLogger;

// Hydraulic/HydraulicController.cs

// 使用你的 DoController

namespace Controller
{
    /// <summary>
    /// 液压控制器：三种控制模式（到压/到时/任一满足），支持“达阈保持时长再泄压”。
    /// 按液压编号整体控制 6 个卡钳，是否启用在 TestConfig.Hydraulics 中统一开/关。
    /// </summary>
    public sealed class HydraulicController
    {
        public delegate double ReadPressureDelegate(int hydraulicId); // 读取实时压力（Bar）
        public delegate Task<bool> SetAoPercentDelegate(int hydraulicId, double percent); // 设置 AO 百分比

        private readonly DoController _do;                       // 操作液压 DO 开/关
        private readonly TestConfig _test;
        private readonly IAppLogger _log;
        private readonly ReadPressureDelegate _readPressure;
        private readonly SetAoPercentDelegate _setAo;

        public HydraulicController(DoController doController,
                                   TestConfig test,
                                   ReadPressureDelegate readPressure,
                                   SetAoPercentDelegate setAo,
                                   IAppLogger log = null)
        {
            _do = doController;
            _test = test;
            _readPressure = readPressure;
            _setAo = setAo;
            _log = log ?? NullLogger.Instance;
        }

        /// <summary>
        /// 执行一次液压循环（如果当前液压未启用，直接返回 true）。
        /// </summary>
        /// <param name="hydraulicId">液压编号（1 控 1~6；2 控 7~12）</param>
        /// <param name="token">取消令牌</param>
        public async Task<bool> RunOnceAsync(int hydraulicId, CancellationToken token)
        {
            var cfg = _test.Hydraulics.Find(h => h.Id == hydraulicId);
            if (cfg == null || !cfg.Enabled) return true;

            // 1) AO 置百分比
            if (!await _setAo(hydraulicId, cfg.SetPercent))
            {
                _log.Error($"液压{hydraulicId} AO 设置百分比失败：{cfg.SetPercent}%", "Hydraulic");
                return false;
            }
            _log.Info($"液压{hydraulicId} AO={cfg.SetPercent}%", "Hydraulic");

            // 2) 打开 DO（上压）
            if (!_do.SetPressure(cfg.PressureDoId, true))
            {
                _log.Error($"液压{hydraulicId} 打开 DO 失败（Id={cfg.PressureDoId}）", "Hydraulic");
                return false;
            }
            _log.Info($"液压{hydraulicId} DO 打开", "Hydraulic");

            // 3) 监控达到条件（到压/到时/任一满足）
            var t0 = DateTime.UtcNow;
            bool byPressure = false, byDuration = false;

            while (!token.IsCancellationRequested)
            {
                // 到压检测
                if (cfg.Mode != HydraulicMode.ByDuration)
                {
                    var p = _readPressure(hydraulicId);
                    if (p >= cfg.PressureThresholdBar)
                    {
                        byPressure = true;
                        _log.Info($"液压{hydraulicId} 达到压力阈值 {p:F2} >= {cfg.PressureThresholdBar} Bar", "Hydraulic");
                    }
                }

                // 到时检测
                if (cfg.Mode != HydraulicMode.ByPressure)
                {
                    var dur = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
                    if (dur >= cfg.DurationMs)
                    {
                        byDuration = true;
                        _log.Info($"液压{hydraulicId} 达到时长阈值 {dur} >= {cfg.DurationMs} ms", "Hydraulic");
                    }
                }

                bool shouldStop = cfg.Mode switch
                {
                    HydraulicMode.ByPressure => byPressure,
                    HydraulicMode.ByDuration => byDuration,
                    HydraulicMode.Either => byPressure || byDuration,
                    _ => false
                };

                if (shouldStop) break;
                await Task.Delay(5, token); // 5ms 轮询，保证“即时性”
            }

            // 4) 达阈后的保持/延时
            if (cfg.HoldAfterReachedMs > 0 && !token.IsCancellationRequested)
            {
                _log.Info($"液压{hydraulicId} 达阈后保持 {cfg.HoldAfterReachedMs}ms", "Hydraulic");
                await Task.Delay(cfg.HoldAfterReachedMs, token);
            }

            // 5) 关闭 DO（泄压）
            if (!_do.SetPressure(cfg.PressureDoId, false))
            {
                _log.Error($"液压{hydraulicId} 关闭 DO 失败（Id={cfg.PressureDoId}）", "Hydraulic");
                return false;
            }
            _log.Info($"液压{hydraulicId} DO 关闭（泄压）", "Hydraulic");

            // 按你的需求：AO 不必置 0，试验结束统一置 0。
            return true;
        }
    }
}
