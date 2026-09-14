using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Linq;

namespace MTTFTest.FallbackGuard
{
    public sealed class RecoveryDatabaseSnapshot
    {
        public int SchemaVersion { get; set; } = 1;
        public string DatabasePath { get; set; }
        public long CreationUtcTicks { get; set; }
        public RecoveryDatabaseLane[] Channels { get; set; }

        public void Validate()
        {
            if (SchemaVersion != 1 || !Path.IsPathRooted(DatabasePath ?? "") || CreationUtcTicks <= 0 ||
                Channels == null || Channels.Length == 0 || Channels.Length > 12 ||
                Channels.Any(c => c == null) || Channels.Select(c => c.Channel).Distinct().Count() != Channels.Length)
                throw new InvalidDataException("RecoveryDatabaseSnapshotInvalid");
            foreach (var lane in Channels)
            {
                if (lane.Channel < 1 || lane.Channel > 12 || lane.LastAllocatedRowId < 0 ||
                    lane.MechanicalCompletedCount < 0 || lane.FormalRecords == null || lane.FormalRecords.Length > 3 ||
                    lane.FormalRecords.Any(r => r == null || r.RowId <= 0 || r.RowId > lane.LastAllocatedRowId ||
                        r.Cycle <= 0 || r.SampleCount <= 0) ||
                    lane.FormalRecords.Select(r => r.RowId).Distinct().Count() != lane.FormalRecords.Length ||
                    lane.FormalRecords.Select(r => r.Cycle).Distinct().Count() != lane.FormalRecords.Length)
                    throw new InvalidDataException("RecoveryDatabaseLaneInvalid");
            }
        }
    }

    public sealed class RecoveryDatabaseLane
    {
        public int Channel { get; set; }
        public long LastAllocatedRowId { get; set; }
        public long MechanicalCompletedCount { get; set; }
        public RecoveryFormalRecord[] FormalRecords { get; set; }
    }

    public sealed class RecoveryFormalRecord
    {
        public long RowId { get; set; }
        public long Cycle { get; set; }
        public long SampleCount { get; set; }
    }

    public static class RecoveryDatabaseEvidence
    {
        // Invoke in the bounded database child, never on the service/UI thread.
        // No schema migration or backfill: an unsupported schema is unreadable,
        // not stalled and not proof of recovery.
        public static RecoveryDatabaseSnapshot Read(string path, int[] channels)
        {
            path = Path.GetFullPath(path);
            if (!File.Exists(path)) throw new FileNotFoundException("DatabaseMissing", path);
            if (channels == null || channels.Length == 0 || channels.Length > 12 ||
                channels.Any(c => c < 1 || c > 12) || channels.Distinct().Count() != channels.Length)
                throw new InvalidDataException("DatabaseChannelsInvalid");
            var creation = File.GetCreationTimeUtc(path).Ticks;
            var builder = new SQLiteConnectionStringBuilder
            { DataSource = path, ReadOnly = true, FailIfMissing = true, Pooling = false, DefaultTimeout = 1 };
            var lanes = new List<RecoveryDatabaseLane>();
            using (var connection = new SQLiteConnection(builder.ConnectionString))
            {
                connection.Open();
                using (var setup = connection.CreateCommand())
                {
                    // A single read snapshot across all lanes and both queries.
                    // Connection disposal rolls back the read transaction.
                    setup.CommandText = "PRAGMA query_only=ON; PRAGMA busy_timeout=500; BEGIN DEFERRED;";
                    setup.ExecuteNonQuery();
                }
                foreach (var channel in channels)
                {
                    var lane = new RecoveryDatabaseLane { Channel = channel };
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandTimeout = 1;
                        command.CommandText = "SELECT COALESCE(MAX(id),0)," +
                            "COALESCE(SUM(CASE WHEN mechanical_completed=1 THEN 1 ELSE 0 END),0) " +
                            "FROM epb_cycles WHERE epb_id=@channel";
                        command.Parameters.AddWithValue("@channel", channel);
                        using (var reader = command.ExecuteReader())
                        {
                            if (!reader.Read()) throw new InvalidDataException("RecoveryDatabaseAggregateMissing");
                            lane.LastAllocatedRowId = reader.GetInt64(0);
                            lane.MechanicalCompletedCount = reader.GetInt64(1);
                        }
                        command.CommandText = "SELECT id,cycle_number,sample_count FROM epb_cycles " +
                            "WHERE epb_id=@channel AND cycle_number>0 AND status='completed' " +
                            "AND mechanical_completed=1 AND mechanical_completed_at IS NOT NULL " +
                            "AND end_time IS NOT NULL AND sample_count>0 ORDER BY id DESC LIMIT 3";
                        var records = new List<RecoveryFormalRecord>();
                        using (var reader = command.ExecuteReader())
                            while (reader.Read()) records.Add(new RecoveryFormalRecord
                            { RowId = reader.GetInt64(0), Cycle = reader.GetInt64(1), SampleCount = reader.GetInt64(2) });
                        lane.FormalRecords = records.ToArray();
                    }
                    lanes.Add(lane);
                }
            }
            if (File.GetCreationTimeUtc(path).Ticks != creation)
                throw new InvalidDataException("DatabaseReplacedDuringRead");
            var snapshot = new RecoveryDatabaseSnapshot
            { DatabasePath = path, CreationUtcTicks = creation, Channels = lanes.ToArray() };
            snapshot.Validate();
            return snapshot;
        }

        // Baseline must be durably captured AFTER retiring old controls and
        // BEFORE dispatching the replacement. A row allocated by the old
        // process cannot prove a new action even if its status changes later.
        // This is database evidence only; the caller must additionally verify
        // current run authority, live exact process and new supervision binding.
        public static int[] UnverifiedChannels(RecoveryDatabaseSnapshot baseline, RecoveryDatabaseSnapshot current)
        {
            if (baseline == null || current == null) throw new InvalidDataException("RecoveryDatabaseEvidenceMissing");
            baseline.Validate(); current.Validate();
            if (!string.Equals(Path.GetFullPath(baseline.DatabasePath), Path.GetFullPath(current.DatabasePath),
                    StringComparison.OrdinalIgnoreCase) || baseline.CreationUtcTicks != current.CreationUtcTicks ||
                !baseline.Channels.Select(c => c.Channel).OrderBy(c => c).SequenceEqual(
                    current.Channels.Select(c => c.Channel).OrderBy(c => c)))
                throw new InvalidDataException("RecoveryDatabaseEvidenceIdentityChanged");
            var pending = new List<int>();
            foreach (var before in baseline.Channels)
            {
                var after = current.Channels.Single(c => c.Channel == before.Channel);
                if (after.LastAllocatedRowId < before.LastAllocatedRowId ||
                    after.MechanicalCompletedCount < before.MechanicalCompletedCount)
                    throw new InvalidDataException("RecoveryDatabaseEvidenceRegressed");
                if (after.MechanicalCompletedCount - before.MechanicalCompletedCount < 3 ||
                    after.FormalRecords.Count(r => r.RowId > before.LastAllocatedRowId) < 3)
                    pending.Add(before.Channel);
            }
            return pending.ToArray();
        }
    }
}
