using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Config;

namespace Timing;

/// <summary>
///     高精度定时执行器：以固定周期调度任务，支持超时策略、暂停/恢复/停止。
/// </summary>
public sealed class HighPrecisionTimer
{
    private readonly CancellationTokenSource _cts = new();
    private readonly IAppLogger _log;
    private readonly ManualResetEventSlim _pauseGate = new(true);
    private readonly object _pauseSync = new();
    private readonly int _periodMs;
    private readonly OverrunPolicy _policy;
    private volatile bool _running;
    private long _pauseStartedTimestamp;
    private int _resumeAtFutureBoundaryRequested;
    private int _resumeDelayMs;
    private int _pauseAfterCurrentCycleRequested;
    // 0=两圈之间，1=圈执行中，2=已暂停。用 CAS 消除“已过暂停门但尚未进圈”的竞态。
    private int _cycleState;
    private TaskCompletionSource<bool> _gracefulPauseCompletion;

    private long _ticksStart; // 计划起点

    public OverrunPolicy Policy => _policy; // 只读
    public bool IsPaused => Volatile.Read(ref _cycleState) == 2;

    public HighPrecisionTimer(int periodMs, OverrunPolicy policy, IAppLogger log = null)
    {
        _periodMs = Math.Max(1, periodMs);
        _policy = policy;
        _log = log ?? NullLogger.Instance;
    }

    /// <summary>
    ///     启动周期任务。
    /// </summary>
    /// <param name="repeat">总次数；null 表示无限</param>
    /// <param name="startDelayMs">首次执行延迟（用于同组错峰）</param>
    /// <param name="work">每周期执行体；返回 true 表示本次成功，false 仅记录</param>
    public Task StartAsync(int? repeat, int startDelayMs, Func<int, CancellationToken, Task<bool>> work)
    {
        if (_running) throw new InvalidOperationException("Timer already running.");
        _running = true;

        return Task.Run(async () =>
        {
            try
            {
                if (startDelayMs > 0)
                {
                    _log.Info($"定时器首延迟 {startDelayMs}ms", "Timer");
                    await Task.Delay(startDelayMs, _cts.Token);
                }

                var sw = Stopwatch.StartNew();
                _ticksStart = sw.ElapsedMilliseconds;
                var i = 0;

                while (!_cts.IsCancellationRequested && (!repeat.HasValue || i < repeat.Value))
                {
                    _pauseGate.Wait(_cts.Token);

                    if (Interlocked.Exchange(ref _resumeAtFutureBoundaryRequested, 0) != 0)
                    {
                        var resumeDelay = Math.Max(1, Interlocked.Exchange(ref _resumeDelayMs, 0));
                        _ticksStart = sw.ElapsedMilliseconds + resumeDelay - (long)i * _periodMs;
                        _log.Info($"定时器按新同步锚点恢复，距下一完整圈 {resumeDelay}ms。", "Timer");
                    }

                    var planned = _ticksStart + (long)i * _periodMs;
                    var now = sw.ElapsedMilliseconds;

                    // 若提前到达，微等待以减小抖动
                    var sleepMs = (int)(planned - now);
                    if (sleepMs > 0)
                        await Task.Delay(sleepMs, _cts.Token);

                    // 暂停请求可能发生在本轮已经越过 _pauseGate、仍等待计划时刻的窗口。
                    // 只有成功把状态从“两圈之间”切到“圈执行中”才允许调用 work。
                    if (Interlocked.CompareExchange(ref _cycleState, 1, 0) != 0)
                    {
                        _pauseGate.Wait(_cts.Token);
                        continue;
                    }

                    var t0 = sw.ElapsedMilliseconds;
                    var ok = false;
                    Exception caught = null;
                    try
                    {
                        ok = await work(i + 1, _cts.Token);
                    }
                    catch (Exception ex)
                    {
                        caught = ex;
                        ok = false;
                    }

                    var t1 = sw.ElapsedMilliseconds;
                    Interlocked.Exchange(ref _cycleState, 0);
                    EnterGracefulPauseIfRequested();
                    var elapsed = (int)(t1 - t0);

                    if (caught is OperationCanceledException && _cts.IsCancellationRequested)
                    {
                        _log.Info($"周期 {i + 1} 随定时器停止请求取消。", "Timer");
                    }
                    else if (caught is OperationCanceledException &&
                             (Volatile.Read(ref _pauseAfterCurrentCycleRequested) != 0 ||
                              Volatile.Read(ref _cycleState) == 2))
                    {
                        _log.Info($"周期 {i + 1} 随暂停/恢复切换取消。", "Timer");
                    }
                    else if (caught != null)
                    {
                        _log.Error($"周期 {i + 1} 执行异常：{caught.Message}", "Timer", caught);
                    }
                    if (!ok) _log.Warn($"周期 {i + 1} 返回失败", "Timer");

                    // 处理超时
                    if (elapsed > _periodMs)
                    {
                        var msg = $"周期 {i + 1} 超时：耗时={elapsed}ms > 设定={_periodMs}ms，策略={_policy}";
                        switch (_policy)
                        {
                            case OverrunPolicy.RunToCompletionSkipMissed:
                                _log.Warn(msg + "（跳过错过的触发点）", "Timer");
                                // 计算下次应当对齐到的整数倍
                                var k = Math.Max(1, (int)Math.Ceiling((t1 - _ticksStart) / (double)_periodMs));
                                i = k; // 跳到下一个整数周期
                                continue; // 直接进入下一轮
                            case OverrunPolicy.SkipNextIfOverrun:
                                _log.Warn(msg + "（跳过下一次）", "Timer");
                                i += 2; // 跳过一次
                                continue;
                            case OverrunPolicy.AlignToWallClock:
                                _log.Warn("周期不足：" + msg + "（滚动到下一个未来周期边界，禁止追赶式执行）", "Timer");
                                var futureSlot = Math.Max(
                                    i + 1,
                                    (int)Math.Floor((t1 - _ticksStart) / (double)_periodMs) + 1);
                                var nextPlanned = _ticksStart + (long)futureSlot * _periodMs;
                                i++;
                                // 保持完成圈号连续，但把调度基准平移到刚算出的未来边界。
                                _ticksStart = nextPlanned - (long)i * _periodMs;
                                continue;
                            case OverrunPolicy.Throw:
                                _log.Error(msg + "（抛出异常）", "Timer");
                                throw new TimeoutException(msg);
                        }
                    }

                    i++;
                }
            }
            catch (OperationCanceledException)
            {
                _log.Info("定时器取消。", "Timer");
            }
            finally
            {
                _running = false;
            }
        }, _cts.Token);
    }

