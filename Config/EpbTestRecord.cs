using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Config
{
    // ======= 嵌套类型：EPB 试验记录 =======
    /// <summary>
    /// 单通道 EPB 试验记录（POCO）。支持将 RunTime 作为字符串进行序列化，
    /// 并在内部用 TimeSpan 做加法/解析。
    /// </summary>
    public sealed class EpbTestRecord
    {
        /// <summary>EPB 通道 Id（1..12）</summary>
        public int Id { get; set; }

        /// <summary>
        /// 首次开始时间（可为 null）。用于记录最初的 StartTime。
        /// 序列化/保存时的文本格式建议使用 "yyyy-MM-dd HH:mm:ss"。
        /// </summary>
        public DateTime? StartTime { get; set; }

        /// <summary>
        /// 最近一次开始（或恢复）时间，每次 Start/Resume 时更新（用于暂停时计算已运行时长增量）。
        /// 序列化文本格式同 StartTime。
        /// </summary>
        public DateTime? LatestStartTime { get; set; }

        /// <summary>
        /// 已累计运行时长（字符串形式），格式示例 "1.02:30:45"（d.hh:mm:ss）。
        /// 便于直接写入 XML/显示。内部通过 RunTimeSpan 操作。
        /// </summary>
        public string RunTime { get; set; } = FormatTimeSpan(TimeSpan.Zero);

        /// <summary>试验计划/总次数（外部设置）。</summary>
        public int TotalCount { get; set; }

        /// <summary>已运行次数（完成的循环次数）。</summary>
        public int RunCount { get; set; }

        /// <summary>当前状态（未开始 / 运行中 / 已暂停）。</summary>
        public EpbTestStatus Status { get; set; } = EpbTestStatus.NotStarted;

        // ---- 内部临时字段（非序列化）：记录最近一次 resume 的时间点，用于 pause 时累加运行时长 ----
        private DateTime? _resumeAtUtc;

        /// <summary>创建一个带默认值的记录（Id 指定）。</summary>
        public static EpbTestRecord CreateDefault(int id)
        {
            return new EpbTestRecord
            {
                Id = id,
                StartTime = null,
                LatestStartTime = null,
                RunTime = FormatTimeSpan(TimeSpan.Zero),
                TotalCount = 0,
                RunCount = 0,
                Status = EpbTestStatus.NotStarted,
                _resumeAtUtc = null
            };
        }

        /// <summary>以 TimeSpan 形式获取/设置 RunTime（会同步更新 RunTime 字符串）。</summary>
        public TimeSpan RunTimeSpan
        {
            get => ParseTimeSpanSafe(RunTime);
            set => RunTime = FormatTimeSpan(value);
        }

        /// <summary>
        /// 标记为开始（首次开始），如果 StartTime 为空则写入；同时设置 LatestStartTime 与内部 resume 时间点。
        /// 不会修改 RunCount（RunCount 通常在完成一个循环时由上层调用 IncrementCycle）。
        /// </summary>
        public void Start(DateTime nowUtc)
        {
            if (!StartTime.HasValue)
                StartTime = nowUtc;
            LatestStartTime = nowUtc;
            _resumeAtUtc = nowUtc;
            Status = EpbTestStatus.Running;
        }

        /// <summary>
        /// 恢复/继续（比如从暂停恢复），更新 LatestStartTime 并设置内部 resume 时间点。
        /// </summary>
        public void Resume(DateTime nowUtc)
        {
            LatestStartTime = nowUtc;
            _resumeAtUtc = nowUtc;
            Status = EpbTestStatus.Running;
        }

        /// <summary>
        /// 暂停：如果之前处于 Running 且存在 resume 时间点，则累加从 resume 到 now 的时长到 RunTime，
        /// 清理内部 resume 时间点，并把状态设为 Paused。
        /// 返回本次累加的 TimeSpan 以便上层记录日志（可选）。
        /// </summary>
        public TimeSpan Pause(DateTime nowUtc)
        {
            if (_resumeAtUtc.HasValue)
            {
                var delta = nowUtc - _resumeAtUtc.Value;
                RunTimeSpan = RunTimeSpan + delta;
                _resumeAtUtc = null;
                Status = EpbTestStatus.Paused;
                return delta;
            }

            Status = EpbTestStatus.Paused;
            return TimeSpan.Zero;
        }

        /// <summary>手动在完成一次循环后增加已运行次数（上层在循环完成时调用）。</summary>
        public void IncrementCycle()
        {
            RunCount++;
        }

        /// <summary>把累计运行时长增加一个 TimeSpan（并更新字符串）。</summary>
        public void AddElapsed(TimeSpan delta)
        {
            RunTimeSpan = RunTimeSpan + delta;
        }

        /// <summary>把记录重置为默认（保留 Id）。</summary>
        public void Reset()
        {
            StartTime = null;
            LatestStartTime = null;
            RunTime = FormatTimeSpan(TimeSpan.Zero);
            TotalCount = 0;
            RunCount = 0;
            Status = EpbTestStatus.NotStarted;
            _resumeAtUtc = null;
        }

        // ---------- 辅助：时间字符串解析/格式化 ----------

        /// <summary>
        /// 将 TimeSpan 按照 "d.hh:mm:ss" 格式转为字符串（例如 "1.02:30:45"）。
        /// 使用固定格式以便 XML 人眼可读且兼容示例。
        /// </summary>
        private static string FormatTimeSpan(TimeSpan ts)
        {
            // 使用自定义格式，确保 days 部分总是存在（即使为 0）
            return ts.ToString(@"d\.hh\:mm\:ss", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 从字符串解析为 TimeSpan，兼容 "d.hh:mm:ss"、"hh:mm:ss" 等常见格式。
        /// 出错时返回 TimeSpan.Zero（调用者若需要可另行抛错）。
        /// </summary>
        private static TimeSpan ParseTimeSpanSafe(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return TimeSpan.Zero;

            // 尝试精确解析 d.hh:mm:ss
            if (TimeSpan.TryParseExact(s.Trim(), @"d\.hh\:mm\:ss", CultureInfo.InvariantCulture, out var t1))
                return t1;

            // 尝试常见 hh:mm:ss 等
            if (TimeSpan.TryParse(s.Trim(), CultureInfo.InvariantCulture, out var t2))
                return t2;

            // 回退为零，避免抛异常
            return TimeSpan.Zero;
        }
    } // class EpbTestRecord


    /// <summary>EPB 试验状态（供序列化/显示）。</summary>
    public enum EpbTestStatus
    {
        /// <summary>未开始（XML/显示：未开始）</summary>
        NotStarted = 0,

        /// <summary>运行中（XML/显示：运行中）</summary>
        Running = 1,

        /// <summary>已暂停（XML/显示：已暂停）</summary>
        Paused = 2
    }


} // class TestConfig



