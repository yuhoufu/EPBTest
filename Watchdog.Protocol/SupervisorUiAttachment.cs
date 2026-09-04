using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace MTTFTest.Watchdog.Protocol
{
    public static class PipePeerIdentity
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

        public static int ServerProcessId(NamedPipeClientStream pipe)
        {
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var value) || value == 0 || value > int.MaxValue)
                throw new IOException("PipeServerIdentityUnavailable");
            return (int)value;
        }

        public static int ClientProcessId(NamedPipeServerStream pipe)
        {
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var value) || value == 0 || value > int.MaxValue)
                throw new IOException("PipeClientIdentityUnavailable");
            return (int)value;
        }
    }

    public sealed class SupervisorUiAttachmentRequest
    {
        public const string Magic = "MTTF-SUPERVISOR-UI-ATTACH-V7-UI2";
        public const string StateRequestMagic = "MTTF-SUPERVISOR-UI-STATE-V7-UI1";
        public bool QueryKernelOnly { get; set; }
        public string RequestId { get; set; } = string.Empty;
        public string ChallengeNonce { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public int RequesterProcessId { get; set; }
        public long RequesterProcessStartUtcTicks { get; set; }
        public bool IsStructurallyValid() => RecoveryProtocolV7.IsGuid(RequestId) &&
            RecoveryProtocolV7.IsGuid(ChallengeNonce) && RecoveryProtocolV7.IsGuid(SessionId) &&
            RequesterProcessId > 0 && RequesterProcessStartUtcTicks > 0;

        public void WriteTo(BinaryWriter writer)
        {
            writer.Write(QueryKernelOnly ? StateRequestMagic : Magic); writer.Write(RequestId); writer.Write(ChallengeNonce);
            writer.Write(SessionId); writer.Write(RequesterProcessId); writer.Write(RequesterProcessStartUtcTicks);
            writer.Flush();
        }

        public static SupervisorUiAttachmentRequest ReadBodyFrom(BinaryReader reader) => new SupervisorUiAttachmentRequest
        {
            RequestId = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
            ChallengeNonce = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
            SessionId = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
            RequesterProcessId = reader.ReadInt32(), RequesterProcessStartUtcTicks = reader.ReadInt64()
        };
    }

    public sealed class SupervisorUiAttachmentResponse
    {
        public const string Magic = "MTTF-SUPERVISOR-UI-ATTACHED-V7-UI2";
        public string RequestId { get; set; } = string.Empty;
        public string ChallengeNonce { get; set; } = string.Empty;
        public bool Accepted { get; set; }
        public string Detail { get; set; } = string.Empty;
        public EngineStateSnapshot Engine { get; set; }
        public EngineUiKernelState ApprovedDesiredState { get; set; }
        public bool InitialObservationOnly { get; set; }
        public int EngineProcessId { get; set; }
        public long EngineProcessStartUtcTicks { get; set; }

        public bool ApprovesRun(string sessionId, string previousRunId, long highestEpoch, long now) =>
            Accepted && Engine?.IsStructurallyValid() == true && EngineProcessId > 0 && EngineProcessStartUtcTicks > 0 &&
            Engine.SessionId == sessionId && Engine.RunEpoch >= highestEpoch &&
            (Engine.RunId == previousRunId || Engine.RunEpoch > highestEpoch) &&
            Engine.CapturedUtcTicks <= now && now - Engine.CapturedUtcTicks < TimeSpan.FromSeconds(3).Ticks &&
            (InitialObservationOnly
                ? ApprovedDesiredState?.Available != true && Engine.RunId == previousRunId && Engine.RunEpoch == highestEpoch &&
                    Engine.State == SystemTerminalState.SafeIdleAlarmed && !Engine.OutputsEnergized &&
                    string.IsNullOrEmpty(Engine.RecoveryOwnerId) && string.IsNullOrEmpty(Engine.RecoveryIncidentId)
                : ApprovedDesiredState?.IsStructurallyValid() == true && ApprovedDesiredState.IsFresh(now) &&
                    ApprovedDesiredState.SessionId == Engine.SessionId && ApprovedDesiredState.RunId == Engine.RunId &&
                    ApprovedDesiredState.RunEpoch == Engine.RunEpoch);

        public void WriteTo(BinaryWriter writer)
        {
            writer.Write(Magic); writer.Write(RequestId); writer.Write(ChallengeNonce);
            writer.Write(Accepted); writer.Write(Detail); writer.Write(EngineProcessId);
            writer.Write(EngineProcessStartUtcTicks);
            writer.Write(new JavaScriptSerializer { MaxJsonLength = 65536 }.Serialize(Engine));
            writer.Write(new JavaScriptSerializer { MaxJsonLength = 65536 }.Serialize(ApprovedDesiredState));
            writer.Write(InitialObservationOnly);
            writer.Flush();
        }

        public static SupervisorUiAttachmentResponse ReadFrom(BinaryReader reader)
        {
            if (SupervisorSessionLaunchRequest.ReadBoundedString(reader) != Magic)
                throw new InvalidDataException("UiAttachmentResponseMagicInvalid");
            var response = new SupervisorUiAttachmentResponse
            {
                RequestId = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                ChallengeNonce = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                Accepted = reader.ReadBoolean(), Detail = SupervisorSessionLaunchRequest.ReadBoundedString(reader),
                EngineProcessId = reader.ReadInt32(), EngineProcessStartUtcTicks = reader.ReadInt64()
            };
            response.Engine = new JavaScriptSerializer { MaxJsonLength = 65536 }.Deserialize<EngineStateSnapshot>(
                SupervisorSessionLaunchRequest.ReadBoundedString(reader));
            response.ApprovedDesiredState = new JavaScriptSerializer { MaxJsonLength = 65536 }.Deserialize<EngineUiKernelState>(
                SupervisorSessionLaunchRequest.ReadBoundedString(reader));
            response.InitialObservationOnly = reader.ReadBoolean();
            return response;
        }
    }
}
