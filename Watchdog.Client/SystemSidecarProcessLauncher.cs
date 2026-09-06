using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Watchdog.Client
{
    public sealed class SystemSidecarProcessLauncher : ISidecarProcessLauncher
    {
        public Task<SidecarProcessLaunchResult> LaunchAsync(
            SidecarProcessLaunchRequest request,
            CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.ExecutablePath))
                throw new ArgumentException("Sidecar executable path is required.", nameof(request));
            cancellationToken.ThrowIfCancellationRequested();
            Process process = null;
            SidecarProcessLaunchResult launch = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                process = Process.Start(new ProcessStartInfo
                {
                    FileName = request.ExecutablePath,
                    Arguments = request.Arguments ?? string.Empty,
                    WorkingDirectory = request.WorkingDirectory ?? Environment.CurrentDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                if (process == null) throw new InvalidOperationException("Sidecar process start returned null.");

                // Process.Start is synchronous, so cancellation can race the
                // exact point at which the OS process becomes live.  Never
                // report a successful launch after that race: kill the exact
                // process and release its handle before propagating cancel.
                if (cancellationToken.IsCancellationRequested)
                {
                    KillAndDispose(process);
                    process = null;
                    cancellationToken.ThrowIfCancellationRequested();
                }

                launch = new SidecarProcessLaunchResult
                {
                    Process = process,
                    Owner = new MTTFTest.Watchdog.Protocol.SidecarProcessHandleOwner(process)
                };
                process = null;
                if (cancellationToken.IsCancellationRequested)
                {
                    DisposeLaunch(launch, true);
                    launch = null;
                    cancellationToken.ThrowIfCancellationRequested();
                }
                return Task.FromResult(launch);
            }
            catch
            {
                DisposeLaunch(launch, true);
                KillAndDispose(process);
                throw;
            }
        }

        private static void DisposeLaunch(SidecarProcessLaunchResult launch, bool kill)
        {
            if (launch == null) return;
            try
            {
                if (kill && launch.Process != null && !launch.Process.HasExited)
                {
                    launch.Process.Kill();
                    try
                    {
                        launch.Process.WaitForExit(
                            WatchdogTransportPolicy.ProcessExitJoin1000);
                    }
                    catch { }
                }
            }
            catch { }
            try { launch.Dispose(); } catch { }
        }

        private static void KillAndDispose(Process process)
        {
            if (process == null) return;
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    try
                    {
                        process.WaitForExit(
                            WatchdogTransportPolicy.ProcessExitJoin1000);
                    }
                    catch { }
                }
            }
            catch { }
            try { process.Dispose(); } catch { }
        }
    }
}
