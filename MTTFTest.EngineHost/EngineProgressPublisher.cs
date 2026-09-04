using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataOperation;

namespace MTTFTest.EngineHost
{
    /// <summary>Small memory-only event sink; no UI, wall-clock arithmetic or file I/O.</summary>
    internal sealed class EngineRunTimeClock
    {
        private readonly object _gate = new object();
        private readonly long[] _ticks;
        private readonly long[] _last = new long[12];
        private readonly bool[] _active = new bool[12];
        private readonly Func<long> _timestamp;
        private readonly long _frequency;

        internal EngineRunTimeClock(long[] baseline, Func<long> timestamp = null, long frequency = 0)
        {
            if (baseline?.Length != 12 || baseline.Any(value => value < 0)) throw new ArgumentException("RunTimeBaselineInvalid");
            _ticks = (long[])baseline.Clone(); _timestamp = timestamp ?? Stopwatch.GetTimestamp;
            _frequency = frequency > 0 ? frequency : Stopwatch.Frequency;
        }

        internal void SetActive(int channel, bool active)
        {
            if (channel < 1 || channel > 12) return;
            lock (_gate)
            {
                Advance(channel - 1, _timestamp());
                _active[channel - 1] = active;
            }
        }

        internal long[] Capture()
        {
            lock (_gate)
            {
                var now = _timestamp();
                for (var i = 0; i < 12; i++) Advance(i, now);
                return (long[])_ticks.Clone();
            }
        }

        private void Advance(int index, long now)
        {
            if (_active[index] && now >= _last[index])
                _ticks[index] = checked(_ticks[index] + (long)((now - _last[index]) * (decimal)TimeSpan.TicksPerSecond / _frequency));
            _last[index] = Math.Max(_last[index], now);
        }
    }

    /// <summary>
    /// Single bounded background publisher. Requests coalesce; control observers only
    /// wake it. A blocked disk never blocks telemetry and retains ownership on shutdown.
    /// </summary>
    internal sealed class EngineProgressPublisher
    {
        private readonly Func<EpbDurableProgress[]> _read;
        private readonly Action<EpbDurableProgress[]> _publish;
        private readonly Action<Exception> _fault;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly SemaphoreSlim _wake = new SemaphoreSlim(0, 1);
        private TaskCompletionSource<bool> _changed = NewSignal();
        private readonly Task _worker;
        private EpbDurableProgress[] _latest;
        private Exception _error;
        private long _requested;
        private long _published;
        private long _lastPublishedTicks;

        internal EngineProgressPublisher(Func<EpbDurableProgress[]> read, Action<EpbDurableProgress[]> publish, Action<Exception> fault)
        {
            _read = read ?? throw new ArgumentNullException(nameof(read));
            _publish = publish ?? throw new ArgumentNullException(nameof(publish));
            _fault = fault ?? throw new ArgumentNullException(nameof(fault));
            _worker = Task.Run(RunAsync);
        }

        internal bool Healthy => Volatile.Read(ref _error) == null &&
            Interlocked.Read(ref _lastPublishedTicks) > 0 &&
            Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastPublishedTicks) <= 3L * Stopwatch.Frequency;
        internal EpbDurableProgress[] Snapshot() => Clone(Volatile.Read(ref _latest));
        internal void Request() { if (_stop.IsCancellationRequested) return; try { _wake.Release(); } catch (SemaphoreFullException) { } }

        internal async Task FlushAsync(int timeoutMs, CancellationToken token)
        {
            if (_stop.IsCancellationRequested) throw new InvalidOperationException("ProgressPublisherStopped");
            var ticket = Interlocked.Increment(ref _requested);
            Request();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(Math.Max(1, timeoutMs));
            var expired = Task.Delay(Timeout.Infinite, deadline.Token);
            try
            {
                while (Interlocked.Read(ref _published) < ticket)
                {
                    if (_stop.IsCancellationRequested) throw new InvalidOperationException("ProgressPublisherStopped");
                    var signal = Volatile.Read(ref _changed);
                    var error = Volatile.Read(ref _error);
                    if (error != null) throw new InvalidOperationException("ProgressProjectionFailed", error);
                    if (Interlocked.Read(ref _published) >= ticket) return;
                    if (await Task.WhenAny(signal.Task, expired).ConfigureAwait(false) != signal.Task)
                    {
                        token.ThrowIfCancellationRequested();
                        throw new TimeoutException("ProgressProjectionDeadline");
                    }
                }
            }
            finally { deadline.Cancel(); }
        }

        internal async Task<bool> StopAsync(int timeoutMs)
        {
            _stop.Cancel(); Request();
            return await Task.WhenAny(_worker, Task.Delay(Math.Max(1, timeoutMs))).ConfigureAwait(false) == _worker;
        }

        private async Task RunAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await _wake.WaitAsync(500, _stop.Token).ConfigureAwait(false);
                    _stop.Token.ThrowIfCancellationRequested();
                    var previousPublished = Interlocked.Read(ref _lastPublishedTicks);
                    var since = previousPublished == 0 ? 500 :
                        (Stopwatch.GetTimestamp() - previousPublished) * 1000L / Stopwatch.Frequency;
                    if (since < 500 && Interlocked.Read(ref _requested) <= Interlocked.Read(ref _published))
                        await Task.Delay((int)Math.Max(1, 500 - since), _stop.Token).ConfigureAwait(false);
                    var ticket = Interlocked.Read(ref _requested);
                    var sample = _read();
                    if (sample?.Length != 12 || sample.Where((item, i) => item == null || item.Channel != i + 1 ||
                        item.FormalCompleted < 0 || item.MechanicalCompleted < item.FormalCompleted || item.RunTimeTicks < 0).Any())
                        throw new InvalidOperationException("ProgressSampleInvalid");
                    var previous = Volatile.Read(ref _latest);
                    if (previous == null || ticket > Interlocked.Read(ref _published) || sample.Where((item, i) =>
                        item.FormalCompleted != previous[i].FormalCompleted || item.MechanicalCompleted != previous[i].MechanicalCompleted ||
                        item.RunTimeTicks != previous[i].RunTimeTicks).Any())
                        _publish(sample);
                    Volatile.Write(ref _latest, Clone(sample));
                    Interlocked.Exchange(ref _lastPublishedTicks, Stopwatch.GetTimestamp());
                    Interlocked.Exchange(ref _published, ticket);
                    Signal();
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Volatile.Write(ref _error, ex); Signal();
                try { _fault(ex); } catch { } // Observation only; no retry/restart/owner here.
            }
            finally { Signal(); }
        }

        private void Signal() => Interlocked.Exchange(ref _changed, NewSignal()).TrySetResult(true);
        private static TaskCompletionSource<bool> NewSignal() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private static EpbDurableProgress[] Clone(EpbDurableProgress[] values) => values?.Select(value => new EpbDurableProgress
        { Channel = value.Channel, FormalCompleted = value.FormalCompleted, MechanicalCompleted = value.MechanicalCompleted, RunTimeTicks = value.RunTimeTicks }).ToArray();
    }
}
