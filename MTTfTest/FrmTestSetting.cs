using Config;
using Config.Models;
using DevExpress.Data.Helpers;
using DevExpress.XtraEditors;
using MTEmbTest;
using MTEmbTest.Models;
using Sunny.UI;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;

namespace MtEmbTest
{
    public partial class FrmTestSetting : Form
    {
        private readonly BindingList<EpbRow> _epbRows = new();
        private readonly GlobalConfig _cfg; // 全部配置对象
        private List<PressureSettingControl> _pressureSettings; // 压力设置
        public FormLoggerAdapter logger;
        private const int MaxErrors = 2000;
        private const int MaxInfos = 2000;
        private const int MaxWarns = 2000;
        private ConcurrentQueue<string> LogInformation = new();
        private ConcurrentQueue<string> LogWarn = new();
        private ConcurrentQueue<string> LogError = new();
        private ConcurrentQueue<byte[]> readyReadBuffer;

        /// <summary>
        /// 界面是否正在执行耗时操作（扫描目录/切换项目/保存配置）。
        /// 用于防止重复点击导致重入与卡死。
        /// </summary>
        private int _busy;

        /// <summary>
        /// 扫描项目列表的取消令牌源（切换存储路径时取消上一次扫描）。
        /// </summary>
        private CancellationTokenSource _scanCts;

        /// <summary>
        /// 记录试验名称获得焦点时的原始内容，
        /// 用于在用户取消切换项目时还原文本。
        /// </summary>
        private string _testNameBeforeEdit;


        /// <summary>
        /// 设置页中“EPB 状态及进度”区域每个通道对应的一组控件。
        /// </summary>
        private sealed class EpbProgressView
        {
            /// <summary>EPB 通道号（1..12）。</summary>
            public int ChannelId { get; set; }

            /// <summary>启用复选框（uiCheckBoxEpbXEnabled）。</summary>
            public UICheckBox CheckBox { get; set; }

            /// <summary>状态灯（uiLightStatusX）。</summary>
            public UILight StatusLight { get; set; }

            /// <summary>进度标签（uiLabelProgressX）。</summary>
            public UILabel ProgressLabel { get; set; }
        }

        /// <summary>
        /// 12 路 EPB 在设置页中的控件映射表。
        /// </summary>
        private EpbProgressView[] _epbProgressViews;

        public FrmTestSetting(GlobalConfig cfg)
        {
            logger = new FormLoggerAdapter(MaxInfos, MaxWarns, MaxErrors,
                LogInformation, LogWarn, LogError, this);
            // 主窗体已经恢复最后项目时复用同一个配置对象；独立打开时再重新加载。
            _cfg = cfg ?? ConfigLoader.LoadAll($@"{Environment.CurrentDirectory}\Config", logger);

            
            _pressureSettings = new List<PressureSettingControl>(); // 初始化
            InitializeComponent();
            InitializePressureCalibrationPage();
        }

        /// <summary>
        /// 在设置界面加载时，基于“软件默认 TestConfig.xml”
        /// 初始化 / 加载当前项目的 TestConfig：
        /// <list type="number">
        ///     <item>1. 使用当前 <see cref="_cfg.Test"/>（默认配置）中的 StoreDir + TestName 推导项目路径；</item>
        ///     <item>2. 调用 <see cref="ConfigLoader.EnsureProjectTestConfig"/>：
        ///         若项目 Config\TestConfig.xml 不存在，则由默认配置复制并清零进度；</item>
        ///     <item>3. 始终以“项目 TestConfig” 覆盖 <see cref="_cfg.Test"/>；</item>
        ///     <item>4. 再调用 <see cref="ConfigLoader.UpdateDefaultTestFromProject"/>，
        ///         用项目配置刷新“软件默认 TestConfig.xml”（仅同步 Basic + TotalCount，
        ///         进度清零，两日期置为当前时间）。</item>
        /// </list>
        /// </summary>
        private void InitializeProjectConfigOnLoad()
        {
            if (_cfg == null || _cfg.Test == null)
                return;

            // 1) 基于当前 _cfg（视为从默认 Config 目录加载）确保项目 TestConfig 存在并返回
            var projectTest = ConfigLoader.EnsureProjectTestConfig(_cfg, logger);

            // 2) 用项目配置刷新默认配置（模板：只同步 Basic + TotalCount，进度清零，日期更新）
            ConfigLoader.UpdateDefaultTestFromProject(projectTest, logger);

            // 3) 当前窗体后续一律使用“项目配置”
            _cfg.Test = projectTest;
        }

        #region EPB 状态及进度面板

        /// <summary>
        /// 初始化 1..12 通道在设置页中的控件映射，
        /// 并给启用勾选框挂上颜色联动事件。
        /// </summary>
        private void InitializeEpbProgressViews()
        {
            _epbProgressViews = new[]
            {
            new EpbProgressView { ChannelId = 1,  CheckBox = uiCheckBoxEpb1Enabled,  StatusLight = uiLightStatus1,  ProgressLabel = uiLabelProgress1 },
            new EpbProgressView { ChannelId = 2,  CheckBox = uiCheckBoxEpb2Enabled,  StatusLight = uiLightStatus2,  ProgressLabel = uiLabelProgress2 },
            new EpbProgressView { ChannelId = 3,  CheckBox = uiCheckBoxEpb3Enabled,  StatusLight = uiLightStatus3,  ProgressLabel = uiLabelProgress3 },
            new EpbProgressView { ChannelId = 4,  CheckBox = uiCheckBoxEpb4Enabled,  StatusLight = uiLightStatus4,  ProgressLabel = uiLabelProgress4 },
            new EpbProgressView { ChannelId = 5,  CheckBox = uiCheckBoxEpb5Enabled,  StatusLight = uiLightStatus5,  ProgressLabel = uiLabelProgress5 },
            new EpbProgressView { ChannelId = 6,  CheckBox = uiCheckBoxEpb6Enabled,  StatusLight = uiLightStatus6,  ProgressLabel = uiLabelProgress6 },
            new EpbProgressView { ChannelId = 7,  CheckBox = uiCheckBoxEpb7Enabled,  StatusLight = uiLightStatus7,  ProgressLabel = uiLabelProgress7 },
            new EpbProgressView { ChannelId = 8,  CheckBox = uiCheckBoxEpb8Enabled,  StatusLight = uiLightStatus8,  ProgressLabel = uiLabelProgress8 },
            new EpbProgressView { ChannelId = 9,  CheckBox = uiCheckBoxEpb9Enabled,  StatusLight = uiLightStatus9,  ProgressLabel = uiLabelProgress9 },
            new EpbProgressView { ChannelId = 10, CheckBox = uiCheckBoxEpb10Enabled, StatusLight = uiLightStatus10, ProgressLabel = uiLabelProgress10 },
            new EpbProgressView { ChannelId = 11, CheckBox = uiCheckBoxEpb11Enabled, StatusLight = uiLightStatus11, ProgressLabel = uiLabelProgress11 },
            new EpbProgressView { ChannelId = 12, CheckBox = uiCheckBoxEpb12Enabled, StatusLight = uiLightStatus12, ProgressLabel = uiLabelProgress12 }
        };

            // 给所有启用复选框加颜色联动事件
            foreach (var v in _epbProgressViews)
            {
                if (v.CheckBox == null) continue;

                // 先移除一次，防止重复订阅
                v.CheckBox.CheckedChanged -= UiCheckBoxEpbEnabled_CheckedChanged;
                v.CheckBox.CheckedChanged += UiCheckBoxEpbEnabled_CheckedChanged;
            }
        }

        /// <summary>
        /// 启用复选框勾选状态改变时：仅负责切换 CheckBox 的颜色，
        /// （可选）顺便把状态写回 _cfg.Test.EpbRecords。
        /// </summary>
        private void UiCheckBoxEpbEnabled_CheckedChanged(object sender, EventArgs e)
        {
            if (sender is not UICheckBox cb)
                return;

            // 1) 更新颜色
            UpdateEpbEnabledCheckColor(cb);

            // 2) 可选：把启用状态写回配置
            if (_cfg?.Test == null || _epbProgressViews == null) return;

            var view = _epbProgressViews.FirstOrDefault(v => v.CheckBox == cb);
            if (view == null) return;

            var rec = _cfg.Test.GetEpbRecord(view.ChannelId);
            if (rec != null)
            {
                rec.Enabled = cb.Checked;
            }
        }

        /// <summary>
        /// 根据勾选状态修改 CheckBox 的颜色：
        /// 勾选=绿色；未勾选=灰色。
        /// </summary>
        private static void UpdateEpbEnabledCheckColor(UICheckBox cb)
        {
            if (cb == null) return;

            cb.CheckBoxColor = cb.Checked
                ? Color.FromArgb(110, 190, 40)   // 绿色（和状态灯保持一致风格）
                : Color.FromArgb(140, 140, 140); // 灰色
        }

        /// <summary>
        /// 使用当前 _cfg.Test.EpbRecords 刷新“EPB 状态及进度”面板。
        /// </summary>
        private void RefreshEpbProgressViewsFromConfig()
        {
            if (_cfg?.Test == null || _epbProgressViews == null)
                return;

            // 确保 1..12 记录存在
            _cfg.Test.EnsureEpbRecords(12);

            foreach (var view in _epbProgressViews)
            {
                var rec = _cfg.Test.GetEpbRecord(view.ChannelId);
                if (rec == null) continue;

                ApplyEpbRecordToProgressView(rec, view);
            }
        }

        /// <summary>
        /// 将单个 EPB 通道的记录应用到设置页的 3 个控件：
        /// 启用勾选框 / 状态灯 / 进度标签。
        /// </summary>
        private void ApplyEpbRecordToProgressView(EpbTestRecord record, EpbProgressView view)
        {
            if (record == null || view == null) return;

            // 1) 启用勾选框
            if (view.CheckBox != null)
            {
                // 暂时取消事件，避免赋值时触发回写逻辑
                view.CheckBox.CheckedChanged -= UiCheckBoxEpbEnabled_CheckedChanged;

                view.CheckBox.Checked = record.Enabled;
                UpdateEpbEnabledCheckColor(view.CheckBox);

                // 恢复事件
                view.CheckBox.CheckedChanged += UiCheckBoxEpbEnabled_CheckedChanged;
            }

            // 2) 进度标签：格式 "已运行/剩余"，例如 "10/90"
            if (view.ProgressLabel != null)
            {
                // 计算总目标次数：
                // 优先用每通道单独的 TotalCount；
                // 若为 0 并且启用“所有 EPB 共用 TestTarget”，则回退到 TestTarget。
                int total = record.TotalCount;
                if (total <= 0 && _cfg.Test.IsSameCycleForAllEpb)
                {
                    total = _cfg.Test.TestTarget;
                }

                if (total < 0) total = 0;
                var completed = record.EffectiveMechanicalCycleCount;
                int left = (int)Math.Max(0L, total - completed);

                view.ProgressLabel.Text = $"{completed}/{left}";
            }

            // 3) 状态灯：参考 FrmEpbMainMonitor.UpdateEpbSummaryPanel
            if (view.StatusLight != null)
            {
                UpdateEpbStatusLight(view.StatusLight, record.Status);
            }
        }

