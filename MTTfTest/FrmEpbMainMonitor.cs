using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Serialization;
using Config;
using Controller;
using DataOperation;
using DevExpress.XtraEditors;
using IO.NI;
using MtEmbTest;
using MTEmbTest.UIHelpers;
using NationalInstruments.DAQmx;
using Sunny.UI;
using ZedGraph;
using Task = NationalInstruments.DAQmx.Task;
//using AsyncListener;
using TestConfig = DataOperation.TestConfig;
using Timer = System.Threading.Timer;

namespace MTEmbTest
{
    public partial class FrmEpbMainMonitor : Form
    {
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

        private const int MaxErrors = 100000;
        private const int MaxInfos = 100000;
        private const int MaxWarns = 100000;

        // private ConcurrentQueue<CanData> dataQueue = new ConcurrentQueue<CanData>();
        private const int CacheLens = 6000; //每秒100帧，3秒处理一次，最多缓存6秒


        private const int DeviceCount = 6; // 共6个设备
        private const string FormKey = "FrmEpbMainMonitor";
        private const int UI_TARGET_FPS = 25; // 目标帧率

        // —— 15 路全局定义 —— //
        private static readonly ChannelDef[] _allChs = BuildChannels();
        private readonly LineItem[] _chCurve = new LineItem[15];

        // —— 15 条曲线/数据/时间缓存 —— //
        private readonly PointPairList[] _chData = new PointPairList[15];

        // —— CheckEdit 映射（全局索引 -> 控件），用于实时控制可见性 —— //
        private readonly Dictionary<int, CheckEdit> _checkByGlobal = new(16);
        private readonly DeviceContext[] _deviceContexts = new DeviceContext[DeviceCount];
        private readonly double[] _lastX = Enumerable.Repeat(0.0, 15).ToArray();

        // 控制曲线显示的check控件名
        private readonly string[] _persistNames =
            Enumerable.Range(1, 12).Select(i => $"CheckEpbA{i}")
                .Concat(new[] { "CheckP1", "CheckP2", "CheckF" })
                .ToArray();

        // —— 快速路由（"Dev#ai" -> 全局索引） —— //
        private readonly Dictionary<string, int> _route = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<byte[]> bufferA = new();
        private readonly ConcurrentQueue<byte[]> bufferB = new();


        private readonly object bufferLock = new(); //实时曲线缓存数据锁

        private readonly object[]
            clampCounterLocks = Enumerable.Range(0, 6).Select(_ => new object()).ToArray(); //指令发送计数锁


        private readonly object currentDevLock = new();

        private readonly ConcurrentDictionary<string, LineItemOperation> curveDictionary = new();
        private readonly long DispinitialTimestamp = 0;


        private readonly Stopwatch Dispstopwatch = new();


        private readonly ClsEMBControler[] EmbGroup = new ClsEMBControler[12];
        private readonly object graphLock = new(); //曲线更新锁
        private readonly bool IsTestConfirm = false;
        private AoController _ao;
        private GlobalConfig _cfg;

        /// <summary>防止 OnFormClosing 重入执行。</summary>
        private int _closingReentry = 0;

        private string _currentDev = "EMB1"; // 添加私有字段
        private volatile bool _dirtyForRedraw; // 有新数据，需要重绘
        private DoController _do;
        private EpbManager _epb;

        /// <summary>固定的 X 轴窗口宽度（秒）。缺省沿用 ClsGlobal.XDuration。</summary>
        private double _fixedXWindowSec;


        private int _formClosedFlag; // 页面关闭 0=运行中；1=已开始关闭


        /// <summary>窗体是否已进入关闭流程（重入/回调统一短路）。</summary>
        private volatile bool _isClosing;

        private bool _isCtrlPowerPressing;
        private double _latestGlobalX; // 所有通道里最新的 X（秒）
        private Timer[] _logtimers = new Timer[DeviceCount * 2];

        private UiConfig _uiCfg;


        // —— UI 刷新节流相关 —— //
        private System.Windows.Forms.Timer _uiTimer;
        private ConcurrentQueue<byte[]> activeWriteBuffer;
        private AiConfigDetail aiConfigDetail;

        private ConcurrentDictionary<int, int> AlertStatus = new();


        //private double ActForceTimeOffset = 0;
        //private double DaqCurrentTimeOffset = 0;
        private Timer curveDisplayTimer;
        private int curveDispSpan = 0;
        private volatile double[] daqSnapshot = Array.Empty<double>();
        private int dataLogSpan = 0;
        private DateTime DispinitialDateTime = DateTime.Now;
        private volatile List<byte[]> forceSnapshot = new();
        private bool IsAutoLearn = false;
        private bool IsRunning = false;


        private DateTime lastGraphyTime = DateTime.Now;


        private ConcurrentQueue<string> LogError = new();

        public FormLoggerAdapter logger;
        private ConcurrentQueue<string> LogInformation = new();
        private ConcurrentQueue<string> LogWarn = new();
        private ConcurrentQueue<byte[]> readyReadBuffer;

        private int[] releaseFailureCounters = new int[6]; //松开失败计数，发送时加1，松开清零，此数超过预设值说明连续加紧，要告警并松开卡钳

        private DateTime runBegin;


        private TestConfig testConfig;
        private TwoDeviceAiAcquirer twoDeviceAiAcquirer;


