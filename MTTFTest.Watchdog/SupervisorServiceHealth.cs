using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Watchdog
{
    internal sealed partial class SupervisorServiceRuntime
    {
        private RecoveryHealthEndpoint _healthEndpoint;
        private long _healthProgressUtcTicks = DateTime.UtcNow.Ticks;
        private Task _agentHealthTask;
        private long _agentHealthProgressUtcTicks = DateTime.UtcNow.Ticks;
        private readonly Dictionary<int, int> _agentHealthFailures = new Dictionary<int, int>();

        private void StartHealthSupervision(string executableDirectory)
        {
            _healthEndpoint = new RecoveryHealthEndpoint(RecoveryHealthEndpoint.SupervisorPipe,
                () => _sessions.Values.Select(s => s.HealthProgressUtcTicks)
                    .Concat(new[] { Interlocked.Read(ref _healthProgressUtcTicks),
                        Interlocked.Read(ref _agentHealthProgressUtcTicks) }).Min(),
                () => "Sessions=" + _sessions.Count + ";Maintenance=" +
                    File.Exists(WatchdogMaintenancePolicy.InhibitPath));
            _ = Task.Run(async () =>
            {
                var iteration = 0;
                while (!_stop.IsCancellationRequested)
                {
                    Interlocked.Exchange(ref _healthProgressUtcTicks, DateTime.UtcNow.Ticks);
                    if (iteration++ % 5 == 0 && (_agentHealthTask == null || _agentHealthTask.IsCompleted))
                        _agentHealthTask = Task.Run(() => CheckAgentHealth(executableDirectory));
                    try { await Task.Delay(1000, _stop.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            });
        }

        private void CheckAgentHealth(string executableDirectory)
        {
            Interlocked.Exchange(ref _agentHealthProgressUtcTicks, DateTime.UtcNow.Ticks);
            if (File.Exists(WatchdogMaintenancePolicy.InhibitPath)) return;
            var expected = Path.Combine(executableDirectory, "MTTFTest.SessionAgent.exe");
            foreach (var process in Process.GetProcessesByName("MTTFTest.SessionAgent"))
            using (process)
            {
                Interlocked.Exchange(ref _agentHealthProgressUtcTicks, DateTime.UtcNow.Ticks);
                try
                {
                    if (!string.Equals(process.MainModule.FileName, expected, StringComparison.OrdinalIgnoreCase)) continue;
                    var started = process.StartTime.ToUniversalTime().Ticks;
                    if (DateTime.UtcNow.Ticks - started < TimeSpan.FromSeconds(30).Ticks) continue;
                    var healthy = false;
                    try { healthy = RecoveryHealthEndpoint.Probe(RecoveryHealthEndpoint.AgentPipe(process.SessionId))
                        ?.Matches(process.Id, started, DateTime.UtcNow) == true; }
                    catch { }
                    _agentHealthFailures.TryGetValue(process.Id, out var failures);
                    _agentHealthFailures[process.Id] = healthy ? 0 : failures + 1;
                    if (healthy || failures < 1) continue;
                    if (File.Exists(WatchdogMaintenancePolicy.InhibitPath)) return;
                    if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == started)
                    {
                        process.Kill();
                        process.WaitForExit(2000);
                        WriteAudit("SessionAgentUnresponsive", "PID=" + process.Id + ";Action=RestartRegisteredTask");
                        using (var launcher = Process.Start(new ProcessStartInfo
                        {
                            FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                            Arguments = "/Run /TN MTTFTestSessionAgent", UseShellExecute = false,
                            CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
                        })) { launcher?.WaitForExit(2000); }
                    }
                }
                catch (Exception ex) { WriteAudit("SessionAgentHealthCheckFailed", ex.GetBaseException().Message); }
            }
            // A missing agent is covered by its existing one-minute/logon task.
        }

        private sealed partial class SupervisorOwnedSession
        {
            private long _healthProgressUtcTicks = DateTime.UtcNow.Ticks;
            private DateTime _nextHealthProbeUtc;
            private int _healthFailures;
            private int _lastHealthPid;
            internal long HealthProgressUtcTicks => _monitorTask?.IsCompleted == true
                ? DateTime.UtcNow.Ticks : Interlocked.Read(ref _healthProgressUtcTicks);

            private async Task CheckHostHealthAsync()
            {
                if (DateTime.UtcNow < _nextHealthProbeUtc || File.Exists(WatchdogMaintenancePolicy.InhibitPath)) return;
                _nextHealthProbeUtc = DateTime.UtcNow.AddSeconds(5);
                int pid;
                long started;
                lock (_gate)
                {
                    if (!IsCurrentProcessAlive()) { _healthFailures = 0; return; }
                    pid = _process.Id; started = _processStartUtcTicks;
                }
                if (_lastHealthPid != pid) { _lastHealthPid = pid; _healthFailures = 0; }
                if (DateTime.UtcNow.Ticks - started < TimeSpan.FromSeconds(30).Ticks) return;
                var healthy = await Task.Run(() =>
                {
                    try { return RecoveryHealthEndpoint.Probe(RecoveryHealthEndpoint.HostPipe(pid))
                        ?.Matches(pid, started, DateTime.UtcNow) == true; }
                    catch { return false; }
                }).ConfigureAwait(false);
                _healthFailures = healthy ? 0 : _healthFailures + 1;
                if (_healthFailures < 2) return;
                lock (_gate)
                {
                    if (_monitorStop.IsCancellationRequested || File.Exists(WatchdogMaintenancePolicy.InhibitPath) ||
                        !IsCurrentProcessAlive() || _process.Id != pid || _processStartUtcTicks != started) return;
                    _process.Kill();
                    _process.WaitForExit(2000);
                    WriteAudit("SessionHostHealthStalled", $"Session={_sessionId};PID={pid};Action=RestoreDurableOwner");
                    _healthFailures = 0;
                }
            }
        }
    }
}
