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
        /// <summary>仅用于确定性故障/竞态测试；生产配置保持 null。</summary>
        public Action<bool> BeforeFlush { get; set; }
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
            public long LogicalLength;
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
                    _options.BeforeFlush?.Invoke(durable);
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
            state.LogicalLength += byteCount;
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
                state.Writer = new StreamWriter(state.Stream, new UTF8Encoding(false)) { AutoFlush = false };
                state.LogicalLength = state.Stream.Length;
                state.ContentDate = now.Date;
            }

            if (state.ContentDate.Date != now.Date)
            {
                Rotate(state, state.ContentDate);
                state.ContentDate = now.Date;
            }
            else if (state.LogicalLength > 0 &&
                     state.LogicalLength + incomingBytes > _options.MaxFileBytes)
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
            var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false };
            return new WriterState
            {
                Stem = stem,
                ActivePath = path,
                ContentDate = now.Date,
                Stream = stream,
                Writer = writer,
                LogicalLength = stream.Length
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
            state.Writer = new StreamWriter(state.Stream, new UTF8Encoding(false)) { AutoFlush = false };
            state.LogicalLength = state.Stream.Length;
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
            public bool IsShutdown;
            public ManualResetEventSlim Completion;
            public bool Result;
        }

        private const int AsyncQueueCapacity = 32768;
        private const int BackgroundFlushIntervalMs = 1000;
        private static readonly object Gate = new object();
        private static readonly object ShutdownGate = new object();
        private static readonly BlockingCollection<AsyncWorkItem> AsyncQueue =
            new BlockingCollection<AsyncWorkItem>(
                new ConcurrentQueue<AsyncWorkItem>(),
                AsyncQueueCapacity);
        private static readonly Thread AsyncWriter;
        private static readonly AsyncWorkItem DurableFlushSignal = new AsyncWorkItem
        {
            IsFlush = true,
            Durable = true
        };
        private static ProjectLogStore _store = new ProjectLogStore();
        private static Exception _asyncFailure;
        private static long _droppedAsyncRecords;
        private static long _softFlushExecutionCount;
        private static long _durableFlushExecutionCount;
        private static long _coalescedFlushRequestCount;
        private static long _writeVersion;
        private static long _flushedWriteVersion;
        private static long _softFlushRequestVersion;
        private static long _softFlushProcessedVersion;
        private static int _durableFlushSignalQueued;
        private static long _durableFlushRequestVersion;
        private static long _durableFlushSignalVersion;
        private static long _durableFlushProcessedVersion;
        private static int _activeAdmissions;
        private static bool _admissionOpen = true;
        private static bool _shutdownClosing;
        private static bool _shutdownBarrierFailed;

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
            if (string.IsNullOrWhiteSpace(projectRoot)) return false;
            using (var completion = new ManualResetEventSlim(false))
            {
                var item = new AsyncWorkItem
                {
                    IsConfigure = true,
                    ProjectRoot = projectRoot,
                    Completion = completion
                };
                if (!TrySubmit(item, 5000, true, true, false)) return false;
                return WaitForCompletion(item, completion, 15000, "项目日志目录配置超过15秒。");
            }
        }

        public static bool EnqueueConfigure(string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(projectRoot)) return false;
            if (TrySubmit(new AsyncWorkItem
                {
                    IsConfigure = true,
                    ProjectRoot = projectRoot
                }, 0, true, true, false))
                return true;
            return false;
        }

        public static bool Write(
            ProjectLogLevel level,
            string message,
            string category = null,
            Exception exception = null)
        {
            using (var completion = new ManualResetEventSlim(false))
            {
                var item = new AsyncWorkItem
                {
                    Level = level,
                    Message = message,
                    Category = category,
                    Exception = exception,
                    Completion = completion
                };
                if (!TrySubmit(item, 5000, false, false, true)) return false;
                return WaitForCompletion(item, completion, 15000, "项目日志同步写入超过15秒。");
            }
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
            return TrySubmit(item, 0, false, false, true);
        }

        /// <summary>
        /// 登记后台刷新请求，不等待磁盘。普通刷新仅设置一个合并标记，由后台线程
        /// 最迟约 1 秒执行 Flush(false)；耐久刷新也只保留一个队列信号，告警风暴
        /// 不会为每条日志创建独立刷新项。
        /// </summary>
        public static bool RequestFlush(bool durable = false)
        {
            if (!TryEnterAdmission(false, false))
            {
                SetAdmissionClosedFailure("项目日志刷新请求");
                return false;
            }
            try
            {
                if (!durable)
                {
                    var pending = Interlocked.Read(ref _softFlushRequestVersion) >
                                  Interlocked.Read(ref _softFlushProcessedVersion);
                    Interlocked.Increment(ref _softFlushRequestVersion);
                    if (pending) Interlocked.Increment(ref _coalescedFlushRequestCount);
                    return true;
                }

                var durablePending = Interlocked.Read(ref _durableFlushRequestVersion) >
                                     Interlocked.Read(ref _durableFlushProcessedVersion);
                Interlocked.Increment(ref _durableFlushRequestVersion);
                if (durablePending || Volatile.Read(ref _durableFlushSignalQueued) != 0)
                    Interlocked.Increment(ref _coalescedFlushRequestCount);
                EnsureDurableFlushSignalQueued();
                return true;
            }
            finally
            {
                ExitAdmission(false);
            }
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
                if (!TrySubmit(item, 5000, false, false, false)) return false;
                return WaitForCompletion(item, completion, 15000, "项目日志后台刷新超过15秒。");
            }
        }

        public static string GetActivePath(ProjectLogLevel level)
        {
            return Volatile.Read(ref _store).GetActivePath(level);
        }

        public static Exception LastFailure =>
            Volatile.Read(ref _asyncFailure) ?? Volatile.Read(ref _store).LastFailure;

        public static long DroppedAsyncRecords => Interlocked.Read(ref _droppedAsyncRecords);

        public static int PendingAsyncRecords => AsyncQueue.Count;

        public static int MaximumPendingAsyncRecords => AsyncQueueCapacity;

        public static int PendingFlushRequestKinds =>
            (Interlocked.Read(ref _softFlushRequestVersion) <=
             Interlocked.Read(ref _softFlushProcessedVersion) ? 0 : 1) +
            (Interlocked.Read(ref _durableFlushRequestVersion) <=
             Interlocked.Read(ref _durableFlushProcessedVersion) ? 0 : 1);

        public static int PendingDurableFlushSignals =>
            Volatile.Read(ref _durableFlushSignalQueued) == 0 ? 0 : 1;

        public static long SoftFlushExecutionCount => Interlocked.Read(ref _softFlushExecutionCount);

        public static long DurableFlushExecutionCount => Interlocked.Read(ref _durableFlushExecutionCount);

        public static long CoalescedFlushRequestCount => Interlocked.Read(ref _coalescedFlushRequestCount);

        public static IReadOnlyList<ProjectLogMemoryRecord> GetMemorySnapshot()
        {
            return Volatile.Read(ref _store).GetMemorySnapshot();
        }

        public static void Shutdown()
        {
            lock (ShutdownGate)
            {
                try
                {
                    lock (Gate)
                    {
                        _shutdownClosing = true;
                        _shutdownBarrierFailed = true;
                        _admissionOpen = false;
                        while (_activeAdmissions > 0)
                            Monitor.Wait(Gate);
                    }

                    using (var completion = new ManualResetEventSlim(false))
                    {
                        var item = new AsyncWorkItem
                        {
                            IsShutdown = true,
                            Completion = completion
                        };
                        if (!AsyncQueue.TryAdd(item, 15000))
                        {
                            Volatile.Write(
                                ref _asyncFailure,
                                new IOException("项目日志关闭屏障无法进入后台队列；旧 Store 保持有效且准入保持关闭。"));
                            return;
                        }
                        WaitForCompletion(item, completion, 30000, "项目日志关闭屏障超过30秒。");
                    }
                }
                catch (Exception ex)
                {
                    Volatile.Write(ref _asyncFailure, ex);
                }
                finally
                {
                    lock (Gate)
                    {
                        _shutdownClosing = false;
                        Monitor.PulseAll(Gate);
                    }
                }
            }
        }

        private static void RunAsyncWriter()
        {
            var nextBackgroundFlushUtc = DateTime.UtcNow.AddMilliseconds(BackgroundFlushIntervalMs);
            while (true)
            {
                var remainingMs = (int)Math.Max(
                    1,
                    Math.Min(
                        BackgroundFlushIntervalMs,
                        (nextBackgroundFlushUtc - DateTime.UtcNow).TotalMilliseconds));
                if (AsyncQueue.TryTake(out var item, remainingMs))
                {
                    ProcessAsyncWorkItem(item);
                    EnsureDurableFlushSignalQueued();
                }

                if (DateTime.UtcNow >= nextBackgroundFlushUtc)
                {
                    ExecuteBackgroundSoftFlushIfRequired();
                    nextBackgroundFlushUtc = DateTime.UtcNow.AddMilliseconds(
                        BackgroundFlushIntervalMs);
                }
            }
        }

        private static void ProcessAsyncWorkItem(AsyncWorkItem item)
        {
            var isSharedDurableSignal = ReferenceEquals(item, DurableFlushSignal);
            try
            {
                if (item.IsShutdown)
                {
                    ProcessShutdownWorkItem(item);
                }
                else if (isSharedDurableSignal)
                {
                    var signalVersion = Interlocked.Read(ref _durableFlushSignalVersion);
                    item.Result = ExecuteFlush(Volatile.Read(ref _store), true, signalVersion);
                }
                else
                {
                    var store = Volatile.Read(ref _store);
                    if (item.IsConfigure)
                    {
                        item.Result = store.Configure(item.ProjectRoot);
                        // Configure 可能已补写一部分早期 pending 后才失败；同样登记新版本，
                        // 让后台 Flush 持续重试，不能只在完全成功时才标记 dirty。
                        Interlocked.Increment(ref _writeVersion);
                    }
                    else if (item.IsFlush)
                    {
                        item.Result = ExecuteFlush(store, item.Durable, null);
                    }
                    else
                    {
                        item.Result = store.Write(
                            item.Level,
                            item.Message,
                            item.Category,
                            item.Exception);
                        // ProjectLogStore.Write(false) 已把原记录保留到待重试队列；无论本次
                        // 文件写入是否成功，该版本都必须保持为待刷新，不能无限漏刷。
                        Interlocked.Increment(ref _writeVersion);
                    }
                }

                if (item.Result)
                    Volatile.Write(ref _asyncFailure, null);
                else
                    Volatile.Write(ref _asyncFailure, Volatile.Read(ref _store).LastFailure);
            }
            catch (Exception ex)
            {
                item.Result = false;
                Volatile.Write(ref _asyncFailure, ex);
            }
            finally
            {
                if (isSharedDurableSignal)
                {
                    Volatile.Write(ref _durableFlushSignalQueued, 0);
                    EnsureDurableFlushSignalQueued();
                }
                try { item.Completion?.Set(); } catch { }
            }
        }

        private static void ExecuteBackgroundSoftFlushIfRequired()
        {
            if (Interlocked.Read(ref _writeVersion) <=
                    Interlocked.Read(ref _flushedWriteVersion) &&
                Interlocked.Read(ref _softFlushRequestVersion) <=
                    Interlocked.Read(ref _softFlushProcessedVersion))
                return;
            try
            {
                var flushed = ExecuteFlush(Volatile.Read(ref _store), false, null);
                if (flushed)
                    Volatile.Write(ref _asyncFailure, null);
                else
                    Volatile.Write(ref _asyncFailure, Volatile.Read(ref _store).LastFailure);
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _asyncFailure, ex);
            }
        }

        private static void EnsureDurableFlushSignalQueued()
        {
            if (Interlocked.Read(ref _durableFlushRequestVersion) <=
                Interlocked.Read(ref _durableFlushProcessedVersion))
                return;
            if (Interlocked.CompareExchange(ref _durableFlushSignalQueued, 1, 0) != 0) return;
            Interlocked.Exchange(
                ref _durableFlushSignalVersion,
                Interlocked.Read(ref _durableFlushRequestVersion));
            if (AsyncQueue.TryAdd(DurableFlushSignal)) return;
            Volatile.Write(ref _durableFlushSignalQueued, 0);
        }

        private static bool ExecuteFlush(
            ProjectLogStore store,
            bool durable,
            long? durableRequestTarget)
        {
            // 只确认 Flush 开始前已观察到的版本。即使未来重新引入其他写入源，Flush
            // 返回与新写入并发时也不会用“清 bool”覆盖新产生的 dirty 状态。
            var writeTarget = Interlocked.Read(ref _writeVersion);
            var softRequestTarget = Interlocked.Read(ref _softFlushRequestVersion);
            var durableTarget = durableRequestTarget ??
                                Interlocked.Read(ref _durableFlushRequestVersion);
            var flushed = store.Flush(durable);
            if (durable)
                Interlocked.Increment(ref _durableFlushExecutionCount);
            else
                Interlocked.Increment(ref _softFlushExecutionCount);
            if (!flushed) return false;

            AdvanceWatermark(ref _flushedWriteVersion, writeTarget);
            AdvanceWatermark(ref _softFlushProcessedVersion, softRequestTarget);
            if (durable)
                AdvanceWatermark(ref _durableFlushProcessedVersion, durableTarget);
            return true;
        }

        private static void ProcessShutdownWorkItem(AsyncWorkItem item)
        {
            var old = Volatile.Read(ref _store);
            if (string.IsNullOrWhiteSpace(old.ProjectRoot))
            {
                // 未配置目录时可能仍有启动早期记录保存在 Store 的 pending 队列。
                // 保留同一个 Store，待下一次 Configure 补写，不能因 Shutdown 交换而丢失。
                item.Result = true;
                SetShutdownBarrierResult(true);
                return;
            }

            var dropped = Interlocked.Read(ref _droppedAsyncRecords);
            old.Write(
                ProjectLogLevel.Info,
                $"ProjectLogShutdown AsyncLogDroppedTotal={dropped} " +
                $"QueueDepth={AsyncQueue.Count} CoalescedFlushRequests=" +
                Interlocked.Read(ref _coalescedFlushRequestCount),
                "SESSION");
            Interlocked.Increment(ref _writeVersion);
            item.Result = ExecuteFlush(old, true, null);
            if (!item.Result)
            {
                // Flush 失败时旧 Store 仍持有所有已接受记录；保持关闭准入并保留 Store，
                // 禁止用一次失败的关闭屏障换取静默数据丢失。
                SetShutdownBarrierResult(false);
                return;
            }

            try { old.Dispose(); } catch { }
            Volatile.Write(ref _store, new ProjectLogStore());
            SetShutdownBarrierResult(true);
        }

        private static void AdvanceWatermark(ref long watermark, long target)
        {
            while (true)
            {
                var current = Interlocked.Read(ref watermark);
                if (current >= target) return;
                if (Interlocked.CompareExchange(ref watermark, target, current) == current) return;
            }
        }

        private static bool TrySubmit(
            AsyncWorkItem item,
            int timeoutMs,
            bool allowClosedForConfigure,
            bool openAdmissionOnSuccess,
            bool countRejectedRecord)
        {
            if (!TryEnterAdmission(allowClosedForConfigure, allowClosedForConfigure && timeoutMs > 0))
            {
                if (countRejectedRecord) Interlocked.Increment(ref _droppedAsyncRecords);
                SetAdmissionClosedFailure(item.IsConfigure ? "项目日志目录配置" : "项目日志写入");
                return false;
            }

            var added = false;
            try
            {
                added = timeoutMs <= 0
                    ? AsyncQueue.TryAdd(item)
                    : AsyncQueue.TryAdd(item, timeoutMs);
                if (!added)
                {
                    if (countRejectedRecord) Interlocked.Increment(ref _droppedAsyncRecords);
                    Volatile.Write(
                        ref _asyncFailure,
                        new IOException(
                            item.IsConfigure
                                ? $"项目日志后台队列已满，目录配置未登记，容量={AsyncQueueCapacity}。"
                                : $"项目日志后台队列已满，容量={AsyncQueueCapacity}。"));
                }
                return added;
            }
            finally
            {
                ExitAdmission(added && openAdmissionOnSuccess);
            }
        }

        private static bool TryEnterAdmission(bool allowClosedForConfigure, bool waitForShutdown)
        {
            lock (Gate)
            {
                if (waitForShutdown)
                {
                    var deadline = DateTime.UtcNow.AddSeconds(15);
                    while (_shutdownClosing)
                    {
                        var remaining = deadline - DateTime.UtcNow;
                        if (remaining <= TimeSpan.Zero) return false;
                        Monitor.Wait(Gate, remaining);
                    }
                }

                if (_shutdownClosing ||
                    (allowClosedForConfigure && _shutdownBarrierFailed) ||
                    (!_admissionOpen && !allowClosedForConfigure))
                    return false;
                _activeAdmissions++;
                return true;
            }
        }

        private static void ExitAdmission(bool openAdmission)
        {
            lock (Gate)
            {
                if (openAdmission && !_shutdownClosing) _admissionOpen = true;
                _activeAdmissions--;
                if (_activeAdmissions == 0) Monitor.PulseAll(Gate);
            }
        }

        private static bool WaitForCompletion(
            AsyncWorkItem item,
            ManualResetEventSlim completion,
            int timeoutMs,
            string timeoutMessage)
        {
            if (completion.Wait(timeoutMs)) return item.Result;
            Volatile.Write(ref _asyncFailure, new TimeoutException(timeoutMessage));
            return false;
        }

        private static void SetAdmissionClosedFailure(string operation)
        {
            Volatile.Write(
                ref _asyncFailure,
                new IOException(operation + "在日志关闭屏障期间被拒绝；记录未被后台队列接受。"));
        }

        private static void SetShutdownBarrierResult(bool succeeded)
        {
            lock (Gate) _shutdownBarrierFailed = !succeeded;
        }
    }
}
