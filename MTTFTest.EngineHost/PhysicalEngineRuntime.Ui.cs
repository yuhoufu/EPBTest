using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Config;
using Controller;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.EngineHost
{
    internal interface IEngineUiSource
    {
        EngineUiSnapshot CaptureUiSnapshot();
    }

    internal static class EngineUiSnapshotFactory
    {
        internal static EngineUiSnapshot Empty(string detail) => new EngineUiSnapshot
        {
            StatusDetail = detail,
            Capabilities = new[] { EngineUiContract.Monitor },
            Channels = Enumerable.Range(1, 12).Select(channel => new EngineUiChannel { Channel = channel }).ToArray(),
            PowerSupplies = Enumerable.Range(1, 4).Select(group => new EngineUiPowerSupply { Group = group }).ToArray(),
            Pressures = new[] { new UiMeasurement(), new UiMeasurement() }
        };
    }

    internal sealed partial class PhysicalEngineRuntime
    {
        public int ChannelPauseMask => CaptureManualChannelMask(false);
        public int ChannelResumeMask => CaptureManualChannelMask(true);

        private int CaptureManualChannelMask(bool resume)
        {
            var manager = _manager;
            if (!Initialized || manager == null || !manager.IsBatchSessionActive || manager.CurrentBatchPauseState != BatchPauseState.Running) return 0;
            var mask = 0;
            foreach (var state in manager.GetChannelRuntimeStates())
                if (!state.PermanentAlarmLatched && state.Channel >= 1 && state.Channel <= 12 &&
                    (resume ? state.State == ChannelRuntimeState.Paused && (_manualBatch.ChannelMask & (1 << (state.Channel - 1))) != 0 :
                        state.State == ChannelRuntimeState.Running || state.State == ChannelRuntimeState.WarningRunning))
                    mask |= 1 << (state.Channel - 1);
            return mask;
        }

        private async System.Threading.Tasks.Task PersistManualChannelBoundaryAsync(System.Threading.CancellationToken token)
        {
            await FlushProgressAsync(token).ConfigureAwait(false);
            // Counts are projected before acknowledging the operation. A manual
            // interruption breaks budget stability; it never migrates a permit.
            _checkpointStore.Update(_identity, value =>
            {
                value.FormalCyclesSinceRecovery = 0; value.StableSinceUtcTicks = 0;
                return true;
            }, "ManualChannelBoundary;HistoricalCountsPreserved");
        }
        private GlobalConfig _displayConfig;
        private EngineDaqConfigurationStore _daqConfigurationStore;
        private EngineAoConfigurationStore _aoConfigurationStore;
        private EngineAoConfigurationSnapshot _aoConfiguration;
        private TestConfigurationCommit _daqConfiguration;
        private EngineUiCurveBuffer _uiCurves = new EngineUiCurveBuffer();
        private Dictionary<string, string[]> _uiRoutes = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        private double _uiSampleRate;
        private UiMeasurement _uiForce = new UiMeasurement();
        private readonly ConcurrentDictionary<int, EngineUiPowerSupply> _uiPower =
            new ConcurrentDictionary<int, EngineUiPowerSupply>();

        private void RefreshTestConfiguration()
        {
            _configurationRevision = _testConfigurationStore.ReadRevision();
            _testConfiguration = EngineTestConfigurationStore.Capture(_config.Test);
            RefreshProjectSelection();
        }

        private EngineUiProjectSelection _projectSelectionInfo;
        private void RefreshProjectSelection()
        {
            // Only the serialized composition/stop/configuration executor reads disk.
            // Display polling clones this immutable cache and cannot delay acquisition.
            var selection = _projectSelectionStore.ReadActive(_identity);
            _projectSelectionInfo = selection == null ? null : new EngineUiProjectSelection
            {
                Revision = selection.Revision, ConfigurationPath = selection.ConfigurationPath,
                LastResetArchivePath = selection.LastResetArchivePath ?? string.Empty,
                ProjectFileSha256 = EngineProjectSelectionStore.Sha(EngineProjectSelectionStore.ReadBytes(
                    selection.ConfigurationPath, EngineProjectSelectionStore.MaximumProjectBytes))
            };
        }

        private void ConfigureUiRoutes(AiConfigDetail ai, double sampleRate)
        {
            _uiCurves = new EngineUiCurveBuffer((int)Math.Max(60, Math.Min(1200, Math.Ceiling(_config.Test.TestPeriod * 2))));
            _uiSampleRate = sampleRate;
            _uiRoutes = ai.Enabled().GroupBy(record => record.物理通道.Split('/')[0], StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Select(record =>
                {
                    if (record.参数名 == "Force") return "F";
                    if (record.参数名 == "Pressure_1") return "P1";
                    if (record.参数名 == "Pressure_2") return "P2";
                    if (record.参数名 != null && record.参数名.StartsWith("EPB", StringComparison.Ordinal) &&
                        record.参数名.EndsWith("_current", StringComparison.Ordinal) &&
                        int.TryParse(record.参数名.Substring(3).Replace("_current", ""), out var channel) && channel >= 1 && channel <= 12)
                        return "A" + channel;
                    return string.Empty;
                }).ToArray(), StringComparer.OrdinalIgnoreCase);
        }

        private void OnUiEngineeringBatch(string device, double[,] values, DateTime current, DateTime last)
        {
            if (!_uiRoutes.TryGetValue(device, out var routes) || values == null) return;
            var buffer = _uiCurves;
            for (var row = 0; row < Math.Min(routes.Length, values.GetLength(0)); row++)
            {
                if (routes[row].Length > 0)
                    buffer.TryAppend(routes[row], values, row, current.ToUniversalTime().Ticks, _uiSampleRate);
                if (routes[row] == "F" && values.GetLength(1) > 0)
                {
                    var force = values[row, values.GetLength(1) - 1];
                    _uiForce = new UiMeasurement { Valid = EngineUiContract.IsFinite(force),
                        Value = EngineUiContract.IsFinite(force) ? force : 0,
                        CapturedUtcTicks = current.ToUniversalTime().Ticks, Reason = "" };
                }
            }
        }

        private void OnUiPowerTelemetry(PowerSupplyTelemetry telemetry)
        {
            if (telemetry == null || telemetry.ElectricalGroupId < 1 || telemetry.ElectricalGroupId > 4) return;
            var value = telemetry.Snapshot;
            _uiPower[telemetry.ElectricalGroupId] = new EngineUiPowerSupply
            {
                Group = telemetry.ElectricalGroupId,
                Valid = value != null && string.IsNullOrEmpty(telemetry.Error) &&
                    EngineUiContract.IsFinite(value.MeasuredVoltage) && EngineUiContract.IsFinite(value.MeasuredCurrent),
                Connected = value?.IsConnected == true,
                OutputEnabled = value?.OutputEnabled == true,
                Mode = value == null ? "未就绪" : value.IsConstantCurrent ? "CC" : value.IsConstantVoltage ? "CV" : "—",
                Voltage = value != null && EngineUiContract.IsFinite(value.MeasuredVoltage) ? value.MeasuredVoltage : 0,
                Current = value != null && EngineUiContract.IsFinite(value.MeasuredCurrent) ? value.MeasuredCurrent : 0,
                CapturedUtcTicks = telemetry.TimestampUtc.ToUniversalTime().Ticks
            };
        }

        // Reads cached facts only. Never performs a DAQ read, SCPI query, config write or recovery action.
        public EngineUiSnapshot CaptureUiSnapshot()
        {
            var result = EngineUiSnapshotFactory.Empty(string.Empty);
            var ao = _aoConfiguration;
            if (ao?.Configuration?.IsStructurallyValid() == true)
            {
                result.AoConfiguration = ao.Configuration.Clone();
                result.AoConfigurationRevision = ao.Revision;
                result.AoConfigurationSha256 = ao.Sha256;
            }
            var daq = _daqConfiguration;
            if (daq?.IsStructurallyValid() == true)
            {
                result.DaqConfiguration = daq.DaqConfiguration.Clone();
                result.DaqConfigurationRevision = daq.BaseConfigurationRevision;
                result.DaqConfigurationSha256 = daq.BaseConfigurationSha256;
            }
            if (_testConfiguration?.IsStructurallyValid() == true)
            {
                result.TestConfiguration = _testConfiguration.Clone();
                result.ConfigurationRevision = _configurationRevision;
                result.ConfigurationSha256 = result.TestConfiguration.ComputeSha256();
                result.Capabilities = new[] { EngineUiContract.Monitor, EngineUiContract.TestConfiguration };
                result.ProjectSelection = _projectSelectionInfo?.Clone();
                if (result.ProjectSelection != null)
                    result.Capabilities = result.Capabilities.Concat(new[] { EngineUiContract.ProjectSwitch, EngineUiContract.ProjectCreation, EngineUiContract.ProjectReset }).ToArray();
            }
            var config = _displayConfig?.Test;
            if (result.DaqConfiguration != null)
                result.Capabilities = result.Capabilities.Concat(new[] { EngineUiContract.DaqConfiguration }).ToArray();
            if (result.AoConfiguration != null && _maintenanceAuthority != null)
                result.Capabilities = result.Capabilities.Concat(new[] { EngineUiContract.PressureMaintenance, EngineUiContract.AoCalibration }).ToArray();
            if (_manager != null && Initialized)
            {
                result.Capabilities = result.Capabilities.Concat(new[] { EngineUiContract.ManualBatchControl,
                    EngineUiContract.ManualChannelControl, EngineUiContract.ChannelQualificationRecovery }).ToArray();
                result.BatchPauseAvailable = _manager.IsBatchSessionActive && _manager.CurrentBatchPauseState == BatchPauseState.Running;
                result.BatchResumeAvailable = _manualBatch.IsPaused && _manager.CurrentBatchPauseState == BatchPauseState.Paused;
            }
            var alarmPanel = _alarmPanel;
            if (alarmPanel != null)
            {
                result.AlarmPanel = alarmPanel.CapturePanelStatus();
                result.Capabilities = result.Capabilities.Concat(new[] { EngineUiContract.AlarmCommands }).ToArray();
            }
            if (config != null)
            {
                result.TestName = config.TestName;
                result.PeriodSeconds = config.TestPeriod;
                result.TargetCycles = config.TestTarget;
                result.SharedTargetCycles = config.IsSameCycleForAllEpb;
            }
            var states = _manager?.GetChannelRuntimeStates();
            var records = config?.EpbRecords?.Snapshot();
            var acquirer = _acquirer;
            var progress = _progressPublisher?.Snapshot();
            var checkpointIsolations = _checkpointStore?.Snapshot()?.IsolatedResources ?? Array.Empty<string>();
            foreach (var channel in result.Channels)
            {
                var record = records?.FirstOrDefault(r => r.Id == channel.Channel);
                if (record != null)
                {
                    channel.Selected = record.Enabled;
                    channel.Isolated = record.PermanentAlarmLatched;
                    channel.FormalCycles = Math.Max(0, record.RunCount);
                    channel.MechanicalCycles = Math.Max(0, record.EffectiveMechanicalCycleCount);
                    channel.RemainingCycles = record.GetRemainingMechanicalCycles(config.TestTarget);
                    channel.RunTimeTicks = Math.Max(0, record.RunTimeSpan.Ticks);
                    channel.CountsValid = _progressPublisher?.Healthy == true || _releasedForSafety || _maintenanceDataBoundaryClosed;
                    channel.Reason = record.PermanentAlarmReason;
                    channel.State = record.Enabled ? "待机" : "未启用";
                }
                var durable = progress?.FirstOrDefault(row => row.Channel == channel.Channel);
                if (durable != null)
                {
                    channel.FormalCycles = durable.FormalCompleted;
                    channel.MechanicalCycles = durable.MechanicalCompleted;
                    channel.RunTimeTicks = durable.RunTimeTicks;
                    var target = record?.TotalCount > 0 ? record.TotalCount : config?.TestTarget ?? 0;
                    channel.RemainingCycles = (int)Math.Max(0L, target - durable.MechanicalCompleted);
                }
                var state = states?.FirstOrDefault(s => s.Channel == channel.Channel);
                if (state != null)
                {
                    channel.State = ChannelText(state.State);
                    channel.Running = state.State == ChannelRuntimeState.Running ||
                        state.State == ChannelRuntimeState.WarningRunning;
                    channel.Reason = state.ReasonText;
                    channel.Isolated |= state.PermanentAlarmLatched;
                }
                channel.Isolated |= checkpointIsolations.Contains("Channel:" + channel.Channel, StringComparer.OrdinalIgnoreCase);
                if (acquirer != null)
                {
                    var sample = acquirer.ReadCurrentFastSample(channel.Channel);
                    channel.Current = new UiMeasurement
                    {
                        Valid = sample.IsFreshAndUsable(3000),
                        Value = EngineUiContract.IsFinite(sample.Sample.CurrentA) ? sample.Sample.CurrentA : 0,
                        CapturedUtcTicks = sample.Available ? sample.Sample.SampleUtc.Ticks : 0,
                        Reason = sample.IsFreshAndUsable(3000) ? string.Empty : "采样未就绪／已失效"
                    };
                }
            }
            for (var index = 0; index < 2; index++)
            {
                if (acquirer == null) continue;
                var sample = acquirer.ReadPressureSample(index + 1);
                result.Pressures[index] = new UiMeasurement
                {
                    Valid = sample.IsFinite && sample.AgeMs >= 0 && sample.AgeMs <= 3000,
                    Value = sample.IsFinite ? sample.ValueBar : 0,
                    CapturedUtcTicks = sample.TimestampUtc.Ticks,
                    Reason = sample.IsFinite ? string.Empty : "压力采样未就绪"
                };
            }
            result.PowerSupplies = result.PowerSupplies.Select(p =>
                _uiPower.TryGetValue(p.Group, out var observed) ? observed : p).ToArray();
            result.Curves = _uiCurves.Snapshot();
            result.CurveWindowSeconds = _uiCurves.WindowSeconds;
            result.Force = _uiForce;
            return result;
        }

        private static string ChannelText(ChannelRuntimeState state)
        {
            switch (state)
            {
                case ChannelRuntimeState.NotEnabled: return "未启用";
                case ChannelRuntimeState.Starting: return "启动中";
                case ChannelRuntimeState.Learning: return "自学习";
                case ChannelRuntimeState.Running: return "运行中";
                case ChannelRuntimeState.WarningRunning: return "预警运行";
                case ChannelRuntimeState.ManualStopped: return "已停止";
                case ChannelRuntimeState.Completed: return "已完成";
                case ChannelRuntimeState.StartBlocked: return "启动受阻";
                case ChannelRuntimeState.Recovering: return "系统自恢复";
                case ChannelRuntimeState.Qualification: return "资格复核";
                case ChannelRuntimeState.InterlockStopped: return "联锁停机";
                case ChannelRuntimeState.AlarmStopped: return "报警停机";
                case ChannelRuntimeState.SystemFault: return "系统故障";
                case ChannelRuntimeState.PausePending: return "暂停中";
                case ChannelRuntimeState.ResumeChecking: return "恢复检查";
                case ChannelRuntimeState.WaitingForSlotBarrier: return "等待同步";
                default: return "已暂停";
            }
        }
    }
}
