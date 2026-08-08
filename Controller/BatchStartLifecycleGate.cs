using System;
using System.Threading;
using System.Threading.Tasks;

namespace Controller
{
    /// <summary>
    /// 串行化批次启动的完整调用生命周期，并允许“重新开始”等待旧启动的 catch/finally 收尾彻底结束。
    /// </summary>
    internal sealed class BatchStartLifecycleGate
    {
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private int _activeOperations;

        public bool IsBusy => Volatile.Read(ref _activeOperations) != 0;

        public async Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken token)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            Interlocked.Increment(ref _activeOperations);
            var entered = false;
            try
            {
                await _gate.WaitAsync(token).ConfigureAwait(false);
                entered = true;
                return await operation().ConfigureAwait(false);
            }
            finally
            {
                if (entered) _gate.Release();
                Interlocked.Decrement(ref _activeOperations);
            }
        }

        public async Task JoinAsync(CancellationToken token)
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            _gate.Release();
        }
    }
}
