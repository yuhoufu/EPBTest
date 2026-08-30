using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Controller;

namespace MTEmbTest
{
    public partial class FrmEpbMainMonitor
    {
        private Panel _closeOverlay;
        private Label _closeOverlayTitle;
        private Label _closeOverlayStage;
        private ProgressBar _closeOverlayProgress;
        private int _closeOverlayScheduled;

        private async void ScheduleCloseOverlay()
        {
            if (Interlocked.Exchange(ref _closeOverlayScheduled, 1) != 0) return;
            await Task.Delay(250).ConfigureAwait(true);
            if (Volatile.Read(ref _closingReentry) == 1 && !IsDisposed)
                ShowCloseOverlay("正在建立安全关闭事务…");
        }

        private void ShowCloseOverlay(string stage)
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired)
            {
                try { BeginInvoke((Action<string>)ShowCloseOverlay, stage); } catch { }
                return;
            }
            if (_closeOverlay == null)
            {
                _closeOverlay = new Panel
                {
                    Dock = DockStyle.Fill,
                    BackColor = Color.FromArgb(245, 248, 252),
                    Padding = new Padding(32)
                };
                _closeOverlayTitle = new Label
                {
                    AutoSize = false,
                    Dock = DockStyle.Top,
                    Height = 56,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Font = new Font(Font.FontFamily, 16F, FontStyle.Bold),
                    ForeColor = Color.FromArgb(31, 92, 154),
                    Text = "正在安全关闭，请稍候"
                };
                _closeOverlayProgress = new ProgressBar
                {
                    Dock = DockStyle.Top,
                    Height = 20,
                    Style = ProgressBarStyle.Marquee,
                    MarqueeAnimationSpeed = 24
                };
                _closeOverlayStage = new Label
                {
                    AutoSize = false,
                    Dock = DockStyle.Top,
                    Height = 72,
                    Padding = new Padding(0, 18, 0, 0),
                    TextAlign = ContentAlignment.TopCenter,
                    Font = new Font(Font.FontFamily, 11F),
                    ForeColor = Color.FromArgb(64, 64, 64)
                };
                _closeOverlay.Controls.Add(_closeOverlayStage);
                _closeOverlay.Controls.Add(_closeOverlayProgress);
                _closeOverlay.Controls.Add(_closeOverlayTitle);
                Controls.Add(_closeOverlay);
            }
            _closeOverlayStage.Text = stage ?? "正在安全关闭…";
            _closeOverlay.Visible = true;
            _closeOverlay.BringToFront();
            UseWaitCursor = true;
        }

        private void UpdateCloseOverlay(StopSafetyProgressSnapshot progress)
        {
            if (progress == null || Volatile.Read(ref _closingReentry) != 1) return;
            var text = progress.Stage < StopSafetyStage.StartPowerDisable
                ? "正在停止全部电机…"
                : progress.Stage < StopSafetyStage.ReleaseHydraulics
                    ? "正在关闭全部供电…"
                    : progress.Stage < StopSafetyStage.ClosePersistenceBoundary
                        ? "正在执行液压卸压并确认压力…"
                        : progress.Stage < StopSafetyStage.VerifyLogicalQuiescence
                            ? "正在保存 Raw、SQLite 和最新圈数据…"
                            : progress.Stage < StopSafetyStage.Completed
                                ? "正在确认逻辑静默与释放 Watchdog…"
                                : "安全终态已完成，正在关闭窗口…";
            ShowCloseOverlay(text);
        }

        private void HideCloseOverlay()
        {
            if (InvokeRequired)
            {
                try { BeginInvoke((Action)HideCloseOverlay); } catch { }
                return;
            }
            if (_closeOverlay != null) _closeOverlay.Visible = false;
            UseWaitCursor = false;
            Interlocked.Exchange(ref _closeOverlayScheduled, 0);
        }
    }
}
