using System;
using System.Collections.Generic;
using System.Linq;

namespace Controller
{
    internal sealed class DaqIncidentContext
    {
        public Guid RunId;
        public string Device;
        public long Generation;
        public Guid CorrelationId;
        public string PrimaryCode;
        public string PrimaryReason;
        public DateTime FirstSeenUtc;
        public DateTime LastSeenUtc;
        public int PrimaryChannel;
        public int[] AffectedChannels;
        public readonly HashSet<string> DerivedCodes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public int SnapshotStarted;

        public DaqIncidentContext Clone()
        {
            var copy = new DaqIncidentContext
            {
                RunId = RunId,
                Device = Device,
                Generation = Generation,
                CorrelationId = CorrelationId,
                PrimaryCode = PrimaryCode,
                PrimaryReason = PrimaryReason,
                FirstSeenUtc = FirstSeenUtc,
                LastSeenUtc = LastSeenUtc,
                PrimaryChannel = PrimaryChannel,
                AffectedChannels = AffectedChannels?.ToArray() ?? Array.Empty<int>(),
                SnapshotStarted = SnapshotStarted
            };
            foreach (var code in DerivedCodes) copy.DerivedCodes.Add(code);
            return copy;
        }
    }

    internal readonly struct DaqIncidentObservation
    {
        public DaqIncidentObservation(
            DaqIncidentContext context,
            bool isFirst,
            bool primaryChanged)
        {
            Context = context;
            IsFirst = isFirst;
            PrimaryChanged = primaryChanged;
        }

        public DaqIncidentContext Context { get; }
        public bool IsFirst { get; }
        public bool PrimaryChanged { get; }
    }

    internal sealed class DaqIncidentLatch
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, DaqIncidentContext> _active =
            new Dictionary<string, DaqIncidentContext>(StringComparer.OrdinalIgnoreCase);

