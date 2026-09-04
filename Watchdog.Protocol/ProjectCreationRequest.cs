using System;
using System.IO;
using System.Linq;

namespace MTTFTest.Watchdog.Protocol
{
    // Metadata only. Existing histories, isolation and execution authority are
    // not writable fields in the operator's explicit New Project request.
    public sealed class ProjectCreationRequest
    {
        public int ContractVersion { get; set; } = 1;
        public EngineTestConfiguration Configuration { get; set; }
        public ProjectCreationRequest Clone() => new ProjectCreationRequest
        { ContractVersion = ContractVersion, Configuration = Configuration?.Clone() };

        public bool IsStructurallyValid(string targetPath)
        {
            if (ContractVersion != 1 || Configuration?.IsStructurallyValid() != true ||
                !ProjectSwitchPlan.IsProjectConfigurationPath(targetPath)) return false;
            var name = Configuration.TestName;
            if (name != name.Trim() || name.StartsWith(".v3-create-", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(".v3-reset-", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".", StringComparison.Ordinal) || name == "." || name == ".." ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            var stem = name.Split('.')[0].ToUpperInvariant();
            if (new[] { "CON", "PRN", "AUX", "NUL" }.Contains(stem) ||
                Enumerable.Range(1, 9).Any(n => stem == "COM" + n || stem == "LPT" + n)) return false;
            try
            {
                var root = Configuration.StoreDir;
                if (root.Length < 3 || root[1] != ':' || root[2] != '\\' || root.IndexOf(':', 2) >= 0 ||
                    !string.Equals(Path.GetFullPath(root).TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return false;
                return string.Equals(Path.Combine(root, name, "Config", "TestConfig.xml"), targetPath, StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (PathTooLongException) { return false; }
        }

        public string ComputeSha256() => SupervisorProtocol.ComputeTextSha256("NewProjectV1\n" + Configuration?.ComputeSha256());
    }
}
