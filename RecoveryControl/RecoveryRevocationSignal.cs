using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace MTTFTest.RecoveryControl
{
    /// <summary>
    /// Best-effort independent stop notification. A receiver must hold the
    /// handle or persist the stop before it can acknowledge durable cancellation.
    /// Missing handles after total process loss are not proof no click occurred.
    /// </summary>
    public sealed class RecoveryRevocationSignal : IDisposable
    {
        private readonly EventWaitHandle _event;
        private readonly EventWaitHandle _pause;
        public RecoveryRevocationSignal(RecoveryAuthorizationToken token)
        {
            _event = OpenOrCreate(Name(token));
            try { _pause = OpenOrCreate(Name(token) + ".Pause"); }
            catch { _event.Dispose(); throw; }
        }

        private static EventWaitHandle OpenOrCreate(string name)
        {
            var security = new EventWaitHandleSecurity();
            security.AddAccessRule(new EventWaitHandleAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                EventWaitHandleRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new EventWaitHandleAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                EventWaitHandleRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new EventWaitHandleAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify, AccessControlType.Allow));
            try { return EventWaitHandle.OpenExisting(name, EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify); }
            catch (WaitHandleCannotBeOpenedException)
            {
                try { return new EventWaitHandle(false, EventResetMode.ManualReset, name, out _, security); }
                catch (UnauthorizedAccessException)
                {
                    return EventWaitHandle.OpenExisting(name, EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify);
                }
            }
        }

        public void Revoke() => _event.Set();
        public void Pause() => _pause.Set();
        public bool IsRevoked => _event.WaitOne(0);
        public void Dispose() { _event.Dispose(); _pause.Dispose(); }

        public static bool IsStopped(RecoveryAuthorizationToken token) => IsSet(token, false);
        public static bool IsPaused(RecoveryAuthorizationToken token) => IsSet(token, true);

        internal static bool HasStopRequest(RecoveryControlState state)
        {
            var token = state.Token();
            if (IsStopped(token)) return true;
            if (state.Intent?.DesiredState != RecoveryDesiredState.Paused ||
                state.Intent.PausedSourceIntentVersion < 1) return false;
            token.IntentVersion = state.Intent.PausedSourceIntentVersion;
            return IsStopped(token);
        }

        private static bool IsSet(RecoveryAuthorizationToken token, bool pause)
        {
            if (token == null) return false;
            try
            {
                using (var handle = EventWaitHandle.OpenExisting(Name(token) + (pause ? ".Pause" : ""), EventWaitHandleRights.Synchronize))
                    return handle.WaitOne(0);
            }
            catch (WaitHandleCannotBeOpenedException) { return false; }
            catch (UnauthorizedAccessException ex) { throw new IOException("RecoveryRevocationSignalUnreadable", ex); }
        }

        private static string Name(RecoveryAuthorizationToken token)
        {
            if (token == null || !Guid.TryParse(token.InstallationId, out var installation) ||
                !Guid.TryParse(token.AuthorizationId, out var authorization) || token.IntentVersion < 1)
                throw new ArgumentException("RecoveryRevocationIdentityInvalid");
            return "Global\\MTTFTest.RecoveryStop.V1." + installation.ToString("N") + "." +
                authorization.ToString("N") + "." + token.IntentVersion;
        }
    }
}