        public void BeginRun(Guid runId, IEnumerable<string> devices)
        {
            lock (_gate)
            {
                foreach (var device in (devices ?? Array.Empty<string>())
                             .Where(x => !string.IsNullOrWhiteSpace(x))
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                    _active.Remove(device);
            }
        }

        public DaqIncidentObservation Observe(
            Guid runId,
            string device,
            long generation,
            string code,
            string reason,
            DateTime seenUtc,
            int[] affectedChannels,
            int primaryChannel = 0,
            Guid correlationId = default)
        {
            if (string.IsNullOrWhiteSpace(device))
                throw new ArgumentException("Device is required.", nameof(device));
            var normalizedCode = string.IsNullOrWhiteSpace(code) ? "DaqSampleStale" : code;
            var normalizedSeen = seenUtc == default
                ? DateTime.UtcNow
                : seenUtc.ToUniversalTime();
            var affected = (affectedChannels ?? Array.Empty<int>())
                .Where(x => x >= 1 && x <= 12)
                .Distinct()
                .OrderBy(x => x)
                .ToArray();

            lock (_gate)
            {
                if (!_active.TryGetValue(device, out var context) || context.RunId != runId)
                {
                    var selectedCorrelation = correlationId;
                    if (selectedCorrelation == Guid.Empty)
                    {
                        // 周期看门狗和设备回调可能先后发现同一次公共调度停顿。
                        // 第二台设备在100ms内出现时，从第一刻起复用同一批次身份，
                        // 避免两个恢复任务在独立监督器下一次扫描前抢占液压所有权。
                        selectedCorrelation = _active.Values
                            .Where(item => item.RunId == runId &&
                                           !string.Equals(
                                               item.Device,
                                               device,
                                               StringComparison.OrdinalIgnoreCase) &&
                                           Math.Abs((normalizedSeen - item.FirstSeenUtc)
                                               .TotalMilliseconds) <= 100)
                            .OrderBy(item => item.FirstSeenUtc)
                            .Select(item => item.CorrelationId)
                            .FirstOrDefault();
                    }
                    context = new DaqIncidentContext
                    {
                        RunId = runId,
                        Device = device,
                        Generation = generation,
                        CorrelationId = selectedCorrelation == Guid.Empty
                            ? Guid.NewGuid()
                            : selectedCorrelation,
                        PrimaryCode = normalizedCode,
                        PrimaryReason = reason ?? string.Empty,
                        FirstSeenUtc = normalizedSeen,
                        LastSeenUtc = normalizedSeen,
                        PrimaryChannel = affected.Contains(primaryChannel)
                            ? primaryChannel
                            : affected.FirstOrDefault(),
                        AffectedChannels = affected
                    };
                    _active[device] = context;
                    return new DaqIncidentObservation(context.Clone(), true, false);
                }

                context.Generation = Math.Max(context.Generation, generation);
                context.LastSeenUtc = normalizedSeen;
                if (!string.Equals(
                        normalizedCode,
                        context.PrimaryCode,
                        StringComparison.OrdinalIgnoreCase))
                    context.DerivedCodes.Add(normalizedCode);
                if (affected.Length > 0)
                {
                    context.AffectedChannels = context.AffectedChannels
                        .Concat(affected)
                        .Distinct()
                        .OrderBy(x => x)
                        .ToArray();
                    if (context.PrimaryChannel <= 0 ||
                        !context.AffectedChannels.Contains(context.PrimaryChannel))
                        context.PrimaryChannel = context.AffectedChannels.FirstOrDefault();
                }

                var primaryChanged = Priority(normalizedCode) > Priority(context.PrimaryCode);
                if (primaryChanged)
                {
                    context.DerivedCodes.Add(context.PrimaryCode);
                    context.PrimaryCode = normalizedCode;
                    context.PrimaryReason = reason ?? string.Empty;
                }
                return new DaqIncidentObservation(context.Clone(), false, primaryChanged);
            }
        }

        public bool TryGet(Guid runId, string device, out DaqIncidentContext context)
        {
            lock (_gate)
            {
                if (_active.TryGetValue(device ?? string.Empty, out var found) && found.RunId == runId)
                {
                    context = found.Clone();
                    return true;
                }
                context = null;
                return false;
            }
        }

        public bool TryStartSnapshot(Guid runId, string device, out DaqIncidentContext context)
        {
            lock (_gate)
            {
                if (!_active.TryGetValue(device ?? string.Empty, out var found) || found.RunId != runId)
                {
                    context = null;
                    return false;
                }
                if (found.SnapshotStarted != 0)
                {
                    context = found.Clone();
                    return false;
                }
                found.SnapshotStarted = 1;
                context = found.Clone();
                return true;
            }
        }

        /// <summary>
        /// 结束一次已经进入终态的事故。后续同一运行、同一设备再次发生故障时，
        /// 必须创建新的关联号，不能被上一次恢复的终态去重记录误判为重复事件。
        /// </summary>
        public bool Complete(string device, Guid correlationId)
        {
            if (string.IsNullOrWhiteSpace(device) || correlationId == Guid.Empty) return false;
            lock (_gate)
            {
                if (!_active.TryGetValue(device, out var found) ||
                    found.CorrelationId != correlationId)
                    return false;
                return _active.Remove(device);
            }
        }

        private static int Priority(string code)
        {
            if (string.Equals(code, "ControlQueueFull", StringComparison.OrdinalIgnoreCase)) return 400;
            if (string.Equals(code, "ControlLatencyExceeded", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(code, "DaqCallbackStale", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(code, "DaqCallbackGap", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(code, "ControlEnqueueStale", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(code, "ControlProcessingStale", StringComparison.OrdinalIgnoreCase)) return 300;
            if (code?.IndexOf("DaqSampleStale", StringComparison.OrdinalIgnoreCase) >= 0) return 200;
            if (code?.IndexOf("OffCurrentUnverifiable", StringComparison.OrdinalIgnoreCase) >= 0) return 100;
            return 50;
        }
    }
}
