using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Threading;
using Config;
using Controller;
using DataOperation;
using PowerSupply.Core;

namespace AdaptiveControlTests
{
    internal static class PowerSupplyTelemetryRecorderTests
    {
        public static int RunAll()
        {
            var passed = 0;
            StrictTelemetryPolicyParsing();
            passed++;
            LayeredTelemetrySealsManifestAndDeduplicatesPreWindow();
            passed++;
            RawTelemetryUsesExplicitRotationBudget();
            passed++;
            EventQueuePressureSpillsWithoutSilentDrop();
            passed++;
            SlowSpoolDoesNotBlockProducer();
            passed++;
            ReplayDeduplicatesSequenceAfterCrashBoundary();
            passed++;
            RestartRestoresSequenceAndSealsOrphanTmpFromSpill();
            passed++;
            RetentionRejectsMaliciousManifestPath();
            passed++;
            DetachShutdownDoesNotWaitForSlowWorker();
            passed++;
            ThreeSupplyFakeClockMaintainsOneHzSteadyRate();
            passed++;
            RetentionLeavesTmpAndUnknownArtifactsUntouched();
            passed++;
            return passed;
        }

        private static void StrictTelemetryPolicyParsing()
        {
            var settings = new NameValueCollection
            {
                ["TelemetryPersistenceMode"] = "Raw",
                ["TelemetryRawRotateBytes"] = "2048",
                ["TelemetryRetentionDays"] = "1"
            };
            var warnings = new System.Collections.Generic.List<string>();
            var policy = ProgramStoragePolicy.Load(warnings.Add, settings).Telemetry;
            Assert(policy.Mode == TelemetryPersistenceMode.Raw, "Raw telemetry mode was not parsed.");
            Assert(policy.RawRotateBytes == 2048, "Raw rotation budget was not parsed.");

            settings["TelemetryPersistenceMode"] = "invalid";
            settings["TelemetryRawRotateBytes"] = "0";
            settings["TelemetryRetentionDays"] = "0";
            policy = ProgramStoragePolicy.Load(warnings.Add, settings).Telemetry;
            Assert(policy.Mode == TelemetryPersistenceMode.Layered, "Invalid mode did not fall back to Layered.");
            Assert(policy.RawRotateBytes == 100L * 1024L * 1024L,
                "Invalid Raw rotation budget did not use the safe default.");
            Assert(policy.RetentionDays == 3,
                "TelemetryRetentionDays=0 did not use the safe 3-day default.");
            Assert(warnings.Any(x => x.IndexOf("TelemetryPersistenceMode", StringComparison.Ordinal) >= 0),
                "Invalid telemetry mode did not emit a startup warning.");
            Assert(warnings.Any(x => x.IndexOf("TelemetryRetentionDays", StringComparison.Ordinal) >= 0),
                "Invalid telemetry retention did not emit a startup warning.");
        }

