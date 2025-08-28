// ====================== HydraulicGroupCoordinator.cs（内联到 EpbManager.cs）======================
// 目的：统一管理“组内首个电控前建压；最后一个电压释放点后释放液压”的跨卡钳节拍。
// 依赖：TestConfig.Hydraulics[*].Members 列出该液压控制的所有卡钳通道号；
//       若 Members 为空，则回退到 DOConfig 中每个 EPB Record 的可选 HydraulicId 关联。
// 线程安全：内部用 ConcurrentDictionary + 锁；可同时被多路 EpbCycleRunner 调用。

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Config;
using IO.NI;
using IAppLogger = Config.IAppLogger;
using NullLogger = Config.NullLogger;

namespace Controller
{
    internal sealed class HydraulicGroupCoordinator
    {
        private readonly AoController _ao;

        // channel -> hydId（优先 TestConfig.Hydraulics[*].Members；其次 DOConfig.EPB[*].HydraulicId）
        private readonly Dictionary<int, int> _channel2Hyd = new();
        private readonly DoController _do;
        private readonly DoConfig _doCfg;

        // 可选：引用已有液压控制器。若存在则优先用它的“保持-外部释放”能力。
        // 注意：我们改成使用你现有的 HydraulicController.BuildAndHoldAsync / Release / TryGetReleaseDelegate
        private readonly HydraulicController _hydCtl;

        private readonly ConcurrentDictionary<int, Latch> _latches = new();
        private readonly IAppLogger _log;
        private readonly Func<int, double> _readPressure;
        private readonly TestConfig _test;

        public HydraulicGroupCoordinator(TestConfig test,
            DoConfig dO,
            DoController doController,
            Func<int, double> readPressure,
            AoController aoController,
            HydraulicController hydCtl,
            IAppLogger log)
        {
            _test = test ?? throw new ArgumentNullException(nameof(test));
            _doCfg = dO ?? new DoConfig();
            _do = doController ?? throw new ArgumentNullException(nameof(doController));
            _ao = aoController; // 可空：无 AO 时仍可只用 HydraulicController
            _readPressure = readPressure; // 可空：若仅用 HydraulicController，可不需要读压
            _hydCtl = hydCtl; // 可空：无专用控制器则走 Fallback
            _log = log ?? NullLogger.Instance;

            // 1) 先用 TestConfig.Hydraulics[*].Members 做映射
            foreach (var h in _test.Hydraulics)
            {
                if (h?.Enabled != true) continue;
                if (h.Members != null && h.Members.Count > 0)
                    foreach (var ch in h.Members.Distinct())
                        _channel2Hyd[ch] = h.Id;
            }

            // 2) 对未覆盖的通道，用 DOConfig.EPB 的 HydraulicId 兜底
            if (_doCfg?.Epb != null)
                foreach (var r in _doCfg.Epb)
                    if (r.Enabled && r.Channel > 0 && r.HydraulicId.HasValue && !_channel2Hyd.ContainsKey(r.Channel))
                        _channel2Hyd[r.Channel] = r.HydraulicId.Value;
        }

        /// <summary>
        ///     ★ 接入点（上电前调用）：声明“我这个通道要开始电控了”，若该组还没建压则先建压。
        ///     关键改动：不再调用不存在的 BeginHoldAsync，而是：
        ///     - 若有 _hydCtl：启动 BuildAndHoldAsync(hydId, token)，并把 ReleaseAction 绑定为 _hydCtl.Release(hydId)
        ///     - 否则：使用 Fallback DO/AO + 读压保持，到统一释放时撤销
        /// </summary>
        public async Task EnterElectricalPhaseAsync(int epbChannel, CancellationToken token)
        {
            if (!_channel2Hyd.TryGetValue(epbChannel, out var hydId))
            {
                _log.Warn($"EPB[{epbChannel}] 未映射到液压，跳过建压逻辑。", "液压协调");
                return;
            }

            var latch = _latches.GetOrAdd(hydId, _ => new Latch());

            lock (latch.Gate)
            {
                latch.InFlight.Add(epbChannel);
            }

            bool needBuild;
            lock (latch.Gate)
            {
                needBuild = !latch.PressureOn;
            }

            if (!needBuild) return;

            try
            {
                lock (latch.Gate)
                {
                    latch.PressureOn = true;
                }

                if (_hydCtl != null)
                {
                    // 方式A：优先用你已有的 HydraulicController —— BuildAndHoldAsync + Release
                    // BuildAndHoldAsync 会：DO 打开 + AO 输出到设定百分比，并保持，直到 Release(hydId) 或 token 取消。
                    // 我们在后台开一个保持任务；ReleaseAction 直接调用 _hydCtl.Release(hydId)。
                    latch.ReleaseActionAsync = async () =>
                    {
                        try
                        {
                            _hydCtl.Release(hydId);
                        }
                        catch
                        {
                            /* 忽略异常 */
                        }

                        await Task.CompletedTask;
                    };

                    // 异步起保持任务（不阻塞 EPB 的电控流程）
                    _ = Task.Run(() => _hydCtl.BuildAndHoldAsync(hydId, token), token);

                    _log.Info($"液压[{hydId}] 进入保持（HydraulicController.BuildAndHoldAsync）。", "液压协调");

                    // 兼容 RunOnceAsync(HoldUntilRelease) 的“委托释放”用法（可选）：
                    // 若你的业务在别处以 RunOnceAsync(HoldUntilRelease) 启动保持，这里也尝试获取一次释放委托。
                    if (_hydCtl.TryGetReleaseDelegate(hydId, out var rel))
                    {
                        // 如果拿到了委托，优先用控制器的委托（写入日志方便排查）
                        latch.ReleaseActionAsync = rel;
                        _log.Info($"液压[{hydId}] 获取到 TryGetReleaseDelegate 的释放委托。", "液压协调");
                    }
                }
                else
                {
                    // 方式B：回退方案 —— 直接 DO/AO + 读压保持，直到统一释放时取消
                    if (_ao == null || _readPressure == null)
                    {
                        _log.Warn($"液压[{hydId}] 无法进入保持：缺少 AO 或 读压方法。", "液压协调");
                        lock (latch.Gate)
                        {
                            latch.PressureOn = false;
                        }

                        return;
                    }

                    latch.Cts = new CancellationTokenSource();
                    _ = Task.Run(() => FallbackHoldLoopAsync(hydId, latch.Cts.Token), latch.Cts.Token);
                    latch.ReleaseActionAsync = async () =>
                    {
                        try
                        {
                            latch.Cts?.Cancel();
                        }
                        catch
                        {
                        }

                        await Task.Delay(10);
                        try
                        {
                            _do.SetPressure(hydId, false);
                        }
                        catch
                        {
                        }

                        try
                        {
                            var dev = hydId == 1 ? "Cylinder1" : "Cylinder2";
                            _ao.WritePercent(dev, 0);
                        }
                        catch
                        {
                        }
                    };
                    _log.Info($"液压[{hydId}] 进入保持（Fallback）。", "液压协调");
                }
            }
            catch (Exception ex)
            {
                lock (latch.Gate)
                {
                    latch.PressureOn = false;
                }

                _log.Error($"液压[{hydId}] 建压保持失败：{ex.Message}", "液压协调", ex);
                throw;
            }
        }

