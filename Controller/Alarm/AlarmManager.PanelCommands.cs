using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MTTFTest.Watchdog.Protocol;

namespace Controller.Alarm
{
    public sealed partial class AlarmManager
    {
        private readonly string _panelInstanceId = RecoveryProtocolV7.NewId();
        private readonly Dictionary<int, long> _activeVersions = new Dictionary<int, long>();
        private long _panelRevision = 1;
        private string _panelCommandDetail = "仅为报警面板状态，不是断能安全证明";

        private bool CurrentBuzzerOutput()
        {
            lock (_active) return _cfg.Behavior.BuzzerOnAnyAlarm && _buzzerEnabled && _active.Count > 0 && DateTime.UtcNow >= _cooldownUntilUtc;
        }

        public AlarmPanelStatus CapturePanelStatus()
        {
            lock (_active) return new AlarmPanelStatus
            {
                Available = true, PanelInstanceId = _panelInstanceId, Revision = _panelRevision,
                TransportOpen = _client.IsOpen, BuzzerEnabled = _buzzerEnabled,
                ActiveChannels = _active.Where(c => c >= 1 && c <= 12).OrderBy(c => c).ToArray(),
                Detail = _panelCommandDetail
            };
        }

        private void RemoveObservedAlarms(long observedRevision)
        {
            lock (_active)
            {
                foreach (var channel in _activeVersions.Where(p => p.Value <= observedRevision).Select(p => p.Key).ToArray())
                {
                    _active.Remove(channel);
                    _activeVersions.Remove(channel);
                }
                _panelRevision++;
            }
        }

        // Compare the displayed revision after acquiring I/O ownership. New alarms raised
        // during serial writes retain a newer version, including a repeat on the same channel.
        // Neither operation reads or modifies EpbRecords/permanent isolation/actuator state.
        public async Task ExecutePanelCommandAsync(OperatorCommand command, CancellationToken token)
        {
            if (command?.IsStructurallyValid() != true || !AlarmPanelCommand.IsPanelOperation(command.Kind))
                throw new InvalidDataException("AlarmPanelCommandInvalid");
            await _ioGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var payload = command.AlarmPanel;
                lock (_active)
                {
                    if (payload.PanelInstanceId != _panelInstanceId || payload.BaseRevision != _panelRevision)
                        throw new InvalidOperationException("AlarmPanelRevisionConflict");
                }
                if (command.Kind == OperatorCommandKind.AcknowledgeAlarms)
                {
                    if (_cfg.Commands.AllOff.Count == 0) throw new InvalidOperationException("AlarmPanelAllOffMappingMissing");
                    foreach (var output in _cfg.Commands.AllOff.Where(c => c != null).OrderBy(c => c.DeviceId))
                        SendPanelFrame(output.Hex, output.ExpectResponse, token);
                    RemoveObservedAlarms(payload.BaseRevision);
                    // No cooldown in the typed path: a new alarm must not be hidden by acknowledgement.
                    _cooldownUntilUtc = DateTime.MinValue;
                    foreach (var mapping in _epbMap)
                    {
                        bool active;
                        lock (_active) active = _active.Contains(mapping.Key);
                        SendPanelCoil(mapping.Value.DeviceId, mapping.Value.Line, active, token);
                    }
                }
                else
                {
                    lock (_active) { _buzzerEnabled = payload.BuzzerEnabled; _panelRevision++; }
                }
                var buzzer = _cfg.Mappings.Buzzer;
                if (buzzer == null) throw new InvalidOperationException("AlarmPanelBuzzerMappingMissing");
                bool buzzerOn;
                lock (_active) buzzerOn = _cfg.Behavior.BuzzerOnAnyAlarm && _buzzerEnabled && _active.Count > 0;
                SendPanelCoil(buzzer.DeviceId, buzzer.Line, buzzerOn, token);
                lock (_active) _panelCommandDetail = "报警命令已写入串口；不代表面板回读或物理安全证明。硬故障隔离保持不变。";
            }
            catch (Exception ex)
            {
                lock (_active) _panelCommandDetail = "报警命令未完成：" + ex.GetBaseException().Message;
                throw;
            }
            finally { _ioGate.Release(); }
        }

        private void SendPanelCoil(int deviceId, int line, bool on, CancellationToken token)
        {
            if (!_singleCoil.TryGetValue((deviceId, line), out var output)) throw new InvalidOperationException("AlarmPanelCoilMappingMissing");
            SendPanelFrame(on ? output.OnHex : output.OffHex, true, token);
        }

        private void SendPanelFrame(string hex, bool expectResponse, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(hex)) throw new InvalidOperationException("AlarmPanelFrameMissing");
            // The vendor protocol's existing optional response read is not an ACK proof.
            // Unlike the legacy best-effort path, serial write exceptions propagate to the receipt.
            _client.Send(HexToBytes(hex), expectResponse);
            token.ThrowIfCancellationRequested();
        }
    }
}
