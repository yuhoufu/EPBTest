using System;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.SafetyAgent
{
    public sealed class IndependentSafetyStageResult
    {
        public string RequestId { get; set; }
        public long Generation { get; set; }
        public IndependentRecoveryPhase Phase { get; set; }
        public bool Confirmed { get; set; }
        public string Detail { get; set; }
    }

    // Separate from the original Watchdog handoff protocol. The SYSTEM executor
    // owns authentication, sealed configuration and the worker's hard deadline.
    // A cancellation token in this process cannot bound a stuck native SDK call.
    public static class IndependentSafetyStages
    {
        public static IndependentSafetyStageResult Execute(IndependentRecoveryTransaction transaction,
            string sealedConfigDirectory, SafetyRuntimeSnapshot runtime, ISafetyHardwareFactory factory)
        {
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));
            transaction.Validate();
            if (transaction.Phase != IndependentRecoveryPhase.PowerOff &&
                transaction.Phase != IndependentRecoveryPhase.OutputsSafe)
                throw new InvalidOperationException("IndependentSafetyPhaseInvalid");
            if (runtime == null) throw new ArgumentNullException(nameof(runtime));
            runtime.Validate();
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            var result = new IndependentSafetyStageResult
            {
                RequestId = transaction.RequestId, Generation = transaction.Generation,
                Phase = transaction.Phase, Confirmed = false
            };
            try
            {
                using (var hardware = factory.Create(sealedConfigDirectory, runtime))
                {
                    if (transaction.Phase == IndependentRecoveryPhase.PowerOff)
                    {
                        result.Confirmed = hardware.ConfirmPowerOff();
                        result.Detail = result.Confirmed ? "IndependentPowerOffConfirmed" : "IndependentPowerOffUnconfirmed";
                    }
                    else
                    {
                        // Recheck power after old controllers have exited. No
                        // success receipt is emitted for a partial safe state.
                        if (!hardware.ConfirmPowerOff()) { result.Detail = "IndependentPowerOffUnconfirmed"; return result; }
                        if (!hardware.ConfirmDoOff()) { result.Detail = "IndependentDoOffUnconfirmed"; return result; }
                        if (!hardware.ConfirmAoZero()) { result.Detail = "IndependentAoZeroUnconfirmed"; return result; }
                        if (!hardware.ConfirmPressureSafe()) { result.Detail = "IndependentPressureUnconfirmed"; return result; }
                        result.Confirmed = true;
                        result.Detail = "IndependentPowerDoAoPressureConfirmed";
                    }
                }
            }
            catch (Exception ex)
            {
                result.Confirmed = false;
                result.Detail = "IndependentSafetyFailed:" + ex.GetType().Name + ":" + ex.Message;
            }
            return result;
        }
    }
}
