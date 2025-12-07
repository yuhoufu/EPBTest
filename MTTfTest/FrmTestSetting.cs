using Config.Models;
using MTEmbTest.Models;
using Sunny.UI;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;
using DevExpress.Data.Helpers;
using DevExpress.XtraEditors;

namespace MtEmbTest
{
    public partial class FrmTestSetting : Form
    {
        private readonly BindingList<EpbRow> _epbRows = new();
        private readonly GlobalConfig _cfg; // 全部配置对象
        private List<PressureSettingControl> _pressureSettings; // 压力设置

        public FrmTestSetting(GlobalConfig cfg)
        {
            _cfg = cfg;
            _pressureSettings = new List<PressureSettingControl>(); // 初始化
            InitializeComponent();
        }

        private void BtnSaveCommand_Click(object sender, EventArgs e)
        {
            try
            {
                short ClampPositionValue;
                if (string.IsNullOrEmpty(TxtClampPosition.Text) ||
                    !short.TryParse(TxtClampPosition.Text, out ClampPositionValue))
                {
                    MessageBox.Show("ClampPosition 输入无效，输入不能为空且必须为数字！");
                    return;
                }

                short ClampSpeedValue;
                if (string.IsNullOrEmpty(TxtClampSpeed.Text) ||
                    !short.TryParse(TxtClampSpeed.Text, out ClampSpeedValue))
                {
                    MessageBox.Show("ClampSpeed 输入无效，输入不能为空且必须为数字！");
                    return;
                }

                byte ClampModReqValue;
                if (string.IsNullOrEmpty(TxtClampModReq.Text) ||
                    !byte.TryParse(TxtClampModReq.Text, out ClampModReqValue))
                {
                    MessageBox.Show("ClampModReq 输入无效，输入不能为空且必须为数字！");
                    return;
                }

                short ClampTorqueValue;
                if (string.IsNullOrEmpty(TxtClampTorque.Text) ||
                    !short.TryParse(TxtClampTorque.Text, out ClampTorqueValue))
                {
                    MessageBox.Show("ClampTorque 输入无效，输入不能为空且必须为数字！");
                    return;
                }

                byte ClampNormalModeValue;
                if (string.IsNullOrEmpty(TxtClampNormalMode.Text) ||
                    !byte.TryParse(TxtClampNormalMode.Text, out ClampNormalModeValue))
                {
                    MessageBox.Show("ClampNormalMode 输入无效，输入不能为空且必须为数字！");
                    return;
                }

                short ClampForceValue;
                if (string.IsNullOrEmpty(TxtClampForce.Text) ||
                    !short.TryParse(TxtClampForce.Text, out ClampForceValue))
                {
                    MessageBox.Show("ClampForce 输入无效，输入不能为空且必须为数字！");
                    return;
                }

                if (ClampForceValue < 0 || ClampForceValue > 32767)
                {
                    MessageBox.Show("ClampForce 输入为无效值！");
                    return;
                }


                byte ClampEnableValue;
                if (string.IsNullOrEmpty(TxtClampEnable.Text) ||
                    !byte.TryParse(TxtClampEnable.Text, out ClampEnableValue))
                {
                    MessageBox.Show("ClampEnable 输入无效，输入不能为空且必须为数字！");
                    return;
                }

                if (ClampEnableValue < 0 || ClampEnableValue > 1)
                {
                    MessageBox.Show("ClampEnableValue 输入为无效值！");
                    return;
                }


                ushort ClampForceReqValue;
                if (string.IsNullOrEmpty(TxtClampForceReq.Text) ||
                    !ushort.TryParse(TxtClampForceReq.Text, out ClampForceReqValue))
                {
                    MessageBox.Show("ClampForceReq 输入无效，输入不能为空且必须为数字！");
                    return;
                }

                short ReleasePositionValue;
                if (string.IsNullOrEmpty(TxtReleasePosition.Text) ||
                    !short.TryParse(TxtReleasePosition.Text, out ReleasePositionValue))
                {
                    MessageBox.Show("ReleasePosition 输入无效，输入不能为空且必须为数字！");
                    return;
                }

                short ReleaseSpeedValue;
                if (string.IsNullOrEmpty(TxtReleaseSpeed.Text) ||
                    !short.TryParse(TxtReleaseSpeed.Text, out ReleaseSpeedValue))
                {
                    MessageBox.Show("ReleaseSpeed 输入无效，输入不能为空且必须为数字！");
                    return;
                }

                byte ReleaseModeReqValue;
                if (string.IsNullOrEmpty(TxtReleaseModeReq.Text) ||
                    !byte.TryParse(TxtReleaseModeReq.Text, out ReleaseModeReqValue))
                {
                    MessageBox.Show("ReleaseModeReq 输入无效，输入不能为空且必须为数字！");
                    return;
                }

                short ReleaseTorqueValue;
                if (string.IsNullOrEmpty(TxtReleaseTorque.Text) ||
                    !short.TryParse(TxtReleaseTorque.Text, out ReleaseTorqueValue))
                {
                    MessageBox.Show("ReleaseTorque 输入无效，输入不能为空且必须为数字！");
                    return;
                }

                byte ReleaseNormalModeValue;
                if (string.IsNullOrEmpty(TxtReleaseNormalMode.Text) ||
                    !byte.TryParse(TxtReleaseNormalMode.Text, out ReleaseNormalModeValue))
                {
                    MessageBox.Show("ReleaseNormalMode 输入无效，输入不能为空且必须为数字！");
                    return;
                }

                short ReleaseForceValue;
                if (string.IsNullOrEmpty(TxtReleaseForce.Text) ||
                    !short.TryParse(TxtReleaseForce.Text, out ReleaseForceValue))
                {
                    MessageBox.Show("ReleaseForce 输入无效，输入不能为空且必须为数字！");
                    return;
                }

                byte ReleaseEnableValue;
                if (string.IsNullOrEmpty(TxtReleaseEnable.Text) ||
                    !byte.TryParse(TxtReleaseEnable.Text, out ReleaseEnableValue))
                {
                    MessageBox.Show("ReleaseEnable 输入无效，输入不能为空且必须为数字！");
                    return;
                }

                if (ReleaseEnableValue < 0 || ReleaseEnableValue > 1)
                {
                    MessageBox.Show("ReleaseEnableValue 输入为无效值！");
                    return;
                }


                ushort ReleaseForceReqValue;
                if (string.IsNullOrEmpty(TxtReleaseForceReq.Text) ||
                    !ushort.TryParse(TxtReleaseForceReq.Text, out ReleaseForceReqValue))
                {
                    MessageBox.Show("ReleaseForceReq 输入无效，输入不能为空且必须为数字！");
                    return;
                }

                // 统一进行保存配置和赋值操作
                ConfigOperation.SaveOneItem("ClampPosition", TxtClampPosition.Text);
                ClsGlobal.ClampPosition = ClampPositionValue;

                ConfigOperation.SaveOneItem("ClampSpeed", TxtClampSpeed.Text);
                ClsGlobal.ClampSpeed = ClampSpeedValue;

                ConfigOperation.SaveOneItem("ClampModReq", TxtClampModReq.Text);
                ClsGlobal.ClampModReq = ClampModReqValue;

                ConfigOperation.SaveOneItem("ClampTorque", TxtClampTorque.Text);
                ClsGlobal.ClampTorque = ClampTorqueValue;

                ConfigOperation.SaveOneItem("ClampNormalMode", TxtClampNormalMode.Text);
                ClsGlobal.ClampNormalMode = ClampNormalModeValue;

                ConfigOperation.SaveOneItem("ClampForce", TxtClampForce.Text);
                ClsGlobal.ClampForce = ClampForceValue;

                ConfigOperation.SaveOneItem("ClampEnable", TxtClampEnable.Text);
                ClsGlobal.ClampEnable = ClampEnableValue;

                ConfigOperation.SaveOneItem("ClampForceReq", TxtClampForceReq.Text);
                ClsGlobal.ClampForceReq = ClampForceReqValue;

                ConfigOperation.SaveOneItem("ReleasePosition", TxtReleasePosition.Text);
                ClsGlobal.ReleasePosition = ReleasePositionValue;

                ConfigOperation.SaveOneItem("ReleaseSpeed", TxtReleaseSpeed.Text);
                ClsGlobal.ReleaseSpeed = ReleaseSpeedValue;

                ConfigOperation.SaveOneItem("ReleaseModeReq", TxtReleaseModeReq.Text);
                ClsGlobal.ReleaseModeReq = ReleaseModeReqValue;

                ConfigOperation.SaveOneItem("ReleaseTorque", TxtReleaseTorque.Text);
                ClsGlobal.ReleaseTorque = ReleaseTorqueValue;

                ConfigOperation.SaveOneItem("ReleaseNormalMode", TxtReleaseNormalMode.Text);
                ClsGlobal.ReleaseNormalMode = ReleaseNormalModeValue;

                ConfigOperation.SaveOneItem("ReleaseForce", TxtReleaseForce.Text);
                ClsGlobal.ReleaseForce = ReleaseForceValue;

                ConfigOperation.SaveOneItem("ReleaseEnable", TxtReleaseEnable.Text);
                ClsGlobal.ReleaseEnable = ReleaseEnableValue;

                ConfigOperation.SaveOneItem("ReleaseForceReq", TxtReleaseForceReq.Text);
                ClsGlobal.ReleaseForceReq = ReleaseForceReqValue;

                MessageBox.Show("保存成功！");
            }

            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        private void FrmTestSetting_Load(object sender, EventArgs e)
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
            BindEpbRunnerGridFromConfig();

            //TabSetting.GetPage(2).Visible = false; // 暂时不显示第3个界面
            TabSetting.TabPages.Remove(tabPageCommand); // 暂时移除tabPageCommand


            try
            {
                // 赋值 Clamp 相关变量到对应 TextBox
                TxtClampPosition.Text = ClsGlobal.ClampPosition.ToString();
                TxtClampSpeed.Text = ClsGlobal.ClampSpeed.ToString();
                TxtClampModReq.Text = ClsGlobal.ClampModReq.ToString();
                TxtClampTorque.Text = ClsGlobal.ClampTorque.ToString();
                TxtClampNormalMode.Text = ClsGlobal.ClampNormalMode.ToString();
                TxtClampForce.Text = ClsGlobal.ClampForce.ToString();
                TxtClampEnable.Text = ClsGlobal.ClampEnable.ToString();
                TxtClampForceReq.Text = ClsGlobal.ClampForceReq.ToString();

                // 赋值 Release 相关变量到对应 TextBox
                TxtReleasePosition.Text = ClsGlobal.ReleasePosition.ToString();
                TxtReleaseSpeed.Text = ClsGlobal.ReleaseSpeed.ToString();
                TxtReleaseModeReq.Text = ClsGlobal.ReleaseModeReq.ToString();
                TxtReleaseTorque.Text = ClsGlobal.ReleaseTorque.ToString();
                TxtReleaseNormalMode.Text = ClsGlobal.ReleaseNormalMode.ToString();
                TxtReleaseForce.Text = ClsGlobal.ReleaseForce.ToString();
                TxtReleaseEnable.Text = ClsGlobal.ReleaseEnable.ToString();
                TxtReleaseForceReq.Text = ClsGlobal.ReleaseForceReq.ToString();

                LoadDaqAiToGridView(Environment.CurrentDirectory + @"\Config\AIConfig.xml");


                LoadTestConfigFromXml(TxtTestCycle, TxtTestName, TxtTestTarget,
                    TxtStoreDir, TxtTestMan, RtbDesc);

                // 压力设置相关-开始
                InitializePressureSettings();

                // 压力设置导入到界面
                LoadDefaultValues(); // 从cfg配置中导入；
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
                    PeakIgnoreMs = r?.PeakIgnoreMs ?? 0
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
                AddCol(nameof(EpbRow.ForwardA), "电流阈值(A)", 187);
                AddCol(nameof(EpbRow.SafetyMarginA), "提前断电值(A)", 237);
                AddCol(nameof(EpbRow.FwdOnLimitMs), "正上限时长(ms)", 249);
                AddCol(nameof(EpbRow.HoldMs), "夹紧保持(ms)", 212);
                AddCol(nameof(EpbRow.RevDecayLimitA), "反向衰减限(A)", 216);
                AddCol(nameof(EpbRow.RevDecayRigidMaxMs), "反衰限制时长(ms)", 287);
                AddCol(nameof(EpbRow.RevEmptyFixedMs), "反向固定空行程(ms)", 311);
                AddCol(nameof(EpbRow.PreReleaseKeepMs), "预释放维持(ms)", 233);
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
                    PeakIgnoreMs = r?.PeakIgnoreMs ?? 0
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
                AddCol(nameof(EpbRow.ForwardA), "电流阈值(A)", 80f);
                AddCol(nameof(EpbRow.SafetyMarginA), "提前断电值(A)", 101f);
                AddCol(nameof(EpbRow.FwdOnLimitMs), "正上限时长(ms)", 106f);
                AddCol(nameof(EpbRow.HoldMs), "夹紧保持(ms)", 90f);
                AddCol(nameof(EpbRow.RevDecayLimitA), "反向衰减限(A)", 92f);
                AddCol(nameof(EpbRow.RevDecayRigidMaxMs), "反衰限制时长(ms)", 122f);
                AddCol(nameof(EpbRow.RevEmptyFixedMs), "反向固定空行程(ms)", 132f);
                AddCol(nameof(EpbRow.PreReleaseKeepMs), "预释放维持(ms)", 99f);
                AddCol(nameof(EpbRow.PeakIgnoreMs), "峰值忽略(ms)", 90f);

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

        private void BtnSaveTest_Click(object sender, EventArgs e)
        {
            try
            {
                // 1) 写回 Basic 信息（周期、名称、目录、负责人等）到 _cfg
                PushBasicInfoToConfig();

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
                    PeakIgnoreMs = Math.Max(0, r.PeakIgnoreMs)
                };
                dict[r.Channel] = item;
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
            UITextBox txtTestName,
            UITextBox txtTestTarget,
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


        private void BtnFindDir_Click(object sender, EventArgs e)
        {
            // 创建文件夹选择对话框
            using (var folderDialog = new FolderBrowserDialog())
            {
                // 对话框基础设置
                folderDialog.Description = "请选择存储目录";
                //  folderDialog.UseDescriptionForTitle = true;  // 将描述作为窗口标题
                folderDialog.ShowNewFolderButton = true; // 允许新建文件夹

                // 可选：设置初始目录（默认从"我的电脑"开始）
                folderDialog.RootFolder = Environment.SpecialFolder.MyComputer;

                // 显示对话框并处理结果
                if (folderDialog.ShowDialog() == DialogResult.OK)
                {
                    // 获取选择的路径并显示到文本框
                    TxtStoreDir.Text = folderDialog.SelectedPath;

                    // 可选：立即验证路径有效性
                    if (!Directory.Exists(TxtStoreDir.Text))
                    {
                        MessageBox.Show("路径不存在，请重新选择！");
                        TxtStoreDir.Clear();
                    }
                }
            }
        }

        private void dgvEmbControl_DataBindingComplete(object sender, DataGridViewBindingCompleteEventArgs e)
        {
            //  dgvEpbRunnerCfgControl.CurrentCell = null;
            //  dgvEpbRunnerCfgControl.SelectedIndex = -1;
        }
    }
}