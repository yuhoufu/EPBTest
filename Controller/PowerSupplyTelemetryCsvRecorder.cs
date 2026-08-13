using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataOperation;

namespace Controller
{
    /// <summary>
    /// Power-supply telemetry persistence policy.
    ///
    /// The protection monitor continues to publish every 100ms.  This writer
    /// only controls durable telemetry: steady-state samples are reduced to
    /// the configured rate, while typed status events promote a bounded
    /// pre-event window and keep the post-event window at the event rate.
    /// </summary>
    public sealed class PowerSupplyTelemetryCsvRecorder : IDisposable
    {
        private const int QueueCapacity = 10000;
        private const int EventQueueCapacity = 100000;
        private const int PromotionQueueCapacity = 32768;
        private const int SpillQueueCapacity = 32768;
        private const int SpillReplayRecordLimit = 256;
        private const int SpillReplayByteLimit = 1024 * 1024;
        private const int SpillMaxRecordBytes = 4 * 1024 * 1024;
        private static readonly TimeSpan WriterWait = TimeSpan.FromMilliseconds(100);

        private readonly BlockingCollection<Row> _steadyQueue =
            new BlockingCollection<Row>(new ConcurrentQueue<Row>(), QueueCapacity);
        private readonly ConcurrentQueue<Row> _eventQueue = new ConcurrentQueue<Row>();
        private readonly BlockingCollection<EventRequest> _promotionQueue;
        private readonly BlockingCollection<SpoolRecord> _spoolQueue;
        private readonly object _spillGate = new object();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly object _gate = new object();
        private readonly Dictionary<int, Queue<Row>> _preBuffer = new Dictionary<int, Queue<Row>>();
        private readonly Dictionary<int, DateTime> _eventUntil = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _lastSteady = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, DateTime> _lastEventSample = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, HashSet<long>> _promotedSequences =
            new Dictionary<int, HashSet<long>>();
        private readonly Dictionary<int, string> _activeEventWindow =
            new Dictionary<int, string>();
        private readonly TelemetryPersistencePolicy _policy;
        private readonly Config.IAppLogger _log;
        private readonly Task _writerTask;
        private readonly Task _promotionTask;
        private readonly Task _spoolTask;
        private readonly Task _deferredLogTask;
        private Task _shutdownTask;
        private long _nextSequence;
        private long _droppedSteady;
        private long _droppedEvents;
        private int _eventQueueCount;
        private int _promotionQueueCount;
        private int _promotionActive;
        private int _spoolActive;
        private int _sealRequested;
        private readonly AutoResetEvent _deferredLogWake = new AutoResetEvent(false);
        private long _lastDeferredLogTicks;
        private long _lastLoggedEventDrops;
        private long _lastLoggedSteadyDrops;
        private int _deferredLogPending;
        private int _deferredLogStopping;
        private int _shutdownRequested;
        private int _disposed;
        private bool _closing;

        // Writer-owned segment state.  The active file is always a .tmp file;
        // only a successfully closed file is atomically renamed to its sealed
        // CSV name and paired manifest.
        private FileStream _segmentStream;
        private StreamWriter _segmentWriter;
        private string _segmentTempPath;
        private string _segmentPath;
        private DateTime _segmentStartedUtc;
        private long _segmentRows;
        private long _segmentSteadyRows;
        private long _segmentEventRows;
        private long _segmentEventDroppedBaseline;
        private readonly HashSet<string> _segmentEventWindows = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<long> _segmentSequences = new HashSet<long>();
        private long _segmentDropBaseline;
        private int _segmentIndex;
        private readonly BlockingCollection<string> _cleanupQueue =
            new BlockingCollection<string>(new ConcurrentQueue<string>(), 8);
        private readonly Task _cleanupTask;
        private readonly string _runId;
        private readonly long _runEpoch;
        private readonly Func<DateTime> _utcNow;
        private readonly string _spillPath;
        private readonly string _spillCursorPath;
        private readonly int _eventQueueLimit;
        private long _spillReadOffset;
        private long _spillReadPosition;
        private long _spillCursorAckOffset;
        private long _segmentSpillAckOffset;
        private readonly HashSet<long> _replayedSequences = new HashSet<long>();
        private readonly HashSet<long> _durableSequences = new HashSet<long>();
        private int _durableSequenceIndexLoaded;
        private readonly Action _spoolWriteHook;
        private string _lastSpillError;

        public PowerSupplyTelemetryCsvRecorder(
            string path,
            Config.IAppLogger log,
            TelemetryPersistencePolicy policy = null,
            string runId = null,
            long runEpoch = 0,
            Func<DateTime> utcNow = null,
            int eventQueueCapacity = EventQueueCapacity,
            Action spoolWriteHook = null)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("遥测文件路径不能为空。", nameof(path));
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("遥测目录无效。");
            Directory.CreateDirectory(directory);

            FilePath = Path.GetFullPath(path);
            _log = log ?? Config.NullLogger.Instance;
            var resolvedPolicy = policy ?? ProgramStoragePolicy.Load(
                message => _log.Warn(message, "程控电源")).Telemetry;
            _policy = ClonePolicy(resolvedPolicy);
            _runId = runId ?? TryInferRunId(FilePath);
            _runEpoch = runEpoch;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _spillPath = FilePath + ".events.spill";
            _spillCursorPath = _spillPath + ".cursor";
            _eventQueueLimit = Math.Max(1, eventQueueCapacity);
            _promotionQueue = new BlockingCollection<EventRequest>(
                new ConcurrentQueue<EventRequest>(), PromotionQueueCapacity);
            _spoolQueue = new BlockingCollection<SpoolRecord>(
                new ConcurrentQueue<SpoolRecord>(),
                Math.Max(4096, Math.Min(SpillQueueCapacity, _eventQueueLimit * 16)));
            _spoolWriteHook = spoolWriteHook;
            RecoverOrphanTempSegments();
            RecoverOrphanSealedSegments();
            _spillReadOffset = LoadSpillCursor();
            _spillReadPosition = _spillReadOffset;
            _spillCursorAckOffset = _spillReadOffset;
            _nextSequence = RestoreNextSequence();
            _writerTask = Task.Run(WriteLoop);
            _promotionTask = Task.Run(PromotionLoop);
            _spoolTask = Task.Run(SpoolLoop);
            _deferredLogTask = Task.Run(DeferredLogLoop);
            _cleanupTask = Task.Run(CleanupLoop);
            _log.Info(
                $"程控电源遥测持久化策略：{_policy} Base={FilePath}",
                "程控电源");
        }

        public string FilePath { get; }

        public TelemetryPersistencePolicy Policy => ClonePolicy(_policy);

        public long DroppedSteadySamples => Interlocked.Read(ref _droppedSteady);

        public long DroppedEventSamples => Interlocked.Read(ref _droppedEvents);

        public bool PersistenceDegraded { get; private set; }

        public string QueueDiagnostics =>
            $"promotion={Volatile.Read(ref _promotionQueueCount)}/{_promotionQueue.Count} " +
            $"promotionActive={Volatile.Read(ref _promotionActive)} " +
            $"spool={_spoolQueue.Count} spoolActive={Volatile.Read(ref _spoolActive)} " +
            $"event={Volatile.Read(ref _eventQueueCount)} spillRead={_spillReadPosition} " +
            $"spillAck={_spillReadOffset} spillLen={GetSpillLength()} " +
            $"segment={( _segmentWriter == null ? "none" : "active")} writer={_writerTask?.Status} " +
            $"sealRequested={Volatile.Read(ref _sealRequested)} dropped={DroppedEventSamples} degraded={PersistenceDegraded} " +
            $"spillError={_lastSpillError}";

        private void SignalWriter()
        {
            try { _wake.Set(); }
            catch (ObjectDisposedException) { }
        }

