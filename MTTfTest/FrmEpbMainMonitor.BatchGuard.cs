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

                if (_epb?.CurrentBatchPauseState == Controller.BatchPauseState.Paused)
                {
                    await _epb.ResumeBatchAsync().ConfigureAwait(true);
                    ClearGracefulPauseCheckpoint("SameProcessResumed");
                    LogInfo("批次已通过恢复预检并继续试验。");
                    return;
                }

                if (_epb?.CurrentBatchPauseState == Controller.BatchPauseState.Running)
                {
                    await _epb.PauseBatchGracefullyAsync().ConfigureAwait(true);
                    SaveGracefulPauseCheckpoint();
                    LogInfo("批次已在所有卡钳完成当前圈后安全暂停。");
                    return;
                }

                if (_epb?.IsBatchSessionActive ?? false)
                {
                    LogInfo("试验正在启动、学习或执行状态转换，请稍候。");
                    return;
                }

                if (await TryResumePendingGracefulPauseAsync().ConfigureAwait(true))
                    return;

                // 新试验不继承上一次报警的面板指示灯、蜂鸣器及内部活动报警集合。
                // ClearAllAsync 自带报警模块的重装延迟，期间若产生新报警会在延迟后重新输出。
                if (_alarmManager != null)
                {
                    await _alarmManager.ClearAllAsync().ConfigureAwait(true);
                    LogInfo("开始新试验前已自动复位上次声光报警。");
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
                if (!IsDisposed && BtnStartTest != null)
                    ApplyBatchPauseState(_epb?.CurrentBatchPauseState ?? Controller.BatchPauseState.Idle);

                Interlocked.Exchange(ref _batchStartUiGuard, 0);
            }
        }
    }
}
