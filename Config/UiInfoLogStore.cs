using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Config
{
    public sealed class UiInfoLogOptions
    {
        public long MaxFileBytes { get; set; } = 10L * 1024L * 1024L;
        public int RetentionDays { get; set; } = 30;
        public int MaximumRecentLines { get; set; } = 2000;
        public Func<DateTime> LocalNowProvider { get; set; } = () => DateTime.Now;
        public Action<string, Exception> WarningSink { get; set; }
    }

    /// <summary>
    /// ui-info.log 的独立单写入源。所有文件异常都在组件内隔离，不能影响 UI 或控制流程。
    /// </summary>
    public sealed class UiInfoLogStore : IDisposable
    {
        private sealed class PendingAppend
        {
            public string Line;
            public TaskCompletionSource<bool> Completion;
        }

        private static readonly Regex ArchivePattern = new Regex(
            @"^ui-info\.\d{8}\.\d{3}\.log$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private readonly UiInfoLogOptions _options;
        private readonly SemaphoreSlim _fileGate = new SemaphoreSlim(1, 1);
        private readonly ConcurrentQueue<PendingAppend> _pending = new ConcurrentQueue<PendingAppend>();
        private int _drainRunning;
        private string _logDirectory;
        private string _activePath;
        private DateTime _contentDate;
        private DateTime _lastCleanupDate = DateTime.MinValue;
        private bool _disposed;

        public UiInfoLogStore(UiInfoLogOptions options = null)
        {
            _options = options ?? new UiInfoLogOptions();
            if (_options.MaxFileBytes <= 0) _options.MaxFileBytes = 10L * 1024L * 1024L;
            if (_options.RetentionDays < 0) _options.RetentionDays = 30;
            if (_options.MaximumRecentLines < 1) _options.MaximumRecentLines = 2000;
            if (_options.LocalNowProvider == null) _options.LocalNowProvider = () => DateTime.Now;
        }

        public string ActivePath => _activePath;
        public int MaximumRecentLines => _options.MaximumRecentLines;

        public bool Initialize(string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(projectRoot)) return false;
            _fileGate.Wait();
            try
            {
                _logDirectory = Path.Combine(Path.GetFullPath(projectRoot), "log");
                _activePath = Path.Combine(_logDirectory, "ui-info.log");
                Directory.CreateDirectory(_logDirectory);
                var now = _options.LocalNowProvider();
                if (File.Exists(_activePath) && new FileInfo(_activePath).Length > 0)
                {
                    _contentDate = File.GetLastWriteTime(_activePath).Date;
                    if (_contentDate != now.Date || new FileInfo(_activePath).Length >= _options.MaxFileBytes)
                        RotateClosedActive(_contentDate);
                }
                _contentDate = now.Date;
                if (!File.Exists(_activePath))
                    using (File.Create(_activePath)) { }
                CleanupArchivesIfDue(now);
                return true;
            }
            catch (Exception ex)
            {
                Warn("ui-info.log 初始化失败", ex);
                return false;
            }
            finally
            {
                _fileGate.Release();
            }
        }

        public IReadOnlyList<string> ReadRecentLines(int maxLines)
        {
            maxLines = Math.Max(1, Math.Min(maxLines, _options.MaximumRecentLines));
            _fileGate.Wait();
            try
            {
                var result = new List<string>(maxLines);
                if (File.Exists(_activePath))
                    result.AddRange(ReadTailLines(_activePath, maxLines));
                var remaining = maxLines - result.Count;
                if (remaining > 0 && Directory.Exists(_logDirectory))
                {
                    foreach (var archive in Directory.EnumerateFiles(
                                 _logDirectory,
                                 "ui-info.*.log",
                                 SearchOption.TopDirectoryOnly)
                             .Where(path => ArchivePattern.IsMatch(Path.GetFileName(path)))
                             .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                    {
                        var prior = ReadTailLines(archive, remaining);
                        if (prior.Count > 0) result.InsertRange(0, prior);
                        remaining = maxLines - result.Count;
                        if (remaining <= 0) break;
                    }
                }
                return result;
            }
            catch (Exception ex)
            {
                Warn("ui-info.log 最近行读取失败", ex);
                return Array.Empty<string>();
            }
            finally
            {
                _fileGate.Release();
            }
        }

        public Task AppendAsync(string line)
        {
            if (_disposed || string.IsNullOrEmpty(line)) return Task.CompletedTask;
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Enqueue(new PendingAppend { Line = line, Completion = completion });
            StartDrain();
            return completion.Task;
        }

        public async Task ReplaceActiveAsync(string text)
        {
            if (_disposed || string.IsNullOrWhiteSpace(_activePath)) return;
            var lines = SplitLines(text).TakeLastCompat(_options.MaximumRecentLines).ToArray();
            await _fileGate.WaitAsync().ConfigureAwait(false);
            try
            {
                File.WriteAllLines(_activePath, lines, new UTF8Encoding(false));
                _contentDate = _options.LocalNowProvider().Date;
            }
            catch (Exception ex)
            {
                Warn("ui-info.log 覆盖失败", ex);
            }
            finally
            {
                _fileGate.Release();
            }
        }

        public async Task FlushAsync()
        {
            while (!_pending.IsEmpty || Volatile.Read(ref _drainRunning) != 0)
                await Task.Delay(10).ConfigureAwait(false);
        }

        private void StartDrain()
        {
            if (Interlocked.CompareExchange(ref _drainRunning, 1, 0) != 0) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (_pending.TryDequeue(out var item))
                    {
                        var ok = await AppendCoreAsync(item.Line).ConfigureAwait(false);
                        item.Completion.TrySetResult(ok);
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _drainRunning, 0);
                    if (!_pending.IsEmpty) StartDrain();
                }
            });
        }

        private async Task<bool> AppendCoreAsync(string line)
        {
            await _fileGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (string.IsNullOrWhiteSpace(_activePath)) return false;
                Directory.CreateDirectory(_logDirectory);
                var now = _options.LocalNowProvider();
                var incomingBytes = Encoding.UTF8.GetByteCount(line + Environment.NewLine);
                var activeLength = File.Exists(_activePath) ? new FileInfo(_activePath).Length : 0L;
                if (activeLength > 0 &&
                    (_contentDate.Date != now.Date || activeLength + incomingBytes > _options.MaxFileBytes))
                    RotateClosedActive(_contentDate == DateTime.MinValue ? now.Date : _contentDate);
                using (var stream = new FileStream(
                           _activePath,
                           FileMode.Append,
                           FileAccess.Write,
                           FileShare.ReadWrite))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    await writer.WriteLineAsync(line).ConfigureAwait(false);
                _contentDate = now.Date;
                CleanupArchivesIfDue(now);
                return true;
            }
            catch (Exception ex)
            {
                Warn("ui-info.log 追加或轮转失败", ex);
                return false;
            }
            finally
            {
                _fileGate.Release();
            }
        }

        private void RotateClosedActive(DateTime archiveDate)
        {
            if (!File.Exists(_activePath) || new FileInfo(_activePath).Length == 0) return;
            var sequence = 1;
            string archive;
            do
            {
                archive = Path.Combine(
                    _logDirectory,
                    $"ui-info.{archiveDate:yyyyMMdd}.{sequence:000}.log");
                sequence++;
            } while (File.Exists(archive));
            File.Move(_activePath, archive);
        }

        private void CleanupArchivesIfDue(DateTime now)
        {
            if (_lastCleanupDate.Date == now.Date) return;
            _lastCleanupDate = now.Date;
            var cutoff = now.AddDays(-_options.RetentionDays);
            foreach (var path in Directory.EnumerateFiles(
                         _logDirectory,
                         "ui-info.*.log",
                         SearchOption.TopDirectoryOnly))
            {
                if (!ArchivePattern.IsMatch(Path.GetFileName(path))) continue;
                try
                {
                    if (File.GetLastWriteTime(path) < cutoff) File.Delete(path);
                }
                catch (Exception ex)
                {
                    Warn($"ui-info 归档清理失败：{path}", ex);
                }
            }
        }

        private static List<string> ReadTailLines(string path, int maxLines)
        {
            if (maxLines <= 0 || !File.Exists(path)) return new List<string>();
            var chunks = new List<byte[]>();
            var newlines = 0;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var position = stream.Length;
                while (position > 0 && newlines <= maxLines)
                {
                    var count = (int)Math.Min(64 * 1024, position);
                    position -= count;
                    stream.Position = position;
                    var chunk = new byte[count];
                    var read = stream.Read(chunk, 0, count);
                    if (read != count) Array.Resize(ref chunk, read);
                    chunks.Add(chunk);
                    for (var i = 0; i < chunk.Length; i++)
                        if (chunk[i] == (byte)'\n') newlines++;
                }
            }
            chunks.Reverse();
            using (var buffer = new MemoryStream())
            {
                foreach (var chunk in chunks) buffer.Write(chunk, 0, chunk.Length);
                var text = Encoding.UTF8.GetString(buffer.ToArray());
                return SplitLines(text).TakeLastCompat(maxLines).ToList();
            }
        }

        private static IEnumerable<string> SplitLines(string text)
        {
            return (text ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private void Warn(string message, Exception exception)
        {
            try { _options.WarningSink?.Invoke(message, exception); }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { FlushAsync().Wait(TimeSpan.FromSeconds(2)); } catch { }
            _fileGate.Dispose();
        }
    }

    internal static class UiInfoEnumerableExtensions
    {
        internal static IEnumerable<T> TakeLastCompat<T>(this IEnumerable<T> source, int count)
        {
            var queue = new Queue<T>();
            foreach (var item in source)
            {
                queue.Enqueue(item);
                while (queue.Count > count) queue.Dequeue();
            }
            return queue;
        }
    }
}
