using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Config;

namespace Controller
{
    /// <summary>
    /// 单个通道在本次运行中的不可变电气组错峰分配。
    /// </summary>
    internal sealed class ChannelStaggerAssignment
    {
        internal ChannelStaggerAssignment(
            int channel,
            int electricalGroupId,
            int batchOrdinal,
            int staggerMs)
        {
            Channel = channel;
            ElectricalGroupId = electricalGroupId;
            BatchOrdinal = batchOrdinal;
            StaggerMs = staggerMs;
            PhaseMs = checked(batchOrdinal * staggerMs);
        }

        public int Channel { get; }
        public int ElectricalGroupId { get; }
        /// <summary>
        /// 固定首/中/尾批次序号。它来自 XML Group/Members 中的物理位置，
        /// 不随稀疏选择、故障隔离或恢复重入而压缩。
        /// </summary>
        public int BatchOrdinal { get; }

        /// <summary>兼容旧取证字段；语义已升级为固定批次序号。</summary>
        public int SelectedIndexInGroup => BatchOrdinal;
        public int StaggerMs { get; }
        public int PhaseMs { get; }
    }

    /// <summary>
    /// 批次启动时生成的不可变错峰计划。运行期间不再读取可变配置。
    /// </summary>
    internal sealed class ElectricalStaggerPlan
    {
        private readonly IReadOnlyDictionary<int, ChannelStaggerAssignment> _assignments;

        internal ElectricalStaggerPlan(
            int periodMs,
            DateTime createdUtc,
            IDictionary<int, ChannelStaggerAssignment> assignments)
        {
            PeriodMs = periodMs;
            CreatedUtc = createdUtc;
            _assignments = new ReadOnlyDictionary<int, ChannelStaggerAssignment>(
                new Dictionary<int, ChannelStaggerAssignment>(assignments));
        }

        public int PeriodMs { get; }
        public DateTime CreatedUtc { get; }
        public IReadOnlyDictionary<int, ChannelStaggerAssignment> Assignments => _assignments;

        public ChannelStaggerAssignment Get(int channel)
        {
            if (!_assignments.TryGetValue(channel, out var assignment))
                throw new KeyNotFoundException($"通道 EPB{channel} 不在当前错峰计划中。");

            return assignment;
        }

        public DateTime GetDueUtc(DateTime anchorUtc, int channel, long zeroBasedCycleIndex)
        {
            if (zeroBasedCycleIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(zeroBasedCycleIndex));

            var assignment = Get(channel);
            var offsetMs = checked(zeroBasedCycleIndex * (long)PeriodMs + assignment.PhaseMs);
            return anchorUtc.AddMilliseconds(offsetMs);
        }
    }

    /// <summary>
    /// 电气组配置或本次选中通道无法生成安全、确定的错峰计划。
    /// </summary>
    internal sealed class ElectricalStaggerPlanException : InvalidOperationException
    {
        public ElectricalStaggerPlanException(IEnumerable<string> errors)
            : base(BuildMessage(errors, out var snapshot))
        {
            Errors = snapshot;
        }

        public IReadOnlyList<string> Errors { get; }

        private static string BuildMessage(IEnumerable<string> errors, out IReadOnlyList<string> snapshot)
        {
            var list = (errors ?? Array.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
            snapshot = new ReadOnlyCollection<string>(list);
            return "电气组错峰配置无效：" + Environment.NewLine + string.Join(Environment.NewLine, list);
        }
    }

