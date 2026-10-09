using System;
using System.IO;
using System.Security.Principal;

namespace MTTFTest.Watchdog.Protocol
{
    // One instance owns one sealed command and its exact native process handle.
    // The service retains this instance while polling the same durable stage.
    public sealed class IndependentSafetyWorkerOperation : IDisposable
    {
        private readonly IndependentSafetyWorkerCommand _command;
        private readonly string _commandHash, _receiptPath;
        private readonly IndependentBoundedWorker _worker;
        private IndependentOperationResult _result = IndependentOperationResult.Pending;
        private bool _disposed;
        public string Detail { get; private set; }
        public int WorkerPid => _worker.ProcessId;
        public long WorkerStartUtcTicks => _worker.StartUtcTicks;

        public IndependentSafetyWorkerOperation(string executable, string executableSha256, string commandPath)
        {
            using (var identity = WindowsIdentity.GetCurrent())
                if (!identity.IsSystem) throw new UnauthorizedAccessException("IndependentSafetyOperationRequiresSystem");
            IndependentProtectedFiles.RequireTrustedFile(executable);
            IndependentProtectedFiles.RequireTrustedFile(commandPath);
            if (executableSha256?.Length != 64 ||
                !string.Equals(SupervisorProtocol.ComputeSha256(executable), executableSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("IndependentSafetyExecutableHashMismatch");
            _commandHash = SupervisorProtocol.ComputeSha256(commandPath);
            _command = BoundedJson.Read<IndependentSafetyWorkerCommand>(commandPath);
            var now = DateTime.UtcNow.Ticks;
            _command.Validate(now);
            if (!string.Equals(SupervisorProtocol.ComputeSha256(commandPath), _commandHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("IndependentSafetyCommandChanged");
            _receiptPath = Path.GetFullPath(commandPath) + ".receipt.json";
            if (File.Exists(_receiptPath)) throw new InvalidOperationException("IndependentSafetyReceiptMustBeNew");
            var remainingMs = (int)Math.Min(300000, Math.Max(1,
                TimeSpan.FromTicks(_command.DeadlineUtcTicks - now).TotalMilliseconds));
            _worker = new IndependentBoundedWorker(Path.GetFullPath(executable),
                "--independent-stage \"" + Path.GetFullPath(commandPath) + "\"",
                Path.GetDirectoryName(Path.GetFullPath(executable)), remainingMs);
            Detail = "IndependentSafetyWorkerStarted";
        }

        public IndependentOperationResult Poll(long now)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(IndependentSafetyWorkerOperation));
            if (_result != IndependentOperationResult.Pending) return _result;
            try
            {
                if (now < _command.IssuedUtcTicks || now >= _command.DeadlineUtcTicks)
                    return Fail("IndependentSafetyExternalDeadlineExceeded");
                var status = _worker.Poll();
                if (status == IndependentWorkerState.Running) return _result;
                if (status != IndependentWorkerState.Completed)
                    return Fail("IndependentSafetyWorkerFailed:" + status + ":" + _worker.ExitCode);
                // Completion is accepted only after the worker exited and the
                // job closed. A positive file from a still-running worker is ignored.
                IndependentProtectedFiles.RequireTrustedFile(_receiptPath);
                var receipt = BoundedJson.Read<IndependentSafetyWorkerReceipt>(_receiptPath);
                if (!receipt.MatchesCurrent(_command, _commandHash, _worker.ProcessId, _worker.StartUtcTicks, now))
                    return Fail("IndependentSafetyReceiptBindingRejected");
                Detail = receipt.Detail;
                _result = IndependentOperationResult.Completed;
                return _result;
            }
            catch (Exception error)
            {
                return Fail("IndependentSafetyObservationFailed:" + error.GetType().Name + ":" + error.Message);
            }
        }

        private IndependentOperationResult Fail(string reason)
        {
            _worker.Dispose();
            Detail = reason.Length <= 512 ? reason : reason.Substring(0, 512);
            _result = IndependentOperationResult.Failed;
            return _result;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _worker.Dispose();
        }
    }
}
