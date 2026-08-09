using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataOperation;

public readonly struct DaqAIData
{
    public DaqAIData(double[,] data, DateTime recvTime, DateTime lastRecvTime = default)
    {
        Data = data;
        RecvTime = recvTime;
        LastRecvTime = lastRecvTime;
        Owned = null;
    }

    public DaqAIData(OwnedDaqRawBatch owned)
    {
        Owned = owned ?? throw new ArgumentNullException(nameof(owned));
        Data = null;
        RecvTime = owned.Current;
        LastRecvTime = owned.Last;
    }

    public double[,] Data { get; }
    public DateTime RecvTime { get; }
    public DateTime LastRecvTime { get; }
    public OwnedDaqRawBatch Owned { get; }
    public int ChannelCount => Owned?.ChannelCount ?? Data?.GetLength(0) ?? 0;
    public int SampleCount => Owned?.SampleCount ?? Data?.GetLength(1) ?? 0;
    public double GetValue(int channel, int sample) =>
        Owned != null ? Owned[channel, sample] : Data[channel, sample];
    public void DisposeOwned() => Owned?.Dispose();
}

public class DaqAIContext
{
    public string DaqCardName { get; }
    private readonly int Channels;
    private readonly string currentStatFileName;
    private readonly ConcurrentQueue<DaqAIData> DaqRawData = new();
    private readonly SemaphoreSlim rawQueueGate = new(1);
    private readonly SemaphoreSlim rawQueueSlots;
    private int rawQueueCount;
    private int rawQueueFullLatched;

    private readonly ConcurrentQueue<DaqAIData> DaqStatData = new();
    private readonly int MaxLens;
    private readonly SemaphoreSlim rawFileLock = new(1);
    private readonly int SamplesPerChannel;
    private readonly SemaphoreSlim statFileLock = new(1);
    private readonly object statAggregateLock = new();
    private double[] statMinimum = Array.Empty<double>();
    private double[] statMaximum = Array.Empty<double>();
    private double[][] statMedianBlocks = Array.Empty<double[]>();
    private int[] statMedianCounts = Array.Empty<int>();
    private int statMedianWindow;
    private DateTime statFirstUtc;
    private bool statHasData;
    private readonly string StorePath;
    private readonly double StoreTimeMinutes;


    private DateTime _lastFlushTime = DateTime.MinValue;
    private string currentRawFileName;
    private double DaqSpanMillSec1;


    public SortedDictionary<string, int> eMBToDaqCurrentChannel = new();
    private int FileCounter;
    public int medianLens = 10;
    public ConcurrentDictionary<string, double> paraNameToOffset = new();
    public ConcurrentDictionary<string, double> paraNameToScale = new();
    public ConcurrentDictionary<string, double> paraNameToZeroValue = new();
    private int SaveRawCounter;
    private int SaveStatCounter;


    public DaqAIContext(string cardName, int maxLens, double storeTimeMinutes, double daqSpanMillSec, int channels,
        int samplesPerChannel, string storePath)
    {
        DaqCardName = cardName;

        MaxLens = Math.Max(256, maxLens);
        rawQueueSlots = new SemaphoreSlim(MaxLens, MaxLens);
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

    public event Action<string, int, int> QueueFull;

    public int RawQueueDepth => Volatile.Read(ref rawQueueCount);
    public int RawQueueCapacity => MaxLens;

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
        if (data == null) return;
        var mappings = eMBToDaqCurrentChannel.ToArray();
        lock (statAggregateLock)
        {
            EnsureStatCapacity(mappings.Length, recvTime.ToUniversalTime());
            for (var mappingIndex = 0; mappingIndex < mappings.Length; mappingIndex++)
            {
                var name = mappings[mappingIndex].Key;
                var sourceChannel = mappings[mappingIndex].Value;
                if (sourceChannel < 0 || sourceChannel >= data.GetLength(0)) continue;
                paraNameToZeroValue.TryGetValue(name, out var zero);
                var scale = paraNameToScale.TryGetValue(name, out var configuredScale) ? configuredScale : 1.0;
                paraNameToOffset.TryGetValue(name, out var offset);
                for (var sample = 0; sample < data.GetLength(1); sample++)
                    UpdateStreamingStat(
                        mappingIndex,
                        (data[sourceChannel, sample] - zero) * scale + offset);
            }
        }
    }


