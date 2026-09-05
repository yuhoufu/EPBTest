using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Web.Script.Serialization;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Watchdog
{
    /// <summary>
    /// Formal-mode SafetyAgent launch boundary.  The watchdog session host
    /// presents the exact handoff/permit identity; only the LocalSystem
    /// supervisor owns Process.Start and the durable process identity.
    /// </summary>
    internal static class SupervisorSafetyAgentLaunchClient
    {
        private static readonly JavaScriptSerializer Json =
            new JavaScriptSerializer();

        internal static Process Start(
            WatchdogSafetyHandoffReceipt receipt,
            string executable,
            string arguments)
        {
            SupervisorSafetyAuthorityToken authorityToken;
            return Start(
                receipt,
                executable,
                arguments,
                null,
                out authorityToken);
        }

        internal static Process Start(
            WatchdogSafetyHandoffReceipt receipt,
            string executable,
            string arguments,
            SupervisorSafetyAuthorityToken fixedAuthorityToken,
            out SupervisorSafetyAuthorityToken authorityToken)
        {
            authorityToken = fixedAuthorityToken;
            if (receipt == null) throw new ArgumentNullException(nameof(receipt));
            if (receipt.SchemaVersion != SupervisorProtocol.SchemaVersion ||
                receipt.State < WatchdogSafetyHandoffState.Accepted ||
                receipt.IsTerminal)
                throw new InvalidDataException(
                    "SupervisorSafetyAuthorityReceiptStateInvalid");
            var executablePath = Path.GetFullPath(executable);
            var workingDirectory = Path.GetDirectoryName(executablePath) ??
                                   Environment.CurrentDirectory;
            var token = Begin(receipt);
            if (fixedAuthorityToken != null &&
                !TokensMatch(fixedAuthorityToken, token))
                throw new InvalidDataException(
                    "SupervisorSafetyAuthorityTokenChanged");
            authorityToken = fixedAuthorityToken ?? token;
            SupervisorSafetyAgentLaunchRequest request;
            using (var current = Process.GetCurrentProcess())
            {
                request = new SupervisorSafetyAgentLaunchRequest
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    ChallengeNonce = Guid.NewGuid().ToString("N"),
                    RequesterProcessId = current.Id,
                    RequesterProcessStartUtcTicks =
                        current.StartTime.ToUniversalTime().Ticks,
                    SessionId = receipt.SessionId,
                    PermitGeneration = receipt.RelaunchPermitGeneration,
                    PermitId = receipt.RelaunchPermitId,
                    HandoffId = receipt.HandoffId,
                    HandoffNonceSha256 =
                        SupervisorProtocol.ComputeTextSha256(receipt.Nonce),
                    AuthorityId = token.AuthorityId,
                    AuthorityReceiptRevision = token.ReceiptRevision,
                    AuthorityReceiptCanonicalSha256 =
                        token.ReceiptCanonicalSha256,
                    ExecutablePath = executablePath,
                    ExecutableSha256 = SupervisorProtocol.ComputeSha256(
                        executablePath),
                    Arguments = arguments ?? string.Empty,
                    ArgumentsSha256 = SupervisorProtocol.ComputeTextSha256(
                        arguments ?? string.Empty),
                    WorkingDirectory = Path.GetFullPath(workingDirectory)
                };
            }

            SupervisorSafetyAgentLaunchResponse response;
            using (var pipe = new NamedPipeClientStream(
                       ".",
                       SupervisorProtocol.PipeName,
                       PipeDirection.InOut,
                       PipeOptions.None))
            {
                pipe.Connect(5000);
                using (var deadline = new PipeExchangeDeadline(pipe, 10000))
                using (var writer = new BinaryWriter(
                           pipe,
                           new UTF8Encoding(false),
                           true))
                using (var reader = new BinaryReader(
                           pipe,
                           new UTF8Encoding(false),
                           true))
                {
                    request.WriteTo(writer);
                    response = SupervisorSafetyAgentLaunchResponse.ReadFrom(reader);
                }
            }

            if (response?.SchemaVersion != SupervisorProtocol.SchemaVersion ||
                !response.Accepted ||
                !string.Equals(response.RequestId, request.RequestId,
                    StringComparison.Ordinal) ||
                !string.Equals(response.ChallengeNonce, request.ChallengeNonce,
                    StringComparison.Ordinal) ||
                response.ProcessId <= 0 || response.ProcessStartUtcTicks <= 0)
                throw new InvalidOperationException(
                    "SupervisorSafetyAgentLaunchRejected:" +
                    (response?.FailureCode ?? "InvalidResponse") + ":" +
                    (response?.Detail ?? string.Empty));

            var process = Process.GetProcessById(response.ProcessId);
            try
            {
                if (process.HasExited ||
                    process.StartTime.ToUniversalTime().Ticks !=
                        response.ProcessStartUtcTicks)
                    throw new InvalidOperationException(
                        "SupervisorSafetyAgentIdentityMismatch");
                return process;
            }
            catch
            {
                try { process.Dispose(); } catch { }
                throw;
            }
        }

        internal static bool TryReadAuthority(
            SupervisorSafetyAuthorityToken authorityToken,
            out WatchdogSafetyHandoffReceipt receipt,
            out string failure)
        {
            receipt = null;
            failure = string.Empty;
            try
            {
                if (authorityToken?.IsValid() != true)
                    throw new InvalidDataException(
                        "SupervisorSafetyAuthorityTokenInvalid");

                SupervisorSafetyAuthorityReadRequest request;
                using (var current = Process.GetCurrentProcess())
                {
                    request = new SupervisorSafetyAuthorityReadRequest
                    {
                        RequestId = Guid.NewGuid().ToString("N"),
                        ChallengeNonce = Guid.NewGuid().ToString("N"),
                        RequesterProcessId = current.Id,
                        RequesterProcessStartUtcTicks =
                            current.StartTime.ToUniversalTime().Ticks,
                        AuthorityId = authorityToken.AuthorityId,
                        SessionId = authorityToken.SessionId,
                        HandoffId = authorityToken.HandoffId,
                        PermitGeneration = authorityToken.PermitGeneration,
                        PermitId = authorityToken.PermitId,
                        InitialReceiptRevision = authorityToken.ReceiptRevision,
                        InitialReceiptCanonicalSha256 =
                            authorityToken.ReceiptCanonicalSha256
                    };
                }

                SupervisorSafetyAuthorityReadResponse response;
                using (var pipe = new NamedPipeClientStream(
                           ".",
                           SupervisorProtocol.PipeName,
                           PipeDirection.InOut,
                           PipeOptions.None))
                {
                    pipe.Connect(5000);
                    using (var deadline = new PipeExchangeDeadline(pipe, 10000))
                    using (var writer = new BinaryWriter(
                               pipe,
                               new UTF8Encoding(false),
                               true))
                    using (var reader = new BinaryReader(
                               pipe,
                               new UTF8Encoding(false),
                               true))
                    {
                        request.WriteTo(writer);
                        response = SupervisorSafetyAuthorityReadResponse
                            .ReadFrom(reader);
                    }
                }

                if (response?.SchemaVersion != SupervisorProtocol.SchemaVersion ||
                    !response.Accepted ||
                    !string.Equals(response.RequestId, request.RequestId,
                        StringComparison.Ordinal) ||
                    !string.Equals(response.ChallengeNonce,
                        request.ChallengeNonce, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "SupervisorSafetyAuthorityReadRejected:" +
                        (response?.FailureCode ?? "InvalidResponse") + ":" +
                        (response?.Detail ?? string.Empty));
                if (!string.Equals(response.AuthorityId,
                        authorityToken.AuthorityId, StringComparison.Ordinal) ||
                    !string.Equals(response.SessionId,
                        authorityToken.SessionId, StringComparison.Ordinal) ||
                    !string.Equals(response.HandoffId,
                        authorityToken.HandoffId, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        "SupervisorSafetyAuthorityReadIdentityMismatch");
                if (response.ReceiptRevision < authorityToken.ReceiptRevision ||
                    !string.Equals(
                        SupervisorProtocol.ComputeTextSha256(
                            response.ReceiptJson),
                        response.ReceiptCanonicalSha256,
                        StringComparison.Ordinal))
                    throw new InvalidDataException(
                        "SupervisorSafetyAuthorityReadCanonicalHashMismatch");

                var currentReceipt = Json.Deserialize<WatchdogSafetyHandoffReceipt>(
                    response.ReceiptJson);
                if (currentReceipt?.SchemaVersion !=
                        SupervisorProtocol.SchemaVersion ||
                    !currentReceipt.IsValidFor(authorityToken.SessionId) ||
                    !string.Equals(currentReceipt.HandoffId,
                        authorityToken.HandoffId, StringComparison.Ordinal) ||
                    currentReceipt.RelaunchPermitGeneration !=
                        authorityToken.PermitGeneration ||
                    !string.Equals(currentReceipt.RelaunchPermitId,
                        authorityToken.PermitId, StringComparison.Ordinal) ||
                    currentReceipt.Revision != response.ReceiptRevision)
                    throw new InvalidDataException(
                        "SupervisorSafetyAuthorityReadReceiptMismatch");

                receipt = currentReceipt;
                return true;
            }
            catch (Exception ex)
            {
                failure = ex.GetBaseException().Message;
                return false;
            }
        }

        private static SupervisorSafetyAuthorityToken Begin(
            WatchdogSafetyHandoffReceipt receipt)
        {
            var receiptJson =
                SupervisorSafetyAuthorityStore.SerializeReceipt(receipt);
            SupervisorSafetyHandoffBeginRequest request;
            using (var current = Process.GetCurrentProcess())
            {
                request = new SupervisorSafetyHandoffBeginRequest
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    ChallengeNonce = Guid.NewGuid().ToString("N"),
                    RequesterProcessId = current.Id,
                    RequesterProcessStartUtcTicks =
                        current.StartTime.ToUniversalTime().Ticks,
                    SessionId = receipt.SessionId,
                    HandoffId = receipt.HandoffId,
                    ProjectDirectory = Path.GetFullPath(receipt.ProjectDirectory),
                    ReceiptJson = receiptJson,
                    ReceiptCanonicalSha256 =
                        SupervisorProtocol.ComputeTextSha256(receiptJson)
                };
            }

            SupervisorSafetyHandoffBeginResponse response;
            using (var pipe = new NamedPipeClientStream(
                       ".",
                       SupervisorProtocol.PipeName,
                       PipeDirection.InOut,
                       PipeOptions.None))
            {
                pipe.Connect(5000);
                using (var deadline = new PipeExchangeDeadline(pipe, 10000))
                using (var writer = new BinaryWriter(
                           pipe,
                           new UTF8Encoding(false),
                           true))
                using (var reader = new BinaryReader(
                           pipe,
                           new UTF8Encoding(false),
                           true))
                {
                    request.WriteTo(writer);
                    response = SupervisorSafetyHandoffBeginResponse.ReadFrom(reader);
                }
            }

            var token = response?.ToToken();
            if (response?.SchemaVersion != SupervisorProtocol.SchemaVersion ||
                !response.Accepted || token?.IsValid() != true ||
                !string.Equals(response.RequestId, request.RequestId,
                    StringComparison.Ordinal) ||
                !string.Equals(response.ChallengeNonce, request.ChallengeNonce,
                    StringComparison.Ordinal) ||
                !string.Equals(token.SessionId, receipt.SessionId,
                    StringComparison.Ordinal) ||
                !string.Equals(token.HandoffId, receipt.HandoffId,
                    StringComparison.Ordinal) ||
                token.PermitGeneration != receipt.RelaunchPermitGeneration ||
                !string.Equals(token.PermitId, receipt.RelaunchPermitId,
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "SupervisorSafetyHandoffBeginRejected:" +
                    (response?.FailureCode ?? "InvalidResponse") + ":" +
                    (response?.Detail ?? string.Empty));
            return token;
        }

        private static bool TokensMatch(
            SupervisorSafetyAuthorityToken left,
            SupervisorSafetyAuthorityToken right)
        {
            return left?.IsValid() == true && right?.IsValid() == true &&
                   left.SchemaVersion == right.SchemaVersion &&
                   string.Equals(left.AuthorityId, right.AuthorityId,
                       StringComparison.Ordinal) &&
                   string.Equals(left.SessionId, right.SessionId,
                       StringComparison.Ordinal) &&
                   string.Equals(left.HandoffId, right.HandoffId,
                       StringComparison.Ordinal) &&
                   left.PermitGeneration == right.PermitGeneration &&
                   string.Equals(left.PermitId, right.PermitId,
                       StringComparison.Ordinal) &&
                   left.ReceiptRevision == right.ReceiptRevision &&
                   string.Equals(left.ReceiptCanonicalSha256,
                       right.ReceiptCanonicalSha256,
                       StringComparison.Ordinal);
        }
    }
}
