using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Controller
{
    internal readonly struct ChannelExecutionPermit
    {
        internal ChannelExecutionPermit(
            int channel,
            long runEpoch,
            long generation,
            bool authorized,
            CancellationToken revocationToken)
        {
            Channel = channel;
            RunEpoch = runEpoch;
            Generation = generation;
            Authorized = authorized;
            RevocationToken = revocationToken;
        }

        internal int Channel { get; }
        internal long RunEpoch { get; }
        internal long Generation { get; }
        internal bool Authorized { get; }
        internal CancellationToken RevocationToken { get; }
    }

    /// <summary>
    ///     每通道执行授权的代际栅栏。终态先递增代际并撤销授权；任何持有旧 permit 的异步
    ///     任务即使晚到，也无法重新创建 Timer/Runner。新试验只能通过 Authorize 取得新代际。
    /// </summary>
    internal sealed class ChannelExecutionFence
    {
        private sealed class Entry
        {
            internal readonly object Gate = new();
            internal long RunEpoch;
            internal long Generation;
            internal bool Authorized;
            internal CancellationTokenSource Revocation = new();
        }

        private readonly ConcurrentDictionary<int, Entry> _entries = new();

        internal ChannelExecutionPermit Authorize(int channel, long runEpoch)
        {
            ValidateChannel(channel);
            if (runEpoch <= 0) throw new ArgumentOutOfRangeException(nameof(runEpoch));
            var entry = _entries.GetOrAdd(channel, _ => new Entry());
            lock (entry.Gate)
            {
                try { entry.Revocation.Cancel(); } catch { }
                entry.Revocation = new CancellationTokenSource();
                entry.Generation++;
                entry.RunEpoch = runEpoch;
                entry.Authorized = true;
                return Snapshot(channel, entry);
            }
        }

        internal ChannelExecutionPermit Revoke(int channel)
        {
            ValidateChannel(channel);
            var entry = _entries.GetOrAdd(channel, _ => new Entry());
            lock (entry.Gate)
            {
                entry.Generation++;
                entry.Authorized = false;
                try { entry.Revocation.Cancel(); } catch { }
                return Snapshot(channel, entry);
            }
        }

        internal ChannelExecutionPermit Capture(int channel)
        {
            ValidateChannel(channel);
            var entry = _entries.GetOrAdd(channel, _ => new Entry());
            lock (entry.Gate)
            {
                return Snapshot(channel, entry);
            }
        }

        internal bool RevokeIfCurrent(ChannelExecutionPermit permit)
        {
            if (permit.Channel < 1 || permit.Channel > 12) return false;
            var entry = _entries.GetOrAdd(permit.Channel, _ => new Entry());
            lock (entry.Gate)
            {
                if (!entry.Authorized || entry.Generation != permit.Generation ||
                    entry.RunEpoch != permit.RunEpoch) return false;
                entry.Generation++;
                entry.Authorized = false;
                try { entry.Revocation.Cancel(); } catch { }
                return true;
            }
        }

        internal bool IsCurrent(ChannelExecutionPermit permit)
        {
            if (!permit.Authorized || permit.Channel < 1 || permit.Channel > 12) return false;
            var current = Capture(permit.Channel);
            return current.Authorized &&
                   current.RunEpoch == permit.RunEpoch &&
                   current.Generation == permit.Generation;
        }

        internal static bool CanCreateRuntime(
            ChannelRuntimeStateChangedEvent state,
            bool enabled,
            bool requiresProcessRestart)
        {
            if (!enabled || requiresProcessRestart || state == null) return false;
            return state.State != ChannelRuntimeState.NotEnabled &&
                   state.State != ChannelRuntimeState.AlarmStopped &&
                   state.State != ChannelRuntimeState.InterlockStopped &&
                   state.State != ChannelRuntimeState.ManualStopped &&
                   state.State != ChannelRuntimeState.Completed &&
                   state.State != ChannelRuntimeState.StartBlocked &&
                   state.State != ChannelRuntimeState.SystemFault;
        }

        private static ChannelExecutionPermit Snapshot(int channel, Entry entry)
        {
            return new ChannelExecutionPermit(
                channel,
                entry.RunEpoch,
                entry.Generation,
                entry.Authorized,
                entry.Revocation.Token);
        }

        private static void ValidateChannel(int channel)
        {
            if (channel < 1 || channel > 12)
                throw new ArgumentOutOfRangeException(nameof(channel));
        }
    }
}
