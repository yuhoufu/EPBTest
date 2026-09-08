using System;
using System.Linq;

namespace MTTFTest.RecoveryControl
{
    public static class RecoveryDecisionPolicy
    {
        private static readonly long ClockToleranceTicks = TimeSpan.FromSeconds(5).Ticks;

        // Completion hands back an already running, proven main; it starts no
        // new action. Its old Verify deadline may have elapsed while the Guard
        // was absent, so require fresh business evidence instead of reopening
        // or extending that action deadline.
        public static bool HasFreshVerificationEvidence(RecoveryControlState state, DateTime nowUtc)
        {
            var tx = state?.Transaction;
            var observed = state?.Observation;
            if (nowUtc.Kind != DateTimeKind.Utc || state?.Intent?.DesiredState != RecoveryDesiredState.Run ||
                tx?.Stage != RecoveryStage.Verify || tx.VerificationStartedUtcTicks <= 0 || tx.VerifiedBusinessCommitCount < 2 ||
                tx.VerificationEvidenceMaxAgeSeconds <= 0 || tx.VerificationEvidenceMaxAgeSeconds > 3600 ||
                observed == null || observed.Expired || observed.ChannelClocks == null || observed.ChannelClocks.Any(c => c == null) ||
                observed.LastVerifiedBusinessCommitUtcTicks <= tx.VerificationStartedUtcTicks ||
                observed.LastVerifiedBusinessCommitUtcTicks > nowUtc.Ticks + ClockToleranceTicks)
                return false;
            var snapshot = observed.LastSnapshot;
            if (!IsFreshSnapshot(state, snapshot, nowUtc.Ticks,
                new RecoveryGuardSettings { SnapshotMaxAgeSeconds = tx.VerificationEvidenceMaxAgeSeconds })) return false;
            var eligible = snapshot.Channels.Where(c => c.Eligible).ToArray();
            if (eligible.Length == 0) return false;
            var maxAge = TimeSpan.FromSeconds(tx.VerificationEvidenceMaxAgeSeconds).Ticks;
            foreach (var channel in eligible)
            {
                var clocks = observed.ChannelClocks.Where(c => c.Channel == channel.Channel).ToArray();
                if (clocks.Length != 1 || clocks[0].PersistedProgressUtcTicks <= tx.VerificationStartedUtcTicks ||
                    clocks[0].PersistedProgressUtcTicks > nowUtc.Ticks + ClockToleranceTicks) return false;
                // A bounded pressure hold can legitimately have no new commit,
                // but must retain its deadline and have fresh real samples.
                var anchor = EffectiveProgressAnchor(channel, clocks[0], nowUtc.Ticks);
                if (anchor <= tx.VerificationStartedUtcTicks || anchor > nowUtc.Ticks + ClockToleranceTicks ||
                    nowUtc.Ticks - anchor >= maxAge) return false;
            }
            return true;
        }

