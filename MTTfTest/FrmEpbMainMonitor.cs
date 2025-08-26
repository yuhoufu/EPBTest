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
using System.IO.Ports;
using ZedGraph;
//using AsyncListener;
using System.Diagnostics;
using System.IO;
using System.Xml.Serialization;
using Config;
using Controller;
using IO.NI;
using DevExpress.Xpo.Logger;
using Task = System.Threading.Tasks.Task;
using TestConfig = DataOperation.TestConfig;
using MTEmbTest.UIHelpers;

namespace MTEmbTest
{
    public partial class FrmEpbMainMonitor : Form
    {
        private ConcurrentQueue<byte[]> bufferA = new ConcurrentQueue<byte[]>();
        private ConcurrentQueue<byte[]> bufferB = new ConcurrentQueue<byte[]>();
        private ConcurrentQueue<byte[]> activeWriteBuffer;
        private ConcurrentQueue<byte[]> readyReadBuffer;


        private TestConfig testConfig;
        private bool IsAutoLearn = false;
        private bool IsTestConfirm = false;
        private bool IsRunning = false;


        private ClsEMBControler[] EmbGroup = new ClsEMBControler[12];

        private DateTime runBegin;


        private ConcurrentQueue<string> LogError = new ConcurrentQueue<string>();
        private ConcurrentQueue<string> LogInformation = new ConcurrentQueue<string>();
        private ConcurrentQueue<string> LogWarn = new ConcurrentQueue<string>();
        private const int MaxErrors = 100000;
        private const int MaxInfos = 100000;
        private const int MaxWarns = 100000;

        #region 曲线处理相关变量

        private LineItem curveForce;
        private PointPairList listForce;

        private LineItem curveDaqCurrent;
        private PointPairList listDaqCurrent;

        private LineItem curveCanCurrent;
        private PointPairList listCanCurrent;

        #endregion


        private Stopwatch Dispstopwatch = new Stopwatch();
        private DateTime DispinitialDateTime = DateTime.Now;
        private long DispinitialTimestamp = 0;
        private volatile List<byte[]> forceSnapshot = new List<byte[]>();
        private volatile double[] daqSnapshot = Array.Empty<double>();


        private DateTime lastGraphyTime = DateTime.Now;
        private int curveDispSpan = 0;
        private int dataLogSpan = 0;


        private readonly object bufferLock = new object(); //实时曲线缓存数据锁
        private readonly object graphLock = new object(); //曲线更新锁

        private readonly object[]
            clampCounterLocks = Enumerable.Range(0, 6).Select(_ => new object()).ToArray(); //指令发送计数锁

        private int[] releaseFailureCounters = new int[6]; //松开失败计数，发送时加1，松开清零，此数超过预设值说明连续加紧，要告警并松开卡钳


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


        //private double ActForceTimeOffset = 0;
        //private double DaqCurrentTimeOffset = 0;
        private System.Threading.Timer curveDisplayTimer;


        public class LineItemOperation
        {
            public LineItem lineItem { get; set; }
            public bool IsActive { get; set; }
        }

        private ConcurrentDictionary<string, LineItemOperation> curveDictionary =
            new ConcurrentDictionary<string, LineItemOperation>();

        // private ConcurrentQueue<CanData> dataQueue = new ConcurrentQueue<CanData>();
        private const int CacheLens = 6000; //每秒100帧，3秒处理一次，最多缓存6秒


        private const int DeviceCount = 6; // 共6个设备
        private readonly DeviceContext[] _deviceContexts = new DeviceContext[DeviceCount];
        private System.Threading.Timer[] _logtimers = new System.Threading.Timer[DeviceCount * 2];

        private ConcurrentDictionary<int, int> AlertStatus = new ConcurrentDictionary<int, int>();
        //AlertStatus  0 正常  1 值太小，连续夹紧   2 值太高  3 FaultMode 报警


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

        public FormLoggerAdapter logger;
        private DoController _do;
        private AoController _ao;
        private Controller.EpbManager _epb;
        private TwoDeviceAiAcquirer twoDeviceAiAcquirer;
        private AiConfigDetail aiConfigDetail;



        public FrmEpbMainMonitor()
        {
            InitializeComponent();

            // 窗口和父容器尺寸变化时都刷新一次
            this.Resize += (_, __) => ResizeLedDisplaysUnified();
            this.Shown += (_, __) => ResizeLedDisplaysUnified();



            // 创建自定义标题栏
            var titleBar = new Panel
            {
                Height = 30,
                Dock = DockStyle.Top,
                BackColor = Color.AliceBlue
            };

            // 添加自定义按钮
            var btnClose = new Button
            {
                Text = @"×",
                Size = new Size(50, 50),
                Dock = DockStyle.Right
            };
            btnClose.Click += (s, e) => Close();

            titleBar.Controls.Add(btnClose);
            Controls.Add(titleBar);

            // 添加拖拽功能
            titleBar.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    //ReleaseCapture();
                    //SendMessage(Handle, 0xA1, 0x2, 0);
                }
            };


            logger = new FormLoggerAdapter(maxInfos: MaxInfos, maxWarns: MaxWarns, maxErrors: MaxErrors,
                logInfo: LogInformation, logWarn: LogWarn, logError: LogError, this);

