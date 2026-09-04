using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Config;
using IO.NI;
using PowerSupply.Core;

namespace Controller
{
    public interface IPowerSupplyHardwareRetirement
    {
        HardwareReleaseSnapshot CaptureHardwareRelease();
    }

    public sealed partial class PowerSupplyCoordinator : IPowerSupplyHardwareRetirement
    {
        private readonly object _retirementGate = new object();
        private readonly HardwareReleaseEvidence _releaseEvidence = new HardwareReleaseEvidence();
        private readonly Dictionary<int, IPswClient> _retiringClients = new Dictionary<int, IPswClient>();
        private readonly Dictionary<int, GroupOperationState> _retiringOperations = new Dictionary<int, GroupOperationState>();

        public HardwareReleaseSnapshot CaptureHardwareRelease()
        {
            var local = _releaseEvidence.Capture();
            if (!local.NativeResourcesReleased) return local;
            // Dispose 后客户端表不再允许创建或替换。保留原实例，不能清表后把空表
            // 当成已经释放；客户端超时后尚未结束的底层 I/O 仍必须包含在证明内。
            var clients = CaptureOwnedClients().Select(client =>
                (client as IPswClientRetirementEvidence)?.CaptureRetirement()).ToArray();
            var missing = clients.Any(client => client == null);
            var failure = missing ? "PowerClientRetirementEvidenceMissing" :
                clients.Select(client => client.Failure).FirstOrDefault(value => value.Length != 0) ?? string.Empty;
            return new HardwareReleaseSnapshot(local.ReleaseRequested,
                !missing && clients.All(client => client.RetirementRequested && client.TransportReleased &&
                    client.PendingTransportTasks == 0),
                local.CallbacksIsolated && _tasks.Snapshot().Length == 0 &&
                !missing && clients.All(client => client.RetirementRequested && client.OperationsExited &&
                    client.PendingOperations == 0 && client.PendingTransportTasks == 0),
                local.PendingCallbacks + clients.Where(client => client != null)
                    .Sum(client => client.PendingOperations + client.PendingTransportTasks), failure);
        }

        private IDisposable RegisterPowerActivity()
        {
            lock (_retirementGate)
            {
                ThrowIfDisposed();
                return _releaseEvidence.RegisterCallback();
            }
        }

        private IPswClient GetOrCreateClient(int groupId, PowerSupplyDeviceConfig supply)
        {
            lock (_retirementGate)
            {
                ThrowIfDisposed();
                if (_clients.TryGetValue(groupId, out var client)) return client;
                if (_retiringClients.TryGetValue(groupId, out var retiring))
                {
                    if ((retiring as IPswClientRetirementEvidence)?.CaptureRetirement().FullyReleased != true)
                        throw new InvalidOperationException("PreviousPowerClientStillOwnsResource");
                    _retiringClients.Remove(groupId);
                }
                client = _clientFactory(supply);
                if (client == null) throw new InvalidOperationException("PowerClientFactoryReturnedNull");
                _clients[groupId] = client;
                return client;
            }
        }

        private bool BeginHardwareRetirement()
        {
            lock (_retirementGate)
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return false;
                _releaseEvidence.RequestRelease();
                _releaseEvidence.CloseCallbackAdmission();
                return true;
            }
        }

        private IPswClient[] CaptureOwnedClients()
        {
            lock (_retirementGate)
                return _clients.Values.Concat(_retiringClients.Values).Distinct().ToArray();
        }

        private GroupOperationState[] CaptureOwnedOperations()
        {
            lock (_retirementGate)
                return _operations.Values.Concat(_retiringOperations.Values).Distinct().ToArray();
        }
    }
}
