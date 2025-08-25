using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Config;
using IO.NI;
using Timing;
using IAppLogger = Config.IAppLogger;
using NullLogger = Config.NullLogger;

namespace Controller
{
    /// <summary>
    /// 12个卡钳统一编排：同组电控“首启”错峰（液压不延时），
    /// 每通道独立高精度定时器，可单独暂停/恢复/结束。
    /// </summary>
    public sealed class EpbManager
    {
        // EpbManager 字段区
        private readonly GlobalConfig _cfg;
        private readonly DoController _do;
        private readonly AoController _ao;
        private readonly HydraulicController _hydraulic;
        private readonly Dictionary<int, HighPrecisionTimer> _timers = new();
        private readonly IAppLogger _log;

        private TwoDeviceAiAcquirer _acq; // ★ 新增：数据采集器
        private HydraulicGroupCoordinator _hydCoordinator; // ★ 新增：液压组协调器


        // —— 回调（采样） —— //
        private readonly EpbCycleRunner.ReadCurrentDelegate _readCurrent;

        public EpbManager(
            GlobalConfig cfg,
            DoController doController,
            HydraulicController hydraulic,
            EpbCycleRunner.ReadCurrentDelegate readCurrent,
            IAppLogger log = null)
        {
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            _do = doController ?? throw new ArgumentNullException(nameof(doController));
            _hydraulic = hydraulic ?? throw new ArgumentNullException(nameof(hydraulic));
            _readCurrent = readCurrent ?? (_ => 0.0);
            _log = log ?? NullLogger.Instance;

            // ★ 尝试创建协调器（此构造器拿不到 AO 和读压委托；仅当你的 HydraulicController 内部提供 BeginHold / RequestRelease 时才可用）
            //   若暂时拿不到 AO/读压，可先传 null；协调器将使用 HydraulicController 的保持/释放能力。
            try
            {
                _hydCoordinator = new HydraulicGroupCoordinator(
                    _cfg.Test,
                    _cfg.DO,
                    _do,
                    readPressure: null, // 没有也没关系：协调器优先走 HydraulicController 的保持模式
                    aoController: null, // 无 AO 也可：不会走 Fallback
                    hydCtl: _hydraulic,
                    log: _log);
            }
            catch (Exception ex)
            {
                _log.Warn($"液压组协调器初始化（构造器1）失败：{ex.Message}。将跳过“组内统一释压”增强逻辑。", "液压协调");
                _hydCoordinator = null;
            }
        }

        public EpbManager(
            GlobalConfig cfg,
            DoController doController,
            AoController aoController,
            TwoDeviceAiAcquirer acq,
            IAppLogger log = null)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            if (doController == null) throw new ArgumentNullException(nameof(doController));
            if (aoController == null) throw new ArgumentNullException(nameof(aoController));
            if (acq == null) throw new ArgumentNullException(nameof(acq));

            _cfg = cfg;
            _do = doController;
            _ao = aoController;
            _readCurrent = acq.ReadCurrent;
            _log = log ?? NullLogger.Instance;
            _acq = acq;

            _hydraulic = new HydraulicController(
                _do,
                _cfg.Test,
                acq.ReadPressure,
                aoController,
                _log);

            // ★ 创建协调器（具备 AO 与读压，可用 Fallback/保持两种实现）
            _hydCoordinator = new HydraulicGroupCoordinator(
                _cfg.Test,
                _cfg.DO,
                _do,
                readPressure: acq.ReadPressure,
                aoController: aoController,
                hydCtl: _hydraulic,
                log: _log);
        }

