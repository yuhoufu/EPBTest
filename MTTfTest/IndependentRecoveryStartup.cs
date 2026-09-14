using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Config;
using MTTFTest.Watchdog.Protocol;

namespace MTEmbTest
{
    internal sealed class IndependentRecoveryStartup
    {
        internal static IndependentRecoveryStartup Current { get; private set; }
        internal string RegistrationPath { get; private set; }
        internal string Nonce { get; private set; }
        internal IndependentExecutorRegistration Registration { get; private set; }
        internal IndependentProcessIdentity Identity { get; private set; }
        internal string ParentRunId { get; private set; }
        internal string RootRunId { get; private set; }
        internal bool IsRecoveryLaunch => Nonce != null;
        internal string RunId { get; private set; }
        internal long RunEpoch { get; private set; }
        internal long Generation { get; private set; }
        private IndependentProjectStateStore _store;
        private System.Threading.Tasks.Task _manualStopPersistence;

        internal IDisposable ObserveCooperativeStop(Func<string, System.Threading.Tasks.Task<bool>> stop)
            => new CooperativeObserver(this, stop);

        private sealed class CooperativeObserver : IDisposable
        {
            private readonly IndependentRecoveryStartup _owner;
            private readonly Func<string, System.Threading.Tasks.Task<bool>> _stop;
            private readonly System.Threading.Timer _timer;
            private int _busy, _disposed;
            private string _lastRequest;

            internal CooperativeObserver(IndependentRecoveryStartup owner, Func<string, System.Threading.Tasks.Task<bool>> stop)
            {
                _owner = owner;
                _stop = stop ?? throw new ArgumentNullException(nameof(stop));
                _timer = new System.Threading.Timer(Tick, null, 1000, 1000);
            }

            private async void Tick(object unused)
            {
                if (System.Threading.Volatile.Read(ref _disposed) != 0 ||
                    System.Threading.Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
                try
                {
                    var state = _owner._store.Read();
                    var tx = state?.Transaction;
                    if (tx == null || tx.Phase != IndependentRecoveryPhase.CooperativeStop ||
                        !state.SafetyCleanupPending || state.Controller?.Matches(_owner.Identity) != true ||
                        tx.RunId != _owner.RunId || tx.RunEpoch != _owner.RunEpoch ||
                        tx.RequestId == _lastRequest || DateTime.UtcNow.Ticks >= tx.PhaseDeadlineUtcTicks ||
                        System.Threading.Volatile.Read(ref _disposed) != 0) return;
                    _lastRequest = tx.RequestId;
                    // This runs off the UI and status publisher. A hung stop remains
                    // bounded by the external executor's process-retirement deadline.
                    if (await _stop(tx.RequestId).ConfigureAwait(false))
                        _owner._store.AcknowledgeCooperativeStop(_owner.Identity, tx.RunId, tx.RunEpoch,
                            tx.RequestId, tx.Generation, DateTime.UtcNow.Ticks);
                }
                catch (Exception error)
                {
                    Trace.TraceError("IndependentCooperativeStop: " + error.GetType().Name + ":" + error.Message);
                }
                finally { System.Threading.Interlocked.Exchange(ref _busy, 0); }
            }

            public void Dispose()
            {
                System.Threading.Interlocked.Exchange(ref _disposed, 1);
                _timer.Dispose();
            }
        }

