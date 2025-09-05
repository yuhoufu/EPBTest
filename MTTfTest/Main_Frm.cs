using System;
using System.Drawing;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Controller;
using CustomTcpClient;
using DataOperation;
using MTEmbTest;
using MTEmbTest.Properties;

namespace MtEmbTest
{
    public partial class Main_Frm : Form
    {
        public bool[] IsPowerConnect = { false };
        private readonly AsyncTcpClient[] powerClient = new AsyncTcpClient[1];

        public Main_Frm()
        {
            InitializeComponent();
            ConfigureMenuStrip();

            // 放在程序启动早期（如 Form_Load / Main 里）
            _ = typeof(EpbManager).FullName; // 用 Controller 内真实存在的公开类型名替换
        }


        /// <summary>
        ///     水平平铺所有子窗口
        /// </summary>
        /// <param name="sender">界面菜单输入</param>
        /// <param name="e">输入事件</param>
        /// <returns>void</returns>
        private void TsmHorizon_Click(object sender, EventArgs e)
        {
            LayoutMdi(MdiLayout.TileHorizontal);
            TsmHorizon.Checked = true;
            TsmLayout.Checked = false;
            TsmVertical.Checked = false;
        }

        /// <summary>
        ///     垂直平铺所有子窗口
        /// </summary>
        /// <param name="sender">界面菜单输入</param>
        /// <param name="e">输入事件</param>
        /// <returns>void</returns>
        private void TsmVertical_Click(object sender, EventArgs e)
        {
            LayoutMdi(MdiLayout.TileVertical);
            TsmHorizon.Checked = false;
            TsmLayout.Checked = false;
            TsmVertical.Checked = true;
        }

        /// <summary>
        ///     层叠所有子窗口
        /// </summary>
        /// <param name="sender">界面菜单输入</param>
        /// <param name="e">输入事件</param>
        /// <returns>void</returns>
        private void TsmLayout_Click(object sender, EventArgs e)
        {
            LayoutMdi(MdiLayout.Cascade);
            TsmHorizon.Checked = false;
            TsmLayout.Checked = true;
            TsmVertical.Checked = false;
        }


