using System.IO;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Watchdog
{
    internal sealed class V3SafetyHandoffBoundary
    {
        internal bool LogicalQuiescent { get; private set; }
        internal bool HardwareResourcesReleased { get; private set; }
        internal bool CallbacksIsolated { get; private set; }
        internal bool ExecutionAuthorizationRevoked { get; private set; }
        internal bool DataBoundaryClosed { get; private set; }
        internal bool OldExecutionIsolated { get; private set; }

        internal static V3SafetyHandoffBoundary Capture(RecoveryCommand command,
            RecoveryCommandReceipt receipt, bool engineProcessExitConfirmed = false)
        {
            if (command?.IsStructurallyValid() != true || receipt == null ||
                receipt.SchemaVersion != EngineHostProtocol.SchemaVersion || receipt.CommandId != command.CommandId ||
                receipt.IdempotencyKey != command.IdempotencyKey)
                throw new InvalidDataException("SafetyHandoffReceiptBindingInvalid");
            if (engineProcessExitConfirmed)
            {
                // OS exit closes handles and execution. The durable active-circle
                // boundary requires independent checkpoint/archive reconciliation.
                return new V3SafetyHandoffBoundary
                {
                    LogicalQuiescent = true, HardwareResourcesReleased = true,
                    CallbacksIsolated = true, ExecutionAuthorizationRevoked = true,
                    OldExecutionIsolated = true, DataBoundaryClosed = false
                };
            }
            var handoff = receipt.HardwareHandoff;
            if (handoff?.Matches(command, handoff.EngineInstanceId) != true)
                throw new InvalidDataException("IndependentHardwareHandoffEvidenceMissingOrStale");
            if (!handoff.ResourcesTransferable || !receipt.ExecutionAuthorizationRevoked)
                throw new System.InvalidOperationException("EngineHardwareStillOwned;IndependentSafetyAgentCannotAcquireResources");
            return new V3SafetyHandoffBoundary
            {
                LogicalQuiescent = handoff.LogicalQuiescent,
                HardwareResourcesReleased = handoff.NativeResourcesReleased,
                CallbacksIsolated = handoff.CallbacksIsolated,
                ExecutionAuthorizationRevoked = receipt.ExecutionAuthorizationRevoked,
                DataBoundaryClosed = receipt.DataBoundaryClosed,
                OldExecutionIsolated = handoff.ResourcesTransferable && receipt.ExecutionAuthorizationRevoked
            };
        }
    }
}
