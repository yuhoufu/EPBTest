using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Controller.Adaptive;
using IO.NI;

namespace Controller
{
    public sealed partial class EpbManager
    {
        private async Task ConfirmDaqRecoveryPhysicalSafetyAsync(DaqAutoRecoveryContext context)
        {
            var owner = context?.RecoveryOwner;
            if (owner == null || _powerSupply == null || _hydCoordinator == null || _acq == null)
                throw new InvalidOperationException("DaqRecoveryPhysicalSafetyOwnerOrHardwareMissing");
            var owned = owner.Contract.Channels.ToArray();
            var physical = GetAffectedHydraulicSafetyScope(context.AffectedChannels);
            if (!IsRecoveryExecutionScopeContained(owner.Contract, context.AffectedChannels, physical))
                throw new RecoveryExecutionRejectedException("DaqRecoveryPhysicalSafetyScopeNotOwned");
            var groupIds = physical.Select(GetHydraulicGroupForChannel).Distinct().ToArray();
            var hydraulics = _cfg.Test.Hydraulics.Where(group => groupIds.Contains(group.Id)).ToArray();
            var electrical = _cfg.Test.Groups.Where(group => group.Members.Any(physical.Contains)).ToArray();
            if (hydraulics.Length != groupIds.Length || hydraulics.Select(group => group.Id).Distinct().Count() != groupIds.Length ||
                physical.Any(channel => !electrical.Any(group => group.Members.Contains(channel))) ||
                electrical.SelectMany(group => group.Members).Any(channel => !physical.Contains(channel)))
                throw new RecoveryExecutionRejectedException("DaqRecoveryPhysicalSafetyMappingIncomplete");
            var identities = physical.ToDictionary(channel => channel, channel => _channelExecutionFence.Capture(channel));
            void VerifyAuthority()
            {
                if (!IsCurrentRecovery(context) || context.RunId != _activeBatchId ||
                    context.Ownerships == null || context.Ownerships.Length < groupIds.Length ||
                    context.Ownerships.Any(lease => lease.Token.IsCancellationRequested) ||
                    identities.Any(pair => !_channelExecutionFence.IsUnchanged(pair.Value)) ||
                    _timers.Keys.Concat(_runners.Keys).Concat(_hydraulicParticipants.Keys)
                        .Any(channel => physical.Contains(channel) && !owned.Contains(channel)))
                    throw new RecoveryExecutionRejectedException("DaqRecoveryPhysicalSafetyAuthorityChanged");
                AssertPowerRecoveryOwnerCurrent(owner, owned);
            }
            var commands = new List<Func<Task>>();
            foreach (var channel in physical)
                commands.Add(() =>
                {
                    if (!CommandEpbOffHighPriority(channel, "DaqJointSafety"))
                        throw new InvalidOperationException("DaqRecoveryOffUnconfirmed:" + channel);
                    return Task.CompletedTask;
                });
            foreach (var group in electrical)
                commands.Add(() => TrackRecoveryHardwareAction(physical,
                    _powerSupply.DisableGroupAsync(group.Id, "DaqJointSafety", context.Cancellation.Token)));
            foreach (var group in hydraulics)
                commands.Add(() => TrackRecoveryHardwareAction(physical,
                    _hydCoordinator.ForceReleaseAsync(group.Id, "DaqJointSafety", _acq.ReadPressureSafetySample)));
            await RunRecoverySafetyBoundaryAsync(commands, VerifyAuthority,
                () => ConfirmJointPhysicalSafetyAsync(physical, hydraulics,
                    () => { VerifyAuthority(); return true; }, context.Cancellation.Token)).ConfigureAwait(false);
        }

        internal static Task RunAffectedGroupFailureSafetyAsync(
            int[] channels, int[] electricalGroups, int hydraulicGroup,
            Func<int, bool> epbOff, Func<int, Task> powerOff, Func<int, Task> release,
            Action verifyAuthority, Func<Task<(bool ok, string error)>> confirmFeedback)
        {
            if (channels == null || channels.Length == 0 ||
                channels.Any(channel => channel < 1 || channel > 12) ||
                hydraulicGroup <= 0 ||
                epbOff == null || powerOff == null || release == null)
                throw new ArgumentException("AffectedGroupFailureSafetyScopeMissing");
            // Materialize the exact scope before any asynchronous command.
            // No mutable runtime enumeration may add a successor's outputs.
            var commands = new List<Func<Task>>();
            foreach (var channel in channels.Distinct())
            {
                var target = channel;
                commands.Add(() =>
                {
                    if (!epbOff(target))
                        throw new InvalidOperationException($"AffectedGroupEpbOffUnconfirmed:EPB{target}");
                    return Task.CompletedTask;
                });
            }
            foreach (var group in (electricalGroups ?? Array.Empty<int>()).DefaultIfEmpty(0).Distinct())
            {
                var target = group;
                commands.Add(() => target > 0 ? powerOff(target) :
                    throw new InvalidOperationException("AffectedGroupFailureElectricalScopeMissing"));
            }
            commands.Add(() => release(hydraulicGroup));
            return RunRecoverySafetyBoundaryAsync(commands, verifyAuthority, confirmFeedback);
        }

