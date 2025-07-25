using AsyncListener;
using DataOperation;
using NationalInstruments.DAQmx;
using NationalInstruments.DataInfrastructure;
using Sunny.UI;
using Sunny.UI.Win32;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;
using ZedGraph;
using ZLGCAN;
using ZlgCanComm;
//using static System.Windows.Forms.VisualStyles.VisualStyleElement;


namespace MtEmbTest
{
    public partial class FrmAdjustTorque : Form
    {
        [DllImport("user32.dll")]
        public static extern int SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        private AutoLearnDetector autoLearnDetector;

        private int AutoLearnClickCount = 0;

        private int CanIDClickCount = 0;
      
        private int StartClickCount = 0;
        TextObj GraphTextLabel;
        private bool IsGetCanID = false;

        private  string NewDirection = "";

        private int BrakeNo = 0;
        private ConcurrentQueue<byte[]> StatLog = new ConcurrentQueue<byte[]>();
        private const int StatLogMaxLens = 100;
        private const int StatLogRecordLens = 77;
        private const int ResetDevWaitSpan = 3000;
        private System.Threading.Timer MinitorTimer;
        private System.Threading.Timer WriteStatTimer;
        private string CurrentStatFileName = "";
        private readonly SemaphoreSlim StatFileLock = new SemaphoreSlim(1);


        private ConcurrentDictionary<string, int> ParaNameToActChannel = new ConcurrentDictionary<string, int>();
        private ConcurrentDictionary<string, string> PhyChannelToParaName = new ConcurrentDictionary<string, string>();
        private ConcurrentDictionary<string, double> ParaNameToScale = new ConcurrentDictionary<string, double>();
        private ConcurrentDictionary<string, double> ParaNameToOffset = new ConcurrentDictionary<string, double>();
        private ConcurrentDictionary<string, double> ParaNameToZeroValue = new ConcurrentDictionary<string, double>();

        private DateTime lastGraphyTime = DateTime.Now;
        private int curveDispSpan = 0;
        private int dataLogSpan = 0;

        private NationalInstruments.DAQmx.Task AoTask;
        private readonly object AotaskLock = new object();


        private NationalInstruments.DAQmx.Task DoTask;
        private readonly object DotaskLock = new object();


        #region  DAQ_DI变量
        private NationalInstruments.DAQmx.Task DiTask;
        private DigitalMultiChannelReader DigitalReader;
        private const int ReadDiCount = 2;    //连续读入DI的点数，避免瞬时干扰  实际应用建议为3

        private ConcurrentDictionary<string, int> DIChannelMapping = new ConcurrentDictionary<string, int>();

        #endregion

        #region  Serial变量
        private TaskCompletionSource<byte[]> serialResponseTcs;
        private static SerialPort serialPort;
        private readonly object _serialPortLock = new object();
        #endregion

        #region  DAQ_AI变量
        private static string[] Dev1UsedDaqAIChannels;
        private NationalInstruments.DAQmx.Task Dev1analogTask;
        private AnalogMultiChannelReader Dev1analogReader;
        private AsyncCallback Dev1analogCallback;
        private NationalInstruments.DAQmx.Task Dev1runningAnalogTask;






        private static ConcurrentDictionary<string, uint> DirectionToSendFrame = new ConcurrentDictionary<string, uint>();
        private static ConcurrentDictionary<string, uint> DirectionToRecvFrame = new ConcurrentDictionary<string, uint>();

        private static ConcurrentDictionary<string, string> EMBToDirection = new ConcurrentDictionary<string, string>();
        private static ConcurrentDictionary<int, uint> EMBHandlerToSendFrame = new ConcurrentDictionary<int, uint>();
        private static ConcurrentDictionary<int, uint> EMBHandlerToRecvFrame = new ConcurrentDictionary<int, uint>();
        private static ConcurrentDictionary<string, uint> EMBNameToSendFrame = new ConcurrentDictionary<string, uint>();
        private static ConcurrentDictionary<string, uint> EMBNameToRecvFrame = new ConcurrentDictionary<string, uint>();


        private double AiMaxVoltage = 10.0;
        private double AiMinVoltage = -10.0;


        //private double CanDeltTime = 0.01;
        //private double DaqDeltTime = 0.01;
        private double DaqTimeSpanMilSeconds = 10.0;



        private ConcurrentQueue<double[]> DaqAiCurrentDispData = new ConcurrentQueue<double[]>();
        private ConcurrentQueue<double[]> DaqAiTorqueDispData = new ConcurrentQueue<double[]>();

        private const int DaqAiDispDataLens = 100;

        private DaqAIContext DaqContext1;

        private System.Threading.Timer DaqLogtimer1;





        #endregion




        #region  CAN卡通信变量


        private bool CanIsOK = false;
        private const int NULL = 0;
        private static IntPtr device_handle_;
        private static IntPtr[] channel_handle_; // 修改为数组
        private bool IsDevOpen = false;
        private bool[] IsChannelStart; // 修改为数组
        private static recvdatathread[] recv_data_thread_; // 修改为数组

        private const int CANFD_BRS = 0x01; /* bit rate switch (second bitrate for payload data) */
       
        private static byte[] FreeClampBytes;
        private static byte[] AutoLearnBytes;
        private const int CANFD_MAX_DLEN = 16;

        private static List<byte[]> ClampBytesList=new List<byte[]>();
        private static List<byte[]> ReleaseBytesList = new List<byte[]>();

        private byte[] ClampCommand;
        private byte[] ReleaseCommand;

        #endregion

        #region  定时处理变量
        [DllImport("winmm.dll")]
        private static extern uint timeSetEvent(uint msDelay, uint msResolution, TimerProc handler, UIntPtr dwUser, uint eventType);

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

            public GCHandle GcHandle; 

            public ConcurrentDictionary<int, int> CycleCounter = new ConcurrentDictionary<int, int>();



        }


        private static ConcurrentDictionary<string, int> EmbToChannel = new ConcurrentDictionary<string, int>();

        private static ConcurrentDictionary<int, int> EmbNoToChannel = new ConcurrentDictionary<int, int>();

        private static ConcurrentDictionary<int, string> EmbNoToName = new ConcurrentDictionary<int, string>();

        private static ConcurrentDictionary<string, string> EmbToAutoSendPath = new ConcurrentDictionary<string, string>();

        private static ConcurrentDictionary<string, IntPtr> EmbToAutoSendPtr = new ConcurrentDictionary<string, IntPtr>();

        private static List<IntPtr> ClampPtr = new List<IntPtr>();

        private static List<IntPtr> ReleasePtr = new List<IntPtr>();



        private static ConcurrentDictionary<string, string> EmbToCancelPath = new ConcurrentDictionary<string, string>();

        private static ConcurrentDictionary<string, IntPtr> EmbToCancelPtr = new ConcurrentDictionary<string, IntPtr>();


        private const uint TIMER_PERIODIC = 1;
        private const uint DEFAULT_RESOLUTION = 1;
        private readonly List<TimerState> EmbControlTimers = new List<TimerState>(1);
      //  private  List<TimerState> EmbControlTimers = new List<TimerState>(1);































        private int activeTimersCount = 0;
        #endregion


        private TestConfig testConfig;
        private bool IsAutoLearn = false;
        private bool IsTestConfirm = false;
        private bool IsRunning = false;


        private volatile List<byte[]> forceSnapshot = new List<byte[]>();
        private volatile double[] daqCurrentSnapshot = Array.Empty<double>();
        private volatile double[] daqTorqueSnapshot = Array.Empty<double>();

        private ConcurrentQueue<byte[]> bufferA = new ConcurrentQueue<byte[]>();
        private ConcurrentQueue<byte[]> bufferB = new ConcurrentQueue<byte[]>();
        private ConcurrentQueue<byte[]> activeWriteBuffer;
        private ConcurrentQueue<byte[]> readyReadBuffer;


        private readonly object bufferLock = new object();   //实时曲线缓存数据锁
        private readonly object graphLock = new object();   //曲线更新锁
        private readonly object[] clampCounterLocks = Enumerable.Range(0, 1).Select(_ => new object()).ToArray();  //指令发送计数锁

        private int[] releaseFailureCounters = new int[1];  //松开失败计数，发送时加1，松开清零，此数超过预设值说明连续加紧，要告警并松开卡钳


        private readonly object currentDevLock = new object();
        private string _currentDev = "EMB1"; // 添加私有字段
        public string CurrentDev
        {
            get
            {
                lock (currentDevLock)
                {
                    return _currentDev;
                }
            }
            set
            {
                lock (currentDevLock)
                {
                    _currentDev = value;
                }
            }
        }


       private readonly object currentClampForce = new object();
       private double _currentClampForce = 15000.0; 
        public double CurrentClampForce
        {
            get
            {
                lock (currentClampForce)
                {
                    return _currentClampForce;
                }
            }
            set
            {
                lock (currentClampForce)
                {
                    _currentClampForce = value;
                }
            }
        }

        private readonly object currentPushVoltage = new object();
        private double _currentPushVoltage = 3.0;
        public double CurrentPushVoltage
        {
            get
            {
                lock (currentPushVoltage)
                {
                    return _currentPushVoltage;
                }
            }
            set
            {
                lock (currentPushVoltage)
                {
                    _currentPushVoltage = value;
                }
            }
        }


        private readonly object currentTarTorque = new object();
        private double _currentTarTorque = 800.0;
        public double CurrentTarTorque
        {
            get
            {
                lock (currentTarTorque)
                {
                    return _currentTarTorque;
                }
            }
            set
            {
                lock (currentTarTorque)
                {
                    _currentTarTorque = value;
                }
            }
        }



        private System.Threading.Timer curveDisplayTimer;




        private LineItem curveForce;
        private PointPairList listForce;

        //private LineItem curveDaqCurrent;
        //private PointPairList listDaqCurrent;

        private LineItem curveDaqTorque;
        private PointPairList listDaqTorque;

        //private LineItem curveCanCurrent;
        //private PointPairList listCanCurrent;

        public class LineItemOperation
        {
            public LineItem lineItem { get; set; }
            public bool IsActive { get; set; }
        }
        private ConcurrentDictionary<string, LineItemOperation> curveDictionary = new ConcurrentDictionary<string, LineItemOperation>();



        private ClsEMBControler[] EmbGroup = new ClsEMBControler[1];
        private DateTime runBegin;


        private ConcurrentQueue<string> LogError = new ConcurrentQueue<string>();
        private ConcurrentQueue<string> LogInformation = new ConcurrentQueue<string>();
        private const int MaxErrors = 100000;
        private const int MaxInfos = 100000;

        //测试使用，正式版删除
        private static AsyncTCPServer TCP_MainTriggerServer;   //主WIFI
        private static AsyncTCPServer TCP_SecTriggerServer;   //次WIFI

        // private ConcurrentQueue<CanData> dataQueue = new ConcurrentQueue<CanData>();
        private const int CacheLens = 6000;  //每秒100帧，3秒处理一次，最多缓存6秒


        private const int DeviceCount = 1; // 共1个设备
        private readonly DeviceContext[] _deviceContexts = new DeviceContext[DeviceCount];
        private System.Threading.Timer[] _logtimers = new System.Threading.Timer[1];

        private ConcurrentDictionary<int, int> AlertStatus = new ConcurrentDictionary<int, int>();
        //AlertStatus  0 正常  1 值太小，连续夹紧   2 值太高  3 FaultMode 报警






        private ConcurrentDictionary<int, int> CanRecvCounter = new ConcurrentDictionary<int, int>();
        //CAN接收计数


        private static ConcurrentDictionary<int, double> EMBHandlerToSendCanForceScale = new ConcurrentDictionary<int, double>();
        private static ConcurrentDictionary<int, double> EMBHandlerToSendCanForceOffset = new ConcurrentDictionary<int, double>();

        private static ConcurrentDictionary<int, double> EMBHandlerToRecvCanForceScale = new ConcurrentDictionary<int, double>();
        private static ConcurrentDictionary<int, double> EMBHandlerToRecvCanForceOffset = new ConcurrentDictionary<int, double>();

        private static ConcurrentDictionary<int, double> EMBHandlerToRecvCanCurrentScale = new ConcurrentDictionary<int, double>();
        private static ConcurrentDictionary<int, double> EMBHandlerToRecvCanCurrentOffset = new ConcurrentDictionary<int, double>();

        private static ConcurrentDictionary<int, double> EMBHandlerToRecvCanTorqueScale = new ConcurrentDictionary<int, double>();
        private static ConcurrentDictionary<int, double> EMBHandlerToRecvCanTorqueOffset = new ConcurrentDictionary<int, double>();


        private static ConcurrentDictionary<string, double> EMBNameToRecvCanForceScale = new ConcurrentDictionary<string, double>();
        private static ConcurrentDictionary<string, double> EMBNameToRecvCanForceOffset = new ConcurrentDictionary<string, double>();

        private static ConcurrentDictionary<string, double> EMBNameToRecvCanCurrentScale = new ConcurrentDictionary<string, double>();
        private static ConcurrentDictionary<string, double> EMBNameToRecvCanCurrentOffset = new ConcurrentDictionary<string, double>();

        private static ConcurrentDictionary<string, double> EMBNameToRecvCanTorqueScale = new ConcurrentDictionary<string, double>();
        private static ConcurrentDictionary<string, double> EMBNameToRecvCanTorqueOffset = new ConcurrentDictionary<string, double>();







        #region  UI滚动消息
        delegate void SetTextCallback(string text);

        private void SetInfoText(string text)
        {
            RtbInfo.AppendText(text + "\n");
            RtbInfo.ScrollToCaret();
        }
        #endregion

        #region  CAN处理函数