        /// <summary>
        ///     ★ 接入点（单通道到达“电压释放点”时调用）：
        ///     若这是该液压组内最后一个待释放的通道，则统一释放液压（调用 ReleaseAction）。
        /// </summary>
        public async Task MarkVoltageReleaseAsync(int epbChannel)
        {
            if (!_channel2Hyd.TryGetValue(epbChannel, out var hydId)) return;
            if (!_latches.TryGetValue(hydId, out var latch)) return;

            bool needReleaseNow;
            lock (latch.Gate)
            {
                latch.InFlight.Remove(epbChannel);
                needReleaseNow = latch.PressureOn && latch.InFlight.Count == 0;
            }

            if (!needReleaseNow) return;

            try
            {
                _log.Info($"液压[{hydId}] 本轮所有成员已到电压释放点：统一释压。", "液压协调");

                if (latch.ReleaseActionAsync != null)
                {
                    await latch.ReleaseActionAsync(); // 优先交由控制器或 Fallback 收尾
                }
                else
                {
                    // 极端兜底
                    try
                    {
                        _do.SetPressure(hydId, false);
                    }
                    catch
                    {
                    }

                    try
                    {
                        var dev = hydId == 1 ? "Cylinder1" : "Cylinder2";
                        _ao?.WritePercent(dev, 0);
                    }
                    catch
                    {
                    }
                }
            }
            finally
            {
                lock (latch.Gate)
                {
                    latch.InFlight.Clear();
                    latch.PressureOn = false;
                    latch.Cts?.Dispose();
                    latch.Cts = null;
                    latch.ReleaseActionAsync = null;
                }
            }
        }

        // —— 回退保持实现：DO 打开 + AO 输出百分比，达到阈值后保持，直到外部取消 —— //
        private async Task FallbackHoldLoopAsync(int hydId, CancellationToken token)
        {
            var item = _test.Hydraulics.FirstOrDefault(h => h.Id == hydId);
            if (item == null || !item.Enabled)
            {
                _log.Warn($"液压[{hydId}] 未启用/找不到配置。", "液压协调");
                return;
            }

            var dev = hydId == 1 ? "Cylinder1" : "Cylinder2";
            if (!_do.SetPressure(hydId, true))
            {
                _log.Error($"液压[{hydId}] DO 打开失败（Fallback）。", "液压协调");
                return;
            }

            if (!_ao.WritePercent(dev, item.SetPercent))
            {
                _log.Error($"液压[{hydId}] AO 输出失败（Fallback）。", "液压协调");
                return;
            }

            if (item.Mode == HydraulicMode.ByPressure || item.Mode == HydraulicMode.Either)
                while (!token.IsCancellationRequested)
                    try
                    {
                        if (_readPressure(hydId) >= item.PressureThresholdBar) break;
                        await Task.Delay(5, token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
            else if (item.Mode == HydraulicMode.ByDuration)
                try
                {
                    await Task.Delay(Math.Max(0, item.DurationMs), token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

            // 保持到取消
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                /* 正常 */
            }
        }

        // hydId 对应的一轮“闩锁”状态
        private sealed class Latch
        {
            public readonly object Gate = new();
            public CancellationTokenSource Cts; // Fallback 持有的 CTS（仅回退方案用）
            public readonly HashSet<int> InFlight = new(); // 仍未到“电压释放点”的通道
            public bool PressureOn; // 是否已进入“建压保持”状态
            public Func<Task> ReleaseActionAsync; // 统一“释压”动作（优先使用 HydraulicController）
        }
    }
}