using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Config;
using NationalInstruments.DAQmx;

// 避免与 System.Threading.Tasks.Task 混淆，做别名
using NIDaqTask = NationalInstruments.DAQmx.Task;
using NIChannelLineGrouping = NationalInstruments.DAQmx.ChannelLineGrouping;
using NITaskAction = NationalInstruments.DAQmx.TaskAction;


using ILogger = Config.IAppLogger;
using NLogger = Config.NullLogger;


namespace IO.NI
{
    /// <summary>
    /// DO 控制器：基于 <see cref="DoConfig"/>（EPB 与 Pressure）统一管理多个数字输出。
    /// - 与 AoController 一致，按“配置对象”而非“读取XML”初始化。
    /// - 支持 EPB 正/反互斥输出、Pressure 点位开/关。
    /// - 线程安全：所有对 NI 任务的构建与写入均有锁保护。
    /// </summary>
    public class DoController : IDisposable
    {
        #region 内部类型与字段

        /// <summary>
        ///     DO 写入专用高优先级 Worker。
        ///     设计目的：将“触发后断电”等关键 DO 写入从线程池/多线程锁竞争中剥离出来，
        ///     以更稳定的调度优先级执行写入，减少尾部抖动。
        /// </summary>
        private sealed class HighPriorityDoWorker : IDisposable
        {
            private const int MaxPendingWorkItems = 64;

            private sealed class WorkItem
            {
                public Func<bool> Work;
                public ManualResetEventSlim Done;
                public bool Result;
                public Exception Error;
            }

            private readonly ConcurrentQueue<WorkItem> _hiQueue = new ConcurrentQueue<WorkItem>();
            private readonly AutoResetEvent _signal = new AutoResetEvent(false);

            private volatile bool _stopping;
            private int _pendingWorkItems;
            private Thread _thread;

            /// <summary>
            ///     启动高优先级 worker 线程。
            /// </summary>
            /// <remarks>
            ///     线程模型：
            ///     <list type="bullet">
            ///         <item>使用专用 <see cref="Thread"/>，不占用线程池。</item>
            ///         <item>线程优先级设为 <see cref="ThreadPriority.Highest"/>。</item>
            ///     </list>
            /// </remarks>
            public void StartIfNeeded()
            {
                if (_thread != null) return;

                var t = new Thread(Loop)
                {
                    IsBackground = true,
                    Name = "DO-HighPriorityWorker",
                    Priority = ThreadPriority.Highest
                };

                _thread = t;
                t.Start();
            }

            /// <summary>
            ///     在高优先级 worker 线程中执行一个 DO 写入任务，并同步等待完成。
            /// </summary>
            /// <param name="work">具体写入逻辑；应为短任务（单次 NI 写入）。</param>
            /// <param name="timeoutMs">
            ///     等待超时（毫秒）。超时后调用方可选择降级为“本线程直接执行”。
            /// </param>
            public bool InvokeHi(Func<bool> work, int timeoutMs)
            {
                if (work == null) return false;

                // 重入时不能在worker线程内再次同步执行NI写入；返回失败交给上层
                // 电源隔离/重试，避免日志或观察者回调形成递归阻塞。
                if (Thread.CurrentThread == _thread)
                    return false;

                StartIfNeeded();

                if (Interlocked.Increment(ref _pendingWorkItems) > MaxPendingWorkItems)
                {
                    Interlocked.Decrement(ref _pendingWorkItems);
                    return false;
                }

                var item = new WorkItem
                {
                    Work = work,
                    Done = new ManualResetEventSlim(false)
                };

                _hiQueue.Enqueue(item);
                _signal.Set();

                // 关键路径：给一个有限等待；若超时由上层决定是否降级直写。
                if (!item.Done.Wait(Math.Max(1, timeoutMs)))
                    return false;

                return item.Result;
            }

            private void Loop()
            {
                while (!_stopping)
                {
                    if (!_hiQueue.TryDequeue(out var item))
                    {
                        _signal.WaitOne(50);
                        continue;
                    }

                    try
                    {
                        item.Result = item.Work();
                    }
                    catch (Exception ex)
                    {
                        item.Error = ex;
                        item.Result = false;
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _pendingWorkItems);
                        try { item.Done.Set(); }
                        catch { /* ignore */ }
                    }
                }
            }

