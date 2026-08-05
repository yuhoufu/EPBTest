using System;
using System.Diagnostics;
using System.Threading;
using Config;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace IO.NI
{
    internal readonly struct ClrGcPauseSnapshot
    {
        internal ClrGcPauseSnapshot(bool available, string status, double durationMs, DateTime utc, long count)
        {
            Available = available;
            Status = status ?? string.Empty;
            LastPauseDurationMs = durationMs;
            LastPauseUtc = utc;
            PauseCount = count;
        }

        internal bool Available { get; }
        internal string Status { get; }
        internal double LastPauseDurationMs { get; }
        internal DateTime LastPauseUtc { get; }
        internal long PauseCount { get; }
    }

    /// <summary>
    /// 监听 CLR GCSuspendEEStart/GCRestartEEStop，记录进程真实 Stop-the-world 暂停。
    /// 初始化失败时只报告 unavailable；绝不以 GC.CollectionCount 推导暂停时长。
    /// </summary>
    internal sealed class ClrGcPauseMonitor : IDisposable
    {
        private readonly Config.IAppLogger _log;
        private readonly int _processId;
        private readonly ManualResetEventSlim _initialized = new ManualResetEventSlim(false);
        private readonly Thread _thread;
        private TraceEventSession _session;
        private double _suspendRelativeMs = double.NaN;
        private long _lastPauseDurationBits;
        private long _lastPauseUtcTicks;
        private long _pauseCount;
        private string _status = "initializing";
        private int _available;
        private int _disposed;

        internal ClrGcPauseMonitor(Config.IAppLogger log)
        {
            _log = log ?? Config.NullLogger.Instance;
            _processId = Process.GetCurrentProcess().Id;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "EPB-CLR-GC-ETW",
                Priority = ThreadPriority.BelowNormal
            };
            _thread.Start();
            if (!_initialized.Wait(2500))
                Volatile.Write(ref _status, "unavailable: ETW initialization timeout");
        }

        internal ClrGcPauseSnapshot Snapshot()
        {
            var utcTicks = Interlocked.Read(ref _lastPauseUtcTicks);
            return new ClrGcPauseSnapshot(
                Volatile.Read(ref _available) != 0,
                Volatile.Read(ref _status) ?? "unavailable",
                BitConverter.Int64BitsToDouble(Interlocked.Read(ref _lastPauseDurationBits)),
                utcTicks > 0 ? new DateTime(utcTicks, DateTimeKind.Utc) : default,
                Interlocked.Read(ref _pauseCount));
        }

        private void Run()
        {
            try
            {
                using (var session = new TraceEventSession(
                           "MTTFTest-GC-" + _processId,
                           TraceEventSessionOptions.Create))
                {
                    _session = session;
                    session.StopOnDispose = true;
                    session.EnableProviderTimeoutMSec = 1000;
                    session.Source.Clr.GCSuspendEEStart += data =>
                    {
                        if (data.ProcessID == _processId)
                            _suspendRelativeMs = data.TimeStampRelativeMSec;
                    };
                    session.Source.Clr.GCRestartEEStop += data =>
                    {
                        if (data.ProcessID != _processId || double.IsNaN(_suspendRelativeMs)) return;
                        var duration = Math.Max(0, data.TimeStampRelativeMSec - _suspendRelativeMs);
                        _suspendRelativeMs = double.NaN;
                        Interlocked.Exchange(ref _lastPauseDurationBits, BitConverter.DoubleToInt64Bits(duration));
                        Interlocked.Exchange(ref _lastPauseUtcTicks, data.TimeStamp.ToUniversalTime().Ticks);
                        Interlocked.Increment(ref _pauseCount);
                    };
                    session.EnableProvider(
                        ClrTraceEventParser.ProviderGuid,
                        TraceEventLevel.Informational,
                        (ulong)ClrTraceEventParser.Keywords.GC);
                    Volatile.Write(ref _status, "available");
                    Volatile.Write(ref _available, 1);
                    _initialized.Set();
                    session.Source.Process();
                }
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _available, 0);
                Volatile.Write(ref _status, "unavailable: " + ex.GetType().Name + ": " + ex.Message);
                try { _log.Warn("CLR ETW GC暂停诊断不可用：" + ex.Message, "AI"); } catch { }
            }
            finally
            {
                _initialized.Set();
                _session = null;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _session?.Dispose(); } catch { }
            try { _thread.Join(1500); } catch { }
            _initialized.Dispose();
        }
    }
}
