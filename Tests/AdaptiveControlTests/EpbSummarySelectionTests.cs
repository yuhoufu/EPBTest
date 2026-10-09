using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Config;
using Controller;
using MTEmbTest;

namespace AdaptiveControlTests
{
    internal static class EpbSummarySelectionTests
    {
        internal static int RunAll()
        {
            var activeStates = new[] { ChannelRuntimeState.Starting, ChannelRuntimeState.Learning,
                ChannelRuntimeState.Running, ChannelRuntimeState.WarningRunning, ChannelRuntimeState.Recovering,
                ChannelRuntimeState.ResumeChecking, ChannelRuntimeState.Qualification, ChannelRuntimeState.WaitingForSlotBarrier };
            foreach (ChannelRuntimeState state in Enum.GetValues(typeof(ChannelRuntimeState)))
                Check(FrmEpbMainMonitor.IsEpbSummaryActiveState(state) == activeStates.Contains(state), "Eligibility: " + state);
            Check(Resolve(0, 9, 7, 8) == 7, "Initial selection uses lowest live channel");
            Check(Resolve(9, 7, 8, 9) == 9, "Manual choice stays despite lower active channels");
            Check(Resolve(9, 7, 8) == 7, "Stopped/paused/completed selection falls back");
            Check(Resolve(9) == 9, "No active channel preserves current display");
            Check(Resolve(0) == 0, "Empty startup does not invent a channel");
            Check(Resolve(9, 0, 13, 8, 7, 7) == 7, "Invalid channels cannot become display targets");
            Check(FrmEpbMainMonitor.ResolveEpbSummarySelection(9, new[] { 7 }, true) == 9, "Batch transition retains selection");
            foreach (var state in new[] { BatchPauseState.PausePending, BatchPauseState.PauseHolding,
                BatchPauseState.Paused, BatchPauseState.Stopping })
                Check(FrmEpbMainMonitor.HoldEpbSummarySelection(state, false, false), "Batch hold: " + state);
            Check(!FrmEpbMainMonitor.HoldEpbSummarySelection(BatchPauseState.Running, false, false), "Settled running can reconcile");
            Check(FrmEpbMainMonitor.HoldEpbSummarySelection(BatchPauseState.Running, true, false), "Start guard");
            Check(FrmEpbMainMonitor.HoldEpbSummarySelection(BatchPauseState.Running, false, true), "Stop guard");

            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { RealSummaryControls(); }
                catch (Exception error) { failure = error; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            if (!thread.Join(15000)) throw new TimeoutException("Summary controls did not finish");
            if (failure != null) throw new InvalidOperationException("Summary UI regression", failure);
            Console.WriteLine("PASS summary selection policy and real WinForms controls 1/1");
            return 1;
        }

        private static int Resolve(int current, params int[] active) =>
            FrmEpbMainMonitor.ResolveEpbSummarySelection(current, active, false);

        private static void RealSummaryControls()
        {
            // Do not Show/Load: no controller, hardware, Watchdog binding, or project persistence is initialized.
            using (var form = new FrmEpbMainMonitor(new DaqRuntimeSettings(2000, 20)))
            {
                var records = Enumerable.Range(1, 12).Select(id => EpbTestRecord.CreateDefault(id, 100)).ToList();
                foreach (var record in records) { record.Enabled = record.Id >= 7 && record.Id <= 9; record.RunCount = record.Id; }
                Set(form, "_uiEpbRecords", records);
                var states = (Dictionary<int, ChannelRuntimeStateChangedEvent>)Get(form, "_channelRuntimeStates");
                Action<int, ChannelRuntimeState> state = (channel, value) => states[channel] =
                    new ChannelRuntimeStateChangedEvent { Channel = channel, State = value };
                var combo = Get(form, "comboBoxEditCurrentRecord");
                Action<int> select = channel => combo.GetType().GetProperty("SelectedItem").SetValue(combo, "EPB-" + channel);
                Action<int> expect = channel => Check((int)Get(form, "_currentEpbSummaryChannel") == channel &&
                    (string)combo.GetType().GetProperty("SelectedItem").GetValue(combo) == "EPB-" + channel, "Selected channel must be " + channel);
                Action refresh = () => Call(form, "SynchronizeEpbSummarySelection");
                var originalCounts = records.Select(record => record.RunCount).ToArray();
                var originalEnabled = records.Select(record => record.Enabled).ToArray();
                var originalStatuses = records.Select(record => record.Status).ToArray();

                state(7, ChannelRuntimeState.Running); state(8, ChannelRuntimeState.Learning); state(9, ChannelRuntimeState.Running);
                Call(form, "InitEpbSummaryPanel"); expect(7); // Historical lower counters must not win.
                select(9); expect(9);
                state(7, ChannelRuntimeState.Paused); Call(form, "RefreshCurrentEpbSummary", 7); expect(9);
                state(7, ChannelRuntimeState.Running); Call(form, "RefreshCurrentEpbSummary", 7); expect(9);
                select(4); expect(7); // Manual selection of an inactive channel returns to lowest live channel.
                select(9); state(9, ChannelRuntimeState.Recovering); refresh(); expect(9);
                state(9, ChannelRuntimeState.PausePending); refresh(); expect(7);
                state(9, ChannelRuntimeState.Running); select(9);
                state(9, ChannelRuntimeState.Completed); Call(form, "RefreshCurrentEpbSummary", 9); expect(7);
                state(7, ChannelRuntimeState.Completed); refresh(); expect(8);
                state(8, ChannelRuntimeState.Completed); refresh(); expect(8);

                state(7, ChannelRuntimeState.Running); state(8, ChannelRuntimeState.Running); state(9, ChannelRuntimeState.Running);
                select(9);
                Set(form, "_stopUiGuard", 1);
                state(9, ChannelRuntimeState.ManualStopped); refresh(); expect(9);
                state(7, ChannelRuntimeState.ManualStopped); state(8, ChannelRuntimeState.ManualStopped);
                Set(form, "_stopUiGuard", 0); refresh(); expect(9);
                Set(form, "_batchStartUiGuard", 1);
                state(7, ChannelRuntimeState.Starting); refresh(); expect(9);
                state(9, ChannelRuntimeState.Qualification); Set(form, "_batchStartUiGuard", 0); refresh(); expect(9);
                state(7, ChannelRuntimeState.Paused); state(9, ChannelRuntimeState.Paused); refresh(); expect(9);
                state(8, ChannelRuntimeState.ResumeChecking); refresh(); expect(8);
                state(7, ChannelRuntimeState.Running); refresh(); expect(8);

                // Exercise the actual idle/redraw timer hook with no new waveform data.
                Call(form, "StartUiRedrawTimer");
                using (var timer = (System.Windows.Forms.Timer)Get(form, "_uiTimer"))
                {
                    Set(form, "_dirtyForRedraw", false);
                    Action tick = () => typeof(System.Windows.Forms.Timer)
                        .GetMethod("OnTick", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(timer, new object[] { EventArgs.Empty });
                    Set(form, "_batchStartUiGuard", 1);
                    state(8, ChannelRuntimeState.ManualStopped); tick(); expect(8);
                    Set(form, "_batchStartUiGuard", 0); tick(); expect(7);
                    var led = Get(form, "LedRunCycles");
                    Check((string)led.GetType().GetProperty("Text").GetValue(led) == "7", "Selected channel counter was not refreshed");
                }

                Check(Get(form, "_epb") == null, "Display changes must not construct a controller");
                Check(records.Select(record => record.RunCount).SequenceEqual(originalCounts), "Counters changed");
                Check(records.Select(record => record.Enabled).SequenceEqual(originalEnabled), "Enabled flags changed");
                Check(records.Select(record => record.Status).SequenceEqual(originalStatuses), "Control statuses changed");
            }
        }

        private static object Get(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        private static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        private static void Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, args);
        private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