    public void EnqueueRawData(double[,] data, DateTime recvTime, DateTime lastTime)
    {
        EnqueueRawCore(new DaqAIData(data, recvTime, lastTime));
    }

    public void EnqueueRawData(OwnedDaqRawBatch batch)
    {
        if (batch == null) return;
        // 统计是派生数据，异常不能撤销 Raw 所有权。Raw 成功进入无丢弃队列后，
        // 上游即可安全结束所有权转移。
        try { AccumulateStat(batch); }
        catch { }
        EnqueueRawCore(new DaqAIData(batch));
    }

    private void EnqueueRawCore(DaqAIData data)
    {
        if (!rawQueueSlots.Wait(0))
        {
            if (Interlocked.CompareExchange(ref rawQueueFullLatched, 1, 0) == 0)
            {
                try { QueueFull?.Invoke(DaqCardName, RawQueueDepth, MaxLens); }
                catch { }
            }
            // 调用线程是 Raw 后台发布线程，不是 NI 回调线程。容量耗尽时把背压
            // 逐级传回采集安全暂停，绝不能删除队头的已接收批次。
            rawQueueSlots.Wait();
        }

        rawQueueGate.Wait();
        try
        {
            DaqRawData.Enqueue(data);
            Interlocked.Increment(ref rawQueueCount);
        }
        catch
        {
            rawQueueSlots.Release();
            data.DisposeOwned();
            throw;
        }
        finally
        {
            rawQueueGate.Release();
        }
    }

    private void AccumulateStat(OwnedDaqRawBatch batch)
    {
        var mappings = eMBToDaqCurrentChannel.ToArray();
        if (mappings.Length == 0) return;
        lock (statAggregateLock)
        {
            EnsureStatCapacity(mappings.Length, batch.Current.ToUniversalTime());
            for (var mappingIndex = 0; mappingIndex < mappings.Length; mappingIndex++)
            {
                var name = mappings[mappingIndex].Key;
                var sourceChannel = mappings[mappingIndex].Value;
                if (sourceChannel < 0 || sourceChannel >= batch.ChannelCount) continue;
                paraNameToZeroValue.TryGetValue(name, out var zero);
                var scale = paraNameToScale.TryGetValue(name, out var configuredScale) ? configuredScale : 1.0;
                paraNameToOffset.TryGetValue(name, out var offset);
                for (var sample = 0; sample < batch.SampleCount; sample++)
                {
                    var value = (batch[sourceChannel, sample] - zero) * scale + offset;
                    UpdateStreamingStat(mappingIndex, value);
                }
            }
        }
    }

    private void EnsureStatCapacity(int count, DateTime firstUtc)
    {
        var window = Math.Max(1, medianLens);
        if (statMinimum.Length == count && statMedianWindow == window) return;
        statMinimum = Enumerable.Repeat(double.PositiveInfinity, count).ToArray();
        statMaximum = Enumerable.Repeat(double.NegativeInfinity, count).ToArray();
        statMedianBlocks = Enumerable.Range(0, count)
            .Select(_ => new double[window])
            .ToArray();
        statMedianCounts = new int[count];
        statMedianWindow = window;
        statFirstUtc = firstUtc;
        statHasData = false;
    }

    private void UpdateStreamingStat(int index, double value)
    {
        var block = statMedianBlocks[index];
        var count = statMedianCounts[index];
        block[count++] = value;
        if (count < statMedianWindow)
        {
            statMedianCounts[index] = count;
            return;
        }

        Array.Sort(block, 0, statMedianWindow);
        var median = block[statMedianWindow / 2];
        statMedianCounts[index] = 0;
        if (median < statMinimum[index]) statMinimum[index] = median;
        if (median > statMaximum[index]) statMaximum[index] = median;
        statHasData = true;
    }

