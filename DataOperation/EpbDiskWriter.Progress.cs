using System;
using System.Globalization;
using System.IO;

namespace DataOperation;

public sealed class EpbDurableProgress
{
    public int Channel { get; set; }
    public int FormalCompleted { get; set; }
    public long MechanicalCompleted { get; set; }
    public long RunTimeTicks { get; set; }
}

/// <summary>Successful formal cycles, not the last allocated (possibly aborted) cycle number.</summary>
public interface IFormalCycleProgressRecorder
{
    int GetCompletedFormalCycleCount(int epbId);
}

public sealed partial class EpbDiskWriter
{
    // The counters are updated in the SAME SQLite transaction as the cycle fact.
    // Retention can delete evidence rows without decrementing lifetime counters.
    // Existing indexes bootstrap from facts still present; callers must preserve
    // their historical configuration/checkpoint floor when importing old stores.
    private void InitializeDurableProgress()
    {
        using var transaction = _conn.BeginTransaction();
        using var command = _conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $@"
CREATE TABLE IF NOT EXISTS cycle_progress(
 epb_id INTEGER PRIMARY KEY CHECK(epb_id BETWEEN 1 AND 12),
 formal_completed INTEGER NOT NULL DEFAULT 0,
 mechanical_completed INTEGER NOT NULL DEFAULT 0,
 last_formal_number INTEGER NOT NULL DEFAULT 0,
 baseline_imported INTEGER NOT NULL DEFAULT 0);
WITH RECURSIVE channels(ch) AS (SELECT 1 UNION ALL SELECT ch+1 FROM channels WHERE ch<12)
INSERT OR IGNORE INTO cycle_progress(epb_id,formal_completed,mechanical_completed,last_formal_number)
SELECT ch,
 (SELECT COUNT(*) FROM {TABLE_CYCLES} WHERE epb_id=ch AND cycle_number>0 AND status='completed'),
 (SELECT COUNT(*) FROM {TABLE_CYCLES} WHERE epb_id=ch AND mechanical_completed=1),
 (SELECT COALESCE(MAX(cycle_number),0) FROM {TABLE_CYCLES} WHERE epb_id=ch AND cycle_number>0)
FROM channels;
CREATE TRIGGER IF NOT EXISTS cycle_progress_insert AFTER INSERT ON {TABLE_CYCLES} BEGIN
 UPDATE cycle_progress SET last_formal_number=MAX(last_formal_number,NEW.cycle_number),
 formal_completed=formal_completed+CASE WHEN NEW.cycle_number>0 AND NEW.status='completed' THEN 1 ELSE 0 END,
 mechanical_completed=mechanical_completed+CASE WHEN NEW.mechanical_completed=1 THEN 1 ELSE 0 END
 WHERE epb_id=NEW.epb_id;
END;
CREATE TRIGGER IF NOT EXISTS cycle_progress_update AFTER UPDATE ON {TABLE_CYCLES}
WHEN (NEW.cycle_number>0 AND NEW.status='completed' AND OLD.status<>'completed')
 OR (NEW.mechanical_completed=1 AND OLD.mechanical_completed=0) BEGIN
 UPDATE cycle_progress SET
 formal_completed=formal_completed+CASE WHEN NEW.cycle_number>0 AND NEW.status='completed' AND OLD.status<>'completed' THEN 1 ELSE 0 END,
 mechanical_completed=mechanical_completed+CASE WHEN NEW.mechanical_completed=1 AND OLD.mechanical_completed=0 THEN 1 ELSE 0 END
 WHERE epb_id=NEW.epb_id;
END;";
        command.ExecuteNonQuery();
        transaction.Commit();
        using var inspect = _conn.CreateCommand();
        inspect.CommandText = "PRAGMA table_info(cycle_progress)";
        var hasTime = false;
        using (var reader = inspect.ExecuteReader())
            while (reader.Read()) hasTime |= string.Equals(Convert.ToString(reader["name"]), "runtime_ticks", StringComparison.Ordinal);
        if (!hasTime)
        {
            inspect.CommandText = "ALTER TABLE cycle_progress ADD COLUMN runtime_ticks INTEGER NOT NULL DEFAULT 0";
            inspect.ExecuteNonQuery();
        }
    }