            // _do = new DoController(logger);
            // _do.SetConfigPath($@"{Environment.CurrentDirectory}\Config\DOConfig.xml");
            // if (!_do.Initialize())
            // {
            //     // 初始化失败时的处理
            //     //MessageBox.Show("DO控制器初始化失败，请检查配置文件或设备连接！", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            //     SetInfoText("DO控制器初始化失败，请检查配置文件或设备连接");
            // }
            //
            // _ao = AOController.FromXml($@"{Environment.CurrentDirectory}\Config\AOConfig.xml",
            //     logger: logger); // 传入你的 IAppLogger
            // if (!_ao.Initialize())
            // {
            //     // 初始化失败时的处理
            //     SetInfoText("AO控制器初始化失败，请检查配置文件或设备连接");
            // }
        }


        private void ResizeLedDisplaysUnified()
        {
            if (LedRunTime?.Parent == null) return;

            // Step 1: 先计算基准控件（LedRunTime）
            LedAutoSizer.ResizeLedToParentWidth(LedRunTime, LedRunTime.Parent, 0.95, g: 1, blocksPerChar: 5);

            // Step 2: 取出基准的 IntervalOn / IntervalIn
            int baseIntervalOn = LedRunTime.IntervalOn;
            int baseIntervalIn = LedRunTime.IntervalIn;

            // Step 3: 直接应用到其他两个控件
            ApplySameInterval(LedRunCycles, baseIntervalOn, baseIntervalIn);
            ApplySameInterval(LedLastCycles, baseIntervalOn, baseIntervalIn);
        }

        /// <summary>
        /// 把 IntervalOn/IntervalIn 设置成一致，并根据 CharCount 重算宽度
        /// </summary>
        private void ApplySameInterval(Sunny.UI.UILedDisplay led, int intervalOn, int IntervalIn, int blocksPerChar = 5)
        {
            if (led == null) return;

            led.IntervalOn = intervalOn;
            led.IntervalIn = IntervalIn;

            // 用公式算实际宽度
            int C = led.CharCount;
            int g = IntervalIn, s = intervalOn, B = blocksPerChar;
            int K = C * (B + 1) - 1;
            int W = g * (1 + K) + s * (2 + K) + 4;

            led.Width = W;

            // 可选：让控件居中
            if (led.Parent != null)
            {
                led.Left = Math.Max(0, (led.Parent.ClientSize.Width - led.Width) / 2);
            }
        }



        private void FrmEpbMainMonitor_Load(object sender, EventArgs e)
        {
            

            try
            {
                DaqTimeSpanMilSeconds = 1000.0 / ClsGlobal.DaqFrequency;

                activeWriteBuffer = bufferA;
                readyReadBuffer = bufferB;

                var ReadMsg = string.Empty;


                ReadMsg = ClsXmlOperation.GetDaqAIUsedChannels(
                    Environment.CurrentDirectory + @"\Config\AIConfig.xml", "Dev1", out Dev1UsedDaqAIChannels);
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
                    Environment.CurrentDirectory + @"\Config\AIConfig.xml", "Dev2", out Dev2UsedDaqAIChannels);
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
                    Environment.CurrentDirectory + @"\Config\AIConfig.xml", "Dev1", Dev1UsedDaqAIChannels,
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
                    Environment.CurrentDirectory + @"\Config\AIConfig.xml", "Dev1", out ParaNameToScale);
                if (ReadMsg.IndexOf("OK") < 0)
                {
                    MessageBox.Show(ReadMsg);
                    return;
                }

                ReadMsg = ClsXmlOperation.GetDaqOffsetMapping(
                    Environment.CurrentDirectory + @"\Config\AIConfig.xml", "Dev1", out ParaNameToOffset);
                if (ReadMsg.IndexOf("OK") < 0)
                {
                    MessageBox.Show(ReadMsg);
                    return;
                }

                ReadMsg = ClsXmlOperation.GetDaqZeroValueMapping(
                    Environment.CurrentDirectory + @"\Config\AIConfig.xml", "Dev1", out ParaNameToZeroValue);
                if (ReadMsg.IndexOf("OK") < 0)
                {
                    MessageBox.Show(ReadMsg);
                    return;
                }


                var handleNo = -1;

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
                //StartListen();
                MakeCurveMapping();
                //MakeDirectionMapping();
                //LoadTestConfigFromXml(); // 已更改，暂时注释 2025/08/20
                //LoadEMBHandlerAndFrameNo();

                RtbInfo.Invoke(new SetTextCallback(SetInfoText), "1. 编辑试验信息并确认");
                //   RtbInfo.Invoke(new SetTextCallback(SetInfoText), "2. CAN卡初始化");
                //   RtbInfo.Invoke(new SetTextCallback(SetInfoText), "3. 打开各个电源开关");
                RtbInfo.Invoke(new SetTextCallback(SetInfoText), "2. 自学习/开始试验");


                // ClsDiskProc.MakeSubDir(testConfig.StoreDir);
                //
                // var MainDrive = testConfig.StoreDir.Trim().Substring(0, 2);
                //
                // var LastSpace = ClsDiskProc.GetHardDiskSpace(MainDrive);
                // if (LastSpace == 0) MessageBox.Show("指定磁盘不存在！");
                //
                // if (LastSpace < 50)
                // {
                // } // 已更改，暂时注释 2025/08/20


                // 1) 加载全局配置（AO/DO/Test）
                 _cfg = ConfigLoader.LoadAll($@"{Environment.CurrentDirectory}\Config", logger);


                // 2) 初始化 DO 控制器
                _do = new DoController(_cfg.DO, logger);

                // 3) 初始化 AO 控制器
                _ao = new AoController(_cfg.AO, logger);

                aiConfigDetail =
                    AiConfigLoader.Load($@"{Environment.CurrentDirectory}\Config\AIConfig.xml");

                twoDeviceAiAcquirer = new TwoDeviceAiAcquirer(cfg: aiConfigDetail, sampleRate: 1000, samplesPerChannel: 50,
                    medianLens: 10, log: logger);

                twoDeviceAiAcquirer.OnEngBatch += Acq_OnEngBatch; // 订阅工程值批次到达事件



            }

            catch (Exception ex)
            {
                MessageBox.Show("初始化错误 : " + ex.Message);
            }
        }

        public void LoadTestConfigFromXml()
        {
            var xmlPath = Path.Combine(Environment.CurrentDirectory, @"Config\TestConfig.xml");


            if (!File.Exists(xmlPath)) return;

            testConfig = LoadTestConfigFromFile();

            testConfig.TestSpan = 1.0 / double.Parse(testConfig.TestCycle);

            if (testConfig == null) return;

            TxtTargetCycles.Text = testConfig.TestTarget;
            TxtTestStandard.Text = testConfig.TestStandard;
            TxtTestName.Text = testConfig.TestName;
            TxtTestCycleTime.Text = testConfig.TestCycle;
        }

        private TestConfig LoadTestConfigFromFile()
        {
            try
            {
                var serializer = new XmlSerializer(typeof(TestConfig));

                var xmlPath = Path.Combine(Environment.CurrentDirectory, @"Config\TestConfig.xml");

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

        private void MakeCurveMapping()
        {
            curveDictionary.Clear();

            curveDictionary.TryAdd("Act_F", new LineItemOperation
            {
                lineItem = curveForce,
                IsActive = true
            });

            curveDictionary.TryAdd("DAQ_I", new LineItemOperation
            {
                lineItem = curveDaqCurrent,
                IsActive = true
            });

            curveDictionary.TryAdd("Act_I", new LineItemOperation
            {
                lineItem = curveCanCurrent,
                IsActive = true
            });
        }


        #region 曲线处理

        private void InitializeCurve()
        {
            try
            {
                var fontSize = 12;
                // 保留原有初始化代码
                var pane = zedGraphRealChart.GraphPane;
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
                pane.YAxis.MajorGrid.Color = Color.FromArgb(80, 160, 255);
                pane.YAxis.Title.FontSpec.Size = fontSize;
                pane.YAxis.Scale.FontSpec.Size = fontSize;
                pane.YAxis.MajorGrid.IsVisible = true;
                pane.YAxis.MajorGrid.DashOn = float.MaxValue;
                pane.YAxis.MajorGrid.DashOff = 0;


                pane.Y2Axis.IsVisible = true;
                pane.Y2Axis.Title.FontSpec.FontColor = Color.Lime;
                pane.Y2Axis.Scale.FontSpec.FontColor = Color.Lime;
                pane.Y2Axis.Color = Color.Lime;
                pane.Y2Axis.Title.FontSpec.Size = fontSize;
                pane.Y2Axis.Scale.FontSpec.Size = fontSize;
                pane.Y2Axis.MajorGrid.IsVisible = false;
                pane.Y2Axis.MajorTic.Color = Color.Gray;
                pane.Y2Axis.MinorTic.Size = 0.0f;
                pane.Y2Axis.MajorGrid.IsZeroLine = false;


                var CanCurrentYAxis = new Y2Axis("");
                pane.Y2AxisList.Add(CanCurrentYAxis);
                CanCurrentYAxis.IsVisible = true;
                CanCurrentYAxis.Title.FontSpec.FontColor = Color.Purple;
                CanCurrentYAxis.Color = Color.Purple;
                CanCurrentYAxis.Scale.FontSpec.FontColor = Color.Purple;
                CanCurrentYAxis.Title.FontSpec.Size = fontSize;
                CanCurrentYAxis.Scale.FontSpec.Size = fontSize;
                CanCurrentYAxis.MajorGrid.IsVisible = false;
                CanCurrentYAxis.MajorGrid.IsZeroLine = false;

                //
                // listForce = new PointPairList();
                // curveForce = pane.AddCurve("Act_F(N)", listForce, Color.FromArgb(80, 160, 255), SymbolType.None);
                // curveForce.Line.Width = 2;
                // curveForce.IsY2Axis = false;
                // curveForce.YAxisIndex = 0;
                //
                //
                // listCanCurrent = new PointPairList();
                // curveCanCurrent = pane.AddCurve("Act_I(A)", listCanCurrent, Color.Purple, SymbolType.None);
                // curveCanCurrent.Line.Width = 2;
                // curveCanCurrent.IsY2Axis = true;
                // curveCanCurrent.YAxisIndex = pane.Y2AxisList.Count - 1;

                listDaqCurrent = new PointPairList();
                curveDaqCurrent = pane.AddCurve("DAQ_I(A)", listDaqCurrent, Color.Lime, SymbolType.None);
                curveDaqCurrent.Line.Width = 2;
                curveDaqCurrent.IsY2Axis = true;
                curveDaqCurrent.YAxisIndex = 0;


                zedGraphRealChart.GraphPane.XAxis.Scale.Max = ClsGlobal.XDuration;
                zedGraphRealChart.GraphPane.XAxis.Scale.Min = 0.0;


                zedGraphRealChart.GraphPane.XAxis.Scale.MagAuto = false;
                zedGraphRealChart.GraphPane.XAxis.Scale.FormatAuto = false;


                zedGraphRealChart.GraphPane.YAxis.Scale.MagAuto = false;
                zedGraphRealChart.GraphPane.YAxis.Scale.FormatAuto = false;

                zedGraphRealChart.GraphPane.Y2Axis.Scale.MagAuto = false;
                zedGraphRealChart.GraphPane.Y2Axis.Scale.FormatAuto = false;

                CanCurrentYAxis.Scale.MagAuto = false;
                CanCurrentYAxis.Scale.FormatAuto = false;


                zedGraphRealChart.AxisChange();

                zedGraphRealChart.Invalidate();

                zedGraphRealChart.Refresh();
            }

            catch (Exception ex)
            {
                MessageBox.Show(@"初始化曲线显示失败！" + ex.Message, @"提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "初始化曲线显示失败！" + ex.Message, "初始化");
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
                // 快速提取数据（最小化锁范围）

                var currentTimestamp = Dispstopwatch.ElapsedMilliseconds;

                var dispTime = DispinitialDateTime.AddMilliseconds(currentTimestamp - DispinitialTimestamp);


                // DateTime dispTime = DateTime.Now;
                lock (bufferLock)
                {
                    // 交换缓冲区
                    (activeWriteBuffer, readyReadBuffer) = (readyReadBuffer, activeWriteBuffer);

                    // 创建数据快照
                    daqSnapshot = ProcessDaqCurrentData();
                    forceSnapshot = readyReadBuffer.ToList();

                    activeWriteBuffer.Clear();
                }

                // UI更新（独立锁）
                if (Monitor.TryEnter(graphLock, 1000))
                    try
                    {
                        UpdateGraphDisplay(dispTime, forceSnapshot, daqSnapshot);
                        UpdateDataGridView(forceSnapshot, daqSnapshot);
                    }
                    finally
                    {
                        Monitor.Exit(graphLock);
                    }
            }
            catch (Exception ex)
            {
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "定时刷新数据曲线出错: " + ex.Message, "定时刷新数据曲线");
            }
        }


        public void UpdateDataGridView(List<byte[]> dataQueue, double[] DaqData)
        {
            if (dgvRealData == null) return;
            if (dgvRealData.InvokeRequired)
            {
                // dgvRealData.Invoke(new Action<ConcurrentQueue<byte[]>, double[]>(UpdateDataGridView), dataQueue, DaqData);
                dgvRealData.Invoke(new Action<List<byte[]>, double[]>(UpdateDataGridView), dataQueue, DaqData);
                return;
            }

            //   lock (graphLock)
            {
                try
                {
                    // 计算最大值
                    var maxDaq = DaqData.Length > 0 ? DaqData.Max() : 0.0;
                    var minDaq = DaqData.Length > 0 ? DaqData.Min() : 0.0;

                    var maxForce = double.MinValue;
                    var minForce = double.MaxValue;

                    var maxCurrent = double.MinValue;
                    var minCurrent = double.MaxValue;

                    byte FaultMode = 255;

                    foreach (var data in dataQueue)
                        if (data.Length >= 2)
                        {
                            double forceValue = 0;
                            double currentValue = 0;
                            byte faultflg = 0;
                            double torque = 0;
                            var parseMsg = ClsBitFieldParser.ParseClampData(data,
                                EMBNameToRecvCanForceScale[CurrentDev],
                                EMBNameToRecvCanTorqueScale[CurrentDev],
                                EMBNameToRecvCanCurrentScale[CurrentDev],
                                out forceValue, out faultflg, out torque, out currentValue);

                            if (parseMsg.IndexOf("OK") < 0)
                                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "CAN数据解析出错: " + parseMsg,
                                    "CAN数据解析");


                            maxForce = Math.Max(maxForce, forceValue);
                            minForce = Math.Min(minForce, forceValue);


                            maxCurrent = Math.Max(maxCurrent, currentValue);
                            minCurrent = Math.Min(minCurrent, currentValue);


                            FaultMode = (byte)faultflg;
                        }

                    if (maxForce == double.MinValue) maxForce = 0.0;
                    if (minForce == double.MaxValue) minForce = 0.0;
                    if (maxCurrent == double.MinValue) maxCurrent = 0.0;
                    if (minCurrent == double.MaxValue) minCurrent = 0.0;


                    // 初始化列（首次运行时）
                    if (dgvRealData.Columns.Count == 0)
                    {
                        dgvRealData.Columns.Add(new DataGridViewCheckBoxColumn
                        {
                            Name = "colCheckBox",
                            HeaderText = "选择",
                            Width = 60 // CheckBox列稍窄
                        });

                        var paramColumn = new DataGridViewTextBoxColumn
                        {
                            Name = "colParameter",
                            HeaderText = "参数",
                            Width = 120
                        };

                        var maxValueColumn = new DataGridViewTextBoxColumn
                        {
                            Name = "colMaxValue",
                            HeaderText = "最大值",
                            Width = 120,
                            DefaultCellStyle = new DataGridViewCellStyle
                            {
                                Format = "F3" // 统一数字格式
                            }
                        };

                        var minValueColumn = new DataGridViewTextBoxColumn
                        {
                            Name = "colMinValue",
                            HeaderText = "最小值",
                            Width = 120,
                            DefaultCellStyle = new DataGridViewCellStyle
                            {
                                Format = "F3" // 统一数字格式
                            }
                        };


                        dgvRealData.Columns.AddRange(paramColumn, maxValueColumn, minValueColumn);
                    }

                    // 更新或添加行
                    UpdateDataRow("DAQ_I", maxDaq, minDaq);
                    UpdateDataRow("Act_F", maxForce, minForce);
                    UpdateDataRow("Act_I", maxCurrent, minCurrent);
                    UpdateDataRow("FaultCode", FaultMode, "");
                }
                catch (Exception ex)
                {
                    ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "更新数据表格出错: " + ex.Message, "数据表格");
                }
            }
        }

        private void UpdateDataRow(string parameter, object maxValue, object minValue)
        {
            try
            {
                // 查找现有行
                foreach (DataGridViewRow row in dgvRealData.Rows)
                    if (row.Cells["colParameter"].Value?.ToString() == parameter)
                    {
                        row.Cells["colMaxValue"].Value = maxValue;
                        row.Cells["colMinValue"].Value = minValue;
                        return;
                    }

                // 添加新行
                var idx = dgvRealData.Rows.Add(
                    true, // CheckBox初始状态
                    parameter, // 参数列
                    maxValue, // 值列
                    minValue
                );
            }
            catch (Exception ex)
            {
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "更新数据表格出错: " + ex.Message, "更新表格");
            }
        }


        public void UpdateGraphDisplay(DateTime dispTime, List<byte[]> dataQueue, double[] DaqData)

        {
            if (zedGraphRealChart == null) return;


            if (zedGraphRealChart.InvokeRequired)
            {
                zedGraphRealChart.Invoke(new Action<DateTime, List<byte[]>, double[]>(UpdateGraphDisplay), dispTime,
                    dataQueue, DaqData);
                return;
            }


            {
                try
                {
                    foreach (var item in curveDictionary)
                    {
                        item.Value.lineItem.IsVisible = item.Value.IsActive;

                        if (item.Key == "Act_F") zedGraphRealChart.GraphPane.YAxis.IsVisible = item.Value.IsActive;
                        if (item.Key == "DAQ_I") zedGraphRealChart.GraphPane.Y2Axis.IsVisible = item.Value.IsActive;
                        if (item.Key == "Act_I")
                            zedGraphRealChart.GraphPane.Y2AxisList[1].IsVisible = item.Value.IsActive;
                    }


                    var DaqDeltTime = 0.0;
                    var CanDeltTime = 0.0;

                    var recvSpan = dispTime.Subtract(lastGraphyTime).TotalSeconds;
                    var graphyHeadertime = lastGraphyTime.Subtract(runBegin).TotalSeconds;


                    var DaqLens = DaqData.Length;

                    if (DaqLens > 0) DaqDeltTime = recvSpan / (double)DaqLens;

                    var IsAxisChanged = false;

                    for (var i = 0; i < DaqLens; i++)
                        listDaqCurrent.Add(graphyHeadertime + (double)i * DaqDeltTime, DaqData[i]);

                    if (listDaqCurrent != null && listDaqCurrent.Count > 0 &&
                        listDaqCurrent[listDaqCurrent.Count - 1].X - listDaqCurrent[0].X > ClsGlobal.XDuration)
                    {
                        // 移除最旧的一半数据点

                        var MidTime = (listDaqCurrent[0].X + listDaqCurrent[listDaqCurrent.Count - 1].X) / 2.0;

                        listDaqCurrent.RemoveAll(p => p.X < MidTime);

                        zedGraphRealChart.GraphPane.XAxis.Scale.Max = listDaqCurrent[0].X + ClsGlobal.XDuration;
                        zedGraphRealChart.GraphPane.XAxis.Scale.Min = listDaqCurrent[0].X;

                        IsAxisChanged = true;
                    }

                    if (dataQueue.Count > 0) CanDeltTime = recvSpan / (double)dataQueue.Count;
                    var j = 0;


                    // 解析数据并填充曲线
                    foreach (var data in dataQueue)
                        // 假设数据格式：每个数据包包含一个short类型的力值
                        if (data.Length >= 2)
                        {
                            double forceValue = 0;
                            double currentValue = 0;
                            byte faultflg = 0;
                            double torque = 0;
                            var parseMsg = ClsBitFieldParser.ParseClampData(data,
                                EMBNameToRecvCanForceScale[CurrentDev],
                                EMBNameToRecvCanTorqueScale[CurrentDev],
                                EMBNameToRecvCanCurrentScale[CurrentDev],
                                out forceValue, out faultflg, out torque, out currentValue);

                            if (parseMsg.IndexOf("OK") < 0)
                                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "CAN数据解析出错: " + parseMsg,
                                    "CAN数据解析");

                            listForce.Add(graphyHeadertime + (double)j * CanDeltTime, forceValue);
                            listCanCurrent.Add(graphyHeadertime + (double)j * CanDeltTime, currentValue);

                            j++;
                        }


                    if (listForce != null && listForce.Count > 0 &&
                        listForce[listForce.Count - 1].X - listForce[0].X > ClsGlobal.XDuration)
                    {
                        // 移除最旧的一半数据点

                        var MidTime = (listForce[0].X + listForce[listForce.Count - 1].X) / 2.0;

                        listForce.RemoveAll(p => p.X < MidTime);
                        listCanCurrent.RemoveAll(p => p.X < MidTime);

                        if (!IsAxisChanged)
                        {
                            zedGraphRealChart.GraphPane.XAxis.Scale.Max = listForce[0].X + ClsGlobal.XDuration;
                            zedGraphRealChart.GraphPane.XAxis.Scale.Min = listForce[0].X;
                            IsAxisChanged = true;
                        }
                    }


                    zedGraphRealChart.AxisChange();
                    zedGraphRealChart.Invalidate();
                }
                catch (Exception ex)
                {
                    ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "更新曲线显示出错: " + ex.Message, "曲线显示");
                    // 记录异常
                }
                finally
                {
                    lastGraphyTime = dispTime;
                }
            }
        }


        /// <summary>
        /// 更新实时曲线（仅 DAQ_I 一条曲线）。
        /// 传入的数据应为工程值（已做零点、比例、偏置与滤波），方法内部会按照采样率
        /// 将样本映射到 X 轴（单位：秒），并维持 X 轴固定时窗（ClsGlobal.XDuration）。
        /// </summary>
        /// <param name="daqData">
        /// 一次刷新的 DAQ_I 数据段（工程值）。允许为空或长度为 0（此时不做任何更新）。
        /// </param>
        public void UpdateGraphDisplay2(double[] daqData)
        {
            // 控件未初始化直接返回
            if (zedGraphRealChart == null) return;

            // 跨线程封送
            if (zedGraphRealChart.InvokeRequired)
            {
                zedGraphRealChart.Invoke(new Action<double[]>(UpdateGraphDisplay2), daqData);
                return;
            }

            try
            {
                if (daqData == null || daqData.Length == 0)
                    return;

                // ===== 只显示 DAQ_I，对应 Y2 轴；隐藏其他轴（若存在则隐藏）=====
                // 如果你仍然使用 curveDictionary 来控制可见性，这里也把 DAQ_I 打开
                if (curveDictionary != null && curveDictionary.TryGetValue("DAQ_I", out var op))
                {
                    op.IsActive = true;
                    if (op.lineItem != null) op.lineItem.IsVisible = true;
                }

                var pane = zedGraphRealChart.GraphPane;
                pane.YAxis.IsVisible = false;                    // 只画 DAQ_I，不用左侧 Y 轴
                pane.Y2Axis.IsVisible = true;                    // 开启 Y2
                if (pane.Y2AxisList.Count > 1)                   // 如果曾经加过第二个 Y2（Act_I），这里隐藏
                    pane.Y2AxisList[1].IsVisible = false;

                // ===== 把采样映射到时间轴 =====
                // 采样周期（秒/点）
                if (ClsGlobal.DaqFrequency <= 0)
                    throw new InvalidOperationException("DaqFrequency 未正确设置。");

                double dt = 1.0 / ClsGlobal.DaqFrequency;

                // 本次追加的起始 X（秒）。
                // 若已有点，则从最后一个点的下一步开始；否则从 0 开始。
                double xStart;
                if (listDaqCurrent != null && listDaqCurrent.Count > 0)
                    xStart = listDaqCurrent[listDaqCurrent.Count - 1].X + dt;
                else
                    xStart = 0.0;

                // 逐点追加（X 轴为相对时间，单位：秒）
                for (int i = 0; i < daqData.Length; i++)
                {
                    listDaqCurrent.Add(xStart + i * dt, daqData[i]);
                }

                // ===== 维持固定时窗（滑动窗口）=====
                if (listDaqCurrent != null && listDaqCurrent.Count > 0)
                {
                    double firstX = listDaqCurrent[0].X;
                    double lastX = listDaqCurrent[listDaqCurrent.Count - 1].X;

                    if (lastX - firstX > ClsGlobal.XDuration)
                    {
                        // 移除最旧的一半，避免频繁整体拷贝导致卡顿
                        double mid = (firstX + lastX) / 2.0;
                        listDaqCurrent.RemoveAll(p => p.X < mid);

                        // 滑动 X 轴范围到最新窗口
                        pane.XAxis.Scale.Min = listDaqCurrent[0].X;
                        pane.XAxis.Scale.Max = listDaqCurrent[0].X + ClsGlobal.XDuration;
                    }
                }

                // 刷新
                zedGraphRealChart.AxisChange();
                zedGraphRealChart.Invalidate();

                // =====（可选）维护 lastGraphyTime，用于你其他地方的时间基准 =====
                // 以样点数与采样率推前 lastGraphyTime，保持与旧代码兼容
                if (daqData.Length > 0)
                {
                    double spanSec = daqData.Length * (1.0 / ClsGlobal.DaqFrequency);
                    lastGraphyTime = lastGraphyTime.AddSeconds(spanSec);
                }
            }
            catch (Exception ex)
            {
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "更新曲线显示出错: " + ex.Message, "曲线显示");
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
                    zedGraphRealChart.GraphPane.XAxis.Scale.Max =
                        listForce[listForce.Count - 1].X + ClsGlobal.XDuration;
                    zedGraphRealChart.GraphPane.XAxis.Scale.Min = listForce[listForce.Count - 1].X;
                    listForce.Clear();

                    // 立即刷新图表
                    zedGraphRealChart.AxisChange();
                    zedGraphRealChart.Invalidate();
                }
            }
        }


        private double[] ProcessDaqCurrentData()
        {
            var RecCount = DaqAiDispData.Count;
            var totalCount = DaqAiDispData.Take(RecCount).Sum(arr => arr.Length);


            var result = new double[totalCount];
            var index = 0;
            var RecCounter = 0;
            while (DaqAiDispData.TryDequeue(out var arr) && RecCounter < RecCount)
            {
                Array.Copy(arr, 0, result, index, arr.Length);
                index += arr.Length;
                RecCounter++;
            }

            for (var i = 0; i < totalCount; i++)
                result[i] = (result[i] - ParaNameToZeroValue[CurrentDev]) * ParaNameToScale[CurrentDev] +
                            ParaNameToOffset[CurrentDev];

            var filterCurrent = ClsDataFilter.MakeMedianFilterReducePoint(ref result, ClsGlobal.MedianLens);


            return filterCurrent;
        }

        #endregion


        private void LoadEmbControler()
        {
            try
            {
                for (var i = 0; i < 6; i++)
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


                for (var i = 0; i < 6; i++)
                {
                    EmbGroup[i].CtrlRunning.Enabled = false; //单个启动按钮设为不允许，启动之后才允许
                    var index = i;
                    EmbGroup[i].CtrlJoinTest.CheckedChanged += (sender, e) => JoinEmbChanged(sender, e, index);

                    // EmbGroup[i].CtrlCurrentEmb.CheckedChanged += (sender, e) => CurrentEmbChanged(sender, e, index); // 界面上没有这个控件，暂时注释掉


                    EmbGroup[i].CtrlRunning.CheckedChanged += (sender, e) =>
                    {
                        RuningStatusChanged(sender, ((UISwitch)sender).Active, index);
                    };

                    EmbGroup[i].CtrlRunning.Click += (sender, e) => RunningClick(sender, e, index);
                    EmbGroup[i].CtrlPower.Click += (sender, e) => PowerClick(sender, e, index);
                    EmbGroup[i].CtrlPower.KeyPress += (sender, e) => CtrlPower_KeyHandler(sender, e, index);
                    EmbGroup[i].CtrlPower.KeyDown += (sender, e) => CtrlPower_KeyHandler(sender, e, index);
                    EmbGroup[i].CtrlPower.KeyUp += (sender, e) => CtrlPower_KeyHandler(sender, e, index);
                    //EmbGroup[i].CtrlPower.CheckedChanged += (sender, e) => PowerClick(sender, e, index);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(@"初始化组件失败！" + ex.Message);
            }
        }

        private void CtrlPower_KeyHandler(object sender, EventArgs e, int index)
        {
            return;
            //throw new NotImplementedException();
        }

        private void RunningClick(object sender, EventArgs e, int index)
        {
            if (!EmbGroup[index].CtrlPower.Checked && EmbGroup[index].CtrlRunning.Checked) //运行状态
            {
                MessageBox.Show(@"请先打开电源！");
                EmbGroup[index].CtrlRunning.Checked = false;
                return;
            }
        }

        private bool _isCtrlPowerPressing = false;
        private GlobalConfig _cfg;

        private async void PowerClick(object sender, EventArgs e, int index)
        {
            if (_isCtrlPowerPressing)
            {
                return;
            }

            _isCtrlPowerPressing = true;
            EmbGroup[index].CtrlPower.Enabled = false; // 禁用按钮，防止重复点击
            EPBGroupBox.Enabled = false; // 禁用整个组框，防止其他操作
            try
            {
                if (!IsTestConfirm)
                {
                    MessageBox.Show(@"请先确认试验信息！");
                    EmbGroup[index].CtrlPower.Toggle();

                    // EmbGroup[index].CtrlPower.Checked = false;
                    //
                    EmbGroup[index].CtrlPower.Refresh();
                    return;
                }


                if (!EmbGroup[index].CtrlPower.Checked && EmbGroup[index].CtrlRunning.Checked) //运行状态想关电源
                {
                    MessageBox.Show(@"请先停止运行再关闭电源！");
                    EmbGroup[index].CtrlPower.Checked = true;
                    return;
                }

                if (!EmbGroup[index].CtrlPower.Checked && !EmbGroup[index].CtrlRunning.Checked) //非运行状态想关电源
                {
                    // MessageBox.Show("调用执行关闭分开关的函数！");
                    // var mainForm = this.MdiParent as Main_Frm;


                    //string powerMsg = mainForm.PowerClose(index / 2 + 1);
                    //if (powerMsg.IndexOf("OK") < 0)
                    //{
                    //    RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "关闭EMB" + (index + 1).ToString() + "电源失败!" + powerMsg);
                    //    ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "关闭EMB" + (index + 1).ToString() + "电源开关失败!", "电源开关操作");

                    //}
                    //else
                    //{
                    //    RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "关闭EMB" + (index + 1).ToString() + "电源!");
                    //    ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "关闭EMB" + (index + 1).ToString() + "电源开关!", "电源开关操作");

                    //}


                    var OpenSuccess = await ClosePowerChannel((byte)index, ClsGlobal.SerialPortRetrys);
                    if (!OpenSuccess)
                    {
                        RtbInfo.Invoke(new SetTextCallback(SetInfoText),
                            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "关闭EMB" + (index + 1).ToString() +
                            "继电器开关失败!");
                        ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError,
                            "关闭EMB" + (index + 1).ToString() + "继电器开关失败!", "串口操作");
                        ClsGlobal.PowerStatus[index] = 2;
                    }
                    else
                    {
                        RtbInfo.Invoke(new SetTextCallback(SetInfoText),
                            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "关闭EMB" + (index + 1).ToString() +
                            "继电器开关!");
                        ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation,
                            "关闭EMB" + (index + 1).ToString() + "继电器开关!", "UI 操作");
                        ClsGlobal.PowerStatus[index] = 1;


                        /*if (EmbGroup[index].IsEnabel)
                    {
                        EmbGroup[index].CtrlAlert.State = UILightState.Off;
                        EmbGroup[index].CtrlAlert.OffCenterColor = Color.FromArgb(140, 140, 140);
                        EmbGroup[index].CtrlAlert.OffColor = Color.FromArgb(140, 140, 140);
                    }*/
                    }


                    return;
                }


                if (EmbGroup[index].CtrlPower.Checked)
                {
                    // MessageBox.Show("调用执行打开分开关的函数！");

                    // var mainForm = this.MdiParent as Main_Frm;

                    //string powerMsg = mainForm.PowerOpen(index / 2 + 1);
                    //if (powerMsg.IndexOf("OK") < 0)
                    //{
                    //    RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "打开EMB" + (index + 1).ToString() + "电源失败!" + powerMsg);
                    //    ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "打开EMB" + (index + 1).ToString() + "电源开关失败!", "电源开关操作");

                    //}
                    //else
                    //{
                    //    RtbInfo.Invoke(new SetTextCallback(SetInfoText), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "打开EMB" + (index + 1).ToString() + "电源!");
                    //    ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation, "打开EMB" + (index + 1).ToString() + "电源开关!", "电源开关操作");

                    //}


                    var OpenSuccess = await OpenPowerChannel((byte)index, ClsGlobal.SerialPortRetrys);
                    if (!OpenSuccess)
                    {
                        RtbInfo.Invoke(new SetTextCallback(SetInfoText),
                            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "打开EMB" + (index + 1).ToString() +
                            "继电器开关失败!");
                        ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError,
                            "打开EMB" + (index + 1).ToString() + "继电器开关失败!", "串口操作");
                        ClsGlobal.PowerStatus[index] = 1;
                    }
                    else
                    {
                        RtbInfo.Invoke(new SetTextCallback(SetInfoText),
                            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "打开EMB" + (index + 1).ToString() +
                            "继电器开关!");
                        ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation,
                            "打开EMB" + (index + 1).ToString() + "继电器开关!", "UI 操作");
                        ClsGlobal.PowerStatus[index] = 2;
                        /*if (EmbGroup[index].IsEnabel)
                    {
                        EmbGroup[index].CtrlAlert.State = UILightState.On;
                        EmbGroup[index].CtrlAlert.OffCenterColor = Color.FromArgb(140, 140, 140);
                        EmbGroup[index].CtrlAlert.OffColor = Color.FromArgb(140, 140, 140);
                    }*/
                    }


                    return;
                }
            }
            finally
            {
                _isCtrlPowerPressing = false;

                EmbGroup[index].CtrlPower.Enabled = true; // 重新启用按钮
                EPBGroupBox.Enabled = true; // 重新启用整个组框
            }
        }


        // 关掉电源通道，目前操作为空
        private async Task<bool> ClosePowerChannel(byte ChannelNo, int maxRetries)
        {
            return false;
        }


        private async Task<bool> OpenPowerChannel(byte ChannelNo, int maxRetries)
        {
            return false;
        }


        private void JoinEmbChanged(object sender, EventArgs e, int index)
        {
            var checkBox = (CheckEdit)sender;
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
                StartEmbControlTimer(index);
            // EmbGroup[index].CtrlAlert.OffCenterColor = Color.FromArgb(140, 140, 140);
            // EmbGroup[index].CtrlAlert.OffColor = Color.FromArgb(140, 140, 140);
            // EmbGroup[index].CtrlAlert.OnCenterColor = Color.Lime;
            // EmbGroup[index].CtrlAlert.OnColor = Color.Lime;
            // EmbGroup[index].CtrlAlert.State = UILightState.Blink;
            else
                StopEmbControlTimer(index);
            // EmbGroup[index].CtrlAlert.State = UILightState.On;
        }


        #region 周期定时处理

        // 初始化12个定时器
        private void InitializeEmbControlTimers(uint TimeInterval)
        {
            try
            {
                EmbControlTimers.Clear();
                for (var i = 0; i < 12; i++)
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
                if (Interlocked.Increment(ref activeTimersCount) == 1) timeBeginPeriod(DEFAULT_RESOLUTION);

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
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError,
                    "启动EMB" + EmbIndex.ToString() + "定时控制失败！" + ex.Message, "CAN通信");
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
                if (Interlocked.Decrement(ref activeTimersCount) == 0) timeEndPeriod(DEFAULT_RESOLUTION);

                return true;
            }

            catch (Exception ex)
            {
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError,
                    "停止EMB" + index.ToString() + "定时控制失败！" + ex.Message, "CAN通信");
                return false;
            }
        }

        // 定时器回调处理
        private void EmbControlTimerHandler(UIntPtr uTimerID, uint uMsg, UIntPtr dwUser, UIntPtr dw1, UIntPtr dw2)
        {
            //传入EMB处理的序号
            var index = (int)dwUser.ToUInt32();
            if (index < 0 || index >= 6)
                return;
            try
            {
                var timer = EmbControlTimers[index];

                // 原先是can通信操作，需要改为6002的AO操作
                Action action = () =>
                {
                    //  SafeLogError("Enter No " + index.ToString());
                    timer.CycleCounter[index]++;

                    if (timer.CycleCounter[index] % 6000 == 0) //60秒记录一次
                        ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation,
                            "EMB" + (index + 1).ToString() + " 发送夹紧指令 ", "CAN通信");

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
                    Invoke(action);
                else
                    action();
            }
            catch (Exception ex)
            {
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError,
                    "EMB" + (index + 1).ToString() + "定时发送指令出错！" + ex.Message, "定时发送指令");
            }
        }

        // 停止所有定时器
        public void StopAllEmbControlTimers()
        {
            for (var i = 0; i < 6; i++) StopEmbControlTimer(i);
        }

        // 设置定时器间隔
        public bool SetEmbControlTimerInterval(int index, uint newInterval)
        {
            if (index < 0 || index >= 6)
                return false;

            var timer = EmbControlTimers[index];
            if (timer.Interval == newInterval)
                return true;

            var wasRunning = timer.IsRunning;
            if (wasRunning) StopEmbControlTimer(index);

            timer.Interval = newInterval;

            if (wasRunning) return StartEmbControlTimer(index);

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


        #region UI滚动消息

        private delegate void SetTextCallback(string text);

        private void SetInfoText(string text)
        {
            RtbInfo.AppendText($"{text}\n");

            RtbInfo.ScrollToCaret();
        }

        #endregion

        private void BtnTest_Click(object sender, EventArgs e)
        {
            // _do.SetEpb(channelNo: 1, directionIsForward: true);
            // _do.SetEpb(channelNo: 9, directionIsForward: true);
        }

        /// <summary>
        /// 按钮点击事件，查看运行日志
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
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

        /// <summary>
        /// 按钮点击事件，查看错误日志
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
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

        /// <summary>
        /// 切换开关事件，测试代码，正式运行时请删除
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void toggleSwitch1_Toggled(object sender, EventArgs e)
        {
            // 打开所有epb
            for (int i = 0; i < 12; i++)
            {
                _do.SetEpb(channelNo:i+1, directionIsForward:this.toggleSwitch1.IsOn);
            }

            // 打开气缸测试
            // _ao.SetPercent("Cylinder1", 50); // => ~5V
            // _ao.SetPercent("Cylinder2", 50); // => ~5V
        }


        /// <summary>
        /// 窗体关闭事件，释放资源
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void FrmEpbMainMonitor_FormClosed(object sender, FormClosedEventArgs e)
        {
            // _do?.Dispose(); // 释放DO对象资源
            // _ao?.Dispose(); // 释放AO对象资源
            _do?.AllOff(); // 停止所有EPB操作
            _do?.Dispose();
            _ao?.ResetAll(); // 停止所有AO操作
            _ao?.Dispose(); // 释放AO对象资源

            twoDeviceAiAcquirer.Stop();

            twoDeviceAiAcquirer?.Dispose(); 

            



            //base.OnFormClosed(e);
        }

        private async void BtnStartTest_Click(object sender, EventArgs e)
        {
            try
            {
                

                twoDeviceAiAcquirer.Start();




                // 4) 组装 EpbManager（把回调委托接进去）
                _epb = new EpbManager(
                    cfg: _cfg,
                    doController: _do,
                    aoController: _ao,
                    acq: twoDeviceAiAcquirer,
                    log: logger);

                // 5) 启动“卡钳1”通道
                //    StartChannel 内部会根据 Test.TestTarget 次数、PeriodMs 周期、Groups 错峰等自动循环
                // _epb.StartChannel(2); //界面卡顿，注释
                await _epb.StartChannelAsync(2);

                // UI 提示
                RtbInfo?.AppendText($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  > 卡钳1测试已启动\n");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"启动卡钳1测试失败：{ex.Message}", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }


        // 工程值批次到达（dev="Dev1" 或 "Dev2"；eng 为 [通道, 样本]）
        private void Acq_OnEngBatch(string dev, double[,] eng, DateTime current, DateTime last)
        {
            if (InvokeRequired)
            {
                // 切回 UI 线程，避免跨线程操作控件异常
                BeginInvoke(new Action(() => Acq_OnEngBatch(dev, eng, current, last)));
                return;
            }

            // ===== 示例1：读取指定通道的“最后一个样本”并显示到 SunnyUI 的 UILabel =====
            // 你在 TwoDeviceAiAcquirer 里已把“最近值快照”维护好了，也暴露了 ReadCurrent/ReadPressure 简便查询接口：
            //   ReadCurrent(int epbChannel), ReadPressure(int id)  —— 直接拿最近值用来显示 UI 即可。:contentReference[oaicite:6]{index=6}
            //double p1 = _acq.ReadPressure(1);           // 压力1 (工程值)
            
            
            
            if (dev == "Dev1")
            {
                // 1) 取第0通道的一个点，顺便刷新数值显示（你原来就是取 [0,0]）
                double epb1 = eng[1, 0];                 // EPB1 电流 (工程值)
                textEditCurrent1.Text = $"{epb1:F2} A";   // 假设 textEditCurrent1 是显示 EPB1 电流的控件

                // 2) 提取第0维（第0通道）的整段样本，准备绘制
                int samples = eng.GetLength(1);           // 列数 = 样本数
                if (samples > 0)
                {
                    // 建议用循环拷贝（最安全、与 .NET 4.8 兼容）
                    var daqI = new double[samples];
                    for (int i = 0; i < samples; i++)
                        daqI[i] = eng[1, i];

                    // 3) 调用你的单曲线绘制方法（内部已做 Invoke 封送，可直接调用）
                    UpdateGraphDisplay2(daqI);
                }


            }

            // uiLabelPressure1.Text = $"{p1:F2} bar";
            // uiLabelEpb1.Text = $"{epb1:F2} A";





            // // ===== 示例2：往 ZedGraph 追加点（以“最后一个样本”作为当前时刻值）=====
            // // 假设 pane.CurveList[0] 是 Pressure 曲线，CurveList[1] 是 EPB1 电流曲线
            // var pane = zedGraphControl1.GraphPane;
            // var t = (current - DateTime.Today).TotalSeconds; // 简单用秒作横轴
            //
            // // 这里假设你已在别处创建了两条曲线，命名 "P1" 与 "I1"
            // var pCurve = pane.CurveList["P1"] as ZedGraph.LineItem;
            // var iCurve = pane.CurveList["I1"] as ZedGraph.LineItem;
            //
            // if (pCurve != null) pCurve.AddPoint(t, p1);
            // if (iCurve != null) iCurve.AddPoint(t, epb1);
            //
            // zedGraphControl1.AxisChange();
            // zedGraphControl1.Invalidate(); // 触发重绘
        }
        

        private void BtnWarnLog_Click(object sender, EventArgs e)
        {

            try
            {
                string OutFile = System.Environment.CurrentDirectory + @"\WarnLog.txt";
                ClsLogProcess.ViewWarnData(ref LogWarn, OutFile);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void BtnStop_Click(object sender, EventArgs e)
        {
            try
            {
                _epb.StopChannel(2);
            }
            catch (Exception ex)
            {
                RtbInfo?.AppendText($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  > 停止卡钳2测试失败\n");
            }
        }

        private void uiLabel27_Click(object sender, EventArgs e)
        {

        }
    }
}