        /// <summary>
        /// Test/diagnostic hook that waits for already-admitted telemetry to
        /// reach the writer without making the 100ms producer wait. Production
        /// shutdown still uses a short bounded wait and leaves durable spill
        /// files for the next process when the timeout expires.
        /// </summary>
        public bool WaitForIdle(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow +
                           (timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout);
            var stable = 0;
            Interlocked.Exchange(ref _sealRequested, 1);
            SignalWriter();
            while (DateTime.UtcNow <= deadline)
            {
                if (_segmentWriter != null)
                {
                    Interlocked.Exchange(ref _sealRequested, 1);
                    SignalWriter();
                }
                if (Volatile.Read(ref _promotionQueueCount) == 0 &&
                    _promotionQueue.Count == 0 &&
                    _spoolQueue.Count == 0 &&
                    Volatile.Read(ref _promotionActive) == 0 &&
                    Volatile.Read(ref _spoolActive) == 0 &&
                    Volatile.Read(ref _eventQueueCount) == 0 &&
                    Volatile.Read(ref _sealRequested) == 0 &&
                    _segmentWriter == null &&
                    !HasUnreadSpill())
                {
                    if (++stable >= 3) return true;
                }
                else
                    stable = 0;
                Thread.Sleep(10);
            }
            return false;
        }

        /// <summary>
        /// Non-blocking producer API.  Event samples and their pre-event
        /// promotion never use the bounded steady queue, so queue pressure
        /// cannot discard a typed safety/status event.
        /// </summary>
        public void Enqueue(PowerSupplyTelemetry telemetry, int cycleNumber, string stage)
        {
            if (telemetry == null || Volatile.Read(ref _disposed) != 0) return;

            var now = _utcNow();
            var row = new Row(
                Interlocked.Increment(ref _nextSequence),
                now,
                telemetry,
                cycleNumber,
                stage ?? string.Empty,
                false,
                string.Empty);
            var typedEvent = telemetry.EventFlags != PowerSupplyTelemetryEventFlags.None;

            lock (_gate)
            {
                if (!_preBuffer.TryGetValue(telemetry.SupplyId, out var buffer))
                {
                    buffer = new Queue<Row>();
                    _preBuffer[telemetry.SupplyId] = buffer;
                    _promotedSequences[telemetry.SupplyId] = new HashSet<long>();
                }
                buffer.Enqueue(row);
                TrimPreBuffer(buffer, now);

                if (typedEvent)
                {
                    var hasActiveWindow = _eventUntil.TryGetValue(telemetry.SupplyId, out var oldUntil) &&
                                          now <= oldUntil;
                    var windowId = hasActiveWindow && _activeEventWindow.TryGetValue(
                        telemetry.SupplyId, out var existingWindow)
                        ? existingWindow
                        : telemetry.SupplyId.ToString(CultureInfo.InvariantCulture) + "-" +
                          row.Sequence.ToString(CultureInfo.InvariantCulture);
                    var until = now.AddSeconds(_policy.PostEventSeconds);
                    if (!_eventUntil.TryGetValue(telemetry.SupplyId, out var existingUntil) || until > existingUntil)
                        _eventUntil[telemetry.SupplyId] = until;
                    if (!hasActiveWindow)
                    {
                        _activeEventWindow[telemetry.SupplyId] = windowId;
                        _promotedSequences[telemetry.SupplyId].Clear();
                        _lastEventSample[telemetry.SupplyId] = DateTime.MinValue;
                    }
                    _lastEventSample[telemetry.SupplyId] = now;
                    TryQueuePromotion(new EventRequest(
                            telemetry.SupplyId,
                            windowId,
                            now,
                            row.WithEventWindow(windowId),
                            !hasActiveWindow));
                    SignalWriter();
                    return;
                }

                if (_policy.Mode == TelemetryPersistenceMode.Raw)
                {
                    TryEnqueueSteady(row);
                    return;
                }

                if (_eventUntil.TryGetValue(telemetry.SupplyId, out var activeUntil) && now <= activeUntil)
                {
                    var interval = EventInterval;
                    if (!_lastEventSample.TryGetValue(telemetry.SupplyId, out var last) ||
                        now - last >= interval)
                    {
                        _lastEventSample[telemetry.SupplyId] = now;
                        var windowId = _activeEventWindow.TryGetValue(
                            telemetry.SupplyId, out var activeWindow)
                            ? activeWindow
                            : string.Empty;
                        TryQueuePromotion(new EventRequest(
                                telemetry.SupplyId,
                                windowId,
                                now,
                                row.WithEventWindow(windowId),
                                false));
                    }
                    return;
                }

                var steadyInterval = SteadyInterval;
                if (!_lastSteady.TryGetValue(telemetry.SupplyId, out var lastSteady) ||
                    now - lastSteady >= steadyInterval)
                {
                    _lastSteady[telemetry.SupplyId] = now;
                    TryEnqueueSteady(row);
                }
            }
        }

        private TimeSpan SteadyInterval => TimeSpan.FromSeconds(1.0 / Math.Max(1, _policy.SteadyRateHz));

        private TimeSpan EventInterval => TimeSpan.FromSeconds(1.0 / Math.Max(1, _policy.EventRateHz));

        private void TrimPreBuffer(Queue<Row> buffer, DateTime now)
        {
            var cutoff = now.AddSeconds(-_policy.PreEventSeconds);
            var supplyId = buffer.Count == 0 ? 0 : buffer.Peek().Telemetry.SupplyId;
            // The producer is called from the 100ms protection monitor. Keep
            // this bounded to one dequeue per sample; promotion and any larger
            // historical-window walk happen on the background worker.
            if (buffer.Count > 1 && buffer.Peek().AdmissionUtc < cutoff)
            {
                var removed = buffer.Dequeue();
                if (_promotedSequences.TryGetValue(supplyId, out var promoted))
                    promoted.Remove(removed.Sequence);
            }
            // A pathological producer must not turn a configured window into
            // an unbounded allocation when a timestamp source is broken.
            if (buffer.Count > 10000)
            {
                var removed = buffer.Dequeue();
                if (_promotedSequences.TryGetValue(supplyId, out var promoted))
                    promoted.Remove(removed.Sequence);
            }
        }

        private void TryQueuePromotion(EventRequest request)
        {
            try
            {
                if (_promotionQueue.TryAdd(request, 0))
                {
                    Interlocked.Increment(ref _promotionQueueCount);
                    return;
                }
            }
            catch (InvalidOperationException)
            {
                // Shutdown races are treated as a persistence degradation, not
                // as a blocking producer path.
            }

            // Every event-window sample is typed persistence work.  A full
            // promotion queue is therefore an explicit degradation, never a
            // silent drop of post-event samples.
            MarkEventDropped(request?.TriggerRow?.Sequence ?? 0,
                "promotion内存队列已满");
        }

        private bool QueueEvent(Row row)
        {
            if (TryQueueEventMemory(row)) return true;

            try
            {
                if (_spoolQueue.TryAdd(new SpoolRecord(row), 0))
                {
                    SignalWriter();
                    return true;
                }
            }
            catch (InvalidOperationException) { }

            MarkEventDropped(row?.Sequence ?? 0, "事件内存队列及后台spill队列均已满");
            return false;
        }

        private bool TryQueueEventMemory(Row row)
        {
            if (row == null) return false;
            while (true)
            {
                var count = Volatile.Read(ref _eventQueueCount);
                if (count >= _eventQueueLimit) return false;
                if (Interlocked.CompareExchange(
                        ref _eventQueueCount,
                        count + 1,
                        count) != count)
                    continue;
                _eventQueue.Enqueue(row);
                SignalWriter();
                return true;
            }
        }

        private void MarkEventDropped(long sequence, string reason)
        {
            Interlocked.Increment(ref _droppedEvents);
            PersistenceDegraded = true;
            QueueDeferredDropLog(
                $"程控电源遥测事件无法进入有界内存/后台spill队列：Sequence={sequence} " +
                $"Reason={reason}；主控制链继续运行。",
                true);
        }

        private void TryEnqueueSteady(Row row)
        {
            if (_steadyQueue.TryAdd(row, 0))
            {
                SignalWriter();
                return;
            }

            Interlocked.Increment(ref _droppedSteady);
            QueueDeferredDropLog(
                "程控电源遥测稳态队列已满，重复稳态样本跳过；typed事件队列不受此丢弃路径影响。",
                false);
        }

        private void QueueDeferredDropLog(string reason, bool eventDrop)
        {
            // This method is called by the 100ms observer path. It performs
            // only atomic state updates and a non-blocking event signal; all
            // logger calls happen on DeferredLogLoop below.
            if (eventDrop) PersistenceDegraded = true;
            if (Interlocked.Exchange(ref _deferredLogPending, 1) == 0)
            {
                try { _deferredLogWake.Set(); }
                catch (ObjectDisposedException) { }
            }
        }

