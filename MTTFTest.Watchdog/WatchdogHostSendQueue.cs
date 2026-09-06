using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MTTFTest.Watchdog
{
    internal sealed class WatchdogHostSendRequest
    {
        internal WatchdogHostSendRequest(string messageType, string payload, bool lifecycle)
        {
            MessageType = messageType ?? string.Empty;
            Payload = payload ?? string.Empty;
            Lifecycle = lifecycle;
            EnqueuedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        internal string MessageType { get; }
        internal string Payload { get; }
        internal bool Lifecycle { get; }
        internal long EnqueuedTimestamp { get; }
        internal readonly TaskCompletionSource<bool> Completion =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal sealed class WatchdogHostSendQueueOwner
    {
        internal const int NormalCapacity = 248;
        internal const int LifecycleCapacity = 8;

        private readonly object _gate = new object();
        private readonly Queue<WatchdogHostSendRequest> _lifecycle =
            new Queue<WatchdogHostSendRequest>();
        private readonly Queue<WatchdogHostSendRequest> _normal =
            new Queue<WatchdogHostSendRequest>();
        private int _accepting = 1;

        internal WatchdogHostSendQueueOwner(
            Stream pipe,
            StreamWriter writer,
            long connectionGeneration)
        {
            Pipe = pipe ?? throw new ArgumentNullException(nameof(pipe));
            Writer = writer ?? throw new ArgumentNullException(nameof(writer));
            ConnectionGeneration = connectionGeneration;
        }

        internal Stream Pipe { get; }
        internal StreamWriter Writer { get; }
        internal long ConnectionGeneration { get; }
        internal readonly SemaphoreSlim Signal = new SemaphoreSlim(0);
        internal readonly CancellationTokenSource Lifetime = new CancellationTokenSource();
        internal Task WorkerTask { get; set; }
        internal Task InflightWriteTask { get; set; }

        internal bool TryEnqueue(WatchdogHostSendRequest request)
        {
            if (request == null) return false;
            lock (_gate)
            {
                if (_accepting == 0) return false;
                var queue = request.Lifecycle ? _lifecycle : _normal;
                var capacity = request.Lifecycle ? LifecycleCapacity : NormalCapacity;
                if (queue.Count >= capacity) return false;
                queue.Enqueue(request);
            }
            try { Signal.Release(); }
            catch (ObjectDisposedException) { return false; }
            return true;
        }

        internal bool TryDequeue(out WatchdogHostSendRequest request)
        {
            lock (_gate)
            {
                if (_lifecycle.Count > 0)
                {
                    request = _lifecycle.Dequeue();
                    return true;
                }
                if (_normal.Count > 0)
                {
                    request = _normal.Dequeue();
                    return true;
                }
            }
            request = null;
            return false;
        }

        internal void StopAccepting()
        {
            Queue<WatchdogHostSendRequest> abandoned = null;
            lock (_gate)
            {
                if (Interlocked.Exchange(ref _accepting, 0) == 0) return;
                abandoned = new Queue<WatchdogHostSendRequest>(_lifecycle.Count + _normal.Count);
                while (_lifecycle.Count > 0) abandoned.Enqueue(_lifecycle.Dequeue());
                while (_normal.Count > 0) abandoned.Enqueue(_normal.Dequeue());
            }
            while (abandoned.Count > 0)
                abandoned.Dequeue().Completion.TrySetResult(false);
            try { Lifetime.Cancel(); } catch { }
            try { Signal.Release(); } catch { }
        }
    }
}
