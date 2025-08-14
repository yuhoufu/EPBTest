using DataOperation;
using MtEmbTest;
using NationalInstruments.DAQmx;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using DevExpress.XtraEditors;
using Sunny.UI;
using System.Threading;

namespace MTEmbTest
{
    public partial class FrmEpbMainMonitor : Form
    {
        private ConcurrentQueue<byte[]> bufferA = new ConcurrentQueue<byte[]>();
        private ConcurrentQueue<byte[]> bufferB = new ConcurrentQueue<byte[]>();
        private ConcurrentQueue<byte[]> activeWriteBuffer;
        private ConcurrentQueue<byte[]> readyReadBuffer;


        private ClsEMBControler[] EmbGroup = new ClsEMBControler[12];

        #region DAQ_AI变量

        private ConcurrentDictionary<string, double> ParaNameToScale = new ConcurrentDictionary<string, double>();
        private ConcurrentDictionary<string, double> ParaNameToOffset = new ConcurrentDictionary<string, double>();
        private ConcurrentDictionary<string, double> ParaNameToZeroValue = new ConcurrentDictionary<string, double>();


        private static string[] Dev1UsedDaqAIChannels;
        private NationalInstruments.DAQmx.Task Dev1analogTask;
        private AnalogMultiChannelReader Dev1analogReader;
        private AsyncCallback Dev1analogCallback;
        private NationalInstruments.DAQmx.Task Dev1runningAnalogTask;


        private static string[] Dev2UsedDaqAIChannels;
        private NationalInstruments.DAQmx.Task Dev2analogTask;
        private AnalogMultiChannelReader Dev2analogReader;
        private AsyncCallback Dev2analogCallback;
        private NationalInstruments.DAQmx.Task Dev2runningAnalogTask;

        private static ConcurrentDictionary<string, int> EMBToDaqCurrentChannel =
            new ConcurrentDictionary<string, int>();

        private static ConcurrentDictionary<string, uint> DirectionToSendFrame =
            new ConcurrentDictionary<string, uint>();

        private static ConcurrentDictionary<string, uint> DirectionToRecvFrame =
            new ConcurrentDictionary<string, uint>();

        private static ConcurrentDictionary<string, string> EMBToDirection = new ConcurrentDictionary<string, string>();
        private static ConcurrentDictionary<int, uint> EMBHandlerToSendFrame = new ConcurrentDictionary<int, uint>();
        private static ConcurrentDictionary<int, uint> EMBHandlerToRecvFrame = new ConcurrentDictionary<int, uint>();
        private static ConcurrentDictionary<string, uint> EMBNameToSendFrame = new ConcurrentDictionary<string, uint>();
        private static ConcurrentDictionary<string, uint> EMBNameToRecvFrame = new ConcurrentDictionary<string, uint>();

        private static ConcurrentDictionary<int, double> EMBHandlerToSendCanForceScale =
            new ConcurrentDictionary<int, double>();

        private static ConcurrentDictionary<int, double> EMBHandlerToSendCanForceOffset =
            new ConcurrentDictionary<int, double>();

        private static ConcurrentDictionary<int, double> EMBHandlerToRecvCanForceScale =
            new ConcurrentDictionary<int, double>();

        private static ConcurrentDictionary<int, double> EMBHandlerToRecvCanForceOffset =
            new ConcurrentDictionary<int, double>();

        private static ConcurrentDictionary<int, double> EMBHandlerToRecvCanCurrentScale =
            new ConcurrentDictionary<int, double>();

        private static ConcurrentDictionary<int, double> EMBHandlerToRecvCanCurrentOffset =
            new ConcurrentDictionary<int, double>();

        private static ConcurrentDictionary<int, double> EMBHandlerToRecvCanTorqueScale =
            new ConcurrentDictionary<int, double>();

        private static ConcurrentDictionary<int, double> EMBHandlerToRecvCanTorqueOffset =
            new ConcurrentDictionary<int, double>();


        private static ConcurrentDictionary<string, double> EMBNameToRecvCanForceScale =
            new ConcurrentDictionary<string, double>();

        private static ConcurrentDictionary<string, double> EMBNameToRecvCanForceOffset =
            new ConcurrentDictionary<string, double>();

