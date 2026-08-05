using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using MTEmbTest;

namespace MtEmbTest
{
    public partial class Main_Frm
    {
        private RecoveryStartupIntent _recoveryStartupIntent;

        internal Main_Frm(RecoveryStartupIntent recoveryStartupIntent) : this()
        {
            _recoveryStartupIntent = recoveryStartupIntent;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_recoveryStartupIntent == null) return;
            var intent = _recoveryStartupIntent;
            _recoveryStartupIntent = null;
            BeginInvoke((Action)(async () => await ResumeUnattendedRunAsync(intent)));
        }

        private async Task ResumeUnattendedRunAsync(RecoveryStartupIntent intent)
        {
            try
            {
                if (!UnattendedRunCheckpointStore.TryConsume(
                        intent,
                        Cfg,
                        out var checkpoint,
                        out var error))
                {
                    MessageBox.Show(
                        "无人值守自动续测已拒绝：" + error + "\r\n所有输出保持关闭。",
                        "系统恢复",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    return;
                }

                var monitor = new FrmEpbMainMonitor { Name = "实时监视" };
                OpenChildForm(monitor);
                await monitor.ResumeFromUnattendedCheckpointAsync(checkpoint);
            }
            catch (Exception ex)
            {
                UnattendedRunCheckpointStore.Disarm("RecoveryPreflightFailed");
                MessageBox.Show(
                    "无人值守恢复预检失败：" + ex.Message + "\r\n检查点已撤销，所有输出保持关闭。",
                    "系统恢复",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
    }
}
