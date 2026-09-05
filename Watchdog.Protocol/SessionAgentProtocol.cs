using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MTTFTest.Watchdog.Protocol
{
    public static class SessionAgentProtocol
    {
        public const int SchemaVersion = 6;
        public const string PipePrefix = "MTTFTestSessionAgent.Launch.v6.";
        public const int RegisteredDesktopSessionId = -1;
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(
            "MTTFTest.SessionAgent.LaunchCapability.Schema6");

        public const string CapabilityArgument = "--supervisor-launch-capability";
        public const string NonceArgument = "--supervisor-launch-nonce";
        public const string SchemaArgument = "--supervisor-launch-schema";
        public const string SessionArgument = "--supervisor-launch-session";

        public static string PipeName(int desktopSessionId) =>
            PipePrefix + desktopSessionId.ToString();

        public static byte[] Seal(SessionLaunchCapability capability)
        {
            if (capability?.IsStructurallyValid() != true)
                throw new InvalidDataException("LaunchCapabilityInvalid");
            return ProtectedData.Protect(
                Encoding.UTF8.GetBytes(capability.ToCanonicalString()),
                Entropy,
                DataProtectionScope.LocalMachine);
        }

        public static bool VerifySeal(
            SessionLaunchCapability capability,
            byte[] sealedCapability)
        {
            try
            {
                if (capability?.IsStructurallyValid() != true ||
                    sealedCapability == null || sealedCapability.Length == 0)
                    return false;
                var clear = ProtectedData.Unprotect(
                    sealedCapability,
                    Entropy,
                    DataProtectionScope.LocalMachine);
                return string.Equals(
                    Encoding.UTF8.GetString(clear),
                    capability.ToCanonicalString(),
                    StringComparison.Ordinal);
            }
            catch { return false; }
        }

        public static string ConsumptionPath(string capabilityId)
        {
            Guid parsed;
            if (!Guid.TryParseExact(capabilityId ?? string.Empty, "N", out parsed))
                throw new InvalidDataException("LaunchCapabilityIdInvalid");
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "MTTFTest",
                "SessionAgent",
                "capabilities",
                "capability-" + capabilityId + ".json");
        }
    }

    public sealed class SessionLaunchCapability
    {
        public int SchemaVersion { get; set; } = SessionAgentProtocol.SchemaVersion;
        public string CapabilityId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public long PermitGeneration { get; set; }
        public string PermitId { get; set; } = string.Empty;
        public int DesktopSessionId { get; set; }
        public string ExecutablePath { get; set; } = string.Empty;
        public string ExecutableSha256 { get; set; } = string.Empty;
        public string Arguments { get; set; } = string.Empty;
        public string ArgumentsSha256 { get; set; } = string.Empty;
        public string WorkingDirectory { get; set; } = string.Empty;
        public string LaunchNonce { get; set; } = string.Empty;
        public long IssuedUtcTicks { get; set; }
        public long ExpiresUtcTicks { get; set; }
        public int IssuerProcessId { get; set; }
        public long IssuerProcessStartUtcTicks { get; set; }

        public bool IsStructurallyValid()
        {
            Guid parsed;
            return SchemaVersion == SessionAgentProtocol.SchemaVersion &&
                   Guid.TryParseExact(CapabilityId ?? string.Empty, "N", out parsed) &&
                   Guid.TryParseExact(SessionId ?? string.Empty, "N", out parsed) &&
                   PermitGeneration > 0 &&
                   Guid.TryParseExact(PermitId ?? string.Empty, "N", out parsed) &&
                   DesktopSessionId >= 0 &&
                   !string.IsNullOrWhiteSpace(ExecutablePath) &&
                   RecoveryFailureReceipt.IsSha256(ExecutableSha256) &&
                   string.Equals(
                       SupervisorProtocol.ComputeTextSha256(Arguments),
                       ArgumentsSha256,
                       StringComparison.Ordinal) &&
                   !string.IsNullOrWhiteSpace(WorkingDirectory) &&
                   WatchdogProcessIdentityPolicy.IsValidChallengeNonce(LaunchNonce) &&
                   IssuedUtcTicks > 0 && ExpiresUtcTicks > IssuedUtcTicks &&
                   ExpiresUtcTicks <= IssuedUtcTicks + TimeSpan.FromMinutes(2).Ticks &&
                   IssuerProcessId > 0 && IssuerProcessStartUtcTicks > 0;
        }

        public string ToCanonicalString()
        {
            return string.Join("\n", new[]
            {
                SchemaVersion.ToString(),
                CapabilityId ?? string.Empty,
                SessionId ?? string.Empty,
                PermitGeneration.ToString(),
                PermitId ?? string.Empty,
                DesktopSessionId.ToString(),
                Path.GetFullPath(ExecutablePath ?? string.Empty),
                ExecutableSha256 ?? string.Empty,
                ArgumentsSha256 ?? string.Empty,
                Path.GetFullPath(WorkingDirectory ?? string.Empty),
                LaunchNonce ?? string.Empty,
                IssuedUtcTicks.ToString(),
                ExpiresUtcTicks.ToString(),
                IssuerProcessId.ToString(),
                IssuerProcessStartUtcTicks.ToString()
            });
        }

        public void WriteTo(BinaryWriter writer, byte[] seal)
        {
            writer.Write("MTTF-SESSION-AGENT-REQUEST-V6-V216");
            writer.Write(SchemaVersion);
            writer.Write(CapabilityId ?? string.Empty);
            writer.Write(SessionId ?? string.Empty);
            writer.Write(PermitGeneration);
            writer.Write(PermitId ?? string.Empty);
            writer.Write(DesktopSessionId);
            writer.Write(ExecutablePath ?? string.Empty);
            writer.Write(ExecutableSha256 ?? string.Empty);
            writer.Write(Arguments ?? string.Empty);
            writer.Write(ArgumentsSha256 ?? string.Empty);
            writer.Write(WorkingDirectory ?? string.Empty);
            writer.Write(LaunchNonce ?? string.Empty);
            writer.Write(IssuedUtcTicks);
            writer.Write(ExpiresUtcTicks);
            writer.Write(IssuerProcessId);
            writer.Write(IssuerProcessStartUtcTicks);
            writer.Write(seal?.Length ?? 0);
            if (seal != null) writer.Write(seal);
            writer.Flush();
        }

        public static SessionLaunchCapability ReadFrom(
            BinaryReader reader,
            out byte[] seal)
        {
            if (!string.Equals(
                    reader.ReadString(),
                    "MTTF-SESSION-AGENT-REQUEST-V6-V216",
                    StringComparison.Ordinal))
                throw new InvalidDataException("SessionAgentRequestMagicMismatch");
            var result = new SessionLaunchCapability
            {
                SchemaVersion = reader.ReadInt32(),
                CapabilityId = reader.ReadString(),
                SessionId = reader.ReadString(),
                PermitGeneration = reader.ReadInt64(),
                PermitId = reader.ReadString(),
                DesktopSessionId = reader.ReadInt32(),
                ExecutablePath = reader.ReadString(),
                ExecutableSha256 = reader.ReadString(),
                Arguments = reader.ReadString(),
                ArgumentsSha256 = reader.ReadString(),
                WorkingDirectory = reader.ReadString(),
                LaunchNonce = reader.ReadString(),
                IssuedUtcTicks = reader.ReadInt64(),
                ExpiresUtcTicks = reader.ReadInt64(),
                IssuerProcessId = reader.ReadInt32(),
                IssuerProcessStartUtcTicks = reader.ReadInt64()
            };
            var length = reader.ReadInt32();
            if (length <= 0 || length > 64 * 1024)
                throw new InvalidDataException("SessionAgentSealLengthInvalid");
            seal = reader.ReadBytes(length);
            if (seal.Length != length)
                throw new EndOfStreamException("SessionAgentSealTruncated");
            return result;
        }
    }

    public sealed class SessionLaunchResponse
    {
        public int SchemaVersion { get; set; } = SessionAgentProtocol.SchemaVersion;
        public string CapabilityId { get; set; } = string.Empty;
        public string LaunchNonce { get; set; } = string.Empty;
        public bool Accepted { get; set; }
        public int ProcessId { get; set; }
        public long ProcessStartUtcTicks { get; set; }
        public string FailureCode { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;

        public void WriteTo(BinaryWriter writer)
        {
            writer.Write("MTTF-SESSION-AGENT-RESPONSE-V6-V216");
            writer.Write(SchemaVersion);
            writer.Write(CapabilityId ?? string.Empty);
            writer.Write(LaunchNonce ?? string.Empty);
            writer.Write(Accepted);
            writer.Write(ProcessId);
            writer.Write(ProcessStartUtcTicks);
            writer.Write(FailureCode ?? string.Empty);
            writer.Write(Detail ?? string.Empty);
            writer.Flush();
        }

        public static SessionLaunchResponse ReadFrom(BinaryReader reader)
        {
            if (!string.Equals(
                    reader.ReadString(),
                    "MTTF-SESSION-AGENT-RESPONSE-V6-V216",
                    StringComparison.Ordinal))
                throw new InvalidDataException("SessionAgentResponseMagicMismatch");
            return new SessionLaunchResponse
            {
                SchemaVersion = reader.ReadInt32(),
                CapabilityId = reader.ReadString(),
                LaunchNonce = reader.ReadString(),
                Accepted = reader.ReadBoolean(),
                ProcessId = reader.ReadInt32(),
                ProcessStartUtcTicks = reader.ReadInt64(),
                FailureCode = reader.ReadString(),
                Detail = reader.ReadString()
            };
        }
    }

    public sealed class SessionLaunchConsumptionRecord
    {
        public int SchemaVersion { get; set; }
        public string CapabilityId { get; set; }
        public string SessionId { get; set; }
        public long PermitGeneration { get; set; }
        public string PermitId { get; set; }
        public string LaunchNonce { get; set; }
        public string ExecutablePath { get; set; }
        public string ExecutableSha256 { get; set; }
        public string ArgumentsSha256 { get; set; }
        public string CapabilitySealBase64 { get; set; }
        public string State { get; set; }
        public long ConsumedUtcTicks { get; set; }
        public int ProcessId { get; set; }
        public long ProcessStartUtcTicks { get; set; }
    }

    public sealed class SupervisorMainLaunchRequest
    {
        public int SchemaVersion { get; set; } = SupervisorProtocol.SchemaVersion;
        public string RequestId { get; set; } = string.Empty;
        public string ChallengeNonce { get; set; } = string.Empty;
        public int RequesterProcessId { get; set; }
        public long RequesterProcessStartUtcTicks { get; set; }
        public int DesktopSessionId { get; set; }
        public string ExecutablePath { get; set; } = string.Empty;
        public string ExecutableSha256 { get; set; } = string.Empty;
        public string Arguments { get; set; } = string.Empty;
        public string ArgumentsSha256 { get; set; } = string.Empty;
        public string WorkingDirectory { get; set; } = string.Empty;
        public bool IsRecoveryLaunch { get; set; }
        public string RecoveryCapabilityId { get; set; } = string.Empty;
        public string RecoverySessionId { get; set; } = string.Empty;
        public long RecoveryPermitGeneration { get; set; }
        public string RecoveryPermitId { get; set; } = string.Empty;
        public long RecoveryAuthorityRevision { get; set; }
        public string RecoveryAuthoritySha256 { get; set; } = string.Empty;

        public bool IsStructurallyValid()
        {
            Guid parsed;
            var initialBindingEmpty =
                !IsRecoveryLaunch &&
                string.IsNullOrEmpty(RecoveryCapabilityId) &&
                string.IsNullOrEmpty(RecoverySessionId) &&
                RecoveryPermitGeneration == 0 &&
                string.IsNullOrEmpty(RecoveryPermitId) &&
                RecoveryAuthorityRevision == 0 &&
                string.IsNullOrEmpty(RecoveryAuthoritySha256);
            var recoveryBindingComplete =
                IsRecoveryLaunch &&
                Guid.TryParseExact(
                    RecoveryCapabilityId ?? string.Empty, "N", out parsed) &&
                Guid.TryParseExact(
                    RecoverySessionId ?? string.Empty, "N", out parsed) &&
                RecoveryPermitGeneration > 0 &&
                Guid.TryParseExact(
                    RecoveryPermitId ?? string.Empty, "N", out parsed) &&
                RecoveryAuthorityRevision > 0 &&
                RecoveryFailureReceipt.IsSha256(RecoveryAuthoritySha256);
            return SchemaVersion == SupervisorProtocol.SchemaVersion &&
                   Guid.TryParseExact(RequestId ?? string.Empty, "N", out parsed) &&
                   WatchdogProcessIdentityPolicy.IsValidChallengeNonce(ChallengeNonce) &&
                   RequesterProcessId > 0 && RequesterProcessStartUtcTicks > 0 &&
                   (IsRecoveryLaunch
                       ? DesktopSessionId ==
                         SessionAgentProtocol.RegisteredDesktopSessionId
                       : DesktopSessionId >= 0) &&
                   !string.IsNullOrWhiteSpace(ExecutablePath) &&
                   RecoveryFailureReceipt.IsSha256(ExecutableSha256) &&
                   string.Equals(
                       SupervisorProtocol.ComputeTextSha256(Arguments),
                       ArgumentsSha256,
                       StringComparison.Ordinal) &&
                   !string.IsNullOrWhiteSpace(WorkingDirectory) &&
                   (initialBindingEmpty || recoveryBindingComplete);
        }

        public void WriteTo(BinaryWriter writer)
        {
            writer.Write(SupervisorProtocol.MainLaunchRequestMagic);
            writer.Write(SchemaVersion);
            writer.Write(RequestId ?? string.Empty);
            writer.Write(ChallengeNonce ?? string.Empty);
            writer.Write(RequesterProcessId);
            writer.Write(RequesterProcessStartUtcTicks);
            writer.Write(DesktopSessionId);
            writer.Write(ExecutablePath ?? string.Empty);
            writer.Write(ExecutableSha256 ?? string.Empty);
            writer.Write(Arguments ?? string.Empty);
            writer.Write(ArgumentsSha256 ?? string.Empty);
            writer.Write(WorkingDirectory ?? string.Empty);
            writer.Write(IsRecoveryLaunch);
            writer.Write(RecoveryCapabilityId ?? string.Empty);
            writer.Write(RecoverySessionId ?? string.Empty);
            writer.Write(RecoveryPermitGeneration);
            writer.Write(RecoveryPermitId ?? string.Empty);
            writer.Write(RecoveryAuthorityRevision);
            writer.Write(RecoveryAuthoritySha256 ?? string.Empty);
            writer.Flush();
        }

        public static SupervisorMainLaunchRequest ReadBodyFrom(
            BinaryReader reader,
            string magic)
        {
            if (!string.Equals(
                    magic,
                    SupervisorProtocol.MainLaunchRequestMagic,
                    StringComparison.Ordinal))
                throw new InvalidDataException("SupervisorMainLaunchRequestMagicMismatch");
            return new SupervisorMainLaunchRequest
            {
                SchemaVersion = reader.ReadInt32(),
                RequestId = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                ChallengeNonce = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                RequesterProcessId = reader.ReadInt32(),
                RequesterProcessStartUtcTicks = reader.ReadInt64(),
                DesktopSessionId = reader.ReadInt32(),
                ExecutablePath = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                ExecutableSha256 = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                Arguments = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                ArgumentsSha256 = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                WorkingDirectory = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                IsRecoveryLaunch = reader.ReadBoolean(),
                RecoveryCapabilityId =
                    SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                RecoverySessionId =
                    SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                RecoveryPermitGeneration = reader.ReadInt64(),
                RecoveryPermitId =
                    SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                RecoveryAuthorityRevision = reader.ReadInt64(),
                RecoveryAuthoritySha256 =
                    SupervisorSessionLaunchRequest.ReadBoundedString(reader)
            };
        }
    }

    public sealed class SupervisorMainLaunchResponse
    {
        public int SchemaVersion { get; set; } = SupervisorProtocol.SchemaVersion;
        public string RequestId { get; set; } = string.Empty;
        public string ChallengeNonce { get; set; } = string.Empty;
        public bool Accepted { get; set; }
        public string CapabilityId { get; set; } = string.Empty;
        public int ProcessId { get; set; }
        public long ProcessStartUtcTicks { get; set; }
        public string FailureCode { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;

        public void WriteTo(BinaryWriter writer)
        {
            writer.Write(SupervisorProtocol.MainLaunchResponseMagic);
            writer.Write(SchemaVersion);
            writer.Write(RequestId ?? string.Empty);
            writer.Write(ChallengeNonce ?? string.Empty);
            writer.Write(Accepted);
            writer.Write(CapabilityId ?? string.Empty);
            writer.Write(ProcessId);
            writer.Write(ProcessStartUtcTicks);
            writer.Write(FailureCode ?? string.Empty);
            writer.Write(Detail ?? string.Empty);
            writer.Flush();
        }

        public static SupervisorMainLaunchResponse ReadFrom(BinaryReader reader)
        {
            if (!string.Equals(
                    reader.ReadString(),
                    SupervisorProtocol.MainLaunchResponseMagic,
                    StringComparison.Ordinal))
                throw new InvalidDataException("SupervisorMainLaunchResponseMagicMismatch");
            return new SupervisorMainLaunchResponse
            {
                SchemaVersion = reader.ReadInt32(),
                RequestId = reader.ReadString(),
                ChallengeNonce = reader.ReadString(),
                Accepted = reader.ReadBoolean(),
                CapabilityId = reader.ReadString(),
                ProcessId = reader.ReadInt32(),
                ProcessStartUtcTicks = reader.ReadInt64(),
                FailureCode = reader.ReadString(),
                Detail = reader.ReadString()
            };
        }
    }
}
