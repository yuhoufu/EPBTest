using System;
using System.Linq;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Recovery.Kernel
{
    /// <summary>Dispatches annunciation only; never creates or changes a recovery Owner.</summary>
    public sealed class AlarmPanelCommandDispatcher
    {
        private readonly RecoveryCoordinator _coordinator;
        public AlarmPanelCommandDispatcher(RecoveryCoordinator coordinator)
        {
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        }

        public OperatorCommandAdmission ExecuteNext(long nowUtcTicks, Func<OperatorCommand, OperatorExecutionReceipt> execute)
        {
            if (!EngineUiContract.IsUtcTicks(nowUtcTicks) || execute == null) throw new ArgumentException("AlarmPanelDispatchArgumentsInvalid");
            var pending = _coordinator.Snapshot().OperatorAdmissions.FirstOrDefault(a => a.PanelTransaction != null && !a.ExecutionCompleted);
            if (pending == null) return null;
            var command = pending.PanelTransaction;
            var receipt = nowUtcTicks - command.IssuedUtcTicks > TimeSpan.FromSeconds(35).Ticks
                ? new OperatorExecutionReceipt { CommandId = command.CommandId, Fingerprint = pending.Fingerprint,
                    Succeeded = false, CompletedUtcTicks = nowUtcTicks,
                    Detail = "报警命令结果在期限内未确认；不更换 ID 重放，不影响硬故障隔离。" }
                : execute(command);
            // An exception or malformed response leaves the SAME durable command pending.
            return _coordinator.CompletePanelCommand(command, receipt);
        }
    }
}
