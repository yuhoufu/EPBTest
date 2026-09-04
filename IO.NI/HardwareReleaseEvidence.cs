using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace IO.NI
{
    /// <summary>
    /// 进程内资源退出事实，不是物理断能证明。Dispose 已返回、对象字段为空或
    /// 执行授权被撤销，都不能替代 NativeResourcesReleased / CallbacksIsolated。
    /// </summary>
    public sealed class HardwareReleaseSnapshot
    {
        internal HardwareReleaseSnapshot(bool requested, bool nativeReleased,
            bool callbacksIsolated, int pendingCallbacks, string failure)
        {
            ReleaseRequested = requested;
            NativeResourcesReleased = nativeReleased;
            CallbacksIsolated = callbacksIsolated;
            PendingCallbacks = pendingCallbacks;
            Failure = failure ?? string.Empty;
        }

        public bool ReleaseRequested { get; }
        public bool NativeResourcesReleased { get; }
        public bool CallbacksIsolated { get; }
        public int PendingCallbacks { get; }
        public string Failure { get; }
        public bool FullyReleased => ReleaseRequested && NativeResourcesReleased &&
                                     CallbacksIsolated && Failure.Length == 0;
    }

    public static class HardwareReleaseBarrier
    {
        /// <summary>
        /// 只观察同一组旧对象，不执行 Dispose、不清引用、不创建替代资源。
        /// 调用者必须在成功后才丢弃旧所有者；失败交给恢复内核升级处理。
        /// </summary>
        public static async Task RequireReleasedAsync(Func<HardwareReleaseSnapshot[]> capture,
            int timeoutMs, CancellationToken token)
        {
            if (capture == null) throw new ArgumentNullException(nameof(capture));
            if (timeoutMs < 1 || timeoutMs > 10000) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
            var clock = Stopwatch.StartNew();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var snapshots = capture();
                if (snapshots == null || snapshots.Any(snapshot => snapshot == null))
                    throw new IOException("HardwareReleaseEvidenceMissing;ProcessReplacementRequired");
                var failed = snapshots.FirstOrDefault(snapshot => snapshot.Failure.Length != 0);
                if (failed != null)
                    throw new IOException("HardwareReleaseFailed;ProcessReplacementRequired;" + failed.Failure);
                if (snapshots.All(snapshot => snapshot.FullyReleased)) return;
                if (clock.ElapsedMilliseconds >= timeoutMs)
                    throw new IOException("HardwareReleaseNotConfirmed;ProcessReplacementRequired");
                await Task.Delay((int)Math.Min(25, Math.Max(1, timeoutMs - clock.ElapsedMilliseconds)), token)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 一个硬件宿主实例只使用一个单调证据对象。原生释放失败永久锁存；异步退出
    /// 则保留活动登记，直到真正退出。轮询或等待超时不能清空活动登记或重新授权。
    /// </summary>
    internal sealed class HardwareReleaseEvidence
    {
        private readonly object _gate = new object();
        private bool _requested;
        private bool _nativeReleased;
        private bool _callbacksClosed;
        private int _pendingCallbacks;
        private string _failure = string.Empty;

        internal void RequestRelease()
        {
            lock (_gate) _requested = true;
        }

        internal void CompleteNativeRelease()
        {
            lock (_gate)
            {
                if (!_requested) throw new InvalidOperationException("Release was not requested.");
                _nativeReleased = true;
            }
        }

        internal void CloseCallbackAdmission()
        {
            lock (_gate) _callbacksClosed = true;
        }

        internal IDisposable RegisterCallback()
        {
            lock (_gate)
            {
                if (_callbacksClosed)
                    throw new ObjectDisposedException(nameof(HardwareReleaseEvidence));
                _pendingCallbacks++;
                return new CallbackLease(this);
            }
        }

        internal void RecordFailure(string operation, Exception exception)
        {
            lock (_gate)
            {
                // 保留第一个失败，不随重复 Dispose 或后续成功被清除，也不形成日志风暴。
                if (_failure.Length != 0) return;
                var detail = (operation ?? "Release") + ": " +
                             (exception?.Message ?? "Unconfirmed");
                _failure = detail.Length <= 512 ? detail : detail.Substring(0, 512);
            }
        }

        internal HardwareReleaseSnapshot Capture()
        {
            lock (_gate)
                return new HardwareReleaseSnapshot(_requested,
                    _requested && _nativeReleased && _failure.Length == 0,
                    _requested && _callbacksClosed && _pendingCallbacks == 0,
                    _pendingCallbacks, _failure);
        }

        private sealed class CallbackLease : IDisposable
        {
            private HardwareReleaseEvidence _owner;
            internal CallbackLease(HardwareReleaseEvidence owner) { _owner = owner; }
            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner == null) return;
                lock (owner._gate) owner._pendingCallbacks--;
            }
        }
    }
}
