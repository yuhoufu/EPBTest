using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

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
        /// <summary>兼容旧调用的通用保留期；新增日志类型未单独配置时使用。</summary>
        public int RetentionDays { get; set; } = 30;
        public int RunRetentionDays { get; set; } = 7;
        public int WarningRetentionDays { get; set; } = 30;
        public int ErrorRetentionDays { get; set; } = 30;
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
            @"^(?<stem>run|warning|error)\.\d{8}\.\d{3}\.log$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public ProjectLogStore(ProjectLogOptions options = null)
        {
            _options = options ?? new ProjectLogOptions();
            if (_options.MaxFileBytes <= 0) _options.MaxFileBytes = 50L * 1024L * 1024L;
            if (_options.RetentionDays < 0) _options.RetentionDays = 30;
            if (_options.RunRetentionDays < 0) _options.RunRetentionDays = 7;
            if (_options.WarningRetentionDays < 0) _options.WarningRetentionDays = 30;
            if (_options.ErrorRetentionDays < 0) _options.ErrorRetentionDays = 30;
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
                // 上一次轮转可能在关闭 writer 后因外部文件占用而失败。
                // 保留 ContentDate，重试时先按原内容日期归档，避免跨日记录混入活动日志。
                if (File.Exists(state.ActivePath) &&
                    new FileInfo(state.ActivePath).Length > 0 &&
                    state.ContentDate.Date != now.Date)
                {
                    RotateClosedFile(state.ActivePath, state.Stem, state.ContentDate.Date);
                }

                state.Stream = _options.AppendStreamFactory(state.ActivePath);
                state.Writer = new StreamWriter(state.Stream, new UTF8Encoding(false)) { AutoFlush = true };
                state.ContentDate = now.Date;
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

            foreach (var path in Directory.EnumerateFiles(_logDirectory, "*.log", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(path);
                var match = ArchiveNamePattern.Match(name);
                if (!match.Success) continue;
                var cutoff = now.AddDays(-GetRetentionDays(match.Groups["stem"].Value));
                if (File.GetLastWriteTime(path) < cutoff)
                {
                    try { File.Delete(path); }
                    catch { }
                }
            }
        }

        private int GetRetentionDays(string stem)
        {
            if (string.Equals(stem, "run", StringComparison.OrdinalIgnoreCase))
                return _options.RunRetentionDays;
            if (string.Equals(stem, "warning", StringComparison.OrdinalIgnoreCase))
                return _options.WarningRetentionDays;
            if (string.Equals(stem, "error", StringComparison.OrdinalIgnoreCase))
                return _options.ErrorRetentionDays;
            return _options.RetentionDays;
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
        private sealed class AsyncWorkItem
        {
            public ProjectLogLevel Level;
            public string Message;
            public string Category;
            public Exception Exception;
            public bool IsConfigure;
            public string ProjectRoot;
            public bool IsFlush;
            public bool Durable;
            public ManualResetEventSlim Completion;
            public bool Result;
        }

        private const int AsyncQueueCapacity = 32768;
        private static readonly object Gate = new object();
        private static readonly BlockingCollection<AsyncWorkItem> AsyncQueue =
            new BlockingCollection<AsyncWorkItem>(
                new ConcurrentQueue<AsyncWorkItem>(),
                AsyncQueueCapacity);
        private static readonly Thread AsyncWriter;
        private static ProjectLogStore _store = new ProjectLogStore();
        private static Exception _asyncFailure;
        private static long _droppedAsyncRecords;

        static ProjectLogHub()
        {
            AsyncWriter = new Thread(RunAsyncWriter)
            {
                IsBackground = true,
                Name = "EPB-ProjectLogWriter",
                Priority = ThreadPriority.BelowNormal
            };
            AsyncWriter.Start();
        }

        public static bool Configure(string projectRoot)
        {
            return _store.Configure(projectRoot);
        }

        public static bool EnqueueConfigure(string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(projectRoot)) return false;
            if (AsyncQueue.TryAdd(new AsyncWorkItem
                {
                    IsConfigure = true,
                    ProjectRoot = projectRoot
                }))
                return true;
            Volatile.Write(
                ref _asyncFailure,
                new IOException($"项目日志后台队列已满，目录配置未登记，容量={AsyncQueueCapacity}。"));
            return false;
        }

        public static bool Write(
            ProjectLogLevel level,
            string message,
            string category = null,
            Exception exception = null)
        {
            return _store.Write(level, message, category, exception);
        }

        /// <summary>
        /// 将日志登记到有界后台队列。调用方不执行文件 I/O，也不等待日志锁；队列满时
        /// 返回 false 并累计丢弃计数，绝不反压 DAQ、DO、AO 或安全状态机线程。
        /// </summary>
        public static bool Enqueue(
            ProjectLogLevel level,
            string message,
            string category = null,
            Exception exception = null)
        {
            var item = new AsyncWorkItem
            {
                Level = level,
                Message = message,
                Category = category,
                Exception = exception
            };
            if (AsyncQueue.TryAdd(item)) return true;

            Interlocked.Increment(ref _droppedAsyncRecords);
            Volatile.Write(
                ref _asyncFailure,
                new IOException($"项目日志后台队列已满，容量={AsyncQueueCapacity}。"));
            return false;
        }

        /// <summary>登记后台刷新请求，不等待磁盘。</summary>
        public static bool RequestFlush(bool durable = false)
        {
            if (AsyncQueue.TryAdd(new AsyncWorkItem
                {
                    IsFlush = true,
                    Durable = durable
                }))
                return true;

            Volatile.Write(
                ref _asyncFailure,
                new IOException($"项目日志后台队列已满，刷新请求未登记，容量={AsyncQueueCapacity}。"));
            return false;
        }

        public static bool Flush(bool durable = false)
        {
            using (var completion = new ManualResetEventSlim(false))
            {
                var item = new AsyncWorkItem
                {
                    IsFlush = true,
                    Durable = durable,
                    Completion = completion
                };
                if (!AsyncQueue.TryAdd(item, 5000))
                {
                    Volatile.Write(
                        ref _asyncFailure,
                        new IOException("项目日志刷新屏障无法进入后台队列。"));
                    return false;
                }
                if (!completion.Wait(15000))
                {
                    Volatile.Write(
                        ref _asyncFailure,
                        new TimeoutException("项目日志后台刷新超过15秒。"));
                    return false;
                }
                return item.Result;
            }
        }

        public static string GetActivePath(ProjectLogLevel level)
        {
            return _store.GetActivePath(level);
        }

        public static Exception LastFailure => Volatile.Read(ref _asyncFailure) ?? _store.LastFailure;

        public static long DroppedAsyncRecords => Interlocked.Read(ref _droppedAsyncRecords);

        public static IReadOnlyList<ProjectLogMemoryRecord> GetMemorySnapshot()
        {
            return _store.GetMemorySnapshot();
        }

        public static void Shutdown()
        {
            try { Flush(true); } catch { }
            lock (Gate)
            {
                var old = _store;
                _store = new ProjectLogStore();
                try { old.Dispose(); } catch { }
                Volatile.Write(ref _asyncFailure, null);
                Interlocked.Exchange(ref _droppedAsyncRecords, 0);
            }
        }

        private static void RunAsyncWriter()
        {
            foreach (var item in AsyncQueue.GetConsumingEnumerable())
            {
                try
                {
                    item.Result = item.IsConfigure
                        ? _store.Configure(item.ProjectRoot)
                        : item.IsFlush
                            ? _store.Flush(item.Durable)
                            : _store.Write(item.Level, item.Message, item.Category, item.Exception);
                    if (item.Result)
                        Volatile.Write(ref _asyncFailure, null);
                    else
                        Volatile.Write(ref _asyncFailure, _store.LastFailure);
                }
                catch (Exception ex)
                {
                    item.Result = false;
                    Volatile.Write(ref _asyncFailure, ex);
                }
                finally
                {
                    try { item.Completion?.Set(); } catch { }
                }
            }
        }
    }
}