        private void DeferredLogLoop()
        {
            try
            {
                try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch { }
                while (Volatile.Read(ref _deferredLogStopping) == 0)
                {
                    _deferredLogWake.WaitOne(TimeSpan.FromSeconds(1));
                    FlushDeferredDropLog(false);
                }
                FlushDeferredDropLog(true);
            }
            catch (Exception ex)
            {
                // Logging must never affect telemetry or control shutdown.
                try { _log.Warn($"程控电源遥测降级日志线程异常：{ex.Message}", "程控电源"); }
                catch { }
            }
        }

        private void FlushDeferredDropLog(bool force)
        {
            if (Volatile.Read(ref _deferredLogPending) == 0) return;
            var nowTicks = DateTime.UtcNow.Ticks;
            var lastTicks = Interlocked.Read(ref _lastDeferredLogTicks);
            if (!force && lastTicks != 0 &&
                nowTicks - lastTicks < TimeSpan.FromMinutes(1).Ticks)
                return;
            if (Interlocked.CompareExchange(
                    ref _lastDeferredLogTicks,
                    nowTicks,
                    lastTicks) != lastTicks)
                return;

            var events = DroppedEventSamples;
            var steady = DroppedSteadySamples;
            var eventDelta = events - Interlocked.Read(ref _lastLoggedEventDrops);
            var steadyDelta = steady - Interlocked.Read(ref _lastLoggedSteadyDrops);
            if (eventDelta > 0)
            {
                _log.Error(
                    $"程控电源遥测事件落盘降级：DroppedEventDelta={eventDelta} " +
                    $"DroppedEventTotal={events}；主控制链继续运行。",
                    "程控电源");
                Interlocked.Exchange(ref _lastLoggedEventDrops, events);
            }
            if (steadyDelta > 0)
            {
                _log.Warn(
                    $"程控电源遥测稳态队列满：DroppedSteadyDelta={steadyDelta} " +
                    $"DroppedSteadyTotal={steady}；typed事件队列不受此路径影响。",
                    "程控电源");
                Interlocked.Exchange(ref _lastLoggedSteadyDrops, steady);
            }
            Interlocked.Exchange(ref _deferredLogPending, 0);
        }

        private void WriteLoop()
        {
            try
            {
                while (true)
                {
                    if (TryReplaySpill())
                        continue;
                    if (TryTakeNext(out var row))
                    {
                        try
                        {
                            EnsureSegment(row.AdmissionUtc);
                            WriteRow(row);
                            if (ShouldRotate())
                                SealSegment();
                        }
                        catch (Exception ex)
                        {
                            // Keep events recoverable across a transient I/O
                            // fault.  Requeueing a steady row is best effort;
                            // it is explicitly allowed to be dropped.
                            _log.Error($"程控电源遥测写盘失败：{ex.Message}", "程控电源", ex);
                            CloseSegmentWithoutSeal();
                            if (row.IsEventSample)
                                QueueEvent(row);
                            else if (!_steadyQueue.IsAddingCompleted)
                                TryEnqueueSteady(row);
                            Thread.Sleep(250);
                        }
                        continue;
                    }

                    if (Interlocked.Exchange(ref _sealRequested, 0) != 0 &&
                        _segmentWriter != null)
                        SealSegment();

                    if (_closing && _steadyQueue.IsCompleted &&
                        _promotionTask.IsCompleted && _spoolTask.IsCompleted &&
                        _eventQueue.IsEmpty)
                    {
                        // Any replay rows currently live only in .tmp must be
                        // sealed before shutdown can acknowledge their journal
                        // cursor. This keeps cursor advancement tied to a
                        // durable manifest even when no normal queue row remains.
                        if (_segmentWriter != null)
                            SealSegment();
                        if (!HasUnreadSpill()) break;
                    }
                    _wake.WaitOne(WriterWait);
                }

                if (_segmentWriter != null)
                    SealSegment();
            }
            catch (Exception ex)
            {
                _log.Error($"程控电源遥测写盘线程异常：{ex.Message}", "程控电源", ex);
                CloseSegmentWithoutSeal();
            }
        }

        private bool TryTakeNext(out Row row)
        {
            if (_eventQueue.TryDequeue(out row))
            {
                Interlocked.Decrement(ref _eventQueueCount);
                return true;
            }
            if (_steadyQueue.TryTake(out row, 0)) return true;
            row = null;
            return false;
        }

        private void PromotionLoop()
        {
            try
            {
                try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch { }
                while (!_promotionQueue.IsCompleted)
                {
                    if (!_promotionQueue.TryTake(out var request, 100)) continue;
                    Interlocked.Decrement(ref _promotionQueueCount);
                    Interlocked.Increment(ref _promotionActive);
                    try { ProcessPromotionRequest(request); }
                    finally { Interlocked.Decrement(ref _promotionActive); }
                }
            }
            catch (Exception ex)
            {
                PersistenceDegraded = true;
                _log.Error($"程控电源遥测事件推广线程异常：{ex.Message}", "程控电源", ex);
            }
            finally
            {
                try { _spoolQueue.CompleteAdding(); } catch { }
                SignalWriter();
            }
        }

        private void ProcessPromotionRequest(EventRequest request)
        {
            if (request == null) return;

            // Snapshot the promotion work under the short producer lock. No
            // queue admission or disk operation is allowed while _gate is held;
            // this prevents a slow spool/segment flush from stalling the 100ms
            // protection callback.
            var rows = new List<Row>();
            lock (_gate)
            {
                if (!_preBuffer.TryGetValue(request.SupplyId, out var buffer) ||
                    !_promotedSequences.TryGetValue(request.SupplyId, out var promoted))
                    return;

                if (request.TriggerRow != null &&
                    promoted.Add(request.TriggerRow.Sequence))
                    rows.Add(request.TriggerRow);

                if (request.PromotePreWindow)
                {
                    var last = DateTime.MinValue;
                    foreach (var previous in buffer)
                    {
                        if (previous.Sequence == 0 || promoted.Contains(previous.Sequence)) continue;
                        if (last != DateTime.MinValue &&
                            previous.AdmissionUtc - last < EventInterval)
                            continue;
                        promoted.Add(previous.Sequence);
                        rows.Add(previous.WithEventWindow(request.WindowId));
                        last = previous.AdmissionUtc;
                    }
                }
            }

            foreach (var row in rows)
                QueueEvent(row);
        }

        private void SpoolLoop()
        {
            try
            {
                try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch { }
                while (!_spoolQueue.IsCompleted)
                {
                    if (!_spoolQueue.TryTake(out var record, 100)) continue;
                    var batch = new List<SpoolRecord> { record };
                    while (batch.Count < SpillReplayRecordLimit &&
                           _spoolQueue.TryTake(out var next, 0))
                        batch.Add(next);
                    Interlocked.Increment(ref _spoolActive);
                    try { SpillEvents(batch); }
                    finally { Interlocked.Decrement(ref _spoolActive); }
                }
                var remainingBatch = new List<SpoolRecord>();
                while (_spoolQueue.TryTake(out var remaining, 0))
                {
                    remainingBatch.Add(remaining);
                    if (remainingBatch.Count >= SpillReplayRecordLimit)
                    {
                        Interlocked.Increment(ref _spoolActive);
                        try { SpillEvents(remainingBatch); }
                        finally { Interlocked.Decrement(ref _spoolActive); }
                        remainingBatch.Clear();
                    }
                }
                if (remainingBatch.Count > 0)
                {
                    Interlocked.Increment(ref _spoolActive);
                    try { SpillEvents(remainingBatch); }
                    finally { Interlocked.Decrement(ref _spoolActive); }
                }
            }
            catch (Exception ex)
            {
                PersistenceDegraded = true;
                _log.Error($"程控电源遥测spill线程异常：{ex.Message}", "程控电源", ex);
            }
            finally
            {
                SignalWriter();
            }
        }

        private bool SpillEvent(SpoolRecord record)
        {
            return SpillEvents(record == null ? null : new[] { record });
        }

