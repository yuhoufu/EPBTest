using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Config;
using Controller; // ✅ 引用你的记录与日志接口

namespace Epb.Control
{
    /// <summary>
    /// 每个压力组一个调度器：由该组 HighPrecisionTimer 定期调用 <see cref="OnTick"/>。
    /// 负责：组内错峰推进、优雅暂停（本圈完毕断电）、循环计数持久化、E-Stop、
    /// 以及跨组锚点的“软屏障”对齐。
    /// </summary>
    public sealed class GroupScheduler
    {
        private readonly ConcurrentDictionary<int, RunnerCtx> _runners = new();

        private readonly PressureGroup _group;
        private readonly Func<int, int> _powerGroupOf;        // EPB -> 电源组索引（0..3）
        private readonly Func<int, int> _indexInPowerGroup;   // EPB -> 组内索引（0..2）
        private readonly int _deltaPGroupMs;
        private readonly int _epsilonInGroupMs;

        // 跨组锚点软屏障
        private volatile bool _resumePending;
        private DateTime _resumeAnchorUtc = DateTime.MinValue;

        // Running 心跳：刷新 LatestStartTime（不改 RunTime）
        private readonly TimeSpan _heartbeat;
        private DateTime _lastHeartbeatUtc = DateTime.MinValue;

        // 内部高精度时基
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private readonly DateTime _t0 = DateTime.UtcNow;

        private readonly IAppLogger _log;

        /// <summary>
        /// 构造一个压力组调度器。
        /// </summary>
        /// <param name="group">压力组 PG1/PG2。</param>
        /// <param name="powerGroupOf">映射：EPB -> 电源组索引（0..3）。</param>
        /// <param name="indexInPowerGroup">映射：EPB -> 组内索引（0..2）。</param>
        /// <param name="deltaPGroupMs">电源组间错峰 ΔP（毫秒）。</param>
        /// <param name="epsilonInGroupMs">组内索引错峰 ε（毫秒）。</param>
        /// <param name="runningHeartbeat">Running 心跳持久化周期。</param>
        /// <param name="log">日志接口。</param>
        public GroupScheduler(
            PressureGroup group,
            Func<int, int> powerGroupOf,
            Func<int, int> indexInPowerGroup,
            int deltaPGroupMs,
            int epsilonInGroupMs,
            TimeSpan runningHeartbeat,
            IAppLogger log)
        {
            _group = group;
            _powerGroupOf = powerGroupOf ?? throw new ArgumentNullException(nameof(powerGroupOf));
            _indexInPowerGroup = indexInPowerGroup ?? throw new ArgumentNullException(nameof(indexInPowerGroup));
            _deltaPGroupMs = Math.Max(0, deltaPGroupMs);
            _epsilonInGroupMs = Math.Max(0, epsilonInGroupMs);
            _heartbeat = runningHeartbeat <= TimeSpan.Zero ? TimeSpan.FromSeconds(10) : runningHeartbeat;
            _log = log ?? new NullAppLogger();
        }

        /// <summary>当前是否处于“同时恢复并对齐”的等待期。</summary>
        public bool ResumePending => _resumePending;

        /// <summary>
        /// 设置跨组锚点的目标 UTC 时刻（到达前冻结推进）。
        /// </summary>
        public void SetResumeAnchor(DateTime tStarUtc)
        {
            _resumeAnchorUtc = tStarUtc;
            _resumePending = true;
            _log.Info($"[{_group}] ResumePending set. Anchor(Utc)={tStarUtc:O}", "Scheduler");
        }

        /// <summary>
        /// 将 EPB 加入本组运行集合，并按“电源组+组内索引”计算错峰起点。
        /// 同时按记录当前状态执行 Start 或 Resume，并立即持久化。
        /// </summary>
        public void AddEpb(int epbId, EpbCycleRunner runner, int initialCycles, EpbTestRecord rec)
        {
            var pg = Math.Max(0, _powerGroupOf(epbId));
            var idx = Math.Max(0, _indexInPowerGroup(epbId));
            var offsetMs = pg * _deltaPGroupMs + idx * _epsilonInGroupMs;

            var ctx = new RunnerCtx(epbId, runner, rec, offsetMs)
            {
                RemainingCycles = Math.Max(0, initialCycles)
            };
            _runners[epbId] = ctx;

            // —— 记录：Start/Resume，并立即落盘 —— //
            var now = DateTime.Now;
            if (rec.Status == EpbTestStatus.NotStarted || !rec.StartTime.HasValue)
                rec.Start(now);
            else
                rec.Resume(now);

            EpbTestRecordStore.SaveSingle(rec); // ✅ 静态调用
        }

