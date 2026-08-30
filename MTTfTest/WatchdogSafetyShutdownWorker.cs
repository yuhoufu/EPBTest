using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Config;
using Controller;
using IO.NI;
using MTTFTest.Watchdog.Protocol;

namespace MtEmbTest
{
    internal static class WatchdogSafetyShutdownWorker
    {
        internal static bool TryParse(string[] args, out string sessionId,
            out string handoffId, out string nonce, out string journalDirectory)
        {
            string Read(string name)
            {
                for (var index = 0; index + 1 < (args?.Length ?? 0); index++)
                    if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
                        return args[index + 1];
                return string.Empty;
            }
            sessionId = Read("--watchdog-safety-shutdown");
            handoffId = Read("--handoff-id");
            nonce = Read("--handoff-nonce");
            journalDirectory = Read("--journal-directory");
            return !string.IsNullOrWhiteSpace(sessionId);
        }

        internal static int Run(
            string sessionId,
            string handoffId,
            string nonce,
            string journalDirectory)
        {
            using (var mutex = new Mutex(false, "Local\\MTTFTest.SafetyHandoff." + handoffId))
            {
                bool owns;
                try { owns = mutex.WaitOne(0, false); }
                catch (AbandonedMutexException) { owns = true; }
                if (!owns) return 2;
                try
                {
                    WatchdogSafetyHandoffReceipt receipt;
                    if (!WatchdogSafetyHandoffReceiptStore.TryRead(
                            journalDirectory, sessionId, out receipt) ||
                        !string.Equals(receipt.HandoffId, handoffId, StringComparison.Ordinal) ||
                        !string.Equals(receipt.Nonce, nonce, StringComparison.Ordinal) ||
                        receipt.State < WatchdogSafetyHandoffState.Accepted)
                        return 3;
                    if (receipt.IsSafetyCompleted) return 0;

                    var appDirectory = Path.GetDirectoryName(
                        string.IsNullOrWhiteSpace(receipt.MainExecutablePath)
                            ? typeof(WatchdogSafetyShutdownWorker).Assembly.Location
                            : receipt.MainExecutablePath) ?? Environment.CurrentDirectory;
                    var snapshot = WatchdogSafetyConfigSnapshotStore.Validate(
                        journalDirectory,
                        receipt.HandoffId,
                        receipt.ConfigSnapshotPath,
                        receipt.ConfigSnapshotManifestPath,
                        receipt.ConfigSnapshotManifestSha256);
                    if (snapshot?.Succeeded != true)
                        throw new InvalidDataException(
                            snapshot?.Error ?? "SafetyConfigSnapshotUnavailable");
                    var configDirectory = snapshot.ConfigDirectory;
                    var config = ConfigLoader.LoadAll(configDirectory, Config.NullLogger.Instance);

                    bool motorsOff;
                    bool aoReleased;
                    bool powerOff;
                    bool pressureSafe;
                    using (var doController = new DoController(config.DO, Config.NullLogger.Instance))
                    using (var aoController = new AoController(config.AO, Config.NullLogger.Instance))
                    {
                        motorsOff = doController.EstablishColdStartAllOffBaseline();
                        aoReleased = aoController.TryResetAll();
                    }

                    var powerConfig = PowerSupplyConfigLoader.Load(
                        Path.Combine(configDirectory, "PowerSupplyConfig.xml"));
                    using (var power = new PowerSupplyCoordinator(
                               powerConfig, config.Test.Groups, Config.NullLogger.Instance))
                    {
                        var disabled = power.DisableAllForSafetyAsync(
                                "WatchdogSafetyHandoff:" + handoffId,
                                CancellationToken.None)
                            .GetAwaiter().GetResult();
                        powerOff = disabled.All(item => item.ConfirmedOff);
                    }

                    pressureSafe = ConfirmPressureSafe(config, appDirectory);
                    if (!motorsOff || !aoReleased || !powerOff || !pressureSafe)
                        return 4;

                    WatchdogSafetyHandoffReceipt latest;
                    if (!WatchdogSafetyHandoffReceiptStore.TryRead(
                            journalDirectory, sessionId, out latest) ||
                        !string.Equals(latest.HandoffId, handoffId, StringComparison.Ordinal) ||
                        !string.Equals(latest.Nonce, nonce, StringComparison.Ordinal))
                        return 5;
                    using (var process = Process.GetCurrentProcess())
                    {
                        latest.WorkerProcessId = process.Id;
                        latest.WorkerProcessStartUtcTicks = process.StartTime.ToUniversalTime().Ticks;
                    }
                    latest.MotorsOff = true;
                    latest.PowerOff = true;
                    latest.PressureSafe = true;
                    latest.State = WatchdogSafetyHandoffState.Completed;
                    latest.Revision++;
                    latest.Detail = "SafetyHandoffCompleted:DO=OFF;Power=OFF;AO=0;PressureSafe";
                    WatchdogSafetyHandoffReceiptStore.WriteThrough(journalDirectory, latest);
                    return 0;
                }
                catch (Exception ex)
                {
                    TryWriteWorkerFailure(journalDirectory, sessionId, handoffId, nonce, ex);
                    return 10;
                }
                finally
                {
                    try { mutex.ReleaseMutex(); } catch { }
                }
            }
        }

