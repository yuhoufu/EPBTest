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

    internal sealed class AdaptiveDecisionTraceBuffer
    {
        private const int MaximumEvents = 72_000;
        private static readonly TimeSpan Retention = TimeSpan.FromSeconds(60);
        private readonly object _gate = new object();
        private readonly Queue<AdaptiveDecisionTraceEvent> _events =
            new Queue<AdaptiveDecisionTraceEvent>();

        public void Append(AdaptiveDecisionTraceEvent item)
        {
            if (item == null) return;
            var copy = item.Clone();
            if (copy.SampleUtc.Kind != DateTimeKind.Utc)
                copy.SampleUtc = copy.SampleUtc.ToUniversalTime();

            lock (_gate)
            {
                _events.Enqueue(copy);
                var cutoff = copy.SampleUtc - Retention;
                while (_events.Count > MaximumEvents ||
                       (_events.Count > 0 && _events.Peek().SampleUtc < cutoff))
                    _events.Dequeue();
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
            lock (_gate)
            {
                return _events
                    .Where(x => x.Channel == channel &&
                                (runId == Guid.Empty || x.RunId == runId) &&
                                x.SampleUtc >= cutoff &&
                                x.SampleUtc <= normalizedAlarmUtc.AddSeconds(1))
                    .OrderBy(x => x.SampleUtc)
                    .ThenBy(x => x.MonotonicTicks)
                    .Select(x => x.Clone())
                    .ToArray();
            }
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
    }
}
