using System;
using System.Globalization;

namespace Config
{
    // ======= 单通道 EPB 试验记录 =======

    /// <summary>
    /// 单通道 EPB 试验记录（POCO）。
    /// <para>
    /// - 支持将 <see cref="RunTime"/> 作为字符串进行序列化（XML 保存）；<br/>
    /// - 内部通过 <see cref="RunTimeSpan"/> 使用 <see cref="TimeSpan"/> 做加法、解析。
    /// </para>
    /// </summary>
    public sealed class EpbTestRecord
    {
        #region 公共属性

        /// <summary>
        /// EPB 通道 Id（1..12）。
        /// </summary>
        public int Id { get; internal set; }

        /// <summary>
        /// EPB 通道 启用
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// 首次开始时间（可为 null）。用于记录最初的开始时间。
        /// <para>序列化/保存时的文本格式建议使用 "yyyy-MM-dd HH:mm:ss"（由外部负责格式化）。</para>
        /// </summary>
        public DateTime? StartTime { get; set; }

        /// <summary>
        /// 最近一次开始（或恢复）时间。
        /// <para>
        /// 语义：<br/>
        /// - 初始化时：若 <see cref="RunCount"/> == 0，则置为当前时间；<br/>
        /// - “开始试验”时：更新为当前时间；<br/>
        /// - 每次完成一圈并累加运行时间后，也可更新为当前时间。
        /// </para>
        /// </summary>
        public DateTime? LatestStartTime { get; set; }

        /// <summary>
        /// 已累计运行时长（字符串形式），格式示例 "1.02:30:45"（d.hh:mm:ss）。
        /// <para>
        /// - 便于直接写入 XML / 显示；<br/>
        /// - 若要进行加减运算，请使用 <see cref="RunTimeSpan"/> 属性。
        /// </para>
        /// </summary>
        public string RunTime { get; set; } = FormatTimeSpan(TimeSpan.Zero);

        /// <summary>
        /// 试验计划/总次数（通常来自 TestConfig.TestTarget，由外部设置）。
        /// </summary>
        public int TotalCount { get; set; }

        /// <summary>
        /// 已运行次数（完成的循环次数）。
        /// </summary>
        public int RunCount { get; set; }

        /// <summary>
        /// 卡钳实际完成的机械夹紧+释放圈数。该口径包含学习圈、资格圈，
        /// 以及控制已完整结束但随后证据提交失败的正式圈；RunCount 仍保留
        /// “正式圈且证据已提交”的质量口径。
        /// </summary>
        private long _mechanicalCycleCount;
        public long MechanicalCycleCount
        {
            get => System.Threading.Interlocked.Read(ref _mechanicalCycleCount);
            set => System.Threading.Interlocked.Exchange(ref _mechanicalCycleCount, value);
        }

        /// <summary>
        /// 耐久目标的权威完成口径。旧项目没有 MechanicalCycleCount 时仍以
        /// RunCount 兜底，升级后则包含学习、资格和证据作废但已完整释放的机械圈。
        /// </summary>
        public long EffectiveMechanicalCycleCount =>
            Math.Max(MechanicalCycleCount, RunCount);

        public int GetRemainingMechanicalCycles(int fallbackTotalCount = 0)
        {
            var total = TotalCount > 0 ? TotalCount : Math.Max(0, fallbackTotalCount);
            return (int)Math.Max(0L, total - EffectiveMechanicalCycleCount);
        }

        /// <summary>
        /// 当前试验状态。
        /// </summary>
        public EpbTestStatus Status { get; set; } = EpbTestStatus.NotStarted;

        /// <summary>需要人工确认后才能清除的永久报警锁存。</summary>
        public bool PermanentAlarmLatched { get; set; }

        public string PermanentAlarmCode { get; set; } = string.Empty;

        public string PermanentAlarmReason { get; set; } = string.Empty;

        public DateTime? PermanentAlarmUtc { get; set; }

        public Guid PermanentAlarmCorrelationId { get; set; }

        /// <summary>
        /// 高负载正向失速的人工完整重学习门禁。该事实独立于永久报警，
        /// 进程重启后仍必须阻止普通开始/无人值守恢复复用旧模型。
        /// </summary>
        public bool OperatorFullRelearningRequired { get; set; }

