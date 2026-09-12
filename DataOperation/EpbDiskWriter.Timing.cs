using System;
using System.Diagnostics;

namespace DataOperation;

/// <summary>单次设备批次写入诊断。阶段可能嵌套，不能相加作为总时长。</summary>
public sealed class EpbWriteTiming
{
    public string Operation { get; internal set; } = "DeviceBatch";
    public int Channel { get; internal set; }
    public int Cycle { get; internal set; }
    public string Device { get; internal set; }
    public long Generation { get; internal set; }
    public long Sequence { get; internal set; }
    public int SampleCount { get; internal set; }
    public int ThreadId { get; internal set; }
    public bool Succeeded { get; internal set; }
    public double TotalMs { get; internal set; }
    public double ChannelGateWaitMs { get; internal set; }
    public double RawGateWaitMs { get; internal set; }
    public double RawStageMs { get; internal set; }
    public double RawCommitMs { get; internal set; }
    public double IndexGateWaitMs { get; internal set; }
    public double IndexCommitMs { get; internal set; }
    public double RingFlushMs { get; internal set; }
    public double CheckpointMs { get; internal set; }
    public double RawPruneMs { get; internal set; }
    public double RawAppendMs { get; internal set; }
    public double RawAppendGateWaitMs { get; internal set; }
    public double ViewRemapMs { get; internal set; }
    public double RingWriteMs { get; internal set; }
}

public sealed partial class EpbDiskWriter
{
    // 写入全部为同步操作；线程内上下文避免两个设备的阶段计时互相覆盖。
    [ThreadStatic] private static EpbWriteTiming _writeTiming;

    private T MeasureStorageOperation<T>(string operation, int channel, int cycle, Func<T> action)
    {
        var sink = _policy.WriteTimingSink;
        if (sink == null || _writeTiming != null) return action();
        var timing = new EpbWriteTiming
        {
            Operation = operation, Channel = channel, Cycle = cycle,
            Device = "EPB" + channel, ThreadId = Environment.CurrentManagedThreadId
        };
        var started = Stopwatch.GetTimestamp();
        _writeTiming = timing;
        try
        {
            var result = action();
            timing.Succeeded = true;
            return result;
        }
        finally
        {
            _writeTiming = null;
            timing.TotalMs = ElapsedWriteMs(started);
            try { sink(timing); } catch { }
        }
    }

    private static double ElapsedWriteMs(long started) =>
        (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
}
