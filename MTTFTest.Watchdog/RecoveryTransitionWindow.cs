using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using MTTFTest.Watchdog.Protocol;

namespace MTTFTest.Watchdog
{
    /// <summary>
    /// Watchdog-owned recovery surface.  It lives on a dedicated STA thread,
    /// so it remains visible while the monitored UI process is stopped or is
    /// waiting in restart backoff.
    /// </summary>
    internal sealed class RecoveryTransitionWindow : IDisposable
    {
        private readonly object _gate = new object();
        private readonly ManualResetEventSlim _ready = new ManualResetEventSlim(false);
        private readonly Action _dismissPromptRequested;
        private readonly Action _operatorStopRequested;
        private Thread _thread;
        private RecoveryForm _form;
        private bool _disposed;
        private int _hiddenByOperator;

        public RecoveryTransitionWindow(
            Action dismissPromptRequested,
            Action operatorStopRequested)
        {
            _dismissPromptRequested = dismissPromptRequested;
            _operatorStopRequested = operatorStopRequested;
            if (!Environment.UserInteractive)
            {
                _ready.Set();
                return;
            }

            _thread = new Thread(RunUi)
            {
                IsBackground = true,
                Name = "MTTFTest.Watchdog.RecoveryTransition"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _ready.Wait(TimeSpan.FromSeconds(3));
        }

        public void Show(string title, string detail, int remainingSeconds, int attempt)
        {
            if (Volatile.Read(ref _hiddenByOperator) != 0) return;
            Invoke(form =>
            {
                form.UpdateStatus(title, detail, remainingSeconds, attempt);
                if (!form.Visible) form.Show();
                form.WindowState = FormWindowState.Normal;
                form.TopMost = true;
                NativeMethods.ShowWindow(form.Handle, 9);
                NativeMethods.SetWindowPos(
                    form.Handle,
                    new IntPtr(-1),
                    0,
                    0,
                    0,
                    0,
                    0x0001 | 0x0002 | 0x0040);
                form.BringToFront();
                form.Activate();
            });
        }

        public void Hide()
        {
            Invoke(form => form.Hide());
        }

        public void BeginTransition()
        {
            Interlocked.Exchange(ref _hiddenByOperator, 0);
            Invoke(form => form.BeginTransition());
        }

        public void DismissByOperator()
        {
            Interlocked.Exchange(ref _hiddenByOperator, 1);
            Hide();
        }

        public void SetPreserveMainProcessPreferred(bool preferred)
        {
            Invoke(form => form.SetPreserveMainProcessPreferred(preferred));
        }

        private void RunUi()
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var form = new RecoveryForm(
                    () =>
                    {
                        DismissByOperator();
                        _dismissPromptRequested?.Invoke();
                    },
                    _operatorStopRequested);
                // Application.Run(form) implicitly shows the form.  The sidecar
                // is created for every ordinary test run, so that behavior made
                // the recovery surface appear even though no takeover had
                // occurred.  Create the native handle while it is still hidden,
                // then run only the message loop; Show() is the sole visibility
                // transition and is called by genuine takeover/relaunch paths.
                var unusedHandle = form.Handle;
                lock (_gate) _form = form;
                _ready.Set();
                using (var context = new ApplicationContext())
                {
                    form.FormClosed += (_, __) => context.ExitThread();
                    Application.Run(context);
                }
            }
            catch
            {
                _ready.Set();
            }
        }

