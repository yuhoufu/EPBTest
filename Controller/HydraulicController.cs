using System;
using System.Collections.Concurrent;
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
        private readonly Func<int, double> _readPressure; // 读取压力：传入 hydId 返回 bar
        private readonly AoController _ao; // AO 控制器（统一做限幅与电压换算）
        private readonly IAppLogger _log;

        //等待“释压”信号的表：key=hydId
        private readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> _releaseWaiters = new();

        private readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> _holdTcs
            = new();


        /// <summary>外部直接请求释压；返回 true 表示本次请求有效（成功触发）。</summary>
        public bool RequestRelease(int hydId)
        {
            if (_releaseWaiters.TryGetValue(hydId, out var tcs))
            {
                var ok = tcs.TrySetResult(true);
                if (ok) _log.Info($"液压[{hydId}] 收到外部释放请求。", "液压");
                return ok;
            }

            return false; // 当前没有处于保持等待的会话
        }

        /// <summary>获取可供外部保存/传递的“释压委托”。</summary>
        public bool TryGetReleaseDelegate(int hydId, out Func<Task> releaseAsync)
        {
            if (_releaseWaiters.TryGetValue(hydId, out var tcs))
            {
                releaseAsync = () =>
                {
                    if (tcs.TrySetResult(true))
                        _log.Info($"液压[{hydId}] 通过委托触发释放。", "液压");
                    return Task.CompletedTask;
                };
                return true;
            }

            releaseAsync = null;
            return false;
        }


        /// <summary>建压并保持，直到调用 Release(hydId) 或 token 取消。</summary>
        public async Task<bool> BuildAndHoldAsync(int hydId, CancellationToken token)
        {
            var item = _test.Hydraulics.Find(h => h.Id == hydId);
            if (item == null || !item.Enabled) return true;

            var aoDevName = hydId == 1 ? "Cylinder1" : "Cylinder2";
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_holdTcs.TryAdd(hydId, tcs)) // 已在保持，直接复用
                return true;

            try
            {
                if (!_do.SetPressure(hydId, true))
                {
                    _log.Error($"液压[{hydId}] DO 打开失败。", "液压");
                    _holdTcs.TryRemove(hydId, out _);
                    return false;
                }

                if (!_ao.WritePercent(aoDevName, item.SetPercent))
                {
                    _log.Error($"液压[{hydId}] AO 输出失败。", "液压");
                    _do.SetPressure(hydId, false);
                    _holdTcs.TryRemove(hydId, out _);
                    return false;
                }

                _log.Info($"液压[{hydId}] 建压并保持：{item.SetPercent:F1}%（HoldUntilRelease）", "液压");

                using var reg = token.Register(() => tcs.TrySetCanceled(token));
                await tcs.Task; // 等待外部 Release()
                return true;
            }
            catch (OperationCanceledException)
            {
                _log.Warn($"液压[{hydId}] 保持被取消。", "液压");
                return false;
            }
            catch (Exception ex)
            {
                _log.Error($"液压[{hydId}] 保持异常：{ex.Message}", "液压", ex);
                return false;
            }
            finally
            {
                // 统一落位
                try
                {
                    _do.SetPressure(hydId, false);
                }
                catch
                {
                }

                try
                {
                    _ao.WritePercent(aoDevName, 0);
                }
                catch
                {
                }

                _holdTcs.TryRemove(hydId, out _);
                _log.Info($"液压[{hydId}] 已释压回零。", "液压");
            }
        }

        /// <summary>外部释放保持的液压。</summary>
        public void Release(int hydId)
        {
            if (_holdTcs.TryGetValue(hydId, out var tcs))
                tcs.TrySetResult(true);
        }


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
            var aoDevName = hydId == 1 ? "Cylinder1" : "Cylinder2";

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
                var reached = false;

                // Step 3: 轮询判定：压力/时间/Either
                while (!token.IsCancellationRequested)
                {
                    var elapsedMs = (DateTime.Now - tStart).TotalMilliseconds;
                    var pBar = _readPressure(hydId);

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


                        case HydraulicMode.HoldUntilRelease: // 新模式：建压判定仍然按阈值/时长逻辑
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

                // === 新增：保持直到外部“释压” ===
                if (!token.IsCancellationRequested && item.Mode == HydraulicMode.HoldUntilRelease)
                {
                    // 注册等待对象（允许外部通过 RequestRelease / 委托触发）
                    var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (!_releaseWaiters.TryAdd(hydId, tcs)) _log.Warn($"液压[{hydId}] 已存在未释放的保持会话，避免重复进入保持。", "液压");

                    _log.Info($"液压[{hydId}] 进入保持，等待外部释放（DO、AO 持续保持）。", "液压");

                    try
                    {
                        // 等待：外部释放 或 取消
                        var cancelTask = Task.Delay(Timeout.Infinite, token);
                        var completed = await Task.WhenAny(tcs.Task, cancelTask);
                        if (completed == cancelTask)
                            token.ThrowIfCancellationRequested();
                        else
                            _log.Info($"液压[{hydId}] 已由外部触发释放。", "液压");
                    }
                    finally
                    {
                        _releaseWaiters.TryRemove(hydId, out _);
                    }

                    // 直接 return true 让 finally 去做落位
                    return true;
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
                try
                {
                    _do.SetPressure(hydId, false);
                }
                catch
                {
                    /* 忽略落位异常 */
                }

                try
                {
                    _ao.WritePercent(aoDevName, 0);
                }
                catch
                {
                    /* 忽略落位异常 */
                }

                _log.Info($"液压[{hydId}] 停止并回零。", "液压");
            }
        }
    }
}