    /// <summary>Absolute monotonic time projection; replay never adds the same interval twice.</summary>
    public void AdvanceRunTime(long[] ticks)
    {
        if (ticks?.Length != 12 || Array.Exists(ticks, value => value < 0))
            throw new ArgumentException("RunTimeRequiresTwelveNonnegativeValues");
        lock (_dbGate)
        {
            using var transaction = _conn.BeginTransaction();
            using var command = _conn.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE cycle_progress SET runtime_ticks=@t WHERE epb_id=@e AND runtime_ticks<@t";
            for (var i = 0; i < 12; i++)
            {
                command.Parameters.Clear(); command.Parameters.AddWithValue("@e", i + 1); command.Parameters.AddWithValue("@t", ticks[i]);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    public EpbDurableProgress[] ReadDurableProgress()
    {
        lock (_dbGate)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = "SELECT epb_id,formal_completed,mechanical_completed,runtime_ticks FROM cycle_progress ORDER BY epb_id";
            using var reader = command.ExecuteReader();
            var result = new EpbDurableProgress[12];
            var index = 0;
            while (reader.Read())
            {
                if (index >= 12 || reader.GetInt32(0) != index + 1) throw new InvalidDataException("ProgressChannelIdentityInvalid");
                result[index++] = new EpbDurableProgress { Channel = reader.GetInt32(0), FormalCompleted = reader.GetInt32(1),
                    MechanicalCompleted = reader.GetInt64(2), RunTimeTicks = reader.GetInt64(3) };
            }
            if (index != 12) throw new InvalidDataException("ProgressRowsMissing");
            return result;
        }
    }

    /// <summary>
    /// Import a preserved project's counters into a NEW empty store, once only.
    /// Never synthesizes completed cycle rows or sample evidence for old counts.
    /// The EngineHost publishes this store only after this transaction is durable.
    /// </summary>
    public void ImportProgressBaseline(int[] formalCompleted, long[] mechanicalCompleted)
    {
        if (formalCompleted?.Length != 12 || mechanicalCompleted?.Length != 12)
            throw new ArgumentException("ProgressBaselineRequiresTwelveChannels");
        for (var i = 0; i < 12; i++)
            if (formalCompleted[i] < 0 || mechanicalCompleted[i] < formalCompleted[i])
                throw new ArgumentException("ProgressBaselineInvalid:" + (i + 1));
        lock (_dbGate)
        {
            using var transaction = _conn.BeginTransaction();
            using var command = _conn.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT (SELECT COUNT(*) FROM {TABLE_CYCLES})+(SELECT COUNT(*) FROM cycle_progress WHERE baseline_imported<>0 OR formal_completed<>0 OR mechanical_completed<>0 OR last_formal_number<>0)";
            if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                throw new InvalidOperationException("ProgressBaselineRequiresPristineStore");
            for (var i = 0; i < 12; i++)
            {
                command.CommandText = "UPDATE cycle_progress SET formal_completed=@f,mechanical_completed=@m,last_formal_number=@f,baseline_imported=1 WHERE epb_id=@e";
                command.Parameters.Clear();
                command.Parameters.AddWithValue("@f", formalCompleted[i]);
                command.Parameters.AddWithValue("@m", mechanicalCompleted[i]);
                command.Parameters.AddWithValue("@e", i + 1);
                if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("ProgressBaselineRowMissing");
            }
            transaction.Commit();
            // WAL checkpoint includes fsync; publication must not depend on an
            // unflushed connection pool or a sidecar outside the staging folder.
            RecoverAndValidateSqliteWal();
        }
    }

    public int GetCompletedFormalCycleCount(int epbId)
    {
        if (epbId < 1 || epbId > 12) throw new ArgumentOutOfRangeException(nameof(epbId));
        lock (_dbGate)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = "SELECT formal_completed FROM cycle_progress WHERE epb_id=@e";
            command.Parameters.AddWithValue("@e", epbId);
            var value = command.ExecuteScalar();
            if (value == null || value == DBNull.Value) throw new InvalidDataException("ProgressRowMissing");
            return checked(Convert.ToInt32(value, CultureInfo.InvariantCulture));
        }
    }
}