        /// <summary>优雅暂停：本圈完成后断电、置 Paused。</summary>
        public void Pause(int epbId)
        {
            if (_runners.TryGetValue(epbId, out var ctx))
            {
                ctx.PauseRequested = true;
                _log.Info($"[{_group}] EPB[{epbId}] Pause requested.", "Scheduler");
            }
        }

        /// <summary>恢复：可选择对齐下一锚点。</summary>
        public void Resume(int epbId, bool alignToNextAnchor)
        {
            if (_runners.TryGetValue(epbId, out var ctx))
            {
                ctx.PauseRequested = false;
                ctx.IsPaused = false;

                // 相位对齐：重置周期起点（PhaseStartAt）
                ctx.PhaseStartAtUtc = alignToNextAnchor
                    ? DateTime.UtcNow.AddMilliseconds(ComputeToNextAnchorMs())
                    : DateTime.UtcNow;

                // 记录恢复并落盘
                recResumeAndSave(ctx);
                _log.Info($"[{_group}] EPB[{epbId}] Resumed. Align={alignToNextAnchor}", "Scheduler");
            }

            // 局部内联，确保用一致动作
            void recResumeAndSave(RunnerCtx c)
            {
                var now = DateTime.Now;
                c.Record.Resume(now);
                EpbTestRecordStore.SaveSingle(c.Record);
            }
        }

        /// <summary>立即停止：立刻断电、置 Paused、移出活跃表并持久化。</summary>
        public void Stop(int epbId)
        {
            if (_runners.TryRemove(epbId, out var ctx))
            {
                SafeOffAndPersistPaused(ctx, "Stop");
            }
        }

        /// <summary>紧急停止：毫秒级断电，不增圈，移出活跃表并持久化。</summary>
        public void EmergencyStop(int epbId)
        {
            if (_runners.TryRemove(epbId, out var ctx))
            {
                ctx.Runner.ForceSafeOff();       // 立即安全断电
                ctx.IsPaused = true;
                ctx.PauseRequested = false;

                var now = DateTime.Now;
                ctx.Record.Pause(now);            // 把运行时长累加到 RunTime
                EpbTestRecordStore.SaveSingle(ctx.Record);

                // 可选：清学习态
                EpbLearnStateStore.Instance.RemoveAndSave(epbId);

                _log.Warn($"[{_group}] EPB[{epbId}] EmergencyStop executed.", "Scheduler");
            }
        }

