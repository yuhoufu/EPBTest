using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Controller
{
    /// <summary>
    /// 统一登记会影响重新开始的恢复任务。它不替代各恢复域自己的取消令牌，
    /// 但为 StopAll 提供一个共同的有界收口屏障，并用 RunEpoch 隔离迟到旧任务。
    /// </summary>
    internal sealed class RecoveryTaskRegistry
    {
        private sealed class Entry
        {
            internal long Id;
            internal string Operation;
            internal long RunEpoch;
            internal Task Task;
        }

        private readonly ConcurrentDictionary<long, Entry> _active = new();
        private long _sequence;

        internal int ActiveCount => _active.Count;

        internal void Track(Task task, string operation, long runEpoch)
        {
            if (task == null || !IsRecoveryOperation(operation)) return;
            var entry = new Entry
            {
                Id = Interlocked.Increment(ref _sequence),
                Operation = operation ?? "Recovery",
                RunEpoch = runEpoch,
                Task = task
            };
            _active[entry.Id] = entry;
            _ = task.ContinueWith(
                completed => _active.TryRemove(entry.Id, out var ignored),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        internal async Task<string[]> DrainThroughEpochAsync(long revokedRunEpoch, int timeoutMs)
        {
            var deadline = Stopwatch.GetTimestamp() +
                           (long)(Math.Max(1, timeoutMs) / 1000d * Stopwatch.Frequency);
            while (true)
            {
                var pending = _active.Values
                    .Where(entry => entry.RunEpoch <= revokedRunEpoch && !entry.Task.IsCompleted)
                    .ToArray();
                if (pending.Length == 0) return Array.Empty<string>();
                var remaining = (int)Math.Ceiling(
                    (deadline - Stopwatch.GetTimestamp()) * 1000d / Stopwatch.Frequency);
                if (remaining <= 0)
                    return pending.Select(entry => entry.Operation).Distinct().OrderBy(x => x).ToArray();
                var all = Task.WhenAll(pending.Select(entry => entry.Task));
                if (await Task.WhenAny(all, Task.Delay(remaining)).ConfigureAwait(false) != all)
                    return pending.Where(entry => !entry.Task.IsCompleted)
                        .Select(entry => entry.Operation).Distinct().OrderBy(x => x).ToArray();
                try { await all.ConfigureAwait(false); } catch { }
            }
        }

        internal static bool IsRecoveryOperation(string operation)
        {
            if (string.IsNullOrWhiteSpace(operation)) return false;
            return operation.IndexOf("Recovery", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   operation.IndexOf("SelfHealing", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   operation.IndexOf("Finalize", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   operation.IndexOf("Restart", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   operation.IndexOf("FaultHandling", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