            public void Dispose()
            {
                _stopping = true;
                try { _signal.Set(); } catch { /* ignore */ }
                try { _signal.Dispose(); } catch { /* ignore */ }
            }
        }

        /// <summary>每个 NI 设备的上下文。</summary>
        private sealed class DoDevice
        {
            public string Name;
            public NIDaqTask Task;
            public DigitalMultiChannelWriter Writer;

            // 每设备独立的通道与默认值表
            public readonly List<string> Lines = new List<string>();
            public readonly List<bool> DefaultStates = new List<bool>();

            // 当前实际状态（与 Lines 一一对应）
            public bool[] States;
        }

        private readonly object _doTaskLock = new object();

        // 设备名 -> 设备上下文
        private readonly Dictionary<string, DoDevice> _devices =
            new Dictionary<string, DoDevice>(StringComparer.OrdinalIgnoreCase);

        // EPB: 通道号 -> (设备名, 正Idx, 反Idx) —— 索引是该设备 DefaultStates/States 的索引
        private readonly Dictionary<int, (string dev, int posIdx, int negIdx)> _epbIndex
            = new Dictionary<int, (string, int, int)>();

        // 压力: 编号 -> (设备名, 索引)
        private readonly Dictionary<int, (string dev, int idx)> _pressureIndex
            = new Dictionary<int, (string, int)>();

        // UI 状态查询必须无锁，不能让 NI 同步写入阻塞 DAQ 后台发布线程。
        // 仅在硬件写成功后更新；写失败时保留上一已确认状态，避免错误掩盖真实信号。
        private readonly ConcurrentDictionary<int, byte> _epbOutputActive =
            new ConcurrentDictionary<int, byte>();
        private readonly ConcurrentDictionary<int, byte> _pressureOutputActive =
            new ConcurrentDictionary<int, byte>();

        // 兼容旧接口：不再使用，但保留以免外部调用报错
        private string _configPath;

        private readonly ILogger _log;

        // ★新增：高优先级 DO worker（用于“触发后断电”等关键写入）
        private readonly HighPriorityDoWorker _hiWorker = new HighPriorityDoWorker();

        // 新增：保存配置对象（来源于外部的 cfgDo）
        private readonly DoConfig _cfg;

        #endregion

        #region 构造与配置

        /// <summary>
        /// 构造 DO 控制器。
        /// </summary>
        /// <param name="cfgDo">数字输出配置（EPB 与 Pressure）。必填。</param>
        /// <param name="logger">可选日志器。</param>
        public DoController(DoConfig cfgDo, ILogger logger = null)
        {
            _cfg = cfgDo ?? throw new ArgumentNullException(nameof(cfgDo));
            _log = logger ?? NLogger.Instance;
        }

        /// <summary>
        /// 兼容旧接口：设置 XML 路径（本实现不会再读取 XML，仅为保持方法签名不变）。
        /// </summary>
        /// <param name="xmlPath">历史遗留参数，忽略。</param>
        public void SetConfigPath(string xmlPath) => _configPath = xmlPath;

        #endregion

        #region 初始化