        /// <summary>
        /// 组计时器回调：推进所有活跃 EPB（错峰 + 优雅暂停 + 循环计数 + 心跳持久化）。
        /// </summary>
        public void OnTick()
        {
            var nowUtc = _t0 + _sw.Elapsed;

            // —— 跨组锚点软屏障 —— //
            if (_resumePending)
            {
                if (nowUtc < _resumeAnchorUtc) return;

                _resumePending = false;
                _log.Info($"[{_group}] Resume anchor reached. Δ={(nowUtc - _resumeAnchorUtc).TotalMilliseconds:F1}ms", "Scheduler");

                // 本组所有活跃 EPB 相位归零 + 错峰（将 PhaseStartAt 调整为 now - offset）
                foreach (var ctx in _runners.Values)
                    ctx.PhaseStartAtUtc = nowUtc.AddMilliseconds(-ctx.StartOffsetMs);
            }

            // —— 快照遍历，避免增删产生枚举异常 —— //
            var snapshot = _runners.Values.ToArray();
            foreach (var ctx in snapshot)
            {
                if (ctx.IsPaused) continue;

                // relPhase = (now - PhaseStartAt) + StartOffset
                var relPhaseMs = (int)(nowUtc - ctx.PhaseStartAtUtc).TotalMilliseconds + ctx.StartOffsetMs;
                if (relPhaseMs < 0) continue; // 尚未到错峰起点

                // 推进状态机
                if (ctx.Runner.TryAdvance(nowUtc, relPhaseMs, out var completedThisCycle))
                {
                    if (completedThisCycle)
                    {
                        // —— 一圈完成：计数 + 落盘 —— //
                        ctx.Record.IncrementCycle();
                        EpbTestRecordStore.SaveSingle(ctx.Record);

                        // —— 优雅暂停：在圈末断电 & 置 Paused —— //
                        if (ctx.PauseRequested)
                        {
                            ctx.Runner.ForceSafeOff();
                            ctx.IsPaused = true;
                            ctx.PauseRequested = false;

                            var now = DateTime.Now;
                            ctx.Record.Pause(now);
                            EpbTestRecordStore.SaveSingle(ctx.Record);

                            _log.Info($"[{_group}] EPB[{ctx.EpbId}] paused gracefully at cycle end.", "Scheduler");
                            continue;
                        }

                        // —— 循环耗尽 → 自动停止 —— //
                        if (ctx.RemainingCycles > 0)
                        {
                            ctx.RemainingCycles--;
                            if (ctx.RemainingCycles == 0)
                            {
                                if (_runners.TryRemove(ctx.EpbId, out _))
                                    SafeOffAndPersistPaused(ctx, "CyclesExhausted");
                                continue;
                            }
                        }

                        // 下一圈相位重置（从锚点+错峰重新计时）
                        ctx.PhaseStartAtUtc = nowUtc.AddMilliseconds(-ctx.StartOffsetMs);
                    }
                }
            }

            // —— Running 心跳：仅刷新 LatestStartTime（不改 RunTime） —— //
            if ((nowUtc - _lastHeartbeatUtc) >= _heartbeat)
            {
                _lastHeartbeatUtc = nowUtc;
                foreach (var ctx in _runners.Values)
                {
                    if (!ctx.IsPaused)
                    {
                        ctx.Record.LatestStartTime = DateTime.Now;
                        EpbTestRecordStore.SaveSingle(ctx.Record);
                    }
                }
            }
        }

        /// <summary>计算“对齐下一锚点”的保护延时（毫秒）。</summary>
        private static int ComputeToNextAnchorMs() => 150;

        /// <summary>统一的安全断电 + 持久化为 Paused。</summary>
        private void SafeOffAndPersistPaused(RunnerCtx ctx, string reason)
        {
            ctx.Runner.ForceSafeOff();
            ctx.IsPaused = true;
            ctx.PauseRequested = false;

            var now = DateTime.Now;
            ctx.Record.Pause(now); // 会把从上次 Resume 到现在的时长累加进 RunTime
            EpbTestRecordStore.SaveSingle(ctx.Record);

            // 可选：清学习态
            EpbLearnStateStore.Instance.RemoveAndSave(ctx.EpbId);

            _log.Info($"[{_group}] EPB[{ctx.EpbId}] stopped. reason={reason}", "Scheduler");
        }

        /// <summary>每个 EPB 的运行上下文。</summary>
        private sealed class RunnerCtx
        {
            public RunnerCtx(int epbId, EpbCycleRunner runner, EpbTestRecord record, int startOffsetMs)
            {
                EpbId = epbId;
                Runner = runner;
                Record = record;
                StartOffsetMs = startOffsetMs;
                PhaseStartAtUtc = DateTime.UtcNow; // 初始相位起点
            }

            public int EpbId { get; }
            public EpbCycleRunner Runner { get; }
            public EpbTestRecord Record { get; }

            public int StartOffsetMs { get; }
            public DateTime PhaseStartAtUtc { get; set; }

            public bool PauseRequested { get; set; }
            public bool IsPaused { get; set; }
            public int RemainingCycles { get; set; }
        }
    }

    /// <summary>兜底日志（若未提供 IAppLogger）。</summary>
    internal sealed class NullAppLogger : IAppLogger
    {
        public void Info(string message, string category = null) { Debug.WriteLine($"INFO[{category}] {message}"); }
        public void Warn(string message, string category = null) { Debug.WriteLine($"WARN[{category}] {message}"); }
        public void Error(string message, string category = null, Exception ex = null) { Debug.WriteLine($"ERR [{category}] {message} {ex}"); }
    }
}
