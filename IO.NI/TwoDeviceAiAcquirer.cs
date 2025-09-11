using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Config;
using DataOperation;
using NationalInstruments.DAQmx;
using NIDaqTask = NationalInstruments.DAQmx.Task;
using Task = System.Threading.Tasks.Task;
using ILogger = Config.IAppLogger;
using NLogger = Config.NullLogger;

namespace IO.NI
{
    /// <summary>
    ///     双设备（Dev1/Dev2）AI 连续采样管理器：
    ///     - DAQ 回调线程提供低时延的“最后一个样本”快速工程值（未滤波），并写入 _lastFastValue；
    ///     - 后台处理线程负责将整批数据转换为工程值并滤波，然后写入 _lastFilteredValue；
    ///     - 提供明确的读取接口以区分快/慢数据的用途（控制用 vs UI/统计用）。
    /// </summary>
    public sealed class TwoDeviceAiAcquirer : IDisposable
    {
        // —— 对接控制逻辑 —— //
        public delegate double ReadCurrentDelegate(int epbChannel);

        // 参数名 -> 本设备内列索引
        private readonly Dictionary<string, int> _colIndexDev1 = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _colIndexDev2 = new(StringComparer.OrdinalIgnoreCase);
        private readonly CancellationTokenSource _cts = new();
        private readonly string[] _dev1Channels, _dev2Channels;
        private readonly List<AiConfigDetailRecord> _enabled;

        // 最近的工程值快照（拆成两份以避免语义冲突）
        // 1) 低时延快照（在 DAQ 回调线程中写入）：未滤波、用于控制逻辑/紧急读数
        private readonly ConcurrentDictionary<string, double> _lastFastValue = new();

        // 2) 滤波后快照（在后台线程中写入）：已滤波、用于 UI / 统计 / 报表
        private readonly ConcurrentDictionary<string, double> _lastFilteredValue = new();

        private readonly ILogger _log;
        private readonly int _medianLens;

        private readonly ConcurrentQueue<Item> _queue = new();
        private readonly double _sampleRate;
        private readonly int _samplesPerChannel;

        // 时间戳（模仿 FrmMainMonitor）
        private readonly Stopwatch _sw = new();
        private readonly Task _worker;
        private DateTime _lastTs = DateTime.Now;
        private AnalogMultiChannelReader _reader1, _reader2;
        private DateTime _t0 = DateTime.Now;
        private long _swStartTicks; // Stopwatch 起点


        // NI 任务
        private NIDaqTask _task1, _task2;
        private long _ts0;

        // 动态置零偏移（参数名 -> offset，工程值单位）
        private readonly ConcurrentDictionary<string, double> _zeroOffsets = new(StringComparer.OrdinalIgnoreCase);

        // 顶部字段处
        private sealed class DevClock // 每设备时钟状态
        {
            public DateTime T0; // 本设备参考起点（与 Start() 同时刻）
            public DateTime Last; // 本设备上一次时间戳
            public long Samples; // 本设备自启动累计样本数（可用于诊断）
        }

        private readonly ConcurrentDictionary<string, DevClock> _devClocks = new();


        private void InitTimeBase()
        {
            _t0 = DateTime.Now;
            _swStartTicks = Stopwatch.GetTimestamp();
            _sw.Restart();
            _ts0 = 0;
            _lastTs = _t0;
        }

        /// <summary>把当前 Stopwatch 读数换算为 DateTime（纳秒级精度，避免整数毫秒量化）。</summary>
        private static DateTime NowFromStopwatch(long startStamp, DateTime t0)
        {
            long now = Stopwatch.GetTimestamp();
            double sec = (now - startStamp) / (double)Stopwatch.Frequency;
            return t0.AddSeconds(sec);
        }