        public string OperatorFullRelearningReason { get; set; } = string.Empty;

        public DateTime? OperatorFullRelearningUtc { get; set; }

        public Guid OperatorFullRelearningCorrelationId { get; set; }

        /// <summary>同一项目内连续完成但超过目标周期的正式圈数。</summary>
        public int ConsecutivePeriodOverrunCount { get; set; }

        public DateTime? LastPeriodOverrunUtc { get; set; }

        #endregion

        #region 内部字段

        /// <summary>
        /// 最近一次用于累加运行时间的参考时间点。
        /// <para>
        /// 当前版本中，主要在 <see cref="IncrementCycleAndUpdateTime"/> 中使用，
        /// 也可以在 Pause/Resume 等高级逻辑中复用。
        /// </para>
        /// </summary>
        private DateTime? _lastElapsedBaseUtc;

        #endregion

        #region 工厂方法

        /// <summary>
        /// 创建一个带默认值的记录。
        /// </summary>
        /// <param name="id">EPB 通道 Id（1..12）。</param>
        /// <param name="totalCount">计划总次数（可为 0）。</param>
        public static EpbTestRecord CreateDefault(int id, int totalCount = 0)
        {
            return new EpbTestRecord
            {
                Id = id,
                Enabled = false, // 默认启用该通道（如有需要可由外部 UI/配置关闭）
                StartTime = null,
                LatestStartTime = null,
                RunTime = FormatTimeSpan(TimeSpan.Zero),
                TotalCount = totalCount,
                RunCount = 0,
                MechanicalCycleCount = 0,
                Status = EpbTestStatus.NotStarted,
                PermanentAlarmLatched = false,
                PermanentAlarmCode = string.Empty,
                PermanentAlarmReason = string.Empty,
                PermanentAlarmUtc = null,
                PermanentAlarmCorrelationId = Guid.Empty,
                OperatorFullRelearningRequired = false,
                OperatorFullRelearningReason = string.Empty,
                OperatorFullRelearningUtc = null,
                OperatorFullRelearningCorrelationId = Guid.Empty,
                ConsecutivePeriodOverrunCount = 0,
                LastPeriodOverrunUtc = null,
                _lastElapsedBaseUtc = null
            };
        }

        #endregion

        #region RunTimeSpan 封装

        /// <summary>
        /// 以 <see cref="TimeSpan"/> 形式获取/设置累计运行时间。
        /// <para>设置时会同步更新 <see cref="RunTime"/> 字符串。</para>
        /// </summary>
        public TimeSpan RunTimeSpan
        {
            get => ParseTimeSpanSafe(RunTime);
            set => RunTime = FormatTimeSpan(value);
        }

        #endregion

        #region 初始化 & 启动/恢复/暂停

        /// <summary>
        /// 在“初始化记录列表”时调用，用于按 RunCount 规则初始化时间字段。
        /// <para>
        /// 规则：<br/>
        /// 1. 若 <see cref="RunCount"/> &gt; 0：认为已有历史记录，不修改
        ///    <see cref="StartTime"/>、<see cref="LatestStartTime"/> 和 <see cref="RunTime"/>；<br/>
        ///    仅在它们为 null/空字符串时进行补全。<br/>
        /// 2. 若 <see cref="RunCount"/> == 0：认为是全新记录，设置：<br/>
        ///    <see cref="StartTime"/> = now;<br/>
        ///    <see cref="LatestStartTime"/> = now;<br/>
        ///    <see cref="RunTimeSpan"/> = 0。
        /// </para>
        /// </summary>
        /// <param name="now">当前时间（建议使用本地时间或统一的 UTC）。</param>
        public void InitializeOnLoad(DateTime now)
        {
            MechanicalCycleCount = Math.Max(MechanicalCycleCount, RunCount);
            if (RunCount > 0)
            {
                // 已有历史：尽量保留原值，仅做容错补充
                if (!StartTime.HasValue)
                    StartTime = now;

                if (!LatestStartTime.HasValue)
                    LatestStartTime = StartTime;

                if (string.IsNullOrWhiteSpace(RunTime))
                    RunTimeSpan = TimeSpan.Zero;

                _lastElapsedBaseUtc = LatestStartTime;
            }
            else
            {
                // 新记录：全部初始化为当前时间 + 零运行时间
                StartTime = now;
                LatestStartTime = now;
                RunTimeSpan = TimeSpan.Zero;
                Status = EpbTestStatus.NotStarted;

                _lastElapsedBaseUtc = LatestStartTime;
            }

            // 永久报警是比历史正式圈数更强的安全事实。启动定位、学习或首个
            // 正式圈提交前也可能锁存报警，此时 RunCount 仍为 0，不能被上面的
            // “全新记录”初始化降级成 NotStarted。
            if (PermanentAlarmLatched)
            {
                Enabled = false;
                Status = EpbTestStatus.Alarm;
            }
        }

