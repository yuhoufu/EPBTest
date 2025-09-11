using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataOperation
{
    public class DaqAIData
    {
        public double[,] Data { get; set; }
        public DateTime RecvTime { get; set; }
        public DateTime LastRecvTime { get; set; }
    }


    public class DaqAIContext
    {
        public string DaqCardName { get; }
        private readonly ConcurrentQueue<DaqAIData> DaqRawData = new ConcurrentQueue<DaqAIData>();

        private readonly ConcurrentQueue<DaqAIData> DaqStatData = new ConcurrentQueue<DaqAIData>();
        private readonly SemaphoreSlim rawFileLock = new SemaphoreSlim(1);
        private readonly SemaphoreSlim statFileLock = new SemaphoreSlim(1);


        private DateTime _lastFlushTime = DateTime.MinValue;
        private readonly int Channels;
        private string currentRawFileName;
        private readonly string currentStatFileName;
        private double DaqSpanMillSec1;


        public SortedDictionary<string, int> eMBToDaqCurrentChannel = new SortedDictionary<string, int>();
        private int FileCounter;
        private readonly int MaxLens;
        public int medianLens;
        public ConcurrentDictionary<string, double> paraNameToOffset = new ConcurrentDictionary<string, double>();
        public ConcurrentDictionary<string, double> paraNameToScale = new ConcurrentDictionary<string, double>();
        public ConcurrentDictionary<string, double> paraNameToZeroValue = new ConcurrentDictionary<string, double>();
        private readonly int SamplesPerChannel;
        private int SaveRawCounter;
        private int SaveStatCounter;
        private readonly string StorePath;
        private readonly double StoreTimeMinutes;


        public DaqAIContext(string cardName, int maxLens, double storeTimeMinutes, double daqSpanMillSec, int channels,
            int samplesPerChannel, string storePath)
        {
            DaqCardName = cardName;

            MaxLens = maxLens;
            StoreTimeMinutes = storeTimeMinutes;
            DaqSpanMillSec1 = daqSpanMillSec;
            SamplesPerChannel = samplesPerChannel;
            Channels = channels;
            StorePath = storePath;
            SaveRawCounter = 0;
            SaveStatCounter = 0;
            FileCounter = 0;
            _lastFlushTime = DateTime.Now;

            currentRawFileName = GenerateRawFileName();
            currentStatFileName = GenerateStatFileName();


            var Lens = DaqRawData.Count;

            for (var i = 0; i < Lens; i++) DaqRawData.TryDequeue(out var daqRawData);


            Lens = DaqStatData.Count;

            for (var i = 0; i < Lens; i++) DaqStatData.TryDequeue(out var daqStatData);
        }

        private string GenerateRawFileName()
        {
            FileCounter++;
            // return  $"{StorePath}\\DAQ_{DaqCardName}_{DateTime.Now:yyyyMMdd}_"+ FileCounter.ToString()+".bin";
            return $"{StorePath}\\DAQ_{DaqCardName}_Raw_" + FileCounter + ".bin";
        }


        private string GenerateStatFileName()
        {
            return $"{StorePath}\\DAQ_{DaqCardName}_Stat.bin";
        }

        public void EnqueueStatData(double[,] data, DateTime recvTime)
        {
            var daqData = new DaqAIData
            {
                Data = data,
                RecvTime = recvTime
            };

            DaqStatData.Enqueue(daqData);
            if (DaqStatData.Count > MaxLens)
            {
                DaqAIData removedData;
                DaqStatData.TryDequeue(out removedData);
            }
        }


        public void EnqueueRawData(double[,] data, DateTime recvTime, DateTime lastTime)
        {
            var daqData = new DaqAIData
            {
                Data = data,
                RecvTime = recvTime,
                LastRecvTime = lastTime
            };

            DaqRawData.Enqueue(daqData);
            if (DaqRawData.Count > MaxLens)
            {
                DaqAIData removedData;
                DaqRawData.TryDequeue(out removedData);
            }
        }

        /// <summary>
        /// 把一批原始采样写入文件：按 last→current 为批内每个样本生成独立时间戳（线性插值）。
        /// </summary>
        /// <param name="bw">已打开的 BinaryWriter。</param>
        /// <param name="raw">原始矩阵 [channel, sample]。</param>
        /// <param name="last">上一批最后一个样本时刻。</param>
        /// <param name="current">本批最后一个样本时刻。</param>
        /// <param name="brakeNo">刹车编号（沿用你的格式）。</param>
        private static void WriteRawBatchWithInterpolatedTimestamps(
            BinaryWriter bw, double[,] raw, DateTime last, DateTime current, int brakeNo, double fallbackSampleRateHz = 1000)
        {
            int ch = raw.GetLength(0);
            int n = raw.GetLength(1);
            if (n <= 0) return;

            long spanTicks = (current - last).Ticks;
            // 正常情况下按 last→current 平均铺开；异常（span<=0）按后备采样率兜底
            double step = spanTicks > 0
                ? spanTicks / (double)n
                : TimeSpan.FromSeconds(1.0 / fallbackSampleRateHz).Ticks;

            long baseTicks = last.Ticks;

            for (int i = 0; i < n; i++)
            {
                long tsTicks = baseTicks + (long)Math.Round(step * (i + 1));
                var ts = new DateTime(tsTicks, DateTimeKind.Local);

                bw.Write(brakeNo);
                bw.Write(ts.ToFileTime());   // 读取端用 FromFileTime 即可
                for (int c = 0; c < ch; c++)
                    bw.Write(raw[c, i]);
            }
        }


        // 定时触发的批量写入
        public async Task FlushRawToDiskAsync_Old()
        {
            await rawFileLock.WaitAsync();
            var Lens = DaqRawData.Count;

            var buffer = new byte[Lens * 8 * Channels * SamplesPerChannel + 12 * SamplesPerChannel * Lens];
            FileStream fs = null;


            try
            {
                if (Lens < 1) return; //finally 还是要先执行，然后才真正的return

                // Step 1: 检查是否需要切换文件
                if ((DateTime.Now - _lastFlushTime).TotalMinutes >= StoreTimeMinutes)
                {
                    currentRawFileName = GenerateRawFileName();
                    _lastFlushTime = DateTime.Now;
                }

                SaveRawCounter++;
                var offset = 0;

                if (SaveRawCounter <= 1)
                {
                    for (var i = 0; i < Lens; i++) DaqRawData.TryDequeue(out var daqData);
                    return;
                }
                //跳过最初一次，数据可能会乱或者不完整


                for (var i = 0; i < Lens; i++)
                    if (DaqRawData.TryDequeue(out var daqData))
                    {
                        var RecvSamples = daqData.Data.GetLength(1);
                        var DaqSpanMillSec = daqData.RecvTime.Subtract(daqData.LastRecvTime).TotalMilliseconds /
                                             RecvSamples;

                        for (var j = 0; j < RecvSamples; j++)
                        {
                            var CounterBytes = BitConverter.GetBytes(SaveRawCounter - 1);
                            Buffer.BlockCopy(CounterBytes, 0, buffer, offset, 4);
                            offset += 4;
                            var DaqTime = daqData.LastRecvTime.AddMilliseconds(DaqSpanMillSec * j);
                            var timeBytes = BitConverter.GetBytes(DaqTime.ToFileTime());
                            Buffer.BlockCopy(timeBytes, 0, buffer, offset, 8);
                            offset += 8;

                            var rowData = new double[Channels];

                            for (var k = 0; k < Channels; k++) rowData[k] = daqData.Data[k, j];

                            Buffer.BlockCopy(rowData, 0, buffer, offset, 8 * Channels);
                            offset += 8 * Channels;
                        }


                    }


                // Step 4: 异步批量写入
                fs = new FileStream(currentRawFileName,
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
                    $"DAQ_{DaqCardName}WriteDiskErrorLog.txt");
                var errorMessage = $"[{DateTime.Now}] DAQ_{DaqCardName} flush error: {ex.Message}";

                File.AppendAllText(logFilePath, errorMessage + Environment.NewLine);
            }
            finally
            {
                rawFileLock.Release();
                buffer = null;
                if (fs != null) fs.Dispose();
            }
        }


        /// <summary>
        /// 将队列中的原始采样批量写入磁盘（二进制）。
        /// 【重要】为“批内每个样本”生成独立时间戳：按 last→current 等分，
        /// 并使用 (j+1) 避免首样本时间与上一批最后一个样本时间重复，
        /// 解决导出 CSV 时 RelTime 出现 0/0.001 交替的问题。
        /// </summary>
        /// <remarks>
        /// 单样本帧格式保持不变：
        ///   int SaveRawCounter(4B) + long TimeFile(8B) + double[Channels] (8*Channels B)
        /// 缓冲大小仍按“Lens * SamplesPerChannel”预估；若你的 SamplesPerChannel
        /// 是固定的，此计算与原逻辑一致。
        /// </remarks>
        public async Task FlushRawToDiskAsync()
        {
            await rawFileLock.WaitAsync();
            var Lens = DaqRawData.Count;

            // 预估缓冲大小：每样本 (4 + 8 + 8*Channels) 字节
            var buffer = new byte[Lens * SamplesPerChannel * (4 + 8 + 8 * Channels)];
            FileStream fs = null;

            try
            {
                if (Lens < 1) return; // finally 仍会执行

                // Step 1: 检查是否需要切换文件
                if ((DateTime.Now - _lastFlushTime).TotalMinutes >= StoreTimeMinutes)
                {
                    currentRawFileName = GenerateRawFileName();
                    _lastFlushTime = DateTime.Now;
                }

                SaveRawCounter++;
                var offset = 0;

                // 跳过首次写盘（保持你原有策略）
                if (SaveRawCounter <= 1)
                {
                    for (var i = 0; i < Lens; i++) DaqRawData.TryDequeue(out var _);
                    return;
                }

                // 复用一块行缓冲，避免在内层循环频繁分配
                var rowData = new double[Channels];

                // Step 2: 逐批取出并展开为“逐样本”记录
                for (var i = 0; i < Lens; i++)
                {
                    if (!DaqRawData.TryDequeue(out var daqData)) continue;

                    int recvSamples = daqData.Data.GetLength(1);
                    if (recvSamples <= 0) continue;

                    // —— 用 Ticks 做线性插值，更精确 —— //
                    long lastTicks = daqData.LastRecvTime.Ticks;
                    long spanTicks = (daqData.RecvTime - daqData.LastRecvTime).Ticks;

                    // 正常：按 last→current 等分；异常（<=0）：用 1ms/样本 兜底
                    double stepTicks = spanTicks > 0
                        ? spanTicks / (double)recvSamples
                        : TimeSpan.FromMilliseconds(1).Ticks;   // 兜底：1 kHz

                    for (int j = 0; j < recvSamples; j++)
                    {
                        // ★ 关键：用 (j + 1) 确保首样本时间 > last（避免跨批重复）
                        long tsTicks = lastTicks + (long)Math.Round(stepTicks * (j + 1));
                        var daqTime = new DateTime(tsTicks, DateTimeKind.Local);

                        // 写 Counter（保持原有 SaveRawCounter-1 语义）
                        var counterBytes = BitConverter.GetBytes(SaveRawCounter - 1);
                        Buffer.BlockCopy(counterBytes, 0, buffer, offset, 4);
                        offset += 4;

                        // 写时间戳（long: ToFileTime）
                        var timeBytes = BitConverter.GetBytes(daqTime.ToFileTime());
                        Buffer.BlockCopy(timeBytes, 0, buffer, offset, 8);
                        offset += 8;

                        // 拷贝本样本的各通道
                        for (int k = 0; k < Channels; k++)
                            rowData[k] = daqData.Data[k, j];

                        Buffer.BlockCopy(rowData, 0, buffer, offset, 8 * Channels);
                        offset += 8 * Channels;
                    }
                }

                // Step 3: 异步批量写入
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
                    $"DAQ_{DaqCardName}WriteDiskErrorLog.txt");
                var errorMessage = $"[{DateTime.Now}] DAQ_{DaqCardName} flush error: {ex.Message}";
                File.AppendAllText(logFilePath, errorMessage + Environment.NewLine);
            }
            finally
            {
                rawFileLock.Release();
                buffer = null;
                fs?.Dispose();
            }
        }


        public async Task FlushStatToDiskAsync()
        {
            await statFileLock.WaitAsync();
            var Lens = DaqStatData.Count;
            FileStream fs = null;


            try
            {
                if (Lens < 1) return; //finally 还是要先执行，然后才真正的return


                SaveStatCounter++;

                if (SaveStatCounter <= 1)
                {
                    for (var i = 0; i < Lens; i++) DaqStatData.TryDequeue(out var daqData);
                    return;
                }

                var StatData = new double[Channels][];
                var index = new int[Channels];
                var RecvTime = new DateTime[Lens];

                var totalCount = DaqStatData.Take(Lens).Sum(arr => arr.Data.GetLength(1));
                for (var i = 0; i < Channels; i++)
                {
                    StatData[i] = new double[totalCount];
                    index[i] = 0;
                }

                for (var i = 0; i < Lens; i++)
                    if (DaqStatData.TryDequeue(out var daqData))
                    {
                        RecvTime[i] = daqData.RecvTime;
                        var j = -1;
                        foreach (var channel in eMBToDaqCurrentChannel)
                        {
                            j++;
                            var ChannelNo = channel.Value;
                            var ChannelData = new double[SamplesPerChannel];
                            Buffer.BlockCopy(daqData.Data, ChannelNo * 8 * SamplesPerChannel, ChannelData, 0,
                                8 * SamplesPerChannel);
                            Array.Copy(ChannelData, 0, StatData[j], index[j], ChannelData.Length);
                            index[j] += ChannelData.Length;
                        }
                    }


                var buffer = new byte[108];
                var offset = 0;

                var CounterBytes = BitConverter.GetBytes(SaveStatCounter - 1);
                Buffer.BlockCopy(CounterBytes, 0, buffer, offset, 4);
                offset += 4;

                var timeBytes = BitConverter.GetBytes(RecvTime[0].ToFileTime()); //取第一个时间作为最值出现的时间
                Buffer.BlockCopy(timeBytes, 0, buffer, offset, 8);
                offset += 8;

                foreach (var channel in eMBToDaqCurrentChannel)
                {
                    var ChannelNo = channel.Value;
                    var EmbName = channel.Key;


                    var result = new double[totalCount];
                    for (var i = 0; i < totalCount; i++)
                        result[i] = (StatData[ChannelNo][i] - paraNameToZeroValue[EmbName]) * paraNameToScale[EmbName] +
                                    paraNameToOffset[EmbName];

                    var filterCurrent = ClsDataFilter.MakeMedianFilterReducePoint(ref result, medianLens);

                    var max = filterCurrent.Max();
                    var min = filterCurrent.Min();

                    var maxBytes = BitConverter.GetBytes(max);
                    Buffer.BlockCopy(maxBytes, 0, buffer, offset, 8);
                    offset += 8;

                    var minBytes = BitConverter.GetBytes(min);
                    Buffer.BlockCopy(minBytes, 0, buffer, offset, 8);
                    offset += 8;
                }

                // Step 4: 异步批量写入
                fs = new FileStream(currentStatFileName,
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
                    $"DAQ_{DaqCardName}WriteDiskErrorLog.txt");
                var errorMessage = $"[{DateTime.Now}] DAQ_{DaqCardName} flush error: {ex.Message}";

                File.AppendAllText(logFilePath, errorMessage + Environment.NewLine);
            }
            finally
            {
                statFileLock.Release();

                if (fs != null) fs.Dispose();
            }
        }
    }
}