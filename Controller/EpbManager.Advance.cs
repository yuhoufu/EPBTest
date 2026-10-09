using Config;
using Epb.Control;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Timing;
using Utils;

namespace Controller
{
    /// <summary>
    /// EPB 管理器（partial）：双计时器（PG1/PG2）+ 组内错峰 + 状态持久化 + 跨组锚点对齐 + E-Stop。
    /// </summary>
    public partial class EpbManager
    {
        private readonly HighPrecisionTimer _timerPg1;
        private readonly HighPrecisionTimer _timerPg2;
        private readonly GroupScheduler _schPg1;
        private readonly GroupScheduler _schPg2;

       // private readonly EpbTestRecordStore _store;
        //private readonly IAppLogger _log;

        // 从 TestConfig 读取的参数
        private readonly int _periodPg1Ms;
        private readonly int _periodPg2Ms;
        private readonly int _deltaPGroupMs;
        private readonly int _epsilonInGroupMs;
        private readonly TimeSpan _hb;

        // —— 你工程已有的：映射 EPB -> 压力组、电源组、组内索引 —— //
        private readonly Func<int, PressureGroup> _pressureGroupOf;
        private readonly Func<int, int> _powerGroupOf;       // 0..3
        private readonly Func<int, int> _indexInPowerGroup;  // 0..2

        public EpbManager(
            TestConfig cfg,
            //EpbTestRecordStore store,
            Func<int, PressureGroup> pressureGroupOf,
            Func<int, int> powerGroupOf,
            Func<int, int> indexInPowerGroup,
            IAppLogger log)
        {
            //_store = store ?? throw new ArgumentNullException(nameof(store));
            _pressureGroupOf = pressureGroupOf ?? DefaultPressureGroupOf;
            _powerGroupOf = powerGroupOf ?? DefaultPowerGroupOf;
            _indexInPowerGroup = indexInPowerGroup ?? DefaultIndexInPowerGroup;
            _log = log ?? new NullAppLogger();

            // —— 读取 TestConfig 新增节点（缺省则给默认） —— //
            _periodPg1Ms = cfg.Timer?.PeriodPg1Ms ?? 5000;
            _periodPg2Ms = cfg.Timer?.PeriodPg2Ms ?? 5000;
            _deltaPGroupMs = cfg.Timer?.DeltaPGroupMs ?? 200;
            _epsilonInGroupMs = cfg.Timer?.EpsilonInGroupMs ?? 50;
            var hbSec = cfg.Timer?.RunningHeartbeatSec ?? 10;
            _hb = TimeSpan.FromSeconds(Math.Max(1, hbSec));

            _schPg1 = new GroupScheduler(PressureGroup.Pg1, _powerGroupOf, _indexInPowerGroup, _deltaPGroupMs, _epsilonInGroupMs, _hb, _log);
            _schPg2 = new GroupScheduler(PressureGroup.Pg2, _powerGroupOf, _indexInPowerGroup, _deltaPGroupMs, _epsilonInGroupMs, _hb, _log);

            // 组计时器：使用你上传的 HighPrecisionTimer
            _timerPg1 = new HighPrecisionTimer(_periodPg1Ms, OverrunPolicy.AlignToWallClock, _log);
            _timerPg2 = new HighPrecisionTimer(_periodPg2Ms, OverrunPolicy.AlignToWallClock, _log);

        }


        /// <summary>
        /// 便捷构造：只传 cfg 和 log，分组规则走默认。
        /// </summary>
        public EpbManager(TestConfig cfg, IAppLogger log = null)
            : this(cfg, DefaultPressureGroupOf, DefaultPowerGroupOf, DefaultIndexInPowerGroup, log)
        {
        }

        /// <summary>12 路按压力 2 组 × 各 6 路。</summary>
        private static PressureGroup DefaultPressureGroupOf(int ch)
        {
            if (ch < 1) ch = 1;
            return (ch <= 6) ? PressureGroup.Pg1 : PressureGroup.Pg2;
        }

        /// <summary>4 个电源组 × 各 3 路：1-3/4-6/7-9/10-12。</summary>
        private static int DefaultPowerGroupOf(int ch)
        {
            if (ch < 1) ch = 1;
            return (ch - 1) / 3; // 0..3
        }

        /// <summary>电源组内索引 0..2。</summary>
        private static int DefaultIndexInPowerGroup(int ch)
        {
            if (ch < 1) ch = 1;
            return (ch - 1) % 3; // 0..2
        }



