using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace MTTFTest.Watchdog.Protocol
{
    public enum InstallationAlarmAction
    {
        Query = 1,
        MuteBuzzer = 2,
        SetIndicator = 3,
        SetBuzzer = 4,
        ClearProjectOutputs = 5,
        ReleaseProjectOutputs = 6,
        SuppressOutputs = 7,
        CompleteSafeClose = 8
    }

    public sealed class InstallationAlarmRequest
    {
        public string RequestId { get; set; }
        public int ProcessId { get; set; }
        public long ProcessStartUtcTicks { get; set; }
        public InstallationAlarmAction Action { get; set; }
        public string EventId { get; set; }
        public long ExpectedRevision { get; set; }
        public int Channel { get; set; }
        public bool On { get; set; }

        public void Validate()
        {
            if (!Guid.TryParseExact(RequestId, "N", out _) || ProcessId <= 0 || ProcessStartUtcTicks <= 0 ||
                !Enum.IsDefined(typeof(InstallationAlarmAction), Action) ||
                Action == InstallationAlarmAction.SetIndicator && (Channel < 1 || Channel > 12) ||
                Action == InstallationAlarmAction.MuteBuzzer &&
                    (!Guid.TryParseExact(EventId, "N", out _) || ExpectedRevision <= 0) ||
                (Action == InstallationAlarmAction.SuppressOutputs || Action == InstallationAlarmAction.CompleteSafeClose) &&
                    (ExpectedRevision <= 0 || !string.IsNullOrEmpty(EventId) && !Guid.TryParseExact(EventId, "N", out _)))
                throw new InvalidDataException("InstallationAlarmRequestInvalid");
        }
    }

    public class InstallationAlarmSnapshot
    {
        public int SchemaVersion { get; set; } = SupervisorProtocol.SchemaVersion;
        public bool Latched { get; set; }
        public bool BuzzerMuted { get; set; }
        public bool OutputsSuppressed { get; set; }
        public string EventId { get; set; }
        public string Code { get; set; }
        public string Detail { get; set; }
        public long Revision { get; set; }
        public long FirstObservedUtcTicks { get; set; }
        public long UpdatedUtcTicks { get; set; }
        public string LastMuteOperatorSid { get; set; }
        public string LastMuteRequestId { get; set; }
        public long LastMuteUtcTicks { get; set; }
        public string LastOutputAction { get; set; }
        public string LastOutputOperatorSid { get; set; }
        public long LastOutputActionUtcTicks { get; set; }
        public long CommandSentRevision { get; set; }
        public string OutputStatus { get; set; }
        public string OutputError { get; set; }
        public int RequestedLightCount { get; set; }
        public bool RequestedBuzzerOn { get; set; }
    }

    public sealed class InstallationAlarmResponse
    {
        public string RequestId { get; set; }
        public bool Accepted { get; set; }
        public string Error { get; set; }
        public InstallationAlarmSnapshot State { get; set; }
    }

    public static class InstallationAlarmProtocol
    {
        public const string RequestMagic = "MTTF-INSTALLATION-ALARM-REQUEST-V1";
        public const string ResponseMagic = "MTTF-INSTALLATION-ALARM-RESPONSE-V1";
        private const int MaximumBytes = 64 * 1024;

        public static void Write<T>(BinaryWriter writer, string magic, T value)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = MaximumBytes }.Serialize(value);
            var bytes = Encoding.UTF8.GetBytes(json);
            if (bytes.Length > MaximumBytes) throw new InvalidDataException("InstallationAlarmFrameTooLarge");
            writer.Write(magic);
            writer.Write(bytes.Length);
            writer.Write(bytes);
            writer.Flush();
        }

        public static T ReadBody<T>(BinaryReader reader)
        {
            var count = reader.ReadInt32();
            if (count < 1 || count > MaximumBytes) throw new InvalidDataException("InstallationAlarmFrameTooLarge");
            var bytes = reader.ReadBytes(count);
            if (bytes.Length != count) throw new EndOfStreamException();
            return new JavaScriptSerializer { MaxJsonLength = MaximumBytes }.Deserialize<T>(
                new UTF8Encoding(false, true).GetString(bytes));
        }
    }

    public static class InstallationAlarmClient
    {
        // The caller must have completed its existing physical-safe stop/close barrier.
        // A crash, disposal, or an unconfirmed stop must never call this operation.
        public static async Task<InstallationAlarmSnapshot> CompleteSafeCloseAsync(CancellationToken cancellation = default)
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                deadline.CancelAfter(15000);
                var committed = await Task.Run(() =>
                {
                    var current = Send(InstallationAlarmAction.Query, cancellation: deadline.Token).State;
                    return Send(InstallationAlarmAction.CompleteSafeClose, expected: current, cancellation: deadline.Token).State;
                }, deadline.Token).ConfigureAwait(false);
                return await WaitForCommandSentAsync(committed,
                    () => Send(InstallationAlarmAction.Query, cancellation: deadline.Token).State,
                    deadline.Token).ConfigureAwait(false);
            }
        }

        internal static async Task<InstallationAlarmSnapshot> WaitForCommandSentAsync(InstallationAlarmSnapshot expected,
            Func<InstallationAlarmSnapshot> query, CancellationToken cancellation)
        {
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                var current = await Task.Run(query, cancellation).ConfigureAwait(false);
                if (current == null || current.EventId != expected.EventId || current.Revision != expected.Revision ||
                    !current.OutputsSuppressed)
                    throw new InvalidOperationException("关闭声光期间报警已变化，未确认当前故障输出关闭。");
                if (current.RequestedLightCount != 0 || current.RequestedBuzzerOn)
                    throw new InvalidOperationException("关闭声光期间出现新的项目报警输出，关闭未确认。");
                if (current.CommandSentRevision == expected.Revision && current.OutputStatus == "CommandSent") return current;
                await Task.Delay(200, cancellation).ConfigureAwait(false);
            }
        }

        public static InstallationAlarmResponse Send(InstallationAlarmAction action, int channel = 0,
            bool on = false, InstallationAlarmSnapshot expected = null, CancellationToken cancellation = default)
        {
            InstallationAlarmRequest request;
            using (var process = Process.GetCurrentProcess())
                request = new InstallationAlarmRequest
                {
                    RequestId = Guid.NewGuid().ToString("N"), ProcessId = process.Id,
                    ProcessStartUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                    Action = action, Channel = channel, On = on,
                    EventId = expected?.EventId, ExpectedRevision = expected?.Revision ?? 0
                };
            request.Validate();
            var result = DeadlinePipeExchange.Execute(SupervisorProtocol.PipeName, 3000,
                writer => InstallationAlarmProtocol.Write(writer, InstallationAlarmProtocol.RequestMagic, request),
                reader =>
                {
                    if (SupervisorProtocol.ReadRequestMagic(reader) != InstallationAlarmProtocol.ResponseMagic)
                        throw new InvalidDataException("InstallationAlarmResponseMismatch");
                    return InstallationAlarmProtocol.ReadBody<InstallationAlarmResponse>(reader);
                }, cancellation, System.Security.Principal.TokenImpersonationLevel.Identification);
            if (result == null || result.RequestId != request.RequestId)
                throw new InvalidDataException("InstallationAlarmResponseIdentityMismatch");
            if (!result.Accepted) throw new InvalidOperationException(result.Error ?? "InstallationAlarmRejected");
            return result;
        }
    }
}
