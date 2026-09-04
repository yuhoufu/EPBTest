using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PowerSupply.Core
{
    public sealed partial class PswTcpClient : IPswClientRetirementEvidence
    {
        private readonly object _lifecycleGate = new object();
        private bool _retired;
        private bool _gateDisposed;
        private int _activeOperations;
        private int _pendingTransportTasks;
        private string _releaseFailure = string.Empty;

        public PswClientRetirementSnapshot CaptureRetirement()
        {
            lock (_lifecycleGate)
                return new PswClientRetirementSnapshot
                {
                    RetirementRequested = _retired,
                    TransportReleased = _retired && _client == null && _stream == null && _writer == null &&
                                        _pendingTransportTasks == 0 && _releaseFailure.Length == 0,
                    OperationsExited = _retired && _activeOperations == 0,
                    PendingOperations = _activeOperations,
                    PendingTransportTasks = _pendingTransportTasks,
                    Failure = _releaseFailure
                };
        }

        private IDisposable RegisterOperation()
        {
            lock (_lifecycleGate)
            {
                ThrowIfRetired();
                _activeOperations++;
                return new OperationLease(this);
            }
        }

        private void ThrowIfRetired()
        {
            lock (_lifecycleGate)
                if (_retired) throw new ObjectDisposedException(nameof(PswTcpClient));
        }

        private void TrackTransportTask(Task task)
        {
            // 调用时持有一个 OperationLease；即使 Dispose 此时到达，也不可能
            // 在登记底层任务之前把整个客户端宣称为已经退出。
            lock (_lifecycleGate) _pendingTransportTasks++;
            _ = task.ContinueWith(completed =>
            {
                if (completed.IsFaulted) { var ignored = completed.Exception; }
                lock (_lifecycleGate) _pendingTransportTasks--;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private void CloseTransportLocked()
        {
            // 先关闭 socket 中断在途 I/O，再释放包装器；不能先同步 Flush 一个
            // 仍连接且不响应的远端。失败锁存，不以字段清空推断成功。
            try { _client?.Close(); }
            catch (Exception ex) { RecordReleaseFailure("TcpClient.Close", ex); }
            try { _stream?.Dispose(); }
            catch (Exception ex) { RecordReleaseFailure("NetworkStream.Dispose", ex); }
            // socket/stream 已关闭时，StreamWriter 的最后 Flush 可能抛异常。
            // 物理传输退出取决于上面两个关闭结果与底层任务实际结束，而不是 Flush 成功。
            try { _writer?.Dispose(); } catch { }
            _writer = null;
            _stream = null;
            _client = null;
            _lastProtectionSetpointReadUtc = DateTime.MinValue;
        }

        private void RecordReleaseFailure(string operation, Exception exception)
        {
            if (_releaseFailure.Length != 0) return;
            var message = operation + ": " + exception.Message;
            _releaseFailure = message.Length <= 512 ? message : message.Substring(0, 512);
        }

        private void RequirePreviousTransportExited()
        {
            ThrowIfRetired();
            if (_pendingTransportTasks != 0 || _releaseFailure.Length != 0)
                throw new IOException("PreviousPswTransportNotReleased");
        }

        private void DisposeGateWhenQuiescent()
        {
            if (_retired && _activeOperations == 0 && !_gateDisposed)
            {
                _gateDisposed = true;
                _gate.Dispose();
            }
        }

        private sealed class OperationLease : IDisposable
        {
            private PswTcpClient _owner;
            internal OperationLease(PswTcpClient owner) { _owner = owner; }
            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner == null) return;
                lock (owner._lifecycleGate)
                {
                    owner._activeOperations--;
                    owner.DisposeGateWhenQuiescent();
                }
            }
        }
    }
}