        private void Main_Frm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (MdiChildren.Length > 0)
            {
                MessageBox.Show("可能存在正在运行的试验，请先停止试验，关闭子窗口，再退出程序！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                e.Cancel = true;
            }


            if (ClsGlobal.PowerStatus[0] > 0)
            {
                MessageBox.Show("请关闭电源", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                e.Cancel = true;
            }
        }


        private int GetTestNo()
        {
            var LastTime = DateTime.Parse(ConfigOperation.SetOneItem("TestDate"));
            var LastNo = int.Parse(ConfigOperation.SetOneItem("TestNo"));

            if (DateTime.Now.DayOfYear > LastTime.DayOfYear) //新的一天返回1
                return 0;

            return LastNo;
        }

        private void Main_Frm_Load(object sender, EventArgs e)
        {
            try
            {
                ClsGlobal.ClampCount = int.Parse(ConfigOperation.SetOneItem("ClampCount"));
                ClsGlobal.ReleaseCount = int.Parse(ConfigOperation.SetOneItem("ReleaseCount"));
                ClsGlobal.PushCount = int.Parse(ConfigOperation.SetOneItem("PushCount"));
                ClsGlobal.ClampSpan = int.Parse(ConfigOperation.SetOneItem("ClampSpan"));
                ClsGlobal.ReleaseSpan = int.Parse(ConfigOperation.SetOneItem("ReleaseSpan"));
                ClsGlobal.PushSpan = int.Parse(ConfigOperation.SetOneItem("PushSpan"));
                ClsGlobal.ReleaseWaitSpan = int.Parse(ConfigOperation.SetOneItem("ReleaseWaitSpan"));
                ClsGlobal.IsPushFirst = int.Parse(ConfigOperation.SetOneItem("IsPushFirst"));
                ClsGlobal.IsLiner = int.Parse(ConfigOperation.SetOneItem("IsLiner"));


                ClsGlobal.DRate = int.Parse(ConfigOperation.SetOneItem("DRate"));
                ClsGlobal.ARate = int.Parse(ConfigOperation.SetOneItem("ARate"));
                ClsGlobal.CardNo = int.Parse(ConfigOperation.SetOneItem("CardNo"));

                ClsGlobal.MsgInterval = int.Parse(ConfigOperation.SetOneItem("MsgInterval"));
                ClsGlobal.ResistorEnabel = int.Parse(ConfigOperation.SetOneItem("ResistorEnabel"));
                ClsGlobal.Protocol = int.Parse(ConfigOperation.SetOneItem("Protocol"));
                ClsGlobal.FrameType = int.Parse(ConfigOperation.SetOneItem("FrameType"));

                ClsGlobal.FrameSendType = int.Parse(ConfigOperation.SetOneItem("FrameSendType"));
                ClsGlobal.FrameExpType = int.Parse(ConfigOperation.SetOneItem("FrameExpType"));
                ClsGlobal.FrameTimerNo = int.Parse(ConfigOperation.SetOneItem("FrameTimerNo"));


                ClsGlobal.SendForceScale = short.Parse(ConfigOperation.SetOneItem("SendForceScale"));
                ClsGlobal.RecvForceScale = double.Parse(ConfigOperation.SetOneItem("RecvForceScale"));
                ClsGlobal.RecvForceJudgeDelt = double.Parse(ConfigOperation.SetOneItem("RecvForceJudgeDelt"));
                ClsGlobal.RecvMsgInterval = double.Parse(ConfigOperation.SetOneItem("RecvMsgInterval"));


                ClsGlobal.ClampPosition = short.Parse(ConfigOperation.SetOneItem("ClampPosition"));
                ClsGlobal.ClampSpeed = short.Parse(ConfigOperation.SetOneItem("ClampSpeed"));
                ClsGlobal.ClampModReq = byte.Parse(ConfigOperation.SetOneItem("ClampModReq"));
                ClsGlobal.ClampTorque = short.Parse(ConfigOperation.SetOneItem("ClampTorque"));
                ClsGlobal.ClampNormalMode = byte.Parse(ConfigOperation.SetOneItem("ClampNormalMode"));
                ClsGlobal.ClampForce = short.Parse(ConfigOperation.SetOneItem("ClampForce"));
                ClsGlobal.ClampEnable = byte.Parse(ConfigOperation.SetOneItem("ClampEnable"));
                ClsGlobal.ClampForceReq = ushort.Parse(ConfigOperation.SetOneItem("ClampForceReq"));
                ClsGlobal.ReleasePosition = short.Parse(ConfigOperation.SetOneItem("ReleasePosition"));
                ClsGlobal.ReleaseSpeed = short.Parse(ConfigOperation.SetOneItem("ReleaseSpeed"));
                ClsGlobal.ReleaseModeReq = byte.Parse(ConfigOperation.SetOneItem("ReleaseModeReq"));
                ClsGlobal.ReleaseTorque = short.Parse(ConfigOperation.SetOneItem("ReleaseTorque"));
                ClsGlobal.ReleaseNormalMode = byte.Parse(ConfigOperation.SetOneItem("ReleaseNormalMode"));
                ClsGlobal.ReleaseForce = short.Parse(ConfigOperation.SetOneItem("ReleaseForce"));
                ClsGlobal.ReleaseEnable = byte.Parse(ConfigOperation.SetOneItem("ReleaseEnable"));
                ClsGlobal.ReleaseForceReq = ushort.Parse(ConfigOperation.SetOneItem("ReleaseForceReq"));

                ClsGlobal.SerialPort = ConfigOperation.SetOneItem("SerialPort");
                ClsGlobal.Baud = int.Parse(ConfigOperation.SetOneItem("Baud"));
                ClsGlobal.Parity = int.Parse(ConfigOperation.SetOneItem("Parity"));
                ClsGlobal.DataBits = int.Parse(ConfigOperation.SetOneItem("DataBits"));
                ClsGlobal.StopBit = int.Parse(ConfigOperation.SetOneItem("StopBit"));

                ClsGlobal.FL_Send = ConfigOperation.SetOneItem("FL_Send");
                ClsGlobal.FL_Recv = ConfigOperation.SetOneItem("FL_Recv");
                ClsGlobal.FR_Send = ConfigOperation.SetOneItem("FR_Send");
                ClsGlobal.FR_Recv = ConfigOperation.SetOneItem("FR_Recv");

                ClsGlobal.RL_Send = ConfigOperation.SetOneItem("RL_Send");
                ClsGlobal.RL_Recv = ConfigOperation.SetOneItem("RL_Recv");
                ClsGlobal.RR_Send = ConfigOperation.SetOneItem("RR_Send");
                ClsGlobal.RR_Recv = ConfigOperation.SetOneItem("RR_Recv");


                ClsGlobal.CanRecvTimeSpanMillSecs = double.Parse(ConfigOperation.SetOneItem("CanRecvTimeSpanMillSecs"));
                ClsGlobal.XDuration = double.Parse(ConfigOperation.SetOneItem("XDuration"));
                ClsGlobal.FileChangeMinutes = double.Parse(ConfigOperation.SetOneItem("FileChangeMinutes"));
                ClsGlobal.DaqFrequency = double.Parse(ConfigOperation.SetOneItem("DaqFrequency"));
                ClsGlobal.SamplesPerChannel = int.Parse(ConfigOperation.SetOneItem("SamplesPerChannel"));

                ClsGlobal.VppmWorkMode = ConfigOperation.SetOneItem("VppmWorkMode");
                ClsGlobal.DoChannel = ConfigOperation.SetOneItem("DoChannel");
                ClsGlobal.DIChannel = ConfigOperation.SetOneItem("DIChannel");
                ClsGlobal.AOChannel = ConfigOperation.SetOneItem("AOChannel");
                ClsGlobal.StartPosDINo = int.Parse(ConfigOperation.SetOneItem("StartPosDINo"));
                ClsGlobal.EndPosDINo = int.Parse(ConfigOperation.SetOneItem("EndPosDINo"));
                ClsGlobal.ClampAoVol = double.Parse(ConfigOperation.SetOneItem("ClampAoVol"));
                ClsGlobal.ReleaseAoVol = double.Parse(ConfigOperation.SetOneItem("ReleaseAoVol"));

                ClsGlobal.DirectionValveChannel = int.Parse(ConfigOperation.SetOneItem("DirectionValveChannel"));
                ClsGlobal.PowerChannel = int.Parse(ConfigOperation.SetOneItem("PowerChannel"));
                ClsGlobal.AlertChannel = int.Parse(ConfigOperation.SetOneItem("AlertChannel"));

                ClsGlobal.VPPMD1No = int.Parse(ConfigOperation.SetOneItem("VPPMD1No"));
                ClsGlobal.VPPMD2No = int.Parse(ConfigOperation.SetOneItem("VPPMD2No"));
                ClsGlobal.VPPMD3No = int.Parse(ConfigOperation.SetOneItem("VPPMD3No"));

                ClsGlobal.SerialPortRetrys = int.Parse(ConfigOperation.SetOneItem("SerialPortRetrys"));

                ClsGlobal.MedianLens = int.Parse(ConfigOperation.SetOneItem("MedianLens"));


                ClsGlobal.RecvCanLens = int.Parse(ConfigOperation.SetOneItem("RecvCanLens"));


                ClsGlobal.LimitStartPosDINo = int.Parse(ConfigOperation.SetOneItem("LimitStartPosDINo"));
                ClsGlobal.LimitEndPosDINo = int.Parse(ConfigOperation.SetOneItem("LimitEndPosDINo"));
                ClsGlobal.AdjustDoNo = int.Parse(ConfigOperation.SetOneItem("AdjustDoNo"));
                ClsGlobal.ValveMode = int.Parse(ConfigOperation.SetOneItem("ValveMode"));

                ClsGlobal.WaitValveGoBack = int.Parse(ConfigOperation.SetOneItem("WaitValveGoBack"));
                ClsGlobal.WaitValveChangeFinishSpan =
                    int.Parse(ConfigOperation.SetOneItem("WaitValveChangeFinishSpan"));
                ClsGlobal.WaitSpanBeforePush = int.Parse(ConfigOperation.SetOneItem("WaitSpanBeforePush"));
                ClsGlobal.SerialSendIntervalSpan = int.Parse(ConfigOperation.SetOneItem("SerialSendIntervalSpan"));
                ClsGlobal.DevResetWaitSpan = int.Parse(ConfigOperation.SetOneItem("DevResetWaitSpan"));

                ClsGlobal.DaqTimeBias = double.Parse(ConfigOperation.SetOneItem("DaqTimeBias"));

                ClsGlobal.Voltage = int.Parse(ConfigOperation.SetOneItem("Voltage"));
                ClsGlobal.MaxCurrent = int.Parse(ConfigOperation.SetOneItem("MaxCurrent"));
                ClsGlobal.MinCurrent = int.Parse(ConfigOperation.SetOneItem("MinCurrent"));
                ClsGlobal.MaxPower = int.Parse(ConfigOperation.SetOneItem("MaxPower"));
                ClsGlobal.MinPower = int.Parse(ConfigOperation.SetOneItem("MinPower"));


                ClsGlobal.PowerServerAdr[0] = ConfigOperation.SetOneItem("Power1ServerAdr");
                ClsGlobal.PowerServerPort[0] = ConfigOperation.SetOneItem("Power1ServerPort");


                var DbcMsg = DbcParser.ParseDbcFile(Environment.CurrentDirectory + @"\Config\CAN_V4_3_0.dbc",
                    out ClsGlobal.Dbc);

                if (DbcMsg.IndexOf("OK") < 0) MessageBox.Show(DbcMsg);
            }

            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }


        public void OpenChildForm(Form FrmChild)
        {
            var IsOpen = false;

            foreach (var frm in MdiChildren)
            {
                if (frm.Name == FrmChild.Name)
                {
                    frm.Activate();

                    frm.TopMost = true;
                    frm.BringToFront();
                    frm.WindowState = FormWindowState.Maximized;
                    FrmChild.Dispose();
                    IsOpen = true;
                    break;
                }

                frm.TopMost = false;
            }

            if (!IsOpen)
            {
                FrmChild.MdiParent = this;
                FrmChild.WindowState = FormWindowState.Maximized;
                FrmChild.Show();
                FrmChild.TopMost = true;
                FrmChild.BringToFront();
            }
        }

        public void OpenChildFormNormal(Form FrmChild)
        {
            var IsOpen = false;

            foreach (var frm in MdiChildren)
            {
                if (frm.Name == FrmChild.Name)
                {
                    frm.Activate();
                    frm.WindowState = FormWindowState.Normal;
                    frm.TopMost = true;
                    frm.BringToFront();
                    FrmChild.Dispose();
                    IsOpen = true;
                    break;
                }

                frm.TopMost = false;
            }

            if (!IsOpen)
            {
                FrmChild.MdiParent = this;
                FrmChild.WindowState = FormWindowState.Normal;
                FrmChild.Show();
                FrmChild.TopMost = true;
                FrmChild.BringToFront();
            }
        }


        private void TsmSetting_Click(object sender, EventArgs e)
        {
            foreach (var childForm in MdiChildren)
                if (childForm.Text == "实时监视")
                {
                    MessageBox.Show("请关闭实时监视界面!");
                    return;
                }


            var Setting = new FrmTestSetting();
            var ScrHeight = Screen.PrimaryScreen.Bounds.Height;
            var ScrWidth = Screen.PrimaryScreen.Bounds.Width;
            Setting.Height = ScrHeight * 7 / 10;
            Setting.Width = ScrWidth * 7 / 10;

            var x = (ScrWidth - Setting.Width) / 2;
            var y = (ScrHeight - Setting.Height) / 2;
            Setting.Location = new Point(x, y);


            Setting.ShowDialog(this);
        }


        private void TsmCanCommControl_Click(object sender, EventArgs e)
        {
        }

        private void TsmDAQ_Click(object sender, EventArgs e)
        {
        }

        private void TsmPlayBack_Click(object sender, EventArgs e)
        {
        }

        private void TsmDAQCalibrate_Click(object sender, EventArgs e)
        {
            foreach (var childForm in MdiChildren)
                if (childForm.Text == "实时监视")
                {
                    MessageBox.Show("请关闭实时监视界面!");
                    return;
                }


            var frmDAQCalibrate = new FrmDAQCalibrate();
            frmDAQCalibrate.Name = "数采卡校准";
            OpenChildForm(frmDAQCalibrate);
        }


        private void ConfigureMenuStrip()
        {
            // 创建自定义颜色表
            var colorTable = new CustomMenuColors();

            // 设置自定义渲染器
            menuStripMain.Renderer = new ToolStripProfessionalRenderer(colorTable);
        }

        private void TsmCharacterPlayBack_Click(object sender, EventArgs e)
        {
            foreach (var childForm in MdiChildren)
                if (childForm.Text == "实时监视")
                {
                    MessageBox.Show("数据采集中，无法回放!");
                    return;
                }


            var frmPlayBack = new FrmPlayBack();
            frmPlayBack.Name = "数据回放";
            OpenChildForm(frmPlayBack);
        }

        private void TsmRawPlayBack_Click(object sender, EventArgs e)
        {
            foreach (var childForm in MdiChildren)
                if (childForm.Text == "实时监视")
                {
                    MessageBox.Show("数据采集中，无法回放!");
                    return;
                }


            var frmPlayBack = new FrmRawPlayBack();
            frmPlayBack.Name = "原始数据回放";
            OpenChildForm(frmPlayBack);
        }

        private void TsmRealMinitor_Click(object sender, EventArgs e)
        {
            foreach (var childForm in MdiChildren)
            {
                if (childForm.Text == "数采卡校准")
                {
                    MessageBox.Show("请关闭数采卡校准界面!");
                    return;
                }

                if (childForm.Text == "扭矩调节")
                {
                    MessageBox.Show("请关闭扭矩调节界面!");
                    return;
                }
            }


            //FrmMainMonitor frmRealMonitor = new FrmMainMonitor();
            var frmRealMonitor = new FrmEpbMainMonitor();
            frmRealMonitor.Name = "实时监视";
            OpenChildForm(frmRealMonitor);
        }

        /*
        private void TsmAdjustTorque_Click(object sender, EventArgs e)
        {
            foreach (Form childForm in this.MdiChildren)
            {
                if (childForm.Text == "数采卡校准")
                {
                    MessageBox.Show("请关闭数采卡校准界面!");
                    return;
                }
                if (childForm.Text == "实时监视")
                {
                    MessageBox.Show("请关闭实时监视界面!");
                    return;
                }
            }



            FrmAdjustTorque frmRealMonitor = new FrmAdjustTorque();
            frmRealMonitor.Name = "扭矩调节";
            OpenChildForm(frmRealMonitor);
        }
        */

        private async void TsmPower_Click(object sender, EventArgs e)
        {
            if (TsmPower.Text == "1-OFF")
            {
                if (!IsPowerConnect[0])
                {
                    var ConnectMsg = ConnectToPowerServer(1);
                    if (ConnectMsg.IndexOf("OK") < 0)
                    {
                        MessageBox.Show(ConnectMsg);
                        return;
                    }
                }

                await Task.Delay(1000);

                var initMsg = InitPower(1, ClsGlobal.Voltage,
                    ClsGlobal.MaxCurrent, ClsGlobal.MinCurrent,
                    ClsGlobal.MaxPower, ClsGlobal.MinPower);

                if (initMsg.IndexOf("OK") < 0)
                {
                    MessageBox.Show(initMsg);
                    return;
                }

                await Task.Delay(1000);


                var powerMsg = PowerOpen(1);
                if (powerMsg.IndexOf("OK") < 0)
                {
                    MessageBox.Show("打开电源1失败：" + powerMsg);
                    ClsGlobal.PowerStatus[0] = 0;
                    return;
                }

                await Task.Delay(1000);
                ClsGlobal.PowerStatus[0] = 1;


                var ReadMsg = InitSerialPort();
                if (ReadMsg.IndexOf("OK") < 0)
                {
                    MessageBox.Show(ReadMsg);
                    return;
                }


                var OpenSuccess = await OpenPowerChannel(0, ClsGlobal.SerialPortRetrys);
                if (!OpenSuccess)
                {
                    MessageBox.Show("打开EMB1电源继电器失败！");
                    ClsGlobal.PowerStatus[0] = 1;

                    return;
                }

                ClsGlobal.PowerStatus[0] = 2;

                UpdateMenuItem(TsmPower, Resources.P4, "1-ON ");
            }
            else
            {
                var ReadMsg = InitSerialPort();
                if (ReadMsg.IndexOf("OK") < 0)
                {
                    MessageBox.Show(ReadMsg);
                    return;
                }


                var OpenSuccess = await ClosePowerChannel(0, ClsGlobal.SerialPortRetrys);
                if (!OpenSuccess)
                {
                    MessageBox.Show("关闭EMB1电源继电器失败！");
                    ClsGlobal.PowerStatus[0] = 2;
                    return;
                }

                ClsGlobal.PowerStatus[0] = 1;

                var powerMsg = PowerClose(1);
                if (powerMsg.IndexOf("OK") < 0)
                {
                    MessageBox.Show("关闭电源1失败：" + powerMsg);
                    ClsGlobal.PowerStatus[0] = 1;
                    return;
                }

                await Task.Delay(1000);
                ClsGlobal.PowerStatus[0] = 0;
                UpdateMenuItem(TsmPower, Resources.P5, "1-OFF");
            }
        }


        private string InitSerialPort()
        {
            try
            {
                serialPort?.Dispose();

                serialPort = new SerialPort();
                serialPort.PortName = ClsGlobal.SerialPort; // 串口号，根据实际情况修改
                serialPort.BaudRate = ClsGlobal.Baud; // 波特率
                serialPort.DataBits = ClsGlobal.DataBits; // 数据位
                serialPort.Parity = Parity.None; // 奇偶校验
                serialPort.StopBits = StopBits.One; // 停止位

                // 订阅数据接收事件
                serialPort.DataReceived += SerialPort_DataReceived;

                // 打开串口
                serialPort.Open();

                return "OK";
            }
            catch (Exception ex)
            {
                //  MessageBox.Show($"打开COM口失败: {ex.Message}");
                //   ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "打开COM口失败: " + ex.Message, "打开COM口");
                serialPort?.Close();
                serialPort?.Dispose();
                return ex.Message;
            }
        }

        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                var sp = (SerialPort)sender;
                var bytesToRead = sp.BytesToRead;
                var buffer = new byte[bytesToRead];
                sp.Read(buffer, 0, bytesToRead);

                BeginInvoke(new Action(() => { serialResponseTcs?.TrySetResult(buffer); }));
            }

