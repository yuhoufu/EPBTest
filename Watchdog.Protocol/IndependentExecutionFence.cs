using System;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace MTTFTest.Watchdog.Protocol
{
    // Process-scoped and irreversible in the attached controller. Starting a
    // new run inside a retired process cannot restore its hardware authority.
    public sealed class IndependentExecutionFence : IDisposable
    {
        private readonly EventWaitHandle _signal;
        private int _revoked;
        private bool _disposed;
        private static IndependentExecutionFence _current;

        private static string Name(string installationId, IndependentProcessIdentity identity)
        {
            if (!Guid.TryParseExact(installationId, "N", out _) || identity == null)
                throw new ArgumentException("IndependentExecutionFenceIdentityInvalid");
            identity.Validate();
            return "Global\\MTTF-ExecutionFence-" + SupervisorProtocol.ComputeTextSha256(
                installationId + "|" + identity.Pid + "|" + identity.StartUtcTicks + "|" +
                identity.WindowsSessionId + "|" + identity.SessionToken + "|" + identity.ExecutablePath.ToUpperInvariant());
        }

        public IndependentExecutionFence(string installationId, IndependentProcessIdentity identity)
        {
            using (var process = Process.GetCurrentProcess())
                if (identity == null || identity.Pid != process.Id || identity.StartUtcTicks != process.StartTime.ToUniversalTime().Ticks ||
                    identity.WindowsSessionId != process.SessionId ||
                    !string.Equals(identity.ExecutablePath, process.MainModule.FileName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("IndependentExecutionFenceNotCurrentProcess");
            var security = new EventWaitHandleSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new EventWaitHandleAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                EventWaitHandleRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new EventWaitHandleAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                EventWaitHandleRights.FullControl, AccessControlType.Allow));
            using (var user = WindowsIdentity.GetCurrent())
                security.AddAccessRule(new EventWaitHandleAccessRule(user.User,
                    EventWaitHandleRights.FullControl, AccessControlType.Allow));
            _signal = new EventWaitHandle(false, EventResetMode.ManualReset, Name(installationId, identity), out _, security);
        }

        public bool IsRevoked
        {
            get
            {
                if (Volatile.Read(ref _revoked) != 0) return true;
                try { if (_disposed || _signal.WaitOne(0)) Interlocked.Exchange(ref _revoked, 1); }
                catch (Exception) { Interlocked.Exchange(ref _revoked, 1); }
                return Volatile.Read(ref _revoked) != 0;
            }
        }

        public static void AttachCurrent(string installationId, IndependentProcessIdentity identity)
        {
            var fence = new IndependentExecutionFence(installationId, identity);
            if (Interlocked.CompareExchange(ref _current, fence, null) != null)
            { fence.Dispose(); throw new InvalidOperationException("IndependentExecutionFenceAlreadyAttached"); }
            // Keep the handle for the process lifetime. Never reset on Stop/Start.
            RequireCurrentAuthority();
        }

        public static void RequireCurrentAuthority()
        {
            var current = Volatile.Read(ref _current);
            if (current != null && current.IsRevoked)
                throw new InvalidOperationException("IndependentExecutionAuthorityRevoked");
        }

        public static void Revoke(string installationId, IndependentProcessIdentity identity)
        {
            var name = Name(installationId, identity);
            try
            {
                using (var signal = EventWaitHandle.OpenExisting(name,
                    EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize))
                {
                    if (!signal.Set() || !signal.WaitOne(0))
                        throw new InvalidOperationException("IndependentExecutionFenceRevocationUnconfirmed");
                }
            }
            catch (WaitHandleCannotBeOpenedException) when (OldProcessGone(identity)) { }
        }

        private static bool OldProcessGone(IndependentProcessIdentity identity)
        {
            try
            {
                using (var process = Process.GetProcessById(identity.Pid))
                    return process.HasExited || process.StartTime.ToUniversalTime().Ticks != identity.StartUtcTicks;
            }
            catch (ArgumentException) { return true; }
        }

        public void Dispose() { if (_disposed) return; _disposed = true; Interlocked.Exchange(ref _revoked, 1); _signal.Dispose(); }
    }
}
