using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace MTTFTest.Watchdog.Protocol
{
    public sealed class IndependentLaunchTaskDefinition
    {
        public string Path { get; set; }
        public string Executable { get; set; }
        public string Arguments { get; set; }
        public string WorkingDirectory { get; set; }
        public string UserSid { get; set; }
        public string SecurityDescriptor { get; set; }
        public bool Enabled { get; set; }
        public bool AllowDemandStart { get; set; }
        public bool AllowHardTerminate { get; set; }
        public bool RequiresIdle { get; set; }
        public bool RequiresNetwork { get; set; }
        public bool DisallowBatteries { get; set; }
        public bool StopOnBatteries { get; set; }
        public int ActionCount { get; set; }
        public int ActionType { get; set; }
        public int TriggerCount { get; set; }
        public int RunLevel { get; set; }
        public int LogonType { get; set; }
        public int MultipleInstances { get; set; }
        public int RestartCount { get; set; }
        public string ExecutionTimeLimit { get; set; }

        public static string ExpectedArguments(string registrationPath) =>
            "--independent-registration \"" + System.IO.Path.GetFullPath(registrationPath) + "\" --independent-ticket $(Arg0)";

        private static bool Trusted(SecurityIdentifier sid) => sid != null &&
            (sid.IsWellKnown(WellKnownSidType.LocalSystemSid) || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));

        public void Validate(IndependentExecutorRegistration registration, string registrationPath)
        {
            registration.Validate();
            if (!Enabled || !AllowDemandStart || AllowHardTerminate || RequiresIdle || RequiresNetwork ||
                DisallowBatteries || StopOnBatteries || ActionCount != 1 || ActionType != 0 || TriggerCount != 0 ||
                RunLevel != 1 || LogonType != 3 || MultipleInstances != 2 || RestartCount != 0 || ExecutionTimeLimit != "PT0S" ||
                !string.Equals(Path, registration.LaunchTaskName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Executable, registration.ExecutablePath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(WorkingDirectory, System.IO.Path.GetDirectoryName(registration.ExecutablePath), StringComparison.OrdinalIgnoreCase) ||
                Arguments != ExpectedArguments(registrationPath) || UserSid != registration.InteractiveUserSid)
                throw new InvalidDataException("IndependentLaunchTaskDefinitionMismatch");
            ValidateSecurityDescriptor(SecurityDescriptor);
        }

        public static void ValidateSecurityDescriptor(string descriptor)
        {
            if (string.IsNullOrWhiteSpace(descriptor) || descriptor.Length > 8192)
                throw new UnauthorizedAccessException("IndependentLaunchTaskSecurityDescriptorInvalid");
            var security = new RawSecurityDescriptor(descriptor);
            if (!Trusted(security.Owner) || security.DiscretionaryAcl == null)
                throw new UnauthorizedAccessException("IndependentLaunchTaskOwnerOrDaclInvalid");
            foreach (GenericAce entry in security.DiscretionaryAcl)
            {
                var ace = entry as CommonAce;
                if (ace == null || ace.IsCallback) throw new UnauthorizedAccessException("IndependentLaunchTaskAceUnsupported");
                // Read/execute only for non-administrative principals. Reject
                // write, delete, ownership and generic-all grants.
                if ((ace.AceFlags & AceFlags.InheritOnly) == 0 &&
                    ace.AceQualifier == AceQualifier.AccessAllowed && !Trusted(ace.SecurityIdentifier) &&
                    (ace.AccessMask & ~0x1200a9) != 0)
                    throw new UnauthorizedAccessException("IndependentLaunchTaskWritableByUntrustedPrincipal");
            }
        }
    }

    // Invoke only in an externally bounded SYSTEM worker: Task Scheduler COM
    // calls may hang. Returning a task instance is dispatch, never recovery proof.
    public static class IndependentInteractiveLauncher
    {
        public static string Dispatch(string registrationPath, string nonce)
        {
            using (var identity = WindowsIdentity.GetCurrent())
                if (!identity.IsSystem) throw new UnauthorizedAccessException("IndependentLaunchRequiresSystem");
            if (!Guid.TryParseExact(nonce, "N", out _)) throw new ArgumentException(nameof(nonce));
            var registration = IndependentExecutorRegistration.LoadTrusted(registrationPath);
            var owned = new List<object>();
            object Keep(object value) { owned.Add(value); return value; }
            try
            {
                dynamic scheduler = Keep(Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)));
                scheduler.Connect();
                dynamic rootFolder = Keep(scheduler.GetFolder(@"\"));
                IndependentLaunchTaskDefinition.ValidateSecurityDescriptor((string)rootFolder.GetSecurityDescriptor(7));
                dynamic folder = Keep(scheduler.GetFolder(@"\MTTFTest"));
                IndependentLaunchTaskDefinition.ValidateSecurityDescriptor((string)folder.GetSecurityDescriptor(7));
                dynamic task = Keep(folder.GetTask("Independent-" + registration.InstallationId));
                dynamic definition = Keep(task.Definition);
                dynamic actions = Keep(definition.Actions);
                dynamic triggers = Keep(definition.Triggers);
                dynamic principal = Keep(definition.Principal);
                dynamic settings = Keep(definition.Settings);
                if ((int)actions.Count != 1) throw new InvalidDataException("IndependentLaunchTaskActionCountInvalid");
                dynamic action = Keep(actions.Item(1));
                var user = (string)principal.UserId;
                var sid = user.StartsWith("S-", StringComparison.OrdinalIgnoreCase)
                    ? new SecurityIdentifier(user) : (SecurityIdentifier)new NTAccount(user).Translate(typeof(SecurityIdentifier));
                new IndependentLaunchTaskDefinition
                {
                    Path = (string)task.Path, Executable = (string)action.Path, Arguments = (string)action.Arguments,
                    WorkingDirectory = (string)action.WorkingDirectory, UserSid = sid.Value,
                    SecurityDescriptor = (string)task.GetSecurityDescriptor(7),
                    Enabled = (bool)task.Enabled && (bool)settings.Enabled, AllowDemandStart = (bool)settings.AllowDemandStart,
                    AllowHardTerminate = (bool)settings.AllowHardTerminate, RequiresIdle = (bool)settings.RunOnlyIfIdle,
                    RequiresNetwork = (bool)settings.RunOnlyIfNetworkAvailable, DisallowBatteries = (bool)settings.DisallowStartIfOnBatteries,
                    StopOnBatteries = (bool)settings.StopIfGoingOnBatteries, ActionCount = (int)actions.Count, ActionType = (int)action.Type,
                    TriggerCount = (int)triggers.Count, RunLevel = (int)principal.RunLevel, LogonType = (int)principal.LogonType,
                    MultipleInstances = (int)settings.MultipleInstances, RestartCount = (int)settings.RestartCount,
                    ExecutionTimeLimit = (string)settings.ExecutionTimeLimit
                }.Validate(registration, registrationPath);
                var store = new IndependentProjectStateStore(registration.StateDirectory);
                var state = store.Read();
                registration.RequireBoundIntent(state.Intent);
                store.MarkLaunchDispatched(state.Revision, nonce, "IndependentExecutor:" + registration.InstallationId, DateTime.UtcNow.Ticks);
                // USE_SESSION_ID | USER_SID. No AS_SELF: SYSTEM must not become
                // the interactive main program's identity in Session 0.
                dynamic running = Keep(task.RunEx(nonce, 4 | 8, state.Ticket.WindowsSessionId, registration.InteractiveUserSid));
                return (string)running.InstanceGuid;
            }
            finally
            {
                for (var index = owned.Count - 1; index >= 0; index--)
                    if (Marshal.IsComObject(owned[index])) Marshal.ReleaseComObject(owned[index]);
            }
        }
    }
}
