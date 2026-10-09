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
            try
            {
                var state = InstallationAlarmClient.Send(InstallationAlarmAction.Query).State;
                InstallationAlarmClient.Send(InstallationAlarmAction.MuteBuzzer, expected: state);
                return true;
            }
            catch { return false; }
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
                var response = DeadlinePipeExchange.Execute(SupervisorProtocol.PipeName, 3000,
                    request.WriteTo, SupervisorP0AlarmResponse.ReadFrom,
                    default, System.Security.Principal.TokenImpersonationLevel.Identification);
                return response?.SchemaVersion ==
                                   SupervisorProtocol.SchemaVersion &&
                               response.Accepted &&
                               string.Equals(response.RequestId, request.RequestId,
                                   StringComparison.Ordinal) &&
                               string.Equals(response.ChallengeNonce,
                                   request.ChallengeNonce,
                                   StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }
    }
}
