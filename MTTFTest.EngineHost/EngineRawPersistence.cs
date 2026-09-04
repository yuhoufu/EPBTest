using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Config;
using DataOperation;

namespace MTTFTest.EngineHost
{
    /// <summary>Owns optional continuous Raw logging; no decision to resume/rebuild hardware.</summary>
    internal sealed class EngineRawPersistence
    {
        private readonly Dictionary<string, DaqAIContext> _contexts = new Dictionary<string, DaqAIContext>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _widths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _flushGate = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly Action<Exception> _fault;
        private readonly Action<string> _warning;
        private readonly Action<string, int, int> _queueFull;
        private readonly Task _worker;
        private int _closed;
        private int _faultReported;
        internal string RootDirectory { get; }

        internal EngineRawPersistence(string projectDataRoot, AiConfigDetail ai, DaqRuntimeSettings settings,
            double rotationMinutes, IEnumerable<string> configFiles, Action<string, int, int> queueFull,
            Action<Exception> fault, Action<string> warning, bool startWorker = true)
        {
            if (rotationMinutes < 1 || rotationMinutes > 10080 || double.IsNaN(rotationMinutes))
                throw new ArgumentOutOfRangeException(nameof(rotationMinutes));
            if (ai == null || settings == null) throw new ArgumentNullException(nameof(ai));
            _fault = fault ?? throw new ArgumentNullException(nameof(fault));
            _warning = warning; _queueFull = queueFull;
            RootDirectory = Path.Combine(Path.GetFullPath(projectDataRoot), "Raw",
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
            var groups = ai.Enabled().GroupBy(row => (row.物理通道 ?? string.Empty).Split('/')[0], StringComparer.OrdinalIgnoreCase).ToArray();
            if (groups.Length == 0 || groups.Any(group => !group.Key.Equals("Dev1", StringComparison.OrdinalIgnoreCase) &&
                !group.Key.Equals("Dev2", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("RawDeviceMappingInvalid");
            Directory.CreateDirectory(Path.Combine(RootDirectory, "Config"));
            foreach (var file in configFiles ?? Array.Empty<string>())
            {
                using var source = File.OpenRead(file);
                using var destination = new FileStream(Path.Combine(RootDirectory, "Config", Path.GetFileName(file)), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                source.CopyTo(destination); destination.Flush(true);
            }
            foreach (var group in groups)
            {
                var device = group.Key.Equals("Dev1", StringComparison.OrdinalIgnoreCase) ? "Dev1" : "Dev2";
                var rows = group.ToArray();
                var context = new DaqAIContext(device, 256, rotationMinutes, 1000.0 / settings.SampleRateHz,
                    rows.Length, settings.SamplesPerChannel, RootDirectory);
                for (var index = 0; index < rows.Length; index++)
                {
                    var row = rows[index];
                    if (string.IsNullOrWhiteSpace(row.参数名) || context.eMBToDaqCurrentChannel.ContainsKey(row.参数名))
                        throw new InvalidDataException("RawParameterMappingInvalid");
                    context.eMBToDaqCurrentChannel.Add(row.参数名, index);
                    context.paraNameToScale[row.参数名] = row.变换斜率;
                    context.paraNameToOffset[row.参数名] = row.变换截距;
                    context.paraNameToZeroValue[row.参数名] = row.零位漂移;
                }
                if (_queueFull != null) context.QueueFull += _queueFull;
                _contexts.Add(device, context); _widths.Add(device, rows.Length);
            }
            _worker = startWorker ? Task.Run(FlushLoopAsync) : Task.CompletedTask;
        }

        internal void Enqueue(OwnedDaqRawBatch batch)
        {
            if (batch == null) throw new ArgumentNullException(nameof(batch));
            if (Volatile.Read(ref _closed) != 0) throw new ObjectDisposedException(nameof(EngineRawPersistence));
            if (!_contexts.TryGetValue(batch.Device, out var context) || batch.ChannelCount != _widths[batch.Device])
                throw new InvalidDataException("RawBatchMappingMismatch:" + batch.Device);
            // On failure ownership stays with the acquirer's retained FIFO.
            context.EnqueueRawData(batch);
        }

        internal async Task FlushBoundaryAsync(IReadOnlyDictionary<string, long> boundaries,
            Func<long, long, int, CancellationToken, Task<bool>> drain, CancellationToken token)
        {
            if (boundaries == null || !boundaries.TryGetValue("Dev1", out var dev1) ||
                !boundaries.TryGetValue("Dev2", out var dev2) || dev1 < 0 || dev2 < 0 || drain == null)
                throw new InvalidOperationException("RawBoundaryMissing");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(10000); var watch = Stopwatch.StartNew();
            int Remaining() => Math.Max(1, 10000 - (int)watch.ElapsedMilliseconds);
            await _flushGate.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                // Free downstream capacity before waiting for upstream delivery.
                await FlushContextsAsync(Remaining, deadline.Token, false).ConfigureAwait(false);
                var publisher = new EnginePersistenceBoundary(drain, Remaining());
                await publisher.FlushAsync(boundaries, deadline.Token).ConfigureAwait(false);
                await FlushContextsAsync(Remaining, deadline.Token, true).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
            }
            finally { _flushGate.Release(); }
        }

        private async Task FlushContextsAsync(Func<int> remaining, CancellationToken token, bool statistics)
        {
            foreach (var context in _contexts.Values)
            {
                await context.FlushRawToDiskAsync(remaining(), token).ConfigureAwait(false);
                if (statistics) await context.FlushStatToDiskAsync(remaining(), token).ConfigureAwait(false);
            }
        }

        private async Task FlushLoopAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await Task.Delay(500, _stop.Token).ConfigureAwait(false);
                    await _flushGate.WaitAsync(_stop.Token).ConfigureAwait(false);
                    try { await FlushContextsAsync(() => 10000, _stop.Token, false).ConfigureAwait(false); }
                    finally { _flushGate.Release(); }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(ref _faultReported, 1) == 0) try { _fault(ex); } catch { }
            }
        }

        // Call only after the acquisition owner has stopped/disposed its Raw producer.
        internal async Task<bool> StopAfterAcquisitionAsync(int timeoutMs)
        {
            Interlocked.Exchange(ref _closed, 1); _stop.Cancel();
            var watch = Stopwatch.StartNew();
            int Remaining() => Math.Max(1, timeoutMs - (int)watch.ElapsedMilliseconds);
            if (await Task.WhenAny(_worker, Task.Delay(Remaining())).ConfigureAwait(false) != _worker) return false;
            if (!await _flushGate.WaitAsync(Remaining()).ConfigureAwait(false)) return false;
            try
            {
                foreach (var context in _contexts.Values)
                {
                    if (!await context.WaitForIoQuiescenceAsync(Remaining()).ConfigureAwait(false)) return false;
                    if (_queueFull != null) context.QueueFull -= _queueFull;
                    var abandoned = context.ReleasePendingRawAfterShutdown();
                    if (abandoned > 0) _warning?.Invoke("AbortedHostRawBuffersReleased;NotDurableProof;Device=" + context.DaqCardName + ";Batches=" + abandoned);
                }
                return true;
            }
            finally { _flushGate.Release(); }
        }
    }
}
