using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace MTEmbTest.Playback
{
    internal struct HistoryPoint
    {
        internal long Index;
        internal int BrakeNo;
        internal DateTime Time;
        internal double Force, CanCurrent, Current, Torque;
        internal double Value(int axis) => axis == 0 ? Force : axis == 1 ? CanCurrent : axis == 2 ? Current : Torque;
    }

    internal sealed class HistoryReadOptions
    {
        internal bool Raw;
        internal int ChannelCount, ChannelIndex;
        internal int MedianLength = 1;
        internal double Scale = 1, Offset, Zero;
        internal int RecordSize => Raw ? 12 + 8 * ChannelCount : 77;
        internal HistoryReadOptions Clone() => (HistoryReadOptions)MemberwiseClone();
        internal void Validate()
        {
            if (Raw && (ChannelCount < 1 || ChannelCount > 64 || ChannelIndex < 0 || ChannelIndex >= ChannelCount ||
                MedianLength < 1 || MedianLength > 65536 || !Finite(Scale) || Scale == 0 || !Finite(Offset) || !Finite(Zero)))
                throw new InvalidDataException("历史采集布局或标定参数无效。");
        }
        internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }

    internal sealed class HistoryReadResult
    {
        internal string SourcePath, SourceSha256, SourceFileIdentity;
        internal long SourceLength, SourceFrames, FilteredFrames;
        internal DateTime FirstTime;
        internal HistoryReadOptions Options;
        internal HistoryPoint[] RawPreview, Points;
        internal bool DisplayReduced => Points.LongLength < FilteredFrames;
    }

    internal sealed class HistoryWindowResult
    {
        internal HistoryPoint[] Points;
        internal long MatchingFrames;
        internal bool TooMany;
    }

    // No hardware, global configuration or formal-count writer. File decoding and
    // CSV export run off the UI thread; display storage never scales with file size.
    internal static class HistoryFileReader
    {
        internal const int MaximumDisplayPoints = 40000;
        internal static long FrameCount(long length, int recordSize)
        {
            if (recordSize < 12 || length <= 0 || length % recordSize != 0)
                throw new InvalidDataException("历史文件为空或包含截断帧（应为 " + recordSize + " 字节整帧）。");
            return length / recordSize;
        }

        internal static HistoryReadResult Read(string path, HistoryReadOptions options, CancellationToken token,
            int displayLimit = MaximumDisplayPoints, Action<long> visited = null)
        {
            options = options?.Clone() ?? throw new ArgumentNullException(nameof(options)); options.Validate();
            using (var cursor = new Cursor(path, options, token))
            {
                var median = options.Raw ? (int)Math.Min(cursor.Count, options.MedianLength) : 1;
                var filteredCount = 1 + (cursor.Count - 1) / median;
                var raw = new Envelope(cursor.Count, displayLimit);
                var filtered = options.Raw ? new Envelope(filteredCount, displayLimit) : raw;
                var processor = new MedianProcessor(options.Raw, median, filtered.Add);
                DateTime first = default;
                while (cursor.MoveNext(out var point))
                {
                    if (point.Index == 0) first = point.Time;
                    raw.Add(point);
                    if (options.Raw) processor.Add(point);
                    visited?.Invoke(point.Index);
                }
                if (options.Raw) processor.Complete();
                token.ThrowIfCancellationRequested();
                var preview = raw.Complete();
                return new HistoryReadResult { SourcePath = Path.GetFullPath(path), SourceLength = cursor.Length,
                    SourceSha256 = cursor.CompleteHash(), SourceFileIdentity = cursor.FileIdentity,
                    SourceFrames = cursor.Count, FilteredFrames = filteredCount, FirstTime = first, Options = options,
                    RawPreview = preview, Points = options.Raw ? filtered.Complete() : preview };
            }
        }

        internal static void Export(HistoryReadResult source, string destination, bool overwrite, CancellationToken token,
            Action<long> visited = null)
        {
            if (source?.Options == null) throw new InvalidOperationException("请先完成历史文件读取。");
            var target = Path.GetFullPath(destination);
            if (!string.Equals(Path.GetExtension(target), ".csv", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("历史导出目标必须是 CSV 文件，不能覆盖历史数据或配置。");
            if (string.Equals(target, source.SourcePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("导出目标不能覆盖历史源文件。");
            var parent = Path.GetDirectoryName(target);
            if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("导出目录不存在：" + parent);
            var existed = File.Exists(target);
            if (existed && !overwrite) throw new IOException("导出目标已存在，尚未确认覆盖。");
            string previousHash = null, previousIdentity = null;
            if (existed)
            {
                if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) throw new IOException("不能覆盖链接形式的导出目标。");
                using (var existing = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    previousIdentity = FileIdentity(existing);
                    if (previousIdentity == source.SourceFileIdentity) throw new InvalidOperationException("导出目标是历史源文件的别名。");
                    previousHash = Hash(existing, token);
                }
            }
            var temporary = Path.Combine(parent, ".epb-export-" + Guid.NewGuid().ToString("N") + ".tmp");
            var created = false;
            try
            {
                using (var cursor = new Cursor(source.SourcePath, source.Options, token))
                {
                    if (cursor.Length != source.SourceLength || cursor.FileIdentity != source.SourceFileIdentity)
                        throw new IOException("历史源文件已被替换或长度变化，请重新读取后导出。");
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
                    {
                        created = true;
                        using (var writer = new StreamWriter(output, new UTF8Encoding(true), 65536, true))
                        {
                            writer.WriteLine(source.Options.Raw ? "TimeStamp,RelTime,DAQBrakeNo,DAQCurrent" :
                                "TimeStamp,RelTime,BrakeNo,CanForce,CanCurrent,DAQCurrent,DAQTorque");
                            var median = source.Options.Raw ? (int)Math.Min(cursor.Count, source.Options.MedianLength) : 1;
                            long written = 0;
                            var processor = new MedianProcessor(source.Options.Raw, median, point =>
                            {
                                token.ThrowIfCancellationRequested();
                                var relative = (point.Time - source.FirstTime).TotalSeconds;
                                writer.WriteLine(source.Options.Raw ? string.Format(CultureInfo.InvariantCulture,
                                    "{0:yyyy-MM-dd HH:mm:ss.fff},{1:0.000},{2},{3:0.000}", point.Time, relative, point.BrakeNo, point.Current) :
                                    string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd HH:mm:ss.fff},{1:0.000},{2},{3:0.000},{4:0.000},{5:0.000},{6:0.000}",
                                        point.Time, relative, point.BrakeNo, point.Force, point.CanCurrent, point.Current, point.Torque));
                                written++;
                                visited?.Invoke(written);
                            });
                            while (cursor.MoveNext(out var point)) processor.Add(point);
                            processor.Complete();
                            if (written != source.FilteredFrames)
                                throw new IOException("导出记录数与读取快照不一致，旧目标保持不变。Expected=" + source.FilteredFrames + ", Actual=" + written);
                            var currentHash = cursor.CompleteHash();
                            if (!string.Equals(currentHash, source.SourceSha256, StringComparison.Ordinal))
                                throw new IOException("历史内容已变化，导出已取消；旧目标保持不变。Loaded=" + source.SourceSha256 + ", Current=" + currentHash);
                            writer.Flush(); output.Flush(true);
                        }
                    }
                }
                token.ThrowIfCancellationRequested();
                if (existed)
                {
                    using (var current = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
                        if (FileIdentity(current) != previousIdentity || Hash(current, token) != previousHash)
                            throw new IOException("导出期间目标被其他程序修改，拒绝覆盖。");
                    token.ThrowIfCancellationRequested(); File.Replace(temporary, target, null);
                }
                else File.Move(temporary, target); // A competing target is never overwritten.
                created = false;
            }
            finally { if (created && File.Exists(temporary)) File.Delete(temporary); }
        }

        internal static HistoryWindowResult ReadWindow(HistoryReadResult source, double minimum, double maximum,
            int maximumPoints, CancellationToken token)
        {
            if (source?.Options == null) throw new InvalidOperationException("请先完成历史文件读取。");
            if (!HistoryReadOptions.Finite(minimum) || !HistoryReadOptions.Finite(maximum) || maximum <= minimum)
                throw new ArgumentOutOfRangeException(nameof(maximum));
            if (maximumPoints < 2 || maximumPoints > MaximumDisplayPoints)
                throw new ArgumentOutOfRangeException(nameof(maximumPoints));
            var options = source.Options.Clone();
            var points = new List<HistoryPoint>(Math.Min(maximumPoints, 4096));
            long matches = 0;
            using (var cursor = new Cursor(source.SourcePath, options, token))
            {
                if (cursor.Length != source.SourceLength || cursor.FileIdentity != source.SourceFileIdentity)
                    throw new IOException("历史源文件已被替换或长度变化，请重新读取。");
                var median = options.Raw ? (int)Math.Min(cursor.Count, options.MedianLength) : 1;
                var processor = new MedianProcessor(options.Raw, median, point =>
                {
                    var axis = options.Raw ? (point.Time - source.FirstTime).TotalSeconds : point.BrakeNo;
                    if (axis < minimum || axis > maximum) return;
                    matches++;
                    if (points.Count < maximumPoints) points.Add(point);
                });
                while (cursor.MoveNext(out var point)) processor.Add(point);
                processor.Complete(); token.ThrowIfCancellationRequested();
                if (!string.Equals(cursor.CompleteHash(), source.SourceSha256, StringComparison.Ordinal))
                    throw new IOException("历史内容已变化，请重新读取。");
            }
            return new HistoryWindowResult { Points = points.ToArray(), MatchingFrames = matches, TooMany = matches > maximumPoints };
        }

        private static string Hash(Stream stream, CancellationToken token)
        {
            using (var hash = SHA256.Create())
            {
                var buffer = new byte[65536]; int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
                { token.ThrowIfCancellationRequested(); hash.TransformBlock(buffer, 0, count, buffer, 0); }
                hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return BitConverter.ToString(hash.Hash).Replace("-", string.Empty);
            }
        }

        private sealed class Cursor : IDisposable
        {
            private readonly FileStream _stream;
            private readonly HistoryReadOptions _options;
            private readonly CancellationToken _token;
            private readonly SHA256 _hash = SHA256.Create();
            private readonly byte[] _buffer;
            private int _offset, _available;
            private long _next;
            private string _digest;
            internal readonly long Length, Count;
            internal readonly string FileIdentity;
            internal Cursor(string path, HistoryReadOptions options, CancellationToken token)
            {
                options.Validate(); _options = options; _token = token; token.ThrowIfCancellationRequested();
                _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
                try
                {
                    Length = _stream.Length; Count = FrameCount(Length, options.RecordSize);
                    FileIdentity = HistoryFileReader.FileIdentity(_stream);
                    _buffer = new byte[Math.Max(1, 65536 / options.RecordSize) * options.RecordSize];
                }
                catch { _stream.Dispose(); _hash.Dispose(); throw; }
            }
            internal bool MoveNext(out HistoryPoint point)
            {
                _token.ThrowIfCancellationRequested(); point = default;
                if (_next == Count) return false;
                if (_offset == _available)
                {
                    _offset = 0; _available = 0;
                    var required = (int)Math.Min(_buffer.Length, (Count - _next) * _options.RecordSize);
                    while (_available < required)
                    {
                        _token.ThrowIfCancellationRequested();
                        var count = _stream.Read(_buffer, _available, required - _available);
                        if (count == 0) throw new EndOfStreamException("历史文件在读取期间被截断。");
                        _available += count;
                    }
                    _hash.TransformBlock(_buffer, 0, _available, _buffer, 0);
                }
                point.Index = _next++; point.BrakeNo = BitConverter.ToInt32(_buffer, _offset);
                try { point.Time = DateTime.FromFileTime(BitConverter.ToInt64(_buffer, _offset + 4)); }
                catch (ArgumentOutOfRangeException ex) { throw new InvalidDataException("历史第 " + point.Index + " 帧时间戳无效。", ex); }
                if (_options.Raw)
                    point.Current = (BitConverter.ToDouble(_buffer, _offset + 12 + _options.ChannelIndex * 8) - _options.Zero) * _options.Scale + _options.Offset;
                else
                {
                    point.Force = BitConverter.ToDouble(_buffer, _offset + 12); point.CanCurrent = BitConverter.ToDouble(_buffer, _offset + 20);
                    point.Torque = BitConverter.ToDouble(_buffer, _offset + 28); point.Current = BitConverter.ToDouble(_buffer, _offset + 36);
                }
                _offset += _options.RecordSize; return true;
            }
            internal string CompleteHash()
            {
                if (_next != Count) throw new InvalidOperationException("历史读取尚未完成。");
                if (_digest == null)
                { _hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0); _digest = BitConverter.ToString(_hash.Hash).Replace("-", string.Empty); }
                return _digest;
            }
            public void Dispose() { _stream.Dispose(); _hash.Dispose(); }
        }

        // Exactly preserves the old reduce-point median: right-pad the final
        // short window with its last value, and select length / 2 after sorting.
        private sealed class MedianProcessor
        {
            private readonly bool _raw;
            private readonly double[] _window;
            private readonly Action<HistoryPoint> _accept;
            private HistoryPoint _first;
            private int _count;
            private long _index;
            internal MedianProcessor(bool raw, int length, Action<HistoryPoint> accept)
            { _raw = raw; _window = new double[length]; _accept = accept; }
            internal void Add(HistoryPoint point)
            {
                if (!_raw) { _accept(point); return; }
                if (_count == 0) _first = point;
                _window[_count++] = point.Current;
                if (_count == _window.Length) Flush();
            }
            internal void Complete() { if (_raw && _count != 0) Flush(); }
            private void Flush()
            {
                for (var i = _count; i < _window.Length; i++) _window[i] = _window[_count - 1];
                Array.Sort(_window); _first.Current = _window[_window.Length / 2]; _first.Index = _index++;
                _accept(_first); _count = 0;
            }
        }

        private sealed class Envelope
        {
            private const int PointsPerBucket = 12;
            private readonly long _width;
            private readonly List<HistoryPoint> _result;
            private readonly HistoryPoint[] _extrema = new HistoryPoint[8];
            private readonly bool[] _valid = new bool[8];
            private HistoryPoint _first, _last, _missingFirst, _missingLast;
            private long _bucket = -1;
            private bool _missing;
            internal Envelope(long count, int limit)
            {
                if (limit < PointsPerBucket || limit > MaximumDisplayPoints) throw new ArgumentOutOfRangeException(nameof(limit));
                _width = count <= limit ? 1 : 1 + (count - 1) / Math.Max(1, limit / PointsPerBucket);
                _result = new List<HistoryPoint>((int)Math.Min(count, limit));
            }
            internal void Add(HistoryPoint point)
            {
                if (_width == 1) { _result.Add(point); return; }
                var bucket = point.Index / _width;
                if (bucket != _bucket)
                {
                    Flush(); _bucket = bucket; _first = point; _missing = false;
                    Array.Clear(_valid, 0, _valid.Length);
                }
                _last = point;
                for (var axis = 0; axis < 4; axis++)
                {
                    var value = point.Value(axis);
                    if (!HistoryReadOptions.Finite(value)) { if (!_missing) _missingFirst = point; _missingLast = point; _missing = true; continue; }
                    if (!_valid[axis * 2] || value < _extrema[axis * 2].Value(axis)) _extrema[axis * 2] = point;
                    if (!_valid[axis * 2 + 1] || value > _extrema[axis * 2 + 1].Value(axis)) _extrema[axis * 2 + 1] = point;
                    _valid[axis * 2] = _valid[axis * 2 + 1] = true;
                }
            }
            private void Flush()
            {
                if (_bucket < 0) return;
                var points = new List<HistoryPoint>(PointsPerBucket) { _first, _last };
                for (var i = 0; i < _extrema.Length; i++) if (_valid[i]) points.Add(_extrema[i]);
                if (_missing) { points.Add(_missingFirst); points.Add(_missingLast); }
                _result.AddRange(points.GroupBy(p => p.Index).OrderBy(g => g.Key).Select(g => g.First()));
            }
            internal HistoryPoint[] Complete() { if (_width != 1) { Flush(); _bucket = -1; } return _result.ToArray(); }
        }

        private static string FileIdentity(FileStream stream)
        {
            if (!GetFileInformationByHandle(stream.SafeFileHandle, out var info))
                throw new IOException("不能确认历史文件身份。", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
            return info.Volume.ToString("X8") + ":" + info.IndexHigh.ToString("X8") + info.IndexLow.ToString("X8");
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            internal uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
            internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    }
}
