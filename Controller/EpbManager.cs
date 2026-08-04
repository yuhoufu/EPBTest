using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Configuration;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Config;
using Config.Models;
using Controller.Adaptive;
using Controller.Alarm;
using DataOperation;
using IO.NI;
using Timing;
using IAppLogger = Config.IAppLogger;
using NullLogger = Config.NullLogger;

namespace Controller
{
    /// <summary>
    ///     12个卡钳统一编排：同组电控“首启”错峰（液压不延时），
    ///     每通道独立高精度定时器，可单独暂停/恢复/结束。
    /// </summary>
    public sealed partial class EpbManager
    {
        /// <summary>可选的圈记录器，外部在创建后赋值。</summary>
        /// // 2025.09.16 新增
        private IEpbCycleRecorder _recorder;
        public IEpbCycleRecorder Recorder
        {
            get => _recorder;
            set
            {
                _recorder = value;
                if (value is IActiveCycleLimitConfigurator configurable && _acq != null)
                {
                    var periodMs = Math.Max(1, _cfg?.Test?.PeriodMs ?? 1);
                    var maxRecords = (int)Math.Ceiling(
                        Math.Max(1.0, _acq.SampleRate) * periodMs / 1000.0 * 1.25);
                    configurable.SetMaxActiveCycleRecords(maxRecords);
                    _log?.Info(
                        $"活动圈样本硬上限已设置：{maxRecords} 条（采样率={_acq.SampleRate:F1}Hz，周期={periodMs}ms，裕量=1.25）。",
                        "落盘");
                }
            }
        }

        /// <summary>可选：报警管理器（M-7055D/RS-485），由 UI 初始化后注入。</summary>
        public AlarmManager Alarm { get; set; }

        /// <summary>可选：报警配置（用于 Runner 报警阈值/报警快照参数），由 UI 初始化后注入。</summary>
        public AlarmConfig AlarmConfig { get; set; }

        /// <summary>事件：某个通道触发了报警。</summary>
        public event Action<int, string> ChannelAlarmRaised;

        /// <summary>事件：某个通道产生软预警；不停止通道、不触发蜂鸣器。</summary>
        public event Action<int, string> ChannelWarningRaised;

        /// <summary>事件：某个通道被暂停。</summary>
        public event Action<int> ChannelPaused;

        /// <summary>事件：某个通道恢复运行。</summary>
        public event Action<int> ChannelResumed;

        /// <summary>程控电源遥测更新；订阅者不得阻塞控制线程。</summary>
        public event Action<PowerSupplyTelemetry> PowerSupplyTelemetryUpdated;

        /// <summary>程控电源组级硬故障。</summary>
        public event Action<PowerSupplyFault> PowerSupplyFaultRaised;

        /// <summary>结构化控制故障；共享资源故障会携带完整受影响成员。</summary>
        public event Action<ControlFault> ControlFaultRaised;
        public event Action<DaqRecoveryResult> DaqRecoveryStateChanged;
        public event Action<PressureQualification> PressureQualificationChanged;

        /// <summary>
        /// 人工复位指定电源组的故障锁存。只有输出已关闭、保护已解除且身份校验通过时才会成功；
        /// 下次启动仍执行完整预检。
        /// </summary>
        public Task ResetPowerSupplyFaultAsync(int electricalGroupId, CancellationToken token = default)
        {
            if (_powerSupply == null)
                throw new InvalidOperationException("程控电源控制未初始化。");
            return _powerSupply.ResetFaultAsync(electricalGroupId, token);
        }

        private readonly TwoDeviceAiAcquirer _acq; // ★ 新增：数据采集器

        private readonly AoController _ao;

        // EpbManager 字段区
        private readonly GlobalConfig _cfg;
        private readonly DoController _do;
        private readonly HydraulicController _hydraulic;
        private readonly IAppLogger _log;
        private readonly IPowerSupplyCoordinator _powerSupply;
        private readonly bool _requirePowerSupply;
        private PowerSupplyTelemetryCsvRecorder _powerTelemetryRecorder;

        private readonly SafetyMarginControlMode _safetyMarginControlMode;
        private readonly EpbControlMode _epbControlMode;
        private readonly bool _adaptiveShadowMode;
        private readonly EpbAdaptiveProfileStore _adaptiveProfileStore;
        private readonly EpbProgramSafetySettings _programSafetySettings;
        private readonly HashSet<int> _adaptiveChannels;


        // —— 回调（采样） —— //
        private readonly EpbCycleRunner.ReadCurrentDelegate _readCurrent;

        private readonly ChannelRuntimeStore<EpbCycleRunner> _runnerRuntime = new();
        private readonly ChannelRuntimeStore<HighPrecisionTimer> _timerRuntime = new();
        private ConcurrentDictionary<int, EpbCycleRunner> _runners => _runnerRuntime.Active;
        private ConcurrentDictionary<int, HighPrecisionTimer> _timers => _timerRuntime.Active;
        private readonly long _wallBaseTicks = Stopwatch.GetTimestamp();
        private readonly DateTime _wallBaseUtc = DateTime.UtcNow;
        private readonly HydraulicGroupCoordinator _hydCoordinator; // ★ 新增：液压组协调器

        private readonly SemaphoreSlim _alarmSnapshotGate = new(1, 1);
        private readonly Dictionary<int, DateTime> _lastAlarmSnapshotUtcByChannel = new();

        // 报警触发“立即停机”去重：同一次运行只处理首个报警；新运行必须显式复位
        private readonly ChannelAlarmStopLatch _alarmStopLatch = new();

        private void FlushPersistentLog(bool durable = true)
        {
            try { (_log as IFlushableAppLogger)?.Flush(durable); }
            catch
            {
                // 日志刷新失败不得回流控制链路。
            }
        }

        private void WriteRecorderBatchSafely(
            IEpbCycleRecorder recorder,
            int epbId,
            DateTime[] timestampsUtc,
            double[] currents,
            double[] pressures)
        {
            try
            {
                recorder.WriteBatch(epbId, timestampsUtc, currents, pressures);
            }
            catch (ActiveCycleDataLimitExceededException ex)
            {
                // 先触发控制层安全停机；封存、快照和 UI 通知均在其后异步执行。
                OnRunnerAlarmRaised(epbId, ex.Message);
            }
            catch (Exception ex)
            {
                _log.Error($"EPB[{epbId}] 写盘失败：{ex.Message}", "落盘", ex);
                SnapshotExportFailed?.Invoke(ex.Message);
            }
        }

        // ★ 跟踪每个通道“当前已 BeginCycle 的圈号”：用于报警停机时把当前圈封为 status='alarm'，避免遗留 running 悬挂圈
        private readonly ConcurrentDictionary<int, int> _currentCycleNumberByChannel = new();

        // 通道级“硬停机”取消源：用于中断当前圈内仍在运行的异步流程（Delay/等待判据等）
        private readonly ConcurrentDictionary<int, CancellationTokenSource> _stopCtsByChannel = new();
        private readonly ConcurrentDictionary<int, byte> _emergencyPowerGroupLatch = new();

        // ★ 当前仍参与“液压组判定”的通道集合：用于把“报警停机/提前结束”的通道排除出释压条件
        // 说明：
        // - 批量对齐启动中，液压建压锚点每圈会对“参与通道”调用 EnterElectricalPhaseAsync 并登记 InFlight。
        // - 若某通道报警停机或提前结束，但仍被重复登记进 InFlight，则会阻塞其它正常通道到达“电压释放点”后的统一释压。
        // - 因此这里维护一个并发集合，确保每圈只登记“仍在跑/仍参与本轮判定”的通道。
        private readonly ConcurrentDictionary<int, byte> _hydraulicParticipants = new();
        private readonly ConcurrentDictionary<int, HydraulicCycleLease> _hydraulicLeaseByChannel = new();
        private long _singleHydraulicGeneration;
        private readonly ConcurrentDictionary<string, int> _daqRecoveryAttemptsByDevice = new();
        private readonly ConcurrentDictionary<string, Task<DaqRecoveryResult>> _daqRecoveryTasks = new();
        private readonly object _daqRecoveryGate = new();

        /// <summary>
        ///     将指定通道标记为“参与液压判定”。
        /// </summary>
        /// <param name="channel">EPB 通道号（1..12）。</param>
        /// <remarks>
        ///     该集合用于批量对齐启动的“建压锚点”过滤：只有参与者才会被登记进
        ///     <see cref="HydraulicGroupCoordinator"/> 的 InFlight，从而避免报警停机/提前结束通道影响释压条件。
        /// </remarks>
        private void MarkHydraulicParticipant(int channel)
        {
            _hydraulicParticipants[channel] = 0;
        }

        /// <summary>
        ///     将指定通道从“参与液压判定”集合中移除。
        /// </summary>
        /// <param name="channel">EPB 通道号（1..12）。</param>
        /// <remarks>
        ///     触发场景：
        ///     <list type="bullet">
        ///         <item>报警触发快速停机；</item>
        ///         <item>人工停止；</item>
        ///         <item>该通道自然完成全部圈数（批量模式下各通道圈数可能不同）。</item>
        ///     </list>
        ///     移除后，该通道不会再被纳入后续每圈的 InFlight 登记，因此不会阻塞其它正常通道的释压。
        /// </remarks>
        private void UnmarkHydraulicParticipant(int channel)
        {
            _hydraulicParticipants.TryRemove(channel, out _);
        }

        /// <summary>
        ///     判断指定通道是否仍参与液压判定。
        /// </summary>
        /// <param name="channel">EPB 通道号（1..12）。</param>
        private bool IsHydraulicParticipant(int channel)
        {
            return _hydraulicParticipants.ContainsKey(channel);
        }