        private bool SpillEvents(IEnumerable<SpoolRecord> records)
        {
            var valid = (records ?? Enumerable.Empty<SpoolRecord>())
                .Where(x => x?.Row != null)
                .ToArray();
            if (valid.Length == 0) return false;
            try
            {
                var builder = new StringBuilder(valid.Length * 512);
                foreach (var record in valid)
                {
                    _spoolWriteHook?.Invoke();
                    var row = record.Row;
                    var window = Convert.ToBase64String(
                        Encoding.UTF8.GetBytes(row.EventWindowId ?? string.Empty));
                    var csv = Convert.ToBase64String(Encoding.UTF8.GetBytes(ToCsvLine(row)));
                    builder.Append(row.Sequence.ToString(CultureInfo.InvariantCulture))
                        .Append('\t').Append(window).Append('\t').Append(csv)
                        .Append(Environment.NewLine);
                }
                var bytes = Encoding.UTF8.GetBytes(builder.ToString());
                lock (_spillGate)
                {
                    using (var stream = new FileStream(
                        _spillPath,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.ReadWrite,
                        64 * 1024,
                        FileOptions.WriteThrough))
                    {
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush(true);
                    }
                }
                SignalWriter();
                return true;
            }
            catch (Exception ex)
            {
                foreach (var record in valid)
                    MarkEventDropped(record.Row.Sequence, "spill journal 写入失败");
                _log.Error($"程控电源遥测 spill journal 写入失败：{ex.Message}", "程控电源", ex);
                return false;
            }
        }

        private bool TryReplaySpill()
        {
            if (!File.Exists(_spillPath)) return false;
            lock (_spillGate)
            {
                try
                {
                    EnsureDurableSequenceIndex();
                    using (var stream = new FileStream(
                        _spillPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite,
                        64 * 1024,
                        FileOptions.SequentialScan))
                    {
                        if (_spillReadOffset > stream.Length || _spillReadPosition > stream.Length)
                        {
                            _spillReadOffset = 0;
                            _spillReadPosition = 0;
                        }
                        if (_spillReadPosition < _spillReadOffset)
                            _spillReadPosition = _spillReadOffset;
                        stream.Position = _spillReadPosition;
                        var originalAckOffset = _spillReadOffset;
                        var processed = 0;
                        var consumedBytes = 0;
                        var hasUnsealedRows = false;
                        var hasUnrecoverableRecord = false;
                        while (processed < SpillReplayRecordLimit &&
                               consumedBytes < SpillReplayByteLimit)
                        {
                            var start = stream.Position;
                            if (!TryReadSpillLine(stream, out var record)) break;
                            var end = stream.Position;
                            consumedBytes += (int)Math.Min(int.MaxValue, end - start);
                            _spillReadPosition = end;
                            if (string.IsNullOrWhiteSpace(record)) continue;
                            processed++;

                            if (!TryParseSpillRecord(record, out var sequence,
                                out var window, out var csv))
                            {
                                PersistenceDegraded = true;
                                MarkEventDropped(0, "spill journal 记录格式无效");
                                hasUnrecoverableRecord = true;
                                continue;
                            }

                            if (_replayedSequences.Contains(sequence) ||
                                _segmentSequences.Contains(sequence) ||
                                _durableSequences.Contains(sequence))
                                continue;
                            EnsureSegment(_utcNow());
                            _segmentWriter.WriteLine(csv);
                            _segmentRows++;
                            _segmentEventRows++;
                            _segmentSequences.Add(sequence);
                            _replayedSequences.Add(sequence);
                            _segmentSpillAckOffset = Math.Max(_segmentSpillAckOffset, end);
                            hasUnsealedRows = true;
                            if (!string.IsNullOrWhiteSpace(window))
                                _segmentEventWindows.Add(window);
                            if (ShouldRotate()) SealSegment();
                        }

                        if (processed > 0 && hasUnsealedRows)
                        {
                            // Keep the read cursor in memory while the active
                            // segment is still a .tmp. The acknowledgement is
                            // emitted only by SealSegment after its manifest is
                            // durably published.
                            _segmentWriter?.Flush();
                            _segmentStream?.Flush(true);
                        }
                        else if (processed > 0 && !hasUnrecoverableRecord &&
                                 _segmentSpillAckOffset <= originalAckOffset)
                        {
                            // Every row in this batch was already present in a
                            // sealed segment; it is safe to acknowledge without
                            // opening a new active segment.
                            PersistSpillCursor(_spillReadPosition);
                        }

                        // Once the cursor reaches EOF, truncate first and reset
                        // the cursor second, but only after the cursor has been
                        // acknowledged by a sealed manifest (or all rows were
                        // already sealed duplicates).
                        if (_spillReadPosition >= stream.Length &&
                            _spillCursorAckOffset >= stream.Length)
                        {
                            stream.Dispose();
                            using (var truncate = new FileStream(
                                _spillPath,
                                FileMode.Create,
                                FileAccess.Write,
                                FileShare.ReadWrite,
                                4096,
                                FileOptions.WriteThrough))
                                truncate.Flush(true);
                            _spillReadOffset = 0;
                            _spillReadPosition = 0;
                            PersistSpillCursor(0);
                            _replayedSequences.Clear();
                        }
                        return processed > 0;
                    }
                }
                catch (Exception ex)
                {
                    PersistenceDegraded = true;
                    _lastSpillError = ex.ToString();
                    _log.Error($"程控电源遥测 spill journal 读取失败：{ex.Message}", "程控电源", ex);
                    return false;
                }
            }
        }

        private bool HasUnreadSpill()
        {
            if (!File.Exists(_spillPath)) return false;
            try { return new FileInfo(_spillPath).Length > _spillReadOffset; }
            catch { return true; }
        }

        private long GetSpillLength()
        {
            try { return File.Exists(_spillPath) ? new FileInfo(_spillPath).Length : 0L; }
            catch { return -1L; }
        }

