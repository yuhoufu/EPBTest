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
            int primaryChannel = 0)
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
                    context = new DaqIncidentContext
                    {
                        RunId = runId,
                        Device = device,
                        Generation = generation,
                        CorrelationId = Guid.NewGuid(),
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

        private static int Priority(string code)
        {
            if (string.Equals(code, "ControlQueueFull", StringComparison.OrdinalIgnoreCase)) return 400;
            if (string.Equals(code, "ControlLatencyExceeded", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(code, "DaqCallbackStale", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(code, "ControlEnqueueStale", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(code, "ControlProcessingStale", StringComparison.OrdinalIgnoreCase)) return 300;
            if (code?.IndexOf("DaqSampleStale", StringComparison.OrdinalIgnoreCase) >= 0) return 200;
            if (code?.IndexOf("OffCurrentUnverifiable", StringComparison.OrdinalIgnoreCase) >= 0) return 100;
            return 50;
        }
    }
}