        /// <summary>
        /// 在“开始试验”时调用。
        /// <para>
        /// 逻辑：<br/>
        /// - 若是第一次开始（<see cref="RunCount"/> == 0 或 <see cref="StartTime"/> 为空），则同步写入 <see cref="StartTime"/>；<br/>
        /// - 始终将 <see cref="LatestStartTime"/> 更新为当前时间；<br/>
        /// - 状态设为 <see cref="EpbTestStatus.Running"/>。
        /// </para>
        /// </summary>
        /// <param name="now">当前时间。</param>
        public void MarkTestStarted(DateTime now)
        {
            if (!StartTime.HasValue || RunCount == 0)
            {
                StartTime = now;
            }

            LatestStartTime = now;
            _lastElapsedBaseUtc = now;
            Status = EpbTestStatus.Running;
        }

        /// <summary>
        /// 从“暂停”状态恢复到“运行”状态。
        /// <para>会更新 <see cref="LatestStartTime"/> 和内部基准时间。</para>
        /// </summary>
        /// <param name="now">当前时间。</param>
        public void Resume(DateTime now)
        {
            LatestStartTime = now;
            _lastElapsedBaseUtc = now;
            Status = EpbTestStatus.Running;
        }

        /// <summary>
        /// 将状态切换为“暂停”。
        /// <para>不自动累加时间，只负责状态切换；时间累加建议仍通过 <see cref="IncrementCycleAndUpdateTime"/> 统一处理。</para>
        /// </summary>
        public void Pause()
        {
            Status = EpbTestStatus.Paused;
        }

        #endregion

        #region 计数 + 运行时间累加

        /// <summary>
        /// 在“完成一圈/一次循环”后调用：
        /// <list type="number">
        /// <item>① 将当前时间与 <see cref="LatestStartTime"/> 的差值视为本次增量时长；</item>
        /// <item>② 把该增量累加到 <see cref="RunTimeSpan"/> 中；</item>
        /// <item>③ 将 <see cref="LatestStartTime"/> 更新为当前时间；</item>
        /// <item>④ 将 <see cref="RunCount"/> 自增 1；</item>
        /// <item>⑤ 若已达到或超过 <see cref="TotalCount"/>，状态切换为 <see cref="EpbTestStatus.Completed"/>。</item>
        /// </list>
        /// </summary>
        /// <param name="now">当前时间（建议与其它接口保持同一时基）。</param>
        /// <returns>本次累加到 <see cref="RunTimeSpan"/> 的时间增量。</returns>
        public TimeSpan IncrementCycleAndUpdateTime(DateTime now)
        {
            // 1) 计算当前时间与 LatestStartTime 的差值
            TimeSpan delta = TimeSpan.Zero;

            if (LatestStartTime.HasValue)
            {
                delta = now - LatestStartTime.Value;

                // 防止由于系统时间回拨导致出现负数
                if (delta < TimeSpan.Zero)
                    delta = TimeSpan.Zero;

                RunTimeSpan = RunTimeSpan + delta;
            }

            // 2) 更新 LatestStartTime / 内部基准
            LatestStartTime = now;
            _lastElapsedBaseUtc = now;

            // 3) 计数 + 状态更新
            RunCount++;

            if (TotalCount > 0 && RunCount >= TotalCount)
            {
                Status = EpbTestStatus.Completed;
            }
            else if (Status == EpbTestStatus.NotStarted)
            {
                Status = EpbTestStatus.Running;
            }

            return delta;
        }

