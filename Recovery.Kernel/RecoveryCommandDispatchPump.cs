using System;
using System.Threading.Tasks;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Recovery.Kernel
{
    /// <summary>Bounded execution lanes; never chooses an intent, retry or recovery budget.</summary>
    public sealed class RecoveryCommandDispatchPump
    {
        private readonly object _gate = new object();
        private readonly Action<RecoveryCommand> _execute;
        private readonly Action<RecoveryCommand, Exception> _failed;
        private Task _normal = Task.CompletedTask;
        private Task _safety = Task.CompletedTask;
        private string _lastNormal;
        private string _lastSafety;
        private bool _stopped;

        public RecoveryCommandDispatchPump(Action<RecoveryCommand> execute, Action<RecoveryCommand, Exception> failed)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _failed = failed ?? throw new ArgumentNullException(nameof(failed));
        }

        public bool TryDispatch(RecoveryCommand command)
        {
            if (command?.IsStructurallyValid() != true) return false;
            var safety = EngineHostProtocol.IsPrioritySafetyCommand(command.Kind);
            lock (_gate)
            {
                if (_stopped || (safety ? !_safety.IsCompleted || _lastSafety == command.CommandId :
                    !_normal.IsCompleted || !_safety.IsCompleted || _lastNormal == command.CommandId)) return false;
                var copy = RecoveryKernelJournalDocument.CloneCommand(command);
                var worker = Task.Run(() =>
                {
                    try { _execute(copy); }
                    catch (Exception ex) { try { _failed(copy, ex); } catch { } }
                });
                if (safety) { _lastSafety = command.CommandId; _safety = worker; }
                else { _lastNormal = command.CommandId; _normal = worker; }
                return true;
            }
        }

        public async Task<bool> StopAsync(int timeoutMs)
        {
            Task pending;
            lock (_gate) { _stopped = true; pending = Task.WhenAll(_normal, _safety); }
            return await Task.WhenAny(pending, Task.Delay(Math.Max(1, timeoutMs))).ConfigureAwait(false) == pending;
        }
    }
}
