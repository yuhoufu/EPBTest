using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.IO.MemoryMappedFiles;

namespace DataOperation;

public sealed class DaqSequenceGapException : IOException
{
    public DaqSequenceGapException(string message) : base(message) { }
}

public sealed class CycleAccountingSummary
{
    public long Mechanical { get; set; }
    public long ValidFormal { get; set; }
    public long LearningOrQualification { get; set; }
    public long Invalid { get; set; }
}

public interface IMechanicalReceiptRecorder
{
    bool TryRecordMechanicalCompletion(int channel, int cycle, DateTime completedUtc);
}

/// <summary>
/// Raw records are committed to a FULL synchronous WAL before touching a ring file.
/// A frame names the immutable cycle, sample offset and physical destination; replay
/// overwrites that destination instead of appending or crediting mechanical work.
/// Frames are reclaimed only after BOTH ring and index have been flushed.
/// All callers own the channel gate. Journal and index gates are never nested.
/// </summary>
public sealed partial class EpbDiskWriter
{
    private void ValidateOrTerminateSequenceGap(int channel, EpbState state, string device, long generation, long sequence)
    {
        if (state.SealInProgress || !state.CurrentCycle.HasValue || !state.SequenceBoundaryEnabled || state.DataGapLatched ||
            state.ActiveCycleLimitLatched ||
            (state.CurrentCycleEndSequence.HasValue && sequence > state.CurrentCycleEndSequence.Value)) return;
        var identityMismatch = !string.Equals(state.CurrentCycleDevice, device, StringComparison.OrdinalIgnoreCase) ||
                               state.CurrentCycleGeneration != generation;
        if (!identityMismatch && (sequence <= state.CurrentCycleLastSequence ||
            (state.CurrentCycleLastSequence == 0 && sequence <= state.CurrentCycleStartAfterSequence + 1) ||
            sequence == state.CurrentCycleLastSequence + 1)) return;
        lock (_dbGate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"UPDATE epb_cycles SET status='data_gap',termination_reason=@reason,
last_complete_sequence=@last,gap_start_sequence=@first,gap_end_sequence=@end,
device_generation=@gen,sample_count=MAX(COALESCE(sample_count,0),@n),end_time=@now
WHERE epb_id=@ch AND cycle_number=@cy AND status='running';";
            cmd.Parameters.AddWithValue("@ch", channel);
            cmd.Parameters.AddWithValue("@cy", state.CurrentCycle.Value);
            cmd.Parameters.AddWithValue("@reason", identityMismatch ? "DaqGenerationMismatch" : "DaqSequenceGap");
            cmd.Parameters.AddWithValue("@last", state.CurrentCycleLastSequence);
            cmd.Parameters.AddWithValue("@first", Math.Max(state.CurrentCycleLastSequence, state.CurrentCycleStartAfterSequence) + 1);
            cmd.Parameters.AddWithValue("@end", Math.Max(Math.Max(state.CurrentCycleLastSequence, state.CurrentCycleStartAfterSequence) + 1, sequence - 1));
            cmd.Parameters.AddWithValue("@gen", state.CurrentCycleGeneration);
            cmd.Parameters.AddWithValue("@n", state.CurrentSampleIndex);
            cmd.Parameters.AddWithValue("@now", DateTime.Now.ToString("o"));
            if (cmd.ExecuteNonQuery() != 1) throw new InvalidDataException("SequenceGapTerminalCommitFailed");
        }
        state.DataGapLatched = true;
        RecordCycleTerminalReceipt(channel, state.CurrentCycle.Value);
        throw new DaqSequenceGapException($"EPB={channel} Cycle={state.CurrentCycle} Previous={state.CurrentCycleLastSequence} Current={sequence} GenerationMismatch={identityMismatch}");
    }

    private readonly object _rawJournalGate = new();
    private SQLiteConnection _rawJournal;
    private SQLiteTransaction _rawJournalTransaction;
    private readonly DateTime[] _rawCheckpointUtc = new DateTime[EPB_COUNT + 1];
    private sealed class PendingRawCheckpoint
    {
        internal List<RawFrame> Frames;
        internal Task Flush;
    }
    private readonly PendingRawCheckpoint[] _pendingRawCheckpoints = new PendingRawCheckpoint[EPB_COUNT + 1];
    private Action<int> _rawCheckpointFlushTestHook;
    private long _rawJournalBytes;
    // Owned by the channel gate, published only after the device FULL commit.
    // Consumed by the immediately following ring write and cleared at batch exit.
    private sealed class CommittedDeviceFrame
    {
        internal int Cycle, First, Count;
        internal long Position, Generation;
        internal byte[] Digest;
    }
    private readonly CommittedDeviceFrame[] _committedDeviceFrames = new CommittedDeviceFrame[EPB_COUNT + 1];
    private Action _deviceRawCommittedTestHook;
    private sealed class RawFrame
    {
        public long Id, Position, Generation, Sequence;
        public int Channel, Cycle, First, Count;
        public byte[] Data;
    }