        internal static async Task RunRecoverySafetyBoundaryAsync(
            IEnumerable<Func<Task>> safeCommands, Action verifyAuthority,
            Func<Task<(bool ok, string error)>> confirmFeedback)
        {
            if (safeCommands == null || verifyAuthority == null || confirmFeedback == null)
                throw new ArgumentException("RecoverySafetyBoundaryMissing");
            var commands = safeCommands.ToArray();
            if (commands.Length == 0 || commands.Any(command => command == null))
                throw new ArgumentException("RecoverySafetyCommandsMissing");
            var failures = new List<Exception>();
            foreach (var command in commands)
            {
                // Authority failure is not an ordinary command failure: no
                // further operation may touch a successor run's outputs.
                verifyAuthority();
                try { await command().ConfigureAwait(false); }
                catch (Exception ex) { failures.Add(ex); }
            }
            verifyAuthority();
            if (failures.Count > 0)
                throw new AggregateException("RecoverySafeCommandsUnconfirmed", failures);
            var physical = await confirmFeedback().ConfigureAwait(false);
            verifyAuthority();
            if (!physical.ok) throw new InvalidOperationException(physical.error);
        }

        internal static Config.HydraulicItem[] SelectStopPhysicalSafetyHydraulics(
            IEnumerable<Config.HydraulicItem> configured)
        {
            if (configured == null) throw new ArgumentNullException(nameof(configured));
            return configured.ToArray();
        }

        // Called only after DO completion, ForceRelease and the existing single
        // power-OFF task. Never opens a competing NI task in the live Main.
        private async Task<(bool ok, string error)> ConfirmJointPhysicalSafetyForStopAsync(long generation)
        {
            // Stop releases every configured hydraulic group, including disabled
            // groups. Do not silently narrow the subsequent joint pressure proof.
            var hydraulics = SelectStopPhysicalSafetyHydraulics(_cfg.Test.Hydraulics);
            var stopped = _stopSafetyProductionState;
            var channels = Enumerable.Range(1, 12)
                .Where(channel => _cfg.Test.GetEpbRecord(channel).Enabled)
                .Concat(stopped?.Generation == generation ? stopped.Channels : Array.Empty<int>())
                .Distinct().OrderBy(channel => channel).ToArray();
            return await ConfirmJointPhysicalSafetyAsync(channels, hydraulics,
                () => generation == Interlocked.Read(ref _stopSafetyGeneration),
                CancellationToken.None).ConfigureAwait(false);
        }

        // This is evidence-only: callers must complete every OFF/release command
        // first and retain exclusive ownership until subsequent energization.
        private async Task<(bool ok, string error)> ConfirmJointPhysicalSafetyAsync(
            int[] channels, Config.HydraulicItem[] hydraulics,
            Func<bool> authorityCurrent, CancellationToken token)
        {
            if (channels == null || channels.Length == 0 ||
                channels.Any(channel => channel < 1 || channel > 12) ||
                channels.Distinct().Count() != channels.Length || hydraulics == null ||
                hydraulics.Select(group => group.Id).Distinct().Count() != hydraulics.Length ||
                authorityCurrent == null || _acq == null)
                return (false, "PhysicalSafetyScopeOrAcquisitionMissing");
            var affectedGroups = channels.Select(GetHydraulicGroupForChannel)
                .Concat(hydraulics.Select(group => group.Id)).Distinct().ToArray();
            Func<bool> hardwareActionsPending = () => affectedGroups.Any(group =>
                _affectedGroupStageActions.TryGetValue(group, out var actions) && actions.HasPending);
            if (hardwareActionsPending())
                return (false, "PhysicalSafetyHardwareActionPending");
            var names = channels.Select(channel => "EPB" + channel + "_current")
                .Concat(hydraulics.Select(group => "Pressure_" + group.Id)).ToArray();
            var currentLimit = Math.Min(EpbProgramSafetySettings.DefaultOffCurrentClearThresholdA,
                _programSafetySettings.OffCurrentClearThresholdA);
            var limits = channels.Select(channel => currentLimit)
                .Concat(hydraulics.Select(group => group.ReleaseSafePressureBar)).ToArray();
            var maxAge = hydraulics.Select(group => group.PressureSampleMaxAgeMs).DefaultIfEmpty(100).Min();
            var stable = Math.Max(100, hydraulics.Select(group => group.ReleaseStableMs).DefaultIfEmpty(100).Max());
            var timeout = hydraulics.Select(group => group.ReleaseTimeoutMs).DefaultIfEmpty(5000).Max();
            var commandCompletedMs = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
            var window = new PhysicalSafetyBatchWindow(names, channels.Length,
                limits, commandCompletedMs, maxAge, stable);
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds <= timeout)
            {
                token.ThrowIfCancellationRequested();
                if (!authorityCurrent())
                    return (false, "PhysicalSafetyAuthoritySuperseded");
                if (hardwareActionsPending())
                    return (false, "PhysicalSafetyHardwareActionPending");
                var now = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
                var frame = new Dictionary<string, PhysicalSafetyBatchSample>(StringComparer.Ordinal);
                for (var index = 0; index < names.Length; index++)
                {
                    var sample = _acq.ReadPhysicalSafetySample(names[index]);
                    frame.Add(names[index], sample);
                }
                if (window.Observe(frame, now) && clock.ElapsedMilliseconds <= timeout &&
                    !token.IsCancellationRequested && authorityCurrent() &&
                    !hardwareActionsPending()) return (true, string.Empty);
                await Task.Delay(10, token).ConfigureAwait(false);
            }
            return (false, "FreshCurrentAndPressureSafetyUnconfirmed");
        }
    }
}
