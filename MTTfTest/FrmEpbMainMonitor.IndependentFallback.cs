using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Config;
using Controller;
using MTTFTest.Watchdog.Protocol;

namespace MTEmbTest
{
    public partial class FrmEpbMainMonitor
    {
        private int _independentFallbackStarted;
        private void StartIndependentFallbackBridge()
        {
            if (Interlocked.Exchange(ref _independentFallbackStarted, 1) != 0) return;
            var directory = Path.Combine(_cfg.Test.StoreDir, _cfg.Test.TestName, "WatchdogSessions");
            Task.Run(async () =>
            {
                string handled = null;
                Task preparation = null;
                long sequence = 0;
                using (var process = Process.GetCurrentProcess())
                {
                    var pid = process.Id;
                    var ticks = process.StartTime.ToUniversalTime().Ticks;
                    while (!IsDisposed && !Disposing)
                    {
                        try
                        {
                            var checkpoint = UnattendedRunCheckpointStore.Load();
                            var heartbeat = CreateWatchdogHeartbeat();
                            heartbeat.ProcessId = pid;
                            heartbeat.ProcessStartUtcTicks = ticks;
                            heartbeat.Sequence = ++sequence;
                            var status = new IndependentFallbackStatus
                            {
                                ProcessId = pid, ProcessStartUtcTicks = ticks,
                                ExecutablePath = process.MainModule.FileName,
                                RunId = checkpoint?.RunId, RunEpoch = checkpoint?.RunEpoch ?? 0,
                                SessionId = checkpoint?.WatchdogSessionId,
                                Armed = checkpoint?.Armed == true && !IsOperatorStopRequested &&
                                    !heartbeat.ManualPauseActive && !heartbeat.ManualPausePending &&
                                    string.Equals(checkpoint.StoreDir, _cfg.Test.StoreDir, StringComparison.OrdinalIgnoreCase) &&
                                    string.Equals(checkpoint.TestName, _cfg.Test.TestName, StringComparison.OrdinalIgnoreCase),
                                Channels = checkpoint?.SelectedChannels ?? Array.Empty<int>(),
                                UpdatedUtcTicks = DateTime.UtcNow.Ticks, Heartbeat = heartbeat
                            };
                            BoundedJson.Write(IndependentFallbackProtocol.StatusPath(directory), status);
                            var path = IndependentFallbackProtocol.RequestPath(directory);
                            if (File.Exists(path))
                            {
                                var request = BoundedJson.Read<IndependentFallbackRequest>(path);
                                if (request.Id != handled && (preparation == null || preparation.IsCompleted) &&
                                    IndependentFallbackProtocol.Matches(request, status, DateTime.UtcNow.Ticks) &&
                                    IndependentFallbackProtocol.IsAlive(request.GuardProcessId, request.GuardStartUtcTicks))
                                {
                                    handled = request.Id;
                                    preparation = PrepareIndependentFallbackAsync(directory, request);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            ProjectLogHub.Write(ProjectLogLevel.Warning, "IndependentFallback: " + ex.Message, "独立兜底");
                        }
                        await Task.Delay(1000).ConfigureAwait(false);
                    }
                    if (preparation != null) await preparation.ConfigureAwait(false);
                }
            });
        }

        private async Task PrepareIndependentFallbackAsync(string directory, IndependentFallbackRequest request)
        {
            var receipt = new IndependentFallbackReceipt { RequestId = request.Id, RunId = request.RunId,
                ProcessId = request.ProcessId, ProcessStartUtcTicks = request.ProcessStartUtcTicks };
            try
            {
                _epb.RevokeExecutionForExternalRecovery("IndependentDatabaseStall:" + request.Id);
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25)))
                {
                    var context = new StopContext { Source = StopSource.SystemFault,
                        Reason = "独立数据库监督确认持续停滞", Initiator = "FallbackGuard",
                        RunId = request.RunId, CorrelationId = request.Id, RequireFreshSafetyEvidence = true };
                    var safety = await _epb.StopAllAsync(context, timeout.Token).ConfigureAwait(false);
                    // Join an already running stop first, then obtain this
                    // request's own fresh transaction; never overlap two cores.
                    if (safety != null && safety.CorrelationId != request.Id)
                        safety = await _epb.StopAllAsync(context, timeout.Token).ConfigureAwait(false);
                    if (safety == null || !safety.FullyConfirmed || !safety.LogicalQuiescenceConfirmed ||
                        safety.DataContinuityCompromised || safety.ReusedPreviousResult ||
                        safety.CorrelationId != request.Id || safety.StartedUtc.Ticks < request.RequestedUtcTicks)
                        throw new InvalidOperationException("IndependentFallbackFreshSafetyUnconfirmed");
                }
                await QuiesceAndFlushForUnattendedRestartAsync().ConfigureAwait(false);
                if (!await _epb.ShutdownPersistenceAsync(10000).ConfigureAwait(false))
                    throw new IOException("IndependentFallbackPersistenceNotDrained");
                _epb.ReleaseHardwareForRestart();
                if (IsOperatorStopRequested ||
                    !UnattendedRunCheckpointStore.TryRegisterIndependentRestart(_cfg, request.Id, request.RunId,
                        out var intent, out var error))
                    throw new InvalidOperationException("IndependentFallbackAuthorizationUnavailable");
                receipt.Nonce = intent.Nonce;
                receipt.Ready = true;
                receipt.Detail = "FreshSafetyConfirmed;PersistenceDrained;HardwareReleased;ExecutionRevoked";
            }
            catch (Exception ex) { receipt.Detail = ex.GetBaseException().Message; }
            receipt.UpdatedUtcTicks = DateTime.UtcNow.Ticks;
            try { BoundedJson.Write(IndependentFallbackProtocol.ReceiptPath(directory), receipt); }
            catch (Exception ex) { ProjectLogHub.Write(ProjectLogLevel.Error, ex.ToString(), "独立兜底回执"); }
        }
    }
}