        public TwoDeviceAiAcquirer(
            AiConfigDetail cfg,
            double sampleRate,
            int samplesPerChannel,
            int medianLens, // 走 ClsDataFilter 的中值窗长（用你全局配置传入）
            ILogger log = null)
        {
            _enabled = cfg.Enabled();
            _sampleRate = sampleRate;
            _samplesPerChannel = samplesPerChannel;
            _medianLens = Math.Max(1, medianLens);
            _log = log ?? NLogger.Instance;

            _dev1Channels = _enabled.Where(r => r.物理通道.StartsWith("Dev1/")).Select(r => r.物理通道).ToArray();
            _dev2Channels = _enabled.Where(r => r.物理通道.StartsWith("Dev2/")).Select(r => r.物理通道).ToArray();

            BuildColumnIndex(_enabled, "Dev1", _dev1Channels, _colIndexDev1);
            BuildColumnIndex(_enabled, "Dev2", _dev2Channels, _colIndexDev2);

            _worker = Task.Run(ProcessLoop, _cts.Token);
        }

        public void Dispose()
        {
            Stop();
            _cts.Cancel();
            try
            {
                _worker?.Wait(1000);
            }
            catch
            {
            }

            _cts.Dispose();
        }


        /// <summary>将指定 EPB 通道（1..12）的“当前值”设为零点。</summary>
        public void ZeroEpbChannel(int epbChannel)
        {
            var key = $"EPB{epbChannel}_current";
            // 优先用 fast 值（低延迟）
            if (_lastFastValue.TryGetValue(key, out var v))
            {
                _zeroOffsets[key] = v;
                _log?.Info($"EPB 通道 {epbChannel} 已置零：offset={v:F4}", "AI");
            }
        }

        /// <summary>清除指定 EPB 通道的动态零点。</summary>
        public void ClearZeroEpbChannel(int epbChannel)
        {
            var key = $"EPB{epbChannel}_current";
            _zeroOffsets.TryRemove(key, out _);
        }

        /// <summary>将指定“参数名”的当前值设为零点（通用：可用于压力等非 EPB 通道）。</summary>
        public void ZeroByParamName(string paramName)
        {
            if (string.IsNullOrWhiteSpace(paramName)) return;
            if (_lastFastValue.TryGetValue(paramName, out var v))
            {
                _zeroOffsets[paramName] = v;
                _log?.Info($"参数 {paramName} 已置零：offset={v:F4}", "AI");
            }
        }

        /// <summary>清除指定“参数名”的动态零点。</summary>
        public void ClearZeroByParamName(string paramName)
        {
            if (string.IsNullOrWhiteSpace(paramName)) return;
            _zeroOffsets.TryRemove(paramName, out _);
        }

        /// <summary>将“所有已知参数”的当前值设为零点（对每个 _lastFastValue 的键）。</summary>
        public void ZeroAllChannels()
        {
            foreach (var kv in _lastFastValue)
                _zeroOffsets[kv.Key] = kv.Value;
            _log?.Warn("已对所有通道执行置零（基于 fast 快照）。", "AI");
        }

        /// <summary>清除全部动态零点。</summary>
        public void ClearAllZeros()
        {
            _zeroOffsets.Clear();
            _log?.Warn("已清除全部通道的动态置零。", "AI");
        }


        // —— 供窗体订阅的两个回调 —— //
        // 原始电压数据（未标定、未滤波）：UI/落盘在窗体里直接调用 DaqAIContext.EnqueueRawData/StatData
        public event Action<string /*Dev1|Dev2*/, double[,], DateTime /*current*/, DateTime /*last*/> OnRawBatch;

        // 工程值（标定 + 滤波 后的全通道矩阵）：给 UI 或调试可选使用
        public event Action<string /*Dev1|Dev2*/, double[,], DateTime /*current*/, DateTime /*last*/> OnEngBatch;

        /// <summary>
        ///     低时延电流样本事件：在 DAQ 回调线程中触发，报告“当前批次最后一个样本”的工程值（未滤波）。
        ///     用于实时控制，不建议做重活（如 IO/磁盘/复杂计算）。
        /// </summary>
        /// <param name="epbChannel">EPB 物理通道号（1..12）。</param>
        /// <param name="amps">电流（A，已做零漂/比例/偏置换算）。</param>
        /// <param name="ts">样本时间戳。</param>
        public event Action<int, double, DateTime> OnFastEpbCurrent;


