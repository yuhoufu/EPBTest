using System;
using MTTFTest.RecoveryControl;

namespace MTTFTest.RecoveryGuard
{
    // A supervised, single-run commissioning session is not production approval.
    // The existing transaction, safety and launch checks still apply unchanged.
    internal sealed class RecoveryCommissioningScope
    {
        internal DateTime StartedUtc { get; }
        internal DateTime ExpiresUtc { get; }
        private readonly string _installationId;
        private readonly string _authorizationId;
        private readonly long _intentVersion;

        internal RecoveryCommissioningScope(string installationId, string authorizationId,
            long intentVersion, DateTime startedUtc, DateTime expiresUtc, RecoveryGuardMode mode)
        {
            if (mode != RecoveryGuardMode.RecoverExited ||
                string.IsNullOrWhiteSpace(installationId) || string.IsNullOrWhiteSpace(authorizationId) ||
                intentVersion < 1 || startedUtc.Kind != DateTimeKind.Utc || expiresUtc.Kind != DateTimeKind.Utc ||
                expiresUtc <= startedUtc || expiresUtc - startedUtc > TimeSpan.FromMinutes(15))
                throw new ArgumentException("CommissioningScopeInvalid");
            _installationId = installationId;
            _authorizationId = authorizationId;
            _intentVersion = intentVersion;
            StartedUtc = startedUtc;
            ExpiresUtc = expiresUtc;
        }

        internal void Demand(RecoveryControlState state, DateTime nowUtc)
        {
            if (nowUtc.Kind != DateTimeKind.Utc || nowUtc < StartedUtc || nowUtc >= ExpiresUtc)
                throw new InvalidOperationException("CommissioningScopeExpiredOrClockReversed");
            if (state?.InstallationId != _installationId || state.Intent?.AuthorizationId != _authorizationId ||
                state.Intent.IntentVersion != _intentVersion || state.Intent.DesiredState != RecoveryDesiredState.Run)
                throw new InvalidOperationException("CommissioningScopeRevoked");
        }
    }
}
