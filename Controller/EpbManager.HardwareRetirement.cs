using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using IO.NI;

namespace Controller
{
    public partial class EpbManager
    {
        /// <summary>
        /// 汇总旧宿主实际退出事实。Legacy ReleaseHardwareForRestart 的 state=2 仅表示
        /// 释放函数已经返回，不能代替 NI、电源、后台任务和逻辑边界的分别确认。
        /// </summary>
        public HardwareReleaseSnapshot CaptureHardwareReleaseForHost()
        {
            var state = Volatile.Read(ref _hardwareReleaseState);
            if (state != 2) return new HardwareReleaseSnapshot(state != 0, false, false, 0, string.Empty);
            var resources = new List<HardwareReleaseSnapshot>();
            if (_acq != null) resources.Add(_acq.CaptureReleaseEvidence());
            if (_ao != null) resources.Add(_ao.CaptureReleaseEvidence());
            if (_do != null) resources.Add(_do.CaptureReleaseEvidence());
            if (_powerSupply is IPowerSupplyHardwareRetirement power)
                resources.Add(power.CaptureHardwareRelease());
            else if (_powerSupply != null)
                return new HardwareReleaseSnapshot(true, false, false, 0, "PowerCoordinatorRetirementEvidenceMissing");

            var backgroundCount = _taskSupervisor.Snapshot().Length +
                                  (_hydCoordinator?.PendingBackgroundTaskCount ?? 0);
            var logicalQuiescent = false;
            // 快照不得被一个卡住的恢复 owner 锁无限挂起。
            var entered = false;
            try
            {
                Monitor.TryEnter(_recoveryContractGate, 0, ref entered);
                if (entered) logicalQuiescent = CaptureLogicalQuiescenceSnapshotLocked().IsQuiescent;
            }
            finally { if (entered) Monitor.Exit(_recoveryContractGate); }
            return new HardwareReleaseSnapshot(true,
                resources.All(resource => resource.NativeResourcesReleased),
                logicalQuiescent && backgroundCount == 0 && resources.All(resource => resource.CallbacksIsolated),
                backgroundCount + resources.Sum(resource => resource.PendingCallbacks),
                resources.Select(resource => resource.Failure).FirstOrDefault(failure => failure.Length != 0));
        }
    }
}