        public long IncrementMechanicalCycle()
        {
            MechanicalCycleCount = EffectiveMechanicalCycleCount + 1;
            return MechanicalCycleCount;
        }

        /// <summary>
        /// Reconciles an absolute mechanical-completion fact from the controller
        /// or SQLite.  This is idempotent and therefore safe when the formal and
        /// mechanical UI callbacks arrive in either order.
        /// </summary>
        public long ReconcileMechanicalCycleCount(long observedCount)
        {
            MechanicalCycleCount = Math.Max(
                EffectiveMechanicalCycleCount,
                Math.Max(0L, observedCount));
            return MechanicalCycleCount;
        }

        /// <summary>
        /// 手动增加指定的运行时间（用于特殊场景修正）。
        /// <para>内部会更新 <see cref="RunTime"/> 字符串。</para>
        /// </summary>
        /// <param name="delta">要增加的时间长度，若为负数则视为 0。</param>
        public void AddElapsed(TimeSpan delta)
        {
            if (delta < TimeSpan.Zero)
                delta = TimeSpan.Zero;

            RunTimeSpan = RunTimeSpan + delta;
        }

        #endregion

        #region 状态切换辅助

        /// <summary>
        /// 将状态标记为“已完成”。
        /// <para>不会修改 <see cref="RunCount"/> 或 <see cref="RunTime"/>。</para>
        /// </summary>
        public void SetCompleted()
        {
            Status = EpbTestStatus.Completed;
        }

        /// <summary>
        /// 将状态标记为“报警”。
        /// <para>建议在上层检测到 EPB 异常时调用。</para>
        /// </summary>
        public void SetAlarm()
        {
            Status = EpbTestStatus.Alarm;
        }

        public void LatchPermanentAlarm(
            string code,
            string reason,
            DateTime utc,
            Guid correlationId)
        {
            PermanentAlarmLatched = true;
            PermanentAlarmCode = code ?? string.Empty;
            PermanentAlarmReason = reason ?? string.Empty;
            PermanentAlarmUtc = utc.Kind == DateTimeKind.Utc ? utc : utc.ToUniversalTime();
            PermanentAlarmCorrelationId = correlationId;
            Enabled = false;
            Status = EpbTestStatus.Alarm;
        }

        public void ClearPermanentAlarm()
        {
            PermanentAlarmLatched = false;
            PermanentAlarmCode = string.Empty;
            PermanentAlarmReason = string.Empty;
            PermanentAlarmUtc = null;
            PermanentAlarmCorrelationId = Guid.Empty;
            ConsecutivePeriodOverrunCount = 0;
            LastPeriodOverrunUtc = null;
            if (Status == EpbTestStatus.Alarm)
                Status = EpbTestStatus.NotStarted;
        }

        public void RequireOperatorFullRelearning(
            string reason,
            DateTime utc,
            Guid correlationId)
        {
            OperatorFullRelearningRequired = true;
            OperatorFullRelearningReason = reason ?? string.Empty;
            OperatorFullRelearningUtc = utc.Kind == DateTimeKind.Utc
                ? utc
                : utc.ToUniversalTime();
            OperatorFullRelearningCorrelationId = correlationId;
        }

        public void ClearOperatorFullRelearning()
        {
            OperatorFullRelearningRequired = false;
            OperatorFullRelearningReason = string.Empty;
            OperatorFullRelearningUtc = null;
            OperatorFullRelearningCorrelationId = Guid.Empty;
        }

        /// <summary>
        /// 将记录重置为默认状态（保留 Id 与 TotalCount）。
        /// </summary>
        public void ResetKeepTotalCount()
        {
            StartTime = DateTime.Now;
            LatestStartTime = DateTime.Now;
            RunTimeSpan = TimeSpan.Zero;
            RunCount = 0;
            MechanicalCycleCount = 0;
            Status = EpbTestStatus.NotStarted;
            ClearPermanentAlarm();
            ClearOperatorFullRelearning();
            _lastElapsedBaseUtc = null;
        }

        #endregion

        #region 时间字符串解析/格式化辅助

