using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Config;
using IO.NI;
using Timing;
using IAppLogger = Config.IAppLogger;
using NullLogger = Config.NullLogger;

namespace Controller
{
    /// <summary>
    /// 12 个卡钳统一编排：同组电控“首启”错峰（液压不延时），
    /// 每通道独立高精度定时器，可单独暂停/恢复/结束。
    /// </summary>
    public sealed class EpbManager
    {
        private readonly GlobalConfig _cfg;
        private readonly DoController _do;
        private readonly HydraulicController _hydraulic;
        private readonly Dictionary<int, HighPrecisionTimer> _timers = new();
        private readonly IAppLogger _log;

        // —— 回调 —— //
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
        }

        /// <summary>
        /// 便捷构造：直接把 TwoDeviceAiAcquirer 与 AoController 串起来。
        /// </summary>
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
            _readCurrent = acq.ReadCurrent;
            _log = log ?? NullLogger.Instance;

            // HydraulicController 需要：压力读取 + AO 百分比设置
            _hydraulic = new HydraulicController(
                _do,
                _cfg.Test,
                acq.ReadPressure,
                aoController,
                _log);
        } 
        
        /// <summary>
        /// 启动指定 EPB 通道的高精度循环（按 TestTarget 次数）。
        /// </summary>
        public void StartChannel(int channel)
        {
            if (_timers.ContainsKey(channel))
            {
                _log.Warn($"EPB[{channel}] 已在运行。", "EPB");
                return;
            }

            // 推断液压编号（1: 1..6；2: 7..12）
            int hydId = channel <= 6 ? 1 : 2;

            // 取电流阈值与时序（从 Test.EpbLimits 中找到对应 channel 的记录）
            var limitRecord = _cfg.Test.EpbLimits.FirstOrDefault(x => GetProp<int>(x, "Channel") == channel);
            if (limitRecord == null)
                throw new InvalidOperationException($"未配置 EPB[{channel}] 电流阈值。");

            // 兼容不同命名：尝试多种字段名
            double posA = GetProp<double>(limitRecord, "PosCurrentA", "PosThresholdA", "ForwardCurrentA", "ForwardThresholdA");
            double negA = GetProp<double>(limitRecord, "NegCurrentA", "NegThresholdA", "ReverseCurrentA", "ReverseThresholdA");
            int fwdMs = GetProp<int>(limitRecord, "ForwardTimeoutMs", "ForwardMaxMs", "FwdMaxMs", "FwdTimeoutMs");
            int revMs = GetProp<int>(limitRecord, "ReverseTimeoutMs", "ReverseMaxMs", "RevMaxMs", "RevTimeoutMs");
            int holdMs = GetProp<int>(limitRecord, "HoldMs", "HoldTimeMs", "HoldDurationMs");

            // 计算“首启”错峰（仅电控；液压不延时）
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

            var timer = new HighPrecisionTimer(_cfg.Test.PeriodMs, _cfg.Test.OverrunPolicy, _log);
            _timers[channel] = timer;

            // var runner = new EpbCycleRunner(
            //     channel,
            //     hydId,
            //     _readCurrent,
            //     _do,
            //     _hydraulic,
            //     posA, negA,
            //     fwdMs, revMs,
            //     holdMs,
            //     _log);  // 暂时注释

            // 调试使用
            var runner = new EpbCycleRunner(
                channel,
                hydId,
                _readCurrent,
                _do,
                _hydraulic,
                3, 1,
                600000, 60000,
                0,
                _log);

            // 指定次数
            timer.StartAsync(_cfg.Test.TestTarget, staggerMs, async (i, token) =>
            {
                _log.Info($"EPB[{channel}] 周期 {i}/{_cfg.Test.TestTarget} 开始。", "EPB");
                var ok = await runner.RunOneAsync(token);
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

        // —— 反射兜底读取配置字段 —— //
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
