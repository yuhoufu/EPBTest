namespace MTEmbTest
{
    public partial class FrmEpbMainMonitor
    {
        /// <summary>
        /// 摘要面板行标题仅显示“完成次数”；圈数数值由 LedRunCycles 承接，
        /// 圈统计明细（有效正式/学习资格/作废）保留在落盘与统计层口径中。
        /// </summary>
        /// <param name="channel">EPB 通道号（1..12）。</param>
        private void UpdateCycleAccountingDisplay(int channel)
        {
            uiLabel57.Text = "完成次数";
        }
    }
}