        /// <summary>启动指定 EPB 通道（跑 TestTarget 圈）</summary>
        public void StartChannel(int channel)
        {
            if (_timers.ContainsKey(channel))
            {
                _log.Warn($"EPB[{channel}] 已在运行。", "EPB");
                return;
            }

            // 1) 液压编号（1:1..6；2:7..12）
            int hydId = channel <= 6 ? 1 : 2;

            // ★ 接入点：创建液压组协调器（建议放在 TestConfig/DO/AO/NI 初始化之后）
            _hydCoordinator = new HydraulicGroupCoordinator(
                _cfg.Test,
                _cfg.DO,
                _do,
                ReadPressure, // 你现有的读压委托/方法
                _ao,
                _hydraulic, // 你现有的 Controller.HydraulicController 实例；没有可传 null
                _log);


            // 2) 取 Runner 参数
            var rcfg = _cfg.Test?.EpbCycleRunner ?? new EpbCycleRunnerConfig();
            int periodMs = _cfg.Test.PeriodMs;
            int sampleMs = 2; // 建议 2~5ms

            // 3) 取阈值/保持时间
            var limitRecord = _cfg.Test.EpbLimits
                .FirstOrDefault(x => GetProp<int>(x, "Channel") == channel);
            if (limitRecord == null)
                throw new InvalidOperationException($"未配置 EPB[{channel}] 电流限值。");

            double forwardA = GetProp<double>(limitRecord, "ForwardA", "PosCurrentA", "PosThresholdA",
                "ForwardThresholdA");
            double reverseA = GetProp<double>(limitRecord, "ReverseA", "NegCurrentA", "NegThresholdA",
                "ReverseThresholdA"); // 预留
            int holdMs = GetProp<int>(limitRecord, "HoldMs", "HoldTimeMs", "HoldDurationMs");

            holdMs = 1000; //设置为 1000ms（1秒），可根据实际需要调整，调试使用


            // 4) 组内首启错峰（只对电控生效；液压不延时）
            int staggerMs = 0;
            foreach (var g in _cfg.Test.Groups)
            {
                if (g.Members.Contains(channel))
                {
                    int indexInGroup = g.Members.OrderBy(x => x).ToList().IndexOf(channel);
                    staggerMs = g.StaggerMs * Math.Max(0, indexInGroup);
                    _log.Info($"EPB[{channel}] 归属组 {g.Id} 首启错峰 {staggerMs}ms（组内位置={indexInGroup}）", "EPB");
                    break;
                }
            }

            // 5) 定时器
            var timer = new HighPrecisionTimer(periodMs, _cfg.Test.OverrunPolicy, _log);
            _timers[channel] = timer;

            // 6) 构造 Runner（签名不变）
            var runner = new EpbCycleRunner(
                channel,
                hydId,
                _readCurrent,
                _do,
                _hydraulic,
                posThresholdA: forwardA,
                holdMs: holdMs,
                sampleMs: sampleMs,
                peakIgnoreMs: rcfg.PeakIgnoreMs,
                ewmaAlpha: rcfg.EwmaAlpha,
                emptyBandA: rcfg.EmptyBandA,
                stableWinMs: rcfg.StableWinMs,
                log: _log,
                manager: this);

            

            // 6.5) 启动前自学习（保持异步，不阻塞 UI）
            int learnCycles = GetProp<int>(rcfg, "LearnCycles");
            if (learnCycles <= 0) learnCycles = 5; // 默认 3
            if (learnCycles > 0)
            {
                _log.Info($"EPB[{channel}] 启动前自学习 {learnCycles} 次。", "EPB");
                try
                {
                    runner.LearnAsync(learnCycles, new CancellationToken())
                        .GetAwaiter().GetResult();
                    _log.Info($"EPB[{channel}] 自学习完成，进入正式试验。", "EPB");
                }
                catch (Exception ex)
                {
                    _log.Warn($"EPB[{channel}] 自学习异常：{ex.Message}，仍将尝试进入正式试验。", "EPB");
                }
            }

            // 7) 高精度定时循环
            timer.StartAsync(_cfg.Test.TestTarget, staggerMs, async (i, token) =>
            {
                _log.Info($"EPB[{channel}] 周期 {i}/{_cfg.Test.TestTarget} 开始。", "EPB");
                var ok = await runner.RunOneAsync(periodMs, token);
                _log.Info($"EPB[{channel}] 周期 {i}/{_cfg.Test.TestTarget} {(ok ? "完成" : "失败")}", "EPB");
                return ok;
            });
        }

        /// <summary>
        /// 读取指定液压组的压力值（委托给 HydraulicController）
        /// </summary>
        /// <param name="hydId"></param>
        /// <returns></returns>
        private double ReadPressure(int hydId)
        {
            return _acq.ReadPressure(hydId);
        }

        // EpbManager.cs 里（EpbManager 类内）新增：
        public Task HydraulicEnterAsync(int channel, CancellationToken token)
            => _hydCoordinator?.EnterElectricalPhaseAsync(channel, token) ?? Task.CompletedTask;

