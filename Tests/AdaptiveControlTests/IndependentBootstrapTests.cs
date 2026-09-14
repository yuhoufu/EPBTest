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
            var count = 0;
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
            File.WriteAllText(selectionConfig, xml.Replace("true", "false"));
            if (legacyConfig.SequenceEqual(UnattendedRunCheckpointStore.ReadStableConfiguration(selectionConfig)) ||
                !independentConfig.SequenceEqual(UnattendedRunCheckpointStore.ReadStableConfiguration(selectionConfig, true)))
                throw new Exception("selection hash separation weakened legacy identity or rejected independent selection");
            count++;
            File.WriteAllText(selectionConfig, xml.Replace(">12<", ">13<"));
            if (independentConfig.SequenceEqual(UnattendedRunCheckpointStore.ReadStableConfiguration(selectionConfig, true)))
                throw new Exception("independent configuration ignored target changes");
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
    }
}
