using System;
using Controller;

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
    /// Retains only a completed, run-bound manual StopAll receipt. A new start
    /// revokes it before any hardware can be re-enabled, preventing an old
    /// operator-stop intent from authorizing a later run's application exit.
    /// </summary>
    internal sealed class ManualStopExitReceiptOwner
    {
        private readonly object _gate = new object();
        private StopSafetyResult _receipt;

        internal bool Publish(StopSafetyResult result)
        {
            lock (_gate)
            {
                _receipt = IsReusable(result) ? result.Clone() : null;
                return _receipt != null;
            }
        }

        internal void RevokeForNewStart()
        {
            Revoke();
        }

        internal void Revoke()
        {
            lock (_gate) _receipt = null;
        }

        internal StopSafetyResult TryCapture(bool batchSessionActive)
        {
            lock (_gate)
            {
                if (batchSessionActive || !IsReusable(_receipt)) return null;
                return _receipt.Clone(reused: true);
            }
        }

        private static bool IsReusable(StopSafetyResult result)
        {
            return result != null &&
                   result.Source == StopSource.ManualUi &&
                   result.RunId != Guid.Empty &&
                   !string.IsNullOrWhiteSpace(result.CorrelationId) &&
                   result.CompletedUtc != default(DateTime) &&
                   result.CanCloseApplication;
        }
    }
}
