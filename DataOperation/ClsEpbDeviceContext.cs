using System;
using MtEmbTest;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataOperation
{
    /// <summary>
    /// EPB设备特定的数据实体 - 支持Dev1/Dev2的不同通道配置
    /// </summary>
    public class EpbDeviceData
    {
        /// <summary>数据接收时间</summary>
        public DateTime RecvTime { get; set; }

        /// <summary>上次数据时间（用于时间戳插值）</summary>
        public DateTime LastRecvTime { get; set; }

        /// <summary>原始数据矩阵 [通道, 样本]</summary>
        public double[,] RawData { get; set; }

        /// <summary>EPB循环计数器映射 (EPB通道号 -> 计数器)</summary>
        public Dictionary<int, int> EpbCycleCounters { get; set; } = new();
    }

    /// <summary>
    /// EPB设备数据上下文 - 每个设备独立管理数据存盘
    /// 支持Dev1/Dev2的不同通道配置和独立文件存储
    /// </summary>
    public class EpbDeviceContext
    {
        // 配置字段
        public string DeviceName { get; }
        private readonly int MaxLens;
        private readonly double StoreTimeMinutes;
        private readonly string StorePath;
        private readonly List<int> EpbChannels; // 该设备管理的EPB通道号列表
        private readonly Dictionary<string, int> ChannelMapping; // 参数名->通道索引映射
        private readonly bool HasPressure1; // 是否有压力1
        private readonly bool HasPressure2; // 是否有压力2
        private readonly bool HasForce; // 是否有夹紧力

        // 数据队列
        private readonly ConcurrentQueue<EpbDeviceData> EpbRawData = new();

        // 文件管理
        private readonly SemaphoreSlim rawFileLock = new(1);
        private DateTime _lastFlushTime = DateTime.MinValue;
        private string currentRawFileName;
        private int FileCounter = 0;
        private int SaveRawCounter = 0;

        // EPB循环计数器（设备级别管理）
        private readonly ConcurrentDictionary<int, int> _epbCycleCounters = new();

        /// <summary>
        /// 初始化EPB设备上下文
        /// </summary>
        /// <param name="deviceName">设备名称 (Dev1/Dev2)</param>
        /// <param name="maxLens">队列最大长度</param>
        /// <param name="storeTimeMinutes">文件切换时间间隔</param>
        /// <param name="storePath">存储路径</param>
        /// <param name="epbChannels">该设备管理的EPB通道号列表</param>
        /// <param name="channelMapping">参数名到通道索引的映射</param>
        /// <param name="hasPressure1">是否包含压力1数据</param>
        /// <param name="hasPressure2">是否包含压力2数据</param>
        /// <param name="hasForce">是否包含夹紧力数据</param>
        public EpbDeviceContext(
            string deviceName,
            int maxLens,
            double storeTimeMinutes,
            string storePath,
            List<int> epbChannels,
            Dictionary<string, int> channelMapping,
            bool hasPressure1 = false,
            bool hasPressure2 = false,
            bool hasForce = false)
        {
            DeviceName = deviceName;
            MaxLens = maxLens;
            StoreTimeMinutes = storeTimeMinutes;
            StorePath = storePath;
            EpbChannels = epbChannels ?? new List<int>();
            ChannelMapping = channelMapping ?? new Dictionary<string, int>();
            HasPressure1 = hasPressure1;
            HasPressure2 = hasPressure2;
            HasForce = hasForce;

            _lastFlushTime = DateTime.Now;
            currentRawFileName = GenerateRawFileName();

            // 初始化EPB循环计数器
            foreach (var epbChannel in EpbChannels)
            {
                _epbCycleCounters[epbChannel] = 0;
            }

            // 清空初始队列
            var lens = EpbRawData.Count;
            for (var i = 0; i < lens; i++)
                EpbRawData.TryDequeue(out _);
        }

        /// <summary>生成原始数据文件名</summary>
        private string GenerateRawFileName()
        {
            FileCounter++;
            return $"{StorePath}\\EPB_{DeviceName}_Raw_{FileCounter}.bin";
        }

        /// <summary>增加EPB循环计数</summary>
        public void IncrementEpbCycleCounter(int epbChannel)
        {
            _epbCycleCounters.AddOrUpdate(epbChannel, 1, (key, oldValue) => oldValue + 1);
        }

        /// <summary>获取EPB循环计数</summary>
        public int GetEpbCycleCounter(int epbChannel)
        {
            return _epbCycleCounters.TryGetValue(epbChannel, out int count) ? count : 0;
        }

        /// <summary>入队原始数据（从Acq_OnRawBatch调用）</summary>
        public void EnqueueRawData(double[,] rawData, DateTime current, DateTime last)
        {
            var deviceData = new EpbDeviceData
            {
                RecvTime = current,
                LastRecvTime = last,
                RawData = rawData,
                EpbCycleCounters = new Dictionary<int, int>()
            };

            // 复制当前的EPB循环计数器状态
            foreach (var epbChannel in EpbChannels)
            {
                deviceData.EpbCycleCounters[epbChannel] = GetEpbCycleCounter(epbChannel);
            }

            EpbRawData.Enqueue(deviceData);
            if (EpbRawData.Count > MaxLens)
            {
                EpbRawData.TryDequeue(out _);
            }
        }

        /// <summary>
        /// EPB设备数据批量落盘异步方法
        /// 数据格式：时间戳(8B) + [EPB Counter(4B) + EPB Current(8B)] × N + 压力(8B×M) + 夹紧力(8B)
        /// </summary>
        public async Task FlushRawToDiskAsync()
        {
            await rawFileLock.WaitAsync();
            var lens = EpbRawData.Count;

            if (lens < 1)
            {
                rawFileLock.Release();
                return;
            }

            // 检查是否需要切换文件
            if ((DateTime.Now - _lastFlushTime).TotalMinutes >= StoreTimeMinutes)
            {
                currentRawFileName = GenerateRawFileName();
                _lastFlushTime = DateTime.Now;
            }

            SaveRawCounter++;

            // 跳过首次写盘避免脏数据
            if (SaveRawCounter <= 1)
            {
                for (var i = 0; i < lens; i++)
                    EpbRawData.TryDequeue(out _);
                rawFileLock.Release();
                return;
            }

            // 计算单条记录大小
            var recordSize = CalculateRecordSize();
            var buffer = new byte[lens * recordSize * 100]; // 预留足够空间（考虑样本数）
            var offset = 0;

            FileStream fs = null;

            try
            {
                // 逐条记录处理
                for (var i = 0; i < lens; i++)
                {
                    if (!EpbRawData.TryDequeue(out var deviceData)) continue;

                    var rawMatrix = deviceData.RawData;
                    var channels = rawMatrix.GetLength(0);
                    var samples = rawMatrix.GetLength(1);

                    // 为每个样本生成时间戳并写入记录
                    var lastTicks = deviceData.LastRecvTime.Ticks;
                    var spanTicks = (deviceData.RecvTime - deviceData.LastRecvTime).Ticks;
                    var stepTicks = spanTicks > 0
                        ? spanTicks / (double)samples
                        : TimeSpan.FromMilliseconds(ClsGlobal.DaqFrequency > 0 ? 1000.0 / ClsGlobal.DaqFrequency : 1.0).Ticks;

                    for (var sample = 0; sample < samples; sample++)
                    {
                        // 计算当前样本的时间戳
                        var sampleTicks = lastTicks + (long)Math.Round(stepTicks * (sample + 1));
                        var sampleTime = new DateTime(sampleTicks, DateTimeKind.Local);

                        // 写入时间戳 (8字节)
                        var timeBytes = BitConverter.GetBytes(sampleTime.ToFileTime());
                        Buffer.BlockCopy(timeBytes, 0, buffer, offset, 8);
                        offset += 8;

                        // 写入EPB电流数据：每个EPB包含Counter(4B) + Current(8B)
                        foreach (var epbChannel in EpbChannels)
                        {
                            // 获取该EPB的通道索引
                            var paramName = $"EPB{epbChannel}_current";
                            if (ChannelMapping.TryGetValue(paramName, out var channelIndex) && channelIndex < channels)
                            {
                                // 写入循环计数器
                                var counterBytes = BitConverter.GetBytes(deviceData.EpbCycleCounters.GetValueOrDefault(epbChannel, 0));
                                Buffer.BlockCopy(counterBytes, 0, buffer, offset, 4);
                                offset += 4;

                                // 写入电流值
                                var currentValue = rawMatrix[channelIndex, sample];
                                var currentBytes = BitConverter.GetBytes(currentValue);
                                Buffer.BlockCopy(currentBytes, 0, buffer, offset, 8);
                                offset += 8;
                            }
                            else
                            {
                                // 如果没有对应通道，写入默认值
                                var counterBytes = BitConverter.GetBytes(0);
                                Buffer.BlockCopy(counterBytes, 0, buffer, offset, 4);
                                offset += 4;

                                var currentBytes = BitConverter.GetBytes(0.0);
                                Buffer.BlockCopy(currentBytes, 0, buffer, offset, 8);
                                offset += 8;
                            }
                        }

                        // 写入压力数据
                        if (HasPressure1)
                        {
                            var pressure1Value = 0.0;
                            if (ChannelMapping.TryGetValue("Pressure_1", out var p1Index) && p1Index < channels)
                                pressure1Value = rawMatrix[p1Index, sample];

                            var pressure1Bytes = BitConverter.GetBytes(pressure1Value);
                            Buffer.BlockCopy(pressure1Bytes, 0, buffer, offset, 8);
                            offset += 8;
                        }

                        if (HasPressure2)
                        {
                            var pressure2Value = 0.0;
                            if (ChannelMapping.TryGetValue("Pressure_2", out var p2Index) && p2Index < channels)
                                pressure2Value = rawMatrix[p2Index, sample];

                            var pressure2Bytes = BitConverter.GetBytes(pressure2Value);
                            Buffer.BlockCopy(pressure2Bytes, 0, buffer, offset, 8);
                            offset += 8;
                        }

                        // 写入夹紧力数据
                        if (HasForce)
                        {
                            var forceValue = 0.0;
                            if (ChannelMapping.TryGetValue("Force", out var forceIndex) && forceIndex < channels)
                                forceValue = rawMatrix[forceIndex, sample];

                            var forceBytes = BitConverter.GetBytes(forceValue);
                            Buffer.BlockCopy(forceBytes, 0, buffer, offset, 8);
                            offset += 8;
                        }
                    }
                }

                // 异步批量写入磁盘
                fs = new FileStream(
                    currentRawFileName,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    8192,
                    FileOptions.WriteThrough | FileOptions.Asynchronous);

                await fs.WriteAsync(buffer, 0, offset);
                await fs.FlushAsync();
            }
            catch (Exception ex)
            {
                var logFilePath = Path.Combine(Directory.GetCurrentDirectory(),
                    $"EPB_{DeviceName}_WriteDiskErrorLog.txt");
                var errorMessage = $"[{DateTime.Now}] EPB_{DeviceName} flush error: {ex.Message}";
                File.AppendAllText(logFilePath, errorMessage + Environment.NewLine);
            }
            finally
            {
                rawFileLock.Release();
                fs?.Dispose();
            }
        }

        /// <summary>计算单条记录的大小</summary>
        private int CalculateRecordSize()
        {
            var size = 8; // 时间戳
            size += EpbChannels.Count * 12; // 每个EPB: Counter(4B) + Current(8B)
            if (HasPressure1) size += 8;
            if (HasPressure2) size += 8;
            if (HasForce) size += 8;
            return size;
        }
    }
}