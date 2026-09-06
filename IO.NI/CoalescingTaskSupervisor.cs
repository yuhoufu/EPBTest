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

        internal int ActiveCount
        {
            get
            {
                lock (_lifecycleGate) return _active.Count;
            }
        }

        internal int ActiveKeyCount
        {
            get
            {
                lock (_lifecycleGate) return _activeKeys.Count;
            }
        }

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
            Task[] tasks;
            var initialLifecycleError = false;
            lock (_lifecycleGate)
            {
                Interlocked.Exchange(ref _accepting, 0);
                tasks = _active.Values.ToArray();
                if (tasks.Length == 0 && !_activeKeys.IsEmpty)
                    initialLifecycleError = true;
                if (tasks.Length == 0 && !initialLifecycleError) return true;
            }
            if (initialLifecycleError)
            {
                ReportLifecycleError(
                    "DAQ后台任务监督状态异常：活动任务为空但业务键仍残留。");
                return false;
            }

            var deadline = Stopwatch.GetTimestamp() +
                           (long)(Math.Max(1, timeoutMs) / 1000d * Stopwatch.Frequency);
            while (true)
            {
                var remainingMs = (int)Math.Ceiling(
                    (deadline - Stopwatch.GetTimestamp()) * 1000d / Stopwatch.Frequency);
                if (remainingMs <= 0) return false;
                try
                {
                    Task.WaitAll(tasks, remainingMs);
                }
                catch (Exception ex)
                {
                    // Complete 会逐个读取并记录任务异常；继续确认活动表已清空。
                    ReportLifecycleError(
                        "DAQ后台任务排空观察到任务异常，将继续确认活动记录。",
                        ex);
                }

                var terminalLifecycleError = false;
                lock (_lifecycleGate)
                {
                    var activeCount = _active.Count;
                    var activeKeyCount = _activeKeys.Count;
                    if (activeCount == 0)
                    {
                        if (activeKeyCount != 0)
                            terminalLifecycleError = true;
                        else
                            return true;
                    }

                    if (terminalLifecycleError)
                    {
                        // Do not call the logger while holding the lifecycle
                        // gate; logger implementations may perform callbacks.
                        tasks = Array.Empty<Task>();
                    }
                    else
                    {
                        // Capture the next immutable task set while holding
                        // the lifecycle gate, then release the gate before
                        // waiting. Execute's finally removes the key and
                        // task in this same gate, so no observer can see a
                        // half-removed pair.
                        tasks = _active.Values.ToArray();
                    }
                }
                if (terminalLifecycleError)
                {
                    ReportLifecycleError(
                        "DAQ后台任务监督状态异常：活动任务已清空但业务键仍残留。");
                    return false;
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
                lock (_lifecycleGate)
                {
                    // Keep the key/task pair atomically visible to drain and
                    // diagnostics.  The key is logically removed first, but
                    // no reader can observe the intermediate state because
                    // both operations are protected by the same gate.
                    RemoveKey(key, id);
                    _active.TryRemove(id, out _);
                }
            }
        }

        private void RemoveKey(string key, long id)
        {
            ((ICollection<KeyValuePair<string, long>>)_activeKeys).Remove(
                new KeyValuePair<string, long>(key, id));
        }

        public void Dispose()
        {
            if (!StopAcceptingAndDrain(5000))
                ReportLifecycleError("DAQ后台任务监督器退出时未能在期限内排空。");
        }

        private void ReportLifecycleError(string message, Exception ex = null)
        {
            try { _log.Error(message, "AI", ex); }
            catch
            {
                // Logging is diagnostic only; never turn a lifecycle guard
                // failure into a second unobserved supervisor exception.
            }
        }
    }
}