        private void Invoke(Action<RecoveryForm> action)
        {
            if (action == null || _disposed) return;
            _ready.Wait(TimeSpan.FromSeconds(1));
            RecoveryForm form;
            lock (_gate) form = _form;
            if (form == null || form.IsDisposed) return;
            try
            {
                if (form.InvokeRequired) form.BeginInvoke(action, form);
                else action(form);
            }
            catch (InvalidOperationException) { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _ready.Wait(TimeSpan.FromSeconds(1));
            RecoveryForm form;
            lock (_gate) form = _form;
            try
            {
                if (form != null && !form.IsDisposed)
                {
                    if (form.InvokeRequired) form.BeginInvoke((Action)(() => form.CloseForShutdown()));
                    else form.CloseForShutdown();
                }
            }
            catch { }
            try { _ready.Dispose(); } catch { }
        }

        private sealed class RecoveryForm : Form
        {
            private readonly Label _title;
            private readonly Label _detail;
            private readonly Label _countdown;
            private readonly ProgressBar _progress;
            private readonly Button _dismissPrompt;
            private readonly Button _operatorStop;
            private readonly System.Windows.Forms.Timer _topmostTimer;
            private bool _allowClose;
            private int _operatorStopRaised;

            public RecoveryForm(
                Action dismissPromptRequested,
                Action operatorStopRequested)
            {
                Text = "MT EPB 试验系统自动恢复";
                ClientSize = new Size(660, 390);
                BackColor = Color.FromArgb(245, 249, 252);
                FormBorderStyle = FormBorderStyle.FixedDialog;
                StartPosition = FormStartPosition.CenterScreen;
                MaximizeBox = false;
                MinimizeBox = false;
                ControlBox = false;
                ShowInTaskbar = true;
                TopMost = true;
                Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular, GraphicsUnit.Point);

                var banner = new Panel
                {
                    Dock = DockStyle.Top,
                    Height = 72,
                    BackColor = Color.FromArgb(30, 116, 190)
                };
                _title = new Label
                {
                    Dock = DockStyle.Fill,
                    ForeColor = Color.White,
                    Font = new Font("Microsoft YaHei UI", 18F, FontStyle.Bold),
                    TextAlign = ContentAlignment.MiddleCenter,
                    Text = "系统正在安全恢复"
                };
                banner.Controls.Add(_title);

                _detail = new Label
                {
                    Location = new Point(34, 95),
                    Size = new Size(592, 64),
                    TextAlign = ContentAlignment.MiddleCenter,
                    Font = new Font("Microsoft YaHei UI", 11F),
                    ForeColor = Color.FromArgb(40, 55, 70)
                };
                _countdown = new Label
                {
                    Location = new Point(34, 161),
                    Size = new Size(592, 46),
                    TextAlign = ContentAlignment.MiddleCenter,
                    Font = new Font("Microsoft YaHei UI", 20F, FontStyle.Bold),
                    ForeColor = Color.FromArgb(30, 116, 190)
                };
                _progress = new ProgressBar
                {
                    Location = new Point(84, 213),
                    Size = new Size(492, 18),
                    Style = ProgressBarStyle.Marquee,
                    MarqueeAnimationSpeed = 25
                };
                var safety = new Label
                {
                    Location = new Point(34, 240),
                    Size = new Size(592, 48),
                    TextAlign = ContentAlignment.MiddleCenter,
                    ForeColor = Color.FromArgb(180, 74, 25),
                    Text = "所有控制输出保持 OFF。请勿重复启动软件或操作试验设备。"
                };
                _dismissPrompt = new Button
                {
                    Location = new Point(34, 313),
                    Size = new Size(282, 42),
                    BackColor = Color.FromArgb(30, 116, 190),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
                    Text = RecoveryTransitionPolicy.DismissPromptButtonText,
                    UseVisualStyleBackColor = false,
                    Cursor = Cursors.Hand,
                    TabStop = true
                };
                _dismissPrompt.FlatAppearance.BorderSize = 0;
                _dismissPrompt.Click += (_, __) =>
                {
                    _dismissPrompt.Enabled = false;
                    ThreadPool.QueueUserWorkItem(_ => dismissPromptRequested?.Invoke());
                };

                _operatorStop = new Button
                {
                    Location = new Point(344, 313),
                    Size = new Size(282, 42),
                    BackColor = Color.FromArgb(198, 56, 56),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
                    Text = RecoveryTransitionPolicy.OperatorStopButtonText,
                    UseVisualStyleBackColor = false,
                    Cursor = Cursors.Hand,
                    TabStop = true
                };
                _operatorStop.FlatAppearance.BorderSize = 0;
                _operatorStop.Click += (_, __) =>
                {
                    var confirmed = MessageBox.Show(
                        this,
                        RecoveryTransitionPolicy.OperatorStopConfirmationText,
                        RecoveryTransitionPolicy.OperatorStopConfirmationTitle,
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2) == DialogResult.Yes;
                    if (!confirmed) return;
                    if (Interlocked.Exchange(ref _operatorStopRaised, 1) != 0) return;
                    _dismissPrompt.Enabled = false;
                    _operatorStop.Enabled = false;
                    _operatorStop.Cursor = Cursors.WaitCursor;
                    _operatorStop.Text = "正在安全停止，请稍候…";
                    _title.Text = "正在停止自动恢复";
                    _detail.Text = "正在请求主程序全断能；超时后只终止身份匹配的恢复进程。";
                    _countdown.Text = "不会再次自动启动";
                    ThreadPool.QueueUserWorkItem(_ => operatorStopRequested?.Invoke());
                };

                AcceptButton = _dismissPrompt;
                CancelButton = _dismissPrompt;
                Controls.Add(_operatorStop);
                Controls.Add(_dismissPrompt);
                Controls.Add(safety);
                Controls.Add(_progress);
                Controls.Add(_countdown);
                Controls.Add(_detail);
                Controls.Add(banner);

                _topmostTimer = new System.Windows.Forms.Timer { Interval = 1000 };
                _topmostTimer.Tick += (_, __) =>
                {
                    if (!Visible) return;
                    TopMost = true;
                    BringToFront();
                };
                _topmostTimer.Start();
            }

            public void UpdateStatus(string title, string detail, int remainingSeconds, int attempt)
            {
                if (Volatile.Read(ref _operatorStopRaised) != 0) return;
                _title.Text = string.IsNullOrWhiteSpace(title) ? "系统正在安全恢复" : title;
                _detail.Text = (string.IsNullOrWhiteSpace(detail) ? "正在等待主程序重新连接" : detail) +
                               (attempt > 0 ? $"\r\n恢复尝试：第 {attempt} 次" : string.Empty);
                _countdown.Text = RecoveryTransitionPolicy.FormatCountdown(remainingSeconds);
                _progress.Style = remainingSeconds > 0
                    ? ProgressBarStyle.Continuous
                    : ProgressBarStyle.Marquee;
                if (remainingSeconds > 0)
                {
                    _progress.Minimum = 0;
                    _progress.Maximum = 60;
                    _progress.Value = Math.Min(_progress.Maximum, remainingSeconds);
                }
            }

            public void SetPreserveMainProcessPreferred(bool preferred)
            {
                if (Volatile.Read(ref _operatorStopRaised) != 0) return;
                _dismissPrompt.Text = preferred
                    ? RecoveryTransitionPolicy.PreserveProcessButtonText
                    : RecoveryTransitionPolicy.DismissPromptButtonText;
                AcceptButton = _dismissPrompt;
                _dismissPrompt.Select();
            }

            public void BeginTransition()
            {
                if (Volatile.Read(ref _operatorStopRaised) != 0) return;
                _dismissPrompt.Enabled = true;
                _dismissPrompt.Cursor = Cursors.Hand;
                _operatorStop.Enabled = true;
                _operatorStop.Cursor = Cursors.Hand;
                _operatorStop.Text = RecoveryTransitionPolicy.OperatorStopButtonText;
                SetPreserveMainProcessPreferred(false);
            }

            public void CloseForShutdown()
            {
                _allowClose = true;
                Close();
            }

            protected override void OnFormClosing(FormClosingEventArgs e)
            {
                if (!_allowClose)
                {
                    e.Cancel = true;
                    Hide();
                    return;
                }
                _topmostTimer.Stop();
                base.OnFormClosing(e);
            }
        }

        private static class NativeMethods
        {
            [DllImport("user32.dll")]
            internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

            [DllImport("user32.dll")]
            internal static extern bool SetWindowPos(
                IntPtr hWnd,
                IntPtr hWndInsertAfter,
                int x,
                int y,
                int cx,
                int cy,
                uint flags);
        }
    }
}
