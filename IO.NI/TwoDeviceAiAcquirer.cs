using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
    /// 双设备（Dev1/Dev2）AI 连续采样管理器：
    /// 1）按 AIConfigDetail 构建通道；
    /// 2）分别创建 Dev1/Dev2 的连续采样任务；
    /// 3）Stopwatch 生成时间戳（与 FrmMainMonitor 一致的 Current/Last）；
    /// 4）原始矩阵通过回调委托给 UI/落盘（<b>原始</b> double[通道,样本]）；
    /// 5）内部把原始矩阵转工程值并做滤波，更新“最近值”并可回调（供控制逻辑/可选 UI）。
    /// </summary>
    public sealed class TwoDeviceAiAcquirer : IDisposable
    {
        // —— 供窗体订阅的两个回调 —— //
        // 原始电压数据（未标定、未滤波）：UI/落盘在窗体里直接调用 DaqAIContext.EnqueueRawData/StatData
        public event Action<string /*Dev1|Dev2*/, double[,], DateTime /*current*/, DateTime /*last*/> OnRawBatch;
        // 工程值（标定 + 滤波 后的全通道矩阵）：给 UI 或调试可选使用
        public event Action<string /*Dev1|Dev2*/, double[,], DateTime /*current*/, DateTime /*last*/> OnEngBatch;

        // —— 对接控制逻辑 —— //
        public delegate double ReadCurrentDelegate(int epbChannel);
        public double ReadCurrent(int epbChannel)
            => _lastValue.TryGetValue($"EPB{epbChannel}_current", out var v) ? v : 0.0;
        public double ReadPressure(int id)
            => _lastValue.TryGetValue($"Pressure_{id}", out var v) ? v : 0.0;

        private readonly ILogger _log;
        private readonly List<AiConfigDetailRecord> _enabled;
        private readonly string[] _dev1Channels, _dev2Channels;
        private readonly int _samplesPerChannel;
        private readonly double _sampleRate;
        private readonly int _medianLens;

        // NI 任务
        private NIDaqTask _task1, _task2;
        private AnalogMultiChannelReader _reader1, _reader2;

        // 时间戳（模仿 FrmMainMonitor）
        private readonly Stopwatch _sw = new();
        private DateTime _t0 = DateTime.Now;
        private long _ts0;
        private DateTime _lastTs = DateTime.Now;

        // 最近的工程值快照（供 ReadCurrent/ReadPressure）
        private readonly ConcurrentDictionary<string, double> _lastValue = new();

        // 参数名 -> 本设备内列索引
        private readonly Dictionary<string, int> _colIndexDev1 = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _colIndexDev2 = new(StringComparer.OrdinalIgnoreCase);

        // —— 后台处理队列，避免在 DAQ 回调里阻塞 —— //
        private record Item(string Device, double[,] Raw, DateTime Current, DateTime Last)
        {
            public string Device { get; } = Device;
            public double[,] Raw { get; } = Raw;
            public DateTime Current { get; } = Current;
            public DateTime Last { get; } = Last;
        }

        private readonly ConcurrentQueue<Item> _queue = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _worker;

        public TwoDeviceAiAcquirer(
            AiConfigDetail cfg,
            double sampleRate,
            int samplesPerChannel,
            int medianLens,                 // 走 ClsDataFilter 的中值窗长（用你全局配置传入）
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

        public void Start(double aiMin = -10, double aiMax = 10, AITerminalConfiguration term = AITerminalConfiguration.Rse)
        {
            Stop();
            if (!_sw.IsRunning)
            {
                _t0 = DateTime.Now;
                _sw.Start();
                _ts0 = _sw.ElapsedMilliseconds;
                _lastTs = _t0;
            }

            if (_dev1Channels.Length > 0)
            {
                _task1 = CreateAiTask("Dev1_AI", _dev1Channels, aiMin, aiMax, term);
                _reader1 = new AnalogMultiChannelReader(_task1.Stream) { SynchronizeCallbacks = true };
                _reader1.BeginReadMultiSample(_samplesPerChannel, Dev1Callback, _task1);
            }

            if (_dev2Channels.Length > 0)
            {
                _task2 = CreateAiTask("Dev2_AI", _dev2Channels, aiMin, aiMax, term);
                _reader2 = new AnalogMultiChannelReader(_task2.Stream) { SynchronizeCallbacks = true };
                _reader2.BeginReadMultiSample(_samplesPerChannel, Dev2Callback, _task2);
            }

            _log.Info($"AI 采集启动：Dev1[{_dev1Channels.Length}] Dev2[{_dev2Channels.Length}] Fs={_sampleRate}Hz N={_samplesPerChannel}", "AI");
        }

        public void Stop()
        {
            try { _task1?.Stop(); } catch { }
            try { _task2?.Stop(); } catch { }
            try { _task1?.Dispose(); } catch { }
            try { _task2?.Dispose(); } catch { }
            _task1 = null; _task2 = null; _reader1 = null; _reader2 = null;
        }

        public void Dispose()
        {
            Stop();
            _cts.Cancel();
            try { _worker?.Wait(1000); } catch { }
            _cts.Dispose();
        }

        // —— DAQ 回调：只负责 EndRead + 入队 + 立刻发起下一次 BeginRead —— //
        private void Dev1Callback(IAsyncResult ar) => OnAiBatch(ar, _reader1, _colIndexDev1, "Dev1", Dev1Callback);
        private void Dev2Callback(IAsyncResult ar) => OnAiBatch(ar, _reader2, _colIndexDev2, "Dev2", Dev2Callback);

        private void OnAiBatch(IAsyncResult ar, AnalogMultiChannelReader reader,
                               Dictionary<string, int> colIndex, string device,
                               AsyncCallback again)
        {
            try
            {
                var task = (NIDaqTask)ar.AsyncState;
                var raw = reader.EndReadMultiSample(ar); // [ch, n]

                long nowMs = _sw.ElapsedMilliseconds;
                var current = _t0.AddMilliseconds(nowMs - _ts0);
                var last = _lastTs;

                // 1) 原始矩阵入队（后台转工程值 + 滤波）
                _queue.Enqueue(new Item(device, raw, current, last));

                // 2) 立刻把原始矩阵回调给窗体（UI/落盘）
                //    —— 这行是轻量的，窗体里写入 DaqAIContext 就完全保留你现有两行风格 —— 
                OnRawBatch?.Invoke(device, raw, current, last);

                // 下一轮
                reader.BeginReadMultiSample(_samplesPerChannel, again, task);
                _lastTs = current;
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

                    // 刷新“最近值”供控制逻辑查询
                    UpdateLastSnapshot(engFiltered, item.Device);

                    // 可选给 UI（全通道、已滤波工程值）
                    OnEngBatch?.Invoke(item.Device, engFiltered, item.Current, item.Last);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.Error($"AI 后台处理异常：{ex}", "AI", ex);
            }
        }

        // —— 工程值转换 & 快照 —— //
        private double[,] ConvertToEngineering(double[,] raw, string device)
        {
            int ch = raw.GetLength(0);
            int n = raw.GetLength(1);
            var eng = new double[ch, n];

            var devRecs = _enabled
                .Where(r => r.物理通道.StartsWith(device + "/"))
                .OrderBy(r => r.序号).ToList();

            for (int c = 0; c < ch; c++)
            {
                var rec = devRecs[c];
                double scale = rec.变换斜率;
                double offs = rec.变换截距;
                double zero = rec.零位漂移;

                for (int i = 0; i < n; i++)
                {
                    var v = raw[c, i];
                    eng[c, i] = (v - zero) * scale + offs;
                }
            }
            return eng;
        }

        private double[,] MedianFilterEachChannel(double[,] src, int medianLens)
        {
            int ch = src.GetLength(0);
            int n = src.GetLength(1);
            var dst = new double[ch, n];

            for (int c = 0; c < ch; c++)
            {
                // 拆出 1 列
                var buf = new double[n];
                for (int i = 0; i < n; i++) buf[i] = src[c, i];

                // 走你项目里的滤波（ClsDataFilter）
                var filt = ClsDataFilter.MakeMedianFilterReducePoint(ref buf, medianLens);

                // 填回
                int copyLen = Math.Min(filt.Length, n);
                for (int i = 0; i < copyLen; i++) dst[c, i] = filt[i];
                // 若降点长度变短，尾部补最后一个样本
                for (int i = copyLen; i < n; i++) dst[c, i] = filt[copyLen - 1];
            }
            return dst;
        }

        private void UpdateLastSnapshot(double[,] engFiltered, string device)
        {
            var devRecs = _enabled
                .Where(r => r.物理通道.StartsWith(device + "/"))
                .OrderBy(r => r.序号).ToList();

            int ch = engFiltered.GetLength(0);
            int n = engFiltered.GetLength(1);
            for (int c = 0; c < ch; c++)
            {
                var rec = devRecs[c];
                _lastValue[rec.参数名] = engFiltered[c, n - 1];
            }
        }

        // —— 工具 —— //
        private NIDaqTask CreateAiTask(string name, string[] channels, double aiMin, double aiMax, AITerminalConfiguration term)
        {
            var task = new NIDaqTask(name);
            foreach (var ch in channels)
                task.AIChannels.CreateVoltageChannel(ch, "", term, aiMin, aiMax, AIVoltageUnits.Volts);

            task.Timing.ConfigureSampleClock("", _sampleRate, SampleClockActiveEdge.Rising,
                SampleQuantityMode.ContinuousSamples, (int)_samplesPerChannel);
            task.Control(TaskAction.Verify);
            task.Start();
            return task;
        }

        private void BuildColumnIndex(IEnumerable<AiConfigDetailRecord> all, string dev, string[] physicals, Dictionary<string, int> dict)
        {
            var devRecs = all.Where(r => r.物理通道.StartsWith(dev + "/")).OrderBy(r => r.序号).ToList();
            for (int i = 0; i < physicals.Length; i++)
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
                    try { _task1.Stop(); } catch { }
                    try { _task1.Dispose(); } catch { }
                    _task1 = null; _reader1 = null;
                    if (_dev1Channels.Length > 0)
                    {
                        _task1 = CreateAiTask("Dev1_AI", _dev1Channels, -10, 10, AITerminalConfiguration.Rse);
                        _reader1 = new AnalogMultiChannelReader(_task1.Stream) { SynchronizeCallbacks = true };
                        _reader1.BeginReadMultiSample(_samplesPerChannel, Dev1Callback, _task1);
                        _log.Warn("Dev1 已重建采集任务并恢复。", "AI");
                    }
                }
                else if (device == "Dev2" && _task2 != null)
                {
                    try { _task2.Stop(); } catch { }
                    try { _task2.Dispose(); } catch { }
                    _task2 = null; _reader2 = null;
                    if (_dev2Channels.Length > 0)
                    {
                        _task2 = CreateAiTask("Dev2_AI", _dev2Channels, -10, 10, AITerminalConfiguration.Rse);
                        _reader2 = new AnalogMultiChannelReader(_task2.Stream) { SynchronizeCallbacks = true };
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
    }
}
