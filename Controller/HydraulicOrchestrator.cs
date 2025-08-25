using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Config;
using IO.NI;
using IAppLogger = Config.IAppLogger;
using NullLogger = Config.NullLogger;

namespace Controller
{
    
    
    /// <summary>
    /// 液压系统协调器。负责管理多个电控通道对液压资源的并发占用和释放。
    /// 核心逻辑：对同一个液压组（Hydraulic）的首次占用触发建压并保持，该组所有占用都释放后，才执行统一释压。
    /// 此机制确保了液压压力在测试过程中持续稳定，避免因单个电控通道完成就释压而影响同组其他通道的测试。
    /// 此类是线程安全的。
    /// </summary>
    public sealed class HydraulicOrchestrator // sealed 密封类，防止被继承，确保行为确定性。
    {
        /// <summary>
        /// 测试配置信息，包含所有液压组的定义及其启用状态。
        /// </summary>
        private readonly TestConfig _test;

        /// <summary>
        /// 底层液压控制器，用于实际执行建压和释压命令。
        /// </summary>
        private readonly HydraulicController _hyd;

        /// <summary>
        /// 日志记录器，用于输出操作日志。
        /// </summary>
        private readonly IAppLogger _log;

        /// <summary>
        /// 内部类，用于跟踪一个液压组的当前状态。
        /// </summary>
        private sealed class GroupState
        {
            /// <summary>
            /// 当前持有（占用）此液压组的电控通道数量（引用计数）。
            /// </summary>
            public int RefCount;

            /// <summary>
            /// 指示此液压组是否已经成功建压。
            /// </summary>
            public bool Building;

            /// <summary>
            /// 用于取消该液压组建压保持操作的取消令牌源。
            /// 当需要释压时，可以通过调用此Cts的Cancel方法来中断可能正在等待的建压保持任务。
            /// </summary>
            public CancellationTokenSource Cts = new();
        }

        /// <summary>
        /// 线程安全字典，用于维护所有液压组的状态。
        /// Key: 液压组的ID (hydId)
        /// Value: 该液压组的状态对象 (GroupState)
        /// </summary>
        private readonly ConcurrentDictionary<int, GroupState> _states = new();

        /// <summary>
        /// 构造函数
        /// </summary>
        /// <param name="test">测试配置</param>
        /// <param name="hyd">液压控制器</param>
        /// <param name="log">日志记录器（可选），如为空则使用空记录器</param>
        public HydraulicOrchestrator(TestConfig test, HydraulicController hyd, IAppLogger log = null)
        {
            _test = test;
            _hyd = hyd;
            _log = log ?? NullLogger.Instance; // 提供默认值，防止空引用
        }

        /// <summary>
        /// 为一个指定的电控通道申请占用其所属的液压组。
        /// 如果该电控通道不属于任何液压组，或所属液压组未启用，则返回一个无操作的虚拟句柄。
        /// 否则，将增加对应液压组的引用计数。如果当前是首个占用（引用计数从0到1），则触发异步建压并保持。
        /// 返回的IDisposable句柄必须在用完后Dispose，以释放对液压组的占用。
        /// </summary>
        /// <param name="epbChannel">电控通道ID</param>
        /// <param name="token">外部传入的取消令牌，可用于取消此次获取操作（注意：此实现中未使用该参数）</param>
        /// <returns>
        /// 一个实现了IDisposable的占用句柄。调用其Dispose方法表示释放占用。
        /// 如果无需真实占用液压，则返回一个虚拟句柄（Dispose方法无任何操作）。
        /// </returns>
        public async Task<IDisposable> AcquireAsync(int epbChannel, CancellationToken token)
        {
            // 1. 查找电控通道所属的液压组ID
            var hydId = FindHydIdByMember(epbChannel);
            // 如果该通道不属于任何液压组，返回虚拟句柄
            if (hydId == null) return DummyHandle.Instance;

            // 2. 获取液压组配置，检查是否启用
            var item = _test.Hydraulics.First(h => h.Id == hydId.Value);
            if (!item.Enabled) return DummyHandle.Instance;

            // 3. 获取或创建该液压组的状态对象
            // GetOrAdd 是线程安全的，确保对于同一个hydId，只会创建一个GroupState
            var st = _states.GetOrAdd(hydId.Value, _ => new GroupState());
            var needBuild = false; // 标记是否需要执行建压操作

            // 4. 修改状态（引用计数、建压标记）需要加锁，因为可能被多个线程同时调用Acquire和Release
            lock (st)
            {
                st.RefCount++; // 增加引用计数
                if (!st.Building) // 如果当前还未建压
                {
                    st.Building = true; // 标记为正在建压/已建压
                    needBuild = true; // 标记需要执行建压操作（由当前线程执行）
                }
            }

            // 5. 如果需要建压（当前线程是此液压组的首个占用者），则启动异步建压任务
            if (needBuild)
            {
                // 注意：这里使用 st.Cts.Token 而不是传入的 token。
                // 这意味着建压保持任务只会被本协调器内部的释放逻辑（通过st.Cts.Cancel()）取消，
                // 而不受外部传入的token影响。外部token通常用于取消“获取”操作本身，
                // 但这里“获取”基本是立即完成的，所以建压任务启动后就不再与之关联。
                // 使用 fire-and-forget ( _ = ... ) 方式启动，不等待其完成。
                // 实际的建压和保持逻辑在 _hyd.BuildAndHAsync 中实现。
                _ = _hyd.BuildAndHoldAsync(hydId.Value, st.Cts.Token);
            }

            // 6. 返回一个释放器，该释放器在Dispose时会减少引用计数并可能触发释压
            return new Releaser(this, hydId.Value);
        }

