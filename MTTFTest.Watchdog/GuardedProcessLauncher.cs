using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Watchdog
{
    internal sealed class GuardedProcessOwnerReceipt : IDisposable
    {
        private readonly GuardedProcessLauncher _owner;
        internal GuardedProcessOwnerReceipt(GuardedProcessLauncher owner, Process process, long startUtcTicks)
        {
            _owner = owner; Process = process; ProcessId = process.Id; StartUtcTicks = startUtcTicks;
        }
        internal Process Process { get; }
        internal int ProcessId { get; }
        internal long StartUtcTicks { get; }
        internal void KillExactAndDispose() => _owner.KillExact(Process);
        public void Dispose() { try { Process?.Dispose(); } catch { } }
    }

    /// <summary>
    /// The only production process-start boundary in the watchdog host.
    /// Callers cannot supply ProcessStartInfo; they must present the
    /// capability returned by the durable V4 authority.
    /// </summary>
    internal sealed class GuardedProcessLauncher
    {
        private readonly object _gate = new object();
        private readonly Func<DurableLaunchIntentCapability, bool> _authorityValidator;
        private readonly Func<DurableLaunchIntentCapability, bool> _authorityConsumer;
        private readonly System.Collections.Generic.HashSet<string> _consumed =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        internal GuardedProcessLauncher(
            Func<DurableLaunchIntentCapability, bool> authorityValidator = null,
            Func<DurableLaunchIntentCapability, bool> authorityConsumer = null)
        {
            if (authorityValidator == null || authorityConsumer == null)
                throw new ArgumentException("Strict V4 authority callbacks are required.");
            _authorityValidator = authorityValidator;
            _authorityConsumer = authorityConsumer;
        }

        internal GuardedProcessOwnerReceipt Start(DurableLaunchIntentCapability capability)
        {
            ValidateCapability(capability);
            if (_authorityValidator != null && !_authorityValidator(capability))
                throw new InvalidOperationException("LaunchCapabilityNotCurrent");
            if (_authorityConsumer != null && !_authorityConsumer(capability))
                throw new InvalidOperationException("LaunchIntentConsumeRejected");
            lock (_gate)
            {
                if (!_consumed.Add(capability.IntentId))
                    throw new InvalidOperationException("LaunchIntentAlreadyConsumed");
            }
            // Re-read the executable after the durable consume CAS and as
            // close as possible to Process.Start.  A pre-consume hash alone
            // leaves a substitution window in which a valid capability could
            // launch different bytes.
            ValidateExecutableHash(capability);
            Process process = null;
            try
            {
                var formalMarker = Path.Combine(
                    Path.GetDirectoryName(capability.ExecutablePath) ?? string.Empty,
                    "MTTFTest.UnattendedMode.required");
                process = File.Exists(formalMarker)
                    ? SessionAgentLaunchClient.Start(capability)
                    : Process.Start(new ProcessStartInfo
                    {
                        FileName = capability.ExecutablePath,
                        Arguments = capability.Arguments ?? string.Empty,
                        WorkingDirectory = capability.WorkingDirectory,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                if (process == null) throw new InvalidOperationException("GuardedProcessStartReturnedNull");
                var startTicks = process.StartTime.ToUniversalTime().Ticks;
                return new GuardedProcessOwnerReceipt(this, process, startTicks);
            }
            catch
            {
                // The durable one-shot reservation is intentionally retained:
                // after an ambiguous start failure recovery must reconcile,
                // never silently start the same intent a second time.
                // If the OS returned an object but identity capture or
                // owner construction failed, release that exact local handle
                // before propagating the failure.  We deliberately do not
                // issue an unverified Kill on an identity we could not read.
                try { process?.Dispose(); } catch { }
                throw;
            }
        }

        internal void KillExact(Process process)
        {
            if (process == null) return;
            try
            {
                var expectedId = process.Id;
                var expectedStart = process.StartTime.ToUniversalTime().Ticks;
                using (var exact = Process.GetProcessById(expectedId))
                {
                    if (exact.HasExited || exact.StartTime.ToUniversalTime().Ticks != expectedStart) return;
                    try { exact.Kill(); } catch { }
                    try { exact.WaitForExit(5000); } catch { }
                }
            }
            finally { try { process.Dispose(); } catch { } }
        }

        private static void ValidateCapability(DurableLaunchIntentCapability capability)
        {
            if (capability == null) throw new ArgumentNullException(nameof(capability));
            var intent = new DurableLaunchIntent
            {
                SessionId = capability.SessionId, SessionNonce = capability.SessionNonce,
                Generation = capability.Generation, PermitId = capability.PermitId,
                PermitNonce = capability.PermitNonce, IntentId = capability.IntentId,
                ExecutablePath = capability.ExecutablePath,
                ExecutableSha256 = capability.ExecutableSha256,
                Arguments = capability.Arguments, WorkingDirectory = capability.WorkingDirectory,
                LaunchOptionsCanonical = capability.LaunchOptionsCanonical,
                LaunchSpecSha256 = capability.LaunchSpecSha256
            };
            string reason;
            if (!DurableLaunchCanonical.TryValidate(intent, out reason))
                throw new InvalidOperationException("LaunchCapabilityInvalid:" + reason);
            var path = Path.GetFullPath(capability.ExecutablePath);
            if (!string.Equals(path, capability.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("LaunchExecutableNotCanonical");
            if (!File.Exists(path)) throw new FileNotFoundException("LaunchExecutableMissing", path);
            var workingDirectory = Path.GetFullPath(capability.WorkingDirectory);
            if (!string.Equals(workingDirectory, capability.WorkingDirectory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("LaunchWorkingDirectoryNotCanonical");
            if (!Directory.Exists(workingDirectory))
                throw new DirectoryNotFoundException("LaunchWorkingDirectoryMissing:" + workingDirectory);
            ValidateExecutableHash(capability);
        }

        private static void ValidateExecutableHash(DurableLaunchIntentCapability capability)
        {
            var path = Path.GetFullPath(capability.ExecutablePath);
            string actual;
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToUpperInvariant();
            if (!string.Equals(actual, capability.ExecutableSha256, StringComparison.Ordinal))
                throw new InvalidOperationException("LaunchExecutableHashMismatch");
        }
    }
}
