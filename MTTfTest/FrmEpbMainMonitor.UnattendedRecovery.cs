using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Config;
using Controller;

namespace MTEmbTest
{
    public partial class FrmEpbMainMonitor
    {
        private const int UnattendedQuiesceTotalTimeoutMs = 30000;

        private void AttachUnattendedRecovery()
        {
            if (_epb == null || _cfg == null) return;
            UnattendedRecoveryCoordinator.Attach(_epb, _cfg);
            UnattendedRecoveryCoordinator.RegisterQuiesceAndFlush(
                QuiesceAndFlushForUnattendedRestartAsync);
        }

        private async Task QuiesceAndFlushForUnattendedRestartAsync()
        {
            var deadline = Stopwatch.GetTimestamp() +
                           (long)(UnattendedQuiesceTotalTimeoutMs / 1000.0 * Stopwatch.Frequency);
            var daqDev1 = _daqDev1;
            var daqDev2 = _daqDev2;
            var acquirer = twoDeviceAiAcquirer;
            try
            {
                // 先刷新末端Raw队列以释放背压槽，再停止并排空上游，最后二次Flush。
                // Flush API没有调用方CancellationToken，因此每一步都必须由共享总期限
                // 的Task.WhenAny隔离，禁止某个文件锁/磁盘I/O永久占住进程重启单飞门。
                if (daqDev1 != null)
                    await AwaitUnattendedQuiesceStageAsync(
                            () => daqDev1.FlushRawToDiskAsync(),
                            "Dev1首次Raw落盘",
                            deadline)
                        .ConfigureAwait(false);
                if (daqDev2 != null)
                    await AwaitUnattendedQuiesceStageAsync(
                            () => daqDev2.FlushRawToDiskAsync(),
                            "Dev2首次Raw落盘",
                            deadline)
                        .ConfigureAwait(false);
                if (acquirer != null)
                {
                    var drainTimeoutMs = Math.Min(
                        10000,
                        RequireUnattendedQuiesceTimeRemaining("DAQ采集/Raw发布链排空", deadline));
                    await AwaitUnattendedQuiesceStageAsync(
                            async () =>
                            {
                                if (!await acquirer.StopAndDrainAsync(drainTimeoutMs)
                                        .ConfigureAwait(false))
                                    throw new TimeoutException(
                                        $"DAQ采集/Raw发布链{drainTimeoutMs}ms内未排空。");
                            },
                            "DAQ采集/Raw发布链排空",
                            deadline)
                        .ConfigureAwait(false);
                }
                if (daqDev1 != null)
                {
                    await AwaitUnattendedQuiesceStageAsync(
                            () => daqDev1.FlushRawToDiskAsync(),
                            "Dev1最终Raw落盘",
                            deadline)
                        .ConfigureAwait(false);
                    await AwaitUnattendedQuiesceStageAsync(
                            () => daqDev1.FlushStatToDiskAsync(),
                            "Dev1最终Stat落盘",
                            deadline)
                        .ConfigureAwait(false);
                }
                if (daqDev2 != null)
                {
                    await AwaitUnattendedQuiesceStageAsync(
                            () => daqDev2.FlushRawToDiskAsync(),
                            "Dev2最终Raw落盘",
                            deadline)
                        .ConfigureAwait(false);
                    await AwaitUnattendedQuiesceStageAsync(
                            () => daqDev2.FlushStatToDiskAsync(),
                            "Dev2最终Stat落盘",
                            deadline)
                        .ConfigureAwait(false);
                }
                if (acquirer != null)
                    await AwaitUnattendedQuiesceStageAsync(
                            () => Task.Run(() => acquirer.Dispose()),
                            "DAQ资源释放",
                            deadline)
                        .ConfigureAwait(false);
            }
            catch
            {
                // StopAll已经确认执行器和压力安全；此处再异步冻结新采样，避免一个
                // 卡死的NI驱动调用突破总期限。失败会回到RestartAsync并释放单飞门，
                // 检查点保留Armed，后续仍可在重启预算内重试。
                RequestAcquirerStopAfterQuiesceFailure(acquirer);
                throw;
            }
        }

        private static async Task AwaitUnattendedQuiesceStageAsync(
            Func<Task> operation,
            string stage,
            long deadline)
        {
            var remainingMs = RequireUnattendedQuiesceTimeRemaining(stage, deadline);
            var operationTask = operation?.Invoke() ??
                                throw new ArgumentNullException(nameof(operation));
            using (var delayCancellation = new CancellationTokenSource())
            {
                var timeoutTask = Task.Delay(remainingMs, delayCancellation.Token);
                var completed = await Task.WhenAny(operationTask, timeoutTask).ConfigureAwait(false);
                if (!ReferenceEquals(completed, operationTask))
                {
                    ObserveLateUnattendedQuiesceTask(operationTask, stage);
                    throw new TimeoutException(
                        $"无人值守静默/落盘超过总期限{UnattendedQuiesceTotalTimeoutMs}ms，" +
                        $"阶段={stage}。");
                }

                delayCancellation.Cancel();
                await operationTask.ConfigureAwait(false);
            }
        }

