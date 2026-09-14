using System;
using System.IO;
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