        // Caller holds the short control-store transaction. No process, hardware
        // or pipe calls occur here. The liveness result was obtained separately.
        internal static RecoveryDecision Observe(RecoveryControlState state,
            RecoveryObservationSnapshot snapshot, ProcessObservation process, string bootId,
            DateTime nowUtc, RecoveryGuardSettings settings, bool maintenance)
        {
            settings.Validate();
            var now = nowUtc.Ticks;
            if (nowUtc.Kind != DateTimeKind.Utc || !Guid.TryParse(bootId, out _))
                return Decision("ClockOrBootIdentityInvalid");
            var intent = state.Intent;
            if (intent == null) return Decision("Unarmed");
            if ((intent.DesiredState == RecoveryDesiredState.Run || intent.DesiredState == RecoveryDesiredState.Paused) &&
                RecoveryRevocationSignal.HasStopRequest(state))
                return Decision("IndependentStopObserved");
            if (intent.DesiredState != RecoveryDesiredState.Run)
                return Decision("Intent" + intent.DesiredState);
            if (RecoveryRevocationSignal.IsPaused(state.Token()))
                return Decision("IndependentPauseObserved");
            var observed = state.Observation;
            if (observed == null || observed.AuthorizationId != intent.AuthorizationId)
            {
                observed = new RecoveryGuardObservation
                {
                    AuthorizationId = intent.AuthorizationId,
                    BootId = bootId,
                    LastGuardUtcTicks = now
                };
                state.Observation = observed;
            }
            if (observed.Expired) return Decision("SupervisionExpired");
            if (observed.LastGuardUtcTicks > now + ClockToleranceTicks ||
                observed.LastTrustedRunUtcTicks > now + ClockToleranceTicks)
                return Decision("ClockRollback");
            if (now - observed.LastGuardUtcTicks >= TimeSpan.FromSeconds(settings.SupervisionExpirySeconds).Ticks)
            {
                observed.Expired = true;
                return Decision("SupervisionExpired");
            }
            if (!string.Equals(observed.BootId, bootId, StringComparison.OrdinalIgnoreCase))
            {
                observed.CrossBootAdmission = true;
                observed.CrossBootAdmissionAnchorUtcTicks = observed.LastGuardUtcTicks;
            }
            // Record continuous Guard supervision, including cooldown. This is
            // never used as evidence of business progress or initial trust.
            observed.LastGuardUtcTicks = now;
            observed.BootId = bootId;
            if (maintenance) return Decision("MaintenanceInhibited");
            if (process == ProcessObservation.Unknown) return Decision("ProcessIdentityUncertain");

            if (process == ProcessObservation.ExactAlive && IsFreshSnapshot(state, snapshot, now, settings))
            {
                var previous = observed.LastSnapshot;
                if (previous != null && previous.MainProcess.Matches(snapshot.MainProcess) &&
                    (snapshot.Sequence < previous.Sequence || snapshot.SourceVersion < previous.SourceVersion ||
                     snapshot.SourceUtcTicks < previous.SourceUtcTicks))
                    return Decision("SnapshotRollback");
                if (previous == null || !previous.MainProcess.Matches(snapshot.MainProcess))
                {
                    observed.LastSnapshot = snapshot;
                    observed.LastSnapshotSequence = snapshot.Sequence;
                    observed.ChannelClocks.Clear();
                    foreach (var channel in snapshot.Channels)
                        observed.ChannelClocks.Add(new RecoveryObservedChannel { Channel = channel.Channel });
                }
                else if (snapshot.Sequence > previous.Sequence)
                {
                    if (!AdvanceChannels(observed, previous, snapshot, now))
                        return Decision("ChannelProgressInvalid");
                    observed.LastSnapshot = snapshot;
                    observed.LastSnapshotSequence = snapshot.Sequence;
                }
                var eligible = snapshot.Channels.Where(c => c.Eligible).ToArray();
                if (eligible.Length == 0)
                    return Decision(snapshot.Channels.All(c => c.Completed) ? "AllCompleted" : "NoEligibleChannels");
                var anchors = eligible.Select(c => EffectiveProgressAnchor(c,
                    observed.ChannelClocks.Single(p => p.Channel == c.Channel), now)).ToArray();
                if (anchors.All(t => t > 0))
                {
                    var anchor = anchors.Min();
                    if (anchor > observed.LastTrustedRunUtcTicks)
                    {
                        observed.Established = true;
                        observed.LastTrustedRunUtcTicks = anchor;
                        observed.LastTrustedSnapshot = snapshot;
                        observed.CrossBootAdmission = false;
                    }
                    var commits = eligible.Select(c => observed.ChannelClocks.Single(p => p.Channel == c.Channel)
                        .PersistedProgressUtcTicks).ToArray();
                    if (commits.All(t => t > 0))
                    {
                        if (commits.Min() > observed.LastVerifiedBusinessCommitUtcTicks &&
                            state.Transaction?.Stage == RecoveryStage.Verify &&
                            commits.Min() > state.Transaction.VerificationStartedUtcTicks)
                            state.Transaction.VerifiedBusinessCommitCount++;
                        observed.LastVerifiedBusinessCommitUtcTicks = Math.Max(
                            observed.LastVerifiedBusinessCommitUtcTicks, commits.Min());
                    }
                }
            }

            // Keep a previously observed terminal participation set effective
            // even if the main dies before it can publish the intent tombstone.
            if (observed.LastSnapshot?.Channels?.Length > 0 &&
                !observed.LastSnapshot.Channels.Any(c => c.Eligible))
                return Decision(observed.LastSnapshot.Channels.All(c => c.Completed) ? "AllCompleted" : "NoEligibleChannels");
            if (!observed.Established) return Decision("WaitingForTrustedProgress");
            var active = state.Transaction?.Active == true;
            if (active)
            {
                if (settings.Mode == RecoveryGuardMode.ObserveOnly) return Decision("ObserveOnlyTransaction");
                if (state.Transaction.AuthorizationId != intent.AuthorizationId ||
                    state.Transaction.IntentVersion != intent.IntentVersion)
                    return Decision("TransactionAuthorizationMismatch");
                if (state.Transaction.NextAttemptUtcTicks > now)
                    return Decision("Cooldown");
                return new RecoveryDecision { Code = "ContinueTransaction", CanContinue = true };
            }
            // Only cross-boot admission uses the 60-minute outage window. A
            // continuously running Guard cannot extend initial admission by
            // repeatedly reporting its own heartbeat.
            var admissionSeconds = observed.CrossBootAdmission
                ? settings.SupervisionExpirySeconds : settings.FirstAdmissionSeconds;
            var admissionAnchor = observed.CrossBootAdmission
                ? observed.CrossBootAdmissionAnchorUtcTicks : observed.LastTrustedRunUtcTicks;
            if (now - admissionAnchor >= TimeSpan.FromSeconds(admissionSeconds).Ticks)
                return Decision("FirstAdmissionExpired");
            var stalled = process == ProcessObservation.Exited ||
                now - observed.LastTrustedRunUtcTicks >= TimeSpan.FromSeconds(settings.BusinessStallSeconds).Ticks;
            if (!stalled)
            {
                observed.SuspectCount = 0;
                observed.SuspectSinceUtcTicks = 0;
                return Decision("Healthy");
            }
            // A retry of the same scan must not manufacture two confirmations.
            if (observed.SuspectCount == 0)
            {
                observed.SuspectSinceUtcTicks = now;
                observed.SuspectCount = 1;
            }
            else if (now - observed.SuspectSinceUtcTicks >=
                     TimeSpan.FromSeconds(settings.ScanSeconds * observed.SuspectCount).Ticks)
                observed.SuspectCount++;
            if (observed.SuspectCount < settings.ConfirmationCount) return Decision("Suspect");
            if (settings.Mode == RecoveryGuardMode.ObserveOnly) return Decision("WouldTakeOver");
            if (process == ProcessObservation.ExactAlive && settings.Mode != RecoveryGuardMode.RecoverStalled)
                return Decision("StalledRecoveryDisabled");
            return new RecoveryDecision { Code = "ReadyToClaim", CanClaim = true };
        }