        private static ConcurrentDictionary<string, double> EMBNameToRecvCanCurrentScale =
            new ConcurrentDictionary<string, double>();

        private static ConcurrentDictionary<string, double> EMBNameToRecvCanCurrentOffset =
            new ConcurrentDictionary<string, double>();

        private static ConcurrentDictionary<string, double> EMBNameToRecvCanTorqueScale =
            new ConcurrentDictionary<string, double>();

        private static ConcurrentDictionary<string, double> EMBNameToRecvCanTorqueOffset =
            new ConcurrentDictionary<string, double>();


        private double AiMaxVoltage = 10.0;
        private double AiMinVoltage = -10.0;


        private double DaqTimeSpanMilSeconds = 10.0;


        private ConcurrentQueue<double[]> DaqAiDispData = new ConcurrentQueue<double[]>();

        private const int DaqAiDispDataLens = 100;

        private DaqAIContext DaqContext1;

        // private DaqAIContext DaqContext2;
        private System.Threading.Timer DaqLogtimer1;
        private System.Threading.Timer DaqLogtimer2;

        private System.Threading.Timer TempTimer;

        #endregion


        #region 定时处理变量

        [DllImport("winmm.dll")]
        private static extern uint timeSetEvent(uint msDelay, uint msResolution, TimerProc handler, UIntPtr dwUser,
            uint eventType);

        [DllImport("winmm.dll")]
        private static extern uint timeKillEvent(uint uTimerId);

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint uPeriod);

        // 定时器回调委托
        private delegate void TimerProc(
            UIntPtr uTimerID,
            uint uMsg,
            UIntPtr dwUser,
            UIntPtr dw1,
            UIntPtr dw2);

        // 定时器状态类
        private class TimerState
        {
            public uint TimerId { get; set; }
            public TimerProc Handler { get; set; }
            public bool IsRunning { get; set; }
            public int Index { get; set; }
            public uint Interval { get; set; }

            public ConcurrentDictionary<int, int> CycleCounter = new ConcurrentDictionary<int, int>();
        }


        private static ConcurrentDictionary<string, int> EmbToChannel = new ConcurrentDictionary<string, int>();

        private static ConcurrentDictionary<int, int> EmbNoToChannel = new ConcurrentDictionary<int, int>();

        private static ConcurrentDictionary<int, string> EmbNoToName = new ConcurrentDictionary<int, string>();

        private static ConcurrentDictionary<string, string> EmbToAutoSendPath =
            new ConcurrentDictionary<string, string>();

        private static ConcurrentDictionary<string, IntPtr> EmbToAutoSendPtr =
            new ConcurrentDictionary<string, IntPtr>();

        private static ConcurrentDictionary<string, string>
            EmbToCancelPath = new ConcurrentDictionary<string, string>();

        private static ConcurrentDictionary<string, IntPtr> EmbToCancelPtr = new ConcurrentDictionary<string, IntPtr>();


        private const uint TIMER_PERIODIC = 1;
        private const uint DEFAULT_RESOLUTION = 1;
        private readonly List<TimerState> EmbControlTimers = new List<TimerState>(6);
        private int activeTimersCount = 0;