        private static void LayeredTelemetrySealsManifestAndDeduplicatesPreWindow()
        {
            var root = Path.Combine(Path.GetTempPath(), "epb-telemetry-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var policy = new TelemetryPersistencePolicy
                {
                    Mode = TelemetryPersistenceMode.Layered,
                    SteadyRateHz = 1,
                    EventRateHz = 10,
                    PreEventSeconds = 60,
                    PostEventSeconds = 1,
                    RetentionDays = 3,
                    RotateHours = 1,
                    RotateBytes = 4L * 1024L * 1024L,
                    RawRotateBytes = 4L * 1024L * 1024L
                };
                var path = Path.Combine(root, "telemetry_" + Guid.NewGuid().ToString("N") + ".csv");
                using (var recorder = new PowerSupplyTelemetryCsvRecorder(path, NullLogger.Instance, policy))
                {
                    for (var i = 0; i < 30; i++)
                        recorder.Enqueue(Telemetry(i, PowerSupplyTelemetryEventFlags.None), i, "Running");
                    recorder.Enqueue(
                        Telemetry(31, PowerSupplyTelemetryEventFlags.ProtectionTripped, "ProtectionTrip"),
                        31,
                        "Running");
                    recorder.Enqueue(
                        Telemetry(32, PowerSupplyTelemetryEventFlags.ProtectionTripped, "ProtectionTrip"),
                        32,
                        "Running");
                }

                var manifests = Directory.GetFiles(root, "*.manifest.json");
                Assert(manifests.Length == 1, "Expected one sealed telemetry manifest.");
                var manifest = File.ReadAllText(manifests[0]);
                Assert(manifest.Contains("\"steadyRows\":"), "Manifest is missing steadyRows.");
                Assert(manifest.Contains("\"eventRows\":"), "Manifest is missing eventRows.");
                Assert(manifest.Contains("\"eventDropped\":0"), "Normal event path reported drops.");
                Assert(manifest.Contains("\"eventWindowCount\":1"), "Overlapping events created duplicate windows.");
                Assert(manifest.Contains("\"sha256\":\""), "Manifest is missing SHA-256.");
                var csvPath = manifests[0].Substring(0, manifests[0].Length - ".manifest.json".Length);
                Assert(File.Exists(csvPath), "Manifest points to a missing sealed CSV.");
                Assert(!File.Exists(csvPath + ".tmp"), "Sealed telemetry left a temporary CSV behind.");
                AssertManifestMatchesCsv(manifests[0], csvPath);
            }
            finally
            {
                TryDelete(root);
            }
        }

        private static void RawTelemetryUsesExplicitRotationBudget()
        {
            var root = Path.Combine(Path.GetTempPath(), "epb-telemetry-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var policy = new TelemetryPersistencePolicy
                {
                    Mode = TelemetryPersistenceMode.Raw,
                    SteadyRateHz = 1,
                    EventRateHz = 10,
                    RotateHours = 1,
                    RotateBytes = 4L * 1024L * 1024L,
                    RawRotateBytes = 1024,
                    RetentionDays = 3
                };
                var path = Path.Combine(root, "raw.csv");
                using (var recorder = new PowerSupplyTelemetryCsvRecorder(path, NullLogger.Instance, policy))
                {
                    for (var i = 0; i < 100; i++)
                        recorder.Enqueue(Telemetry(i, PowerSupplyTelemetryEventFlags.None), i, "Raw");
                }
                var manifests = Directory.GetFiles(root, "*.manifest.json");
                Assert(manifests.Length >= 2, "Raw rotation budget did not create multiple sealed segments.");
                Assert(manifests.All(x => File.ReadAllText(x).Contains("\"mode\":\"Raw\"")),
                    "Raw segment manifest did not preserve mode.");
            }
            finally
            {
                TryDelete(root);
            }
        }

        private static void EventQueuePressureSpillsWithoutSilentDrop()
        {
            var root = Path.Combine(Path.GetTempPath(), "epb-telemetry-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var policy = new TelemetryPersistencePolicy
                {
                    Mode = TelemetryPersistenceMode.Layered,
                    SteadyRateHz = 1,
                    EventRateHz = 10,
                    PreEventSeconds = 0,
                    PostEventSeconds = 1,
                    RotateHours = 1,
                    RotateBytes = 4L * 1024L * 1024L,
                    RawRotateBytes = 4L * 1024L * 1024L
                };
                var path = Path.Combine(root, "pressure.csv");
                const int count = 2000;
                using (var recorder = new PowerSupplyTelemetryCsvRecorder(
                    path,
                    NullLogger.Instance,
                    policy,
                    null,
                    0,
                    null,
                    1))
                {
                    for (var i = 0; i < count; i++)
                        recorder.Enqueue(
                            Telemetry(i, PowerSupplyTelemetryEventFlags.ProtectionTripped, "ProtectionTrip"),
                            i,
                            "Event");
                    Assert(recorder.WaitForIdle(TimeSpan.FromSeconds(30)),
                        "Telemetry pressure queues did not drain before bounded Dispose: " +
                        recorder.QueueDiagnostics);
                    Assert(recorder.DroppedEventSamples == 0,
                        "Typed event queue pressure reported a silent event drop.");
                }
                var csvFiles = Directory.GetFiles(root, "*.csv");
                var rows = csvFiles.SelectMany(File.ReadAllLines).Skip(1).ToArray();
                Assert(rows.Length >= count,
                    $"Not every typed event reached a sealed CSV under queue pressure: rows={rows.Length} files={csvFiles.Length}.");
                Assert(rows.Select(x => x.Split(',')[1]).Distinct().Count() >= count,
                    "Typed event monotonic ticks were duplicated/lost under queue pressure.");
            }
            finally { TryDelete(root); }
        }

