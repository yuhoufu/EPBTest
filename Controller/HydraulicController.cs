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
    public sealed class PressureQualification
    {
        public PressureQualification(int hydraulicId, long generationId, double targetBar, double actualBar,
            DateTime reachedUtc, int stableMs, double minBar, double maxBar,
            double aoCommandPressureBar, double aoVoltage)
        {
            HydraulicId = hydraulicId;
            GenerationId = generationId;
            TargetBar = targetBar;
            ActualBar = actualBar;
            ReachedUtc = reachedUtc;
            StableMs = stableMs;
            MinBar = minBar;
            MaxBar = maxBar;
            AoCommandPressureBar = aoCommandPressureBar;
            AoVoltage = aoVoltage;
        }

        public int HydraulicId { get; }
        public long GenerationId { get; }
        public double TargetBar { get; }
        public double ActualBar { get; }
        public DateTime ReachedUtc { get; }
        public int StableMs { get; }
        public double MinBar { get; }
        public double MaxBar { get; }
        public double AoCommandPressureBar { get; }
        public double AoVoltage { get; }
    }

    public class HydraulicBuildException : InvalidOperationException
    {
        public HydraulicBuildException(string message) : base(message) { }
    }

    public sealed class HydraulicBuildTimeoutException : TimeoutException
    {
        public HydraulicBuildTimeoutException(
            int hydraulicId,
            double targetBar,
            double toleranceBar,
            double actualBar,
            int timeoutMs,
            string detail)
            : base(BuildMessage(
                hydraulicId,
                targetBar,
                toleranceBar,
                actualBar,
                timeoutMs,
                detail)) { }

        private static string BuildMessage(
            int hydraulicId,
            double targetBar,
            double toleranceBar,
            double actualBar,
            int timeoutMs,
            string detail)
        {
            var inspection = GetInspectionGuidance(detail);
            return $"HydraulicBuildTimeout Hydraulic={hydraulicId} Target={targetBar:F3}bar " +
                   $"Tolerance=±{toleranceBar:F3}bar " +
                   $"Allowed=[{targetBar - toleranceBar:F3},{targetBar + toleranceBar:F3}]bar " +
                   $"Actual={actualBar:F3}bar Timeout={timeoutMs}ms Detail={detail}; {inspection}";
        }

        private static string GetInspectionGuidance(string detail)
        {
            if (!string.IsNullOrEmpty(detail) &&
                detail.IndexOf("AboveToleranceWindow", StringComparison.OrdinalIgnoreCase) >= 0)
                return "inspect pressure regulator/control valve, AO calibration, pressure-sensor scaling and control response";

            if (!string.IsNullOrEmpty(detail) &&
                (detail.IndexOf("PressureSample", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 detail.IndexOf("NoPressureSample", StringComparison.OrdinalIgnoreCase) >= 0))
                return "inspect pressure-sensor wiring, DAQ sampling and hydraulic-channel mapping";

            return "inspect brake-fluid level/leakage, caliper cracks, joints, pipes, pump output and pressure calibration";
        }
    }

    /// <summary>
    ///     液压控制器：
    ///     - 负责单路液压的启/停与到达判定；
    ///     - 支持三种模式：ByPressure / ByDuration / Either；
    ///     - 通过 AoController 输出“能力百分比”并由其内部转换为电压。
    /// </summary>
    public sealed class HydraulicController
    {
        private readonly AoController _ao; // AO 控制器（统一做限幅与电压换算）
        private readonly DoController _do;

        private readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> _holdTcs
            = new();

        private readonly IAppLogger _log;
        private readonly Func<int, PressureSample> _readPressureSample;

        //等待“释压”信号的表：key=hydId
        private readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> _releaseWaiters = new();
        private readonly TestConfig _test;


        public HydraulicController(DoController doController,
            TestConfig test,
            Func<int, double> readPressure,
            AoController aoController,
            IAppLogger log = null)
            : this(
                doController,
                test,
                id => new PressureSample(id, readPressure(id), DateTime.UtcNow, System.Diagnostics.Stopwatch.GetTimestamp()),
                aoController,
                log)
        {
        }

        public HydraulicController(DoController doController,
            TestConfig test,
            Func<int, PressureSample> readPressureSample,
            AoController aoController,
            IAppLogger log = null)
        {
            _do = doController ?? throw new ArgumentNullException(nameof(doController));
            _test = test ?? throw new ArgumentNullException(nameof(test));
            _readPressureSample = readPressureSample ?? throw new ArgumentNullException(nameof(readPressureSample));
            _ao = aoController ?? throw new ArgumentNullException(nameof(aoController));
            _log = log ?? NullLogger.Instance;
        }

        /// <summary>
        /// 打开液压输出并等待实际压力连续稳定达到配置目标。返回前输出保持开启；
        /// 任意失败都会立即回零DO/AO，调用方不得在异常时给电机上电。
        /// </summary>
        public async Task<PressureQualification> BuildAndQualifyAsync(
            int hydId,
            long generationId,
            CancellationToken token)
        {
            var item = _test.Hydraulics.Find(h => h.Id == hydId);
            if (item == null || !item.Enabled)
                throw new HydraulicBuildException($"Hydraulic={hydId} 未启用或缺少配置，拒绝绕过压力资格。");

            var aoDevName = hydId == 1 ? "Cylinder1" : "Cylinder2";
            var outputArmed = false;
            try
            {
                if (!_do.SetPressure(hydId, true))
                    throw new HydraulicBuildException($"Hydraulic={hydId} PressureDOOpenFailed");

                outputArmed = true;
                var aoResult = _ao.WritePressureDetailed(aoDevName, item.PressureThresholdBar);
                if (!aoResult.Success)
                    throw new HydraulicBuildException($"Hydraulic={hydId} PressureAOWriteFailed Device={aoDevName}");

                _log.Info(
                    $"HydraulicQualificationStarted Hydraulic={hydId} Generation={generationId} " +
                    $"TargetPressureBar={item.PressureThresholdBar:F3} " +
                    $"ToleranceBar=±{Math.Max(0, item.PressureToleranceBar):F3} " +
                    $"AoCommandPressureBar={aoResult.CommandPressureBar:F3} AoVoltage={aoResult.Voltage:F3}",
                    "液压");

                var timeoutMs = Math.Max(1, item.BuildTimeoutMs);
                var stableMs = Math.Max(0, item.BuildStableMs);
                var toleranceBar = Math.Max(0, item.PressureToleranceBar);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                long? stableSince = null;
                var min = double.PositiveInfinity;
                var max = double.NegativeInfinity;
                var last = double.NaN;
                var lastDetail = "NoPressureSample";

                while (clock.ElapsedMilliseconds <= timeoutMs)
                {
                    token.ThrowIfCancellationRequested();
                    var sample = _readPressureSample(hydId);
                    last = sample.ValueBar;
                    var fresh = IsPressureSampleQualified(
                        sample,
                        double.NegativeInfinity,
                        item.PressureSampleMaxAgeMs);
                    if (fresh)
                    {
                        min = Math.Min(min, last);
                        max = Math.Max(max, last);
                        var inTargetWindow = IsPressureWithinTarget(
                            last,
                            item.PressureThresholdBar,
                            toleranceBar);
                        lastDetail = inTargetWindow
                            ? "StabilizingWithinTolerance"
                            : last < item.PressureThresholdBar - toleranceBar
                                ? "BelowToleranceWindow"
                                : "AboveToleranceWindow";
                        if (inTargetWindow)
                        {
                            if (!stableSince.HasValue) stableSince = clock.ElapsedMilliseconds;
                            if (clock.ElapsedMilliseconds - stableSince.Value >= stableMs)
                            {
                                var reachedUtc = DateTime.UtcNow;
                                var qualification = new PressureQualification(
                                    hydId,
                                    generationId,
                                    item.PressureThresholdBar,
                                    last,
                                    reachedUtc,
                                    stableMs,
                                    double.IsPositiveInfinity(min) ? last : min,
                                    double.IsNegativeInfinity(max) ? last : max,
                                    aoResult.CommandPressureBar,
                                    aoResult.Voltage);
                                _log.Info(
                                    $"PressureQualified Hydraulic={hydId} Generation={generationId} " +
                                    $"TargetPressureBar={qualification.TargetBar:F3} ActualPressureBar={last:F3} " +
                                    $"ToleranceBar=±{toleranceBar:F3} " +
                                    $"StableMs={stableMs} MinBar={qualification.MinBar:F3} MaxBar={qualification.MaxBar:F3}",
                                    "液压");
                                return qualification;
                            }
                        }
                        else
                        {
                            stableSince = null;
                        }
                    }
                    else
                    {
                        stableSince = null;
                        lastDetail = sample.IsFinite
                            ? $"PressureSampleStale AgeMs={sample.AgeMs:F1}"
                            : "PressureSampleInvalid";
                    }

                    await Task.Delay(10, token).ConfigureAwait(false);
                }

                throw new HydraulicBuildTimeoutException(
                    hydId,
                    item.PressureThresholdBar,
                    toleranceBar,
                    last,
                    timeoutMs,
                    lastDetail);
            }
            catch
            {
                if (outputArmed)
                    await ForceReleaseAsync(hydId).ConfigureAwait(false);
                throw;
            }
        }

        public static bool IsPressureWithinTarget(double actualBar, double targetBar, double toleranceBar)
        {
            if (double.IsNaN(actualBar) || double.IsInfinity(actualBar) ||
                double.IsNaN(targetBar) || double.IsInfinity(targetBar))
                return false;
            var tolerance = Math.Max(0, toleranceBar);
            return actualBar >= targetBar - tolerance && actualBar <= targetBar + tolerance;
        }

        public static bool IsPressureSampleQualified(
            PressureSample sample,
            double minimumBar,
            int maximumAgeMs)
        {
            return !ClassifyPressureSampleFailure(sample, minimumBar, maximumAgeMs).HasValue;
        }

        public static HydraulicPressureFailureReason? ClassifyPressureSampleFailure(
            PressureSample sample,
            double minimumBar,
            int maximumAgeMs)
        {
            if (sample.MonotonicTicks <= 0 || sample.TimestampUtc == DateTime.MinValue)
                return HydraulicPressureFailureReason.NoSample;
            if (!sample.IsFinite)
                return HydraulicPressureFailureReason.InvalidValue;
            if (sample.AgeMs < 0 || sample.AgeMs > Math.Max(1, maximumAgeMs))
                return HydraulicPressureFailureReason.StaleSample;
            if (sample.ValueBar < minimumBar)
                return HydraulicPressureFailureReason.BelowMinimum;
            return null;
        }

        /// <summary>无条件撤销液压DO并将AO回零；该方法不等待压力反馈。</summary>
        public Task ForceReleaseAsync(int hydId)
        {
            var aoDevName = hydId == 1 ? "Cylinder1" : "Cylinder2";
            try { _do.SetPressure(hydId, false); } catch { }
            try { _ao.WritePressure(aoDevName, 0); } catch { }
            _log.Warn($"HydraulicForceRelease Hydraulic={hydId} DO=Off AO=0", "液压");
            return Task.CompletedTask;
        }


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
                await BuildAndQualifyAsync(hydId, 0, token).ConfigureAwait(false);

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
                await ForceReleaseAsync(hydId).ConfigureAwait(false);

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

        /// <summary>
        ///     执行一次液压控制（按 TestConfig.Hydraulics 中的配置项）。
        ///     hydId: 1/2（两路液压）
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

                _log.Info($"液压[{hydId}] 启动，设定={item.PressureThresholdBar:F1}% 模式={item.Mode}", "液压");

                // Step 2: 输出 AO 百分比（由 AoController 内部做限幅与电压换算）
                // 同步版：WritePressure；如需无阻塞可改用 await _ao.SetPressureAsync(...)
                if (!_ao.WritePressure(aoDevName, item.PressureThresholdBar))
                {
                    _log.Error($"液压[{hydId}] AO 输出失败（设备={aoDevName} 压力={item.PressureThresholdBar:F1}%）。", "液压");
                    return false;
                }

                var tStart = DateTime.Now;
                var reached = false;

                // Step 3: 轮询判定：压力/时间/Either
                while (!token.IsCancellationRequested)
                {
                    var elapsedMs = (DateTime.Now - tStart).TotalMilliseconds;
                    var pBar = _readPressureSample(hydId).ValueBar;

                    switch (item.Mode)
                    {
                        case HydraulicMode.ByPressure:
                            if (IsPressureWithinTarget(pBar, item.PressureThresholdBar, item.PressureToleranceBar)) reached = true;
                            break;

                        case HydraulicMode.ByDuration:
                            if (elapsedMs >= item.DurationMs) reached = true;
                            break;

                        case HydraulicMode.Either:
                            if (IsPressureWithinTarget(pBar, item.PressureThresholdBar, item.PressureToleranceBar) || elapsedMs >= item.DurationMs) reached = true;
                            break;


                        case HydraulicMode.HoldUntilRelease: // 新模式：建压判定仍然按阈值/时长逻辑
                            if (IsPressureWithinTarget(pBar, item.PressureThresholdBar, item.PressureToleranceBar) || elapsedMs >= item.DurationMs) reached = true;
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
                    _ao.WritePressure(aoDevName, 0);
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