        /// <summary>
        ///     从参数名（例如 "EPB9_current"）中提取 EPB 通道号（9）。
        ///     不匹配返回 -1。
        /// </summary>
        private static int TryParseEpbChannel(string paramName)
        {
            if (string.IsNullOrEmpty(paramName)) return -1;
            // 形如 EPB1_current / EPB12_current
            var s = paramName;
            var i = s.IndexOf("EPB", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return -1;
            i += 3;
            var j = i;
            while (j < s.Length && char.IsDigit(s[j])) j++;
            if (j == i) return -1;
            if (int.TryParse(s.Substring(i, j - i), out var ch)) return ch;
            return -1;
        }

        /// <summary>
        ///     读取 EPB 电流的“常用”接口（向后兼容）。
        ///     语义：优先返回低延迟快照（fast），若没有则返回滤波后值（filtered），否则返回 0.0。
        ///     建议：控制逻辑应显式调用 <see cref="ReadCurrentFast" /> 或订阅 <see cref="OnFastEpbCurrent" />；
        ///     UI/统计应使用 <see cref="ReadCurrentFiltered" /> 或订阅 <see cref="OnEngBatch" />.
        /// </summary>
        /// <param name="epbChannel">EPB 通道号（1..12）。</param>
        /// <returns>电流（A）。</returns>
        public double ReadCurrent(int epbChannel)
        {
            var key = $"EPB{epbChannel}_current";
            if (_lastFastValue.TryGetValue(key, out var vFast)) return vFast;
            if (_lastFilteredValue.TryGetValue(key, out var vFilt)) return vFilt;
            return 0.0;
        }

        /// <summary>
        ///     明确读取“低延迟 / 未滤波”的 EPB 电流快照（由 DAQ 回调线程写入）。
        ///     若不存在返回 0.0。
        /// </summary>
        /// <param name="epbChannel">EPB 通道号（1..12）。</param>
        /// <returns>低延迟电流（A），或 0.0。</returns>
        public double ReadCurrentFast(int epbChannel)
        {
            var key = $"EPB{epbChannel}_current";
            return _lastFastValue.TryGetValue(key, out var v) ? v : 0.0;
        }

        /// <summary>
        ///     明确读取“滤波后 / 平滑”的 EPB 电流快照（由后台线程写入）。
        ///     若不存在返回 0.0。
        /// </summary>
        /// <param name="epbChannel">EPB 通道号（1..12）。</param>
        /// <returns>滤波后电流（A），或 0.0。</returns>
        public double ReadCurrentFiltered(int epbChannel)
        {
            var key = $"EPB{epbChannel}_current";
            return _lastFilteredValue.TryGetValue(key, out var v) ? v : 0.0;
        }

        /// <summary>
        ///     读取压力（默认行为同 ReadCurrent：优先 fast，再 filtered）。
        ///     压力通常不会出现在 fast 分支；此方法保持兼容性并返回合适的值。
        /// </summary>
        /// <param name="id">压力编号（例如 1/2）。</param>
        /// <returns>压力值（单位由配置定义），或 0.0。</returns>
        public double ReadPressure(int id)
        {
            var key = $"Pressure_{id}";
            if (_lastFastValue.TryGetValue(key, out var vFast)) return vFast;
            if (_lastFilteredValue.TryGetValue(key, out var vFilt)) return vFilt;
            return 0.0;
        }

        /// <summary>
        ///     明确读取滤波后的压力值（UI/统计推荐使用）。
        /// </summary>
        public double ReadPressureFiltered(int id)
        {
            var key = $"Pressure_{id}";
            return _lastFilteredValue.TryGetValue(key, out var v) ? v : 0.0;
        }

        /// <summary>
        ///     明确读取（若存在）由 fast 分支写入的压力值（一般不会有）。
        /// </summary>
        public double ReadPressureFast(int id)
        {
            var key = $"Pressure_{id}";
            return _lastFastValue.TryGetValue(key, out var v) ? v : 0.0;
        }

        public void Start(double aiMin = -10, double aiMax = 10,
            AITerminalConfiguration term = AITerminalConfiguration.Rse)
        {
            Stop();
            if (!_sw.IsRunning)
            {
                //InitTimeBase();
                _t0 = DateTime.Now;
                _sw.Start();
                _ts0 = _sw.ElapsedMilliseconds;
            }


            // 为每个实际启用的设备放入独立的时钟
            if (_dev1Channels.Length > 0)
                _devClocks["Dev1"] = new DevClock { T0 = _t0, Last = _t0, Samples = 0 };

            if (_dev2Channels.Length > 0)
                _devClocks["Dev2"] = new DevClock { T0 = _t0, Last = _t0, Samples = 0 };



            if (_dev1Channels.Length > 0)
            {
                _task1 = CreateAiTask("Dev1_AI", _dev1Channels, aiMin, aiMax, term);
                _reader1 = new AnalogMultiChannelReader(_task1.Stream) { SynchronizeCallbacks = false };
                _reader1.BeginReadMultiSample(_samplesPerChannel, Dev1Callback, _task1);
            }

            // 暂时注释dev2
            if (_dev2Channels.Length > 0)
            {
                _task2 = CreateAiTask("Dev2_AI", _dev2Channels, aiMin, aiMax, term);
                _reader2 = new AnalogMultiChannelReader(_task2.Stream) { SynchronizeCallbacks = false };
                _reader2.BeginReadMultiSample(_samplesPerChannel, Dev2Callback, _task2);
            }

            _log.Info(
                $"AI 采集启动：Dev1[{_dev1Channels.Length}] Dev2[{_dev2Channels.Length}] Fs={_sampleRate}Hz N={_samplesPerChannel}",
                "AI");
        }

        public void Stop()
        {
            try
            {
                _task1?.Stop();
            }
            catch
            {
            }

            try
            {
                _task2?.Stop();
            }
            catch
            {
            }

            try
            {
                _task1?.Dispose();
            }
            catch
            {
            }

            try
            {
                _task2?.Dispose();
            }
            catch
            {
            }

            _task1 = null;
            _task2 = null;
            _reader1 = null;
            _reader2 = null;
        }

        // —— DAQ 回调：只负责 EndRead + 入队 + 立刻发起下一次 BeginRead —— //
        private void Dev1Callback(IAsyncResult ar)
        {
            OnAiBatch(ar, _reader1, _colIndexDev1, "Dev1", Dev1Callback);
        }

        private void Dev2Callback(IAsyncResult ar)
        {
            OnAiBatch(ar, _reader2, _colIndexDev2, "Dev2", Dev2Callback);
        }


        private void OnAiBatch(IAsyncResult ar, AnalogMultiChannelReader reader,
            Dictionary<string, int> colIndex, string device,
            AsyncCallback again)
        {
            try
            {
                var task = (NIDaqTask)ar.AsyncState;
                var raw = reader.EndReadMultiSample(ar); // [ch, n]
                int n = raw.GetLength(1);

                // ① 先 re-arm 下一批，减小回调耗时对节拍的影响
                reader.BeginReadMultiSample(_samplesPerChannel, again, task);

                // ② 取本设备的时钟状态
                if (!_devClocks.TryGetValue(device, out var clk))
                {
                    // 极端情况下（热插拔/重启后）没有就创建
                    clk = new DevClock { T0 = _t0, Last = _t0, Samples = 0 };
                    _devClocks[device] = clk;
                }
                
                var last = clk.Last;
                
                //  两种时间：主机“实测” + 按采样率推进的“理想”
                var hostNow = _t0.AddMilliseconds(_sw.ElapsedMilliseconds - _ts0); // 主机"实测时间"


                var idealNow = last.AddSeconds(n / _sampleRate); //  理想时间：由采样率推进，避免 jitter 抖动 Fs * n

                // ③ 轻微纠偏（例如 >5ms 时用主机时间，否则用理想时间，避免长期漂移）
                var driftMs = (hostNow - idealNow).TotalMilliseconds;
                var current = Math.Abs(driftMs) > 5 ? hostNow : idealNow;

                //var current =  idealNow; // 不用纠偏，直接采用理想时间

                // ④ （可选）诊断丢块：host Δt 远大于 n/Fs
                var hostDt = (hostNow - last).TotalSeconds;
                var expectDt = n / _sampleRate;
                if (hostDt > expectDt * 1.5)             // 系数可按经验调
                {
                    var lost = (int)Math.Round(hostDt * _sampleRate) - n;
                    if (lost > 0)
                        _log.Warn($"[{device}] 疑似丢样：hostΔt={hostDt:F4}s 期望={expectDt:F4}s 约缺 {lost} 点（≈{lost / (double)_samplesPerChannel:F2} 批）。", "AI");
                }


                // 1) 原始矩阵入队（后台转工程值 + 滤波）
                _queue.Enqueue(new Item(device, raw, current, last));

                // 2) 立刻把原始矩阵回调给窗体（UI/落盘）
                //    —— 这行是轻量的，窗体里写入 DaqAIContext 就完全保留你现有两行风格 —— 
                OnRawBatch?.Invoke(device, raw, current, last);


                #region 仅针对本设备的“电流类”通道，取最后一个样本做快速工程值换算并上报

                try
                {
                    // —— 修改 fast 分支：所有通道都写入 _lastFastValue —— //
                    var devRecs = _enabled
                        .Where(r => r.物理通道.StartsWith(device + "/"))
                        .OrderBy(r => r.序号)
                        .ToList();

                    var chCount = raw.GetLength(0);
                    var lastCol = raw.GetLength(1) - 1;
                    if (lastCol >= 0)
                        for (var c = 0; c < chCount; c++)
                        {
                            var rec = devRecs[c];

                            // 工程值换算（电压→工程值）
                            var v = raw[c, lastCol];
                            var eng = (v - rec.零位漂移) * rec.变换斜率 + rec.变换截距;

                            // 应用动态置零（工程值域）
                            if (_zeroOffsets.TryGetValue(rec.参数名, out var z))
                                eng -= z;

                            // ① 对所有参数名都更新 fast 快照（包括 Pressure_1 / Pressure_2 / Force）
                            _lastFastValue[rec.参数名] = eng;

                            // ② 仅对 EPB 电流触发低时延事件（保持原有行为）
                            var epbCh = TryParseEpbChannel(rec.参数名);
                            if (epbCh >= 1 && epbCh <= 12)
                                OnFastEpbCurrent?.Invoke(epbCh, eng, current);
                        }
                }
                catch
                {
                    // 快速分支的异常不要影响主流程
                }

                #endregion


                // 下一轮
                //reader.BeginReadMultiSample(_samplesPerChannel, again, task);
                //_lastTs = current;

                // ⑧ 更新本设备的时钟
                clk.Last = current;
                clk.Samples += n;

            }
            catch (DaqException ex)
            {
                _log.Error($"{device} 回调异常（DAQ）：{ex.Message}", "AI", ex);
                RestartDevice(device);
            }
            catch (Exception ex)
            {
                _log.Error($"{device} 回调异常：{ex}", "AI", ex);
                RestartDevice(device);
            }
        }

        // —— 后台线程：转工程值 + 滤波 + 更新快照 + 可选回调 —— //
        private async Task ProcessLoop()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    if (!_queue.TryDequeue(out var item))
                    {
                        await Task.Delay(1, _cts.Token);
                        continue;
                    }

                    // 转工程值（使用配置）
                    var eng = ConvertToEngineering(item.Raw, item.Device);
                    // 滤波（每通道独立中值/降点，与你项目一致）
                    var engFiltered = MedianFilterEachChannel(eng, _medianLens);

                    // 刷新“最近值”供控制逻辑查询（**改动：写入 _lastFilteredValue**）
                    UpdateLastSnapshot(engFiltered, item.Device);


                    // 4) 生成发给 UI 的绝对值副本（不修改 engFiltered）
                    //    这样 UI 看到的是绝对值，但内部仍保留带符号的数据用于控制/记录等。
                    var uiEng = MakeEngineeringAbsoluteCopy(engFiltered);

                    // 5) 通知 UI（全通道、已滤波、已取绝对值的工程值）
                    OnEngBatch?.Invoke(item.Device, uiEng, item.Current, item.Last);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _log.Error($"AI 后台处理异常：{ex}", "AI", ex);
            }
        }