        private static void ThreeSupplyFakeClockMaintainsOneHzSteadyRate()
        {
            var root = Path.Combine(Path.GetTempPath(), "epb-telemetry-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var policy = new TelemetryPersistencePolicy
                {
                    Mode = TelemetryPersistenceMode.Layered,
                    SteadyRateHz = 1,
                    EventRateHz = 10,
                    PreEventSeconds = 60,
                    PostEventSeconds = 30,
                    RotateHours = 1,
                    RotateBytes = 4L * 1024L * 1024L,
                    RawRotateBytes = 4L * 1024L * 1024L
                };
                var clock = DateTime.UtcNow;
                var path = Path.Combine(root, "steady.csv");
                using (var recorder = new PowerSupplyTelemetryCsvRecorder(
                    path,
                    NullLogger.Instance,
                    policy,
                    null,
                    0,
                    () => clock))
                {
                    for (var supply = 1; supply <= 3; supply++)
                    {
                        for (var second = 0; second < 600; second++)
                        {
                            clock = clock.AddSeconds(1);
                            recorder.Enqueue(
                                Telemetry(second, PowerSupplyTelemetryEventFlags.None, "", supply),
                                second,
                                "Running");
                        }
                    }
                }
                var rows = Directory.GetFiles(root, "*.csv")
                    .SelectMany(File.ReadAllLines)
                    .Skip(1)
                    .ToArray();
                for (var supply = 1; supply <= 3; supply++)
                    Assert(rows.Count(x => x.Split(',')[2] == supply.ToString()) == 600,
                        $"Supply {supply} steady persistence was not 1Hz over the fake 10-minute window.");
            }
            finally { TryDelete(root); }
        }

        private static void SlowSpoolDoesNotBlockProducer()
        {
            var root = Path.Combine(Path.GetTempPath(), "epb-telemetry-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var policy = new TelemetryPersistencePolicy
                {
                    Mode = TelemetryPersistenceMode.Layered,
                    PreEventSeconds = 0,
                    PostEventSeconds = 1,
                    RotateHours = 1,
                    RotateBytes = 4L * 1024L * 1024L,
                    RawRotateBytes = 4L * 1024L * 1024L
                };
                var hookCalls = 0;
                var maxMilliseconds = 0L;
                var path = Path.Combine(root, "slow.csv");
                using (var recorder = new PowerSupplyTelemetryCsvRecorder(
                    path,
                    NullLogger.Instance,
                    policy,
                    null,
                    0,
                    null,
                    1,
                    () =>
                    {
                        Interlocked.Increment(ref hookCalls);
                        Thread.Sleep(50);
                    }))
                {
                    for (var i = 0; i < 80; i++)
                    {
                        var watch = Stopwatch.StartNew();
                        recorder.Enqueue(
                            Telemetry(i, PowerSupplyTelemetryEventFlags.ProtectionTripped, "SlowDisk"),
                            i,
                            "Event");
                        watch.Stop();
                        maxMilliseconds = Math.Max(maxMilliseconds, watch.ElapsedMilliseconds);
                    }
                    Assert(maxMilliseconds < 100,
                        $"Producer Enqueue was blocked by slow spool: max={maxMilliseconds}ms.");
                    Assert(recorder.WaitForIdle(TimeSpan.FromSeconds(30)),
                        "Slow spool did not drain within the bounded test window.");
                }
                Assert(hookCalls > 0, "Slow spool injection did not exercise the background writer.");
            }
            finally { TryDelete(root); }
        }