        /// <summary>
        /// 启动（或追加）一批 EPB 通道，进入“学习→循环”的标准流程。
        /// </summary>
        public async Task StartChannelsAsync(int[] channels, CancellationToken token, Dictionary<int, int> cyclesEach = null)
        {
            if (channels == null || channels.Length == 0) return;

            foreach (var ch in channels)
            {
                var rec = LoadOrCreateRecord(ch); // ✅ 静态加载或默认创建
                var pg = _pressureGroupOf(ch);
                var runner = CreateRunnerFromConfig(ch);

                var cycles = (cyclesEach != null && cyclesEach.TryGetValue(ch, out var n))
                    ? n : Math.Max(0, rec.TotalCount - rec.RunCount);

                if (pg == PressureGroup.Pg1)
                    _schPg1.AddEpb(ch, runner, cycles, rec);
                else
                    _schPg2.AddEpb(ch, runner, cycles, rec);
            }

            await EnsureTimerStartedAsync(_timerPg1, () => _schPg1.OnTick(), token);
            await EnsureTimerStartedAsync(_timerPg2, () => _schPg2.OnTick(), token);
        }
        /// <summary>从 TestConfig.xml 加载该 EPB 的记录；若不存在则创建默认值（不立即写盘）。</summary>
        private static EpbTestRecord LoadOrCreateRecord(int id)
        {
            var rec = EpbTestRecordStore.LoadSingle(id);
            return rec ?? EpbTestRecord.CreateDefault(id);
        }

        /// <summary>确保组计时器启动（若已启动则忽略）。</summary>
        private static async Task EnsureTimerStartedAsync(Timing.HighPrecisionTimer timer, Action onTick, CancellationToken token)
        {
            if (timer == null) throw new ArgumentNullException(nameof(timer));
            if (timer.IsRunning) return;

            // 当外部 token 被取消时，停止 timer（防止任务一直运行）。
            using var reg = token.Register(() =>
            {
                try { timer.Stop(); } catch { /* 忽略 Stop 的异常 */ }
            });

            // 注意：HighPrecisionTimer.StartAsync 的签名是 (int? repeat, int startDelayMs, Func<int, CancellationToken, Task<bool>> work)
            await timer.StartAsync(
                repeat: null,
                startDelayMs: 0,
                work: async (iteration, ct) =>
                {
                    // 在这里尽量不要做耗时工作；仅通知 Scheduler 的 OnTick
                    try
                    {
                        onTick();
                    }
                    catch (Exception ex)
                    {
                        // 如果你有日志，可在这里记录 ex
                        Debug.WriteLine($"OnTick threw: {ex.Message}");
                    }

                    await Task.CompletedTask;
                    return true;
                }
            ).ConfigureAwait(false);
        }


        /// <summary>暂停（优雅）：本圈结束后断电。</summary>
        public void Pause(IEnumerable<int> channels)
        {
            foreach (var ch in channels)
            {
                var pg = _pressureGroupOf(ch);
                (pg == PressureGroup.Pg1 ? _schPg1 : _schPg2).Pause(ch);
            }
        }

        /// <summary>恢复：可选择对齐下一锚点。</summary>
        public void Resume(IEnumerable<int> channels, bool alignToNextAnchor = true)
        {
            foreach (var ch in channels)
            {
                var pg = _pressureGroupOf(ch);
                (pg == PressureGroup.Pg1 ? _schPg1 : _schPg2).Resume(ch, alignToNextAnchor);
            }
        }

        /// <summary>立即停止：立刻断电并移除。</summary>
        public void Stop(IEnumerable<int> channels)
        {
            foreach (var ch in channels)
            {
                var pg = _pressureGroupOf(ch);
                (pg == PressureGroup.Pg1 ? _schPg1 : _schPg2).Stop(ch);
            }
        }

        /// <summary>紧急停止：毫秒级断电，不增圈并移除。</summary>
        public void EmergencyStop(IEnumerable<int> channels)
        {
            foreach (var ch in channels)
            {
                var pg = _pressureGroupOf(ch);
                (pg == PressureGroup.Pg1 ? _schPg1 : _schPg2).EmergencyStop(ch);
            }
        }

        public void StopAll()
        {
            // 这里为了简单起见，你可以维护一个全通道列表；或调用各 _sch* 内部方法全量遍历
            // 留空按需实现

            // 这里假设你维护了一个 1..12 的全集；或者从硬件/配置读取
            var all = Enumerable.Range(1, 12);
            Stop(all);
        }

        public void EmergencyStopAll()
        {

            var all = Enumerable.Range(1, 12);
            EmergencyStop(all);
        }