        /// <summary>
        ///     返回一个新的二维数组，该数组为源数组元素的绝对值副本，源数组不被修改。
        ///     适用于 channels x samples 的 double[,] 格式数据。
        /// </summary>
        /// <param name="eng">源工程值数组（channels x samples），允许为 null。</param>
        /// <returns>
        ///     新的二维数组（与源数组维度相同）或 null（当源为 null 时）。
        /// </returns>
        private static double[,] MakeEngineeringAbsoluteCopy(double[,] eng)
        {
            if (eng == null) return null;

            var dim0 = eng.GetLength(0);
            var dim1 = eng.GetLength(1);
            var copy = new double[dim0, dim1];

            // 双重循环逐元素取绝对值
            for (var i = 0; i < dim0; i++)
            for (var j = 0; j < dim1; j++)
                // Math.Abs 对 double 语义清晰
                copy[i, j] = Math.Abs(eng[i, j]);

            return copy;
        }

        // —— 工程值转换 & 快照 —— //
        private double[,] ConvertToEngineering(double[,] raw, string device)
        {
            var ch = raw.GetLength(0);
            var n = raw.GetLength(1);
            var eng = new double[ch, n];

            var devRecs = _enabled
                .Where(r => r.物理通道.StartsWith(device + "/"))
                .OrderBy(r => r.序号).ToList();

            for (var c = 0; c < ch; c++)
            {
                var rec = devRecs[c];
                var scale = rec.变换斜率;
                var offs = rec.变换截距;
                var zero = rec.零位漂移;

                for (var i = 0; i < n; i++)
                {
                    var v = raw[c, i];
                    eng[c, i] = (v - zero) * scale + offs;

                    // —— 新增：应用动态置零（工程值维度）——
                    if (_zeroOffsets.TryGetValue(rec.参数名, out var z))
                        eng[c, i] -= z;
                }
            }

            return eng;
        }

