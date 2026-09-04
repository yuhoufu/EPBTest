using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Config;
using Controller.Alarm;
using MTTFTest.Watchdog.Protocol;

namespace AdaptiveControlTests
{
    internal static class AlarmPanelCommandTests
    {
        internal static int RunAll()
        {
            Check("报警确认仅清除已观察事实", panel =>
            {
                panel.SetAlarmAsync(4, true).GetAwaiter().GetResult();
                panel.ExecutePanelCommandAsync(Command(panel), CancellationToken.None).GetAwaiter().GetResult();
                Assert(panel.CapturePanelStatus().ActiveChannels.Length == 0);
            });
            using (var transport = new Transport())
            using (var panel = new AlarmManager(Configuration(), NullLogger.Instance, transport))
            {
                panel.SetAlarmAsync(4, true).GetAwaiter().GetResult();
                Task late = null;
                transport.OnSend = frame =>
                {
                    if (frame[0] != 255) return;
                    transport.OnSend = null;
                    late = panel.SetAlarmAsync(4, true, "new occurrence during acknowledgement");
                };
                panel.ExecutePanelCommandAsync(Command(panel), CancellationToken.None).GetAwaiter().GetResult();
                late?.GetAwaiter().GetResult();
                Assert(panel.CapturePanelStatus().ActiveChannels.SequenceEqual(new[] { 4 }));
                Console.WriteLine("PASS 同通道新报警不被在途确认清除");
            }
            using (var transport = new Transport())
            using (var panel = new AlarmManager(Configuration(), NullLogger.Instance, transport))
            {
                panel.SetAlarmAsync(4, true).GetAwaiter().GetResult();
                Task late = null;
                transport.OnSend = frame =>
                {
                    if (frame[0] != 255) return;
                    transport.OnSend = null; late = panel.SetAlarmAsync(5, true);
                };
                panel.ClearAllAsync().GetAwaiter().GetResult();
                late?.GetAwaiter().GetResult();
                Assert(panel.CapturePanelStatus().ActiveChannels.SequenceEqual(new[] { 5 }));
                Console.WriteLine("PASS 旧批量确认也保留并发新报警");
            }
            Check("过期报警 revision 拒绝", panel =>
            {
                var command = Command(panel);
                panel.SetAlarmAsync(4, true).GetAwaiter().GetResult();
                ExpectFailure(() => panel.ExecutePanelCommandAsync(command, CancellationToken.None).GetAwaiter().GetResult());
                Assert(panel.IsAnyAlarmActive());
                command = Command(panel); command.AlarmPanel.PanelInstanceId = RecoveryProtocolV7.NewId();
                command.PayloadSha256 = command.AlarmPanel.ComputeSha256();
                ExpectFailure(() => panel.ExecutePanelCommandAsync(command, CancellationToken.None).GetAwaiter().GetResult());
            });
            Check("消音不清除报警事实", panel =>
            {
                panel.SetAlarmAsync(4, true).GetAwaiter().GetResult();
                var command = Command(panel); command.Kind = OperatorCommandKind.SetBuzzerEnabled;
                panel.ExecutePanelCommandAsync(command, CancellationToken.None).GetAwaiter().GetResult();
                Assert(!panel.BuzzerEnabled && panel.IsAnyAlarmActive());
            });
            using (var transport = new Transport())
            using (var panel = new AlarmManager(Configuration(), NullLogger.Instance, transport))
            {
                transport.OnSend = _ => throw new IOException("synthetic serial failure");
                ExpectFailure(() => panel.ExecutePanelCommandAsync(Command(panel), CancellationToken.None).GetAwaiter().GetResult());
                Assert(panel.CapturePanelStatus().Detail.Contains("synthetic serial failure"));
                Console.WriteLine("PASS 串口写失败不能回报成功");
            }
            using (var transport = new Transport())
            using (var panel = new AlarmManager(Configuration(), NullLogger.Instance, transport))
            using (var cancellation = new CancellationTokenSource())
            {
                transport.OnSend = _ => cancellation.Cancel();
                ExpectFailure(() => panel.ExecutePanelCommandAsync(Command(panel), cancellation.Token).GetAwaiter().GetResult());
                Assert(transport.Frames.Count == 1);
                Console.WriteLine("PASS 报警命令取消后不继续发送剩余帧");
            }
            return 7;
        }

        private static void Check(string name, Action<AlarmManager> action)
        {
            using (var transport = new Transport())
            using (var panel = new AlarmManager(Configuration(), NullLogger.Instance, transport)) action(panel);
            Console.WriteLine("PASS " + name);
        }
        private static void ExpectFailure(Action action)
        {
            var failed = false; try { action(); } catch { failed = true; } Assert(failed);
        }
        private static void Assert(bool value) { if (!value) throw new Exception("Alarm panel assertion failed"); }
        private static OperatorCommand Command(AlarmManager panel)
        {
            var state = panel.CapturePanelStatus();
            var payload = new AlarmPanelCommand { PanelInstanceId = state.PanelInstanceId, BaseRevision = state.Revision };
            return new OperatorCommand { CommandId = RecoveryProtocolV7.NewId(), SessionId = RecoveryProtocolV7.NewId(),
                RunId = RecoveryProtocolV7.NewId(), RunEpoch = 1, Kind = OperatorCommandKind.AcknowledgeAlarms,
                AlarmPanel = payload, PayloadSha256 = payload.ComputeSha256(), IssuedUtcTicks = DateTime.UtcNow.Ticks };
        }
        private static AlarmConfig Configuration()
        {
            var config = new AlarmConfig();
            config.Behavior.BuzzerDebounceMs = config.Behavior.RearmDelayMs = config.Behavior.Retry = 0;
            config.Commands.AllOff.Add(new AlarmAllOffCommand { DeviceId = 1, Hex = "FF 00" });
            foreach (var channel in new[] { 4, 5 })
            {
                config.Mappings.Epb.Add(new AlarmEpbMapping { Channel = channel, DeviceId = 1, Line = channel });
                config.Commands.SingleCoil.Add(new AlarmSingleCoilCommand { DeviceId = 1, Line = channel,
                    OnHex = "0" + channel + " 01", OffHex = "0" + channel + " 00" });
            }
            config.Mappings.Buzzer = new AlarmBuzzerMapping { DeviceId = 1, Line = 6 };
            config.Commands.SingleCoil.Add(new AlarmSingleCoilCommand { DeviceId = 1, Line = 6, OnHex = "06 01", OffHex = "06 00" });
            return config;
        }
        private sealed class Transport : IAlarmPanelTransport
        {
            public bool IsOpen { get; private set; }
            internal Action<byte[]> OnSend;
            internal readonly List<byte[]> Frames = new List<byte[]>();
            public void Open() { IsOpen = true; }
            public void Send(byte[] frame, bool expectResponse) { Frames.Add(frame.ToArray()); OnSend?.Invoke(frame); }
            public void Dispose() { IsOpen = false; }
        }
    }
}
