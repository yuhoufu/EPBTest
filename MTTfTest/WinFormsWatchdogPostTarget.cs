using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MTTFTest.Watchdog.Client;

namespace MtEmbTest
{
    /// <summary>
    /// WinForms adapter for the safety callback lane.  The form/control is a
    /// stable owner for the lifetime of the Main_Frm window; accepted posts
    /// are never cancelled by Dispose, while new admissions are rejected.
    /// </summary>
    internal sealed class WinFormsWatchdogPostTarget : IWatchdogCallbackPostTarget, IDisposable
    {
        private readonly object _gate = new object();
        private readonly Control _owner;
        private long _sequence;
        private int _disposed;

        internal WinFormsWatchdogPostTarget(Control owner)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        }

        internal Control Owner => _owner;
        internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public WatchdogPostReceipt TryPost(Func<Task> callback)
        {
            if (callback == null) return WatchdogPostReceipt.Rejected("CallbackMissing");

            TaskCompletionSource<bool> completion;
            long sequence;
            Control owner;
            lock (_gate)
            {
                if (_disposed != 0) return WatchdogPostReceipt.Rejected("TargetDisposed");
                owner = _owner;
                if (owner.IsDisposed || owner.Disposing || !owner.IsHandleCreated)
                    return WatchdogPostReceipt.Rejected("ControlHandleUnavailable");
                completion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                sequence = ++_sequence;
            }

            try
            {
                // BeginInvoke is the admission boundary.  It is intentionally
                // outside _gate so the UI callback can re-enter the target or
                // begin shutdown without taking the target lock.
                owner.BeginInvoke((Action)(async () =>
                {
                    try
                    {
                        var task = callback();
                        if (task != null) await task.ConfigureAwait(true);
                        completion.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        completion.TrySetException(ex);
                    }
                }));
                return new WatchdogPostReceipt(
                    true, completion.Task, string.Empty, sequence);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
                return WatchdogPostReceipt.Rejected(
                    "BeginInvokeFailed:" + ex.GetType().Name);
            }
        }

        /// <summary>
        /// Stops new posts.  Already accepted BeginInvoke callbacks retain
        /// their completion source and are allowed to finish or fault.
        /// </summary>
        public void Dispose()
        {
            lock (_gate) _disposed = 1;
        }
    }
}
