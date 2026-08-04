using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Controller
{
    /// <summary>
    /// 为 1..12 通道提供并发安全的活动表、缓存表和逐通道原子生命周期操作。
    /// 不同通道可并行，同一通道的创建、激活和移除严格串行。
    /// </summary>
    internal sealed class ChannelRuntimeStore<T> where T : class
    {
        private const int MaximumChannel = 12;
        private readonly object[] _channelGates = Enumerable.Range(0, MaximumChannel + 1)
            .Select(_ => new object())
            .ToArray();

        public ConcurrentDictionary<int, T> Active { get; } = new();
        public ConcurrentDictionary<int, T> Cache { get; } = new();

        public T GetOrCreate(int channel, Func<T> factory, Action<T> activate, out bool created)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            ValidateChannel(channel);
            lock (_channelGates[channel])
            {
                if (!Cache.TryGetValue(channel, out var value) &&
                    !Active.TryGetValue(channel, out value))
                {
                    value = factory() ?? throw new InvalidOperationException(
                        $"Channel runtime factory returned null. Channel={channel}");
                    created = true;
                }
                else
                {
                    created = false;
                }

                Cache[channel] = value;
                Active[channel] = value;
                activate?.Invoke(value);
                return value;
            }
        }

        public IReadOnlyList<T> Remove(int channel, Action<T> deactivate = null)
        {
            ValidateChannel(channel);
            T[] removed;
            lock (_channelGates[channel])
            {
                Active.TryRemove(channel, out var active);
                Cache.TryRemove(channel, out var cached);
                removed = new[] { active, cached }
                    .Where(x => x != null)
                    .Distinct(ReferenceEqualityComparer<T>.Instance)
                    .ToArray();
            }
            // 停止 Timer 等回调型清理不得在通道锁内执行，避免回调收尾重入死锁。
            foreach (var value in removed) deactivate?.Invoke(value);
            return removed;
        }

        public void Clear(Action<T> deactivate = null)
        {
            var channels = Active.Keys.Concat(Cache.Keys).Distinct().OrderBy(x => x).ToArray();
            foreach (var channel in channels)
            {
                if (channel >= 1 && channel <= MaximumChannel)
                    Remove(channel, deactivate);
            }
            Active.Clear();
            Cache.Clear();
        }

        private static void ValidateChannel(int channel)
        {
            if (channel < 1 || channel > MaximumChannel)
                throw new ArgumentOutOfRangeException(nameof(channel), "通道必须在 1～12 范围内。");
        }

        private sealed class ReferenceEqualityComparer<TValue> : IEqualityComparer<TValue>
            where TValue : class
        {
            public static readonly ReferenceEqualityComparer<TValue> Instance = new();
            public bool Equals(TValue x, TValue y) => ReferenceEquals(x, y);
            public int GetHashCode(TValue obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
