using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace MTTFTest.Watchdog.Protocol
{
    // Separate from the launch/Watchdog protocols: read-only, local administrator health evidence.
    public sealed class RecoveryHealthSnapshot
    {
        public int Version { get; set; } = 1;
        public int ProcessId { get; set; }
        public long ProcessStartUtcTicks { get; set; }
        public long ProgressUtcTicks { get; set; }
        public string Stage { get; set; }
        public string Detail { get; set; }
        public bool Matches(int pid, long startTicks, DateTime nowUtc, int staleSeconds = 15)
        {
            return Version == 1 && ProcessId == pid && ProcessStartUtcTicks == startTicks &&
                ProgressUtcTicks > 0 && ProgressUtcTicks <= nowUtc.AddSeconds(5).Ticks &&
                nowUtc.Ticks - ProgressUtcTicks < TimeSpan.FromSeconds(staleSeconds).Ticks;
        }
    }

    public sealed class RecoveryHealthEndpoint : IDisposable
    {
        public const string SupervisorPipe = "MTTFTest.Health.Supervisor.V1";
        public static string AgentPipe(int sessionId) => "MTTFTest.Health.Agent.V1." + sessionId;
        public static string HostPipe(int pid) => "MTTFTest.Health.Host.V1." + pid;
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly Task _loop;
        private int _disposed;

        public RecoveryHealthEndpoint(string pipeName, Func<long> progress, Func<string> stage,
            Func<string> detail = null)
        {
            int pid;
            long started;
            using (var process = Process.GetCurrentProcess())
            { pid = process.Id; started = process.StartTime.ToUniversalTime().Ticks; }
            _loop = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    try
                    {
                        var security = new PipeSecurity();
                        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User,
                            PipeAccessRights.FullControl, AccessControlType.Allow));
                        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid,
                                     WellKnownSidType.BuiltinAdministratorsSid })
                            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid, null),
                                PipeAccessRights.ReadWrite, AccessControlType.Allow));
                        using (var pipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1,
                            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security))
                        {
                            await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                            using (var deadline = new PipeExchangeDeadline(pipe, 2000))
                            using (var writer = new BinaryWriter(pipe, Encoding.UTF8, true))
                                writer.Write(new JavaScriptSerializer().Serialize(new RecoveryHealthSnapshot
                                {
                                    ProcessId = pid, ProcessStartUtcTicks = started,
                                    ProgressUtcTicks = progress(), Stage = stage(), Detail = detail?.Invoke()
                                }));
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch
                    {
                        try { await Task.Delay(250, _stop.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { break; }
                    }
                }
            });
        }

        public static RecoveryHealthSnapshot Probe(string pipeName, int timeoutMs = 2000)
        {
            using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.In,
                       PipeOptions.None, TokenImpersonationLevel.Identification))
            {
                pipe.Connect(timeoutMs);
                using (var deadline = new PipeExchangeDeadline(pipe, timeoutMs))
                using (var reader = new BinaryReader(pipe, Encoding.UTF8, true))
                {
                    var value = SupervisorSessionLaunchRequest.ReadBoundedString(reader);
                    return new JavaScriptSerializer().Deserialize<RecoveryHealthSnapshot>(value);
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stop.Cancel();
            // Never block a safety/exit caller on a diagnostic client.
            _ = _loop.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
        }
    }
}
