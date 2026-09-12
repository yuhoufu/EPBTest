using System;
using System.Threading;
using System.Threading.Tasks;
using DataOperation;

namespace Controller
{
    public enum RunAdmissionOrigin { ManualStart, ManualContinue, AutomaticRecovery }

    public sealed class RunAdmissionRequest
    {
        public RunChainIdentity Identity { get; }
        public int[] Channels { get; }
        public RunAdmissionOrigin Origin { get; }

        internal RunAdmissionRequest(RunChainIdentity identity, int[] channels, RunAdmissionOrigin origin)
        {
            Identity = identity;
            Channels = (int[])channels.Clone();
            Origin = origin;
        }
    }

    public sealed partial class EpbManager
    {
        // Optional host seam: Controller never reads authorization files. The
        // host installs the handler before any batch can start. Tests and hosts
        // without external Guard retain their existing admission policy.
        public Func<RunAdmissionRequest, CancellationToken, Task> ExternalRunAdmissionAsync { get; set; }
        // A bounded in-memory notification; it must not wait for persistence or
        // prevent the controller from completing its physical pause sequence.
        public Action<Guid> ExternalManualPauseRequested { get; set; }

        private void NotifyExternalManualPause()
        {
            try { ExternalManualPauseRequested?.Invoke(_activeBatchId); }
            catch { /* Preserve the existing physical pause path. */ }
        }
    }
}
