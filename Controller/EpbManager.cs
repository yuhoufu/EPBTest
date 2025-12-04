using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Config;
using Config.Models;
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
        public IEpbCycleRecorder Recorder { get; set; }

        private readonly TwoDeviceAiAcquirer _acq; // ★ 新增：数据采集器

        private readonly AoController _ao;

        // EpbManager 字段区
        private readonly GlobalConfig _cfg;
        private readonly DoController _do;
        private readonly HydraulicController _hydraulic;
        private readonly IAppLogger _log;


        // —— 回调（采样） —— //
        private readonly EpbCycleRunner.ReadCurrentDelegate _readCurrent;

        private readonly Dictionary<int, EpbCycleRunner> _runners = new();
        private readonly Dictionary<int, HighPrecisionTimer> _timers = new();
        private readonly long _wallBaseTicks = Stopwatch.GetTimestamp();
        private readonly DateTime _wallBaseUtc = DateTime.UtcNow;
        private readonly HydraulicGroupCoordinator _hydCoordinator; // ★ 新增：液压组协调器

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
                    null, // 没有也没关系：协调器优先走 HydraulicController 的保持模式
                    null, // 无 AO 也可：不会走 Fallback
                    _hydraulic,
                    _log);
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
            //_readCurrent = acq.ReadCurrent;
            _readCurrent = acq.ReadCurrentFast;
            _log = log ?? NullLogger.Instance;
            _acq = acq;

            // 从cfg中获取控制参数；
            PeriodMs = cfg.Test.PeriodMs; // 周期时长
            TestCycle = cfg.Test.TestTarget; // 总周期数



            // —— 订阅“低时延电流样本”并转发给对应 Runner —— //
            _acq.OnFastEpbCurrent += (ch, amps, ts) =>
            {
                if (_runners.TryGetValue(ch, out var r))
                {
                    var tick = ToStopwatchTicks(ts.ToUniversalTime());
                    r.FeedCurrentSample(ch, tick, amps);
                }
            };

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
                acq.ReadPressure,
                aoController,
                _hydraulic,
                _log);
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
            return _hydCoordinator?.EnterElectricalPhaseAsync(channel, token) ?? Task.CompletedTask;
        }


        /// <summary>
        ///     释放液压
        /// </summary>
        /// <param name="channel"></param>
        /// <returns></returns>
        public Task HydraulicMarkReleaseAsync(int channel)
        {
            return _hydCoordinator?.MarkVoltageReleaseAsync(channel) ?? Task.CompletedTask;
        }

        // （保留你已有的 StartChannelAsync / Pause/Resume/Stop 等实现，不改对外签名）
        public async Task StartChannelAsync(int channel, CancellationToken uiToken = default)
        {
            if (_timers.ContainsKey(channel))
            {
                _log.Warn($"EPB[{channel}] 已在运行。", "EPB");
                return;
            }

            var hydId = channel <= 6 ? 1 : 2;

            //var rcfg = _cfg.Test?.EpbCycleRunner ?? new EpbCycleRunnerConfig();
            var rcfg = _cfg.Test?.EpbCycleRunner.GetRunnerChannel(channel);



            var periodMs = _cfg.Test.PeriodMs;
            var sampleMs = 2;


            var forwardA = rcfg.ForwardA;
            var holdMs = rcfg.HoldMs;                             

            // 如果 holdMs 为 null、0 或无效值，则设置为默认值 1000ms
            holdMs = holdMs <= 0 ? 1000 : holdMs; // 设置为 1000ms（1秒），可根据实际需要调整，调试使用

            var staggerMs = 0;
            foreach (var g in _cfg.Test.Groups)
                if (g.Members.Contains(channel))
                {
                    var indexInGroup = g.Members.OrderBy(x => x).ToList().IndexOf(channel);

                    staggerMs = g.StaggerMs * Math.Max(0, indexInGroup);
                    _log.Info($"EPB[{channel}] 归属组 {g.Id} 首启错峰 {staggerMs}ms（组内位置={indexInGroup}）", "EPB");
                    break;
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
                forwardA,
                holdMs,
                sampleMs,
                rcfg.PeakIgnoreMs,
                _log,
                this);

            // —— 新增：登记 Runner —— //
            _runners[channel] = runner;

            var learnCycles = GetProp<int>(rcfg, "LearnCycles");
            if (learnCycles <= 0) learnCycles = 5;

            if (learnCycles > 0)
            {
                _log.Info($"EPB[{channel}] 启动前自学习 {learnCycles} 次。", "EPB");
                try
                {
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

            timer.StartAsync(_cfg.Test.TestTarget, staggerMs, async (i, token) =>
            {
                _log.Info($"EPB[{channel}] 周期 {i}/{_cfg.Test.TestTarget} 开始。", "EPB");


                // —— 新增：圈开始（圈号 i，以 1 开始；若你的计数为 0 开始，可按需调整）
                Recorder?.BeginCycle(channel, i, DateTime.UtcNow);


                var ok = await runner.RunOneAsync(periodMs, token).ConfigureAwait(false);

                // —— 新增：圈结束（取本圈累计样本数做 finalN；若 Recorder 为 null 则 finalN=0）
                var finalN = Recorder?.GetCurrentCycleSampleCount(channel) ?? 0;
                Recorder?.CompleteCycle(channel, i, finalN, DateTime.UtcNow);

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

        public void StopChannelOld(int channel)
        {
            if (_timers.TryGetValue(channel, out var t)) t.Stop();
            _timers.Remove(channel);

            // —— 新增：移除 Runner —— //
            _runners.Remove(channel);

            // —— 新增：停止时强制把最近 N=10 圈落盘（含常开圈0）
            Recorder?.FlushRecent(channel, 10);

            _do.SetEpbOff(channel); // 安全落位
        }
        
        public void StopAllOld()
        {
            foreach (var ch in _timers.Keys.ToArray()) StopChannel(ch);
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
            // —— 停止“当前轮”的计时器 —— //
            HighPrecisionTimer t;
            if (_timers.TryGetValue(channel, out t))
            {
                try { t.Stop(); } catch { /* 忽略 Stop 异常 */ }
                _timers.Remove(channel);
            }

            // —— 同步清理“缓存计时器”，只 Stop + Remove，不做 Dispose（类型未实现 IDisposable）—— //
            HighPrecisionTimer cached;
            if (_timerCache.TryGetValue(channel, out cached))
            {
                try { cached.Stop(); } catch { /* 忽略 */ }
                _timerCache.Remove(channel);   // 关键：不要留下以免二次启动被误复用
            }

            // —— Runner 同样清理：运行表与缓存表都移除 —— //
            EpbCycleRunner runnerObj;
            if (_runners.TryGetValue(channel, out runnerObj))
            {
                // 退出前解绑事件，防止潜在内存泄漏
                runnerObj.ChannelCycleCompleted -= OnRunnerChannelCycleCompleted;

                _runners.Remove(channel);
            }
            _runnerCache.Remove(channel);

            // —— 安全落位与收尾（按你现有逻辑调整）—— //
            try { Recorder?.FlushRecent(channel, 10); } catch { /* 忽略 */ }
            try { _do.SetEpbOff(channel); } catch { /* 忽略 */ }
        }


        /// <summary>
        /// 停止所有通道：依次调用 <see cref="StopChannel"/> ，
        /// 并做一次兜底清空，确保下一次开始是“干净环境”。 
        /// </summary>
        public void StopAll()
        {
            var keys = _timers.Keys.ToArray(); // 拷贝快照，避免枚举期间修改
            for (int i = 0; i < keys.Length; i++)
                StopChannel(keys[i]);

            // 兜底清空（防御式）
            _timers.Clear();
            _runnerCache.Clear();
            _timerCache.Clear();
            _runners.Clear();
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
            Recorder?.CompleteCycle(epbId, cycleNumber, finalN, DateTime.UtcNow);
        }


        #region —— 私有辅助：学习延时、锚点、索引等 ——

        /// <summary>
        ///     延时后启动单通道学习。
        /// </summary>
        private async Task<(int ch, bool ok, Exception ex)> StartOneLearnWithDelayAsync(
            int channel,
            EpbCycleRunner runner,
            int learnCycles,
            int periodMs,
            int delayMs,
            CancellationToken token)
        {
            try
            {
                if (delayMs > 0)
                {
                    _log.Info($"EPB[{channel}] 学习延时 {delayMs}ms（组内错峰）。", "EPB");
                    await Task.Delay(delayMs, token).ConfigureAwait(false);
                }

                _log.Info($"EPB[{channel}] 开始学习（{learnCycles} 次）。", "EPB");
                var ok = await runner.LearnAsync(learnCycles, token, periodMs).ConfigureAwait(false);
                _log.Info($"EPB[{channel}] 学习 {(ok ? "完成" : "失败")}。", "EPB");

                // 返回带名字的元组：ch、ok、ex（学习成功时 ex 为 null）
                return (channel, ok, ok ? null : new Exception("LearnAsync 返回 false"));
            }
            catch (OperationCanceledException oce)
            {
                _log.Warn($"EPB[{channel}] 学习被取消：{oce.Message}", "EPB");
                return (channel, false, oce);
            }
            catch (Exception ex)
            {
                return (channel, false, ex);
            }
        }

        /// <summary>
        ///     构建“通道 -> 组”的映射。若通道未出现在任何组中，则不加入映射（视作独立组）。
        /// </summary>
        private static Dictionary<int, ElectricalGroup> MapChannelToGroup(IEnumerable<ElectricalGroup> groups)
        {
            var map = new Dictionary<int, ElectricalGroup>();
            foreach (var g in groups ?? Array.Empty<ElectricalGroup>())
            {
                if (g?.Members == null) continue;
                foreach (var ch in g.Members)
                    // 若一个通道在多个组中，只保留第一次出现（配置应避免重复归属）
                    if (!map.ContainsKey(ch))
                        map[ch] = g;
            }

            return map;
        }

        /// <summary>
        ///     计算“组内索引”：对“本次被选中 ∩ 该组成员”的通道，按通道号升序编号 i=0..n-1。
        ///     未分组通道的索引为 0。
        /// </summary>
        private static Dictionary<int, int> ComputeIndexInGroup(
            IList<int> selected,
            Dictionary<int, ElectricalGroup> groupByChannel)
        {
            var result = new Dictionary<int, int>();

            // 先按“组”分类
            var buckets = new Dictionary<ElectricalGroup?, List<int>>();
            foreach (var ch in selected)
            {
                groupByChannel.TryGetValue(ch, out var grp); // grp 可能为 null（无组）
                if (!buckets.TryGetValue(grp, out var list))
                {
                    list = new List<int>();
                    buckets[grp] = list;
                }

                list.Add(ch);
            }

            // 每个桶内部按升序重新编号
            foreach (var kv in buckets)
            {
                var list = kv.Value.OrderBy(x => x).ToList();
                for (var i = 0; i < list.Count; i++)
                    result[list[i]] = i;
            }

            return result;
        }

        /// <summary>
        ///     计算“现在 + secondsAhead”后向上对齐到 PeriodMs 边界的 UTC 锚点。
        /// </summary>
        private static DateTime ComputeAlignedAnchorUtc(int periodMs, int secondsAhead)
        {
            var nowUtc = DateTime.UtcNow;
            var baseUtc = nowUtc.AddSeconds(secondsAhead);

            // 以 Unix Epoch 做整数对齐，减少多定时器首发相位误差
            var msFromEpoch = (long)(baseUtc - new DateTime(1970, 1, 1)).TotalMilliseconds;
            var aligned = (msFromEpoch + periodMs - 1) / periodMs * periodMs;
            return new DateTime(1970, 1, 1).AddMilliseconds(aligned);
        }

        #endregion




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
        /// - 默认并发执行全部通道的预释放。若你希望遵守“电源组错峰”，可以按 IndexInPowerGroup 分三波执行。<br/>
        /// - 该方法仅做“学习前的姿态归零”，不做液压建压/释压；正式流程仍由“每圈锚点”统一控制。
        /// </remarks>
        public async Task PreReleaseBatchAsync(int[] channels, int? keepMs, CancellationToken token)
        {
            if (channels == null || channels.Length == 0)
                throw new ArgumentException("channels 不能为空。", nameof(channels));

            // 并发跑每个通道的预释放
            var tasks = new List<Task>();
            var enabled = channels.Distinct().OrderBy(x => x).ToArray();

            for (int i = 0; i < enabled.Length; i++)
            {
                var ch = enabled[i];
                var runner = GetRunner(ch); // 你在 BatchStart.cs 中实现的对接

                // 若 keepMs==null，runner 内部会使用 DefaultPreReleaseKeepMs
                tasks.Add(runner.PreReleaseAsync(keepMs, token));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        /// <summary>
        /// （可选增强）按“电源组相位 0/Δ/2Δ”三波错峰执行批量预释放。
        /// 当你担心同时反向上电电流过大时使用。
        /// </summary>
        public async Task PreReleaseBatchStaggeredAsync(int[] channels, int? keepMs, int deltaMs, CancellationToken token)
        {
            if (channels == null || channels.Length == 0)
                throw new ArgumentException("channels 不能为空。", nameof(channels));

            var enabled = channels.Distinct().OrderBy(x => x).ToArray();

            // 三个相位桶：索引 0：1/4/7/10；索引 1：2/5/8/11；索引 2：3/6/9/12
            var buckets = new[] { new List<int>(), new List<int>(), new List<int>() };
            for (int i = 0; i < enabled.Length; i++)
            {
                var ch = enabled[i];
                var idx = IndexInPowerGroup(ch);
                buckets[idx].Add(ch);
            }

            var t0 = DateTime.UtcNow.AddMilliseconds(500); // 给 500ms 预热时间（可按需调整）

            for (int phaseIdx = 0; phaseIdx < 3; phaseIdx++)
            {
                var bucket = buckets[phaseIdx];
                if (bucket.Count == 0) continue;

                var at = t0.AddMilliseconds(phaseIdx * deltaMs);
                var delay = at - DateTime.UtcNow;
                if (delay.TotalMilliseconds > 1)
                    await Task.Delay(delay, token).ConfigureAwait(false);

                var tasks = new List<Task>();
                for (int j = 0; j < bucket.Count; j++)
                {
                    var ch = bucket[j];
                    var runner = GetRunner(ch);
                    tasks.Add(runner.PreReleaseAsync(keepMs, token));
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
        }

        // 你已有的工具：电源组索引（1/4/7/10→0；2/5/8/11→1；3/6/9/12→2）
        private static int IndexInPowerGroup(int ch)
        {
            if (ch < 1) ch = 1;
            return (ch - 1) % 3;
        }

        #endregion

    }
}