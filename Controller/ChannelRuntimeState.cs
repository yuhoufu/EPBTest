using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Controller
{
    public enum ChannelRuntimeState
    {
        NotEnabled = 0,
        Starting = 1,
        Learning = 2,
        Running = 3,
        WarningRunning = 4,
        Paused = 5,
        AlarmStopped = 6,
        InterlockStopped = 7,
        ManualStopped = 8,
        Completed = 9,
        StartBlocked = 10,
        Recovering = 11,
        SystemFault = 12,
        PausePending = 13,
        ResumeChecking = 14,
        Qualification = 15
    }

    public enum BatchPauseState
    {
        Idle = 0,
        Running = 1,
        PausePending = 2,
        Paused = 3,
        ResumeChecking = 4,
        Qualification = 5,
        Stopping = 6
    }

    public sealed class BatchPauseStateChangedEvent
    {
        public BatchPauseState State { get; set; }
        public int[] Channels { get; set; } = Array.Empty<int>();
        public DateTime TimestampUtc { get; set; }
        public string Reason { get; set; } = string.Empty;
        public Guid RunId { get; set; }
    }

    public sealed class ChannelRuntimeStateChangedEvent
    {
        /// <summary>
        ///     控制层为同一通道发布的单调递增版本号。
        ///     UI 必须用它丢弃迟到的异步消息，不能让旧停机状态覆盖新运行状态。
        /// </summary>
        public long Revision { get; set; }
        public int Channel { get; set; }
        public ChannelRuntimeState State { get; set; }
        public string ReasonCode { get; set; } = string.Empty;
        public string ReasonText { get; set; } = string.Empty;
        public int? SourceChannel { get; set; }
        public int[] AffectedChannels { get; set; } = Array.Empty<int>();
        public DateTime TimestampUtc { get; set; }
        public Guid CorrelationId { get; set; }
        public Guid RunId { get; set; }

        public ChannelRuntimeStateChangedEvent Clone()
        {
            return new ChannelRuntimeStateChangedEvent
            {
                Revision = Revision,
                Channel = Channel,
                State = State,
                ReasonCode = ReasonCode,
                ReasonText = ReasonText,
                SourceChannel = SourceChannel,
                AffectedChannels = AffectedChannels?.ToArray() ?? Array.Empty<int>(),
                TimestampUtc = TimestampUtc,
                CorrelationId = CorrelationId,
                RunId = RunId
            };
        }

        public bool IsNewerThan(ChannelRuntimeStateChangedEvent current)
        {
            if (current == null) return true;
            if (Channel != current.Channel) return false;

            // 新版控制层始终提供 Revision。保留时间比较只用于兼容进程内尚未带版本号的旧事件。
            if (Revision > 0 || current.Revision > 0)
                return Revision > current.Revision;
            return TimestampUtc > current.TimestampUtc;
        }
    }

    internal sealed class ChannelRuntimeStateStore
    {
        private readonly ConcurrentDictionary<int, ChannelRuntimeStateChangedEvent> _states = new();

        internal ChannelRuntimeStateChangedEvent Publish(
            ChannelRuntimeStateChangedEvent next,
            bool allowTerminalReset = false)
        {
            if (next == null) throw new ArgumentNullException(nameof(next));
            if (next.Channel < 1 || next.Channel > 12)
                throw new ArgumentOutOfRangeException(nameof(next.Channel));

            next.TimestampUtc = next.TimestampUtc == default ? DateTime.UtcNow : next.TimestampUtc;
            next.CorrelationId = next.CorrelationId == Guid.Empty ? Guid.NewGuid() : next.CorrelationId;
            next.AffectedChannels = (next.AffectedChannels ?? Array.Empty<int>())
                .Distinct()
                .OrderBy(x => x)
                .ToArray();
            var candidate = next.Clone();

            return _states.AddOrUpdate(
                    candidate.Channel,
                    _ => CloneWithRevision(candidate, 1),
                    (_, current) => IsLatchedStop(current.State) && !allowTerminalReset
                        ? current
                        : CloneWithRevision(candidate, current.Revision + 1))
                .Clone();
        }

        private static ChannelRuntimeStateChangedEvent CloneWithRevision(
            ChannelRuntimeStateChangedEvent state,
            long revision)
        {
            var clone = state.Clone();
            clone.Revision = revision;
            return clone;
        }

        internal ChannelRuntimeStateChangedEvent Get(int channel)
        {
            return _states.TryGetValue(channel, out var state) ? state.Clone() : null;
        }

        internal IReadOnlyList<ChannelRuntimeStateChangedEvent> Snapshot()
        {
            return _states.Values.Select(x => x.Clone()).OrderBy(x => x.Channel).ToArray();
        }

        internal static bool IsLatchedStop(ChannelRuntimeState state)
        {
            return state == ChannelRuntimeState.AlarmStopped ||
                   state == ChannelRuntimeState.InterlockStopped ||
                   state == ChannelRuntimeState.SystemFault ||
                   state == ChannelRuntimeState.StartBlocked;
        }
    }

    public sealed class StopSafetyResult
    {
        public string CorrelationId { get; set; } = string.Empty;
        public Guid RunId { get; set; }
        public bool MotorOffCommandSucceeded { get; set; }
        public bool PowerOffConfirmed { get; set; }
        public bool PressureSafeConfirmed { get; set; }
        public bool ReusedPreviousResult { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime CompletedUtc { get; set; }
        public string MotorError { get; set; } = string.Empty;
        public string PowerError { get; set; } = string.Empty;
        public string PressureError { get; set; } = string.Empty;

        public bool CanReleaseAcquisition => MotorOffCommandSucceeded && PowerOffConfirmed;
        public bool FullyConfirmed => CanReleaseAcquisition && PressureSafeConfirmed;

        public StopSafetyResult Clone(bool reused = false)
        {
            return new StopSafetyResult
            {
                CorrelationId = CorrelationId,
                RunId = RunId,
                MotorOffCommandSucceeded = MotorOffCommandSucceeded,
                PowerOffConfirmed = PowerOffConfirmed,
                PressureSafeConfirmed = PressureSafeConfirmed,
                ReusedPreviousResult = reused,
                StartedUtc = StartedUtc,
                CompletedUtc = CompletedUtc,
                MotorError = MotorError,
                PowerError = PowerError,
                PressureError = PressureError
            };
        }
    }
}
