using System;
using System.Threading;
using System.Threading.Tasks;

namespace MTEmbTest
{
    public partial class FrmEpbMainMonitor
    {
        private int _batchStartUiGuard;

        /// <summary>
        /// UI 入口防重复：批量会话存续期间禁用“开始试验”，并把真正的互斥交给 EpbManager。
        /// </summary>
        private async void BtnStartTestGuarded_Click(object sender, EventArgs e)
        {
            if (Interlocked.CompareExchange(ref _batchStartUiGuard, 1, 0) != 0 ||
                (_epb?.IsBatchSessionActive ?? false))
            {
                LogInfo("试验正在启动或运行中，请勿重复点击“开始试验”。");
                return;
            }

            try
            {
                BtnStartTest.Enabled = false;

                // 新试验不继承上一次报警的面板指示灯、蜂鸣器及内部活动报警集合。
                // ClearAllAsync 自带报警模块的重装延迟，期间若产生新报警会在延迟后重新输出。
                if (_alarmManager != null)
                {
                    await _alarmManager.ClearAllAsync().ConfigureAwait(true);
                    LogInfo("开始新试验前已自动复位上次声光报警。");
                }

                BtnStartTest_Click(sender, e);

                // 原处理函数是 async void；等待控制层会话真正建立或确认本次没有启动。
                for (var i = 0; i < 20 && !(_epb?.IsBatchSessionActive ?? false); i++)
                    await Task.Delay(100).ConfigureAwait(true);

                while (_epb?.IsBatchSessionActive ?? false)
                    await Task.Delay(250).ConfigureAwait(true);
            }
            finally
            {
                if (!IsDisposed && BtnStartTest != null)
                    BtnStartTest.Enabled = true;

                Interlocked.Exchange(ref _batchStartUiGuard, 0);
            }
        }
    }
}
