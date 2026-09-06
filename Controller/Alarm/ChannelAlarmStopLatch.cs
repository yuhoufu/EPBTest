using System.Collections.Concurrent;

namespace Controller.Alarm
{
    /// <summary>
    ///     通道级报警停机锁存。
    ///     同一次运行中只允许第一个报警触发停机；开始新运行时必须显式复位。
    /// </summary>
    internal sealed class ChannelAlarmStopLatch
    {
        private readonly ConcurrentDictionary<int, byte> _requested = new();

        public bool IsStopRequested(int channel)
        {
            return _requested.ContainsKey(channel);
        }

        public bool TryRequestStop(int channel)
        {
            return _requested.TryAdd(channel, 0);
        }

        public void BeginRun(int channel)
        {
            _requested.TryRemove(channel, out _);
        }
    }
}
