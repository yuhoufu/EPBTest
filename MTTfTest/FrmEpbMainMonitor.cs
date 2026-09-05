using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Configuration;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Config;
using Controller;
using DataOperation;
using Controller.Alarm;
using DevExpress.UITemplates.Collection.Editors;
using DevExpress.XtraEditors;
using IO.NI;
using MTTFTest.Watchdog.Protocol;
using MtEmbTest;
using MTEmbTest.UIHelpers;
using NationalInstruments.DAQmx;
using Sunny.UI;
using ZedGraph;
using IAppLogger = Config.IAppLogger;
using Task = NationalInstruments.DAQmx.Task;
//using AsyncListener;
using Timer = System.Threading.Timer;


// ReSharper disable AsyncVoidLambda

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

        // 持久化日志由 ProjectLogStore 管理；这三个仅为遗留内存浏览缓冲，不应各自保留10万条。
        private const int MaxErrors = 2000;
        private const int MaxInfos = 2000;
        private const int MaxWarns = 2000;

        private static SafetyMarginControlMode ReadSafetyMarginControlModeFromAppConfig(IAppLogger logger)
        {
            try
            {
                var raw = ConfigurationManager.AppSettings["EpbSafetyMarginControlMode"];
                var mode = SafetyMarginControlModeParser.ParseOrDefault(raw, SafetyMarginControlMode.Legacy20251010);
                logger?.Info($"SafetyMargin 控制模式：{mode}（AppSetting=EpbSafetyMarginControlMode, raw='{raw ?? ""}'）", "EPB");
                return mode;
            }
            catch (Exception ex)
            {
                logger?.Warn($"读取 App.config 的 EpbSafetyMarginControlMode 失败：{ex.Message}，将使用默认 Legacy20251010。", "EPB");
                return SafetyMarginControlMode.Legacy20251010;
            }
        }

        // private ConcurrentQueue<CanData> dataQueue = new ConcurrentQueue<CanData>();
        private const int CacheLens = 6000; //每秒100帧，3秒处理一次，最多缓存6秒

        private const int DeviceCount = 6; // 共6个设备
        private const string FormKey = "FrmEpbMainMonitor";
        private const int UI_TARGET_FPS = 10; // 目标帧率（降低以减轻全通道绘制压力）合适的范围是 5-15 FPS

        private readonly System.Windows.Forms.Timer _autoSaveTimer = new System.Windows.Forms.Timer();


        #region 概览区域相关属性、字段

        /// <summary>
        /// 当前在“EPB 概览”区域中选中的 EPB 通道号（1..12；0 表示未选）。
        /// </summary>
        private int _currentEpbSummaryChannel = 0;

        #endregion

        // 修改为动态从配置构建通道映射
        private static readonly ChannelDef[] _allChs = BuildChannelsFromConfig();
        private readonly LineItem[] _chCurve = new LineItem[15];

        // —— 15 条曲线/数据/时间缓存 —— //
        private readonly PointPairList[] _chData = new PointPairList[15];

        // —— CheckEdit 映射（全局索引 -> 控件），用于实时控制可见性 —— //
        private readonly Dictionary<int, CheckEdit> _checkByGlobal = new(16);
        private readonly DeviceContext[] _deviceContexts = new DeviceContext[DeviceCount];

        // —— 瞬时值显示控件映射（全局索引 -> 文本控件）—— //
        private readonly Dictionary<int, TextEdit> _instantDisplayControls = new(16);
        private readonly double[] _lastX = Enumerable.Repeat(0.0, 15).ToArray();

        // 控制曲线显示的check控件名
        private readonly string[] _persistNames =
            Enumerable.Range(1, 12).Select(i => $"CheckEpbA{i}")
                .Concat(new[] { "CheckP1", "CheckP2", "CheckF" })
                .ToArray();

        /// <summary>计划总次数显示（通道 → UILabel）。</summary>
        private readonly Dictionary<int, UILabel> _planLabelByChannel = new();

        // —— 快速路由（"Dev#ai" -> 全局索引） —— //
        private readonly Dictionary<string, int> _route = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>切换开关（通道 → ToggleButton）。</summary>
        private readonly Dictionary<int, ToggleButton> _switchByChannel =
            new();

        private readonly ConcurrentQueue<byte[]> bufferA = new();
        private readonly ConcurrentQueue<byte[]> bufferB = new();


        private readonly object bufferLock = new(); //实时曲线缓存数据锁

        private readonly object[]
            clampCounterLocks = Enumerable.Range(0, 6).Select(_ => new object()).ToArray(); //指令发送计数锁


        private readonly object currentDevLock = new();

        private readonly ConcurrentDictionary<string, LineItemOperation> curveDictionary = new();
        private readonly long DispinitialTimestamp = 0;


        private readonly Stopwatch Dispstopwatch = new();

        // UI 激活状态，用于前后台切换时调整曲线补点策略，降低“窗口切走”带来的视觉断线
        private volatile bool _uiActive = true;


        private readonly ClsEPBControler[] EpbGroup = new ClsEPBControler[12];
        private readonly object graphLock = new(); //曲线更新锁
        private readonly bool IsTestConfirm = false;
        private AoController _ao;

        private CancellationTokenSource _batchCts; // 批量操作取消令牌源
        private GlobalConfig _cfg;
        private readonly DaqRuntimeSettings _daqRuntimeSettings;

        /// <summary>防止 OnFormClosing 重入执行。</summary>
        private int _closingReentry = 0;
        private int _closeSafetyWarningShown;
        private int _closePersistenceWarningShown;
        /// <summary>操作员已明确点击“停止试验”；允许关闭或抛弃旧批次诊断后重新开始。</summary>
        private int _operatorStopRequested;
        private int _monitorLifecycle = (int)EpbMonitorLifecycle.Initializing;
        private int _monitorEnergizationAttempted;
        private int _stopUiGuard;
        private readonly ManualStopExitReceiptOwner _manualStopExitReceipt =
            new ManualStopExitReceiptOwner();
        private readonly StopSessionReceiptOwner _stopSessionReceipt =
            new StopSessionReceiptOwner();
        private readonly EpbMonitorHardwareReleaseOwner _hardwareReleaseOwner =
            new EpbMonitorHardwareReleaseOwner();
        private StopSafetyResult _preparedCloseSafety;
        private RuntimeTransportSessionContext _preparedCloseContext;
        private ApplicationCloseReceipt _applicationCloseReceipt;

        private string _currentDev = "EMB1"; // 添加私有字段


        // —— 数据落盘上下文与定时器（沿用旧项目结构）——
        private DaqAIContext _daqDev1;
        private DaqAIContext _daqDev2;
        private Timer _daqRawTimerDev1, _daqStatTimerDev1;
        private Timer _daqRawTimerDev2, _daqStatTimerDev2;

        /// <summary>
        ///     每帧样本时间跨度（毫秒），与旧项目一致：1000 / 采样频率。
        ///     数据落盘使用
        /// </summary>
        private double _daqTimeSpanMs = 10.0; // Load 时按不可变 DAQ 运行参数计算。

        /// <summary>本次试验的数据根目录（每次试验一个唯一文件夹）。</summary>
        private string _dataStorePath = string.Empty;

        private volatile bool _dirtyForRedraw; // 有新数据，需要重绘

        /// <summary>
        ///     工程值批次（OnEngBatch）到达时的 UI 合并调度标记。
        ///     <para>
        ///     DAQ 回调频率较高（例如每 50ms 一批），若每批都直接 <see cref="Control.BeginInvoke(Delegate)"/>
        ///     则容易造成 UI 消息队列堆积，进而在窗口前后台切换时触发卡顿甚至调试助手
        ///     <c>ContextSwitchDeadlock</c>。
        ///     </para>
        /// </summary>
        private int _engUiWorkScheduled;

        /// <summary>
        ///     最近一次待处理的工程值批次（按设备保留“最新一批”，中间批次会被覆盖）。
        ///     <remarks>
        ///     这是为 UI 侧“只取最新值显示”设计的：控制逻辑不依赖该事件；
        ///     丢弃部分 UI 批次不会影响控制，但能显著降低 UI 负载。
        ///     </remarks>
        /// </summary>
        private readonly LatestPairMailbox<EngBatchPending> _pendingEngBatches = new();

        /// <summary>
        ///     曲线显示的最大“绘图采样率”（Hz）。
        ///     <para>
        ///     仅影响显示层：对控制逻辑/落盘无影响。通过抽稀点数降低全通道显示时的 CPU/GC/重绘压力。
        ///     </para>
        /// </summary>
        // 仅用于显示抽稀；2 kHz 原始采集、峰值判定与落盘保持不变。
        // 现场全通道运行时 100 Hz 绘图会持续占用 UI/CPU，10 Hz 已足够观察动作波形。
        private const int UiMaxPlotHz = 10;

        // 旧点裁剪按至少 1 秒批量执行。窗口仍保持有界，但避免窗口滚动后每 100ms
        // 为每条曲线复制整个保留段，降低长时间运行时的 CPU 与 GC 压力。
        private const double UiPurgeBatchMinSec = 1.0;

        // 显示层按设备只保留最新批次。控制、峰值判定、Raw 落盘均走独立链路，
        // 因此 UI 忙时追赶历史显示批次既没有数据完整性收益，反而会在消息泵恢复后
        // 一次处理最多 64 批，制造长 UI 占用和新的调度抖动。
        /// <summary>
        ///     瞬时值（文本框）刷新节流：避免每批都刷新导致 UI 抖动。
        /// </summary>
        private int _lastInstantUiUpdateTick;

        private const int InstantUiUpdateMinIntervalMs = 200;

        /// <summary>
        ///     绘图零点时间（绝对时间），用于将 DAQ 的绝对时间戳转换为曲线的相对时间 X。
        ///     <para>在 ResetDisplaySystem 时重置，在首个数据包到达时锚定。</para>
        /// </summary>
        private DateTime _plotZeroTime = DateTime.MinValue;

        /// <summary>
        ///     记录“快速渲染设置”是否已输出过一次日志（避免 Activated 多次触发刷屏）。
        /// </summary>
        private int _fastRenderSettingsLogged;

        /// <summary>
        ///     OnEngBatch 的待处理参数包（引用类型，便于用 null 表示“无待处理”）。
        /// </summary>
        private sealed class EngBatchPending
        {
            public string Dev;
            public double[,] Eng;
            public DateTime Current;
            public DateTime Last;
        }

        // 落盘相关字段
        private EpbDiskWriter _diskWriter;

        /// <summary>
        ///     启动加载试验时，是否应当用 DB(index.db) 回填 RunCount。
        ///     <para>
        ///     仅当“项目目录下已有 index.db”时为 true，避免首次新建项目时误把 XML 进度覆盖为 0。
        ///     </para>
        /// </summary>
        private bool _shouldBackfillRunCountFromDbOnLoad;

        /// <summary>
        ///     启动加载试验时，RunCount 是否发生过 DB→UI 的回填变更。
        ///     <para>用于决定是否立即写回项目 TestConfig.xml。</para>
        /// </summary>
        private bool _startupRunCountBackfillChanged;

        private DoController _do;
        private EpbManager _epb;

        // 报警子系统（泓格 M-7055D / RS-485）
        private AlarmManager _alarmManager;
        private Config.AlarmConfig _alarmCfg;

        // 报警面板输出测试窗体（用于直控 12 路指示灯 + 蜂鸣器）
        private FrmAlarmPanelTest _alarmPanelTestForm;

        /// <summary>固定的 X 轴窗口宽度（秒）。缺省沿用 ClsGlobal.XDuration。</summary>
        private double _fixedXWindowSec;


        private int _formClosedFlag; // 页面关闭 0=运行中；1=已开始关闭


        /// <summary>窗体是否已进入关闭流程（重入/回调统一短路）。</summary>
        private volatile bool _isClosing;

        private bool _isCtrlPowerPressing;
        private double _latestGlobalX; // 所有通道里最新的 X（秒）
        private Timer[] _logtimers = new Timer[DeviceCount * 2];
        private IEpbCycleRecorder _recorder;

        private UiConfig _uiCfg;
        private const int UiInfoRecentLineLimit = 2000;
        private const int UiInfoTrimWatermark = 1800;
        private const int UiInfoPendingLineLimit = 512;
        private const int UiInfoBatchMaxLines = 50;
        private const int UiInfoAutoScrollMinIntervalMs = 500;
        private UiInfoLogStore _uiInfoLogStore;
        private bool _suppressRtbInfoTextChanged;
        private readonly BoundedConcurrentQueue<string> _pendingUiInfoLines =
            new BoundedConcurrentQueue<string>(UiInfoPendingLineLimit);
        private readonly List<string> _uiInfoBatch = new List<string>(UiInfoBatchMaxLines);
        private System.Windows.Forms.Timer _uiInfoFlushTimer;
        private int _uiInfoVisibleLineCount;
        private bool _uiInfoAutoScrollPending;
        private long _uiInfoLastAutoScrollTick;
        private readonly List<double> _uiHeartbeatDelayMs = new List<double>(96);
        private readonly List<double> _uiHeartbeatFlushMs = new List<double>(96);
        private long _uiHeartbeatLastTick;
        private long _uiHeartbeatWindowStartedTick;
        private double _uiInfoAppendMaxMs;
        private double _uiInfoTrimMaxMs;
        private double _uiInfoScrollMaxMs;
        private long _uiInfoRenderedBatches;
        private long _uiInfoRenderedLines;


        /// <summary>内存中的 12 路 EPB 记录，来源于 TestConfig.xml 的 &lt;EpbRecords&gt;。</summary>
        private List<EpbTestRecord> _uiEpbRecords = new();

        // 加一个锁，避免未来多线程回调时踩踏）
        private readonly object _epbRecordsLock = new object();


        // —— UI 刷新节流相关 —— //
        private System.Windows.Forms.Timer _uiTimer;


        private void TryInitAlarmSubsystem(IAppLogger logger)
        {
            try
            {
                // 默认：先禁用/隐藏，只有启用报警后再打开
                if (CbBuzzerEnabled != null) { CbBuzzerEnabled.Enabled = false; CbBuzzerEnabled.Visible = false; }
                if (BtnClearAlarms != null) { BtnClearAlarms.Enabled = false; BtnClearAlarms.Visible = false; }

                var alarmCfgPath = RuntimeConfigPaths.GetPath("AlarmConfig.xml");
                if (!File.Exists(alarmCfgPath))
                {
                    logger?.Warn($"未找到报警配置：{alarmCfgPath}（将不启用 RS-485 报警输出）", "报警");
                    return;
                }

                _alarmCfg = AlarmConfigLoader.Load(alarmCfgPath, logger);
                _alarmManager = new AlarmManager(_alarmCfg, logger);

                _epb.Alarm = _alarmManager;
                _epb.AlarmConfig = _alarmCfg;

                // —— 改为：Designer 中固定存在控件，运行时只做状态/事件绑定 ——
                if (CbBuzzerEnabled != null)
                {
                    CbBuzzerEnabled.Visible = true;
                    CbBuzzerEnabled.Enabled = true;
                    CbBuzzerEnabled.Checked = _alarmManager.BuzzerEnabled;

                    // 防重复订阅
                    CbBuzzerEnabled.CheckedChanged -= CbBuzzerEnabled_CheckedChanged;
                    CbBuzzerEnabled.CheckedChanged += CbBuzzerEnabled_CheckedChanged;
                }

                if (BtnClearAlarms != null)
                {
                    BtnClearAlarms.Visible = true;
                    BtnClearAlarms.Enabled = true;

                    // 防重复订阅
                    BtnClearAlarms.Click -= BtnClearAlarms_Click;
                    BtnClearAlarms.Click += BtnClearAlarms_Click;
                }
            }
            catch (Exception ex)
            {
                logger?.Warn($"报警子系统初始化失败：{ex.Message}", "报警");
            }
        }

        private void CbBuzzerEnabled_CheckedChanged(object sender, EventArgs e)
        {
            try
            {
                _alarmManager?.SetBuzzerEnabled(CbBuzzerEnabled.Checked);
            }
            catch
            {
                // ignore
            }
        }

        private async void BtnClearAlarms_Click(object sender, EventArgs e)
        {
            try
            {
                if (_alarmManager != null)
                    await _alarmManager.ClearAllAsync();
            }
            catch (Exception ex)
            {
                logger?.Warn($"全关报警失败：{ex.Message}", "报警");
            }
        }
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


        public FormLoggerAdapter logger;
        private ConcurrentQueue<string> LogError = new();
        private ConcurrentQueue<string> LogInformation = new();
        private ConcurrentQueue<string> LogWarn = new();
        private ConcurrentQueue<byte[]> readyReadBuffer;

        private int[] releaseFailureCounters = new int[6]; //松开失败计数，发送时加1，松开清零，此数超过预设值说明连续加紧，要告警并松开卡钳

        private DateTime runBegin;

        // Supplied by the recovery coordinator before EpbManager/housekeeping
        // construction.  Controller never reads the checkpoint file itself.
        private Guid _protectedLearningRootId;


        private DataOperation.TestConfig testConfig;
        private TwoDeviceAiAcquirer twoDeviceAiAcquirer;


        public FrmEpbMainMonitor()
            : this(DaqRuntimeSettings.Load(
                System.Configuration.ConfigurationManager.AppSettings))
        {
        }

        internal FrmEpbMainMonitor(DaqRuntimeSettings daqRuntimeSettings)
        {
            _daqRuntimeSettings = daqRuntimeSettings ??
                throw new ArgumentNullException(nameof(daqRuntimeSettings));
            InitializeComponent();

            Activated += (_, __) =>
            {
                _uiActive = true;
                ApplyZedGraphFastRenderSettings();
            };

            Deactivate += (_, __) => { _uiActive = false; };

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

        private static DialogResult ShowOperatorMessage(
            string message,
            string caption = "提示",
            MessageBoxButtons buttons = MessageBoxButtons.OK,
            MessageBoxIcon icon = MessageBoxIcon.None)
        {
            if (!UnattendedRecoveryCoordinator.IsRecoveryProcessMode)
                return MessageBox.Show(message, caption, buttons, icon);

            // 自动恢复子进程从启动到续测结束都不得出现需要现场人员点击的模态框。
            // 所有这类信息进入持久化项目日志；控制层安全门禁决定是否继续或重启。
            ProjectLogHub.Write(
                icon == MessageBoxIcon.Error
                    ? ProjectLogLevel.Error
                    : ProjectLogLevel.Warning,
                $"SuppressModalDialog Caption={caption}; Message={message}",
                "无人值守恢复");
            return DialogResult.OK;
        }

        /// <summary>
        ///     动态从AIConfig.xml读取配置并构建通道映射，按界面控件顺序排列
        ///     界面顺序：CheckEpbA1-A12, CheckP1, CheckP2, CheckF
        /// </summary>
        private static ChannelDef[] BuildChannelsFromConfig()
        {
            try
            {
                // 读取AIConfig.xml配置
                var configPath = RuntimeConfigPaths.GetPath("AIConfig.xml");
                var aiConfig = AiConfigLoader.Load(configPath);
                var enabledRecords = aiConfig.Enabled().ToList();

                var result = new List<ChannelDef>();
                var globalIndex = 0;

                // 1. 先添加EPB1-12电流（按编号顺序）
                for (var epbNum = 1; epbNum <= 12; epbNum++)
                {
                    var record = enabledRecords.FirstOrDefault(r => r.参数名 == $"EPB{epbNum}_current");
                    if (record != null)
                    {
                        var channelDef = CreateChannelDef(record, globalIndex);
                        if (channelDef != null)
                        {
                            result.Add(channelDef);
                            globalIndex++;
                        }
                    }
                }

                // 2. 添加压力P1
                var pressureP1 = enabledRecords.FirstOrDefault(r => r.参数名 == "Pressure_1");
                if (pressureP1 != null)
                {
                    var channelDef = CreateChannelDef(pressureP1, globalIndex);
                    if (channelDef != null)
                    {
                        result.Add(channelDef);
                        globalIndex++;
                    }
                }

                // 3. 添加压力P2
                var pressureP2 = enabledRecords.FirstOrDefault(r => r.参数名 == "Pressure_2");
                if (pressureP2 != null)
                {
                    var channelDef = CreateChannelDef(pressureP2, globalIndex);
                    if (channelDef != null)
                    {
                        result.Add(channelDef);
                        globalIndex++;
                    }
                }

                // 4. 添加夹紧力F
                var force = enabledRecords.FirstOrDefault(r => r.参数名 == "Force");
                if (force != null)
                {
                    var channelDef = CreateChannelDef(force, globalIndex);
                    if (channelDef != null)
                    {
                        result.Add(channelDef);
                        globalIndex++;
                    }
                }

                return result.ToArray();
            }
            catch (Exception ex)
            {
                // 配置读取失败时，回退到最小化的默认配置
                ShowOperatorMessage($"读取AIConfig.xml失败，使用默认配置：{ex.Message}", "配置错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return GetFallbackChannels();
            }
        }

        /// <summary>
        ///     根据配置记录创建通道定义
        /// </summary>
        private static ChannelDef CreateChannelDef(dynamic record, int globalIndex)
        {
            // 解析物理通道：如 "Dev1/ai0" -> Device="Dev1", AiIndex=0
            var parts = record.物理通道.Split('/');
            if (parts.Length != 2) return null;

            var device = parts[0]; // Dev1 或 Dev2
            var aiChannel = parts[1]; // ai0, ai1, etc.

            // 明确初始化aiIndex变量
            var aiIndex = -1; // 默认值
            if (!aiChannel.StartsWith("ai") ||
                !int.TryParse(aiChannel.Substring(2), out aiIndex))
                return null; // 解析失败，直接返回null

            // 根据参数名动态判断信号类型和显示名
            SignalType signalType;
            string displayName;

            if (record.参数名.Contains("_current"))
            {
                signalType = SignalType.Current;
                // 从EPB1_current提取编号1
                var epbNumStr = record.参数名.Replace("EPB", "").Replace("_current", "");
                if (int.TryParse(epbNumStr, out int epbNum))
                    displayName = $"DAQ_A{epbNum}_I(A)";
                else
                    return null; // 解析失败
            }
            else if (record.参数名 == "Pressure_1")
            {
                signalType = SignalType.Pressure;
                displayName = "DAQ_P1_(bar)";
            }
            else if (record.参数名 == "Pressure_2")
            {
                signalType = SignalType.Pressure;
                displayName = "DAQ_P2_(bar)";
            }
            else if (record.参数名 == "Force")
            {
                signalType = SignalType.Force;
                displayName = "DAQ_F_(N)";
            }
            else
            {
                return null; // 跳过不认识的参数
            }

            return new ChannelDef
            {
                Device = device,
                AiIndex = aiIndex, // aiIndex现在肯定已经初始化
                GlobalIndex = globalIndex, // 按界面顺序分配全局索引
                DisplayName = displayName,
                Type = signalType
            };
        }

        internal FrmEpbMainMonitor(Guid protectedLearningRootId)
            : this(protectedLearningRootId, DaqRuntimeSettings.Load(
                System.Configuration.ConfigurationManager.AppSettings))
        {
        }

        internal FrmEpbMainMonitor(
            Guid protectedLearningRootId,
            DaqRuntimeSettings daqRuntimeSettings) : this(daqRuntimeSettings)
        {
            _protectedLearningRootId = protectedLearningRootId;
        }

        /// <summary>
        ///     当配置读取失败时的回退配置（按界面顺序：EPB1-12, P1, P2, F）
        /// </summary>
        private static ChannelDef[] GetFallbackChannels()
        {
            var list = new List<ChannelDef>();
            var globalIndex = 0;

            // 1. EPB1-12电流通道（按界面顺序）
            for (var epbNum = 1; epbNum <= 12; epbNum++)
            {
                var device = epbNum <= 6 ? "Dev1" : "Dev2";
                var aiIndex = epbNum <= 6 ? epbNum - 1 : epbNum - 7;

                list.Add(new ChannelDef
                {
                    GlobalIndex = globalIndex++,
                    DisplayName = $"DAQ_A{epbNum}_I(A)",
                    Device = device,
                    AiIndex = aiIndex,
                    Type = SignalType.Current
                });
            }

            // 2. 压力P1（globalIndex=12）
            list.Add(new ChannelDef
            {
                GlobalIndex = globalIndex++, // 12
                DisplayName = "DAQ_P1_(bar)",
                Device = "Dev1",
                AiIndex = 6,
                Type = SignalType.Pressure
            });

            // 3. 压力P2（globalIndex=13）
            list.Add(new ChannelDef
            {
                GlobalIndex = globalIndex++, // 13
                DisplayName = "DAQ_P2_(bar)",
                Device = "Dev2",
                AiIndex = 7,
                Type = SignalType.Pressure
            });

            // 4. 夹紧力F（globalIndex=14）
            list.Add(new ChannelDef
            {
                GlobalIndex = globalIndex++, // 14
                DisplayName = "DAQ_F_(N)",
                Device = "Dev2",
                AiIndex = 6,
                Type = SignalType.Force
            });

            return list.ToArray();
        }

        /// <summary>
        ///     旧版本硬编码通道映射（用于测试问题根源）
        /// </summary>
        private static ChannelDef[] BuildChannelsOld()
        {
            var list = new List<ChannelDef>();

            // Dev1: EPB1..EPB6 -> ai0..ai5
            for (var i = 0; i < 6; i++)
                list.Add(new ChannelDef
                {
                    GlobalIndex = i,
                    DisplayName = $"DAQ_A{i + 1}_I(A)",
                    Device = "Dev1",
                    AiIndex = i,
                    Type = SignalType.Current
                });

            // Dev1: P1 -> ai6
            list.Add(new ChannelDef
            {
                GlobalIndex = 12, DisplayName = "DAQ_P1_(bar)", Device = "Dev1", AiIndex = 6, Type = SignalType.Pressure
            });

            // Dev2: EPB7..EPB12 -> ai0..ai5
            for (var i = 0; i < 6; i++)
                list.Add(new ChannelDef
                {
                    GlobalIndex = 6 + i,
                    DisplayName = $"DAQ_A{7 + i}_I(A)",
                    Device = "Dev2",
                    AiIndex = i,
                    Type = SignalType.Current
                });

            // Dev2: F -> ai6, P2 -> ai7
            list.Add(new ChannelDef
                { GlobalIndex = 14, DisplayName = "DAQ_F_(N)", Device = "Dev2", AiIndex = 6, Type = SignalType.Force });
            list.Add(new ChannelDef
            {
                GlobalIndex = 13, DisplayName = "DAQ_P2_(bar)", Device = "Dev2", AiIndex = 7, Type = SignalType.Pressure
            });

            return list.ToArray();
        }

        private static string RouteKey(string dev, int ai)
        {
            return $"{dev}#{ai}";
        }

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
            SetMonitorLifecycle(EpbMonitorLifecycle.Initializing);
            try
            {
                DaqTimeSpanMilSeconds = 1000.0 / _daqRuntimeSettings.SampleRateHz;

                activeWriteBuffer = bufferA;
                readyReadBuffer = bufferB;


                var ReadMsg = ClsXmlOperation.GetDaqAIUsedChannels(
                    RuntimeConfigPaths.GetPath("AIConfig.xml"), "Dev1", out Dev1UsedDaqAIChannels);
                if (ReadMsg.IndexOf("OK", StringComparison.Ordinal) < 0)
                {
                    ShowOperatorMessage(ReadMsg);
                    return;
                }

                if (Dev1UsedDaqAIChannels.Length < 1)
                {
                    ShowOperatorMessage(@"未读取到 Dev1 DAQ AI 相关信息！");
                    return;
                }


                ReadMsg = ClsXmlOperation.GetDaqAIUsedChannels(
                    RuntimeConfigPaths.GetPath("AIConfig.xml"), "Dev2", out Dev2UsedDaqAIChannels);
                if (ReadMsg.IndexOf("OK", StringComparison.Ordinal) < 0)
                {
                    ShowOperatorMessage(ReadMsg);
                    return;
                }

                if (Dev2UsedDaqAIChannels.Length < 1)
                {
                    ShowOperatorMessage(@"未读取到 Dev2 DAQ AI 相关信息！");
                    return;
                }


                ReadMsg = ClsXmlOperation.GetDaqAIChannelMapping(
                    RuntimeConfigPaths.GetPath("AIConfig.xml"), "Dev1", Dev1UsedDaqAIChannels,
                    out Dev1DaqChannel, new string[] { }); //paramTypeFilter 参数为空，处理所有类型
                if (ReadMsg.IndexOf("OK", StringComparison.Ordinal) < 0)
                {
                    ShowOperatorMessage(ReadMsg);
                    return;
                }

                if (Dev1DaqChannel.Count < 1)
                {
                    ShowOperatorMessage(@"未读取到DAQ电流和EPB卡钳对应关系！");
                    return;
                }

                // Dev2通道, Dev2DaqChannel
                ReadMsg = ClsXmlOperation.GetDaqAIChannelMapping(
                    RuntimeConfigPaths.GetPath("AIConfig.xml"), "Dev2", Dev2UsedDaqAIChannels,
                    out Dev2DaqChannel, new string[] { }); //paramTypeFilter 参数为空，不过滤
                if (ReadMsg.IndexOf("OK", StringComparison.Ordinal) < 0)
                {
                    ShowOperatorMessage(ReadMsg);
                    return;
                }

                if (Dev2DaqChannel.Count < 1)
                {
                    ShowOperatorMessage(@"未读取到DAQ电流和EPB卡钳对应关系！");
                    return;
                }

                // Dev1的系数映射
                ReadMsg = ClsXmlOperation.GetDaqScaleMapping(
                    RuntimeConfigPaths.GetPath("AIConfig.xml"), "Dev1", out Dev1ParaNameToScale);
                if (ReadMsg.IndexOf("OK", StringComparison.Ordinal) < 0)
                {
                    ShowOperatorMessage(ReadMsg);
                    return;
                }

                ReadMsg = ClsXmlOperation.GetDaqOffsetMapping(
                    RuntimeConfigPaths.GetPath("AIConfig.xml"), "Dev1", out Dev1ParaNameToOffset);
                if (ReadMsg.IndexOf("OK", StringComparison.Ordinal) < 0)
                {
                    ShowOperatorMessage(ReadMsg);
                    return;
                }

                ReadMsg = ClsXmlOperation.GetDaqZeroValueMapping(
                    RuntimeConfigPaths.GetPath("AIConfig.xml"), "Dev1", out Dev1ParaNameToZeroValue);
                if (ReadMsg.IndexOf("OK", StringComparison.Ordinal) < 0)
                {
                    ShowOperatorMessage(ReadMsg);
                    return;
                }

                // Dev2的系数映射
                ReadMsg = ClsXmlOperation.GetDaqScaleMapping(
                    RuntimeConfigPaths.GetPath("AIConfig.xml"), "Dev2", out Dev2ParaNameToScale);
                if (ReadMsg.IndexOf("OK", StringComparison.Ordinal) < 0)
                {
                    ShowOperatorMessage(ReadMsg);
                    return;
                }

                ReadMsg = ClsXmlOperation.GetDaqOffsetMapping(
                    RuntimeConfigPaths.GetPath("AIConfig.xml"), "Dev2", out Dev2ParaNameToOffset);
                if (ReadMsg.IndexOf("OK", StringComparison.Ordinal) < 0)
                {
                    ShowOperatorMessage(ReadMsg);
                    return;
                }

                ReadMsg = ClsXmlOperation.GetDaqZeroValueMapping(
                    RuntimeConfigPaths.GetPath("AIConfig.xml"), "Dev2", out Dev2ParaNameToZeroValue);
                if (ReadMsg.IndexOf("OK", StringComparison.Ordinal) < 0)
                {
                    ShowOperatorMessage(ReadMsg);
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


                // 1) 先加载“软件默认 Config”下的配置（主要为了拿到 TestName / StoreDir 以及硬件配置）
                var defaultConfigDir = RuntimeConfigPaths.Directory;
                var defaultCfg = ConfigLoader.LoadAll(defaultConfigDir, logger);

                // 2) 根据默认 TestConfig 推算“项目 Config\TestConfig.xml”
                //    若该项目已有配置：直接加载；否则创建一份并清零 EpbRecords 进度
                var projectTest = ConfigLoader.EnsureProjectTestConfig(defaultCfg, logger);

                // 3) 用“项目 TestConfig”替换默认配置中的 Test 部分，
                //    这样后续代码统一使用 _cfg.Test 即表示“当前项目”的试验配置和进度
                defaultCfg.Test = projectTest;
                _cfg = defaultCfg;

                InitializeUiInfoLog();

                // 4) 确保默认 Config\TestConfig.xml 中也同步了 Basic 和 TotalCount（但进度清零）
                //    方便下次启动软件时，仍然能通过默认配置推算出当前项目路径。
                ConfigLoader.UpdateDefaultTestFromProject(projectTest, logger);
                PublishCurrentProjectBuildIdentity();


                // ===== 数据落盘：优先初始化写盘器（用于启动时从 DB 回填 RunCount） =====
                var projectIndexDir = Path.Combine(_cfg.Test.StoreDir, _cfg.Test.TestName);
                var existingIndexDbPath = Path.Combine(projectIndexDir, "index.db");
                _shouldBackfillRunCountFromDbOnLoad = File.Exists(existingIndexDbPath);

                // 1) 创建写盘器（使用 DataRetentionPolicy）
                var latestRetention = _cfg.Test.DataStorageRetention?.Latest
                                      ?? new LatestSnapshotRetentionConfig();
                var programStorage = ProgramStoragePolicy.Load(
                    message => logger?.Warn(message, "Storage"));
                logger?.Info(programStorage.ToStartupLogLine(), "Storage");
                var policy = new DataRetentionPolicy
                {
                    DataStorePath = Path.Combine(Environment.CurrentDirectory, "DataStore"), // 数据根目录
                    IndexAndExportPath = projectIndexDir, // 索引和导出目录
                    FileSizeMb = 100, // 每通道 .dat大小，单位MB，可按需改 384
                    RetainLatestCycles = 10, // 停止时“最新N圈”
                    CleanupMode = "archive", // 或 "delete"
                    RetainLatestStopPackagesPerChannel = latestRetention.RetainStopPackagesPerChannel,
                    RetainAllLatestStopPackages =
                        latestRetention.RetentionMode == StorageRetentionMode.Unlimited,
                    LatestStorageLevel = programStorage.Latest,
                    AlarmStorageLevel = programStorage.Alarm,
                    LearningStorageLevel = programStorage.Learning,
                    HistoricalEnabled = programStorage.HistoricalEnabled,
                    HistoricalRetainCyclesPerChannel = programStorage.HistoricalRetainCyclesPerChannel,
                    RetentionWarningSink = message => logger?.Warn(message, "Storage")
                };
                _diskWriter = new EpbDiskWriter(policy);
                //_diskWriter.StartFreeRun(1); // 暂时注释

                // 适配器：实现 IEpbCycleRecorder，把 EpbDiskWriter 包起来
                _recorder = new DiskWriterRecorderAdapter(_diskWriter);


                // 初始化 EPB 控制器的记录
                InitializeEpbRecords();

                // 若启动时按 DB 权威口径修正了 RunCount，则立即写回项目 TestConfig.xml，保证下次启动一致
                if (_startupRunCountBackfillChanged)
                {
                    SaveEpbRecordsToTestConfigSafe();
                }

                // 初始化通道记录概览区域
                InitEpbSummaryPanel();

                // 30 秒自动保存一次（30,000 毫秒）
                _autoSaveTimer.Interval = 30000;
                _autoSaveTimer.Tick += AutoSaveTimer_Tick;
                _autoSaveTimer.Start();


                LoadEpbController(); // 

                // 初始化曲线
                InitializeCurve();
                //StartListen();
                MakeCurveMapping();
                //MakeDirectionMapping();
                LoadTestConfigToUI();
                //LoadEMBHandlerAndFrameNo();

                LogInfo("1. 编辑试验信息并确认");
                LogInfo("2. 自学习/开始试验");


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


                // 2) 初始化 DO 控制器
                _do = new DoController(_cfg.DO, logger);

                // 3) 初始化 AO 控制器
                _ao = new AoController(_cfg.AO, logger);

                aiConfigDetail =
                    AiConfigLoader.Load(RuntimeConfigPaths.GetPath("AIConfig.xml"));

                twoDeviceAiAcquirer = new TwoDeviceAiAcquirer(
                    aiConfigDetail,
                    _daqRuntimeSettings.SampleRateHz,
                    _daqRuntimeSettings.SamplesPerChannel,
                    10, logger);

                twoDeviceAiAcquirer.OnEngBatch += Acq_OnEngBatch; // 订阅工程值批次到达事件

                twoDeviceAiAcquirer.OnRawBatch += Acq_OnRawBatch; // ← 新增：订阅原始批次事件（两卡通用 ) // 2025/09/09

                // 使用循环初始所有EpbGroup中的CtrlCycles
                foreach (var epbGroup in EpbGroup)
                {
                    var epbRecord = EnsureEpbRecord(epbGroup.EpbNo);
                    epbGroup.CtrlCycles.Text =
                        Math.Max(epbRecord.MechanicalCycleCount, epbRecord.RunCount).ToString();
                }


                // epb管理器初始化
                var safetyMarginMode = ReadSafetyMarginControlModeFromAppConfig(logger);
                _epb = new EpbManager(
                    _cfg,
                    _do,
                    _ao,
                    twoDeviceAiAcquirer,
                    logger,
                    safetyMarginMode,
                    protectedLearningRootIds: _protectedLearningRootId == Guid.Empty
                        ? null
                        : new[] { _protectedLearningRootId });
                _epb.SetRecoveryInfrastructureHealthProvider(
                    WatchdogRuntime.IsRecoveryInfrastructureHealthy);

                // ★ 新增：订阅 EPB 单圈完成事件，用于更新 _uiEpbRecords
                _epb.ChannelCycleCompleted += OnEpbChannelCycleCompleted;
                _epb.ChannelMechanicalCycleCompleted += OnEpbMechanicalCycleCompleted;
                _epb.ChannelAlarmRaised += OnEpbChannelAlarmRaised;
                _epb.ChannelPaused += OnEpbChannelPaused;
                _epb.ChannelResumed += OnEpbChannelResumed;
                _epb.ManualPauseProgressChanged += OnManualPauseProgressChanged;


                // ===== 报警系统初始化（M-7055D / RS-485）=====
                TryInitAlarmSubsystem(logger);


                // 1) 创建写盘器（使用 DataRetentionPolicy）
                // 2) 注入到 EpbManager，数据落盘由 EpbManager 控制
                _epb.Recorder = _recorder;
                AttachSafetyUiEvents();


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
                // Project configuration is the authoritative upstream source for EPB selection.
                ApplyProjectEpbSelectionToMonitor();
                ConfigLoader.SaveUI(_uiCfg);

                #endregion

                twoDeviceAiAcquirer.Start(); // 开始采集


                //数据落盘相关

                // 1) 计算每帧毫秒跨度（旧工程做法） 数据落盘中使用  On 2025/09/09
                _daqTimeSpanMs = 1000.0 / _daqRuntimeSettings.SampleRateHz; // 设置单个试验的采样周期

                // 2) 仅在程序级 Raw 开关显式启用时准备时间戳目录和原始落盘定时器。
                //    禁用时不创建 W\DataStore 下的空日期目录；未来可通过该开关恢复显式 Raw 路径。
                var rawLoggingEnabled = ProgramStoragePolicy.ParseBoolean(
                    ConfigurationManager.AppSettings["RawDataLoggingEnabled"],
                    false,
                    "RawDataLoggingEnabled",
                    message => logger?.Warn(message, "Storage"));
                if (rawLoggingEnabled)
                {
                    PrepareDataStoreDirectory();
                    InitDaqLogTimer(500);
                    logger?.Info("Raw 原始数据落盘已显式启用。", "Storage");
                }
                else
                {
                    _dataStorePath = string.Empty;
                    logger?.Info("Raw 原始数据落盘未启用；不创建 DataStore\\时间戳空目录。", "Storage");
                }
                SetMonitorLifecycle(EpbMonitorLifecycle.Idle);
            }

            catch (Exception ex)
            {
                SetMonitorLifecycle(EpbMonitorLifecycle.InitializationFailed);
                logger?.Error("主监控初始化失败。", "启动", ex);
                ShowOperatorMessage(@"初始化错误 : " + ex.Message, "无人值守初始化", MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                if (!IsDisposed && !Disposing && IsHandleCreated)
                    BeginInvoke((Action)Close);
            }
        }

        internal EpbMonitorLifecycle MonitorLifecycle =>
            (EpbMonitorLifecycle)Volatile.Read(ref _monitorLifecycle);

        private void SetMonitorLifecycle(EpbMonitorLifecycle lifecycle)
        {
            Interlocked.Exchange(ref _monitorLifecycle, (int)lifecycle);
        }

        private bool CanUseIdleFastClose()
        {
            var pendingStop = _stopSessionReceipt.CaptureTask();
            return EpbMonitorClosePolicy.CanUseIdleFastClose(
                MonitorLifecycle,
                _epb?.IsBatchSessionActive == true,
                Volatile.Read(ref _monitorEnergizationAttempted) != 0,
                pendingStop != null && !pendingStop.IsCompleted);
        }

        internal bool CanUseIdleFastCloseForApplicationExit => CanUseIdleFastClose();

        /// <summary>
        /// 初始化 EPB 控制器的试验记录列表：
        /// 1) 从 <see cref="_cfg.Test.EpbRecords" /> 加载已有记录；
        /// 2) 确保 1..12 每个通道至少有一条 <see cref="EpbTestRecord" /> 记录；
        /// 3) 后续运行中所有更新都针对 <see cref="_uiEpbRecords" />。
        /// </summary>
        private void InitializeEpbRecords()
        {
            _uiEpbRecords = new List<EpbTestRecord>();

            var targetCyclesFromBasic = _cfg.Test.TestTarget; // 


            // 1) 从配置加载
            var cfgRecords = _cfg?.Test?.EpbRecords;
            if (cfgRecords != null)
            {
                foreach (var record in cfgRecords)
                {
                    if (record != null)
                    {
                        // 如果IsSameCycleForAllEpb为true，则TotalCount赋值为_cfg.Test.TestTarget;
                        if (_cfg.Test.IsSameCycleForAllEpb) record.TotalCount = targetCyclesFromBasic;

                        _uiEpbRecords.Add(record);
                    }
                }
            }

            // 2) 补齐 1..12 的默认记录（如果缺少）
            for (var id = 1; id <= 12; id++)
            {
                if (_uiEpbRecords.Find(r => r.Id == id) == null)
                {
                    _uiEpbRecords.Add(EpbTestRecord.CreateDefault(id));
                }
            }

            // 3) 按通道排序一下，便于 UI 显示
            _uiEpbRecords.Sort((a, b) => a.Id.CompareTo(b.Id));

            // 3.1) 启动加载时：按“DB 为权威”的口径回填 RunCount（completed + alarm）
            _startupRunCountBackfillChanged = TryBackfillRunCountFromDiskIndex();

            foreach (var rec in _uiEpbRecords)
            {
                rec.InitializeOnLoad(DateTime.Now);
            }
        }


        /// <summary>
        ///     启动加载试验时，从 SQLite(index.db) 回填每个 EPB 的累计圈次数到 <see cref="_uiEpbRecords"/>。
        /// </summary>
        /// <returns>
        ///     若存在任何通道的 <see cref="EpbTestRecord.RunCount"/> 被更新，则返回 true；否则返回 false。
        /// </returns>
        /// <remarks>
        ///     <para>
        ///     口径说明：
        ///     <list type="bullet">
        ///         <item>
        ///             <description>
        ///             本项目中 <see cref="EpbTestRecord.RunCount"/> 既用于 UI 展示“已运行圈数”，也用于“下一圈号”的续号基准；
        ///             因此启动回填必须与落盘使用的圈号口径一致。
        ///             </description>
        ///         </item>
        ///         <item>
        ///             <description>
        ///             回填采用：<c>RunCount = MAX(cycle_number)</c>（CycleNumber &gt; 0）。
        ///             这样即便现场手工修正过 <c>cycle_number</c>（例如补齐/跳号），
        ///             也能保证 TestConfig.xml 与 index.db 的“圈号基准”一致，避免开始后出现差 1 或唯一键冲突。
        ///             </description>
        ///         </item>
        ///     </list>
        ///     </para>
        ///     <para>
        ///     为避免“首次新建项目/缺失 DB 文件”导致把 XML 进度覆盖成 0：
        ///     仅当启动时检测到项目目录下已存在 index.db 时，才执行回填。
        ///     </para>
        /// </remarks>
        private bool TryBackfillRunCountFromDiskIndex()
        {
            if (!_shouldBackfillRunCountFromDbOnLoad)
                return false;

            var writer = _diskWriter;
            if (writer == null)
                return false;

            var changed = false;

            try
            {
                foreach (var rec in _uiEpbRecords)
                {
                    if (rec == null || rec.Id < 1 || rec.Id > 12)
                        continue;

                    // 关键：使用“最大圈号”回填，保证与 BeginCycle/续号基准一致。
                    var dbLastCycleNumber = writer.GetLastCycleNumber(rec.Id);
                    if (dbLastCycleNumber < 0) dbLastCycleNumber = 0;

                    if (rec.RunCount != dbLastCycleNumber)
                    {
                        rec.RunCount = dbLastCycleNumber;
                        changed = true;
                    }
                    if (rec.MechanicalCycleCount < dbLastCycleNumber)
                    {
                        rec.MechanicalCycleCount = dbLastCycleNumber;
                        changed = true;
                    }
                    var dbMechanicalCount = writer.GetMechanicalCycleCompletedCount(rec.Id);
                    if (rec.MechanicalCycleCount < dbMechanicalCount)
                    {
                        rec.MechanicalCycleCount = dbMechanicalCount;
                        changed = true;
                    }
                }
            }
            catch (Exception ex)
            {
                // 启动容错：不因为 DB 回填失败阻塞程序
                logger?.Warn("启动时从 index.db 回填 RunCount 失败: " + ex.Message, "数据落盘");
                return false;
            }

            return changed;
        }


        /// <summary>
        /// 确保并返回指定通道的试验记录：
        /// 如果列表中不存在，则创建默认记录并加入列表。
        /// </summary>
        /// <param name="id">EPB 通道 Id（1..12）。</param>
        /// <returns>该通道对应的 <see cref="EpbTestRecord" /> 实例。</returns>
        private EpbTestRecord EnsureEpbRecord(int id)
        {
            lock (_epbRecordsLock)
            {
                var rec = _uiEpbRecords.Find(r => r.Id == id);
                if (rec != null)
                    return rec;

                rec = EpbTestRecord.CreateDefault(id);
                _uiEpbRecords.Add(rec);
                return rec;
            }
        }

        /// <summary>
        /// 来自 EpbManager 的“单圈完成”事件回调：
        /// 在这里把每个 EPB 的运行圈数同步到 _uiEpbRecords。
        /// </summary>
        /// <param name="channel">EPB 通道号（1..12）。</param>
        /// <param name="sessionRunCount">
        /// 本次试验 Session 内的圈数（从 1 开始），
        /// 如无需要可仅用于日志，不参与计算。
        /// </param>
        private void OnEpbChannelCycleCompleted(int channel, int sessionRunCount)
        {
            // —— 1) UI 线程同步 —— //
            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(new Action<int, int>(OnEpbChannelCycleCompleted), channel, sessionRunCount);
                }
                catch
                {
                    // ignored
                }

                return;
            }

            if (_uiEpbRecords == null)
                return;

            // —— 2) 用锁保护记录访问 —— //
            EpbTestRecord record;

            lock (_epbRecordsLock)
            {
                record = _uiEpbRecords.FirstOrDefault(r => r.Id == channel);
                if (record == null)
                    return;

                // 更新运行时间 + RunCount + LatestStartTime
                record.IncrementCycleAndUpdateTime(DateTime.Now);

                if (record.Status == EpbTestStatus.Completed) // 已完成
                {
                    // EpbManager 已在自然完成事务中执行最终断能、移除运行对象、
                    // 释放液压租约并发布 Completed。这里仅更新UI，不再调用
                    // StopChannel；旧调用会把已完成通道重新覆盖为ManualStopped，
                    // 使看门狗无法区分自然完成与人工单通道停止。
                    LogInfo($"EPB-{record.Id} 已完成试验。");
                }
            }

            // —— 3) 更新左侧 EPBGroup —— //
            EpbGroup[channel - 1].CtrlCycles.Text =
                Math.Max(record.MechanicalCycleCount, record.RunCount).ToString();

            if (_currentEpbSummaryChannel == channel && record.Status == EpbTestStatus.Completed)
            {
                int nextChannel;
                lock (_epbRecordsLock)
                    nextChannel = EpbProjectPolicies.FindSummaryChannelAfterCompletion(
                        _uiEpbRecords,
                        channel);

                if (nextChannel != channel)
                    SelectEpbSummaryChannel(nextChannel);
                else
                    RefreshCurrentEpbSummary(channel);
            }
            else
            {
                RefreshCurrentEpbSummary(channel);
            }

            // —— 4) 下拉框右侧面板选中时刷新 —— //
            // —— ?? 取消实时保存，改为“定时自动保存” —— //
        }

        private void OnEpbMechanicalCycleCompleted(
            int channel,
            CycleAttemptKind kind,
            int cycleNumber)
        {
            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(new Action<int, CycleAttemptKind, int>(
                        OnEpbMechanicalCycleCompleted), channel, kind, cycleNumber);
                }
                catch { }
                return;
            }

            EpbTestRecord record;
            lock (_epbRecordsLock)
            {
                record = _uiEpbRecords?.FirstOrDefault(r => r.Id == channel);
                if (record == null) return;
                try
                {
                    // Use the manager/SQLite monotonic fact instead of blindly
                    // adding one on the UI thread.  Mechanical and formal
                    // completion callbacks are independently marshalled with
                    // BeginInvoke; their arrival order must not double-count a
                    // successful formal circle.
                    record.ReconcileMechanicalCycleCount(
                        _epb?.GetDurableMechanicalCycleCount(channel) ?? 0);
                }
                catch
                {
                    // The event itself proves one physical completion.  This is
                    // only a last-resort fallback when durable reconciliation is
                    // temporarily unavailable.
                    record.IncrementMechanicalCycle();
                }
            }
            EpbGroup[channel - 1].CtrlCycles.Text = record.MechanicalCycleCount.ToString();
            RefreshCurrentEpbSummary(channel);
            SaveEpbRecordsToTestConfigSafe();
            // The durable database and the always-visible channel counter are
            // the authoritative per-cycle evidence.  Emitting one operator UI
            // line per channel/cycle obscures warnings and recovery events.
        }

        private void OnEpbChannelAlarmRaised(int channel, string reason)
        {
            var record = EnsureEpbRecord(channel);
            lock (_epbRecordsLock) record.SetAlarm();
            RefreshCurrentEpbSummary(channel);
            LogInfo($"卡钳{channel} 报警：{AlarmMessageLocalizer.ToUserMessage(reason)}");
        }

        private void OnEpbChannelPaused(int channel)
        {
            var record = EnsureEpbRecord(channel);
            lock (_epbRecordsLock) record.Pause();
            RefreshCurrentEpbSummary(channel);
            LogInfo($"卡钳{channel} 已暂停");
        }

        private void OnEpbChannelResumed(int channel)
        {
            var record = EnsureEpbRecord(channel);
            lock (_epbRecordsLock) record.Resume(DateTime.Now);
            RefreshCurrentEpbSummary(channel);
            LogInfo($"卡钳{channel} 已恢复运行");
        }


        /// <summary>
        ///     将 TestConfig 内容加载到 UI（带空值保护 + 派生值 + Led 显示更新）
        /// </summary>
        private void LoadTestConfigToUI()
        {
            try
            {
                if (_cfg?.Test == null)
                {
                    ShowOperatorMessage(@"TestConfig 尚未加载！", @"提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var test = _cfg.Test;

                // ====  文本框显示基本参数 ===========================================
                TxtTestName.Text = test.TestName ?? string.Empty;
                TxtTestCycleTime.Text = test.TestPeriod.ToString(CultureInfo.InvariantCulture);
                TxtTargetCycles.Text = test.TestTarget.ToString(CultureInfo.InvariantCulture);

                //—— IsSameCycleForAllEpb —— //
                uiCheckBoxIsSameCycleForAllEpb.Checked = test.IsSameCycleForAllEpb;

                // ====  UI 提示 =====================================================
                LogInfo("已加载试验配置。");
            }
            catch (Exception ex)
            {
                ShowOperatorMessage($@"加载试验配置失败：{ex.Message}",
                    @"错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
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


        private void LoadEpbController()
        {
            try
            {
                for (var i = 0; i < 12; i++)
                    EpbGroup[i] = new ClsEPBControler
                    {
                        EpbNo = i + 1,
                        EpbName = "EPB" + (i + 1),
                        //  EpbGroup[i].Cycles = 0;
                        IsEnabel = true
                    };

                EpbGroup[0].CtrlJoinTest = ChkEpb1;
                EpbGroup[1].CtrlJoinTest = ChkEpb2;
                EpbGroup[2].CtrlJoinTest = ChkEpb3;
                EpbGroup[3].CtrlJoinTest = ChkEpb4;
                EpbGroup[4].CtrlJoinTest = ChkEpb5;
                EpbGroup[5].CtrlJoinTest = ChkEpb6;
                EpbGroup[6].CtrlJoinTest = ChkEpb7;
                EpbGroup[7].CtrlJoinTest = ChkEpb8;
                EpbGroup[8].CtrlJoinTest = ChkEpb9;
                EpbGroup[9].CtrlJoinTest = ChkEpb10;
                EpbGroup[10].CtrlJoinTest = ChkEpb11;
                EpbGroup[11].CtrlJoinTest = ChkEpb12;


                /*
                EpbGroup[0].CtrlCurrentEmb = RadEmb1;
                EpbGroup[1].CtrlCurrentEmb = RadEmb2;
                EpbGroup[2].CtrlCurrentEmb = RadEmb3;
                EpbGroup[3].CtrlCurrentEmb = RadEmb4;
                EpbGroup[4].CtrlCurrentEmb = RadEmb5;
                EpbGroup[5].CtrlCurrentEmb = RadEmb6; */

                EpbGroup[0].CtrlRunning = SwitchEpb1;
                EpbGroup[1].CtrlRunning = SwitchEpb2;
                EpbGroup[2].CtrlRunning = SwitchEpb3;
                EpbGroup[3].CtrlRunning = SwitchEpb4;
                EpbGroup[4].CtrlRunning = SwitchEpb5;
                EpbGroup[5].CtrlRunning = SwitchEpb6;
                EpbGroup[6].CtrlRunning = SwitchEpb7;
                EpbGroup[7].CtrlRunning = SwitchEpb8;
                EpbGroup[8].CtrlRunning = SwitchEpb9;
                EpbGroup[9].CtrlRunning = SwitchEpb10;
                EpbGroup[10].CtrlRunning = SwitchEpb11;
                EpbGroup[11].CtrlRunning = SwitchEpb12;


                EpbGroup[0].CtrlCycles = LabEpb1;
                EpbGroup[1].CtrlCycles = LabEpb2;
                EpbGroup[2].CtrlCycles = LabEpb3;
                EpbGroup[3].CtrlCycles = LabEpb4;
                EpbGroup[4].CtrlCycles = LabEpb5;
                EpbGroup[5].CtrlCycles = LabEpb6;
                EpbGroup[6].CtrlCycles = LabEpb7;
                EpbGroup[7].CtrlCycles = LabEpb8;
                EpbGroup[8].CtrlCycles = LabEpb9;
                EpbGroup[9].CtrlCycles = LabEpb10;
                EpbGroup[10].CtrlCycles = LabEpb11;
                EpbGroup[11].CtrlCycles = LabEpb12;

                /*
                EpbGroup[0].CtrlAlert = AlertEmb1;
                EpbGroup[1].CtrlAlert = AlertEmb2;
                EpbGroup[2].CtrlAlert = AlertEmb3;
                EpbGroup[3].CtrlAlert = AlertEmb4;
                EpbGroup[4].CtrlAlert = AlertEmb5;
                EpbGroup[5].CtrlAlert = AlertEmb6;   // 界面上没有这些控件，暂时注释掉
                */


                // EpbGroup[0].CtrlPower = SwitchPower1;
                // EpbGroup[1].CtrlPower = SwitchPower2;
                // EpbGroup[2].CtrlPower = SwitchPower3;
                // EpbGroup[3].CtrlPower = SwitchPower4;
                // EpbGroup[4].CtrlPower = SwitchPower5;
                // EpbGroup[5].CtrlPower = SwitchPower6;


                for (var i = 0; i < 12; i++)
                {
                    //EpbGroup[i].CtrlRunning.Enabled = false; //单个启动按钮设为不允许，启动之后才允许
                    var index = i;
                    EpbGroup[i].CtrlJoinTest.CheckedChanged += (sender, e) => JoinEmbChanged(sender, e, index);

                    // EpbGroup[i].CtrlCurrentEmb.CheckedChanged += (sender, e) => CurrentEmbChanged(sender, e, index); // 界面上没有这个控件，暂时注释掉


                    EpbGroup[i].CtrlRunning.CheckedChanged += (sender, e) =>
                    {
                        RuningStatusChanged(sender, ((ToggleButton)sender).Checked, index);
                    };

                    EpbGroup[i].CtrlRunning.Click += (sender, e) => RunningClick(sender, e, index);
                    //EpbGroup[i].CtrlPower.Click += (sender, e) => PowerClick(sender, e, index);
                    //EpbGroup[i].CtrlPower.KeyPress += (sender, e) => CtrlPower_KeyHandler(sender, e, index);
                    //EpbGroup[i].CtrlPower.KeyDown += (sender, e) => CtrlPower_KeyHandler(sender, e, index);
                    //EpbGroup[i].CtrlPower.KeyUp += (sender, e) => CtrlPower_KeyHandler(sender, e, index);
                    //EpbGroup[i].CtrlPower.CheckedChanged += (sender, e) => PowerClick(sender, e, index);
                }
            }
            catch (Exception ex)
            {
                ShowOperatorMessage(@"初始化组件失败！" + ex.Message, "无人值守初始化",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void CtrlPower_KeyHandler(object sender, EventArgs e, int index)
        {
            //throw new NotImplementedException();
        }

        private void RunningClick(object sender, EventArgs e, int index)
        {
            // 暂时注释处理
            /*if (!EpbGroup[index].CtrlPower.Checked && EpbGroup[index].CtrlRunning.Checked) //运行状态
            {
                ShowOperatorMessage(@"请先打开电源！");
                EpbGroup[index].CtrlRunning.Checked = false;
            }*/
        }


        private async void PowerClick(object sender, EventArgs e, int index)
        {
            if (_isCtrlPowerPressing) return;

            _isCtrlPowerPressing = true;
            EpbGroup[index].CtrlPower.Enabled = false; // 禁用按钮，防止重复点击
            EPBGroupBox.Enabled = false; // 禁用整个组框，防止其他操作
            try
            {
                if (!IsTestConfirm)
                {
                    ShowOperatorMessage(@"请先确认试验信息！");
                    EpbGroup[index].CtrlPower.Toggle();

                    // EpbGroup[index].CtrlPower.Checked = false;
                    //
                    EpbGroup[index].CtrlPower.Refresh();
                    return;
                }


                if (!EpbGroup[index].CtrlPower.Checked && EpbGroup[index].CtrlRunning.Checked) //运行状态想关电源
                {
                    ShowOperatorMessage(@"请先停止运行再关闭电源！");
                    EpbGroup[index].CtrlPower.Checked = true;
                    return;
                }

                if (!EpbGroup[index].CtrlPower.Checked && !EpbGroup[index].CtrlRunning.Checked) //非运行状态想关电源
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
                        LogInfo($"关闭EMB{index + 1} 继电器开关失败");
                        ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError,
                            "关闭EMB" + (index + 1) + "继电器开关失败!", "串口操作");
                        ClsGlobal.PowerStatus[index] = 2;
                    }
                    else
                    {
                        LogInfo($"关闭EMB{index + 1} 继电器开关");
                        ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation,
                            "关闭EMB" + (index + 1) + "继电器开关!", "UI 操作");
                        ClsGlobal.PowerStatus[index] = 1;


                        /*if (EpbGroup[index].IsEnabel)
                    {
                        EpbGroup[index].CtrlAlert.State = UILightState.Off;
                        EpbGroup[index].CtrlAlert.OffCenterColor = Color.FromArgb(140, 140, 140);
                        EpbGroup[index].CtrlAlert.OffColor = Color.FromArgb(140, 140, 140);
                    }*/
                    }


                    return;
                }


                if (EpbGroup[index].CtrlPower.Checked)
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
                        LogInfo($"打开EMB{index + 1} 继电器开关失败");
                        ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError,
                            "打开EMB" + (index + 1) + "继电器开关失败!", "串口操作");
                        ClsGlobal.PowerStatus[index] = 1;
                    }
                    else
                    {
                        LogInfo($"打开EMB{index + 1} 继电器开关");
                        ClsLogProcess.AddToInfoList(MaxInfos, ref LogInformation,
                            "打开EMB" + (index + 1) + "继电器开关!", "UI 操作");
                        ClsGlobal.PowerStatus[index] = 2;
                        /*if (EpbGroup[index].IsEnabel)
                    {
                        EpbGroup[index].CtrlAlert.State = UILightState.On;
                        EpbGroup[index].CtrlAlert.OffCenterColor = Color.FromArgb(140, 140, 140);
                        EpbGroup[index].CtrlAlert.OffColor = Color.FromArgb(140, 140, 140);
                    }*/
                    }
                }
            }
            finally
            {
                _isCtrlPowerPressing = false;

                EpbGroup[index].CtrlPower.Enabled = true; // 重新启用按钮
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


        private void ApplyProjectEpbSelectionToMonitor()
        {
            if (_cfg?.Test == null) return;

            _cfg.Test.EnsureEpbRecords(12);
            for (var i = 0; i < 12; i++)
            {
                var selection = EpbProjectPolicies.ApplySettingsSelection(
                    _cfg.Test.GetEpbRecord(i + 1).Enabled);
                var powerCheck = EpbGroup[i]?.CtrlJoinTest;
                if (powerCheck != null && powerCheck.Checked != selection.PowerSelected)
                    powerCheck.Checked = selection.PowerSelected;

                var curveCheck = Controls.Find($"CheckEpbA{i + 1}", true)
                    .OfType<CheckEdit>()
                    .FirstOrDefault();
                if (curveCheck != null && curveCheck.Checked != selection.CurveSelected)
                    curveCheck.Checked = selection.CurveSelected;
            }
        }

        private void JoinEmbChanged(object sender, EventArgs e, int index)
        {
            var checkBox = (CheckEdit)sender;

            // One-way propagation: power-group selection drives the curve only.
            var curveCheck = Controls.Find($"CheckEpbA{index + 1}", true)
                .OfType<CheckEdit>()
                .FirstOrDefault();
            var currentSelection = new EpbSelectionState(
                _cfg?.Test?.GetEpbRecord(index + 1)?.Enabled ?? false,
                checkBox.Checked,
                curveCheck?.Checked ?? false);
            var propagated = EpbProjectPolicies.ApplyPowerSelection(
                currentSelection,
                checkBox.Checked);
            if (curveCheck != null && curveCheck.Checked != propagated.CurveSelected)
                curveCheck.Checked = propagated.CurveSelected;

            if (checkBox.Checked)
            {
                // EpbGroup[index].CtrlCurrentEmb.Enabled = true; // 界面上没有这个控件，暂时注释掉
                // EpbGroup[index].CtrlPower.Enabled = true; // 界面上没有这个控件，暂时注释
                // EpbGroup[index].CtrlAlert.Enabled = true; // 界面上没有这个控件，暂时注释掉
                EpbGroup[index].CtrlCycles.Enabled = true;
                EpbGroup[index].IsEnabel = true;
            }
            else
            {
                // EpbGroup[index].CtrlCurrentEmb.Enabled = false; // 界面上没有这个控件，暂时注释掉
                // EpbGroup[index].CtrlPower.Enabled = false; // 界面上没有这个控件暂时注释
                // EpbGroup[index].CtrlAlert.Enabled = false; // 界面上没有这个控件，暂时注释掉
                EpbGroup[index].CtrlCycles.Enabled = false;
                // EpbGroup[index].CtrlCurrentEmb.Checked = false; // 界面上没有这个控件，暂时注释掉
                EpbGroup[index].IsEnabel = false;
            }
        }

        private void RuningStatusChanged(object sender, bool value, int index)
        {
            // if (value)
            //     // StartEmbControlTimer(index); // 启动指定通道
            // // EpbGroup[index].CtrlAlert.OffCenterColor = Color.FromArgb(140, 140, 140);
            // // EpbGroup[index].CtrlAlert.OffColor = Color.FromArgb(140, 140, 140);
            // // EpbGroup[index].CtrlAlert.OnCenterColor = Color.Lime;
            // // EpbGroup[index].CtrlAlert.OnColor = Color.Lime;
            // // EpbGroup[index].CtrlAlert.State = UILightState.Blink;
            // else
            //     // StopEmbControlTimer(index); // 停止指定通道
            // // EpbGroup[index].CtrlAlert.State = UILightState.On;
        }

        private void BtnTest_Click(object sender, EventArgs e)
        {
            try
            {
                if (_alarmManager == null)
                {
                    ShowOperatorMessage(@"报警子系统未初始化（未加载 AlarmConfig.xml 或初始化失败）。", @"提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (_alarmPanelTestForm != null && !_alarmPanelTestForm.IsDisposed)
                {
                    _alarmPanelTestForm.Close();
                    _alarmPanelTestForm = null;
                    return;
                }

                _alarmPanelTestForm = new FrmAlarmPanelTest(_alarmManager);
                _alarmPanelTestForm.FormClosed += (_, __) => { _alarmPanelTestForm = null; };
                _alarmPanelTestForm.Show(this);
            }
            catch (Exception ex)
            {
                ShowOperatorMessage($@"打开测试界面失败：{ex.Message}", @"提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        #region 测试相关代码 - 正式运行删除

        /// <summary>
        ///     切换开关事件，测试代码，正式运行时请删除
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void toggleSwitch1_Toggled(object sender, EventArgs e)
        {
            // 打开所有epb
            //for (var i = 0; i < 12; i++) _do.SetEpb(i + 1, toggleSwitch1.IsOn);

            // if (toggleSwitch1.IsOn)
            //     await _epb.StartChannelAsync(4);
            // else
            //     _epb.StopChannel(4);

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
            SetMonitorLifecycle(EpbMonitorLifecycle.Closed);
            try
            {
                ReleaseOwnedControlHardwareOnce();
            }
            catch (Exception ex)
            {
                logger?.Warn("FormClosed 最终硬件释放重试失败：" + ex.Message, "EPB");
            }
        }

        private bool ReleaseOwnedControlHardwareOnce()
        {
            if (_epb != null)
                _epb.ManualPauseProgressChanged -= OnManualPauseProgressChanged;
            Action managerRelease = _epb == null
                ? null
                : (Action)_epb.ReleaseHardwareForRestart;
            return _hardwareReleaseOwner.Release(
                managerRelease,
                ReleaseDirectControlHardwareFallback,
                ex => logger?.Warn(
                    "关闭窗口时控制层后台任务/硬件释放失败，执行直接硬件兜底：" + ex.Message,
                    "EPB"));
        }

        private void OnManualPauseProgressChanged(ManualPauseProgressSnapshot snapshot)
        {
            WatchdogRuntime.PersistManualPauseProgress(snapshot);
        }

        private void ReleaseDirectControlHardwareFallback()
        {
            // EpbManager 尚未接管（初始化中途失败）或管理器释放异常时，先断开
            // 电机DO，再归零液压AO，最后停止采集；随后按创建顺序的逆序释放
            // DAQ、AO、DO。每个控制器本身也必须幂等。
            try { _do?.AllOff(); }
            catch (Exception ex) { logger?.Warn("直接兜底关闭DO失败：" + ex.Message, "DO"); }
            try { _ao?.ResetAll(); }
            catch (Exception ex) { logger?.Warn("直接兜底归零AO失败：" + ex.Message, "AO"); }
            try { twoDeviceAiAcquirer?.Stop(); }
            catch (Exception ex) { logger?.Warn("直接兜底停止DAQ失败：" + ex.Message, "DAQ"); }
            try { twoDeviceAiAcquirer?.Dispose(); }
            catch (Exception ex) { logger?.Warn("直接兜底释放DAQ失败：" + ex.Message, "DAQ"); }
            try { _ao?.Dispose(); }
            catch (Exception ex) { logger?.Warn("直接兜底释放AO失败：" + ex.Message, "AO"); }
            try { _do?.Dispose(); }
            catch (Exception ex) { logger?.Warn("直接兜底释放DO失败：" + ex.Message, "DO"); }
        }

        private void RevokeManualStopExitAuthorizationBeforeEnergization()
        {
            _stopSessionReceipt.RevokeForNewStart();
            _manualStopExitReceipt.RevokeForNewStart();
            Interlocked.Exchange(ref _operatorStopRequested, 0);
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

            // —— 合并：仅保留每个设备“最新一批”，并调度一次 UI 处理 —— //
            if (eng == null) return;

            var item = new EngBatchPending { Dev = dev, Eng = eng, Current = current, Last = last };
            var slot = string.Equals(dev, "Dev2", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            _pendingEngBatches.Publish(slot, item);

            ScheduleEngBatchUiWork();
        }

        /// <summary>
        ///     调度一次“工程值批次”的 UI 处理。
        ///     <para>
        ///     该方法保证同一时刻最多只有一个 UI 处理任务在消息队列中，避免高频回调造成的
        ///     <see cref="Control.BeginInvoke(Delegate)"/> 洪泛与 STA 消息泵阻塞。
        ///     </para>
        /// </summary>
        private void ScheduleEngBatchUiWork()
        {
            if (_isClosing || Volatile.Read(ref _formClosedFlag) == 1 || IsDisposed || !IsHandleCreated)
                return;

            if (Interlocked.Exchange(ref _engUiWorkScheduled, 1) == 1)
                return;

            try
            {
                if (InvokeRequired)
                {
                    BeginInvoke(new Action(ProcessPendingEngBatches));
                }
                else
                {
                    ProcessPendingEngBatches();
                }
            }
            catch
            {
                Interlocked.Exchange(ref _engUiWorkScheduled, 0);
            }
        }

        /// <summary>
        ///     在 UI 线程上处理“待处理的最新工程值批次”。
        ///     <remarks>
        ///     处理策略：每个设备仅消费最后一批；若处理过程中又有新批次到达，退出前会再次调度。
        ///     </remarks>
        /// </summary>
        private void ProcessPendingEngBatches()
        {
            if (_isClosing || Volatile.Read(ref _formClosedFlag) == 1 || IsDisposed)
            {
                Interlocked.Exchange(ref _engUiWorkScheduled, 0);
                return;
            }

            try
            {
                EngBatchPending p1;
                EngBatchPending p2;

                _pendingEngBatches.TryTake(out p1, out p2);

                if (p1 != null)
                    ApplyEngBatchToCurves(p1.Dev, p1.Eng, p1.Current, p1.Last);

                if (p2 != null)
                    ApplyEngBatchToCurves(p2.Dev, p2.Eng, p2.Current, p2.Last);

                // 瞬时值刷新节流：最多每 200ms 更新一次
                var nowTick = Environment.TickCount;
                if ((p1 != null || p2 != null) &&
                    unchecked(nowTick - _lastInstantUiUpdateTick) >= InstantUiUpdateMinIntervalMs)
                {
                    _lastInstantUiUpdateTick = nowTick;
                    UpdateInstantDisplayValues();
                }
            }
            finally
            {
                Interlocked.Exchange(ref _engUiWorkScheduled, 0);

                // 若在处理期间又来了新批次：补一次调度
                if (_pendingEngBatches.HasPending)
                    ScheduleEngBatchUiWork();
            }
        }

        /// <summary>
        ///     将单个设备的一批工程值样本路由并追加到曲线缓存。
        /// </summary>
        /// <param name="dev">设备标识（通常为 "Dev1" 或 "Dev2"）。</param>
        /// <param name="eng">工程值矩阵：行=通道，列=样本。</param>
        /// <param name="current">本批次到达时间。</param>
        /// <param name="last">上一批次到达时间。</param>
        private void ApplyEngBatchToCurves(string dev, double[,] eng, DateTime current, DateTime last)
        {
            if (_isClosing || Volatile.Read(ref _formClosedFlag) == 1) return;
            if (eng == null) return;

            var rows = eng.GetLength(0);
            var cols = eng.GetLength(1);
            if (rows <= 0 || cols <= 0) return;

            if (_daqRuntimeSettings.SampleRateHz <= 0)
            {
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "DaqFrequency 未正确设置", "曲线显示");
                return;
            }

            var dt = 1.0 / _daqRuntimeSettings.SampleRateHz;

            // —— 时间轴对齐 ——
            // 旧的 gapSec 逻辑已移除，改用绝对时间戳 current 对齐，彻底解决多设备不同步问题。
            // 无论 UI 是否丢帧，X 轴都严格锚定到 DAQ 的绝对时间。

            try
            {
                // UI 发布允许限频和覆盖，因此“距上次绘制点很远”只能说明 UI 跳过了显示批次，
                // 不能据此断笔。只有当前批次携带的相邻 DAQ 时间戳确实不连续时才断线。
                var breakLine = UiCurveContinuityPolicy.ShouldBreakLine(current, last, cols, dt);

                for (var r = 0; r < rows; r++)
                {
                    if (!_route.TryGetValue(RouteKey(dev, r), out var g))
                        continue;

                    var draw = _checkByGlobal.TryGetValue(g, out var cb) ? cb.Checked : true;

                    // 直接从矩阵追加，避免每批/每通道分配数组造成 GC 抖动
                    AppendChannelBatchFromMatrix(g, eng, r, cols, dt, draw, current, breakLine);
                }

                lastGraphyTime = current;
            }
            catch (Exception ex)
            {
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError, "批次绘制出错: " + ex.Message, "曲线显示");
            }
        }

        /// <summary>
        ///     从工程值矩阵中取出指定行（通道）的一批样本追加到曲线缓存。
        /// </summary>
        /// <param name="globalIndex">全局通道索引（0..14）。</param>
        /// <param name="eng">工程值矩阵：行=通道，列=样本。</param>
        /// <param name="row">要追加的行索引。</param>
        /// <param name="colCount">样本列数（本批次样本数）。</param>
        /// <param name="dt">相邻样本时间间隔（秒/点）。</param>
        /// <param name="draw">是否显示该通道。</param>
        /// <param name="batchEndUtc">本批次结束的绝对时间戳（用于绝对对齐）。</param>
        private void AppendChannelBatchFromMatrix(int globalIndex, double[,] eng, int row, int colCount, double dt,
            bool draw, DateTime batchEndUtc, bool breakLine)
        {
            if (_isClosing || Volatile.Read(ref _formClosedFlag) == 1) return;
            if (zedGraphRealChart == null || zedGraphRealChart.IsDisposed) return;

            if (zedGraphRealChart.InvokeRequired)
            {
                try
                {
                    zedGraphRealChart.BeginInvoke(
                        new Action<int, double[,], int, int, double, bool, DateTime, bool>(AppendChannelBatchFromMatrix),
                        globalIndex, eng, row, colCount, dt, draw, batchEndUtc, breakLine);
                }
                catch
                {
                }

                return;
            }

            if (globalIndex < 0 || globalIndex >= _allChs.Length) return;
            if (eng == null) return;
            if (row < 0 || row >= eng.GetLength(0)) return;
            if (colCount <= 0 || colCount > eng.GetLength(1)) return;

            var list = _chData[globalIndex];
            var line = _chCurve[globalIndex];
            if (line != null) line.IsVisible = draw;

            // —— 绝对时间轴计算（彻底解决不同步） —— //
            // 1. 确保绘图零点已锚定
            if (_plotZeroTime == DateTime.MinValue)
                _plotZeroTime = batchEndUtc.AddSeconds(-(colCount - 1) * dt);

            // 2. 计算本批次首个样本的绝对 X 坐标
            //    batchEndUtc 对应 index = colCount - 1
            //    startX 对应 index = 0
            var endX = (batchEndUtc - _plotZeroTime).TotalSeconds;
            var startX = endX - (colCount - 1) * dt;

            // 3. 只有相邻 DAQ 批次本身不连续才断线。UI 限频或邮箱覆盖造成的显示空窗
            //    由 ZedGraph 连接相邻可见点，避免把 UI 忙误画成采集断点。
            var lastX = _lastX[globalIndex];
            var expectedX = list.Count > 0 ? lastX + dt : startX;
            if (breakLine)
            {
                // 不要用 NaN 作为 X：否则后续清理时 (x < purgeBefore) 比较恒为 false，可能卡住裁剪导致点数无限增长。
                // 断线用“正常 X + Y=NaN”即可让 ZedGraph 断笔，同时不影响裁剪。
                if (list.Count > 0)
                    list.Add(expectedX, double.NaN);
            }

            // 显示层抽稀
            var stride = 1;
            try
            {
                if (_daqRuntimeSettings.SampleRateHz > 0)
                {
                    stride = (int)Math.Round(_daqRuntimeSettings.SampleRateHz / UiMaxPlotHz);
                    if (stride < 1) stride = 1;
                }
            }
            catch
            {
                stride = 1;
            }
            var step = dt * stride;

            // 4. 循环添加点
            //    注意：这里直接用 startX + i*dt 计算，不再依赖累加，避免浮点漂移
            for (var i = 0; i < colCount; i += stride)
            {
                var y = eng[row, i];
                if (double.IsNaN(y) || double.IsInfinity(y)) y = double.NaN;
                list.Add(startX + i * dt, y);
            }

            // 更新最后一点的 X
            _lastX[globalIndex] = startX + (colCount - 1) * dt;

            _latestGlobalX = Math.Max(_latestGlobalX, _lastX[globalIndex]);
            _dirtyForRedraw = true;
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
                    // 避免同步 Invoke 阻塞 STA 消息泵（调试期易触发 ContextSwitchDeadlock）
                    zedGraphRealChart.BeginInvoke(
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

            // 显示层抽稀：保持与矩阵追加一致的最大绘图采样率
            var stride = 1;
            try
            {
                if (_daqRuntimeSettings.SampleRateHz > 0)
                {
                    stride = (int)Math.Round(_daqRuntimeSettings.SampleRateHz / UiMaxPlotHz);
                    if (stride < 1) stride = 1;
                }
            }
            catch
            {
                stride = 1;
            }
            var step = dt * stride;

            for (var i = 0; i < daqData.Length; i += stride)
            {
                var y = daqData[i];
                if (double.IsNaN(y) || double.IsInfinity(y)) y = double.NaN;
                list.Add(x, y);
                x += step;
            }

            _lastX[globalIndex] = x - dt;

            // —— 只更新全局最新 X 并请求 UI 定时器刷新 —— //
            _latestGlobalX = Math.Max(_latestGlobalX, _lastX[globalIndex]);
            _dirtyForRedraw = true;
        }

        #endregion


        private async System.Threading.Tasks.Task<BatchStartResult> StartNewBatchAsync(
            bool unattendedRecovery)
        {
            #region 旧的代码

            /*
            try
            {
                // 4) 组装 EpbManager（把回调委托接进去）
                /*_epb = new EpbManager(
                    _cfg,
                    _do,
                    _ao,
                    twoDeviceAiAcquirer,
                    logger);#1#

                // 5) 启动“卡钳1”通道
                //    StartChannel 内部会根据 Test.TestTarget 次数、PeriodMs 周期、Groups 错峰等自动循环
                // _epb.StartChannel(2); //界面卡顿，注释
                // await _epb.StartChannelAsync(1);
                // await _epb.StartChannelAsync(2);
                //await _epb.StartChannelAsync(4);
                //await _epb.StartChannelAsync(5);


                #region 【同步起跑（电源保护）】：学习阶段同组错峰 + 正式阶段锚点对齐且同组错峰（首周期）


                // 1) 收集勾选通道
                var selected = new List<int>();
                for (int chIndex = 0; chIndex < 12; chIndex++)
                {
                    var ch = chIndex + 1;
                    if (EpbGroup[chIndex].CtrlJoinTest.Checked)
                        selected.Add(ch);
                }

                if (selected.Count == 0)
                {
                    // Create and initialize an object with message box settings.
                    XtraMessageBoxArgs args = new XtraMessageBoxArgs()
                    {
                        Caption = "提示",
                        Text = "请至少勾选一个通道！",
                        Buttons = new DialogResult[] { DialogResult.Yes },
                        Icon = SystemIcons.Warning,        // 警告图标
                        DefaultButtonIndex = 0                  // 默认按钮（0=第一个）

                    };
                    // Assign a message box icon.
                    // Display the message box and close the application if the user clicks "Yes".
                    if (await XtraMessageBox.ShowAsync(args) == DialogResult.Yes)
                        return;
                }

                try
                {
                    using var cts = new CancellationTokenSource();

                    // 可绑定到“停止”按钮以触发取消：
                    // uiButtonStop.Click += (_, __) => cts.Cancel();

                    // 若你希望“任一通道学习失败即整体中止”，把第三个参数传 true
                    await _epb.StartChannelsSynchronizedPowerAwareAsync(selected, cts.Token, abortAllIfAnyLearnFailed: false);


                    LogInfo("已按电源保护策略：学习错峰 + 组间同步起跑（同组首周期错峰）");
                }
                catch (OperationCanceledException)
                {
                    LogInfo("操作已取消");
                }
                catch (Exception ex)
                {
                    LogInfo($"启动失败：{ex.Message}");
                }
                finally
                {
                    //启用按钮
                }



                #endregion



                // UI 提示
                LogInfo("卡钳1测试已启动");
            }
            catch (Exception ex)
            {
                ShowOperatorMessage($@"启动卡钳1测试失败：{ex.Message}", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            */

            #endregion

            var channels = new int[] { };
            var startedChannels = Array.Empty<int>();
            BatchStartResult completedStart = null;
            try
            {
                // 4) 组装 EpbManager（把回调委托接进去）
                /*_epb = new EpbManager(
                    _cfg,
                    _do,
                    _ao,
                    twoDeviceAiAcquirer,
                    logger);*/

                // 5) 启动“卡钳1”通道
                //    StartChannel 内部会根据 Test.TestTarget 次数、PeriodMs 周期、Groups 错峰等自动循环
                // _epb.StartChannel(2); //界面卡顿，注释
                // await _epb.StartChannelAsync(1);
                // await _epb.StartChannelAsync(2);
                //await _epb.StartChannelAsync(4);
                //await _epb.StartChannelAsync(5);

                #region 【同步起跑（电源保护）】：学习阶段同组错峰 + 正式阶段锚点对齐且同组错峰（首周期）

                // 1) 收集勾选通道
                var selected = new List<int>();
                for (var chIndex = 0; chIndex < 12; chIndex++)
                {
                    var ch = chIndex + 1;
                    if (EpbGroup[chIndex].CtrlJoinTest.Checked)
                    {
                        selected.Add(ch);
                    }
                }

                if (selected.Count == 0)
                {
                    if (unattendedRecovery)
                        throw new InvalidOperationException(
                            "无人值守恢复没有勾选任何检查点通道，拒绝静默停在启动界面。");
                    // Create and initialize an object with message box settings.
                    var args = new XtraMessageBoxArgs
                    {
                        Caption = "提示",
                        Text = "请至少勾选一个通道！",
                        Buttons = new[] { DialogResult.Yes },
                        Icon = SystemIcons.Warning, // 警告图标
                        DefaultButtonIndex = 0 // 默认按钮（0=第一个）
                    };
                    // Assign a message box icon.
                    // Display the message box and close the application if the user clicks "Yes".
                    if (await XtraMessageBox.ShowAsync(args) == DialogResult.Yes)
                        return null;
                }

                // Revoke the preceding run's manual-stop authorization before
                // any new batch can reconfigure or energize control hardware.
                RevokeManualStopExitAuthorizationBeforeEnergization();

                // 读取自学习圈数（比如从一个文本框；没有就用3）
                var learnCycles = _cfg.Test.LearnCycles;


                #region 重新给每个通道的执行次数赋值

                _epb.EpbTestCycle = _cfg.Test.CreateEpbStartPlan(
                    _cfg.Test.TestTarget,
                    12);

                #endregion 


                // int.TryParse(TxtLearnCycles.Text, out learnCycles) 也可以

                if (_batchCts != null)
                {
                    _batchCts.Dispose();
                    _batchCts = null;
                }

                _batchCts = new CancellationTokenSource();

                channels = selected.ToArray(); // 例如: {1,2,4,6} 或 {1..12}

                LogInfo($"准备启动卡钳：{string.Join(",", channels)}；自学习 {learnCycles} 圈。");
                try
                {
                    RunChainIdentity chainIdentity = null;
                    if (unattendedRecovery)
                    {
                        var checkpoint = UnattendedRunCheckpointStore.Load();
                        if (checkpoint == null ||
                            !Guid.TryParse(checkpoint.RunId, out var recoveredRunId) ||
                            recoveredRunId == Guid.Empty)
                            throw new InvalidOperationException("无人值守恢复缺少有效父RunId，拒绝创建无身份学习链。");
                        Guid.TryParse(checkpoint.RootRunId, out var rootRunId);
                        Guid.TryParse(checkpoint.ParentRunId, out var parentRunId);
                        chainIdentity = new RunChainIdentity(
                            Guid.NewGuid(),
                            rootRunId == Guid.Empty ? recoveredRunId : rootRunId,
                            parentRunId == Guid.Empty ? recoveredRunId : parentRunId,
                            Math.Max(0, checkpoint.RestartGeneration + 1),
                            Math.Max(1, checkpoint.RunEpoch + 1));
                    }
                    var startResult = await _epb.StartBatchSynchronizedWithResultAsync(
                        channels, // 批量要跑的通道
                        learnCycles, // 自学习圈数（按你期望）
                        chainIdentity,
                        _batchCts.Token // 取消令牌（Stop 按钮用）
                    );
                    completedStart = startResult;
                    startedChannels = startResult.StartedChannels;

                    if (startResult.CompletedDuringStartChannels.Length > 0)
                        LogInfo(
                            $"机械目标已在启动前或学习/资格阶段完成：" +
                            $"[{string.Join(",", startResult.CompletedDuringStartChannels)}]；" +
                            (startResult.StartedChannels.Length > 0
                                ? $"其余运行通道=[{string.Join(",", startResult.StartedChannels)}]。"
                                : "未再启动正式机械圈。"));
                    if (startResult.Faults.Length > 0)
                        LogInfo(
                            $"[安全] 批量部分启动：运行卡钳[{string.Join(",", startResult.StartedChannels)}]；" +
                            $"隔离卡钳[{string.Join(",", startResult.Faults.Select(x => x.Channel))}]。请查看上方通道报警及 AlarmSnapshots。");
                    else if (startResult.StartedChannels.Length > 0)
                        LogInfo("批量启动完成：学习阶段已对齐并错峰，上线后每圈对齐运行中…");
                }
                catch (OperationCanceledException)
                {
                    LogInfo("批量启动取消。");
                    if (unattendedRecovery) throw;
                    if (ShouldPublishRunStoppedForBatchCancellation(
                            unattendedRecovery,
                            Volatile.Read(ref _operatorStopRequested) != 0))
                    {
                        WatchdogRuntime.NotifyRunStopped(
                            new MTTFTest.Watchdog.Protocol.WatchdogStopSummary
                            {
                                Detail = "OperatorBatchStartCancelled"
                            });
                    }
                    else
                    {
                        LogInfo(
                            "非人工批次取消由当前恢复/StopAll事务收口，" +
                            "不发布RunStopped，避免撤销自动替换许可。");
                    }
                    var main = MdiParent as Main_Frm;
                    if (main == null)
                        throw new InvalidOperationException(
                            "批量启动取消时缺少 Main-owned Watchdog shutdown owner。");
                    // 启动取消只发布运行停止事实。Watchdog 会话由人工停止/应用关闭
                    // 的唯一事务收口，禁止在最终 StopSafetyResult 落盘前提前拆除。
                }
                catch (Exception ex)
                {
                    LogInfo($"批量启动失败：{ex.Message}");
                    if (unattendedRecovery)
                        throw new InvalidOperationException("无人值守恢复批量启动失败。", ex);
                    WatchdogRuntime.NotifyBatchStartFailed(
                        "BatchStartFailed:" + ex.GetBaseException().Message);
                    LogInfo("[启动保护] 已安全回滚；正式运行尚未提交时独立看门狗不会杀进程，" +
                            "请根据启动安全基线诊断处理后再次开始。");
                }

                #endregion

                // 点击“开始试验”按钮时 更新相关通道；
                foreach (var channel in startedChannels)
                {
                    var record = EnsureEpbRecord(channel);
                    record.MarkTestStarted(DateTime.Now);
                    RefreshCurrentEpbSummary(channel);
                }


                // UI 提示
                // RtbInfo?.AppendText($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  > 卡钳1测试已启动\n");
                return completedStart;
            }
            catch (Exception ex)
            {
                var channelText = string.Join(",", channels);
                logger?.Error($"启动卡钳{channelText}测试失败。", "启动", ex);
                LogInfo($"启动卡钳{channelText} 测试失败：{ex.Message}");
                if (unattendedRecovery) throw;
                ShowOperatorMessage($@"启动卡钳{channelText}测试失败：{ex.Message}", @"提示", MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return null;
            }
        }

        /// <summary>
        ///     停止试验按钮
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void BtnStop_Click(object sender, EventArgs e)
        {
            #region 旧的代码

            /*try
            {
                //_epb.StopChannel(1);
                //_epb.StopChannel(2);
                // _epb.StopChannel(4);
                // _epb.StopChannel(5);

                _epb.StopAll(); // 停止所有通道
                LogInfo("停止试验");
            }
            catch (Exception ex)
            {
                LogInfo("停止卡钳2测试失败");
            }*/

            #endregion

            if (Interlocked.CompareExchange(ref _stopUiGuard, 1, 0) != 0)
            {
                LogInfo("停止试验正在处理中，请勿重复点击。");
                return;
            }

            _manualStopExitReceipt.Revoke();
            Interlocked.Exchange(ref _operatorStopRequested, 1);
            var stopCommandId = Guid.NewGuid().ToString("N");
            _stopSessionReceipt.Begin(stopCommandId);
            var stopWatchdogContext = WatchdogRuntime.CaptureTransportSnapshot()?.Context;
            StopSafetyResult completedSafety = null;
            LogInfo($"已接收停止试验命令，正在执行安全断能与数据收口。CommandId={stopCommandId}");
            PostSafetyStatus("停止命令已接收，正在安全断能与收口…", false);
            BtnStop.Enabled = false;
            BtnStop.Cursor = Cursors.WaitCursor;
            BtnStartTest.Enabled = false;
            BtnStartTest.Cursor = Cursors.WaitCursor;
            try
            {
                // 物理断电必须成为停止按钮后的第一个可能阻塞操作。恢复检查点的
                // WriteThrough/Flush、Watchdog 管道与批次取消回调全部移到后台并行执行。
                var stopTask = _epb.StopAllAsync(
                    new StopContext
                    {
                        Source = StopSource.ManualUi,
                        Reason = "操作员点击停止试验",
                        Initiator = nameof(BtnStop_Click),
                        CorrelationId = stopCommandId,
                        RequestedUtc = DateTime.UtcNow
                    });

                // StopAll 已取得事务身份后立即建立不可逆 Close Fence；即使后续
                // 管道回执、detach 或 UI 收口失败，同一 Watchdog 会话也不能再拉起主程序。
                var stopProgress = _epb.CaptureStopSafetyProgress();
                var closeFence = WatchdogRuntime.BeginSessionCloseExact(
                    stopWatchdogContext,
                    "ManualStopIntent",
                    stopProgress?.TransactionId ?? Guid.Empty,
                    stopProgress?.RunId ?? Guid.Empty,
                    stopProgress?.RunEpoch ?? 0,
                    stopProgress?.Generation ?? 0);
                if (stopWatchdogContext != null && !closeFence.IsIrreversible)
                    logger?.Warn(
                        $"人工停止 Close Fence 未形成双写耐久回执，将保留关闭事务重试；" +
                        $"Mark={closeFence.MarkOutcome}; Error={closeFence.Error}",
                        "Watchdog");

                // StopAll 已经开始后再同步 UI 开关；即使控件事件处理异常，也不会挡住断能。
                for (var chIndex = 0; chIndex < 12; chIndex++)
                    EpbGroup[chIndex].CtrlRunning.Checked = false;

                var watchdogNotificationTask = System.Threading.Tasks.Task.Run(() =>
                {
                    try { WatchdogRuntime.NotifyManualStop("操作员点击停止试验"); }
                    catch (Exception ex)
                    {
                        logger?.Warn(
                            $"人工停止已进入安全断能，但 Watchdog 通知失败：{ex.Message}",
                            "Watchdog");
                    }
                });
                QueueManualStopCheckpointCleanup(stopCommandId);
                QueueBatchCancellation(stopCommandId);

                // 让 ManualStopIntent 优先于完成消息抵达，但绝不让外部 I/O 挡住安全停机。
                await System.Threading.Tasks.Task.WhenAny(
                    watchdogNotificationTask,
                    System.Threading.Tasks.Task.Delay(250));
                var stopUiStarted = DateTime.UtcNow;
                while (!stopTask.IsCompleted)
                {
                    await System.Threading.Tasks.Task.WhenAny(
                        stopTask,
                        System.Threading.Tasks.Task.Delay(1000));
                    if (stopTask.IsCompleted) break;
                    var elapsed = (DateTime.UtcNow - stopUiStarted).TotalSeconds;
                    var progress = _epb.CaptureStopSafetyProgress();
                    if (elapsed < 5)
                        LogInfo(
                            $"正在安全停止：{progress.Stage}，{progress.Detail} " +
                            $"({elapsed:F0}/5秒)");
                    else
                        LogInfo(
                            $"正在安全收尾：阶段={progress.Stage}，{progress.Detail}；" +
                            $"已等待{elapsed:F0}秒。人工停止后不自动续跑。");
                }
                var safety = await stopTask;
                completedSafety = safety;
                WatchdogRuntime.AdvanceSessionCloseSafety(stopWatchdogContext, safety);
                if (!_manualStopExitReceipt.Publish(safety, stopCommandId))
                    logger?.Warn(
                        $"人工停止结果不满足关闭复用条件，将在关闭时重新执行安全停机。" +
                        $"CommandId={stopCommandId}; RunId={safety.RunId:N}; " +
                        $"CanClose={safety.CanCloseApplication}",
                        "EPB");
                if (!watchdogNotificationTask.IsCompleted)
                    await System.Threading.Tasks.Task.WhenAny(
                        watchdogNotificationTask,
                        System.Threading.Tasks.Task.Delay(750));
                if (safety.RequiresProcessRestart || safety.TimedOut)
                {
                    await RequestManualStopSafetyHandoffAsync(stopWatchdogContext, safety);
                    _stopSessionReceipt.Complete(new StopSessionReceipt
                    {
                        CommandId = stopCommandId,
                        SessionId = stopWatchdogContext?.SessionId ?? string.Empty,
                        SessionGeneration = stopWatchdogContext?.SessionGeneration ?? 0,
                        SessionLease = stopWatchdogContext?.SessionLease ?? 0,
                        StopSafety = safety,
                        ManualExitIntent = _manualStopExitReceipt.CaptureIntent(),
                        CompletedUtc = DateTime.UtcNow,
                        Error = ProcessRestartUiPolicy.GetOperatorMessage(safety.TimedOut)
                    });
                    LogInfo(ProcessRestartUiPolicy.GetOperatorMessage(safety.TimedOut));
                    BtnStop.Enabled = false;
                    BtnStartTest.Enabled = false;
                }
                else
                {
                    LogInfo($"设备与数据侧停止完成，正在释放Watchdog精确会话。CommandId={stopCommandId}");
                    PostSafetyStatus("设备输出已OFF，正在释放Watchdog会话…", false);
                    if (!watchdogNotificationTask.IsCompleted)
                        await watchdogNotificationTask.ConfigureAwait(true);
                    var combined = await CompleteManualStopSessionAsync(
                            safety,
                            stopCommandId,
                            stopWatchdogContext)
                        .ConfigureAwait(true);
                    _stopSessionReceipt.Complete(combined);
                    if (!combined.CanRestart)
                        throw new InvalidOperationException(
                            "人工停止组合终态未完成：" + combined.Error);
                    Interlocked.Exchange(ref _monitorEnergizationAttempted, 0);
                    SetMonitorLifecycle(EpbMonitorLifecycle.Idle);
                    LogInfo($"停止试验组合终态完成；可以关闭软件或重新开始。" +
                            $"CommandId={stopCommandId}; Session={combined.SessionId}; " +
                            $"Lease={combined.SessionLease}");
                    PostSafetyStatus("停止收口已完成，可以关闭或重新开始。", false);
                }
            }
            catch (Exception ex)
            {
                _stopSessionReceipt.Complete(new StopSessionReceipt
                {
                    CommandId = stopCommandId,
                    SessionId = stopWatchdogContext?.SessionId ?? string.Empty,
                    SessionGeneration = stopWatchdogContext?.SessionGeneration ?? 0,
                    SessionLease = stopWatchdogContext?.SessionLease ?? 0,
                    StopSafety = completedSafety,
                    ManualExitIntent = _manualStopExitReceipt.CaptureIntent(),
                    CompletedUtc = DateTime.UtcNow,
                    Error = ex.GetBaseException().Message
                });
                // 操作员的停止意图已经成立；关闭或下一次启动会再次执行幂等清场。
                LogInfo($"设备已 OFF，监督会话关闭待重试；自动恢复保持撤权：{ex.Message}");
                BtnStartTest.Enabled = false;
            }
            finally
            {
                Interlocked.Exchange(ref _stopUiGuard, 0);
                if (!IsDisposed && BtnStop != null)
                {
                    BtnStop.Enabled = !(_epb?.RequiresProcessRestart ?? false);
                    BtnStop.Cursor = Cursors.Hand;
                }
                if (!IsDisposed && BtnStartTest != null)
                {
                    ApplyBatchPauseState(_epb?.CurrentBatchPauseState ?? BatchPauseState.Idle);
                    if (_epb?.RequiresProcessRestart == true)
                        BtnStartTest.Enabled = false;
                }
            }
        }

        private void QueueManualStopCheckpointCleanup(string stopCommandId)
        {
            _pendingGracefulPauseCheckpoint = null;
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await UnattendedRecoveryCoordinator
                        .DisarmAsync("ManualStopIntent")
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger?.Warn(
                        $"人工停止检查点撤权失败，安全断能不受影响。CommandId={stopCommandId}; " +
                        $"Error={ex.Message}",
                        "Recovery");
                }

                try
                {
                    UnattendedRunCheckpointStore.ClearGracefulPause("ManualStopRequested");
                }
                catch (Exception ex)
                {
                    logger?.Warn(
                        $"人工停止清理正常暂停检查点失败。CommandId={stopCommandId}; Error={ex.Message}",
                        "Recovery");
                }
            });
        }

        private void QueueBatchCancellation(string stopCommandId)
        {
            var batchCancellation = _batchCts;
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try { batchCancellation?.Cancel(); }
                catch (ObjectDisposedException) { }
                catch (Exception ex)
                {
                    logger?.Warn(
                        $"人工停止的批次取消回调异常，StopAll 已独立执行。" +
                        $"CommandId={stopCommandId}; Error={ex.Message}",
                        "EPB");
                }
            });
        }

        private async System.Threading.Tasks.Task<StopSessionReceipt>
            CompleteManualStopSessionAsync(
            StopSafetyResult safety,
            string stopCommandId,
            RuntimeTransportSessionContext capturedContext)
        {
            var summary = WinFormsWatchdogStopHandlerCore.ToWatchdogStopSummary(safety);
            await System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    if (safety.PhysicalSafetyConfirmed)
                        WatchdogRuntime.NotifyPhysicalStopConfirmed(
                            "ManualStopPhysicalSafetyConfirmed");
                    WatchdogRuntime.NotifyStopCompleted(summary, "ManualStopCompleted");
                }
                catch (Exception ex)
                {
                    logger?.Warn(
                        $"人工停止已完成，但 Watchdog 完成通知失败。" +
                        $"CommandId={stopCommandId}; Error={ex.Message}",
                        "Watchdog");
                }
            }).ConfigureAwait(true);

            var main = MdiParent as Main_Frm;
            if (main == null)
            {
                return new StopSessionReceipt
                {
                    CommandId = stopCommandId,
                    SessionId = capturedContext?.SessionId ?? string.Empty,
                    SessionGeneration = capturedContext?.SessionGeneration ?? 0,
                    SessionLease = capturedContext?.SessionLease ?? 0,
                    StopSafety = safety,
                    ManualExitIntent = _manualStopExitReceipt.CaptureIntent(),
                    CompletedUtc = DateTime.UtcNow,
                    Error = "缺少Main-owned Watchdog shutdown owner。"
                };
            }

            var shutdown = await main.ShutdownWatchdogSessionAndReleaseUiAsync(
                    "ManualStopCompleted")
                .ConfigureAwait(true);
            var receipt = new StopSessionReceipt
            {
                CommandId = stopCommandId,
                SessionId = capturedContext?.SessionId ?? shutdown?.SessionId ?? string.Empty,
                SessionGeneration = capturedContext?.SessionGeneration ??
                                    shutdown?.SessionGeneration ?? 0,
                SessionLease = capturedContext?.SessionLease ?? shutdown?.SessionLease ?? 0,
                StopSafety = safety,
                ManualExitIntent = _manualStopExitReceipt.CaptureIntent(),
                WatchdogShutdown = shutdown,
                UiResourcesReleased = shutdown?.IsTerminal == true,
                CompletedUtc = DateTime.UtcNow
            };
            if (!receipt.CanRestart)
            {
                var workers = shutdown?.EngineReceipt?.WorkerTermination;
                receipt.Error = shutdown == null
                    ? "Watchdog shutdown未返回回执。"
                    : $"Watchdog终态={shutdown.IsTerminal}; " +
                      $"Reader={workers?.ReaderTerminal}; Send={workers?.SendTerminal}; " +
                      $"Heartbeat={workers?.HeartbeatTerminal}; Monitor={workers?.MonitorTerminal}; " +
                      $"Reconnect={workers?.ReconnectTerminal}; Connect={workers?.ConnectTerminal}; " +
                      $"Launch={workers?.LaunchTerminal}; Retained={shutdown.Retained}。";
            }
            return receipt;
        }

        #region 3) 窗体关闭：一次性解绑/停止/释放

        /// <summary>
        ///     窗体关闭：标记关闭状态，解绑事件，停止 UI 定时器与采集，避免回调打到已销毁的 UI。
        /// </summary>
        private void FrmEpbMainMonitor_FormClosing(object sender, FormClosingEventArgs e)
        {
            // 第一次关闭只负责取消框架本轮关闭并启动一个可等待的收尾任务。
            // 最后一轮关闭必须等所有异步停机、写盘和资源释放均已完成后才放行。
            if (Volatile.Read(ref _closingReentry) == 3) return;

            e.Cancel = true;
            if (Interlocked.CompareExchange(ref _closingReentry, 1, 0) != 0) return;
            SetMonitorLifecycle(EpbMonitorLifecycle.Stopping);
            _isClosing = true;
            ScheduleCloseOverlay();
            BeginMonitorCloseSequence();
        }

        private async void BeginMonitorCloseSequence()
        {
            try
            {
                var completed = await PrepareAndFinalizeMonitorCloseAsync(
                    closeAfterPreparation: true);
                if (!completed)
                {
                    HideCloseOverlay();
                    _isClosing = false;
                    Interlocked.Exchange(ref _formClosedFlag, 0);
                    Interlocked.Exchange(ref _closingReentry, 0);
                    SetMonitorLifecycle(
                        _epb?.IsBatchSessionActive == true
                            ? EpbMonitorLifecycle.Running
                            : EpbMonitorLifecycle.Idle);
                }
            }
            catch (Exception ex)
            {
                logger?.Error("实时监控窗口关闭收尾异常，已转入诊断并允许重试：" + ex, "EPB");
                HideCloseOverlay();
                _isClosing = false;
                Interlocked.Exchange(ref _formClosedFlag, 0);
                Interlocked.Exchange(ref _closingReentry, 0);
                SetMonitorLifecycle(
                    _epb?.IsBatchSessionActive == true
                        ? EpbMonitorLifecycle.Running
                        : EpbMonitorLifecycle.Idle);
            }
        }

        internal async System.Threading.Tasks.Task<bool> PrepareForMainApplicationExitAsync()
        {
            if (Volatile.Read(ref _closingReentry) == 3) return true;
            Interlocked.CompareExchange(ref _closingReentry, 1, 0);
            _isClosing = true;
            ScheduleCloseOverlay();
            return await PrepareAndFinalizeMonitorCloseAsync(
                    closeAfterPreparation: false)
                .ConfigureAwait(true);
        }

        internal void CloseAfterMainExitAuthorized()
        {
            Interlocked.Exchange(ref _closingReentry, 3);
            if (!IsDisposed && !Disposing) Close();
        }

        internal static StopSource ResolveMonitorCloseStopSource(bool watchdogOwnsExit)
        {
            return watchdogOwnsExit
                ? StopSource.SystemFault
                : StopSource.ApplicationClosing;
        }

        private async System.Threading.Tasks.Task<bool> PrepareAndFinalizeMonitorCloseAsync(
            bool closeAfterPreparation)
        {
                var idleFastClose = CanUseIdleFastClose();
                SetMonitorLifecycle(EpbMonitorLifecycle.Stopping);
                var closeContext = WatchdogRuntime.CaptureTransportSnapshot()?.Context;
                _preparedCloseContext = _preparedCloseContext ?? closeContext;
                var watchdogOwnsExit = Volatile.Read(ref _watchdogTakeoverExit) != 0;
                var wasExplicitlyStopped = Volatile.Read(ref _operatorStopRequested) != 0 ||
                                           watchdogOwnsExit ||
                                           !(_epb?.IsBatchSessionActive ?? false);
                StopSafetyResult safety;
                var stopSessionTask = idleFastClose ? null : _stopSessionReceipt.CaptureTask();
                StopSessionReceipt combinedStop = null;
                if (stopSessionTask != null)
                {
                    PostSafetyStatus("关闭请求已加入人工停止收口，正在等待组合终态…", false);
                    var completed = await System.Threading.Tasks.Task.WhenAny(
                            stopSessionTask,
                            System.Threading.Tasks.Task.Delay(15000))
                        .ConfigureAwait(true);
                    if (!ReferenceEquals(completed, stopSessionTask))
                    {
                        LogInfo("人工停止超过15秒，继续加入同一 StopAll；完成数据边界后将自动安全交接。");
                        ShowCloseOverlay("停止事务仍在收口，正在等待数据安全边界…");
                    }
                    combinedStop = await stopSessionTask.ConfigureAwait(true);
                }

                var reusableManualStop = combinedStop?.StopSafety?.CanCloseApplication == true
                    ? combinedStop.StopSafety
                    : _manualStopExitReceipt.TryCapture(
                        _epb?.IsBatchSessionActive ?? false);
                if (idleFastClose)
                {
                    safety = new StopSafetyResult
                    {
                        Source = StopSource.ApplicationClosing,
                        CorrelationId = Guid.NewGuid().ToString("N"),
                        MotorOffCommandSucceeded = true,
                        PowerOffConfirmed = false,
                        PowerDisposition = PowerShutdownDisposition.NotRequiredNoActiveTrial,
                        PersistenceBoundaryConfirmed = true,
                        RawStorageFlushed = true,
                        LogicalQuiescenceConfirmed = true,
                        StartedUtc = DateTime.UtcNow,
                        CompletedUtc = DateTime.UtcNow
                    };
                    LogInfo("监控窗未开始试验，执行空闲快速关闭；不连接、不查询程控电源。");
                }
                else if (reusableManualStop != null)
                {
                    safety = reusableManualStop;
                    LogInfo(
                        $"关闭复用人工停止组合任务中的设备安全凭证，不重复执行StopAll。" +
                        $"CorrelationId={safety.CorrelationId}; RunId={safety.RunId:N}");
                }
                else
                {
                    try
                    {
                        var closeStopTask = _epb.StopAllAsync(
                                new StopContext
                                {
                                    // Watchdog takeover already persisted the restart handoff.
                                    // Keep this final close in the SystemFault transaction so
                                    // ApplicationClosing cannot revoke the armed checkpoint
                                    // between WatchdogTakeoverExit and the replacement process.
                                    Source = ResolveMonitorCloseStopSource(watchdogOwnsExit),
                                    Reason = watchdogOwnsExit
                                        ? "Watchdog 接管后的主窗体关闭收口"
                                        : "主窗体关闭",
                                    Initiator = nameof(FrmEpbMainMonitor_FormClosing),
                                    CorrelationId = Guid.NewGuid().ToString("N"),
                                    RequestedUtc = DateTime.UtcNow
                                },
                                CancellationToken.None);
                        var closeProgress = _epb.CaptureStopSafetyProgress();
                        var closeFence = WatchdogRuntime.BeginSessionCloseExact(
                            closeContext,
                            "ApplicationClosing",
                            closeProgress?.TransactionId ?? Guid.Empty,
                            closeProgress?.RunId ?? Guid.Empty,
                            closeProgress?.RunEpoch ?? 0,
                            closeProgress?.Generation ?? 0);
                        if (closeContext != null && !closeFence.IsIrreversible)
                            logger?.Warn(
                                "监控关闭未能建立耐久Close Fence：" + closeFence.Error,
                                "Watchdog");
                        safety = await closeStopTask.ConfigureAwait(true);
                        WatchdogRuntime.AdvanceSessionCloseSafety(closeContext, safety);
                    }
                    catch (Exception ex)
                    {
                        safety = new StopSafetyResult
                        {
                            MotorError = ex.Message,
                            PowerError = ex.Message
                        };
                    }
                }

                if (!safety.CanReleaseAcquisition)
                {
                    var items = new List<string>();
                    if (!safety.MotorOffCommandSucceeded)
                        items.Add("电机DO关闭未确认：" + (safety.MotorError ?? "无详细信息"));
                    if (!safety.PowerOffConfirmed)
                        items.Add("程控电源关闭回读未确认：" + (safety.PowerError ?? "无详细信息"));
                    if (wasExplicitlyStopped)
                        LogInfo("[关闭警告] 已明确停止试验，电机/电源确认异常不再阻止退出：" +
                                string.Join("; ", items));
                    else LogInfo("[安全关闭] 将转入无界面安全交接：" + string.Join("; ", items));
                }

                if (!safety.CanCloseApplication)
                {
                    var persistenceMessage =
                        "电机和程控电源已经安全关闭，但最后一批 Raw/SQLite 数据尚未完成落盘。\r\n" +
                        (string.IsNullOrWhiteSpace(safety.PersistenceError)
                            ? "写盘恢复链仍在后台重试。"
                            : safety.PersistenceError) +
                        "\r\n\r\n窗口保持打开，禁止结束进程。请恢复磁盘/网络存储后再次关闭，" +
                        "避免丢失最后圈或报警证据。后续重试只更新日志，不再重复弹框。";
                    LogInfo("[关闭等待] 数据耐久边界仍未确认，保持窗口与进程并自动重试：" +
                            (safety.PersistenceError ?? "无详细信息"));
                    ShowCloseOverlay("数据保存尚未完成，修复存储后请再次关闭…");
                    return false;
                }

                if (!idleFastClose && !safety.PressureSafeConfirmed)
                    LogInfo("[安全警告] 停机处置已满足退出条件；压力安全证据因采样陈旧/不可用未确认，按现场策略继续退出。" +
                            (string.IsNullOrWhiteSpace(safety.PressureError) ? string.Empty : " " + safety.PressureError));

            // Main_Frm's shutdown boundary sends ApplicationClosing as part
            // of ShutdownRuntimeWithReceipt.  Do not publish a second
            // protocol path here; it used to race the retention owner.

            // 只执行一次。若上一轮已经完成资源释放、只是在旧 Watchdog 授权门上
            // 返回过 false，本轮必须真正再次发起 Close，不能只返回 true 后让窗口复活。
            if (Interlocked.Exchange(ref _formClosedFlag, 1) != 0)
            {
                if (closeAfterPreparation)
                {
                    Interlocked.Exchange(ref _closingReentry, 3);
                    if (!IsDisposed && !Disposing && IsHandleCreated)
                        BeginInvoke((Action)Close);
                }
                return true;
            }
            _isClosing = true;

            // StopAll 已经完成物理安全和持久化边界；在窗体直接释放 DAQ/DO/液压
            // 对象之前，必须先让控制层监督的恢复、证据和设备任务完成异常观察与退场。
            // 否则迟到任务仍可能访问下面即将 Dispose 的采集器/写盘器。
            var controlHardwareReleased = false;
            try
            {
                controlHardwareReleased = ReleaseOwnedControlHardwareOnce();
            }
            catch (Exception ex)
            {
                logger?.Warn("关闭窗口时统一硬件释放失败：" + ex.Message, "EPB");
            }

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
                // ignored
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
                        // ignored
                    }

                    if (!controlHardwareReleased)
                    {
                        try
                        {
                            twoDeviceAiAcquirer.Stop();
                        }
                        catch
                        {
                            // ignored
                        }

                        try
                        {
                            twoDeviceAiAcquirer.Dispose();
                        }
                        catch
                        {
                            // ignored
                        }
                    }

                    twoDeviceAiAcquirer = null;
                }
            }
            catch
            {
                // ignored
            }

            // 3.5) 释放报警子系统（串口）
            try
            {
                try
                {
                    if (_alarmPanelTestForm != null && !_alarmPanelTestForm.IsDisposed)
                        _alarmPanelTestForm.Close();
                }
                catch
                {
                    // ignored
                }
                finally
                {
                    _alarmPanelTestForm = null;
                }

                _alarmManager?.Dispose();
                _alarmManager = null;
            }
            catch
            {
                // ignored
            }

            // 4) 停止并释放落盘定时器，并做最后一次 Flush on 2025/09/09
            try
            {
                // 停止定时器
                void StopTimer(ref Timer t)
                {
                    try
                    {
                        t?.Change(Timeout.Infinite, Timeout.Infinite);
                        t?.Dispose();
                        t = null;
                    }
                    catch
                    {
                        // ignored
                    }
                }

                StopTimer(ref _daqRawTimerDev1);
                StopTimer(ref _daqStatTimerDev1);
                StopTimer(ref _daqRawTimerDev2);
                StopTimer(ref _daqStatTimerDev2);

                // 直接等待异步Flush完成后再释放写盘器。事件处理器本身是async，
                // 不会阻塞消息泵，也不会留下“窗口已关闭但Flush仍访问已释放资源”的裸任务。
                try
                {
                    if (_daqDev1 != null)
                    {
                        await _daqDev1.FlushRawToDiskAsync();
                        await _daqDev1.FlushStatToDiskAsync();
                    }

                    if (_daqDev2 != null)
                    {
                        await _daqDev2.FlushRawToDiskAsync();
                        await _daqDev2.FlushStatToDiskAsync();
                    }
                }
                catch (Exception ex)
                {
                    logger?.Error("关闭窗口时DAQ最终Flush失败：" + ex.Message, "DAQ", ex);
                }
            }
            catch
            {
                /* 关闭阶段忽略单次失败 */
            }


            // // 测试
            // _diskWriter.ExportFreeRunBySamples(1, 100000,
            //     Path.Combine(Environment.CurrentDirectory, @$"DataStore\EPB1-{DateTime.Now:yyyy_MM_dd-HH_mm_ss}.csv"));

            // 结束自动定时保存器
            try
            {
                // —— 1) 停止自动保存定时器 —— //
                if (_autoSaveTimer != null)
                {
                    _autoSaveTimer.Stop();
                    _autoSaveTimer.Tick -= AutoSaveTimer_Tick; // 清理事件
                }

                // —— 2) 最终保存一次（兜底）—— //
                lock (_epbRecordsLock)
                {
                    FlushUiEpbRecordsToConfig();
                }

                SaveEpbRecordsToTestConfigSafe();
            }
            catch (Exception ex)
            {
                logger?.Warn("关闭窗口时保存 EPB 记录失败：" + ex.Message, "EPB");
            }


            try
            {
                // 5) 关闭并释放落盘器（非常关键）：
                //    EpbDiskWriter 内部持有 MemoryMappedFile 和 SQLite 连接，如果不 Dispose，
                //    对应的 EPB*_sliding.dat 文件会一直被当前进程独占，导致下次 new 时打不开。
                var writer = _diskWriter;
                _diskWriter = null; // 提前置空，防止后续误用

                if (writer != null)
                {
                    // 如果你确实需要在窗体关闭时导出一份 Free-Run 数据，
                    // 可以保留下面这段导出逻辑；不需要的话可以整体删掉。
                    try
                    {
                        // var exportPath = Path.Combine(
                        //     Environment.CurrentDirectory,
                        //     $@"DataStore\EPB1-{DateTime.Now:yyyy_MM_dd-HH_mm_ss}.csv");
                        //
                        // writer.ExportFreeRunBySamples(1, 100000, exportPath);
                    }
                    catch
                    {
                        // 关闭阶段导出失败可以忽略，避免影响主流程
                    }

                    // 真正释放文件句柄和内存映射
                    writer.Dispose();
                }
            }
            catch
            {
                /* 关闭阶段忽略单次失败 */
            }

            #region 解绑ChannelCycleCompleted事件

            try
            {
                if (_epb != null)
                {
                    _epb.ChannelCycleCompleted -= OnEpbChannelCycleCompleted;
                    _epb.ChannelMechanicalCycleCompleted -= OnEpbMechanicalCycleCompleted;
                }
            }
            catch
            {
                // 忽略异常
            }

            #endregion

            try
            {
                if (_uiInfoFlushTimer != null)
                {
                    _uiInfoFlushTimer.Stop();
                    _uiInfoFlushTimer.Tick -= UiInfoFlushTimer_Tick;
                    _uiInfoFlushTimer.Dispose();
                    _uiInfoFlushTimer = null;
                }

                _uiInfoLogStore?.Dispose();
                _uiInfoLogStore = null;
            }
            catch
            {
                /* UI log flush failure must not block closing. */
            }

            // 所有异步收尾和资源释放均已完成。下一轮 FormClosing 由状态 3 放行，
            // 不再在事件处理器内部调用 base.OnFormClosing，避免递归触发。
            _preparedCloseSafety = safety;
            _preparedCloseContext = _preparedCloseContext ??
                                    WatchdogRuntime.CaptureTransportSnapshot()?.Context;
            if (closeAfterPreparation)
            {
                var closeReceipt = await AuthorizeApplicationExitAfterPreparationAsync()
                    .ConfigureAwait(true);
                if (closeReceipt?.CanExit != true)
                {
                    LogInfo("关闭授权尚未建立：需要完整 Watchdog 终态或已接受的耐久安全交接。");
                    HideCloseOverlay();
                    _isClosing = false;
                    Interlocked.Exchange(ref _closingReentry, 0);
                    return false;
                }
                (MdiParent as Main_Frm)?.CacheApplicationCloseReceipt(closeReceipt);
                Interlocked.Exchange(ref _closingReentry, 3);
                if (!IsDisposed && !Disposing && IsHandleCreated)
                    BeginInvoke((Action)Close);
            }
            return true;
        }

        internal async System.Threading.Tasks.Task<ApplicationCloseReceipt>
            AuthorizeApplicationExitAfterPreparationAsync()
        {
            if (_applicationCloseReceipt?.CanExit == true)
                return _applicationCloseReceipt;
            var safety = _preparedCloseSafety;
            var context = _preparedCloseContext;
            if (safety == null || !safety.PersistenceBoundaryConfirmed ||
                _hardwareReleaseOwner.ReleaseCount <= 0)
                return null;

            var main = MdiParent as Main_Frm;
            var snapshot = WatchdogRuntime.CaptureTransportSnapshot();
            if (CanCloseMonitorWithoutWatchdogBinding(
                    main?.WatchdogUiHasResources == true,
                    WatchdogRuntime.IsExactAttachedSnapshot(snapshot)))
            {
                // Watchdog 启动/握手在 UI 绑定前失败时，不存在需要监控窗继续承载的
                // callback target。设备、持久化和本窗资源均已闭合后允许直接关闭。
                return _applicationCloseReceipt = new ApplicationCloseReceipt
                {
                    SessionId = context?.SessionId ?? string.Empty,
                    SessionGeneration = context?.SessionGeneration ?? 0,
                    SessionLease = context?.SessionLease ?? 0,
                    HardwareResourcesReleased = true,
                    WatchdogTerminal = true,
                    Disposition = ApplicationExitDisposition.Graceful,
                    CompletedUtc = DateTime.UtcNow,
                    DiagnosticDetail = "NoWatchdogUiBinding"
                };
            }

            RuntimeShutdownReceipt shutdown = null;
            if (EpbMonitorClosePolicy.CanShutdownWatchdogGracefully(safety))
            {
                if (main != null)
                    shutdown = await (safety.PowerDisposition ==
                                      PowerShutdownDisposition.NotRequiredNoActiveTrial
                            ? main.ShutdownIdleWatchdogSessionAndReleaseUiAsync(
                                "IdleMonitorCloseCompleted")
                            : main.ShutdownWatchdogSessionAndReleaseUiAsync(
                                "MonitorCloseCompleted"))
                        .ConfigureAwait(true);
            }
            if (shutdown?.IsCloseAuthorized == true)
            {
                return _applicationCloseReceipt = new ApplicationCloseReceipt
                {
                    SessionId = context?.SessionId ?? shutdown.SessionId ?? string.Empty,
                    SessionGeneration = context?.SessionGeneration ?? shutdown.SessionGeneration,
                    SessionLease = context?.SessionLease ?? shutdown.SessionLease,
                    HardwareResourcesReleased = true,
                    WatchdogTerminal = true,
                    Disposition = ApplicationExitDisposition.Graceful,
                    CompletedUtc = DateTime.UtcNow
                };
            }

            var handoff = WatchdogRuntime.RequestSafetyHandoff(context, safety, true);
            if (handoff == null) return null;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                WatchdogSafetyHandoffReceipt latest;
                if (WatchdogSafetyHandoffReceiptStore.TryRead(
                        context.JournalDirectory, context.SessionId, out latest) &&
                    string.Equals(latest.HandoffId, handoff.HandoffId, StringComparison.Ordinal) &&
                    latest.State >= WatchdogSafetyHandoffState.Accepted &&
                    latest.CanExitApplication)
                {
                    return _applicationCloseReceipt = new ApplicationCloseReceipt
                    {
                        SessionId = context.SessionId,
                        SessionGeneration = context.SessionGeneration,
                        SessionLease = context.SessionLease,
                        HardwareResourcesReleased = true,
                        SafetyHandoffAccepted = true,
                        Disposition = ApplicationExitDisposition.SafetyHandoff,
                        CompletedUtc = DateTime.UtcNow
                    };
                }
                await System.Threading.Tasks.Task.Delay(100).ConfigureAwait(true);
            }
            return null;
        }

        internal static bool CanCloseMonitorWithoutWatchdogBinding(
            bool watchdogUiHasResources,
            bool exactAttached)
        {
            return !watchdogUiHasResources && !exactAttached;
        }

        internal ApplicationCloseReceipt CaptureApplicationCloseReceipt() =>
            _applicationCloseReceipt;

        #endregion


        // 把全选中项做置零或清零
        private void ZeroOrClearSelected(bool isZero)
        {
            if (twoDeviceAiAcquirer == null)
            {
                XtraMessageBox.Show("采集器未初始化。");
                return;
            }

            // 取被勾选的全局索引（1..15）
            var picked = _checkByGlobal
                .Where(kv => kv.Value?.Checked == true)
                .Select(kv => kv.Key)
                .OrderBy(x => x)
                .ToList();

            if (picked.Count == 0)
            {
                XtraMessageBox.Show("请先勾选要操作的通道。");
                return;
            }

            foreach (var idx in picked)
                if (idx >= 0 && idx <= 11)
                {
                    // EPB 电流通道
                    if (isZero) twoDeviceAiAcquirer.ZeroEpbChannel(idx + 1);
                    else twoDeviceAiAcquirer.ClearZeroEpbChannel(idx + 1);
                }
                else
                {
                    // P1 / P2 / F -> 参数名
                    var paramName = idx switch
                    {
                        12 => "Pressure_1",
                        13 => "Pressure_2",
                        14 => "Force",
                        _ => null
                    };
                    if (string.IsNullOrEmpty(paramName)) continue;

                    if (isZero) twoDeviceAiAcquirer.ZeroByParamName(paramName);
                    else twoDeviceAiAcquirer.ClearZeroByParamName(paramName);
                }

            // 可选：简单提示
            var label = isZero ? "置零" : "清除置零";
            var list = string.Join(", ", picked.Select(IndexToDisplayName));
            // 你也可以换成状态栏提示
            Console.WriteLine($"{label}完成：{list}");
        }

        // 把全局索引转成界面显示名（1..12, P1, P2, F）
        private static string IndexToDisplayName(int idx)
        {
            return idx switch
            {
                >= 0 and <= 11 => $"#{idx + 1}",
                12 => "P1",
                13 => "P2",
                14 => "F",
                _ => $"#{idx}"
            };
        }

        private void ZeroButton_Click(object sender, EventArgs e)
        {
            ZeroOrClearSelected(true);
        }

        private void ClearZeroButton_Click(object sender, EventArgs e)
        {
            ZeroOrClearSelected(false);
        }

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

        #region EPB 概览区域 相关方法

        /// <summary>
        /// 初始化 EPB 概览区域：
        /// 1. 用 _uiEpbRecords 填充下拉框；
        /// 2. 默认选中第一个通道并刷新 Led / 进度条 / 状态灯。
        /// </summary>
        private void InitEpbSummaryPanel()
        {
            // 保护：没有记录就直接返回
            if (_uiEpbRecords == null || _uiEpbRecords.Count == 0)
                return;

            // 清空原有项目
            comboBoxEditCurrentRecord.Properties.Items.Clear();

            // 按通道号排序后填入下拉框
            foreach (var rec in _uiEpbRecords.OrderBy(r => r.Id))
            {
                // 显示文本你可以自己定，这里用 EPB-1、EPB-2 ...
                string displayText = $"EPB-{rec.Id}";
                comboBoxEditCurrentRecord.Properties.Items.Add(displayText);
            }

            // 防止重复绑定事件
            comboBoxEditCurrentRecord.SelectedIndexChanged -= comboBoxEditCurrentRecord_SelectedIndexChanged;

            // 如果有项目，默认选中第一项
            if (comboBoxEditCurrentRecord.Properties.Items.Count > 0)
            {
                var initialChannel = EpbProjectPolicies.FindInitialSummaryChannel(_uiEpbRecords);
                comboBoxEditCurrentRecord.SelectedIndex = Math.Max(0, initialChannel - 1);
            }

            // 重新绑定事件
            comboBoxEditCurrentRecord.SelectedIndexChanged += comboBoxEditCurrentRecord_SelectedIndexChanged;

            // 根据默认选中的项刷新一遍显示
            RefreshSummaryByComboSelection();
        }

        /// <summary>
        /// 概览区域下拉框选中变化：
        /// 解析选中的文本得到 EPB 通道号，然后刷新显示。
        /// </summary>
        private void comboBoxEditCurrentRecord_SelectedIndexChanged(object sender, EventArgs e)
        {
            RefreshSummaryByComboSelection();
        }

        /// <summary>
        /// 根据下拉框当前选项，解析出 EPB 通道号，并调用 <see>
        ///     <cref>UpdateEpbSummaryPanel</cref>
        /// </see>
        /// 刷新显示。
        /// </summary>
        private void RefreshSummaryByComboSelection()
        {
            // —— 1) 基本安全检查 —— //
            if (comboBoxEditCurrentRecord == null ||
                comboBoxEditCurrentRecord.Properties == null ||
                comboBoxEditCurrentRecord.Properties.Items == null)
            {
                return;
            }

            // 未选中任何项：清空显示即可
            if (comboBoxEditCurrentRecord.SelectedIndex < 0)
            {
                _currentEpbSummaryChannel = 0;
                ClearEpbSummaryPanel();
                return;
            }

            var selectedObj = comboBoxEditCurrentRecord.SelectedItem;
            if (selectedObj == null)
            {
                _currentEpbSummaryChannel = 0;
                ClearEpbSummaryPanel();
                return;
            }

            var selectedText = selectedObj.ToString();
            if (string.IsNullOrWhiteSpace(selectedText))
            {
                _currentEpbSummaryChannel = 0;
                ClearEpbSummaryPanel();
                return;
            }

            // —— 2) 从文本中解析通道号 —— //
            // 允许 "EPB-1" / "EPB1" / "EPB 01" 等格式：取最后一段数字
            Match lastDigitMatch = null;
            var matches = Regex.Matches(selectedText, @"\d+");
            if (matches.Count > 0)
            {
                lastDigitMatch = matches[matches.Count - 1];
            }

            int channelId;
            if (lastDigitMatch == null || !int.TryParse(lastDigitMatch.Value, out channelId))
            {
                // 文本里根本没有数字，防御性处理：清空显示
                _currentEpbSummaryChannel = 0;
                ClearEpbSummaryPanel();
                return;
            }

            // 这里可以根据实际通道范围做一次限幅，例如 1..12
            if (channelId < 1 || channelId > 12)
            {
                _currentEpbSummaryChannel = 0;
                ClearEpbSummaryPanel();
                return;
            }

            // —— 3) 更新当前选中通道并刷新显示 —— //
            _currentEpbSummaryChannel = channelId;
            var curRecord = EnsureEpbRecord(channelId);

            UpdateEpbSummaryPanel(curRecord);
        }

        /// <summary>
        /// Refreshes the summary only when the changed channel is the channel selected in the summary combo box.
        /// </summary>
        private void RefreshCurrentEpbSummary(int channel)
        {
            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(new Action<int>(RefreshCurrentEpbSummary), channel);
                }
                catch
                {
                    // Form is closing; no UI refresh is required.
                }

                return;
            }

            if (_currentEpbSummaryChannel != channel)
                return;

            EpbTestRecord record;
            lock (_epbRecordsLock)
            {
                record = _uiEpbRecords?.FirstOrDefault(r => r.Id == channel);
            }

            if (record != null)
                UpdateEpbSummaryPanel(record);
        }

        /// <summary>
        /// 清空 EPB 概览区域显示，用于“未选中”或解析失败的情况。
        /// </summary>
        private void SelectEpbSummaryChannel(int channel)
        {
            if (channel < 1 || channel > 12 || comboBoxEditCurrentRecord == null) return;
            var selectedIndex = channel - 1;
            if (selectedIndex >= comboBoxEditCurrentRecord.Properties.Items.Count) return;

            comboBoxEditCurrentRecord.SelectedIndex = selectedIndex;
            RefreshSummaryByComboSelection();
        }

        private void ClearEpbSummaryPanel()
        {
            // ② 运行时间
            LedRunTime.Text = "00D 00H 00M 00S";

            // ③ 完成次数
            LedRunCycles.Text = "0";

            // ④ 剩余次数
            LedLastCycles.Text = "0";

            // ⑤ 进度条
            ProcBar.Value = 0;

            // ⑥ 状态灯（灰色熄灭）
            uiLightStatus.OnCenterColor = Color.Gray;
            uiLightStatus.OnColor = Color.Gray;
            uiLightStatus.State = UILightState.Off;
        }


        /// <summary>
        /// 根据指定 EPB 通道的试验记录，刷新：
        /// ② LedRunTime    – 运行时间
        /// ③ LedRunCycles  – 完成次数
        /// ④ LedLastCycles – 剩余次数
        /// ⑤ ProcBar       – 进度条
        /// ⑥ uiLightStatus   – 状态灯(运行=绿闪；报警=红闪；其他=灰色常灭)
        /// </summary>
        /// <param name="record">EPB 通道记录（1..12）。</param>
        private void UpdateEpbSummaryPanel(EpbTestRecord record)
        {
            if (record == null) return;

            // === ② LedRunTime 显示 "00D 00H 00M" ===
            LedRunTime.Text = EpbTestRecord.FormatDHMS(record.RunTimeSpan);

            // === ③ 完成次数 ===
            uiLabel57.Text = "机械完成次数";
            var mechanicalCount = Math.Max(record.MechanicalCycleCount, record.RunCount);
            LedRunCycles.Text = mechanicalCount.ToString();

            // === ④ 剩余次数 ===
            int total = record.TotalCount > 0 ? record.TotalCount : (_cfg?.Test?.TestTarget ?? 0);
            int left = (int)Math.Max(0, total - mechanicalCount);
            LedLastCycles.Text = left.ToString();

            // === ⑤ 进度条百分比 ===
            int percent = (total > 0) ? (int)Math.Round(mechanicalCount * 100.0 / total) : 0;

            percent = Math.Max(0, Math.Min(100, percent));
            ProcBar.Value = percent;

            // === ⑥ 状态灯 ===
            switch (record.Status)
            {
                case EpbTestStatus.Running:
                    uiLightStatus.OnCenterColor = Color.LimeGreen;
                    uiLightStatus.OnColor = Color.LimeGreen;
                    uiLightStatus.State = UILightState.Blink;
                    break;

                case EpbTestStatus.Alarm:
                    uiLightStatus.OnCenterColor = Color.Red;
                    uiLightStatus.OnColor = Color.Red;
                    uiLightStatus.State = UILightState.Blink;
                    break;

                case EpbTestStatus.Completed:
                    uiLightStatus.OnCenterColor = Color.DodgerBlue;
                    uiLightStatus.OnColor = Color.DodgerBlue;
                    uiLightStatus.State = UILightState.On;
                    break;

                default:
                    uiLightStatus.OnCenterColor = Color.Gray;
                    uiLightStatus.OnColor = Color.Gray;
                    uiLightStatus.State = UILightState.Off;
                    break;
            }
        }

        /// <summary>
        /// 将界面维护的 <see cref="_uiEpbRecords"/> 写回到底层配置
        /// <see>
        ///     <cref>_cfg.Test.EpbRecords</cref>
        /// </see>
        /// 中。
        /// </summary>
        /// <remarks>
        /// - 仅负责内存对象之间的同步，不负责写入磁盘；
        /// - 调用方若需落盘，请再调用 <see>
        ///     <cref>SaveEpbRecordsToTestConfigSafe</cref>
        /// </see>
        /// 。
        /// </remarks>
        private void FlushUiEpbRecordsToConfig()
        {
            if (_cfg?.Test == null) return;

            lock (_epbRecordsLock)
            {
                // 单次原子替换，读线程只会看到替换前或替换后的完整唯一快照。
                _cfg.Test.EpbRecords.ReplaceAll(_uiEpbRecords.OrderBy(x => x.Id));
            }
        }

        /// <summary>
        /// 把当前 UI 侧 EPB 记录回写到 <see cref="_cfg.Test.EpbRecords"/>，
        /// 并尝试保存到 Config\TestConfig.xml。
        /// </summary>
        private void SaveEpbRecordsToTestConfigSafeOld()
        {
            if (_cfg?.Test == null) return;

            try
            {
                // 1) 先把 _uiEpbRecords 写回 _cfg.Test.EpbRecords
                FlushUiEpbRecordsToConfig();

                // 2) 再调用 ConfigLoader 统一保存（内部负责拼 TestConfig.xml 路径）
                ConfigLoader.SaveTest(_cfg.Test);
            }
            catch (Exception ex)
            {
                // 不因为保存失败干扰试验，只打个日志
                logger?.Warn("保存 EPB 试验记录到 TestConfig.xml 失败: " + ex.Message, "配置");
            }
        }


        /// <summary>
        /// 把当前 UI 侧 EPB 记录回写到 <see cref="_cfg.Test.EpbRecords"/>，
        /// 并尝试保存到“项目”下的 Config\TestConfig.xml。
        /// </summary>
        private void SaveEpbRecordsToTestConfigSafe()
        {
            if (_cfg?.Test == null) return;

            try
            {
                // 1) 先把 _uiEpbRecords 写回 _cfg.Test.EpbRecords
                FlushUiEpbRecordsToConfig();

                // 2) 计算“项目配置”的 TestConfig.xml 路径：
                //    约定：项目 Config 目录 = StoreDir\TestName\Config
                //          项目 TestConfig = StoreDir\TestName\Config\TestConfig.xml
                var projectPath = ConfigLoader.GetProjectTestConfigPath(
                    _cfg.Test.StoreDir,
                    _cfg.Test.TestName);

                if (!string.IsNullOrEmpty(projectPath) && File.Exists(projectPath))
                {
                    // 优先写入“项目专用”的 TestConfig.xml（带运行进度）
                    ConfigLoader.SaveTest(projectPath, _cfg.Test);
                }
                else
                {
                    // 若项目路径无效或文件不存在（极端情况/旧项目），
                    // 退回到旧逻辑：写入软件默认 Config\TestConfig.xml
                    // （保证兼容性，但正常情况下不会走到这里）
                    ConfigLoader.SaveTest(_cfg.Test);
                }
            }
            catch (Exception ex)
            {
                // 不因为保存失败干扰试验，只打个日志
                logger?.Warn("保存 EPB 试验记录到项目 TestConfig.xml 失败: " + ex.Message, "配置");
            }
        }


        private void AutoSaveTimer_Tick(object sender, EventArgs e)
        {
            // 使用 lock 确保与 OnEpbChannelCycleCompleted 并发安全
            lock (_epbRecordsLock)
            {
                FlushUiEpbRecordsToConfig();
            }

            SaveEpbRecordsToTestConfigSafe();
        }

        #endregion

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

        private ConcurrentDictionary<string, double> Dev1ParaNameToScale = new();
        private ConcurrentDictionary<string, double> Dev1ParaNameToOffset = new();
        private ConcurrentDictionary<string, double> Dev1ParaNameToZeroValue = new();

        private ConcurrentDictionary<string, double> Dev2ParaNameToScale = new();
        private ConcurrentDictionary<string, double> Dev2ParaNameToOffset = new();
        private ConcurrentDictionary<string, double> Dev2ParaNameToZeroValue = new();


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

        private static ConcurrentDictionary<string, int> Dev1DaqChannel = new();
        private static ConcurrentDictionary<string, int> Dev2DaqChannel = new();

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

            ShowOperatorMessage(@"已将当前勾选状态保存为默认值。");
        }

        #endregion


        #region 曲线处理

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

                // 防御：若历史代码/异常路径曾经向 YAxisList/Y2AxisList 追加过额外轴，
                // 会持续挤压绘图区（看起来“曲线显示区域越来越小”）。
                // 这里统一把“非必需轴”隐藏，只保留：
                // - 左侧：主 Y 轴 +（可选）压力轴
                // - 右侧：主 Y2 轴
                NormalizeRealtimeAxes(pane);

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

                // 轴布局约定（按现场习惯）：左侧=电流 + 压力，右侧=力
                // - 电流：用主左轴（YAxis, index 0）
                // - 压力：用第二左轴（PRESSURE_AXIS）
                // - 力：用右轴（Y2Axis, index 0）
                pane.Y2Axis.Color = Color.Purple;
                pane.Y2Axis.Scale.FontSpec.FontColor = Color.Purple;
                pane.Y2Axis.Title.FontSpec.FontColor = Color.Purple;
                pane.Y2Axis.Title.IsVisible = false;


                // ★ 新增：确保有一个用于压力的第二左轴，并拿到它的索引
                var pressureAxisIndex = EnsurePressureYAxis(pane);

                // —— 路由表重建 —— //
                _route.Clear();
                foreach (var c in _allChs)
                    _route[RouteKey(c.Device, c.AiIndex)] = c.GlobalIndex;

                // —— 绑定/缓存 15 个 CheckEdit —— //
                _checkByGlobal.Clear();
                _instantDisplayControls.Clear();
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

                    // 映射瞬时显示控件 - 直接通过属性引用而非Controls.Find
                    TextEdit displayCtl = null;
                    if (g < _allChs.Length)
                    {
                        var ch = _allChs[g];
                        switch (ch.Type)
                        {
                            case SignalType.Current:
                                // EPB电流通道 - 根据DisplayName中的编号映射到对应控件
                                if (ch.DisplayName.Contains("DAQ_A") && ch.DisplayName.Contains("_I(A)"))
                                {
                                    // 从"DAQ_A7_I(A)"中提取编号7
                                    var startIndex = ch.DisplayName.IndexOf("DAQ_A") + 5;
                                    var endIndex = ch.DisplayName.IndexOf("_I(A)");
                                    if (startIndex < endIndex &&
                                        int.TryParse(ch.DisplayName.Substring(startIndex, endIndex - startIndex),
                                            out var epbNum))
                                        displayCtl = epbNum switch
                                        {
                                            1 => textEditCurrent1,
                                            2 => textEditCurrent2,
                                            3 => textEditCurrent3,
                                            4 => textEditCurrent4,
                                            5 => textEditCurrent5,
                                            6 => textEditCurrent6,
                                            7 => textEditCurrent7,
                                            8 => textEditCurrent8,
                                            9 => textEditCurrent9,
                                            10 => textEditCurrent10,
                                            11 => textEditCurrent11,
                                            12 => textEditCurrent12,
                                            _ => null
                                        };
                                }

                                break;
                            case SignalType.Pressure:
                                // 压力通道 -> textEditP1, textEditP2
                                displayCtl = ch.DisplayName.Contains("P1") ? textEditP1 :
                                    ch.DisplayName.Contains("P2") ? textEditP2 : null;
                                break;
                            case SignalType.Force:
                                // 夹紧力通道 -> textEditF
                                displayCtl = textEditF;
                                break;
                        }

                        if (displayCtl != null)
                            _instantDisplayControls[g] = displayCtl;
                        // 调试日志
                        // logger?.Info(
                        //     $"控件映射成功: 全局索引{g} -> {displayCtl.Name} (设备:{ch.Device}, 通道:{ch.AiIndex}, 参数:{ch.DisplayName}, 类型:{ch.Type})");
                        else
                            logger?.Warn($"未找到对应控件: 全局索引{g}, 参数:{ch.DisplayName}, 类型:{ch.Type}");
                    }
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
                            // 电流 -> 左侧主轴（YAxis, index 0）
                            curve.IsY2Axis = false;
                            curve.YAxisIndex = 0;
                            break;

                        case SignalType.Pressure:
                            // 两个压力 -> 新增的第二左轴（pressureAxisIndex >= 1）
                            curve.IsY2Axis = false; // 左侧轴族
                            curve.YAxisIndex = pressureAxisIndex;
                            break;

                        case SignalType.Force:
                        default:
                            // 力 -> 右侧轴（Y2Axis, index 0）
                            curve.IsY2Axis = true;
                            // curve.Y2AxisIndex = 0; // 默认 0
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

                ApplyZedGraphFastRenderSettings();

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

                // 设置曲线的应该的固定宽度为周期的4倍
                // ReSharper disable once PossibleLossOfFraction
                _fixedXWindowSec = _cfg.Test.PeriodMs / 1000 * 2;

                // 曲线应用固定宽度（若未显式设置，则用 ClsGlobal.XDuration）
                SetXWindowSeconds(_fixedXWindowSec > 0 ? _fixedXWindowSec : ClsGlobal.XDuration);


                // —— 启动 UI 定时器（25FPS），统一 AxisChange + Invalidate —— //
                StartUiRedrawTimer();
            }
            catch (Exception ex)
            {
                ShowOperatorMessage(@"初始化曲线显示失败！" + ex.Message, @"提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                ClsErrorProcess.AddToErrorList(MaxErrors, ref LogError,
                    "初始化曲线显示失败！" + ex.Message, "初始化");
            }
        }


        /// <summary>
        ///     统一规整实时曲线的坐标轴：**移除**多余堆叠轴（而不是仅隐藏），避免绘图区被挤压。
        ///     ZedGraph 即使轴 IsVisible=false，轴对象仍在列表中时可能影响布局计算。
        /// </summary>
        private static void NormalizeRealtimeAxes(GraphPane pane)
        {
            if (pane == null) return;

            try
            {
                // 右侧：移除所有额外的 Y2 轴，只保留主 Y2 轴（Index 0）
                if (pane.Y2AxisList != null)
                    while (pane.Y2AxisList.Count > 1)
                        pane.Y2AxisList.RemoveAt(pane.Y2AxisList.Count - 1);

                // 左侧：移除所有额外的 Y 轴，只保留主 Y 轴（Index 0）
                // 压力轴会在 EnsurePressureYAxis 中重新创建（有 Tag 防重复）
                if (pane.YAxisList != null)
                    while (pane.YAxisList.Count > 1)
                        pane.YAxisList.RemoveAt(pane.YAxisList.Count - 1);
            }
            catch
            {
                // 规整失败不影响主流程
            }
        }

        #region 轴创建与选择

        /// <summary>
        ///     创建或获取用于“压力（bar）”显示的左侧第二 Y 轴，并返回其索引。
        ///     - 轴放在左侧（YAxisList）
        ///     - 通过 Axis.Tag 标记，避免重复创建
        ///     - 为了区分，采用对比度较高的配色；网格默认关闭，防止与主轴混乱
        /// </summary>
        /// <param name="pane">ZedGraph 的 GraphPane</param>
        /// <returns>压力轴在 YAxisList 中的索引（>=1）</returns>
        private static int EnsurePressureYAxis(GraphPane pane)
        {
            // 1) 若已存在（通过 Tag 标记），直接返回
            for (var i = 0; i < pane.YAxisList.Count; i++)
                if (pane.YAxisList[i]?.Tag is string tag && tag == "PRESSURE_AXIS")
                    return i;

            // 2) 创建新的左侧 Y 轴（将出现在默认 Y 轴的左边堆叠显示）
            var pressureAxis = new YAxis("Pressure (bar)")
            {
                IsVisible = true,
                // 颜色尽量与主轴（蓝色系）区分

                /*
                Color = Color.DarkOrange,
                Title = { FontSpec = { Size = 12, FontColor = Color.DarkOrange } },
                Scale = { FontSpec = { Size = 12, FontColor = Color.DarkOrange } },
                */


                Color = Color.DarkBlue,
                Title = { FontSpec = { Size = 12, FontColor = Color.DarkBlue } },
                Scale = { FontSpec = { Size = 12, FontColor = Color.DarkBlue } },
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
        ///     对 ZedGraph 的绘制参数做“性能优先”设置。
        /// </summary>
        /// <remarks>
        ///     <para>
        ///     该方法的目标是：在全通道显示与窗口前后台切换场景下，尽量降低绘制开销并减少渲染抖动。
        ///     </para>
        ///     <para>
        ///     ZedGraph 的不同版本/分支可能不存在 <c>IsFastLine</c> 属性，因此这里用反射“有则启用，无则跳过”，
        ///     以避免因版本差异导致编译失败。
        ///     </para>
        /// </remarks>
        private void ApplyZedGraphFastRenderSettings()
        {
            if (_isClosing || Volatile.Read(ref _formClosedFlag) == 1) return;
            if (zedGraphRealChart == null || zedGraphRealChart.IsDisposed) return;

            try
            {
                if (zedGraphRealChart.InvokeRequired)
                {
                    zedGraphRealChart.BeginInvoke(new Action(ApplyZedGraphFastRenderSettings));
                    return;
                }

                // 控件级别抗锯齿（你之前已经关闭过，这里做一次兜底）
                zedGraphRealChart.IsAntiAlias = false;

                var pane = zedGraphRealChart.GraphPane;
                if (pane == null) return;

                var hasFastLineProperty = false;

                // 曲线级别：关闭抗锯齿/平滑，尽量走“快线”路径
                foreach (var item in pane.CurveList)
                {
                    if (item is not LineItem li) continue;

                    li.Line.IsAntiAlias = false;
                    li.Line.IsSmooth = false;

                    // 某些 ZedGraph 版本支持 IsFastLine；有则开启
                    hasFastLineProperty |= TrySetBoolProperty(li.Line, "IsFastLine", true);
                    hasFastLineProperty |= TrySetBoolProperty(li, "IsFastLine", true);
                }

                if (Interlocked.Exchange(ref _fastRenderSettingsLogged, 1) == 0)
                    logger?.Info(
                        $"ZedGraph 快速渲染设置已应用：AntiAlias=OFF, Smooth=OFF, IsFastLine={(hasFastLineProperty ? "ON" : "N/A")}, Curves={pane.CurveList.Count}",
                        "UI");
            }
            catch
            {
                // 性能设置失败不影响主流程
            }
        }


        /// <summary>
        ///     通过反射给目标对象设置布尔属性（属性不存在/不可写则忽略）。
        /// </summary>
        /// <param name="target">要设置属性的对象。</param>
        /// <param name="propertyName">属性名。</param>
        /// <param name="value">要写入的值。</param>
        /// <returns>
        ///     若属性存在且成功写入返回 <c>true</c>；否则返回 <c>false</c>。
        /// </returns>
        private static bool TrySetBoolProperty(object target, string propertyName, bool value)
        {
            if (target == null) return false;
            if (string.IsNullOrWhiteSpace(propertyName)) return false;

            try
            {
                var p = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
                if (p == null) return false;
                if (!p.CanWrite) return false;
                if (p.PropertyType != typeof(bool)) return false;

                p.SetValue(target, value, null);
                return true;
            }
            catch
            {
                return false;
            }
        }


        /// <summary>
        ///     启动 UI 重绘定时器：统一在该定时器里进行 AxisChange / Invalidate，
        ///     并批量删除旧点，避免在采集回调里高频重绘导致卡顿。
        /// </summary>
        private void StartUiRedrawTimer()
        {
            if (_uiTimer != null) return;

            _uiTimer = new System.Windows.Forms.Timer
            {
                Interval = Math.Max(10, 1000 / UI_TARGET_FPS) // 约 UI_TARGET_FPS FPS
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
                var purgeTriggerBefore = Math.Max(
                    0.0,
                    purgeBefore - Math.Max(UiPurgeBatchMinSec, width * 0.02));

                // —— 按通道批量清理 —— //
                for (var g = 0; g < _chData.Length; g++)
                {
                    var list = _chData[g];
                    if (list == null || list.Count == 0) continue;

                    // 最早的点尚未越过批量裁剪触发线时继续保留；这只多保留约 1 秒
                    // 显示点，不改变可见窗口，也避免每个 UI Tick 都复制整个点列。
                    // 若最早点 X 是 NaN/Inf，则后续比较会失效：需要强制进入裁剪逻辑清掉它
                    if (!double.IsNaN(list[0].X) && !double.IsInfinity(list[0].X) &&
                        list[0].X >= purgeTriggerBefore)
                        continue;

                    // 线性寻界（点数很多时可改成二分搜索）
                    int cut = 0, cnt = list.Count;
                    while (cut < cnt)
                    {
                        var x = list[cut].X;
                        if (double.IsNaN(x) || double.IsInfinity(x) || x < purgeBefore)
                        {
                            cut++;
                            continue;
                        }

                        break;
                    }

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
                        while (cut2 < cnt2)
                        {
                            var x = list[cut2].X;
                            if (double.IsNaN(x) || double.IsInfinity(x) || x < ownKeepMin)
                            {
                                cut2++;
                                continue;
                            }

                            break;
                        }
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
                // 仅清空点数据与缓冲区，不重建曲线/坐标轴。
                // 避免运行中（误触发/异常路径）反复 InitializeCurve() 导致轴堆叠挤压绘图区。
                listForce?.Clear();
                for (var g = 0; g < _chData.Length; g++)
                    _chData[g]?.Clear();

                for (var i = 0; i < _lastX.Length; i++) _lastX[i] = 0.0;
                _latestGlobalX = 0.0;
                _plotZeroTime = DateTime.MinValue; // 重置绘图零点
                _dirtyForRedraw = true;

                bufferA.Clear();
                bufferB.Clear();
                activeWriteBuffer = bufferA;
                readyReadBuffer = bufferB;
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
                if (_daqRuntimeSettings.SampleRateHz <= 0)
                    throw new InvalidOperationException("DaqFrequency 未正确设置。");

                var dt = 1.0 / _daqRuntimeSettings.SampleRateHz;

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
                    var spanSec = daqData.Length * (1.0 / _daqRuntimeSettings.SampleRateHz);
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
                result[i] = (result[i] - Dev2ParaNameToZeroValue[CurrentDev]) * Dev2ParaNameToScale[CurrentDev] +
                            Dev2ParaNameToOffset[CurrentDev];

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
                ShowOperatorMessage(@"初始化定时访问组件失败！" + ex.Message);
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
                    ShowOperatorMessage($@"Timer {EmbIndex} failed to start!");
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
            return $"Timer {index}: {(timer.IsRunning ? "? Running" : "? Stopped")}\n" +
                   $"Interval: {timer.Interval}ms\n" +
                   $"Counter: {timer.CycleCounter[index]}";
        }

        #endregion


        #region UI滚动消息

        private void InitializeUiInfoLog()
        {
            var storeDir = _cfg?.Test?.StoreDir;
            var testName = _cfg?.Test?.TestName;
            var projectRoot = ConfigLoader.GetProjectRootDir(storeDir, testName);
            if (string.IsNullOrEmpty(projectRoot))
                projectRoot = Path.Combine(Environment.CurrentDirectory, "ProjectLogs");

            _uiInfoLogStore?.Dispose();
            _uiInfoLogStore = new UiInfoLogStore(new UiInfoLogOptions
            {
                MaxFileBytes = 10L * 1024L * 1024L,
                RetentionDays = 30,
                MaximumRecentLines = UiInfoRecentLineLimit,
                WarningSink = (message, exception) =>
                    logger?.Warn($"{message}: {exception?.Message}", "UI日志")
            });
            if (!_uiInfoLogStore.Initialize(projectRoot))
                return;

            var existingLines = _uiInfoLogStore.ReadRecentLines(UiInfoRecentLineLimit)
                .Where(ShouldDisplayOperatorInfo)
                .ToList();
            _suppressRtbInfoTextChanged = true;
            RtbInfo.Text = existingLines.Count == 0
                ? string.Empty
                : string.Join(Environment.NewLine, existingLines) + Environment.NewLine;
            _uiInfoVisibleLineCount = existingLines.Count;
            _suppressRtbInfoTextChanged = false;

            RtbInfo.TextChanged -= RtbInfo_TextChanged;
            RtbInfo.TextChanged += RtbInfo_TextChanged;

            if (_uiInfoFlushTimer == null)
            {
                _uiInfoFlushTimer = new System.Windows.Forms.Timer { Interval = 150 };
                _uiInfoFlushTimer.Tick += UiInfoFlushTimer_Tick;
                _uiHeartbeatLastTick = Stopwatch.GetTimestamp();
                _uiHeartbeatWindowStartedTick = _uiHeartbeatLastTick;
                _uiInfoFlushTimer.Start();
            }
        }

        private void LogInfo(string message)
        {
            if (!ShouldDisplayOperatorInfo(message))
                return;

            var formatted = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message.Trim()}";
            AppendInfoLine(formatted);
            _ = _uiInfoLogStore?.AppendAsync(formatted);
        }

        internal static bool ShouldDisplayOperatorInfo(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return false;
            return message.IndexOf("机械完成圈已计数", StringComparison.OrdinalIgnoreCase) < 0 &&
                   message.IndexOf("WatchdogJournalPolicy ", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private void AppendInfoLine(string formattedLine)
        {
            if (string.IsNullOrEmpty(formattedLine) || _isClosing)
                return;

            _pendingUiInfoLines.Enqueue(formattedLine);
        }

        private void UiInfoFlushTimer_Tick(object sender, EventArgs e)
        {
            var enteredTick = Stopwatch.GetTimestamp();
            var appendMs = 0.0;
            var trimMs = 0.0;
            var scrollMs = 0.0;
            try
            {
                if (_isClosing || RtbInfo == null || RtbInfo.IsDisposed)
                    return;

                _uiInfoBatch.Clear();
                while (_uiInfoBatch.Count < UiInfoBatchMaxLines &&
                       _pendingUiInfoLines.TryDequeue(out var line))
                {
                    _uiInfoBatch.Add(line);
                }
                if (_uiInfoBatch.Count > 0)
                {
                    _suppressRtbInfoTextChanged = true;
                    try
                    {
                        var phaseStarted = Stopwatch.GetTimestamp();
                        RtbInfo.AppendText(
                            string.Join(Environment.NewLine, _uiInfoBatch) + Environment.NewLine);
                        appendMs = UiElapsedMilliseconds(phaseStarted, Stopwatch.GetTimestamp());
                        _uiInfoVisibleLineCount += _uiInfoBatch.Count;
                        _uiInfoRenderedBatches++;
                        _uiInfoRenderedLines += _uiInfoBatch.Count;

                        // 不再读取/赋值 RichTextBox.Lines 重建全部文本；只用原生行索引
                        // 原位删除头部越界段，持久日志仍由 UiInfoLogStore 完整保存。
                        if (_uiInfoVisibleLineCount > UiInfoRecentLineLimit)
                        {
                            phaseStarted = Stopwatch.GetTimestamp();
                            var actualLineCount = RtbInfo.TextLength == 0
                                ? 0
                                : RtbInfo.GetLineFromCharIndex(RtbInfo.TextLength - 1) + 1;
                            var removeLines = UiLogDisplayPolicy.CalculateLinesToRemove(
                                actualLineCount,
                                UiInfoRecentLineLimit,
                                UiInfoTrimWatermark);
                            if (removeLines > 0)
                            {
                                var removeChars = RtbInfo.GetFirstCharIndexFromLine(removeLines);
                                if (removeChars > 0)
                                {
                                    RtbInfo.Select(0, removeChars);
                                    RtbInfo.SelectedText = string.Empty;
                                    _uiInfoVisibleLineCount = actualLineCount - removeLines;
                                }
                            }
                            trimMs = UiElapsedMilliseconds(phaseStarted, Stopwatch.GetTimestamp());
                        }
                        _uiInfoAutoScrollPending = true;
                    }
                    finally
                    {
                        _suppressRtbInfoTextChanged = false;
                    }
                }

                var nowTick = Stopwatch.GetTimestamp();
                if (_uiInfoAutoScrollPending &&
                    UiLogDisplayPolicy.ShouldAutoScroll(
                        nowTick,
                        _uiInfoLastAutoScrollTick,
                        Stopwatch.Frequency,
                        UiInfoAutoScrollMinIntervalMs))
                {
                    var phaseStarted = Stopwatch.GetTimestamp();
                    RtbInfo.SelectionStart = RtbInfo.TextLength;
                    RtbInfo.ScrollToCaret();
                    var completed = Stopwatch.GetTimestamp();
                    scrollMs = UiElapsedMilliseconds(phaseStarted, completed);
                    _uiInfoLastAutoScrollTick = completed;
                    _uiInfoAutoScrollPending = false;
                }
            }
            finally
            {
                RecordUiHeartbeat(
                    enteredTick,
                    Stopwatch.GetTimestamp(),
                    appendMs,
                    trimMs,
                    scrollMs);
            }
        }

        private void RecordUiHeartbeat(
            long enteredTick,
            long completedTick,
            double appendMs,
            double trimMs,
            double scrollMs)
        {
            var previous = _uiHeartbeatLastTick;
            _uiHeartbeatLastTick = enteredTick;
            if (previous > 0 && enteredTick >= previous)
            {
                var intervalMs = (enteredTick - previous) * 1000.0 / Stopwatch.Frequency;
                _uiHeartbeatDelayMs.Add(Math.Max(0, intervalMs - 150.0));
            }
            if (completedTick >= enteredTick)
                _uiHeartbeatFlushMs.Add(
                    (completedTick - enteredTick) * 1000.0 / Stopwatch.Frequency);
            _uiInfoAppendMaxMs = Math.Max(_uiInfoAppendMaxMs, appendMs);
            _uiInfoTrimMaxMs = Math.Max(_uiInfoTrimMaxMs, trimMs);
            _uiInfoScrollMaxMs = Math.Max(_uiInfoScrollMaxMs, scrollMs);

            var windowStarted = _uiHeartbeatWindowStartedTick;
            if (windowStarted <= 0)
            {
                _uiHeartbeatWindowStartedTick = enteredTick;
                return;
            }
            if ((completedTick - windowStarted) * 1000.0 / Stopwatch.Frequency < 10000)
                return;

            var delayP95 = Percentile(_uiHeartbeatDelayMs, 0.95);
            var delayMax = _uiHeartbeatDelayMs.Count == 0 ? 0 : _uiHeartbeatDelayMs.Max();
            var flushP95 = Percentile(_uiHeartbeatFlushMs, 0.95);
            var flushMax = _uiHeartbeatFlushMs.Count == 0 ? 0 : _uiHeartbeatFlushMs.Max();
            var filePending = _uiInfoLogStore?.PendingCount ?? 0;
            var fileDropped = _uiInfoLogStore?.DroppedLines ?? 0;
            logger?.Info(
                $"FieldMetric UI DelayP95Ms={delayP95:F3} DelayMaxMs={delayMax:F3} " +
                $"FlushP95Ms={flushP95:F3} FlushMaxMs={flushMax:F3} " +
                $"AppendMaxMs={_uiInfoAppendMaxMs:F3} TrimMaxMs={_uiInfoTrimMaxMs:F3} " +
                $"ScrollMaxMs={_uiInfoScrollMaxMs:F3} RenderedBatches={_uiInfoRenderedBatches} " +
                $"RenderedLines={_uiInfoRenderedLines} " +
                $"Pending={_pendingUiInfoLines.Count} Dropped={_pendingUiInfoLines.DroppedCount} " +
                $"FilePending={filePending} FileDropped={fileDropped}",
                "FIELD");
            _uiHeartbeatDelayMs.Clear();
            _uiHeartbeatFlushMs.Clear();
            _uiInfoAppendMaxMs = 0;
            _uiInfoTrimMaxMs = 0;
            _uiInfoScrollMaxMs = 0;
            _uiInfoRenderedBatches = 0;
            _uiInfoRenderedLines = 0;
            _uiHeartbeatWindowStartedTick = completedTick;
        }

        private static double Percentile(IReadOnlyCollection<double> values, double fraction)
        {
            if (values == null || values.Count == 0) return 0;
            var ordered = values.OrderBy(value => value).ToArray();
            var index = Math.Min(
                ordered.Length - 1,
                Math.Max(0, (int)Math.Round(
                    (ordered.Length - 1) * fraction,
                    MidpointRounding.AwayFromZero)));
            return ordered[index];
        }

        private static double UiElapsedMilliseconds(long startedTick, long completedTick) =>
            completedTick >= startedTick
                ? (completedTick - startedTick) * 1000.0 / Stopwatch.Frequency
                : 0.0;

        private async void RtbInfo_TextChanged(object sender, EventArgs e)
        {
            if (_suppressRtbInfoTextChanged || _uiInfoLogStore == null)
                return;

            await _uiInfoLogStore.ReplaceActiveAsync(RtbInfo.Text).ConfigureAwait(false);
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
            OpenProjectLog(ProjectLogLevel.Info);
        }

        /// <summary>
        ///     警告日志按钮点击事件
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void BtnWarnLog_Click(object sender, EventArgs e)
        {
            OpenProjectLog(ProjectLogLevel.Warning);
        }

        /// <summary>
        ///     按钮点击事件，查看错误日志
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void BtnErrorLog_Click(object sender, EventArgs e)
        {
            OpenProjectLog(ProjectLogLevel.Error);
        }

        private void OpenProjectLog(ProjectLogLevel level)
        {
            try
            {
                logger?.Flush();
                var path = ProjectLogHub.GetActivePath(level);
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    ShowOperatorMessage(
                        "\u5f53\u524d\u9879\u76ee\u65e5\u5fd7\u5c1a\u672a\u521d\u59cb\u5316\u6216\u8be5\u7ea7\u522b\u5c1a\u65e0\u8bb0\u5f55\u3002",
                        "\u9879\u76ee\u65e5\u5fd7",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                Process.Start("notepad.exe", $"\"{path}\"");
            }
            catch (Exception ex)
            {
                ShowOperatorMessage(ex.Message);
            }
        }

        #endregion


        #region 数据落盘相关 2025/09/09

        /// <summary>
        ///     生成一次试验的落盘根目录，并拷贝关键配置，便于追溯。
        ///     命名示例：DataStore\2025-09-08_12-34-56\
        /// </summary>
        private void PublishCurrentProjectBuildIdentity()
        {
            if (_cfg?.Test == null) return;
            var configDirectory = ConfigLoader.GetProjectConfigDir(
                _cfg.Test.StoreDir,
                _cfg.Test.TestName);
            var projectRoot = ConfigLoader.GetProjectRootDir(
                _cfg.Test.StoreDir,
                _cfg.Test.TestName);
            var identity = RuntimeBuildIdentity.Capture();
            if (identity.TryWriteProjectJson(
                    configDirectory,
                    _cfg.Test.TestName,
                    projectRoot,
                    out var path,
                    out var error))
            {
                logger?.Info($"已更新项目运行构建身份：{path}", "配置");
                return;
            }

            logger?.Warn($"写入项目运行构建身份失败：{error}", "配置");
        }

        private void PrepareDataStoreDirectory()
        {
            var root = Path.Combine(Environment.CurrentDirectory, "DataStore");
            Directory.CreateDirectory(root);
            _dataStorePath = Path.Combine(root, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
            Directory.CreateDirectory(_dataStorePath);

            // 备份关键配置（AI/DO/AO/Test），和旧项目一样便于追溯
            void TryCopy(string file)
            {
                try
                {
                    var src = RuntimeConfigPaths.GetPath(file);
                    if (File.Exists(src)) File.Copy(src, Path.Combine(_dataStorePath, file), true);
                }
                catch
                {
                    /* 忽略单个文件的拷贝失败 */
                }
            }

            TryCopy("AIConfig.xml");
            TryCopy("DOConfig.xml");
            TryCopy("AOConfig.xml");
            TryCopy("TestConfig.xml");
        }

        /// <summary>
        ///     初始化 DAQ 数据落盘上下文与定时器（按设备划分：Dev1/Dev2）。
        /// </summary>
        /// <param name="logSpanMs">定时落盘周期（毫秒），建议 100~500ms；与旧项目相同。</param>
        private void InitDaqLogTimer(int logSpanMs)
        {
            // 1) Dev1 上下文
            var dev1ChannelCount = Dev1UsedDaqAIChannels?.Length ?? 0; //dev1的使用通道数量，动态获取


            _daqDev1 = new DaqAIContext(
                "Dev1",
                100, // 单批缓存上限（沿用旧工程缺省）
                ClsGlobal.FileChangeMinutes,
                _daqTimeSpanMs, // 或用 DaqTimeSpanMilSeconds
                dev1ChannelCount,
                _daqRuntimeSettings.SamplesPerChannel,
                _dataStorePath)
            {
                // Dev1：建立通道映射（EPB1..6电流 + Pressure_1压力 -> Dev1各通道序号）
                // 旧工程用 ClsXmlOperation.GetDaqAIChannelMapping 读到的 EMB->通道索引用于统计落盘。
                // 你当前窗体已加载了 Dev1 的 Dev1DaqChannel，可直接复用。
                eMBToDaqCurrentChannel = new SortedDictionary<string, int>(Dev1DaqChannel),
                // Dev1：工程值变换（scale/offset/zero），用于统计落盘转工程值:contentReference[oaicite:18]{index=18}
                paraNameToScale = new ConcurrentDictionary<string, double>(Dev1ParaNameToScale),
                paraNameToOffset = new ConcurrentDictionary<string, double>(Dev1ParaNameToOffset),
                paraNameToZeroValue = new ConcurrentDictionary<string, double>(Dev1ParaNameToZeroValue)
            };

            // 2) Dev2 上下文（与 Dev1 对称）
            var dev2ChannelCount = Dev2UsedDaqAIChannels?.Length ?? 0;
            _daqDev2 = new DaqAIContext(
                "Dev2",
                100,
                ClsGlobal.FileChangeMinutes,
                _daqTimeSpanMs,
                dev2ChannelCount,
                _daqRuntimeSettings.SamplesPerChannel,
                _dataStorePath);

            // Dev2 的 EMB->通道映射：建议再次调用配置读取方法获取 Dev2 的映射
            // （若你的 AIConfig.xml 已定义 EPB7..12 -> Dev2/ai#），否则统计落盘会只写 Dev1。
            // 这里演示读取：

            _daqDev2.eMBToDaqCurrentChannel = new SortedDictionary<string, int>(Dev2DaqChannel);
            _daqDev2.paraNameToScale = new ConcurrentDictionary<string, double>(Dev2ParaNameToScale);
            _daqDev2.paraNameToOffset = new ConcurrentDictionary<string, double>(Dev2ParaNameToOffset);
            _daqDev2.paraNameToZeroValue = new ConcurrentDictionary<string, double>(Dev2ParaNameToZeroValue);

            // 3) 定时器：原始落盘 + 统计落盘（与旧项目一样双定时器，每个设备两只）
            _daqRawTimerDev1 = new Timer(async _ =>
                {
                    try
                    {
                        await _daqDev1.FlushRawToDiskAsync();
                    }
                    catch
                    {
                    }
                },
                null, logSpanMs, logSpanMs);

            // 暂时注释
            /*_daqStatTimerDev1 = new Timer(async _ =>
                {
                    try
                    {
                        await _daqDev1.FlushStatToDiskAsync();
                    }
                    catch
                    {
                    }
                },
                null, logSpanMs, logSpanMs);*/

            _daqRawTimerDev2 = new Timer(async _ =>
                {
                    try
                    {
                        await _daqDev2.FlushRawToDiskAsync();
                    }
                    catch
                    {
                    }
                },
                null, logSpanMs, logSpanMs);

            // 暂时注释
            /*_daqStatTimerDev2 = new Timer(async _ =>
                {
                    try
                    {
                        await _daqDev2.FlushStatToDiskAsync();
                    }
                    catch
                    {
                    }
                },
                null, logSpanMs, logSpanMs);*/
        }

        /// <summary>
        ///     根据全局通道索引获取对应的瞬时显示控件名称
        /// </summary>
        /// <param name="globalIndex">全局通道索引 0-14</param>
        /// <returns>控件名称，如果没有对应控件则返回null</returns>
        private static string GetDisplayControlName(int globalIndex)
        {
            return globalIndex switch
            {
                // EPB电流通道 (0-11) -> textEditCurrent1-12
                >= 0 and <= 11 => $"textEditCurrent{globalIndex + 1}",
                // 压力通道 (12-13) -> textEditP1, textEditP2
                12 => "textEditP1",
                13 => "textEditP2",
                // 夹紧力通道 (14) -> textEditF
                14 => "textEditF",
                _ => null
            };
        }

        /// <summary>
        ///     动态更新所有通道的瞬时显示值
        /// </summary>
        private void UpdateInstantDisplayValues()
        {
            if (_isClosing || IsDisposed || !IsHandleCreated) return;

            // 如果需要跨线程调用，封送到UI线程
            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(new Action(UpdateInstantDisplayValues));
                }
                catch
                {
                    // 窗体已销毁，忽略
                }

                return;
            }

            try
            {
                foreach (var kvp in _instantDisplayControls)
                {
                    var globalIndex = kvp.Key;
                    var textEdit = kvp.Value;

                    if (globalIndex < 0 || globalIndex >= _chData.Length) continue;
                    if (_chData[globalIndex] == null || _chData[globalIndex].Count == 0) continue;
                    if (textEdit == null || textEdit.IsDisposed) continue;

                    try
                    {
                        var latestValue = _chData[globalIndex][_chData[globalIndex].Count - 1].Y;
                        var formattedText = FormatDisplayValue(globalIndex, latestValue);
                        textEdit.Text = formattedText;
                    }
                    catch (Exception ex)
                    {
                        // 忽略单个控件更新失败，避免影响其他控件
                        logger?.Error($"更新通道{globalIndex}显示值失败: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Error($"批量更新瞬时显示值失败: {ex.Message}");
            }
        }

        private void uiTableLayoutPanel15_Paint(object sender, PaintEventArgs e)
        {
        }


        /// <summary>
        ///     根据通道类型格式化显示值
        /// </summary>
        /// <param name="globalIndex">全局通道索引</param>
        /// <param name="value">原始数值</param>
        /// <returns>格式化后的显示文本</returns>
        private string FormatDisplayValue(int globalIndex, double value)
        {
            if (globalIndex < 0 || globalIndex >= _allChs.Length)
                return $"{value:F2}";

            var channel = _allChs[globalIndex];
            return channel.Type switch
            {
                SignalType.Current => $"{value:F3} A", // 电流显示3位小数 + 单位A
                SignalType.Pressure => $"{value:F1} bar", // 压力显示1位小数 + 单位bar
                SignalType.Force => $"{value:F0} N", // 夹紧力显示整数 + 单位N
                _ => $"{value:F2}"
            };
        }

        /// <summary>
        ///     采集线程回调：接收原始二维阵列并入队（旧项目同款策略）。
        ///     注意：这里只做入队，不做磁盘 I/O；I/O 交给定时器线程做（避免阻塞采集）。
        /// </summary>
        private void Acq_OnRawBatch(string device, double[,] raw, DateTime current, DateTime last)
        {
            if (_isClosing) return;

            if (device.Equals("Dev1", StringComparison.OrdinalIgnoreCase))
            {
                _daqDev1?.EnqueueRawData(raw, current, last);
                _daqDev1?.EnqueueStatData(raw, current); // 统计队列（依赖 eMB->通道映射）
            }
            else if (device.Equals("Dev2", StringComparison.OrdinalIgnoreCase))
            {
                _daqDev2?.EnqueueRawData(raw, current, last);
                _daqDev2?.EnqueueStatData(raw, current);
            }
        }

        #endregion
    }
}