        /// <summary>
        /// 初始化所有 DO 通道：根据 <see cref="DoConfig"/> 构建任务、创建通道、索引映射，并下发默认值。
        /// </summary>
        /// <returns>成功/失败。</returns>
        public bool Initialize()
        {
            lock (_doTaskLock)
            {
                try
                {
                    ResetAllDevices(clearMaps: true);

                    // ===== EPB：正/反两路，隶属同一设备且互斥 =====
                    if (_cfg?.Epb != null)
                    {
                        foreach (var r in _cfg.Epb)
                        {
                            if (!(r?.Enabled ?? false)) continue;
                            if (r.Channel <= 0) continue;
                            if (string.IsNullOrWhiteSpace(r.Pos) || string.IsNullOrWhiteSpace(r.Neg)) continue;

                            string devNamePos = GetDeviceName(r.Pos);
                            string devNameNeg = GetDeviceName(r.Neg);
                            if (!devNamePos.Equals(devNameNeg, StringComparison.OrdinalIgnoreCase))
                            {
                                LogError($"EPB{r.Channel} 的正/反分属不同设备（{devNamePos} / {devNameNeg}），不支持跨设备互斥，已跳过。", "DO初始化");
                                continue;
                            }

                            var dev = EnsureDevice(devNamePos);
                            string def = string.IsNullOrWhiteSpace(r.Default) ? "全关" : r.Default.Trim();

                            // 正
                            dev.Task.DOChannels.CreateChannel(r.Pos, $"EPB{r.Channel}-正", NIChannelLineGrouping.OneChannelForEachLine);
                            dev.Lines.Add(r.Pos);
                            dev.DefaultStates.Add(def == "正");
                            int posIdx = dev.DefaultStates.Count - 1;

                            // 反
                            dev.Task.DOChannels.CreateChannel(r.Neg, $"EPB{r.Channel}-反", NIChannelLineGrouping.OneChannelForEachLine);
                            dev.Lines.Add(r.Neg);
                            dev.DefaultStates.Add(def == "反");
                            int negIdx = dev.DefaultStates.Count - 1;

                            if (def == "全关")
                            {
                                dev.DefaultStates[posIdx] = false;
                                dev.DefaultStates[negIdx] = false;
                            }

                            _epbIndex[r.Channel] = (devNamePos, posIdx, negIdx);
                        }
                    }

                    // ===== Pressure：单点 DO，开/关 =====
                    if (_cfg?.Pressure != null)
                    {
                        foreach (var p in _cfg.Pressure)
                        {
                            if (!(p?.Enabled ?? false)) continue;
                            if (p.Id <= 0) continue;
                            if (string.IsNullOrWhiteSpace(p.Physical)) continue;

                            string devName = GetDeviceName(p.Physical);
                            var dev = EnsureDevice(devName);

                            bool defVal = p.DefaultValue == 1;

                            dev.Task.DOChannels.CreateChannel(p.Physical, $"Pressure-{p.Id}", NIChannelLineGrouping.OneChannelForEachLine);
                            dev.Lines.Add(p.Physical);
                            dev.DefaultStates.Add(defVal);
                            int idx = dev.DefaultStates.Count - 1;

                            _pressureIndex[p.Id] = (devName, idx);
                        }
                    }

                    if (_devices.Count == 0)
                    {
                        LogError("DO 初始化失败：未创建任何设备任务（配置可能为空或全部禁用）", "DO初始化");
                        ResetAllDevices(clearMaps: true);
                        return false;
                    }

                    // 逐设备 Verify + Writer + 下发默认值
                    int totalLines = 0;
                    foreach (var dev in _devices.Values)
                    {
                        if (dev.Lines.Count == 0) continue;

                        dev.Task.Control(NITaskAction.Verify);
                        dev.Writer = new DigitalMultiChannelWriter(dev.Task.Stream);
                        dev.States = dev.DefaultStates.ToArray();

                        // 下发默认
                        dev.Writer.WriteSingleSampleSingleLine(true, dev.States);
                        totalLines += dev.Lines.Count;
                    }

                    PublishConfirmedOutputStates();

                    LogInfo($"DO 初始化完成：设备数={_devices.Count}，总线数={totalLines}，EPB组={_epbIndex.Count}，压力点={_pressureIndex.Count}", "DO初始化");
                    return true;
                }
                catch (DaqException ex)
                {
                    LogError("DO 初始化失败（DAQ）：" + ex.Message, "DO初始化", ex);
                    ResetAllDevices(clearMaps: false);
                    return false;
                }
                catch (Exception ex2)
                {
                    LogError($"DO 初始化失败（未知）：{ex2}", "DO初始化", ex2);
                    ResetAllDevices(clearMaps: false);
                    return false;
                }
            }
        }

        #endregion

        #region 写入：EPB 与 Pressure（保持原有方法名/签名）

