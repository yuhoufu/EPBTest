using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace MTTFTest.RecoveryControl
{
    // Framework-only wire contract: an independently deployed Guard does not
    // load Watchdog.Protocol from the application's Current directory.
    public static class RecoveryGuardSupervisorProtocol
    {
        public const string PipeName = "MTTFTestSupervisor.Control.v7.V217";
        public const string RequestMagic = "MTTF-RECOVERY-GUARD-LAUNCH-V1";
        public const string ResponseMagic = "MTTF-RECOVERY-GUARD-LAUNCH-RESULT-V1";
        public const string SafetyPrepareMagic = "MTTF-RECOVERY-GUARD-SAFETY-PREPARE-V1";
        public const string SafetyPrepareResponseMagic = "MTTF-RECOVERY-GUARD-SAFETY-PREPARE-RESULT-V1";
        public const string SafetyExecuteMagic = "MTTF-RECOVERY-GUARD-SAFETY-EXECUTE-V1";
        public const string SafetyExecuteResponseMagic = "MTTF-RECOVERY-GUARD-SAFETY-EXECUTE-RESULT-V1";
        public const string LaunchPrepareMagic = "MTTF-RECOVERY-GUARD-LAUNCH-PREPARE-V1";
        public const string LaunchPrepareResponseMagic = "MTTF-RECOVERY-GUARD-LAUNCH-PREPARE-RESULT-V1";
        private const int MaximumBytes = 64 * 1024;

        public static bool IsId(string value) => Guid.TryParseExact(value, "N", out var id) && id != Guid.Empty;

        public static void Write<T>(BinaryWriter writer, string magic, T value)
        {
            var bytes = new UTF8Encoding(false, true).GetBytes(new JavaScriptSerializer().Serialize(value));
            if (bytes.Length > MaximumBytes) throw new InvalidDataException("RecoveryGuardMessageOversized");
            writer.Write(magic);
            writer.Write(bytes.Length);
            writer.Write(bytes);
            writer.Flush();
        }

        public static T ReadBody<T>(BinaryReader reader)
        {
            var count = reader.ReadInt32();
            if (count < 1 || count > MaximumBytes) throw new InvalidDataException("RecoveryGuardMessageSizeInvalid");
            var bytes = reader.ReadBytes(count);
            if (bytes.Length != count) throw new EndOfStreamException("RecoveryGuardMessageTruncated");
            return new JavaScriptSerializer().Deserialize<T>(new UTF8Encoding(false, true).GetString(bytes));
        }

        public static void ReadMagic(BinaryReader reader, string expected)
        {
            // Our ASCII magics fit in a single BinaryWriter length byte.
            // Reject a hostile length prefix before allocating a string.
            if (expected.Length >= 128 || reader.ReadByte() != expected.Length ||
                Encoding.ASCII.GetString(reader.ReadBytes(expected.Length)) != expected)
                throw new InvalidDataException("RecoveryGuardResponseMagicMismatch");
        }

        public static RecoveryGuardLaunchResponse Launch(RecoveryGuardLaunchRequest request)
            => LaunchAsync(request, CancellationToken.None).GetAwaiter().GetResult();

        public static Task<RecoveryGuardLaunchResponse> LaunchAsync(RecoveryGuardLaunchRequest request,
            CancellationToken cancellationToken)
            => LaunchOnPipeAsync(request, PipeName, cancellationToken);

        // Private endpoint seam permits isolated transport tests without
        // connecting to the installed Supervisor or exposing endpoint overrides.
        private static async Task<RecoveryGuardLaunchResponse> LaunchOnPipeAsync(RecoveryGuardLaunchRequest request,
            string pipeName, CancellationToken cancellationToken)
        {
            request.Validate();
            var response = await ExchangeAsync<RecoveryGuardLaunchRequest, RecoveryGuardLaunchResponse>(request,
                pipeName, RequestMagic, ResponseMagic, cancellationToken).ConfigureAwait(false);
            if (response?.SchemaVersion != 1 || response.RequestId != request.RequestId ||
                response.OperationId != request.OperationId || (response.Accepted && response.Process?.IsValid() != true))
                throw new InvalidDataException("RecoveryGuardResponseIdentityMismatch");
            return response;
        }

        public static async Task<RecoveryGuardSafetyPrepareResponse> PrepareSafetyAsync(RecoveryGuardSafetyPrepareRequest request,
            CancellationToken cancellationToken)
        {
            request.Validate();
            var response = await ExchangeAsync<RecoveryGuardSafetyPrepareRequest, RecoveryGuardSafetyPrepareResponse>(request,
                PipeName, SafetyPrepareMagic, SafetyPrepareResponseMagic, cancellationToken).ConfigureAwait(false);
            if (response?.SchemaVersion != 1 || response.RequestId != request.RequestId ||
                (response.Accepted && !IsId(response.SafetyAuthorityId)))
                throw new InvalidDataException("RecoveryGuardResponseIdentityMismatch");
            return response;
        }

        private static async Task<TResponse> ExchangeAsync<TRequest, TResponse>(TRequest request,
            string pipeName, string requestMagic, string responseMagic, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            // Framework pipe reads may not cancel an already-issued overlapped
            // read from the token alone. Closing our handle ends that I/O while
            // leaving the peer and any server-side operation untouched.
            using (deadline.Token.Register(() => pipe.Dispose()))
            {
                try
                {
                    deadline.CancelAfter(5000);
                    await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
                    deadline.CancelAfter(20000);
                    // All public calls use the canonical endpoint. The private
                    // alternate pipe seam is only used by isolated wire tests.
                    if (pipeName == PipeName) RecoverySupervisorPeer.Assert(pipe);
                    cancellationToken.ThrowIfCancellationRequested();
                    byte[] requestBytes;
                    using (var buffer = new MemoryStream())
                    using (var writer = new BinaryWriter(buffer, Encoding.UTF8, true))
                    {
                        Write(writer, requestMagic, request);
                        requestBytes = buffer.ToArray();
                    }
                    await pipe.WriteAsync(requestBytes, 0, requestBytes.Length, deadline.Token).ConfigureAwait(false);
                    await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);
                    var prefix = await ReadExactlyAsync(pipe, 1, deadline.Token).ConfigureAwait(false);
                    if (prefix[0] != responseMagic.Length)
                        throw new InvalidDataException("RecoveryGuardResponseMagicMismatch");
                    var magic = await ReadExactlyAsync(pipe, responseMagic.Length, deadline.Token).ConfigureAwait(false);
                    if (Encoding.ASCII.GetString(magic) != responseMagic)
                        throw new InvalidDataException("RecoveryGuardResponseMagicMismatch");
                    var sizeBytes = await ReadExactlyAsync(pipe, 4, deadline.Token).ConfigureAwait(false);
                    int count;
                    using (var sizeReader = new BinaryReader(new MemoryStream(sizeBytes))) count = sizeReader.ReadInt32();
                    if (count < 1 || count > MaximumBytes)
                        throw new InvalidDataException("RecoveryGuardMessageSizeInvalid");
                    var body = await ReadExactlyAsync(pipe, count, deadline.Token).ConfigureAwait(false);
                    var response = new JavaScriptSerializer().Deserialize<TResponse>(
                        new UTF8Encoding(false, true).GetString(body));
                    if (pipeName == PipeName) RecoverySupervisorPeer.Assert(pipe);
                    cancellationToken.ThrowIfCancellationRequested();
                    return response;
                }
                catch (Exception ex) when (deadline.IsCancellationRequested &&
                    (ex is OperationCanceledException || ex is IOException || ex is ObjectDisposedException))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // The server may already have consumed the operation.
                    // A transport timeout never proves that no process started.
                    throw new IOException("RecoveryGuardLaunchTransportTimedOut;ReconcileOperationBeforeRetry");
                }
            }
        }

        public static async Task<RecoveryGuardSafetyExecuteResponse> ExecuteSafetyAsync(RecoveryGuardSafetyExecuteRequest request,
            CancellationToken cancellationToken)
        {
            request.Validate();
            var response = await ExchangeAsync<RecoveryGuardSafetyExecuteRequest, RecoveryGuardSafetyExecuteResponse>(request,
                PipeName, SafetyExecuteMagic, SafetyExecuteResponseMagic, cancellationToken).ConfigureAwait(false);
            if (response?.SchemaVersion != 1 || response.RequestId != request.RequestId ||
                response.SafetyAuthorityId != request.SafetyAuthorityId || !Enum.IsDefined(typeof(RecoveryGuardSafetyExecutionState), response.State) ||
                (response.State == RecoveryGuardSafetyExecutionState.Completed && (!response.Accepted || response.Worker?.IsValid() != true)))
                throw new InvalidDataException("RecoveryGuardResponseIdentityMismatch");
            return response;
        }

        public static async Task<RecoveryGuardLaunchPreparationResponse> PrepareLaunchAsync(RecoveryGuardLaunchPreparationRequest request,
            CancellationToken cancellationToken)
        {
            request.Validate();
            var response = await ExchangeAsync<RecoveryGuardLaunchPreparationRequest, RecoveryGuardLaunchPreparationResponse>(request,
                PipeName, LaunchPrepareMagic, LaunchPrepareResponseMagic, cancellationToken).ConfigureAwait(false);
            if (response?.SchemaVersion != 1 || response.RequestId != request.RequestId ||
                response.SafetyAuthorityId != request.SafetyAuthorityId || (response.Accepted && !IsId(response.OperationId)))
                throw new InvalidDataException("RecoveryGuardResponseIdentityMismatch");
            return response;
        }

        private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count, CancellationToken cancellationToken)
        {
            var bytes = new byte[count];
            var offset = 0;
            while (offset < count)
            {
                var read = await stream.ReadAsync(bytes, offset, count - offset, cancellationToken).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("RecoveryGuardMessageTruncated");
                offset += read;
            }
            return bytes;
        }
    }

    public sealed class RecoveryGuardLaunchRequest
    {
        public int SchemaVersion { get; set; } = 1;
        public string RequestId { get; set; }
        public string TransactionId { get; set; }
        public long Epoch { get; set; }
        public RecoveryProcessIdentity Owner { get; set; }
        public string OperationId { get; set; }
        public string SafetyAuthorityId { get; set; }

        public void Validate()
        {
            if (SchemaVersion != 1 || !RecoveryGuardSupervisorProtocol.IsId(RequestId) ||
                !RecoveryGuardSupervisorProtocol.IsId(TransactionId) || Epoch < 1 || Owner?.IsValid() != true ||
                !RecoveryGuardSupervisorProtocol.IsId(OperationId) || !RecoveryGuardSupervisorProtocol.IsId(SafetyAuthorityId))
                throw new InvalidDataException("RecoveryGuardLaunchRequestInvalid");
        }
    }

    public sealed class RecoveryGuardSafetyPrepareRequest
    {
        public int SchemaVersion { get; set; } = 1;
        public string RequestId { get; set; }
        public string TransactionId { get; set; }
        public long Epoch { get; set; }
        public RecoveryProcessIdentity Owner { get; set; }

        public void Validate()
        {
            if (SchemaVersion != 1 || !RecoveryGuardSupervisorProtocol.IsId(RequestId) ||
                !RecoveryGuardSupervisorProtocol.IsId(TransactionId) || Epoch < 1 || Owner?.IsValid() != true)
                throw new InvalidDataException("RecoveryGuardSafetyPrepareRequestInvalid");
        }
    }

    public sealed class RecoveryGuardSafetyPrepareResponse
    {
        public int SchemaVersion { get; set; } = 1;
        public string RequestId { get; set; }
        public bool Accepted { get; set; }
        public string SafetyAuthorityId { get; set; }
        public string Detail { get; set; }
    }

    public sealed class RecoveryGuardSafetyExecuteRequest
    {
        public int SchemaVersion { get; set; } = 1;
        public string RequestId { get; set; }
        public string TransactionId { get; set; }
        public long Epoch { get; set; }
        public RecoveryProcessIdentity Owner { get; set; }
        public string SafetyAuthorityId { get; set; }
        public void Validate()
        {
            new RecoveryGuardSafetyPrepareRequest { SchemaVersion = SchemaVersion, RequestId = RequestId,
                TransactionId = TransactionId, Epoch = Epoch, Owner = Owner }.Validate();
            if (!RecoveryGuardSupervisorProtocol.IsId(SafetyAuthorityId))
                throw new InvalidDataException("RecoveryGuardSafetyExecuteRequestInvalid");
        }
    }

    public enum RecoveryGuardSafetyExecutionState { Pending, Completed, Blocked }

    public sealed class RecoveryGuardLaunchPreparationRequest
    {
        public int SchemaVersion { get; set; } = 1;
        public string RequestId { get; set; }
        public string TransactionId { get; set; }
        public long Epoch { get; set; }
        public RecoveryProcessIdentity Owner { get; set; }
        public string SafetyAuthorityId { get; set; }
        public void Validate() => new RecoveryGuardSafetyExecuteRequest
        { SchemaVersion = SchemaVersion, RequestId = RequestId, TransactionId = TransactionId, Epoch = Epoch,
            Owner = Owner, SafetyAuthorityId = SafetyAuthorityId }.Validate();
    }

    public sealed class RecoveryGuardLaunchPreparationResponse
    {
        public int SchemaVersion { get; set; } = 1;
        public string RequestId { get; set; }
        public string SafetyAuthorityId { get; set; }
        public string OperationId { get; set; }
        public bool Accepted { get; set; }
        public string Detail { get; set; }
    }

    public sealed class RecoveryGuardSafetyExecuteResponse
    {
        public int SchemaVersion { get; set; } = 1;
        public string RequestId { get; set; }
        public bool Accepted { get; set; }
        public string SafetyAuthorityId { get; set; }
        public RecoveryGuardSafetyExecutionState State { get; set; }
        public RecoveryProcessIdentity Worker { get; set; }
        public string Detail { get; set; }
    }

    public sealed class RecoveryGuardLaunchResponse
    {
        public int SchemaVersion { get; set; } = 1;
        public string RequestId { get; set; }
        public string OperationId { get; set; }
        public bool Accepted { get; set; }
        public RecoveryProcessIdentity Process { get; set; }
        public string Detail { get; set; }
    }
}
