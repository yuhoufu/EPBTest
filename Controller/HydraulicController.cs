using System;
using System.Threading;
using System.Threading.Tasks;
using Config;
using IO.NI;
using IAppLogger = Config.IAppLogger;
using NullLogger = Config.NullLogger;

namespace Controller
{
    /// <summary>
    /// 液压控制器：
    /// - 负责单路液压的启/停与到达判定；
    /// - 支持三种模式：ByPressure / ByDuration / Either；
    /// - 通过 AoController 输出“能力百分比”并由其内部转换为电压。
    /// </summary>
    public sealed class HydraulicController
    {
        private readonly DoController _do;
        private readonly TestConfig _test;
        private readonly Func<int, double> _readPressure;   // 读取压力：传入 hydId 返回 bar
        private readonly AoController _ao;                  // AO 控制器（统一做限幅与电压换算）
        private readonly IAppLogger _log;

        public HydraulicController(DoController doController,
                                   TestConfig test,
                                   Func<int, double> readPressure,
                                   AoController aoController,
                                   IAppLogger log = null)
        {
            _do = doController ?? throw new ArgumentNullException(nameof(doController));
            _test = test ?? throw new ArgumentNullException(nameof(test));
            _readPressure = readPressure ?? throw new ArgumentNullException(nameof(readPressure));
            _ao = aoController ?? throw new ArgumentNullException(nameof(aoController));
            _log = log ?? NullLogger.Instance;
        }

        /// <summary>
        /// 执行一次液压控制（按 TestConfig.Hydraulics 中的配置项）。
        /// hydId: 1/2（两路液压）
        /// </summary>
        public async Task<bool> RunOnceAsync(int hydId, CancellationToken token)
        {
            var item = _test.Hydraulics.Find(h => h.Id == hydId);
            if (item == null || !item.Enabled)
            {
                _log.Info($"液压[{hydId}] 跳过（未启用）。", "液压");
                return true;
            }

            // 选择 AO 设备名：如有配置项，可在此改为从配置读取
            // 与 AoConfig.Devices 中的 Name 对应即可（例如 "Cylinder1" / "Cylinder2"）
            string aoDevName = hydId == 1 ? "Cylinder1" : "Cylinder2";

            try
            {
                // Step 1: 打开压力 DO
                if (!_do.SetPressure(hydId, true))
                {
                    _log.Error($"液压[{hydId}] DO 打开失败。", "液压");
                    return false;
                }

                _log.Info($"液压[{hydId}] 启动，设定={item.SetPercent:F1}% 模式={item.Mode}", "液压");

                // Step 2: 输出 AO 百分比（由 AoController 内部做限幅与电压换算）
                // 同步版：WritePercent；如需无阻塞可改用 await _ao.SetPercentAsync(...)
                if (!_ao.WritePercent(aoDevName, item.SetPercent))
                {
                    _log.Error($"液压[{hydId}] AO 输出失败（设备={aoDevName} 百分比={item.SetPercent:F1}%）。", "液压");
                    return false;
                }

                var tStart = DateTime.Now;
                bool reached = false;

                // Step 3: 轮询判定：压力/时间/Either
                while (!token.IsCancellationRequested)
                {
                    double elapsedMs = (DateTime.Now - tStart).TotalMilliseconds;
                    double pBar = _readPressure(hydId);

                    switch (item.Mode)
                    {
                        case HydraulicMode.ByPressure:
                            if (pBar >= item.PressureThresholdBar) reached = true;
                            break;

                        case HydraulicMode.ByDuration:
                            if (elapsedMs >= item.DurationMs) reached = true;
                            break;

                        case HydraulicMode.Either:
                            if (pBar >= item.PressureThresholdBar || elapsedMs >= item.DurationMs) reached = true;
                            break;
                    }

                    if (reached)
                    {
                        _log.Info($"液压[{hydId}] 达到条件：P={pBar:F2} bar, t={elapsedMs:F0} ms", "液压");
                        break;
                    }

                    await Task.Delay(5, token); // 减少 CPU 忙等
                }

                // Step 4: 达到后保持
                if (!token.IsCancellationRequested && item.HoldAfterReachedMs > 0)
                {
                    _log.Info($"液压[{hydId}] 延时保持 {item.HoldAfterReachedMs} ms", "液压");
                    await Task.Delay(item.HoldAfterReachedMs, token);
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                _log.Warn($"液压[{hydId}] 被取消。", "液压");
                return false;
            }
            catch (Exception ex)
            {
                _log.Error($"液压[{hydId}] 异常：{ex.Message}", "液压", ex);
                return false;
            }
            finally
            {
                // Step 5: 关闭压力 DO；AO 回零百分比（落位）
                try { _do.SetPressure(hydId, false); } catch { /* 忽略落位异常 */ }
                try { _ao.WritePercent(aoDevName, 0); } catch { /* 忽略落位异常 */ }

                _log.Info($"液压[{hydId}] 停止并回零。", "液压");
            }
        }
    }
}
