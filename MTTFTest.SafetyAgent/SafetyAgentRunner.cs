using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Text.RegularExpressions;
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
        public string AuthorityId { get; private set; }
        public string AuthorityReceiptPath { get; private set; }
        public long AuthorityRevision { get; private set; }
        public string AuthorityCanonicalSha256 { get; private set; }

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
                JournalDirectory = Read("--journal-directory"),
                AuthorityId = Read("--authority-id"),
                AuthorityReceiptPath = Read("--authority-receipt"),
                AuthorityCanonicalSha256 = Read("--authority-sha256")
            };
            long.TryParse(Read("--authority-revision"), out var revision);
            value.AuthorityRevision = revision;
            Guid parsed;
            return Guid.TryParseExact(value.SessionId ?? string.Empty, "N", out parsed) &&
                   Guid.TryParseExact(value.HandoffId ?? string.Empty, "N", out parsed) &&
                   Guid.TryParseExact(value.AuthorityId ?? string.Empty, "N", out parsed) &&
                   WatchdogProcessIdentityPolicy.IsValidChallengeNonce(value.Nonce) &&
                   !string.IsNullOrWhiteSpace(value.JournalDirectory) &&
                   !string.IsNullOrWhiteSpace(value.AuthorityReceiptPath) &&
                   value.AuthorityRevision > 0 &&
                   Regex.IsMatch(
                       value.AuthorityCanonicalSha256 ?? string.Empty,
                       "^[0-9A-Fa-f]{64}$",
                       RegexOptions.CultureInvariant);
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
                        receipt.SchemaVersion != SupervisorProtocol.SchemaVersion ||
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
            SupervisorSafetyAuthorityStore.Advance(
                AuthorityDirectory(args),
                args.AuthorityId,
                args.AuthorityRevision,
                args.AuthorityCanonicalSha256,
                receipt);
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
                SupervisorSafetyAuthorityStore.Advance(
                    AuthorityDirectory(args),
                    args.AuthorityId,
                    args.AuthorityRevision,
                    args.AuthorityCanonicalSha256,
                    receipt);
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
            receipt = null;
            var directory = AuthorityDirectory(args);
            var expectedPath = SupervisorSafetyAuthorityStore.GetPath(
                directory,
                args.AuthorityId);
            if (!string.Equals(
                    Path.GetFullPath(expectedPath),
                    Path.GetFullPath(args.AuthorityReceiptPath),
                    StringComparison.OrdinalIgnoreCase))
                return RecordIdentityRejection(args, "AuthorityPathMismatch Expected=" + expectedPath);
            SupervisorSafetyAuthorityRecord authority;
            string failure;
            if (!SupervisorSafetyAuthorityStore.TryRead(
                    directory,
                    args.AuthorityId,
                    out authority,
                    out failure))
                return RecordIdentityRejection(args, "AuthorityReadFailed:" + failure);
            if (authority.InitialReceiptRevision != args.AuthorityRevision)
                return RecordIdentityRejection(args, "InitialRevisionMismatch Expected=" + authority.InitialReceiptRevision + " Actual=" + args.AuthorityRevision);
            if (!string.Equals(authority.InitialReceiptCanonicalSha256, args.AuthorityCanonicalSha256, StringComparison.Ordinal))
                return RecordIdentityRejection(args, "InitialCanonicalSha256Mismatch");
            if (!string.Equals(authority.SessionId, args.SessionId, StringComparison.Ordinal))
                return RecordIdentityRejection(args, "SessionIdMismatch Expected=" + authority.SessionId);
            if (!string.Equals(authority.HandoffId, args.HandoffId, StringComparison.Ordinal))
                return RecordIdentityRejection(args, "HandoffIdMismatch Expected=" + authority.HandoffId);
            if (!string.Equals(authority.Receipt.Nonce, args.Nonce, StringComparison.Ordinal))
                return RecordIdentityRejection(args, "NonceMismatch");
            receipt = authority.Receipt;
            return true;
        }

        private static bool RecordIdentityRejection(SafetyAgentArguments args, string detail)
        {
            var text = DateTime.UtcNow.ToString("O") + " Session=" + args.SessionId +
                " Handoff=" + args.HandoffId + " AuthorityPath=" + args.AuthorityReceiptPath + " " + detail;
            try { Console.Error.WriteLine(text); } catch { }
            try
            {
                var path = Path.Combine(AuthorityDirectory(args), "safety-agent-identity-rejections.log");
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                {
                    if (File.Exists(path + ".1")) File.Delete(path + ".1");
                    File.Move(path, path + ".1");
                }
                File.AppendAllText(path, text + Environment.NewLine);
            }
            catch { }
            return false;
        }

        private static string AuthorityDirectory(SafetyAgentArguments args)
        {
            var fullPath = Path.GetFullPath(args.AuthorityReceiptPath ?? string.Empty);
            return Path.GetDirectoryName(fullPath) ?? string.Empty;
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
