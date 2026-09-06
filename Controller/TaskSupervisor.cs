using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Config;

namespace Controller
{
    public sealed class TaskSupervisor
    {
        private readonly IAppLogger _log;
        private readonly ConcurrentDictionary<long, TaskSupervisorEntry> _active = new();
        private readonly ConcurrentDictionary<string, long> _recentFaults = new();
        private long _sequence;

        public TaskSupervisor(IAppLogger log)
        {
            _log = log ?? NullLogger.Instance;
        }

        public int ActiveCount => _active.Count;

        public void Observe(Task task, string operation, Guid runId, int channel = 0)
        {
            if (task == null) return;
            var id = Interlocked.Increment(ref _sequence);
            var entry = new TaskSupervisorEntry(
                id,
                string.IsNullOrWhiteSpace(operation) ? "Unnamed" : operation,
                runId,
                channel,
                DateTime.UtcNow,
                task);
            _active[id] = entry;
            _ = task.ContinueWith(
                completed => Complete(entry, completed),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        internal TaskSupervisorEntry[] Snapshot()
        {
            return _active.Values.OrderBy(x => x.Id).ToArray();
        }

        public async Task<bool> DrainAsync(int timeoutMs)
        {
            var deadline = Stopwatch.GetTimestamp() +
                           (long)(Math.Max(1, timeoutMs) / 1000d * Stopwatch.Frequency);
            while (true)
            {
                var completions = _active.Values
                    .Select(x => x.ObservationCompleted.Task)
                    .ToArray();
                if (completions.Length == 0) return true;
                var remainingMs = (int)Math.Ceiling(
                    (deadline - Stopwatch.GetTimestamp()) * 1000d / Stopwatch.Frequency);
                if (remainingMs <= 0) return false;
                var all = Task.WhenAll(completions);
                var winner = await Task.WhenAny(all, Task.Delay(remainingMs)).ConfigureAwait(false);
                if (winner != all) return false;
                await all.ConfigureAwait(false);
            }
        }

        private void Complete(TaskSupervisorEntry entry, Task completed)
        {
            try
            {
                if (!completed.IsFaulted) return;
                var aggregate = completed.Exception;
                var root = aggregate?.GetBaseException();
                var faultKey = $"{entry.Operation}:{root?.GetType().FullName}:{root?.Message}";
                var now = Stopwatch.GetTimestamp();
                if (!_recentFaults.TryAdd(faultKey, now))
                {
                    if (!_recentFaults.TryGetValue(faultKey, out var previous) ||
                        (now - previous) * 1000.0 / Stopwatch.Frequency < 10_000 ||
                        !_recentFaults.TryUpdate(faultKey, now, previous))
                        return;
                }
                if (_recentFaults.Count > 256)
                {
                    foreach (var item in _recentFaults)
                        if ((now - item.Value) * 1000.0 / Stopwatch.Frequency > 60_000)
                            _recentFaults.TryRemove(item.Key, out _);
                }

                _log.Error(
                    $"后台异步操作失败：Task={entry.Operation} RunId={entry.RunId:N} " +
                    $"EPB={entry.Channel} StartedUtc={entry.StartedUtc:O} " +
                    $"Error={root?.Message}",
                    "EPB",
                    root ?? aggregate);
            }
            catch
            {
                // 监督器本身绝不能把日志故障重新传播到线程池。
            }
            finally
            {
                entry.ObservationCompleted.TrySetResult(true);
                _active.TryRemove(entry.Id, out _);
            }
        }
    }

    internal sealed class TaskSupervisorEntry
    {
        internal TaskSupervisorEntry(
            long id,
            string operation,
            Guid runId,
            int channel,
            DateTime startedUtc,
            Task task)
        {
            Id = id;
            Operation = operation;
            RunId = runId;
            Channel = channel;
            StartedUtc = startedUtc;
            Task = task;
            ObservationCompleted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        internal long Id { get; }
        internal string Operation { get; }
        internal Guid RunId { get; }
        internal int Channel { get; }
        internal DateTime StartedUtc { get; }
        internal Task Task { get; }
        internal TaskCompletionSource<bool> ObservationCompleted { get; }
    }
}
