using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Watchdog
{
    // One bounded connection, independent of hardware/control dispatch. Can only
    // renew an existing kernel-owned lease; cannot start maintenance.
    internal sealed class PressureMaintenanceHeartbeatServer
    {
        private readonly Func<int, long, string, bool> _authorizeUi;
        private readonly Func<PressureMaintenanceHeartbeat, PressureMaintenanceLease> _renew;
        private readonly string _pipeName;
        internal PressureMaintenanceHeartbeatServer(Func<int, long, string, bool> authorizeUi,
            Func<PressureMaintenanceHeartbeat, PressureMaintenanceLease> renew, string pipeName = null)
        {
            _authorizeUi = authorizeUi ?? throw new ArgumentNullException(nameof(authorizeUi));
            _renew = renew ?? throw new ArgumentNullException(nameof(renew));
            _pipeName = pipeName ?? PressureMaintenanceTransport.SupervisorPipeName;
        }
        internal async Task RunAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using (var pipe = CreatePipe())
                    using (token.Register(() => { try { pipe.Dispose(); } catch { } }))
                    {
                        await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                        {
                            deadline.CancelAfter(PressureMaintenanceTransport.TimeoutMilliseconds);
                            using (deadline.Token.Register(() => { try { pipe.Dispose(); } catch { } }))
                            {
                                var request = PressureMaintenanceTransport.Decode<PressureMaintenanceHeartbeatRequest>(
                                    await PressureMaintenanceTransport.ReadFrameAsync(pipe, deadline.Token).ConfigureAwait(false));
                                var response = new PressureMaintenanceHeartbeatResponse
                                { RequestId = request?.RequestId ?? string.Empty, ChallengeNonce = request?.ChallengeNonce ?? string.Empty };
                                try
                                {
                                    if (request?.IsStructurallyValid() != true) throw new InvalidDataException("MaintenanceHeartbeatRequestInvalid");
                                    var heartbeat = request.Heartbeat;
                                    if (PipePeerIdentity.ClientProcessId(pipe) != heartbeat.UiProcessId ||
                                        !_authorizeUi(heartbeat.UiProcessId, heartbeat.UiProcessStartUtcTicks, heartbeat.SessionId))
                                        throw new UnauthorizedAccessException("MaintenanceHeartbeatUiNotAuthorized");
                                    deadline.Token.ThrowIfCancellationRequested();
                                    response.Lease = _renew(heartbeat);
                                    response.Accepted = true;
                                    response.Detail = "DurableMaintenanceLeaseRenewed;NotAnOutputCommand";
                                    if (!response.Matches(request, DateTime.UtcNow.Ticks))
                                        throw new InvalidDataException("MaintenanceHeartbeatRenewalInvalid");
                                }
                                catch (Exception ex)
                                {
                                    response.Accepted = false; response.Lease = null;
                                    response.Detail = ex.GetBaseException().Message;
                                    if (response.Detail.Length > 2048) response.Detail = response.Detail.Substring(0, 2048);
                                }
                                await PressureMaintenanceTransport.WriteFrameAsync(pipe, response, deadline.Token).ConfigureAwait(false);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
                catch (IOException) { }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    // Invalid traffic must not turn into a log/retry storm.
                    try { await Task.Delay(100, token).ConfigureAwait(false); } catch (OperationCanceledException) { }
                }
            }
        }
        private NamedPipeServerStream CreatePipe()
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
            return new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, PressureMaintenanceTransport.MaximumBytes, PressureMaintenanceTransport.MaximumBytes, security);
        }
    }
}
