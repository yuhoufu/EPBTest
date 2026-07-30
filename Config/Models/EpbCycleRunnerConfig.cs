using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Config.Models
{
    /// <summary>
    /// EPB 循环配置（新版）。对应 TestConfig.xml 的 &lt;EpbCycleRunnerConfig&gt;，
    /// 内部包含 12 个 &lt;Record&gt;（每通道一条）。
    /// </summary>
    public sealed class EpbCycleRunnerConfig
    {
        /// <summary>
        /// 每个通道的一条记录（映射 XML 的 &lt;Record&gt;）。
        /// </summary>
        public sealed class Record
        {
            /// <summary>通道号（1..12）。</summary>
            public int Channel { get; set; }

            /// <summary>通道名（如“EPB1”）。</summary>
            public string Name { get; set; }

            /// <summary>正向实际夹紧峰值目标（A）。</summary>
            public double ForwardA { get; set; }

            /// <summary>
            /// AdaptiveCurrent 模式首次控流的提前量种子（A）；获得真实断电尾部观测后，
            /// 程序改用每通道自适应预测值。LegacyFixedTiming 模式仍保留原有固定余量语义。
            /// </summary>
            public double SafetyMarginA { get; set; }

            /// <summary>正向上电最长时长限制（ms）。超过即强制切换/断电。</summary>
            public int FwdOnLimitMs { get; set; }

            /// <summary>夹紧保持时长（ms）。</summary>
            public int HoldMs { get; set; }

            /// <summary>反向电流“衰减限值”（A）。低于该值视为衰减完成。</summary>
            public double RevDecayLimitA { get; set; }

            /// <summary>反向峰值“刚性衰减”最长等待（ms）。</summary>
            public int RevDecayRigidMaxMs { get; set; }

            /// <summary>反向固定空行程时长（ms）。</summary>
            public int RevEmptyFixedMs { get; set; }

            /// <summary>（可选）预释放保持时长（ms）。XML 未配置则为 null。</summary>
            public int? PreReleaseKeepMs { get; set; }

            /// <summary>
            /// （可选）预释放寻找反向空行程的最长等待（ms）。
            /// XML 未配置时使用 <see cref="RevDecayRigidMaxMs"/>，不得与保持时长混用。
            /// </summary>
            public int? PreReleaseDetectTimeoutMs { get; set; }

            /// <summary>上电涌流忽略时间（ms）。</summary>
            public int PeakIgnoreMs { get; set; }
        }

        /// <summary>
        /// Key=Channel（1..12），Value=该通道的记录。
        /// </summary>
        public System.Collections.Generic.Dictionary<int, Record> Channels { get; }
            = new System.Collections.Generic.Dictionary<int, Record>();

        /// <summary>
        /// 获取指定通道的配置；不存在返回 <c>null</c>。
        /// </summary>
        public Record GetRunnerChannel(int channel)
        {
            if (channel <= 0) throw new System.ArgumentOutOfRangeException(nameof(channel));
            Channels.TryGetValue(channel, out var r);
            return r;
        }

        /// <summary>
        /// 尝试获取指定通道的配置。
        /// </summary>
        public bool TryGetRunnerChannel(int channel, out Record record)
        {
            record = null;
            if (channel <= 0) return false;
            return Channels.TryGetValue(channel, out record);
        }
    }
}
