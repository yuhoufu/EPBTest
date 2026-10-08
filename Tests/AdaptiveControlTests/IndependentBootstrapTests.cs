using System;
using System.IO;
using System.Linq;
using MTEmbTest;

namespace AdaptiveControlTests
{
    internal static class IndependentBootstrapTests
    {
        internal static int RunAll()
        {
            var count = VerifyInitializationBoundary() + VerifyIndependentConfigurationInputs();
            var nonce = Guid.NewGuid().ToString("N");
            var path = @"D:\registered\executor.json";
            void Reject(string[] args)
            {
                try { IndependentRecoveryStartup.Parse(args); }
                catch (InvalidOperationException) { count++; return; }
                throw new Exception("malformed or mixed independent launch accepted");
            }
            if (IndependentRecoveryStartup.Parse(Array.Empty<string>()) != null)
                throw new Exception("ordinary launch treated as recovery");
            count++;
            var manual = IndependentRecoveryStartup.Parse(new[] { "--independent-installation", path });
            if (manual == null || manual.IsRecoveryLaunch || manual.Nonce != null || IndependentRecoveryStartup.Current != null)
                throw new Exception("manual installed launch consumed recovery authority");
            count++;
            Reject(new[] { "--independent-installation", "relative.json" });
            Reject(new[] { "--independent-installation", path, "--independent-ticket", nonce });
            Reject(new[] { "--independent-installation", path, "--epb-recover", nonce });
            Reject(new[] { "--independent-ticket", nonce });
            Reject(new[] { "--independent-registration", path });
            Reject(new[] { "--independent-registration", path, "--independent-ticket" });
            Reject(new[] { "--independent-registration", path, "--independent-ticket", "invalid" });
            Reject(new[] { "--independent-registration", "relative.json", "--independent-ticket", nonce });
            Reject(new[] { "--independent-registration", path, "--independent-ticket", nonce, "--independent-ticket", nonce });
            Reject(new[] { "--independent-registration", path, "--independent-ticket", nonce, "--epb-recover", nonce });
            Reject(new[] { "--independent-registration", path, "--independent-ticket", nonce, "--watchdog-recover", nonce });
            var parsed = IndependentRecoveryStartup.Parse(new[] { "--independent-registration", path, "--independent-ticket", nonce });
            if (parsed.Nonce != nonce || parsed.RegistrationPath != path || IndependentRecoveryStartup.Current != null)
                throw new Exception("parsing changed process authority");
            count++;
            var root = Path.Combine(Environment.GetEnvironmentVariable("EPB_TEST_ARTIFACT_ROOT") ?? Path.GetTempPath(),
                "bootstrap-rejection-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var selectionConfig = Path.Combine(root, "selection.xml");
            var xml = "<TestConfig><EpbRecords><Record><Id>7</Id><Enabled>true</Enabled><TotalCount>12</TotalCount></Record></EpbRecords></TestConfig>";
            File.WriteAllText(selectionConfig, xml);
            var legacyConfig = UnattendedRunCheckpointStore.ReadStableConfiguration(selectionConfig);
            var independentConfig = UnattendedRunCheckpointStore.ReadStableConfiguration(selectionConfig, true);
            var progressXml = xml.Replace("</Record>", "<MechanicalCycleCount>8</MechanicalCycleCount></Record>");
            File.WriteAllText(selectionConfig, progressXml);
            if (!legacyConfig.SequenceEqual(UnattendedRunCheckpointStore.ReadStableConfiguration(selectionConfig)) ||
                !independentConfig.SequenceEqual(UnattendedRunCheckpointStore.ReadStableConfiguration(selectionConfig, true)) ||
                File.ReadAllText(selectionConfig) != progressXml)
                throw new Exception("Mechanical progress changed configuration identity or was rewritten on disk");
            count++;
            File.WriteAllText(selectionConfig, progressXml.Replace(">8<", ">0<"));
            if (!independentConfig.SequenceEqual(UnattendedRunCheckpointStore.ReadStableConfiguration(selectionConfig, true)))
                throw new Exception("Default configuration progress normalization invalidated independent recovery");
            count++;
            File.WriteAllText(selectionConfig, xml.Replace("true", "false"));
            if (legacyConfig.SequenceEqual(UnattendedRunCheckpointStore.ReadStableConfiguration(selectionConfig)) ||
                !independentConfig.SequenceEqual(UnattendedRunCheckpointStore.ReadStableConfiguration(selectionConfig, true)))
                throw new Exception("selection hash separation weakened legacy identity or rejected independent selection");
            count++;
            File.WriteAllText(selectionConfig, xml.Replace(">12<", ">13<"));
            if (independentConfig.SequenceEqual(UnattendedRunCheckpointStore.ReadStableConfiguration(selectionConfig, true)))
                throw new Exception("independent configuration ignored target changes");
            count++;
            File.WriteAllText(selectionConfig, xml.Replace("</Record>", "<PermanentAlarmLatched>true</PermanentAlarmLatched><PermanentAlarmReason>Confirmed</PermanentAlarmReason><ConsecutivePeriodOverrunCount>2</ConsecutivePeriodOverrunCount></Record>"));
            if (!independentConfig.SequenceEqual(UnattendedRunCheckpointStore.ReadStableConfiguration(selectionConfig, true)) ||
                legacyConfig.SequenceEqual(UnattendedRunCheckpointStore.ReadStableConfiguration(selectionConfig)))
                throw new Exception("eligibility metadata invalidated independent safety identity or weakened legacy identity");
            count++;
            var missing = IndependentRecoveryStartup.Parse(new[] { "--independent-registration", Path.Combine(root, "missing.json"),
                "--independent-ticket", nonce });
            var denied = false;
            try { missing.ConsumeAndBind(); } catch (InvalidDataException) { denied = true; }
            if (!denied || IndependentRecoveryStartup.Current != null) throw new Exception("missing installed registration bound recovery");
            count++;
            Console.WriteLine($"PASS independent bootstrap {count}/{count}");
            return count;
        }

        private static int VerifyIndependentConfigurationInputs()
        {
            var root = Path.Combine(Environment.GetEnvironmentVariable("EPB_TEST_ARTIFACT_ROOT") ?? Path.GetTempPath(),
                "binding-hash-" + Guid.NewGuid().ToString("N"));
            var project = Path.Combine(root, "project", "Config");
            Directory.CreateDirectory(project);
            var projectPath = Path.Combine(project, "TestConfig.xml");
            File.WriteAllText(projectPath, "<TestConfig><PeriodMs>15000</PeriodMs><TestTarget>100</TestTarget></TestConfig>");
            var config = new GlobalConfig { Test = new TestConfig { StoreDir = root, TestName = "project" } };
            var runtime = Config.RuntimeConfigPaths.Directory;
            Directory.CreateDirectory(runtime);
            var uiPath = Path.Combine(runtime, "UIConfig.xml");
            var templatePath = Path.Combine(runtime, "TestConfig.xml");
            var controlPath = Path.Combine(runtime, "BindingControlRegression.xml");
            var oldUi = File.Exists(uiPath) ? File.ReadAllBytes(uiPath) : null;
            var oldTemplate = File.Exists(templatePath) ? File.ReadAllBytes(templatePath) : null;
            var oldControl = File.Exists(controlPath) ? File.ReadAllBytes(controlPath) : null;
            try
            {
                File.WriteAllText(uiPath, "<UIConfig><Curve Checked=\"true\" /></UIConfig>");
                File.WriteAllText(templatePath, "<TestConfig><TestName>old-template</TestName></TestConfig>");
                File.WriteAllText(controlPath, "<Control><CurrentLimit>18</CurrentLimit></Control>");
                var baseline = UnattendedRunCheckpointStore.ComputeIndependentConfigurationHash(config);
                if (baseline == "unavailable") throw new Exception("configuration fixture could not be hashed");
                File.WriteAllText(uiPath, "<UIConfig><Curve Checked=\"false\" /></UIConfig>");
                if (baseline != UnattendedRunCheckpointStore.ComputeIndependentConfigurationHash(config))
                    throw new Exception("display-only UI state changed independent control identity");
                File.WriteAllText(templatePath, "<TestConfig><TestName>new-template</TestName></TestConfig>");
                if (baseline != UnattendedRunCheckpointStore.ComputeIndependentConfigurationHash(config))
                    throw new Exception("default template changed an already-selected project's identity");
                File.WriteAllText(projectPath, "<TestConfig><PeriodMs>16000</PeriodMs><TestTarget>100</TestTarget></TestConfig>");
                if (baseline == UnattendedRunCheckpointStore.ComputeIndependentConfigurationHash(config))
                    throw new Exception("project control period no longer protected");
                File.WriteAllText(projectPath, "<TestConfig><PeriodMs>15000</PeriodMs><TestTarget>100</TestTarget></TestConfig>");
                File.WriteAllText(controlPath, "<Control><CurrentLimit>19</CurrentLimit></Control>");
                if (baseline == UnattendedRunCheckpointStore.ComputeIndependentConfigurationHash(config))
                    throw new Exception("non-UI control XML no longer protected");
                File.Delete(projectPath);
                if (UnattendedRunCheckpointStore.ComputeIndependentConfigurationHash(config) != "unavailable")
                    throw new Exception("missing active project configuration silently fell back to template");
                return 5;
            }
            finally
            {
                if (oldUi == null) File.Delete(uiPath); else File.WriteAllBytes(uiPath, oldUi);
                if (oldTemplate == null) File.Delete(templatePath); else File.WriteAllBytes(templatePath, oldTemplate);
                if (oldControl == null) File.Delete(controlPath); else File.WriteAllBytes(controlPath, oldControl);
            }
        }

        private static int VerifyInitializationBoundary()
        {
            var partial = new MonitorInitializationGate();
            partial.CompleteLoad();
            if (partial.WaitAsync(5).GetAwaiter().GetResult())
                throw new Exception("Load without Shown admitted recovery");
            partial.CompleteShown();
            if (!partial.WaitAsync(100).GetAwaiter().GetResult())
                throw new Exception("completed initialization was blocked after an earlier wait timed out");
            partial.Fail();
            if (partial.WaitAsync(100).GetAwaiter().GetResult())
                throw new Exception("closed initialized monitor remained ready");

            var failed = new MonitorInitializationGate();
            failed.CompleteLoad();
            var waiting = failed.WaitAsync(30000);
            failed.Fail();
            if (!waiting.Wait(1000) || waiting.Result)
                throw new Exception("initialization failure did not release the recovery waiter");
            failed.CompleteShown();
            failed.CompleteLoad();
            if (failed.IsReady || failed.WaitAsync(100).GetAwaiter().GetResult())
                throw new Exception("late completion resurrected failed initialization");

            var reverse = new MonitorInitializationGate();
            reverse.CompleteShown();
            reverse.CompleteShown();
            if (reverse.WaitAsync(5).GetAwaiter().GetResult())
                throw new Exception("duplicate Shown substituted for Load completion");
            reverse.CompleteLoad();
            if (!reverse.WaitAsync(100).GetAwaiter().GetResult())
                throw new Exception("both initialization stages did not release recovery");
            return 7;
        }
    }
}