        private void InitCanDev()
        {
            try
            {



                CanIsOK = false;

                uint devNo = (uint)ClsGlobal.CardNo;
                device_handle_ = ZlgCanOperation.ZCAN_OpenDevice(Define.ZCAN_USBCANFD_MINI, devNo, 0);
                if (NULL == (int)device_handle_)
                {
                    MessageBox.Show("打开设备失败,请检查设备类型和设备索引号是否正确！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "打开设备失败,请检查设备类型和设备索引号是否正确", "打开CAN卡");
                    return;
                }

                ZCAN_DEVICE_INFO deINFO = new ZCAN_DEVICE_INFO();
                IntPtr pdinfo = Marshal.AllocHGlobal(Marshal.SizeOf(deINFO));
                Marshal.StructureToPtr(deINFO, pdinfo, true);
                uint ret = ZlgCanOperation.ZCAN_GetDeviceInf(device_handle_, pdinfo);
                deINFO = (ZCAN_DEVICE_INFO)Marshal.PtrToStructure(pdinfo, typeof(ZCAN_DEVICE_INFO));
                Marshal.FreeHGlobal(pdinfo);
                IsDevOpen = true;

                string DevSN = Encoding.Default.GetString(deINFO.str_Serial_Num, 0, deINFO.str_Serial_Num.Length);

                RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "打开设备成功！SN = " + DevSN + "\n\r");
                ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "打开设备成功！SN = " + DevSN, "打开CAN卡");

                System.Threading.Thread.Sleep(500);

                if (!IsDevOpen)
                {
                    return;
                }

                if (EmbToChannel.Count < 1)
                {
                    MessageBox.Show("尚未获取EMB设备和CAN通道之间的关联关系！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "尚未获取EMB设备和CAN通道之间的关联关系", "打开CAN卡");
                    return;
                }

                int channelCount = 1;
                channel_handle_ = new IntPtr[channelCount];
                IsChannelStart = new bool[channelCount];


                int handleIndex = -1;


                var sortedKeys = EmbToChannel.Keys.OrderBy(key => key).ToList();

                foreach (var key in sortedKeys)
                {
                    handleIndex++;    //从0开始

                    if (!EmbGroup[handleIndex].IsEnabel)
                    {
                        continue;
                    }


                    int CanChannelIndex = EmbToChannel[key];


               

                
                    uint aBaud = (uint)ClsGlobal.ARate;
                    uint dBaud = (uint)ClsGlobal.DRate;

                    if (!setFdBaudrate(CanChannelIndex, aBaud, dBaud))
                    {
                        MessageBox.Show($"通道 {CanChannelIndex} 设置波特率失败!", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, $"通道 {CanChannelIndex} 设置波特率失败!", "打开CAN卡");
                        IsDevOpen = false;
                        return;
                    }

                    RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + $"通道 {CanChannelIndex} 设置波特率成功!");
                    ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, $"通道 {CanChannelIndex} 设置波特率成功!", "打开CAN卡");

                    ZCAN_CHANNEL_INIT_CONFIG config_ = new ZCAN_CHANNEL_INIT_CONFIG();
                    config_.can_type = Define.TYPE_CANFD;
                    config_.canfd.mode = 0;

                    IntPtr pConfig = Marshal.AllocHGlobal(Marshal.SizeOf(config_));
                    Marshal.StructureToPtr(config_, pConfig, true);

                    channel_handle_[handleIndex] = ZlgCanOperation.ZCAN_InitCAN(device_handle_, (uint)CanChannelIndex, pConfig);
                    Marshal.FreeHGlobal(pConfig);

                    if (NULL == (int)channel_handle_[handleIndex])
                    {
                        MessageBox.Show($"通道 {CanChannelIndex} 初始化CAN失败!", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, $"通道 {CanChannelIndex} 初始化CAN失败!", "打开CAN卡");
                        IsDevOpen = false;
                        return;
                    }

                    RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + $"通道 {CanChannelIndex} 初始化成功!");
                    ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, $"通道 {CanChannelIndex} 初始化成功!", "打开CAN卡");

                    if (ClsGlobal.ResistorEnabel == 1)
                    {
                        if (!setResistanceEnable(CanChannelIndex, "1"))
                        {
                            MessageBox.Show($"通道 {CanChannelIndex} 设置终端电阻使能失败!", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, $"通道 {CanChannelIndex} 设置终端电阻使能失败!", "打开CAN卡");
                            IsDevOpen = false;
                            return;
                        }

                        RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + $"通道 {CanChannelIndex} 设置终端电阻使能成功!");
                        ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, $"通道 {CanChannelIndex} 设置终端电阻使能成功!", "打开CAN卡");
                    }

                    System.Threading.Thread.Sleep(500);

                    if (ZlgCanOperation.ZCAN_StartCAN(channel_handle_[handleIndex]) != Define.STATUS_OK)
                    {
                        MessageBox.Show($"通道 {CanChannelIndex} 启动CAN失败!", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, $"通道 {CanChannelIndex} 启动CAN失败!", "打开CAN卡");
                        IsDevOpen = false;
                        return;
                    }

                    IsChannelStart[handleIndex] = true;
                    RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + $"通道 {CanChannelIndex} 启动成功!");
                    ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, $"通道 {CanChannelIndex} 启动成功!", "打开CAN卡");
                }


                StartRecvThread();



                CanIsOK = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("初始化CAN卡失败!  " + ex.Message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, ex.Message, "打开CAN卡");
                IsDevOpen = false;
                CanIsOK = false;
            }
        }


        private void StartRecvThread()
        {
            try
            {
                recv_data_thread_ = new recvdatathread[1];


                if (EmbGroup[0].IsEnabel)
                {
                    if (null == recv_data_thread_[0])
                    {
                        recv_data_thread_[0] = new recvdatathread();
                        recv_data_thread_[0].setChannelHandle(channel_handle_[0]);
                        recv_data_thread_[0].setStart(IsChannelStart[0]);
                        recv_data_thread_[0].RecvFDData += (data, len) => this.RecvCanFdData(data, len, 0);    //传入EMB名称
                        recv_data_thread_[0].setDeviceHandle(device_handle_);
                    }
                    else
                    {
                        recv_data_thread_[0].setChannelHandle(channel_handle_[0]);
                        recv_data_thread_[0].setDeviceHandle(device_handle_);
                    }
                }





            }
            catch (Exception ex)
            {
                MessageBox.Show("设置CAN接收函数失败!  " + ex.Message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, ex.Message, "打开CAN卡");
                IsDevOpen = false;
                CanIsOK = false;
            }
        }




        private bool setFdBaudrate(int channelIndex, UInt32 abaud, UInt32 dbaud)
        {
            try
            {
                string path = channelIndex.ToString() + "/canfd_abit_baud_rate";
                string value = abaud.ToString();
                if (1 != ZlgCanOperation.ZCAN_SetValue(device_handle_, path, Encoding.ASCII.GetBytes(value)))
                {
                    return false;
                }
                path = channelIndex + "/canfd_dbit_baud_rate";
                value = dbaud.ToString();
                if (1 != ZlgCanOperation.ZCAN_SetValue(device_handle_, path, Encoding.ASCII.GetBytes(value)))
                {
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                return false;
            }
        }


        //设置终端电阻使能
        private bool setResistanceEnable(int channelIndex, string value)
        {
            try
            {
                string path = channelIndex.ToString() + "/initenal_resistance";

                return 1 == ZlgCanOperation.ZCAN_SetValue(device_handle_, path, Encoding.ASCII.GetBytes(value));
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        private  string UpdateEMBConfig(string filename, string currentName,  string newDirection)
        {
            try
            {
                // 加载 XML 文档
                XmlDocument doc = new XmlDocument();
                doc.Load(filename);

                // 获取根节点
                XmlNode root = doc.DocumentElement;
                if (root == null || root.Name != "EMBControl")
                {
                    return "错误：无效的XML根节点";
                }

                // 查找匹配的 EMB 元素
                bool found = false;
                foreach (XmlNode embNode in root.ChildNodes)
                {
                    if (embNode.Name != "EMB") continue;

                    XmlNode nameNode = null;
                    XmlNode directionNode = null;

                    // 查找名称和方向子节点
                    foreach (XmlNode child in embNode.ChildNodes)
                    {
                        if (child.Name == "名称") nameNode = child;
                        if (child.Name == "方向") directionNode = child;
                    }

                    // 检查是否匹配当前名称
                    if (nameNode != null && nameNode.InnerText.Trim() == currentName)
                    {
                        found = true;
                        // 更新方向
                        if (!string.IsNullOrEmpty(newDirection))
                        {
                            if (directionNode != null)
                            {
                                directionNode.InnerText = newDirection;
                            }
                            else
                            {
                                // 如果方向节点不存在，则创建新节点
                                XmlElement newDirectionNode = doc.CreateElement("方向");
                                newDirectionNode.InnerText = newDirection;
                                embNode.AppendChild(newDirectionNode);
                            }
                        }

                        // 保存修改
                        doc.Save(filename);
                        break;
                    }
                }

                if (!found)
                {
                    return $"错误：未找到名称为 '{currentName}' 的 EMB 元素";
                }

                return "OK";
            }
            catch (Exception ex)
            {
                return $"错误：{ex.Message}";
            }
        }

        private void RecvCanFdData(ZCAN_ReceiveFD_Data[] data, uint len, int HandleIndex)
        {
            try
            {

                if (IsGetCanID&&!IsAutoLearn&&!IsRunning)
                {
                    for (uint i = 0; i < len; ++i)
                    {
                        uint id = data[i].frame.can_id;
                        if(id== 0x602)
                        {
                            
                            NewDirection= "FL";
                            RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "CAN MsgID = "+ id.ToString()+ " Lens = "+ data[i].frame.data.Length.ToString()+"  FL");

                        }

                        if (id == 0x606)
                        {
                            NewDirection = "FR";
                            RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "CAN MsgID = " + id.ToString() + " Lens = " + data[i].frame.data.Length.ToString() + "  FR");

                        }
                        if (id == 0x60a)
                        {
                            NewDirection = "RL";
                            RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "CAN MsgID = " + id.ToString() + " Lens = " + data[i].frame.data.Length.ToString() + "  RL");

                        }
                        if (id == 0x60e)
                        {
                            NewDirection = "RR";
                            RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "CAN MsgID = " + id.ToString() + " Lens = " + data[i].frame.data.Length.ToString() + "  RR");

                        }
                    }


                  
                     

                    return;
                }     //获得CAN ID 完成



                if (!IsGetCanID && !IsAutoLearn && IsRunning)
                {
                    DateTime LastRecvTime = DateTime.Now;

                    int RecvFrames = 0;

                    for (uint i = 0; i < len; ++i)
                    {
                        uint id = data[i].frame.can_id;

                        if (id == EMBHandlerToRecvFrame[HandleIndex])
                        {
                            RecvFrames++;
                        }
                    }

                    DateTime FirstRecvTime = LastRecvTime.AddMilliseconds(ClsGlobal.CanRecvTimeSpanMillSecs * (double)(RecvFrames - 1) * -1.0);



                    int RecvCounter = 0;

                    for (uint i = 0; i < len; ++i)
                    {
                        uint id = data[i].frame.can_id;

                        ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "id = " + id.ToString() + "  lens=" + data[i].frame.data.Length.ToString(), "告警");

                        if (id == EMBHandlerToRecvFrame[HandleIndex] && data[i].frame.data.Length == ClsGlobal.RecvCanLens)
                        {
                            AlertStatus[HandleIndex] = 0;    //取消告警
                            CanRecvCounter[HandleIndex]++;
                            if ((CanRecvCounter[HandleIndex] % 6000) == 0)  //60秒记录一次
                            {
                                ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "EMB" + (HandleIndex + 1).ToString() + " 发送松开指令 ", "CAN通信");
                            }

                            double ActForce = ClsBitFieldParser.GetClampForce(data[i].frame.data, EMBHandlerToRecvCanForceScale[HandleIndex]);


                            if (ActForce > double.Parse(testConfig.AlertLimit))           //高值告警
                            {
                                AlertStatus[HandleIndex] = 2;

                                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "EMB" + (HandleIndex + 1).ToString() + "超出最大预警值告警！", "告警");
                            }


                            if (_deviceContexts[HandleIndex] != null)
                            {

                                DateTime RecvTime = FirstRecvTime.AddMilliseconds(ClsGlobal.CanRecvTimeSpanMillSecs * (double)(RecvCounter));
                                _deviceContexts[HandleIndex].EnqueueRawData(data[i].frame.data, RecvTime);
                            //    _deviceContexts[HandleIndex].EnqueueStatData(data[i].frame.data, RecvTime);
                                RecvCounter++;


                                if ((CanRecvCounter[HandleIndex] % 6000) == 0)  //60秒记录一次
                                {
                                    ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "EMB" + (HandleIndex + 1).ToString() + " Raw = " + ByteToString(data[i].frame.data), "CAN通信");
                                }


                            }
                            if (CurrentDev == "EMB" + (HandleIndex + 1).ToString())
                            {
                                lock (bufferLock)
                                {
                                    activeWriteBuffer.Enqueue(data[i].frame.data);
                                }
                            }
                        }

                    }
                }



                if (!IsGetCanID && IsAutoLearn && !IsRunning)   //自学习
                {


                    int RecvFrames = 0;

                    for (uint i = 0; i < len; ++i)
                    {
                        uint id = data[i].frame.can_id;

                        if (id == EMBHandlerToRecvFrame[HandleIndex])
                        {
                            RecvFrames++;
                        }
                    }



                    int RecvCounter = 0;

                    for (uint i = 0; i < len; ++i)
                    {
                        uint id = data[i].frame.can_id;

                        if (id == EMBHandlerToRecvFrame[HandleIndex])
                        {

                            double forceValue = ClsBitFieldParser.GetClampForce(data[i].frame.data, EMBHandlerToRecvCanForceScale[HandleIndex]);
                            string autoMsg = "";
                            if (autoLearnDetector != null)
                            {
                                autoMsg = autoLearnDetector.ProcessForceValue(forceValue);
                            }

                            if (autoMsg.IndexOf("OK") < 0)
                            {
                                AlertStatus[HandleIndex] = 3;    //自学习过程中显示黄色灯
                            }
                            else
                            {
                                AlertStatus[HandleIndex] = 0;
                            }

                            CanRecvCounter[HandleIndex]++;

                            if (_deviceContexts[HandleIndex] != null)
                            {
                                RecvCounter++;
                                if ((CanRecvCounter[HandleIndex] % 6000) == 0)  //60秒记录一次
                                {
                                    ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "EMB" + (HandleIndex + 1).ToString() + " Raw = " + ByteToString(data[i].frame.data), "CAN通信");
                                }
                            }
                            if (CurrentDev == "EMB" + (HandleIndex + 1).ToString())
                            {
                                lock (bufferLock)
                                {
                                    activeWriteBuffer.Enqueue(data[i].frame.data);
                                }
                            }
                        }

                    }
                }
            }

            catch (Exception ex)
            {
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "EMB" + (HandleIndex + 1).ToString() + "接收数据出错！" + ex.Message, "接收数据");
            }


        }







        private void ApplyAutoSend(int ChannelIndex)
        {
            string path = ChannelIndex.ToString() + "/apply_auto_send";
            string value = "0";
            uint result = ZlgCanOperation.ZCAN_SetValue(device_handle_, path, Encoding.ASCII.GetBytes(value));  //开启定时发送功能

            if (result == 1)
            {
                // RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "定时报文确认成功！");
                ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, ChannelIndex.ToString() + " 定时报文确认成功!", "CAN 通信");

            }
            else
            {

                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "定时报文确认失败！", "CAN 通信");
            }
        }

        private void ClearAutoSend(int ChannelIndex)
        {
            try
            {
                string path = ChannelIndex.ToString() + "/clear_auto_send";
                string value = "0";
                uint result = ZlgCanOperation.ZCAN_SetValue(device_handle_, path, Encoding.ASCII.GetBytes(value));

                if (result == 1)
                {
                    ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, ChannelIndex.ToString() + " 清除定时报文成功!", "CAN 通信");
                }
                else
                {
                    // RtbError.Invoke(new SetTextCallback(SetErrorText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + ChannelIndex.ToString() + " 暂停定时报文失败！");
                    ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, ChannelIndex.ToString() + " 清除定时报文失败！", "CAN 通信");

                }
            }
            catch (Exception ex)
            {
                // RtbError.Invoke(new SetTextCallback(SetErrorText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + ChannelIndex.ToString() + " 暂停定时报文失败！" + ex.Message);
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, ChannelIndex.ToString() + " 清除定时报文失败！" + ex.Message, "CAN 通信");
            }

        }


        private void SendAutoLearnCommandToDevice(string EmbName)
        {


            uint result = ZlgCanOperation.ZCAN_SetValue(device_handle_, EmbToAutoSendPath[EmbName], EmbToAutoSendPtr[EmbName]);
            if (result == 1)
            {
                ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, EmbName + " 添加自学习指令定时发送成功!", "CAN 通信");
            }
            else
            {
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, EmbName + " 添加自学习指令定时发送失败", "CAN 通信");
            }
        }


       


        private void SendClampCommandToDevice(string EmbName)
        {

            for (int i = 0; i < 20; i++)
            {
                uint result = ZlgCanOperation.ZCAN_SetValue(device_handle_, EmbToAutoSendPath[EmbName], ClampPtr[i]);
                if (result == 1)
                {
                    // RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + EmbName + " 添加定时发送成功！");
                   // ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, EmbName + " 添加夹紧指令定时发送成功!", "CAN 通信");

                }
                else
                {
                    // RtbError.Invoke(new SetTextCallback(SetErrorText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + EmbName + " 添加定时发送失败，请检查设备型号以及当前设备状态！");
                    ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, EmbName + " 添加夹紧指令定时发送失败", "CAN 通信");
                }
            }
        }


        private void SendReleaseCommandToDevice(string EmbName)
        {

            for (int i = 0; i < 20; i++)
            {
                uint result = ZlgCanOperation.ZCAN_SetValue(device_handle_, EmbToCancelPath[EmbName], ReleasePtr[i]);
                if (result == 1)
                {
                  
                }
                else
                {
                    // RtbError.Invoke(new SetTextCallback(SetErrorText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + EmbName + " 添加定时发送失败，请检查设备型号以及当前设备状态！");
                    ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, EmbName + " 添加夹紧指令定时发送失败", "CAN 通信");
                }
            }
        }


      


        private void AssignToAutoLearn(string EmbName)
        {
            try
            {

             
                uint id = EMBNameToSendFrame[EmbName];
                int frame_type_index = ClsGlobal.FrameType;
                int protocol_index = ClsGlobal.Protocol;
                int send_type_index = ClsGlobal.FrameSendType;
                int canfd_exp_index = ClsGlobal.FrameExpType;

                ZCANFD_AUTO_TRANSMIT_OBJ auto_canfd = new ZCANFD_AUTO_TRANSMIT_OBJ();                   //定时发送CAN
                auto_canfd.enable = 1;
                auto_canfd.index = (ushort)ClsGlobal.FrameTimerNo;         //报文索引 1
                auto_canfd.interval = (uint)ClsGlobal.MsgInterval;     //周期单位，ms
                auto_canfd.obj.frame.can_id = MakeCanId(id, frame_type_index, 0, 0);
                auto_canfd.obj.frame.data = new byte[64];
                auto_canfd.obj.frame.len = 16;

                AssignDataToAutoLearn(AutoLearnBytes, ref auto_canfd.obj.frame.data, CANFD_MAX_DLEN);
                auto_canfd.obj.transmit_type = (uint)send_type_index;
                auto_canfd.obj.frame.flags = (byte)((canfd_exp_index != 0) ? CANFD_BRS : 0);

                EmbToAutoSendPath[EmbName] = EmbToChannel[EmbName].ToString() + "/auto_send_canfd";

                EmbToAutoSendPtr[EmbName] = Marshal.AllocHGlobal(Marshal.SizeOf(auto_canfd));
                Marshal.StructureToPtr(auto_canfd, EmbToAutoSendPtr[EmbName], true);

            }
            catch (Exception ex)
            {
                MessageBox.Show("通道 " + EmbToChannel[EmbName].ToString() + " 设置发送数据帧格式失败！" + ex.Message);
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "通道 " + EmbToChannel[EmbName].ToString() + " 设置发送数据帧格式失败！" + ex.Message, "CAN 通信");
            }

        }



        //private void AssignToClamp(string EmbName)
        //{
        //    try
        //    {
        //        uint id = EMBNameToSendFrame[EmbName];

        //        int frame_type_index = ClsGlobal.FrameType;
        //        int protocol_index = ClsGlobal.Protocol;
        //        int send_type_index = ClsGlobal.FrameSendType;
        //        int canfd_exp_index = ClsGlobal.FrameExpType;

        //        ZCANFD_AUTO_TRANSMIT_OBJ auto_canfd = new ZCANFD_AUTO_TRANSMIT_OBJ();                   //定时发送CAN
        //        auto_canfd.enable = 1;
        //        auto_canfd.index = (ushort)ClsGlobal.FrameTimerNo;         //报文索引 1
        //      // auto_canfd.interval = (uint)ClsGlobal.MsgInterval;     //周期单位，ms

        //         auto_canfd.interval = 1000;     //周期单位，ms      //写一个长时间的只执行一次

        //        auto_canfd.obj.frame.can_id = MakeCanId(id, frame_type_index, 0, 0);
        //        auto_canfd.obj.frame.data = new byte[64];
        //        auto_canfd.obj.frame.len = 16;

        //        AssignDataToClamp(ClampBytes, ref auto_canfd.obj.frame.data, CANFD_MAX_DLEN);
        //        auto_canfd.obj.transmit_type = (uint)send_type_index;
        //        auto_canfd.obj.frame.flags = (byte)((canfd_exp_index != 0) ? CANFD_BRS : 0);

        //        EmbToAutoSendPath[EmbName] = EmbToChannel[EmbName].ToString() + "/auto_send_canfd";

        //        EmbToAutoSendPtr[EmbName] = Marshal.AllocHGlobal(Marshal.SizeOf(auto_canfd));
        //        Marshal.StructureToPtr(auto_canfd, EmbToAutoSendPtr[EmbName], true);

        //    }
        //    catch (Exception ex)
        //    {
        //        MessageBox.Show("通道 " + EmbToChannel[EmbName].ToString() + " 设置发送数据帧格式失败！" + ex.Message);
        //        ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "通道 " + EmbToChannel[EmbName].ToString() + " 设置发送数据帧格式失败！" + ex.Message, "CAN 通信");
        //    }

        //}



        private void AssignToClamp(string EmbName)
        {
            try
            {

                ClampPtr.Clear();

                for (int i = 0; i < 20; i++)
                {

                    uint id = EMBNameToSendFrame[EmbName];

                    int frame_type_index = ClsGlobal.FrameType;
                    int protocol_index = ClsGlobal.Protocol;
                    int send_type_index = ClsGlobal.FrameSendType;
                    int canfd_exp_index = ClsGlobal.FrameExpType;


                    ZCANFD_AUTO_TRANSMIT_OBJ auto_canfd = new ZCANFD_AUTO_TRANSMIT_OBJ();                   //定时发送CAN
                    auto_canfd.enable = 1;
                    auto_canfd.index = (ushort)ClsGlobal.FrameTimerNo;         //报文索引 1
                    auto_canfd.interval = (uint)ClsGlobal.MsgInterval;     //周期单位，ms
                    auto_canfd.obj.frame.can_id = MakeCanId(id, frame_type_index, 0, 0);
                    auto_canfd.obj.frame.data = new byte[64];
                    auto_canfd.obj.frame.len = 16;

                    AssignDataToClamp(ClampBytesList[i], ref auto_canfd.obj.frame.data, CANFD_MAX_DLEN);
                    auto_canfd.obj.transmit_type = (uint)send_type_index;
                    auto_canfd.obj.frame.flags = (byte)((canfd_exp_index != 0) ? CANFD_BRS : 0);

                    EmbToAutoSendPath[EmbName] = EmbToChannel[EmbName].ToString() + "/auto_send_canfd";

                    

                    ClampPtr.Add(Marshal.AllocHGlobal(Marshal.SizeOf(auto_canfd)));
                    Marshal.StructureToPtr(auto_canfd, ClampPtr.Last(), true);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("通道 " + EmbToChannel[EmbName].ToString() + " 设置发送数据帧格式失败！" + ex.Message);
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "通道 " + EmbToChannel[EmbName].ToString() + " 设置发送数据帧格式失败！" + ex.Message, "CAN 通信");
            }

        }


        private void AssignToRelease(string EmbName)
        {
            try
            {
                ReleasePtr.Clear();
                for (int i = 0; i < 20; i++)
                {

                    uint id = EMBNameToSendFrame[EmbName];

                    int frame_type_index = ClsGlobal.FrameType;
                    int protocol_index = ClsGlobal.Protocol;
                    int send_type_index = ClsGlobal.FrameSendType;
                    int canfd_exp_index = ClsGlobal.FrameExpType;


                    ZCANFD_AUTO_TRANSMIT_OBJ auto_canfd = new ZCANFD_AUTO_TRANSMIT_OBJ();                   //定时发送CAN
                    auto_canfd.enable = 1;
                    auto_canfd.index = (ushort)ClsGlobal.FrameTimerNo;         //报文索引 1
                    auto_canfd.interval = (uint)ClsGlobal.MsgInterval;     //周期单位，ms

                    // auto_canfd.interval = 1000;     //周期单位，ms      //写一个长时间的只执行一次

                    auto_canfd.obj.frame.can_id = MakeCanId(id, frame_type_index, 0, 0);
                    auto_canfd.obj.frame.data = new byte[64];
                    auto_canfd.obj.frame.len = 16;

                    AssignDataToClamp(ReleaseBytesList[i], ref auto_canfd.obj.frame.data, CANFD_MAX_DLEN);
                    auto_canfd.obj.transmit_type = (uint)send_type_index;
                    auto_canfd.obj.frame.flags = (byte)((canfd_exp_index != 0) ? CANFD_BRS : 0);

                  
                    EmbToCancelPath[EmbName] = EmbToChannel[EmbName].ToString() + "/auto_send_canfd";


                    ReleasePtr.Add(Marshal.AllocHGlobal(Marshal.SizeOf(auto_canfd)));
                    Marshal.StructureToPtr(auto_canfd, ReleasePtr.Last(), true);
                }



                //for (int i = 11; i < 22; i++)
                //{

                //    uint id = EMBNameToSendFrame[EmbName];

                //    int frame_type_index = ClsGlobal.FrameType;
                //    int protocol_index = ClsGlobal.Protocol;
                //    int send_type_index = ClsGlobal.FrameSendType;
                //    int canfd_exp_index = ClsGlobal.FrameExpType;


                //    ZCANFD_AUTO_TRANSMIT_OBJ auto_canfd = new ZCANFD_AUTO_TRANSMIT_OBJ();                   //定时发送CAN
                //    auto_canfd.enable = 1;
                //    auto_canfd.index = (ushort)ClsGlobal.FrameTimerNo;         //报文索引 1
                //                                                               //  auto_canfd.interval = (uint)ClsGlobal.MsgInterval;     //周期单位，ms

                //    auto_canfd.interval = 1000;     //周期单位，ms      //写一个长时间的只执行一次

                //    auto_canfd.obj.frame.can_id = MakeCanId(id, frame_type_index, 0, 0);
                //    auto_canfd.obj.frame.data = new byte[64];
                //    auto_canfd.obj.frame.len = 16;

                //    AssignDataToClamp(ReleaseBytesList[i], ref auto_canfd.obj.frame.data, CANFD_MAX_DLEN);
                //    auto_canfd.obj.transmit_type = (uint)send_type_index;
                //    auto_canfd.obj.frame.flags = (byte)((canfd_exp_index != 0) ? CANFD_BRS : 0);

                //    EmbToAutoSendPath[EmbName] = EmbToChannel[EmbName].ToString() + "/auto_send_canfd";

                //    ReleasePtr.Add(Marshal.AllocHGlobal(Marshal.SizeOf(auto_canfd)));
                //    Marshal.StructureToPtr(auto_canfd, ReleasePtr.Last(), true);
                //}







            }
            catch (Exception ex)
            {
                MessageBox.Show("通道 " + EmbToChannel[EmbName].ToString() + " 设置发送数据帧格式失败！" + ex.Message);
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "通道 " + EmbToChannel[EmbName].ToString() + " 设置发送数据帧格式失败！" + ex.Message, "CAN 通信");
            }

        }

        private void AssignToReleaseV3(string EmbName)
        {
            try
            {
                ReleasePtr.Clear();
                for (int i = 0; i < 1; i++)
                {

                    uint id = EMBNameToSendFrame[EmbName];

                    int frame_type_index = ClsGlobal.FrameType;
                    int protocol_index = ClsGlobal.Protocol;
                    int send_type_index = ClsGlobal.FrameSendType;
                    int canfd_exp_index = ClsGlobal.FrameExpType;


                    ZCANFD_AUTO_TRANSMIT_OBJ auto_canfd = new ZCANFD_AUTO_TRANSMIT_OBJ();                   //定时发送CAN
                    auto_canfd.enable = 1;
                    auto_canfd.index = (ushort)ClsGlobal.FrameTimerNo;         //报文索引 1
                    auto_canfd.interval = (uint)ClsGlobal.MsgInterval;     //周期单位，ms

                    // auto_canfd.interval = 1000;     //周期单位，ms      //写一个长时间的只执行一次

                    auto_canfd.obj.frame.can_id = MakeCanId(id, frame_type_index, 0, 0);
                    auto_canfd.obj.frame.data = new byte[64];
                    auto_canfd.obj.frame.len = 16;

                    AssignDataToClamp(ReleaseBytesList[i], ref auto_canfd.obj.frame.data, CANFD_MAX_DLEN);
                    auto_canfd.obj.transmit_type = (uint)send_type_index;
                    auto_canfd.obj.frame.flags = (byte)((canfd_exp_index != 0) ? CANFD_BRS : 0);


                    EmbToCancelPath[EmbName] = EmbToChannel[EmbName].ToString() + "/auto_send_canfd";
                    ReleasePtr.Add(Marshal.AllocHGlobal(Marshal.SizeOf(auto_canfd)));
                    Marshal.StructureToPtr(auto_canfd, ReleasePtr.Last(), true);
                }



            }
            catch (Exception ex)
            {
                MessageBox.Show("通道 " + EmbToChannel[EmbName].ToString() + " 设置发送数据帧格式失败！" + ex.Message);
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "通道 " + EmbToChannel[EmbName].ToString() + " 设置发送数据帧格式失败！" + ex.Message, "CAN 通信");
            }

        }


       
        public uint MakeCanId(uint id, int eff, int rtr, int err)//1:extend frame 0:standard frame
        {
            uint ueff = (uint)(!!(Convert.ToBoolean(eff)) ? 1 : 0);
            uint urtr = (uint)(!!(Convert.ToBoolean(rtr)) ? 1 : 0);
            uint uerr = (uint)(!!(Convert.ToBoolean(err)) ? 1 : 0);
            return id | ueff << 31 | urtr << 30 | uerr << 29;
        }


        private int AssignDataToRelease(byte[] Inputdata, ref byte[] transData, int maxLen)
        {
            if (FreeClampBytes != null && FreeClampBytes.Length > 0)
            {
                for (int i = 0; (i < maxLen) && (i < Inputdata.Length); i++)
                {
                    transData[i] = Inputdata[i];
                }

                return Inputdata.Length;
            }
            else
            {
                MessageBox.Show("未正确生成指令码!  ", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);

                return 0;
            }
        }

        private int AssignDataToClamp(byte[] Inputdata, ref byte[] transData, int maxLen)
        {
            if (Inputdata != null && Inputdata.Length > 0)
            {
                for (int i = 0; (i < maxLen) && (i < Inputdata.Length); i++)
                {
                    transData[i] = Inputdata[i];
                }

                return Inputdata.Length;
            }
            else
            {
                MessageBox.Show("未正确生成指令码!  ", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);

                return 0;
            }
        }



        private int AssignDataToAutoLearn(byte[] Inputdata, ref byte[] transData, int maxLen)
        {
            if (AutoLearnBytes != null && AutoLearnBytes.Length > 0)
            {
                for (int i = 0; (i < maxLen) && (i < Inputdata.Length); i++)
                {
                    transData[i] = Inputdata[i];
                }

                return Inputdata.Length;
            }
            else
            {
                MessageBox.Show("未正确生成指令码!  ", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);

                return 0;
            }
        }


        private void GenerateAutoLearnCommand()
        {
            try
            {

                var control = new EmbControl
                {
                    setPoint_torque = 0,
                    setPoint_speed = 0,
                    setPoint_position = 0,
                    setPoint_clampForce = 0,
                    operationMod_Req = 0,
                    normalMode = 4,
                    epbClampForceReq = 0,
                    enable = 1
                };

                AutoLearnBytes = ClsZlgCommandMaker.GetEmbControlBytes(control);

                string HexCommand = ByteToString(AutoLearnBytes);
                RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "生成自学习指令 : " + HexCommand);
                ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "生成自学习指令 : " + HexCommand, "打开CAN卡");
            }
            catch (Exception ex)
            {
                MessageBox.Show("生成CAN通信指令失败！" + ex.Message);
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "生成CAN通信指令失败！" + ex.Message, "CAN 通信");
            }
        }


      
    
        
       
       
        private void MakeClampSingle(int MaxClampForce)
        {
            try
            {
                short clampForce = (short)MaxClampForce;
                if (clampForce < 1)
                {
                    clampForce = 0;
                }
                var control = new EmbControl
                {
                    setPoint_torque = (short)ClsGlobal.ClampTorque,
                    setPoint_speed = (short)ClsGlobal.ClampSpeed,
                    setPoint_position = (short)ClsGlobal.ClampPosition,
                    setPoint_clampForce = (short)((short)clampForce / ClsGlobal.SendForceScale),
                    operationMod_Req = (byte)ClsGlobal.ClampModReq,
                    normalMode = (byte)ClsGlobal.ClampNormalMode,
                    epbClampForceReq = (ushort)ClsGlobal.ClampForceReq,
                    enable = (byte)ClsGlobal.ClampEnable
                };

                ClampCommand = ClsZlgCommandMaker.GetEmbControlBytes(control);

                string HexCommand = ByteToString(ClampCommand);

                ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "生成指令 : " + HexCommand, "打开CAN卡");
            }
            catch (Exception ex)
            {
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError,  " 生成CAN指令码失败！" + ex.Message, "CAN 通信");
            }


        }
        private void SendToDevice(string EmbName)
        {
            try
            {
                uint id = EMBNameToSendFrame[EmbName];
                int frame_type_index = ClsGlobal.FrameType;
                int protocol_index = ClsGlobal.Protocol;
                int send_type_index = ClsGlobal.FrameSendType;
                int canfd_exp_index = ClsGlobal.FrameExpType;


                ZCAN_TransmitFD_Data canfd_data = new ZCAN_TransmitFD_Data();
                canfd_data.frame.can_id = MakeCanId(id, frame_type_index, 0, 0);
                canfd_data.frame.data = new byte[64];
                canfd_data.frame.len = 16;
                AssignDataToClamp(ClampCommand, ref canfd_data.frame.data, CANFD_MAX_DLEN);
                canfd_data.transmit_type = (uint)send_type_index;
                canfd_data.frame.flags = (byte)((canfd_exp_index != 0) ? CANFD_BRS : 0);
                IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf(canfd_data));
                Marshal.StructureToPtr(canfd_data, ptr, true);
                uint result = ZlgCanOperation.ZCAN_TransmitFD(device_handle_, ptr, 1);

                if (result == 1)
                {
                    ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, EmbName + " CAN指令发送成功!", "CAN 通信");
                }
                else
                {
                    ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, EmbName + " CAN指令发送失败", "CAN 通信");
                }
                Marshal.FreeHGlobal(ptr);
            }


            catch (Exception ex)
            {
              
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "通道 " + EmbToChannel[EmbName].ToString() + " CAN指令发送失败！" + ex.Message, "CAN 通信");
            }
        }




        private void GenerateClampCommand(double MaxClampForce)
        {
            //产生命令的byte[]
            try
            {

                ClampBytesList.Clear();


                short InitForce = 0;

                short DeltForce = (short)(MaxClampForce / 10.0) ;


                for (int i = 1; i < 11; i++)      //逐步增加   间隔10毫秒持续100毫秒
                {
                    short clampForce = (short)(InitForce + DeltForce*i);
                    if (clampForce > (short)MaxClampForce)
                    {
                        clampForce = (short)MaxClampForce;
                    }

               

                    var control = new EmbControl
                    {
                        setPoint_torque = (short)ClsGlobal.ClampTorque,
                        setPoint_speed = (short)ClsGlobal.ClampSpeed,
                        setPoint_position = (short)ClsGlobal.ClampPosition,
                        setPoint_clampForce = (short)((short)clampForce / ClsGlobal.SendForceScale),
                        operationMod_Req = (byte)ClsGlobal.ClampModReq,
                        normalMode = (byte)ClsGlobal.ClampNormalMode,
                        epbClampForceReq = (ushort)ClsGlobal.ClampForceReq,
                        enable = (byte)ClsGlobal.ClampEnable
                    };

                    byte[] ClampBytes = ClsZlgCommandMaker.GetEmbControlBytes(control);

                    string HexCommand = ByteToString(ClampBytes);
                    ClampBytesList.Add(ClampBytes);
                    //   RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "生成Clamp指令 : " + HexCommand);
                    ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "生成Clamp指令 : " + HexCommand, "打开CAN卡");
                }


                for (int i = 0; i < 10; i++)     //持续的最大值
                {


                 
                    var control = new EmbControl
                    {
                        setPoint_torque = (short)ClsGlobal.ClampTorque,
                        setPoint_speed = (short)ClsGlobal.ClampSpeed,
                        setPoint_position = (short)ClsGlobal.ClampPosition,
                        setPoint_clampForce = (short)(MaxClampForce / ClsGlobal.SendForceScale),
                        operationMod_Req = (byte)ClsGlobal.ClampModReq,
                        normalMode = (byte)ClsGlobal.ClampNormalMode,
                        epbClampForceReq = (ushort)ClsGlobal.ClampForceReq,
                        enable = (byte)ClsGlobal.ClampEnable
                    };

                    byte[] ClampBytes = ClsZlgCommandMaker.GetEmbControlBytes(control);

                    string HexCommand = ByteToString(ClampBytes);
                    ClampBytesList.Add(ClampBytes);
                    //   RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "生成Clamp指令 : " + HexCommand);
                    ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "生成Clamp指令 : " + HexCommand, "打开CAN卡");
                }








            }
            catch (Exception ex)
            {
                MessageBox.Show("生成CAN通信指令失败！" + ex.Message);
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "生成CAN通信指令失败！" + ex.Message, "CAN 通信");
            }
        }

        private void GenerateReleaseCommand(double MaxClampForce)
        {
            //产生命令的byte[]

            try
            {
                ReleaseBytesList.Clear();
                short InitForce = (short)MaxClampForce;

                short DeltForce = (short)(ClsGlobal.ClampForce / 10);

                for (int i = 0; i < 10; i++)
                {
                    short clampForce = (short)(InitForce- DeltForce*i);
                    if (clampForce < 1)
                    {
                        clampForce = 0;
                    }

                  
                    var control = new EmbControl
                    {
                        setPoint_torque = (short)ClsGlobal.ClampTorque,
                        setPoint_speed = (short)ClsGlobal.ClampSpeed,
                        setPoint_position = (short)ClsGlobal.ClampPosition,
                        setPoint_clampForce = (short)((short)clampForce / ClsGlobal.SendForceScale),
                        operationMod_Req = (byte)ClsGlobal.ClampModReq,
                        normalMode = (byte)ClsGlobal.ClampNormalMode,
                        epbClampForceReq = (ushort)ClsGlobal.ClampForceReq,
                        enable = (byte)ClsGlobal.ClampEnable
                    };

                    byte[] ClampBytes = ClsZlgCommandMaker.GetEmbControlBytes(control);

                    string HexCommand = ByteToString(ClampBytes);
                    ReleaseBytesList.Add(ClampBytes);
              
                    ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "生成Release指令 : " + HexCommand, "打开CAN卡");
                }


                for (int i = 0; i < 10; i++)      //保持0
                {


                   
                    var control = new EmbControl
                    {
                        setPoint_torque = (short)ClsGlobal.ClampTorque,
                        setPoint_speed = (short)ClsGlobal.ClampSpeed,
                        setPoint_position = (short)ClsGlobal.ClampPosition,
                        setPoint_clampForce = (short)(ClsGlobal.ReleaseForce / ClsGlobal.SendForceScale),
                        operationMod_Req = (byte)ClsGlobal.ClampModReq,
                        normalMode = (byte)ClsGlobal.ClampNormalMode,
                        epbClampForceReq = (ushort)ClsGlobal.ClampForceReq,
                        enable = (byte)ClsGlobal.ClampEnable
                    };

                    byte[] ClampBytes = ClsZlgCommandMaker.GetEmbControlBytes(control);

                    string HexCommand = ByteToString(ClampBytes);
                    ReleaseBytesList.Add(ClampBytes);
                    //   RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "生成Clamp指令 : " + HexCommand);
                    ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "生成Release指令 : " + HexCommand, "打开CAN卡");
                }



            }
            catch (Exception ex)
            {
                MessageBox.Show("生成CAN通信指令失败！" + ex.Message);
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "生成CAN通信指令失败！" + ex.Message, "CAN 通信");
            }
        }



       




        private string ByteToString(byte[] inputBytes)
        {
            try
            {
                StringBuilder temp = new StringBuilder(2048);
                foreach (byte tempByte in inputBytes)
                {
                    temp.Append(tempByte > 15 ?
                    Convert.ToString(tempByte, 16) : '0' + Convert.ToString(tempByte, 16));
                    temp.Append(' ');
                }
                return temp.ToString().ToUpper();
            }
            catch (Exception ex)
            {
                return ("Error: " + ex.Message).Substring(0, 20);
            }
        }



        #endregion

        public FrmAdjustTorque()
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
                Text = "X",
                Size = new Size(30, 30),
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
                    ReleaseCapture();
                    SendMessage(Handle, 0xA1, 0x2, 0);
                }
            };

        }
        #region 测试使用，正式版删除
        private void StartListen()
        {
            try
            {
                TCP_MainTriggerServer = new AsyncTCPServer(IPAddress.Parse("0.0.0.0"), 8899);
                TCP_MainTriggerServer.Encoding = Encoding.UTF8;
                TCP_MainTriggerServer.ClientConnected +=
                   new EventHandler<AsyncEventArgs>(server_MainTriggerClientConnected);

                TCP_MainTriggerServer.DataReceived += (s, e) =>
                server_MainTriggerTCPTextReceived(s, e, "EMB1");

                //TCP_MainTriggerServer.DataReceived +=
                //   new EventHandler<AsyncEventArgs>(server_MainTriggerTCPTextReceived);

                TCP_MainTriggerServer.Start();

                TCP_SecTriggerServer = new AsyncTCPServer(IPAddress.Parse("0.0.0.0"), 9988);
                TCP_SecTriggerServer.Encoding = Encoding.UTF8;
                TCP_SecTriggerServer.ClientConnected +=
                   new EventHandler<AsyncEventArgs>(server_SecTriggerClientConnected);
                TCP_SecTriggerServer.DataReceived += (s, e) =>
                   server_SecTriggerTCPTextReceived(s, e, "EMB2");

                TCP_SecTriggerServer.Start();


            }

            catch (Exception ex)
            {
                // ClsErrorProc.AddToErrorList(ref LogError, ex.Message, "Start Listen!");
            }

        }


        private void server_MainTriggerClientConnected(object sender, AsyncEventArgs e)
        {
            try
            {


            }
            catch (Exception ex)
            {
                // ClsErrorProc.AddToErrorList(ref LogError, ex.Message, "Main Wifi Connect");
            }

        }



        private void server_SecTriggerClientConnected(object sender, AsyncEventArgs e)
        {
            try
            {
                //  RTB_RawDisp.Invoke(new SetTextCallback(SetErrorText), DateTime.Now.ToString("yyyy-MM-dd:HH:mm:ss  ") + "Second Wifi Connect!");

            }
            catch (Exception ex)
            {
                //  ClsErrorProc.AddToErrorList(ref LogError, ex.Message, "Second Wifi Connect");
            }

        }



        private void server_MainTriggerClientDisConnected(object sender, AsyncEventArgs e)
        {
            try
            {
                //   RTB_RawDisp.Invoke(new SetTextCallback(SetErrorText), DateTime.Now.ToString("yyyy-MM-dd:HH:mm:ss  ") + "Main Wifi DisConnect!");

            }
            catch (Exception ex)
            {
                // ClsErrorProc.AddToErrorList(ref LogError, ex.Message, "Main Wifi DisConnect");
            }

        }


        private void server_SecTriggerClientDisConnected(object sender, AsyncEventArgs e)
        {
            try
            {
                //  RTB_RawDisp.Invoke(new SetTextCallback(SetErrorText), DateTime.Now.ToString("yyyy-MM-dd:HH:mm:ss  ") + "Second Wifi DisConnect!");

            }
            catch (Exception ex)
            {
                // ClsErrorProc.AddToErrorList(ref LogError, ex.Message, "Second Wifi DisConnect");
            }

        }



        private void server_MainTriggerTCPTextReceived(object sender, AsyncEventArgs e, string DevId)
        {
            try
            {
                if (_deviceContexts[0] != null)
                {
                    _deviceContexts[0].EnqueueRawData(e._state.buff, DateTime.Now);

                //    _deviceContexts[0].EnqueueStatData(e._state.buff, DateTime.Now);

                    //  RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + ByteToString(e._state.buff));
                    ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, ByteToString(e._state.buff), "数据接收");
                }
                if (CurrentDev == "EMB1")
                {

                    lock (bufferLock)
                    {
                        activeWriteBuffer.Enqueue(e._state.buff);
                    }
                }

                byte FaultMode = e._state.buff[4];

                if (FaultMode > 0)           //Fault告警
                {
                    AlertStatus[0] = 3;

                    // ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "EMB" + (HandleIndex + 1).ToString() + "自学习进行中！", "自学习");
                }

                else
                {
                    AlertStatus[0] = 0;
                }


            }
            catch (Exception ex)
            {
                // ClsErrorProc.AddToErrorList(ref LogError, ex.Message, "Main Wifi Recv");
            }
        }




        private void server_SecTriggerTCPTextReceived(object sender, AsyncEventArgs e, string DevId)
        {
            try
            {
                if (_deviceContexts[1] != null)
                {
                    _deviceContexts[1].EnqueueRawData(e._state.buff, DateTime.Now);
                  //  _deviceContexts[0].EnqueueStatData(e._state.buff, DateTime.Now);
                }

                // RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + ByteToString(e._state.buff));
                ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, ByteToString(e._state.buff), "数据接收");

                if (CurrentDev == "EMB2")
                {

                    lock (bufferLock)
                    {
                        activeWriteBuffer.Enqueue(e._state.buff);
                    }
                }


            }
            catch (Exception ex)
            {
                //  ClsErrorProc.AddToErrorList(ref LogError, ex.Message, "Main Wifi Recv");
            }
        }









        private void MakeCurveMapping()
        {
            curveDictionary.Clear();
            curveDictionary.TryAdd("Act_Force", new LineItemOperation
            {
                lineItem = curveForce,
                IsActive = true
            });

        

            curveDictionary.TryAdd("DAQ_Torque", new LineItemOperation
            {
                lineItem = curveDaqTorque,
                IsActive = true
            });

           






        }

        private void MakeDirectionMapping()
        {
            DirectionToSendFrame.Clear();
            DirectionToSendFrame["FL"] = (uint)System.Convert.ToInt32(ClsGlobal.FL_Send, 16);
            DirectionToSendFrame["FR"] = (uint)System.Convert.ToInt32(ClsGlobal.FR_Send, 16);
            DirectionToSendFrame["RL"] = (uint)System.Convert.ToInt32(ClsGlobal.RL_Send, 16);
            DirectionToSendFrame["RR"] = (uint)System.Convert.ToInt32(ClsGlobal.RR_Send, 16);


            DirectionToRecvFrame["FL"] = (uint)System.Convert.ToInt32(ClsGlobal.FL_Recv, 16);
            DirectionToRecvFrame["FR"] = (uint)System.Convert.ToInt32(ClsGlobal.FR_Recv, 16);
            DirectionToRecvFrame["RL"] = (uint)System.Convert.ToInt32(ClsGlobal.RL_Recv, 16);
            DirectionToRecvFrame["RR"] = (uint)System.Convert.ToInt32(ClsGlobal.RR_Recv, 16);


        }


        public void UpdateGraphDisplay(DateTime dispTime, List<byte[]> CanData, double[] DaqCurrentData, double[] DaqTorqueData)
        {

            if (!IsRunning && !IsAutoLearn)
            {
                return;
            }


          

            if (zedGraphRealChart == null)
            {
                return;
            }

            if (zedGraphRealChart.InvokeRequired)
            {
                //  zedGraphRealChart.Invoke(new Action<ConcurrentQueue<byte[]>, double[]>(UpdateGraphDisplay), dataQueue, DaqData);
                zedGraphRealChart.Invoke(new Action<DateTime, List<byte[]>, double[], double[]>(UpdateGraphDisplay), dispTime, CanData, DaqCurrentData, DaqTorqueData);
                return;
            }


            try
            {




                foreach (var item in curveDictionary)
                {
                    item.Value.lineItem.IsVisible = item.Value.IsActive;

                    if (item.Key == "Act_Force")
                    {
                        zedGraphRealChart.GraphPane.YAxis.IsVisible = item.Value.IsActive;
                    }
                   
                    if (item.Key == "DAQ_Torque")
                    {
                        zedGraphRealChart.GraphPane.Y2Axis.IsVisible = item.Value.IsActive;
                    }
                   

                }


                double DaqDeltTime = 0.0;
                double CanDeltTime = 0.0;

                double recvSpan = dispTime.Subtract(lastGraphyTime).TotalSeconds;
                double graphyHeadertime = lastGraphyTime.Subtract(runBegin).TotalSeconds;
          
                int DaqLens = DaqCurrentData.Length;
                if (DaqLens > 0)
                {
                    DaqDeltTime = recvSpan / (double)DaqLens;
                }
                bool IsAxisChanged = false;
                for (int i = 0; i < DaqLens; i++)
                {
                  //  listDaqCurrent.Add(graphyHeadertime +ClsGlobal.DaqTimeBias + (double)i * DaqDeltTime, DaqCurrentData[i]);
                    listDaqTorque.Add(graphyHeadertime + ClsGlobal.DaqTimeBias + (double)i * DaqDeltTime, DaqTorqueData[i]);
                    //  currentTime += DaqDeltTime;
                }

                if (listDaqTorque != null && listDaqTorque.Count > 0 && (listDaqTorque[listDaqTorque.Count - 1].X - listDaqTorque[0].X) >= ClsGlobal.XDuration)
                {
                    // 移除最旧的一半数据点

                      double MidTime = (listDaqTorque[0].X + listDaqTorque[listDaqTorque.Count - 1].X) / 2.0;

                  //  double MidTime = (zedGraphRealChart.GraphPane.XAxis.Scale.Max + zedGraphRealChart.GraphPane.XAxis.Scale.Min) / 2.0;



                 //   listDaqCurrent.RemoveAll(p => p.X < MidTime);
                    listDaqTorque.RemoveAll(p => p.X < MidTime);

                    zedGraphRealChart.GraphPane.XAxis.Scale.Max = listDaqTorque[0].X + ClsGlobal.XDuration;
                    zedGraphRealChart.GraphPane.XAxis.Scale.Min = listDaqTorque[0].X;

                    IsAxisChanged = true;


                }


                if (CanData.Count > 0)
                {
                    CanDeltTime = recvSpan / (double)CanData.Count;
                }




                int j = 0;
                // 解析数据并填充曲线
                foreach (var data in CanData)
                {
                    // 假设数据格式：每个数据包包含一个short类型的力值
                    if (data.Length >= 2)
                    {

                       
                        double forceValue = 0;
                        double currentValue = 0;
                        byte faultflg = 0;
                        double torque = 0;
                        string parseMsg = ClsBitFieldParser.ParseClampData(data,
                            EMBNameToRecvCanForceScale[CurrentDev],
                            EMBNameToRecvCanTorqueScale[CurrentDev],
                            EMBNameToRecvCanCurrentScale[CurrentDev],
                            out forceValue, out faultflg, out torque, out currentValue);




                        if (parseMsg.IndexOf("OK") < 0)
                        {
                            ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "CAN数据解析出错: " + parseMsg, "CAN数据解析");
                        }

                        // 添加数据点（X轴为相对时间，单位：秒）
                        listForce.Add(graphyHeadertime + (double)j * CanDeltTime, forceValue);
                       
                        j++;



                    }
                }


                if (listForce != null && listForce.Count > 0 && (listForce[listForce.Count - 1].X - listForce[0].X) >= ClsGlobal.XDuration)
                {
                    // 移除最旧的一半数据点

                      double MidTime = (listForce[0].X + listForce[listForce.Count - 1].X) / 2.0;

                  //  double MidTime = (zedGraphRealChart.GraphPane.XAxis.Scale.Max + zedGraphRealChart.GraphPane.XAxis.Scale.Min) / 2.0;

                    listForce.RemoveAll(p => p.X < MidTime);
                  
                    if (!IsAxisChanged)
                    {
                        zedGraphRealChart.GraphPane.XAxis.Scale.Max = listForce[0].X + ClsGlobal.XDuration;
                        zedGraphRealChart.GraphPane.XAxis.Scale.Min = listForce[0].X;
                        IsAxisChanged = true;
                    }
                }



                double maxTorqueDaq = DaqTorqueData.Length > 0 ? DaqTorqueData.Max() : 0.0;
                double minTorqueDaq = DaqTorqueData.Length > 0 ? DaqTorqueData.Min() : 0.0;


                if (IsRunning)
                {
                    GraphTextLabel.IsVisible = true;
                    GraphTextLabel.Text = maxTorqueDaq.ToString("f1");




                    GraphTextLabel.Location = new Location((zedGraphRealChart.GraphPane.XAxis.Scale.Min + zedGraphRealChart.GraphPane.XAxis.Scale.Max) / 2.0, zedGraphRealChart.GraphPane.YAxis.Scale.Max, CoordType.AxisXYScale);


                    if (Math.Abs(CurrentTarTorque - maxTorqueDaq) < 50.0)
                    {
                        GraphTextLabel.FontSpec.FontColor = Color.LimeGreen;
                        curveDaqTorque.Color = Color.LimeGreen;
                    }
                    else
                    {
                        GraphTextLabel.FontSpec.FontColor = Color.Orange;
                        curveDaqTorque.Color = Color.Orange;
                    }
                }

                else
                {
                    GraphTextLabel.IsVisible = false;
                }



                    lastGraphyTime = dispTime;
                // 刷新图表
                zedGraphRealChart.AxisChange();
                zedGraphRealChart.Invalidate();
            }
            catch (Exception ex)
            {
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "更新曲线显示出错: " + ex.Message, "曲线显示");
                // 记录异常
            }

        }


        public void SafeClearGraphData()
        {
            if (zedGraphRealChart.InvokeRequired)
            {
                zedGraphRealChart.Invoke(new Action(SafeClearGraphData));
                return;
            }

            lock (graphLock)
            {
                // 清空数据列表
                // 重置时间偏移
                // timeOffset = 0;

                // 重置坐标轴范围
                // zedGraphRealChart.GraphPane.XAxis.Scale.Min = 0;
                // zedGraphRealChart.GraphPane.XAxis.Scale.Max = xDuration;

                if (listForce != null && listForce.Count > 0)
                {
                    zedGraphRealChart.GraphPane.XAxis.Scale.Max = listForce[listForce.Count - 1].X + ClsGlobal.XDuration;
                    zedGraphRealChart.GraphPane.XAxis.Scale.Min = listForce[listForce.Count - 1].X;
                    listForce.Clear();

                    // 立即刷新图表
                    zedGraphRealChart.AxisChange();
                    zedGraphRealChart.Invalidate();
                }
            }
        }















        #endregion


        #region  周期定时处理
        // 初始化6个定时器
        private void InitializeEmbControlTimers(uint TimeInterval)
        {
            try
            {
               // EmbControlTimers = new List<TimerState>(1);

                EmbControlTimers.Clear();
                for (int i = 0; i < 1; i++)
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
                MessageBox.Show("初始化定时访问组件失败！" + ex.Message);
            }

        }

        // 启动指定定时器
        public bool StartEmbControlTimer(int EmbIndex)
        {
            try
            {
                if (EmbIndex < 0 || EmbIndex >= 1)
                    return false;

                var timer = EmbControlTimers[EmbIndex];
                if (timer.IsRunning)
                    return true;

                // 首次启动时设置高精度定时器
                if (Interlocked.Increment(ref activeTimersCount) == 1)
                {
                    timeBeginPeriod(DEFAULT_RESOLUTION);
                }

                //  timer.Handler = null;



                if (timer.GcHandle.IsAllocated)
                {
                    timer.GcHandle.Free();
                }

                // 创建新委托并固定
                timer.Handler = new TimerProc(EmbControlTimerHandler);
                timer.GcHandle = GCHandle.Alloc(timer.Handler); // 固定委托



               // timer.Handler = new TimerProc(EmbControlTimerHandler);
                timer.TimerId = timeSetEvent(
                    timer.Interval,
                    DEFAULT_RESOLUTION,
                    timer.Handler,
                    (UIntPtr)EmbIndex,
                    TIMER_PERIODIC
                );

                if (timer.TimerId == 0)
                {
                    MessageBox.Show($"Timer {EmbIndex} failed to start!");
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
                if (index < 0 || index >= 1)
                    return false;


                if (!EmbControlTimers[index].IsRunning)
                    return true;


                timeKillEvent(EmbControlTimers[index].TimerId);

                EmbControlTimers[index].IsRunning = false;
                EmbControlTimers[index].TimerId = 0;

                if (EmbControlTimers[index].GcHandle.IsAllocated)
                {
                    EmbControlTimers[index].GcHandle.Free();
                }



                //   EmbControlTimers[index].Handler = null;

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
        private async void EmbControlTimerHandler(UIntPtr uTimerID, uint uMsg, UIntPtr dwUser, UIntPtr dw1, UIntPtr dw2)
        {
           
            //传入EMB处理的序号
            int index = (int)dwUser.ToUInt32();
            if (index < 0 || index >= 1)
                return;

            if (!IsRunning&&!IsAutoLearn)
            {
                return;
            }


            try
            {
              
                if (!IsAutoLearn)
                {
                    var timer = EmbControlTimers[index];

                    // 使用异步委托
                    Func<System.Threading.Tasks.Task> asyncAction = async () =>
                    {

                        if (ClsGlobal.IsLiner > 0)
                        {
                            short DeltForce = (short)(CurrentClampForce / ClsGlobal.ClampCount);
                            short InitForce = DeltForce;

                            for (int i = 0; i < ClsGlobal.ClampCount; i++)
                            {
                                short clampForce = (short)(InitForce + DeltForce * i);
                                clampForce = clampForce > (short)CurrentClampForce ? (short)CurrentClampForce : clampForce;
                                MakeClampSingle(clampForce);
                                SendToDevice(EmbNoToName[0]);
                                  await System.Threading.Tasks.Task.Delay(ClsGlobal.ClampSpan);
                               // System.Threading.Thread.Sleep(ClsGlobal.ClampSpan);

                            }

                            //以上是夹紧


                             await System.Threading.Tasks.Task.Delay(ClsGlobal.WaitSpanBeforePush);
                           // System.Threading.Thread.Sleep(ClsGlobal.WaitSpanBeforePush);

                            double PushDelt = CurrentPushVoltage / (double)ClsGlobal.PushCount;
                            for (int i = 0; i < ClsGlobal.PushCount; i++)
                            {



                                AdjustPressure(PushDelt * (double)(i + 1));
                                await System.Threading.Tasks.Task.Delay(ClsGlobal.PushSpan);
                              //  System.Threading.Thread.Sleep(ClsGlobal.PushSpan);
                            }

                            bool IsReadDiTimeOut1 = false;
                            ReadDigitalInputWithCondition(testConfig.TestSpan * 1000.0 / 2.0, ReadDiCount, false, "EndPos", out IsReadDiTimeOut1);

                            if (IsReadDiTimeOut1)
                            {
                                RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + " 等待到终末位置超时!");
                                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "等待开关到终末位置超时!", "试验循环");
                            }
                            else
                            {

                                ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "正常到达远端!", "试验循环");

                            }

                            for (int i = 0; i < ClsGlobal.PushCount; i++)
                            {
                                // RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "Push = " + (CurrentPushVoltage - PushDelt * (double)(i + 1)).ToString("f2"));

                                AdjustPressure(CurrentPushVoltage - PushDelt * (double)(i + 1));
                                await System.Threading.Tasks.Task.Delay(ClsGlobal.PushSpan);
                              //  System.Threading.Thread.Sleep(ClsGlobal.PushSpan);
                            }






                        }
                        else
                        {
                            short InitForce = (short)(CurrentClampForce / Math.Pow(2.0, (double)(ClsGlobal.ClampCount - 1)) + 1.0);

                            for (int i = 0; i < ClsGlobal.ClampCount; i++)
                            {
                                short clampForce = (short)(InitForce * Math.Pow(2.0, (double)i));

                                clampForce = clampForce > (short)CurrentClampForce ? (short)CurrentClampForce : clampForce;
                                MakeClampSingle(clampForce);
                                SendToDevice(EmbNoToName[0]);
                                 await System.Threading.Tasks.Task.Delay(ClsGlobal.ClampSpan);
                               // System.Threading.Thread.Sleep(ClsGlobal.ClampSpan);

                            }

                            //以上是夹紧
                             await System.Threading.Tasks.Task.Delay(ClsGlobal.WaitSpanBeforePush);
                           // System.Threading.Thread.Sleep(ClsGlobal.WaitSpanBeforePush);

                            double InitPush = CurrentPushVoltage / Math.Pow(2.0, (double)(ClsGlobal.PushCount - 1));
                            for (int i = 0; i < ClsGlobal.PushCount; i++)
                            {
                                double currentPush = InitPush * Math.Pow(2.0, (double)i);

                                currentPush = currentPush > CurrentPushVoltage ? CurrentPushVoltage : currentPush;



                                AdjustPressure(currentPush);
                                await System.Threading.Tasks.Task.Delay(ClsGlobal.PushSpan);
                               // System.Threading.Thread.Sleep(ClsGlobal.PushSpan);
                            }

                            bool IsReadDiTimeOut2 = false;
                            ReadDigitalInputWithCondition(testConfig.TestSpan * 1000.0 / 2.0, ReadDiCount, false, "EndPos", out IsReadDiTimeOut2);

                            if (IsReadDiTimeOut2)
                            {

                                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "等待开关到终末位置超时!", "试验循环");
                            }
                            else
                            {

                                ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "正常到达远端!", "试验循环");

                            }

                            for (int i = 0; i < ClsGlobal.PushCount; i++)
                            {

                                double currentPush = CurrentPushVoltage / Math.Pow(2.0, (double)(i + 1));
                                currentPush = currentPush < 0.5 ? 0 : currentPush;
                                //  RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "Push = " + currentPush.ToString("f2"));

                                AdjustPressure(currentPush);
                                  await System.Threading.Tasks.Task.Delay(ClsGlobal.PushSpan);
                               // System.Threading.Thread.Sleep(ClsGlobal.PushSpan);
                            }

                        }

                        //以上是向前推

                        timer.CycleCounter[index]++;

                        if ((timer.CycleCounter[index] % 10) == 0)  //循环10次记录一次
                        {
                            ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "EMB" + (index + 1).ToString() + " 发送夹紧指令 ", "CAN通信");
                        }

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
                        }




                        //   await Release();

                        await System.Threading.Tasks.Task.Delay(ClsGlobal.ReleaseWaitSpan);

                       // System.Threading.Thread.Sleep(ClsGlobal.ReleaseWaitSpan);


                        if (ClsGlobal.ValveMode == 0)
                        {
                            bool OpenSuccess = await OpenSerialChannel((byte)ClsGlobal.DirectionValveChannel, ClsGlobal.SerialPortRetrys);
                            if (!OpenSuccess)
                            {
                                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError,
                                    $"通道打开失败！通道号：{ClsGlobal.DirectionValveChannel}",
                                    "硬件操作");
                                // return;
                            }

                             await System.Threading.Tasks.Task.Delay(ClsGlobal.WaitValveChangeFinishSpan);
                            //System.Threading.Thread.Sleep(ClsGlobal.WaitValveChangeFinishSpan);

                        }
                        else
                        {

                            SafeWriteDo(true);
                             await System.Threading.Tasks.Task.Delay(ClsGlobal.WaitValveChangeFinishSpan);
                           // System.Threading.Thread.Sleep(ClsGlobal.WaitValveChangeFinishSpan);
                        }





                        if (ClsGlobal.IsLiner > 0)
                        {

                            double deltForce = CurrentClampForce / (double)ClsGlobal.ReleaseCount;

                            for (int i = 0; i < ClsGlobal.ReleaseCount; i++)
                            {
                                short clampForce = (short)(CurrentClampForce - (double)(i + 1) * deltForce);

                                if (clampForce < 1)
                                {
                                    clampForce = 0;
                                }



                                MakeClampSingle(clampForce);
                                SendToDevice(EmbNoToName[0]);

                                 await System.Threading.Tasks.Task.Delay(ClsGlobal.ReleaseSpan);
                               // System.Threading.Thread.Sleep(ClsGlobal.ReleaseSpan);

                            }
                        }

                        else
                        {

                            for (int i = 0; i < ClsGlobal.ReleaseCount; i++)
                            {
                                short clampForce = (short)(CurrentClampForce / Math.Pow(2.0, (double)(i + 1)));
                                if (clampForce < 1000)
                                {
                                    clampForce = 0;
                                }

                                MakeClampSingle(clampForce);
                                SendToDevice(EmbNoToName[0]);

                                  await System.Threading.Tasks.Task.Delay(ClsGlobal.ReleaseSpan);
                               // System.Threading.Thread.Sleep(ClsGlobal.ReleaseSpan);

                            }
                        }

                        lock (clampCounterLocks[index])
                        {
                                releaseFailureCounters[index] = 0;
                        }

                        //打开换向阀往回走
                          await System.Threading.Tasks.Task.Delay(ClsGlobal.ReleaseWaitSpan);
                        //  await Back();


                        AdjustPressure(ClsGlobal.ReleaseAoVol);   //调压

                        bool IsReadDiTimeOut3 = false;

                        ReadDigitalInputWithCondition(testConfig.TestSpan * 1000.0 / 2.0, ReadDiCount, false, "StartPos", out IsReadDiTimeOut3);

                        if (IsReadDiTimeOut3)
                        {
                            RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "等待开关到初始位置超时!");

                            ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError,
                                        "等待开关到初始位置超时!",
                                        "试验循环");

                        }

                        else
                        {

                            ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "正常回到起点!", "试验循环");


                        }



                        AdjustPressure(0.0);   //调压


                        if (ClsGlobal.ValveMode == 0)
                        {
                            // 关闭换向阀并等待完成
                            bool closeSuccess = await CloseSerialChannel(
                            (byte)ClsGlobal.DirectionValveChannel,
                            ClsGlobal.SerialPortRetrys
                        );
                          //  await System.Threading.Tasks.Task.Delay(ClsGlobal.WaitValveGoBack);
                            if (!closeSuccess)
                            {
                                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError,
                                    $"通道关闭失败！通道号：{ClsGlobal.DirectionValveChannel}",
                                    "硬件操作");
                                // return;
                            }
                        }

                        else
                        {
                            SafeWriteDo(false);
                           // await System.Threading.Tasks.Task.Delay(ClsGlobal.WaitValveGoBack);
                        }











                    };

                    // 异步跨线程调用处理
                    if (InvokeRequired)
                    {
                         await (System.Threading.Tasks.Task)Invoke(asyncAction);
                        //  (System.Threading.Tasks.Task)Invoke(asyncAction);

                      //  Invoke(asyncAction);

                    }
                    else
                    {
                        await asyncAction();
                    }
                }














                else        //自学习过程
                {

                    var timer = EmbControlTimers[index];

                    Action action = () =>
                    {

                        ClearAutoSend(EmbNoToChannel[index]);
                        SendAutoLearnCommandToDevice(EmbNoToName[index]);
                        ApplyAutoSend(EmbNoToChannel[index]);
                        timer.CycleCounter[index]++;

                        if ((timer.CycleCounter[index] % 6000) == 0)  //60秒记录一次
                        {
                            ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "EMB" + (index + 1).ToString() + " 发送自学习指令 ", "CAN通信");
                        }
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
            catch(Exception ex)
            {
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "EMB" + (index + 1).ToString() + "定时发送指令出错！"+ex.Message, "定时发送指令");
            }
        }

        // 停止所有定时器
        public void StopAllEmbControlTimers()
        {
            for (int i = 0; i < 1; i++)
            {
                StopEmbControlTimer(i);
            }
        }

        // 设置定时器间隔
        public bool SetEmbControlTimerInterval(int index, uint newInterval)
        {
            if (index < 0 || index >= 1)
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
            if (index < 0 || index >= 1)
                return "Invalid index";

            var timer = EmbControlTimers[index];
            return $"Timer {index}: {(timer.IsRunning ? "▶ Running" : "⏹ Stopped")}\n" +
                   $"Interval: {timer.Interval}ms\n" +
                   $"Counter: {timer.CycleCounter[index]}";
        }

        #endregion 


        #region 曲线处理
        private void InitializeCurve()
        {
            try
            {
                int fontSize = 12;

                // 保留原有初始化代码
                GraphPane pane = zedGraphRealChart.GraphPane;
                // 设置 X 轴和 Y 轴以及刻度线为灰色


                pane.XAxis.Color = Color.Gray;
                pane.XAxis.MajorTic.Color = Color.Gray;
                pane.XAxis.MinorTic.Size = 0.0f;

                pane.YAxis.Color = Color.Gray;
                pane.YAxis.MajorTic.Color = Color.Gray;
                pane.YAxis.MinorTic.Size = 0.0f;



                pane.Title.IsVisible = false;
                pane.XAxis.Title.Text = "Time";
                pane.YAxis.Title.IsVisible = false;
                pane.XAxis.Title.IsVisible = false;


               

                pane.Fill = new Fill(Color.FromArgb(255, 255, 255));
                pane.Chart.Fill = new Fill(Color.FromArgb(248, 248, 248));





                pane.Chart.Border.IsVisible = false;
                //边框不可见，若可见不显示坐标轴颜色

                // 设置图例背景色和曲线区域一致
                pane.Legend.Fill = new Fill(Color.FromArgb(255, 255, 255));



                // 设置图例字体为白色，不显示边框
                pane.Legend.FontSpec.FontColor = Color.FromArgb(80, 160, 255);
                pane.Legend.FontSpec.Size = fontSize;
                pane.Legend.Border.IsVisible = false;


                //   pane.XAxis.Type = AxisType.Date;
                //   pane.XAxis.Scale.Format = "HH:mm:ss";

                pane.XAxis.Type = AxisType.Linear;
                // pane.XAxis.Scale.Format = "HH:mm:ss";


                pane.XAxis.Title.FontSpec.FontColor = Color.FromArgb(80, 160, 255);
                pane.XAxis.Scale.FontSpec.FontColor = Color.FromArgb(80, 160, 255);

                // 设置 X 轴和 Y 轴的网格线为实线且可见
                pane.XAxis.MajorGrid.IsVisible = true;
                pane.XAxis.MajorGrid.Color = Color.Gray;
                pane.XAxis.MajorGrid.DashOn = float.MaxValue; // 设置为实线
                pane.XAxis.MajorGrid.DashOff = 0;

                // 调小坐标轴文字字体大小
                pane.XAxis.Title.FontSpec.Size = fontSize;
                pane.XAxis.Scale.FontSpec.Size = fontSize;



                pane.YAxis.Title.FontSpec.FontColor = Color.FromArgb(80, 160, 255);
                pane.YAxis.Scale.FontSpec.FontColor = Color.FromArgb(80, 160, 255);
                pane.YAxis.Title.FontSpec.Size = fontSize;
                pane.YAxis.Scale.FontSpec.Size = fontSize;
                pane.YAxis.MajorGrid.IsVisible = true;
                pane.YAxis.MajorGrid.Color = Color.Gray;
                pane.YAxis.MajorGrid.DashOn = float.MaxValue;
                pane.YAxis.MajorGrid.DashOff = 0;

                pane.YAxis.MajorTic.Size = 0.0f;
                pane.YAxis.MinorTic.Size = 0.0f;
                pane.YAxis.MajorTic.IsOpposite = false;



                pane.Y2Axis.IsVisible = true;
                pane.Y2Axis.Title.FontSpec.FontColor = Color.Orange;
                pane.Y2Axis.Color = Color.Orange;
                pane.Y2Axis.Scale.FontSpec.FontColor = Color.Orange;
                pane.Y2Axis.Title.FontSpec.Size = fontSize;
                pane.Y2Axis.Scale.FontSpec.Size = fontSize;
                pane.Y2Axis.MajorGrid.IsVisible = false;
                pane.Y2Axis.MajorGrid.IsZeroLine = false;

                pane.Y2Axis.MajorTic.Size = 0.0f;
                pane.Y2Axis.MinorTic.Size = 0.0f;


               


             











                listForce = new PointPairList();
                curveForce = pane.AddCurve("Act_Force(N)", listForce, Color.FromArgb(80, 160, 255), SymbolType.None);
                curveForce.Line.Width = 2;
                curveForce.YAxisIndex = 0;
                curveForce.IsY2Axis = false;


                listDaqTorque = new PointPairList();
                curveDaqTorque = pane.AddCurve("DAQ_Torque(Nm)", listDaqTorque, Color.Orange, SymbolType.None);
                curveDaqTorque.Line.Width = 2;
                curveDaqTorque.YAxisIndex = pane.Y2AxisList.Count - 1;
                curveDaqTorque.IsY2Axis = true; // 


               
               


               

                








               

                zedGraphRealChart.GraphPane.XAxis.Scale.Max = ClsGlobal.XDuration;
                zedGraphRealChart.GraphPane.XAxis.Scale.Min = 0.0;


                zedGraphRealChart.GraphPane.XAxis.Scale.MagAuto = false;
                zedGraphRealChart.GraphPane.XAxis.Scale.FormatAuto = false;
                zedGraphRealChart.GraphPane.YAxis.Scale.MagAuto = false;
                zedGraphRealChart.GraphPane.YAxis.Scale.FormatAuto = false;

                zedGraphRealChart.GraphPane.Y2Axis.Scale.MagAuto = false;
                zedGraphRealChart.GraphPane.Y2Axis.Scale.FormatAuto = false;



                GraphTextLabel = new TextObj("0.00", 2, 1, CoordType.AxisXYScale, AlignH.Center, AlignV.Center)
                {
                    FontSpec = new FontSpec("Arial", 32, Color.Orange, true, false, false)
                    {
                        Border = new Border(Color.Transparent, 1),
                        Fill = new Fill(Color.Transparent)
                    },
                    Location = { CoordinateFrame = CoordType.AxisXYScale }
                };
                pane.GraphObjList.Add(GraphTextLabel);


                GraphTextLabel.Text = "";
                GraphTextLabel.Location = new Location((zedGraphRealChart.GraphPane.XAxis.Scale.Min + zedGraphRealChart.GraphPane.XAxis.Scale.Max) / 2.0, zedGraphRealChart.GraphPane.YAxis.Scale.Max, CoordType.AxisXYScale);










                zedGraphRealChart.AxisChange();
                zedGraphRealChart.Invalidate();
                zedGraphRealChart.Refresh();
            }

            catch (Exception ex)
            { 
                MessageBox.Show("初始化曲线显示失败！"+ex.Message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "初始化曲线显示失败！" +ex.Message, "初始化");

            }
        }


        private void ResetDisplaySystem()
        {
            lock (graphLock)
            {
                listForce.Clear();
                bufferA.Clear();
                bufferB.Clear();
                activeWriteBuffer = bufferA;
                readyReadBuffer = bufferB;
                InitializeCurve();
                zedGraphRealChart.Invalidate();
            }
        }


      
        private void DisplayCallback(object state)
        {
            try
            {
                DateTime dispTime;
                // 快速提取数据（最小化锁范围）
                lock (bufferLock)
                {
                    // 交换缓冲区
                    (activeWriteBuffer, readyReadBuffer) = (readyReadBuffer, activeWriteBuffer);
                    // 创建数据快照
                   
                    forceSnapshot = readyReadBuffer.ToList();
                    dispTime = DateTime.Now;
                    daqCurrentSnapshot = ProcessDaqCurrentData();
                    daqTorqueSnapshot = ProcessDaqTorqueData();

                    activeWriteBuffer.Clear();
                }

                // UI更新（独立锁）
                if (Monitor.TryEnter(graphLock, 1000))
                {
                    try
                    {
                        UpdateGraphDisplay(dispTime,forceSnapshot, daqCurrentSnapshot, daqTorqueSnapshot);
                        
                    }
                    finally
                    {
                        Monitor.Exit(graphLock);
                    }
                }
            }
            catch (Exception ex)
            {
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "定时刷新数据曲线出错: " + ex.Message, "定时刷新数据曲线");
            }
        }

        private double[] ProcessDaqCurrentData()
        {
            int RecCount = DaqAiCurrentDispData.Count;
            int totalCount = DaqAiCurrentDispData.Take(RecCount).Sum(arr => arr.Length);

       
            double[] result = new double[totalCount];
            int index = 0;
            int RecCounter = 0;
            while (DaqAiCurrentDispData.TryDequeue(out var arr)&& RecCounter < RecCount)
            {
                Array.Copy(arr, 0, result, index, arr.Length);
                index += arr.Length;
                RecCounter++;
            }

            for (int i = 0; i < totalCount; i++)
            {
                result[i] = (result[i] - ParaNameToZeroValue["EMB1_current"]) * ParaNameToScale["EMB1_current"] + ParaNameToOffset["EMB1_current"];
            }

            double[] filterCurrent = ClsDataFilter.MakeMedianFilterReducePoint(ref result, ClsGlobal.MedianLens);


            return filterCurrent;
        }

        private double[] ProcessDaqTorqueData()
        {
            int RecCount = DaqAiTorqueDispData.Count;
            int totalCount = DaqAiTorqueDispData.Take(RecCount).Sum(arr => arr.Length);
            double[] result = new double[totalCount];
            int index = 0;
            int RecCounter = 0;
            while (DaqAiTorqueDispData.TryDequeue(out var arr) && RecCounter < RecCount)
            {
                Array.Copy(arr, 0, result, index, arr.Length);
                index += arr.Length;
            }

            for (int i = 0; i < totalCount; i++)
            {
                result[i] = (result[i] - ParaNameToZeroValue["EMB1_torque"]) * ParaNameToScale["EMB1_torque"] + ParaNameToOffset["EMB1_torque"];
            }

            double[] filterTorque = ClsDataFilter.MakeMedianFilterReducePoint(ref result, ClsGlobal.MedianLens);


            return filterTorque;
        }





        #endregion


        public void LoadCanDbc()
        {
            foreach (var frame in EMBHandlerToSendFrame)
            {
                double SendFactor = 0.0;
                double SendOffset = 0.0;
                string DbcMsg = DbcParser.TryGetFactorOffset(ClsGlobal.Dbc, frame.Value, "setPoint_clampForce", out SendFactor, out SendOffset);
                if (DbcMsg.IndexOf("OK") < 0)
                {
                    MessageBox.Show(DbcMsg);
                    return;
                }
                EMBHandlerToSendCanForceScale[frame.Key] = SendFactor;
                EMBHandlerToSendCanForceOffset[frame.Key] = SendOffset;
            }

            foreach (var frame in EMBHandlerToRecvFrame)
            {
                double SendFactor = 0.0;
                double SendOffset = 0.0;
                string DbcMsg = DbcParser.TryGetFactorOffset(ClsGlobal.Dbc, frame.Value, "actClampForce", out SendFactor, out SendOffset);
                if (DbcMsg.IndexOf("OK") < 0)
                {
                    MessageBox.Show(DbcMsg);
                    return;
                }
                EMBHandlerToRecvCanForceScale[frame.Key] = SendFactor;
                EMBHandlerToRecvCanForceOffset[frame.Key] = SendOffset;

                string EmbName = "EMB" + (frame.Key + 1).ToString();

                EMBNameToRecvCanForceScale[EmbName] = SendFactor;
                EMBNameToRecvCanForceOffset[EmbName] = SendOffset;
            }


            foreach (var frame in EMBHandlerToRecvFrame)
            {
                double SendFactor = 0.0;
                double SendOffset = 0.0;
                string DbcMsg = DbcParser.TryGetFactorOffset(ClsGlobal.Dbc, frame.Value, "dcCurrent", out SendFactor, out SendOffset);
                if (DbcMsg.IndexOf("OK") < 0)
                {
                    MessageBox.Show(DbcMsg);
                    return;
                }
                EMBHandlerToRecvCanCurrentScale[frame.Key] = SendFactor;
                EMBHandlerToRecvCanCurrentOffset[frame.Key] = SendOffset;

                string EmbName = "EMB" + (frame.Key + 1).ToString();

                EMBNameToRecvCanCurrentScale[EmbName] = SendFactor;
                EMBNameToRecvCanCurrentOffset[EmbName] = SendOffset;


            }

            foreach (var frame in EMBHandlerToRecvFrame)
            {
                double SendFactor = 0.0;
                double SendOffset = 0.0;
                string DbcMsg = DbcParser.TryGetFactorOffset(ClsGlobal.Dbc, frame.Value, "actTorque", out SendFactor, out SendOffset);
                if (DbcMsg.IndexOf("OK") < 0)
                {
                    MessageBox.Show(DbcMsg);
                    return;
                }
                EMBHandlerToRecvCanTorqueScale[frame.Key] = SendFactor;
                EMBHandlerToRecvCanTorqueOffset[frame.Key] = SendOffset;

                string EmbName = "EMB" + (frame.Key + 1).ToString();

                EMBNameToRecvCanTorqueScale[EmbName] = SendFactor;
                EMBNameToRecvCanTorqueOffset[EmbName] = SendOffset;

            }


        }


        private void InitDaqLogTimer(int LogSpan)
        {
            DaqContext1 = new DaqAIContext("Dev1", 100, ClsGlobal.FileChangeMinutes, DaqTimeSpanMilSeconds, Dev1UsedDaqAIChannels.Length, ClsGlobal.SamplesPerChannel, testConfig.StoreDir);
          
            DaqLogtimer1 = new System.Threading.Timer(async _ =>
            {
                try
                {
                    await DaqContext1.FlushRawToDiskAsync();
                }
                catch (Exception ex)
                {
                    BeginInvoke((Action)(() =>
                        MessageBox.Show($"DAQ Dev1 error: {ex.Message}")));
                }
            }, null, LogSpan, LogSpan);


            

        }


    


        private void InitCanRawLogTimer(int LogSpan)
        {
            for (int i = 0; i < DeviceCount; i++)
            {
                _deviceContexts[i] = new DeviceContext(i, CacheLens, ClsGlobal.CanRecvTimeSpanMillSecs, ClsGlobal.FileChangeMinutes, testConfig.StoreDir, ClsGlobal.RecvCanLens);

                _deviceContexts[i].eMBHandlerToRecvCanForceScale = EMBHandlerToRecvCanForceScale;
                _deviceContexts[i].eMBHandlerToRecvCanForceOffset = EMBHandlerToRecvCanForceOffset;
                _deviceContexts[i].eMBHandlerToRecvCanCurrentScale = EMBHandlerToRecvCanCurrentScale;
                _deviceContexts[i].eMBHandlerToRecvCanCurrentOffset = EMBHandlerToRecvCanCurrentOffset;
                _deviceContexts[i].eMBHandlerToRecvCanTorqueScale = EMBHandlerToRecvCanTorqueScale;
                _deviceContexts[i].eMBHandlerToRecvCanTorqueOffset = EMBHandlerToRecvCanTorqueOffset;


            }



            if (EmbGroup[0].IsEnabel)
            {
                _logtimers[0] = new System.Threading.Timer(async _ =>
                {
                    try
                    {
                        await _deviceContexts[0].FlushRawToDiskAsync();
                    }
                    catch (Exception ex)
                    {
                        BeginInvoke((Action)(() =>
                            MessageBox.Show($"Device 0 error: {ex.Message}")));
                    }
                }, null, LogSpan, LogSpan);



                //_logtimers[1] = new System.Threading.Timer(async _ =>
                //{
                //    try
                //    {
                //        await _deviceContexts[0].FlushStatToDiskAsync();
                //    }
                //    catch (Exception ex)
                //    {
                //        BeginInvoke((Action)(() =>
                //            MessageBox.Show($"Device 0 error: {ex.Message}")));
                //    }
                //}, null, LogSpan, LogSpan);




            }
        }


        private void FrmMainMonitor_Load(object sender, EventArgs e)
        {
            try
            {

             
                DaqTimeSpanMilSeconds = 1000.0 / ClsGlobal.DaqFrequency;
                activeWriteBuffer = bufferA;
                readyReadBuffer = bufferB;
                
                string ReadMsg = ClsXmlOperation.ReadCanNameChannelToDictionary(System.Environment.CurrentDirectory + @"\Config\CanChannel.xml", out EmbToChannel);
                if (ReadMsg.IndexOf("OK") < 0)
                {
                    MessageBox.Show(ReadMsg);
                    return;
                }

                if (EmbToChannel.Count < 1)
                {
                    MessageBox.Show("未读取到EMB和CAN的关联关系！");
                    return;
                }

                 ReadMsg = ClsXmlOperation.GetDaqAIUsedChannels(System.Environment.CurrentDirectory + @"\Config\AIConfig.xml", "Dev1",out Dev1UsedDaqAIChannels);
                if (ReadMsg.IndexOf("OK") < 0)
                {
                    MessageBox.Show(ReadMsg);
                    return;
                }

                if (Dev1UsedDaqAIChannels.Length < 1)
                {
                    MessageBox.Show("未读取到 Dev1 DAQ AI 相关信息！");
                    return;
                }


                 ReadMsg = ClsXmlOperation.GetDaqPhyChanelToNameMapping(System.Environment.CurrentDirectory + @"\Config\AIConfig.xml", "Dev1", out PhyChannelToParaName);
                if (ReadMsg.IndexOf("OK") < 0)
                {
                    MessageBox.Show(ReadMsg);
                    return;
                }

               
                for (int i=0;i< Dev1UsedDaqAIChannels.Length;i++)
                {
                    ParaNameToActChannel.TryAdd(PhyChannelToParaName[Dev1UsedDaqAIChannels[i]], i);
                }


               

               


              

              

                ReadMsg = ClsXmlOperation.GetDaqScaleMapping(System.Environment.CurrentDirectory + @"\Config\AIConfig.xml", "Dev1", out ParaNameToScale);
                if (ReadMsg.IndexOf("OK") < 0)
                {
                    MessageBox.Show(ReadMsg);
                    return;
                }

                ReadMsg = ClsXmlOperation.GetDaqOffsetMapping(System.Environment.CurrentDirectory + @"\Config\AIConfig.xml", "Dev1", out ParaNameToOffset);
                if (ReadMsg.IndexOf("OK") < 0)
                {
                    MessageBox.Show(ReadMsg);
                    return;
                }

                ReadMsg = ClsXmlOperation.GetDaqZeroValueMapping(System.Environment.CurrentDirectory + @"\Config\AIConfig.xml", "Dev1", out ParaNameToZeroValue);
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
                    EmbNoToChannel[handleNo] = EmbToChannel[key];         //处理顺序和波道对应
                    EmbNoToName[handleNo] = key;
                }

                //给处理序号和通道号字典赋值



                LoadEmbControler();

               
                InitializeCurve();
                StartListen();
                MakeCurveMapping();
                MakeDirectionMapping();

                ReadMsg = LoadTestConfigFromXml();
                if (ReadMsg.IndexOf("OK") < 0)
                {
                    MessageBox.Show(ReadMsg);
                    return;
                }


                LoadEMBHandlerAndFrameNo();


                ReadMsg = InitDIDaq();
                if (ReadMsg.IndexOf("OK") < 0)
                {
                    MessageBox.Show(ReadMsg);
                    return;
                }




                //    InitializeAoTask();


               

                RtbInfo.Invoke(new SetTextCallback(SetInfoText),  "1. 启动");
                RtbInfo.Invoke(new SetTextCallback(SetInfoText), "2. 调节夹紧力和推力等级");
                RtbInfo.Invoke(new SetTextCallback(SetInfoText), "3. 达到设定扭矩");
                RtbInfo.Invoke(new SetTextCallback(SetInfoText), "4. 停止");




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



                SetVppmWorkMode(ClsGlobal.VppmWorkMode);



                

                InitializeAoTask();

                InitializeDoTask();

                InitMinitorTimer();

                InitWriteStatTimer();


                LoadCanDbc();


              



            }

            catch(Exception ex)
            {
                MessageBox.Show("初始化错误 : " + ex.Message);
            }

            


        }

        private void timerProgressDisp_Tick(object sender, EventArgs e)
        {

            if (!IsRunning && !IsAutoLearn)
            {
                return;
            }
            int currentDevIndex = int.Parse(CurrentDev.Replace("EMB", "")) - 1;

            
           
           

            

            for (int i = 0; i < 1; i++)
            {
                if (EmbGroup[i].IsEnabel)
                {
                    if (AlertStatus[i] > 0&& AlertStatus[i] < 3)   //连续夹紧和高值告警
                    {
                        EmbGroup[i].CtrlAlert.OffCenterColor = Color.FromArgb(140, 140, 140);
                        EmbGroup[i].CtrlAlert.OffColor = Color.FromArgb(140, 140, 140);
                        EmbGroup[i].CtrlAlert.OnCenterColor = Color.Red;
                        EmbGroup[i].CtrlAlert.OnColor = Color.Red;
                        EmbGroup[i].CtrlAlert.State = UILightState.Blink;
                    }

                    else if (AlertStatus[i] == 3)   //自学习未结束
                    {
                        EmbGroup[i].CtrlAlert.OffCenterColor = Color.FromArgb(140, 140, 140);
                        EmbGroup[i].CtrlAlert.OffColor = Color.FromArgb(140, 140, 140);
                        EmbGroup[i].CtrlAlert.OnCenterColor = Color.Orange;
                        EmbGroup[i].CtrlAlert.OnColor = Color.Orange;
                        EmbGroup[i].CtrlAlert.State = UILightState.Blink;
                    }


                    else   //正常
                    {
                        EmbGroup[i].CtrlAlert.OffCenterColor = Color.FromArgb(140, 140, 140);
                        EmbGroup[i].CtrlAlert.OffColor = Color.FromArgb(140, 140, 140);
                        EmbGroup[i].CtrlAlert.OnCenterColor = Color.Lime;
                        EmbGroup[i].CtrlAlert.OnColor = Color.Lime;
                        EmbGroup[i].CtrlAlert.State = UILightState.Blink;
                    }
                }
            }

               

         

        }
    
        private void LoadEmbControler()
        {
            try
            {
                for (int i = 0; i < 1; i++)
                {
                    EmbGroup[i] = new ClsEMBControler();
                    EmbGroup[i].EmbNo = i+1;
                    EmbGroup[i].EmbName = "EMB" +( i + 1).ToString();
                  //  EmbGroup[i].Cycles = 0;
                   EmbGroup[i].IsEnabel = true;
                

                }

           
                EmbGroup[0].CtrlAlert = AlertEmb1;

            }
            catch(Exception ex)
            {
                MessageBox.Show("初始化组件失败！" + ex.Message);
            }
        }

       

       
        
       
       
       

      



        
     



        private  void BtnStartTest_Click(object sender, EventArgs e)
        {
            try
            {

                StartClickCount++;
                if (StartClickCount % 2 == 1)
                {
                    if (ClsGlobal.PowerStatus[0] < 2)
                    {
                        MessageBox.Show("请打开电源");
                        StartClickCount = 0;
                        return;
                    }

                    BtnStartTest.Text = "停止";
                    BtnStartTest.FillColor = Color.IndianRed;
                    BtnStartTest.RectColor = Color.IndianRed;
                    BtnStartTest.FillHoverColor = Color.Red;

                    Application.DoEvents();
                    StartAdjust();
                }
                else
                {
                    BtnStartTest.Text = "启动";
                    BtnStartTest.RectColor = Color.FromArgb(80, 160, 255);
                    BtnStartTest.FillColor = Color.FromArgb(80, 160, 255);
                    BtnStartTest.FillHoverColor = Color.FromArgb(115, 179, 255);



                    Application.DoEvents();
                    StopAdjust();
                    MessageBox.Show("扭矩调节完成！若后续拆装操作，请关闭电源！", "安全提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);

                }
            }

            catch(Exception ex)
            {
                MessageBox.Show(ex.Message);
                MessageBox.Show("扭矩调节出错！若后续拆装操作，请关闭电源！", "安全提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);

            }

        }

        private async void StartAdjust()
        {
            try
            {

                if (IsRunning)
                {
                    MessageBox.Show("试验正在运行中，不要重复开始! ", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if ( ClsGlobal.PowerStatus[0] < 2)
                {
                    MessageBox.Show("请打开电源! ", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }



                string ReadMsg = InitSerialPort();
                if (ReadMsg.IndexOf("OK") < 0)
                {
                    MessageBox.Show(ReadMsg);
                    return;
                }



                ConfirmNormalTest();



                IsRunning = false;




                await System.Threading.Tasks.Task.Delay(1000);

                if (!CanIsOK)
                {
                    InitCanDev();
                }
                for (int i = 0; i < 1; i++)
                {
                    releaseFailureCounters[i] = 0;
                    AlertStatus[i] = 0;
                    CanRecvCounter[i] = 0;
                }

                CurrentStatFileName = $"{testConfig.StoreDir}\\StatData.bin";



                listDaqTorque.Clear();
                listForce.Clear();


                BrakeNo = 0;
                StatLog.Clear();

                zedGraphRealChart.GraphPane.XAxis.Scale.Max = ClsGlobal.XDuration;
                zedGraphRealChart.GraphPane.XAxis.Scale.Min = 0;
                zedGraphRealChart.AxisChange();
                zedGraphRealChart.Invalidate();
                zedGraphRealChart.Refresh();

                DaqAiCurrentDispData.Clear();
                DaqAiTorqueDispData.Clear();


                bufferA = new ConcurrentQueue<byte[]>();
                bufferB = new ConcurrentQueue<byte[]>();
                activeWriteBuffer = new ConcurrentQueue<byte[]>();
                readyReadBuffer = new ConcurrentQueue<byte[]>();
                activeWriteBuffer.Clear();
                readyReadBuffer.Clear();


                activeWriteBuffer = bufferA;
                readyReadBuffer = bufferB;














                for (int i = 0; i < 1; i++)
                {
                    if (EmbGroup[i].IsEnabel)
                    {
                        EmbGroup[i].CtrlAlert.State = UILightState.Blink;
                        EmbGroup[i].CtrlAlert.OffCenterColor = Color.FromArgb(140, 140, 140);
                        EmbGroup[i].CtrlAlert.OffColor = Color.FromArgb(140, 140, 140);
                        EmbGroup[i].CtrlAlert.OnCenterColor = Color.Lime;
                        EmbGroup[i].CtrlAlert.OnColor = Color.Lime;
                    }
                }

                await System.Threading.Tasks.Task.Delay(1000);





                timerProgressDisp.Interval = curveDispSpan;
                timerProgressDisp.Enabled = true;
                curveDisplayTimer = new System.Threading.Timer(DisplayCallback, null, curveDispSpan, curveDispSpan);
                InitializeEmbControlTimers((uint)(testConfig.TestSpan * 1000.0));



                for (int i = 0; i < 1; i++)
                {
                    if (EmbGroup[i].IsEnabel)
                    {
                        bool timer = StartEmbControlTimer(i);

                    }
                }
                Dev1StartDaqAITask();



                await DeviceReset(ClsGlobal.DevResetWaitSpan, ClsGlobal.ReleaseAoVol);





                RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "调节开始!");


                


                StartMinitorTimer(120000);   //两分钟重启监控一次

                StatLog.Clear();
                runBegin = DateTime.Now;
                lastGraphyTime = runBegin;
               

                BtnAutoLearn.Enabled = false;
                BtnGetCanID.Enabled = false;

                IsGetCanID = false;
                IsAutoLearn = false;
                IsRunning = true;


            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
                IsGetCanID = false;
                IsAutoLearn = false;
                IsRunning = false;
            }
        }
      

        private void FrmMainMonitor_FormClosing(object sender, FormClosingEventArgs e)
        {
           if(IsRunning)
            {
               
                MessageBox.Show("试验正在进行中！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                e.Cancel = true;
                return;
            }

            SafeDisposeSerialPort();

        }

      
       
        private void BtnCancel_Click(object sender, EventArgs e)
        {
            IsTestConfirm = false;
        }




        private async void StopAdjust()
        {
            try
            {
              
                    if (!IsRunning)
                    {
                        MessageBox.Show("试验尚未开始!");
                        return;
                    }



                    curveDisplayTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    curveDisplayTimer.Dispose();

                    //曲线显示停止


                    timerProgressDisp.Enabled = false;

                    //  SetEmbControlTimerInterval(0, uint.MaxValue);

                    Dev1StopTask();
                    StopAllEmbControlTimers();
                    //控制EMB启停的指令停止

                    for (int i = 0; i < 1; i++)
                    {
                        if (EmbGroup[i].IsEnabel)
                        {
                            //  EmbGroup[i].CtrlRunning.Active = false;
                            EmbGroup[i].CtrlAlert.State = UILightState.On;
                            //   EmbGroup[i].CtrlRunning.Enabled = false;
                        }
                    }

                    StopWriteStatTimer();
                    WriteStatFinal();



                    await DeviceReset(ClsGlobal.DevResetWaitSpan, ClsGlobal.ReleaseAoVol);

                    //  System.Threading.Tasks.Task.Delay(10000);

                    //  System.Threading.Thread.Sleep(15000);

                    


                    ConfigOperation.SaveOneItem("ClampForce", CurrentClampForce.ToString("f0"));
                    ClsGlobal.ClampForce = short.Parse(CurrentClampForce.ToString("f0"));

                 
                    ConfigOperation.SaveOneItem("ClampAoVol", CurrentPushVoltage.ToString("f1"));
                    ClsGlobal.ClampAoVol = double.Parse(CurrentPushVoltage.ToString("f1"));


                    RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "调节结果已记录!");

                    RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "调节停止!");


                    IsRunning = false;


                


                BtnGetCanID.Enabled = true;
                BtnAutoLearn.Enabled = true;

                StopMinitorTimer();


               



            }
            catch (Exception ex)
            {
                MessageBox.Show("试图停止试验失败! " + ex.Message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                BtnGetCanID.Enabled = true;
                BtnAutoLearn.Enabled = true;
            }

            finally
            {
                SafeDisposeSerialPort();
                IsGetCanID = false;
                IsAutoLearn = false;
                IsRunning = false;
            }
        }




        private void BtnRunLog_Click(object sender, EventArgs e)
        {
            try
            {
                string OutFile = System.Environment.CurrentDirectory + @"\RunLog.txt";
                ClsLogProcess.ViewLogData(ref LogInformation, OutFile);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void BtnErrorLog_Click(object sender, EventArgs e)
        {
            try
            {
                string OutFile = System.Environment.CurrentDirectory + @"\ErrorLog.txt";
                ClsErrorProcess.ViewErrorData(ref LogError, OutFile);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }


        #region DAQ AI 处理
        private bool Dev1StartDaqAITask()
        {
            try
            {
                Dev1analogTask = new NationalInstruments.DAQmx.Task("Dev1AITask");
                int Dev1AiChannelCounts = Dev1UsedDaqAIChannels.Length;
           
                    for (int i = 0; i < Dev1AiChannelCounts; i++)
                    {
                    Dev1analogTask.AIChannels.CreateVoltageChannel(Dev1UsedDaqAIChannels[i],
                    "",
                    AITerminalConfiguration.Differential,
                    AiMinVoltage,
                    AiMaxVoltage,
                    AIVoltageUnits.Volts);
                }
        Dev1analogTask.Timing.ConfigureSampleClock("",
                   ClsGlobal.DaqFrequency,
                   SampleClockActiveEdge.Rising,
                   SampleQuantityMode.ContinuousSamples,
                   ClsGlobal.SamplesPerChannel);

                // Verify the tasks
                Dev1analogTask.Control(TaskAction.Verify);




                Dev1StartTask();


                Dev1analogTask.Start();


                // Start reading as well
                Dev1analogCallback = new AsyncCallback(Dev1AnalogRead);
                Dev1analogReader = new AnalogMultiChannelReader(Dev1analogTask.Stream);



                // Use SynchronizeCallbacks to specify that the object 
                // marshals callbacks across threads appropriately.
                Dev1analogReader.SynchronizeCallbacks = true;


                Dev1analogReader.BeginReadMultiSample(ClsGlobal.SamplesPerChannel, Dev1analogCallback, Dev1analogTask);
            

             
                return true;
            }
            catch (Exception ex)
            {
                Dev1StopTask();
                SafeLogError($"启动采集卡失败: {ex.Message}");
                return false;
            }
        }

        private void Dev1StartTask()
        {
            try
            {
                if (Dev1runningAnalogTask == null)
                {
                    // Change state
                    Dev1runningAnalogTask = Dev1analogTask;
                }
            }
            catch (Exception ex)
            {
               // MessageBox.Show("启动数据采集卡错误： " + ex.Message);
                SafeLogError($"启动数据采集卡错误: {ex.Message}");
            }
        }

        private void Dev1StopTask()
        {
            try
            {
                // Change state
                Dev1runningAnalogTask = null;
                // Stop tasks
                Dev1analogTask.Stop();
                Dev1analogTask.Dispose();
              
            }

            catch (Exception ex)
            {
              //  MessageBox.Show("停止数据采集卡错误： " + ex.Message);
                SafeLogError($"停止数据采集卡错误: {ex.Message}");
            }
        }

        private void Dev1AnalogRead(IAsyncResult ar)
        {
            try
            {

               // SafeLogError(IsRunning.ToString());

               

                DateTime RecvTime = DateTime.Now;

                if (Dev1runningAnalogTask != null && Dev1runningAnalogTask == ar.AsyncState)
                {
                 
                    double[,] data = Dev1analogReader.EndReadMultiSample(ar);
                    if (data == null)
                    {
                       
                        ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "DAQ Dev1 未读取到数据", "DAQ Dev1");
                        return;
                    }

                    if (IsRunning)
                    {
                        int DaqDispCurrentNo = ParaNameToActChannel["EMB1_current"];
                        double[] DispCurrentData = new double[ClsGlobal.SamplesPerChannel];
                        Buffer.BlockCopy(data, DaqDispCurrentNo * 8 * ClsGlobal.SamplesPerChannel, DispCurrentData, 0, 8 * ClsGlobal.SamplesPerChannel);

                        int DaqDispTorqueNo = ParaNameToActChannel["EMB1_torque"];
                        double[] DispTorqueData = new double[ClsGlobal.SamplesPerChannel];
                        Buffer.BlockCopy(data, DaqDispTorqueNo * 8 * ClsGlobal.SamplesPerChannel, DispTorqueData, 0, 8 * ClsGlobal.SamplesPerChannel);




                        AddToDaqAiDispCache(DaqAiDispDataLens, DispCurrentData, ref DaqAiCurrentDispData);
                        AddToDaqAiDispCache(DaqAiDispDataLens, DispTorqueData, ref DaqAiTorqueDispData);

                       // SafeLogError(DaqAiCurrentDispData.Count.ToString());

                     //   DaqContext1.EnqueueData(data, RecvTime);

                    }

                    if (IsAutoLearn)
                    {
                        int DaqDispCurrentNo = ParaNameToActChannel["EMB1_current"];
                        double[] DispCurrentData = new double[ClsGlobal.SamplesPerChannel];
                        Buffer.BlockCopy(data, DaqDispCurrentNo * 8 * ClsGlobal.SamplesPerChannel, DispCurrentData, 0, 8 * ClsGlobal.SamplesPerChannel);

                        int DaqDispTorqueNo = ParaNameToActChannel["EMB1_torque"];
                        double[] DispTorqueData = new double[ClsGlobal.SamplesPerChannel];
                        Buffer.BlockCopy(data, DaqDispTorqueNo * 8 * ClsGlobal.SamplesPerChannel, DispTorqueData, 0, 8 * ClsGlobal.SamplesPerChannel);

                        AddToDaqAiDispCache(DaqAiDispDataLens, DispCurrentData, ref DaqAiCurrentDispData);
                        AddToDaqAiDispCache(DaqAiDispDataLens, DispTorqueData, ref DaqAiTorqueDispData);

                    }





                    Dev1analogReader.BeginReadMultiSample(ClsGlobal.SamplesPerChannel, Dev1analogCallback, Dev1analogTask);
                }


            }
            catch (Exception ex)
            {
                Dev1StopTask();
                SafeLogError($"DAQ Dev1 读取数据出错: {ex.Message}");

                System.Threading.Tasks.Task.Run(() =>
                {
                    const int maxRetries = 30;
                    int retryCount = 0;
                    bool success = false;

                    while (retryCount < maxRetries && !success)
                    {
                        retryCount++;
                        try
                        {
                            if (this.InvokeRequired)
                            {
                                this.Invoke(new Action(() =>
                                {
                                    try
                                    {
                                        success= Dev1StartDaqAITask();
                                        if (success)
                                        {
                                            SafeLogError($"第 {retryCount} 次重连成功");
                                        }
                                    }
                                    catch (Exception invokeEx)
                                    {
                                        SafeLogError($"第 {retryCount} 次重试失败: {invokeEx.Message}");
                                    }
                                }));
                            }

                            if (success) break;
                            Thread.Sleep(1000);
                        }
                        catch (Exception retryEx)
                        {
                            SafeLogError($"重试过程异常: {retryEx.Message}");
                        }
                    }

                    if (!success)
                    {
                        SafeLogError($"采集卡重连失败（共尝试 {maxRetries} 次），请检查硬件连接");
                    }
                });
            }
        }


        

          private void AddToDaqAiDispCache(int maxLens,double[] Data,ref ConcurrentQueue<double[]> daqAiDispData)
        {
            try
            {
                daqAiDispData.Enqueue(Data);
                if (daqAiDispData.Count > maxLens)
                {
                    double[] removedData;
                    daqAiDispData.TryDequeue(out removedData);
                }
            }
            catch (Exception ex)
            {
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "缓存 Dev1 显示数据出错: " + ex.Message, "DAQ Dev1");
            }
        }

        private void AddToStatLogCache(int maxLens, byte[] Data, ref ConcurrentQueue<byte[]> StatLogData)
        {
            try
            {
                StatLogData.Enqueue(Data);
                if (StatLogData.Count > maxLens)
                {
                    byte[] removedData;
                    StatLogData.TryDequeue(out removedData);
                }
            }
            catch (Exception ex)
            {
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "缓存Stat数据出错: " + ex.Message, "Stat  Result");
            }
        }



       


        #endregion

        private void BtnAutoLearn_Click(object sender, EventArgs e)
        {
            try
            {


                AutoLearnClickCount++;
                if (AutoLearnClickCount % 2 == 1)
                {

                    if (ClsGlobal.PowerStatus[0] < 2)
                    {
                        MessageBox.Show("请打开电源");
                        AutoLearnClickCount = 0;
                        return;
                    }


                    BtnAutoLearn.Text = "停止自学习";
                    BtnAutoLearn.FillColor = Color.IndianRed;
                    BtnAutoLearn.RectColor = Color.IndianRed;
                    BtnAutoLearn.FillHoverColor = Color.Red;
                    Application.DoEvents();
                    AutoLearnStart();
                }
                else
                {
                    BtnAutoLearn.Text = "自学习";
                    BtnAutoLearn.RectColor = Color.FromArgb(80, 160, 255);
                    BtnAutoLearn.FillColor = Color.FromArgb(80, 160, 255);
                    BtnAutoLearn.FillHoverColor = Color.FromArgb(115, 179, 255);
                    Application.DoEvents();
                    AutoLearnStop();

                    MessageBox.Show("自学习完成！若后续拆装操作，请关闭电源！", "安全提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);


                }


            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
                MessageBox.Show("自学习出错！若后续拆装操作，请关闭电源！", "安全提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);

            }
        }


        private void AutoLearnStop()
        {
            try
            {
                curveDisplayTimer.Change(Timeout.Infinite, Timeout.Infinite);
                curveDisplayTimer.Dispose();
                //曲线显示停止
                timerProgressDisp.Enabled = false;
                StopAllEmbControlTimers();
                //控制EMB启停的指令停止

                for (int i = 0; i < 1; i++)
                {
                    if (EmbGroup[i].IsEnabel)
                    {
                        ClearAutoSend(EmbNoToChannel[i]);
                        System.Threading.Thread.Sleep(300);                //清空指令
                        RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "EMB" + (i+1).ToString() + " 清空CAN定时指令!");


                        EmbGroup[i].CtrlAlert.State = UILightState.On;

                    }
                }

                RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "自学习结束!");
                System.Threading.Thread.Sleep(1000);

                BtnStartTest.Enabled = true;
                BtnGetCanID.Enabled = true;
              
            }
            catch (Exception ex)
            {
                BtnStartTest.Enabled = true;
                BtnGetCanID.Enabled = true;
             
                MessageBox.Show(ex.Message);
            }
            finally
            {
                IsAutoLearn = false;
                IsRunning = false;
                IsGetCanID = false;
            }

        }


        private void AutoLearnStart()
        {
            try
            {
               
                if (IsAutoLearn)
                {
                    MessageBox.Show("自学习进行中，不要重复开始! ", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (ClsGlobal.PowerStatus[0] < 2)
                {
                    MessageBox.Show("请打开电源! ", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }



                if (!CanIsOK)
                {
                    InitCanDev();
                }

                ConfirmAutoLearnTest();

                for (int i = 0; i < 1; i++)
                {
                    releaseFailureCounters[i] = 0;
                    AlertStatus[i] = 0;
                    CanRecvCounter[i] = 0;
                }


                listDaqTorque.Clear();
                listForce.Clear();

                zedGraphRealChart.GraphPane.XAxis.Scale.Max = ClsGlobal.XDuration;
                zedGraphRealChart.GraphPane.XAxis.Scale.Min = 0;
                zedGraphRealChart.AxisChange();
                zedGraphRealChart.Invalidate();

                DaqAiCurrentDispData.Clear();
                DaqAiTorqueDispData.Clear();

                bufferA.Clear();
                bufferB.Clear();
                activeWriteBuffer = bufferA;
                readyReadBuffer = bufferB;




                timerProgressDisp.Interval = curveDispSpan;
                timerProgressDisp.Enabled = true;
                curveDisplayTimer = new System.Threading.Timer(DisplayCallback, null, curveDispSpan, curveDispSpan);
                InitializeEmbControlTimers((uint)(testConfig.TestSpan * 1000.0));
                timerProgressDisp.Enabled = true;
                GenerateAutoLearnCommand();

                for (int i = 0; i < 1; i++)
                {

                    if (EmbGroup[i].IsEnabel)
                    {
                        AssignToAutoLearn(EmbGroup[i].EmbName);

                        EmbGroup[i].CtrlAlert.State = UILightState.Blink;
                        EmbGroup[i].CtrlAlert.OffCenterColor = Color.FromArgb(140, 140, 140);
                        EmbGroup[i].CtrlAlert.OffColor = Color.FromArgb(140, 140, 140);
                        EmbGroup[i].CtrlAlert.OnCenterColor = Color.Lime;
                        EmbGroup[i].CtrlAlert.OnColor = Color.Lime;
                    }
                }
                System.Threading.Thread.Sleep(1000);

                autoLearnDetector = new AutoLearnDetector(3, 10);
                autoLearnDetector.Reset();


                

                for (int i = 0; i < 1; i++)
                {
                    if (EmbGroup[i].IsEnabel)
                    {
                        StartEmbControlTimer(i);
                    }
                }

                //  Dev1StartDaqAITask();
                runBegin = DateTime.Now;
                lastGraphyTime = runBegin;
                RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "自学习开始!");


                BtnStartTest.Enabled = false;
                BtnGetCanID.Enabled = false;

                IsAutoLearn = true;
                IsRunning = false;
                IsGetCanID = false;

            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
                IsAutoLearn = false;
                IsRunning = false;
                IsGetCanID = false;
            }
        }

        #region   数据表格
     


        private void ToggleCurveVisibility(string paramName, bool isVisible)
        {
            if (zedGraphRealChart.InvokeRequired)
            {
                zedGraphRealChart.Invoke(new Action<string, bool>(ToggleCurveVisibility), paramName, isVisible);
                return;
            }

            //lock (graphLock)
            //{
                try
                {
                    //var pane = zedGraphRealChart.GraphPane;
                    //if (curveDictionary.TryGetValue(paramName, out var curve))
                    //{
                    //curve.IsVisible = isVisible;

                    //if(paramName=="Act_Force")
                    //{
                    //    zedGraphRealChart.GraphPane.YAxis.IsVisible = isVisible;
                    //}
                    // if (paramName == "DAQ_Current")
                    //{
                    //    zedGraphRealChart.GraphPane.Y2Axis.IsVisible = isVisible;
                    //}
                    // if (paramName == "DAQ_Torque")
                    //{
                    //    zedGraphRealChart.GraphPane.YAxisList[1].IsVisible = isVisible;
                    //}
                    //if (paramName == "Act_Current")
                    //{
                    //    zedGraphRealChart.GraphPane.Y2AxisList[1].IsVisible = isVisible;
                    //}

                    //}


                    // zedGraphRealChart.AxisChange();
                    // zedGraphRealChart.Invalidate();

                    if (curveDictionary.ContainsKey(paramName))
                    {
                        LineItemOperation line;
                        curveDictionary.TryGetValue(paramName, out line);
                        line.IsActive = isVisible;
                    }



                }
                catch (Exception ex)
                {
                    ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "选择参数更新曲线出错: " + ex.Message, "更新曲线");
                }
           // }
        }


        private void dgvRealData_CellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.ColumnIndex == 0 && e.RowIndex >= 0) // CheckBox列为第一列
            {
                var grid = (DataGridView)sender;
                var cell = (DataGridViewCheckBoxCell)grid.Rows[e.RowIndex].Cells[0];
                cell.Value = cell.Value == null || !(bool)cell.Value; // 切换状态

                var paramName = grid.Rows[e.RowIndex].Cells["colParameter"].Value.ToString();
                ToggleCurveVisibility(paramName, (bool)cell.Value);

                grid.EndEdit(); // 立即提交更改
            }
        }




        #endregion




        public void LoadEMBHandlerAndFrameNo()
        {
            try
            {
                // 创建DataTable结构
                DataTable dt = new DataTable();
                dt.Columns.Add("名称", typeof(string));
                dt.Columns.Add("型号", typeof(string));
                dt.Columns.Add("产品编号", typeof(string));
                dt.Columns.Add("方向", typeof(string));

                // 加载XML文件
                string xmlPath = Path.Combine(Environment.CurrentDirectory, @"Config\EMBControl.XML");
                XDocument xdoc = XDocument.Load(xmlPath);

                // 解析XML数据
                foreach (XElement emb in xdoc.Descendants("EMB"))
                {
                    dt.Rows.Add(
                        (string)emb.Element("名称"),
                        (string)emb.Element("型号"),
                        (string)emb.Element("产品编号"),
                        (string)emb.Element("方向")
                    );
                }

               for(int i=0;i< dt.Rows.Count;i++)
                {
                    EMBToDirection[dt.Rows[i]["名称"].ToString()] = dt.Rows[i]["方向"].ToString();

                    EMBNameToSendFrame[dt.Rows[i]["名称"].ToString()]= DirectionToSendFrame[dt.Rows[i]["方向"].ToString()];

                    EMBNameToRecvFrame[dt.Rows[i]["名称"].ToString()] = DirectionToRecvFrame[dt.Rows[i]["方向"].ToString()];


                }

                var sortedKeys = EMBToDirection.Keys.OrderBy(key => key).ToList();

                int handleNo = -1;

                foreach (var key in sortedKeys)
                {
                    handleNo++;
                    EMBHandlerToSendFrame[handleNo] = DirectionToSendFrame[EMBToDirection[key]];
                    EMBHandlerToRecvFrame[handleNo] = DirectionToRecvFrame[EMBToDirection[key]];
                }


               


            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载EMB配置失败: {ex.Message}");
            }
        }


        #region 串口操作
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
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "COM口接收数据出错: " + ex.Message, "COM口接收数据");
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

       


        private async Task<bool> OpenSerialChannel(byte ChannelNo, int maxRetries)
        {
            lock (_serialPortLock)
            {
                if (!serialPort.IsOpen)
                {
                    string OpenMsg = InitSerialPort();
                    if (OpenMsg.IndexOf("OK") < 0)
                    {
                        ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "打开COM口失败: " + OpenMsg, "打开COM口");
                        RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "打开COM口失败: " + OpenMsg);
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
                
                    ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, $"第 {retryCount + 1}/{maxRetries} 次尝试发送指令...", "串口操作");


                    var currentTcs = new TaskCompletionSource<byte[]>();
                    serialResponseTcs = currentTcs;

                    var responseTask = currentTcs.Task;
                    var delayTask = System.Threading.Tasks.Task.Delay(ClsGlobal.SerialSendIntervalSpan);
                    var completedTask = await System.Threading.Tasks.Task.WhenAny(responseTask, delayTask);

                    if (completedTask == delayTask)
                    {
                        ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, $"第 {retryCount + 1}/{maxRetries} 次响应超时", "串口操作");
                        retryCount++;
                        continue;
                    }

                    byte[] response = await responseTask;

                    if (response[0] == command[0] && response[1] == command[1] + 0x80)
                    {                    
                        ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, $"第 {retryCount + 1}/{maxRetries} 次异常响应", "串口操作");
                        retryCount++;
                    }
                    else
                    {
                       
                        ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "COM口回应正常！", "串口操作");

                        operationSuccess = true;
                    }
                }

                if (!operationSuccess)
                {
                    string errorMsg = $"已达最大重试次数（{maxRetries}次），操作失败";
                    ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, errorMsg, "COM口通信");
                    RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + errorMsg);
                    SafeDisposeSerialPort();
                    return false; // 返回失败结果
                }

                return true; // 操作成功
            }
            catch (Exception ex)
            {
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "COM口通信错误：" + ex.Message, "COM口通信");
                RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "COM口通信错误: " + ex.Message);
                SafeDisposeSerialPort();
                return false; // 返回失败结果
            }
        }


        private async Task<bool> CloseSerialChannel(byte ChannelNo, int maxRetries)
        {
            lock (_serialPortLock)
            {
                if (!serialPort.IsOpen)
                {
                    string OpenMsg = InitSerialPort();
                    if (OpenMsg.IndexOf("OK") < 0)
                    {
                        ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "打开COM口失败: " + OpenMsg, "打开COM口");
                        RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "打开COM口失败: " + OpenMsg);
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
                    ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, $"第 {retryCount + 1}/{maxRetries} 次尝试发送指令...", "串口操作");

                    var currentTcs = new TaskCompletionSource<byte[]>();
                    serialResponseTcs = currentTcs;

                    var responseTask = currentTcs.Task;
                    var delayTask = System.Threading.Tasks.Task.Delay(ClsGlobal.SerialSendIntervalSpan);
                    var completedTask = await System.Threading.Tasks.Task.WhenAny(responseTask, delayTask);

                    if (completedTask == delayTask)
                    {
                        ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, $"第 {retryCount + 1}/{maxRetries} 次响应超时", "串口操作");
                        retryCount++;
                        continue;
                    }

                    byte[] response = await responseTask;

                    if (response[0] == command[0] && response[1] == command[1] + 0x80)
                    {
                        ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, $"第 {retryCount + 1}/{maxRetries} 次异常响应", "串口操作");
                        retryCount++;
                    }
                    else
                    {

                        ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "COM口回应正常！", "串口操作");
                        operationSuccess = true;
                    }
                }

                if (!operationSuccess)
                {
                    string errorMsg = $"已达最大重试次数（{maxRetries}次），操作失败";
                    ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, errorMsg, "COM口通信");
                    RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + errorMsg);
                    SafeDisposeSerialPort();
                    return false; // 返回失败结果
                }

                return true; // 操作成功
            }
            catch (Exception ex)
            {
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "COM口通信错误：" + ex.Message, "COM口通信");
                RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "COM口通信错误: " + ex.Message);
                SafeDisposeSerialPort();
                return false; // 返回失败结果
            }
        }


        //private Task<bool> NewSafeWriteDo(bool IsOpen)
        //{
        //    lock (DotaskLock)
        //    {
        //        const int maxRetries = 1;
        //        int attempts = 0;
        //        bool success = false;

        //        while (!success && attempts <= maxRetries)
        //        {
        //            try
        //            {
        //                // 确保Task已初始化
        //                if (DoTask == null && !InitializeDoTask())
        //                {
        //                    attempts++;
        //                    continue;
        //                }


        //                bool[] dataArray = new bool[1];
        //                dataArray[0] = IsOpen;


        //                DigitalMultiChannelWriter writer = new DigitalMultiChannelWriter(DoTask.Stream);
        //                writer.WriteSingleSampleSingleLine(true, dataArray);
        //                success = true;

        //                ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, $"数字输出成功: {IsOpen.ToString()}", "串口操作");

        //            }
        //            catch (DaqException ex)
        //            {
        //                SafeLogError($"数字输出失败: {ex.Message}");
        //                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, $"数字输出失败: {ex.Message}", "DO操作");
        //                attempts++;

        //                // 最后一次尝试后执行清理
        //                if (attempts > maxRetries)
        //                {
        //                    DoTask?.Dispose();
        //                    DoTask = null;
        //                }
        //                else
        //                {
        //                    // 尝试重新初始化
        //                    InitializeDoTask();
        //                }
        //            }
        //        }

        //        if (!success)
        //        {
        //            SafeLogError("数字输出失败，已超过最大重试次数");
        //            ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "数字输出失败，已超过最大重试次数", "DO操作");
        //        }

        //        return success;
        //    }
        //}



        private bool SafeWriteDo(bool IsOpen)
        {
            lock (DotaskLock)
            {
                const int maxRetries = 1;
                int attempts = 0;
                bool success = false;

                while (!success && attempts <= maxRetries)
                {
                    try
                    {
                        // 确保Task已初始化
                        if (DoTask == null && !InitializeDoTask())
                        {
                            attempts++;
                            continue;
                        }


                        bool[] dataArray = new bool[1];
                        dataArray[0] = IsOpen;


                        DigitalMultiChannelWriter writer = new DigitalMultiChannelWriter(DoTask.Stream);
                        writer.WriteSingleSampleSingleLine(true, dataArray);
                        success = true;
                      
                        ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, $"数字输出成功: {IsOpen.ToString()}", "串口操作");

                    }
                    catch (DaqException ex)
                    {
                        SafeLogError($"数字输出失败: {ex.Message}");
                        ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, $"数字输出失败: {ex.Message}", "DO操作");
                        attempts++;

                        // 最后一次尝试后执行清理
                        if (attempts > maxRetries)
                        {
                            DoTask?.Dispose();
                            DoTask = null;
                        }
                        else
                        {
                            // 尝试重新初始化
                            InitializeDoTask();
                        }
                    }
                }

                if (!success)
                {
                    SafeLogError("数字输出失败，已超过最大重试次数");
                    ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "数字输出失败，已超过最大重试次数", "DO操作");
                }

                return success;
            }
        }





        private async void AlertProc(int HandlerIndex)
        {
            try
            {
                StopEmbControlTimer(HandlerIndex);

                SendReleaseCommandToDevice(EmbNoToName[HandlerIndex]);  //卡钳松开
                ApplyAutoSend(EmbNoToChannel[HandlerIndex]);

                System.Threading.Thread.Sleep(300);
                RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "EMB" + HandlerIndex.ToString() + " 告警处置松开!");

                ClearAutoSend(EmbNoToChannel[HandlerIndex]);
                System.Threading.Thread.Sleep(300);                //清空指令
                RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "EMB" + HandlerIndex.ToString() + " 告警处置CAN 卡指令清零!");

                


                await CloseSerialChannel((byte)ClsGlobal.PowerChannel, ClsGlobal.SerialPortRetrys);  //关闭电源指令
               
                await  OpenSerialChannel((byte)ClsGlobal.AlertChannel, ClsGlobal.SerialPortRetrys);  //点亮告警灯指令
            }
            catch (Exception ex)
            {
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "告警处理出错：" + ex.Message, "告警处理");
            }
        }


        #endregion






        public string LoadTestConfigFromXml()
        {
            try
            {
                string xmlPath = Path.Combine(Environment.CurrentDirectory, @"Config\TestConfig.xml");


                if (!File.Exists(xmlPath)) return "未发现试验配置文件";

                testConfig = LoadTestConfigFromFile();

                if (testConfig == null) return "读取试验配置失败";

                testConfig.TestSpan = 1.0 / double.Parse(testConfig.TestCycle);

               

            

                return "OK";
            }
            catch(Exception ex)
            {
                return ex.Message;
            }
          
        }


        private TestConfig LoadTestConfigFromFile()
        {
            try
            {
                var serializer = new XmlSerializer(typeof(TestConfig));

                string xmlPath = Path.Combine(Environment.CurrentDirectory, @"Config\TestConfig.xml");

                using (var reader = new StreamReader(xmlPath))
                {
                    return (TestConfig)serializer.Deserialize(reader);
                }
            }
            catch
            {
                return new TestConfig(); // 返回空配置避免异常
            }
        }

        private void zedGraphRealChart_ContextMenuBuilder(ZedGraphControl control, ContextMenuStrip menuStrip, Point mousePt, ZedGraphControl.ContextMenuObjectState objState)
        {

            menuStrip.Items.Clear();

            // 添加分隔线
            menuStrip.Items.Add(new ToolStripSeparator());
            // 添加设置坐标轴范围的菜单项
            ToolStripMenuItem setAxisRangeItem = new ToolStripMenuItem("设置显示范围");
            setAxisRangeItem.Click += (sender, e) =>
            {
                GraphPane pane = control.GraphPane;
                // 获取当前坐标轴范围
                double currentXMin = pane.XAxis.Scale.Min;
                double currentXMax = pane.XAxis.Scale.Max;
                double currentYMin = pane.YAxis.Scale.Min;
                double currentYMax = pane.YAxis.Scale.Max;

               

                using (AxisRangeDialog dialog = new AxisRangeDialog(
                   0, currentXMax- currentXMin, currentYMin, currentYMax))

                {
                    // 获取鼠标在屏幕上的位置
                    Point screenMousePoint = control.PointToScreen(mousePt);
                    // 设置对话框的起始位置为手动
                    dialog.StartPosition = FormStartPosition.Manual;
                    // 设置对话框的位置为鼠标位置
                    dialog.Location = screenMousePoint;

                    if (dialog.ShowDialog() == DialogResult.OK)
                    {
                        // 设置新的坐标轴范围
                        //   pane.XAxis.Scale.Min = currentXMin;
                        //    pane.XAxis.Scale.Max = currentXMin+ dialog.XMax;


                        //   pane.XAxis.Scale.Min = currentXMin;
                            pane.XAxis.Scale.Max = pane.XAxis.Scale.Min + dialog.XMax;

                    
                        listDaqTorque.RemoveAll(p => p.X > (currentXMin + dialog.XMax));

                        listForce.RemoveAll(p => p.X > (currentXMin + dialog.XMax));
                       
                        listDaqTorque.RemoveAll(p => p.X < currentXMin);

                        listForce.RemoveAll(p => p.X < currentXMin);
                      


                        ClsGlobal.XDuration = dialog.XMax;

                        ConfigOperation.SaveOneItem("XDuration", ClsGlobal.XDuration.ToString("f1"));
                        
                     
                        // 禁用自动缩放
                        //pane.XAxis.Scale.MinAuto = false;
                        //pane.XAxis.Scale.MaxAuto = false;
                        //pane.YAxis.Scale.MinAuto = false;
                        //pane.YAxis.Scale.MaxAuto = false;

                        control.AxisChange();
                        control.Refresh();
                    }
                }
            };
            menuStrip.Items.Add(setAxisRangeItem);


          
        }

        private void DiReadTimer_Tick(object sender, EventArgs e)
        {
            bool[] readData;
            readData = DigitalReader.ReadSingleSampleSingleLine();
         
        }


        // 初始化DoTask
        private bool InitializeDoTask()
        {
            lock (DotaskLock)
            {
                try
                {
                    // 清理原有资源
                    if (DoTask != null)
                    {
                        DoTask.Dispose();
                        DoTask = null;
                    }

                    // 创建并配置新Task
                    DoTask = new NationalInstruments.DAQmx.Task();
                    DoTask.DOChannels.CreateChannel(ClsGlobal.DoChannel + ClsGlobal.AdjustDoNo.ToString(), "",
                    ChannelLineGrouping.OneChannelForEachLine);
                    return true;
                }
                catch (DaqException ex)
                {
                    SafeLogError($"初始化采集卡失败: {ex.Message}");
                    DoTask?.Dispose();
                    DoTask = null;
                    return false;
                }
            }
        }
        


        // 初始化AoTask
        private bool InitializeAoTask()
        {
            lock (AotaskLock)
            {
                try
                {
                    // 清理原有资源
                    if (AoTask != null)
                    {
                        AoTask.Dispose();
                        AoTask = null;
                    }

                    // 创建并配置新Task
                    AoTask = new NationalInstruments.DAQmx.Task();
                    AoTask.AOChannels.CreateVoltageChannel(
                        ClsGlobal.AOChannel,
                        "aoChannel",
                        -10.0,
                        10.0,
                        AOVoltageUnits.Volts);
                    return true;
                }
                catch (DaqException ex)
                {
                    SafeLogError($"初始化采集卡失败: {ex.Message}");
                    AoTask?.Dispose();
                    AoTask = null;
                    return false;
                }
            }
        }

        // 带错误恢复的写入操作
        private void SafeWriteVoltage(double voltage)
        {
            lock (AotaskLock)
            {
                const int maxRetries = 1;
                int attempts = 0;
                bool success = false;

                while (!success && attempts <= maxRetries)
                {
                    try
                    {
                        // 确保Task已初始化
                        if (AoTask == null && !InitializeAoTask())
                        {
                            attempts++;
                            continue;
                        }

                        // 执行写入操作
                        var writer = new AnalogSingleChannelWriter(AoTask.Stream);
                        writer.WriteSingleSample(true, voltage);
                        success = true;
                        ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, $"写入电压成功: {voltage.ToString()}", "串口操作");




                    }
                    catch (DaqException ex)
                    {
                        SafeLogError($"写入调压电压失败: {ex.Message}");
                        ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, $"写入调压电压失败: {ex.Message}", "AO操作");
                        attempts++;

                        // 最后一次尝试后执行清理
                        if (attempts > maxRetries)
                        {
                            AoTask?.Dispose();
                            AoTask = null;
                        }
                        else
                        {
                            // 尝试重新初始化
                            InitializeAoTask();
                        }
                    }
                }

                if (!success)
                {
                    SafeLogError("写入调压电压失败，已超过最大重试次数");
                    ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "写入调压电压失败，已超过最大重试次数", "AO操作");
                }
            }
        }

        // 安全的跨线程日志记录
        private void SafeLogError(string message)
        {
            var formattedMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  > {message}";

            if (RtbInfo.InvokeRequired)
            {
                RtbInfo.BeginInvoke(new Action(() =>
                {
                    RtbInfo.AppendText(formattedMessage + Environment.NewLine);
                    RtbInfo.ScrollToCaret();
                }));

            }
            else
            {
                RtbInfo.AppendText(formattedMessage + Environment.NewLine);
                RtbInfo.ScrollToCaret();
            }
        }


        public void AdjustPressure(double voltage)
        {
            SafeWriteVoltage(voltage);
        }

   









        private string  InitDIDaq()
        {
            try
            {
                DiTask?.Dispose();
                DiTask = new NationalInstruments.DAQmx.Task();
                
                DiTask.DIChannels.CreateChannel(
                 ClsGlobal.DIChannel+ClsGlobal.StartPosDINo.ToString(),
                "",
                ChannelLineGrouping.OneChannelForEachLine);

                DiTask.DIChannels.CreateChannel(
                ClsGlobal.DIChannel + ClsGlobal.EndPosDINo.ToString(),
               "",
               ChannelLineGrouping.OneChannelForEachLine);

                DiTask.DIChannels.CreateChannel(
                ClsGlobal.DIChannel + ClsGlobal.VPPMD3No.ToString(),
               "",
               ChannelLineGrouping.OneChannelForEachLine);

                DiTask.DIChannels.CreateChannel(
               ClsGlobal.DIChannel + ClsGlobal.LimitStartPosDINo.ToString(),
              "",
              ChannelLineGrouping.OneChannelForEachLine);

                DiTask.DIChannels.CreateChannel(
              ClsGlobal.DIChannel + ClsGlobal.LimitEndPosDINo.ToString(),
             "",
             ChannelLineGrouping.OneChannelForEachLine);


                DIChannelMapping.Clear();

                DIChannelMapping["StartPos"] = 0;
                DIChannelMapping["EndPos"] = 1;
                DIChannelMapping["VPPMD3"] = 2;
                DIChannelMapping["LimitStart"] = 3;
                DIChannelMapping["LimitEnd"] = 4;

                DigitalReader = new DigitalMultiChannelReader(DiTask.Stream);
         
                return "OK";
            }

            catch (Exception ex)
            {
                DiTask?.Dispose();
                return "初始化数字输入错误：" + ex.Message;     
            }
        }

        /// <summary>
        /// 读取数字输入通道状态，直到满足连续成功条件或超时
        /// </summary>
        /// <param name="timeoutMs">超时时间（毫秒）</param>
        /// <param name="requiredConsecutiveCount">需要连续成功的次数</param>
        /// <param name="expectedValue">期望的通道状态</param>
        /// <param name="channelName">目标通道名称</param>
        /// <param name="isTimeout">输出是否超时</param>
        private void ReadDigitalInputWithCondition(
            double timeoutMs,
            int requiredConsecutiveCount,
            bool expectedValue,
            string channelName,
            out bool isTimeout)
        {
            isTimeout = false;
            int successCount = 0;
            var timeoutTimer = System.Diagnostics.Stopwatch.StartNew();

            while (true)
            {
                // 超时检查
                if (timeoutTimer.Elapsed.TotalMilliseconds >= timeoutMs)
                {
                    isTimeout = true;
                    break;
                }

                try
                {
                    int channel = DIChannelMapping[channelName];

                    bool[] readData = DigitalReader.ReadSingleSampleSingleLine();

                    // 数据有效性校验
                    if (readData != null && readData.Length > channel)
                    {
                        if (readData[channel] == expectedValue)
                        {
                            successCount++;
                            if (successCount >= requiredConsecutiveCount)
                            {
                                break; // 条件满足退出
                            }
                        }
                        else
                        {
                            successCount = 0; // 状态不连续则重置
                        }
                    }
                }
                //  catch (NationalInstruments.DAQmx.DaqException ex)
                catch (Exception ex)
                {
                    successCount = 0;
                    DiTask?.Dispose();
                    string initResult = InitDIDaq();

                    // UI错误提示（跨线程安全）
                    RtbInfo.Invoke((Action)(() =>
                        RtbInfo.AppendText($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} > 数字输入异常: {ex.Message}\n")));
                }
                //catch (Exception ex)
                //{
                //    RtbInfo.Invoke((Action)(() =>
                //        RtbInfo.AppendText($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} > 系统错误: {ex.GetType().Name}\n")));
                //}

                Thread.Sleep(5); // 防止CPU过载
            }
        }

        /// <summary>
        /// 读取数字输入通道的稳定状态（连续N次读取到相同值时返回）
        /// </summary>
        /// <param name="channelName">目标通道名称</param>
        /// <param name="requiredConsecutiveCount">需要连续相同的次数</param>
        /// <returns>最终稳定的通道值</returns>
        private bool ReadStableDigitalInput(string channelName, int requiredConsecutiveCount)
        {
          
            int matchCount = 0;
            bool? targetValue = null; // 用于记录当前比较的目标值
            int channel = DIChannelMapping[channelName];

            try
            {

               

              

                while (true)
                {
                    bool[] readData = DigitalReader.ReadSingleSampleSingleLine();

                    // 数据有效性校验
                    if (readData != null && readData.Length > channel)
                    {
                        bool currentValue = readData[channel];

                        // 首次读取或值变化时重置计数器
                        if (targetValue != currentValue)
                        {
                            targetValue = currentValue;
                            matchCount = 1;
                        }
                        else
                        {
                            matchCount++;
                        }

                        // 满足连续次数要求
                        if (matchCount >= requiredConsecutiveCount)
                        {
                            return currentValue;
                        }
                    }

                    // 降低CPU占用
                    System.Threading.Thread.Sleep(10);
                }
            }
            catch (NationalInstruments.DAQmx.DaqException ex)
            {
                // DAQ硬件异常恢复
                DiTask?.Dispose();
                string initResult = InitDIDaq();
                if (initResult != "OK")
                {
                    RtbInfo.Invoke((Action)(() =>
                      RtbInfo.AppendText($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} > 数字输入设备恢复异常: {ex.Message}\n")));
                }
                return ReadStableDigitalInput(channelName, requiredConsecutiveCount); // 自动重试
            }
            catch (Exception ex)
            {
                RtbInfo.Invoke((Action)(() =>
                     RtbInfo.AppendText($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} >读取数字输入出现错误 : {ex.Message}\n")));

                throw new InvalidOperationException("读取数字输入失败", ex);

            }
        }



        private void SetVppmWorkMode(string workMode)
        {
           
            try
            {
                //  1,0  快速调节
                //  0,1  出厂模式，通用调节
                //  1,1  精确调节
                bool D1 = false;
                bool D2 = false;
                if (workMode=="Fast")
                {
                    D1 = true;
                    D2 = false;
                }
                else if (workMode == "Factory")
                {
                    D1 = false;
                    D2 = true;
                }
                else if (workMode == "Accurate")
                {
                    D1 = true;
                    D2 = true;
                }
                else
                {
                    SafeLogError("工作模式只支持 Fast、Factory、Accurate");
                    return;
                }




                    using (NationalInstruments.DAQmx.Task digitalWriteTask = new NationalInstruments.DAQmx.Task())
                    {

                        digitalWriteTask.DOChannels.CreateChannel(ClsGlobal.DoChannel + ClsGlobal.VPPMD1No.ToString(), "",
                            ChannelLineGrouping.OneChannelForEachLine);

                        digitalWriteTask.DOChannels.CreateChannel(ClsGlobal.DoChannel + ClsGlobal.VPPMD2No.ToString(), "",
                           ChannelLineGrouping.OneChannelForEachLine);

                        bool[] dataArray = new bool[2];
                        dataArray[0] = D1;
                        dataArray[1] = D2;

                        DigitalMultiChannelWriter writer = new DigitalMultiChannelWriter(digitalWriteTask.Stream);
                        writer.WriteSingleSampleSingleLine(true, dataArray);

                        
                       RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "调压阀工作模式："+ workMode);

                }
            }
            catch (DaqException ex)
            {
                SafeLogError("设置气压阀工作模式错误：" + ex.Message);
            }
            
        }



       

       
        private async System.Threading.Tasks.Task DeviceReset(int LongWaitTime,double AdjustVol)   //回到起点
        {
           try
            {

                SafeLogError("开始复位！");

                ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "开始复位！", "复位操作");

                //  SendReleaseCommandToDevice(EmbNoToName[0]);  //卡钳松开
                //  ApplyAutoSend(EmbNoToChannel[0]);

                if (ClsGlobal.IsLiner > 0)
                {

                    double deltForce = CurrentClampForce / (double)ClsGlobal.ReleaseCount;

                    for (int i = 0; i < ClsGlobal.ReleaseCount; i++)
                    {
                        short clampForce = (short)(CurrentClampForce - (double)(i + 1) * deltForce);

                        if (clampForce < 1)
                        {
                            clampForce = 0;
                        }



                        MakeClampSingle(clampForce);
                        SendToDevice(EmbNoToName[0]);

                        await System.Threading.Tasks.Task.Delay(ClsGlobal.ReleaseSpan);

                    }
                }

                else
                {

                    for (int i = 0; i < ClsGlobal.ReleaseCount; i++)
                    {
                        short clampForce = (short)(CurrentClampForce / Math.Pow(2.0, (double)(i + 1)));
                        if (clampForce < 1000)
                        {
                            clampForce = 0;
                        }

                        MakeClampSingle(clampForce);
                        SendToDevice(EmbNoToName[0]);

                        await System.Threading.Tasks.Task.Delay(ClsGlobal.ReleaseSpan);

                    }
                }











                await System.Threading.Tasks.Task.Delay(300);

              
                ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "卡钳松开！", "复位操作");

                ClearAutoSend(EmbNoToChannel[0]);
                await System.Threading.Tasks.Task.Delay(300);           //清空指令


                AdjustPressure(0.0);
                await System.Threading.Tasks.Task.Delay(300);            //卸掉压力

                ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "卸掉压力！", "复位操作");

                bool startPos = ReadStableDigitalInput("StartPos", ReadDiCount);
                bool LimitStart= ReadStableDigitalInput("LimitStart", ReadDiCount);



                if (!startPos)    // 0表示已经在开始位   关闭换向阀
                {
                   
                    ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "已在开始位置！", "复位操作");

                    if (ClsGlobal.ValveMode == 0)
                    {
                        bool isSuccess = await CloseSerialChannel((byte)ClsGlobal.DirectionValveChannel, ClsGlobal.SerialPortRetrys);   //关闭换向阀，加压前进 
                                                                                                                                       
                        await System.Threading.Tasks.Task.Delay(LongWaitTime);

                        if (isSuccess)
                        {
                           
                            ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "换向阀关闭！", "复位操作");

                        }
                        else
                        {
                            SafeLogError("复位时关闭换向阀失败！");
                            ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "复位时关闭换向阀失败！" , "复位操作");
                        }
                    }

                    else
                    {
                        bool isSuccess = SafeWriteDo(false);   //关闭换向阀，加压前进 
                                                            
                        await System.Threading.Tasks.Task.Delay(LongWaitTime);

                        if (isSuccess)
                        {
                           
                            ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "换向阀关闭！", "复位操作");

                        }
                        else
                        {
                            SafeLogError("复位时关闭换向阀失败！");
                            ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "复位时关闭换向阀失败！", "复位操作");
                        }
                    }


                    return;
                }

                if (!LimitStart)    // 0表示已经在最开始位   关闭换向阀
                {
                    

                    ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "已在近端边缘！", "复位操作");


                    if (ClsGlobal.ValveMode == 0)
                    {
                        bool isSuccess = await CloseSerialChannel((byte)ClsGlobal.DirectionValveChannel, ClsGlobal.SerialPortRetrys);   //关闭换向阀，加压前进 
                                                                                                                                       
                        await System.Threading.Tasks.Task.Delay(LongWaitTime);

                        if (isSuccess)
                        {
                        
                            ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "换向阀关闭！", "复位操作");

                        }
                        else
                        {
                            SafeLogError("复位时关闭换向阀失败！");
                            ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "复位时关闭换向阀失败！", "复位操作");
                          //  return;
                        }
                    }

                    else
                    {
                        bool isSuccess = SafeWriteDo(false);   //关闭换向阀，加压前进 
                                                           
                        await System.Threading.Tasks.Task.Delay(LongWaitTime);

                        if (isSuccess)
                        {
                           
                            ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "换向阀关闭！", "复位操作");

                        }
                        else
                        {
                            SafeLogError("复位时关闭换向阀失败！");
                            ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "复位时关闭换向阀失败！", "复位操作");
                            
                        }
                    }

                   
                    AdjustPressure(AdjustVol);

                    bool IsReadStartDiTimeOut = false;

                    ReadDigitalInputWithCondition((double)LongWaitTime, ReadDiCount, false, "StartPos", out IsReadStartDiTimeOut);

                    if (IsReadStartDiTimeOut)
                    {
                     
                        ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "未在限定时间内有近端边缘走到起点！", "复位操作");

                       // return;
                    }
                    else
                    {
                        SafeLogError("复位到起点!");
                        ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "复位到起点！", "复位操作");

                    }
                    AdjustPressure(0.0);
                  
                    ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "卸掉压力！", "复位操作");

                    return;
                }





                //以下是没在初始位置时的处理，先回到最远的起点，再回到起点


                if (ClsGlobal.ValveMode == 0)
                {
                    bool isSuccess = await OpenSerialChannel((byte)ClsGlobal.DirectionValveChannel, ClsGlobal.SerialPortRetrys);   //打开换向阀，加压回退   
                    await System.Threading.Tasks.Task.Delay(LongWaitTime);

                    if (isSuccess)
                    {
                        AdjustPressure(AdjustVol);
                    }
                    else
                    {
                       
                        SafeLogError("复位时换向阀打开失败！");
                        ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "复位时换向阀打开失败！", "复位操作");


                    }
                }
                else
                {
                    bool isSuccess = SafeWriteDo(true);   //打开换向阀，加压回退   
                    await System.Threading.Tasks.Task.Delay(LongWaitTime);

                    if (isSuccess)
                    {
                        AdjustPressure(AdjustVol);
                    }
                    else
                    {
                        SafeLogError("复位时换向阀打开失败！");
                        ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "复位时换向阀打开失败！", "复位操作");


                    }
                }

                bool IsReadLimitStartDiTimeOut = false;

                ReadDigitalInputWithCondition((double)LongWaitTime*2.0, ReadDiCount, false, "LimitStart", out IsReadLimitStartDiTimeOut);

                if (IsReadLimitStartDiTimeOut)
                {
                 
                   
                    ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "等待回到近端边缘超时！", "复位操作");


                }
                else
                {
                   

                    ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "回到近端边缘！", "复位操作");

                }
                AdjustPressure(0.0);

                if (ClsGlobal.ValveMode == 0)
                {
                   bool isSuccess = await CloseSerialChannel((byte)ClsGlobal.DirectionValveChannel, ClsGlobal.SerialPortRetrys);   //关闭换向阀，加压前进 
                                                                                                                              
                    await System.Threading.Tasks.Task.Delay(LongWaitTime);

                    if (isSuccess)
                    {
                     
                        ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "换向阀关闭，准备移动到起点！", "复位操作");

                    }
                    else
                    {
                        SafeLogError("复位时换向阀关闭失败！");
                        ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "复位时换向阀关闭失败！", "复位操作");

                    }
                }

                else
                {
                    bool isSuccess = SafeWriteDo(false);   //关闭换向阀，加压前进 
                                                                                                                                    // System.Threading.Thread.Sleep(3000);
                    await System.Threading.Tasks.Task.Delay(LongWaitTime);

                    if (isSuccess)
                    {
                        ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "换向阀关闭，准备移动到起点！", "复位操作");

                    }
                    else
                    {
                        SafeLogError("复位时换向阀关闭失败！");
                        ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "复位时换向阀关闭失败！", "复位操作");

                    }
                }

                AdjustPressure(AdjustVol);

                bool IsReadOnceStartDiTimeOut = false;

                ReadDigitalInputWithCondition((double)LongWaitTime, ReadDiCount, false, "StartPos", out IsReadOnceStartDiTimeOut);

                if (IsReadOnceStartDiTimeOut)
                {
                    ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "未在限定时间内有近端边缘走到起点！", "复位操作");

                  
                }
                else
                {
                    SafeLogError("复位到起点!");
                    ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "复位到起点！", "复位操作");

                }
                AdjustPressure(0.0);
             
                ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "卸载压力！", "复位操作");

            }
            catch (Exception ex)
            {
                SafeLogError("复位失败 : " + ex.Message);
            }
        }



        private void InitWriteStatTimer()
        {
            try
            {
                WriteStatTimer = new System.Threading.Timer(
                callback: new TimerCallback(WriteStatEvent),
                state: null,
                dueTime: Timeout.Infinite, // 初始不触发
                period: Timeout.Infinite); // 初始不触发


            }
            catch (Exception ex)
            {
                SafeLogError("初始化统计记录线程出错！" + ex.Message);
            }
        }



        private async void WriteStatFinal()
        {

            await StatFileLock.WaitAsync();
            FileStream fs = null;
            try
            {
                ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "统计特征值记录结束!", "数据记录");

                int ResultLens = StatLog.Count;
                int totalBytes = ResultLens * StatLogRecordLens;
                byte[] mergedData = new byte[totalBytes];
                int currentOffset = 0;


                for (int i = 0; i < ResultLens; i++)
                {
                    if (StatLog.TryDequeue(out byte[] canResultData))
                    {
                        Buffer.BlockCopy(
                        src: canResultData,
                       srcOffset: 0,
                       dst: mergedData,
                       dstOffset: currentOffset,
                       count: StatLogRecordLens); // 明确指定拷贝长度
                        currentOffset += StatLogRecordLens; // 固定步长递增
                    }
                }

                fs = new FileStream(CurrentStatFileName,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                8192,
                FileOptions.WriteThrough | FileOptions.Asynchronous);
                await fs.WriteAsync(mergedData, 0, totalBytes);
                await fs.FlushAsync();
            }

            catch (Exception ex)
            {
                SafeLogError("统计记录线程运行出错！" + ex.Message);
            }
            finally
            {
                StatFileLock.Release();
                if (fs != null)
                {
                    fs.Dispose();
                }
            }
        }


        private async void WriteStatEvent(object state)
        {

            await StatFileLock.WaitAsync();
            FileStream fs = null;
            try
            {
                ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "统计特征值记录!", "数据记录");

                int ResultLens = StatLog.Count;
                int totalBytes = ResultLens * StatLogRecordLens;
                byte[] mergedData = new byte[totalBytes];
                int currentOffset = 0;
              

                for (int i = 0; i < ResultLens; i++)
                {
                    if (StatLog.TryDequeue(out byte[] canResultData))
                    {
                        Buffer.BlockCopy(
                        src: canResultData,
                       srcOffset: 0,
                       dst: mergedData,
                       dstOffset: currentOffset,
                       count: StatLogRecordLens); // 明确指定拷贝长度
                        currentOffset += StatLogRecordLens; // 固定步长递增
                    }
                }

                fs = new FileStream(CurrentStatFileName,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                8192,
                FileOptions.WriteThrough | FileOptions.Asynchronous);
                await fs.WriteAsync(mergedData, 0, totalBytes);
                await fs.FlushAsync();
            }

            catch (Exception ex)
            {
                SafeLogError("统计记录线程运行出错！" + ex.Message);
            }
            finally
            {
                StatFileLock.Release();
                if (fs != null)
                {
                    fs.Dispose();
                }
            }
        }

        private void InitMinitorTimer()
        {
            try
            {
                MinitorTimer = new System.Threading.Timer(
                callback: new TimerCallback(MinitorEvent),
                state: null,
                dueTime: Timeout.Infinite, // 初始不触发
                period: Timeout.Infinite); // 初始不触发

           
            }
            catch (Exception ex)
            {
                SafeLogError("初始化监控线程出错！" + ex.Message);
            }
        }

        

        private async void MinitorEvent(object state)
        {
            try
            {
                ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "进入监控判定!", "状态监测");

                bool LimitStart = ReadStableDigitalInput("LimitStart", ReadDiCount);
                bool LimitEnd= ReadStableDigitalInput("LimitEnd", ReadDiCount);

               
                if (LimitStart && LimitEnd)
                {
                    ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation,"运行区间正常!", "状态监测");
                }
                else
                {
                    System.Threading.Thread.Sleep(1000);

                    bool WaitLimitStart = ReadStableDigitalInput("LimitStart", ReadDiCount);
                    bool WaitLimitEnd = ReadStableDigitalInput("LimitEnd", ReadDiCount);

                    if (WaitLimitStart && WaitLimitEnd)
                    {
                        ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "运行区间恢复正常!", "状态监测");
                    }

                    else   //1秒内未恢复强制复位
                    {

                        try
                        {
                            Dev1StopTask();
                            StopAllEmbControlTimers();
                            await DeviceReset(ClsGlobal.DevResetWaitSpan, ClsGlobal.ReleaseAoVol);

                        }
                        finally
                        {
                            StartEmbControlTimer(0);
                            Dev1StartDaqAITask();
                        }

                    }


                }



            }
            catch (Exception ex)
            {
                SafeLogError("监控线程运行出错！" + ex.Message);
            }
            finally
            {
               
            }
        }


        private void StartWriteStatTimer(int Span)
        {
            try
            {
                WriteStatTimer?.Change(dueTime: 0, period: Span);
            }
            catch (Exception ex)
            {
                SafeLogError("开始统计特征记录线程出错！" + ex.Message);
            }
        }


        private void StopWriteStatTimer()
        {
            try
            {
                WriteStatTimer?.Change(dueTime: Timeout.Infinite, period: Timeout.Infinite);
            }
            catch (Exception ex)
            {
                SafeLogError("停止统计特征记录出错！" + ex.Message);
            }

        }





        // 启动定时器（首次延迟 0 秒，间隔 10 秒）
        private  void StartMinitorTimer(int Span)
        {
            try
            {
                MinitorTimer?.Change(dueTime: Span, period: Span);
            }
            catch (Exception ex)
            {
                SafeLogError("开始监控线程出错！" + ex.Message);
            }
        }

        // 停止定时器（将间隔设为无限大）
        private  void StopMinitorTimer()
        {
            try
            {
                MinitorTimer?.Change(dueTime: Timeout.Infinite, period: Timeout.Infinite);
            }
            catch (Exception ex)
            {
                SafeLogError("停止监控线程出错！" + ex.Message);
            }

        }

        // 释放资源
        private  void DisposeMinitorTimer()
        {
            try
            {
                MinitorTimer?.Dispose();
            }
            catch (Exception ex)
            {
                SafeLogError("释放监控线程出错！" + ex.Message);
            }

        }

        private async void AlertEmb1_Click(object sender, EventArgs e)
        {
              await CloseSerialChannel((byte) ClsGlobal.AlertChannel, ClsGlobal.SerialPortRetrys);  
        }

        private void BtnGetCanID_Click(object sender, EventArgs e)
        {
            try
            {

                CanIDClickCount++;
                if (CanIDClickCount % 2 == 1)
                {
                    if (ClsGlobal.PowerStatus[0] < 2)
                    {
                        MessageBox.Show("请打开电源");
                        CanIDClickCount = 0;
                        return;
                    }


                    if (!CanIsOK)
                    {
                        InitCanDev();
                    }

                    BtnGetCanID.Text = "停止获取";
                    BtnGetCanID.FillColor = Color.IndianRed;
                    BtnGetCanID.RectColor = Color.IndianRed;
                    BtnGetCanID.FillHoverColor = Color.Red;
                    
                    Application.DoEvents();
                    NewDirection = "";
                    BtnStartTest.Enabled = false;
                    BtnAutoLearn.Enabled = false;

                    IsGetCanID = true;
                    IsAutoLearn = false;
                    IsRunning = false;


                }
                else
                {
                    BtnGetCanID.Text = "获取CAN通信ID";
                    BtnGetCanID.RectColor = Color.FromArgb(80, 160, 255);
                    BtnGetCanID.FillColor = Color.FromArgb(80, 160, 255);
                    BtnGetCanID.FillHoverColor = Color.FromArgb(115, 179, 255);


                    
                    BtnStartTest.Enabled = true;
                    BtnAutoLearn.Enabled = true;
                    Application.DoEvents();



                    string xmlPath = Path.Combine(Environment.CurrentDirectory, @"Config\EMBControl.XML");
                    string UpdateMsg = UpdateEMBConfig(xmlPath, "EMB1", NewDirection);
                    RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "Update config " + UpdateMsg);

                    IsGetCanID = false;
                    IsAutoLearn = false;
                    IsRunning = false;

                    MessageBox.Show("获取CAN通信ID完成！若后续拆装操作，请关闭电源！", "安全提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);


                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
                MessageBox.Show("获取CAN通信ID出错！若后续拆装操作，请关闭电源！", "安全提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);

                IsGetCanID = false;
                IsAutoLearn = false;
                IsRunning = false;
            }





        }

        private void BtnAdjustClampForce_ValueChanged(object sender, int value)
        {
            CurrentClampForce = (double)value;
        }

        private void BtnAdjustPushVol_ValueChanged(object sender, int value)
        {
            CurrentPushVoltage = (double)value/10.0;
        }

        private void BtnTarTorque_ValueChanged(object sender, int value)
        {
            CurrentTarTorque = (double)value;
        }



        private async  System.Threading.Tasks.Task Clamp()
        {
            try
            {
                if (ClsGlobal.IsLiner>0)
                {
                    short DeltForce = (short)(CurrentClampForce / ClsGlobal.ClampCount);
                    short InitForce = DeltForce;

                    for (int i = 0; i < ClsGlobal.ClampCount; i++)
                    {
                        short clampForce = (short)(InitForce + DeltForce * i);
                        clampForce = clampForce > (short)CurrentClampForce ? (short)CurrentClampForce : clampForce;               
                        MakeClampSingle(clampForce);
                        SendToDevice(EmbNoToName[0]);
                        await System.Threading.Tasks.Task.Delay(ClsGlobal.ClampSpan);

                    }
                }
                else
                {                 
                    short InitForce = (short)(CurrentClampForce / Math.Pow(2.0, (double)(ClsGlobal.ClampCount -1))+1.0);

                    for (int i = 0; i < ClsGlobal.ClampCount; i++)
                    {
                        short clampForce = (short)(InitForce * Math.Pow(2.0, (double)i));

                        clampForce = clampForce > (short)CurrentClampForce ? (short)CurrentClampForce : clampForce;


                    
                        MakeClampSingle(clampForce);
                        SendToDevice(EmbNoToName[0]);
                        await System.Threading.Tasks.Task.Delay(ClsGlobal.ClampSpan);

                    }
                }

            }

            catch (Exception ex)
            {
                RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "发送夹紧指令失败：" + ex.Message);

            }
        }

        private async System.Threading.Tasks.Task Release()
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(ClsGlobal.ReleaseWaitSpan);

                if (ClsGlobal.ValveMode == 0)
                {                  
                        bool OpenSuccess = await OpenSerialChannel((byte)ClsGlobal.DirectionValveChannel, ClsGlobal.SerialPortRetrys);
                        if (!OpenSuccess)
                        {
                            ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError,
                                $"通道打开失败！通道号：{ClsGlobal.DirectionValveChannel}",
                                "硬件操作");
                            // return;
                        }

                        await System.Threading.Tasks.Task.Delay(ClsGlobal.WaitValveChangeFinishSpan);
                    
                }
                else
                {
                  
                        SafeWriteDo(true);
                        await System.Threading.Tasks.Task.Delay(ClsGlobal.WaitValveChangeFinishSpan);
                }


                


                if (ClsGlobal.IsLiner>0)
                {

                    double deltForce = CurrentClampForce / (double)ClsGlobal.ReleaseCount;

                    for (int i = 0; i < ClsGlobal.ReleaseCount; i++)
                    {
                        short clampForce = (short)(CurrentClampForce - (double)(i + 1) * deltForce);

                        if (clampForce < 1)
                        {
                            clampForce = 0;
                        }

                

                        MakeClampSingle(clampForce);
                        SendToDevice(EmbNoToName[0]);

                        await System.Threading.Tasks.Task.Delay(ClsGlobal.ReleaseSpan);

                    }
                }

                else
                {
                   
                    for (int i = 0; i < ClsGlobal.ReleaseCount; i++)
                    {
                        short clampForce = (short)(CurrentClampForce/Math.Pow(2.0,(double)(i+1)));
                        if (clampForce < 1000)
                        {
                            clampForce = 0;
                        }

                        MakeClampSingle(clampForce);
                        SendToDevice(EmbNoToName[0]);

                        await System.Threading.Tasks.Task.Delay(ClsGlobal.ReleaseSpan);

                    }
                }



            }
            catch (Exception ex)
            {
                RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "发送松开指令失败：" + ex.Message);

            }
        }

        private async System.Threading.Tasks.Task PushGoAndClamp()
        {
            try
            {

               

                if (ClsGlobal.IsLiner>0)
                {
                    double PushDelt = CurrentPushVoltage / (double)ClsGlobal.PushCount;

                    short DeltForce = (short)(CurrentClampForce / ClsGlobal.PushCount);
                    short InitForce = DeltForce;



                    for (int i = 0; i < ClsGlobal.PushCount; i++)
                    {

                      //  RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "Push = " + (PushDelt * (double)(i + 1)).ToString("f2"));
                        AdjustPressure(PushDelt * (double)(i + 1));
                        await System.Threading.Tasks.Task.Delay(ClsGlobal.PushSpan);



                        short clampForce = (short)(InitForce + DeltForce * i);

                        clampForce = clampForce > (short)CurrentClampForce ? (short)CurrentClampForce : clampForce;

                     //   RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "Clamp Force = " + clampForce.ToString());           
                        MakeClampSingle(clampForce);
                        SendToDevice(EmbNoToName[0]);
                    }

                    bool IsReadDiTimeOut = false;
                    ReadDigitalInputWithCondition(testConfig.TestSpan * 1000.0 / 2.0, ReadDiCount, false, "EndPos", out IsReadDiTimeOut);

                    if (IsReadDiTimeOut)
                    {

                        ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "等待开关到终末位置超时!", "试验循环");
                    }
                    else
                    {

                        ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "正常到达远端!", "试验循环");

                    }

                    for (int i = 0; i < ClsGlobal.PushCount; i++)
                    {
                       // RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "Push = " + (CurrentPushVoltage - PushDelt * (double)(i + 1)).ToString("f2"));

                        AdjustPressure(CurrentPushVoltage - PushDelt * (double)(i + 1));
                        await System.Threading.Tasks.Task.Delay(ClsGlobal.PushSpan);
                    }
                }

                else
                {
                    short InitForce = (short)(CurrentClampForce / Math.Pow(2.0, (double)(ClsGlobal.PushCount - 1)) + 1.0);

                    double InitPush = CurrentPushVoltage / Math.Pow(2.0, (double)(ClsGlobal.PushCount - 1));
                    for (int i = 0; i < ClsGlobal.PushCount; i++)
                    {
                        double currentPush = InitPush * Math.Pow(2.0, (double)i);

                        currentPush = currentPush > CurrentPushVoltage ? CurrentPushVoltage : currentPush;

                      


                        AdjustPressure(currentPush);
                        await System.Threading.Tasks.Task.Delay(ClsGlobal.PushSpan);



                       
                      
                            short clampForce = (short)(InitForce * Math.Pow(2.0, (double)i));
                            clampForce = clampForce > (short)CurrentClampForce ? (short)CurrentClampForce : clampForce;

                      
                            MakeClampSingle(clampForce);
                            SendToDevice(EmbNoToName[0]);








                        }

                    bool IsReadDiTimeOut = false;
                    ReadDigitalInputWithCondition(testConfig.TestSpan * 1000.0 / 2.0, ReadDiCount, false, "EndPos", out IsReadDiTimeOut);

                    if (IsReadDiTimeOut)
                    {

                        ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "等待开关到终末位置超时!", "试验循环");
                    }
                    else
                    {

                        ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "正常到达远端!", "试验循环");

                    }

                    for (int i = 0; i < ClsGlobal.PushCount; i++)
                    {

                        double currentPush = CurrentPushVoltage / Math.Pow(2.0, (double)(i + 1));
                        currentPush = currentPush < 0.5 ? 0 : currentPush;
                    
                        AdjustPressure(currentPush);
                        await System.Threading.Tasks.Task.Delay(ClsGlobal.PushSpan);
                    }
                }

            }

            catch (Exception ex)
            {
                RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "向前推失败：" + ex.Message);

            }
        }

        private  async System.Threading.Tasks.Task PushGo()
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(ClsGlobal.WaitSpanBeforePush);

                if (ClsGlobal.IsLiner>0)
                {
                    double PushDelt = CurrentPushVoltage / (double)ClsGlobal.PushCount;
                    for (int i = 0; i < ClsGlobal.PushCount; i++)
                    {

                      //  RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "Push = " + (PushDelt * (double)(i + 1)).ToString("f2"));
                       

                        AdjustPressure(PushDelt * (double)(i + 1));
                        await System.Threading.Tasks.Task.Delay(ClsGlobal.PushSpan);
                    }

                    bool IsReadDiTimeOut = false;
                    ReadDigitalInputWithCondition(testConfig.TestSpan * 1000.0 / 2.0, ReadDiCount, false, "EndPos", out IsReadDiTimeOut);

                    if (IsReadDiTimeOut)
                    {
                        RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + " 等待到终末位置超时!");
                        ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "等待开关到终末位置超时!", "试验循环");
                    }
                    else
                    {

                        ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "正常到达远端!", "试验循环");

                    }

                    for (int i = 0; i < ClsGlobal.PushCount; i++)
                    {
                      // RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "Push = " + (CurrentPushVoltage - PushDelt * (double)(i + 1)).ToString("f2"));

                        AdjustPressure(CurrentPushVoltage - PushDelt * (double)(i + 1));
                        await System.Threading.Tasks.Task.Delay(ClsGlobal.PushSpan);
                    }
                }

                else
                {

                    double InitPush = CurrentPushVoltage / Math.Pow(2.0, (double)(ClsGlobal.PushCount - 1));
                    for (int i = 0; i < ClsGlobal.PushCount; i++)
                    {
                        double currentPush = InitPush * Math.Pow(2.0, (double)i);

                        currentPush = currentPush > CurrentPushVoltage ? CurrentPushVoltage : currentPush;

                     

                        AdjustPressure(currentPush);
                        await System.Threading.Tasks.Task.Delay(ClsGlobal.PushSpan);
                    }

                    bool IsReadDiTimeOut = false;
                    ReadDigitalInputWithCondition(testConfig.TestSpan * 1000.0 / 2.0, ReadDiCount, false, "EndPos", out IsReadDiTimeOut);

                    if (IsReadDiTimeOut)
                    {

                        ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "等待开关到终末位置超时!", "试验循环");
                    }
                    else
                    {

                        ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "正常到达远端!", "试验循环");

                    }

                    for (int i = 0; i < ClsGlobal.PushCount; i++)
                    {

                        double currentPush = CurrentPushVoltage / Math.Pow(2.0, (double)(i + 1));
                        currentPush = currentPush < 0.5 ? 0 : currentPush;
                      //  RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "Push = " + currentPush.ToString("f2"));

                        AdjustPressure(currentPush);
                        await System.Threading.Tasks.Task.Delay(ClsGlobal.PushSpan);
                    }

                   // await System.Threading.Tasks.Task.Delay(1000);

                }

            }

            catch (Exception ex)
            {
                RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "向前推失败：" + ex.Message);

            }
        }

        private async System.Threading.Tasks.Task Back()
        {
            try
            {
          
                AdjustPressure(ClsGlobal.ReleaseAoVol);   //调压

                bool IsReadDiTimeOut = false;

                ReadDigitalInputWithCondition(testConfig.TestSpan * 1000.0 / 2.0, ReadDiCount, false, "StartPos", out IsReadDiTimeOut);

                if (IsReadDiTimeOut)
                {
                    RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "等待开关到初始位置超时!");

                    ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError,
                                "等待开关到初始位置超时!",
                                "试验循环");

                }

                else
                {

                    ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "正常回到起点!", "试验循环");


                }


              
                AdjustPressure(0.0);   //调压


                if (ClsGlobal.ValveMode == 0)
                {
                    // 关闭换向阀并等待完成
                    bool closeSuccess = await CloseSerialChannel(
                    (byte)ClsGlobal.DirectionValveChannel,
                    ClsGlobal.SerialPortRetrys
                );
                    await System.Threading.Tasks.Task.Delay(ClsGlobal.WaitValveGoBack);
                    if (!closeSuccess)
                    {
                        ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError,
                            $"通道关闭失败！通道号：{ClsGlobal.DirectionValveChannel}",
                            "硬件操作");
                        // return;
                    }
                }

                else
                {
                    SafeWriteDo(false);
                    await System.Threading.Tasks.Task.Delay(ClsGlobal.WaitValveGoBack);
                }

            }

            catch (Exception ex)
            {
                RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "后退失败：" + ex.Message);

            }
        }


        private  void ConfirmNormalTest()
        {
            GenerateClampCommand(CurrentClampForce);
            GenerateReleaseCommand(CurrentClampForce);
            AssignToClamp(EmbGroup[0].EmbName);
            AssignToRelease(EmbGroup[0].EmbName);
            for (int i = 0; i < 1; i++)
            {
                if (EmbGroup[i].IsEnabel)
                {
                    CurrentDev = "EMB" + (i + 1).ToString();
                    break;
                }
            }


            testConfig.TestSpan = 1.0 / double.Parse(testConfig.TestCycle);
            string xmlPath = Path.Combine(Environment.CurrentDirectory, @"Config\TestConfig.xml");




            curveDispSpan = (int)(testConfig.TestSpan * 1000.0);  //曲线显示周期和数据记录周期与测试周期一致
            dataLogSpan = (int)(testConfig.TestSpan * 1000.0);

            ClsGlobal.SamplesPerChannel = (int)(ClsGlobal.DaqFrequency * testConfig.TestSpan / 50.0);  //每个显示周期数采卡取样十次

            ClsGlobal.XDuration = testConfig.TestSpan * 4.5;


          
        }

        private void ConfirmAutoLearnTest()
        {
           
            for (int i = 0; i < 1; i++)
            {
                if (EmbGroup[i].IsEnabel)
                {
                    CurrentDev = "EMB" + (i + 1).ToString();
                    break;
                }
            }


            testConfig.TestSpan = 1.0 / double.Parse(testConfig.TestCycle);
            string xmlPath = Path.Combine(Environment.CurrentDirectory, @"Config\TestConfig.xml");




            curveDispSpan = (int)(testConfig.TestSpan * 1000.0);  //曲线显示周期和数据记录周期与测试周期一致
            
            ClsGlobal.SamplesPerChannel = (int)(ClsGlobal.DaqFrequency * testConfig.TestSpan / 50.0);  //每个显示周期数采卡取样十次

            ClsGlobal.XDuration = testConfig.TestSpan * 4.5;



        }


    }
}