        /// <summary>
        /// 按 EPB 试验状态更新状态灯外观。
        /// 逻辑与 FrmEpbMainMonitor.UpdateEpbSummaryPanel 中的 uiLightStatus 保持一致：
        /// Running  = 绿闪；Alarm = 红闪；Completed = 蓝常亮；其他 = 灰常灭。
        /// </summary>
        private static void UpdateEpbStatusLight(UILight light, EpbTestStatus status)
        {
            if (light == null) return;

            switch (status)
            {
                case EpbTestStatus.Running:
                    light.OnCenterColor = Color.LimeGreen;
                    light.OnColor = Color.LimeGreen;
                    light.State = UILightState.Blink;
                    break;

                case EpbTestStatus.Alarm:
                    light.OnCenterColor = Color.Red;
                    light.OnColor = Color.Red;
                    light.State = UILightState.Blink;
                    break;

                case EpbTestStatus.Completed:
                    light.OnCenterColor = Color.DodgerBlue;
                    light.OnColor = Color.DodgerBlue;
                    light.State = UILightState.On;
                    break;

                default:
                    light.OnCenterColor = Color.Gray;
                    light.OnColor = Color.Gray;
                    light.State = UILightState.Off;
                    break;
            }
        }

        #endregion

        /// <summary>
        /// 设置页面忙碌状态：禁用关键输入，避免重入；
        /// 只在 UI 线程调用。
        /// </summary>
        private void SetBusy(bool busy)
        {
            BtnSaveTest.Enabled = !busy;
            BtnFindDir.Enabled = !busy;
            uiButtonResetEpbRecord.Enabled = !busy;
            TxtTestName.Enabled = !busy;
            TxtStoreDir.Enabled = !busy;

            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        }




        /// <summary>
        /// 设置窗口加载时：
        /// 1) 使用当前 _cfg.Test 填充界面；
        /// 2) 根据 _cfg.Test.TestName + StoreDir 计算项目路径，
        ///    如果该项目下还没有 Config\TestConfig.xml，则从默认 Config 拷贝一份过去。
        /// </summary>
        private async void FrmTestSetting_Load(object sender, EventArgs e)
        {
            // // 1) 通过 MdiParent 拿到父窗体引用
            // if (this.MdiParent is Main_Frm main)
            // {
            //     // 2) 访问父窗体的 public 属性
            //     _cfg = main.Cfg;
            // }
            // // else
            // // {
            // //     // 不是 MDI 子窗体或父窗体类型不对
            // // }

            // ★★ 第一步：先建立“默认配置 ↔ 项目配置”的关系，并切换到项目配置 ★★
            InitializeProjectConfigOnLoad();

            // 之后，_cfg.Test 已经是“当前项目的 TestConfig”
            BindEpbRunnerGridFromConfig();

            try
            {
                // DAQ AI
                LoadDaqAiToGridView(Environment.CurrentDirectory + @"\Config\AIConfig.xml");

                // 气缸压力输出校正
                LoadPressureCalibrationConfiguration();


                #region 和试验配置导入刷新有关

                // ★★ 用“项目配置”填充基本信息 UI ★★
                LoadTestConfigFromXml(TxtTestCycle, TxtTestName, TxtTestTarget,
                    uiCheckBoxIsSameCycleForAllEpb, TxtStoreDir, TxtTestMan, RtbDesc);

                // ★★ 用“项目配置”填充基本信息 UI ★★
                LoadTestConfigFromXml(TxtTestCycle, TxtTestName, TxtTestTarget,
                    uiCheckBoxIsSameCycleForAllEpb, TxtStoreDir, TxtTestMan, RtbDesc);

                // ==== 刷新试验名称下拉框列表（扫描当前存储路径下已有项目） ==== //
                await RefreshTestNameComboItemsAsync();

                // 绑定试验名称输入框的 Enter / Leave 事件：
                // Enter：记录原始名称；
                // Leave：校验非法字符 + 检测重名项目并提示是否切换。
                TxtTestName.Enter -= TxtTestName_Enter;
                TxtTestName.Enter += TxtTestName_Enter;
                TxtTestName.Leave -= TxtTestName_Leave;
                TxtTestName.Leave += TxtTestName_Leave;

                #endregion

                // 压力设置相关-开始
                InitializePressureSettings();



                // 压力设置相关-开始
                InitializePressureSettings();

                // 压力设置导入到界面
                LoadDefaultValues(); // 从cfg配置中导入；

                // —— 初始化并刷新“EPB 状态及进度”面板 —— //
                InitializeEpbProgressViews();
                RefreshEpbProgressViewsFromConfig();


            }

            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }


        private void BindEpbRunnerGridFromConfig()
        {
            _epbRows.Clear();

            // —— 直接使用 ConfigLoader 解析好的结果 —— //
            var dict = _cfg?.Test?.EpbCycleRunner?.Channels;
            if (dict == null) return;

            // 通道 1..12 都填一行（缺失时给空默认）
            for (var ch = 1; ch <= 12; ch++)
            {
                dict.TryGetValue(ch, out var r);

                // 新增：从 EpbRecords 里拿该通道的目标次数
                int targetTotal = 0;
                if (_cfg?.Test != null)
                {
                    // 确保记录存在
                    var rec = _cfg.Test.GetEpbRecord(ch); // TestConfig.GetEpbRecord(...) 已经有实现:contentReference[oaicite:1]{index=1}
                    if (rec != null)
                        targetTotal = rec.TotalCount;

                    // 若你希望“所有 EPB 共用 TestTarget” 时，初始显示统一值，可以加这一句：
                    if (targetTotal <= 0 && _cfg.Test.IsSameCycleForAllEpb)
                        targetTotal = _cfg.Test.TestTarget;
                }

                _epbRows.Add(new EpbRow
                {
                    Channel = ch,
                    Name = r != null ? r.Name : $"EPB{ch}",
                    ForwardA = r?.ForwardA ?? 0,
                    SafetyMarginA = r?.SafetyMarginA ?? 0,
                    FwdOnLimitMs = r?.FwdOnLimitMs ?? 0,
                    HoldMs = r?.HoldMs ?? 0,
                    RevDecayLimitA = r?.RevDecayLimitA ?? 0,
                    RevDecayRigidMaxMs = r?.RevDecayRigidMaxMs ?? 0,
                    RevEmptyFixedMs = r?.RevEmptyFixedMs ?? 0,
                    PreReleaseKeepMs = r?.PreReleaseKeepMs,
                    PreReleaseDetectTimeoutMs = r?.PreReleaseDetectTimeoutMs,
                    PeakIgnoreMs = r?.PeakIgnoreMs ?? 0,
                    ForwardProgressConfirmMs = r?.ForwardProgressConfirmMs ?? 1000,
                    ForwardMinimumRiseSlopeAperMs = r?.ForwardMinimumRiseSlopeAperMs ?? 0.001,
                    ForwardProgressDeadlineMs = r?.ForwardProgressDeadlineMs ?? 5000,
                    ReverseProgressConfirmMs = r?.ReverseProgressConfirmMs ?? 200,
                    ReverseMinimumDecaySlopeAperMs = r?.ReverseMinimumDecaySlopeAperMs ?? 0.001,
                    ReverseProgressDeadlineMs = r?.ReverseProgressDeadlineMs ?? 2500,
                    OffCurrentClearThresholdA = r?.OffCurrentClearThresholdA ?? 0.1,
                    OffCurrentClearTimeoutMs = r?.OffCurrentClearTimeoutMs ?? 1000,
                    // ★ 额外：目标次数
                    TargetTotalCount = targetTotal
                });
            }

            dgvEpbRunnerCfgControl.AutoGenerateColumns = false;
            dgvEpbRunnerCfgControl.DataSource = _epbRows;


            // 仅首次构造列
            if (dgvEpbRunnerCfgControl.Columns.Count == 0)
            {
                // 工具：快速添加文本列
                DataGridViewTextBoxColumn AddCol(string dataProperty, string header, int width = 90,
                    bool readOnly = false)
                {
                    var c = new DataGridViewTextBoxColumn
                    {
                        DataPropertyName = dataProperty,
                        HeaderText = header,
                        //Width = width, // 暂时注释宽度
                        ReadOnly = readOnly,
                        SortMode = DataGridViewColumnSortMode.NotSortable
                        //AutoSizeMode = DataGridViewAutoSizeColumnMode.None
                    };
                    dgvEpbRunnerCfgControl.Columns.Add(c);
                    return c;
                }

                AddCol(nameof(EpbRow.Channel), "通道", 80, true);
                AddCol(nameof(EpbRow.Name), "名称", 125);
                // ★ 新增一列：目标次数
                AddCol(nameof(EpbRow.TargetTotalCount), "目标次数", 120);
                AddCol(nameof(EpbRow.ForwardA), "夹紧目标(A)", 187);
                AddCol(nameof(EpbRow.SafetyMarginA), "首次提前量(A)", 237);
                AddCol(nameof(EpbRow.FwdOnLimitMs), "正上限时长(ms)", 249);
                AddCol(nameof(EpbRow.HoldMs), "夹紧保持(ms)", 212);
                AddCol(nameof(EpbRow.RevDecayLimitA), "反向衰减限(A)", 216);
                AddCol(nameof(EpbRow.RevDecayRigidMaxMs), "反衰限制时长(ms)", 287);
                AddCol(nameof(EpbRow.RevEmptyFixedMs), "反向固定空行程(ms)", 311);
                AddCol(nameof(EpbRow.PreReleaseKeepMs), "预释放维持(ms)", 233);
                AddCol(nameof(EpbRow.PreReleaseDetectTimeoutMs), "预释放判定超时(ms)", 270);
                AddCol(nameof(EpbRow.PeakIgnoreMs), "峰值忽略(ms)", 212);


                // 整体按内容自动调整列宽
                dgvEpbRunnerCfgControl.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells;

                dgvEpbRunnerCfgControl.RowHeadersWidth = 40;
                dgvEpbRunnerCfgControl.AllowUserToAddRows = false;
                dgvEpbRunnerCfgControl.AllowUserToDeleteRows = false;
                dgvEpbRunnerCfgControl.AllowUserToResizeRows = false;
                dgvEpbRunnerCfgControl.MultiSelect = false;
                dgvEpbRunnerCfgControl.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
                dgvEpbRunnerCfgControl.EditMode = DataGridViewEditMode.EditOnEnter;

                // —— 设置表头与内容居中 —— //
                dgvEpbRunnerCfgControl.ColumnHeadersDefaultCellStyle.Alignment =
                    DataGridViewContentAlignment.MiddleCenter;
                dgvEpbRunnerCfgControl.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            }
        }

