using System;
using System.Collections.Generic;

namespace Controller.Alarm
{
    public enum AdaptiveWarningCode
    {
        ForwardPeakOvershootWarning,
        ForwardCurrentRiseStallWarning
    }

    public sealed class AdaptiveWarningEvent
    {
        public int Channel { get; set; }
        public AdaptiveWarningCode Code { get; set; }
        public DateTime OccurredUtc { get; set; }
        public double PeakCurrentA { get; set; }
        public double TargetCurrentA { get; set; }
        public double PeakErrorA { get; set; }
        public double SlopeAperMs { get; set; }
        public int WindowSpanMs { get; set; }
        public int Streak { get; set; }
        public int ConfirmThreshold { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    public sealed class WarningSnapshotRequest
    {
        public Guid TestRunId { get; set; }
        public int Channel { get; set; }
        public int CycleNumber { get; set; }
        public AdaptiveWarningEvent Warning { get; set; }
        public string IdempotencyKey =>
            $"{TestRunId:N}:{Channel}:{CycleNumber}:{Warning?.Code}";
    }

    public sealed class WarningSnapshotManifest
    {
        public WarningSnapshotRequest Trigger { get; set; }
        public DateTime FirstSampleUtc { get; set; }
        public DateTime LastSampleUtc { get; set; }
        public int SampleCount { get; set; }
        public bool IsCompleteCycle { get; set; }
        public Dictionary<string, string> FileSha256 { get; } = new Dictionary<string, string>();
        public string LinkedAlarmSnapshotPath { get; set; } = string.Empty;
    }

    public sealed class WarningSnapshotStorageStatus
    {
        public string RootDirectory { get; set; } = string.Empty;
        public long UsedBytes { get; set; }
        public long FreeBytes { get; set; }
        public long EstimatedAdditionalCycles { get; set; }
        public bool IsBelowFreeSpaceWarning { get; set; }
    }
}
