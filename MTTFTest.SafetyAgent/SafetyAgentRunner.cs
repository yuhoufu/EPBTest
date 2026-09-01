using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using MTTFTest.SafetyHardware;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.SafetyAgent
{
    public sealed class SafetyAgentArguments
    {
        public string SessionId { get; private set; }
        public string HandoffId { get; private set; }
        public string Nonce { get; private set; }
        public string JournalDirectory { get; private set; }

        public static bool TryParse(string[] args, out SafetyAgentArguments value)
        {
            string Read(string name)
            {
                for (var index = 0; index + 1 < (args?.Length ?? 0); index++)
                    if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
                        return args[index + 1];
                return string.Empty;
            }
            value = new SafetyAgentArguments
            {
                SessionId = Read("--session-id"),
                HandoffId = Read("--handoff-id"),
                Nonce = Read("--handoff-nonce"),
                JournalDirectory = Read("--journal-directory")
            };
            Guid parsed;
            return Guid.TryParseExact(value.SessionId ?? string.Empty, "N", out parsed) &&
                   Guid.TryParseExact(value.HandoffId ?? string.Empty, "N", out parsed) &&
                   WatchdogProcessIdentityPolicy.IsValidChallengeNonce(value.Nonce) &&
                   !string.IsNullOrWhiteSpace(value.JournalDirectory);
        }
    }

    public interface ISafetyHardware : IDisposable
    {
        bool ConfirmDoOff();
        bool ConfirmAoZero();
        bool ConfirmPowerOff();
        bool ConfirmPressureSafe();
    }

    public interface ISafetyHardwareFactory
    {
        ISafetyHardware Create(string configDirectory, SafetyRuntimeSnapshot runtime);
    }

    public static class SafetyAgentRunner
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

        public static int Run(SafetyAgentArguments args, ISafetyHardwareFactory factory)
        {
            if (args == null || factory == null) return 64;
            using (var mutex = new Mutex(false, "Local\\MTTFTest.SafetyAgent." + args.HandoffId))
            {
                bool owns;
                try { owns = mutex.WaitOne(0, false); }
                catch (AbandonedMutexException) { owns = true; }
                if (!owns) return 2;
                try
                {
                    WatchdogSafetyHandoffReceipt receipt;
                    if (!TryReadExact(args, out receipt) ||
                        (receipt.SchemaVersion != 3 && receipt.SchemaVersion != 4 &&
                         receipt.SchemaVersion != 5) ||
                        receipt.State < WatchdogSafetyHandoffState.Accepted)
                        return 3;
                    if (receipt.IsSafetyCompleted) return 0;

                    var snapshot = WatchdogSafetyConfigSnapshotStore.Validate(
                        args.JournalDirectory,
                        args.HandoffId,
                        receipt.ConfigSnapshotPath,
                        receipt.ConfigSnapshotManifestPath,
                        receipt.ConfigSnapshotManifestSha256);
                    ValidateBinding(receipt, snapshot);
                    Advance(args, receipt, WatchdogSafetyStage.AgentStarted,
                        "SafetyAgentStarted", null);

                    using (var hardware = factory.Create(snapshot.ConfigDirectory, snapshot.Runtime))
                    {
                        receipt = ReadExact(args);
                        if (receipt.Stage < WatchdogSafetyStage.DoOffConfirmed)
                        {
                            if (!hardware.ConfirmDoOff())
                                throw new SafetyHardwareUnavailableException("SafetyDoOffUnconfirmed");
                            Advance(args, receipt, WatchdogSafetyStage.DoOffConfirmed,
                                "DO=OFF confirmed", value => value.MotorsOff = true);
                        }
                        receipt = ReadExact(args);
                        if (receipt.Stage < WatchdogSafetyStage.AoZeroConfirmed)
                        {
                            if (!hardware.ConfirmAoZero())
                                throw new SafetyHardwareUnavailableException("SafetyAoZeroUnconfirmed");
                            Advance(args, receipt, WatchdogSafetyStage.AoZeroConfirmed,
                                "AO=0 confirmed", null);
                        }
                        receipt = ReadExact(args);
                        if (receipt.Stage < WatchdogSafetyStage.PowerOffConfirmed)
                        {
                            if (!hardware.ConfirmPowerOff())
                            {
                                var powerEvidence = hardware as ISafetyPowerEvidence;
                                var detail = powerEvidence?.LastPowerOffReport?.ToDiagnosticString();
                                throw new SafetyHardwareUnavailableException(
                                    string.IsNullOrWhiteSpace(detail)
                                        ? "SafetyPowerOffUnconfirmed"
                                        : "SafetyPowerOffUnconfirmed:" + detail);
                            }
                            var confirmedPower = hardware as ISafetyPowerEvidence;
                            Advance(args, receipt, WatchdogSafetyStage.PowerOffConfirmed,
                                "Power=OFF confirmed;" +
                                (confirmedPower?.LastPowerOffReport?.ToDiagnosticString() ?? string.Empty),
                                value => value.PowerOff = true);
                        }
                        receipt = ReadExact(args);
                        if (receipt.Stage < WatchdogSafetyStage.PressureSafeConfirmed)
                        {
                            if (!hardware.ConfirmPressureSafe())
                                throw new SafetyHardwareUnavailableException("SafetyPressureUnconfirmed");
                            Advance(args, receipt, WatchdogSafetyStage.PressureSafeConfirmed,
                                "Pressure safe confirmed", value => value.PressureSafe = true);
                        }
                    }

                    receipt = ReadExact(args);
                    Advance(args, receipt, WatchdogSafetyStage.Completed,
                        "SafetyHandoffCompleted:DO=OFF;Power=OFF;AO=0;PressureSafe",
                        value =>
                        {
                            value.MotorsOff = true;
                            value.PowerOff = true;
                            value.PressureSafe = true;
                            value.State = WatchdogSafetyHandoffState.Completed;
                            value.FailureCode = string.Empty;
                            value.FailureDomain = RecoveryFailureDomain.None;
                        });
                    return 0;
                }
                catch (SafetyHardwareConfigurationException ex)
                {
                    RecordFailure(args, "SafetyAgentConfigInvalid", RecoveryFailureDomain.SafetyAgent,
                        ex.GetBaseException().Message, true);
                    return 10;
                }
                catch (InvalidDataException ex)
                {
                    RecordFailure(args, "SafetyAgentConfigInvalid", ClassifyInvalidData(ex),
                        ex.GetBaseException().Message, true);
                    return 10;
                }
                catch (ArgumentException ex)
                {
                    RecordFailure(args, "SafetyAgentConfigInvalid", RecoveryFailureDomain.SafetyAgent,
                        ex.GetBaseException().Message, true);
                    return 10;
                }
                catch (SafetyHardwareUnavailableException ex)
                {
                    RecordFailure(args, ex.Message, RecoveryFailureDomain.HardwareUnavailable,
                        ex.Message, false);
                    return 20;
                }
                catch (Exception ex)
                {
                    RecordFailure(args, "SafetyAgentProcessFailure", RecoveryFailureDomain.SafetyAgent,
                        ex.GetBaseException().Message, false);
                    return 30;
                }
                finally
                {
                    try { mutex.ReleaseMutex(); } catch { }
                }
            }
        }

        private static void ValidateBinding(
            WatchdogSafetyHandoffReceipt receipt,
            WatchdogSafetyConfigSnapshotResult snapshot)
        {
            if (snapshot?.Succeeded != true || snapshot.Manifest == null || snapshot.Runtime == null)
                throw new InvalidDataException(snapshot?.Error ?? "SafetyAgentConfigInvalid");
            var manifest = snapshot.Manifest;
            if (!string.Equals(manifest.SessionId, receipt.SessionId, StringComparison.Ordinal) ||
                manifest.SessionGeneration != receipt.SessionGeneration ||
                manifest.SessionLease != receipt.SessionLease ||
                !string.Equals(manifest.HandoffId, receipt.HandoffId, StringComparison.Ordinal) ||
                manifest.PermitGeneration != receipt.RelaunchPermitGeneration ||
                !string.Equals(manifest.PermitId, receipt.RelaunchPermitId ?? string.Empty,
                    StringComparison.Ordinal) ||
                !string.Equals(manifest.MainExecutableSha256, receipt.MainExecutableSha256,
                    StringComparison.Ordinal) ||
                !string.Equals(manifest.SafetyAgentExecutableSha256,
                    receipt.SafetyAgentExecutableSha256, StringComparison.Ordinal))
                throw new InvalidDataException("SafetyAgentConfigInvalid:IdentityBindingMismatch");
        }

        private static void Advance(
            SafetyAgentArguments args,
            WatchdogSafetyHandoffReceipt receipt,
            WatchdogSafetyStage stage,
            string detail,
            Action<WatchdogSafetyHandoffReceipt> mutate)
        {
            if (receipt.Stage >= stage) return;
            var previousBytes = new UTF8Encoding(false).GetBytes(Serializer.Serialize(receipt));
            receipt.PreviousStageReceiptSha256 = DurableJsonFileStore.ComputeSha256(previousBytes);
            receipt.Stage = stage;
            receipt.State = stage == WatchdogSafetyStage.Completed
                ? WatchdogSafetyHandoffState.Completed
                : WatchdogSafetyHandoffState.WorkerStarted;
            receipt.StageUtcTicks = DateTime.UtcNow.Ticks;
            var currentMonotonicMs = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
            receipt.StageMonotonicElapsedMs = Math.Max(
                receipt.StageMonotonicElapsedMs + 0.001,
                currentMonotonicMs);
            receipt.Detail = detail ?? string.Empty;
            mutate?.Invoke(receipt);
            receipt.Revision++;
            WatchdogSafetyHandoffReceiptStore.WriteThrough(args.JournalDirectory, receipt);
        }

        private static void RecordFailure(
            SafetyAgentArguments args,
            string code,
            RecoveryFailureDomain domain,
            string detail,
            bool terminal)
        {
            try
            {
                var receipt = ReadExact(args);
                if (receipt.IsSafetyCompleted) return;
                receipt.FailureCode = code ?? "SafetyAgentFailure";
                receipt.FailureDomain = domain;
                receipt.Detail = detail ?? string.Empty;
                if (terminal) receipt.State = WatchdogSafetyHandoffState.Failed;
                receipt.Revision++;
                WatchdogSafetyHandoffReceiptStore.WriteThrough(args.JournalDirectory, receipt);
            }
            catch { }
        }

        private static WatchdogSafetyHandoffReceipt ReadExact(SafetyAgentArguments args)
        {
            WatchdogSafetyHandoffReceipt receipt;
            if (!TryReadExact(args, out receipt))
                throw new InvalidDataException("SafetyAgentConfigInvalid:ReceiptIdentityMismatch");
            return receipt;
        }

        private static bool TryReadExact(
            SafetyAgentArguments args,
            out WatchdogSafetyHandoffReceipt receipt)
        {
            return WatchdogSafetyHandoffReceiptStore.TryRead(
                       args.JournalDirectory, args.SessionId, out receipt) &&
                   string.Equals(receipt.HandoffId, args.HandoffId, StringComparison.Ordinal) &&
                   string.Equals(receipt.Nonce, args.Nonce, StringComparison.Ordinal);
        }

        private static RecoveryFailureDomain ClassifyInvalidData(InvalidDataException exception)
        {
            var detail = exception?.GetBaseException().Message ?? string.Empty;
            return detail.IndexOf("IdentityBinding", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   detail.IndexOf("Manifest", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   detail.IndexOf("HashMismatch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   detail.IndexOf("Snapshot", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   detail.IndexOf("ReceiptIdentity", StringComparison.OrdinalIgnoreCase) >= 0
                ? RecoveryFailureDomain.EvidenceBinding
                : RecoveryFailureDomain.SafetyAgent;
        }

    }
}