        private static bool ConfirmPressureSafe(GlobalConfig config, string appDirectory)
        {
            using (var hardware = new PressureCalibrationHardware(
                       config, Config.NullLogger.Instance, appDirectory))
            {
                foreach (var hydraulic in config.Test.Hydraulics
                             .Where(item => item.Enabled)
                             .OrderBy(item => item.Id))
                {
                    hardware.SetPressureDo(hydraulic.Id, false);
                    hardware.WritePressure(
                        hydraulic.Id == 1 ? "Cylinder1" : "Cylinder2", 0);
                    var deadline = DateTime.UtcNow.AddMilliseconds(
                        Math.Max(1000, hydraulic.ReleaseTimeoutMs));
                    DateTime? stableSince = null;
                    while (DateTime.UtcNow <= deadline)
                    {
                        var sample = hardware.ReadPressureSample(hydraulic.Id);
                        var valid = !HydraulicController.ClassifyPressureSampleFailure(
                            sample,
                            double.NegativeInfinity,
                            hydraulic.PressureSampleMaxAgeMs).HasValue;
                        if (valid && sample.ValueBar <= Math.Max(0, hydraulic.ReleaseSafePressureBar))
                        {
                            if (!stableSince.HasValue) stableSince = DateTime.UtcNow;
                            if ((DateTime.UtcNow - stableSince.Value).TotalMilliseconds >=
                                Math.Max(0, hydraulic.ReleaseStableMs))
                                break;
                        }
                        else stableSince = null;
                        Thread.Sleep(10);
                    }
                    if (!stableSince.HasValue ||
                        (DateTime.UtcNow - stableSince.Value).TotalMilliseconds <
                        Math.Max(0, hydraulic.ReleaseStableMs))
                        return false;
                }
                return true;
            }
        }

        private static void TryWriteWorkerFailure(
            string journalDirectory,
            string sessionId,
            string handoffId,
            string nonce,
            Exception exception)
        {
            try
            {
                WatchdogSafetyHandoffReceipt receipt;
                if (WatchdogSafetyHandoffReceiptStore.TryRead(
                        journalDirectory,
                        sessionId,
                        out receipt) &&
                    string.Equals(receipt.HandoffId, handoffId, StringComparison.Ordinal) &&
                    string.Equals(receipt.Nonce, nonce, StringComparison.Ordinal) &&
                    !receipt.IsTerminal)
                {
                    var root = exception?.GetBaseException();
                    receipt.State = WatchdogSafetyHandoffState.Failed;
                    receipt.FailureCode = root is FileNotFoundException ||
                                          root is DirectoryNotFoundException ||
                                          root is InvalidDataException
                        ? "SafetyConfigSnapshotInvalid"
                        : "SafetyWorkerExecutionFailed";
                    receipt.Detail = root?.Message ?? "SafetyWorkerExecutionFailed";
                    receipt.Revision++;
                    WatchdogSafetyHandoffReceiptStore.WriteThrough(
                        journalDirectory,
                        receipt);
                }
                var path = Path.Combine(
                    WatchdogJournalPaths.ValidateProjectDirectory(journalDirectory),
                    "session-" + WatchdogJournalPaths.SafeName(sessionId) + ".safety-worker.log");
                File.AppendAllText(
                    path,
                    DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " +
                    handoffId + " " + nonce + " " + exception.GetBaseException() + Environment.NewLine);
            }
            catch { }
        }
    }
}
