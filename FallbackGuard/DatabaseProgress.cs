using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.FallbackGuard
{
    public sealed class DatabaseProgressSnapshot
    {
        public string DatabasePath { get; set; }
        public long CreationUtcTicks { get; set; }
        public string JournalMode { get; set; }
        public DatabaseLaneProgress[] Channels { get; set; } = Array.Empty<DatabaseLaneProgress>();
    }

    public sealed class DatabaseLaneProgress
    {
        public int Channel { get; set; }
        public long Cycle { get; set; }
        public string EndTime { get; set; }
        public long[] RecentCompletedCycles { get; set; } = Array.Empty<long>();
    }

    public sealed class DatabaseWatchIntent
    {
        public int SchemaVersion { get; set; } = 1;
        public string SessionId { get; set; }
        public string RunId { get; set; }
        public long RunEpoch { get; set; }
        public int ProcessId { get; set; }
        public long ProcessStartUtcTicks { get; set; }
        public string DatabasePath { get; set; }
        public long DatabaseCreationUtcTicks { get; set; }
        public int[] Channels { get; set; } = Array.Empty<int>();
        public long PeriodMs { get; set; }
        public bool ManualStopped { get; set; }
    }

    // No hardware or Controller references. The parent bounds the whole
    // process, including native SQLite waits that command cancellation cannot stop.
    public static class DatabaseProgressReader
    {
        public static DatabaseProgressSnapshot Read(string path, int[] channels)
        {
            path = Path.GetFullPath(path);
            if (!File.Exists(path)) throw new FileNotFoundException("DatabaseMissing", path);
            if (channels == null || channels.Length == 0 || channels.Length > 12 ||
                channels.Any(c => c < 1 || c > 12) || channels.Distinct().Count() != channels.Length)
                throw new InvalidDataException("DatabaseChannelsInvalid");
            var before = File.GetCreationTimeUtc(path).Ticks;
            var builder = new SQLiteConnectionStringBuilder
            { DataSource = path, ReadOnly = true, FailIfMissing = true, Pooling = false, DefaultTimeout = 1 };
            using (var connection = new SQLiteConnection(builder.ConnectionString))
            {
                connection.Open();
                using (var setup = connection.CreateCommand())
                { setup.CommandText = "PRAGMA query_only=ON; PRAGMA busy_timeout=500;"; setup.ExecuteNonQuery(); }
                string mode;
                using (var command = connection.CreateCommand())
                { command.CommandText = "PRAGMA journal_mode"; mode = Convert.ToString(command.ExecuteScalar()); }
                var result = new List<DatabaseLaneProgress>();
                using (var command = connection.CreateCommand())
                {
                    command.CommandTimeout = 1;
                    command.CommandText = "SELECT cycle_number,substr(end_time,1,64) FROM epb_cycles " +
                        "WHERE epb_id=@channel AND cycle_number>0 AND status='completed' " +
                        "ORDER BY cycle_number DESC LIMIT 3";
                    command.Parameters.Add("@channel", System.Data.DbType.Int32);
                    foreach (var channel in channels)
                    {
                        command.Parameters[0].Value = channel;
                        using (var reader = command.ExecuteReader())
                        {
                            var lane = new DatabaseLaneProgress { Channel = channel };
                            var cycles = new List<long>();
                            while (reader.Read())
                            {
                                if (cycles.Count == 0)
                                { lane.Cycle = reader.GetInt64(0); lane.EndTime = reader.IsDBNull(1) ? null : reader.GetString(1); }
                                cycles.Add(reader.GetInt64(0));
                            }
                            lane.RecentCompletedCycles = cycles.ToArray();
                            result.Add(lane);
                        }
                    }
                }
                if (File.GetCreationTimeUtc(path).Ticks != before)
                    throw new InvalidDataException("DatabaseReplacedDuringRead");
                return new DatabaseProgressSnapshot { DatabasePath = path,
                    CreationUtcTicks = before, JournalMode = mode, Channels = result.ToArray() };
            }
        }

        public static DatabaseProgressSnapshot ReadIsolated(string path, int[] channels)
            => ReadIsolated<DatabaseProgressSnapshot>("--read-database", path, channels);

        public static RecoveryDatabaseSnapshot ReadRecoveryIsolated(string path, int[] channels)
        {
            var snapshot = ReadIsolated<RecoveryDatabaseSnapshot>("--read-recovery-database", path, channels);
            if (snapshot == null) throw new InvalidDataException("RecoveryDatabaseEvidenceMissing");
            snapshot.Validate();
            return snapshot;
        }

        private static T ReadIsolated<T>(string mode, string path, int[] channels)
        {
            using (var current = Process.GetCurrentProcess())
            using (var worker = new Process())
            {
                worker.StartInfo = new ProcessStartInfo(current.MainModule.FileName,
                    mode + " \"" + path.Replace("\"", "") + "\" --channels " + string.Join(",", channels))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                    RedirectStandardError = true };
                worker.Start();
                var output = worker.StandardOutput.ReadToEndAsync();
                var error = worker.StandardError.ReadToEndAsync();
                var deadline = Stopwatch.StartNew();
                try
                {
                    while (!worker.WaitForExit(25))
                    {
                        try
                        {
                            worker.Refresh();
                            if (worker.HasExited) break;
                            if (deadline.ElapsedMilliseconds >= 2000 || worker.PrivateMemorySize64 > 256L * 1024 * 1024)
                                throw new TimeoutException("DatabaseReaderBudgetExceeded");
                        }
                        // The child can finish between WaitForExit and the
                        // OS process-info query. Its output/exit code remains
                        // authoritative; an exited child is not a failed read.
                        catch (InvalidOperationException) when (worker.HasExited) { break; }
                        catch (System.ComponentModel.Win32Exception) when (worker.HasExited) { break; }
                    }
                    if (!output.Wait(250) || !error.Wait(250)) throw new TimeoutException("DatabaseReaderOutputTimeout");
                    if (worker.ExitCode != 0) throw new IOException("DatabaseUnreadable:" + error.Result.Substring(0, Math.Min(512, error.Result.Length)));
                    if (output.Result.Length > 65536) throw new InvalidDataException("DatabaseResultTooLarge");
                    return new JavaScriptSerializer().Deserialize<T>(output.Result);
                }
                finally
                {
                    if (!worker.HasExited)
                    { worker.Kill(); if (!worker.WaitForExit(1000)) throw new IOException("DatabaseReaderCleanupUnconfirmed"); }
                }
            }
        }
    }

    public sealed class DatabaseStallMonitor
    {
        private readonly Dictionary<int, Tuple<long, long, int>> _lanes = new Dictionary<int, Tuple<long, long, int>>();
        private string _identity;
        private long _lastSuccessful;
        private string _verificationIdentity;
        private readonly Dictionary<int, long> _verificationBaseline = new Dictionary<int, long>();
        private long _verifiedSince = -1;
        public bool RecoveryVerified { get; private set; }
        public int[] StalledChannels { get; private set; } = Array.Empty<int>();

        public void BeginVerification(int pid, long startTicks)
        {
            var identity = pid > 0 && startTicks > 0 ? pid + ":" + startTicks : null;
            if (identity == _verificationIdentity) return;
            _verificationIdentity = identity;
            _verificationBaseline.Clear();
            _verifiedSince = -1;
            RecoveryVerified = false;
        }

        public void Unreadable()
        {
            // Retain the previous progress, but require three new successful
            // reads after a gap. A failed query is not no-progress evidence.
            foreach (var key in _lanes.Keys.ToArray())
            { var prior = _lanes[key]; _lanes[key] = Tuple.Create(prior.Item1, prior.Item2, 0); }
            StalledChannels = Array.Empty<int>();
            RecoveryVerified = false;
            _verifiedSince = -1;
        }

        public bool Observe(DatabaseWatchIntent intent, DatabaseProgressSnapshot snapshot, long nowMs)
        {
            if (intent == null || intent.ManualStopped)
            {
                _identity = null;
                _lanes.Clear();
                _verificationBaseline.Clear();
                Unreadable();
                return false;
            }
            if (snapshot == null) { Unreadable(); return false; }
            if (intent.Channels == null || intent.Channels.Length > 12 ||
                intent.Channels.Any(c => c < 1 || c > 12) || intent.Channels.Distinct().Count() != intent.Channels.Length ||
                intent.PeriodMs <= 0 || intent.PeriodMs > 86400000)
                throw new InvalidDataException("DatabaseIntentChannelsOrPeriodInvalid");
            if (snapshot.CreationUtcTicks != intent.DatabaseCreationUtcTicks ||
                !string.Equals(snapshot.DatabasePath, intent.DatabasePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("DatabaseIdentityChanged");
            var identity = intent.SessionId + ":" + intent.RunId + ":" + intent.RunEpoch + ":" +
                intent.PeriodMs + ":" + string.Join(",", intent.Channels.OrderBy(c => c));
            if (_identity != identity)
            {
                _identity = identity; _lanes.Clear(); _lastSuccessful = -1;
                _verificationBaseline.Clear(); _verifiedSince = -1; RecoveryVerified = false;
            }
            if (nowMs < _lastSuccessful) throw new InvalidDataException("MonotonicClockRegressed");
            if (nowMs == _lastSuccessful) return StalledChannels.Length > 0;
            _lastSuccessful = nowMs;
            var stalled = new List<int>();
            foreach (var channel in intent.Channels)
            {
                var lane = snapshot.Channels.SingleOrDefault(c => c.Channel == channel)
                    ?? throw new InvalidDataException("DatabaseChannelMissing");
                if (!_lanes.TryGetValue(channel, out var prior))
                    _lanes[channel] = Tuple.Create(lane.Cycle, nowMs, 0);
                else
                {
                    if (lane.Cycle < prior.Item1) throw new InvalidDataException("DatabaseProgressRegressed");
                    if (lane.Cycle > prior.Item1) _lanes[channel] = Tuple.Create(lane.Cycle, nowMs, 0);
                    else
                    {
                        var confirmations = nowMs - prior.Item2 >= Math.Max(90000L, intent.PeriodMs * 6)
                            ? prior.Item3 + 1 : 0;
                        _lanes[channel] = Tuple.Create(lane.Cycle, prior.Item2, confirmations);
                        if (confirmations >= 3) stalled.Add(channel);
                    }
                }
            }
            StalledChannels = stalled.ToArray();
            var advanced = _verificationIdentity != null && intent.Channels.Length > 0;
            foreach (var channel in intent.Channels)
            {
                var cycle = snapshot.Channels.Single(c => c.Channel == channel).Cycle;
                if (!_verificationBaseline.TryGetValue(channel, out var baseline))
                { _verificationBaseline[channel] = cycle; baseline = cycle; }
                var commits = snapshot.Channels.Single(c => c.Channel == channel).RecentCompletedCycles;
                if (commits == null || commits.Distinct().Count(c => c > baseline) < 3) advanced = false;
            }
            if (!advanced || stalled.Count != 0) _verifiedSince = -1;
            else if (_verifiedSince < 0) _verifiedSince = nowMs;
            RecoveryVerified = _verifiedSince >= 0 && nowMs - _verifiedSince >= Math.Max(1000L, intent.PeriodMs * 2);
            return stalled.Count > 0;
        }
    }
}