        /// <summary>
        /// 同时恢复并对齐（跨组锚点对齐）。
        /// AnchorMode.AfterMs/NowAligned/AtTime/AtNextKMultiples。
        /// </summary>
        public void ResumeBothAnchored(AnchorMode mode, int? value = null, bool snapToPeriodMultiple = true, DateTime? atTimeLocal = null)
        {
            var now = DateTime.Now;           // 本地时
            var nowUtc = DateTime.UtcNow;     // UTC（内部统一用 UTC）
            DateTime tStarUtc;

            int Lcm(int a, int b)
            {
                int Gcd(int x, int y) => y == 0 ? x : Gcd(y, x % y);
                return a / Gcd(a, b) * b;
            }

            switch (mode)
            {
                case AnchorMode.AfterMs:
                    var guard = Math.Max(50, value ?? 200);
                    tStarUtc = nowUtc.AddMilliseconds(guard);
                    break;
                case AnchorMode.NowAligned:
                    var lcm = Lcm(_periodPg1Ms, _periodPg2Ms);
                    var msNow = (long)(DateTime.UtcNow - DateTimeCompat.UnixEpoch).TotalMilliseconds;
                    var next = (msNow / lcm + 1) * lcm + 200; // +200ms 保护带
                    tStarUtc = DateTimeCompat.UnixEpoch.AddMilliseconds(next);
                    break;
                case AnchorMode.AtTime:
                    if (atTimeLocal == null) throw new ArgumentNullException(nameof(atTimeLocal));
                    tStarUtc = atTimeLocal.Value.ToUniversalTime();
                    break;
                case AnchorMode.AtNextKMultiples:
                    var k = Math.Max(1, value ?? 1);
                    var l = Lcm(_periodPg1Ms, _periodPg2Ms);
                    var ms = (long)(nowUtc - DateTimeCompat.UnixEpoch).TotalMilliseconds;
                    var nextK = ((ms / l) + k) * l + 200;
                    tStarUtc = DateTimeCompat.UnixEpoch.AddMilliseconds(nextK);
                    break;
                default:
                    tStarUtc = nowUtc.AddMilliseconds(200);
                    break;
            }

            if (snapToPeriodMultiple)
            {
                var lcm = Lcm(_periodPg1Ms, _periodPg2Ms);
                var ms = (long)(tStarUtc - DateTimeCompat.UnixEpoch).TotalMilliseconds;
                var snapped = ((ms + lcm - 1) / lcm) * lcm;
                tStarUtc = DateTimeCompat.UnixEpoch.AddMilliseconds(snapped);
            }

            _schPg1.SetResumeAnchor(tStarUtc);
            _schPg2.SetResumeAnchor(tStarUtc);

            _log.Info($"ResumeBothAnchored set. T*={tStarUtc:O}, mode={mode}, snap={snapToPeriodMultiple}", "EpbManager");
        }

        public void ResumeBothAnchoredQuick() => ResumeBothAnchored(AnchorMode.AfterMs, 200, true);


        /// <summary>
        /// 进程启动时调用：把上次处于 Running 的通道强转为 Paused，避免上电不确定；
        /// 可选：autoResume=true 时，按锚点对齐后一键恢复。
        /// </summary>
        public void RecoverFromPersistedRecords(bool autoResume = false)
        {
            var all = Config.EpbTestRecordStore.LoadAll() ?? new List<Config.EpbTestRecord>();
            var runningIds = new List<int>();

            foreach (var rec in all)
            {
                // 把历史 Running 统统折中为 Paused（安全）
                if (rec.Status == Config.EpbTestStatus.Running)
                {
                    rec.Pause(DateTime.Now);
                    Config.EpbTestRecordStore.SaveSingle(rec);
                    runningIds.Add(rec.Id);
                }
            }

            if (autoResume && runningIds.Count > 0)
            {
                // 根据记录的 TotalCount/RunCount 计算剩余圈数
                // var cycles = runningIds.ToDictionary(id =>
                // {
                //     var r = all.First(x => x.Id == id);
                //     var remain = Math.Max(0, r.TotalCount - r.RunCount);
                //     return new KeyValuePair<int, int>(id, remain);
                // });

                var cycles = all
                    .Where(r => runningIds.Contains(r.Id))
                    .ToDictionary(r => r.Id, r => Math.Max(0, r.TotalCount - r.RunCount));


                // 启动并同时对齐恢复
                _ = StartChannelsAsync(runningIds.ToArray(), CancellationToken.None, cycles);
                ResumeBothAnchoredQuick();
            }
        }


       

        private EpbCycleRunner CreateRunnerFromConfig(int epbId)
        {
            var limitRecord = _cfg.Test.EpbLimits.FirstOrDefault(x => GetProp<int>(x, "Channel") == epbId)
                              ?? throw new InvalidOperationException($"未配置 EPB[{epbId}] 电流限值。");

            

            // 这里按你的 AIConfig/DOConfig/TestConfig 取值；下面给默认值示意
            var holdMs = limitRecord?.HoldMs ?? 200;
            var cutA = limitRecord?.CutCurrentA ?? 8.0;
            var limitA = limitRecord?.ForwardA ?? 10.0;
            return new EpbCycleRunner(epbId, holdMs, cutA, limitA, _log);
        }

    }
}