        #endregion


        
        public FrmEpbMainMonitor()
        {
            InitializeComponent();

            // 创建自定义标题栏
            Panel titleBar = new Panel
            {
                Height = 30,
                Dock = DockStyle.Top,
                BackColor = Color.AliceBlue
            };

            // 添加自定义按钮
            Button btnClose = new Button
            {
                Text = @"×",
                Size = new Size(50, 50),
                Dock = DockStyle.Right
            };
            btnClose.Click += (s, e) => this.Close();

            titleBar.Controls.Add(btnClose);
            this.Controls.Add(titleBar);

            // 添加拖拽功能
            titleBar.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    //ReleaseCapture();
                    //SendMessage(Handle, 0xA1, 0x2, 0);
                }
            };
        }
        
        private void FrmEpbMainMonitor_Load(object sender, EventArgs e)
        {
            try
            {
                DaqTimeSpanMilSeconds = 1000.0 / ClsGlobal.DaqFrequency;
        
                activeWriteBuffer = bufferA;
                readyReadBuffer = bufferB;
        
                string ReadMsg = String.Empty;
        
        
                ReadMsg = ClsXmlOperation.GetDaqAIUsedChannels(
                    System.Environment.CurrentDirectory + @"\Config\AIConfig.xml", "Dev1", out Dev1UsedDaqAIChannels);
                if (ReadMsg.IndexOf("OK") < 0)
                {
                    MessageBox.Show(ReadMsg);
                    return;
                }
        
                if (Dev1UsedDaqAIChannels.Length < 1)
                {
                    MessageBox.Show(@"未读取到 Dev1 DAQ AI 相关信息！");
                    return;
                }
        
        
                ReadMsg = ClsXmlOperation.GetDaqAIUsedChannels(
                    System.Environment.CurrentDirectory + @"\Config\AIConfig.xml", "Dev2", out Dev2UsedDaqAIChannels);
                if (ReadMsg.IndexOf("OK") < 0)
                {
                    MessageBox.Show(ReadMsg);
                    return;
                }
        
                if (Dev2UsedDaqAIChannels.Length < 1)
                {
                    MessageBox.Show(@"未读取到 Dev2 DAQ AI 相关信息！");
                    return;
                }
        
        
                ReadMsg = ClsXmlOperation.GetDaqAIChannelMapping(
                    System.Environment.CurrentDirectory + @"\Config\AIConfig.xml", "Dev1", Dev1UsedDaqAIChannels,
                    out EMBToDaqCurrentChannel);
                if (ReadMsg.IndexOf("OK", StringComparison.Ordinal) < 0)
                {
                    MessageBox.Show(ReadMsg);
                    return;
                }
        
                if (EMBToDaqCurrentChannel.Count < 1)
                {
                    MessageBox.Show(@"未读取到DAQ电流和EMB控制器对应关系！");
                    return;
                }
        
        
                ReadMsg = ClsXmlOperation.GetDaqScaleMapping(
                    System.Environment.CurrentDirectory + @"\Config\AIConfig.xml", "Dev1", out ParaNameToScale);
                if (ReadMsg.IndexOf("OK") < 0)
                {
                    MessageBox.Show(ReadMsg);
                    return;
                }
        
                ReadMsg = ClsXmlOperation.GetDaqOffsetMapping(
                    System.Environment.CurrentDirectory + @"\Config\AIConfig.xml", "Dev1", out ParaNameToOffset);
                if (ReadMsg.IndexOf("OK") < 0)
                {
                    MessageBox.Show(ReadMsg);
                    return;
                }
        
                ReadMsg = ClsXmlOperation.GetDaqZeroValueMapping(
                    System.Environment.CurrentDirectory + @"\Config\AIConfig.xml", "Dev1", out ParaNameToZeroValue);
                if (ReadMsg.IndexOf("OK") < 0)
                {
                    MessageBox.Show(ReadMsg);
                    return;
                }
        
        
                int handleNo = -1;
        
                // EmbToChannel 是无序的，要排序后再对应，此处应该有捂脸的表情包
        
        
                var sortedKeys = EmbToChannel.Keys.OrderBy(key => key).ToList();
        
                foreach (var key in sortedKeys)
                {
                    handleNo++;
                    EmbNoToChannel[handleNo] = EmbToChannel[key]; //处理顺序和波道对应
                    EmbNoToName[handleNo] = key;
                }
        
                //给处理序号和通道号字典赋值
        
        
                LoadEmbControler();
        
        
                InitializeCurve();
                StartListen();
                MakeCurveMapping();
                MakeDirectionMapping();
                LoadTestConfigFromXml();
                LoadEMBHandlerAndFrameNo();
        
                RtbInfo.Invoke(new SetTextCallback(SetInfoText), "1. 编辑试验信息并确认");
                //   RtbInfo.Invoke(new SetTextCallback(SetInfoText), "2. CAN卡初始化");
                //   RtbInfo.Invoke(new SetTextCallback(SetInfoText), "3. 打开各个电源开关");
                RtbInfo.Invoke(new SetTextCallback(SetInfoText), "2. 自学习/开始试验");
        
        
                ClsDiskProc.MakeSubDir(testConfig.StoreDir);
        
                string MainDrive = testConfig.StoreDir.Trim().Substring(0, 2);
        
                long LastSpace = ClsDiskProc.GetHardDiskSpace(MainDrive);
                if (LastSpace == 0)
                {
                    MessageBox.Show("指定磁盘不存在！");
                }
        
                if (LastSpace < 50)
                {
                    MessageBox.Show("剩余磁盘空间小于" + LastSpace.ToString() + "GB");
                }
        
        
                ChkEmb3.Checked = false;
                ChkEmb4.Checked = false;
                ChkEmb5.Checked = false;
                ChkEmb6.Checked = false;
        
        
                LoadCanDbc();
            }
        
            catch (Exception ex)
            {
                MessageBox.Show("初始化错误 : " + ex.Message);
            }
        }
        
        
        private void LoadEmbControler()
        {
            try
            {
                for (int i = 0; i < 6; i++)
                {
                    EmbGroup[i] = new ClsEMBControler();
                    EmbGroup[i].EmbNo = i + 1;
                    EmbGroup[i].EmbName = "EPB" + (i + 1).ToString();
                    //  EmbGroup[i].Cycles = 0;
                    EmbGroup[i].IsEnabel = true;
        
        
                }
        
                EmbGroup[0].CtrlJoinTest = ChkEpb1;
                EmbGroup[1].CtrlJoinTest = ChkEpb2;
                EmbGroup[2].CtrlJoinTest = ChkEpb3;
                EmbGroup[3].CtrlJoinTest = ChkEpb4;
                EmbGroup[4].CtrlJoinTest = ChkEpb5;
                EmbGroup[5].CtrlJoinTest = ChkEpb6;
        
        
                /*
                EmbGroup[0].CtrlCurrentEmb = RadEmb1;
                EmbGroup[1].CtrlCurrentEmb = RadEmb2;
                EmbGroup[2].CtrlCurrentEmb = RadEmb3;
                EmbGroup[3].CtrlCurrentEmb = RadEmb4;
                EmbGroup[4].CtrlCurrentEmb = RadEmb5;
                EmbGroup[5].CtrlCurrentEmb = RadEmb6; */
        
                EmbGroup[0].CtrlRunning = SwitchEpb1;
                EmbGroup[1].CtrlRunning = SwitchEpb2;
                EmbGroup[2].CtrlRunning = SwitchEpb3;
                EmbGroup[3].CtrlRunning = SwitchEpb4;
                EmbGroup[4].CtrlRunning = SwitchEpb5;
                EmbGroup[5].CtrlRunning = SwitchEpb6;
        
        
                EmbGroup[0].CtrlCycles = LabEpb1;
                EmbGroup[1].CtrlCycles = LabEpb2;
                EmbGroup[2].CtrlCycles = LabEpb3;
                EmbGroup[3].CtrlCycles = LabEpb4;
                EmbGroup[4].CtrlCycles = LabEpb5;
                EmbGroup[5].CtrlCycles = LabEpb6;
                
                /*
                EmbGroup[0].CtrlAlert = AlertEmb1;
                EmbGroup[1].CtrlAlert = AlertEmb2;
                EmbGroup[2].CtrlAlert = AlertEmb3;
                EmbGroup[3].CtrlAlert = AlertEmb4;
                EmbGroup[4].CtrlAlert = AlertEmb5;
                EmbGroup[5].CtrlAlert = AlertEmb6;   // 界面上没有这些控件，暂时注释掉
                */


                EmbGroup[0].CtrlPower = SwitchPower1;
                EmbGroup[1].CtrlPower = SwitchPower2;
                EmbGroup[2].CtrlPower = SwitchPower3;
                EmbGroup[3].CtrlPower = SwitchPower4;
                EmbGroup[4].CtrlPower = SwitchPower5;
                EmbGroup[5].CtrlPower = SwitchPower6;
        
        
        
                for (int i = 0; i < 6; i++)
                {
        
                    EmbGroup[i].CtrlRunning.Enabled = false;     //单个启动按钮设为不允许，启动之后才允许
                    int index = i;
                    EmbGroup[i].CtrlJoinTest.CheckedChanged += (sender, e) => JoinEmbChanged(sender, e, index);
        
                    // EmbGroup[i].CtrlCurrentEmb.CheckedChanged += (sender, e) => CurrentEmbChanged(sender, e, index); // 界面上没有这个控件，暂时注释掉


                    EmbGroup[i].CtrlRunning.ValueChanged += (sender, e) =>
                    {
                        RuningStatusChanged(sender, ((UISwitch)sender).Active, index);
                    };
        
                    EmbGroup[i].CtrlRunning.Click += (sender, e) => RunningClick(sender, e, index);
                    EmbGroup[i].CtrlPower.Click += (sender, e) => PowerClick(sender, e, index);
        
        
        
        
        
                }
        
        
        
            }
            catch (Exception ex)
            {
                MessageBox.Show(@"初始化组件失败！" + ex.Message);
            }
        }



        private void JoinEmbChanged(object sender, EventArgs e, int index)
        {
            CheckEdit checkBox = (CheckEdit)sender;
            if (checkBox.Checked)
            {
                // EmbGroup[index].CtrlCurrentEmb.Enabled = true; // 界面上没有这个控件，暂时注释掉
                EmbGroup[index].CtrlPower.Enabled = true;
                // EmbGroup[index].CtrlAlert.Enabled = true; // 界面上没有这个控件，暂时注释掉
                EmbGroup[index].CtrlCycles.Enabled = true;
                EmbGroup[index].IsEnabel = true;
            }
            else
            {
                // EmbGroup[index].CtrlCurrentEmb.Enabled = false; // 界面上没有这个控件，暂时注释掉
                EmbGroup[index].CtrlPower.Enabled = false;
                // EmbGroup[index].CtrlAlert.Enabled = false; // 界面上没有这个控件，暂时注释掉
                EmbGroup[index].CtrlCycles.Enabled = false;
                // EmbGroup[index].CtrlCurrentEmb.Checked = false; // 界面上没有这个控件，暂时注释掉
                EmbGroup[index].IsEnabel = false;
            }
        }

        private void RuningStatusChanged(object sender, bool value, int index)
        {
            if (value)
            {
                StartEmbControlTimer(index);
                // EmbGroup[index].CtrlAlert.OffCenterColor = Color.FromArgb(140, 140, 140);
                // EmbGroup[index].CtrlAlert.OffColor = Color.FromArgb(140, 140, 140);
                // EmbGroup[index].CtrlAlert.OnCenterColor = Color.Lime;
                // EmbGroup[index].CtrlAlert.OnColor = Color.Lime;
                // EmbGroup[index].CtrlAlert.State = UILightState.Blink;
            }
            else
            {
                StopEmbControlTimer(index);


                // EmbGroup[index].CtrlAlert.State = UILightState.On;


            }


        }


        #region  周期定时处理
        // 初始化12个定时器
        private void InitializeEmbControlTimers(uint TimeInterval)
        {
            try
            {
                EmbControlTimers.Clear();
                for (int i = 0; i < 12; i++)
                {
                    EmbControlTimers.Add(new TimerState
                    {
                        Index = i,
                        Interval = TimeInterval,
                        IsRunning = false

                    });
                    EmbControlTimers[i].CycleCounter[i] = 0;

                }
            }

            catch (Exception ex)
            {
                MessageBox.Show(@"初始化定时访问组件失败！" + ex.Message);
            }

        }

        // 启动指定定时器
        public bool StartEmbControlTimer(int EmbIndex)
        {
            try
            {
                if (EmbIndex < 0 || EmbIndex >= 6)
                    return false;

                var timer = EmbControlTimers[EmbIndex];
                if (timer.IsRunning)
                    return true;

                // 首次启动时设置高精度定时器
                if (Interlocked.Increment(ref activeTimersCount) == 1)
                {
                    timeBeginPeriod(DEFAULT_RESOLUTION);
                }

                timer.Handler = new TimerProc(EmbControlTimerHandler);
                timer.TimerId = timeSetEvent(
                    timer.Interval,
                    DEFAULT_RESOLUTION,
                    timer.Handler,
                    (UIntPtr)EmbIndex,
                    TIMER_PERIODIC
                );

                //  SafeLogError(EmbIndex.ToString() + " create!");

                if (timer.TimerId == 0)
                {
                    MessageBox.Show($@"Timer {EmbIndex} failed to start!");
                    Interlocked.Decrement(ref activeTimersCount);
                    return false;
                }

                timer.IsRunning = true;
                return true;
            }
            catch (Exception ex)
            {
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "启动EMB" + EmbIndex.ToString() + "定时控制失败！" + ex.Message, "CAN通信");
                return false;
            }
        }

        // 停止指定定时器
        public bool StopEmbControlTimer(int index)
        {
            try
            {
                if (index < 0 || index >= 6)
                    return false;


                if (!EmbControlTimers[index].IsRunning)
                    return true;


                timeKillEvent(EmbControlTimers[index].TimerId);

                EmbControlTimers[index].IsRunning = false;
                EmbControlTimers[index].TimerId = 0;
                EmbControlTimers[index].Handler = null;

                // 最后一个定时器停止时恢复分辨率
                if (Interlocked.Decrement(ref activeTimersCount) == 0)
                {
                    timeEndPeriod(DEFAULT_RESOLUTION);
                }
                return true;
            }

            catch (Exception ex)
            {
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "停止EMB" + index.ToString() + "定时控制失败！" + ex.Message, "CAN通信");
                return false;
            }

        }

        // 定时器回调处理
        private void EmbControlTimerHandler(UIntPtr uTimerID, uint uMsg, UIntPtr dwUser, UIntPtr dw1, UIntPtr dw2)
        {
            //传入EMB处理的序号
            int index = (int)dwUser.ToUInt32();
            if (index < 0 || index >= 6)
                return;
            try
            {
                if (!IsAutoLearn)
                {
                    var timer = EmbControlTimers[index];

                    // 原先是can通信操作，需要改为6002的AO操作
                    Action action = () =>
                    {
                        //  SafeLogError("Enter No " + index.ToString());
                        timer.CycleCounter[index]++;

                        if ((timer.CycleCounter[index] % 6000) == 0)  //60秒记录一次
                        {
                            ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "EMB" + (index + 1).ToString() + " 发送夹紧指令 ", "CAN通信");
                        }

                        /*
                        lock (clampCounterLocks[index])
                        {
                            if (++releaseFailureCounters[index] >= 3)
                            {
                                //  TriggerAlarm(index);
                                AlertStatus[index] = 1;
                                ClearAutoSend(EmbNoToChannel[index]);
                                SendReleaseCommandToDevice(EmbNoToName[index]);
                                ApplyAutoSend(EmbNoToChannel[index]);
                                releaseFailureCounters[index] = 0;
                                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "EMB" + (index + 1).ToString() + "连续夹紧告警！", "告警");
                            }
                        }*/


                    };

                    if (InvokeRequired)
                    {
                        Invoke(action);
                    }
                    else
                    {
                        action();
                    }
                }



            }
            catch (Exception ex)
            {
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "EMB" + (index + 1).ToString() + "定时发送指令出错！" + ex.Message, "定时发送指令");
            }
        }

        // 停止所有定时器
        public void StopAllEmbControlTimers()
        {
            for (int i = 0; i < 6; i++)
            {
                StopEmbControlTimer(i);
            }
        }

        // 设置定时器间隔
        public bool SetEmbControlTimerInterval(int index, uint newInterval)
        {
            if (index < 0 || index >= 6)
                return false;

            var timer = EmbControlTimers[index];
            if (timer.Interval == newInterval)
                return true;

            bool wasRunning = timer.IsRunning;
            if (wasRunning)
            {
                StopEmbControlTimer(index);
            }

            timer.Interval = newInterval;

            if (wasRunning)
            {
                return StartEmbControlTimer(index);
            }
            return true;
        }

        // 获取定时器状态信息
        public string GetTimerStatus(int index)
        {
            if (index < 0 || index >= 6)
                return "Invalid index";

            var timer = EmbControlTimers[index];
            return $"Timer {index}: {(timer.IsRunning ? "▶ Running" : "⏹ Stopped")}\n" +
                   $"Interval: {timer.Interval}ms\n" +
                   $"Counter: {timer.CycleCounter[index]}";
        }

        #endregion 










    }
}