        private static int RequireUnattendedQuiesceTimeRemaining(string stage, long deadline)
        {
            var remainingTicks = deadline - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
                throw new TimeoutException(
                    $"无人值守静默/落盘超过总期限{UnattendedQuiesceTotalTimeoutMs}ms，" +
                    $"阶段={stage}。");

            var remainingMs = (long)Math.Ceiling(
                remainingTicks * 1000.0 / Stopwatch.Frequency);
            return (int)Math.Max(1L, Math.Min((long)int.MaxValue, remainingMs));
        }

        private static void ObserveLateUnattendedQuiesceTask(Task operationTask, string stage)
        {
            _ = operationTask.ContinueWith(
                faulted => ProjectLogHub.Write(
                    ProjectLogLevel.Error,
                    $"无人值守静默超时后后台阶段最终失败。Stage={stage}",
                    "无人值守恢复",
                    faulted.Exception?.GetBaseException()),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static void RequestAcquirerStopAfterQuiesceFailure(IO.NI.TwoDeviceAiAcquirer acquirer)
        {
            if (acquirer == null) return;
            Task stopTask;
            try
            {
                stopTask = Task.Run(() => acquirer.Stop());
            }
            catch (Exception ex)
            {
                ProjectLogHub.Write(
                    ProjectLogLevel.Error,
                    "无人值守静默失败后无法调度DAQ停止。",
                    "无人值守恢复",
                    ex);
                return;
            }

            ObserveLateUnattendedQuiesceTask(stopTask, "失败后DAQ停止");
        }

        private void UpdateUnattendedRunAuthorization(ChannelRuntimeStateChangedEvent state)
        {
            if (state == null || _cfg?.Test == null) return;
            if (state.State == ChannelRuntimeState.Starting)
            {
                var selected = Enumerable.Range(1, 12)
                    .Where(channel => EpbGroup[channel - 1]?.CtrlJoinTest?.Checked == true)
                    .ToArray();
                UnattendedRecoveryCoordinator.Arm(_cfg, selected, state.RunId);
                return;
            }

            if (state.State != ChannelRuntimeState.Completed) return;
            var checkpoint = UnattendedRunCheckpointStore.Load();
            if (checkpoint == null || !checkpoint.Armed || checkpoint.SelectedChannels == null) return;
            lock (_channelRuntimeStates)
            {
                if (checkpoint.SelectedChannels.All(channel =>
                        _channelRuntimeStates.TryGetValue(channel, out var current) &&
                        current.State == ChannelRuntimeState.Completed))
                    UnattendedRunCheckpointStore.DisarmIfRunMatches(
                        state.RunId.ToString("N"),
                        "FormalRunCompleted");
            }
        }

        internal async Task ResumeFromUnattendedCheckpointAsync(UnattendedRunCheckpoint checkpoint)
        {
            if (checkpoint == null) throw new ArgumentNullException(nameof(checkpoint));
            for (var attempt = 0; attempt < 100 && (_epb == null || _cfg == null); attempt++)
                await Task.Delay(100);
            if (_epb == null || _cfg?.Test == null)
                throw new InvalidOperationException("实时监视硬件与控制对象初始化超时。所有输出保持关闭。");

            var selected = (checkpoint.SelectedChannels ?? Array.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            if (selected.Length == 0)
                throw new InvalidOperationException("检查点没有有效的测试通道。所有输出保持关闭。");

            _cfg.Test.LearnCycles = Math.Max(5, checkpoint.LearnCycles);
            for (var channel = 1; channel <= 12; channel++)
            {
                var control = EpbGroup[channel - 1]?.CtrlJoinTest;
                if (control != null) control.Checked = selected.Contains(channel);
            }

            PostSafetyStatus(
                $"系统恢复预检：项目={checkpoint.TestName}，通道=[{string.Join(",", selected)}]，" +
                $"重新学习={_cfg.Test.LearnCycles}圈；正式计数从SQLite/XML已完成圈数的下一完整圈继续。",
                false);
            await Task.Delay(250);
            BtnStartTest.PerformClick();
        }

        protected override void OnFormClosing(System.Windows.Forms.FormClosingEventArgs e)
        {
            if (!UnattendedRunCheckpointStore.IsGracefulPauseArmed())
                UnattendedRecoveryCoordinator.Disarm("MonitorClosing");
            try
            {
                if (!UnattendedRecoveryCoordinator.DrainBackgroundTasksAsync(2000)
                        .GetAwaiter()
                        .GetResult())
                    ProjectLogHub.Write(
                        ProjectLogLevel.Warning,
                        "窗口关闭时无人值守恢复任务未在2秒内收口；任务仍受监督，进程退出后不会继续控制硬件。",
                        "无人值守恢复");
            }
            catch (Exception ex)
            {
                ProjectLogHub.Write(
                    ProjectLogLevel.Error,
                    "窗口关闭等待无人值守恢复任务异常：" + ex.Message,
                    "无人值守恢复");
            }
            base.OnFormClosing(e);
        }
    }
}