        internal static IndependentRecoveryStartup Parse(string[] args)
        {
            IndependentInstallationBinding installed;
            using (var process = Process.GetCurrentProcess())
                installed = IndependentInstallationBinding.Resolve(process.MainModule.FileName);
            var present = args.Any(value => value == "--independent-registration" || value == "--independent-ticket" || value == "--independent-installation");
            if (!present)
            {
                if (installed == null) return null;
                if (args.Contains("--watchdog-recover") || args.Contains("--epb-recover"))
                    throw new InvalidOperationException("IndependentExecutorOwnsProcessRelaunch");
                return new IndependentRecoveryStartup { RegistrationPath = installed.RegistrationPath };
            }
            if (args.Contains("--watchdog-recover") || args.Contains("--epb-recover"))
                throw new InvalidOperationException("MixedRecoveryProtocolsRejected");
            string Read(string key)
            {
                if (args.Count(value => value == key) != 1) throw new InvalidOperationException("IndependentBootstrapArgumentsInvalid");
                var index = Array.IndexOf(args, key);
                if (index + 1 >= args.Length) throw new InvalidOperationException("IndependentBootstrapArgumentsMissing");
                return args[index + 1];
            }
            if (args.Contains("--independent-installation"))
            {
                if (args.Contains("--independent-registration") || args.Contains("--independent-ticket"))
                    throw new InvalidOperationException("MixedIndependentLaunchModesRejected");
                var installedPath = Read("--independent-installation");
                if (installed != null && !string.Equals(installedPath, installed.RegistrationPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("IndependentInstallationArgumentMismatch");
                if (!Path.IsPathRooted(installedPath)) throw new InvalidOperationException("IndependentBootstrapArgumentsInvalid");
                return new IndependentRecoveryStartup { RegistrationPath = installedPath };
            }
            var path = Read("--independent-registration");
            if (installed != null && !string.Equals(path, installed.RegistrationPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("IndependentInstallationArgumentMismatch");
            var nonce = Read("--independent-ticket");
            if (!Path.IsPathRooted(path) || !Guid.TryParseExact(nonce, "N", out _))
                throw new InvalidOperationException("IndependentBootstrapArgumentsInvalid");
            return new IndependentRecoveryStartup { RegistrationPath = path, Nonce = nonce };
        }

        internal void ConsumeAndBind()
        {
            if (Current != null) throw new InvalidOperationException("IndependentBootstrapAlreadyConsumed");
            Registration = IndependentExecutorRegistration.LoadTrusted(RegistrationPath);
            using (var user = System.Security.Principal.WindowsIdentity.GetCurrent())
                if (user.User.Value != Registration.InteractiveUserSid)
                    throw new UnauthorizedAccessException("IndependentBootstrapInteractiveUserMismatch");
            _store = new IndependentProjectStateStore(Registration.StateDirectory);
            var state = _store.Read();
            if (state == null) throw new InvalidOperationException("IndependentInstalledStateMissing");
            using (var process = Process.GetCurrentProcess())
                Identity = new IndependentProcessIdentity
                { Pid = process.Id, StartUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                    WindowsSessionId = process.SessionId, ExecutablePath = process.MainModule.FileName,
                    SessionToken = Guid.NewGuid().ToString("N") };
            var executableHash = SupervisorProtocol.ComputeSha256(Identity.ExecutablePath);
            if (!IsRecoveryLaunch)
            {
                if (state.Maintenance || state.SafetyCleanupPending || state.Transaction?.IsTerminal == false ||
                    state.Intent?.RecoveryChannels().Length > 0)
                    throw new InvalidOperationException("IndependentActiveRunRequiresRecoveryTicket");
                if (!string.Equals(Identity.ExecutablePath, Registration.ExecutablePath, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(executableHash, Registration.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("IndependentInstalledExecutableMismatch");
                IndependentExecutionFence.AttachCurrent(Registration.InstallationId, Identity);
                Current = this;
                return;
            }
            Registration.RequireBoundIntent(state.Intent);
            var intent = _store.ConsumeLaunchTicket(state.Revision, Nonce, Identity, executableHash, DateTime.UtcNow.Ticks);
            ParentRunId = intent.RunId;
            RootRunId = state.RootRunId;
            RunEpoch = checked(intent.RunEpoch + 1);
            RunId = Guid.NewGuid().ToString("N");
            Generation = state.Transaction.Generation;
            IndependentExecutionFence.AttachCurrent(Registration.InstallationId, Identity);
            _store.CommitReplacementRun(_store.Read().Revision, Identity, RunId, RunEpoch, DateTime.UtcNow.Ticks);
            Current = this;
            ValidateCurrent();
        }

        internal IndependentRunIntent ValidateCurrent()
        {
            IndependentExecutionFence.RequireCurrentAuthority();
            var state = _store.Read();
            Registration.RequireBoundIntent(state.Intent);
            if (state.Maintenance || state.SafetyCleanupPending || !Identity.Matches(state.Controller) ||
                state.Intent.RunId != RunId || state.Intent.RunEpoch != RunEpoch ||
                state.Intent.RecoveryChannels().Length == 0)
                throw new InvalidOperationException("IndependentBootstrapAuthorityChanged");
            if (IsRecoveryLaunch && (state.Ticket == null || state.Ticket.Revoked || !Identity.Matches(state.Ticket.Consumer) || state.Transaction == null ||
                state.Transaction.RunId != RunId || state.Transaction.RunEpoch != RunEpoch ||
                state.Transaction.IntentRevision != state.Intent.Revision ||
                (state.Transaction.Phase != IndependentRecoveryPhase.LaunchPending &&
                 state.Transaction.Phase != IndependentRecoveryPhase.Verifying && state.Transaction.Phase != IndependentRecoveryPhase.Verified) ||
                (state.Transaction.Phase != IndependentRecoveryPhase.Verified && DateTime.UtcNow.Ticks >= state.Transaction.PhaseDeadlineUtcTicks)))
                throw new InvalidOperationException("IndependentBootstrapAuthorityChanged");
            return state.Intent;
        }

        internal DataOperation.RunChainIdentity ArmManualRun(GlobalConfig config, int[] channels, int learnCycles)
        {
            RequireManualStopPersistenceCompleted();
            IndependentExecutionFence.RequireCurrentAuthority();
            ValidateProjectConfiguration(config);
            var state = _store.Read();
            var runId = Guid.NewGuid();
            var epoch = checked((state?.Intent?.RunEpoch ?? 0) + 1);
            var intent = new IndependentRunIntent
            {
                Revision = 1, ProjectDirectory = Registration.ProjectDirectory,
                DatabasePath = Registration.DatabasePath, DatabaseCreationUtcTicks = Registration.DatabaseCreationUtcTicks,
                ExecutablePath = Registration.ExecutablePath, ConfigurationSha256 = Registration.ConfigurationSha256,
                RunId = runId.ToString("N"), RunEpoch = epoch, SelectedChannels = channels.ToArray(), Armed = true,
                PeriodMs = config.Test.PeriodMs,
                StartupBudgetMs = checked(Registration.StartupPositioningBudgetMs +
                    (Math.Max(0L, learnCycles) + 2) * config.Test.PeriodMs)
            };
            _store.ArmManualRun(state.Revision, intent, Identity, DateTime.UtcNow.Ticks);
            Nonce = null; RunId = intent.RunId; RootRunId = RunId; ParentRunId = Guid.Empty.ToString("N");
            RunEpoch = epoch; Generation = 0;
            ValidateCurrent();
            UnattendedRecoveryCoordinator.SetRecoveryProcessMode(false);
            return new DataOperation.RunChainIdentity(runId, runId, Guid.Empty, 0, epoch);
        }

        internal System.Threading.Tasks.Task PersistManualStopAsync(string commandId)
        {
            if (RunId == null) return System.Threading.Tasks.Task.CompletedTask;
            var task = System.Threading.Tasks.Task.Run(() =>
                _store.RecordControllerManualStop(Identity, RunId, RunEpoch, commandId, DateTime.UtcNow.Ticks));
            _manualStopPersistence = task;
            task.ContinueWith(failed => ProjectLogHub.Write(ProjectLogLevel.Error,
                    "独立人工停止授权写入失败：" + failed.Exception.GetBaseException().Message, "独立恢复", failed.Exception),
                System.Threading.CancellationToken.None,
                System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted,
                System.Threading.Tasks.TaskScheduler.Default);
            return task;
        }

        internal System.Threading.Tasks.Task PersistChannelSelectionAsync(int channel, bool selected)
        {
            var runId = RunId; var epoch = RunEpoch;
            if (runId == null) return System.Threading.Tasks.Task.CompletedTask;
            var command = Guid.NewGuid().ToString("N");
            return System.Threading.Tasks.Task.Run(() =>
                _store.SetControllerSelection(Identity, runId, epoch, channel, selected, command, DateTime.UtcNow.Ticks));
        }

        internal System.Threading.Tasks.Task PersistManualPauseAsync(bool paused, string commandId)
        {
            if (RunId == null) throw new InvalidOperationException("IndependentPauseRunMissing");
            return System.Threading.Tasks.Task.Run(() =>
                _store.SetControllerManualPause(Identity, RunId, RunEpoch, paused, commandId, DateTime.UtcNow.Ticks));
        }

        internal void RequireManualStopPersistenceCompleted()
        {
            if (_manualStopPersistence != null && _manualStopPersistence.Status != System.Threading.Tasks.TaskStatus.RanToCompletion)
                throw new InvalidOperationException("独立人工停止授权仍未持久确认，不能重新开始。");
        }

        internal IndependentRunIntent ValidateConfiguration(GlobalConfig config)
        {
            var intent = ValidateCurrent();
            ValidateProjectConfiguration(config);
            return intent;
        }

        private void ValidateProjectConfiguration(GlobalConfig config)
        {
            if (config?.Test == null ||
                !string.Equals(Path.GetFullPath(Path.Combine(config.Test.StoreDir, config.Test.TestName)).TrimEnd('\\'),
                    Path.GetFullPath(Registration.ProjectDirectory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(UnattendedRunCheckpointStore.ComputeIndependentConfigurationHash(config),
                    Registration.ConfigurationSha256, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(Registration.DatabasePath) ||
                File.GetCreationTimeUtc(Registration.DatabasePath).Ticks != Registration.DatabaseCreationUtcTicks)
                throw new InvalidOperationException("IndependentBootstrapProjectConfigurationMismatch");
        }
    }
}
