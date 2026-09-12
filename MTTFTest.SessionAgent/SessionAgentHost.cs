using System;
using System.Diagnostics;
using MTTFTest.RecoveryControl;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.SessionAgent
{
    internal static class SessionAgentHost
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        internal static int Run()
        {
            using (var process = Process.GetCurrentProcess())
            using (var singleton = new Mutex(
                       false,
                       "Local\\MTTFTest.SessionAgent." + process.SessionId))
            {
                bool owns;
                try { owns = singleton.WaitOne(0, false); }
                catch (AbandonedMutexException) { owns = true; }
                if (!owns) return 0;
                try
                {
                    long busySince = 0;
                    using (var health = new RecoveryHealthEndpoint(
                        RecoveryHealthEndpoint.AgentPipe(process.SessionId),
                        () => Interlocked.Read(ref busySince) == 0 ? DateTime.UtcNow.Ticks : Interlocked.Read(ref busySince),
                        () => Interlocked.Read(ref busySince) == 0 ? "Listening" : "LaunchExchange"))
                    {
                    WriteAudit(
                        "SessionAgentStarted",
                        $"Schema={SessionAgentProtocol.SchemaVersion};" +
                        $"DesktopSession={process.SessionId};PID={process.Id}");
                    while (true)
                    {
                        using (var pipe = CreatePipe(process.SessionId))
                        {
                            pipe.WaitForConnection();
                            Interlocked.Exchange(ref busySince, DateTime.UtcNow.Ticks);
                            try
                            {
                                using (var deadline = new PipeExchangeDeadline(pipe, 10000))
                                    Handle(pipe, process.SessionId);
                            }
                            finally { Interlocked.Exchange(ref busySince, 0); }
                        }
                    }
                    }
                }
                finally
                {
                    try { singleton.ReleaseMutex(); } catch { }
                }
            }
        }

        private static NamedPipeServerStream CreatePipe(int desktopSessionId)
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(
                WindowsIdentity.GetCurrent().User,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
            return new NamedPipeServerStream(
                SessionAgentProtocol.PipeName(desktopSessionId),
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.None,
                64 * 1024,
                64 * 1024,
                security);
        }

        private static void Handle(NamedPipeServerStream pipe, int desktopSessionId)
        {
            using (var reader = new BinaryReader(
                       pipe,
                       new UTF8Encoding(false),
                       true))
            using (var writer = new BinaryWriter(
                       pipe,
                       new UTF8Encoding(false),
                       true))
            {
                SessionLaunchCapability capability = null;
                SessionLaunchResponse response;
                try
                {
                    capability = SessionLaunchCapability.ReadFrom(
                        reader,
                        out var seal);
                    response = ConsumeAndLaunch(
                        capability,
                        seal,
                        desktopSessionId);
                }
                catch (Exception ex)
                {
                    response = new SessionLaunchResponse
                    {
                        CapabilityId = capability?.CapabilityId ?? string.Empty,
                        LaunchNonce = capability?.LaunchNonce ?? string.Empty,
                        Accepted = false,
                        FailureCode = "SessionAgentLaunchRejected",
                        Detail = ex.GetBaseException().Message
                    };
                }
                response.WriteTo(writer);
            }
        }

        private static SessionLaunchResponse ConsumeAndLaunch(
            SessionLaunchCapability capability,
            byte[] seal,
            int desktopSessionId)
        {
            if (capability?.IsStructurallyValid() != true ||
                !SessionAgentProtocol.VerifySeal(capability, seal))
                throw new InvalidDataException("LaunchCapabilitySealInvalid");
            if (capability.DesktopSessionId != desktopSessionId)
                throw new InvalidDataException("LaunchCapabilityDesktopMismatch");
            var now = DateTime.UtcNow.Ticks;
            if (now < capability.IssuedUtcTicks - TimeSpan.FromSeconds(5).Ticks ||
                now > capability.ExpiresUtcTicks)
                throw new InvalidDataException("LaunchCapabilityExpired");
            using (var issuer = Process.GetProcessById(capability.IssuerProcessId))
            {
                if (issuer.HasExited || issuer.StartTime.ToUniversalTime().Ticks !=
                    capability.IssuerProcessStartUtcTicks)
                    throw new InvalidDataException("LaunchCapabilityIssuerMismatch");
            }
            var executable = Path.GetFullPath(capability.ExecutablePath);
            if (!string.Equals(executable, capability.ExecutablePath,
                    StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(executable) ||
                !string.Equals(
                    SupervisorProtocol.ComputeSha256(executable),
                    capability.ExecutableSha256,
                    StringComparison.Ordinal))
                throw new InvalidDataException("LaunchCapabilityExecutableMismatch");

            var path = SessionAgentProtocol.ConsumptionPath(
                capability.CapabilityId);
            var recoveryControl = new RecoveryControlStore();
            var recoveryFence = RecoveryLaunchFence.Parse(capability.RecoveryFenceJson, capability.IsRecoveryLaunch);
            if (recoveryFence != null)
                recoveryControl.AssertLaunchFence(recoveryFence, DateTime.UtcNow);
            else if (recoveryControl.IsRegisteredOrPending)
                throw new InvalidOperationException("RecoveryLaunchFenceMissing");
            var stateDirectory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(stateDirectory);
            if (File.Exists(path))
                throw new InvalidOperationException("LaunchCapabilityAlreadyConsumed");
            var record = new SessionLaunchConsumptionRecord
            {
                SchemaVersion = SessionAgentProtocol.SchemaVersion,
                CapabilityId = capability.CapabilityId,
                SessionId = capability.SessionId,
                PermitGeneration = capability.PermitGeneration,
                PermitId = capability.PermitId,
                LaunchNonce = capability.LaunchNonce,
                ExecutablePath = executable,
                ExecutableSha256 = capability.ExecutableSha256,
                ArgumentsSha256 = capability.ArgumentsSha256,
                CapabilitySealBase64 = Convert.ToBase64String(seal),
                State = "LaunchIntent",
                ProcessBootId = RecoveryProcessProbe.ReadBootId(),
                ConsumedUtcTicks = now
            };
            WriteNew(path + ".capability", Json.Serialize(capability));
            WriteNew(path, Json.Serialize(record));
            if (recoveryFence != null)
            {
                if (capability.IsRecoveryLaunch)
                    recoveryControl.ConsumeLaunch(recoveryFence.Authorization, capability.CapabilityId, DateTime.UtcNow);
                else recoveryControl.AssertLaunchFence(recoveryFence, DateTime.UtcNow);
            }
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = capability.Arguments ?? string.Empty,
                WorkingDirectory = Path.GetFullPath(capability.WorkingDirectory),
                UseShellExecute = false,
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Normal
            });
            if (process == null)
                throw new InvalidOperationException("SessionAgentProcessStartReturnedNull");
            var startTicks = process.StartTime.ToUniversalTime().Ticks;
            record.State = "Started";
            record.ProcessId = process.Id;
            record.ProcessStartUtcTicks = startTicks;
            try
            {
                if (recoveryFence?.IsRecovery == true)
                    recoveryControl.RecordLaunchResult(capability.CapabilityId, new RecoveryProcessIdentity
                    {
                        ProcessId = process.Id, StartUtcTicks = startTicks, ExecutablePath = executable,
                        BootId = record.ProcessBootId
                    }, false);
            }
            finally
            {
                // Preserve exact creation evidence even when the shared store
                // fails. The child still requires both records before admission.
                try { Replace(path, Json.Serialize(record)); }
                finally { process.Dispose(); }
            }
            WriteAudit(
                "LaunchCapabilityConsumed",
                $"Capability={capability.CapabilityId};Session={capability.SessionId};" +
                $"PermitGeneration={capability.PermitGeneration};PID={record.ProcessId}");
            var response = new SessionLaunchResponse
            {
                CapabilityId = capability.CapabilityId,
                LaunchNonce = capability.LaunchNonce,
                Accepted = true,
                ProcessId = record.ProcessId,
                ProcessStartUtcTicks = startTicks,
                Detail = "VisibleDesktopProcessStarted"
            };
            return response;
        }

        private static void WriteNew(string path, string payload)
        {
            using (var stream = new FileStream(
                       path,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.Read))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(payload ?? string.Empty);
                writer.Flush();
                stream.Flush(true);
            }
        }

        private static void Replace(string path, string payload)
        {
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, payload ?? string.Empty, new UTF8Encoding(false));
                using (var stream = new FileStream(
                           temporary,
                           FileMode.Open,
                           FileAccess.ReadWrite,
                           FileShare.Read))
                    stream.Flush(true);
                File.Replace(temporary, path, null);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch { }
            }
        }

        internal static void WriteAudit(string eventType, string detail)
        {
            try
            {
                var directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MTTFTest",
                    "SessionAgent");
                Directory.CreateDirectory(directory);
                File.AppendAllText(
                    Path.Combine(directory, "session-agent.log"),
                    DateTime.UtcNow.ToString("O") + " " + eventType + " " +
                    (detail ?? string.Empty) + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
        }

    }
}