        public static bool IsFreshSnapshot(RecoveryControlState state, RecoveryObservationSnapshot snapshot,
            long now, RecoveryGuardSettings settings)
        {
            if (snapshot?.SchemaVersion != 1 || !snapshot.SourceAvailable || !state.Matches(snapshot.Authorization) ||
                snapshot.Sequence <= 0 || snapshot.SourceVersion <= 0 || snapshot.MainProcess?.IsValid() != true ||
                !snapshot.MainProcess.Matches(state.Intent.MainProcess) || snapshot.RunId != state.Intent.RunId ||
                snapshot.ConfigurationIdentity != state.Intent.ConfigurationIdentity || snapshot.Channels == null ||
                snapshot.Channels.Length == 0 || snapshot.Channels.Length > 12 ||
                snapshot.Channels.Any(c => c == null || c.Channel < 1 || c.Channel > 12 ||
                    c.SampleSequence < 0 || c.SampleGeneration < 0 || c.ControlSequence < 0 || c.PersistedSequence < 0 ||
                    (c.Eligible && (c.Completed || c.PermanentlyIsolated || c.ManuallyExcluded))) ||
                snapshot.Channels.Select(c => c.Channel).Distinct().Count() != snapshot.Channels.Length)
                return false;
            var maxAge = TimeSpan.FromSeconds(settings.SnapshotMaxAgeSeconds).Ticks;
            return snapshot.PublishedUtcTicks > 0 && snapshot.SourceUtcTicks > 0 &&
                snapshot.PublishedUtcTicks <= now + ClockToleranceTicks &&
                snapshot.SourceUtcTicks <= snapshot.PublishedUtcTicks + ClockToleranceTicks &&
                now - snapshot.PublishedUtcTicks < maxAge && now - snapshot.SourceUtcTicks < maxAge;
        }

