using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MTTFTest.Watchdog.Protocol
{
    public static class SupervisorProtocol
    {
        public const int SchemaVersion = 6;
        public const string CompatibilityFamily = "EPB-V2.16";
        public const string PipeName = "MTTFTestSupervisor.Control.v6.V216";
        public const string RequestMagic = "MTTF-SUPERVISOR-REQUEST-V6-V216";
        public const string ResponseMagic = "MTTF-SUPERVISOR-RESPONSE-V6-V216";
        public const string MainLaunchRequestMagic =
            "MTTF-SUPERVISOR-MAIN-LAUNCH-REQUEST-V6-V216";
        public const string MainLaunchResponseMagic =
            "MTTF-SUPERVISOR-MAIN-LAUNCH-RESPONSE-V6-V216";
        public const string SafetyHandoffBeginRequestMagic =
            "MTTF-SUPERVISOR-SAFETY-BEGIN-REQUEST-V6-V216";
        public const string SafetyHandoffBeginResponseMagic =
            "MTTF-SUPERVISOR-SAFETY-BEGIN-RESPONSE-V6-V216";
        public const string SafetyAuthorityReadRequestMagic =
            "MTTF-SUPERVISOR-SAFETY-AUTHORITY-READ-REQUEST-V6-V216";
        public const string SafetyAuthorityReadResponseMagic =
            "MTTF-SUPERVISOR-SAFETY-AUTHORITY-READ-RESPONSE-V6-V216";
        public const string SafetyAgentRequestMagic =
            "MTTF-SUPERVISOR-SAFETY-REQUEST-V6-V216";
        public const string SafetyAgentResponseMagic =
            "MTTF-SUPERVISOR-SAFETY-RESPONSE-V6-V216";
        public const string P0AlarmRequestMagic =
            "MTTF-SUPERVISOR-P0-ALARM-REQUEST-V6-V216";
        public const string P0AlarmResponseMagic =
            "MTTF-SUPERVISOR-P0-ALARM-RESPONSE-V6-V216";
        public const int MaximumTextLength = 1024 * 1024;

        public static string ComputeSha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(
                       path,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
                return BitConverter.ToString(sha.ComputeHash(stream))
                    .Replace("-", string.Empty)
                    .ToUpperInvariant();
        }

        public static string ComputeTextSha256(string value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(
                        Encoding.UTF8.GetBytes(value ?? string.Empty)))
                    .Replace("-", string.Empty)
                    .ToUpperInvariant();
        }

        public static long GetProcessStartUtcTicks(Process process)
        {
            return process?.StartTime.ToUniversalTime().Ticks ?? 0;
        }

        public static string ReadRequestMagic(BinaryReader reader)
        {
            return SupervisorSessionLaunchRequest.ReadBoundedString(reader);
        }
    }

    public sealed class SupervisorSessionLaunchRequest
    {
        public int SchemaVersion { get; set; } = SupervisorProtocol.SchemaVersion;
        public string RequestId { get; set; } = string.Empty;
        public string ChallengeNonce { get; set; } = string.Empty;
        public int RequesterProcessId { get; set; }
        public long RequesterProcessStartUtcTicks { get; set; }
        public string ExecutablePath { get; set; } = string.Empty;
        public string ExecutableSha256 { get; set; } = string.Empty;
        public string Arguments { get; set; } = string.Empty;
        public string ArgumentsSha256 { get; set; } = string.Empty;
        public string WorkingDirectory { get; set; } = string.Empty;

        public bool IsStructurallyValid()
        {
            Guid parsed;
            return SchemaVersion == SupervisorProtocol.SchemaVersion &&
                   Guid.TryParseExact(RequestId ?? string.Empty, "N", out parsed) &&
                   WatchdogProcessIdentityPolicy.IsValidChallengeNonce(
                       ChallengeNonce) &&
                   RequesterProcessId > 0 && RequesterProcessStartUtcTicks > 0 &&
                   !string.IsNullOrWhiteSpace(ExecutablePath) &&
                   RecoveryFailureReceipt.IsSha256(ExecutableSha256) &&
                   string.Equals(
                       SupervisorProtocol.ComputeTextSha256(Arguments),
                       ArgumentsSha256,
                       StringComparison.Ordinal) &&
                   !string.IsNullOrWhiteSpace(WorkingDirectory);
        }

        public void WriteTo(BinaryWriter writer)
        {
            writer.Write(SupervisorProtocol.RequestMagic);
            writer.Write(SchemaVersion);
            writer.Write(RequestId ?? string.Empty);
            writer.Write(ChallengeNonce ?? string.Empty);
            writer.Write(RequesterProcessId);
            writer.Write(RequesterProcessStartUtcTicks);
            writer.Write(ExecutablePath ?? string.Empty);
            writer.Write(ExecutableSha256 ?? string.Empty);
            writer.Write(Arguments ?? string.Empty);
            writer.Write(ArgumentsSha256 ?? string.Empty);
            writer.Write(WorkingDirectory ?? string.Empty);
            writer.Flush();
        }

        public static SupervisorSessionLaunchRequest ReadFrom(BinaryReader reader)
        {
            var magic = ReadBoundedString(reader);
            return ReadBodyFrom(reader, magic);
        }

        public static SupervisorSessionLaunchRequest ReadBodyFrom(
            BinaryReader reader,
            string magic)
        {
            if (!string.Equals(magic, SupervisorProtocol.RequestMagic,
                    StringComparison.Ordinal))
                throw new InvalidDataException("SupervisorRequestMagicMismatch");
            return new SupervisorSessionLaunchRequest
            {
                SchemaVersion = reader.ReadInt32(),
                RequestId = ReadBoundedString(reader),
                ChallengeNonce = ReadBoundedString(reader),
                RequesterProcessId = reader.ReadInt32(),
                RequesterProcessStartUtcTicks = reader.ReadInt64(),
                ExecutablePath = ReadBoundedString(reader),
                ExecutableSha256 = ReadBoundedString(reader),
                Arguments = ReadBoundedString(reader),
                ArgumentsSha256 = ReadBoundedString(reader),
                WorkingDirectory = ReadBoundedString(reader)
            };
        }

        internal static string ReadBoundedString(BinaryReader reader)
        {
            var value = reader.ReadString();
            if (value != null && value.Length > SupervisorProtocol.MaximumTextLength)
                throw new InvalidDataException("SupervisorTextTooLong");
            return value ?? string.Empty;
        }
    }

    public sealed class SupervisorSafetyHandoffBeginRequest
    {
        public int SchemaVersion { get; set; } = SupervisorProtocol.SchemaVersion;
        public string RequestId { get; set; } = string.Empty;
        public string ChallengeNonce { get; set; } = string.Empty;
        public int RequesterProcessId { get; set; }
        public long RequesterProcessStartUtcTicks { get; set; }
        public string SessionId { get; set; } = string.Empty;
        public string HandoffId { get; set; } = string.Empty;
        public string ProjectDirectory { get; set; } = string.Empty;
        public string ReceiptJson { get; set; } = string.Empty;
        public string ReceiptCanonicalSha256 { get; set; } = string.Empty;

        public bool IsStructurallyValid()
        {
            Guid parsed;
            return SchemaVersion == SupervisorProtocol.SchemaVersion &&
                   Guid.TryParseExact(RequestId ?? string.Empty, "N", out parsed) &&
                   WatchdogProcessIdentityPolicy.IsValidChallengeNonce(
                       ChallengeNonce) &&
                   RequesterProcessId > 0 && RequesterProcessStartUtcTicks > 0 &&
                   Guid.TryParseExact(SessionId ?? string.Empty, "N", out parsed) &&
                   Guid.TryParseExact(HandoffId ?? string.Empty, "N", out parsed) &&
                   !string.IsNullOrWhiteSpace(ProjectDirectory) &&
                   !string.IsNullOrWhiteSpace(ReceiptJson) &&
                   ReceiptJson.Length <= SupervisorProtocol.MaximumTextLength &&
                   RecoveryFailureReceipt.IsSha256(ReceiptCanonicalSha256) &&
                   string.Equals(
                       SupervisorProtocol.ComputeTextSha256(ReceiptJson),
                       ReceiptCanonicalSha256,
                       StringComparison.Ordinal);
        }

        public void WriteTo(BinaryWriter writer)
        {
            writer.Write(SupervisorProtocol.SafetyHandoffBeginRequestMagic);
            writer.Write(SchemaVersion);
            writer.Write(RequestId ?? string.Empty);
            writer.Write(ChallengeNonce ?? string.Empty);
            writer.Write(RequesterProcessId);
            writer.Write(RequesterProcessStartUtcTicks);
            writer.Write(SessionId ?? string.Empty);
            writer.Write(HandoffId ?? string.Empty);
            writer.Write(ProjectDirectory ?? string.Empty);
            writer.Write(ReceiptJson ?? string.Empty);
            writer.Write(ReceiptCanonicalSha256 ?? string.Empty);
            writer.Flush();
        }

        public static SupervisorSafetyHandoffBeginRequest ReadBodyFrom(
            BinaryReader reader,
            string magic)
        {
            if (!string.Equals(
                    magic,
                    SupervisorProtocol.SafetyHandoffBeginRequestMagic,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "SupervisorSafetyHandoffBeginRequestMagicMismatch");
            return new SupervisorSafetyHandoffBeginRequest
            {
                SchemaVersion = reader.ReadInt32(),
                RequestId = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                ChallengeNonce = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                RequesterProcessId = reader.ReadInt32(),
                RequesterProcessStartUtcTicks = reader.ReadInt64(),
                SessionId = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                HandoffId = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                ProjectDirectory = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                ReceiptJson = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                ReceiptCanonicalSha256 =
                    SupervisorSessionLaunchRequest.ReadBoundedString(reader)
            };
        }
    }

    public sealed class SupervisorSafetyHandoffBeginResponse
    {
        public int SchemaVersion { get; set; } = SupervisorProtocol.SchemaVersion;
        public string RequestId { get; set; } = string.Empty;
        public string ChallengeNonce { get; set; } = string.Empty;
        public bool Accepted { get; set; }
        public string AuthorityId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string HandoffId { get; set; } = string.Empty;
        public long PermitGeneration { get; set; }
        public string PermitId { get; set; } = string.Empty;
        public long ReceiptRevision { get; set; }
        public string ReceiptCanonicalSha256 { get; set; } = string.Empty;
        public string FailureCode { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;

        public SupervisorSafetyAuthorityToken ToToken()
        {
            return new SupervisorSafetyAuthorityToken
            {
                SchemaVersion = SchemaVersion,
                AuthorityId = AuthorityId,
                SessionId = SessionId,
                HandoffId = HandoffId,
                PermitGeneration = PermitGeneration,
                PermitId = PermitId,
                ReceiptRevision = ReceiptRevision,
                ReceiptCanonicalSha256 = ReceiptCanonicalSha256
            };
        }

        public void WriteTo(BinaryWriter writer)
        {
            writer.Write(SupervisorProtocol.SafetyHandoffBeginResponseMagic);
            writer.Write(SchemaVersion);
            writer.Write(RequestId ?? string.Empty);
            writer.Write(ChallengeNonce ?? string.Empty);
            writer.Write(Accepted);
            writer.Write(AuthorityId ?? string.Empty);
            writer.Write(SessionId ?? string.Empty);
            writer.Write(HandoffId ?? string.Empty);
            writer.Write(PermitGeneration);
            writer.Write(PermitId ?? string.Empty);
            writer.Write(ReceiptRevision);
            writer.Write(ReceiptCanonicalSha256 ?? string.Empty);
            writer.Write(FailureCode ?? string.Empty);
            writer.Write(Detail ?? string.Empty);
            writer.Flush();
        }

        public static SupervisorSafetyHandoffBeginResponse ReadFrom(
            BinaryReader reader)
        {
            if (!string.Equals(
                    SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                    SupervisorProtocol.SafetyHandoffBeginResponseMagic,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "SupervisorSafetyHandoffBeginResponseMagicMismatch");
            return new SupervisorSafetyHandoffBeginResponse
            {
                SchemaVersion = reader.ReadInt32(),
                RequestId = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                ChallengeNonce = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                Accepted = reader.ReadBoolean(),
                AuthorityId = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                SessionId = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                HandoffId = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                PermitGeneration = reader.ReadInt64(),
                PermitId = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                ReceiptRevision = reader.ReadInt64(),
                ReceiptCanonicalSha256 =
                    SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                FailureCode = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                Detail = SupervisorSessionLaunchRequest.ReadBoundedString(reader)
            };
        }
    }

    public sealed class SupervisorSafetyAgentLaunchRequest
    {
        public int SchemaVersion { get; set; } = SupervisorProtocol.SchemaVersion;
        public string RequestId { get; set; } = string.Empty;
        public string ChallengeNonce { get; set; } = string.Empty;
        public int RequesterProcessId { get; set; }
        public long RequesterProcessStartUtcTicks { get; set; }
        public string SessionId { get; set; } = string.Empty;
        public long PermitGeneration { get; set; }
        public string PermitId { get; set; } = string.Empty;
        public string HandoffId { get; set; } = string.Empty;
        public string HandoffNonceSha256 { get; set; } = string.Empty;
        public string AuthorityId { get; set; } = string.Empty;
        public long AuthorityReceiptRevision { get; set; }
        public string AuthorityReceiptCanonicalSha256 { get; set; } = string.Empty;
        public string ExecutablePath { get; set; } = string.Empty;
        public string ExecutableSha256 { get; set; } = string.Empty;
        public string Arguments { get; set; } = string.Empty;
        public string ArgumentsSha256 { get; set; } = string.Empty;
        public string WorkingDirectory { get; set; } = string.Empty;

        public bool IsStructurallyValid()
        {
            Guid parsed;
            return SchemaVersion == SupervisorProtocol.SchemaVersion &&
                   Guid.TryParseExact(RequestId ?? string.Empty, "N", out parsed) &&
                   WatchdogProcessIdentityPolicy.IsValidChallengeNonce(
                       ChallengeNonce) &&
                   RequesterProcessId > 0 && RequesterProcessStartUtcTicks > 0 &&
                   Guid.TryParseExact(SessionId ?? string.Empty, "N", out parsed) &&
                   PermitGeneration > 0 &&
                   Guid.TryParseExact(PermitId ?? string.Empty, "N", out parsed) &&
                   Guid.TryParseExact(HandoffId ?? string.Empty, "N", out parsed) &&
                   RecoveryFailureReceipt.IsSha256(HandoffNonceSha256) &&
                   Guid.TryParseExact(AuthorityId ?? string.Empty, "N", out parsed) &&
                   AuthorityReceiptRevision > 0 &&
                   RecoveryFailureReceipt.IsSha256(
                       AuthorityReceiptCanonicalSha256) &&
                   !string.IsNullOrWhiteSpace(ExecutablePath) &&
                   RecoveryFailureReceipt.IsSha256(ExecutableSha256) &&
                   string.Equals(
                       SupervisorProtocol.ComputeTextSha256(Arguments),
                       ArgumentsSha256,
                       StringComparison.Ordinal) &&
                   !string.IsNullOrWhiteSpace(WorkingDirectory);
        }

        public void WriteTo(BinaryWriter writer)
        {
            writer.Write(SupervisorProtocol.SafetyAgentRequestMagic);
            writer.Write(SchemaVersion);
            writer.Write(RequestId ?? string.Empty);
            writer.Write(ChallengeNonce ?? string.Empty);
            writer.Write(RequesterProcessId);
            writer.Write(RequesterProcessStartUtcTicks);
            writer.Write(SessionId ?? string.Empty);
            writer.Write(PermitGeneration);
            writer.Write(PermitId ?? string.Empty);
            writer.Write(HandoffId ?? string.Empty);
            writer.Write(HandoffNonceSha256 ?? string.Empty);
            writer.Write(AuthorityId ?? string.Empty);
            writer.Write(AuthorityReceiptRevision);
            writer.Write(AuthorityReceiptCanonicalSha256 ?? string.Empty);
            writer.Write(ExecutablePath ?? string.Empty);
            writer.Write(ExecutableSha256 ?? string.Empty);
            writer.Write(Arguments ?? string.Empty);
            writer.Write(ArgumentsSha256 ?? string.Empty);
            writer.Write(WorkingDirectory ?? string.Empty);
            writer.Flush();
        }

        public static SupervisorSafetyAgentLaunchRequest ReadBodyFrom(
            BinaryReader reader,
            string magic)
        {
            if (!string.Equals(
                    magic,
                    SupervisorProtocol.SafetyAgentRequestMagic,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "SupervisorSafetyAgentRequestMagicMismatch");
            return new SupervisorSafetyAgentLaunchRequest
            {
                SchemaVersion = reader.ReadInt32(),
                RequestId = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                ChallengeNonce = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                RequesterProcessId = reader.ReadInt32(),
                RequesterProcessStartUtcTicks = reader.ReadInt64(),
                SessionId = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                PermitGeneration = reader.ReadInt64(),
                PermitId = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                HandoffId = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                HandoffNonceSha256 =
                    SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                AuthorityId = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                AuthorityReceiptRevision = reader.ReadInt64(),
                AuthorityReceiptCanonicalSha256 =
                    SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                ExecutablePath = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                ExecutableSha256 = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                Arguments = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                ArgumentsSha256 = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                WorkingDirectory = SupervisorSessionLaunchRequest.ReadBoundedString(reader)
            };
        }
    }

    public sealed class SupervisorSafetyAuthorityReadRequest
    {
        public int SchemaVersion { get; set; } = SupervisorProtocol.SchemaVersion;
        public string RequestId { get; set; } = string.Empty;
        public string ChallengeNonce { get; set; } = string.Empty;
        public int RequesterProcessId { get; set; }
        public long RequesterProcessStartUtcTicks { get; set; }
        public string AuthorityId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string HandoffId { get; set; } = string.Empty;
        public long PermitGeneration { get; set; }
        public string PermitId { get; set; } = string.Empty;
        public long InitialReceiptRevision { get; set; }
        public string InitialReceiptCanonicalSha256 { get; set; } = string.Empty;

        public bool IsStructurallyValid()
        {
            Guid parsed;
            return SchemaVersion == SupervisorProtocol.SchemaVersion &&
                   Guid.TryParseExact(RequestId ?? string.Empty, "N", out parsed) &&
                   WatchdogProcessIdentityPolicy.IsValidChallengeNonce(
                       ChallengeNonce) &&
                   RequesterProcessId > 0 && RequesterProcessStartUtcTicks > 0 &&
                   Guid.TryParseExact(AuthorityId ?? string.Empty, "N", out parsed) &&
                   Guid.TryParseExact(SessionId ?? string.Empty, "N", out parsed) &&
                   Guid.TryParseExact(HandoffId ?? string.Empty, "N", out parsed) &&
                   PermitGeneration > 0 &&
                   Guid.TryParseExact(PermitId ?? string.Empty, "N", out parsed) &&
                   InitialReceiptRevision > 0 &&
                   RecoveryFailureReceipt.IsSha256(
                       InitialReceiptCanonicalSha256);
        }

        public void WriteTo(BinaryWriter writer)
        {
            writer.Write(SupervisorProtocol.SafetyAuthorityReadRequestMagic);
            writer.Write(SchemaVersion);
            writer.Write(RequestId ?? string.Empty);
            writer.Write(ChallengeNonce ?? string.Empty);
            writer.Write(RequesterProcessId);
            writer.Write(RequesterProcessStartUtcTicks);
            writer.Write(AuthorityId ?? string.Empty);
            writer.Write(SessionId ?? string.Empty);
            writer.Write(HandoffId ?? string.Empty);
            writer.Write(PermitGeneration);
            writer.Write(PermitId ?? string.Empty);
            writer.Write(InitialReceiptRevision);
            writer.Write(InitialReceiptCanonicalSha256 ?? string.Empty);
            writer.Flush();
        }

        public static SupervisorSafetyAuthorityReadRequest ReadBodyFrom(
            BinaryReader reader,
            string magic)
        {
            if (!string.Equals(
                    magic,
                    SupervisorProtocol.SafetyAuthorityReadRequestMagic,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "SupervisorSafetyAuthorityReadRequestMagicMismatch");
            return new SupervisorSafetyAuthorityReadRequest
            {
                SchemaVersion = reader.ReadInt32(),
                RequestId = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                ChallengeNonce = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                RequesterProcessId = reader.ReadInt32(),
                RequesterProcessStartUtcTicks = reader.ReadInt64(),
                AuthorityId = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                SessionId = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                HandoffId = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                PermitGeneration = reader.ReadInt64(),
                PermitId = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                InitialReceiptRevision = reader.ReadInt64(),
                InitialReceiptCanonicalSha256 =
                    SupervisorSessionLaunchRequest.ReadBoundedString(reader)
            };
        }
    }

    public sealed class SupervisorSafetyAuthorityReadResponse
    {
        public int SchemaVersion { get; set; } = SupervisorProtocol.SchemaVersion;
        public string RequestId { get; set; } = string.Empty;
        public string ChallengeNonce { get; set; } = string.Empty;
        public bool Accepted { get; set; }
        public string AuthorityId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string HandoffId { get; set; } = string.Empty;
        public long ReceiptRevision { get; set; }
        public string ReceiptCanonicalSha256 { get; set; } = string.Empty;
        public string ReceiptJson { get; set; } = string.Empty;
        public string FailureCode { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;

        public void WriteTo(BinaryWriter writer)
        {
            writer.Write(SupervisorProtocol.SafetyAuthorityReadResponseMagic);
            writer.Write(SchemaVersion);
            writer.Write(RequestId ?? string.Empty);
            writer.Write(ChallengeNonce ?? string.Empty);
            writer.Write(Accepted);
            writer.Write(AuthorityId ?? string.Empty);
            writer.Write(SessionId ?? string.Empty);
            writer.Write(HandoffId ?? string.Empty);
            writer.Write(ReceiptRevision);
            writer.Write(ReceiptCanonicalSha256 ?? string.Empty);
            writer.Write(ReceiptJson ?? string.Empty);
            writer.Write(FailureCode ?? string.Empty);
            writer.Write(Detail ?? string.Empty);
            writer.Flush();
        }

        public static SupervisorSafetyAuthorityReadResponse ReadFrom(
            BinaryReader reader)
        {
            if (!string.Equals(
                    SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                    SupervisorProtocol.SafetyAuthorityReadResponseMagic,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "SupervisorSafetyAuthorityReadResponseMagicMismatch");
            return new SupervisorSafetyAuthorityReadResponse
            {
                SchemaVersion = reader.ReadInt32(),
                RequestId = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                ChallengeNonce = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                Accepted = reader.ReadBoolean(),
                AuthorityId = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                SessionId = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                HandoffId = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                ReceiptRevision = reader.ReadInt64(),
                ReceiptCanonicalSha256 =
                    SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                ReceiptJson = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                FailureCode = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                Detail = SupervisorSessionLaunchRequest.ReadBoundedString(reader)
            };
        }
    }

    public sealed class SupervisorSafetyAgentLaunchResponse
    {
        public int SchemaVersion { get; set; } = SupervisorProtocol.SchemaVersion;
        public string RequestId { get; set; } = string.Empty;
        public string ChallengeNonce { get; set; } = string.Empty;
        public bool Accepted { get; set; }
        public int ProcessId { get; set; }
        public long ProcessStartUtcTicks { get; set; }
        public string FailureCode { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;

        public void WriteTo(BinaryWriter writer)
        {
            writer.Write(SupervisorProtocol.SafetyAgentResponseMagic);
            writer.Write(SchemaVersion);
            writer.Write(RequestId ?? string.Empty);
            writer.Write(ChallengeNonce ?? string.Empty);
            writer.Write(Accepted);
            writer.Write(ProcessId);
            writer.Write(ProcessStartUtcTicks);
            writer.Write(FailureCode ?? string.Empty);
            writer.Write(Detail ?? string.Empty);
            writer.Flush();
        }

        public static SupervisorSafetyAgentLaunchResponse ReadFrom(
            BinaryReader reader)
        {
            if (!string.Equals(
                    SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                    SupervisorProtocol.SafetyAgentResponseMagic,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "SupervisorSafetyAgentResponseMagicMismatch");
            return new SupervisorSafetyAgentLaunchResponse
            {
                SchemaVersion = reader.ReadInt32(),
                RequestId = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                ChallengeNonce = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                Accepted = reader.ReadBoolean(),
                ProcessId = reader.ReadInt32(),
                ProcessStartUtcTicks = reader.ReadInt64(),
                FailureCode = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                Detail = SupervisorSessionLaunchRequest.ReadBoundedString(reader)
            };
        }
    }

    public enum SupervisorP0AlarmAction
    {
        Latch = 1,
        ClearTransient = 2,
        MuteBuzzer = 3
    }

    public sealed class SupervisorP0AlarmRequest
    {
        public int SchemaVersion { get; set; } = SupervisorProtocol.SchemaVersion;
        public string RequestId { get; set; } = string.Empty;
        public string ChallengeNonce { get; set; } = string.Empty;
        public int RequesterProcessId { get; set; }
        public long RequesterProcessStartUtcTicks { get; set; }
        public SupervisorP0AlarmAction Action { get; set; }
        public string EventId { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;

        public bool IsStructurallyValid()
        {
            Guid parsed;
            return SchemaVersion == SupervisorProtocol.SchemaVersion &&
                   Guid.TryParseExact(RequestId ?? string.Empty, "N", out parsed) &&
                   WatchdogProcessIdentityPolicy.IsValidChallengeNonce(
                       ChallengeNonce) &&
                   RequesterProcessId > 0 && RequesterProcessStartUtcTicks > 0 &&
                   Enum.IsDefined(typeof(SupervisorP0AlarmAction), Action) &&
                   Guid.TryParseExact(EventId ?? string.Empty, "N", out parsed) &&
                   (Code ?? string.Empty).Length <= 1024 &&
                   (Detail ?? string.Empty).Length <=
                       SupervisorProtocol.MaximumTextLength;
        }

        public void WriteTo(BinaryWriter writer)
        {
            writer.Write(SupervisorProtocol.P0AlarmRequestMagic);
            writer.Write(SchemaVersion);
            writer.Write(RequestId ?? string.Empty);
            writer.Write(ChallengeNonce ?? string.Empty);
            writer.Write(RequesterProcessId);
            writer.Write(RequesterProcessStartUtcTicks);
            writer.Write((int)Action);
            writer.Write(EventId ?? string.Empty);
            writer.Write(Code ?? string.Empty);
            writer.Write(Detail ?? string.Empty);
            writer.Flush();
        }

        public static SupervisorP0AlarmRequest ReadBodyFrom(
            BinaryReader reader,
            string magic)
        {
            if (!string.Equals(
                    magic,
                    SupervisorProtocol.P0AlarmRequestMagic,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "SupervisorP0AlarmRequestMagicMismatch");
            return new SupervisorP0AlarmRequest
            {
                SchemaVersion = reader.ReadInt32(),
                RequestId = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                ChallengeNonce = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                RequesterProcessId = reader.ReadInt32(),
                RequesterProcessStartUtcTicks = reader.ReadInt64(),
                Action = (SupervisorP0AlarmAction)reader.ReadInt32(),
                EventId = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                Code = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                Detail = SupervisorSessionLaunchRequest.ReadBoundedString(reader)
            };
        }
    }

    public sealed class SupervisorP0AlarmResponse
    {
        public int SchemaVersion { get; set; } = SupervisorProtocol.SchemaVersion;
        public string RequestId { get; set; } = string.Empty;
        public string ChallengeNonce { get; set; } = string.Empty;
        public bool Accepted { get; set; }
        public string FailureCode { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;

        public void WriteTo(BinaryWriter writer)
        {
            writer.Write(SupervisorProtocol.P0AlarmResponseMagic);
            writer.Write(SchemaVersion);
            writer.Write(RequestId ?? string.Empty);
            writer.Write(ChallengeNonce ?? string.Empty);
            writer.Write(Accepted);
            writer.Write(FailureCode ?? string.Empty);
            writer.Write(Detail ?? string.Empty);
            writer.Flush();
        }

        public static SupervisorP0AlarmResponse ReadFrom(BinaryReader reader)
        {
            if (!string.Equals(
                    SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                    SupervisorProtocol.P0AlarmResponseMagic,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "SupervisorP0AlarmResponseMagicMismatch");
            return new SupervisorP0AlarmResponse
            {
                SchemaVersion = reader.ReadInt32(),
                RequestId = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                ChallengeNonce = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                Accepted = reader.ReadBoolean(),
                FailureCode = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                Detail = SupervisorSessionLaunchRequest.ReadBoundedString(reader)
            };
        }
    }

    public sealed class SupervisorSessionLaunchResponse
    {
        public int SchemaVersion { get; set; } = SupervisorProtocol.SchemaVersion;
        public string RequestId { get; set; } = string.Empty;
        public string ChallengeNonce { get; set; } = string.Empty;
        public bool Accepted { get; set; }
        public int ProcessId { get; set; }
        public long ProcessStartUtcTicks { get; set; }
        public string FailureCode { get; set; } = string.Empty;
        public string Detail { get; set; } = string.Empty;

        public void WriteTo(BinaryWriter writer)
        {
            writer.Write(SupervisorProtocol.ResponseMagic);
            writer.Write(SchemaVersion);
            writer.Write(RequestId ?? string.Empty);
            writer.Write(ChallengeNonce ?? string.Empty);
            writer.Write(Accepted);
            writer.Write(ProcessId);
            writer.Write(ProcessStartUtcTicks);
            writer.Write(FailureCode ?? string.Empty);
            writer.Write(Detail ?? string.Empty);
            writer.Flush();
        }

        public static SupervisorSessionLaunchResponse ReadFrom(BinaryReader reader)
        {
            if (!string.Equals(
                    SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                    SupervisorProtocol.ResponseMagic,
                    StringComparison.Ordinal))
                throw new InvalidDataException("SupervisorResponseMagicMismatch");
            return new SupervisorSessionLaunchResponse
            {
                SchemaVersion = reader.ReadInt32(),
                RequestId = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                ChallengeNonce = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                Accepted = reader.ReadBoolean(),
                ProcessId = reader.ReadInt32(),
                ProcessStartUtcTicks = reader.ReadInt64(),
                FailureCode = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                Detail = SupervisorSessionLaunchRequest.ReadBoundedString(reader)
            };
        }
    }
}
