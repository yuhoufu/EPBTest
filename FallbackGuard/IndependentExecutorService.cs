using System;
using System.IO;
using System.ServiceProcess;
using System.Threading;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.FallbackGuard
{
    internal sealed class IndependentExecutorService : ServiceBase
    {
        private readonly string _registrationPath;
        private readonly IndependentExecutorRegistration _registration;
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private IndependentExecutorRuntime _runtime;
        private Thread _thread;

        private IndependentExecutorService(string registrationPath)
        {
            _registrationPath = Path.GetFullPath(registrationPath);
            _registration = IndependentExecutorRegistration.LoadTrusted(_registrationPath);
            ServiceName = "MTTFTestIndependent-" + _registration.InstallationId;
            CanStop = true;
            CanShutdown = true;
            AutoLog = true;
        }

        internal static int Run(string registrationPath)
        {
            using (var service = new IndependentExecutorService(registrationPath)) ServiceBase.Run(service);
            return 0;
        }

        protected override void OnStart(string[] args)
        {
            // Fail service startup if SYSTEM, protected registration or exclusive
            // executor ownership cannot be established. No misleading Running.
            _runtime = new IndependentExecutorRuntime(_registrationPath);
            _stop.Reset();
            _thread = new Thread(Loop) { IsBackground = true, Name = "IndependentProjectExecutor" };
            try { _thread.Start(); }
            catch { _runtime.Dispose(); _runtime = null; throw; }
        }

        private void Loop()
        {
            var registration = _registration;
            var statusPath = Path.Combine(registration.StateDirectory, "executor-observation.json");
            var store = new IndependentProjectStateStore(registration.StateDirectory);
            long lastPublished = 0;
            string detail = "Starting";
            try
            {
                while (!_stop.WaitOne(200))
                {
                    try { _runtime.Tick(); detail = _runtime.Detail; }
                    catch (Exception error)
                    {
                        // CAS races are retried from fresh durable state. Corrupt
                        // authority never becomes permission to energize or reset.
                        detail = "ExecutorOperationFailed:" + error.GetType().Name + ":" + error.Message;
                    }
                    var now = DateTime.UtcNow.Ticks;
                    if (now - lastPublished < TimeSpan.FromSeconds(5).Ticks) continue;
                    lastPublished = now;
                    try
                    {
                        BoundedJson.Write(statusPath, IndependentExecutorObservation.Capture(
                            registration.InstallationId, store.Read(), now, detail));
                    }
                    catch { /* Observation failure does not change durable recovery authority. */ }
                }
            }
            finally { _runtime.Dispose(); }
        }

        protected override void OnStop()
        {
            _stop.Set();
            if (_thread != null && !_thread.Join(TimeSpan.FromSeconds(15)))
                throw new System.TimeoutException("IndependentExecutorStopNotConfirmed");
            _thread = null;
            _runtime = null;
        }

        protected override void OnShutdown() { OnStop(); base.OnShutdown(); }
        protected override void Dispose(bool disposing)
        {
            if (disposing && (_thread == null || !_thread.IsAlive)) _stop.Dispose();
            base.Dispose(disposing);
        }
    }

}
