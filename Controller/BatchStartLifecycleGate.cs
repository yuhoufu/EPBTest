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

        public async Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken token)
        {
            if (operation == null) throw new ArgumentNullException(nameof(operation));
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                return await operation().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task JoinAsync(CancellationToken token)
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            _gate.Release();
        }
    }
}