        private static bool AdvanceChannels(RecoveryGuardObservation observed, RecoveryObservationSnapshot previous,
            RecoveryObservationSnapshot current, long now)
        {
            // A omitted channel is not an isolation or completion receipt.
            if (previous.Channels.Any(p => !current.Channels.Any(c => c.Channel == p.Channel))) return false;
            foreach (var channel in current.Channels)
            {
                var old = previous.Channels.SingleOrDefault(c => c.Channel == channel.Channel);
                if (old == null) return false; // participation changes require a new intent
                if (channel.SampleGeneration < old.SampleGeneration ||
                    (channel.SampleGeneration == old.SampleGeneration && channel.SampleSequence < old.SampleSequence) ||
                    channel.ControlSequence < old.ControlSequence ||
                    channel.PersistedSequence < old.PersistedSequence ||
                    (old.PermanentlyIsolated && !channel.PermanentlyIsolated) ||
                    (old.Completed && !channel.Completed) ||
                    (!channel.Eligible && !(channel.Completed || channel.PermanentlyIsolated || channel.ManuallyExcluded)) ||
                    (old.Stage == channel.Stage && old.StageStartedUtcTicks == channel.StageStartedUtcTicks &&
                     old.StageDeadlineUtcTicks != channel.StageDeadlineUtcTicks))
                    return false;
            }
            foreach (var channel in current.Channels)
            {
                var old = previous.Channels.Single(c => c.Channel == channel.Channel);
                var clock = observed.ChannelClocks.Single(c => c.Channel == channel.Channel);
                if (channel.SampleGeneration != old.SampleGeneration) clock.SampleProgressUtcTicks = 0;
                else if (channel.SampleSequence > old.SampleSequence) clock.SampleProgressUtcTicks = now;
                if (channel.ControlSequence > old.ControlSequence) clock.ControlProgressUtcTicks = now;
                if (channel.PersistedSequence > old.PersistedSequence) clock.PersistedProgressUtcTicks = now;
            }
            return true;
        }

        private static long EffectiveProgressAnchor(RecoveryChannelProgress channel, RecoveryObservedChannel clock, long now)
        {
            // An explicitly bounded legitimate control stage may wait without
            // completing another step, but must still produce real samples.
            var boundedWait = channel.StageStartedUtcTicks > 0 && channel.StageStartedUtcTicks <= now &&
                channel.StageDeadlineUtcTicks > now && !string.IsNullOrWhiteSpace(channel.Stage);
            return boundedWait ? clock.SampleProgressUtcTicks :
                Math.Min(clock.SampleProgressUtcTicks, Math.Max(clock.ControlProgressUtcTicks, clock.PersistedProgressUtcTicks));
        }

        private static RecoveryDecision Decision(string code) => new RecoveryDecision { Code = code };
    }
}