        /// <summary>
        /// 设置 EPB 通道的方向。
        /// </summary>
        /// <param name="channelNo">EPB 通道号。</param>
        /// <param name="directionIsForward">true=正，false=反。</param>
        /// <returns>成功/失败。</returns>
        public bool SetEpb(int channelNo, bool directionIsForward)
        {
            lock (_doTaskLock)
            {
                const int maxRetries = 1;
                int attempts = 0;

                while (attempts <= maxRetries)
                {
                    try
                    {
                        if (!EnsureReady()) { attempts++; continue; }

                        if (!_epbIndex.TryGetValue(channelNo, out var map))
                        {
                            LogError($"EPB 通道号未找到：{channelNo}", "DO操作");
                            return false;
                        }

                        if (!_devices.TryGetValue(map.dev, out var dev))
                        {
                            LogError($"EPB[{channelNo}] 所属设备未就绪：{map.dev}", "DO操作");
                            return false;
                        }

                        var toWrite = (bool[])dev.States.Clone();
                        toWrite[map.posIdx] = directionIsForward;
                        toWrite[map.negIdx] = !directionIsForward;

                        dev.Writer.WriteSingleSampleSingleLine(true, toWrite);
                        dev.States = toWrite;
                        _epbOutputActive[channelNo] = 1;

                        LogInfo($"EPB[{channelNo}]@{map.dev} => {(directionIsForward ? "正" : "反")}", "DO操作");
                        return true;
                    }
                    catch (DaqException ex)
                    {
                        attempts++;
                        LogError($"EPB 写入失败（第{attempts}/{maxRetries + 1}次）：{ex.Message}", "DO操作", ex);
                        if (attempts <= maxRetries) { ResetAllDevices(clearMaps: false); Initialize(); }
                    }
                    catch (Exception ex2)
                    {
                        LogError($"EPB 写入异常：{ex2}", "DO操作", ex2);
                        break;
                    }
                }

                LogError("EPB 写入失败：超过最大重试次数", "DO操作");
                return false;
            }
        }

        /// <summary>
        /// 关闭指定 EPB 通道（正/反全关）。
        /// </summary>
        /// <param name="channelNo">EPB 通道号。</param>
        /// <returns>成功/失败。</returns>
        public bool SetEpbOff(int channelNo)
        {
            lock (_doTaskLock)
            {
                try
                {
                    if (!EnsureReady()) return false;

                    if (!_epbIndex.TryGetValue(channelNo, out var map))
                    {
                        LogError($"EPB 通道号未找到：{channelNo}", "DO操作");
                        return false;
                    }
                    if (!_devices.TryGetValue(map.dev, out var dev))
                    {
                        LogError($"EPB[{channelNo}] 所属设备未就绪：{map.dev}", "DO操作");
                        return false;
                    }

                    var toWrite = (bool[])dev.States.Clone();
                    toWrite[map.posIdx] = false;
                    toWrite[map.negIdx] = false;
                    dev.Writer.WriteSingleSampleSingleLine(true, toWrite);
                    dev.States = toWrite;
                    _epbOutputActive[channelNo] = 0;

                    LogInfo($"EPB[{channelNo}]@{map.dev} => 全关", "DO操作");
                    return true;
                }
                catch (Exception ex)
                {
                    LogError("EPB 关闭失败：" + ex.Message, "DO操作", ex);
                    return false;
                }
            }
        }

