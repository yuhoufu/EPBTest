using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml;
using MTTFTest.RecoveryControl;

namespace MTTFTest.RecoveryGuard
{
    internal sealed class RecoveryWorkerDispatchResult
    {
        public string Code { get; set; }
        public bool TaskRequested { get; set; }
        public bool NeedsAttention { get; set; }
    }

    // Starts only the separately installed executor task. This process does not
    // own hardware, issue launch capabilities or claim a recovery transaction.
    internal static class RecoveryWorkerDispatch
    {
        internal const string TaskName = "MTTFTestRecoveryGuardExecution";
        private const int MaximumText = 64 * 1024;

        internal static RecoveryWorkerDispatchResult Coordinate(RecoveryControlState state, RecoveryDecision decision,
            RecoveryGuardSettings settings, bool maintenance, DateTime now,
            Func<RecoveryProcessIdentity, ProcessObservation> probe, Action requestTask,
            Func<RecoveryTakeoverTransaction, bool> retireWorker = null,
            Func<RecoveryExecutionWorker, bool> retireIdleWorker = null)
        {
            if (settings.Mode == RecoveryGuardMode.ObserveOnly || maintenance || state.Intent == null ||
                state.Intent.DesiredState != RecoveryDesiredState.Run || state.Observation?.Expired == true)
                return Result("ExecutionNotRequested");
            var tx = state.Transaction;
            var unfinished = tx != null && !tx.OwnershipReleased;
            if (!decision.CanClaim && !decision.CanContinue && !unfinished)
                return Result("ExecutionNotNeeded");
            if (!unfinished && state.ExecutionWorker != null)
            {
                var worker = state.ExecutionWorker;
                var observed = probe(worker.Owner);
                if (observed == ProcessObservation.Unknown) return Result("IdleExecutionIdentityUnproven", true);
                if (observed == ProcessObservation.ExactAlive)
                {
                    if (worker.LeaseUntilUtcTicks > now.Ticks) return Result("IdleExecutionWorkerRunning");
                    if (retireIdleWorker == null) return Result("IdleExecutionWorkerLeaseExpired", true);
                    var retired = retireIdleWorker(worker);
                    return Result(retired ? "IdleExecutionWorkerRetired;AwaitingNextDispatch" : "IdleExecutionRetirementPending", !retired);
                }
            }
            if (unfinished)
            {
                var observed = probe(tx.Owner);
                if (observed == ProcessObservation.Unknown)
                    return Result("ExecutionOwnerIdentityUnproven", true);
                if (observed == ProcessObservation.ExactAlive)
                {
                    if (tx.Active && tx.LeaseUntilUtcTicks <= now.Ticks && retireWorker != null)
                    {
                        var retired = retireWorker(tx);
                        return Result(retired ? "ExecutionWorkerRetired;AwaitingNextDispatch" : "ExecutionWorkerRetirementPending", !retired);
                    }
                    return Result(tx.LeaseUntilUtcTicks <= now.Ticks ? "ExecutionWorkerLeaseExpired" : "ExecutionWorkerRunning",
                        tx.LeaseUntilUtcTicks <= now.Ticks);
                }
                if (tx.LeaseUntilUtcTicks > now.Ticks)
                    return Result("ExecutionOwnerExitedAwaitingLease");
            }
            requestTask();
            return new RecoveryWorkerDispatchResult { Code = "ExecutionTaskRequested;RecoveryNotYetVerified", TaskRequested = true };
        }

        internal static void RequestInstalledTask(string executable, string settingsPath, string controlRoot)
        {
            ValidateInstalledTask(executable, settingsPath, controlRoot);
            // The executor rechecks authority when the scheduler starts it.
            RunScheduler("/Run /TN " + TaskName);
        }

        internal static void ValidateInstalledTask(string executable, string settingsPath, string controlRoot)
        {
            if (!SamePath(controlRoot, RecoveryControlStore.DefaultRoot))
                throw new InvalidOperationException("RecoveryWorkerDispatchRequiresInstallationRoot");
            var stateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MTTFTestRecoveryGuard");
            var expectedSettings = Path.Combine(stateRoot, "guard-settings.json");
            if (!SamePath(settingsPath, expectedSettings))
                throw new InvalidOperationException("RecoveryWorkerDispatchSettingsMismatch");
            var registration = ReadBoundedFile(Path.Combine(stateRoot, "installation.json"));
            ValidateRegistration(registration, executable);
            var xml = RunScheduler("/Query /TN " + TaskName + " /XML");
            ValidateTask(xml, executable, expectedSettings, Path.Combine(stateRoot, "execution-journal"));
            // The executor independently checks current intent, maintenance,
            // ownership and its process mutex when the scheduler starts it.
        }