    /// <summary>
    ///     把一批原始采样写入文件：按 last→current 为批内每个样本生成独立时间戳（线性插值）。
    /// </summary>
    /// <param name="bw">已打开的 BinaryWriter。</param>
    /// <param name="raw">原始矩阵 [channel, sample]。</param>
    /// <param name="last">上一批最后一个样本时刻。</param>
    /// <param name="current">本批最后一个样本时刻。</param>
    /// <param name="brakeNo">刹车编号（沿用你的格式）。</param>
    private static void WriteRawBatchWithInterpolatedTimestamps(
        BinaryWriter bw, double[,] raw, DateTime last, DateTime current, int brakeNo,
        double fallbackSampleRateHz = 1000)
    {
        var ch = raw.GetLength(0);
        var n = raw.GetLength(1);
        if (n <= 0) return;

        var spanTicks = (current - last).Ticks;
        // 正常情况下按 last→current 平均铺开；异常（span<=0）按后备采样率兜底
        var step = spanTicks > 0
            ? spanTicks / (double)n
            : TimeSpan.FromSeconds(1.0 / fallbackSampleRateHz).Ticks;

        var baseTicks = last.Ticks;

        for (var i = 0; i < n; i++)
        {
            var tsTicks = baseTicks + (long)Math.Round(step * (i + 1));
            var ts = new DateTime(tsTicks, DateTimeKind.Local);

            bw.Write(brakeNo);
            bw.Write(ts.ToFileTime()); // 读取端用 FromFileTime 即可
            for (var c = 0; c < ch; c++)
                bw.Write(raw[c, i]);
        }
    }

    private static void WriteInt32LittleEndian(byte[] buffer, ref int offset, int value)
    {
        unchecked
        {
            buffer[offset++] = (byte)value;
            buffer[offset++] = (byte)(value >> 8);
            buffer[offset++] = (byte)(value >> 16);
            buffer[offset++] = (byte)(value >> 24);
        }
    }

    private static void WriteInt64LittleEndian(byte[] buffer, ref int offset, long value)
    {
        unchecked
        {
            var bits = (ulong)value;
            buffer[offset++] = (byte)bits;
            buffer[offset++] = (byte)(bits >> 8);
            buffer[offset++] = (byte)(bits >> 16);
            buffer[offset++] = (byte)(bits >> 24);
            buffer[offset++] = (byte)(bits >> 32);
            buffer[offset++] = (byte)(bits >> 40);
            buffer[offset++] = (byte)(bits >> 48);
            buffer[offset++] = (byte)(bits >> 56);
        }
    }


    /// <summary>
    ///     将队列中的原始采样批量写入磁盘（二进制）。
    ///     【重要】为“批内每个样本”生成独立时间戳：按 last→current 等分，
    ///     并使用 (j+1) 避免首样本时间与上一批最后一个样本时间重复，
    ///     解决导出 CSV 时 RelTime 出现 0/0.001 交替的问题。
    /// </summary>
    /// <remarks>
    ///     单样本帧格式保持不变：
    ///     int SaveRawCounter(4B) + long TimeFile(8B) + double[Channels] (8*Channels B)
    ///     缓冲大小仍按“Lens * SamplesPerChannel”预估；若你的 SamplesPerChannel
    ///     是固定的，此计算与原逻辑一致。
    /// </remarks>
    public async Task FlushRawToDiskAsync()
    {
        await rawFileLock.WaitAsync();
        await rawQueueGate.WaitAsync();
        var pending = new List<DaqAIData>();
        byte[] buffer = null;
        FileStream fs = null;
        long originalLength = -1;
        string targetFile = null;

        try
        {
            var Lens = Volatile.Read(ref rawQueueCount);
            if (Lens < 1) return; // finally 仍会执行

            for (var i = 0; i < Lens; i++)
            {
                if (!DaqRawData.TryDequeue(out var daqData)) break;
                pending.Add(daqData);
                Interlocked.Decrement(ref rawQueueCount);
            }
            if (pending.Count == 0) return;

            // 按真实批次样本数计算缓冲，避免配置样本数与现场批次变化时越界。
            var totalSamples = pending.Sum(item => item.SampleCount);
            var estimatedBytes = checked(totalSamples * (4 + 8 + 8 * Channels));
            buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, estimatedBytes));

            // Step 1: 检查是否需要切换文件
            if ((DateTime.Now - _lastFlushTime).TotalMinutes >= StoreTimeMinutes)
            {
                currentRawFileName = GenerateRawFileName();
                _lastFlushTime = DateTime.Now;
            }

            targetFile = currentRawFileName;
            var nextSaveRawCounter = checked(SaveRawCounter + 1);
            var offset = 0;

