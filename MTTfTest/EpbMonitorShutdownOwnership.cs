using System;
using System.Threading.Tasks;
using Controller;
using MTTFTest.Watchdog.Protocol;

namespace MTEmbTest
{
    /// <summary>
    /// Owns the monitor form's hardware release boundary. The manager path is
    /// preferred once EpbManager has taken ownership; direct cleanup is only a
    /// partial-initialization/failure fallback. Every caller waits for the same
    /// synchronous release and no resource path is admitted twice.
    /// </summary>
    internal sealed class EpbMonitorHardwareReleaseOwner
    {
        private readonly object _gate = new object();
        private int _state;
        private int _releaseCount;

        internal int ReleaseCount
        {
            get
            {
                lock (_gate) return _releaseCount;
            }
        }

        internal bool Release(
            Action managerRelease,
            Action fallbackRelease,
            Action<Exception> managerFailure)
        {
            lock (_gate)
            {
                if (_state == 2) return true;
                if (_state == 1) return false;
                _state = 1;
                try
                {
                    if (managerRelease != null)
                    {
                        try
                        {
                            managerRelease();
                        }
                        catch (Exception ex)
                        {
                            try { managerFailure?.Invoke(ex); }
                            catch { /* diagnostics must never block the safety fallback */ }
                            fallbackRelease?.Invoke();
                        }
                    }
                    else
                    {
                        fallbackRelease?.Invoke();
                    }

                    _releaseCount++;
                    _state = 2;
                    return true;
                }
                catch
                {
                    // The form's fallback is individually guarded, but keep retry
                    // possible if an unexpected host/logger exception escapes.
                    _state = 0;
                    throw;
                }
            }
        }
    }

    /// <summary>
    /// Immutable adoption of an already completed StopAll transaction by a
    /// later operator exit intent.  The original source remains audit data;
    /// SystemFault and ManualUi transactions are equally reusable only when
    /// their exact safety identity and all terminal boundaries are present.
    /// </summary>
    internal sealed class ManualExitIntentReceipt
    {
        internal string CommandId { get; set; } = string.Empty;
        internal Guid SafetyTransactionId { get; set; }
        internal Guid RunId { get; set; }
        internal long RunEpoch { get; set; }
        internal long SafetyBoundaryGeneration { get; set; }
        internal StopSource OriginalSource { get; set; }
        internal string OriginalCorrelationId { get; set; } = string.Empty;
        internal DateTime AdoptedUtc { get; set; }

        internal bool Matches(StopSafetyResult result)
        {
            return result != null &&
                   SafetyTransactionId != Guid.Empty &&
                   SafetyTransactionId == result.SafetyTransactionId &&
                   RunId == result.RunId &&
                   RunEpoch == result.RunEpoch &&
                   SafetyBoundaryGeneration == result.SafetyBoundaryGeneration;
        }

        internal ManualExitIntentReceipt Clone()
        {
            return (ManualExitIntentReceipt)MemberwiseClone();
        }
    }

    /// <summary>
    /// Retains a completed, identity-bound StopAll receipt adopted by an
    /// operator exit. A new start revokes it before hardware can be enabled.
    /// </summary>
    internal sealed class ManualStopExitReceiptOwner
    {
        private readonly object _gate = new object();
        private StopSafetyResult _receipt;
        private ManualExitIntentReceipt _intent;

        internal bool Publish(StopSafetyResult result, string commandId = null)
        {
            lock (_gate)
            {
                _receipt = IsReusable(result) ? result.Clone() : null;
                _intent = _receipt == null
                    ? null
                    : new ManualExitIntentReceipt
                    {
                        CommandId = commandId ?? string.Empty,
                        SafetyTransactionId = result.SafetyTransactionId,
                        RunId = result.RunId,
                        RunEpoch = result.RunEpoch,
                        SafetyBoundaryGeneration = result.SafetyBoundaryGeneration,
                        OriginalSource = result.Source,
                        OriginalCorrelationId = result.CorrelationId ?? string.Empty,
                        AdoptedUtc = DateTime.UtcNow
                    };
                return _receipt != null;
            }
        }

        internal void RevokeForNewStart()
        {
            Revoke();
        }

        internal void Revoke()
        {
            lock (_gate)
            {
                _receipt = null;
                _intent = null;
            }
        }

        internal StopSafetyResult TryCapture(bool batchSessionActive)
        {
            lock (_gate)
            {
                if (batchSessionActive || !IsReusable(_receipt)) return null;
                return _receipt.Clone(reused: true);
            }
        }

        internal ManualExitIntentReceipt CaptureIntent()
        {
            lock (_gate) return _intent?.Clone();
        }

        private static bool IsReusable(StopSafetyResult result)
        {
            return result != null &&
                   result.SafetyTransactionId != Guid.Empty &&
                   result.RunId != Guid.Empty &&
                   result.RunEpoch > 0 &&
                   result.SafetyBoundaryGeneration > 0 &&
                   !string.IsNullOrWhiteSpace(result.CorrelationId) &&
                   result.CompletedUtc != default(DateTime) &&
                   result.CanReleaseAcquisition &&
                   result.PersistenceBoundaryConfirmed &&
                   result.LogicalQuiescenceConfirmed &&
                   result.LastStage == StopSafetyStage.Completed;
        }
    }

