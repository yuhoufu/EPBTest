using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Config;

namespace Timing
{
    /// <summary>
    /// 高精度定时执行器：以固定周期调度任务，支持超时策略、暂停/恢复/停止。
    /// </summary>
    public sealed class HighPrecisionTimer
    {
        private readonly int _periodMs;
        private readonly OverrunPolicy _policy;
        private readonly IAppLogger _log;

        private readonly CancellationTokenSource _cts = new();
        private readonly ManualResetEventSlim _pauseGate = new(true);

        private long _ticksStart;  // 计划起点
        private volatile bool _running;

        public HighPrecisionTimer(int periodMs, OverrunPolicy policy, IAppLogger log = null)
        {
            _periodMs = Math.Max(1, periodMs);
            _policy = policy;
            _log = log ?? NullLogger.Instance;
        }

        /// <summary>
        /// 启动周期任务。
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
                    int i = 0;

                    while (!_cts.IsCancellationRequested && (!repeat.HasValue || i < repeat.Value))
                    {
                        _pauseGate.Wait(_cts.Token);

                        var planned = _ticksStart + (long)i * _periodMs;
                        var now = sw.ElapsedMilliseconds;

                        // 若提前到达，微等待以减小抖动
                        var sleepMs = (int)(planned - now);
                        if (sleepMs > 0)
                            await Task.Delay(sleepMs, _cts.Token);

                        var t0 = sw.ElapsedMilliseconds;
                        bool ok = false;
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
                        var elapsed = (int)(t1 - t0);

                        if (caught != null) _log.Error($"周期 {i + 1} 执行异常：{caught.Message}", "Timer", caught);
                        if (!ok) _log.Warn($"周期 {i + 1} 返回失败", "Timer");

                        // 处理超时
                        if (elapsed > _periodMs)
                        {
                            string msg = $"周期 {i + 1} 超时：耗时={elapsed}ms > 设定={_periodMs}ms，策略={_policy}";
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
                                    _log.Warn(msg + "（保持与时钟对齐，不追赶）", "Timer");
                                    i++;
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
            _pauseGate.Reset();
            _log.Info("定时器已暂停。", "Timer");
        }

        /// <summary>恢复周期执行。</summary>
        public void Resume()
        {
            _pauseGate.Set();
            _log.Info("定时器已恢复。", "Timer");
        }

        /// <summary>终止定时器。</summary>
        public void Stop()
        {
            _cts.Cancel();
            _pauseGate.Set();
            _log.Info("定时器 Stop。", "Timer");
        }
    }
}