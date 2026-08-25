using System;
using System.Threading.Tasks;
using Controller;
using MTTFTest.Watchdog.Client;
using MTTFTest.Watchdog.Protocol;

namespace MTEmbTest
{
    /// <summary>
    /// Production stop boundary shared by FrmEpbMainMonitor and the
    /// hardware-independent UI acceptance seam.  Only the final hardware
    /// safety result and the exact transport identity are supplied by the
    /// caller; correlation, validation and exit ordering live here.
    /// </summary>
    internal interface IWinFormsWatchdogStopSafetyPort
    {
        Task<StopSafetyResult> PrepareForFreshRestartAsync(StopContext context);
        void NotifyStopCompleted(WatchdogStopSummary summary, string reason);
        void RequestWatchdogOwnedExit(string reason);
    }

    internal static class WinFormsWatchdogStopHandlerCore
    {
        internal static async Task HandleAsync(
            IWinFormsWatchdogStopSafetyPort port,
            WatchdogStopAllOfferEnvelope envelope,
            RuntimeTransportSessionContext context,
            RuntimeValidatedAttachIdentity expectedIdentity,
            long expectedPipelineGeneration,
            bool requireGlobalAttached)
        {
            if (port == null) throw new ArgumentNullException(nameof(port));
            if (envelope == null) throw new InvalidOperationException("StopAll envelope missing.");
            if (context == null || expectedIdentity == null ||
                context.PipelineState != RuntimeCallbackPipelineState.Ready ||
                context.PipelineGeneration != expectedPipelineGeneration ||
                !WatchdogRuntime.IsUiBindingIdentityCurrent(
                    context, expectedIdentity, requireGlobalAttached))
                throw new InvalidOperationException("Watchdog UI callback identity changed.");

            var correlation = string.IsNullOrWhiteSpace(envelope.CorrelationId)
                ? Guid.NewGuid().ToString("N")
                : envelope.CorrelationId;
            var safety = await port.PrepareForFreshRestartAsync(
                    new StopContext
                    {
                        Source = StopSource.SystemFault,
                        Reason = "独立看门狗整批接管：" + envelope.ReasonCode,
                        Initiator = "FrmEpbMainMonitor.WatchdogSafety",
                        CorrelationId = correlation,
                        RequestedUtc = DateTime.UtcNow
                    })
                .ConfigureAwait(true);
            if (safety == null || !safety.CanCloseApplication ||
                safety.Outcome == StopSafetyOutcome.PhysicalSafetyUnconfirmed)
                throw new InvalidOperationException(
                    "Watchdog StopAll 未达到真实安全终态：" +
                    (safety?.StageError ?? "StopSafetyResultUnavailable"));

            port.NotifyStopCompleted(ToWatchdogStopSummary(safety), envelope.ReasonCode);
            port.RequestWatchdogOwnedExit(
                "WatchdogStopAllCompleted:" + correlation);
        }

        internal static WatchdogStopSummary ToWatchdogStopSummary(
            StopSafetyResult safety)
        {
            return new WatchdogStopSummary
            {
                MotorOffConfirmed = safety?.MotorOffCommandSucceeded == true,
                PowerOffConfirmed = safety?.PowerOffConfirmed == true,
                PressureSafeConfirmed = safety?.PressureSafeConfirmed == true,
                RawDrained = safety?.RawStorageFlushed == true,
                PersistenceConfirmed = safety?.PersistenceBoundaryConfirmed == true,
                ContinuityConfirmed = safety?.DataContinuityCompromised == false,
                LogicalQuiescenceConfirmed = safety?.LogicalQuiescenceConfirmed == true,
                RequiresProcessRestart = safety?.RequiresProcessRestart == true,
                TimedOut = safety?.TimedOut == true,
                Outcome = safety?.Outcome.ToString() ?? string.Empty,
                LastStage = safety?.LastStage.ToString() ?? string.Empty,
                Detail = safety == null ? "StopSafetyResultUnavailable" :
                    $"CanClose={safety.CanCloseApplication};Motor={safety.MotorError};Power={safety.PowerError};" +
                    $"Pressure={safety.PressureError};Persistence={safety.PersistenceError};Logical={safety.LogicalError}"
            };
        }
    }
}