        private static void ReplayDeduplicatesSequenceAfterCrashBoundary()
        {
            var root = Path.Combine(Path.GetTempPath(), "epb-telemetry-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var path = Path.Combine(root, "crash.csv");
                using (var recorder = new PowerSupplyTelemetryCsvRecorder(
                    path,
                    NullLogger.Instance,
                    new TelemetryPersistencePolicy { RetentionDays = 3 }))
                {
                    recorder.Enqueue(Telemetry(42, PowerSupplyTelemetryEventFlags.None), 1, "Running");
                }

                var csvPath = Directory.GetFiles(root, "*.csv")
                    .Single(x => !x.EndsWith(".manifest.json", StringComparison.OrdinalIgnoreCase));
                var lines = File.ReadAllLines(csvPath);
                Assert(lines.Length == 2, "Crash-boundary setup did not create one persisted row.");
                var manifestPath = csvPath + ".manifest.json";
                Assert(File.Exists(manifestPath), "Crash-boundary setup did not create a manifest.");
                // Simulate power loss after the CSV rename but before the
                // manifest rename. Startup must supplement the sidecar before
                // indexing durable sequences, otherwise the spill duplicate
                // would be written a second time.
                File.Delete(manifestPath);
                var spillPath = path + ".events.spill";
                var window = Convert.ToBase64String(Encoding.UTF8.GetBytes("crash-window"));
                var csv = Convert.ToBase64String(Encoding.UTF8.GetBytes(lines[1]));
                File.WriteAllText(
                    spillPath,
                    "1\t" + window + "\t" + csv + Environment.NewLine,
                    new UTF8Encoding(false));

                using (var replay = new PowerSupplyTelemetryCsvRecorder(
                    path,
                    NullLogger.Instance,
                    new TelemetryPersistencePolicy { RetentionDays = 3 }))
                {
                    Assert(replay.WaitForIdle(TimeSpan.FromSeconds(10)),
                        "Crash-boundary spill replay did not drain: " + replay.QueueDiagnostics);
                }

                Assert(File.Exists(manifestPath),
                    "Startup did not supplement a manifest after CSV rename boundary.");
                var rows = Directory.GetFiles(root, "*.csv")
                    .SelectMany(File.ReadAllLines)
                    .Count(x => !x.StartsWith("Utc,", StringComparison.OrdinalIgnoreCase));
                Assert(rows == 1,
                    "Sequence already durable before journal acknowledgement was replayed twice.");
                Assert(!File.Exists(spillPath) || new FileInfo(spillPath).Length == 0,
                    "Crash-boundary spill journal was not acknowledged after sequence deduplication: " +
                    (File.Exists(spillPath) ? new FileInfo(spillPath).Length.ToString() : "missing"));
            }
            finally { TryDelete(root); }
        }

        private static void RestartRestoresSequenceAndSealsOrphanTmpFromSpill()
        {
            var root = Path.Combine(Path.GetTempPath(), "epb-telemetry-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var path = Path.Combine(root, "restart.csv");
                string row;
                using (var first = new PowerSupplyTelemetryCsvRecorder(
                    path, NullLogger.Instance,
                    new TelemetryPersistencePolicy { RetentionDays = 3 }))
                {
                    first.Enqueue(Telemetry(7, PowerSupplyTelemetryEventFlags.ProtectionTripped), 1, "Event");
                    Assert(first.WaitForIdle(TimeSpan.FromSeconds(10)), "Initial sequence did not drain.");
                }
                var initialCsv = Directory.GetFiles(root, "*.csv").Single();
                row = File.ReadAllLines(initialCsv)[1];
                File.Copy(initialCsv, path + ".tmp", true);
                File.Delete(initialCsv);
                var initialManifest = initialCsv + ".manifest.json";
                if (File.Exists(initialManifest)) File.Delete(initialManifest);

                var spillPath = path + ".events.spill";
                var sequence = row.Substring(row.LastIndexOf(',') + 1);
                File.WriteAllText(
                    spillPath,
                    sequence + "\t" +
                    Convert.ToBase64String(Encoding.UTF8.GetBytes("orphan")) + "\t" +
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(row)) + Environment.NewLine,
                    new UTF8Encoding(false));

                using (var restarted = new PowerSupplyTelemetryCsvRecorder(
                    path, NullLogger.Instance,
                    new TelemetryPersistencePolicy { RetentionDays = 3 }))
                {
                    restarted.Enqueue(Telemetry(8, PowerSupplyTelemetryEventFlags.ProtectionTripped), 2, "Event");
                    Assert(restarted.WaitForIdle(TimeSpan.FromSeconds(15)),
                        "Restarted recorder did not drain orphan spill.");
                }

                var sealedRows = Directory.GetFiles(root, "*.manifest.json")
                    .SelectMany(x => File.ReadAllLines(x.Substring(0, x.Length - ".manifest.json".Length)).Skip(1))
                    .ToArray();
                Assert(sealedRows.Count(x => x.EndsWith(",1", StringComparison.Ordinal)) == 1,
                    "Orphan .tmp spill row was not sealed exactly once.");
                Assert(sealedRows.Any(x => x.EndsWith(",2", StringComparison.Ordinal)),
                    "Restarted sequence did not advance beyond historical spill sequence.");
            }
            finally { TryDelete(root); }
        }

