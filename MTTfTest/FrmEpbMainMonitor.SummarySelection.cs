using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Controller;

namespace MTEmbTest
{
    public partial class FrmEpbMainMonitor
    {
        private bool _synchronizingEpbSummary;

        // Display eligibility only. Never use this policy to authorize control/recovery.
        internal static bool IsEpbSummaryActiveState(ChannelRuntimeState state)
        {
            switch (state)
            {
                case ChannelRuntimeState.Starting:
                case ChannelRuntimeState.Learning:
                case ChannelRuntimeState.Running:
                case ChannelRuntimeState.WarningRunning:
                case ChannelRuntimeState.WaitingForSlotBarrier:
                case ChannelRuntimeState.Recovering:
                case ChannelRuntimeState.ResumeChecking:
                case ChannelRuntimeState.Qualification:
                    return true;
                default:
                    return false;
            }
        }

        internal static bool HoldEpbSummarySelection(
            BatchPauseState batchState, bool starting, bool stopping)
        {
            return starting || stopping ||
                   batchState == BatchPauseState.PausePending ||
                   batchState == BatchPauseState.PauseHolding ||
                   batchState == BatchPauseState.Paused ||
                   batchState == BatchPauseState.Stopping;
        }

        internal static int ResolveEpbSummarySelection(
            int current, IEnumerable<int> activeChannels, bool holdSelection)
        {
            if (holdSelection) return current;
            var active = (activeChannels ?? Enumerable.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12).Distinct().OrderBy(channel => channel).ToArray();
            if (active.Length == 0 || active.Contains(current)) return current;
            return active[0];
        }

        private void SynchronizeEpbSummarySelection()
        {
            if (_synchronizingEpbSummary || IsDisposed || Disposing ||
                comboBoxEditCurrentRecord == null || comboBoxEditCurrentRecord.Properties.Items.Count == 0)
                return;

            // Read the existing live-state cache, not historical counters/status loaded from XML.
            int[] active;
            lock (_channelRuntimeStates)
                active = _channelRuntimeStates.Values
                    .Where(state => IsEpbSummaryActiveState(state.State))
                    .Select(state => state.Channel).ToArray();
            var hold = HoldEpbSummarySelection(
                _epb?.CurrentBatchPauseState ?? BatchPauseState.Idle,
                Volatile.Read(ref _batchStartUiGuard) != 0,
                Volatile.Read(ref _stopUiGuard) != 0 || MonitorLifecycle == EpbMonitorLifecycle.Stopping);
            var next = ResolveEpbSummarySelection(_currentEpbSummaryChannel, active, hold);
            if (next == _currentEpbSummaryChannel) return;

            _synchronizingEpbSummary = true;
            try { SelectEpbSummaryChannel(next); }
            finally { _synchronizingEpbSummary = false; }
        }
    }
}