        /// <summary>
        ///     判断指定通道是否已进入“报警停机”流程（用于圈结账时写入 <c>status='alarm'</c>）。
        /// </summary>
        /// <param name="channel">EPB 通道号（1..12）。</param>
        /// <returns>若该通道已触发报警停机则返回 true，否则返回 false。</returns>
        private bool IsAlarmStopRequested(int channel)
        {
            return _alarmStopLatch.IsStopRequested(channel);
        }


        /// <summary>
        ///     记录通道“当前圈号”（BeginCycle 后调用），用于报警停机时封圈。
        /// </summary>
        /// <param name="channel">EPB 通道号（1..12）。</param>
        /// <param name="cycleNumber">当前圈号（与 Recorder.BeginCycle 一致）。</param>
        private void MarkCurrentCycleNumber(int channel, int cycleNumber)
        {
            _currentCycleNumberByChannel[channel] = cycleNumber;
        }


        /// <summary>
        ///     清除通道“当前圈号”（Complete/Alarm 封圈后调用），避免后续误封圈。
        /// </summary>
        /// <param name="channel">EPB 通道号（1..12）。</param>
        private void ClearCurrentCycleNumber(int channel)
        {
            _currentCycleNumberByChannel.TryRemove(channel, out _);
        }


        /// <summary>
        ///     报警快照流程结束后封圈：只有当前报警圈的 CSV/BIN 均已落盘时才写
        ///     <c>status='alarm'</c>；快照失败则写为 <c>failed</c>。
        /// </summary>
        /// <param name="channel">EPB 通道号（1..12）。</param>
        /// <remarks>
        ///     线程模型：可在报警事件回调线程/后台任务中调用；内部不抛异常（仅 best-effort）。
        ///     <para>
        ///     设计目的：保证 UI(EpbTestRecord) 计数与落盘圈数一致，避免 BeginCycle 后未 Complete 导致的“running 悬挂圈”。
        ///     </para>
        /// </remarks>
        private void TryFinalizeCurrentCycleAfterSnapshot(int channel, bool hasSnapshotFiles)
        {
            var recorder = Recorder;
            if (recorder == null) return;

            if (!_currentCycleNumberByChannel.TryRemove(channel, out var cycleNumber))
                return;

            try
            {
                var finalN = recorder.GetCurrentCycleSampleCount(channel);
                if (hasSnapshotFiles)
                {
                    recorder.AlarmCycle(channel, cycleNumber, finalN, DateTime.UtcNow);
                }
                else
                {
                    recorder.AbortCycle(
                        channel,
                        cycleNumber,
                        finalN,
                        DateTime.UtcNow,
                        "failed");
                    _log.Warn(
                        $"EPB[{channel}] 报警快照文件未完整生成，当前圈记为 failed，不写入 alarm。",
                        "落盘");
                }
            }
            catch
            {
                // ignore
            }
        }

        internal void GetPeakCaptureIdentity(int channel, out Guid runId, out int cycleNumber)
        {
            runId = _activeBatchId;
            if (runId == Guid.Empty) runId = Guid.Empty;
            cycleNumber = _currentCycleNumberByChannel.TryGetValue(channel, out var current)
                ? current
                : 0;
        }