        /// <summary>
        /// 将 <see cref="TimeSpan"/> 按照 "d.hh:mm:ss" 格式转为字符串（例如 "1.02:30:45"）。
        /// <para>使用固定格式，便于 XML 人眼可读且兼容示例。</para>
        /// </summary>
        private static string FormatTimeSpan(TimeSpan ts)
        {
            // 使用自定义格式，确保 days 部分总是存在（即使为 0）
            return ts.ToString(@"d\.hh\:mm\:ss", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 从字符串解析为 <see cref="TimeSpan"/>，兼容 "d.hh:mm:ss"、"hh:mm:ss" 等常见格式。
        /// <para>出错时返回 <see cref="TimeSpan.Zero"/>（调用者若需要可另行抛错）。</para>
        /// </summary>
        private static TimeSpan ParseTimeSpanSafe(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return TimeSpan.Zero;

            // 尝试精确解析 d.hh:mm:ss
            TimeSpan t;
            if (TimeSpan.TryParseExact(s.Trim(), @"d\.hh\:mm\:ss",
                    CultureInfo.InvariantCulture, out t))
            {
                return t;
            }

            // 尝试常见格式（如 hh:mm:ss）
            if (TimeSpan.TryParse(s.Trim(), CultureInfo.InvariantCulture, out t))
            {
                return t;
            }

            // 回退为零，避免抛异常
            return TimeSpan.Zero;
        }

        /// <summary>
        /// 将累计运行时间格式化为 "00D 00H 00M" 形式。
        /// </summary>
        public static string FormatDHM(TimeSpan ts)
        {
            return string.Format("{0:00}D {1:00}H {2:00}M",
                (int)ts.TotalDays,
                ts.Hours,
                ts.Minutes);
        }

        /// <summary>
        /// 将累计运行时间格式化为 "00D 00H 00M 00S" 形式。
        /// </summary>
        public static string FormatDHMS(TimeSpan ts)
        {
            if (ts < TimeSpan.Zero)
                ts = TimeSpan.Zero;

            return string.Format("{0:00}D {1:00}H {2:00}M {3:00}S",
                (int)ts.TotalDays,
                ts.Hours,
                ts.Minutes,
                ts.Seconds);
        }


        #endregion
    }

    /// <summary>
    /// 项目 TestConfig.xml 中单通道报警/超限事实的原子更新载荷。
    /// Enabled 为 null 时只更新报警和超限字段。
    /// </summary>
    public sealed class EpbAlarmPersistenceUpdate
    {
        public int Channel { get; set; }
        public bool? Enabled { get; set; }
        public bool PermanentAlarmLatched { get; set; }
        public string PermanentAlarmCode { get; set; } = string.Empty;
        public string PermanentAlarmReason { get; set; } = string.Empty;
        public DateTime? PermanentAlarmUtc { get; set; }
        public Guid PermanentAlarmCorrelationId { get; set; }
        public bool? OperatorFullRelearningRequired { get; set; }
        public string OperatorFullRelearningReason { get; set; } = string.Empty;
        public DateTime? OperatorFullRelearningUtc { get; set; }
        public Guid OperatorFullRelearningCorrelationId { get; set; }
        public int ConsecutivePeriodOverrunCount { get; set; }
        public DateTime? LastPeriodOverrunUtc { get; set; }
    }

    /// <summary>
    /// EPB 试验状态（供序列化/显示/状态灯映射）。
    /// </summary>
    public enum EpbTestStatus
    {
        /// <summary>未开始。</summary>
        NotStarted = 0,

        /// <summary>运行中。</summary>
        Running = 1,

        /// <summary>已暂停。</summary>
        Paused = 2,

        /// <summary>已正常完成（RunCount 达到 TotalCount）。</summary>
        Completed = 3,

        /// <summary>出现报警/故障。</summary>
        Alarm = 4,

        /// <summary>人工停止。</summary>
        ManualStopped = 5,

        /// <summary>因同组或同设备故障被联锁停止。</summary>
        Interlocked = 6,

        /// <summary>安全预检或启动流程受阻。</summary>
        StartBlocked = 7,

        /// <summary>正在执行学习阶段。</summary>
        Learning = 8
    }
}
