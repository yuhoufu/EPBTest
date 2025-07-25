using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Diagnostics;
using System.IO;
using NationalInstruments.DAQmx;
using MTEmbTest;
using DataOperation;
using MTEmbTest.Properties;
using CustomTcpClient;
using System.IO.Ports;
using System.Threading.Tasks;

namespace MtEmbTest
{
    public partial class Main_Frm : Form
    {
        #region  Serial变量
        private TaskCompletionSource<byte[]> serialResponseTcs;
        private static SerialPort serialPort;
        private readonly object _serialPortLock = new object();
        #endregion

        public bool[] IsPowerConnect = { false};
        private AsyncTcpClient[] powerClient = new AsyncTcpClient[1];

        public Main_Frm()
        {
            InitializeComponent();
            ConfigureMenuStrip();
        }

       
       
      
       
       


       


        /// <summary>
        /// 水平平铺所有子窗口
        /// </summary>
        /// <param name="sender">界面菜单输入</param>
        ///  <param name="e">输入事件</param>
        /// <returns>void</returns>
        private void TsmHorizon_Click(object sender, EventArgs e)
        {
            LayoutMdi(MdiLayout.TileHorizontal);
            TsmHorizon.Checked = true;
            TsmLayout.Checked = false;
            TsmVertical.Checked = false;
        }
        /// <summary>
        /// 垂直平铺所有子窗口
        /// </summary>
        /// <param name="sender">界面菜单输入</param>
        ///  <param name="e">输入事件</param>
        /// <returns>void</returns>
        private void TsmVertical_Click(object sender, EventArgs e)
        {
            LayoutMdi(MdiLayout.TileVertical);
            TsmHorizon.Checked = false;
            TsmLayout.Checked = false;
            TsmVertical.Checked = true;
        }
        /// <summary>
        /// 层叠所有子窗口
        /// </summary>
        /// <param name="sender">界面菜单输入</param>
        ///  <param name="e">输入事件</param>
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
            if (this.MdiChildren.Length > 0)
            {
                MessageBox.Show("可能存在正在运行的试验，请先停止试验，关闭子窗口，再退出程序！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                e.Cancel = true;
            }


           
                if (ClsGlobal.PowerStatus[0] > 0)
                {
                    MessageBox.Show("请关闭电源" , "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    e.Cancel = true;
                }
            



        }








        private int GetTestNo()
        {
            DateTime LastTime = DateTime.Parse(ConfigOperation.SetOneItem("TestDate").ToString());
            int LastNo = int.Parse(ConfigOperation.SetOneItem("TestNo").ToString());

            if (DateTime.Now.DayOfYear > LastTime.DayOfYear)  //新的一天返回1
            {
                return 0;
            }
            else
            {
                return LastNo;
            }

        }

        private void Main_Frm_Load(object sender, EventArgs e)
        {
            try
            {

                ClsGlobal.ClampCount = int.Parse(ConfigOperation.SetOneItem("ClampCount").ToString());
                ClsGlobal.ReleaseCount = int.Parse(ConfigOperation.SetOneItem("ReleaseCount").ToString());
                ClsGlobal.PushCount = int.Parse(ConfigOperation.SetOneItem("PushCount").ToString());
                ClsGlobal.ClampSpan = int.Parse(ConfigOperation.SetOneItem("ClampSpan").ToString());
                ClsGlobal.ReleaseSpan = int.Parse(ConfigOperation.SetOneItem("ReleaseSpan").ToString());
                ClsGlobal.PushSpan = int.Parse(ConfigOperation.SetOneItem("PushSpan").ToString());
                ClsGlobal.ReleaseWaitSpan = int.Parse(ConfigOperation.SetOneItem("ReleaseWaitSpan").ToString());
                ClsGlobal.IsPushFirst = int.Parse(ConfigOperation.SetOneItem("IsPushFirst").ToString());
                ClsGlobal.IsLiner = int.Parse(ConfigOperation.SetOneItem("IsLiner").ToString());


                ClsGlobal.DRate = int.Parse(ConfigOperation.SetOneItem("DRate").ToString());
                ClsGlobal.ARate = int.Parse(ConfigOperation.SetOneItem("ARate").ToString());
                ClsGlobal.CardNo = int.Parse(ConfigOperation.SetOneItem("CardNo").ToString());
           
                ClsGlobal.MsgInterval = int.Parse(ConfigOperation.SetOneItem("MsgInterval").ToString());
                ClsGlobal.ResistorEnabel = int.Parse(ConfigOperation.SetOneItem("ResistorEnabel").ToString());
                ClsGlobal.Protocol = int.Parse(ConfigOperation.SetOneItem("Protocol").ToString());
                ClsGlobal.FrameType = int.Parse(ConfigOperation.SetOneItem("FrameType").ToString());

                ClsGlobal.FrameSendType = int.Parse(ConfigOperation.SetOneItem("FrameSendType").ToString());
                ClsGlobal.FrameExpType = int.Parse(ConfigOperation.SetOneItem("FrameExpType").ToString());
                ClsGlobal.FrameTimerNo = int.Parse(ConfigOperation.SetOneItem("FrameTimerNo").ToString());


                ClsGlobal.SendForceScale = short.Parse(ConfigOperation.SetOneItem("SendForceScale").ToString());
                ClsGlobal.RecvForceScale = double.Parse(ConfigOperation.SetOneItem("RecvForceScale").ToString());
                ClsGlobal.RecvForceJudgeDelt = double.Parse(ConfigOperation.SetOneItem("RecvForceJudgeDelt").ToString());
                ClsGlobal.RecvMsgInterval = double.Parse(ConfigOperation.SetOneItem("RecvMsgInterval").ToString());


                ClsGlobal.ClampPosition = short.Parse(ConfigOperation.SetOneItem("ClampPosition").ToString());
                ClsGlobal.ClampSpeed = short.Parse(ConfigOperation.SetOneItem("ClampSpeed").ToString());
                ClsGlobal.ClampModReq = byte.Parse(ConfigOperation.SetOneItem("ClampModReq").ToString());
                ClsGlobal.ClampTorque = short.Parse(ConfigOperation.SetOneItem("ClampTorque").ToString());
                ClsGlobal.ClampNormalMode = byte.Parse(ConfigOperation.SetOneItem("ClampNormalMode").ToString());
                ClsGlobal.ClampForce = short.Parse(ConfigOperation.SetOneItem("ClampForce").ToString());
                ClsGlobal.ClampEnable = byte.Parse(ConfigOperation.SetOneItem("ClampEnable").ToString());
                ClsGlobal.ClampForceReq = ushort.Parse(ConfigOperation.SetOneItem("ClampForceReq").ToString());
                ClsGlobal.ReleasePosition = short.Parse(ConfigOperation.SetOneItem("ReleasePosition").ToString());
                ClsGlobal.ReleaseSpeed = short.Parse(ConfigOperation.SetOneItem("ReleaseSpeed").ToString());
                ClsGlobal.ReleaseModeReq = byte.Parse(ConfigOperation.SetOneItem("ReleaseModeReq").ToString());
                ClsGlobal.ReleaseTorque = short.Parse(ConfigOperation.SetOneItem("ReleaseTorque").ToString());
                ClsGlobal.ReleaseNormalMode = byte.Parse(ConfigOperation.SetOneItem("ReleaseNormalMode").ToString());
                ClsGlobal.ReleaseForce = short.Parse(ConfigOperation.SetOneItem("ReleaseForce").ToString());
                ClsGlobal.ReleaseEnable = byte.Parse(ConfigOperation.SetOneItem("ReleaseEnable").ToString());
                ClsGlobal.ReleaseForceReq = ushort.Parse(ConfigOperation.SetOneItem("ReleaseForceReq").ToString());

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

                ClsGlobal.VppmWorkMode= ConfigOperation.SetOneItem("VppmWorkMode");
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
                ClsGlobal.WaitValveChangeFinishSpan =   int.Parse(ConfigOperation.SetOneItem("WaitValveChangeFinishSpan"));
                ClsGlobal.WaitSpanBeforePush =   int.Parse(ConfigOperation.SetOneItem("WaitSpanBeforePush"));
                ClsGlobal.SerialSendIntervalSpan = int.Parse(ConfigOperation.SetOneItem("SerialSendIntervalSpan"));
                ClsGlobal.DevResetWaitSpan =   int.Parse(ConfigOperation.SetOneItem("DevResetWaitSpan"));

                ClsGlobal.DaqTimeBias = double.Parse(ConfigOperation.SetOneItem("DaqTimeBias"));

                ClsGlobal.Voltage = int.Parse(ConfigOperation.SetOneItem("Voltage"));
                ClsGlobal.MaxCurrent = int.Parse(ConfigOperation.SetOneItem("MaxCurrent"));
                ClsGlobal.MinCurrent = int.Parse(ConfigOperation.SetOneItem("MinCurrent"));
                ClsGlobal.MaxPower = int.Parse(ConfigOperation.SetOneItem("MaxPower"));
                ClsGlobal.MinPower = int.Parse(ConfigOperation.SetOneItem("MinPower"));


                ClsGlobal.PowerServerAdr[0] = ConfigOperation.SetOneItem("Power1ServerAdr");
                ClsGlobal.PowerServerPort[0] = ConfigOperation.SetOneItem("Power1ServerPort");



                string DbcMsg = DbcParser.ParseDbcFile(System.Environment.CurrentDirectory + @"\Config\CAN_V4_3_0.dbc",out ClsGlobal.Dbc);

                if(DbcMsg.IndexOf("OK")<0)
                {
                    MessageBox.Show(DbcMsg);
                }



            }

            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }

        }


