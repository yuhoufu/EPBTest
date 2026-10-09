using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace MTTFTest.Watchdog.Protocol
{
    // The open file handle is the lease, not the persisted diagnostic contents.
    // Unlike a Mutex this lease is not tied to a managed thread, so a service
    // can start, poll and stop on different threads without abandoning it.
    public sealed class IndependentExecutorLease : IDisposable
    {
        private readonly FileStream _owner;
        private bool _disposed;
        public string ExecutorIdentity { get; }
        public string InstanceId { get; }
        public string StateDirectory { get; }

        public static IndependentExecutorLease TryAcquire(string stateDirectory, string installationId)
        {
            try { return new IndependentExecutorLease(stateDirectory, installationId); }
            catch (IOException error) when ((error.HResult & 0xffff) == 32 || (error.HResult & 0xffff) == 33)
            { return null; }
        }

        public IndependentExecutorLease(string stateDirectory, string installationId)
        {
            if (!Guid.TryParseExact(installationId, "N", out _)) throw new ArgumentException(nameof(installationId));
            if (string.IsNullOrWhiteSpace(stateDirectory) || !Path.IsPathRooted(stateDirectory) ||
                stateDirectory.StartsWith(@"\\", StringComparison.Ordinal)) throw new ArgumentException(nameof(stateDirectory));
            StateDirectory = Path.GetFullPath(stateDirectory);
            if (!Directory.Exists(StateDirectory)) throw new DirectoryNotFoundException(StateDirectory);
            ExecutorIdentity = "IndependentExecutor:" + installationId;
            InstanceId = Guid.NewGuid().ToString("N");
            // Fixed filename prevents a changed installation ID from acquiring
            // another lease for the same project state directory.
            _owner = new FileStream(Path.Combine(StateDirectory, "independent-executor.lease"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough);
            try
            {
                using (var process = Process.GetCurrentProcess())
                {
                    var bytes = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(new
                    {
                        SchemaVersion = 1, ExecutorIdentity, InstanceId, Pid = process.Id,
                        ProcessStartUtcTicks = process.StartTime.ToUniversalTime().Ticks,
                        AcquiredUtcTicks = DateTime.UtcNow.Ticks
                    }));
                    if (bytes.Length > 2048) throw new InvalidDataException("IndependentExecutorLeaseEvidenceOversize");
                    _owner.SetLength(0);
                    _owner.Write(bytes, 0, bytes.Length);
                    _owner.Flush(true);
                }
            }
            catch { _owner.Dispose(); throw; }
        }

        public void RequireHeld()
        {
            if (_disposed || _owner.SafeFileHandle.IsClosed || _owner.SafeFileHandle.IsInvalid)
                throw new InvalidOperationException("IndependentExecutorLeaseNotHeld");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner.Dispose();
            // Keep diagnostic contents. A surviving file is not a live owner.
        }
    }
}
