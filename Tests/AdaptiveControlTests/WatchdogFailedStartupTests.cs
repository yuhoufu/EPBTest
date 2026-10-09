using System;
using System.IO;
using MTEmbTest;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    internal static class WatchdogFailedStartupTests
    {
        internal static int RunAll()
        {
            var checks = VerifyCancellationEvidence();
            var rejectedContext = WatchdogRuntime.CreateUiBindingProductionContext();
            if (!rejectedContext.TryRejectBeforeControlAdmission())
                throw new Exception("unadmitted initial context could not be cancelled");
            try
            {
                rejectedContext.MarkControlAdmission();
                throw new Exception("cancelled context granted new control admission");
            }
            catch (InvalidOperationException) { checks++; }
            var admittedContext = WatchdogRuntime.CreateUiBindingProductionContext();
            admittedContext.MarkControlAdmission();
            if (admittedContext.TryRejectBeforeControlAdmission())
                throw new Exception("already admitted control was treated as unissued startup");
            checks++;
            var root = Path.Combine(Environment.GetEnvironmentVariable("EPB_TEST_ARTIFACT_ROOT") ?? Path.GetTempPath(),
                "failed-start-" + Guid.NewGuid().ToString("N"));
            var journal = Path.Combine(root, "WatchdogSessions");
            Directory.CreateDirectory(Path.Combine(root, "Config"));
            // Invalid XML deterministically fails seed preparation before any helper or hardware starts.
            File.WriteAllText(Path.Combine(root, "Config", "TestConfig.xml"), "<TestConfig>");
            WatchdogRuntime.ConfigureDaqRuntimeSettings(new Config.DaqRuntimeSettings(
                Config.DaqRuntimeSettings.DefaultSampleRateHz, Config.DaqRuntimeSettings.DefaultSamplesPerChannel));
            WatchdogRuntime.ConfigureJournalExportPath(journal);
            WatchdogRuntime.SetHeartbeatProvider(() => new WatchdogHeartbeat { Phase = "Idle" });
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var result = WatchdogRuntime.StartSessionAsync(new[] { 5, 8, 9 }).GetAwaiter().GetResult();
                if (result.Attached || string.IsNullOrWhiteSpace(result.Warning) ||
                    result.Warning.Contains("上一个看门狗会话"))
                    throw new Exception("failed initial start was not independently rejected on retry: " + result.Warning);
                var retained = WatchdogRuntime.CaptureRetainedShutdown();
                if (retained != null)
                    throw new Exception("pre-control startup failure retained an unfinishable Closing session: " +
                        retained.RuntimeReceipt?.TerminalReason);
                var receipt = WatchdogRuntime.ShutdownRuntimeWithReceipt();
                if (!receipt.IsTerminal)
                    throw new Exception("failed initial start has no combined terminal receipt");
                WatchdogClosingTombstone closing;
                if (!WatchdogClosingTombstoneStore.TryRead(journal, receipt.SessionId, out closing) ||
                    closing.State != WatchdogClosingTombstoneState.Terminal)
                    throw new Exception("failed initial start has no durable terminal record");
                if (closing.IsSafetyTerminal || closing.FinalSafetyResultCommitted ||
                    closing.MotorsOff || closing.PowerOff || closing.PressureSafe)
                    throw new Exception("cancelled startup fabricated physical safety evidence");
            }
            Console.WriteLine($"PASS failed startup {checks + 2}/{checks + 2}");
            return checks + 2;
        }

        private static int VerifyCancellationEvidence()
        {
            Func<WatchdogClosingTombstone> create = () => new WatchdogClosingTombstone
            {
                SchemaVersion = 6, SessionId = Guid.NewGuid().ToString("N"),
                SessionGeneration = 1, SessionLease = 1, StateVersion = 1,
                State = WatchdogClosingTombstoneState.Terminal,
                SafetyStage = WatchdogClosingSafetyStage.Terminal,
                StartupRejectedBeforeControl = true
            };
            var terminal = create();
            if (!terminal.IsValidFor(terminal.SessionId) || !terminal.IsSessionTerminal || terminal.IsSafetyTerminal)
                throw new Exception("unadmitted cancellation was confused with physical safety");
            var unsafeChanges = new Action<WatchdogClosingTombstone>[]
            {
                x => x.StopSafetyTransactionId = Guid.NewGuid().ToString("N"),
                x => x.StopRunId = Guid.NewGuid().ToString("N"),
                x => x.StopRunEpoch = 1,
                x => x.StopSafetyBoundaryGeneration = 1,
                x => x.SafetyHandoffId = "pending",
                x => x.TakeoverTransactionId = Guid.NewGuid().ToString("N"),
                x => x.RelaunchPermitGeneration = 1,
                x => x.RelaunchPermitId = Guid.NewGuid().ToString("N"),
                x => x.RelaunchPermitNonceSha256 = new string('a', 64),
                x => x.FinalSafetyResultCommitted = true,
                x => x.MotorsOff = true,
                x => x.PowerOff = true,
                x => x.PressureSafe = true,
                x => x.ControllerStopStage = 1,
                x => x.ControllerProgressVersion = 1,
                x => x.OldProcessExitProven = true,
                x => x.OldProcessId = 7,
                x => x.OldProcessStartUtcTicks = DateTime.UtcNow.Ticks,
                x => x.OldProcessExitObservedUtcTicks = DateTime.UtcNow.Ticks
            };
            foreach (var change in unsafeChanges)
            {
                var invalid = create();
                change(invalid);
                if (invalid.IsValidFor(invalid.SessionId) || invalid.IsSessionTerminal)
                    throw new Exception("active safety or recovery identity accepted startup cancellation");
            }
            for (var schema = 1; schema <= 5; schema++)
            {
                var legacy = create();
                legacy.SchemaVersion = schema;
                if (legacy.IsValidFor(legacy.SessionId) || legacy.IsSessionTerminal)
                    throw new Exception("legacy record gained unadmitted cancellation authority");
                legacy.StartupRejectedBeforeControl = false;
                if (!legacy.IsValidFor(legacy.SessionId) || legacy.IsSessionTerminal)
                    throw new Exception("legacy incomplete safety record changed meaning");
            }
            return 1 + unsafeChanges.Length + 5;
        }
    }
}
