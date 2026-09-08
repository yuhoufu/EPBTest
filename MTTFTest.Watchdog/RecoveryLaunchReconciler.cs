using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using MTTFTest.RecoveryControl;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Watchdog
{
    // Reconciliation records an already created process. It never issues a
    // capability, starts a process, or changes the current operator intent.
    internal static class RecoveryLaunchReconciler
    {
        internal static void ReconcileOutstanding(RecoveryControlStore store)
        {
            foreach (var reserved in store.Read().Launches.Where(l => l.State == "Reserved"))
                store.ConfirmReservationExpiredBeforeConsumption(reserved.OperationId, DateTime.UtcNow);
            foreach (var pending in store.Read().Launches.Where(l => l.State == "Consumed"))
                TryReconcile(store, pending.OperationId);
            foreach (var created in store.Read().Launches.Where(l => l.State == "Started"))
                store.ConfirmLaunchedProcessExited(created.OperationId);
        }

        internal static bool TryReconcile(RecoveryControlStore store, string operationId, string receiptPath = null)
        {
            var state = store.Read();
            var reservation = state.Launches.SingleOrDefault(l => l.OperationId == operationId);
            if (reservation == null) throw new InvalidOperationException("RecoveryReconcileReservationMissing");
            if (reservation.State == "Started" || reservation.State == "Exited" || reservation.State == "StartFailed") return true;
            if (reservation.State != "Consumed") return false;
            var path = receiptPath ?? SessionAgentProtocol.ConsumptionPath(operationId);
            if (!File.Exists(path) || !File.Exists(path + ".capability")) return false;
            var json = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 };
            var record = json.Deserialize<SessionLaunchConsumptionRecord>(ReadBounded(path));
            var capability = json.Deserialize<SessionLaunchCapability>(ReadBounded(path + ".capability"));
            if (record?.State != "Started") return false;
            if (capability?.IsStructurallyValid() != true || !capability.IsRecoveryLaunch ||
                !SessionAgentProtocol.VerifySeal(capability, Convert.FromBase64String(record.CapabilitySealBase64 ?? "")))
                throw new InvalidDataException("RecoveryReconcileSealInvalid");
            var fence = RecoveryLaunchFence.Parse(capability.RecoveryFenceJson, true);
            var expected = reservation.Authorization;
            if (fence == null || expected == null ||
                fence.Authorization.InstallationId != expected.InstallationId ||
                fence.Authorization.AuthorizationId != expected.AuthorizationId ||
                fence.Authorization.IntentVersion != expected.IntentVersion ||
                fence.Authorization.TakeoverEpoch != expected.TakeoverEpoch ||
                record.SchemaVersion != SessionAgentProtocol.SchemaVersion ||
                record.CapabilityId != operationId || capability.CapabilityId != operationId ||
                record.SessionId != capability.SessionId || record.PermitId != capability.PermitId ||
                record.PermitGeneration != capability.PermitGeneration || record.LaunchNonce != capability.LaunchNonce ||
                record.ExecutableSha256 != capability.ExecutableSha256 || record.ArgumentsSha256 != capability.ArgumentsSha256 ||
                !string.Equals(record.ExecutablePath, capability.ExecutablePath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(record.ExecutablePath, state.MainExecutablePath, StringComparison.OrdinalIgnoreCase) ||
                record.ConsumedUtcTicks < capability.IssuedUtcTicks - TimeSpan.FromSeconds(5).Ticks ||
                record.ConsumedUtcTicks >= capability.ExpiresUtcTicks ||
                record.ProcessStartUtcTicks < record.ConsumedUtcTicks - TimeSpan.FromSeconds(5).Ticks ||
                record.ProcessStartUtcTicks > DateTime.UtcNow.AddSeconds(5).Ticks)
                throw new InvalidDataException("RecoveryReconcileBindingMismatch");
            var process = new RecoveryProcessIdentity
            {
                ProcessId = record.ProcessId, StartUtcTicks = record.ProcessStartUtcTicks,
                ExecutablePath = record.ExecutablePath, BootId = record.ProcessBootId
            };
            if (!process.IsValid()) throw new InvalidDataException("RecoveryReconcileProcessIdentityInvalid");
            // Exited is also a definite result: retain the creation receipt and
            // let the recovery transaction decide whether to make a new attempt.
            if (RecoveryProcessProbe.Observe(process, RecoveryProcessProbe.ReadBootId()) == ProcessObservation.Unknown)
                return false;
            store.RecordLaunchResult(operationId, process, false);
            return true;
        }

        private static string ReadBounded(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                if (stream.Length > 64 * 1024) throw new InvalidDataException("RecoveryReconcileReceiptOversized");
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                    return Encoding.UTF8.GetString(reader.ReadBytes(64 * 1024 + 1));
            }
        }
    }
}