            catch (Exception ex)
            {
                MessageBox.Show("COM口接收数据出错: " + ex.Message);
            }
        }

        private void SafeDisposeSerialPort()
        {
            lock (_serialPortLock)
            {
                if (serialPort?.IsOpen == true)
                {
                    serialPort.DataReceived -= SerialPort_DataReceived;
                    serialPort.Close();
                }

                serialPort?.Dispose();
            }
        }

        private async Task<bool> OpenPowerChannel(byte ChannelNo, int maxRetries)
        {
            lock (_serialPortLock)
            {
                if (!serialPort.IsOpen)
                {
                    var OpenMsg = InitSerialPort();
                    if (OpenMsg.IndexOf("OK") < 0)
                    {
                        MessageBox.Show("打开COM口失败: " + OpenMsg);
                        return false; // 返回失败结果
                    }
                }
            }

            try
            {
                var Channel = new byte[2];
                Channel[0] = 0;
                Channel[1] = ChannelNo;
                byte[] Status = { 0xff, 0 };
                var command = ClsSerialCommandMaker.GenerateDoCommand(0x01, 0x05, Channel, Status);
                var retryCount = 0;
                var operationSuccess = false;
                while (retryCount < maxRetries && !operationSuccess)
                {
                    serialPort.Write(command, 0, command.Length);


                    var currentTcs = new TaskCompletionSource<byte[]>();
                    serialResponseTcs = currentTcs;

                    var responseTask = currentTcs.Task;
                    var delayTask = Task.Delay(3000);
                    var completedTask = await Task.WhenAny(responseTask, delayTask);

                    if (completedTask == delayTask)
                    {
                        retryCount++;
                        continue;
                    }

                    var response = await responseTask;

                    if (response[0] == command[0] && response[1] == command[1] + 0x80)
                        retryCount++;
                    else
                        operationSuccess = true;
                }

                if (!operationSuccess)
                {
                    var errorMsg = $"已达最大重试次数（{maxRetries}次），操作失败";

                    MessageBox.Show("打开COM口失败: " + errorMsg);
                    SafeDisposeSerialPort();
                    return false; // 返回失败结果
                }

                return true; // 操作成功
            }
            catch (Exception ex)
            {
                MessageBox.Show("COM口通信错误: " + ex.Message);
                SafeDisposeSerialPort();
                return false; // 返回失败结果
            }
            finally
            {
                SafeDisposeSerialPort();
            }
        }