        /// <summary>
        /// 根据电控通道ID，查找其所属的液压组ID。
        /// </summary>
        /// <param name="epbChannel">电控通道ID</param>
        /// <returns>所属液压组ID，如果找不到则返回null</returns>
        private int? FindHydIdByMember(int epbChannel)
        {
            // 遍历配置中的所有液压组
            foreach (var h in _test.Hydraulics)
                // 检查该液压组的成员列表是否包含指定的电控通道
                if (h.Members.Contains(epbChannel))
                    return h.Id; // 找到则返回液压组ID
            return null; // 未找到返回null
        }

        /// <summary>
        /// 内部类，实现IDisposable，用于在释放时减少对应液压组的引用计数。
        /// </summary>
        private sealed class Releaser : IDisposable
        {
            private readonly HydraulicOrchestrator _owner; // 所属的协调器实例
            private readonly int _hydId; // 对应的液压组ID
            private bool _disposed; // 防止重复释放的标志

            public Releaser(HydraulicOrchestrator owner, int hydId)
            {
                _owner = owner;
                _hydId = hydId;
            }

            /// <summary>
            /// 释放占用的液压资源。减少引用计数，如果减到0，则触发统一释压。
            /// </summary>
            public void Dispose()
            {
                if (_disposed) return; // 如果已经释放过，则直接返回
                _disposed = true; // 标记为已释放

                // 尝试从字典中获取该液压组的状态对象
                if (!_owner._states.TryGetValue(_hydId, out var st)) return; // 如果找不到（理论上不应发生），直接返回

                var shouldRelease = false; // 标记是否需要执行释压操作

                // 修改状态需要加锁
                lock (st)
                {
                    // 减少引用计数，确保不会小于0
                    st.RefCount = Math.Max(0, st.RefCount - 1);
                    // 如果引用计数减到0，说明这是最后一个占用者
                    if (st.RefCount == 0)
                    {
                        shouldRelease = true; // 标记需要释压
                        st.Building = false; // 重置建压状态
                        // 注意：这里没有重置或处置 st.Cts，因为字典项即将被移除
                    }
                }

                // 如果需要释压（当前线程是此液压组的最后一个释放者）
                if (shouldRelease)
                {
                    // 记录日志
                    _owner._log.Info($"液压[{_hydId}] 所有成员完成，统一释压。", "液压");
                    // 调用液压控制器执行释压命令
                    _owner._hyd.Release(_hydId);

                    // 可选：取消建压保持任务（如果_hyd.BuildAndHAsync内部正在等待一个可取消的操作，例如Task.Delay(Timeout.Infinite, st.Cts.Token)）
                    // st.Cts.Cancel(); 
                    // 当前实现选择直接调用 _hyd.Release，因此取消操作可能不是必须的。
                    // 这取决于 _hyd.BuildAndHAsync 的具体实现。如果它内部是一个无限循环等待取消，那么取消它很好。
                    // 如果它只是一个发送一次建压命令然后结束的任务，则取消没有必要。
                    // 这里注释掉了取消操作，说明可能依赖于 _hyd.Release 来实际停止保压。

                    // 从状态字典中移除该液压组的状态对象
                    // 这样下次再有占用时，会创建一个新的GroupState（包含新的CTS）
                    _owner._states.TryRemove(_hydId, out _);
                }
            }
        }

        /// <summary>
        /// 虚拟占用句柄。用于那些不占用真实液压资源的电控通道。
        /// 其Dispose方法无任何操作。
        /// </summary>
        private sealed class DummyHandle : IDisposable
        {
            /// <summary>
            /// 虚拟句柄的单例实例。
            /// </summary>
            public static readonly DummyHandle Instance = new();

            /// <summary>
            /// 无操作的Dispose方法。
            /// </summary>
            public void Dispose()
            {
            }
        }
    }
    



}