        private void EnsureDurableSequenceIndex()
        {
            if (Interlocked.Exchange(ref _durableSequenceIndexLoaded, 1) != 0)
                return;
            try
            {
                var directory = Path.GetDirectoryName(FilePath);
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
                foreach (var candidate in EnumerateDurableCsvPaths(directory))
                {
                    using (var reader = new StreamReader(candidate, Encoding.UTF8, true, 64 * 1024))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (line.StartsWith("Utc,", StringComparison.OrdinalIgnoreCase)) continue;
                            if (TryExtractSequence(line, out var sequence))
                                _durableSequences.Add(sequence);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // A failed index is safe: the cursor remains conservative and
                // the event is retried. Do not fail the control process.
                _log.Warn($"程控电源遥测历史序号索引读取失败，将保守回放：{ex.Message}", "程控电源");
            }
        }

        private static bool TryExtractSequence(string csvLine, out long sequence)
        {
            sequence = 0;
            if (string.IsNullOrWhiteSpace(csvLine)) return false;
            var separator = csvLine.LastIndexOf(',');
            return separator >= 0 &&
                   long.TryParse(csvLine.Substring(separator + 1),
                       NumberStyles.Integer,
                       CultureInfo.InvariantCulture,
                       out sequence) &&
                   sequence > 0;
        }

        private long LoadSpillCursor()
        {
            try
            {
                if (!File.Exists(_spillCursorPath)) return 0;
                return long.TryParse(
                           File.ReadAllText(_spillCursorPath, Encoding.ASCII),
                           NumberStyles.Integer,
                           CultureInfo.InvariantCulture,
                           out var offset) && offset >= 0
                    ? offset
                    : 0;
            }
            catch { return 0; }
        }

        private void PersistSpillCursor(long offset)
        {
            var temp = _spillCursorPath + ".tmp";
            var text = Math.Max(0L, offset).ToString(CultureInfo.InvariantCulture);
            using (var stream = new FileStream(
                temp,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                var bytes = Encoding.ASCII.GetBytes(text);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            if (File.Exists(_spillCursorPath))
            {
                try { File.Replace(temp, _spillCursorPath, null); }
                catch
                {
                    File.Copy(temp, _spillCursorPath, true);
                    File.Delete(temp);
                }
            }
            else
                File.Move(temp, _spillCursorPath);
            _spillCursorAckOffset = Math.Max(0L, offset);
        }

        private long RestoreNextSequence()
        {
            long max = 0;
            try
            {
                var directory = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                {
                    foreach (var path in EnumerateDurableCsvPaths(directory))
                    {
                        using (var reader = new StreamReader(path, Encoding.UTF8, true, 64 * 1024))
                        {
                            string line;
                            while ((line = reader.ReadLine()) != null)
                                if (TryExtractSequence(line, out var sequence)) max = Math.Max(max, sequence);
                        }
                    }
                }
                if (File.Exists(_spillPath))
                {
                    using (var stream = new FileStream(
                        _spillPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite,
                        64 * 1024,
                        FileOptions.SequentialScan))
                    {
                        string line;
                        while (TryReadSpillLine(stream, out line))
                        {
                            var separator = line.IndexOf('\t');
                            if (separator > 0 && long.TryParse(
                                    line.Substring(0, separator), NumberStyles.Integer,
                                    CultureInfo.InvariantCulture, out var sequence))
                                max = Math.Max(max, sequence);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"程控电源遥测历史序号恢复失败，从当前最大值继续：{ex.Message}", "程控电源");
            }
            return max;
        }

        private IEnumerable<string> EnumerateDurableCsvPaths(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                yield break;
            var baseName = Path.GetFileName(FilePath);
            var stem = Path.GetFileNameWithoutExtension(FilePath);
            foreach (var manifestPath in Directory.EnumerateFiles(directory, "*.manifest.json"))
            {
                string text;
                try { text = File.ReadAllText(manifestPath, Encoding.UTF8); }
                catch { continue; }
                var fileName = JsonValue(text, "fileName");
                if (!(string.Equals(fileName, baseName, StringComparison.OrdinalIgnoreCase) ||
                      fileName.StartsWith(stem + "_seg", StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (TryResolveManifestSegment(
                        directory,
                        manifestPath,
                        text,
                        out var sealedPath,
                        out var manifestBytes,
                        out var manifestHash) &&
                    new FileInfo(sealedPath).Length == manifestBytes &&
                    string.Equals(ComputeSha256(sealedPath), manifestHash,
                        StringComparison.OrdinalIgnoreCase))
                    yield return sealedPath;
            }
        }

        private void RecoverOrphanTempSegments()
        {
            try
            {
                var directory = Path.GetDirectoryName(FilePath);
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
                var baseName = Path.GetFileName(FilePath);
                var stem = Path.GetFileNameWithoutExtension(FilePath);
                foreach (var temp in Directory.EnumerateFiles(directory))
                {
                    var name = Path.GetFileName(temp);
                    var isBaseTemp = name.StartsWith(baseName + ".tmp", StringComparison.OrdinalIgnoreCase);
                    var isSegmentTemp = name.StartsWith(stem + "_seg", StringComparison.OrdinalIgnoreCase) &&
                                        name.IndexOf(".csv.tmp", StringComparison.OrdinalIgnoreCase) >= 0;
                    if ((!isBaseTemp && !isSegmentTemp) || IsReparsePoint(temp) ||
                        !LooksLikeTelemetryCsv(temp)) continue;
                    var marker = name.IndexOf(".tmp", StringComparison.OrdinalIgnoreCase);
                    if (marker <= 0) continue;
                    var sealedName = name.Substring(0, marker);
                    if (!IsSafeManifestFileName(sealedName)) continue;
                    var target = Path.Combine(directory, sealedName);
                    if (File.Exists(target) || File.Exists(target + ".manifest.json")) continue;
                    try
                    {
                        File.Move(temp, target);
                        PublishRecoveredManifest(target);
                    }
                    catch (Exception ex)
                    {
                        _log.Warn(
                            $"程控电源遥测孤儿临时段恢复失败，保留未确认tmp并由spill重放：{temp}，原因：{ex.Message}",
                            "程控电源");
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"程控电源遥测孤儿临时段扫描失败，将保守重放spill：{ex.Message}", "程控电源");
            }
        }

        /// <summary>
        /// A crash can occur after the CSV rename and before the manifest
        /// rename.  Such a CSV is complete evidence, but without a manifest
        /// it must not be indexed or used for retention.  Validate the exact
        /// telemetry header and publish a fresh manifest at startup so the
        /// row sequence is durable and spill replay is idempotent.
        /// </summary>
        private void RecoverOrphanSealedSegments()
        {
            try
            {
                var directory = Path.GetDirectoryName(FilePath);
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
                var baseName = Path.GetFileName(FilePath);
                var stem = Path.GetFileNameWithoutExtension(FilePath);
                foreach (var candidate in Directory.EnumerateFiles(directory, "*.csv"))
                {
                    if (IsReparsePoint(candidate)) continue;
                    var name = Path.GetFileName(candidate);
                    if (!(string.Equals(name, baseName, StringComparison.OrdinalIgnoreCase) ||
                          name.StartsWith(stem + "_seg", StringComparison.OrdinalIgnoreCase)))
                        continue;
                    var manifestPath = candidate + ".manifest.json";
                    if (File.Exists(manifestPath) || !LooksLikeTelemetryCsv(candidate)) continue;
                    try
                    {
                        PublishRecoveredManifest(candidate);
                    }
                    catch (Exception ex)
                    {
                        _log.Warn(
                            $"程控电源遥测孤儿封存CSV补发manifest失败，暂不计入durable：{candidate}，原因：{ex.Message}",
                            "程控电源");
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"程控电源遥测孤儿封存CSV扫描失败，将保守重放spill：{ex.Message}", "程控电源");
            }
        }

        private static bool LooksLikeTelemetryCsv(string path)
        {
            try
            {
                using (var reader = new StreamReader(path, Encoding.UTF8, true, 4096))
                    return string.Equals(reader.ReadLine(), Header, StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private void PublishRecoveredManifest(string sealedPath)
        {
            var bytes = new FileInfo(sealedPath).Length;
            var rows = 0L;
            using (var reader = new StreamReader(sealedPath, Encoding.UTF8, true, 64 * 1024))
            {
                while (reader.ReadLine() != null) rows++;
            }
            rows = Math.Max(0, rows - 1);
            var stamp = File.GetLastWriteTimeUtc(sealedPath);
            var manifest = BuildManifest(
                sealedPath,
                stamp,
                _utcNow(),
                rows,
                rows,
                0,
                Enumerable.Empty<string>(),
                bytes,
                0,
                0,
                ComputeSha256(sealedPath));
            var manifestPath = sealedPath + ".manifest.json";
            var temp = manifestPath + ".tmp." + Guid.NewGuid().ToString("N");
            using (var stream = new FileStream(
                temp,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(manifest);
                writer.Flush();
                stream.Flush(true);
            }
            File.Move(temp, manifestPath);
        }

        private static bool TryReadSpillLine(FileStream stream, out string line)
        {
            var buffer = new StringBuilder(1024);
            while (true)
            {
                var value = stream.ReadByte();
                if (value < 0)
                {
                    line = buffer.Length == 0 ? null : buffer.ToString();
                    return line != null;
                }
                if (value == '\n')
                {
                    if (buffer.Length > 0 && buffer[buffer.Length - 1] == '\r')
                        buffer.Length--;
                    line = buffer.ToString();
                    return true;
                }
                if (buffer.Length >= SpillMaxRecordBytes)
                    throw new InvalidDataException("spill journal 单条记录超过上限。");
                buffer.Append((char)value);
            }
        }

        private static bool TryParseSpillRecord(
            string record,
            out long sequence,
            out string window,
            out string csv)
        {
            sequence = 0;
            window = string.Empty;
            csv = string.Empty;
            var first = record.IndexOf('\t');
            var second = first < 0 ? -1 : record.IndexOf('\t', first + 1);
            if (first <= 0 || second <= first + 1 || second >= record.Length - 1 ||
                !long.TryParse(record.Substring(0, first).Trim('\uFEFF'), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out sequence))
                return false;
            try
            {
                window = Encoding.UTF8.GetString(Convert.FromBase64String(
                    record.Substring(first + 1, second - first - 1)));
                csv = Encoding.UTF8.GetString(Convert.FromBase64String(
                    record.Substring(second + 1)));
                return csv.Length > 0;
            }
            catch { return false; }
        }

        private void EnsureSegment(DateTime rowUtc)
        {
            if (_segmentWriter != null) return;
            var candidate = NextSegmentPath();
            _segmentPath = candidate;
            _segmentTempPath = candidate + ".tmp";
            if (File.Exists(_segmentTempPath))
                _segmentTempPath = candidate + ".tmp." + Guid.NewGuid().ToString("N");
            _segmentStream = new FileStream(
                _segmentTempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            _segmentWriter = new StreamWriter(_segmentStream, new UTF8Encoding(true), 64 * 1024);
            _segmentWriter.WriteLine(Header);
            _segmentWriter.Flush();
            _segmentStartedUtc = rowUtc == default ? _utcNow() : rowUtc;
            _segmentRows = 0;
            _segmentSteadyRows = 0;
            _segmentEventRows = 0;
            _segmentEventWindows.Clear();
            _segmentSequences.Clear();
            _segmentEventDroppedBaseline = Interlocked.Read(ref _droppedEvents);
            _segmentDropBaseline = Interlocked.Read(ref _droppedSteady);
        }

        private string NextSegmentPath()
        {
            if (_segmentIndex == 0 &&
                !File.Exists(FilePath) &&
                !File.Exists(FilePath + ".manifest.json"))
            {
                _segmentIndex = 1;
                return FilePath;
            }

            var directory = Path.GetDirectoryName(FilePath);
            var extension = Path.GetExtension(FilePath);
            var stem = Path.GetFileNameWithoutExtension(FilePath);
            while (true)
            {
                var suffix = _segmentIndex <= 0 ? 1 : _segmentIndex;
                _segmentIndex = suffix + 1;
                var path = Path.Combine(directory,
                    stem + "_seg" + suffix.ToString("D4", CultureInfo.InvariantCulture) + extension);
                if (!File.Exists(path) && !File.Exists(path + ".manifest.json") && !File.Exists(path + ".tmp"))
                    return path;
            }
        }

        private void WriteRow(Row row)
        {
            if (row == null || _segmentSequences.Contains(row.Sequence)) return;
            _segmentWriter.WriteLine(ToCsvLine(row));
            _segmentRows++;
            _segmentSequences.Add(row.Sequence);
            if (row.IsEventSample)
            {
                _segmentEventRows++;
                if (!string.IsNullOrWhiteSpace(row.EventWindowId))
                    _segmentEventWindows.Add(row.EventWindowId);
            }
            else
                _segmentSteadyRows++;
        }

        private bool ShouldRotate()
        {
            _segmentWriter.Flush();
            var bytes = _segmentStream.Length;
            var rotateBytes = _policy.Mode == TelemetryPersistenceMode.Raw
                ? Math.Min(_policy.RotateBytes, _policy.RawRotateBytes)
                : _policy.RotateBytes;
            return bytes >= rotateBytes ||
                   _utcNow() - _segmentStartedUtc >= TimeSpan.FromHours(_policy.RotateHours);
        }

        private void SealSegment()
        {
            if (_segmentWriter == null) return;
            var temp = _segmentTempPath;
            var sealedPath = _segmentPath;
            var rows = _segmentRows;
            var steadyRows = _segmentSteadyRows;
            var eventRows = _segmentEventRows;
            var eventWindows = _segmentEventWindows.ToArray();
            var start = _segmentStartedUtc;
            var drops = Interlocked.Read(ref _droppedSteady) - _segmentDropBaseline;
            var eventDrops = Interlocked.Read(ref _droppedEvents) - _segmentEventDroppedBaseline;
            var sealedUtc = _utcNow();
            try
            {
                _segmentWriter.Flush();
                _segmentStream.Flush(true);
                _segmentWriter.Dispose();
                _segmentWriter = null;
                _segmentStream?.Dispose();
                _segmentStream = null;
                File.Move(temp, sealedPath);
                var bytes = new FileInfo(sealedPath).Length;
                var hash = ComputeSha256(sealedPath);
                var manifestPath = sealedPath + ".manifest.json";
                var manifest = BuildManifest(
                    sealedPath,
                    start,
                    sealedUtc,
                    rows,
                    steadyRows,
                    eventRows,
                    eventWindows,
                    bytes,
                    drops,
                    eventDrops,
                    hash);
                var manifestTemp = manifestPath + ".tmp." + Guid.NewGuid().ToString("N");
                using (var manifestStream = new FileStream(
                    manifestTemp,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough))
                using (var manifestWriter = new StreamWriter(manifestStream, new UTF8Encoding(false)))
                {
                    manifestWriter.Write(manifest);
                    manifestWriter.Flush();
                    manifestStream.Flush(true);
                }
                File.Move(manifestTemp, manifestPath);
                if (_segmentSpillAckOffset > _spillCursorAckOffset)
                    PersistSpillCursor(_segmentSpillAckOffset);
                QueueCleanup(sealedPath);
            }
            catch (Exception ex)
            {
                _log.Warn($"程控电源遥测段封口失败，保留临时文件：{temp}，原因：{ex.Message}", "程控电源");
                CloseSegmentWithoutSeal();
            }
            finally
            {
                _segmentTempPath = null;
                _segmentPath = null;
            _segmentRows = 0;
            _segmentSteadyRows = 0;
            _segmentEventRows = 0;
            _segmentEventWindows.Clear();
            _segmentSpillAckOffset = 0;
            }
        }

        private void CloseSegmentWithoutSeal()
        {
            try { _segmentWriter?.Dispose(); } catch { }
            try { _segmentStream?.Dispose(); } catch { }
            _segmentWriter = null;
            _segmentStream = null;
        }

        private void QueueCleanup(string sealedPath)
        {
            if (!_cleanupQueue.TryAdd(sealedPath))
                _log.Warn($"程控电源遥测清理队列已满，延期旧段清理：{sealedPath}", "程控电源");
        }

        private void CleanupLoop()
        {
            try
            {
                try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch { }
                while (_cleanupQueue.TryTake(out var sealedPath, Timeout.Infinite))
                {
                    // Keep retention I/O out of the sealing/writer path and
                    // throttle each housekeeping pass.
                    Thread.Sleep(TimeSpan.FromSeconds(5));
                    CleanupExpiredSegments(
                        _utcNow() - TimeSpan.FromDays(_policy.RetentionDays),
                        sealedPath,
                        16,
                        32L * 1024L * 1024L);
                    while (_cleanupQueue.TryTake(out sealedPath, 0))
                    {
                        CleanupExpiredSegments(
                            _utcNow() - TimeSpan.FromDays(_policy.RetentionDays),
                            sealedPath,
                            16,
                            32L * 1024L * 1024L);
                        Thread.Sleep(TimeSpan.FromSeconds(5));
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // CompleteAdding during orderly shutdown.
            }
            catch (Exception ex)
            {
                _log.Warn($"程控电源遥测清理线程异常，主进程继续运行：{ex.Message}", "程控电源");
            }
        }

        private void CleanupExpiredSegments(
            DateTime cutoffUtc,
            string currentSealedPath,
            int maxFiles,
            long maxBytes)
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
            var stem = Path.GetFileNameWithoutExtension(FilePath);
            var deletedFiles = 0;
            long deletedBytes = 0;
            foreach (var manifestPath in Directory.EnumerateFiles(directory, "*.manifest.json"))
            {
                if (deletedFiles >= maxFiles || deletedBytes >= maxBytes) break;
                try
                {
                    var text = File.ReadAllText(manifestPath, Encoding.UTF8);
                    if (text.IndexOf("\"kind\":\"PowerSupplyTelemetry\"", StringComparison.Ordinal) < 0)
                        continue;
                    var fileName = JsonValue(text, "fileName");
                    var sealedUtcText = JsonValue(text, "sealedUtc");
                    if (string.IsNullOrWhiteSpace(fileName) ||
                        !DateTime.TryParse(sealedUtcText, CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind, out var sealedUtc) ||
                        sealedUtc >= cutoffUtc)
                        continue;
                    if (!(string.Equals(fileName, Path.GetFileName(FilePath), StringComparison.OrdinalIgnoreCase) ||
                          fileName.StartsWith(stem + "_seg", StringComparison.OrdinalIgnoreCase)))
                        continue;
                    if (!TryResolveManifestSegment(
                            directory,
                            manifestPath,
                            text,
                            out var sealedPath,
                            out var manifestBytes,
                            out var manifestHash))
                        continue;
                    if (string.Equals(Path.GetFullPath(sealedPath), Path.GetFullPath(currentSealedPath),
                        StringComparison.OrdinalIgnoreCase))
                        continue;
                    var fileBytes = new FileInfo(sealedPath).Length;
                    if (manifestBytes != fileBytes ||
                        !string.Equals(ComputeSha256(sealedPath), manifestHash,
                            StringComparison.OrdinalIgnoreCase))
                        continue;
                    File.Delete(sealedPath);
                    File.Delete(manifestPath);
                    deletedFiles++;
                    deletedBytes += fileBytes;
                }
                catch (Exception ex)
                {
                    _log.Warn($"程控电源遥测旧封存段清理失败，已跳过：{manifestPath}，原因：{ex.Message}", "程控电源");
                }
            }
        }

        internal static bool IsSafeManifestFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName) || Path.IsPathRooted(fileName)) return false;
            if (fileName.IndexOfAny(new[] { '\\', '/' }) >= 0 || fileName.Contains("..")) return false;
            return string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal);
        }

        private static bool TryResolveManifestSegment(
            string root,
            string manifestPath,
            string manifest,
            out string sealedPath,
            out long manifestBytes,
            out string manifestHash)
        {
            sealedPath = null;
            manifestBytes = -1;
            manifestHash = string.Empty;
            try
            {
                var fileName = JsonValue(manifest, "fileName");
                if (!IsSafeManifestFileName(fileName)) return false;
                var rootFull = Path.GetFullPath(root);
                var candidate = Path.GetFullPath(Path.Combine(rootFull, fileName));
                var expectedManifest = candidate + ".manifest.json";
                var actualManifest = Path.GetFullPath(manifestPath);
                // A manifest is trusted only when it is the exact sidecar for
                // the target CSV; arbitrary fileName/manifestPath pairings
                // must never influence retention or sequence recovery.
                if (!string.Equals(actualManifest, expectedManifest,
                        StringComparison.OrdinalIgnoreCase) ||
                    !IsSafeRetentionPath(rootFull, candidate) ||
                    !IsSafeRetentionPath(rootFull, actualManifest) ||
                    !File.Exists(candidate))
                    return false;
                manifestBytes = JsonLong(manifest, "bytes");
                manifestHash = JsonValue(manifest, "sha256");
                if (manifestBytes < 0 || string.IsNullOrWhiteSpace(manifestHash)) return false;
                sealedPath = candidate;
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Retention and durable-index operations only accept direct children
        /// of the trusted telemetry root and reject reparse/junction points in
        /// the root, target, or every ancestor traversed to the target.
        /// </summary>
        internal static bool IsSafeRetentionPathForTests(string trustedRoot, string targetPath)
        {
            return IsSafeRetentionPath(trustedRoot, targetPath);
        }

        private static bool IsSafeRetentionPath(string trustedRoot, string targetPath)
        {
            try
            {
                var rootFull = Path.GetFullPath(trustedRoot);
                var targetFull = Path.GetFullPath(targetPath);
                var rootTrimmed = rootFull.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                var targetTrimmed = targetFull.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                if (string.Equals(rootTrimmed, targetTrimmed,
                        StringComparison.OrdinalIgnoreCase))
                    return false;
                var rootPrefix = rootTrimmed + Path.DirectorySeparatorChar;
                if (!targetFull.StartsWith(rootPrefix,
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(
                        Path.GetDirectoryName(targetTrimmed).TrimEnd(
                            Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar),
                        rootTrimmed,
                        StringComparison.OrdinalIgnoreCase))
                    return false;

                if (IsReparsePoint(rootFull)) return false;
                var cursor = targetFull;
                while (!string.IsNullOrWhiteSpace(cursor))
                {
                    if (IsReparsePoint(cursor)) return false;
                    var parent = Path.GetDirectoryName(cursor.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar));
                    if (string.IsNullOrWhiteSpace(parent) ||
                        string.Equals(parent, cursor,
                            StringComparison.OrdinalIgnoreCase))
                        break;
                    cursor = parent;
                    if (string.Equals(cursor.TrimEnd(
                            Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar),
                        rootTrimmed,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        if (IsReparsePoint(cursor)) return false;
                        break;
                    }
                }
                return true;
            }
            catch { return false; }
        }

        private static bool IsReparsePoint(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch { return true; }
        }

        /// <summary>
        /// Detaches the producer from this recorder and schedules the seal,
        /// spill replay, and cleanup shutdown on a background worker.  This is
        /// intentionally non-blocking so StopAll cannot inherit slow-disk
        /// latency from the telemetry persistence path.
        /// </summary>
        public void DetachAndShutdown()
        {
            RequestShutdown();
            StartShutdownWorker();
        }

        public void Dispose()
        {
            RequestShutdown();
            StartShutdownWorker();
            var shutdown = _shutdownTask;
            if (shutdown == null) return;
            // Final application disposal has a bounded wait. The worker keeps
            // running after this return when a slow filesystem exceeds the
            // bound, preserving .tmp/spill evidence for the next process.
            try
            {
                if (!shutdown.Wait(TimeSpan.FromSeconds(5)))
                    _log.Warn("程控电源遥测后台退出超过5秒，保留tmp/spill由下次启动恢复。", "程控电源");
            }
            catch (AggregateException ex)
            {
                _log.Warn($"程控电源遥测后台退出异常：{ex.GetBaseException().Message}", "程控电源");
            }
        }

        private void RequestShutdown()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (_gate) _closing = true;
            try { _steadyQueue.CompleteAdding(); } catch { }
            try { _promotionQueue.CompleteAdding(); } catch { }
            SignalWriter();
        }

        private void StartShutdownWorker()
        {
            if (Interlocked.CompareExchange(ref _shutdownRequested, 1, 0) != 0)
                return;
            _shutdownTask = Task.Run(ShutdownCore);
        }

        private void ShutdownCore()
        {
            var writerCompleted = false;
            try
            {
                if (!_promotionTask.Wait(TimeSpan.FromSeconds(5)))
                    _log.Warn("程控电源遥测事件推广线程未在退出窗口内完成；保留待处理spill队列。", "程控电源");
                if (!_spoolTask.Wait(TimeSpan.FromSeconds(5)))
                    _log.Warn("程控电源遥测spill线程未在退出窗口内完成；主进程继续退出。", "程控电源");
                SignalWriter();
                writerCompleted = _writerTask.Wait(TimeSpan.FromSeconds(5));
                if (!writerCompleted)
                    _log.Warn($"程控电源遥测文件未能在退出前完成封口：{FilePath}", "程控电源");

                if (writerCompleted)
                {
                    try { _cleanupQueue.CompleteAdding(); } catch { }
                    if (!_cleanupTask.Wait(TimeSpan.FromSeconds(2)))
                        _log.Warn("程控电源遥测清理线程未在退出窗口内完成；保留其待处理封存段。", "程控电源");
                }
            }
            catch (Exception ex)
            {
                try { _log.Warn($"程控电源遥测退出协调异常：{ex.Message}", "程控电源"); }
                catch { }
            }
            finally
            {
                Interlocked.Exchange(ref _deferredLogStopping, 1);
                _deferredLogWake.Set();
                try { _deferredLogTask.Wait(TimeSpan.FromSeconds(1)); } catch { }

                // Never dispose synchronization handles while a timed-out
                // writer/cleanup worker may still reference them.
                if (writerCompleted && _writerTask.IsCompleted)
                {
                    try { _steadyQueue.Dispose(); } catch { }
                    if (_cleanupTask.IsCompleted)
                    {
                        try { _cleanupQueue.Dispose(); } catch { }
                    }
                    try { _wake.Dispose(); } catch { }
                }
                if (_deferredLogTask.IsCompleted)
                {
                    try { _deferredLogWake.Dispose(); } catch { }
                }
            }
        }

        private static readonly string Header =
            "Utc,MonotonicTicks,SupplyId,ElectricalGroup,Cycle,Stage,Connected,Output," +
            "VSet,ISet,OVP,OCP,VOut,IOut,POut,CV,CC,VoltageLimited,CurrentLimited," +
                "PowerLimited,ProtectionTripped,OperationStatus,QuestionableStatus,EventFlags,EventCode,Error,Sequence";

        private static string ToCsvLine(Row row)
        {
            var item = row.Telemetry;
            var s = item.Snapshot;
            var sb = new StringBuilder(512);
            sb.Append(item.TimestampUtc.ToString("O", CultureInfo.InvariantCulture)).Append(',')
                .Append(item.MonotonicTicks).Append(',')
                .Append(item.SupplyId).Append(',')
                .Append(item.ElectricalGroupId).Append(',')
                .Append(row.CycleNumber).Append(',')
                .Append(Csv(row.Stage)).Append(',')
                .Append(s != null && s.IsConnected).Append(',')
                .Append(s != null && s.OutputEnabled).Append(',')
                .Append(Num(s?.SetVoltage)).Append(',')
                .Append(Num(s?.SetCurrent)).Append(',')
                .Append(Num(s?.Ovp)).Append(',')
                .Append(Num(s?.Ocp)).Append(',')
                .Append(Num(s?.MeasuredVoltage)).Append(',')
                .Append(Num(s?.MeasuredCurrent)).Append(',')
                .Append(Num(s?.MeasuredPower)).Append(',')
                .Append(s != null && s.IsConstantVoltage).Append(',')
                .Append(s != null && s.IsConstantCurrent).Append(',')
                .Append(s != null && s.IsVoltageLimited).Append(',')
                .Append(s != null && s.IsCurrentLimited).Append(',')
                .Append(s != null && s.IsPowerLimited).Append(',')
                .Append(s != null && s.ProtectionTripped).Append(',')
                .Append(s?.OperationStatus ?? 0).Append(',')
                .Append(s?.QuestionableStatus ?? 0).Append(',')
                .Append(Csv(item.EventFlags.ToString())).Append(',')
                .Append(Csv(item.EventCode)).Append(',')
                .Append(Csv(item.Error)).Append(',')
                .Append(row.Sequence);
            return sb.ToString();
        }

        private string BuildManifest(
            string sealedPath,
            DateTime startUtc,
            DateTime endUtc,
            long rows,
            long steadyRows,
            long eventRows,
            IEnumerable<string> eventWindows,
            long bytes,
            long drops,
            long eventDrops,
            string hash)
        {
            var windows = (eventWindows ?? Enumerable.Empty<string>()).ToArray();
            return "{" +
                "\"schema\":1," +
                "\"kind\":\"PowerSupplyTelemetry\"," +
                "\"fileName\":\"" + EscapeJson(Path.GetFileName(sealedPath)) + "\"," +
                "\"startedUtc\":\"" + EscapeJson(startUtc.ToUniversalTime().ToString("O")) + "\"," +
                "\"endedUtc\":\"" + EscapeJson(endUtc.ToUniversalTime().ToString("O")) + "\"," +
                "\"sealedUtc\":\"" + EscapeJson(endUtc.ToUniversalTime().ToString("O")) + "\"," +
                "\"mode\":\"" + _policy.Mode + "\"," +
                "\"steadyRateHz\":" + _policy.SteadyRateHz + "," +
                "\"eventRateHz\":" + _policy.EventRateHz + "," +
                "\"preEventSeconds\":" + _policy.PreEventSeconds + "," +
                "\"postEventSeconds\":" + _policy.PostEventSeconds + "," +
                "\"retentionDays\":" + _policy.RetentionDays + "," +
                "\"rotateHours\":" + _policy.RotateHours + "," +
                "\"rotateBytes\":" + _policy.RotateBytes + "," +
                "\"rawRotateBytes\":" + _policy.RawRotateBytes + "," +
                "\"rows\":" + rows + "," +
                "\"steadyRows\":" + steadyRows + "," +
                "\"eventRows\":" + eventRows + "," +
                "\"eventWindowCount\":" + windows.Length + "," +
                "\"eventWindowIds\":[" + string.Join(",", windows.Select(x => "\"" + EscapeJson(x) + "\"")) + "]," +
                "\"bytes\":" + bytes + "," +
                "\"dropCount\":" + drops + "," +
                "\"eventDropped\":" + eventDrops + "," +
                "\"runId\":\"" + EscapeJson(_runId) + "\"," +
                "\"runEpoch\":" + _runEpoch + "," +
                "\"sha256\":\"" + hash + "\"}";
        }

        private static string JsonValue(string json, string name)
        {
            var marker = "\"" + name + "\":\"";
            var start = json.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return string.Empty;
            start += marker.Length;
            var end = json.IndexOf('"', start);
            return end < 0 ? string.Empty : json.Substring(start, end - start);
        }

        private static long JsonLong(string json, string name)
        {
            var marker = "\"" + name + "\":";
            var start = json.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return -1;
            start += marker.Length;
            var end = start;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-')) end++;
            return long.TryParse(
                       json.Substring(start, end - start),
                       NumberStyles.Integer,
                       CultureInfo.InvariantCulture,
                       out var value)
                ? value
                : -1;
        }

        private static string TryInferRunId(string path)
        {
            var stem = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
            var separator = stem.LastIndexOf('_');
            if (separator < 0 || separator == stem.Length - 1) return string.Empty;
            var candidate = stem.Substring(separator + 1);
            return Guid.TryParseExact(candidate, "N", out _) ? candidate : string.Empty;
        }

        private static string ComputeSha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static TelemetryPersistencePolicy ClonePolicy(TelemetryPersistencePolicy source)
        {
            source = source ?? new TelemetryPersistencePolicy();
            return new TelemetryPersistencePolicy
            {
                Mode = source.Mode,
                SteadyRateHz = source.SteadyRateHz,
                EventRateHz = source.EventRateHz,
                PreEventSeconds = source.PreEventSeconds,
                PostEventSeconds = source.PostEventSeconds,
                RetentionDays = source.RetentionDays,
                RotateHours = source.RotateHours,
                RotateBytes = source.RotateBytes,
                RawRotateBytes = source.RawRotateBytes
            };
        }

        private static string Num(double? value) =>
            value.HasValue ? value.Value.ToString("0.######", CultureInfo.InvariantCulture) : string.Empty;

        private static string Csv(string value)
        {
            value = value ?? string.Empty;
            return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
                ? "\"" + value.Replace("\"", "\"\"") + "\""
                : value;
        }

        private static string EscapeJson(string value) =>
            (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");

        private sealed class Row
        {
            public Row(
                long sequence,
                DateTime admissionUtc,
                PowerSupplyTelemetry telemetry,
                int cycleNumber,
                string stage,
                bool isEventSample,
                string eventWindowId)
            {
                Sequence = sequence;
                AdmissionUtc = admissionUtc;
                Telemetry = telemetry;
                CycleNumber = cycleNumber;
                Stage = stage;
                IsEventSample = isEventSample;
                EventWindowId = eventWindowId ?? string.Empty;
            }

            public long Sequence { get; }
            public DateTime AdmissionUtc { get; }
            public PowerSupplyTelemetry Telemetry { get; }
            public int CycleNumber { get; }
            public string Stage { get; }
            public bool IsEventSample { get; }
            public string EventWindowId { get; }

            public Row AsEventSample() =>
                IsEventSample
                    ? this
                    : new Row(Sequence, AdmissionUtc, Telemetry, CycleNumber, Stage, true, EventWindowId);

            public Row WithEventWindow(string eventWindowId) =>
                new Row(Sequence, AdmissionUtc, Telemetry, CycleNumber, Stage, true, eventWindowId);
        }

        private sealed class EventRequest
        {
            public EventRequest(
                int supplyId,
                string windowId,
                DateTime eventUtc,
                Row triggerRow,
                bool promotePreWindow)
            {
                SupplyId = supplyId;
                WindowId = windowId ?? string.Empty;
                EventUtc = eventUtc;
                TriggerRow = triggerRow;
                PromotePreWindow = promotePreWindow;
            }

            public int SupplyId { get; }
            public string WindowId { get; }
            public DateTime EventUtc { get; }
            public Row TriggerRow { get; }
            public bool PromotePreWindow { get; }
        }

        private sealed class SpoolRecord
        {
            public SpoolRecord(Row row) { Row = row; }
            public Row Row { get; }
        }
    }
}