        private async Task<bool> ClosePowerChannel(byte ChannelNo, int maxRetries)
        {
            lock (_serialPortLock)
            {
                if (!serialPort.IsOpen)
                {
                    var OpenMsg = InitSerialPort();
                    if (OpenMsg.IndexOf("OK") < 0)
                    {
                        MessageBox.Show("打开COM口失败: " + OpenMsg);
                        return false; // 返回失败结果
                    }
                }
            }

            try
            {
                var Channel = new byte[2];
                Channel[0] = 0;
                Channel[1] = ChannelNo;
                byte[] Status = { 0, 0 };
                var command = ClsSerialCommandMaker.GenerateDoCommand(0x01, 0x05, Channel, Status);

                var retryCount = 0;
                var operationSuccess = false;

                while (retryCount < maxRetries && !operationSuccess)
                {
                    serialPort.Write(command, 0, command.Length);

                    var currentTcs = new TaskCompletionSource<byte[]>();
                    serialResponseTcs = currentTcs;

                    var responseTask = currentTcs.Task;
                    var delayTask = Task.Delay(3000);
                    var completedTask = await Task.WhenAny(responseTask, delayTask);

                    if (completedTask == delayTask)
                    {
                        retryCount++;
                        continue;
                    }

                    var response = await responseTask;

                    if (response[0] == command[0] && response[1] == command[1] + 0x80)
                        retryCount++;
                    else
                        operationSuccess = true;
                }

                if (!operationSuccess)
                {
                    var errorMsg = $"已达最大重试次数（{maxRetries}次），操作失败";
                    MessageBox.Show(errorMsg);
                    SafeDisposeSerialPort();
                    return false; // 返回失败结果
                }

                return true; // 操作成功
            }
            catch (Exception ex)
            {
                MessageBox.Show("COM口通信错误：" + ex.Message);
                SafeDisposeSerialPort();
                return false; // 返回失败结果
            }
            finally
            {
                SafeDisposeSerialPort();
            }
        }