        /// <summary>
        ///     高优先级关闭指定 EPB 通道（正/反全关）。
        /// </summary>
        /// <param name="channelNo">EPB 通道号。</param>
        /// <returns>成功/失败。</returns>
        /// <remarks>
        ///     线程模型：
        ///     <list type="bullet">
        ///         <item>通过专用高优先级 worker 线程执行，减少线程池调度/锁竞争带来的尾部抖动；</item>
        ///         <item>若 worker 等待超时则立即返回失败，由上层切断电源组并持续重试；禁止调用线程同步直写。</item>
        ///     </list>
        ///     注意：该方法仍会进入 <see cref="SetEpbOff"/> 的锁保护，
        ///     但因为关键路径集中到单线程，整体竞争通常显著降低。
        /// </remarks>
        public bool SetEpbOffHighPriority(int channelNo)
        {
            // 只允许专用worker执行NI同步写。worker超时后若在调用线程再次直写，
            // 会等待同一个_doTaskLock/NI调用，曾使DAQ恢复协程阻塞二十余分钟。
            // 超时返回false后，上层会保持电源组隔离并由看门狗持续重试；已排队的
            // OFF命令仍会在worker恢复后执行，因此不会把软件阻塞扩散到状态机。
            return _hiWorker.InvokeHi(() => SetEpbOff(channelNo), timeoutMs: 30);
        }

        /// <summary>
        /// 设置压力点位开/关。
        /// </summary>
        /// <param name="id">压力点位编号。</param>
        /// <param name="start">true=启动；false=停止。</param>
        /// <returns>成功/失败。</returns>
        public bool SetPressure(int id, bool start)
        {
            lock (_doTaskLock)
            {
                const int maxRetries = 1;
                int attempts = 0;

                while (attempts <= maxRetries)
                {
                    try
                    {
                        if (!EnsureReady()) { attempts++; continue; }

                        if (!_pressureIndex.TryGetValue(id, out var map))
                        {
                            LogError($"压力编号未找到：{id}", "DO操作");
                            return false;
                        }

                        if (!_devices.TryGetValue(map.dev, out var dev))
                        {
                            LogError($"压力[{id}] 所属设备未就绪：{map.dev}", "DO操作");
                            return false;
                        }

                        var toWrite = (bool[])dev.States.Clone();
                        toWrite[map.idx] = start;

                        dev.Writer.WriteSingleSampleSingleLine(true, toWrite);
                        dev.States = toWrite;
                        _pressureOutputActive[id] = start ? (byte)1 : (byte)0;

                        LogInfo($"压力[{id}]@{map.dev} => {(start ? "启动" : "停止")}", "DO操作");
                        return true;
                    }
                    catch (DaqException ex)
                    {
                        attempts++;
                        LogError($"压力写入失败（第{attempts}/{maxRetries + 1}次）：{ex.Message}", "DO操作", ex);
                        if (attempts <= maxRetries) { ResetAllDevices(clearMaps: false); Initialize(); }
                    }
                    catch (Exception ex2)
                    {
                        LogError($"压力写入异常：{ex2}", "DO操作", ex2);
                        break;
                    }
                }

                LogError("压力写入失败：超过最大重试次数", "DO操作");
                return false;
            }
        }

        #endregion

        #region 便捷方法（保持原名）

        /// <summary>将所有 DO 路线全部关闭（所有设备）。</summary>
        public bool AllOff()
        {
            lock (_doTaskLock)
            {
                try
                {
                    if (!EnsureReady()) return false;

                    foreach (var dev in _devices.Values)
                    {
                        var zeros = new bool[dev.States.Length];
                        dev.Writer.WriteSingleSampleSingleLine(true, zeros);
                        dev.States = zeros;
                    }

                    foreach (var channel in _epbIndex.Keys) _epbOutputActive[channel] = 0;
                    foreach (var id in _pressureIndex.Keys) _pressureOutputActive[id] = 0;

                    LogInfo("DO 全部关闭（所有设备）", "DO操作");
                    return true;
                }
                catch (Exception ex)
                {
                    LogError("全部关闭失败：" + ex.Message, "DO操作", ex);
                    return false;
                }
            }
        }

        /// <summary>方向友好名称封装，兼容旧调用：设为正向。</summary>
        public bool SetEpbForward(int channelNo) => SetEpb(channelNo, true);

        /// <summary>方向友好名称封装，兼容旧调用：设为反向。</summary>
        public bool SetEpbReverse(int channelNo) => SetEpb(channelNo, false);

        /// <summary>压力友好名称封装：启动。</summary>
        public bool PressureOn(int id) => SetPressure(id, true);

        /// <summary>压力友好名称封装：停止。</summary>
        public bool PressureOff(int id) => SetPressure(id, false);