    /// <summary>
    /// 按 XML Group/Members 中的固定物理位置生成首/中/尾相位。
    /// 未选、禁用或隔离的成员只跳过自身位置，后续成员不得向前压缩。
    /// </summary>
    internal static class ElectricalStaggerPlanner
    {
        public static ElectricalStaggerPlan Build(
            IEnumerable<int> selectedChannels,
            IEnumerable<ElectricalGroup> configuredGroups,
            int periodMs)
        {
            var errors = new List<string>();
            var selected = (selectedChannels ?? Array.Empty<int>())
                .Distinct()
                .OrderBy(x => x)
                .ToList();
            var groups = (configuredGroups ?? Array.Empty<ElectricalGroup>()).ToList();

            if (selected.Count == 0)
                errors.Add("至少选择一个EPB通道。");
            if (periodMs <= 0)
                errors.Add($"试验周期必须大于0 ms，当前为 {periodMs} ms。");

            foreach (var channel in selected.Where(x => x < 1 || x > 12))
                errors.Add($"已选通道 EPB{channel} 超出允许范围1～12。");

            var groupIds = new HashSet<int>();
            var groupByChannel = new Dictionary<int, ElectricalGroup>();
            foreach (var group in groups)
            {
                if (group == null)
                {
                    errors.Add("电气组配置中存在空Group节点。");
                    continue;
                }

                if (group.Id <= 0)
                    errors.Add($"电气组ID必须为正数，当前为 {group.Id}。");
                else if (!groupIds.Add(group.Id))
                    errors.Add($"电气组ID {group.Id} 重复。");

                if (group.StaggerMs <= 0)
                    errors.Add($"电气组 {group.Id} 的 StaggerMs 必须大于0，当前为 {group.StaggerMs}。");

                if (group.Members == null || group.Members.Count == 0)
                {
                    errors.Add($"电气组 {group.Id} 未配置成员通道。");
                    continue;
                }
                if (group.Members.Count > 3)
                    errors.Add(
                        $"电气组 {group.Id} 配置了 {group.Members.Count} 个成员；固定首/中/尾批次最多允许3个成员。");

                var membersSeenInGroup = new HashSet<int>();
                foreach (var channel in group.Members)
                {
                    if (channel < 1 || channel > 12)
                        errors.Add($"电气组 {group.Id} 的通道 EPB{channel} 超出允许范围1～12。");
                    if (!membersSeenInGroup.Add(channel))
                        errors.Add($"电气组 {group.Id} 内重复配置通道 EPB{channel}。");

                    if (groupByChannel.TryGetValue(channel, out var previous))
                        errors.Add($"通道 EPB{channel} 同时属于电气组 {previous.Id} 和 {group.Id}。");
                    else
                        groupByChannel[channel] = group;
                }
            }

            foreach (var channel in selected.Where(x => x >= 1 && x <= 12))
                if (!groupByChannel.ContainsKey(channel))
                    errors.Add($"已选通道 EPB{channel} 未配置到任何电气组。");

            var assignments = new Dictionary<int, ChannelStaggerAssignment>();
            foreach (var group in groups.Where(x => x?.Members != null))
            {
                var selectedSet = new HashSet<int>(selected);
                var selectedMembers = group.Members
                    .Select((channel, batchOrdinal) => new { channel, batchOrdinal })
                    .Where(item => selectedSet.Contains(item.channel))
                    .ToList();
                if (selectedMembers.Count == 0)
                    continue;

                for (var index = 0; index < selectedMembers.Count; index++)
                {
                    try
                    {
                        var member = selectedMembers[index];
                        var assignment = new ChannelStaggerAssignment(
                            member.channel,
                            group.Id,
                            member.batchOrdinal,
                            group.StaggerMs);
                        assignments[assignment.Channel] = assignment;
                    }
                    catch (OverflowException)
                    {
                        errors.Add($"电气组 {group.Id} 的错峰相位计算溢出。");
                        break;
                    }
                }

                if (periodMs > 0 && group.StaggerMs > 0)
                {
                    var maxPhase = (long)selectedMembers.Max(item => item.batchOrdinal) * group.StaggerMs;
                    if (maxPhase >= periodMs)
                        errors.Add(
                            $"电气组 {group.Id} 最大相位 {maxPhase} ms 必须小于试验周期 {periodMs} ms。");
                }
            }

            if (errors.Count > 0)
                throw new ElectricalStaggerPlanException(errors);

            return new ElectricalStaggerPlan(periodMs, DateTime.UtcNow, assignments);
        }
    }

    /// <summary>
    /// 一次性创建全部任务，并按计划相位延迟执行；不会等待前一相位任务完成。
    /// </summary>
    internal static class ElectricalStaggerExecutor
    {
        internal sealed class QualifiedPhaseWindow
        {
            internal QualifiedPhaseWindow(DateTime actuationAnchorUtc, DateTime deadlineUtc)
            {
                ActuationAnchorUtc = actuationAnchorUtc;
                DeadlineUtc = deadlineUtc;
            }

            public DateTime ActuationAnchorUtc { get; }
            public DateTime DeadlineUtc { get; }

            public DateTime GetDueUtc(int phaseMs)
            {
                if (phaseMs < 0) throw new ArgumentOutOfRangeException(nameof(phaseMs));
                return ActuationAnchorUtc.AddMilliseconds(phaseMs);
            }
        }

