using System;
using System.IO;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.FallbackGuard
{
    public sealed class IndependentExecutorObservation
    {
        public int SchemaVersion { get; set; } = 2;
        public string InstallationId { get; set; }
        public long CapturedUtcTicks { get; set; }
        public string Detail { get; set; }
        public long StateRevision { get; set; }
        public string RunId { get; set; }
        public long RunEpoch { get; set; }
        public int ControllerPid { get; set; }
        public long ControllerStartUtcTicks { get; set; }
        public string ControllerSessionToken { get; set; }
        public string BindingState { get; set; }
        public string TransactionId { get; set; }
        public string TransactionPhase { get; set; }
        // Historical transaction result, never a current action/liveness claim.
        public bool LastTransactionVerified { get; set; }

        public static IndependentExecutorObservation Capture(string installationId,
            IndependentProjectState state, long now, string detail)
        {
            if (!Guid.TryParseExact(installationId, "N", out _) || now <= 0)
                throw new InvalidDataException("IndependentObservationIdentityInvalid");
            state?.Validate();
            return new IndependentExecutorObservation
            {
                InstallationId = installationId, CapturedUtcTicks = now,
                Detail = detail == null || detail.Length <= 512 ? detail : detail.Substring(0, 512),
                StateRevision = state?.Revision ?? 0,
                RunId = state?.Intent?.RunId, RunEpoch = state?.Intent?.RunEpoch ?? 0,
                ControllerPid = state?.Controller?.Pid ?? 0,
                ControllerStartUtcTicks = state?.Controller?.StartUtcTicks ?? 0,
                ControllerSessionToken = state?.Controller?.SessionToken,
                BindingState = state?.Intent == null ? "AwaitingRunIntent" : state.Controller == null ? "ControllerIdentityMissing" :
                    state.Ticket?.Consumer != null && !state.Ticket.Consumer.Matches(state.Controller) ? "ReplacementAwaitingRunBinding" : "BoundToDurableRun",
                TransactionId = state?.Transaction?.RequestId,
                TransactionPhase = state?.Transaction?.Phase.ToString(),
                LastTransactionVerified = state?.Transaction?.Phase == IndependentRecoveryPhase.Verified
            };
        }
    }
}