        /// <summary>返回最近一次成功下发后，指定 EPB 正/反输出是否有一路处于激活状态。</summary>
        public bool IsEpbOutputActive(int channelNo)
            => _epbOutputActive.TryGetValue(channelNo, out var active) && active != 0;

        /// <summary>返回最近一次成功下发后，指定气缸压力 DO 是否处于激活状态。</summary>
        public bool IsPressureOutputActive(int id)
            => _pressureOutputActive.TryGetValue(id, out var active) && active != 0;

        #endregion

        #region 内部工具与清理

        /// <summary>确保设备上下文存在，不存在则创建。</summary>
        private DoDevice EnsureDevice(string deviceName)
        {
            if (!_devices.TryGetValue(deviceName, out var dev))
            {
                dev = new DoDevice
                {
                    Name = deviceName,
                    Task = new NIDaqTask("DO_" + deviceName)
                };
                _devices[deviceName] = dev;
            }
            return dev;
        }

        private void PublishConfirmedOutputStates()
        {
            foreach (var pair in _epbIndex)
            {
                var active = _devices.TryGetValue(pair.Value.dev, out var dev) &&
                             dev.States != null &&
                             pair.Value.posIdx >= 0 && pair.Value.posIdx < dev.States.Length &&
                             pair.Value.negIdx >= 0 && pair.Value.negIdx < dev.States.Length &&
                             (dev.States[pair.Value.posIdx] || dev.States[pair.Value.negIdx]);
                _epbOutputActive[pair.Key] = active ? (byte)1 : (byte)0;
            }

            foreach (var pair in _pressureIndex)
            {
                var active = _devices.TryGetValue(pair.Value.dev, out var dev) &&
                             dev.States != null &&
                             pair.Value.idx >= 0 && pair.Value.idx < dev.States.Length &&
                             dev.States[pair.Value.idx];
                _pressureOutputActive[pair.Key] = active ? (byte)1 : (byte)0;
            }
        }

        /// <summary>从物理线名取设备名，如 "Dev1/port0/line0" -> "Dev1"。</summary>
        private static string GetDeviceName(string physicalLine)
        {
            if (string.IsNullOrWhiteSpace(physicalLine)) return "";
            int slash = physicalLine.IndexOf('/');
            return slash > 0 ? physicalLine.Substring(0, slash) : physicalLine;
        }

        /// <summary>确保当前对象已完成初始化，必要时调用 <see cref="Initialize"/>。</summary>
        private bool EnsureReady()
        {
            if (_devices.Count == 0) return Initialize();

            foreach (var dev in _devices.Values)
            {
                if (dev.Task == null || dev.Writer == null || dev.States == null)
                    return Initialize();
            }
            return true;
        }

        /// <summary>释放并清空所有设备任务、索引等。</summary>
        private void ResetAllDevices(bool clearMaps)
        {
            foreach (var dev in _devices.Values)
            {
                try { dev.Task?.Dispose(); } catch { /* ignore */ }
                dev.Task = null;
                dev.Writer = null;
                dev.Lines.Clear();
                dev.DefaultStates.Clear();
                dev.States = null;
            }
            if (clearMaps)
            {
                _devices.Clear();
                _epbIndex.Clear();
                _pressureIndex.Clear();
            }
        }

        private void LogInfo(string message, string category = null)
            => _log?.Info(message, category ?? "DO");

        private void LogError(string message, string category = null, Exception ex = null)
            => _log?.Error(message, category ?? "DO", ex);

        /// <summary>
        ///     释放所有 NI 资源，并停止内部 DO 写入 worker。
        /// </summary>
        /// <remarks>
        ///     线程模型：该方法会停止专用 worker，并在锁保护下释放 NI Task/Writer。
        /// </remarks>
        public void Dispose()
        {
            try { _hiWorker.Dispose(); } catch { /* ignore */ }

            lock (_doTaskLock)
            {
                try { ResetAllDevices(clearMaps: true); }
                catch { /* ignore */ }
            }

            GC.SuppressFinalize(this);
        }

        #endregion
    }
}