        public FrmEpbMainMonitor()
        {
            InitializeComponent();

            // 窗口和父容器尺寸变化时都刷新一次
            Resize += (_, __) => ResizeLedDisplaysUnified();
            Shown += (_, __) => ResizeLedDisplaysUnified();


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


            logger = new FormLoggerAdapter(MaxInfos, MaxWarns, MaxErrors,
                LogInformation, LogWarn, LogError, this);

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

        private static ChannelDef[] BuildChannels()
        {
            var list = new List<ChannelDef>();

            // Dev1: EPB1..EPB8 -> ai0..ai7
            for (var i = 0; i < 8; i++)
                list.Add(new ChannelDef
                {
                    GlobalIndex = i,
                    DisplayName = $"DAQ_A{i + 1}_I(A)",
                    Device = "Dev1",
                    AiIndex = i,
                    Type = SignalType.Current
                });

            // Dev2: EPB9..EPB12 -> ai0..ai3
            for (var i = 0; i < 4; i++)
                list.Add(new ChannelDef
                {
                    GlobalIndex = 8 + i,
                    DisplayName = $"DAQ_A{9 + i}_I(A)",
                    Device = "Dev2",
                    AiIndex = i,
                    Type = SignalType.Current
                });

            // Dev2: P1, P2, F -> ai4, ai5, ai6
            list.Add(new ChannelDef
            {
                GlobalIndex = 12, DisplayName = "DAQ_P1_(bar)", Device = "Dev2", AiIndex = 4, Type = SignalType.Pressure
            });
            list.Add(new ChannelDef
            {
                GlobalIndex = 13, DisplayName = "DAQ_P2_(bar)", Device = "Dev2", AiIndex = 5, Type = SignalType.Pressure
            });
            list.Add(new ChannelDef
                { GlobalIndex = 14, DisplayName = "DAQ_F_(N)", Device = "Dev2", AiIndex = 6, Type = SignalType.Force });

            return list.ToArray();
        }

        private static string RouteKey(string dev, int ai)
        {
            return $"{dev}#{ai}";
        }

        /// <summary>
        ///     设置固定的 X 轴显示窗口（秒）。调用后立即应用到图表。
        ///     例如：SetXWindowSeconds(25);
        /// </summary>
        /// <param name="seconds">窗口宽度（秒，大于 0）。</param>
        public void SetXWindowSeconds(double seconds)
        {
            if (seconds <= 0) return;
            _fixedXWindowSec = seconds;

            if (zedGraphRealChart != null)
            {
                var pane = zedGraphRealChart.GraphPane;

                // 起步阶段始终显示 [0, 宽度]，这样只有最初才会看到左侧空白
                pane.XAxis.Scale.Min = 0;
                pane.XAxis.Scale.Max = _fixedXWindowSec;
                pane.XAxis.Scale.MinAuto = false;
                pane.XAxis.Scale.MaxAuto = false;

                zedGraphRealChart.AxisChange();
                zedGraphRealChart.Invalidate();
            }
        }


        private void ResizeLedDisplaysUnified()
        {
            if (LedRunTime?.Parent == null) return;

            // Step 1: 先计算基准控件（LedRunTime）
            LedAutoSizer.ResizeLedToParentWidth(LedRunTime, LedRunTime.Parent);

            // Step 2: 取出基准的 IntervalOn / IntervalIn
            var baseIntervalOn = LedRunTime.IntervalOn;
            var baseIntervalIn = LedRunTime.IntervalIn;

            // Step 3: 直接应用到其他两个控件
            ApplySameInterval(LedRunCycles, baseIntervalOn, baseIntervalIn);
            ApplySameInterval(LedLastCycles, baseIntervalOn, baseIntervalIn);
        }

        /// <summary>
        ///     把 IntervalOn/IntervalIn 设置成一致，并根据 CharCount 重算宽度
        /// </summary>
        private void ApplySameInterval(UILedDisplay led, int intervalOn, int IntervalIn, int blocksPerChar = 5)
        {
            if (led == null) return;

            led.IntervalOn = intervalOn;
            led.IntervalIn = IntervalIn;

            // 用公式算实际宽度
            var C = led.CharCount;
            int g = IntervalIn, s = intervalOn, B = blocksPerChar;
            var K = C * (B + 1) - 1;
            var W = g * (1 + K) + s * (2 + K) + 4;

            led.Width = W;

            // 可选：让控件居中
            if (led.Parent != null) led.Left = Math.Max(0, (led.Parent.ClientSize.Width - led.Width) / 2);
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

                // 初始化曲线
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

                twoDeviceAiAcquirer = new TwoDeviceAiAcquirer(aiConfigDetail, 1000, 50,
                    10, logger);

                twoDeviceAiAcquirer.OnEngBatch += Acq_OnEngBatch; // 订阅工程值批次到达事件

                #region 曲线勾选控件相关

                // 1) 载入 UI 配置
                _uiCfg = _cfg.UI;

                // 2) 获取/创建该表单的配置容器
                var formState = _uiCfg.GetOrAddForm(FormKey);

                // 3) 应用各控件状态 & 绑定事件（只绑一次）
                foreach (var name in _persistNames)
                {
                    var ctl = Controls.Find(name, true).FirstOrDefault();
                    if (ctl is not CheckEdit cb) continue; // 若是 SunnyUI.UICheckBox，同样有 Checked/CheckedChanged

                    var st = formState
                        .GetOrAdd(name); // 若 xml 中还没有，会新建节点（Checked=false/Enabled=true/DefaultChecked=false）

                    // 应用状态
                    cb.Checked = st.Checked;
                    cb.Enabled = st.Enabled;

                    // 防重复绑定
                    cb.CheckedChanged -= Cb_CheckedChanged_Save;
                    cb.EnabledChanged -= Cb_EnabledChanged_Save;

                    // 即时保存
                    cb.CheckedChanged += Cb_CheckedChanged_Save;
                    cb.EnabledChanged += Cb_EnabledChanged_Save;
                }

                // 4) 如果文件里缺少某些控件项，第一次加载会补齐；这里统一保存一次，保证文件完整
                ConfigLoader.SaveUI(_uiCfg);

                #endregion

                twoDeviceAiAcquirer.Start(); // 开始采集
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


        private void LoadEmbControler()
        {
            try
            {
                for (var i = 0; i < 6; i++)
                {
                    EmbGroup[i] = new ClsEMBControler();
                    EmbGroup[i].EmbNo = i + 1;
                    EmbGroup[i].EmbName = "EPB" + (i + 1);
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
            //throw new NotImplementedException();
        }

        private void RunningClick(object sender, EventArgs e, int index)
        {
            if (!EmbGroup[index].CtrlPower.Checked && EmbGroup[index].CtrlRunning.Checked) //运行状态
            {
                MessageBox.Show(@"请先打开电源！");
                EmbGroup[index].CtrlRunning.Checked = false;
            }
        }


        private async void PowerClick(object sender, EventArgs e, int index)
        {
            if (_isCtrlPowerPressing) return;

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
                            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "关闭EMB" + (index + 1) +
                            "继电器开关失败!");
                        ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError,
                            "关闭EMB" + (index + 1) + "继电器开关失败!", "串口操作");
                        ClsGlobal.PowerStatus[index] = 2;
                    }
                    else
                    {
                        RtbInfo.Invoke(new SetTextCallback(SetInfoText),
                            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "关闭EMB" + (index + 1) +
                            "继电器开关!");
                        ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation,
                            "关闭EMB" + (index + 1) + "继电器开关!", "UI 操作");
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
                            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "打开EMB" + (index + 1) +
                            "继电器开关失败!");
                        ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError,
                            "打开EMB" + (index + 1) + "继电器开关失败!", "串口操作");
                        ClsGlobal.PowerStatus[index] = 1;
                    }
                    else
                    {
                        RtbInfo.Invoke(new SetTextCallback(SetInfoText),
                            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff  > ") + "打开EMB" + (index + 1) +
                            "继电器开关!");
                        ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation,
                            "打开EMB" + (index + 1) + "继电器开关!", "UI 操作");
                        ClsGlobal.PowerStatus[index] = 2;
                        /*if (EmbGroup[index].IsEnabel)
                    {
                        EmbGroup[index].CtrlAlert.State = UILightState.On;
                        EmbGroup[index].CtrlAlert.OffCenterColor = Color.FromArgb(140, 140, 140);
                        EmbGroup[index].CtrlAlert.OffColor = Color.FromArgb(140, 140, 140);
                    }*/
                    }
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

        private void BtnTest_Click(object sender, EventArgs e)
        {
            // _do.SetEpb(channelNo: 1, directionIsForward: true);
            // _do.SetEpb(channelNo: 9, directionIsForward: true);
        }

        #region 测试相关代码 - 正式运行删除

        /// <summary>
        ///     切换开关事件，测试代码，正式运行时请删除
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void toggleSwitch1_Toggled(object sender, EventArgs e)
        {
            // 打开所有epb
            for (var i = 0; i < 12; i++) _do.SetEpb(i + 1, toggleSwitch1.IsOn);

            // 打开气缸测试
            // _ao.SetPercent("Cylinder1", 50); // => ~5V
            // _ao.SetPercent("Cylinder2", 50); // => ~5V
        }

        #endregion


        /// <summary>
        ///     窗体关闭事件，释放资源
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

            // twoDeviceAiAcquirer.Stop();
            //
            // twoDeviceAiAcquirer?.Dispose();


            //base.OnFormClosed(e);
        }

        #region 1) 批次回调：只做“路由 + 追加点”

        /// <summary>
        ///     工程值批次到达（dev = "Dev1"/"Dev2"，<paramref name="eng" />: 行=通道、列=样本）。
        ///     仅负责路由每一行到全局曲线，并把数据批量追加；
        ///     不做删除/改轴/刷新 —— 这些都由 UI 定时器统一完成。
        /// </summary>
        private void Acq_OnEngBatch(string dev, double[,] eng, DateTime current, DateTime last)
        {
            // —— 已进入关闭流程或窗体已销毁：直接返回 —— //
            if (_isClosing || Volatile.Read(ref _formClosedFlag) == 1 || IsDisposed || !IsHandleCreated)
                return;

            // —— 跨线程封送到 UI 线程 —— //
            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(new Action<string, double[,], DateTime, DateTime>(Acq_OnEngBatch),
                        dev, eng, current, last);
                }
                catch
                {
                    /* 窗口已销毁/句柄无效，忽略 */
                }

                return;
            }

            // —— 参数与采样率检查 —— //
            if (eng == null) return;
            var rows = eng.GetLength(0);
            var cols = eng.GetLength(1);
            if (rows <= 0 || cols <= 0) return;

            if (ClsGlobal.DaqFrequency <= 0)
            {
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "DaqFrequency 未正确设置", "曲线显示");
                return;
            }

            var dt = 1.0 / ClsGlobal.DaqFrequency;

            try
            {
                // —— 逐行路由并追加 —— //
                for (var r = 0; r < rows; r++)
                {
                    if (!_route.TryGetValue(RouteKey(dev, r), out var g))
                        continue; // 非我们关心的通道

                    var draw = _checkByGlobal.TryGetValue(g, out var cb) ? cb.Checked : true;

                    // 拷贝该行样本到一维缓冲
                    var buf = new double[cols];
                    for (var i = 0; i < cols; i++) buf[i] = eng[r, i];

                    AppendChannelBatch(g, buf, dt, draw);
                }

                // —— 示例：刷新 EPB1 瞬时显示 —— //
                var epb1 = _allChs.FirstOrDefault(c => c.Device == "Dev1" && c.AiIndex == 0);
                if (epb1 != null && _chData[epb1.GlobalIndex].Count > 0)
                {
                    var v = _chData[epb1.GlobalIndex][_chData[epb1.GlobalIndex].Count - 1].Y;
                    textEditCurrent1.Text = $"{v:F2} A";
                }

                lastGraphyTime = current;
            }
            catch (Exception ex)
            {
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "批次绘制出错: " + ex.Message, "曲线显示");
            }
        }

        #endregion

        #region 2) 追加样本：只加点 + 标记重绘（不删点/不刷新）

        /// <summary>
        ///     向指定“全局通道”追加一批样本；
        ///     仅负责把点追加到对应 <see cref="_chData" />，并更新全局最新时间；
        ///     不做 AxisChange/Invalidate，不裁剪数据、不改坐标轴。
        /// </summary>
        /// <param name="globalIndex">全局通道索引（0..14）。</param>
        /// <param name="daqData">工程值样本数组。</param>
        /// <param name="dt">相邻样本的时间间隔（秒/点）。</param>
        /// <param name="draw">是否显示该通道（由 CheckEdit 控制）。</param>
        private void AppendChannelBatch(int globalIndex, double[] daqData, double dt, bool draw)
        {
            if (_isClosing || Volatile.Read(ref _formClosedFlag) == 1) return;
            if (zedGraphRealChart == null || zedGraphRealChart.IsDisposed) return;

            if (zedGraphRealChart.InvokeRequired)
            {
                try
                {
                    zedGraphRealChart.Invoke(
                        new Action<int, double[], double, bool>(AppendChannelBatch),
                        globalIndex, daqData, dt, draw);
                }
                catch
                {
                    /* 窗口已销毁/句柄无效，忽略 */
                }

                return;
            }

            if (globalIndex < 0 || globalIndex >= _allChs.Length) return;
            if (daqData == null || daqData.Length == 0) return;

            var list = _chData[globalIndex];
            var line = _chCurve[globalIndex];
            if (line != null) line.IsVisible = draw;

            // —— 连续时间轴追加 —— //
            var x = _lastX[globalIndex];
            if (list.Count == 0 && x == 0.0) x = 0.0;
            else x += dt;

            for (var i = 0; i < daqData.Length; i++)
            {
                list.Add(x, daqData[i]);
                x += dt;
            }

            _lastX[globalIndex] = x - dt;

            // —— 只更新全局最新 X 并请求 UI 定时器刷新 —— //
            _latestGlobalX = Math.Max(_latestGlobalX, _lastX[globalIndex]);
            _dirtyForRedraw = true;
        }

        #endregion


        private async void BtnStartTest_Click(object sender, EventArgs e)
        {
            try
            {
                // 4) 组装 EpbManager（把回调委托接进去）
                _epb = new EpbManager(
                    _cfg,
                    _do,
                    _ao,
                    twoDeviceAiAcquirer,
                    logger);

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

        /// <summary>
        ///     停止试验按钮
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
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

        private void CheckEpbA7_CheckedChanged(object sender, EventArgs e)
        {
            Console.WriteLine(@"CheckEpbA7_CheckedChanged");
        }

        private void CheckEpbA7_CheckStateChanged(object sender, EventArgs e)
        {
            Console.WriteLine(@"CheckEpbA7_CheckStateChanged");
        }

        #region 3) 窗体关闭：一次性解绑/停止/释放

        /// <summary>
        ///     窗体关闭：标记关闭状态，解绑事件，停止 UI 定时器与采集，避免回调打到已销毁的 UI。
        /// </summary>
        private void FrmEpbMainMonitor_FormClosing(object sender, FormClosingEventArgs e)
        {
            // 只执行一次
            if (Interlocked.Exchange(ref _formClosedFlag, 1) != 0) return;
            _isClosing = true;

            // 1) 解绑曲线可见性事件（避免关闭过程中再次触发）
            try
            {
                foreach (var kv in _checkByGlobal)
                    if (kv.Value != null)
                        kv.Value.CheckedChanged -= OnCurveCheckChanged;
            }
            catch
            {
                /* 忽略个别控件异常 */
            }

            // 2) 停止并释放 UI 定时器（重绘交由定时器统一完成，关闭时必须先停）
            try
            {
                if (_uiTimer != null)
                {
                    _uiTimer.Stop();
                    _uiTimer.Dispose();
                    _uiTimer = null;
                }
            }
            catch
            {
            }

            // 3) 解绑采集事件并停止采集（按你的实例名/事件名修改）
            try
            {
                // 如果事件名不同，请改为你的实际事件名
                if (twoDeviceAiAcquirer != null)
                {
                    try
                    {
                        twoDeviceAiAcquirer.OnEngBatch -= Acq_OnEngBatch;
                    }
                    catch
                    {
                    }

                    try
                    {
                        twoDeviceAiAcquirer.Stop();
                    }
                    catch
                    {
                    }

                    try
                    {
                        twoDeviceAiAcquirer?.Dispose();
                    }
                    catch
                    {
                    }

                    twoDeviceAiAcquirer = null;
                }
            }
            catch
            {
            }

            base.OnFormClosing(e);
        }

        #endregion

        /// <summary>信号类型（用于决定放哪根轴与命名等）。</summary>
        private enum SignalType
        {
            Current,
            Pressure,
            Force
        }

        /// <summary>全局通道定义（把设备 + AI 行号，映射到 15 路全局曲线）。</summary>
        private sealed class ChannelDef
        {
            /// <summary>设备内 AI 行号（0-based）。</summary>
            public int AiIndex;

            /// <summary>所属设备（"Dev1"/"Dev2"）。</summary>
            public string Device;

            /// <summary>曲线显示名。</summary>
            public string DisplayName;

            /// <summary>全局索引：EPB1..12 -> 0..11；P1->12；P2->13；F->14。</summary>
            public int GlobalIndex;

            /// <summary>信号类型。</summary>
            public SignalType Type;
        }


        public class LineItemOperation
        {
            public LineItem lineItem { get; set; }
            public bool IsActive { get; set; }
        }

        #region 曲线处理相关变量

        private LineItem curveForce;
        private PointPairList listForce;

        private LineItem curveDaqCurrent;
        private PointPairList listDaqCurrent;

        private LineItem curveCanCurrent;
        private PointPairList listCanCurrent;

        // 高对比度深色系调色板（至少 15 种，便于区分不同曲线）
        private readonly Color[] _curveColors =
        {
            Color.Blue,
            Color.Red,
            Color.Green,
            Color.Orange,
            Color.Purple,
            Color.Brown,
            Color.DarkCyan,
            Color.Magenta,
            Color.DarkOliveGreen,
            Color.Maroon,
            Color.Teal,
            Color.Goldenrod,
            Color.DarkBlue,
            Color.DarkRed,
            Color.DarkGreen
        };

        // 曲线对象集合（ZedGraph 的 LineItem 列表）
        private readonly List<LineItem> _curveItems = new();

        // 曲线数据源集合（ZedGraph 的 PointPairList 列表）
        private readonly List<PointPairList> _curveDataLists = new();

        #endregion

        //AlertStatus  0 正常  1 值太小，连续夹紧   2 值太高  3 FaultMode 报警


        #region DAQ_AI变量

        private ConcurrentDictionary<string, double> ParaNameToScale = new();
        private ConcurrentDictionary<string, double> ParaNameToOffset = new();
        private ConcurrentDictionary<string, double> ParaNameToZeroValue = new();


        private static string[] Dev1UsedDaqAIChannels;
        private Task Dev1analogTask;
        private AnalogMultiChannelReader Dev1analogReader;
        private AsyncCallback Dev1analogCallback;
        private Task Dev1runningAnalogTask;


        private static string[] Dev2UsedDaqAIChannels;
        private Task Dev2analogTask;
        private AnalogMultiChannelReader Dev2analogReader;
        private AsyncCallback Dev2analogCallback;
        private Task Dev2runningAnalogTask;

        private static ConcurrentDictionary<string, int> EMBToDaqCurrentChannel = new();

        private static ConcurrentDictionary<string, uint> DirectionToSendFrame = new();

        private static ConcurrentDictionary<string, uint> DirectionToRecvFrame = new();

        private static ConcurrentDictionary<string, string> EMBToDirection = new();
        private static ConcurrentDictionary<int, uint> EMBHandlerToSendFrame = new();
        private static ConcurrentDictionary<int, uint> EMBHandlerToRecvFrame = new();
        private static ConcurrentDictionary<string, uint> EMBNameToSendFrame = new();
        private static ConcurrentDictionary<string, uint> EMBNameToRecvFrame = new();

        private static ConcurrentDictionary<int, double> EMBHandlerToSendCanForceScale = new();

        private static ConcurrentDictionary<int, double> EMBHandlerToSendCanForceOffset = new();

        private static ConcurrentDictionary<int, double> EMBHandlerToRecvCanForceScale = new();

        private static ConcurrentDictionary<int, double> EMBHandlerToRecvCanForceOffset = new();

        private static ConcurrentDictionary<int, double> EMBHandlerToRecvCanCurrentScale = new();

        private static ConcurrentDictionary<int, double> EMBHandlerToRecvCanCurrentOffset = new();

        private static ConcurrentDictionary<int, double> EMBHandlerToRecvCanTorqueScale = new();

        private static ConcurrentDictionary<int, double> EMBHandlerToRecvCanTorqueOffset = new();


        private static readonly ConcurrentDictionary<string, double> EMBNameToRecvCanForceScale = new();

        private static ConcurrentDictionary<string, double> EMBNameToRecvCanForceOffset = new();

        private static readonly ConcurrentDictionary<string, double> EMBNameToRecvCanCurrentScale = new();

        private static ConcurrentDictionary<string, double> EMBNameToRecvCanCurrentOffset = new();

        private static readonly ConcurrentDictionary<string, double> EMBNameToRecvCanTorqueScale = new();

        private static ConcurrentDictionary<string, double> EMBNameToRecvCanTorqueOffset = new();


        private double AiMaxVoltage = 10.0;
        private double AiMinVoltage = -10.0;


        private double DaqTimeSpanMilSeconds = 10.0;


        private readonly ConcurrentQueue<double[]> DaqAiDispData = new();

        private const int DaqAiDispDataLens = 100;

        private DaqAIContext DaqContext1;

        // private DaqAIContext DaqContext2;
        private Timer DaqLogtimer1;
        private Timer DaqLogtimer2;

        private Timer TempTimer;

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

            public readonly ConcurrentDictionary<int, int> CycleCounter = new();
        }


        private static readonly ConcurrentDictionary<string, int> EmbToChannel = new();

        private static readonly ConcurrentDictionary<int, int> EmbNoToChannel = new();

        private static readonly ConcurrentDictionary<int, string> EmbNoToName = new();

        private static ConcurrentDictionary<string, string> EmbToAutoSendPath = new();

        private static ConcurrentDictionary<string, IntPtr> EmbToAutoSendPtr = new();

        private static ConcurrentDictionary<string, string>
            EmbToCancelPath = new();

        private static ConcurrentDictionary<string, IntPtr> EmbToCancelPtr = new();


        private const uint TIMER_PERIODIC = 1;
        private const uint DEFAULT_RESOLUTION = 1;
        private readonly List<TimerState> EmbControlTimers = new(6);
        private int activeTimersCount;

        #endregion

        #region 曲线的勾选控件相关

        // —— 勾选变更：立即保存 —— //
        private void Cb_CheckedChanged_Save(object sender, EventArgs e)
        {
            if (sender is CheckEdit cb)
                ConfigLoader.UpdateUIChecked(_uiCfg, FormKey, cb.Name, cb.Checked);
        }

        // —— 启用状态变更：立即保存 —— //
        private void Cb_EnabledChanged_Save(object sender, EventArgs e)
        {
            if (sender is CheckEdit cb)
                ConfigLoader.UpdateUIChecked(_uiCfg, FormKey, cb.Name, cb.Checked, cb.Enabled);
        }

        // —— 可选：恢复默认按钮（把所有勾选恢复为 DefaultChecked，并触发保存） —— //
        private void BtnRestoreDefault_Click(object sender, EventArgs e)
        {
            var formState = _uiCfg.GetOrAddForm(FormKey);
            foreach (var name in _persistNames)
            {
                var ctl = Controls.Find(name, true).FirstOrDefault();
                if (ctl is CheckBox cb)
                {
                    var st = formState.GetOrAdd(name);
                    cb.Checked = st.DefaultChecked; // 触发 CheckedChanged → 自动保存
                }
            }
        }

        // —— 可选：将“当前状态”写为默认值，并保存到文件 —— //
        private void BtnSetCurrentAsDefault_Click(object sender, EventArgs e)
        {
            foreach (var name in _persistNames)
            {
                var ctl = Controls.Find(name, true).FirstOrDefault();
                if (ctl is CheckBox cb)
                    ConfigLoader.UpdateUIDefaultChecked(_uiCfg, FormKey, name, cb.Checked);
            }

            MessageBox.Show(@"已将当前勾选状态保存为默认值。");
        }

        #endregion


        #region 曲线处理

        /// <summary>
        ///     曲线初始化
        /// </summary>
        private void InitializeCurve_Old()
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


                var forceYAxis = new Y2Axis("");
                pane.Y2AxisList.Add(forceYAxis);
                forceYAxis.IsVisible = true;
                forceYAxis.Title.FontSpec.FontColor = Color.Purple;
                forceYAxis.Color = Color.Purple;
                forceYAxis.Scale.FontSpec.FontColor = Color.Purple;
                forceYAxis.Title.FontSpec.Size = fontSize;
                forceYAxis.Scale.FontSpec.Size = fontSize;
                forceYAxis.MajorGrid.IsVisible = false;
                forceYAxis.MajorGrid.IsZeroLine = false;


                // 添加 12 根电流曲线
                for (var i = 1; i <= 12; i++)
                {
                    var dataList = new PointPairList();
                    _curveDataLists.Add(dataList);

                    var curveName = $"DAQ_{i}_I(A)";
                    var curve = pane.AddCurve(curveName, dataList, _curveColors[(i - 1) % _curveColors.Length],
                        SymbolType.None);

                    curve.Line.Width = 2;
                    curve.IsY2Axis = true;
                    curve.YAxisIndex = 1;

                    _curveItems.Add(curve);
                }

                // 添加 P1 / P2 / F
                string[] extraNames = { "DAQ_P1_(bar)", "DAQ_P2_(bar)" };
                for (var i = 0; i < extraNames.Length; i++)
                {
                    var dataList = new PointPairList();
                    _curveDataLists.Add(dataList);

                    var curve = pane.AddCurve(extraNames[i], dataList, _curveColors[12 + i], SymbolType.None);

                    curve.Line.Width = 2;
                    curve.IsY2Axis = true;
                    curve.YAxisIndex = 1;

                    _curveItems.Add(curve);
                }


                var forceDataList = new PointPairList();
                curveDaqCurrent = pane.AddCurve("DAQ_F_(N)", forceDataList, _curveColors[_curveColors.Length - 1],
                    SymbolType.None);
                curveDaqCurrent.Line.Width = 2;
                curveDaqCurrent.IsY2Axis = false;
                curveDaqCurrent.YAxisIndex = 1;


                zedGraphRealChart.GraphPane.XAxis.Scale.Max = ClsGlobal.XDuration;
                zedGraphRealChart.GraphPane.XAxis.Scale.Min = 0.0;


                zedGraphRealChart.GraphPane.XAxis.Scale.MagAuto = false;
                zedGraphRealChart.GraphPane.XAxis.Scale.FormatAuto = false;


                zedGraphRealChart.GraphPane.YAxis.Scale.MagAuto = false;
                zedGraphRealChart.GraphPane.YAxis.Scale.FormatAuto = false;

                zedGraphRealChart.GraphPane.Y2Axis.Scale.MagAuto = false;
                zedGraphRealChart.GraphPane.Y2Axis.Scale.FormatAuto = false;

                forceYAxis.Scale.MagAuto = false;
                forceYAxis.Scale.FormatAuto = false;


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


        /// <summary>
        ///     曲线初始化：创建 15 条曲线（EPB 电流 12 路 + P1 + P2 + F），
        ///     勾选控件（CheckEdit）实时控制可见性；X 轴为时间（秒）。
        /// </summary>
        private void InitializeCurve()
        {
            try
            {
                var pane = zedGraphRealChart.GraphPane;
                var fontSize = 12;

                // —— 基础外观（沿用你原有设置）——
                pane.CurveList.Clear();
                _curveItems.Clear();
                _curveDataLists.Clear();

                pane.Title.IsVisible = false;
                pane.XAxis.Type = AxisType.Linear;
                pane.XAxis.Title.IsVisible = false;
                pane.YAxis.Title.IsVisible = false;

                pane.Chart.Border.IsVisible = false;
                pane.Fill = new Fill(Color.FromArgb(255, 255, 255));
                pane.Chart.Fill = new Fill(Color.FromArgb(248, 248, 248));



                pane.XAxis.Color = Color.Gray;
                pane.XAxis.MajorTic.Color = Color.Gray;
                pane.XAxis.MinorTic.Size = 0.0f;
                pane.XAxis.MajorGrid.IsVisible = true;
                pane.XAxis.MajorGrid.Color = Color.Gray;
                pane.XAxis.MajorGrid.DashOn = float.MaxValue;
                pane.XAxis.MajorGrid.DashOff = 0;
                pane.XAxis.Title.FontSpec.Size = fontSize;
                pane.XAxis.Scale.FontSpec.Size = fontSize;

                pane.YAxis.Color = Color.Gray;
                pane.YAxis.MajorTic.Color = Color.Gray;
                pane.YAxis.MinorTic.Size = 0.0f;
                pane.YAxis.MajorGrid.IsVisible = true;
                pane.YAxis.MajorGrid.Color = Color.FromArgb(80, 160, 255);
                pane.YAxis.MajorGrid.DashOn = float.MaxValue;
                pane.YAxis.MajorGrid.DashOff = 0;
                pane.YAxis.Title.FontSpec.Size = fontSize;
                pane.YAxis.Scale.FontSpec.Size = fontSize;

                pane.Y2Axis.IsVisible = true;
                pane.Y2Axis.MajorGrid.IsVisible = false;
                pane.Y2Axis.MajorTic.Color = Color.Gray;
                pane.Y2Axis.MinorTic.Size = 0.0f;
                pane.Y2Axis.Title.FontSpec.Size = fontSize;
                pane.Y2Axis.Scale.FontSpec.Size = fontSize;


                // ★ 新增：确保有一个用于压力的第二左轴，并拿到它的索引
                int pressureAxisIndex = EnsurePressureYAxis(pane);

                // —— 路由表重建 —— //
                _route.Clear();
                foreach (var c in _allChs)
                    _route[RouteKey(c.Device, c.AiIndex)] = c.GlobalIndex;

                // —— 绑定/缓存 15 个 CheckEdit —— //
                _checkByGlobal.Clear();
                var n = Math.Min(_allChs.Length, _persistNames.Length);
                for (var g = 0; g < n; g++)
                {
                    var name = _persistNames[g];
                    var ctl = Controls.Find(name, true).FirstOrDefault() as CheckEdit;
                    if (ctl == null) continue;

                    _checkByGlobal[g] = ctl;
                    ctl.Tag = g; // 保存全局曲线索引
                    ctl.CheckedChanged -= OnCurveCheckChanged; // 防止重复绑定
                    ctl.CheckedChanged += OnCurveCheckChanged;
                }

                // —— 创建 15 条曲线 —— //
                for (var g = 0; g < _allChs.Length; g++)
                {
                    _chData[g] = new PointPairList();
                    var color = _curveColors[g % _curveColors.Length];

                    var curve = pane.AddCurve(_allChs[g].DisplayName, _chData[g], color, SymbolType.None);
                    curve.Line.Width = 2f;

                    // 电流 -> Y2；压力/夹紧力 -> 左轴
                    // curve.IsY2Axis = _allChs[g].Type == SignalType.Current; //原来的y轴分配逻辑

                    // ★ 关键：按信号类型把曲线分配到对应的轴
                    switch (_allChs[g].Type)
                    {
                        case SignalType.Current:
                            // 12 路电流 -> 右轴（Y2）
                            curve.IsY2Axis = true;       // 右侧轴
                            // curve.Y2AxisIndex = 0;    // 可省，默认 0（只有一个右轴）
                            break;

                        case SignalType.Pressure:
                            // 两个压力 -> 新增的第二左轴（pressureAxisIndex >= 1）
                            curve.IsY2Axis = false;      // 左侧轴族
                            curve.YAxisIndex = pressureAxisIndex;
                            break;

                        case SignalType.Force:
                        default:
                            // 夹紧力 F -> 默认左轴（索引 0）
                            curve.IsY2Axis = false;      // 左侧轴族
                            curve.YAxisIndex = 0;
                            break;
                    }



                    // 初始可见性 = 复选框状态（若未找到控件则默认可见）
                    var visible = !_checkByGlobal.TryGetValue(g, out var cb) || cb.Checked;
                    curve.IsVisible = visible;

                    _chCurve[g] = curve;

                    // 为兼容你旧逻辑保留的集合（有人可能还在用）
                    _curveItems.Add(curve);
                    _curveDataLists.Add(_chData[g]);
                }

                // 兼容旧字段：让 listForce 指向 F 的数据，避免 ResetDisplaySystem() 空引用
                listForce = _chData[14];

                // 初始 X 轴窗口
                pane.XAxis.Scale.Min = 0;
                pane.XAxis.Scale.Max = ClsGlobal.XDuration;

                pane.XAxis.Scale.MagAuto = false;
                pane.XAxis.Scale.FormatAuto = false;
                pane.YAxis.Scale.MagAuto = false;
                pane.YAxis.Scale.FormatAuto = false;
                pane.Y2Axis.Scale.MagAuto = false;
                pane.Y2Axis.Scale.FormatAuto = false;

                zedGraphRealChart.AxisChange();
                zedGraphRealChart.Invalidate();
                zedGraphRealChart.Refresh();

                // —— 关掉抗锯齿，减少 CPU 消耗 —— //
                zedGraphRealChart.IsAntiAlias = false;


                // 曲线应用固定宽度（若未显式设置，则用 ClsGlobal.XDuration）
                SetXWindowSeconds(_fixedXWindowSec > 0 ? _fixedXWindowSec : ClsGlobal.XDuration);


                // —— 启动 UI 定时器（25FPS），统一 AxisChange + Invalidate —— //
                StartUiRedrawTimer();
            }
            catch (Exception ex)
            {
                MessageBox.Show(@"初始化曲线显示失败！" + ex.Message, @"提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError,
                    "初始化曲线显示失败！" + ex.Message, "初始化");
            }
        }

        #region 轴创建与选择

        /// <summary>
        /// 创建或获取用于“压力（bar）”显示的左侧第二 Y 轴，并返回其索引。
        /// - 轴放在左侧（YAxisList）
        /// - 通过 Axis.Tag 标记，避免重复创建
        /// - 为了区分，采用对比度较高的配色；网格默认关闭，防止与主轴混乱
        /// </summary>
        /// <param name="pane">ZedGraph 的 GraphPane</param>
        /// <returns>压力轴在 YAxisList 中的索引（>=1）</returns>
        private static int EnsurePressureYAxis(GraphPane pane)
        {
            // 1) 若已存在（通过 Tag 标记），直接返回
            for (int i = 0; i < pane.YAxisList.Count; i++)
            {
                if (pane.YAxisList[i]?.Tag is string tag && tag == "PRESSURE_AXIS")
                    return i;
            }

            // 2) 创建新的左侧 Y 轴（将出现在默认 Y 轴的左边堆叠显示）
            var pressureAxis = new YAxis("Pressure (bar)")
            {
                IsVisible = true,
                // 颜色尽量与主轴（蓝色系）区分
                Color = Color.DarkOrange,
                Title = { FontSpec = { Size = 12, FontColor = Color.DarkOrange } },
                Scale = { FontSpec = { Size = 12, FontColor = Color.DarkOrange } },
                MajorGrid = { IsVisible = false, IsZeroLine = false },
                MajorTic = { Color = Color.Gray },
                MinorTic = { Size = 0.0f }
            };

            // 3) 关闭自动“数量级/格式”跳变，与你现有做法保持一致，避免闪动
            pressureAxis.Scale.MagAuto = false;
            pressureAxis.Scale.FormatAuto = false;

            // 4) 打个标签，便于下次查找
            pressureAxis.Tag = "PRESSURE_AXIS";

            // 5) 加入到左轴列表并返回索引
            pane.YAxisList.Add(pressureAxis);
            return pane.YAxisList.Count - 1;
        }

        #endregion


        /// <summary>
        ///     启动 UI 重绘定时器：统一在该定时器里进行 AxisChange / Invalidate，
        ///     并批量删除旧点，避免在采集回调里高频重绘导致卡顿。
        /// </summary>
        private void StartUiRedrawTimer()
        {
            if (_uiTimer != null) return;

            _uiTimer = new System.Windows.Forms.Timer
            {
                Interval = Math.Max(10, 1000 / UI_TARGET_FPS) // 约 25 FPS
            };

            _uiTimer.Tick += (_, __) =>
            {
                if (Volatile.Read(ref _formClosedFlag) == 1) return;
                if (zedGraphRealChart == null || zedGraphRealChart.IsDisposed) return;

                if (!_dirtyForRedraw || zedGraphRealChart == null) return;

                var pane = zedGraphRealChart.GraphPane;

                // ① 固定窗口宽度（秒）
                var width = _fixedXWindowSec > 0 ? _fixedXWindowSec : Math.Max(1.0, ClsGlobal.XDuration);

                // ② 计算显示窗口 [minX, maxX]
                var maxX = Math.Max(0.0, _latestGlobalX);
                var minX = maxX < width ? 0.0 : maxX - width;

                // ③ 应用到坐标轴：起步阶段只显示 [0, width]；之后滚动 [maxX - width, maxX]
                pane.XAxis.Scale.Min = minX;
                pane.XAxis.Scale.Max = maxX < width ? width : maxX;

                // ④ 仅删除“窗口之外”的点，但留出一个安全边距（padding），
                //    防止由于各通道时间轴微抖动/抽稀/不同批次导致的“窗口内被删”。
                //    建议 padding 为窗口宽度的 1%～5%，且不小于 0.2s。
                var padding = Math.Max(0.2, width * 0.02);
                var purgeBefore = Math.Max(0.0, minX - padding);

                // —— 按通道批量清理 —— //
                for (var g = 0; g < _chData.Length; g++)
                {
                    var list = _chData[g];
                    if (list == null || list.Count == 0) continue;

                    // 最早的点仍在“保留区”(>= purgeBefore)，无需清理
                    if (list[0].X >= purgeBefore) continue;

                    // 线性寻界（点数很多时可改成二分搜索）
                    int cut = 0, cnt = list.Count;
                    while (cut < cnt && list[cut].X < purgeBefore) cut++;

                    if (cut > 0)
                    {
                        // 一次性拷贝保留段，避免 O(n^2) 的头删
                        var keep = cnt - cut;
                        if (keep > 0)
                        {
                            var tmp = new PointPair[keep];
                            for (var i = 0; i < keep; i++) tmp[i] = list[cut + i];

                            list.Clear();
                            for (var i = 0; i < keep; i++) list.Add(tmp[i].X, tmp[i].Y);
                        }
                        else
                        {
                            list.Clear();
                        }
                    }

                    // ——（可选更强保护）按通道自身“最后 X”再留一层余量：
                    //     保证每条曲线自身至少保留 width + padding 的跨度
                    var lastX = _lastX != null && g < _lastX.Length ? _lastX[g] :
                        list.Count > 0 ? list[list.Count - 1].X : 0.0;
                    var ownKeepMin = Math.Max(0.0, lastX - (width + padding));
                    if (list.Count > 0 && list[0].X < ownKeepMin)
                    {
                        var cut2 = 0;
                        var cnt2 = list.Count;
                        while (cut2 < cnt2 && list[cut2].X < ownKeepMin) cut2++;
                        if (cut2 > 0)
                        {
                            var keep2 = cnt2 - cut2;
                            var tmp2 = new PointPair[keep2];
                            for (var i = 0; i < keep2; i++) tmp2[i] = list[cut2 + i];

                            list.Clear();
                            for (var i = 0; i < keep2; i++) list.Add(tmp2[i].X, tmp2[i].Y);
                        }
                    }
                }

                // 统一重算与刷新
                zedGraphRealChart.AxisChange();
                zedGraphRealChart.Invalidate();

                _dirtyForRedraw = false;
            };


            _uiTimer.Start();
        }


        /// <summary>
        ///     CheckEdit 勾选变化 -> 显隐对应曲线。
        ///     使用控件的 Tag 作为全局曲线索引，避免闭包问题。
        /// </summary>
        private void OnCurveCheckChanged(object sender, EventArgs e)
        {
            if (sender is CheckEdit cb &&
                cb.Tag is int gi &&
                gi >= 0 && gi < _chCurve.Length)
            {
                var line = _chCurve[gi];
                if (line != null)
                {
                    line.IsVisible = cb.Checked;
                    zedGraphRealChart.Invalidate();
                }
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


                            FaultMode = faultflg;
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

                    if (DaqLens > 0) DaqDeltTime = recvSpan / DaqLens;

                    var IsAxisChanged = false;

                    for (var i = 0; i < DaqLens; i++)
                        listDaqCurrent.Add(graphyHeadertime + i * DaqDeltTime, DaqData[i]);

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

                    if (dataQueue.Count > 0) CanDeltTime = recvSpan / dataQueue.Count;
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

                            listForce.Add(graphyHeadertime + j * CanDeltTime, forceValue);
                            listCanCurrent.Add(graphyHeadertime + j * CanDeltTime, currentValue);

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

                    //
                    // zedGraphRealChart.AxisChange();
                    // zedGraphRealChart.Invalidate();
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
        ///     更新实时曲线（仅 DAQ_I 一条曲线）。
        ///     传入的数据应为工程值（已做零点、比例、偏置与滤波），方法内部会按照采样率
        ///     将样本映射到 X 轴（单位：秒），并维持 X 轴固定时窗（ClsGlobal.XDuration）。
        /// </summary>
        /// <param name="daqData">
        ///     一次刷新的 DAQ_I 数据段（工程值）。允许为空或长度为 0（此时不做任何更新）。
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
                pane.YAxis.IsVisible = false; // 只画 DAQ_I，不用左侧 Y 轴
                pane.Y2Axis.IsVisible = true; // 开启 Y2
                if (pane.Y2AxisList.Count > 1) // 如果曾经加过第二个 Y2（Act_I），这里隐藏
                    pane.Y2AxisList[1].IsVisible = false;

                // ===== 把采样映射到时间轴 =====
                // 采样周期（秒/点）
                if (ClsGlobal.DaqFrequency <= 0)
                    throw new InvalidOperationException("DaqFrequency 未正确设置。");

                var dt = 1.0 / ClsGlobal.DaqFrequency;

                // 本次追加的起始 X（秒）。
                // 若已有点，则从最后一个点的下一步开始；否则从 0 开始。
                double xStart;
                if (listDaqCurrent != null && listDaqCurrent.Count > 0)
                    xStart = listDaqCurrent[listDaqCurrent.Count - 1].X + dt;
                else
                    xStart = 0.0;

                // 逐点追加（X 轴为相对时间，单位：秒）
                for (var i = 0; i < daqData.Length; i++) listDaqCurrent.Add(xStart + i * dt, daqData[i]);

                // ===== 维持固定时窗（滑动窗口）=====
                if (listDaqCurrent != null && listDaqCurrent.Count > 0)
                {
                    var firstX = listDaqCurrent[0].X;
                    var lastX = listDaqCurrent[listDaqCurrent.Count - 1].X;

                    if (lastX - firstX > ClsGlobal.XDuration)
                    {
                        // 移除最旧的一半，避免频繁整体拷贝导致卡顿
                        var mid = (firstX + lastX) / 2.0;
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
                    var spanSec = daqData.Length * (1.0 / ClsGlobal.DaqFrequency);
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

                timer.Handler = EmbControlTimerHandler;
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
                    "启动EMB" + EmbIndex + "定时控制失败！" + ex.Message, "CAN通信");
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
                    "停止EMB" + index + "定时控制失败！" + ex.Message, "CAN通信");
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
                            "EMB" + (index + 1) + " 发送夹紧指令 ", "CAN通信");

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
                    "EMB" + (index + 1) + "定时发送指令出错！" + ex.Message, "定时发送指令");
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

        #region 日志相关

        /// <summary>
        ///     按钮点击事件，查看运行日志
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void BtnRunLog_Click(object sender, EventArgs e)
        {
            try
            {
                var OutFile = Environment.CurrentDirectory + @"\RunLog.txt";
                ClsLogProcess.ViewLogData(ref LogInformation, OutFile);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        /// <summary>
        ///     警告日志按钮点击事件
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void BtnWarnLog_Click(object sender, EventArgs e)
        {
            try
            {
                var OutFile = Environment.CurrentDirectory + @"\WarnLog.txt";
                ClsLogProcess.ViewWarnData(ref LogWarn, OutFile);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        /// <summary>
        ///     按钮点击事件，查看错误日志
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void BtnErrorLog_Click(object sender, EventArgs e)
        {
            try
            {
                var OutFile = Environment.CurrentDirectory + @"\ErrorLog.txt";
                ClsErrorProcess.ViewErrorData(ref LogError, OutFile);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        #endregion
    }
}