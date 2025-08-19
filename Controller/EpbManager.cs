using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Config;
using IO.NI;
using Timing;
using IAppLogger = Config.IAppLogger;
using NullLogger = Config.NullLogger;

// Epb/EpbManager.cs

namespace Controller
{
    /// <summary>
    /// 12 个卡钳统一编排：同组电控“首启”错峰（Hydraulic 不延时），
    /// 每个卡钳各自一个高精度定时器，可单独暂停/恢复/结束。
    /// </summary>
    public sealed class EpbManager
    {
        private readonly GlobalConfig _cfg;
        private readonly DoController _do;
        private readonly HydraulicController _hydraulic;
        private readonly Dictionary<int, HighPrecisionTimer> _timers = new();
        private readonly Dictionary<int, CancellationTokenSource> _cts = new();
        private readonly IAppLogger _log;

        // 回调：从你的采集链路获取电流/压力
        private readonly EpbCycleRunner.ReadCurrentDelegate _readCurrent;
        private readonly HydraulicController.ReadPressureDelegate _readPressure;
        private readonly HydraulicController.SetAoPercentDelegate _setAoPercent;

        public EpbManager(GlobalConfig cfg,
                          DoController doController,
                          EpbCycleRunner.ReadCurrentDelegate readCurrent,
                          HydraulicController.ReadPressureDelegate readPressure,
                          HydraulicController.SetAoPercentDelegate setAoPercent,
                          IAppLogger log = null)
        {
            _cfg = cfg;
            _do = doController;
            _readCurrent = readCurrent;
            _readPressure = readPressure;
            _setAoPercent = setAoPercent;
            _log = log ?? NullLogger.Instance;

            _hydraulic = new HydraulicController(_do, _cfg.Test, _readPressure, _setAoPercent, _log);
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

            // 推断该通道所属液压编号（1: 1~6；2: 7~12）
            int hydId = channel <= 6 ? 1 : 2;

            // 找到该通道电流阈值
            var limit = _cfg.Test.EpbLimits.FirstOrDefault(x => x.Channel == channel)
                        ?? throw new InvalidOperationException($"未配置 EPB[{channel}] 电流阈值。");

            // 计算“首启”错峰（电控-only；液压不延时）
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
            var cts = new CancellationTokenSource();
            _timers[channel] = timer;
            _cts[channel] = cts;

            var runner = new EpbCycleRunner(channel, hydId, limit, _do, _hydraulic, _readCurrent, _log);

            // 启动：指定次数
            timer.StartAsync(_cfg.Test.TestTarget, staggerMs, async (i, token) =>
            {
                _log.Info($"EPB[{channel}] 周期 {i}/{_cfg.Test.TestTarget} 开始。", "EPB");
                var ok = await runner.RunOneAsync(token);
                _log.Info($"EPB[{channel}] 周期 {i}/{_cfg.Test.TestTarget} {(ok ? "完成" : "失败")}", "EPB");
                return ok;
            });
        }

        /// <summary>暂停指定通道。</summary>
        public void PauseChannel(int channel)
        {
            if (_timers.TryGetValue(channel, out var t)) t.Pause();
        }

        /// <summary>恢复指定通道。</summary>
        public void ResumeChannel(int channel)
        {
            if (_timers.TryGetValue(channel, out var t)) t.Resume();
        }

        /// <summary>停止指定通道。</summary>
        public void StopChannel(int channel)
        {
            if (_timers.TryGetValue(channel, out var t)) t.Stop();
            if (_cts.TryGetValue(channel, out var c)) c.Cancel();
            _timers.Remove(channel);
            _cts.Remove(channel);
            _do.SetEpbOff(channel); // 安全落位
        }

        /// <summary>停止全部通道。</summary>
        public void StopAll()
        {
            foreach (var ch in _timers.Keys.ToArray()) StopChannel(ch);
        }
    }
}