        internal static void ValidateRegistration(string json, string executable)
        {
            if (json == null || json.Length > MaximumText) throw new InvalidDataException("RecoveryWorkerRegistrationInvalid");
            var registration = new JavaScriptSerializer().Deserialize<Installation>(json);
            if (registration?.schemaVersion != 2 || !registration.executionEnabled ||
                registration.executionTask != TaskName || !SamePath(registration.directory, Path.GetDirectoryName(executable)))
                throw new InvalidDataException("RecoveryWorkerInstallationNotEnabled");
        }

        internal static void ValidateTask(string xml, string executable, string settingsPath, string journalPath)
        {
            if (xml == null || xml.Length > MaximumText) throw new InvalidDataException("RecoveryWorkerTaskInvalid");
            var document = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumText }))
                document.Load(reader);
            var ns = new XmlNamespaceManager(document.NameTable);
            ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
            Func<string, string> value = path => document.SelectSingleNode("/t:Task/" + path, ns)?.InnerText;
            var user = value("t:Principals/t:Principal/t:UserId");
            var logonType = value("t:Principals/t:Principal/t:LogonType");
            var enabled = value("t:Settings/t:Enabled");
            if ((user != "S-1-5-18" && !string.Equals(user, "SYSTEM", StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(logonType) && logonType != "ServiceAccount") ||
                value("t:Principals/t:Principal/t:RunLevel") != "HighestAvailable" ||
                document.SelectNodes("/t:Task/t:Principals/t:Principal", ns).Count != 1 ||
                document.SelectNodes("/t:Task/t:Actions/*", ns).Count != 1 ||
                (!string.IsNullOrEmpty(enabled) && enabled != "true") ||
                value("t:Settings/t:MultipleInstancesPolicy") != "IgnoreNew" ||
                value("t:Settings/t:ExecutionTimeLimit") != "PT0S" ||
                value("t:Settings/t:AllowHardTerminate") != "false" ||
                !SamePath(value("t:Actions/t:Exec/t:Command"), executable) ||
                !SamePath(value("t:Actions/t:Exec/t:WorkingDirectory"), Path.GetDirectoryName(executable)) ||
                value("t:Actions/t:Exec/t:Arguments") != "--execute --settings " + Quote(settingsPath) + " --journal " + Quote(journalPath))
                throw new InvalidDataException("RecoveryWorkerTaskDefinitionMismatch");
        }

        private static string RunScheduler(string arguments)
        {
            using (var process = new Process { StartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"), Arguments = arguments,
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true
            } })
            {
                if (!process.Start()) throw new IOException("RecoveryWorkerSchedulerStartFailed");
                var output = ReadBoundedAsync(process.StandardOutput);
                var error = ReadBoundedAsync(process.StandardError);
                try
                {
                    if (!process.WaitForExit(5000) || !Task.WhenAll(output, error).Wait(1000))
                        throw new IOException("RecoveryWorkerSchedulerTimedOut;ReconcileTaskBeforeRetry");
                    if (process.ExitCode != 0) throw new IOException("RecoveryWorkerSchedulerRejected:" + process.ExitCode);
                    return output.GetAwaiter().GetResult();
                }
                finally
                {
                    // This is only our short-lived schtasks client, never the
                    // execution task, main program or a safety actor.
                    if (!process.HasExited) { process.Kill(); process.WaitForExit(1000); }
                }
            }
        }

        private static async Task<string> ReadBoundedAsync(StreamReader reader)
        {
            var result = new StringBuilder();
            var buffer = new char[2048];
            int count;
            while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) != 0)
            {
                if (result.Length + count > MaximumText) throw new InvalidDataException("RecoveryWorkerSchedulerOutputTooLarge");
                result.Append(buffer, 0, count);
            }
            return result.ToString();
        }

        private static string ReadBoundedFile(string path)
        {
            if (new FileInfo(path).Length > MaximumText) throw new InvalidDataException("RecoveryWorkerRegistrationOversized");
            return File.ReadAllText(path);
        }

        private static bool SamePath(string left, string right) => !string.IsNullOrWhiteSpace(left) &&
            !string.IsNullOrWhiteSpace(right) && Path.IsPathRooted(left) && Path.IsPathRooted(right) &&
            string.Equals(Path.GetFullPath(left).TrimEnd('\\'), Path.GetFullPath(right).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        private static string Quote(string value)
        {
            if (value == null || value.IndexOfAny(new[] { '"', '\r', '\n' }) >= 0 || value.EndsWith("\\", StringComparison.Ordinal))
                throw new InvalidDataException("RecoveryWorkerTaskPathInvalid");
            return "\"" + value + "\"";
        }
        private static RecoveryWorkerDispatchResult Result(string code, bool attention = false) =>
            new RecoveryWorkerDispatchResult { Code = code, NeedsAttention = attention };
        private sealed class Installation
        {
            public int schemaVersion { get; set; }
            public string directory { get; set; }
            public string executionTask { get; set; }
            public bool executionEnabled { get; set; }
        }
    }
}
