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
        internal string RunId { get; private set; }
        internal long RunEpoch { get; private set; }
        internal long Generation { get; private set; }
        private IndependentProjectStateStore _store;
        private System.Threading.Tasks.Task _manualStopPersistence;

        internal static IndependentRecoveryStartup Parse(string[] args)
        {
            var present = args.Any(value => value == "--independent-registration" || value == "--independent-ticket");
            if (!present) return null;
            if (args.Contains("--watchdog-recover") || args.Contains("--epb-recover"))
                throw new InvalidOperationException("MixedRecoveryProtocolsRejected");
            string Read(string key)
            {
                if (args.Count(value => value == key) != 1) throw new InvalidOperationException("IndependentBootstrapArgumentsInvalid");
                var index = Array.IndexOf(args, key);
                if (index + 1 >= args.Length) throw new InvalidOperationException("IndependentBootstrapArgumentsMissing");
                return args[index + 1];
            }
            var path = Read("--independent-registration");
            var nonce = Read("--independent-ticket");
            if (!Path.IsPathRooted(path) || !Guid.TryParseExact(nonce, "N", out _))
                throw new InvalidOperationException("IndependentBootstrapArgumentsInvalid");
            return new IndependentRecoveryStartup { RegistrationPath = path, Nonce = nonce };
        }

        internal void ConsumeAndBind()
        {
            if (Current != null) throw new InvalidOperationException("IndependentBootstrapAlreadyConsumed");
            Registration = IndependentExecutorRegistration.LoadTrusted(RegistrationPath);
            _store = new IndependentProjectStateStore(Registration.StateDirectory);
            var state = _store.Read();
            Registration.RequireBoundIntent(state.Intent);
            using (var process = Process.GetCurrentProcess())
                Identity = new IndependentProcessIdentity
                { Pid = process.Id, StartUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                    WindowsSessionId = process.SessionId, ExecutablePath = process.MainModule.FileName,
                    SessionToken = Guid.NewGuid().ToString("N") };
            var executableHash = SupervisorProtocol.ComputeSha256(Identity.ExecutablePath);
            var intent = _store.ConsumeLaunchTicket(state.Revision, Nonce, Identity, executableHash, DateTime.UtcNow.Ticks);
            ParentRunId = intent.RunId;
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
                state.Ticket == null || state.Ticket.Revoked || !Identity.Matches(state.Ticket.Consumer) ||
                state.Intent.RunId != RunId || state.Intent.RunEpoch != RunEpoch ||
                state.Intent.RecoveryChannels().Length == 0 || state.Transaction == null ||
                state.Transaction.RunId != RunId || state.Transaction.RunEpoch != RunEpoch ||
                state.Transaction.IntentRevision != state.Intent.Revision ||
                (state.Transaction.Phase != IndependentRecoveryPhase.LaunchPending &&
                 state.Transaction.Phase != IndependentRecoveryPhase.Verifying && state.Transaction.Phase != IndependentRecoveryPhase.Verified) ||
                (state.Transaction.Phase != IndependentRecoveryPhase.Verified && DateTime.UtcNow.Ticks >= state.Transaction.PhaseDeadlineUtcTicks))
                throw new InvalidOperationException("IndependentBootstrapAuthorityChanged");
            return state.Intent;
        }

        internal System.Threading.Tasks.Task PersistManualStopAsync(string commandId)
        {
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

        internal void RequireManualStopPersistenceCompleted()
        {
            if (_manualStopPersistence != null && _manualStopPersistence.Status != System.Threading.Tasks.TaskStatus.RanToCompletion)
                throw new InvalidOperationException("独立人工停止授权仍未持久确认，不能重新开始。");
        }

        internal IndependentRunIntent ValidateConfiguration(GlobalConfig config)
        {
            var intent = ValidateCurrent();
            if (config?.Test == null ||
                !string.Equals(Path.GetFullPath(Path.Combine(config.Test.StoreDir, config.Test.TestName)).TrimEnd('\\'),
                    Path.GetFullPath(Registration.ProjectDirectory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(UnattendedRunCheckpointStore.ComputeConfigurationHash(config),
                    Registration.ConfigurationSha256, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(Registration.DatabasePath) ||
                File.GetCreationTimeUtc(Registration.DatabasePath).Ticks != Registration.DatabaseCreationUtcTicks)
                throw new InvalidOperationException("IndependentBootstrapProjectConfigurationMismatch");
            return intent;
        }
    }
}