        private void UpdateMenuItem(ToolStripMenuItem item, Image newImage, string newText)
        {
            // 确保在主线程操作（如果跨线程需Invoke）
            if (item.GetCurrentParent().InvokeRequired)
            {
                item.GetCurrentParent().Invoke(new Action(() =>
                    UpdateMenuItem(item, newImage, newText)));
                return;
            }

            item.Image = newImage; // 更新图片
            item.Text = newText; // 更新文本
        }


        public string ConnectToPowerServer(int powerIndex)
        {
            try
            {
                powerClient[powerIndex - 1] = new AsyncTcpClient(powerIndex, "PowerClient" + powerIndex, 4096);

                var ConnMsg = powerClient[powerIndex - 1].ConnectToServer(ClsGlobal.PowerServerAdr[powerIndex - 1],
                    int.Parse(ClsGlobal.PowerServerPort[powerIndex - 1]));

                if (ConnMsg.IndexOf("OK") < 0)
                {
                    IsPowerConnect[powerIndex - 1] = false;
                    return ConnMsg;
                }

                powerClient[powerIndex - 1].DataReceived += delegate(object sender1, RecvEventArg e1)
                {
                    //创建Cors连接的函数组
                    powerDataReceived(sender1, e1, powerIndex);
                };

                IsPowerConnect[powerIndex - 1] = true;
                return "OK";
            }
            catch (Exception ex)
            {
                IsPowerConnect[powerIndex - 1] = false;
                return ex.Message;
            }
        }

