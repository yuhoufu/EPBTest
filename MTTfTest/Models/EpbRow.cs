using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MTEmbTest.Models
{
    /// <summary>
    /// UI 上用于显示/编辑的行模型，字段基本等同于 EpbCycleRunnerConfig.Record。
    /// </summary>
    public sealed class EpbRow
    {
        /// <summary>通道号（1..12）。</summary>
        public int Channel { get; set; }

        /// <summary>名称（如 EPB1）。</summary>
        public string Name { get; set; }

        /// <summary>正向实际夹紧峰值目标 A。</summary>
        public double ForwardA { get; set; }

        /// <summary>自适应模式首次提前量 A；后续由真实峰值自动修正。</summary>
        public double SafetyMarginA { get; set; }

        /// <summary>正向上电最长时长 ms（超时则强制结束正向）。</summary>
        public int FwdOnLimitMs { get; set; }

        /// <summary>夹紧保持时长 ms。</summary>
        public int HoldMs { get; set; }

        /// <summary>反向“衰减限制”电流 A（电流衰减到此以下认为达标）。</summary>
        public double RevDecayLimitA { get; set; }

        /// <summary>反向峰值“刚性衰减”最长等待 ms（超时记告警）。</summary>
        public int RevDecayRigidMaxMs { get; set; }

        /// <summary>反向固定“空行程”时长 ms（学习/生产阶段的反向空行程）。</summary>
        public int RevEmptyFixedMs { get; set; }

        /// <summary>预释放维持时长 ms（可空；为 null 表示不用）。</summary>
        public int? PreReleaseKeepMs { get; set; }

        /// <summary>忽略峰值的抑制窗口 ms（启动后前 Xms 不做峰值统计）。</summary>
        public int PeakIgnoreMs { get; set; }

        /// <summary>
        /// 每个 EPB 的目标次数，对应 Test.EpbRecords[x].TotalCount。
        /// </summary>
        public int TargetTotalCount { get; set; }
    }
}