        /// <summary>
        ///     通道“自然完成全部圈数”后的统一收尾：
        ///     <list type="number">
        ///         <item>将通道从“液压判定参与者”集合中移除，避免影响其它通道释压；</item>
        ///         <item>将通道从运行字典（Timer/Runner）中移除，避免被误判为仍在运行；</item>
        ///         <item>执行安全落位：断电 + 请求液压释放；</item>
        ///         <item>导出最近10圈（FlushRecent），满足“停止即存最近10圈”的现场要求。</item>
        ///     </list>
        /// </summary>
        /// <param name="channel">EPB 通道号（1..12）。</param>
        /// <remarks>
        ///     该方法用于两种运行模式：
        ///     <list type="bullet">
        ///         <item>单通道 StartChannelAsync 的自然完成；</item>
        ///         <item>批量对齐启动（BatchStart）中某通道 runs 不一致导致的提前完成。</item>
        ///     </list>
        ///     注意：这里不会调用 Timer.Stop()；因为调用时机在“最后一圈回调”内，计时器即将自然退出。
        /// </remarks>
        private void FinalizeChannelAfterNaturalCompletion(int channel)
        {
            _log.Info(
                $"EPB[{channel}] 已完成全部目标圈数，开始安全断电、液压释放和最近10圈持久化。",
                "EPB");

            // 1) 先从液压判定参与者中移除
            UnmarkHydraulicParticipant(channel);

            // 2) 取消并释放硬停机 CTS（该通道已完成）
            try { CancelStopCts(channel); } catch { /* ignore */ }

            // 3) 原子移除运行对象，避免与并发启动/采集路由交叉。
            RemoveTimerRuntime(channel, nameof(FinalizeChannelAfterNaturalCompletion));
            RemoveRunnerRuntime(channel, nameof(FinalizeChannelAfterNaturalCompletion));

            // 4) 安全落位：断电 + 请求液压释放
            try { CommandEpbOff(channel, nameof(FinalizeChannelAfterNaturalCompletion)); } catch { /* ignore */ }
            try { _ = HydraulicMarkReleaseAsync(channel); } catch { /* ignore */ }

            // 5) 停止即存最近10圈：不阻塞当前线程
            try
            {
                var recorder = Recorder;
                if (recorder != null)
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            recorder.FlushRecent(channel, 10);
                        }
                        catch (Exception ex)
                        {
                            _log.Warn($"EPB[{channel}] 停止导出失败：{ex.Message}", "落盘");
                        }
                    });
            }
            catch
            {
                // ignore
            }

            TryEndBatchSessionWhenIdle();
            FlushPersistentLog();
        }

        /// <summary>
        /// 为指定通道创建新的“硬停机”取消源；若已存在则先取消并释放旧实例。
        /// </summary>
        /// <param name="channel">EPB 通道号（1..12）。</param>
        /// <returns>新的取消源实例。</returns>
        private CancellationTokenSource RenewStopCts(int channel)
        {
            if (_stopCtsByChannel.TryRemove(channel, out var old))
            {
                try { old.Cancel(); } catch { /* ignore */ }
                try { old.Dispose(); } catch { /* ignore */ }
            }

            var cts = new CancellationTokenSource();
            _stopCtsByChannel[channel] = cts;
            return cts;
        }

        /// <summary>
        /// 取消并移除指定通道的“硬停机”取消源。
        /// </summary>
        private void CancelStopCts(int channel)
        {
            if (_stopCtsByChannel.TryRemove(channel, out var cts))
            {
                try { cts.Cancel(); } catch { /* ignore */ }
                try { cts.Dispose(); } catch { /* ignore */ }
            }
        }


        public EpbManager(
            GlobalConfig cfg,
            DoController doController,
            AoController aoController,
            TwoDeviceAiAcquirer acq,
            IAppLogger log = null,
            SafetyMarginControlMode safetyMarginControlMode = SafetyMarginControlMode.Legacy20251010,
            EpbControlMode epbControlMode = EpbControlMode.LegacyFixedTiming,
            bool adaptiveShadowMode = true,
            IPowerSupplyCoordinator powerSupply = null,
            bool requirePowerSupply = false)
        {
            _do = doController ?? throw new ArgumentNullException(nameof(doController));
            _ao = aoController ?? throw new ArgumentNullException(nameof(aoController));
            _acq = acq ?? throw new ArgumentNullException(nameof(acq));
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));

            _do = doController;
            _ao = aoController;
            //_readCurrent = acq.ReadCurrent;
            _readCurrent = acq.ReadCurrentFast;
            _log = log ?? NullLogger.Instance;
            _acq = acq;
            _requirePowerSupply = requirePowerSupply || ReadBooleanAppSetting("PowerSupplyIntegrationRequired", false);
            if (powerSupply == null && _requirePowerSupply)
            {
                var powerConfigPath = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "Config",
                    "PowerSupplyConfig.xml");
                var powerConfig = PowerSupplyConfigLoader.Load(powerConfigPath);
                powerSupply = new PowerSupplyCoordinator(powerConfig, cfg.Test.Groups, _log);
            }
            _powerSupply = powerSupply;
            if (_powerSupply != null)
            {
                _powerSupply.TelemetryUpdated += OnPowerSupplyTelemetryUpdated;
                _powerSupply.FaultRaised += OnPowerSupplyFaultRaised;
            }

            _safetyMarginControlMode = safetyMarginControlMode;
            _epbControlMode = ReadEpbControlMode(epbControlMode);
            _adaptiveShadowMode = ReadAdaptiveShadowMode(adaptiveShadowMode);
            _adaptiveChannels = ReadAdaptiveChannels();
            _programSafetySettings = EpbProgramSafetySettings.Load(_log);

            try
            {
                var projectConfigDir = ConfigLoader.GetProjectConfigDir(cfg.Test.StoreDir, cfg.Test.TestName);
                _adaptiveProfileStore = new EpbAdaptiveProfileStore(projectConfigDir, _log);
                _log.Info(
                    $"EPB 控制模式={_epbControlMode}，灰度通道={string.Join(",", _adaptiveChannels)}，" +
                    $"影子判定={_adaptiveShadowMode}，模型={_adaptiveProfileStore.FilePath}",
                    "EPB");
                _log.Info(
                    "项目 TestConfig.xml 中旧的正/反向失速与断电清零字段仅为兼容读取，" +
                    "运行时统一使用 EXE 同名配置中的程序级安全策略。",
                    "EPB");
            }
            catch (Exception ex)
            {
                _log.Warn($"初始化 EPB 自适应模型存储失败，将使用内存空模型：{ex.Message}", "EPB");
            }

            // 从cfg中获取控制参数；
            PeriodMs = cfg.Test.PeriodMs; // 周期时长
            TestCycle = cfg.Test.TestTarget; // 总周期数

            // 添加每个epb通道的目标次数
            foreach (var epbRecord in cfg.Test.EpbRecords)
            {

                EpbTestCycle!.Add(epbRecord.Id,epbRecord.TotalCount - epbRecord.RunCount);  // 需要能够每次开始由总次数-已运行次数
                
            }
            



            // —— 订阅“低时延电流样本”并转发给对应 Runner —— //
            _acq.OnFastEpbCurrent += (ch, amps, ts) =>
            {
                if (_runners.TryGetValue(ch, out var r))
                {
                    var sampleUtc = ts.ToUniversalTime();
                    var tick = ToStopwatchTicks(sampleUtc);
                    r.FeedCurrentSample(ch, tick, amps, sampleUtc);
                }
            };

            #region 写盘批次桥接：TwoDeviceAiAcquirer → IEpbCycleRecorder

            // —— 写盘批次桥接：TwoDeviceAiAcquirer → IEpbCycleRecorder —— //
            _acq.OnDiskBatch += (device, tsUtc, currentsByEpb, pressureGroup1, pressureGroup2) =>
            {
                var recorder = Recorder;
                if (recorder == null) return;
                if (tsUtc == null || tsUtc.Length == 0) return;
                if (currentsByEpb == null || currentsByEpb.Count == 0) return;

                foreach (var kvp in currentsByEpb)
                {
                    var epbId = kvp.Key; // 1..12
                    var currents = kvp.Value; // 电流数组
                    if (currents == null || currents.Length == 0)
                        continue;

                    // === ★ 电流取绝对值（不修改原数组，避免影响其它模块） ===
                    var absCurrents = new double[currents.Length];
                    for (int i = 0; i < currents.Length; i++)
                    {
                        absCurrents[i] = Math.Abs(currents[i]);
                    }

                    // 1..6 → 压力组1，7..12 → 压力组2
                    double[] pressure = null;
                    if (epbId >= 1 && epbId <= 6)
                        pressure = pressureGroup1;
                    else if (epbId >= 7 && epbId <= 12)
                        pressure = pressureGroup2;

                    // 没有压力时，用 0 填充数组，仍然让电流落盘
                    if (pressure == null || pressure.Length == 0)
                        pressure = new double[currents.Length];

                    // 对齐长度：取三者最小值
                    var n = Math.Min(tsUtc.Length, Math.Min(absCurrents.Length, pressure.Length));
                    if (n <= 0)
                        continue;

                    if (n == tsUtc.Length && n == absCurrents.Length && n == pressure.Length)
                    {
                        WriteRecorderBatchSafely(recorder, epbId, tsUtc, absCurrents, pressure);
                    }
                    else
                    {
                        var tsBuf = new DateTime[n];
                        var curBuf = new double[n];
                        var prBuf = new double[n];

                        Array.Copy(tsUtc, tsBuf, n);
                        Array.Copy(absCurrents, curBuf, n);
                        Array.Copy(pressure, prBuf, n);

                        WriteRecorderBatchSafely(recorder, epbId, tsBuf, curBuf, prBuf);
                    }
                }
            };


            #endregion


            _hydraulic = new HydraulicController(
                _do,
                _cfg.Test,
                acq.ReadPressureSample,
                aoController,
                _log);

            // ★ 创建协调器（具备 AO 与读压，可用 Fallback/保持两种实现）
            _hydCoordinator = new HydraulicGroupCoordinator(
                _cfg.Test,
                _cfg.DO,
                _do,
                acq.ReadPressureSample,
                aoController,
                _hydraulic,
                _log);
            _hydCoordinator.FaultRaised += OnHydraulicFaultRaised;
        }

        private void SaveProgramSafetySnapshot()
        {
            var projectConfigDir = ConfigLoader.GetProjectConfigDir(
                _cfg?.Test?.StoreDir,
                _cfg?.Test?.TestName);
            if (_programSafetySettings == null) return;
            _log.Info(
                $"本次试验EPB程序级安全策略：Policy={EpbProgramSafetySettings.SafetyPolicyVersion}; " +
                _programSafetySettings.ToAuditLogText(),
                "EPB");
            _programSafetySettings.SaveEffectiveSnapshot(projectConfigDir, _log);
        }

        // 将 DateTime（采集回调给的 ts）换算为当前进程 Stopwatch Ticks
        private long ToStopwatchTicks(DateTime tsUtc)
        {
            var dtSec = (tsUtc - _wallBaseUtc).TotalSeconds;
            return _wallBaseTicks + (long)(dtSec * Stopwatch.Frequency);
        }

        /// <summary>
        ///     读取指定液压组的压力值（委托给 HydraulicController）
        /// </summary>
        /// <param name="hydId"></param>
        /// <returns></returns>
        private double ReadPressure(int hydId)
        {
            return _acq.ReadPressure(hydId);
        }

        // EpbManager.cs 里（EpbManager 类内）新增：
        public Task HydraulicEnterAsync(int channel, CancellationToken token)
        {
            if (_hydCoordinator == null) return Task.CompletedTask;
            if (_hydraulicLeaseByChannel.ContainsKey(channel)) return Task.CompletedTask;

            var hydId = channel <= 6 ? 1 : 2;
            var runId = _activeBatchId == Guid.Empty ? Guid.NewGuid() : _activeBatchId;
            var key = new HydraulicGenerationKey(
                runId,
                hydId,
                HydraulicPhaseKind.SingleChannel,
                Interlocked.Increment(ref _singleHydraulicGeneration));
            return EnterSingleHydraulicGenerationAsync(key, channel, token);
        }

        private async Task EnterSingleHydraulicGenerationAsync(
            HydraulicGenerationKey key,
            int channel,
            CancellationToken token)
        {
            var lease = await _hydCoordinator.EnterGenerationAsync(key, new[] { channel }, token)
                .ConfigureAwait(false);
            _hydraulicLeaseByChannel[channel] = lease;
            try { PressureQualificationChanged?.Invoke(lease.Qualification); } catch { }
        }

        /// <summary>
        ///     释放液压
        /// </summary>
        /// <param name="channel"></param>
        /// <returns></returns>
        public async Task HydraulicMarkReleaseAsync(int channel)
        {
            if (_hydCoordinator == null) return;
            if (!_hydraulicLeaseByChannel.TryGetValue(channel, out var lease))
            {
                await _hydCoordinator.MarkVoltageReleaseAsync(channel).ConfigureAwait(false);
                return;
            }

            try
            {
                await _hydCoordinator.MarkVoltageReleaseAsync(lease, channel).ConfigureAwait(false);
            }
            finally
            {
                _hydraulicLeaseByChannel.TryRemove(channel, out _);
            }
        }

        // （保留你已有的 StartChannelAsync / Pause/Resume/Stop 等实现，不改对外签名）
        public async Task StartChannelAsync(int channel, CancellationToken uiToken = default)
        {
            if (_timers.ContainsKey(channel))
            {
                _log.Warn($"EPB[{channel}] 已在运行。", "EPB");
                return;
            }

            EnsureStrictCurveControl(new[] { channel });
            SaveProgramSafetySnapshot();
            if (_powerSupply != null)
                await _powerSupply.PrepareAndEnableAsync(new[] { channel }, uiToken).ConfigureAwait(false);

            // 若上一次因报警触发过停机，这里允许重新启动
            _alarmStopLatch.BeginRun(channel);

            // 本次启动为该通道刷新“硬停机”取消源
            var stopCts = RenewStopCts(channel);

            //var rcfg = _cfg.Test?.EpbCycleRunner ?? new EpbCycleRunnerConfig();
            var rcfg = _cfg.Test?.EpbCycleRunner.GetRunnerChannel(channel);


            var periodMs = _cfg.Test.PeriodMs;
            var sampleMs = 2;


            var forwardA = rcfg.ForwardA;
            var holdMs = rcfg.HoldMs;

            // 如果 holdMs 为 null、0 或无效值，则设置为默认值 1000ms
            holdMs = holdMs <= 0 ? 1000 : holdMs; // 设置为 1000ms（1秒），可根据实际需要调整，调试使用

            var singleChannelPlan = ElectricalStaggerPlanner.Build(
                new[] { channel },
                _cfg.Test.Groups,
                periodMs);
            var singleRunId = Guid.NewGuid();
            RegisterRunContext(singleRunId, singleChannelPlan);
            var singleAssignment = singleChannelPlan.Get(channel);
            var staggerMs = singleAssignment.PhaseMs;
            _log.Info(
                $"EPB[{channel}] 单通道运行 Run={singleRunId:N}，归属组 {singleAssignment.ElectricalGroupId}，" +
                $"按已选集合重新编号后首启相位={staggerMs}ms。",
                "EPB");

            //日志记录周期
            _log.Info($"高精度定时器，  EPB[{channel}] 周期 {periodMs}ms，采样 {sampleMs}ms，前进阈值 {forwardA}A，保持时间 {holdMs}ms",
                "EPB");

            var timer = GetTimer(channel, periodMs, _cfg.Test.OverrunPolicy);

            // 标记为“参与液压判定”（用于后续批量建压锚点过滤；单通道模式也保持一致）
            MarkHydraulicParticipant(channel);

            var runner = (EpbCycleRunner)GetRunner(channel);

            var learnCycles = GetProp<int>(rcfg, "LearnCycles");
            if (learnCycles <= 0) learnCycles = 5;

            if (learnCycles > 0)
            {
                _log.Info($"EPB[{channel}] 启动前自学习 {learnCycles} 次。", "EPB");
                try
                {
                    MarkElectricalPhaseDue(channel, DateTime.UtcNow);
                    await runner.LearnAsync(learnCycles, uiToken, periodMs).ConfigureAwait(false);
                    _log.Info($"EPB[{channel}] 自学习完成，进入正式试验。", "EPB");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.Warn($"EPB[{channel}] 自学习异常：{ex.Message}，仍将尝试进入正式试验。", "EPB");
                }
            }

            var singleFormalAnchorUtc = DateTime.UtcNow.AddMilliseconds(staggerMs);
            _ = timer.StartAsync(_cfg.Test.TestTarget, staggerMs, async (i, token) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, stopCts.Token);
                var ct = linked.Token;
                var actualStartUtc = DateTime.UtcNow;
                var nominalDueUtc = singleFormalAnchorUtc.AddMilliseconds((long)(i - 1) * periodMs);
                var elapsedSinceNominalMs = (actualStartUtc - nominalDueUtc).TotalMilliseconds;
                var rolledPeriods = elapsedSinceNominalMs <= 0
                    ? 0L
                    : (long)Math.Floor(elapsedSinceNominalMs / periodMs);
                var plannedStartUtc = nominalDueUtc.AddMilliseconds(rolledPeriods * periodMs);
                MarkElectricalPhaseDue(channel, plannedStartUtc);

                _log.Info(
                    $"EPB[{channel}] 周期 {i}/{_cfg.Test.TestTarget} 开始，Run={singleRunId:N} " +
                    $"Group={singleAssignment.ElectricalGroupId} Phase={singleAssignment.PhaseMs}ms " +
                    $"PlannedUtc={plannedStartUtc:O} ActualUtc={actualStartUtc:O} " +
                    $"DeviationMs={(actualStartUtc - plannedStartUtc).TotalMilliseconds:F3}。",
                    "EPB");


                // —— 圈开始（圈号 i，以 1 开始；若你的计数为 0 开始，可按需调整）——
                Recorder?.BeginCycle(channel, i, DateTime.UtcNow);
                MarkCurrentCycleNumber(channel, i);

                var ok = false;
                try
                {
                    ok = await runner.RunOneAsync(periodMs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    ok = false;
                }
                catch
                {
                    ok = false;
                }

                // —— 圈结束：根据是否报警停机决定封圈状态 ——
                var recorder = Recorder;
                if (recorder != null)
                {
                    try
                    {
                        var finalN = recorder.GetCurrentCycleSampleCount(channel);
                        if (IsAlarmStopRequested(channel))
                        {
                            // 报警后台流程负责在快照文件存在后封圈。
                        }
                        else if (runner.LastCycleOutcome.Kind == EpbCycleOutcomeKind.HardFault)
                            recorder.AbortCycle(channel, i, finalN, DateTime.UtcNow, "failed");
                        else if (runner.LastCycleOutcome.IsSuccess)
                            CompleteCycleAndScheduleEvidence(
                                recorder,
                                channel,
                                i,
                                finalN,
                                DateTime.UtcNow);
                        else
                            recorder.AbortCycle(
                                channel,
                                i,
                                finalN,
                                DateTime.UtcNow,
                                runner.LastCycleOutcome.Kind == EpbCycleOutcomeKind.Canceled
                                    ? "canceled"
                                    : "failed");
                    }
                    catch
                    {
                        // ignore
                    }
                }

                if (!IsAlarmStopRequested(channel))
                    ClearCurrentCycleNumber(channel);

                // 若本通道自然完成最后一圈，则做统一收尾（含“停止即存最近10圈”）
                if (i >= _cfg.Test.TestTarget)
                    FinalizeChannelAfterNaturalCompletion(channel);

                _log.Info($"EPB[{channel}] 周期 {i}/{_cfg.Test.TestTarget} {(ok ? "完成" : "失败")}", "EPB");
                return ok;
            });
        }

        public void PauseChannel(int channel)
        {
            if (_timers.TryGetValue(channel, out var t)) t.Pause();
            ChannelPaused?.Invoke(channel);
        }

        public void ResumeChannel(int channel)
        {
            if (_timers.TryGetValue(channel, out var t)) t.Resume();
            ChannelResumed?.Invoke(channel);
        }


        /// <summary>
        /// 停止指定通道：
        /// 1) 停止并移除当前轮正在使用的计时器；
        /// 2) 同时清理计时器缓存（不再复用旧实例）；
        /// 3) 移除运行器与其缓存；
        /// 4) 落位并做必要的收尾。
        /// </summary>
        public void StopChannel(int channel)
        {
            // 该通道停止后不再参与液压判定
            UnmarkHydraulicParticipant(channel);

            // 先取消“硬停机”Token，尽快中断当前圈内仍在运行的异步逻辑
            try { CancelStopCts(channel); } catch { /* ignore */ }

            RemoveTimerRuntime(channel, nameof(StopChannel));

            // —— 安全落位（优先）：尽快断电并请求液压释放 —— //
            try
            {
                CommandEpbOff(channel, nameof(StopChannel));
            }
            catch
            {
                /* 忽略 */
            }

            try
            {
                _ = HydraulicMarkReleaseAsync(channel);
            }
            catch
            {
                /* 忽略 */
            }

            RemoveRunnerRuntime(channel, nameof(StopChannel));

            // —— 收尾：落盘导出（Stop 场景保留原逻辑）—— //
            try
            {
                Recorder?.FlushRecent(channel, 10);
            }
            catch (Exception ex)
            {
                _log.Warn($"EPB[{channel}] 停止导出失败：{ex.Message}", "落盘");
            }

            TryEndBatchSessionWhenIdle();
            TryDisableIdlePowerGroup(channel, "通道停止后电源组已无运行通道");
        }


        /// <summary>
        /// 报警触发时的“快速停机”：
        /// - 立即停止该通道计时器（不再进入下一圈）；
        /// - 立即断电并请求液压释放；
        /// - 清理 Runner 引用，避免采集回调继续喂样本；
        /// - 不做任何耗时导出（快照导出在报警链路中单独处理）。
        /// </summary>
        /// <param name="channel">EPB 通道号（1..12）。</param>
        private void StopChannelOnAlarm(int channel)
        {
            // 报警停机：立即从“参与液压判定”集合中移除，防止其阻塞其它通道释压
            UnmarkHydraulicParticipant(channel);

            // 先取消“硬停机”Token，尽快中断当前圈内仍在运行的异步逻辑
            try { CancelStopCts(channel); } catch { /* ignore */ }

            RemoveTimerRuntime(channel, nameof(StopChannelOnAlarm));

            // —— 安全落位：立即断电 + 请求液压释放 —— //
            try { CommandEpbOffHighPriority(channel, nameof(StopChannelOnAlarm)); } catch { /* ignore */ }
            try { _ = HydraulicMarkReleaseAsync(channel); } catch { /* ignore */ }

            RemoveRunnerRuntime(channel, nameof(StopChannelOnAlarm));

            // —— 现场要求：停止即存最近10圈 ——
            // 说明：报警停机路径不应阻塞 Runner/定时器线程，因此这里用后台任务异步 Flush。
            try
            {
                var recorder = Recorder;
                if (recorder != null)
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            recorder.FlushRecent(channel, 10);
                        }
                        catch (Exception ex)
                        {
                            _log.Warn($"EPB[{channel}] 停止导出失败：{ex.Message}", "落盘");
                        }
                    });
            }
            catch
            {
                // ignore
            }

            TryEndBatchSessionWhenIdle();
            TryDisableIdlePowerGroup(channel, "报警通道停止后电源组已无运行通道");
        }


        private void OnRunnerAlarmRaised(int channel, string reason)
        {
            if (_powerSupply != null &&
                reason?.IndexOf("AbnormalHighCurrentPlateau", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var groupId = GetElectricalGroupId(channel);
                if (groupId > 0 && _powerSupply.HasFreshPowerFaultEvidence(groupId))
                {
                    var snapshot = _powerSupply.GetLatestSnapshot(groupId);
                    var affected = _cfg.Test.Groups
                        .First(x => x.Id == groupId)
                        .Members
                        .Where(x => IsHydraulicParticipant(x) || _timers.ContainsKey(x) || x == channel)
                        .Distinct()
                        .OrderBy(x => x)
                        .ToArray();
                    OnPowerSupplyFaultRaised(new PowerSupplyFault
                    {
                        TimestampUtc = DateTime.UtcNow,
                        SupplyId = snapshot?.SupplyId ?? groupId,
                        ElectricalGroupId = groupId,
                        Code = "SharedPowerLimiting",
                        Reason = $"支路平台与程控电源限流/低压证据同时出现。首发EPB={channel}；{reason}",
                        AffectedChannels = affected.Length > 0 ? affected : new[] { channel },
                        Snapshot = snapshot
                    });
                    return;
                }
            }

            // ★同步去重 latch：保证计时器回调能尽快识别“本圈应封为 alarm”，但不在此线程做 IO
            if (!_alarmStopLatch.TryRequestStop(channel))
                return;

            // 通道硬故障只取消本通道。共享压力/电源/DAQ故障由各自组级处理器扩大范围。
            try { CancelStopCts(channel); } catch { }

            var alarmUtc = DateTime.UtcNow;
            var channelFault = new ControlFault(
                ExtractFaultCode(reason),
                reason,
                FaultScope.Channel,
                new[] { channel },
                null,
                alarmUtc,
                Guid.NewGuid());
            _log.Error(
                $"EPB[{channel}] 硬故障，立即停止该通道并导出报警快照。" +
                $"CorrelationId={channelFault.CorrelationId:N} 原因={reason}",
                "报警");
            FlushPersistentLog();
            try { ControlFaultRaised?.Invoke(channelFault); } catch { }
            ChannelAlarmRaised?.Invoke(channel, reason);

            // 不阻塞 Runner/定时器线程
            _ = Task.Run(async () =>
            {
                foreach (var affectedChannel in ChannelFaultIsolationPolicy.GetChannelsToStop(channel))
                {
                    try { StopChannelOnAlarm(affectedChannel); } catch { /* ignore */ }
                }

                try
                {
                    if (Alarm != null)
                        await Alarm.SetAlarmAsync(channel, true, reason).ConfigureAwait(false);
                }
                catch
                {
                    // ignore
                }

                AlarmCycleSnapshotEvidence snapshotEvidence = null;
                try
                {
                    snapshotEvidence = await ExportAlarmSnapshotAsync(channel, reason, alarmUtc).ConfigureAwait(false);
                }
                catch
                {
                    // ignore
                }

                if (snapshotEvidence == null)
                    TryFinalizeCurrentCycleAfterSnapshot(channel, false);
                else
                    _currentCycleNumberByChannel.TryRemove(channel, out _);
            });
        }

        private static string ExtractFaultCode(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return "ChannelFault";
            var end = reason.IndexOfAny(new[] { ' ', ':', ';' });
            return end > 0 ? reason.Substring(0, end) : reason;
        }

        private void OnRunnerRecoverableFaultRaised(int channel, string reason)
        {
            var device = _acq.GetDeviceForEpbChannel(channel);
            if (string.IsNullOrWhiteSpace(device))
            {
                OnRunnerAlarmRaised(channel, "DaqRecoveryUnavailable " + reason);
                return;
            }

            Task<DaqRecoveryResult> recoveryTask;
            lock (_daqRecoveryGate)
            {
                if (_daqRecoveryTasks.TryGetValue(device, out recoveryTask) && !recoveryTask.IsCompleted)
                    return; // 同一设备同一次断流的兄弟通道加入现有恢复，不重复计数。

                var attempt = _daqRecoveryAttemptsByDevice.AddOrUpdate(device, 1, (_, old) => old + 1);
                if (attempt > 1)
                {
                    var affected = GetDaqGroupChannels(device);
                    PublishDaqGroupFault(
                        device,
                        "RepeatedDaqSampleStale",
                        reason,
                        affected);
                    foreach (var affectedChannel in affected)
                        OnRunnerAlarmRaised(
                            affectedChannel,
                            $"DaqGroupHardFault Device={device} RepeatedDaqSampleStale {reason}");
                    return;
                }

                var affectedChannels = GetDaqGroupChannels(device);
                foreach (var affectedChannel in affectedChannels)
                {
                    try { CommandEpbOff(affectedChannel, "DaqRecoverableFault"); } catch { }
                    try { _ = HydraulicMarkReleaseAsync(affectedChannel); } catch { }
                }

                recoveryTask = RecoverDaqDeviceAsync(channel, device, affectedChannels, reason);
                _daqRecoveryTasks[device] = recoveryTask;
            }
        }

        private async Task<DaqRecoveryResult> RecoverDaqDeviceAsync(
            int triggeringChannel,
            string device,
            int[] affectedChannels,
            string reason)
        {
            _log.Warn(
                $"DAQ安全恢复开始 Device={device} TriggerEPB={triggeringChannel} " +
                $"Affected=[{string.Join(",", affectedChannels)}] Reason={reason}",
                "AI");
            var result = await _acq.RecoverForEpbAsync(
                    triggeringChannel,
                    timeoutMs: 3000,
                    requiredFreshCallbacks: 10,
                    maxAgeMs: 100,
                    token: CancellationToken.None)
                .ConfigureAwait(false);
            try { DaqRecoveryStateChanged?.Invoke(result); } catch { }
            if (result.Recovered)
            {
                _log.Info(
                    $"DAQ安全恢复完成 Device={device} FreshCallbacks={result.FreshCallbacks}/" +
                    $"{result.RequiredFreshCallbacks} ElapsedMs={result.ElapsedMs}",
                    "AI");
                return result;
            }

            foreach (var channel in affectedChannels)
                OnRunnerAlarmRaised(
                    channel,
                    $"DaqGroupHardFault Device={device} RecoveryFailed={result.FailureReason}");
            PublishDaqGroupFault(
                device,
                "DaqRecoveryFailed",
                result.FailureReason,
                affectedChannels);
            return result;
        }

        private void PublishDaqGroupFault(
            string device,
            string code,
            string reason,
            int[] affectedChannels)
        {
            var fault = new ControlFault(
                code,
                $"Device={device} {reason}",
                FaultScope.DaqGroup,
                affectedChannels ?? Array.Empty<int>(),
                null,
                DateTime.UtcNow,
                Guid.NewGuid());
            _log.Error(
                $"DAQ组故障 [{code}] Device={device} Affected=[{string.Join(",", fault.AffectedChannels)}] " +
                $"CorrelationId={fault.CorrelationId:N} Reason={reason}",
                "AI");
            try { ControlFaultRaised?.Invoke(fault); } catch { }
        }

        private int[] GetDaqGroupChannels(string device)
        {
            return Enumerable.Range(1, 12)
                .Where(ch => string.Equals(
                    _acq.GetDeviceForEpbChannel(ch),
                    device,
                    StringComparison.OrdinalIgnoreCase))
                .Where(ch => IsHydraulicParticipant(ch) || _timers.ContainsKey(ch) || _runners.ContainsKey(ch))
                .ToArray();
        }

        private async Task WaitForDaqRecoveryAsync(int channel, CancellationToken token)
        {
            var device = _acq.GetDeviceForEpbChannel(channel);
            if (string.IsNullOrWhiteSpace(device)) return;
            if (!_daqRecoveryTasks.TryGetValue(device, out var task)) return;
            var completed = await Task.WhenAny(task, Task.Delay(3000, token)).ConfigureAwait(false);
            if (completed != task)
                throw new TimeoutException($"DaqRecoveryWaitTimeout Device={device}");
            var result = await task.ConfigureAwait(false);
            if (!result.Recovered)
                throw new InvalidOperationException(
                    $"DaqRecoveryFailed Device={device} Reason={result.FailureReason}");
        }

        private void OnPowerSupplyTelemetryUpdated(PowerSupplyTelemetry telemetry)
        {
            var recorder = _powerTelemetryRecorder;
            if (recorder != null && telemetry != null)
            {
                var group = _cfg.Test.Groups.FirstOrDefault(x => x.Id == telemetry.ElectricalGroupId);
                var cycle = group == null
                    ? 0
                    : group.Members
                        .Select(channel =>
                            _currentCycleNumberByChannel.TryGetValue(channel, out var value) ? value : 0)
                        .DefaultIfEmpty(0)
                        .Max();
                var stage = telemetry.Snapshot == null || !telemetry.Snapshot.OutputEnabled
                    ? "Preflight/Stopped"
                    : cycle > 0 ? "Running" : "Armed";
                recorder.Enqueue(telemetry, cycle, stage);
            }
            try { PowerSupplyTelemetryUpdated?.Invoke(telemetry); } catch { }
        }

        private void OnHydraulicFaultRaised(ControlFault fault)
        {
            if (fault == null) return;
            try { ControlFaultRaised?.Invoke(fault); } catch { }

            // 故障回调首先执行不等待IO的电机断电；液压控制器已在发布事件前回零输出。
            foreach (var channel in fault.AffectedChannels.Distinct())
            {
                try { CommandEpbOff(channel, "HydraulicFailSafe:" + fault.Code); } catch { }
                try { CancelStopCts(channel); } catch { }
                UnmarkHydraulicParticipant(channel);
                _hydraulicLeaseByChannel.TryRemove(channel, out _);
            }
            FlushPersistentLog();

            _ = Task.Run(async () =>
            {
                foreach (var channel in fault.AffectedChannels.Distinct().OrderBy(x => x))
                {
                    if (!_alarmStopLatch.TryRequestStop(channel)) continue;
                    try { ChannelAlarmRaised?.Invoke(channel, fault.Reason); } catch { }
                    try { StopChannelOnAlarm(channel); } catch { }
                    try
                    {
                        if (Alarm != null)
                            await Alarm.SetAlarmAsync(channel, true, fault.Reason).ConfigureAwait(false);
                    }
                    catch { }
                    try
                    {
                        await ExportAlarmSnapshotAsync(channel, fault.Reason, fault.TimestampUtc)
                            .ConfigureAwait(false);
                    }
                    catch { }
                }
            });
        }

        private void BeginPowerSupplyTelemetryRecording(Guid runId)
        {
            if (_powerSupply == null) return;
            var directory = System.IO.Path.Combine(
                _cfg.Test.StoreDir,
                _cfg.Test.TestName,
                "PowerSupplyTelemetry");
            var path = System.IO.Path.Combine(
                directory,
                $"{DateTime.Now:yyyyMMdd_HHmmss}_{runId:N}.csv");
            var next = new PowerSupplyTelemetryCsvRecorder(path, _log);
            var previous = Interlocked.Exchange(ref _powerTelemetryRecorder, next);
            previous?.Dispose();
            _log.Info($"程控电源连续遥测文件：{path}", "程控电源");
        }

        private void EndPowerSupplyTelemetryRecording()
        {
            var recorder = Interlocked.Exchange(ref _powerTelemetryRecorder, null);
            recorder?.Dispose();
        }

        private void OnPowerSupplyFaultRaised(PowerSupplyFault fault)
        {
            if (fault == null) return;
            CancelActiveLearningPhase();
            foreach (var channel in fault.AffectedChannels.Distinct())
            {
                try { CancelStopCts(channel); } catch { }
            }
            var controlFault = new ControlFault(
                "PowerSupply" + fault.Code,
                fault.Reason,
                FaultScope.ElectricalGroup,
                fault.AffectedChannels?.Distinct().ToArray() ?? Array.Empty<int>(),
                fault.ElectricalGroupId,
                fault.TimestampUtc == default ? DateTime.UtcNow : fault.TimestampUtc,
                Guid.NewGuid());
            try { ControlFaultRaised?.Invoke(controlFault); } catch { }
            try { PowerSupplyFaultRaised?.Invoke(fault); } catch { }

            _ = Task.Run(async () =>
            {
                var reason = $"PowerSupply[{fault.Code}] {fault.Reason}";
                foreach (var channel in fault.AffectedChannels.Distinct().OrderBy(x => x))
                {
                    if (!_alarmStopLatch.TryRequestStop(channel)) continue;
                    try { ChannelAlarmRaised?.Invoke(channel, reason); } catch { }
                    try { StopChannelOnAlarm(channel); } catch { }
                    try
                    {
                        if (Alarm != null)
                            await Alarm.SetAlarmAsync(channel, true, reason).ConfigureAwait(false);
                    }
                    catch { }

                    var alarmUtc = fault.TimestampUtc == default ? DateTime.UtcNow : fault.TimestampUtc;
                    AlarmCycleSnapshotEvidence snapshotEvidence = null;
                    try
                    {
                        snapshotEvidence = await ExportAlarmSnapshotAsync(channel, reason, alarmUtc)
                            .ConfigureAwait(false);
                    }
                    catch { }
                    if (snapshotEvidence == null)
                        TryFinalizeCurrentCycleAfterSnapshot(channel, false);
                    else
                        _currentCycleNumberByChannel.TryRemove(channel, out _);
                }

                // 先完成同组所有 EPB 高优先级断电，再关闭共享电源输出。
                try
                {
                    await _powerSupply.DisableGroupAsync(
                        fault.ElectricalGroupId,
                        reason,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch { }
                finally
                {
                    if (_powerSupply.ActiveGroups.Count == 0)
                        EndPowerSupplyTelemetryRecording();
                }
            });
        }

        private void OnRunnerWarningRaised(int channel, string reason)
        {
            _log.Warn($"EPB[{channel}] 自适应软预警：{reason}", "EPB");
            try { ChannelWarningRaised?.Invoke(channel, reason); }
            catch { /* UI 订阅者异常不得影响控制线程 */ }
        }

        private EpbAdaptiveProfile GetAdaptiveProfile(int channel)
        {
            try
            {
                return _adaptiveProfileStore?.GetOrCreate(channel) ??
                       new EpbAdaptiveProfile { Channel = channel };
            }
            catch (Exception ex)
            {
                _log.Warn($"EPB[{channel}] 加载自适应模型失败，使用空模型：{ex.Message}", "EPB");
                return new EpbAdaptiveProfile { Channel = channel };
            }
        }

        private void SaveAdaptiveProfile(EpbAdaptiveProfile profile)
        {
            if (profile == null || _adaptiveProfileStore == null) return;
            try
            {
                _adaptiveProfileStore.Save(profile);
            }
            catch (Exception ex)
            {
                _log.Warn($"EPB[{profile.Channel}] 保存自适应模型失败：{ex.Message}", "EPB");
            }
        }

        private EpbControlMode ReadEpbControlMode(EpbControlMode fallback)
        {
            try
            {
                var raw = ConfigurationManager.AppSettings["EpbControlMode"];
                return EpbControlModeParser.ParseOrDefault(raw, fallback);
            }
            catch (Exception ex)
            {
                _log.Warn($"读取 EpbControlMode 失败，使用 {fallback}：{ex.Message}", "EPB");
                return fallback;
            }
        }

        private bool ReadAdaptiveShadowMode(bool fallback)
        {
            try
            {
                var raw = ConfigurationManager.AppSettings["EpbAdaptiveShadowMode"];
                return bool.TryParse(raw, out var enabled) ? enabled : fallback;
            }
            catch (Exception ex)
            {
                _log.Warn($"读取 EpbAdaptiveShadowMode 失败，使用 {fallback}：{ex.Message}", "EPB");
                return fallback;
            }
        }

        private HashSet<int> ReadAdaptiveChannels()
        {
            var channels = new HashSet<int>();
            try
            {
                var raw = ConfigurationManager.AppSettings["EpbAdaptiveChannels"] ?? "10";
                foreach (var token in raw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (int.TryParse(token.Trim(), out var channel) && channel >= 1 && channel <= 12)
                        channels.Add(channel);
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"读取 EpbAdaptiveChannels 失败，回退 EPB10：{ex.Message}", "EPB");
            }

            if (channels.Count == 0) channels.Add(10);
            return channels;
        }

        private EpbControlMode GetEpbControlMode(int channel)
        {
            return _epbControlMode == EpbControlMode.AdaptiveCurrent && _adaptiveChannels.Contains(channel)
                ? EpbControlMode.AdaptiveCurrent
                : EpbControlMode.LegacyFixedTiming;
        }


        private async Task<AlarmCycleSnapshotEvidence> ExportAlarmSnapshotAsync(
            int alarmChannel,
            string reason,
            DateTime alarmUtc)
        {
            var recorder = Recorder;
            if (recorder == null) return null;
            if (!_currentCycleNumberByChannel.TryGetValue(alarmChannel, out var alarmCycleNumber))
                return null;

            // 快照去抖：同一通道在 cooldown 内只导出一次
            var cooldownMs = AlarmConfig?.Behavior?.SnapshotCooldownMs ?? 2000;
            var now = DateTime.UtcNow;
            lock (_lastAlarmSnapshotUtcByChannel)
            {
                if (_lastAlarmSnapshotUtcByChannel.TryGetValue(alarmChannel, out var last))
                {
                    if ((now - last).TotalMilliseconds < cooldownMs)
                        return null;
                }

                _lastAlarmSnapshotUtcByChannel[alarmChannel] = now;
            }

            var postOffTailMs = AlarmConfig?.Behavior?.SnapshotPostOffTailMs ?? 1000;
            postOffTailMs = Math.Max(0, Math.Min(3000, postOffTailMs));
            if (postOffTailMs > 0)
                await Task.Delay(postOffTailMs).ConfigureAwait(false);

            await _alarmSnapshotGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var lastN = AlarmConfig?.WarningSnapshots?.HardAlarmLastNCycles
                            ?? AlarmConfig?.Behavior?.SnapshotLastNCycles
                            ?? 10;
                lastN = Math.Max(1, lastN);

                // 根目录：StoreDir\TestName\AlarmSnapshots
                var baseDir = System.IO.Path.Combine(_cfg.Test.StoreDir, _cfg.Test.TestName, "AlarmSnapshots");
                System.IO.Directory.CreateDirectory(baseDir);

                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var snapshotDir = System.IO.Path.Combine(baseDir, $"{stamp}-EPB{alarmChannel:D2}");
                System.IO.Directory.CreateDirectory(snapshotDir);

                int[] running;
                try
                {
                    running = _timers.Keys.ToArray();
                }
                catch
                {
                    running = Array.Empty<int>();
                }

                // 先冻结报警通道，保证 PostOffTailMs 是明确边界；其它通道随后作为辅助证据导出。
                // StopChannelOnAlarm 通常已把报警通道从 _timers 移除，因此显式放到首位。
                running = new[] { alarmChannel }
                    .Concat(running.Where(channel => channel != alarmChannel))
                    .ToArray();

                AlarmCycleSnapshotEvidence alarmEvidence = null;
                foreach (var ch in running)
                {
                    var subName = ch == alarmChannel ? $"EPB{ch:D2}_ALARM" : $"EPB{ch:D2}";
                    var subDir = System.IO.Path.Combine(snapshotDir, subName);
                    System.IO.Directory.CreateDirectory(subDir);

                    try
                    {
                        if (ch == alarmChannel)
                        {
                            alarmEvidence = recorder.SealAndExportAlarmCycle(
                                ch,
                                alarmCycleNumber,
                                subDir,
                                DateTime.UtcNow);
                            if (alarmEvidence.IsValid)
                                recorder.FlushRecentTo(ch, lastN, subDir, includeRunningCycle: false);
                        }
                        else
                        {
                            recorder.FlushRecentTo(ch, lastN, subDir, includeRunningCycle: true);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Warn($"报警快照导出失败：EPB[{ch}] {ex.Message}", "落盘");
                    }
                }

                var alarmSubDir = System.IO.Path.Combine(snapshotDir, $"EPB{alarmChannel:D2}_ALARM");
                alarmEvidence ??= new AlarmCycleSnapshotEvidence
                {
                    CsvPath = System.IO.Path.Combine(
                        alarmSubDir,
                        $"EPB{alarmChannel}_Cycle_{alarmCycleNumber:D6}.csv"),
                    BinPath = System.IO.Path.Combine(
                        alarmSubDir,
                        $"EPB{alarmChannel}_Cycle_{alarmCycleNumber:D6}.bin"),
                    ValidationError = "报警圈未完成原子封存。"
                };

                // DO时间线和错峰计划是辅助证据；其写入失败不得改变当前报警圈
                // 由CSV/BIN完整性决定的 alarm/failed 结果。
                try
                {
                    ExportControlEvidence(
                        snapshotDir,
                        alarmChannel,
                        alarmCycleNumber,
                        reason,
                        alarmEvidence,
                        alarmUtc);
                }
                catch (Exception ex)
                {
                    _log.Warn($"报警控制证据导出失败：EPB[{alarmChannel}] {ex.Message}", "落盘");
                }

                try
                {
                    var electricalGroupId = GetElectricalGroupId(alarmChannel);
                    if (_powerSupply != null && electricalGroupId > 0)
                    {
                        var powerTelemetry = _powerSupply.GetRecentTelemetry(
                            electricalGroupId,
                            TimeSpan.FromSeconds(60));
                        PowerSupplyCoordinator.ExportTelemetryCsv(
                            System.IO.Path.Combine(snapshotDir, "power-supply-telemetry.csv"),
                            powerTelemetry,
                            alarmCycleNumber,
                            reason);
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn($"报警电源遥测导出失败：EPB[{alarmChannel}] {ex.Message}", "落盘");
                }

                try
                {
                    WriteAlarmSnapshotManifest(
                        snapshotDir,
                        alarmChannel,
                        alarmCycleNumber,
                        lastN,
                        reason);
                    WriteWarningChain(snapshotDir, alarmChannel, reason, alarmCycleNumber);
                }
                catch (Exception ex)
                {
                    _log.Warn($"报警快照清单写入失败：{ex.Message}", "落盘");
                }

                if (alarmEvidence.IsValid)
                    _log.Warn($"报警快照已导出：EPB[{alarmChannel}] {reason} -> {snapshotDir}", "落盘");
                else
                    _log.Warn(
                        $"报警快照校验失败：EPB[{alarmChannel}] Cycle={alarmCycleNumber} " +
                        $"Error={alarmEvidence.ValidationError} -> {snapshotDir}",
                        "落盘");

                return alarmEvidence;
            }
            finally
            {
                _alarmSnapshotGate.Release();
            }
        }


        /// <summary>
        /// 停止所有通道：依次调用 <see cref="StopChannel"/> ，
        /// 并做一次兜底清空，确保下一次开始是“干净环境”。 
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void StopAll()
        {
            var caller = new StackTrace().GetFrame(1)?.GetMethod()?.Name;
            StopAll(StopContext.Legacy(caller));
        }

        public void StopAll(StopContext context)
        {
            context ??= StopContext.Legacy(null);
            var safetyStartedUtc = DateTime.UtcNow;
            _log.Info(
                $"收到停止全部 EPB 请求：{context.ToLogText()}; SafetyActionStartedUtc={safetyStartedUtc:O}",
                "EPB");
            // 学习阶段尚未创建通道 Timer 时，也必须能通过控制层自己的 CTS 停止。
            EndBatchSession(cancel: true);

            var keys = _timers.Keys.Concat(_runners.Keys).Concat(_hydraulicParticipants.Keys)
                .Distinct().ToArray();
            for (int i = 0; i < keys.Length; i++)
                StopChannel(keys[i]);

            ClearChannelRuntimes(nameof(StopAll));
            _ = CompleteStopSafetyNoThrowAsync(context);
            _log.Info(
                $"全部 EPB 电机断电请求已提交：CorrelationId={context.CorrelationId}; MotorOffRequestedUtc={DateTime.UtcNow:O}",
                "EPB");
            FlushPersistentLog();
        }

        /// <summary>停止全部 EPB，并等待四台程控电源输出关闭回读完成。</summary>
        public async Task StopAllAsync(CancellationToken token = default)
        {
            await StopAllAsync(StopContext.Legacy(nameof(StopAllAsync)), token).ConfigureAwait(false);
        }

        public async Task StopAllAsync(StopContext context, CancellationToken token = default)
        {
            context ??= StopContext.Legacy(null);
            var safetyStartedUtc = DateTime.UtcNow;
            _log.Info(
                $"收到停止全部 EPB 请求：{context.ToLogText()}; SafetyActionStartedUtc={safetyStartedUtc:O}",
                "EPB");
            EndBatchSession(cancel: true);
            var keys = _timers.Keys
                .Concat(_runners.Keys)
                .Concat(_hydraulicParticipants.Keys)
                .Distinct()
                .ToArray();
            foreach (var channel in keys)
            {
                try { StopChannel(channel); } catch { }
            }
            ClearChannelRuntimes(nameof(StopAllAsync));
            await CompleteStopSafetyAsync(context, token).ConfigureAwait(false);
        }

        private async Task CompleteStopSafetyAsync(StopContext context, CancellationToken token)
        {
            try
            {
                var hydraulicIds = _cfg.Test.Hydraulics.Select(x => x.Id).Distinct().ToArray();
                var releaseTasks = hydraulicIds.Select(id =>
                    _hydCoordinator.ForceReleaseAsync(id, $"StopAll:{context.Source}"));
                await Task.WhenAll(releaseTasks).ConfigureAwait(false);
                var pressureSafeUtc = DateTime.UtcNow;

                if (_powerSupply != null)
                    await _powerSupply.DisableAllAsync(
                            $"StopAll Source={context.Source} CorrelationId={context.CorrelationId}",
                            token)
                        .ConfigureAwait(false);

                _log.Info(
                    $"StopAll 安全收尾完成：CorrelationId={context.CorrelationId}; " +
                    $"PressureSafeConfirmedUtc={pressureSafeUtc:O}; " +
                    $"MotorPowerOffConfirmedUtc={DateTime.UtcNow:O}; CompletedUtc={DateTime.UtcNow:O}",
                    "EPB");
            }
            catch (Exception ex)
            {
                _log.Error(
                    $"StopAll 安全收尾未完全确认：CorrelationId={context.CorrelationId}; {ex.Message}",
                    "EPB",
                    ex);
                throw;
            }
            finally
            {
                EndPowerSupplyTelemetryRecording();
                FlushPersistentLog();
            }
        }

        private async Task CompleteStopSafetyNoThrowAsync(StopContext context)
        {
            try
            {
                await CompleteStopSafetyAsync(context, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // CompleteStopSafetyAsync 已持久化详细故障；兼容同步入口不能产生未观察任务异常。
            }
        }

        private async Task DisableAllPowerSafeAsync(string reason)
        {
            try
            {
                if (_powerSupply != null)
                    await _powerSupply.DisableAllAsync(reason, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) { _log.Error($"程控电源关闭未完全确认：{ex.Message}", "程控电源", ex); }
            finally { EndPowerSupplyTelemetryRecording(); }
        }

        private void TryDisableIdlePowerGroup(int channel, string reason)
        {
            if (_powerSupply == null) return;
            var groupId = GetElectricalGroupId(channel);
            if (groupId <= 0) return;
            var members = _cfg.Test.Groups.FirstOrDefault(x => x.Id == groupId)?.Members ?? new List<int>();
            if (!IsPowerGroupIdle(members, IsHydraulicParticipant, x => _timers.ContainsKey(x))) return;
            _ = Task.Run(async () =>
            {
                try { await _powerSupply.DisableGroupAsync(groupId, reason, CancellationToken.None).ConfigureAwait(false); }
                catch { }
                finally
                {
                    if (_powerSupply.ActiveGroups.Count == 0)
                        EndPowerSupplyTelemetryRecording();
                }
            });
        }

        internal static bool IsPowerGroupIdle(
            IEnumerable<int> members,
            Func<int, bool> isHydraulicParticipant,
            Func<int, bool> hasRunningTimer)
        {
            if (members == null) return true;
            return !members.Any(
                channel =>
                    (isHydraulicParticipant?.Invoke(channel) ?? false) ||
                    (hasRunningTimer?.Invoke(channel) ?? false));
        }

        private int GetElectricalGroupId(int channel)
        {
            return _cfg.Test.Groups.FirstOrDefault(x => x.Members.Contains(channel))?.Id ?? 0;
        }

        internal void RequestElectricalGroupEmergencyShutdown(int sourceChannel, string reason)
        {
            CancelActiveLearningPhase();
            var groupId = GetElectricalGroupId(sourceChannel);
            if (groupId <= 0)
            {
                _log.Error(
                    $"EPB[{sourceChannel}] 请求电源组紧急关闭，但未找到电气组映射。Reason={reason}",
                    "程控电源");
                return;
            }
            if (!_emergencyPowerGroupLatch.TryAdd(groupId, 0)) return;

            var members = _cfg.Test.Groups
                .FirstOrDefault(x => x.Id == groupId)?
                .Members
                .Distinct()
                .OrderBy(x => x)
                .ToArray() ?? new[] { sourceChannel };

            _log.Error(
                $"电源组{groupId}触发失效安全联锁：Source=EPB{sourceChannel} " +
                $"Affected=[{string.Join(",", members)}] Reason={reason}",
                "程控电源");

            // 先在当前线程阻止同组任何通道继续执行，并逐路发出高优先级DO关闭；
            // 网络电源关闭及回读随后独立执行，不能阻塞采样回调。
            foreach (var member in members)
            {
                try { UnmarkHydraulicParticipant(member); } catch { }
                try { CancelStopCts(member); } catch { }
                RemoveTimerRuntime(member, nameof(RequestElectricalGroupEmergencyShutdown));
                RemoveRunnerRuntime(member, nameof(RequestElectricalGroupEmergencyShutdown));
                try { CommandEpbOffHighPriority(member, nameof(RequestElectricalGroupEmergencyShutdown)); }
                catch { }
                try { _ = HydraulicMarkReleaseAsync(member); }
                catch { }
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    if (_powerSupply != null)
                        await _powerSupply.DisableGroupAsync(
                                groupId,
                                $"EPB失效安全联锁 Source={sourceChannel} Reason={reason}",
                                CancellationToken.None)
                            .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Error(
                        $"电源组{groupId}紧急关闭未得到可靠回读：{ex.Message}",
                        "程控电源",
                        ex);
                }
                finally
                {
                    if (_powerSupply == null || _powerSupply.ActiveGroups.Count == 0)
                        EndPowerSupplyTelemetryRecording();
                }
            });
        }

        private void EnsureStrictCurveControl(IEnumerable<int> channels)
        {
            if (_requirePowerSupply && _powerSupply == null)
                throw new InvalidOperationException(
                    "程控电源闭环未初始化，禁止启动。请检查 PowerSupplyConfig.xml。");
            foreach (var channel in channels)
            {
                if (GetEpbControlMode(channel) != EpbControlMode.AdaptiveCurrent)
                    throw new InvalidOperationException(
                        $"EPB{channel} 未启用 AdaptiveCurrent 严格完整曲线控制，禁止启动正式试验。");
            }
            if (_adaptiveShadowMode)
                throw new InvalidOperationException("EpbAdaptiveShadowMode=true 时只观察不保护，禁止启动正式试验。");
        }

        private void EnsureAdaptiveProfilesReady(IEnumerable<int> channels)
        {
            var notReady = (channels ?? Enumerable.Empty<int>())
                .Distinct()
                .Where(channel => !GetAdaptiveProfile(channel).IsStable)
                .OrderBy(channel => channel)
                .ToArray();
            if (notReady.Length > 0)
                throw new InvalidOperationException(
                    $"严格完整曲线基线尚未形成：EPB[{string.Join(",", notReady)}]。" +
                    "每路至少需要5个完整有效学习圈，禁止进入正式试验。");
        }

        private static bool ReadBooleanAppSetting(string key, bool fallback)
        {
            try
            {
                var raw = ConfigurationManager.AppSettings[key];
                return bool.TryParse(raw, out var value) ? value : fallback;
            }
            catch
            {
                return fallback;
            }
        }


        // —— 反射兜底读取配置字段（兼容不同旧配置命名）—— //
        private static T GetProp<T>(object obj, string name)
        {
            var p = obj.GetType().GetProperty(name);
            if (p == null) return default;
            var v = p.GetValue(obj);
            if (v == null) return default;
            return (T)Convert.ChangeType(v, typeof(T));
        }

        private static T GetProp<T>(object obj, params string[] tryNames)
        {
            foreach (var n in tryNames)
            {
                var p = obj.GetType().GetProperty(n);
                if (p == null) continue;
                var v = p.GetValue(obj);
                if (v == null) continue;
                return (T)Convert.ChangeType(v, typeof(T));
            }

            return default;
        }


        // —— 圈开始（如仍保留该方法供其他调用）
        private void OnCycleBegin(int epbId, int cycleNumber)
        {
            Recorder?.BeginCycle(epbId, cycleNumber, DateTime.UtcNow);
        }

        // —— 圈结束
        private void OnCycleComplete(int epbId, int cycleNumber)
        {
            var finalN = Recorder?.GetCurrentCycleSampleCount(epbId) ?? 0;
            CompleteCycleAndScheduleEvidence(
                Recorder,
                epbId,
                cycleNumber,
                finalN,
                DateTime.UtcNow);
        }


        #region 卡钳预释放

        /// <summary>
        /// 批量执行“预释放”（反向进入空行程并保持）。
        /// </summary>
        /// <param name="channels">要执行预释放的通道号（1..12）。</param>
        /// <param name="keepMs">
        /// 反向空行程保持时长（毫秒）。为 <c>null</c> 时，每个通道使用其 Runner 的默认值
        ///（通常来自配置字段 <c>_revEmptyKeepMs</c>）。
        /// </param>
        /// <param name="token">取消令牌。</param>
        /// <returns>全部通道任务完成的 <see cref="Task"/>。</returns>
        /// <remarks>
        /// - 按本次选中集合生成XML驱动的不可变错峰计划；不同电源组可并行，同组按计划相位启动。<br/>
        /// - 预释放同样受实际压力资格联锁保护，压力未达标时不会给卡钳电机上电。
        /// </remarks>
        public async Task PreReleaseBatchAsync(int[] channels, int? keepMs, CancellationToken token)
        {
            if (channels == null || channels.Length == 0)
                throw new ArgumentException("channels 不能为空。", nameof(channels));

            var enabled = channels.Distinct().OrderBy(x => x).ToArray();
            var plan = ElectricalStaggerPlanner.Build(enabled, _cfg.Test.Groups, PeriodMs);
            var runId = Guid.NewGuid();
            RegisterRunContext(runId, plan);
            LogStaggerPlan(runId, plan);
            var failed = await PreReleaseBatchWithPlanAsync(enabled, keepMs, plan, token).ConfigureAwait(false);
            EnsurePreReleaseBatchSucceeded(failed, _log);
        }

        /// <summary>
        /// 保留原有公共签名以兼容调用方。错峰值统一从XML电气组读取，
        /// <paramref name="deltaMs"/> 不再参与安全调度。
        /// </summary>
        public async Task PreReleaseBatchStaggeredAsync(
            int[] channels,
            int? keepMs,
            int deltaMs,
            CancellationToken token)
        {
            if (channels == null || channels.Length == 0)
                throw new ArgumentException("channels 不能为空。", nameof(channels));

            var enabled = channels.Distinct().OrderBy(x => x).ToArray();
            var plan = ElectricalStaggerPlanner.Build(enabled, _cfg.Test.Groups, PeriodMs);
            var runId = Guid.NewGuid();
            RegisterRunContext(runId, plan);
            LogStaggerPlan(runId, plan);
            _log.Warn(
                $"PreReleaseBatchStaggeredAsync 的 deltaMs={deltaMs} 已忽略；实际使用XML ElectricalGroups/StaggerMs。",
                "EPB");
            var failed = await PreReleaseBatchWithPlanAsync(enabled, keepMs, plan, token).ConfigureAwait(false);
            EnsurePreReleaseBatchSucceeded(failed, _log);
        }

        /// <summary>
        /// 按批次不可变错峰计划启动预释放。所有任务一次性创建，不等待前一相位完成。
        /// </summary>
        private async Task<int[]> PreReleaseBatchWithPlanAsync(
            int[] channels,
            int? keepMs,
            ElectricalStaggerPlan staggerPlan,
            CancellationToken token)
        {
            if (channels == null || channels.Length == 0)
                throw new ArgumentException("channels 不能为空。", nameof(channels));
            if (staggerPlan == null)
                throw new ArgumentNullException(nameof(staggerPlan));

            var enabled = channels.Distinct().OrderBy(x => x).ToArray();
            var anchorUtc = DateTime.UtcNow.AddMilliseconds(500);
            var failedChannels = new ConcurrentBag<int>();
            var runId = _activeBatchId == Guid.Empty ? Guid.NewGuid() : _activeBatchId;

            // P0：任何预释放电机动作前，先按压力组完成实际压力资格。
            foreach (var group in enabled.GroupBy(ch => ch <= 6 ? 1 : 2))
            {
                var members = group.ToArray();
                await HydraulicEnterAtGroupAnchorAsync(
                        new HydraulicGenerationKey(runId, group.Key, HydraulicPhaseKind.PreRelease, 0),
                        members,
                        token)
                    .ConfigureAwait(false);
            }

            try
            {
                await ElectricalStaggerExecutor.RunAsync(
                    enabled,
                    staggerPlan,
                    anchorUtc,
                    async (ch, ct) =>
                    {
                    var assignment = staggerPlan.Get(ch);
                    var plannedStartUtc = anchorUtc.AddMilliseconds(assignment.PhaseMs);
                    var actualStartUtc = DateTime.UtcNow;
                    MarkElectricalPhaseDue(ch, plannedStartUtc);
                    _log.Info(
                        $"EPB[{ch}] 预释放计划启动：Group={assignment.ElectricalGroupId}，" +
                        $"Index={assignment.SelectedIndexInGroup}，Phase={assignment.PhaseMs}ms，" +
                        $"PlannedUtc={plannedStartUtc:O}，ActualUtc={actualStartUtc:O}，" +
                        $"DeviationMs={(actualStartUtc - plannedStartUtc).TotalMilliseconds:F3}。",
                        "EPB");
                    var runner = GetRunner(ch);
                    var baseDetectMs = Math.Max(1, runner.DefaultPreReleaseDetectTimeoutMs);
                    var released = false;
                    for (var attempt = 0; attempt < 3 && !released; attempt++)
                    {
                        var detectMs = Math.Min(baseDetectMs * 2, baseDetectMs + attempt * 500);
                        released = await runner.PreReleaseAsync(keepMs, detectMs, ct).ConfigureAwait(false);
                        if (!released && attempt < 2)
                        {
                            _log.Warn(
                                $"EPB[{ch}] 预释放第{attempt + 1}次未确认，已断电250ms后按有界预算重试；" +
                                "过流和绝对上电保护不放宽。",
                                "EPB");
                            await Task.Delay(250, ct).ConfigureAwait(false);
                        }
                    }
                    if (!released)
                        failedChannels.Add(ch);
                    },
                    token).ConfigureAwait(false);
            }
            finally
            {
                var releases = enabled.Select(HydraulicMarkReleaseAsync).ToArray();
                try { await Task.WhenAll(releases).ConfigureAwait(false); } catch { }
            }

            var failed = failedChannels.Distinct().OrderBy(x => x).ToArray();
            if (failed.Length == 0) return Array.Empty<int>();

            foreach (var channel in failed)
            {
                try { CommandEpbOff(channel, nameof(PreReleaseBatchWithPlanAsync)); }
                catch (Exception ex)
                {
                    _log.Warn($"预释放失败回滚时 EPB[{channel}] 断电命令异常：{ex.Message}", "EPB");
                }
            }

            return failed;
        }

        internal static void EnsurePreReleaseBatchSucceeded(
            IEnumerable<int> failedChannels,
            IAppLogger logger = null)
        {
            var failed = (failedChannels ?? Enumerable.Empty<int>())
                .Distinct()
                .OrderBy(x => x)
                .ToArray();
            if (failed.Length == 0) return;

            var message =
                $"批量预释放失败：通道[{string.Join(",", failed)}]未在检测预算内确认进入反向空行程；" +
                "已拒绝进入学习/正式阶段。";
            logger?.Error(message, "EPB");
            throw new InvalidOperationException(message);
        }

        #endregion
    }
}
