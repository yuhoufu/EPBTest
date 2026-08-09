using System;
using System.Threading;
using System.Threading.Tasks;

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
            if (Interlocked.CompareExchange(ref _batchStartUiGuard, 1, 0) != 0)
            {
                LogInfo("开始/暂停操作正在处理中，请勿重复点击。");
                return;
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
                    await _epb.ResumeBatchAsync().ConfigureAwait(true);
                    ClearGracefulPauseCheckpoint("SameProcessResumed");
                    LogInfo("批次已通过恢复预检并继续试验。");
                    return;
                }

                if (requestedState == Controller.BatchPauseState.Running)
                {
                    await _epb.PauseBatchGracefullyAsync().ConfigureAwait(true);
                    SaveGracefulPauseCheckpoint();
                    LogInfo("批次已在所有卡钳完成当前圈后安全暂停。");
                    return;
                }

                // 新试验或跨进程检查点恢复必须从完整正式包启动。bin\Release 可能被
                // VS 普通生成覆盖而仍保留旧版本号；只看窗口标题无法识别混包。
                var identity = Controller.RuntimeBuildIdentity.Capture();
                var package = Controller.ReleasePackageVerifier.VerifyCurrent(identity, refresh: true);
                if (!package.Verified)
                    throw new InvalidOperationException(
                        "当前程序目录不是完整、已批准的正式候选包，已拒绝开始试验。\r\n" +
                        $"Code={package.Code}\r\n{package.Detail}\r\n" +
                        "请从独立版本发布目录重新启动，禁止直接运行 bin\\Release。" );

                if (await TryResumePendingGracefulPauseAsync().ConfigureAwait(true))
                    return;

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
                            })
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

                BtnStartTest_Click(sender, e);

                // 原处理函数是 async void；只等待控制层建立会话，不再占用按钮到整批结束。
                for (var i = 0; i < 20 && !(_epb?.IsBatchSessionActive ?? false); i++)
                    await Task.Delay(100).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                LogInfo($"开始/暂停/继续操作失败：{ex.Message}");
                System.Windows.Forms.MessageBox.Show(
                    ex.Message,
                    "试验状态转换失败",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Warning);
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