    private void StageDeviceRawJournal(DeviceBatchBoundary boundary, DateTime[] times,
        EpbChannelDiskBatch[] channels, int channelCount, int sampleCount)
    {
        // One FULL commit per device batch rather than one fsync per channel.
        // No index gate is taken here, so the second device cannot invert lock order.
        // Caller owns all channel gates: eligibility cannot change during this check.
        var hasFrames = false;
        for (var i = 0; i < channelCount; i++)
            if (ShouldStageRawFrame(_states[channels[i].EpbId], boundary, sampleCount))
            { hasFrames = true; break; }
        if (!hasFrames) return;
        var stageStarted = Stopwatch.GetTimestamp();
        try
        {
            var gateStarted = Stopwatch.GetTimestamp();
            lock (_rawJournalGate)
            {
                if (_writeTiming != null) _writeTiming.RawGateWaitMs += ElapsedWriteMs(gateStarted);
                var beforeBytes = _rawJournalBytes;
                var committed = new Dictionary<int, CommittedDeviceFrame>();
                using var tx = _rawJournal.BeginTransaction();
                _rawJournalTransaction = tx;
                try
                {
                    for (var i = 0; i < channelCount; i++)
                    {
                        var channel = channels[i];
                        var state = _states[channel.EpbId];
                        if (!ShouldStageRawFrame(state, boundary, sampleCount)) continue;
                        var from = 0;
                        var to = sampleCount;
                        if (!state.SequenceBoundaryEnabled)
                        {
                            // Learning uses the timestamp-bounded recorder path. Stage
                            // the same accepted slice as WriteBatch, in this device's
                            // FULL transaction instead of one fsync per learning channel.
                            while (from < to && times[from].ToUniversalTime() < state.CurrentCycleStartUtc) from++;
                            if (state.CurrentCycleEndUtc.HasValue)
                                while (to > from && times[to - 1].ToUniversalTime() > state.CurrentCycleEndUtc.Value) to--;
                        }
                        var accepted = to - from;
                        if (accepted <= 0 || (_policy.MaxActiveCycleRecords > 0 &&
                            state.CurrentSampleIndex + accepted > _policy.MaxActiveCycleRecords)) continue;
                        var records = new SampleRecord[accepted];
                        for (var sample = 0; sample < accepted; sample++)
                            records[sample] = new SampleRecord { TimestampBinary = times[from + sample].ToLocalTime().ToBinary(),
                                CycleNumber = state.CurrentCycle.Value, SampleIndex = state.CurrentSampleIndex + sample,
                                EpbCurrent = channel.Currents[from + sample], GroupPressure = channel.Pressures[from + sample] };
                        var digest = AppendRawJournal(channel.EpbId, state, records, accepted,
                            state.SequenceBoundaryEnabled ? boundary.Sequence : (long?)null);
                        committed[channel.EpbId] = new CommittedDeviceFrame
                        {
                            Cycle = records[0].CycleNumber, First = records[0].SampleIndex,
                            Count = accepted, Position = state.TotalWritten % state.CapacityRecords,
                            Generation = state.CurrentCycleGeneration, Digest = digest
                        };
                    }
                    var commitStarted = Stopwatch.GetTimestamp();
                    try { tx.Commit(); }
                    finally { if (_writeTiming != null) _writeTiming.RawCommitMs += ElapsedWriteMs(commitStarted); }
                    foreach (var item in committed) _committedDeviceFrames[item.Key] = item.Value;
                }
                catch { _rawJournalBytes = beforeBytes; throw; }
                finally { _rawJournalTransaction = null; }
            }
        }
        finally { if (_writeTiming != null) _writeTiming.RawStageMs += ElapsedWriteMs(stageStarted); }
    }