        private double[,] MedianFilterEachChannel(double[,] src, int medianLens)
        {
            var ch = src.GetLength(0);
            var n = src.GetLength(1);
            var dst = new double[ch, n];

            for (var c = 0; c < ch; c++)
            {
                // 拆出 1 列
                var buf = new double[n];
                for (var i = 0; i < n; i++) buf[i] = src[c, i];

                // 走你项目里的滤波（ClsDataFilter）
                var filt = ClsDataFilter.MakeMedianFilterReducePoint(ref buf, medianLens);

                // 填回
                var copyLen = Math.Min(filt.Length, n);
                for (var i = 0; i < copyLen; i++) dst[c, i] = filt[i];
                // 若降点长度变短，尾部补最后一个样本
                for (var i = copyLen; i < n; i++) dst[c, i] = filt[copyLen - 1];
            }

            return dst;
        }

        /// <summary>
        ///     将已滤波的工程值最后一个样本写入到 _lastFilteredValue（UI/统计用）。
        ///     与以前不同：不再覆盖 _lastFastValue（以避免破坏控制用的低延迟读数）。
        /// </summary>
        /// <param name="engFiltered">滤波后的工程值矩阵（channels x samples）。</param>
        /// <param name="device">设备名（"Dev1" 或 "Dev2"）。</param>
        private void UpdateLastSnapshot(double[,] engFiltered, string device)
        {
            var devRecs = _enabled
                .Where(r => r.物理通道.StartsWith(device + "/"))
                .OrderBy(r => r.序号).ToList();

            var ch = engFiltered.GetLength(0);
            var n = engFiltered.GetLength(1);
            for (var c = 0; c < ch; c++)
            {
                var rec = devRecs[c];
                // 写入滤波后的快照（不覆盖 fast）
                _lastFilteredValue[rec.参数名] = engFiltered[c, n - 1];
            }
        }