    /// <summary>暂停周期执行。</summary>
    public void Pause()
    {
        Interlocked.CompareExchange(ref _pauseStartedTimestamp, Stopwatch.GetTimestamp(), 0);
        Interlocked.Exchange(ref _pauseAfterCurrentCycleRequested, 1);
        _pauseGate.Reset();
        if (Interlocked.CompareExchange(ref _cycleState, 2, 0) == 0 ||
            Volatile.Read(ref _cycleState) == 2)
            CompleteGracefulPause();
        _log.Info("定时器已暂停。", "Timer");
    }

    /// <summary>
    /// 请求在当前圈自然结束后暂停；若当前尚未进入圈执行，则立即封住下一圈。
    /// 返回的任务只在定时器确认“不再启动新圈”后完成。
    /// </summary>
    public Task PauseAfterCurrentCycleAsync()
    {
        Task completion;
        lock (_pauseSync)
        {
            if (!_running)
                return Task.CompletedTask;

            if (_gracefulPauseCompletion == null || _gracefulPauseCompletion.Task.IsCompleted)
                _gracefulPauseCompletion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            completion = _gracefulPauseCompletion.Task;
        }

        Interlocked.CompareExchange(ref _pauseStartedTimestamp, Stopwatch.GetTimestamp(), 0);
        Interlocked.Exchange(ref _pauseAfterCurrentCycleRequested, 1);
        _pauseGate.Reset();
        if (Interlocked.CompareExchange(ref _cycleState, 2, 0) == 0 ||
            Volatile.Read(ref _cycleState) == 2)
            CompleteGracefulPause();

        _log.Info("定时器已请求在当前圈结束后暂停。", "Timer");
        return completion;
    }

    /// <summary>恢复周期执行。</summary>
    public void Resume()
    {
        Interlocked.Exchange(ref _pauseAfterCurrentCycleRequested, 0);
        Interlocked.CompareExchange(ref _cycleState, 0, 2);
        var pausedAt = Interlocked.Exchange(ref _pauseStartedTimestamp, 0);
        if (pausedAt != 0)
        {
            var pausedMs = (Stopwatch.GetTimestamp() - pausedAt) * 1000L / Stopwatch.Frequency;
            Interlocked.Add(ref _ticksStart, pausedMs);
        }
        _pauseGate.Set();
        _log.Info("定时器已恢复。", "Timer");
    }

    /// <summary>
    /// 放弃旧半圈剩余相位，把当前尚未执行的圈重新锚定到指定的未来时刻。
    /// </summary>
    public void ResumeAtNextBoundary(int delayMs)
    {
        Interlocked.Exchange(ref _pauseAfterCurrentCycleRequested, 0);
        Interlocked.CompareExchange(ref _cycleState, 0, 2);
        Interlocked.Exchange(ref _pauseStartedTimestamp, 0);
        Interlocked.Exchange(ref _resumeDelayMs, Math.Max(1, delayMs));
        Interlocked.Exchange(ref _resumeAtFutureBoundaryRequested, 1);
        _pauseGate.Set();
        _log.Info($"定时器等待未来同步锚点恢复，Delay={Math.Max(1, delayMs)}ms。", "Timer");
    }

    /// <summary>让尚未执行的下一圈在统一 UTC 锚点恢复。</summary>
    public void ResumeAtUtcBoundary(DateTime boundaryUtc)
    {
        var utc = boundaryUtc.Kind == DateTimeKind.Utc
            ? boundaryUtc
            : boundaryUtc.ToUniversalTime();
        var delayMs = Math.Max(1, (int)Math.Ceiling((utc - DateTime.UtcNow).TotalMilliseconds));
        ResumeAtNextBoundary(delayMs);
    }

    /// <summary>终止定时器。</summary>
    public void Stop()
    {
        _cts.Cancel();
        _pauseGate.Set();
        CompleteGracefulPause();
        _log.Info("定时器 Stop。", "Timer");
    }

    private void EnterGracefulPauseIfRequested()
    {
        if (Volatile.Read(ref _pauseAfterCurrentCycleRequested) == 0)
            return;

        _pauseGate.Reset();
        if (Interlocked.CompareExchange(ref _cycleState, 2, 0) == 0 ||
            Volatile.Read(ref _cycleState) == 2)
            CompleteGracefulPause();
    }

    private void CompleteGracefulPause()
    {
        TaskCompletionSource<bool> completion;
        lock (_pauseSync) completion = _gracefulPauseCompletion;
        completion?.TrySetResult(true);
    }
}