    private bool ShouldStageRawFrame(EpbState state, DeviceBatchBoundary boundary, int sampleCount) =>
        !state.SealInProgress && state.CurrentCycle.HasValue && sampleCount > 0 &&
        !state.ActiveCycleLimitLatched &&
        (!state.SequenceBoundaryEnabled ||
         ((!state.CurrentCycleEndSequence.HasValue || boundary.Sequence <= state.CurrentCycleEndSequence.Value) &&
          (state.CurrentCycleGeneration != boundary.Generation || boundary.Sequence > state.CurrentCycleLastSequence) &&
          (_policy.MaxActiveCycleRecords <= 0 || state.CurrentSampleIndex + sampleCount <= _policy.MaxActiveCycleRecords)));

    private bool ValidateOrQuarantineBatch(DeviceBatchBoundary boundary, DateTime[] times,
        EpbChannelDiskBatch channel, int count)
    {
        var formatInvalid = count < 0 || times == null || count > times.Length ||
            channel.Currents == null || channel.Pressures == null ||
            count > channel.Currents.Length || count > channel.Pressures.Length;
        if (!formatInvalid)
            for (var i = 0; i < count; i++)
                if (double.IsNaN(channel.Currents[i]) || double.IsInfinity(channel.Currents[i]) ||
                    double.IsNaN(channel.Pressures[i]) || double.IsInfinity(channel.Pressures[i]) ||
                    (i > 0 && times[i] < times[i - 1])) { formatInvalid = true; break; }
        if (!formatInvalid) return true;
        var state = GetState(channel.EpbId);
        lock (state.Gate)
        {
            bool first;
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(count);
                var timeCount = Math.Min(Math.Max(0, count), times?.Length ?? 0);
                writer.Write(timeCount);
                for (var i = 0; i < timeCount; i++) writer.Write(times[i].ToBinary());
                foreach (var values in new[] { channel.Currents, channel.Pressures })
                {
                    var n = Math.Min(Math.Max(0, count), values?.Length ?? 0);
                    writer.Write(n);
                    for (var i = 0; i < n; i++) writer.Write(values[i]);
                }
            }
            var evidence = stream.ToArray();
            lock (_rawJournalGate)
            {
                using var cmd = _rawJournal.CreateCommand();
                cmd.Parameters.AddWithValue("@dev", boundary.Device); cmd.Parameters.AddWithValue("@gen", boundary.Generation);
                cmd.Parameters.AddWithValue("@seq", boundary.Sequence); cmd.Parameters.AddWithValue("@ch", channel.EpbId);
                cmd.CommandText = "SELECT COUNT(*) FROM invalid_batches WHERE device=@dev AND generation=@gen AND sequence=@seq AND channel=@ch;";
                first = Convert.ToInt32(cmd.ExecuteScalar()) == 0;
                if (first)
                {
                    if (_rawJournalBytes + evidence.Length > _policy.RawJournalMaxBytes)
                        throw new IOException("RawJournalCapacityExceeded: malformed evidence retained in queue");
                    cmd.Parameters.AddWithValue("@cy", state.SealInProgress ? 0 : state.CurrentCycle ?? 0); cmd.Parameters.AddWithValue("@data", evidence);
                    cmd.CommandText = @"INSERT INTO invalid_batches VALUES(@dev,@gen,@seq,@ch,@cy,'InvalidDaqBatch',@data);";
                    cmd.ExecuteNonQuery();
                    _rawJournalBytes += evidence.Length;
                }
            }
            if (!state.SealInProgress && state.CurrentCycle.HasValue && !state.DataGapLatched)
            {
                lock (_dbGate)
                {
                    using var cmd = _conn.CreateCommand();
                    cmd.CommandText = @"UPDATE epb_cycles SET status='data_gap',termination_reason='InvalidDaqBatch',
end_time=@now,gap_start_sequence=@seq,gap_end_sequence=@seq WHERE epb_id=@ch AND cycle_number=@cy AND status='running';";
                    cmd.Parameters.AddWithValue("@ch", channel.EpbId); cmd.Parameters.AddWithValue("@cy", state.CurrentCycle.Value);
                    cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O")); cmd.Parameters.AddWithValue("@seq", boundary.Sequence);
                    cmd.ExecuteNonQuery();
                }
                state.DataGapLatched = true;
                RecordCycleTerminalReceipt(channel.EpbId, state.CurrentCycle.Value);
                first = true;
            }
            if (first) throw new DaqSequenceGapException("InvalidDaqBatch durably quarantined: EPB=" + channel.EpbId);
            return false;
        }
    }

    private void OpenRawJournal()
    {
        using (var index = _conn.CreateCommand())
        {
            index.CommandText = @"CREATE TABLE IF NOT EXISTS cycle_receipts(
epb_id INTEGER NOT NULL,cycle_number INTEGER NOT NULL,mechanical INTEGER NOT NULL DEFAULT 0,
completed_at TEXT,status TEXT,PRIMARY KEY(epb_id,cycle_number));
INSERT OR IGNORE INTO cycle_receipts(epb_id,cycle_number,mechanical,completed_at,status)
SELECT epb_id,cycle_number,mechanical_completed,mechanical_completed_at,status FROM epb_cycles;
CREATE TABLE IF NOT EXISTS mechanical_baselines(epb_id INTEGER PRIMARY KEY, legacy_offset INTEGER NOT NULL);";
            index.ExecuteNonQuery();
        }
        _rawJournal = new SQLiteConnection($"Data Source={Path.Combine(_indexDir, "raw-journal.db")};Pooling=False;Journal Mode=WAL;Synchronous=Full");
        _rawJournal.Open();
        using var cmd = _rawJournal.CreateCommand();
        cmd.CommandText = @"CREATE TABLE IF NOT EXISTS raw_frames(
