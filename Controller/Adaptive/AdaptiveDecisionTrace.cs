using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Controller.Adaptive
{
    internal sealed class AdaptiveDecisionTraceEvent
    {
        public DateTime SampleUtc { get; set; }
        public long MonotonicTicks { get; set; }
        public Guid RunId { get; set; }
        public int CycleNumber { get; set; }
        public int Channel { get; set; }
        public string Direction { get; set; }
        public EpbCurrentStage Stage { get; set; }
        public int ElapsedMs { get; set; }
        public double CurrentA { get; set; }
        public int WindowSampleCount { get; set; }
        public int WindowSpanMs { get; set; }
        public double WindowMedianA { get; set; }
        public double WindowMadA { get; set; }
        public double WindowP10A { get; set; }
        public double WindowP90A { get; set; }
        public double ReleaseThresholdA { get; set; }
        public double AllowedSpreadA { get; set; }
        public double CutoffCurrentA { get; set; }
        public double EstimatedSlopeAperMs { get; set; }
        public double PredictedPeakA { get; set; }
        public double ObservedFullRatePeakA { get; set; }
        public double PredictionLeadMs { get; set; }
        public string CutoffReason { get; set; }
        public int ReleaseCandidateElapsedMs { get; set; }
        public bool WindowQualified { get; set; }
        public string Action { get; set; }
        public string Reason { get; set; }

        public AdaptiveDecisionTraceEvent Clone()
        {
            return (AdaptiveDecisionTraceEvent)MemberwiseClone();
        }
    }

    internal struct AdaptiveDecisionTraceSample
    {
        public DateTime SampleUtc;
        public long MonotonicTicks;
        public Guid RunId;
        public int CycleNumber;
        public int Channel;
        public string Direction;
        public EpbCurrentStage Stage;
        public int ElapsedMs;
        public double CurrentA;
        public int WindowSampleCount;
        public int WindowSpanMs;
        public double WindowMedianA;
        public double WindowMadA;
        public double WindowP10A;
        public double WindowP90A;
        public double ReleaseThresholdA;
        public double AllowedSpreadA;
        public double CutoffCurrentA;
        public double EstimatedSlopeAperMs;
        public double PredictedPeakA;
        public double ObservedFullRatePeakA;
        public double PredictionLeadMs;
        public string CutoffReason;
        public int ReleaseCandidateElapsedMs;
        public bool WindowQualified;
        public string Action;
        public string Reason;

        public AdaptiveDecisionTraceEvent ToEvent()
        {
            return new AdaptiveDecisionTraceEvent
            {
                SampleUtc = SampleUtc,
                MonotonicTicks = MonotonicTicks,
                RunId = RunId,
                CycleNumber = CycleNumber,
                Channel = Channel,
                Direction = Direction,
                Stage = Stage,
                ElapsedMs = ElapsedMs,
                CurrentA = CurrentA,
                WindowSampleCount = WindowSampleCount,
                WindowSpanMs = WindowSpanMs,
                WindowMedianA = WindowMedianA,
                WindowMadA = WindowMadA,
                WindowP10A = WindowP10A,
                WindowP90A = WindowP90A,
                ReleaseThresholdA = ReleaseThresholdA,
                AllowedSpreadA = AllowedSpreadA,
                CutoffCurrentA = CutoffCurrentA,
                EstimatedSlopeAperMs = EstimatedSlopeAperMs,
                PredictedPeakA = PredictedPeakA,
                ObservedFullRatePeakA = ObservedFullRatePeakA,
                PredictionLeadMs = PredictionLeadMs,
                CutoffReason = CutoffReason,
                ReleaseCandidateElapsedMs = ReleaseCandidateElapsedMs,
                WindowQualified = WindowQualified,
                Action = Action,
                Reason = Reason
            };
        }

        public static AdaptiveDecisionTraceSample FromEvent(AdaptiveDecisionTraceEvent item)
        {
            return new AdaptiveDecisionTraceSample
            {
                SampleUtc = item.SampleUtc,
                MonotonicTicks = item.MonotonicTicks,
                RunId = item.RunId,
                CycleNumber = item.CycleNumber,
                Channel = item.Channel,
                Direction = item.Direction,
                Stage = item.Stage,
                ElapsedMs = item.ElapsedMs,
                CurrentA = item.CurrentA,
                WindowSampleCount = item.WindowSampleCount,
                WindowSpanMs = item.WindowSpanMs,
                WindowMedianA = item.WindowMedianA,
                WindowMadA = item.WindowMadA,
                WindowP10A = item.WindowP10A,
                WindowP90A = item.WindowP90A,
                ReleaseThresholdA = item.ReleaseThresholdA,
                AllowedSpreadA = item.AllowedSpreadA,
                CutoffCurrentA = item.CutoffCurrentA,
                EstimatedSlopeAperMs = item.EstimatedSlopeAperMs,
                PredictedPeakA = item.PredictedPeakA,
                ObservedFullRatePeakA = item.ObservedFullRatePeakA,
                PredictionLeadMs = item.PredictionLeadMs,
                CutoffReason = item.CutoffReason,
                ReleaseCandidateElapsedMs = item.ReleaseCandidateElapsedMs,
                WindowQualified = item.WindowQualified,
                Action = item.Action,
                Reason = item.Reason
            };
        }
    }

    internal sealed class AdaptiveDecisionTraceBuffer
    {
        private const int Channels = 13;
        private const int EventsPerChannel = 2048;
        private static readonly TimeSpan Retention = TimeSpan.FromSeconds(60);
        private readonly ChannelRing[] _rings = new ChannelRing[Channels];

        public AdaptiveDecisionTraceBuffer()
        {
            for (var i = 0; i < _rings.Length; i++)
                _rings[i] = new ChannelRing();
        }

        public void Append(AdaptiveDecisionTraceEvent item)
        {
            if (item == null) return;
            Append(AdaptiveDecisionTraceSample.FromEvent(item));
        }

        public void Append(AdaptiveDecisionTraceSample item)
        {
            if (item.Channel < 1 || item.Channel >= Channels) return;
            if (item.SampleUtc.Kind != DateTimeKind.Utc)
                item.SampleUtc = item.SampleUtc.ToUniversalTime();
            var ring = _rings[item.Channel];
            lock (ring.Gate)
            {
                ring.Items[ring.Next] = item;
                ring.Next = (ring.Next + 1) % ring.Items.Length;
                if (ring.Count < ring.Items.Length) ring.Count++;
            }
        }

        public IReadOnlyList<AdaptiveDecisionTraceEvent> Snapshot(
            DateTime alarmUtc,
            Guid runId,
            int channel)
        {
            var normalizedAlarmUtc = alarmUtc.Kind == DateTimeKind.Utc
                ? alarmUtc
                : alarmUtc.ToUniversalTime();
            var cutoff = normalizedAlarmUtc - Retention;
            if (channel < 1 || channel >= Channels)
                return Array.Empty<AdaptiveDecisionTraceEvent>();
            var ring = _rings[channel];
            var result = new List<AdaptiveDecisionTraceEvent>(ring.Count);
            lock (ring.Gate)
            {
                var start = (ring.Next - ring.Count + ring.Items.Length) % ring.Items.Length;
                for (var i = 0; i < ring.Count; i++)
                {
                    var item = ring.Items[(start + i) % ring.Items.Length];
                    if (runId != Guid.Empty && item.RunId != runId) continue;
                    if (item.SampleUtc < cutoff || item.SampleUtc > normalizedAlarmUtc.AddSeconds(1))
                        continue;
                    result.Add(item.ToEvent());
                }
            }
            return result
                .OrderBy(x => x.SampleUtc)
                .ThenBy(x => x.MonotonicTicks)
                .ToArray();
        }

        internal int CountForChannel(int channel)
        {
            if (channel < 1 || channel >= Channels) return 0;
            var ring = _rings[channel];
            lock (ring.Gate) return ring.Count;
        }

        public static void ExportCsv(
            string path,
            IEnumerable<AdaptiveDecisionTraceEvent> events)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            using (var writer = new StreamWriter(
                       path,
                       false,
                       new UTF8Encoding(false)))
            {
                writer.WriteLine(
                    "Utc,MonotonicTicks,RunId,Cycle,Channel,Direction,Stage,ElapsedMs,CurrentA," +
                    "WindowSamples,WindowSpanMs,MedianA,MadA,P10A,P90A,ReleaseThresholdA," +
                    "AllowedSpreadA,CutoffCurrentA,EstimatedSlopeAperMs,PredictedPeakA," +
                    "ObservedFullRatePeakA,PredictionLeadMs,CutoffReason,CandidateElapsedMs," +
                    "WindowQualified,Action,Reason");
                foreach (var item in events ?? Enumerable.Empty<AdaptiveDecisionTraceEvent>())
                {
                    writer.WriteLine(string.Join(",",
                        Csv(item.SampleUtc.ToString("O", CultureInfo.InvariantCulture)),
                        item.MonotonicTicks.ToString(CultureInfo.InvariantCulture),
                        item.RunId.ToString("N"),
                        item.CycleNumber.ToString(CultureInfo.InvariantCulture),
                        item.Channel.ToString(CultureInfo.InvariantCulture),
                        Csv(item.Direction),
                        Csv(item.Stage.ToString()),
                        item.ElapsedMs.ToString(CultureInfo.InvariantCulture),
                        Number(item.CurrentA),
                        item.WindowSampleCount.ToString(CultureInfo.InvariantCulture),
                        item.WindowSpanMs.ToString(CultureInfo.InvariantCulture),
                        Number(item.WindowMedianA),
                        Number(item.WindowMadA),
                        Number(item.WindowP10A),
                        Number(item.WindowP90A),
                        Number(item.ReleaseThresholdA),
                        Number(item.AllowedSpreadA),
                        Number(item.CutoffCurrentA),
                        Number(item.EstimatedSlopeAperMs),
                        Number(item.PredictedPeakA),
                        Number(item.ObservedFullRatePeakA),
                        Number(item.PredictionLeadMs),
                        Csv(item.CutoffReason),
                        item.ReleaseCandidateElapsedMs.ToString(CultureInfo.InvariantCulture),
                        item.WindowQualified.ToString(),
                        Csv(item.Action),
                        Csv(item.Reason)));
                }
            }
        }

        private static string Number(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value)
                ? string.Empty
                : value.ToString("F6", CultureInfo.InvariantCulture);
        }

        private static string Csv(string value)
        {
            var text = value ?? string.Empty;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        private sealed class ChannelRing
        {
            public readonly object Gate = new object();
            public readonly AdaptiveDecisionTraceSample[] Items =
                new AdaptiveDecisionTraceSample[EventsPerChannel];
            public int Next;
            public int Count;
        }
    }
}