        // —— 工具 —— //
        private NIDaqTask CreateAiTask(string name, string[] channels, double aiMin, double aiMax,
            AITerminalConfiguration term)
        {
            var task = new NIDaqTask(name);
            foreach (var ch in channels)
                task.AIChannels.CreateVoltageChannel(ch, "", term, aiMin, aiMax, AIVoltageUnits.Volts);

            task.Timing.ConfigureSampleClock("", _sampleRate, SampleClockActiveEdge.Rising,
                SampleQuantityMode.ContinuousSamples, _samplesPerChannel);
            //task.Stream.ConfigureInputBuffer(0);

            task.Control(TaskAction.Verify);
            task.Start();
            return task;
        }

        private void BuildColumnIndex(IEnumerable<AiConfigDetailRecord> all, string dev, string[] physicals,
            Dictionary<string, int> dict)
        {
            var devRecs = all.Where(r => r.物理通道.StartsWith(dev + "/")).OrderBy(r => r.序号).ToList();
            for (var i = 0; i < physicals.Length; i++)
            {
                var rec = devRecs[i];
                dict[rec.参数名] = i;
            }
        }

        private void RestartDevice(string device)
        {
            try
            {
                if (device == "Dev1" && _task1 != null)
                {
                    try
                    {
                        _task1.Stop();
                    }
                    catch
                    {
                    }

                    try
                    {
                        _task1.Dispose();
                    }
                    catch
                    {
                    }

                    _task1 = null;
                    _reader1 = null;
                    if (_dev1Channels.Length > 0)
                    {
                        _task1 = CreateAiTask("Dev1_AI", _dev1Channels, -10, 10, AITerminalConfiguration.Rse);
                        _reader1 = new AnalogMultiChannelReader(_task1.Stream) { SynchronizeCallbacks = false };
                        _reader1.BeginReadMultiSample(_samplesPerChannel, Dev1Callback, _task1);
                        _log.Warn("Dev1 已重建采集任务并恢复。", "AI");
                    }
                }
                else if (device == "Dev2" && _task2 != null)
                {
                    try
                    {
                        _task2.Stop();
                    }
                    catch
                    {
                    }

                    try
                    {
                        _task2.Dispose();
                    }
                    catch
                    {
                    }

                    _task2 = null;
                    _reader2 = null;
                    if (_dev2Channels.Length > 0)
                    {
                        _task2 = CreateAiTask("Dev2_AI", _dev2Channels, -10, 10, AITerminalConfiguration.Rse);
                        _reader2 = new AnalogMultiChannelReader(_task2.Stream) { SynchronizeCallbacks = false };
                        _reader2.BeginReadMultiSample(_samplesPerChannel, Dev2Callback, _task2);
                        _log.Warn("Dev2 已重建采集任务并恢复。", "AI");
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error($"重建 {device} 失败：{ex.Message}", "AI", ex);
            }
        }

        // —— 后台处理队列，避免在 DAQ 回调里阻塞 —— //
        private record Item(string Device, double[,] Raw, DateTime Current, DateTime Last)
        {
            public string Device { get; } = Device;
            public double[,] Raw { get; } = Raw;
            public DateTime Current { get; } = Current;
            public DateTime Last { get; } = Last;
        }
    }
}