        public void OpenChildForm(Form FrmChild)
        {
            bool IsOpen = false;

            foreach (Form frm in this.MdiChildren)
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
                else
                {
                    frm.TopMost = false;
                }
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
            bool IsOpen = false;

            foreach (Form frm in this.MdiChildren)
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
                else
                {
                    frm.TopMost = false;
                }
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

            foreach (Form childForm in this.MdiChildren)
            {
                if (childForm.Text == "实时监视")
                {
                    MessageBox.Show("请关闭实时监视界面!");
                    return;
                }
            }



            FrmTestSetting Setting = new FrmTestSetting();
            int ScrHeight = Screen.PrimaryScreen.Bounds.Height;
            int ScrWidth = Screen.PrimaryScreen.Bounds.Width;
            Setting.Height = ScrHeight * 7 / 10;
            Setting.Width = ScrWidth * 7 / 10;

            int x = (ScrWidth - Setting.Width) / 2;
            int y = (ScrHeight - Setting.Height) / 2;
            Setting.Location = new System.Drawing.Point(x, y);


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

            foreach (Form childForm in this.MdiChildren)
            {
                if (childForm.Text == "实时监视")
                {
                    MessageBox.Show("请关闭实时监视界面!");
                    return;
                }
            }


            FrmDAQCalibrate frmDAQCalibrate = new FrmDAQCalibrate();
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

        private void TsmCharacterPlayBack_Click(object sender, EventArgs e)
        {
            foreach (Form childForm in this.MdiChildren)
            {
                if (childForm.Text == "实时监视")
                {
                    MessageBox.Show("数据采集中，无法回放!");
                    return;
                }
            }


            FrmPlayBack frmPlayBack = new FrmPlayBack();
            frmPlayBack.Name = "数据回放";
            OpenChildForm(frmPlayBack);
        }

        private void TsmRawPlayBack_Click(object sender, EventArgs e)
        {
            foreach (Form childForm in this.MdiChildren)
            {
                if (childForm.Text == "实时监视")
                {
                    MessageBox.Show("数据采集中，无法回放!");
                    return;
                }
            }


            FrmRawPlayBack frmPlayBack = new FrmRawPlayBack();
            frmPlayBack.Name = "原始数据回放";
            OpenChildForm(frmPlayBack);
        }

        private void TsmRealMinitor_Click(object sender, EventArgs e)
        {
            foreach (Form childForm in this.MdiChildren)
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



            FrmMainMonitor frmRealMonitor = new FrmMainMonitor();
            frmRealMonitor.Name = "实时监视";
            OpenChildForm(frmRealMonitor);
        }

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

        private async void TsmPower_Click(object sender, EventArgs e)
        {
            if (TsmPower.Text == "1-OFF")
            {
                if (!IsPowerConnect[0])
                {
                    string ConnectMsg = ConnectToPowerServer(1);
                    if (ConnectMsg.IndexOf("OK") < 0)
                    {
                        MessageBox.Show(ConnectMsg);
                        return;
                    }

                }

                await System.Threading.Tasks.Task.Delay(1000);

                string initMsg = InitPower(1, ClsGlobal.Voltage,
                    ClsGlobal.MaxCurrent, ClsGlobal.MinCurrent,
                    ClsGlobal.MaxPower, ClsGlobal.MinPower);

                if (initMsg.IndexOf("OK") < 0)
                {
                    MessageBox.Show(initMsg);
                    return;
                }

                await System.Threading.Tasks.Task.Delay(1000);


                string powerMsg = PowerOpen(1);
                if (powerMsg.IndexOf("OK") < 0)
                {
                    MessageBox.Show("打开电源1失败：" + powerMsg);
                    ClsGlobal.PowerStatus[0] = 0;
                    return;
                }

                await System.Threading.Tasks.Task.Delay(1000);
                ClsGlobal.PowerStatus[0] = 1;
              

                string ReadMsg = InitSerialPort();
                if (ReadMsg.IndexOf("OK") < 0)
                {
                    MessageBox.Show(ReadMsg);
                    return;
                }



                bool OpenSuccess = await OpenPowerChannel(0, ClsGlobal.SerialPortRetrys);
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

                string ReadMsg = InitSerialPort();
                if (ReadMsg.IndexOf("OK") < 0)
                {
                    MessageBox.Show(ReadMsg);
                    return;
                }



                bool OpenSuccess = await ClosePowerChannel(0, ClsGlobal.SerialPortRetrys);
                if (!OpenSuccess)
                {

                    MessageBox.Show("关闭EMB1电源继电器失败！");
                    ClsGlobal.PowerStatus[0] = 2;
                    return;
                }

                ClsGlobal.PowerStatus[0] = 1;

                string powerMsg = PowerClose(1);
                if (powerMsg.IndexOf("OK") < 0)
                {
                    MessageBox.Show("关闭电源1失败：" + powerMsg);
                    ClsGlobal.PowerStatus[0] = 1;
                    return;
                }

                await System.Threading.Tasks.Task.Delay(1000);
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
                serialPort.DataReceived += new SerialDataReceivedEventHandler(SerialPort_DataReceived);

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
                SerialPort sp = (SerialPort)sender;
                int bytesToRead = sp.BytesToRead;
                byte[] buffer = new byte[bytesToRead];
                sp.Read(buffer, 0, bytesToRead);

                this.BeginInvoke(new Action(() =>
                {
                    serialResponseTcs?.TrySetResult(buffer);
                }));
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
                    string OpenMsg = InitSerialPort();
                    if (OpenMsg.IndexOf("OK") < 0)
                    {
                        MessageBox.Show("打开COM口失败: " + OpenMsg);
                        return false; // 返回失败结果
                    }
                }
            }

            try
            {
                byte[] Channel = new byte[2];
                Channel[0] = 0;
                Channel[1] = ChannelNo;
                byte[] Status = { 0xff, 0 };
                byte[] command = ClsSerialCommandMaker.GenerateDoCommand(0x01, 0x05, Channel, Status);
                int retryCount = 0;
                bool operationSuccess = false;
                while (retryCount < maxRetries && !operationSuccess)
                {
                    serialPort.Write(command, 0, command.Length);


                    var currentTcs = new TaskCompletionSource<byte[]>();
                    serialResponseTcs = currentTcs;

                    var responseTask = currentTcs.Task;
                    var delayTask = System.Threading.Tasks.Task.Delay(3000);
                    var completedTask = await System.Threading.Tasks.Task.WhenAny(responseTask, delayTask);

                    if (completedTask == delayTask)
                    {

                        retryCount++;
                        continue;
                    }

                    byte[] response = await responseTask;

                    if (response[0] == command[0] && response[1] == command[1] + 0x80)
                    {

                        retryCount++;
                    }
                    else
                    {

                        operationSuccess = true;
                    }
                }

                if (!operationSuccess)
                {
                    string errorMsg = $"已达最大重试次数（{maxRetries}次），操作失败";

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
                    string OpenMsg = InitSerialPort();
                    if (OpenMsg.IndexOf("OK") < 0)
                    {
                        MessageBox.Show("打开COM口失败: " + OpenMsg);
                        return false; // 返回失败结果
                    }
                }
            }

            try
            {
                byte[] Channel = new byte[2];
                Channel[0] = 0;
                Channel[1] = ChannelNo;
                byte[] Status = { 0, 0 };
                byte[] command = ClsSerialCommandMaker.GenerateDoCommand(0x01, 0x05, Channel, Status);

                int retryCount = 0;
                bool operationSuccess = false;

                while (retryCount < maxRetries && !operationSuccess)
                {
                    serialPort.Write(command, 0, command.Length);

                    var currentTcs = new TaskCompletionSource<byte[]>();
                    serialResponseTcs = currentTcs;

                    var responseTask = currentTcs.Task;
                    var delayTask = System.Threading.Tasks.Task.Delay(3000);
                    var completedTask = await System.Threading.Tasks.Task.WhenAny(responseTask, delayTask);

                    if (completedTask == delayTask)
                    {
                        retryCount++;
                        continue;
                    }

                    byte[] response = await responseTask;

                    if (response[0] == command[0] && response[1] == command[1] + 0x80)
                    {

                        retryCount++;
                    }
                    else
                    {
                        operationSuccess = true;
                    }
                }

                if (!operationSuccess)
                {
                    string errorMsg = $"已达最大重试次数（{maxRetries}次），操作失败";
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



        void UpdateMenuItem(ToolStripMenuItem item, Image newImage, string newText)
        {
            // 确保在主线程操作（如果跨线程需Invoke）
            if (item.GetCurrentParent().InvokeRequired)
            {
                item.GetCurrentParent().Invoke(new Action(() =>
                    UpdateMenuItem(item, newImage, newText)));
                return;
            }

            item.Image = newImage;  // 更新图片
            item.Text = newText;    // 更新文本
        }






        public string ConnectToPowerServer(int powerIndex)
        {

            try
            {
                powerClient[powerIndex - 1] = new AsyncTcpClient(powerIndex, "PowerClient" + powerIndex.ToString(), 4096);

                string ConnMsg = powerClient[powerIndex - 1].ConnectToServer(ClsGlobal.PowerServerAdr[powerIndex - 1], int.Parse(ClsGlobal.PowerServerPort[powerIndex - 1]));

                if (ConnMsg.IndexOf("OK") < 0)
                {
                    IsPowerConnect[powerIndex - 1] = false;
                    return ConnMsg;
                }
                else
                {


                    powerClient[powerIndex - 1].DataReceived += delegate (object sender1, RecvEventArg e1)
                    {
                        //创建Cors连接的函数组
                        powerDataReceived(sender1, e1, powerIndex);
                    };

                    IsPowerConnect[powerIndex - 1] = true;
                    return "OK";
                }

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

                string command1 = "SYST: REM" + System.Environment.NewLine;
                byte[] byteSend1 = System.Text.Encoding.Default.GetBytes(command1);
                powerClient[index - 1].SendBinaryToServer("PowerClient" + index.ToString(), byteSend1);
                System.Threading.Thread.Sleep(100);


                string command2 = "FUNC VOLT" + System.Environment.NewLine;
                byte[] byteSend2 = System.Text.Encoding.Default.GetBytes(command2);
                powerClient[index - 1].SendBinaryToServer("PowerClient" + index.ToString(), byteSend2);
                System.Threading.Thread.Sleep(100);


                string command3 = "VOLT TT" + System.Environment.NewLine;
                byte[] byteSend3 = System.Text.Encoding.Default.GetBytes(command3.Replace("TT", voltage.ToString()));
                powerClient[index - 1].SendBinaryToServer("PowerClient" + index.ToString(), byteSend3);
                System.Threading.Thread.Sleep(100);


                string command4 = "VOLT:SLEW:POS 0.1" + System.Environment.NewLine;
                byte[] byteSend4 = System.Text.Encoding.Default.GetBytes(command4);
                powerClient[index - 1].SendBinaryToServer("PowerClient" + index.ToString(), byteSend4);
                System.Threading.Thread.Sleep(100);


                string command5 = "VOLT:SLEW:NEG 0.1" + System.Environment.NewLine;
                byte[] byteSend5 = System.Text.Encoding.Default.GetBytes(command5);
                powerClient[index - 1].SendBinaryToServer("PowerClient" + index.ToString(), byteSend5);
                System.Threading.Thread.Sleep(100);


                string command6 = "CURR:LIM TTA" + System.Environment.NewLine;
                byte[] byteSend6 = System.Text.Encoding.Default.GetBytes(command6.Replace("TT", maxCurrent.ToString()));
                powerClient[index - 1].SendBinaryToServer("PowerClient" + index.ToString(), byteSend6);
                System.Threading.Thread.Sleep(100);


                string command7 = "CURR: LIM: NEG TTA" + System.Environment.NewLine;
                byte[] byteSend7 = System.Text.Encoding.Default.GetBytes(command7.Replace("TT", minCurrent.ToString()));
                powerClient[index - 1].SendBinaryToServer("PowerClient" + index.ToString(), byteSend7);
                System.Threading.Thread.Sleep(100);

                string command8 = "POW: LIM TTW" + System.Environment.NewLine;
                byte[] byteSend8 = System.Text.Encoding.Default.GetBytes(command8.Replace("TT", maxPower.ToString()));
                powerClient[index - 1].SendBinaryToServer("PowerClient" + index.ToString(), byteSend8);
                System.Threading.Thread.Sleep(100);


                string command9 = "POW:LIM:NEG TTW" + System.Environment.NewLine;
                byte[] byteSend9 = System.Text.Encoding.Default.GetBytes(command9.Replace("TT", minPower.ToString()));
                powerClient[index - 1].SendBinaryToServer("PowerClient" + index.ToString(), byteSend9);
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
                string command1 = "OUTP 1" + System.Environment.NewLine;
                byte[] byteSend1 = System.Text.Encoding.Default.GetBytes(command1);
                powerClient[index - 1].SendBinaryToServer("PowerClient" + index.ToString(), byteSend1);
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
                string command1 = "OUTP 0" + System.Environment.NewLine;
                byte[] byteSend1 = System.Text.Encoding.Default.GetBytes(command1);
                powerClient[index - 1].SendBinaryToServer("PowerClient" + index.ToString(), byteSend1);
                return "OK";
            }

            catch (Exception ex)
            {
                return ex.Message;
            }
        }



    }
}
