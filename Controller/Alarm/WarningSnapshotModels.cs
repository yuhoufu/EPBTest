using System;
using System.Collections.Generic;
using System.Linq;

namespace Controller.Alarm
{
    public enum AdaptiveWarningCode
    {
        ForwardPeakOvershootWarning,
        ForwardCurrentRiseStallWarning,
        PeakEvidenceMismatchWarning,
        PeakEvidenceLagWarning,
        RecoverableControlFaultWarning
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
        public string FaultCode { get; set; } = string.Empty;
        public string ScopeKey { get; set; } = string.Empty;
        public Guid CorrelationId { get; set; }
        public long AttemptId { get; set; }

        public string NormalizedCode => string.IsNullOrWhiteSpace(FaultCode)
            ? Code.ToString()
            : FaultCode.Trim();
    }

    public sealed class WarningSnapshotRequest
    {
        public Guid TestRunId { get; set; }
        public int Channel { get; set; }
        public int CycleNumber { get; set; }
        public AdaptiveWarningEvent Warning { get; set; }
        public string IdempotencyKey =>
            $"{TestRunId:N}:{Channel}:{(Warning?.AttemptId > 0 ? Warning.AttemptId : CycleNumber)}:{Warning?.NormalizedCode}";
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

    public enum FaultConfirmationDisposition
    {
        RecoverableWarningFault,
        ConfirmedRecoverableAlarm
    }

    public sealed class FaultConfirmationObservation
    {
        public FaultConfirmationDisposition Disposition { get; set; }
        public int Streak { get; set; }
        public int ConfirmThreshold { get; set; }
        public bool DuplicateAttempt { get; set; }
    }

    /// <summary>按作用域、故障码和尝试圈去重的连续复现确认器。</summary>
    public sealed class FaultConfirmationTracker
    {
        private sealed class State
        {
            public long LastAttemptId;
            public int Streak;
            public FaultConfirmationObservation LastObservation;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, State> _states =
            new Dictionary<string, State>(StringComparer.OrdinalIgnoreCase);

        public FaultConfirmationObservation Observe(
            string scopeKey,
            string faultCode,
            long attemptId,
            int confirmThreshold)
        {
            var threshold = Math.Max(3, confirmThreshold);
            var key = $"{scopeKey ?? string.Empty}:{faultCode ?? string.Empty}";
            lock (_gate)
            {
                if (!_states.TryGetValue(key, out var state))
                {
                    state = new State();
                    _states[key] = state;
                }
                if (attemptId != 0 && state.LastAttemptId == attemptId && state.LastObservation != null)
                {
                    return new FaultConfirmationObservation
                    {
                        Disposition = state.LastObservation.Disposition,
                        Streak = state.LastObservation.Streak,
                        ConfirmThreshold = state.LastObservation.ConfirmThreshold,
                        DuplicateAttempt = true
                    };
                }

                state.LastAttemptId = attemptId;
                state.Streak++;
                state.LastObservation = new FaultConfirmationObservation
                {
                    Disposition = state.Streak >= threshold
                        ? FaultConfirmationDisposition.ConfirmedRecoverableAlarm
                        : FaultConfirmationDisposition.RecoverableWarningFault,
                    Streak = state.Streak,
                    ConfirmThreshold = threshold
                };
                return state.LastObservation;
            }
        }

        public void ResetScope(string scopeKey)
        {
            var prefix = (scopeKey ?? string.Empty) + ":";
            lock (_gate)
            {
                foreach (var key in _states.Keys
                             .Where(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                             .ToArray())
                    _states.Remove(key);
            }
        }

        public void Clear()
        {
            lock (_gate) _states.Clear();
        }
    }
}
