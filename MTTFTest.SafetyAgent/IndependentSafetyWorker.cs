using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.SafetyAgent
{
    internal static class IndependentSafetyWorker
    {
        internal static int Run(string commandPath)
            => Run(commandPath, new ProductionSafetyHardwareFactory());

        internal static int Run(string commandPath, ISafetyHardwareFactory factory)
        {
            using (var identity = WindowsIdentity.GetCurrent())
                if (!identity.IsSystem) return 77;
            // Do not write a receipt to an untrusted caller-controlled path.
            IndependentProtectedFiles.RequireTrustedFile(commandPath);
            var commandHash = SupervisorProtocol.ComputeSha256(commandPath);
            var command = BoundedJson.Read<IndependentSafetyWorkerCommand>(commandPath);
            command.Validate(DateTime.UtcNow.Ticks);
            foreach (var file in command.Files)
            {
                var path = Path.Combine(command.ConfigDirectory, file.Name);
                IndependentProtectedFiles.RequireTrustedFile(path);
                if (!string.Equals(SupervisorProtocol.ComputeSha256(path), file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("IndependentSealedConfigHashMismatch:" + file.Name);
            }
            if (!string.Equals(SupervisorProtocol.ComputeSha256(commandPath), commandHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("IndependentSafetyCommandChangedDuringValidation");
            var result = IndependentSafetyStages.Execute(command.Transaction, command.ConfigDirectory,
                command.Runtime, factory);
            using (var current = Process.GetCurrentProcess())
            {
                var completed = DateTime.UtcNow.Ticks;
                var receipt = new IndependentSafetyWorkerReceipt
                {
                    StageNonce = command.StageNonce, CommandSha256 = commandHash,
                    RequestId = result.RequestId, Generation = result.Generation, Phase = result.Phase,
                    WorkerPid = current.Id, WorkerStartUtcTicks = current.StartTime.ToUniversalTime().Ticks,
                    CompletedUtcTicks = completed, Confirmed = result.Confirmed && completed < command.DeadlineUtcTicks,
                    Detail = completed >= command.DeadlineUtcTicks ? "IndependentSafetyWorkerDeadlineExceeded" : result.Detail
                };
                var receiptPath = Path.GetFullPath(commandPath) + ".receipt.json";
                if (receipt.Detail?.Length > 512) receipt.Detail = receipt.Detail.Substring(0, 512);
                if (File.Exists(receiptPath)) throw new InvalidOperationException("IndependentSafetyReceiptAlreadyExists");
                BoundedJson.Write(receiptPath, receipt);
                return receipt.Confirmed ? 0 : 2;
            }
        }
    }
}