        private void powerDataReceived(object sender, RecvEventArg e, int CorsIndex)
        {
            var whichSock = (AsyncTcpClient)sender;
        }


        public string InitPower(int index, int voltage, int maxCurrent, int minCurrent, int maxPower, int minPower)
        {
            try
            {
                var command1 = "SYST: REM" + Environment.NewLine;
                var byteSend1 = Encoding.Default.GetBytes(command1);
                powerClient[index - 1].SendBinaryToServer("PowerClient" + index, byteSend1);
                Thread.Sleep(100);


                var command2 = "FUNC VOLT" + Environment.NewLine;
                var byteSend2 = Encoding.Default.GetBytes(command2);
                powerClient[index - 1].SendBinaryToServer("PowerClient" + index, byteSend2);
                Thread.Sleep(100);


                var command3 = "VOLT TT" + Environment.NewLine;
                var byteSend3 = Encoding.Default.GetBytes(command3.Replace("TT", voltage.ToString()));
                powerClient[index - 1].SendBinaryToServer("PowerClient" + index, byteSend3);
                Thread.Sleep(100);


                var command4 = "VOLT:SLEW:POS 0.1" + Environment.NewLine;
                var byteSend4 = Encoding.Default.GetBytes(command4);
                powerClient[index - 1].SendBinaryToServer("PowerClient" + index, byteSend4);
                Thread.Sleep(100);


                var command5 = "VOLT:SLEW:NEG 0.1" + Environment.NewLine;
                var byteSend5 = Encoding.Default.GetBytes(command5);
                powerClient[index - 1].SendBinaryToServer("PowerClient" + index, byteSend5);
                Thread.Sleep(100);


                var command6 = "CURR:LIM TTA" + Environment.NewLine;
                var byteSend6 = Encoding.Default.GetBytes(command6.Replace("TT", maxCurrent.ToString()));
                powerClient[index - 1].SendBinaryToServer("PowerClient" + index, byteSend6);
                Thread.Sleep(100);


                var command7 = "CURR: LIM: NEG TTA" + Environment.NewLine;
                var byteSend7 = Encoding.Default.GetBytes(command7.Replace("TT", minCurrent.ToString()));
                powerClient[index - 1].SendBinaryToServer("PowerClient" + index, byteSend7);
                Thread.Sleep(100);

                var command8 = "POW: LIM TTW" + Environment.NewLine;
                var byteSend8 = Encoding.Default.GetBytes(command8.Replace("TT", maxPower.ToString()));
                powerClient[index - 1].SendBinaryToServer("PowerClient" + index, byteSend8);
                Thread.Sleep(100);


                var command9 = "POW:LIM:NEG TTW" + Environment.NewLine;
                var byteSend9 = Encoding.Default.GetBytes(command9.Replace("TT", minPower.ToString()));
                powerClient[index - 1].SendBinaryToServer("PowerClient" + index, byteSend9);
                return "OK";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }


        public string PowerOpen(int index)
        {
            try
            {
                var command1 = "OUTP 1" + Environment.NewLine;
                var byteSend1 = Encoding.Default.GetBytes(command1);
                powerClient[index - 1].SendBinaryToServer("PowerClient" + index, byteSend1);
                return "OK";
            }

            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        public string PowerClose(int index)
        {
            try
            {
                var command1 = "OUTP 0" + Environment.NewLine;
                var byteSend1 = Encoding.Default.GetBytes(command1);
                powerClient[index - 1].SendBinaryToServer("PowerClient" + index, byteSend1);
                return "OK";
            }

            catch (Exception ex)
            {
                return ex.Message;
            }
        }


        // 自定义颜色表
        public class CustomMenuColors : ProfessionalColorTable
        {
            // 主菜单条背景色（渐变色开始）
            // public override Color MenuStripGradientBegin => Color.LightBlue;

            public override Color MenuStripGradientBegin => Color.FromArgb(243, 249, 255);


            // 主菜单条背景色（渐变色结束）
            public override Color MenuStripGradientEnd => Color.LightBlue;

            // 下拉菜单项背景色
            //  public override Color ToolStripDropDownBackground => Color.White;

            public override Color ToolStripDropDownBackground => Color.FromArgb(243, 249, 255);

            // 菜单项选中时的背景色
            public override Color MenuItemSelected => Color.CornflowerBlue;

            // 菜单项按下时的背景色
            public override Color MenuItemPressedGradientBegin => Color.SteelBlue;
        }

        #region Serial变量

        private TaskCompletionSource<byte[]> serialResponseTcs;
        private static SerialPort serialPort;
        private readonly object _serialPortLock = new();

        #endregion
    }
}