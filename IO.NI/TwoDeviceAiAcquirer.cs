using System;
using System.CodeDom;
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
using static DataOperation.ClsDataFilter;

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


        // 以设备处理矩阵 channels × samples 为例
        private ClsDataFilter.MedianStreamCausal _dev1MedianCausal;         // 因果，无延迟（控制/实时）
        private ClsDataFilter.MedianStreamCausal _dev2MedianCausal;         // 因果，无延迟（控制/实时）
        private ClsDataFilter.MedianStreamSymmetric _medianSymmetric;   // 对称，有延迟（显示/报表）

        // 你的配置：单侧点数（等同 UI 的 “Max. smoothing width on one side”）
        private readonly int _medianHalfWidth = 31;
        // 你的通道数（与 eng 矩阵第 0 维一致）
        private readonly int _channels;

        /// <summary>fast 分支的低时延滤波策略。</summary>
        private readonly FastFilter _fastFilter = new FastFilter(
            medianK: 9,      // 3 或 5，推荐 5
            ewmaAlpha: 0.4,  // 0.3~0.6 之间调
            maxSlewAperSec: 0 // 每秒最大电流变化（A/s），依硬件调
        );

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

            _dev1MedianCausal = new ClsDataFilter.MedianStreamCausal(_dev1Channels.Length, _medianHalfWidth,
                MedianSelectPointsMode.OnlyPrevious); // 控制用因果滤波
                
            _dev2MedianCausal = new ClsDataFilter.MedianStreamCausal(_dev2Channels.Length, _medianHalfWidth,
                MedianSelectPointsMode.OnlyPrevious); // 控制用因果滤波

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
        /// 写盘批次事件（后台线程触发）：
        /// device: "Dev1" | "Dev2"
        /// tsUtc:   本批次每个样本的 UTC 时间戳（长度 = n）
        /// currentsByEpb: 字典，键为 1..12 的 EPB 通道号，值为该通道的电流数组（长度 = n）
        /// pressureGroup1: 组1（EPB1..6）对应的压力数组（长度 = n；若不存在则为 null）
        /// pressureGroup2: 组2（EPB7..12）对应的压力数组（长度 = n；若不存在则为 null）
        /// </summary>
        public event Action<string, DateTime[], Dictionary<int, double[]>, double[], double[]> OnDiskBatch;



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
            //if (_lastFastValue.TryGetValue(key, out var vFast)) return vFast;
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
                if (reader is null) return; // 任务已停止，不处理

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
                /*var hostDt = (hostNow - last).TotalSeconds;
                var expectDt = n / _sampleRate;
                if (hostDt > expectDt * 1.5)             // 系数可按经验调
                {
                    var lost = (int)Math.Round(hostDt * _sampleRate) - n;
                    if (lost > 0)
                        _log.Warn($"[{device}] 疑似丢样：hostΔt={hostDt:F4}s 期望={expectDt:F4}s 约缺 {lost} 点（≈{lost / (double)_samplesPerChannel:F2} 批）。", "AI");
                }*/


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
                            // var eng = (v - rec.零位漂移) * rec.变换斜率 + rec.变换截距;
                            //
                            // // 应用动态置零（工程值域）
                            // if (_zeroOffsets.TryGetValue(rec.参数名, out var z))
                            //     eng -= z;

                            // —— 批内聚合：尾部中值/截尾均值/最后样本 —— //
                            var eng = ComputeFastRepresentative(raw, c, lastCol, rec, current);


                            // —— 新增：低时延稳态快照 —— //
                            eng = _fastFilter.Update(rec.参数名, eng, current);

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

        #region  fast 快照的批内聚合选项。

        /// <summary>
        /// fast 快照的批内聚合选项。
        /// </summary>
        private sealed class FastSnapshotOptions
        {
            /// <summary>聚合模式。</summary>
            public enum ModeKind { LastSample, TailMedian, TailTrimmedMean }

            /// <summary>使用的聚合模式（默认 TailMedian）。</summary>
            public ModeKind Mode { get; set; } = ModeKind.TailMedian;

            /// <summary>
            /// 尾部参与聚合的样本数 K（建议奇数 3/5/7/9）。
            /// 仅当 Mode=TailMedian 或 TailTrimmedMean 时生效。
            /// </summary>
            public int TailCount { get; set; } = 5;

            /// <summary>
            /// 截尾比例（0..0.45），仅对 TailTrimmedMean 生效。
            /// 例如 0.2 表示两端各截去 20% 再求均值。
            /// </summary>
            public double TrimRatio { get; set; } = 0.2;
        }

        /// <summary>fast 快照批内聚合选项（可按需改默认值）。</summary>
        private readonly FastSnapshotOptions _fastSnap = new FastSnapshotOptions
        {
            Mode = FastSnapshotOptions.ModeKind.TailMedian,
            TailCount = 5,
            TrimRatio = 0.2
        };

        /// <summary>
        /// 计算某通道在“当前批次”上的鲁棒代表值：
        /// - LastSample：取最后一个样本；
        /// - TailMedian：取批尾 K 点中值；
        /// - TailTrimmedMean：批尾 K 点按比例截尾后的均值；
        /// 然后再交给外层的 _fastFilter.Update 做因果平滑与限速。
        /// </summary>
        /// <param name="raw">当前批次原始电压数组 [ch, n]。</param>
        /// <param name="ch">通道索引。</param>
        /// <param name="lastCol">最后一列索引（n-1）。</param>
        /// <param name="rec">该通道的配置记录（用于电压→工程值）。</param>
        /// <param name="now">当前主机时间戳，用于 fastFilter 的 dt。</param>
        /// <returns>批内聚合后的工程值代表。</returns>
        private double ComputeFastRepresentative(double[,] raw, int ch, int lastCol, dynamic rec, DateTime now)
        {
            // 工具：把“电压样本”换算为“工程值样本”（含动态置零）
            double ToEng(double v)
            {
                var eng = (v - rec.零位漂移) * rec.变换斜率 + rec.变换截距;
                if (_zeroOffsets.TryGetValue(rec.参数名, out double z)) eng -= z;
                return eng;
            }

            if (_fastSnap.Mode == FastSnapshotOptions.ModeKind.LastSample || lastCol < 0)
            {
                // 仅最后一个样本（几乎零延迟）
                return ToEng(raw[ch, lastCol]);
            }

            // 参与聚合的尾部窗口 [startCol..lastCol]
            int k = Math.Max(1, _fastSnap.TailCount);
            int startCol = Math.Max(0, lastCol - k + 1);
            int count = lastCol - startCol + 1;

            // 收集尾部 K 个样本（工程值域）
            var buf = new double[count];
            for (int j = 0, col = startCol; col <= lastCol; col++, j++)
                buf[j] = ToEng(raw[ch, col]);

            if (_fastSnap.Mode == FastSnapshotOptions.ModeKind.TailMedian)
            {
                Array.Sort(buf);                   // K ≤ 9 时排序成本极小
                return buf[count / 2];             // 中值（奇数严格中位；偶数取上中位）
            }
            else // TailTrimmedMean
            {
                Array.Sort(buf);
                int trim = (int)Math.Round(count * Math.Min(0.45, Math.Max(0.0, _fastSnap.TrimRatio)));
                int s = trim;
                int e = count - trim;              // [s, e) 保留
                if (e <= s) { s = 0; e = count; }  // 太短就退化为普通均值

                double sum = 0;
                for (int i = s; i < e; i++) sum += buf[i];
                return sum / (e - s);
            }
        }

        #endregion



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
                    //var engFiltered = MedianFilterEachChannel(eng, _medianLens); // 暂时去掉滤波
                    //var engFiltered = eng;


                    // 因果中值滤波（无延迟，控制用）
                    var engSmoothed =item.Device.Equals("Dev1") ?  _dev1MedianCausal.Process(eng) : _dev2MedianCausal.Process(eng);

                    var engFiltered = engSmoothed;


                    // 刷新“最近值”供控制逻辑查询（**改动：写入 _lastFilteredValue**）
                    UpdateLastSnapshot(engFiltered, item.Device);
                    
                    #region 生成“落盘批次”并触发 OnDiskBatch（使用 engFiltered，不取绝对值） On 2025.09.16 

                    // ====== 生成“落盘批次”并触发 OnDiskBatch（使用 engFiltered，不取绝对值） ======
                    try
                    {
                        // 1) 计算时间戳数组（以本批最后一个样本对齐 item.Current，向前按 Fs 均匀回推）
                        var n = engFiltered.GetLength(1);
                        var tsUtc = new DateTime[n];
                        double dt = 1.0 / _sampleRate;                 // 你的 Fs
                        var tStart = item.Current.ToUniversalTime().AddSeconds(-(n - 1) * dt);
                        for (int k = 0; k < n; k++) tsUtc[k] = tStart.AddSeconds(k * dt);

                        // 2) 构建“每 EPB 通道”的电流数组（从当前 device 的工程值矩阵提取）
                        var currentsByEpb = new Dictionary<int, double[]>();
                        var devRecs = _enabled
                            .Where(r => r.物理通道.StartsWith(item.Device + "/"))
                            .OrderBy(r => r.序号)
                            .ToList();
                        var chCount = engFiltered.GetLength(0);
                        for (int c = 0; c < chCount; c++)
                        {
                            var name = devRecs[c].参数名; // 形如 EPB1_current / Pressure_1
                            int epb = TryParseEpbChannel(name);
                            if (epb >= 1 && epb <= 12)
                            {
                                var arr = new double[n];
                                for (int i = 0; i < n; i++) arr[i] = engFiltered[c, i]; // 不取绝对值
                                currentsByEpb[epb] = arr;
                            }
                        }

                        // 3) 提取两组压力（多名称兜底：Pressure_1/2、Hyd1/2_pressure、P1/2）
                        int colP1 = FindColumnIndex(devRecs, "Pressure_1", "Hyd1_pressure", "P1", "Pressure1");
                        int colP2 = FindColumnIndex(devRecs, "Pressure_2", "Hyd2_pressure", "P2", "Pressure2");

                        double[] pressure1 = null, pressure2 = null;
                        if (colP1 >= 0)
                        {
                            pressure1 = new double[n];
                            for (int i = 0; i < n; i++) pressure1[i] = engFiltered[colP1, i];
                        }
                        if (colP2 >= 0)
                        {
                            pressure2 = new double[n];
                            for (int i = 0; i < n; i++) pressure2[i] = engFiltered[colP2, i];
                        }


                        #region 获取一段时间内的最大值

                        // === 基于“全数据”的峰值捕获：逐样本扫描（仅对处于捕获状态的通道进行） ===
                        try
                        {
                            if (AnyPeakArmed && tsUtc != null && tsUtc.Length > 0)
                            {
                                foreach (var kv in currentsByEpb)
                                {
                                    int epb = kv.Key;              // 1..12
                                    var data = kv.Value;           // double[n]
                                    PeakTracker tracker;
                                    if (!_peakTrackers.TryGetValue(epb, out tracker)) continue;

                                    // 仅对“已开始捕获”的通道更新
                                    bool active;
                                    lock (tracker.Sync) active = tracker.Active;
                                    if (!active) continue;

                                    // 逐样本纳入峰值统计（时间转为本地时间）
                                    for (int i = 0; i < data.Length; i++)
                                    {
                                        // tsUtc 与 data 一一对应
                                        var tLocal = tsUtc[i].ToLocalTime();
                                        var amp = data[i];
                                        lock (tracker.Sync)
                                        {
                                            if (tracker.Active) tracker.Update(amp, tLocal);
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _log?.Warn($"全数据峰值捕获更新异常（已忽略）：{ex.Message}", "AI");
                        }

                        #endregion



                        // 4) 触发“写盘批次”事件（上层订阅后直接喂给 _diskWriter.WriteBatch）
                        OnDiskBatch?.Invoke(item.Device, tsUtc, currentsByEpb, pressure1, pressure2);
                    }
                    catch (Exception ex)
                    {
                        _log?.Warn($"生成写盘批次时出现异常（已忽略）：{ex.Message}", "AI");
                    }
                    

                    #endregion
                    
                    // 4) 生成发给 UI 的绝对值副本（不修改 engFiltered）
                    //    这样 UI 看到的是绝对值，但内部仍保留带符号的数据用于控制/记录等。
                    var uiEng = MakeEngineeringAbsoluteCopy(engFiltered);

                    // 5) 通知 UI（全通道、已滤波、已取绝对值的工程值）
                    OnEngBatch?.Invoke(item.Device, uiEng, item.Current, item.Last);

                    //OnFastEpbCurrent?.Invoke(epbCh, eng, item.Current);
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
        /// 在 devRecs（本 device 的通道描述）中按多个“候选参数名”查找列索引；找不到返回 -1。
        /// </summary>
        private static int FindColumnIndex(List<AiConfigDetailRecord> devRecs, params string[] candidateNames)
        {
            if (devRecs == null || candidateNames == null) return -1;
            for (int c = 0; c < devRecs.Count; c++)
            {
                var name = devRecs[c].参数名;
                foreach (var key in candidateNames)
                {
                    if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
                        return c;
                }
            }
            return -1;
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



        #region 获取最大值相关的类和字段

        /// <summary>
        /// EPB 电流峰值结果摘要（基于“全数据”捕获）。
        /// </summary>
        public struct EpbCurrentPeak
        {
            /// <summary>EPB 物理通道（1..12）。</summary>
            public int Channel;

            /// <summary>峰值电流（A）。若期间无样本则为 0。</summary>
            public double MaxAmp;

            /// <summary>峰值发生时刻（本地时间）。</summary>
            public DateTime MaxAt;

            /// <summary>捕获开始时刻（本地时间）。</summary>
            public DateTime StartAt;

            /// <summary>捕获结束时刻（本地时间）。</summary>
            public DateTime EndAt;

            /// <summary>期间累计样本数（用于判断是否有有效样本）。</summary>
            public long SampleCount;

            /// <summary>是否仍在捕获中。</summary>
            public bool IsActive;
        }

        /// <summary> 单通道峰值跟踪器（线程安全，基于“全数据批处理”逐样本更新）。 </summary>
        private sealed class PeakTracker
        {
            public readonly object Sync = new object();
            public bool Active;
            public DateTime StartAt;
            public DateTime EndAt;
            public DateTime MaxAt;
            public double MaxAmp;
            public long SampleCount;


            /// <summary>进入捕获状态并复位统计。</summary>
            public void Arm(DateTime t0)
            {
                Active = true;
                StartAt = t0;
                EndAt = t0;
                MaxAmp = double.NegativeInfinity;
                MaxAt = t0;
                SampleCount = 0;
            }

            /// <summary>纳入一个样本（全数据逐点）。</summary>
            public void Update(double amp, DateTime tsLocal)
            {
                SampleCount++;
                if (amp > MaxAmp || SampleCount == 1)
                {
                    MaxAmp = amp;
                    MaxAt = tsLocal;
                }
                EndAt = tsLocal; // 批内最后一个样本的时间
            }

            /// <summary>结束捕获。</summary>
            public void Finish(DateTime tEndLocal)
            {
                Active = false;
                EndAt = tEndLocal;
                if (SampleCount == 0)
                {
                    MaxAmp = 0.0;
                    MaxAt = StartAt;
                }
            }

            /// <summary>生成快照。</summary>
            public EpbCurrentPeak Snapshot(int ch)
            {
                return new EpbCurrentPeak
                {
                    Channel = ch,
                    MaxAmp = double.IsNegativeInfinity(MaxAmp) ? 0.0 : MaxAmp,
                    MaxAt = MaxAt,
                    StartAt = StartAt,
                    EndAt = EndAt,
                    SampleCount = SampleCount,
                    IsActive = Active
                };
            }
        }



        // —— 字段：每个 EPB 通道一个峰值跟踪器 —— //
        private readonly ConcurrentDictionary<int, PeakTracker> _peakTrackers =
            new ConcurrentDictionary<int, PeakTracker>();

        /// <summary>是否存在任意处于捕获状态的通道（用于快速短路）。</summary>
        private bool AnyPeakArmed
        {
            get
            {
                foreach (var kv in _peakTrackers)
                {
                    var t = kv.Value;
                    lock (t.Sync)
                    {
                        if (t.Active) return true;
                    }
                }
                return false;
            }
        }

        #endregion


        #region 获取最大值的相关的公共方法

        /// <summary>
        /// 开始对指定 EPB 通道（1..12）进行“正向上电段”的电流峰值捕获（基于全数据）。
        /// 建议在“下达正向上电指令”后立刻调用。
        /// </summary>
        /// <param name="epbChannel">EPB 物理通道（1..12）。</param>
        public void BeginEpbCurrentPeak(int epbChannel)
        {
            if (epbChannel < 1 || epbChannel > 12) return;
            var t = _peakTrackers.GetOrAdd(epbChannel, _ => new PeakTracker());
            lock (t.Sync)
            {
                t.Arm(DateTime.Now);
            }
            _log?.Info($"EPB[{epbChannel}]（全数据）峰值捕获开始。", "AI");
        }

        /// <summary>
        /// 结束对指定 EPB 通道的峰值捕获，并返回本段期间的峰值结果（基于全数据）。
        /// 建议在“检测到断电/结束指令”后调用。
        /// </summary>
        /// <param name="epbChannel">EPB 物理通道（1..12）。</param>
        /// <returns>峰值结果（若期间无样本，MaxAmp=0，SampleCount=0）。</returns>
        public EpbCurrentPeak EndEpbCurrentPeak(int epbChannel)
        {
            var res = new EpbCurrentPeak { Channel = epbChannel };
            PeakTracker t;
            if (!_peakTrackers.TryGetValue(epbChannel, out t)) return res;

            lock (t.Sync)
            {
                if (t.Active)
                {
                    // 若在两批之间结束，就用当前本地时刻封口
                    t.Finish(DateTime.Now);
                }
                res = t.Snapshot(epbChannel);
            }
            _log?.Info($"EPB[{epbChannel}]（全数据）峰值捕获结束：Max={res.MaxAmp:F3}A @{res.MaxAt:HH:mm:ss.fff}，Samples={res.SampleCount}", "AI");
            return res;
        }

        /// <summary>
        /// 不结束捕获，实时窥视当前峰值（基于全数据已处理到的样本）。
        /// </summary>
        public EpbCurrentPeak PeekEpbCurrentPeak(int epbChannel)
        {
            PeakTracker t;
            if (!_peakTrackers.TryGetValue(epbChannel, out t))
                return new EpbCurrentPeak { Channel = epbChannel };

            lock (t.Sync) return t.Snapshot(epbChannel);
        }

        /// <summary>
        /// 取消并清除当前峰值捕获（本段数据作废）。
        /// </summary>
        public void CancelEpbCurrentPeak(int epbChannel)
        {
            PeakTracker t;
            if (_peakTrackers.TryGetValue(epbChannel, out t))
            {
                lock (t.Sync)
                {
                    t.Active = false;
                    t.SampleCount = 0;
                    t.MaxAmp = 0.0;
                }
            }
            _log?.Warn($"EPB[{epbChannel}]（全数据）峰值捕获已取消。", "AI");
        }

        #endregion







    }




}