        public Task HydraulicMarkReleaseAsync(int channel)
            => _hydCoordinator?.MarkVoltageReleaseAsync(channel) ?? Task.CompletedTask;


        // （保留你已有的 StartChannelAsync / Pause/Resume/Stop 等实现，不改对外签名）
        public async Task StartChannelAsync(int channel, CancellationToken uiToken = default)
        {
            if (_timers.ContainsKey(channel))
            {
                _log.Warn($"EPB[{channel}] 已在运行。", "EPB");
                return;
            }

            int hydId = channel <= 6 ? 1 : 2;

            var rcfg = _cfg.Test?.EpbCycleRunner ?? new EpbCycleRunnerConfig();
            int periodMs = _cfg.Test.PeriodMs;
            int sampleMs = 2;

            var limitRecord = _cfg.Test.EpbLimits
                .FirstOrDefault(x => GetProp<int>(x, "Channel") == channel);
            if (limitRecord == null)
                throw new InvalidOperationException($"未配置 EPB[{channel}] 电流限值。");

            double forwardA = GetProp<double>(limitRecord, "ForwardA", "PosCurrentA", "PosThresholdA",
                "ForwardThresholdA");
            int holdMs = GetProp<int>(limitRecord, "HoldMs", "HoldTimeMs", "HoldDurationMs");
            holdMs = 1000; // 设置为 1000ms（1秒），可根据实际需要调整，调试使用

            int staggerMs = 0;
            foreach (var g in _cfg.Test.Groups)
            {
                if (g.Members.Contains(channel))
                {
                    int indexInGroup = g.Members.OrderBy(x => x).ToList().IndexOf(channel);

                    staggerMs = g.StaggerMs * Math.Max(0, indexInGroup);
                    _log.Info($"EPB[{channel}] 归属组 {g.Id} 首启错峰 {staggerMs}ms（组内位置={indexInGroup}）", "EPB");
                    break;
                }
            }

            //日志记录周期
            _log.Info($"高精度定时器，  EPB[{channel}] 周期 {periodMs}ms，采样 {sampleMs}ms，前进阈值 {forwardA}A，保持时间 {holdMs}ms",
                "EPB");

            var timer = new HighPrecisionTimer(periodMs, _cfg.Test.OverrunPolicy, _log);
            _timers[channel] = timer;


            var runner = new EpbCycleRunner(
                channel,
                hydId,
                _readCurrent,
                _do,
                _hydraulic,
                posThresholdA: forwardA,
                holdMs: holdMs,
                sampleMs: sampleMs,
                peakIgnoreMs: rcfg.PeakIgnoreMs,
                ewmaAlpha: rcfg.EwmaAlpha,
                emptyBandA: rcfg.EmptyBandA,
                stableWinMs: rcfg.StableWinMs,
                log: _log,
                manager:this);

            int learnCycles = GetProp<int>(rcfg, "LearnCycles");
            if (learnCycles <= 0) learnCycles = 5;

            if (learnCycles > 0)
            {
                _log.Info($"EPB[{channel}] 启动前自学习 {learnCycles} 次。", "EPB");
                try
                {
                    await runner.LearnAsync(learnCycles, uiToken).ConfigureAwait(false);
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
            // 暂时注释掉正式运行

            timer.StartAsync(_cfg.Test.TestTarget, staggerMs, async (i, token) =>
            {
                _log.Info($"EPB[{channel}] 周期 {i}/{_cfg.Test.TestTarget} 开始。", "EPB");
                var ok = await runner.RunOneAsync(periodMs, token).ConfigureAwait(false);
                _log.Info($"EPB[{channel}] 周期 {i}/{_cfg.Test.TestTarget} {(ok ? "完成" : "失败")}", "EPB");
                return ok;
            });
        }

        public void PauseChannel(int channel)
        {
            if (_timers.TryGetValue(channel, out var t)) t.Pause();
        }

        public void ResumeChannel(int channel)
        {
            if (_timers.TryGetValue(channel, out var t)) t.Resume();
        }

        public void StopChannel(int channel)
        {
            if (_timers.TryGetValue(channel, out var t)) t.Stop();
            _timers.Remove(channel);
            _do.SetEpbOff(channel); // 安全落位
        }

        public void StopAll()
        {
            foreach (var ch in _timers.Keys.ToArray()) StopChannel(ch);
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
    }
}