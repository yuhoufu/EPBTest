using System;
using System.Threading;

namespace DataOperation
{
    /// <summary>Current writer lifetime only. Historical/replayed totals are not live progress.</summary>
    public sealed class EpbCommittedProgress
    {
        public string WriterId { get; }
        public long Sequence { get; }
        public long MechanicalCommits { get; }
        public long SuccessfulCommits { get; }
        public long FormalCommits { get; }
        public long CommittedUtcTicks { get; }

        internal EpbCommittedProgress(string writerId, long sequence, long mechanical,
            long successful, long formal, long committedUtcTicks)
        {
            WriterId = writerId;
            Sequence = sequence;
            MechanicalCommits = mechanical;
            SuccessfulCommits = successful;
            FormalCommits = formal;
            CommittedUtcTicks = committedUtcTicks;
        }
    }

    public interface ICommittedCycleProgressSource
    {
        EpbCommittedProgress CaptureCommittedCycleProgress(int channel);
    }

    public sealed partial class EpbDiskWriter
    {
        private readonly string _recoveryWriterId = Guid.NewGuid().ToString("N");
        private readonly EpbCommittedProgress[] _recoveryCommittedProgress = new EpbCommittedProgress[EPB_COUNT + 1];
        private readonly int[] _recoveryPendingSuccessful = new int[EPB_COUNT + 1];
        private readonly int[] _recoveryPendingFormal = new int[EPB_COUNT + 1];

        // This getter intentionally does not enter _dbGate, touch SQLite or query
        // the raw journal. A stalled disk cannot block the observation thread.
        public EpbCommittedProgress CaptureCommittedCycleProgress(int channel)
        {
            if (channel < 1 || channel > EPB_COUNT) throw new ArgumentOutOfRangeException(nameof(channel));
            return Volatile.Read(ref _recoveryCommittedProgress[channel]) ??
                new EpbCommittedProgress(_recoveryWriterId, 0, 0, 0, 0, 0);
        }

        // Called under _dbGate only AFTER the corresponding durable commit.
        private void PublishRecoveryCommit(int channel, int mechanical, int successful, int formal)
        {
            var previous = CaptureCommittedCycleProgress(channel);
            Volatile.Write(ref _recoveryCommittedProgress[channel], new EpbCommittedProgress(
                _recoveryWriterId, previous.Sequence + 1, previous.MechanicalCommits + mechanical,
                previous.SuccessfulCommits + successful, previous.FormalCommits + formal, DateTime.UtcNow.Ticks));
        }

        private void StageRecoveryTerminalCommit(int channel, string previousStatus, string status)
        {
            if (!IsRecoverySuccessfulStatus(status) || IsRecoverySuccessfulStatus(previousStatus)) return;
            var formal = string.Equals(status, "completed", StringComparison.Ordinal) ? 1 : 0;
            if (_activeBatchTransaction == null) PublishRecoveryCommit(channel, 0, 1, formal);
            else
            {
                _recoveryPendingSuccessful[channel]++;
                _recoveryPendingFormal[channel] += formal;
            }
        }

        private void PublishRecoveryBatchCommits()
        {
            for (var channel = 1; channel <= EPB_COUNT; channel++)
                if (_recoveryPendingSuccessful[channel] > 0)
                    PublishRecoveryCommit(channel, 0, _recoveryPendingSuccessful[channel], _recoveryPendingFormal[channel]);
        }

        private void ClearRecoveryPendingCommits()
        {
            Array.Clear(_recoveryPendingSuccessful, 0, _recoveryPendingSuccessful.Length);
            Array.Clear(_recoveryPendingFormal, 0, _recoveryPendingFormal.Length);
        }

        private static bool IsRecoverySuccessfulStatus(string status) => status == "completed" ||
            status == "learning_completed" || status == "qualification_completed";
    }
}