id INTEGER PRIMARY KEY, channel INTEGER NOT NULL, cycle INTEGER NOT NULL,
first_sample INTEGER NOT NULL, position INTEGER NOT NULL, sample_count INTEGER NOT NULL,
generation INTEGER NOT NULL, sequence INTEGER NOT NULL, data BLOB NOT NULL, sha256 BLOB NOT NULL,
UNIQUE(channel,cycle,first_sample));
CREATE TABLE IF NOT EXISTS mechanical_pending(channel INTEGER NOT NULL,cycle INTEGER NOT NULL,completed_at TEXT NOT NULL,
PRIMARY KEY(channel,cycle));
CREATE TABLE IF NOT EXISTS invalid_batches(device TEXT,generation INTEGER,sequence INTEGER,channel INTEGER,
cycle INTEGER,reason TEXT,evidence BLOB,PRIMARY KEY(device,generation,sequence,channel));";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "PRAGMA quick_check;";
        if (!string.Equals(Convert.ToString(cmd.ExecuteScalar()), "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("RawJournalIntegrityFailed");
        cmd.CommandText = "SELECT (SELECT COALESCE(SUM(length(data)),0) FROM raw_frames) + (SELECT COALESCE(SUM(length(evidence)),0) FROM invalid_batches);";
        _rawJournalBytes = Convert.ToInt64(cmd.ExecuteScalar());
    }

    private byte[] AppendRawJournal(int channel, EpbState state, SampleRecord[] records, int count, long? batchSequence = null)
    {
        var started = Stopwatch.GetTimestamp();
        try { return AppendRawJournalCore(channel, state, records, count, batchSequence); }
        finally
        {
            if (_writeTiming != null) _writeTiming.RawAppendMs += ElapsedWriteMs(started);
        }
    }

    private byte[] AppendRawJournalCore(int channel, EpbState state, SampleRecord[] records, int count, long? batchSequence)
    {
        if (count <= 0) return null;
        byte[] bytes;
        using (var stream = new MemoryStream(checked(count * SampleRecord.Size)))
        {
            using var writer = new BinaryWriter(stream);
            for (var i = 0; i < count; i++)
            {
                writer.Write(records[i].TimestampBinary);
                writer.Write(records[i].CycleNumber);
                writer.Write(records[i].SampleIndex);
                writer.Write(records[i].EpbCurrent);
                writer.Write(records[i].GroupPressure);
            }
            bytes = stream.ToArray();
        }
        using var sha = SHA256.Create();
        var digest = sha.ComputeHash(bytes);
        var committed = _committedDeviceFrames[channel];
        if (committed != null)
        {
            _committedDeviceFrames[channel] = null;
            if (committed.Cycle != records[0].CycleNumber || committed.First != records[0].SampleIndex ||
                committed.Count != count || committed.Position != state.TotalWritten % state.CapacityRecords ||
                committed.Generation != state.CurrentCycleGeneration || !committed.Digest.SequenceEqual(digest))
                throw new InvalidDataException("RawAttemptIdentityConflict: committed device frame differs");
            return digest;
        }
        var gateStarted = Stopwatch.GetTimestamp();
        lock (_rawJournalGate)
        {
            if (_writeTiming != null) _writeTiming.RawAppendGateWaitMs += ElapsedWriteMs(gateStarted);
            using var cmd = _rawJournal.CreateCommand();
            cmd.Transaction = _rawJournalTransaction;
            cmd.CommandText = "SELECT sha256 FROM raw_frames WHERE channel=@ch AND cycle=@cy AND first_sample=@first;";
            cmd.Parameters.AddWithValue("@ch", channel);
            cmd.Parameters.AddWithValue("@cy", records[0].CycleNumber);
            cmd.Parameters.AddWithValue("@first", records[0].SampleIndex);
            var existing = cmd.ExecuteScalar() as byte[];
            if (existing != null)
            {
                if (!existing.SequenceEqual(digest))
                    throw new InvalidDataException("RawAttemptIdentityConflict: original evidence retained");
                return digest;
            }
            if (_rawJournalBytes + bytes.Length > Math.Max(SampleRecord.Size, _policy.RawJournalMaxBytes))
                throw new IOException("RawJournalCapacityExceeded: unapplied evidence retained; safety pause required");
            cmd.CommandText = @"INSERT INTO raw_frames(channel,cycle,first_sample,position,sample_count,generation,sequence,data,sha256)
VALUES(@ch,@cy,@first,@pos,@count,@gen,@seq,@data,@sha);";
            cmd.Parameters.AddWithValue("@pos", state.TotalWritten % state.CapacityRecords);
            cmd.Parameters.AddWithValue("@count", count);
            cmd.Parameters.AddWithValue("@gen", state.CurrentCycleGeneration);
            cmd.Parameters.AddWithValue("@seq", batchSequence ?? state.CurrentCycleLastSequence);
            cmd.Parameters.AddWithValue("@data", bytes);
            cmd.Parameters.AddWithValue("@sha", digest);
            cmd.ExecuteNonQuery(); // FULL synchronous commit: durable before MMF write.
            _rawJournalBytes += bytes.Length;
        }
        return digest;
    }

    private List<RawFrame> ReadRawFrames(int channel)
    {
        var frames = new List<RawFrame>();
        lock (_rawJournalGate)
        {
            using var cmd = _rawJournal.CreateCommand();
            cmd.CommandText = "SELECT id,cycle,first_sample,position,sample_count,generation,sequence,data,sha256 FROM raw_frames WHERE channel=@ch ORDER BY id;";
            cmd.Parameters.AddWithValue("@ch", channel);
            using var reader = cmd.ExecuteReader();
            using var sha = SHA256.Create();
            while (reader.Read())
            {
                var frame = new RawFrame { Id = reader.GetInt64(0), Channel = channel, Cycle = reader.GetInt32(1),
                    First = reader.GetInt32(2), Position = reader.GetInt64(3), Count = reader.GetInt32(4),
                    Generation = reader.GetInt64(5), Sequence = reader.GetInt64(6), Data = (byte[])reader[7] };
                if (frame.Count <= 0 || frame.Data.Length != (long)frame.Count * SampleRecord.Size ||
                    !sha.ComputeHash(frame.Data).SequenceEqual((byte[])reader[8]))
                    throw new InvalidDataException("RawJournalFrameCorrupt: frame=" + frame.Id);
                frames.Add(frame);
            }
        }
        return frames;
    }

    private void ReplayRawJournal()
    {
        var mechanical = new List<Tuple<int, int, DateTime>>();
        using (var cmd = _rawJournal.CreateCommand())
        {
            cmd.CommandText = "SELECT channel,cycle,completed_at FROM mechanical_pending;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) mechanical.Add(Tuple.Create(reader.GetInt32(0), reader.GetInt32(1),
                DateTime.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind)));
        }
        foreach (var receipt in mechanical) TryRecordMechanicalCompletion(receipt.Item1, receipt.Item2, receipt.Item3);
        for (var channel = 1; channel <= EPB_COUNT; channel++)
        {
                var frames = ReadRawFrames(channel);
                CheckpointRawJournal(channel, force: true, flushSynchronously: true);
                _states[channel].TotalWritten = RestoreNextWritePosition(channel, _states[channel].CapacityRecords);
            }
        }

        private void ApplyRawFrames(int channel, List<RawFrame> frames)
        {
                foreach (var frame in frames)
                {
                    var records = new SampleRecord[frame.Count];
                    using var stream = new MemoryStream(frame.Data, writable: false);
                    using var reader = new BinaryReader(stream);
                    for (var i = 0; i < frame.Count; i++)
                        records[i] = new SampleRecord { TimestampBinary = reader.ReadInt64(), CycleNumber = reader.ReadInt32(),
                            SampleIndex = reader.ReadInt32(), EpbCurrent = reader.ReadDouble(), GroupPressure = reader.ReadDouble() };
                    var capacity = _states[channel].CapacityRecords;
                    if (frame.Position < 0 || frame.Position >= capacity || frame.Count > capacity)
                        throw new InvalidDataException("RawJournalDestinationInvalid");
                    var first = (int)Math.Min(frame.Count, capacity - frame.Position);
                    WriteRecordSegment(channel, frame.Position, records, 0, first);
                    if (first < frame.Count) WriteRecordSegment(channel, 0, records, first, frame.Count - first);
                }
        }

        private void CheckpointRawJournal(int channel, bool force, bool flushSynchronously = false)
        {
            var pending = _pendingRawCheckpoints[channel];
            if (pending != null)
            {
                if (!pending.Flush.IsCompleted && !flushSynchronously)
                {
                    if (!force) return;
                }
                else
                {
                // Observe failure before reclaiming any replay evidence.
                pending.Flush.GetAwaiter().GetResult();
                CommitRawCheckpoint(channel, pending.Frames);
                _pendingRawCheckpoints[channel] = null;
                _rawCheckpointUtc[channel] = DateTime.UtcNow;
                pending = null;
                }
            }
            if (!force && DateTime.UtcNow - _rawCheckpointUtc[channel] < TimeSpan.FromSeconds(1)) return;
            var checkpointStarted = Stopwatch.GetTimestamp();
            try
            {
            var frames = ReadRawFrames(channel);
            if (frames.Count == 0) return;
            // A failed device write can leave staged frames which have never reached MMF.
            // Closing/aborting must apply those exact destinations before reclaiming WAL.
            ApplyRawFrames(channel, frames);
            var state = _states[channel];
            foreach (var frame in frames.Where(f => state.CurrentCycle == f.Cycle))
            {
                var count = checked(frame.First + frame.Count);
                if (count > state.CurrentSampleIndex)
                {
                    state.TotalWritten += count - state.CurrentSampleIndex;
                    state.CurrentSampleIndex = count;
                }
                if (state.CurrentCycleGeneration == frame.Generation)
                    state.CurrentCycleLastSequence = Math.Max(state.CurrentCycleLastSequence, frame.Sequence);
            }
            var flushStarted = Stopwatch.GetTimestamp();
            try
            {
                if (!flushSynchronously)
                {
                    // Finalizing a cycle must apply every staged frame, but its
                    // FULL replay log already supplies the durable copy. Never
                    // join a slow ring flush while holding realtime state gates.
                    if (pending != null) return;
                    var work = new PendingRawCheckpoint { Frames = frames };
                    // Only flush already-applied bytes here. This worker never
                    // takes state/SQLite locks or publishes a durable prefix.
                    work.Flush = Task.Run(() => FlushRawFrameRanges(channel, frames));
                    _pendingRawCheckpoints[channel] = work;
                    return;
                }
                FlushRawFrameRanges(channel, frames);
            }
            finally { if (_writeTiming != null) _writeTiming.RingFlushMs += ElapsedWriteMs(flushStarted); }
            CommitRawCheckpoint(channel, frames);
            _rawCheckpointUtc[channel] = DateTime.UtcNow;
        }
        finally { if (_writeTiming != null) _writeTiming.CheckpointMs += ElapsedWriteMs(checkpointStarted); }
    }

    private void FlushRawFrameRanges(int channel, List<RawFrame> frames)
    {
        _rawCheckpointFlushTestHook?.Invoke(channel);
        var capacity = _states[channel].CapacityRecords;
        var ranges = new List<Tuple<long, long>>();
        foreach (var frame in frames)
        {
            var first = Math.Min(frame.Count, capacity - frame.Position);
            ranges.Add(Tuple.Create(frame.Position * SampleRecord.Size, first * SampleRecord.Size));
            if (first < frame.Count)
                ranges.Add(Tuple.Create(0L, (frame.Count - first) * SampleRecord.Size));
        }
        long start = -1, end = -1;
        foreach (var range in ranges.OrderBy(item => item.Item1))
        {
            if (start >= 0 && range.Item1 > end)
            {
                FlushRange(start, end);
                start = -1;
            }
            if (start < 0) start = range.Item1;
            end = Math.Max(end, range.Item1 + range.Item2);
        }
        if (start >= 0) FlushRange(start, end);
        _ringFiles[channel].Flush(flushToDisk: true);

        void FlushRange(long from, long to)
        {
            // Separate bounded views survive producer view remapping. Dispose
            // waits for this worker before closing the underlying mapping.
            const long window = 1024 * 1024;
            while (from < to)
            {
                var length = Math.Min(window, to - from);
                using (var view = _mmfs[channel].CreateViewAccessor(from, length, MemoryMappedFileAccess.ReadWrite))
                    view.Flush();
                from += length;
            }
        }
    }

    private void CommitRawCheckpoint(int channel, List<RawFrame> frames)
    {
            var indexStarted = Stopwatch.GetTimestamp();
            lock (_dbGate)
            {
                if (_writeTiming != null) _writeTiming.IndexGateWaitMs += ElapsedWriteMs(indexStarted);
                using var tx = _conn.BeginTransaction();
                // A checkpoint may contain ~100 frames for one cycle. Its index
                // needs the maximum committed prefix once, not one UPDATE per frame.
                foreach (var cycleFrames in frames.Where(frame => frame.Cycle != 0).GroupBy(frame => frame.Cycle))
                {
                    using var cmd = _conn.CreateCommand();
                    cmd.Transaction = tx;
                    // Never turn an interrupted/invalid cycle into a successful one.
                    cmd.CommandText = "UPDATE epb_cycles SET sample_count=MAX(COALESCE(sample_count,0),@n) WHERE epb_id=@ch AND cycle_number=@cy;";
                    cmd.Parameters.AddWithValue("@ch", channel);
                    cmd.Parameters.AddWithValue("@cy", cycleFrames.Key);
                    cmd.Parameters.AddWithValue("@n", cycleFrames.Max(frame => checked(frame.First + frame.Count)));
                    if (cmd.ExecuteNonQuery() != 1) throw new InvalidDataException("RawJournalCycleIdentityMissing");
                }
                var commitStarted = Stopwatch.GetTimestamp();
                try { tx.Commit(); }
                finally { if (_writeTiming != null) _writeTiming.IndexCommitMs += ElapsedWriteMs(commitStarted); }
            }
            var rawStarted = Stopwatch.GetTimestamp();
            lock (_rawJournalGate)
            {
                if (_writeTiming != null) _writeTiming.RawGateWaitMs += ElapsedWriteMs(rawStarted);
                var pruneStarted = Stopwatch.GetTimestamp();
                try
                {
                    using var cmd = _rawJournal.CreateCommand();
                    cmd.CommandText = "DELETE FROM raw_frames WHERE channel=@ch AND id<=@id;";
                    cmd.Parameters.AddWithValue("@ch", channel);
                    cmd.Parameters.AddWithValue("@id", frames[frames.Count - 1].Id);
                    cmd.ExecuteNonQuery();
                    _rawJournalBytes -= frames.Sum(f => (long)f.Data.Length);
                }
                finally { if (_writeTiming != null) _writeTiming.RawPruneMs += ElapsedWriteMs(pruneStarted); }
            }
    }

    public bool TryRecordMechanicalCompletion(int channel, int cycle, DateTime completedUtc)
    {
        if (cycle == 0) throw new ArgumentOutOfRangeException(nameof(cycle));
        lock (_dbGate)
        {
            using var identity = _conn.CreateCommand();
            identity.CommandText = "SELECT COUNT(*) FROM epb_cycles WHERE epb_id=@ch AND cycle_number=@cy;";
            identity.Parameters.AddWithValue("@ch", channel); identity.Parameters.AddWithValue("@cy", cycle);
            if (Convert.ToInt64(identity.ExecuteScalar()) != 1)
                throw new InvalidOperationException("MechanicalReceiptCycleIdentityMissing");
        }
        var completed = completedUtc.ToUniversalTime().ToString("O");
        lock (_rawJournalGate)
        {
            using var raw = _rawJournal.CreateCommand();
            raw.CommandText = "INSERT OR IGNORE INTO mechanical_pending(channel,cycle,completed_at) VALUES(@ch,@cy,@at);";
            raw.Parameters.AddWithValue("@ch", channel); raw.Parameters.AddWithValue("@cy", cycle); raw.Parameters.AddWithValue("@at", completed);
            raw.ExecuteNonQuery();
        }
        bool first;
        lock (_dbGate)
        {
            using var tx = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.Parameters.AddWithValue("@ch", channel); cmd.Parameters.AddWithValue("@cy", cycle); cmd.Parameters.AddWithValue("@at", completed);
            cmd.CommandText = "SELECT COALESCE(MAX(mechanical),0) FROM cycle_receipts WHERE epb_id=@ch AND cycle_number=@cy;";
            first = Convert.ToInt32(cmd.ExecuteScalar()) == 0;
            cmd.CommandText = @"INSERT OR IGNORE INTO cycle_receipts(epb_id,cycle_number,mechanical) VALUES(@ch,@cy,0);
UPDATE cycle_receipts SET mechanical=1,completed_at=COALESCE(completed_at,@at) WHERE epb_id=@ch AND cycle_number=@cy;
UPDATE epb_cycles SET mechanical_completed=1,mechanical_completed_at=COALESCE(mechanical_completed_at,@at) WHERE epb_id=@ch AND cycle_number=@cy;";
            cmd.ExecuteNonQuery();
            tx.Commit();
            if (first) PublishRecoveryCommit(channel, 1, 0, 0);
        }
        lock (_rawJournalGate)
        {
            using var raw = _rawJournal.CreateCommand();
            raw.CommandText = "DELETE FROM mechanical_pending WHERE channel=@ch AND cycle=@cy;";
            raw.Parameters.AddWithValue("@ch", channel); raw.Parameters.AddWithValue("@cy", cycle);
            raw.ExecuteNonQuery();
        }
        return first;
    }

    private void RecordCycleTerminalReceipt(int channel, int cycle)
    {
        lock (_dbGate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = _activeBatchTransaction;
            cmd.Parameters.AddWithValue("@ch", channel); cmd.Parameters.AddWithValue("@cy", cycle);
            cmd.CommandText = "SELECT status FROM cycle_receipts WHERE epb_id=@ch AND cycle_number=@cy;";
            var previousStatus = Convert.ToString(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            cmd.CommandText = @"INSERT OR IGNORE INTO cycle_receipts(epb_id,cycle_number) VALUES(@ch,@cy);
UPDATE cycle_receipts SET status=(SELECT status FROM epb_cycles WHERE epb_id=@ch AND cycle_number=@cy)
WHERE epb_id=@ch AND cycle_number=@cy;";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "SELECT status FROM cycle_receipts WHERE epb_id=@ch AND cycle_number=@cy;";
            var status = Convert.ToString(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            StageRecoveryTerminalCommit(channel, previousStatus, status);
        }
    }

    // Import the reported historical total once. Cycle numbers are identities, never counts.
    // Subsequent starts add durable receipts to that fixed baseline, including crash replay.
    public long ReconcileMechanicalBaseline(int channel, long reportedHistoricalTotal)
    {
        lock (_dbGate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"INSERT OR IGNORE INTO mechanical_baselines(epb_id,legacy_offset)
SELECT @ch,MAX(0,@reported-COALESCE(SUM(mechanical),0)) FROM cycle_receipts WHERE epb_id=@ch;";
            cmd.Parameters.AddWithValue("@ch", channel);
            cmd.Parameters.AddWithValue("@reported", Math.Max(0, reportedHistoricalTotal));
            cmd.ExecuteNonQuery();
            return Math.Max(reportedHistoricalTotal, GetMechanicalCycleCompletedCount(channel));
        }
    }

    public CycleAccountingSummary GetCycleAccounting(int channel)
    {
        lock (_dbGate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"SELECT COALESCE(SUM(mechanical),0),
COALESCE(SUM(CASE WHEN status='completed' AND cycle_number>0 THEN 1 ELSE 0 END),0),
COALESCE(SUM(CASE WHEN status IN ('learning_completed','qualification_completed') THEN 1 ELSE 0 END),0),
COALESCE(SUM(CASE WHEN status IS NOT NULL AND status NOT IN ('running','completed','learning_completed','qualification_completed') THEN 1 ELSE 0 END),0)
FROM cycle_receipts WHERE epb_id=@ch;";
            cmd.Parameters.AddWithValue("@ch", channel);
            using var reader = cmd.ExecuteReader();
            reader.Read();
            return new CycleAccountingSummary { Mechanical = reader.GetInt64(0), ValidFormal = reader.GetInt64(1),
                LearningOrQualification = reader.GetInt64(2), Invalid = reader.GetInt64(3) };
        }
    }
}