    /// <summary>
    /// Immutable combined terminal for one operator stop.  Device/persistence
    /// completion alone never authorizes a new run; the exact Watchdog lease
    /// and Main-owned UI resources must have reached their terminal receipt.
    /// </summary>
    internal sealed class StopSessionReceipt
    {
        internal string CommandId { get; set; } = string.Empty;
        internal string SessionId { get; set; } = string.Empty;
        internal long SessionGeneration { get; set; }
        internal long SessionLease { get; set; }
        internal StopSafetyResult StopSafety { get; set; }
        internal ManualExitIntentReceipt ManualExitIntent { get; set; }
        internal RuntimeShutdownReceipt WatchdogShutdown { get; set; }
        internal WatchdogSafetyHandoffReceipt SafetyHandoff { get; set; }
        internal bool UiResourcesReleased { get; set; }
        internal DateTime CompletedUtc { get; set; }
        internal string Error { get; set; } = string.Empty;

        internal bool ExactSessionTerminal =>
            WatchdogShutdown != null && WatchdogShutdown.IsTerminal &&
            string.Equals(SessionId, WatchdogShutdown.SessionId, StringComparison.Ordinal) &&
            SessionGeneration == WatchdogShutdown.SessionGeneration &&
            SessionLease == WatchdogShutdown.SessionLease;

        internal bool ExactSafetyHandoffAccepted =>
            SafetyHandoff != null &&
            SafetyHandoff.State >= WatchdogSafetyHandoffState.Accepted &&
            string.Equals(SessionId, SafetyHandoff.SessionId, StringComparison.Ordinal) &&
            SessionGeneration == SafetyHandoff.SessionGeneration &&
            SessionLease == SafetyHandoff.SessionLease &&
            SafetyHandoff.CanExitApplication;

        internal bool CanStartNewSession => StopSafety?.CanRestartInProcess == true &&
                                    ManualExitIntent?.Matches(StopSafety) == true &&
                                    ExactSessionTerminal &&
                                    UiResourcesReleased;
        internal bool CanExitApplication =>
            StopSafety?.PersistenceBoundaryConfirmed == true &&
            UiResourcesReleased &&
            (ExactSessionTerminal || ExactSafetyHandoffAccepted);
        internal bool CanRestart => CanStartNewSession;
        internal bool CanClose => CanExitApplication;
    }

    internal enum ApplicationExitDisposition
    {
        None = 0,
        Graceful = 1,
        SafetyHandoff = 2,
        ForcedDeadlineExit = 3
    }

    internal sealed class ApplicationCloseReceipt
    {
        internal string SessionId { get; set; } = string.Empty;
        internal long SessionGeneration { get; set; }
        internal long SessionLease { get; set; }
        internal bool HardwareResourcesReleased { get; set; }
        internal bool WatchdogTerminal { get; set; }
        internal bool SafetyHandoffAccepted { get; set; }
        internal ApplicationExitDisposition Disposition { get; set; }
        internal DateTime RequestedUtc { get; set; }
        internal DateTime HardDeadlineUtc { get; set; }
        internal string DiagnosticDetail { get; set; } = string.Empty;
        internal DateTime CompletedUtc { get; set; }

        internal bool CanExit => HardwareResourcesReleased &&
                                 (WatchdogTerminal || SafetyHandoffAccepted);

        internal bool Matches(RuntimeTransportSessionContext context)
        {
            return context == null ||
                   string.Equals(SessionId, context.SessionId, StringComparison.Ordinal) &&
                   SessionGeneration == context.SessionGeneration &&
                   SessionLease == context.SessionLease;
        }
    }

    /// <summary>
    /// Single-flight owner shared by stop, immediate start and monitor close.
    /// A new start explicitly revokes only a completed receipt; an in-flight
    /// stop must first be joined and cannot be replaced by another shutdown.
    /// </summary>
    internal sealed class StopSessionReceiptOwner
    {
        private readonly object _gate = new object();
        private TaskCompletionSource<StopSessionReceipt> _completion;
        private StopSessionReceipt _receipt;

        internal Task<StopSessionReceipt> Begin(string commandId)
        {
            lock (_gate)
            {
                if (_completion != null && !_completion.Task.IsCompleted)
                    return _completion.Task;
                _receipt = null;
                _completion = new TaskCompletionSource<StopSessionReceipt>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                return _completion.Task;
            }
        }

        internal Task<StopSessionReceipt> CaptureTask()
        {
            lock (_gate) return _completion?.Task;
        }

        internal void Complete(StopSessionReceipt receipt)
        {
            TaskCompletionSource<StopSessionReceipt> completion;
            lock (_gate)
            {
                _receipt = receipt;
                completion = _completion;
            }
            completion?.TrySetResult(receipt);
        }

        internal StopSessionReceipt TryCaptureCompleted()
        {
            lock (_gate) return _receipt?.CanClose == true ? _receipt : null;
        }

        internal void RevokeForNewStart()
        {
            lock (_gate)
            {
                if (_completion != null && !_completion.Task.IsCompleted)
                    throw new InvalidOperationException("人工停止组合终态仍在收口，不能撤权。");
                _receipt = null;
                _completion = null;
            }
        }
    }
}
