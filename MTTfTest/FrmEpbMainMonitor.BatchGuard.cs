using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Controller;

namespace MTEmbTest
{
    public partial class FrmEpbMainMonitor
    {
        private int _batchStartUiGuard;

        /// <summary>
        /// 开始/暂停/继续复用入口。按钮只提交状态转换请求，显示状态由控制层事件回写。
        /// </summary>
        private async void BtnStartTestGuarded_Click(object sender, EventArgs e)
        {
            if (!ProcessRestartUiPolicy.CanStartInProcess(
                    _epb?.RequiresProcessRestart == true))
            {
                ApplyBatchPauseState(_epb.CurrentBatchPauseState);
                LogInfo(ProcessRestartUiPolicy.GetOperatorMessage(false));
                return;
            }
            try
            {
                await HandleBatchStartRequestAsync(
                        sender,
                        e,
                        unattendedRecovery: false,
                        expectedChannels: null)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // 人工按钮入口保留可见提示；自动恢复入口由其调用者记录并进入有界
                // 进程交接，绝不能在无人值守路径弹出需要人工确认的消息框。
                LogInfo($"开始/暂停/继续操作失败：{ex.Message}");
                System.Windows.Forms.MessageBox.Show(
                    ex.Message,
                    "试验状态转换失败",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Warning);
            }
        }

        internal Task<BatchStartResult> StartUnattendedBatchAsync(int[] expectedChannels)
        {
            var expected = (expectedChannels ?? Array.Empty<int>())
                .Where(channel => channel >= 1 && channel <= 12)
                .Distinct()
                .OrderBy(channel => channel)
                .ToArray();
            if (expected.Length == 0)
                throw new InvalidOperationException("无人值守恢复没有有效的目标通道。");
            return HandleBatchStartRequestAsync(
                this,
                EventArgs.Empty,
                unattendedRecovery: true,
                expectedChannels: expected);
        }

