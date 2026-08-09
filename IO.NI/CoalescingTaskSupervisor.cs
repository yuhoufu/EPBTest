using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ILogger = Config.IAppLogger;
using NLogger = Config.NullLogger;

namespace IO.NI
{
    /// <summary>
    /// 监督非实时后台动作，并按业务键合并重复请求。
    /// 任务在启动前登记，完成回调始终读取 Exception，避免未观察异常和同根任务风暴。
    /// </summary>
    internal sealed class CoalescingTaskSupervisor : IDisposable
    {
        private readonly ILogger _log;
        private readonly ConcurrentDictionary<long, Task> _active =
            new ConcurrentDictionary<long, Task>();
        private readonly ConcurrentDictionary<string, long> _activeKeys =
            new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        private readonly object _lifecycleGate = new object();
        private long _sequence;
        private long _coalesced;
        private int _accepting = 1;

        internal CoalescingTaskSupervisor(ILogger log)
        {
            _log = log ?? NLogger.Instance;
        }

        internal int ActiveCount => _active.Count;

        internal int ActiveKeyCount => _activeKeys.Count;

        internal long CoalescedCount => Interlocked.Read(ref _coalesced);

        internal bool TryRun(string operationKey, Action action)
        {
            if (action == null) return false;
            lock (_lifecycleGate)
            {
                if (Volatile.Read(ref _accepting) == 0) return false;
                var key = string.IsNullOrWhiteSpace(operationKey) ? "Unnamed" : operationKey.Trim();
                var id = Interlocked.Increment(ref _sequence);
                if (!_activeKeys.TryAdd(key, id))
                {
                    Interlocked.Increment(ref _coalesced);
                    return false;
                }

                var task = new Task(
                    () => Execute(id, key, action),
                    CancellationToken.None,
                    TaskCreationOptions.DenyChildAttach);
                _active[id] = task;
                try
                {
                    task.Start(TaskScheduler.Default);
                    return true;
                }
                catch
                {
                    RemoveKey(key, id);
                    _active.TryRemove(id, out _);
                    throw;
                }
            }
        }

        internal bool StopAcceptingAndDrain(int timeoutMs)
        {
            lock (_lifecycleGate)
                Interlocked.Exchange(ref _accepting, 0);
            var deadline = Stopwatch.GetTimestamp() +
                           (long)(Math.Max(1, timeoutMs) / 1000d * Stopwatch.Frequency);
            while (true)
            {
                var tasks = _active.Values.ToArray();
                if (tasks.Length == 0) return true;
                var remainingMs = (int)Math.Ceiling(
                    (deadline - Stopwatch.GetTimestamp()) * 1000d / Stopwatch.Frequency);
                if (remainingMs <= 0) return false;
                try
                {
                    if (!Task.WaitAll(tasks, remainingMs)) return false;
                }
                catch
                {
                    // Complete 会逐个读取并记录任务异常；继续确认活动表已清空。
                }
            }
        }

        private void Execute(long id, string key, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                try
                {
                    _log.Error(
                        $"AI后台任务失败：Task={key} Error={ex.Message}",
                        "AI",
                        ex);
                }
                catch
                {
                    // 监督器本身不能重新制造未观察异常。
                }
            }
            finally
            {
                _active.TryRemove(id, out _);
                RemoveKey(key, id);
            }
        }

        private void RemoveKey(string key, long id)
        {
            ((ICollection<KeyValuePair<string, long>>)_activeKeys).Remove(
                new KeyValuePair<string, long>(key, id));
        }

        public void Dispose()
        {
            StopAcceptingAndDrain(5000);
        }
    }
}