        private void BindEpbRunnerGridFromConfigNew()
        {
            _epbRows.Clear();

            var dict = _cfg?.Test?.EpbCycleRunner?.Channels;
            if (dict == null) return;

            for (var ch = 1; ch <= 12; ch++)
            {
                dict.TryGetValue(ch, out var r);

                _epbRows.Add(new EpbRow
                {
                    Channel = ch,
                    Name = r != null ? r.Name : $"EPB{ch}",
                    ForwardA = r?.ForwardA ?? 0,
                    SafetyMarginA = r?.SafetyMarginA ?? 0,
                    FwdOnLimitMs = r?.FwdOnLimitMs ?? 0,
                    HoldMs = r?.HoldMs ?? 0,
                    RevDecayLimitA = r?.RevDecayLimitA ?? 0,
                    RevDecayRigidMaxMs = r?.RevDecayRigidMaxMs ?? 0,
                    RevEmptyFixedMs = r?.RevEmptyFixedMs ?? 0,
                    PreReleaseKeepMs = r?.PreReleaseKeepMs,
                    PreReleaseDetectTimeoutMs = r?.PreReleaseDetectTimeoutMs,
                    PeakIgnoreMs = r?.PeakIgnoreMs ?? 0,
                    ForwardProgressConfirmMs = r?.ForwardProgressConfirmMs ?? 1000,
                    ForwardMinimumRiseSlopeAperMs = r?.ForwardMinimumRiseSlopeAperMs ?? 0.001,
                    ForwardProgressDeadlineMs = r?.ForwardProgressDeadlineMs ?? 5000,
                    ReverseProgressConfirmMs = r?.ReverseProgressConfirmMs ?? 200,
                    ReverseMinimumDecaySlopeAperMs = r?.ReverseMinimumDecaySlopeAperMs ?? 0.001,
                    ReverseProgressDeadlineMs = r?.ReverseProgressDeadlineMs ?? 2500,
                    OffCurrentClearThresholdA = r?.OffCurrentClearThresholdA ?? 0.1,
                    OffCurrentClearTimeoutMs = r?.OffCurrentClearTimeoutMs ?? 1000
                });
            }

            dgvEpbRunnerCfgControl.AutoGenerateColumns = false;
            dgvEpbRunnerCfgControl.DataSource = _epbRows;

            if (dgvEpbRunnerCfgControl.Columns.Count == 0)
            {
                // 整体按比例填充，而不是固定像素
                dgvEpbRunnerCfgControl.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;

                /// <summary>
                /// 按“比例”添加一列，而不是固定像素。
                /// </summary>
                /// <param name="dataProperty">绑定的属性名（EpbRow.xx）。</param>
                /// <param name="header">列头文本。</param>
                /// <param name="fillWeight">
                /// 列的相对宽度权重。所有列的 FillWeight 之和为 100% 的宽度，
                /// 值越大，该列占用的宽度比例越大。
                /// </param>
                /// <param name="readOnly">是否只读。</param>
                /// <returns>创建好的列对象。</returns>
                DataGridViewTextBoxColumn AddCol(string dataProperty, string header, float fillWeight,
                    bool readOnly = false)
                {
                    var c = new DataGridViewTextBoxColumn
                    {
                        DataPropertyName = dataProperty,
                        HeaderText = header,
                        ReadOnly = readOnly,
                        SortMode = DataGridViewColumnSortMode.NotSortable,

                        // 关键：使用 Fill 模式 + FillWeight
                        AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                        FillWeight = fillWeight
                    };
                    dgvEpbRunnerCfgControl.Columns.Add(c);
                    return c;
                }

                // 下面这些 FillWeight 是按照你原来像素宽度的比例换算出来的（大概等比例）
                AddCol(nameof(EpbRow.Channel), "通道", 34f, true);
                AddCol(nameof(EpbRow.Name), "名称", 53f);
                AddCol(nameof(EpbRow.ForwardA), "夹紧目标(A)", 80f);
                AddCol(nameof(EpbRow.SafetyMarginA), "首次提前量(A)", 101f);
                AddCol(nameof(EpbRow.FwdOnLimitMs), "正上限时长(ms)", 106f);
                AddCol(nameof(EpbRow.HoldMs), "夹紧保持(ms)", 90f);
                AddCol(nameof(EpbRow.RevDecayLimitA), "反向衰减限(A)", 92f);
                AddCol(nameof(EpbRow.RevDecayRigidMaxMs), "反衰限制时长(ms)", 122f);
                AddCol(nameof(EpbRow.RevEmptyFixedMs), "反向固定空行程(ms)", 132f);
                AddCol(nameof(EpbRow.PreReleaseKeepMs), "预释放维持(ms)", 99f);
                AddCol(nameof(EpbRow.PreReleaseDetectTimeoutMs), "预释放判定超时(ms)", 118f);
                AddCol(nameof(EpbRow.PeakIgnoreMs), "峰值忽略(ms)", 90f);
                // ★ 新增：每个 EPB 的目标次数
                AddCol(nameof(EpbRow.TargetTotalCount), "目标次数", 80f);

                dgvEpbRunnerCfgControl.RowHeadersWidth = 40;
                dgvEpbRunnerCfgControl.AllowUserToAddRows = false;
                dgvEpbRunnerCfgControl.AllowUserToDeleteRows = false;
                dgvEpbRunnerCfgControl.AllowUserToResizeRows = false;
                dgvEpbRunnerCfgControl.MultiSelect = false;
                dgvEpbRunnerCfgControl.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
                dgvEpbRunnerCfgControl.EditMode = DataGridViewEditMode.EditOnEnter;

                dgvEpbRunnerCfgControl.ColumnHeadersDefaultCellStyle.Alignment =
                    DataGridViewContentAlignment.MiddleCenter;
                dgvEpbRunnerCfgControl.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            }
        }


        public void SaveEMBControlsToXML(DataGridView dgvEmbControl)
        {
            try
            {
                var dt = (DataTable)dgvEmbControl.DataSource;
                if (dt == null || dt.Rows.Count == 0)
                {
                    MessageBox.Show("没有需要保存的数据");
                    return;
                }

                // 验证数据
                var names = new List<string>();
                string[] validDirections = { "FL", "FR", "RL", "RR" };

                foreach (DataRow row in dt.Rows)
                {
                    // 检查空值
                    if (row.ItemArray.Any(f => string.IsNullOrWhiteSpace(f?.ToString())))
                    {
                        MessageBox.Show("所有字段都必须填写完整");
                        return;
                    }

                    // 验证方向有效性
                    var direction = row["方向"].ToString().Trim();
                    if (!validDirections.Contains(direction))
                    {
                        MessageBox.Show($"无效的方向值: {direction}");
                        return;
                    }

                    names.Add(row["名称"].ToString().Trim());
                }

                // 验证名称
                var requiredNames = new HashSet<string> { "EMB1" };

                if (names.Count != 1 ||
                    names.Distinct().Count() != 1 ||
                    !names.All(n => requiredNames.Contains(n)))
                {
                    MessageBox.Show("名称必须包含且仅包含EMB1");
                    return;
                }

                // 构建XML结构
                var root = new XElement("EMBControl",
                    from row in dt.AsEnumerable()
                    select new XElement("EMB",
                        new XElement("名称", row["名称"]),
                        new XElement("型号", row["型号"]),
                        new XElement("产品编号", row["产品编号"]),
                        new XElement("方向", row["方向"])
                    )
                );

                // 保存文件
                var xmlPath = Path.Combine(Environment.CurrentDirectory, @"Config\EMBControl.XML");
                File.WriteAllText(xmlPath, root.ToString());
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存失败: {ex.Message}");
            }
        }


        private void LoadDaqAiToGridView(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    var dt = new DataTable();
                    dt.Columns.Add("序号", typeof(int));
                    dt.Columns.Add("物理通道", typeof(string));
                    dt.Columns.Add("参数名", typeof(string));
                    dt.Columns.Add("单位", typeof(string));
                    dt.Columns.Add("变换斜率", typeof(double));
                    dt.Columns.Add("变换截距", typeof(double));
                    dt.Columns.Add("参数类型", typeof(string));
                    dt.Columns.Add("是否启用", typeof(bool));
                    dt.Columns.Add("零位漂移", typeof(string));

                    var xmlDoc = new XmlDocument();
                    xmlDoc.Load(filePath);
                    var records = xmlDoc.SelectNodes("//Records");

                    foreach (XmlNode record in records)
                    {
                        var row = dt.NewRow();

                        row["序号"] = int.Parse(record["序号"].InnerText);
                        row["物理通道"] = record["物理通道"].InnerText;
                        row["参数名"] = record["参数名"].InnerText;
                        row["单位"] = record["单位"].InnerText;
                        row["变换斜率"] = double.Parse(record["变换斜率"].InnerText);
                        row["变换截距"] = double.Parse(record["变换截距"].InnerText);
                        row["参数类型"] = record["参数类型"].InnerText;
                        row["是否启用"] = int.Parse(record["是否启用"].InnerText) == 1;
                        row["零位漂移"] = record["零位漂移"].InnerText;
                        dt.Rows.Add(row);
                    }

                    dgvDaqAI.DataSource = dt;

                    // 设置参数类型列为 ComboBox 列
                    var comboBoxColumn = new DataGridViewComboBoxColumn();
                    comboBoxColumn.Name = "参数类型";
                    comboBoxColumn.DataPropertyName = "参数类型";
                    comboBoxColumn.HeaderText = "参数类型";
                    comboBoxColumn.Items.Add("电流");
                    comboBoxColumn.Items.Add("管路压力");
                    comboBoxColumn.Items.Add("夹紧力");
                    // 可根据实际情况添加更多参数类型选项
                    var index = dgvDaqAI.Columns["参数类型"].Index;
                    dgvDaqAI.Columns.RemoveAt(index);
                    dgvDaqAI.Columns.Insert(index, comboBoxColumn);

                    // 设置是否启用列为 CheckBox 列
                    var checkBoxColumn = new DataGridViewCheckBoxColumn();
                    checkBoxColumn.Name = "是否启用";
                    checkBoxColumn.DataPropertyName = "是否启用";
                    checkBoxColumn.HeaderText = "是否启用";
                    index = dgvDaqAI.Columns["是否启用"].Index;
                    dgvDaqAI.Columns.RemoveAt(index);
                    dgvDaqAI.Columns.Insert(index, checkBoxColumn);

                    dgvDaqAI.ColumnHeadersHeight = 60;
                    dgvDaqAI.RowTemplate.Height = 60;
                    dgvDaqAI.Columns[0].Width = 120;
                    dgvDaqAI.Columns[1].Width = 200;
                    dgvDaqAI.Columns[2].Width = 240;
                    dgvDaqAI.Columns[3].Width = 120;
                    dgvDaqAI.Columns[4].Width = 180;
                    dgvDaqAI.Columns[5].Width = 180;
                    dgvDaqAI.Columns[6].Width = 180;
                    dgvDaqAI.Columns[7].Width = 180;
                    dgvDaqAI.Columns[8].Width = 180;

                    dgvDaqAI.Columns[0].ReadOnly = true;
                    dgvDaqAI.Columns[1].ReadOnly = true;
                    dgvDaqAI.Columns[8].ReadOnly = true;
                    dgvDaqAI.Columns[8].Visible = false;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("读入DAQ AI配置出错：" + ex.Message);
            }
        }

