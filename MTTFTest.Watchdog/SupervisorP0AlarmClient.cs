using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Watchdog
{
    internal static class SupervisorP0AlarmClient
    {
        internal static bool TryLatch(string eventId, string code, string detail)
        {
            return TrySend(
                SupervisorP0AlarmAction.Latch,
                eventId,
                code,
                detail);
        }

        internal static bool TryClearTransient(string code, string detail)
        {
            return TrySend(
                SupervisorP0AlarmAction.ClearTransient,
                Guid.NewGuid().ToString("N"),
                code,
                detail);
        }

        internal static bool TryMuteBuzzer(string code, string detail)
        {
            return TrySend(
                SupervisorP0AlarmAction.MuteBuzzer,
                Guid.NewGuid().ToString("N"),
                code,
                detail);
        }

        private static bool TrySend(
            SupervisorP0AlarmAction action,
            string eventId,
            string code,
            string detail)
        {
            try
            {
                SupervisorP0AlarmRequest request;
                using (var current = Process.GetCurrentProcess())
                {
                    request = new SupervisorP0AlarmRequest
                    {
                        RequestId = Guid.NewGuid().ToString("N"),
                        ChallengeNonce = Guid.NewGuid().ToString("N"),
                        RequesterProcessId = current.Id,
                        RequesterProcessStartUtcTicks =
                            current.StartTime.ToUniversalTime().Ticks,
                        Action = action,
                        EventId = eventId,
                        Code = code ?? string.Empty,
                        Detail = detail ?? string.Empty
                    };
                }
                using (var pipe = new NamedPipeClientStream(
                           ".",
                           SupervisorProtocol.PipeName,
                           PipeDirection.InOut,
                           PipeOptions.None))
                {
                    pipe.Connect(1500);
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
                        var response = SupervisorP0AlarmResponse.ReadFrom(reader);
                        return response?.SchemaVersion ==
                                   SupervisorProtocol.SchemaVersion &&
                               response.Accepted &&
                               string.Equals(response.RequestId, request.RequestId,
                                   StringComparison.Ordinal) &&
                               string.Equals(response.ChallengeNonce,
                                   request.ChallengeNonce,
                                   StringComparison.Ordinal);
                    }
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