        /// <summary>
        /// 在液压资格完成后创建整组共享的执行窗口。截止点按本组最后一个相位统一选择；
        /// 若资格过晚跨过当前墙钟周期，则整组共同顺延，不能让各通道独自跨期。
        /// </summary>
        internal static QualifiedPhaseWindow CreateQualifiedPhaseWindow(
            DateTime actuationAnchorUtc,
            DateTime wallClockAnchorUtc,
            int periodMs,
            int maxPhaseMs)
        {
            if (periodMs <= 0) throw new ArgumentOutOfRangeException(nameof(periodMs));
            if (maxPhaseMs < 0) throw new ArgumentOutOfRangeException(nameof(maxPhaseMs));

            var anchor = actuationAnchorUtc.Kind == DateTimeKind.Utc
                ? actuationAnchorUtc
                : actuationAnchorUtc.ToUniversalTime();
            var wallAnchor = wallClockAnchorUtc.Kind == DateTimeKind.Utc
                ? wallClockAnchorUtc
                : wallClockAnchorUtc.ToUniversalTime();
            var elapsedAnchorMs = Math.Max(0, (anchor - wallAnchor).TotalMilliseconds);
            var anchorSlot = Math.Max(0L, (long)Math.Floor(elapsedAnchorMs / periodMs));
            var currentBoundaryUtc = wallAnchor.AddMilliseconds((anchorSlot + 1L) * periodMs);
            var lastDueUtc = anchor.AddMilliseconds(maxPhaseMs);
            if (lastDueUtc >= currentBoundaryUtc)
            {
                // 最后一个相位会跨出本墙钟周期时，整组从下一周期边界重新开始；
                // 禁止零相位留在旧周期而后续相位单独跨期。
                anchor = currentBoundaryUtc;
                lastDueUtc = anchor.AddMilliseconds(maxPhaseMs);
            }

            var elapsedMs = Math.Max(0, (lastDueUtc - wallAnchor).TotalMilliseconds);
            var boundarySlot = Math.Max(1L, (long)Math.Floor(elapsedMs / periodMs) + 1L);
            var deadlineUtc = wallAnchor.AddMilliseconds(boundarySlot * (long)periodMs);
            return new QualifiedPhaseWindow(anchor, deadlineUtc);
        }

        internal static DateTime EnsureAnchorInFuture(
            DateTime anchorUtc,
            DateTime nowUtc,
            int safetyMs = 2)
        {
            var normalizedNow = nowUtc.Kind == DateTimeKind.Utc
                ? nowUtc
                : nowUtc.ToUniversalTime();
            var normalizedAnchor = anchorUtc.Kind == DateTimeKind.Utc
                ? anchorUtc
                : anchorUtc.ToUniversalTime();
            var minimumAnchor = normalizedNow.AddMilliseconds(Math.Max(1, safetyMs));
            return normalizedAnchor >= minimumAnchor ? normalizedAnchor : minimumAnchor;
        }

        public static Task RunAsync(
            IEnumerable<int> channels,
            ElectricalStaggerPlan plan,
            DateTime anchorUtc,
            Func<int, CancellationToken, Task> work,
            CancellationToken token)
        {
            if (channels == null) throw new ArgumentNullException(nameof(channels));
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (work == null) throw new ArgumentNullException(nameof(work));

            // 整体平移过期锚点，保留同组 0/Δ/2Δ 的相对相位；禁止过期任务全部 Task.Yield 后同刻放行。
            var effectiveAnchorUtc = EnsureAnchorInFuture(anchorUtc, DateTime.UtcNow);
            var tasks = channels.Distinct().OrderBy(x => x).Select(async channel =>
            {
                var dueUtc = effectiveAnchorUtc.AddMilliseconds(plan.Get(channel).PhaseMs);
                var delay = dueUtc - DateTime.UtcNow;
                if (delay.TotalMilliseconds > 1)
                    await Task.Delay(delay, token).ConfigureAwait(false);
                else
                {
                    token.ThrowIfCancellationRequested();
                    await Task.Yield();
                }

                await work(channel, token).ConfigureAwait(false);
            });

            return Task.WhenAll(tasks);
        }
    }

    /// <summary>
    /// 本阶段硬故障隔离范围固定为故障通道本身，不扩展为电源组级停机。
    /// </summary>
    internal static class ChannelFaultIsolationPolicy
    {
        public static IReadOnlyList<int> GetChannelsToStop(int faultedChannel)
        {
            if (faultedChannel < 1 || faultedChannel > 12)
                throw new ArgumentOutOfRangeException(nameof(faultedChannel));

            return new[] { faultedChannel };
        }
    }
}