        private void SaveDaqAIToXML(string filePath)
        {
            // string filePath = "AIConfig.XML";
            try
            {
                var xmlDoc = new XmlDocument();
                var root = xmlDoc.CreateElement("AIConfigDetail");
                xmlDoc.AppendChild(root);

                var dt = (DataTable)dgvDaqAI.DataSource;


                foreach (DataRow row in dt.Rows)
                {
                    var record = xmlDoc.CreateElement("Records");

                    var id = xmlDoc.CreateElement("序号");
                    id.InnerText = row["序号"].ToString();
                    record.AppendChild(id);

                    var physicalChannel = xmlDoc.CreateElement("物理通道");
                    physicalChannel.InnerText = row["物理通道"].ToString();
                    record.AppendChild(physicalChannel);

                    var paramName = xmlDoc.CreateElement("参数名");
                    paramName.InnerText = row["参数名"].ToString();
                    record.AppendChild(paramName);

                    var unit = xmlDoc.CreateElement("单位");
                    unit.InnerText = row["单位"].ToString();
                    record.AppendChild(unit);

                    var slope = xmlDoc.CreateElement("变换斜率");
                    slope.InnerText = row["变换斜率"].ToString();
                    record.AppendChild(slope);

                    var intercept = xmlDoc.CreateElement("变换截距");
                    intercept.InnerText = row["变换截距"].ToString();
                    record.AppendChild(intercept);

                    var paramType = xmlDoc.CreateElement("参数类型");
                    paramType.InnerText = row["参数类型"].ToString();
                    record.AppendChild(paramType);

                    var isEnabled = xmlDoc.CreateElement("是否启用");
                    isEnabled.InnerText = (bool)row["是否启用"] ? "1" : "0";
                    record.AppendChild(isEnabled);

                    var ZeroValue = xmlDoc.CreateElement("零位漂移");
                    ZeroValue.InnerText = row["零位漂移"].ToString();
                    record.AppendChild(ZeroValue);

                    root.AppendChild(record);
                }

                xmlDoc.Save(filePath);

                MessageBox.Show("保存 DAQ AI配置成功，请退出程序，重新进入！");
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存DAQ AI配置出错：" + ex.Message);
            }
        }


        private void BtnSaveDaqAI_Click(object sender, EventArgs e)
        {
            SaveDaqAIToXML(Environment.CurrentDirectory + @"\Config\AIConfig.xml");
        }

