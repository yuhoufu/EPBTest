using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Config
{
    public enum ProjectLogLevel
    {
        Info,
        Warning,
        Error
    }

    public sealed class ProjectLogOptions
    {
        public long MaxFileBytes { get; set; } = 50L * 1024L * 1024L;
        public int RetentionDays { get; set; } = 30;
        public int MemoryBufferCapacity { get; set; } = 10000;
        public Func<DateTime> LocalNowProvider { get; set; } = () => DateTime.Now;
        public Func<string, Stream> AppendStreamFactory { get; set; } =
            path => new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
    }

    public sealed class ProjectLogMemoryRecord
    {
        public DateTime Timestamp { get; internal set; }
        public ProjectLogLevel Level { get; internal set; }
        public string Category { get; internal set; }
        public string Message { get; internal set; }
    }

    /// <summary>
    /// 项目日志的单写入源。所有公开方法均吞掉文件系统异常，控制链路不会因日志失败而中断。
    /// </summary>
    public sealed class ProjectLogStore : IDisposable
    {
        private sealed class WriterState
        {
            public string Stem;
            public string ActivePath;
            public DateTime ContentDate;
            public Stream Stream;
            public StreamWriter Writer;
        }

        private readonly object _gate = new object();
        private readonly ProjectLogOptions _options;
        private readonly Queue<ProjectLogMemoryRecord> _pending = new Queue<ProjectLogMemoryRecord>();
        private readonly Queue<ProjectLogMemoryRecord> _memory = new Queue<ProjectLogMemoryRecord>();
        private readonly Dictionary<ProjectLogLevel, WriterState> _writers =
            new Dictionary<ProjectLogLevel, WriterState>();
        private string _projectRoot;
        private string _logDirectory;
        private DateTime _lastRetentionDate = DateTime.MinValue;
        private Exception _lastFailure;
        private static readonly Regex ArchiveNamePattern = new Regex(
            @"^(run|warning|error)\.\d{8}\.\d{3}\.log$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public ProjectLogStore(ProjectLogOptions options = null)
        {
            _options = options ?? new ProjectLogOptions();
            if (_options.MaxFileBytes <= 0) _options.MaxFileBytes = 50L * 1024L * 1024L;
            if (_options.RetentionDays < 0) _options.RetentionDays = 30;
            if (_options.MemoryBufferCapacity <= 0) _options.MemoryBufferCapacity = 10000;
            if (_options.LocalNowProvider == null) _options.LocalNowProvider = () => DateTime.Now;
            if (_options.AppendStreamFactory == null)
                _options.AppendStreamFactory =
                    path => new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        }

        public string ProjectRoot
        {
            get { lock (_gate) return _projectRoot; }
        }

        public Exception LastFailure
        {
            get { lock (_gate) return _lastFailure; }
        }

        public bool Configure(string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(projectRoot)) return false;
            lock (_gate)
            {
                try
                {
                    var fullRoot = Path.GetFullPath(projectRoot);
                    if (!string.Equals(_projectRoot, fullRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        CloseWriters();
                        _projectRoot = fullRoot;
                        _logDirectory = Path.Combine(fullRoot, "log");
                        _lastRetentionDate = DateTime.MinValue;
                    }

                    Directory.CreateDirectory(_logDirectory);
                    CleanupArchivesIfDue(_options.LocalNowProvider());
                    var drained = DrainPending();
                    if (drained) _lastFailure = null;
                    return drained;
                }
                catch (Exception ex)
                {
                    _lastFailure = ex;
                    return false;
                }
            }
        }

        public bool Write(
            ProjectLogLevel level,
            string message,
            string category = null,
            Exception exception = null)
        {
            lock (_gate)
            {
                var detail = exception == null ? message : $"{message} | {exception}";
                var record = new ProjectLogMemoryRecord
                {
                    Timestamp = _options.LocalNowProvider(),
                    Level = level,
                    Category = category ?? DefaultCategory(level),
                    Message = detail ?? string.Empty
                };
                Remember(record);

                if (string.IsNullOrWhiteSpace(_logDirectory))
                {
                    EnqueuePending(record);
                    return true;
                }

                try
                {
                    if (_pending.Count > 0)
                    {
                        EnqueuePending(record);
                        var drained = DrainPending();
                        if (drained) _lastFailure = null;
                        return drained;
                    }

                    WriteRecord(record);
                    _lastFailure = null;
                    return true;
                }
                catch (Exception ex)
                {
                    _lastFailure = ex;
                    EnqueuePending(record);
                    return false;
                }
            }
        }

        public bool Flush(bool durable)
        {
            lock (_gate)
            {
                try
                {
                    if (!DrainPending()) return false;
                    foreach (var state in _writers.Values)
                    {
                        state.Writer.Flush();
                        if (durable && state.Stream is FileStream fileStream)
                            fileStream.Flush(true);
                        else if (durable)
                            state.Stream.Flush();
                    }

                    _lastFailure = null;
                    return true;
                }
                catch (Exception ex)
                {
                    _lastFailure = ex;
                    return false;
                }
            }
        }

        public string GetActivePath(ProjectLogLevel level)
        {
            lock (_gate)
            {
                if (string.IsNullOrWhiteSpace(_logDirectory)) return null;
                return Path.Combine(_logDirectory, GetStem(level) + ".log");
            }
        }

        public IReadOnlyList<ProjectLogMemoryRecord> GetMemorySnapshot()
        {
            lock (_gate) return _memory.ToArray();
        }

        public void Dispose()
        {
            lock (_gate)
            {
                try { Flush(true); } catch { }
                CloseWriters();
            }
        }

        private bool DrainPending()
        {
            if (string.IsNullOrWhiteSpace(_logDirectory)) return true;
            try
            {
                while (_pending.Count > 0)
                {
                    WriteRecord(_pending.Peek());
                    _pending.Dequeue();
                }

                return true;
            }
            catch (Exception ex)
            {
                _lastFailure = ex;
                return false;
            }
        }

        private void WriteRecord(ProjectLogMemoryRecord record)
        {
            Directory.CreateDirectory(_logDirectory);
            CleanupArchivesIfDue(record.Timestamp);

            var line = FormatRecord(record);
            var byteCount = Encoding.UTF8.GetByteCount(line + Environment.NewLine);
            var state = EnsureWriter(record.Level, record.Timestamp, byteCount);
            state.Writer.WriteLine(line);
        }

        private WriterState EnsureWriter(ProjectLogLevel level, DateTime now, int incomingBytes)
        {
            if (!_writers.TryGetValue(level, out var state))
            {
                state = CreateWriterState(level, now);
                _writers[level] = state;
            }
            else if (state.Writer == null || state.Stream == null)
            {
                _writers.Remove(level);
                state = CreateWriterState(level, now);
                _writers[level] = state;
            }

            if (state.ContentDate.Date != now.Date)
            {
                Rotate(state, state.ContentDate);
                state.ContentDate = now.Date;
            }
            else if (state.Stream.Length > 0 &&
                     state.Stream.Length + incomingBytes > _options.MaxFileBytes)
            {
                Rotate(state, now);
                state.ContentDate = now.Date;
            }

            return state;
        }

        private WriterState CreateWriterState(ProjectLogLevel level, DateTime now)
        {
            var stem = GetStem(level);
            var path = Path.Combine(_logDirectory, stem + ".log");
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                var contentDate = File.GetLastWriteTime(path).Date;
                if (contentDate != now.Date || new FileInfo(path).Length >= _options.MaxFileBytes)
                    RotateClosedFile(path, stem, contentDate);
            }

            var stream = _options.AppendStreamFactory(path);
            var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            return new WriterState
            {
                Stem = stem,
                ActivePath = path,
                ContentDate = now.Date,
                Stream = stream,
                Writer = writer
            };
        }

        private void Rotate(WriterState state, DateTime archiveDate)
        {
            state.Writer?.Flush();
            state.Writer?.Dispose();
            state.Stream?.Dispose();
            state.Writer = null;
            state.Stream = null;
            RotateClosedFile(state.ActivePath, state.Stem, archiveDate.Date);
            state.Stream = _options.AppendStreamFactory(state.ActivePath);
            state.Writer = new StreamWriter(state.Stream, new UTF8Encoding(false)) { AutoFlush = true };
        }

        private void RotateClosedFile(string activePath, string stem, DateTime archiveDate)
        {
            if (!File.Exists(activePath) || new FileInfo(activePath).Length == 0) return;
            var sequence = 1;
            string archivePath;
            do
            {
                archivePath = Path.Combine(
                    _logDirectory,
                    $"{stem}.{archiveDate:yyyyMMdd}.{sequence:000}.log");
                sequence++;
            } while (File.Exists(archivePath));
            File.Move(activePath, archivePath);
        }

        private void CleanupArchivesIfDue(DateTime now)
        {
            if (_lastRetentionDate.Date == now.Date) return;
            _lastRetentionDate = now.Date;
            if (!Directory.Exists(_logDirectory)) return;

            var cutoff = now.AddDays(-_options.RetentionDays);
            foreach (var path in Directory.EnumerateFiles(_logDirectory, "*.log", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(path);
                if (!ArchiveNamePattern.IsMatch(name)) continue;
                if (File.GetLastWriteTime(path) < cutoff)
                {
                    try { File.Delete(path); }
                    catch { }
                }
            }
        }

        private void Remember(ProjectLogMemoryRecord record)
        {
            _memory.Enqueue(record);
            while (_memory.Count > _options.MemoryBufferCapacity)
                _memory.Dequeue();
        }

        private void EnqueuePending(ProjectLogMemoryRecord record)
        {
            if (_pending.Count >= _options.MemoryBufferCapacity)
                _pending.Dequeue();
            _pending.Enqueue(record);
        }

        private void CloseWriters()
        {
            foreach (var state in _writers.Values.ToArray())
            {
                try { state.Writer?.Flush(); } catch { }
                try { state.Writer?.Dispose(); } catch { }
                try { state.Stream?.Dispose(); } catch { }
            }
            _writers.Clear();
        }

        private static string FormatRecord(ProjectLogMemoryRecord record)
        {
            return
                $"{record.Timestamp:yyyy-MM-dd HH:mm:ss.fff}\t{LevelText(record.Level)}\t" +
                $"{Escape(record.Category)}\t{Escape(record.Message)}";
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");
        }

        private static string GetStem(ProjectLogLevel level)
        {
            switch (level)
            {
                case ProjectLogLevel.Warning: return "warning";
                case ProjectLogLevel.Error: return "error";
                default: return "run";
            }
        }

        private static string DefaultCategory(ProjectLogLevel level)
        {
            switch (level)
            {
                case ProjectLogLevel.Warning: return "警告";
                case ProjectLogLevel.Error: return "错误";
                default: return "信息";
            }
        }

        private static string LevelText(ProjectLogLevel level)
        {
            switch (level)
            {
                case ProjectLogLevel.Warning: return "WARN";
                case ProjectLogLevel.Error: return "ERROR";
                default: return "INFO";
            }
        }
    }

    public static class ProjectLogHub
    {
        private static readonly object Gate = new object();
        private static ProjectLogStore _store = new ProjectLogStore();

        public static bool Configure(string projectRoot)
        {
            return _store.Configure(projectRoot);
        }

        public static bool Write(
            ProjectLogLevel level,
            string message,
            string category = null,
            Exception exception = null)
        {
            return _store.Write(level, message, category, exception);
        }

        public static bool Flush(bool durable = false)
        {
            return _store.Flush(durable);
        }

        public static string GetActivePath(ProjectLogLevel level)
        {
            return _store.GetActivePath(level);
        }

        public static Exception LastFailure => _store.LastFailure;

        public static IReadOnlyList<ProjectLogMemoryRecord> GetMemorySnapshot()
        {
            return _store.GetMemorySnapshot();
        }

        public static void Shutdown()
        {
            lock (Gate)
            {
                var old = _store;
                _store = new ProjectLogStore();
                try { old.Dispose(); } catch { }
            }
        }
    }
}