        private static void RetentionRejectsMaliciousManifestPath()
        {
            Assert(!PowerSupplyTelemetryCsvRecorder.IsSafeManifestFileName("..\\outside.csv"),
                "Manifest path traversal was accepted.");
            Assert(!PowerSupplyTelemetryCsvRecorder.IsSafeManifestFileName("C:\\outside.csv"),
                "Manifest rooted path was accepted.");
            Assert(!PowerSupplyTelemetryCsvRecorder.IsSafeManifestFileName("nested/outside.csv"),
                "Manifest separator path was accepted.");
            Assert(PowerSupplyTelemetryCsvRecorder.IsSafeManifestFileName("telemetry_seg0001.csv"),
                "Valid telemetry leaf filename was rejected.");

            var root = Path.Combine(Path.GetTempPath(), "epb-telemetry-root-" + Guid.NewGuid().ToString("N"));
            var realRoot = Path.Combine(root, "real");
            var junctionRoot = Path.Combine(root, "junction");
            Directory.CreateDirectory(realRoot);
            try
            {
                var normalTarget = Path.Combine(realRoot, "telemetry_seg0001.csv");
                File.WriteAllText(normalTarget, string.Empty);
                Assert(PowerSupplyTelemetryCsvRecorder.IsSafeRetentionPathForTests(realRoot, normalTarget),
                    "Normal telemetry root/target path was rejected.");
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c mklink /J \"" + junctionRoot + "\" \"" + realRoot + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var process = Process.Start(psi))
                {
                    if (process != null && process.WaitForExit(5000) && process.ExitCode == 0)
                    {
                        Assert(!PowerSupplyTelemetryCsvRecorder.IsSafeRetentionPathForTests(
                                junctionRoot,
                                Path.Combine(junctionRoot, "telemetry_seg0001.csv")),
                            "Retention accepted a telemetry root junction/reparse point.");
                    }
                }
            }
            finally
            {
                TryDelete(root);
            }
        }

        private static void DetachShutdownDoesNotWaitForSlowWorker()
        {
            var root = Path.Combine(Path.GetTempPath(), "epb-telemetry-shutdown-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var policy = new TelemetryPersistencePolicy
                {
                    Mode = TelemetryPersistenceMode.Layered,
                    PreEventSeconds = 0,
                    PostEventSeconds = 1,
                    RotateHours = 1,
                    RotateBytes = 4L * 1024L * 1024L,
                    RawRotateBytes = 4L * 1024L * 1024L
                };
                var hookEntered = new ManualResetEventSlim(false);
                var path = Path.Combine(root, "shutdown.csv");
                var recorder = new PowerSupplyTelemetryCsvRecorder(
                    path,
                    NullLogger.Instance,
                    policy,
                    null,
                    0,
                    null,
                    1,
                    () =>
                    {
                        hookEntered.Set();
                        Thread.Sleep(250);
                    });
                for (var i = 0; i < 16; i++)
                    recorder.Enqueue(
                        Telemetry(i, PowerSupplyTelemetryEventFlags.ProtectionTripped, "SlowShutdown"),
                        i,
                        "Event");
                hookEntered.Wait(3000);
                var watch = Stopwatch.StartNew();
                recorder.DetachAndShutdown();
                watch.Stop();
                Assert(watch.ElapsedMilliseconds < 250,
                    "DetachAndShutdown synchronously waited for the slow spool worker: " +
                    watch.ElapsedMilliseconds + "ms.");
                for (var i = 0; i < 80 && !File.Exists(path + ".manifest.json"); i++)
                    Thread.Sleep(100);
            }
            finally
            {
                TryDelete(root);
            }
        }

        private static void AssertManifestMatchesCsv(string manifestPath, string csvPath)
        {
            var bytes = new FileInfo(csvPath).Length;
            var rows = File.ReadAllLines(csvPath).Length - 1;
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(csvPath))
            {
                var hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                var manifest = File.ReadAllText(manifestPath);
                Assert(ExtractLong(manifest, "bytes") == bytes, "Manifest bytes do not match CSV length.");
                Assert(ExtractLong(manifest, "rows") == rows, "Manifest row count does not match CSV lines.");
                Assert(manifest.Contains("\"sha256\":\"" + hash + "\""),
                    "Manifest SHA-256 does not match sealed CSV.");
            }
        }

        private static void RetentionLeavesTmpAndUnknownArtifactsUntouched()
        {
            var root = Path.Combine(Path.GetTempPath(), "epb-telemetry-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var path = Path.Combine(root, "telemetry.csv");
                var tmp = path + ".tmp";
                var unknown = Path.Combine(root, "unrelated-sealed.csv");
                var unknownManifest = unknown + ".manifest.json";
                File.WriteAllText(tmp, "active");
                File.WriteAllText(unknown, "unknown");
                File.WriteAllText(unknownManifest,
                    "{\"kind\":\"Other\",\"sealedUtc\":\"2000-01-01T00:00:00.0000000Z\",\"fileName\":\"unrelated-sealed.csv\"}");
                using (var recorder = new PowerSupplyTelemetryCsvRecorder(
                    path,
                    NullLogger.Instance,
                    new TelemetryPersistencePolicy { RetentionDays = 3 }))
                    recorder.Enqueue(Telemetry(1, PowerSupplyTelemetryEventFlags.None), 1, "Running");
                Assert(File.Exists(tmp), "Retention touched an active .tmp file.");
                Assert(File.Exists(unknown) && File.Exists(unknownManifest),
                    "Retention touched an unknown/non-telemetry artifact.");
            }
            finally { TryDelete(root); }
        }

        private static long ExtractLong(string json, string name)
        {
            var marker = "\"" + name + "\":";
            var start = json.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return -1;
            start += marker.Length;
            var end = start;
            while (end < json.Length && char.IsDigit(json[end])) end++;
            return long.TryParse(json.Substring(start, end - start), out var value) ? value : -1;
        }

        private static PowerSupplyTelemetry Telemetry(
            int sequence,
            PowerSupplyTelemetryEventFlags flags,
            string eventCode = "",
            int supplyId = 1)
        {
            return new PowerSupplyTelemetry
            {
                TimestampUtc = DateTime.UtcNow,
                MonotonicTicks = sequence,
                SupplyId = supplyId,
                ElectricalGroupId = supplyId,
                EventFlags = flags,
                EventCode = eventCode,
                Snapshot = new PswSnapshot
                {
                    TimestampUtc = DateTime.UtcNow,
                    SupplyId = supplyId,
                    IsConnected = true,
                    IsVerifiedPsw = true,
                    OutputEnabled = true,
                    SetVoltage = 12,
                    SetCurrent = 10,
                    MeasuredVoltage = 12,
                    MeasuredCurrent = 1,
                    MeasuredPower = 12
                }
            };
        }

        private static void TryDelete(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); }
            catch { }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