            // Step 2: 逐批取出并展开为“逐样本”记录
            foreach (var daqData in pending)
            {
                var recvSamples = daqData.SampleCount;
                if (recvSamples <= 0) continue;

                // —— 用 Ticks 做线性插值，更精确 —— //
                var lastTicks = daqData.LastRecvTime.Ticks;
                var spanTicks = (daqData.RecvTime - daqData.LastRecvTime).Ticks;
                var stepTicks = spanTicks > 0
                    ? spanTicks / (double)recvSamples
                    : TimeSpan.FromMilliseconds(DaqSpanMillSec1).Ticks;

                for (var j = 0; j < recvSamples; j++)
                {
                    var tsTicks = lastTicks + (long)Math.Round(stepTicks * (j + 1));
                    var daqTime = new DateTime(tsTicks, DateTimeKind.Local);
                    WriteInt32LittleEndian(buffer, ref offset, nextSaveRawCounter);
                    WriteInt64LittleEndian(buffer, ref offset, daqTime.ToFileTime());
                    for (var k = 0; k < Channels; k++)
                        WriteInt64LittleEndian(
                            buffer,
                            ref offset,
                            BitConverter.DoubleToInt64Bits(daqData.GetValue(k, j)));
                }
            }

            // Step 3: 记录追加前长度。任何异常先回滚文件，再把原批次按 FIFO 放回队头语义；
            // 在 rawQueueGate 持有期间没有新生产者插入，因此重新入队不会改变顺序。
            fs = new FileStream(
                targetFile,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.Read,
                8192,
                FileOptions.WriteThrough | FileOptions.Asynchronous);
            originalLength = fs.Length;
            fs.Position = originalLength;

            await fs.WriteAsync(buffer, 0, offset);
            await fs.FlushAsync();
            SaveRawCounter = nextSaveRawCounter;

            foreach (var daqData in pending)
            {
                daqData.DisposeOwned();
                rawQueueSlots.Release();
            }
            pending.Clear();
            Interlocked.Exchange(ref rawQueueFullLatched, 0);
        }
        catch (Exception ex)
        {
            Exception rollbackError = null;
            if (fs != null && originalLength >= 0)
            {
                try
                {
                    fs.SetLength(originalLength);
                    await fs.FlushAsync();
                }
                catch (Exception rollbackEx)
                {
                    rollbackError = rollbackEx;
                }
            }

            foreach (var daqData in pending)
            {
                DaqRawData.Enqueue(daqData);
                Interlocked.Increment(ref rawQueueCount);
            }
            pending.Clear();

            try
            {
                if (Directory.Exists(StorePath))
                {
                    var logFilePath = Path.Combine(StorePath,
                        $"DAQ_{DaqCardName}WriteDiskErrorLog.txt");
                    var errorMessage = $"[{DateTime.Now}] DAQ_{DaqCardName} flush error: {ex.Message}";
                    File.AppendAllText(logFilePath, errorMessage + Environment.NewLine);
                }
            }
            catch { }

            if (rollbackError != null)
                throw new AggregateException("Raw写入失败且文件长度回滚失败；保留内存批次并禁止静默继续。", ex, rollbackError);
            throw;
        }
        finally
        {
            if (buffer != null) ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
            fs?.Dispose();
            rawQueueGate.Release();
            rawFileLock.Release();
        }
    }


    public async Task FlushStatToDiskAsync()
    {
        await statFileLock.WaitAsync();
        FileStream fs = null;
        try
        {
            double[] minimum;
            double[] maximum;
            DateTime firstUtc;
            lock (statAggregateLock)
            {
                if (!statHasData) return;
                minimum = (double[])statMinimum.Clone();
                maximum = (double[])statMaximum.Clone();
                firstUtc = statFirstUtc;
                statMinimum = Enumerable.Repeat(double.PositiveInfinity, minimum.Length).ToArray();
                statMaximum = Enumerable.Repeat(double.NegativeInfinity, maximum.Length).ToArray();
                statFirstUtc = DateTime.UtcNow;
                statHasData = false;
            }
            SaveStatCounter++;
            var buffer = new byte[12 + maximum.Length * 16];
            var offset = 0;
            Buffer.BlockCopy(BitConverter.GetBytes(SaveStatCounter), 0, buffer, offset, 4);
            offset += 4;
            Buffer.BlockCopy(BitConverter.GetBytes(firstUtc.ToLocalTime().ToFileTime()), 0, buffer, offset, 8);
            offset += 8;
            for (var i = 0; i < maximum.Length; i++)
            {
                Buffer.BlockCopy(BitConverter.GetBytes(maximum[i]), 0, buffer, offset, 8);
                offset += 8;
                Buffer.BlockCopy(BitConverter.GetBytes(minimum[i]), 0, buffer, offset, 8);
                offset += 8;
            }
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
