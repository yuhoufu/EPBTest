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
using AsyncListener;
using System.Diagnostics;
using System.IO;
using System.Xml.Serialization;
using IO.NI;

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
        private const int MaxErrors = 100000;
        private const int MaxInfos = 100000;

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

        public  FormLoggerAdapter logger;
        public DoController doCtrl;

        public FrmEpbMainMonitor()
        {
            InitializeComponent();



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
            logger = new FormLoggerAdapter(MaxInfos, MaxErrors, LogInformation, LogError, this);
            doCtrl = new DoController(logger);


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
                LoadTestConfigFromXml();
                //LoadEMBHandlerAndFrameNo();

                RtbInfo.Invoke(new SetTextCallback(SetInfoText), "1. 编辑试验信息并确认");
                //   RtbInfo.Invoke(new SetTextCallback(SetInfoText), "2. CAN卡初始化");
                //   RtbInfo.Invoke(new SetTextCallback(SetInfoText), "3. 打开各个电源开关");
                RtbInfo.Invoke(new SetTextCallback(SetInfoText), "2. 自学习/开始试验");


                ClsDiskProc.MakeSubDir(testConfig.StoreDir);

                var MainDrive = testConfig.StoreDir.Trim().Substring(0, 2);

                var LastSpace = ClsDiskProc.GetHardDiskSpace(MainDrive);
                if (LastSpace == 0) MessageBox.Show("指定磁盘不存在！");

                if (LastSpace < 50)
                {
                }
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


                listForce = new PointPairList();
                curveForce = pane.AddCurve("Act_F(N)", listForce, Color.FromArgb(80, 160, 255), SymbolType.None);
                curveForce.Line.Width = 2;
                curveForce.IsY2Axis = false;
                curveForce.YAxisIndex = 0;


                listCanCurrent = new PointPairList();
                curveCanCurrent = pane.AddCurve("Act_I(A)", listCanCurrent, Color.Purple, SymbolType.None);
                curveCanCurrent.Line.Width = 2;
                curveCanCurrent.IsY2Axis = true;
                curveCanCurrent.YAxisIndex = pane.Y2AxisList.Count - 1;

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

        private void RunningClick(object sender, EventArgs e, int index)
        {
            if (!EmbGroup[index].CtrlPower.Active && EmbGroup[index].CtrlRunning.Active) //运行状态
            {
                MessageBox.Show(@"请先打开电源！");
                EmbGroup[index].CtrlRunning.Active = false;
                return;
            }
        }


        private async void PowerClick(object sender, EventArgs e, int index)
        {
            if (!IsTestConfirm)
            {
                MessageBox.Show(@"请先确认试验信息！");
                EmbGroup[index].CtrlPower.Active = false;
                return;
            }


            if (!EmbGroup[index].CtrlPower.Active && EmbGroup[index].CtrlRunning.Active) //运行状态想关电源
            {
                MessageBox.Show(@"请先停止运行再关闭电源！");
                EmbGroup[index].CtrlPower.Active = true;
                return;
            }

            if (!EmbGroup[index].CtrlPower.Active && !EmbGroup[index].CtrlRunning.Active) //非运行状态想关电源
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


            if (EmbGroup[index].CtrlPower.Active)
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
    }
}