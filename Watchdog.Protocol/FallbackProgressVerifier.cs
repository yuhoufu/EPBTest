using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace MTTFTest.Watchdog.Protocol
{
    public sealed class FallbackProgressVerifier
    {
        private readonly Dictionary<int, Tuple<long, long, long>> _baseline = new Dictionary<int, Tuple<long, long, long>>();
        private string _identity;
        private long _lastSequence;
        private long _stableSince;
        public bool Observe(WatchdogHeartbeat heartbeat, FallbackLedger ledger, long timestamp)
        {
            if (heartbeat == null || ledger == null || ledger.ManualStopped ||
                heartbeat.ProcessId != ledger.LaunchProcessId ||
                heartbeat.ProcessStartUtcTicks != ledger.LaunchProcessStartUtcTicks ||
                heartbeat.ManualStopRequested || heartbeat.RunId != ledger.RunId ||
                heartbeat.RunEpoch <= 0) return false;
            var identity = heartbeat.ProcessId + ":" + heartbeat.ProcessStartUtcTicks + ":" + heartbeat.RunEpoch;
            if (_identity != identity)
            { _identity = identity; _baseline.Clear(); _lastSequence = 0; _stableSince = 0; }
            if (heartbeat.Sequence <= _lastSequence) return false;
            _lastSequence = heartbeat.Sequence;
            var required = (heartbeat.RecoveryEligibleChannels ?? Array.Empty<int>())
                .Except(heartbeat.CompletedChannels ?? Array.Empty<int>())
                .Except(heartbeat.ManuallyDisabledChannels ?? Array.Empty<int>())
                .Except(heartbeat.PermanentAlarmedChannels ?? Array.Empty<int>()).Distinct().ToArray();
            if (required.Length == 0) return false;
            var all = true;
            foreach (var channel in required)
            {
                var progress = (heartbeat.ChannelProgress ?? Array.Empty<WatchdogChannelProgress>())
                    .SingleOrDefault(value => value.Channel == channel);
                if (progress == null || progress.FormalCommitRunEpoch != heartbeat.RunEpoch ||
                    string.IsNullOrEmpty(progress.FormalCommitIdentity)) { all = false; continue; }
                if (!_baseline.TryGetValue(channel, out var start))
                { start = Tuple.Create(progress.FormalCommitSequence, progress.DoCommandSequence, progress.MechanicalCompletedCount); _baseline[channel] = start; }
                if (progress.FormalCommitSequence - start.Item1 < 3 || progress.DoCommandSequence <= start.Item2 || progress.MechanicalCompletedCount - start.Item3 < 3 ||
                    !string.Equals(progress.State, "Running", StringComparison.OrdinalIgnoreCase)) all = false;
            }
            if (!all) { _stableSince = 0; return false; }
            if (_stableSince == 0) _stableSince = timestamp;
            var stableMs = Math.Max(1000, heartbeat.ExpectedCyclePeriodMs);
            return (timestamp - _stableSince) * 1000d / Stopwatch.Frequency >= stableMs;
        }
    }
}