        private void BtnSaveTest_ClickOld(object sender, EventArgs e)
        {
            try
            {
                // 1) 写回 Basic 信息（周期、名称、目录、负责人等）到 _cfg
                PushBasicInfoToConfig();

                // ★ 1.5) 写回每个 EPB 的目标次数到 EpbRecords
                PushEpbTargetCountsToConfig();

                // 2) 写回 EPB 循环参数（12 个通道）到 _cfg
                PushEpbCycleRunnerToConfig();

                // 3) 写回液压配置（EPB1-6 和 EPB7-12 的压力设置）到 _cfg
                PushHydraulicSettingsToConfig();

                // 4) 保存 TestConfig.xml（包含 Basic、EpbCycleRunnerConfig、Hydraulics 等全部配置）
                var cfgDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config");
                var testPath = Path.Combine(cfgDir, "TestConfig.xml");
                ConfigLoader.SaveTest(testPath, _cfg.Test);

                XtraMessageBox.Show("保存成功", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                XtraMessageBox.Show(@"保存失败：\r\n" + ex.Message, @"错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        ///     “保存试验配置”按钮点击事件。
        ///     实现的功能：
        ///     <list type="number">
        ///         <item>1. 将界面上的基本信息和 EPB 参数写回到 <see cref="_cfg.Test"/>；</item>
        ///         <item>2. 根据旧/新 TestName + StoreDir 判断是否“切换项目”；</item>
        ///         <item>3. 若新项目路径下已存在配置，提示用户选择：
        ///             <list type="bullet">
        ///                 <item>“是”：切换到已有项目（加载其 TestConfig 和运行进度）；</item>
        ///                 <item>“否”：以当前界面设置覆盖保存到该项目（视为重命名或新建，EPB 进度会清零）。</item>
        ///             </list>
        ///         </item>
        ///         <item>4. 始终将当前项目配置保存到“项目 Config\TestConfig.xml”；</item>
        ///         <item>5. 同步更新“软件默认 Config\TestConfig.xml”，
        ///             其中仅同步 TotalCount，其他进度保持为“全新的模板”。</item>
        ///     </list>
        /// </summary>
        private void BtnSaveTest_ClickOld2(object sender, EventArgs e)
        {
            try
            {
                if (_cfg?.Test == null)
                {
                    MessageBox.Show(@"内部配置对象为空，无法保存试验配置。", @"错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // —— 1) 保存前先记录“旧项目名”和“旧存储路径”，用于判断是否切换项目 —— //
                var oldTestName = _cfg.Test.TestName;
                var oldStoreDir = _cfg.Test.StoreDir;

                // —— 2) 将界面上的 Basic / EPB 相关参数回写到 _cfg.Test —— //
                PushBasicInfoToConfig();   // TestName / TestCycle / StoreDir / Owner / Description ...
                PushEpbCycleRunnerToConfig();    // EpbCycleRunner + 每通道目标次数（写到 EpbRecords.TotalCount）

                // —— 3) 计算新项目的路径（根目录 + Config + TestConfig.xml） —— //
                var newStoreDir = _cfg.Test.StoreDir;
                var newTestName = _cfg.Test.TestName;

                var projectRoot = ConfigLoader.GetProjectRootDir(newStoreDir, newTestName);
                var projectConfigDir = ConfigLoader.GetProjectConfigDir(newStoreDir, newTestName);
                var projectTestPath = ConfigLoader.GetProjectTestConfigPath(newStoreDir, newTestName);

                if (string.IsNullOrEmpty(projectRoot) || string.IsNullOrEmpty(projectConfigDir) ||
                    string.IsNullOrEmpty(projectTestPath))
                {
                    MessageBox.Show(@"StoreDir 或 TestName 为空，无法生成项目 Config 路径，请检查输入。", @"错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // —— 4) 判断项目是否发生变化（项目名或存储路径） —— //
                var projectChanged =
                    !string.Equals(oldTestName, newTestName, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(oldStoreDir, newStoreDir, StringComparison.OrdinalIgnoreCase);

                if (projectChanged && File.Exists(projectTestPath))
                {
                    // ===============================
                    // 情况 A：改了项目名/路径，且新路径下“已经存在配置”
                    // ===============================
                    var msg =
                        $"检测到目标项目路径下已存在配置文件：\r\n{projectTestPath}\r\n\r\n" +
                        "你可以选择：\r\n" +
                        "【是】→ 切换到该项目（加载该项目原有的 TestConfig 和运行进度）；\r\n" +
                        "【否】→ 以当前界面设置覆盖保存到该项目（视为重命名或新建，EPB 进度将被清零）。";

                    var dr = MessageBox.Show(msg, @"项目已存在", MessageBoxButtons.YesNoCancel,
                        MessageBoxIcon.Question);

                    if (dr == DialogResult.Cancel)
                        return;

                    if (dr == DialogResult.Yes)
                    {
                        // —— A1：切换到已有项目 —— //
                        var loaded = ConfigLoader.LoadTest(projectTestPath,logger);
                        _cfg.Test = loaded;

                        // 同步默认配置（只带 TotalCount，进度清零）
                        ConfigLoader.UpdateDefaultTestFromProject(_cfg.Test);

                        // 刷新界面显示
                        LoadTestConfigFromXml(TxtTestCycle, TxtTestName, TxtTestTarget,
                            uiCheckBoxIsSameCycleForAllEpb, TxtStoreDir, TxtTestMan, RtbDesc);
                        BindEpbRunnerGridFromConfig();

                        MessageBox.Show(@"已切换到已有项目配置。", @"提示",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    // —— A2：DialogResult.No → 视为“重命名/新建项目”，以当前界面设置覆盖保存到该项目 —— //
                    _cfg.Test.EnsureEpbRecords();
                    foreach (var rec in _cfg.Test.EpbRecords)
                    {
                        // 清零运行进度，但保留目标次数 TotalCount
                        rec.ResetKeepTotalCount();
                    }
                }
                else
                {
                    // ===============================
                    // 情况 B：未改路径 / 新路径下尚无配置
                    // ===============================
                    if (projectChanged)
                    {
                        // 新项目：需要从旧项目“干干净净”开始，清零运行进度
                        _cfg.Test.EnsureEpbRecords();
                        foreach (var rec in _cfg.Test.EpbRecords)
                        {
                            rec.ResetKeepTotalCount();
                        }
                    }
                    // 若 projectChanged == false：继续当前项目，不清零进度，直接保存即可。
                }

                // —— 5) 保存当前项目 TestConfig 到“项目 Config\TestConfig.xml” —— //
                if (!Directory.Exists(projectConfigDir))
                    Directory.CreateDirectory(projectConfigDir);

                ConfigLoader.SaveTest(projectTestPath, _cfg.Test);

                // —— 6) 同步更新“软件默认 Config\TestConfig.xml”（仅 TotalCount，进度清零） —— //
                ConfigLoader.UpdateDefaultTestFromProject(_cfg.Test);

                MessageBox.Show(@"保存成功！", @"提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(@"保存试验配置失败：" + ex.Message, @"错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// 保存试验配置按钮：
        /// <list type="number">
        ///     <item>1) 校验试验名称（非空、无非法路径字符）。</item>
        ///     <item>2) 将界面输入写回到 <see cref="_cfg.Test"/>。</item>
        ///     <item>3) 判断是否切换到新项目（StoreDir/TestName 变化则清零运行进度，仅保留 TotalCount）。</item>
        ///     <item>4) 后台线程执行耗时 IO：确保项目配置文件存在 → 保存项目配置 → 同步更新默认模板配置。</item>
        ///     <item>5) 回到 UI 线程刷新“EPB 状态及进度”面板。</item>
        /// </list>
        /// </summary>
        private async void BtnSaveTest_Click(object sender, EventArgs e)
        {
           

            // —— 防重入（避免连点/重入导致重复保存、UI 卡死） —— //
            if (System.Threading.Interlocked.Exchange(ref _busy, 1) == 1)
                return;

            try
            {
                SetBusy(true);

                if (_cfg?.Test == null)
                {
                    XtraMessageBox.Show(@"当前试验配置为空，无法保存！", @"错误",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                #region 0.5) 校验试验名称不能为空且合法

                var testNameText = (TxtTestName.Text ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(testNameText))
                {
                    XtraMessageBox.Show(
                        @"试验名称不能为空，请输入新项目名称或从下拉列表选择已有项目。",
                        @"提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);

                    TxtTestName.Focus();
                    TxtTestName.SelectAll();
                    return;
                }

                if (testNameText.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    XtraMessageBox.Show(
                        @"试验名称中包含 Windows 不允许的字符，请重新输入。",
                        @"试验名称非法",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);

                    TxtTestName.Focus();
                    TxtTestName.SelectAll();
                    return;
                }

                #endregion

                // —— 0) 保存前先记住原来的项目标识（用于判断是否“切换到新项目”） —— //
                var oldStoreDir = _cfg.Test.StoreDir ?? string.Empty;
                var oldTestName = _cfg.Test.TestName ?? string.Empty;

                // —— 1) 写回 Basic 信息（周期、名称、目录、负责人等）到 _cfg —— //
                // 注意：这些操作访问 UI 控件，必须在 UI 线程执行
                PushBasicInfoToConfig();

                // —— 1.5) 写回每个 EPB 的目标次数到 EpbRecords —— //
                PushEpbTargetCountsToConfig();

                // —— 2) 写回 EPB 循环参数（12 个通道）到 _cfg —— //
                PushEpbCycleRunnerToConfig();

                // —— 3) 写回液压配置（EPB1-6 和 EPB7-12 的压力设置）到 _cfg —— //
                PushHydraulicSettingsToConfig();

                // —— 4) 判断是否“项目名 / 存储路径”发生了变化 —— //
                var newStoreDir = _cfg.Test.StoreDir ?? string.Empty;
                var newTestName = _cfg.Test.TestName ?? string.Empty;

                var isProjectChanged =
                    !string.Equals(oldStoreDir, newStoreDir, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(oldTestName, newTestName, StringComparison.OrdinalIgnoreCase);

                if (isProjectChanged)
                {
                    var targetRoot = ConfigLoader.GetProjectRootDir(newStoreDir, newTestName);
                    var targetConfig = ConfigLoader.GetProjectTestConfigPath(newStoreDir, newTestName);
                    if (File.Exists(targetConfig))
                        throw new IOException(
                            $"目标项目已存在，请先通过试验名称下拉框切换后再保存：{targetConfig}");
                    if (Directory.Exists(targetRoot))
                        throw new IOException(
                            $"目标目录已存在但缺少有效 TestConfig.xml，请先处理残留目录：{targetRoot}");

                    // Keep the source project's identity stable while the helper builds
                    // and validates a separate candidate configuration.
                    _cfg.Test.StoreDir = oldStoreDir;
                    _cfg.Test.TestName = oldTestName;
                    var oldProjectConfig =
                        ConfigLoader.GetProjectTestConfigPath(oldStoreDir, oldTestName);
                    var templatePath = File.Exists(oldProjectConfig)
                        ? oldProjectConfig
                        : GetDefaultTestConfigPath();
                    var source = _cfg.Test;

                    var candidate = await Task.Run(() =>
                        ConfigLoader.CreateNewProjectTestConfig(
                            source,
                            templatePath,
                            newStoreDir,
                            newTestName,
                            logger));

                    _cfg.Test = candidate;
                    ConfigLoader.UpdateDefaultTestFromProject(candidate, logger);
                    RememberCurrentProjectOrWarn();
                    LoadCurrentProjectIntoUi();
                    await RefreshTestNameComboItemsAsync();
                    XtraMessageBox.Show(
                        "新项目已创建并保存；12 路 EPB 均为未勾选、未启动状态。",
                        "提示",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                // —— 5/6) 后台线程执行耗时 IO：避免阻塞 UI 线程导致卡死 —— //
                var cfgSnapshot = _cfg.Test; // 捕获引用（同一对象），确保后台保存用的就是当前配置
                await Task.Run(() =>
                {
                    // ★关键：确保项目 TestConfig.xml 文件存在（不存在就从默认模板复制）★
                    // 这个步骤包含磁盘 IO，放到后台线程执行
                    EnsureProjectTestConfigExists();

                    // 先 Ensure 再取路径：新项目名时才不会出现“文件不存在”
                    var projectTestPath = GetProjectTestConfigPath();
                    if (!string.IsNullOrEmpty(projectTestPath))
                    {
                        ConfigLoader.SaveTest(projectTestPath, cfgSnapshot);
                    }

                    // 根据“项目配置”更新“软件默认 Config\TestConfig.xml”（模板）
                    ConfigLoader.UpdateDefaultTestFromProject(cfgSnapshot, logger);
                });

                // —— 7) 保存后刷新“EPB 状态及进度”面板（必须回到 UI 线程） —— //
                // 使设置页中的“EPB 启用/状态灯/已运行/剩余”等信息与最新配置保持一致。
                RefreshEpbProgressViewsFromConfig();
                RememberCurrentProjectOrWarn();

                XtraMessageBox.Show("保存成功", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);

            }
            catch (Exception ex)
            {
                if (_cfg?.Test != null)
                {
                    TxtTestName.Text = _cfg.Test.TestName ?? string.Empty;
                    TxtStoreDir.Text = _cfg.Test.StoreDir ?? string.Empty;
                }
                XtraMessageBox.Show(
                    $"保存试验配置失败：\r\n{ex.Message}",
                    "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false);
                System.Threading.Interlocked.Exchange(ref _busy, 0);
            }
        }




        /// <summary>
        /// 将网格当前数据写回到 _global.Test.EpbCycleRunner.Channels 字典中。
        /// </summary>
        private void PushEpbCycleRunnerToConfig()
        {
            if (_cfg?.Test?.EpbCycleRunner == null) return;

            var dict = _cfg.Test.EpbCycleRunner.Channels;
            dict.Clear();
            foreach (var r in _epbRows.OrderBy(x => x.Channel))
            {
                var item = new EpbCycleRunnerConfig.Record
                {
                    Channel = r.Channel,
                    Name = r.Name,
                    ForwardA = r.ForwardA,
                    SafetyMarginA = r.SafetyMarginA,
                    FwdOnLimitMs = Math.Max(0, r.FwdOnLimitMs),
                    HoldMs = Math.Max(0, r.HoldMs),
                    RevDecayLimitA = r.RevDecayLimitA,
                    RevDecayRigidMaxMs = Math.Max(0, r.RevDecayRigidMaxMs),
                    RevEmptyFixedMs = Math.Max(0, r.RevEmptyFixedMs),
                    PreReleaseKeepMs = r.PreReleaseKeepMs,
                    PreReleaseDetectTimeoutMs = r.PreReleaseDetectTimeoutMs,
                    PeakIgnoreMs = Math.Max(0, r.PeakIgnoreMs),
                    ForwardProgressConfirmMs = Math.Max(20, r.ForwardProgressConfirmMs),
                    ForwardMinimumRiseSlopeAperMs = Math.Max(0.00001, r.ForwardMinimumRiseSlopeAperMs),
                    ForwardProgressDeadlineMs = Math.Max(
                        Math.Max(20, r.ForwardProgressConfirmMs),
                        r.ForwardProgressDeadlineMs),
                    ReverseProgressConfirmMs = Math.Max(20, r.ReverseProgressConfirmMs),
                    ReverseMinimumDecaySlopeAperMs = Math.Max(0.00001, r.ReverseMinimumDecaySlopeAperMs),
                    ReverseProgressDeadlineMs = Math.Max(
                        Math.Max(20, r.ReverseProgressConfirmMs),
                        r.ReverseProgressDeadlineMs),
                    OffCurrentClearThresholdA = Math.Max(0.01, r.OffCurrentClearThresholdA),
                    OffCurrentClearTimeoutMs = Math.Max(1000, r.OffCurrentClearTimeoutMs)
                };
                dict[r.Channel] = item;
            }
        }

        /// <summary>
        /// 将每个 EPB 的目标次数写回到 _cfg.Test.EpbRecords[].TotalCount。
        /// </summary>
        private void PushEpbTargetCountsToConfig()
        {
            if (_cfg?.Test == null) return;

            // 确保 1..12 的记录存在
            _cfg.Test.EnsureEpbRecords(12);

            foreach (var row in _epbRows)
            {
                var rec = _cfg.Test.GetEpbRecord(row.Channel);
                if (rec == null) continue;

                // 防御性：不允许负数
                rec.TotalCount = Math.Max(0, row.TargetTotalCount);
            }
        }



        /// <summary>
        /// 将基本信息写回到配置对象中（带健壮性校验）。
        /// </summary>
        private void PushBasicInfoToConfig()
        {
            if (_cfg?.Test == null) return;

            // —— 1) TestPeriod —— //
            if (int.TryParse(TxtTestCycle.Text.Trim(), out var cycleHz) && cycleHz > 0)
                _cfg.Test.TestPeriod = cycleHz;
            else
                _cfg.Test.TestPeriod = 1; // 安全默认值

            // —— 2) TestName —— //
            _cfg.Test.TestName = (TxtTestName.Text ?? "").Trim();

            // —— 3) TestTarget —— //
            if (int.TryParse(TxtTestTarget.Text.Trim(), out var target) && target > 0)
                _cfg.Test.TestTarget = target;
            else
                _cfg.Test.TestTarget = 1; // 默认 1 次

            // —— 3.5) IsSameCycleForAllEpb —— //

            _cfg.Test.IsSameCycleForAllEpb = uiCheckBoxIsSameCycleForAllEpb.Checked; 

            // —— 4) StoreDir —— //
            var dir = (TxtStoreDir.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(dir) || dir.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                // 如果用户输入的目录不合法，回退到原值或默认路径
                dir = _cfg.Test.StoreDir ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data");
            }

            _cfg.Test.StoreDir = dir;

            // —— 5) Owner —— //
            _cfg.Test.Owner = (TxtTestMan.Text ?? "").Trim();

            // —— 6) Description —— //
            _cfg.Test.Description = RtbDesc.Text ?? "";
        }

        /// <summary>
        /// 获取“软件默认”的 TestConfig.xml 完整路径：
        ///   .\Config\TestConfig.xml
        /// </summary>
        private static string GetDefaultTestConfigPath()
        {
            var cfgDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config");
            // 确保目录存在
            Directory.CreateDirectory(cfgDir);
            return Path.Combine(cfgDir, "TestConfig.xml");
        }

        /// <summary>
        /// 根据当前 _cfg.Test 的 StoreDir + TestName 计算“项目专用”的 TestConfig.xml 路径：
        ///   {StoreDir}\{TestName}\Config\TestConfig.xml
        /// </summary>
        private string GetProjectTestConfigPath()
        {
            if (_cfg?.Test == null)
                return null;

            // —— 存储根目录 —— //
            var storeDir = string.IsNullOrWhiteSpace(_cfg.Test.StoreDir)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data")
                : _cfg.Test.StoreDir.Trim();

            // —— 项目名称 —— //
            var projectName = string.IsNullOrWhiteSpace(_cfg.Test.TestName)
                ? "DefaultProject"
                : _cfg.Test.TestName.Trim();

            var projectRoot = Path.Combine(storeDir, projectName);
            var projectConfigDir = Path.Combine(projectRoot, "Config");

            // 这里就先把目录建好（不存在则创建）
            Directory.CreateDirectory(projectConfigDir);

            return Path.Combine(projectConfigDir, "TestConfig.xml");
        }

        /// <summary>
        /// 根据当前存储路径 <see cref="TxtStoreDir"/>，
        /// 扫描其中所有包含 Config\TestConfig.xml 的子目录，
        /// 并将子目录名称填充到 <see cref="TxtTestName"/> 的下拉列表中。
        /// </summary>
        private void RefreshTestNameComboItems()
        {
            // 先清空原有项
            TxtTestName.Items.Clear();

            var storeDir = (TxtStoreDir.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(storeDir) || !Directory.Exists(storeDir))
                return;

            try
            {
                var dirs = Directory.GetDirectories(storeDir);
                Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);

                foreach (var dir in dirs)
                {
                    var projectName = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(projectName))
                        continue;

                    var configPath = Path.Combine(dir, "Config", "TestConfig.xml");
                    if (File.Exists(configPath))
                    {
                        // Sunny.UI.UIComboBox 继承自 ComboBox，Items 为 object 集合
                        if (!TxtTestName.Items.Contains(projectName))
                        {
                            TxtTestName.Items.Add(projectName);
                        }
                    }
                }
            }
            catch
            {
                // LIST 失败不影响主流程，这里不弹框，避免打扰操作
                // 如需调试可以在此处写日志。
            }
        }

        /// <summary>
        /// 异步刷新试验名称下拉列表：后台线程扫描 {StoreDir}\*\Config\TestConfig.xml，
        /// 扫描完成后回到 UI 线程更新 TxtTestName.Items。
        /// </summary>
        private async Task RefreshTestNameComboItemsAsync()
        {
            var storeDir = (TxtStoreDir.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(storeDir) || !Directory.Exists(storeDir))
            {
                TxtTestName.Items.Clear();
                return;
            }

            // 取消上一轮扫描，避免“换路径后上一轮还在跑”
            if (_scanCts != null)
            {
                try { _scanCts.Cancel(); } catch { /* ignore */ }
                try { _scanCts.Dispose(); } catch { /* ignore */ }
            }
            _scanCts = new CancellationTokenSource();
            var token = _scanCts.Token;

            string[] names;

            try
            {
                // 后台扫描：千万别在 UI 线程做
                names = await Task.Run(() =>
                {
                    var list = new List<string>();

                    // Directory.GetDirectories 在目录巨大/网络盘时非常慢
                    var dirs = Directory.GetDirectories(storeDir);
                    Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);

                    foreach (var dir in dirs)
                    {
                        token.ThrowIfCancellationRequested();

                        var projectName = Path.GetFileName(dir);
                        if (string.IsNullOrEmpty(projectName)) continue;

                        var configPath = Path.Combine(dir, "Config", "TestConfig.xml");
                        if (File.Exists(configPath))
                        {
                            list.Add(projectName);
                        }
                    }

                    return list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                }, token);
            }
            catch (OperationCanceledException)
            {
                return; // 被取消就直接退出
            }
            catch
            {
                // 扫描失败不弹框，避免打扰；必要时你可以 logger.Error(...)
                return;
            }

            // 回到 UI 线程更新控件
            TxtTestName.Items.Clear();
            foreach (var n in names)
                TxtTestName.Items.Add(n);
        }



        /// <summary>
        /// 判断当前存储路径下是否存在指定名称的项目目录。
        /// 条件：{StoreDir}\{testName}\Config\TestConfig.xml 存在。
        /// </summary>
        /// <param name="testName">待检查的试验名称/项目名。</param>
        /// <returns>存在返回 true，否则 false。</returns>
        private bool ProjectExistsInCurrentStoreDir(string testName)
        {
            var name = (testName ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(name))
                return false;

            var storeDir = (TxtStoreDir.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(storeDir) || !Directory.Exists(storeDir))
                return false;

            var projectRoot = Path.Combine(storeDir, name);
            var configPath = Path.Combine(projectRoot, "Config", "TestConfig.xml");
            return File.Exists(configPath);
        }



        /// <summary>
        /// 确保“当前项目”的 Config\TestConfig.xml 存在。
        /// 如果不存在，则从“默认 Config\TestConfig.xml” 拷贝一份作为模板。
        /// 只负责“有 / 没有文件”这一件事，不改动 _cfg。
        /// </summary>
        private void EnsureProjectTestConfigExists()
        {
            var defaultTestPath = GetDefaultTestConfigPath();
            var projectTestPath = GetProjectTestConfigPath();

            if (string.IsNullOrEmpty(projectTestPath))
                return;

            // 目录由 GetProjectTestConfigPath 中保证

            if (!File.Exists(projectTestPath))
            {
                if (!File.Exists(defaultTestPath))
                    throw new FileNotFoundException("默认 TestConfig.xml 不存在。", defaultTestPath);

                // 第一次创建某个项目的配置：直接以默认配置为模板拷贝过去
                File.Copy(defaultTestPath, projectTestPath, overwrite: false);
            }
        }

        /// <summary>
        /// 针对当前 _cfg.Test，将 1..12 通道的“运行进度”清零，只保留 TotalCount（目标次数）。<br/>
        /// 用于：用户修改了 TestName / StoreDir，等于创建一个全新的项目时。
        /// </summary>
        private void ResetEpbRecordsRuntimeStateKeepTotalCount()
        {
            if (_cfg?.Test == null)
                return;

            // 确保 1..12 都有记录
            _cfg.Test.EnsureEpbRecords(12);

            foreach (var rec in _cfg.Test.EpbRecords)
            {
                var total = rec.TotalCount; // 先记住目标次数

                // 清零运行状态
                rec.RunCount = 0;
                rec.Status = EpbTestStatus.NotStarted;
                rec.StartTime = null;
                rec.LatestStartTime = null;
                rec.RunTime = "0";

                // 再把目标次数写回去
                rec.TotalCount = Math.Max(0, total);
            }
        }




        // 统一保存逻辑
        private void SaveTestConfigToFile(TestConfig config)
        {
            var serializer = new XmlSerializer(typeof(TestConfig));

            var xmlPath = Path.Combine(Environment.CurrentDirectory, @"Config\TestConfig.xml");

            using (var writer = new StreamWriter(xmlPath))
            {
                serializer.Serialize(writer, config);
            }
        }

        // 显式控件参数加载方式
        public void LoadTestConfigFromXml(UITextBox txtTestCycle,
            UIComboBox txtTestName,
            UITextBox txtTestTarget,
            UICheckBox checkIsSameCycleForAllEpb,
            UITextBox txtStoreDir,
            UITextBox txtTestMan,
            UIRichTextBox rtbDesc)
        {
            // var xmlPath = Path.Combine(Environment.CurrentDirectory, @"Config\TestConfig.xml");
            //
            //
            // if (!File.Exists(xmlPath)) return;

            txtTestCycle.Text = _cfg.Test.TestPeriod.ToString(CultureInfo.CurrentCulture);
            txtTestName.Text = _cfg.Test.TestName;
            txtTestTarget.Text = _cfg.Test.TestTarget.ToString();
            checkIsSameCycleForAllEpb.Checked = _cfg.Test.IsSameCycleForAllEpb;
            txtStoreDir.Text = _cfg.Test.StoreDir;
            txtTestMan.Text = _cfg.Test.Owner; // TestMan 对应 Owner，测试员、负责人等
            rtbDesc.Text = _cfg.Test.Description; // 描述
        }


        #region 压力设置相关

        private void InitializePressureSettings()
        {
            // 清空列表
            _pressureSettings.Clear();

            // 添加EPB压力设置 (Id=1)
            var epb1To6Pressure = new PressureSettingControl(
                id: 1,
                name: "EPB1-6的压力",
                enableCheckEdit: checkEditPressure1To6,
                pressureTextEdit: textEditPressureValue1To6,
                unit: "bar"
            );
            _pressureSettings.Add(epb1To6Pressure);

            // 添加辅助压力设置 (Id=2)
            var epb7To12Pressure = new PressureSettingControl(
                id: 2,
                name: "EPB7-12的压力",
                enableCheckEdit: checkEditPressure7To12,
                pressureTextEdit: textEditPressureValue7To12,
                unit: "bar"
            );
            _pressureSettings.Add(epb7To12Pressure);

            // 可以继续添加更多压力设置...
            // var thirdPressure = new PressureSettingControl(3, "第三压力", checkEdit3, textEdit3);
            // _pressureSettings.Add(thirdPressure);
        }

        // 加载默认值
        private void LoadDefaultValues()
        {
            try
            {
                // 使用 _cfg.Test.GetHydraulicItemById(id) 获取 HydraulicItem
                // 分别获取id为1和2的HydraulicItem

                // 获取ID为1的HydraulicItem
                var hydraulicItem1 = _cfg.Test.GetHydraulicItemById(1);
                if (hydraulicItem1 != null)
                {
                    var epb1To6Setting = GetPressureSettingById(1);
                    if (epb1To6Setting != null)
                    {
                        // 设置压力值
                        epb1To6Setting.SetPressure(
                            pressure: (int)hydraulicItem1.PressureThresholdBar,
                            enable: hydraulicItem1.Enabled
                        );

                        // 可选：在界面上显示相关信息
                        System.Diagnostics.Debug.WriteLine($"加载EPB1-6压力: ID={hydraulicItem1.Id}, " +
                                                           $"Enabled={hydraulicItem1.Enabled}, " +
                                                           $"Pressure={hydraulicItem1.PressureThresholdBar} bar");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"警告: 未找到ID=1的压力设置控件");
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"警告: 未找到ID=1的HydraulicItem，使用默认值");
                    // 如果没有找到配置，使用硬编码的默认值
                    var epb1To6Setting = GetPressureSettingById(1);
                    if (epb1To6Setting != null)
                    {
                        epb1To6Setting.SetPressure(30, true); // 默认30bar，启用
                    }
                }

                // 获取ID为2的HydraulicItem
                var hydraulicItem2 = _cfg.Test.GetHydraulicItemById(2);
                if (hydraulicItem2 != null)
                {
                    var epb7To12Setting = GetPressureSettingById(2);
                    if (epb7To12Setting != null)
                    {
                        // 设置压力值
                        epb7To12Setting.SetPressure(
                            pressure: hydraulicItem2.PressureThresholdBar,
                            enable: hydraulicItem2.Enabled
                        );

                        // 可选：在界面上显示相关信息
                        System.Diagnostics.Debug.WriteLine($"加载EPB7-12压力: ID={hydraulicItem2.Id}, " +
                                                           $"Enabled={hydraulicItem2.Enabled}, " +
                                                           $"Pressure={hydraulicItem2.PressureThresholdBar} bar");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"警告: 未找到ID=2的压力设置控件");
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"警告: 未找到ID=2的HydraulicItem，使用默认值");
                    // 如果没有找到配置，使用硬编码的默认值
                    var epb7To12Setting = GetPressureSettingById(2);
                    if (epb7To12Setting != null)
                    {
                        epb7To12Setting.SetPressure(15, true); // 默认15bar，启用
                    }
                }

                // 可选：验证加载的数据
                ValidateLoadedData();

                // 更新界面显示
                //DisplayCurrentSettings();
            }
            catch (Exception ex)
            {
                // 记录异常并使用默认值
                System.Diagnostics.Debug.WriteLine($"加载压力设置时发生错误: {ex.Message}");

                // 发生异常时使用硬编码默认值
                UseHardcodedDefaults();
            }
        }

        // 验证加载的数据
        private void ValidateLoadedData()
        {
            var validationRules = new Dictionary<int, (int min, int max)>
            {
                { 1, (0, 200) }, // EPB1-6压力范围 0-200
                { 2, (0, 200) } // EPB7-12压力范围 0-200
            };

            foreach (var setting in _pressureSettings)
            {
                if (setting.IsEnabled)
                {
                    if (validationRules.ContainsKey(setting.Id))
                    {
                        var (min, max) = validationRules[setting.Id];
                        if (!setting.ValidatePressure(min, max))
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"警告: ID={setting.Id}的压力值{setting.PressureValue}超出范围({min}-{max})");

                            // 可选：自动修正到范围内的值
                            if (setting.PressureValue < min)
                            {
                                setting.PressureValue = min;
                            }
                            else if (setting.PressureValue > max)
                            {
                                setting.PressureValue = max;
                            }
                        }
                    }
                }
            }
        }

        // 使用硬编码的默认值
        private void UseHardcodedDefaults()
        {
            foreach (var setting in _pressureSettings)
            {
                switch (setting.Id)
                {
                    case 1:
                        setting.SetPressure(30, true); // EPB1-6默认值
                        break;
                    case 2:
                        setting.SetPressure(15, true); // EPB7-12默认值
                        break;
                    default:
                        setting.SetPressure(0, false); // 其他ID使用默认值
                        break;
                }
            }
        }

        // 批量加载所有HydraulicItem到压力设置
        private void LoadAllHydraulicItems()
        {
            // 这个方法可以用于批量加载所有HydraulicItem
            for (int id = 1; id <= 2; id++) // 假设有2个，可以根据实际情况调整
            {
                var hydraulicItem = _cfg.Test.GetHydraulicItemById(id);
                if (hydraulicItem != null)
                {
                    var pressureSetting = GetPressureSettingById(id);
                    if (pressureSetting != null)
                    {
                        pressureSetting.SetPressure(
                            hydraulicItem.PressureThresholdBar,
                            hydraulicItem.Enabled
                        );
                    }
                }
            }
        }

        // 获取HydraulicItem并更新压力设置的方法
        private bool TryLoadHydraulicItemToPressureSetting(int hydraulicId)
        {
            try
            {
                var hydraulicItem = _cfg.Test.GetHydraulicItemById(hydraulicId);
                if (hydraulicItem == null)
                {
                    System.Diagnostics.Debug.WriteLine($"未找到ID={hydraulicId}的HydraulicItem");
                    return false;
                }

                var pressureSetting = GetPressureSettingById(hydraulicId);
                if (pressureSetting == null)
                {
                    System.Diagnostics.Debug.WriteLine($"未找到ID={hydraulicId}对应的压力设置控件");
                    return false;
                }

                pressureSetting.SetPressure(
                    hydraulicItem.PressureThresholdBar,
                    hydraulicItem.Enabled
                );

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载HydraulicItem ID={hydraulicId}时出错: {ex.Message}");
                return false;
            }
        }

        // 保存当前设置回HydraulicItem；写回_cfg
        private void PushHydraulicSettingsToConfig()
        {
            try
            {
                foreach (var pressureSetting in _pressureSettings)
                {
                    var hydraulicItem = _cfg.Test.GetHydraulicItemById(pressureSetting.Id);
                    if (hydraulicItem != null)
                    {
                        // 更新HydraulicItem的值
                        hydraulicItem.Enabled = pressureSetting.IsEnabled;
                        hydraulicItem.PressureThresholdBar = pressureSetting.PressureValue;

                        System.Diagnostics.Debug.WriteLine($"保存压力设置: ID={pressureSetting.Id}, " +
                                                           $"Enabled={pressureSetting.IsEnabled}, " +
                                                           $"Pressure={pressureSetting.PressureValue} bar");
                    }
                }

                // 可选：调用保存配置的方法
                // _cfg.Test.SaveHydraulicItems();

                /*XtraMessageBox.Show("压力设置保存成功", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);*/
            }
            catch (Exception ex)
            {
                XtraMessageBox.Show($"压力设置，修改失败: {ex.Message}", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        #region 压力相关的辅助方法

        // 通过Id获取压力设置
        private PressureSettingControl GetPressureSettingById(int id)
        {
            return _pressureSettings.FirstOrDefault(p => p.Id == id);
        }

        // 通过Name获取压力设置
        private PressureSettingControl GetPressureSettingByName(string name)
        {
            return _pressureSettings.FirstOrDefault(p => p.Name == name);
        }

        // 获取所有启用的压力设置
        private List<PressureSettingControl> GetEnabledSettings()
        {
            return _pressureSettings.Where(p => p.IsEnabled).ToList();
        }

        // 获取所有有效的压力设置
        private List<PressureSettingControl> GetValidSettings()
        {
            return _pressureSettings.Where(p => p.IsValid).ToList();
        }

        #endregion

        #endregion


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
        
        /// <summary>
        /// “存储路径选择”按钮点击事件：
        /// 1. 让用户选择新的存储路径；
        /// 2. 若路径发生变化，则：
        ///    a) 刷新 TxtTestName 下拉列表中的项目名称；
        ///    b) 清空当前试验名称并将焦点移到 TxtTestName，
        ///       提示用户输入新项目名称或选择已有项目。
        /// </summary>
        private async void BtnFindDir_Click(object sender, EventArgs e)
        {
            var oldDir = (TxtStoreDir.Text ?? string.Empty).Trim();

            using (var fd = new FolderBrowserDialog())
            {
                fd.Description = @"请选择存储路径";
                fd.SelectedPath = string.IsNullOrWhiteSpace(oldDir)
                    ? AppDomain.CurrentDomain.BaseDirectory
                    : oldDir;

                if (fd.ShowDialog() == DialogResult.OK)
                {
                    var path = fd.SelectedPath;
                    if (!Directory.Exists(path))
                    {
                        MessageBox.Show(@"所选路径不存在，请重新选择。", @"错误",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    // 仅在路径真正发生变化时才执行后续逻辑
                    if (!string.Equals(oldDir, path, StringComparison.OrdinalIgnoreCase))
                    {
                        TxtStoreDir.Text = path;

                        // 1) 刷新“试验名称”下拉列表
                        await RefreshTestNameComboItemsAsync();

                        // 2) 清空名称并聚焦，让用户决定“新建 or 切换”
                        TxtTestName.Text = string.Empty;
                        TxtTestName.SelectedIndex = -1;
                        TxtTestName.Focus();

                        MessageBox.Show(
                            @"存储路径已更换，请在“试验名称”中输入新项目名称，" +
                            @"或从下拉列表中选择该路径下已有的项目。",
                            @"提示",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
        }


        /// <summary>
        /// 试验名称输入框获得焦点时：
        /// 记录当前文本内容，用于后续在用户选择“否，不切换项目”时恢复。
        /// </summary>
        private void TxtTestName_Enter(object sender, EventArgs e)
        {
            _testNameBeforeEdit = TxtTestName.Text;
            BtnSaveTest.Enabled = false; // 禁用保存按钮，防止误操作
        }

        /// <summary>
        /// 试验名称输入框失去焦点时：
        /// 1) 校验名称中是否包含非法路径字符；
        /// 2) 若名称与当前存储路径下已有项目重名，则提示是否切换到该项目。
        /// </summary>
        private async void TxtTestName_Leave(object sender, EventArgs e)
        {
            if (System.Threading.Interlocked.Exchange(ref _busy, 1) == 1)
                return;

            var newName = (TxtTestName.Text ?? string.Empty).Trim();

            try
            {
                SetBusy(true);

                if (_cfg?.Test == null || string.IsNullOrEmpty(newName))
                    return;

                if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    MessageBox.Show(
                        @"试验名称中包含 Windows 不允许的字符，请重新输入。",
                        @"试验名称非法",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    RestorePreviousTestName();
                    return;
                }

                var oldTestName = _cfg.Test.TestName ?? string.Empty;
                var oldStoreDir = _cfg.Test.StoreDir ?? string.Empty;
                var newStoreDir = (TxtStoreDir.Text ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(newStoreDir) ||
                    newStoreDir.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                {
                    MessageBox.Show(@"项目存储路径为空或不合法。", @"存储路径非法",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    RestorePreviousTestName();
                    return;
                }

                var sameProject =
                    string.Equals(newName, oldTestName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        Path.GetFullPath(newStoreDir),
                        Path.GetFullPath(oldStoreDir),
                        StringComparison.OrdinalIgnoreCase);
                if (sameProject)
                    return;

                var targetRoot = ConfigLoader.GetProjectRootDir(newStoreDir, newName);
                var targetConfig = ConfigLoader.GetProjectTestConfigPath(newStoreDir, newName);
                TestConfig candidate;
                string successMessage;

                if (File.Exists(targetConfig))
                {
                    var dr = MessageBox.Show(
                        $"当前存储路径下已存在名为“{newName}”的项目。\r\n\r\n" +
                        "请选择：\r\n" +
                        "“是”＝继续旧进度；\r\n" +
                        "“否”＝清空旧进度和运行数据，重新学习后开始；\r\n" +
                        "“取消”＝不切换。\r\n\r\n" +
                        "当前界面未保存的修改将丢失。",
                        @"项目已存在",
                        MessageBoxButtons.YesNoCancel,
                        MessageBoxIcon.Warning);
                    if (dr == DialogResult.Cancel)
                    {
                        RestorePreviousTestName();
                        return;
                    }

                    candidate = await Task.Run(() =>
                        ConfigLoader.LoadProjectTestConfig(newStoreDir, newName, logger));
                    if (dr == DialogResult.No)
                    {
                        var monitorOpened = Application.OpenForms
                            .OfType<MTEmbTest.FrmEpbMainMonitor>()
                            .Any();
                        if (monitorOpened)
                        {
                            MessageBox.Show(
                                @"EPB 主监控界面仍处于打开状态，不能清空项目。请先停止试验并关闭主监控界面。",
                                @"禁止运行中清空",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Warning);
                            RestorePreviousTestName();
                            return;
                        }

                        var confirmClear = MessageBox.Show(
                            $"即将永久清空项目“{newName}”的旧运行数据、索引和自适应模型。\r\n" +
                            "项目参数、通道选择和目标次数会保留；旧日志将先轮转。\r\n" +
                            "下一次开始将重新执行完整学习。\r\n\r\n" +
                            "该操作不可恢复，请确认已完成项目级外部备份。是否继续？",
                            @"确认清空并重新学习",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Warning,
                            MessageBoxDefaultButton.Button2);
                        if (confirmClear != DialogResult.Yes)
                        {
                            RestorePreviousTestName();
                            return;
                        }

                        var resetResult = await Task.Run(() =>
                        {
                            ProjectLogHub.Shutdown();
                            return ProjectRestartCleanupService.ResetForFreshLearning(
                                targetRoot,
                                candidate,
                                logger,
                                () => UnattendedRunCheckpointStore.ClearForProject(
                                    newStoreDir,
                                    newName,
                                    "ProjectProgressCleared"));
                        });
                        successMessage =
                            $"已清空旧进度和运行数据，将重新学习。隔离数据：" +
                            $"{resetResult.BytesIsolated / 1024d / 1024d:F1} MiB。\r\n" +
                            $"审计日志：{resetResult.AuditPath}";
                    }
                    else
                    {
                        successMessage = @"已切换到已有项目配置并继续旧进度。";
                    }
                }
                else
                {
                    if (Directory.Exists(targetRoot))
                    {
                        MessageBox.Show(
                            $"目标目录已存在，但缺少有效的 TestConfig.xml。\r\n" +
                            $"为避免覆盖残留数据，已阻止创建：\r\n{targetRoot}",
                            @"项目目录不完整",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                        RestorePreviousTestName();
                        return;
                    }

                    PushBasicInfoToConfig();
                    PushEpbTargetCountsToConfig();
                    PushEpbCycleRunnerToConfig();
                    PushHydraulicSettingsToConfig();
                    _cfg.Test.TestName = oldTestName;
                    _cfg.Test.StoreDir = oldStoreDir;

                    var oldProjectConfig =
                        ConfigLoader.GetProjectTestConfigPath(oldStoreDir, oldTestName);
                    var templatePath = File.Exists(oldProjectConfig)
                        ? oldProjectConfig
                        : GetDefaultTestConfigPath();
                    var source = _cfg.Test;

                    candidate = await Task.Run(() =>
                        ConfigLoader.CreateNewProjectTestConfig(
                            source,
                            templatePath,
                            newStoreDir,
                            newName,
                            logger));
                    successMessage = @"已创建并切换到新项目；12 路 EPB 均为未勾选、未启动状态。";
                }

                // 候选配置完成保存并可重新解析后，才替换当前项目。
                _cfg.Test = candidate;
                ConfigLoader.UpdateDefaultTestFromProject(candidate, logger);
                RememberCurrentProjectOrWarn();
                LoadCurrentProjectIntoUi();
                await RefreshTestNameComboItemsAsync();
                _testNameBeforeEdit = candidate.TestName;

                MessageBox.Show(successMessage, @"提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception exSwitch)
            {
                RestorePreviousTestName();
                MessageBox.Show(
                    @"切换或创建项目失败，当前项目保持不变：" + exSwitch.Message,
                    @"错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false);
                System.Threading.Interlocked.Exchange(ref _busy, 0);
            }
        }

        private void RememberCurrentProjectOrWarn()
        {
            if (_cfg?.Test == null) return;
            if (LastProjectSelectionStore.TrySave(
                    _cfg.Test.StoreDir,
                    _cfg.Test.TestName,
                    out var error))
            {
                logger?.Info(
                    $"已记住当前项目：{ConfigLoader.GetProjectRootDir(_cfg.Test.StoreDir, _cfg.Test.TestName)}",
                    "配置");
                return;
            }

            logger?.Warn($"保存最后项目选择失败：{error}", "配置");
            XtraMessageBox.Show(
                "项目已成功切换/保存，但无法记住为下次启动项目：\r\n" + error,
                "项目选择未保存",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }

        private void RestorePreviousTestName()
        {
            TxtTestName.Text = _cfg?.Test?.TestName ?? _testNameBeforeEdit ?? string.Empty;
        }

        private void LoadCurrentProjectIntoUi()
        {
            LoadTestConfigFromXml(
                TxtTestCycle, TxtTestName, TxtTestTarget,
                uiCheckBoxIsSameCycleForAllEpb,
                TxtStoreDir, TxtTestMan, RtbDesc);
            BindEpbRunnerGridFromConfig();
            RefreshEpbProgressViewsFromConfig();
        }

        private void dgvEmbControl_DataBindingComplete(object sender, DataGridViewBindingCompleteEventArgs e)
        {
            //  dgvEpbRunnerCfgControl.CurrentCell = null;
            //  dgvEpbRunnerCfgControl.SelectedIndex = -1;
        }

        /// <summary>
        /// “重置所有 EPB 进度”按钮点击事件。
        /// <list type="number">
        ///     <item>1. 若 EPB 主监控界面仍打开，则提示用户先关闭该界面；</item>
        ///     <item>2. 把当前界面参数写回到 <see cref="_cfg.Test"/>；</item>
        ///     <item>3. 隔离并删除旧运行数据、索引和自适应模型；</item>
        ///     <item>4. 将 1..12 通道进度清零并原子保存项目配置；</item>
        ///     <item>5. 调用 <see cref="ConfigLoader.UpdateDefaultTestFromProject"/>，
        ///         同步更新“软件默认 Config\TestConfig.xml”（只同步 TotalCount，默认配置保持为干净模板）；</item>
        ///     <item>6. 轮转旧日志、写审计日志并清除匹配的无人值守恢复检查点。</item>
        /// </list>
        /// </summary>
        private async void uiButtonResetEpbRecord_Click(object sender, EventArgs e)
        {
            if (System.Threading.Interlocked.Exchange(ref _busy, 1) == 1)
                return;

            try
            {
                SetBusy(true);

                if (_cfg?.Test == null)
                {
                    MessageBox.Show(@"当前试验配置为空，无法重置 EPB 进度！",
                        @"错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // ===== 0) 若 EPB 主监控界面仍打开，则不允许重置 =====
                // 防止 EpbDiskWriter 正在占用 index.db 导致删除失败，同时避免运行中间被强行清零。
                var monitorOpened = Application.OpenForms
                    .OfType<MTEmbTest.FrmEpbMainMonitor>()
                    .Any();

                if (monitorOpened)
                {
                    MessageBox.Show(
                        @"检测到 EPB 主监控界面仍在打开状态。" +
                        @"请先关闭 EPB 主监控（结束试验），然后再执行“重置所有 EPB 进度”。",
                        @"提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var currentStoreDir = (_cfg.Test.StoreDir ?? string.Empty).Trim();
                var currentTestName = (_cfg.Test.TestName ?? string.Empty).Trim();
                var currentProjectRoot =
                    ConfigLoader.GetProjectRootDir(currentStoreDir, currentTestName);
                var currentProjectConfig =
                    ConfigLoader.GetProjectTestConfigPath(currentStoreDir, currentTestName);

                // ===== 0.5) 用户确认 =====
                var dr = MessageBox.Show(
                    $"确认要清空以下当前项目的所有 EPB 运行进度和旧运行数据？{Environment.NewLine}" +
                    $"项目：{currentTestName}{Environment.NewLine}" +
                    $"路径：{currentProjectRoot}{Environment.NewLine}{Environment.NewLine}" +
                    @"将保留项目参数、通道选择和目标次数（TotalCount），" + Environment.NewLine +
                    @"但会删除旧快照、索引和自适应模型；旧日志先轮转，下一次开始重新学习。" +
                    Environment.NewLine + Environment.NewLine +
                    @"操作不可恢复，请确认已经完成项目级外部备份。",
                    @"确认清空并重新学习",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);

                if (dr != DialogResult.Yes)
                    return;

                // ===== 1) 先把界面当前内容写回 _cfg.Test =====
                // 基础信息：试验名、周期、目标次数、存储路径、负责人、描述等
                PushBasicInfoToConfig();
                // 项目身份以确认框展示的当前项目为准，禁止未完成的文本编辑改变重置目标。
                _cfg.Test.StoreDir = currentStoreDir;
                _cfg.Test.TestName = currentTestName;

                // EPB 目标次数：把表格中的“目标次数”写回到 _cfg.Test.EpbRecords.TotalCount
                PushEpbTargetCountsToConfig();

                // EPB 循环参数：正向限流、提前断电、保持时间等
                PushEpbCycleRunnerToConfig();

                // 液压压力设置：EPB1-6 和 7-12 的目标压力
                PushHydraulicSettingsToConfig();

                EnsureProjectTestConfigExists();
                var resetResult = await Task.Run(() =>
                {
                    ProjectLogHub.Shutdown();
                    return ProjectRestartCleanupService.ResetForFreshLearning(
                        currentProjectRoot,
                        _cfg.Test,
                        logger,
                        () => UnattendedRunCheckpointStore.ClearForProject(
                            currentStoreDir,
                            currentTestName,
                            "ProjectProgressCleared"));
                });

                RefreshEpbProgressViewsFromConfig();
                ConfigLoader.UpdateDefaultTestFromProject(_cfg.Test);

                MessageBox.Show(
                    @"已清空所有 EPB 进度和旧运行数据，下一次开始将重新学习。" +
                    Environment.NewLine +
                    $"隔离数据：{resetResult.BytesIsolated / 1024d / 1024d:F1} MiB" +
                    Environment.NewLine +
                    $"审计日志：{resetResult.AuditPath}",
                    @"提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(@"重置 EPB 进度失败：" + ex.Message,
                    @"错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false);
                System.Threading.Interlocked.Exchange(ref _busy, 0);
            }
        }

    }
}