        private async Task<BatchStartResult> HandleBatchStartRequestAsync(
            object sender,
            EventArgs e,
            bool unattendedRecovery,
            int[] expectedChannels)
        {
            if (Interlocked.CompareExchange(ref _batchStartUiGuard, 1, 0) != 0)
            {
                if (unattendedRecovery)
                    throw new InvalidOperationException("无人值守恢复启动入口正被其它操作占用。");
                LogInfo("开始/暂停操作正在处理中，请勿重复点击。");
                return null;
            }

            try
            {
                BtnStartTest.Enabled = false;
                BtnStartTest.Cursor = System.Windows.Forms.Cursors.WaitCursor;

                var requestedState = _epb?.CurrentBatchPauseState ?? Controller.BatchPauseState.Idle;
                if (requestedState == Controller.BatchPauseState.Running)
                    BtnStartTest.Text = "正在暂停…";
                else if (requestedState == Controller.BatchPauseState.Paused)
                    BtnStartTest.Text = "正在恢复…";

                if (requestedState == Controller.BatchPauseState.Paused)
                {
                    if (unattendedRecovery)
                        throw new InvalidOperationException(
                            "自动重启子进程出现非预期 Paused 状态，拒绝把它当作新运行继续。");
                    await _epb.ResumeBatchAsync().ConfigureAwait(true);
                    ClearGracefulPauseCheckpoint("SameProcessResumed");
                    LogInfo("批次已通过恢复预检并继续试验。");
                    return null;
                }

                if (requestedState == Controller.BatchPauseState.Running)
                {
                    if (unattendedRecovery)
                        throw new InvalidOperationException(
                            "自动重启子进程已存在 Running 批次，拒绝重复提交恢复启动。");
                    await _epb.PauseBatchGracefullyAsync().ConfigureAwait(true);
                    SaveGracefulPauseCheckpoint();
                    LogInfo("批次已在所有卡钳完成当前圈后安全暂停。");
                    return null;
                }

                if (!unattendedRecovery &&
                    await TryResumePendingGracefulPauseAsync().ConfigureAwait(true))
                    return null;

                var explicitlyStopped = Volatile.Read(ref _operatorStopRequested) != 0;

                // 只要用户再次选择“开始”，就把上一批次的软件问题和仍在收尾的启动任务一并抛弃。
                // 同一次点击会等待安全清场结束并直接发起新批次，不要求用户稍后再点一次。
                if ((_epb?.IsBatchSessionActive ?? false) || _batchCts != null)
                {
                    LogInfo("收到重新开始请求：正在抛弃旧批次状态并执行安全清场，完成后自动启动。");
                    try { _batchCts?.Cancel(); }
                    catch (ObjectDisposedException) { }

                    var safety = await _epb.PrepareForFreshRestartAsync(
                            new Controller.StopContext
                            {
                                Source = Controller.StopSource.ManualUi,
                                Reason = "用户请求重新开始；抛弃旧批次问题并立即进入新批次",
                                Initiator = nameof(BtnStartTestGuarded_Click),
                                CorrelationId = Guid.NewGuid().ToString("N"),
                                RequestedUtc = DateTime.UtcNow
                            },
                            discardHistoricalStopChecks: explicitlyStopped)
                        .ConfigureAwait(true);
                    ClearGracefulPauseCheckpoint("FreshRestart");
                    LogInfo(
                        safety.PressureSafeConfirmed
                            ? "旧批次清场完成，正在自动开始新试验。"
                            : "旧批次电机与电源已确认关闭；压力证据暂缺，不阻碍实时预检和重新开始。");
                }

                // 新试验不继承上一次报警的面板指示灯、蜂鸣器及内部活动报警集合。
                // ClearAllAsync 自带报警模块的重装延迟，期间若产生新报警会在延迟后重新输出。
                if (_alarmManager != null)
                {
                    try
                    {
                        await _alarmManager.ClearAllAsync().ConfigureAwait(true);
                        LogInfo("开始新试验前已自动复位上次声光报警。");
                    }
                    catch (Exception alarmEx)
                    {
                        // 声光报警属于观察/提示设备，清除失败不能成为卡钳重新开始的许可门。
                        // 新试验中的实时控制保护仍由控制层独立执行并再次发布报警。
                        LogInfo(
                            $"上次声光报警复位异常，但不阻碍重新开始：{alarmEx.Message}");
                    }
                }

                if (!unattendedRecovery && !WatchdogRuntime.IsAttached)
                {
                    try
                    {
                        WatchdogRuntime.ConfigureJournalExportPath(
                            Path.Combine(_cfg.Test.StoreDir, _cfg.Test.TestName, "WatchdogSessions"));
                    }
                    catch (Exception exportPathError)
                    {
                        LogInfo("独立看门狗Journal封存路径配置失败；不阻止试验启动：" +
                            exportPathError.GetBaseException().Message);
                    }
                    var watchdogChannels = Enumerable.Range(1, 12)
                        .Where(channel => EpbGroup[channel - 1]?.CtrlJoinTest?.Checked == true)
                        .ToArray();
                    var watchdog = await WatchdogRuntime.StartSessionAsync(watchdogChannels)
                        .ConfigureAwait(true);
                    if (watchdog.Attached)
                    {
                        UnattendedRunCheckpointStore.BindWatchdogSession(watchdog.SessionId);
                        WatchdogRuntime.SetHeartbeatProvider(CreateWatchdogHeartbeat);
                        LogInfo("独立看门狗已就绪。");
                        if (!string.IsNullOrWhiteSpace(watchdog.Warning))
                            LogInfo("[警告] " + watchdog.Warning);
                    }
                    else
                    {
                        LogInfo("[警告] " + watchdog.Warning);
                    }
                }

                var startTask = StartNewBatchAsync(unattendedRecovery);

                if (unattendedRecovery)
                {
                    var startResult = await startTask.ConfigureAwait(true);
                    var validationError = EpbManager.ValidateUnattendedBatchStartResult(
                        expectedChannels,
                        startResult);
                    if (!string.IsNullOrWhiteSpace(validationError))
                        throw new InvalidOperationException(validationError);
                    Interlocked.Exchange(ref _operatorStopRequested, 0);
                    return startResult;
                }

                // 原处理函数是 async void；只等待控制层建立会话，不再占用按钮到整批结束。
                for (var i = 0; i < 20 && !(_epb?.IsBatchSessionActive ?? false); i++)
                    await Task.Delay(100).ConfigureAwait(true);
                if (_epb?.IsBatchSessionActive ?? false)
                    Interlocked.Exchange(ref _operatorStopRequested, 0);

                // 人工入口维持原来的快速释放按钮行为，但必须观察后台启动任务，避免
                // async void 异常成为未观察异常。StartNewBatchAsync 的人工路径会自行
                // 记录并显示可操作错误。
                _ = startTask.ContinueWith(
                    task => LogInfo($"启动后台任务异常：{task.Exception?.GetBaseException().Message}"),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
                return null;
            }
            finally
            {
                // 先释放 UI 操作锁，再按控制层最终状态恢复按钮；否则稳定态仍会被
                // guard 判为不可点击，表现为“继续试验”无悬浮、无手型且点击无效。
                Interlocked.Exchange(ref _batchStartUiGuard, 0);

                if (!IsDisposed && BtnStartTest != null)
                    ApplyBatchPauseState(_epb?.CurrentBatchPauseState ?? Controller.BatchPauseState.Idle);
            }
        }
    }
}
