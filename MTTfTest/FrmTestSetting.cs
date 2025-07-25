using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Runtime.Remoting.Channels;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Linq;
using System.IO;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;
using ZlgCanComm;
using DataOperation;
using System.Xml.Serialization;
using System.Runtime.Remoting.Messaging;

namespace MtEmbTest
{
    public partial class FrmTestSetting: Form
    {
        public FrmTestSetting()
        {
            InitializeComponent();
        }

        private void BtnSaveCommand_Click(object sender, EventArgs e)
        {


            try
            {
                short ClampPositionValue;
                if (string.IsNullOrEmpty(TxtClampPosition.Text) || !short.TryParse(TxtClampPosition.Text, out ClampPositionValue))
                {
                    MessageBox.Show("ClampPosition 输入无效，输入不能为空且必须为数字！");
                    return;
                }

                short ClampSpeedValue;
                if (string.IsNullOrEmpty(TxtClampSpeed.Text) || !short.TryParse(TxtClampSpeed.Text, out ClampSpeedValue))
                {
                    MessageBox.Show("ClampSpeed 输入无效，输入不能为空且必须为数字！");
                    return;
                }

                byte ClampModReqValue;
                if (string.IsNullOrEmpty(TxtClampModReq.Text) || !byte.TryParse(TxtClampModReq.Text, out ClampModReqValue))
                {
                    MessageBox.Show("ClampModReq 输入无效，输入不能为空且必须为数字！");
                    return;
                }

                short ClampTorqueValue;
                if (string.IsNullOrEmpty(TxtClampTorque.Text) || !short.TryParse(TxtClampTorque.Text, out ClampTorqueValue))
                {
                    MessageBox.Show("ClampTorque 输入无效，输入不能为空且必须为数字！");
                    return;
                }

                byte ClampNormalModeValue;
                if (string.IsNullOrEmpty(TxtClampNormalMode.Text) || !byte.TryParse(TxtClampNormalMode.Text, out ClampNormalModeValue))
                {
                    MessageBox.Show("ClampNormalMode 输入无效，输入不能为空且必须为数字！");
                    return;
                }

                short ClampForceValue;
                if (string.IsNullOrEmpty(TxtClampForce.Text) || !short.TryParse(TxtClampForce.Text, out ClampForceValue))
                {
                    MessageBox.Show("ClampForce 输入无效，输入不能为空且必须为数字！");
                    return;
                }

                if (ClampForceValue<0|| ClampForceValue>32767)
                {
                    MessageBox.Show("ClampForce 输入为无效值！");
                    return;
                }



                byte ClampEnableValue;
                if (string.IsNullOrEmpty(TxtClampEnable.Text) || !byte.TryParse(TxtClampEnable.Text, out ClampEnableValue))
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
                if (string.IsNullOrEmpty(TxtClampForceReq.Text) || !ushort.TryParse(TxtClampForceReq.Text, out ClampForceReqValue))
                {
                    MessageBox.Show("ClampForceReq 输入无效，输入不能为空且必须为数字！");
                    return;
                }

                short ReleasePositionValue;
                if (string.IsNullOrEmpty(TxtReleasePosition.Text) || !short.TryParse(TxtReleasePosition.Text, out ReleasePositionValue))
                {
                    MessageBox.Show("ReleasePosition 输入无效，输入不能为空且必须为数字！");
                    return;
                }

                short ReleaseSpeedValue;
                if (string.IsNullOrEmpty(TxtReleaseSpeed.Text) || !short.TryParse(TxtReleaseSpeed.Text, out ReleaseSpeedValue))
                {
                    MessageBox.Show("ReleaseSpeed 输入无效，输入不能为空且必须为数字！");
                    return;
                }

                byte ReleaseModeReqValue;
                if (string.IsNullOrEmpty(TxtReleaseModeReq.Text) || !byte.TryParse(TxtReleaseModeReq.Text, out ReleaseModeReqValue))
                {
                    MessageBox.Show("ReleaseModeReq 输入无效，输入不能为空且必须为数字！");
                    return;
                }

                short ReleaseTorqueValue;
                if (string.IsNullOrEmpty(TxtReleaseTorque.Text) || !short.TryParse(TxtReleaseTorque.Text, out ReleaseTorqueValue))
                {
                    MessageBox.Show("ReleaseTorque 输入无效，输入不能为空且必须为数字！");
                    return;
                }

                byte ReleaseNormalModeValue;
                if (string.IsNullOrEmpty(TxtReleaseNormalMode.Text) || !byte.TryParse(TxtReleaseNormalMode.Text, out ReleaseNormalModeValue))
                {
                    MessageBox.Show("ReleaseNormalMode 输入无效，输入不能为空且必须为数字！");
                    return;
                }

                short ReleaseForceValue;
                if (string.IsNullOrEmpty(TxtReleaseForce.Text) || !short.TryParse(TxtReleaseForce.Text, out ReleaseForceValue))
                {
                    MessageBox.Show("ReleaseForce 输入无效，输入不能为空且必须为数字！");
                    return;
                }

                byte ReleaseEnableValue;
                if (string.IsNullOrEmpty(TxtReleaseEnable.Text) || !byte.TryParse(TxtReleaseEnable.Text, out ReleaseEnableValue))
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
                if (string.IsNullOrEmpty(TxtReleaseForceReq.Text) || !ushort.TryParse(TxtReleaseForceReq.Text, out ReleaseForceReqValue))
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

            catch(Exception ex)
            {
                MessageBox.Show(ex.Message);
            }





        }

        private void FrmTestSetting_Load(object sender, EventArgs e)
        {
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


                TxtDRate.Text = ClsGlobal.DRate.ToString();
                TxtARate.Text = ClsGlobal.ARate.ToString();
                TxtCardNo.Text = ClsGlobal.CardNo.ToString();
              //  TxtFrameID.Text = ClsGlobal.SendFrameID;
                TxtMsgInterval.Text = ClsGlobal.MsgInterval.ToString();
                ChkResistorEnabel.Checked = ClsGlobal.ResistorEnabel == 1;
                ComProtocol.SelectedIndex = ClsGlobal.Protocol;
                ComFrameType.SelectedIndex = ClsGlobal.FrameType;





              


                LoadDaqAiToGridView(System.Environment.CurrentDirectory + @"\Config\AIConfig.xml");

                LoadEMBControlsToDataGridView(dgvEmbControl);


            
               LoadTestConfigFromXml(TxtTestCycle, TxtTestStandard, TxtTestName, TxtTestTarget,
                              TxtStoreDir, TxtTestMan, RtbDesc, TxtAlertLimit, ComboTestEnvir);



            }

            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }

        public void LoadEMBControlsToDataGridView(DataGridView dgvEmbControl)
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

                // 配置DataGridView
                dgvEmbControl.AutoGenerateColumns = false;
                dgvEmbControl.DataSource = dt;
                dgvEmbControl.Columns.Clear();

                // 名称列（只读）
                dgvEmbControl.Columns.Add(new DataGridViewTextBoxColumn
                {
                    DataPropertyName = "名称",
                    HeaderText = "名称",
                    ReadOnly = true
                });

                // 型号列
                dgvEmbControl.Columns.Add(new DataGridViewTextBoxColumn
                {
                    DataPropertyName = "型号",
                    HeaderText = "型号"
                });

                // 产品编号列
                dgvEmbControl.Columns.Add(new DataGridViewTextBoxColumn
                {
                    DataPropertyName = "产品编号",
                    HeaderText = "产品编号"
                });

                // 方向列（下拉框）
                DataGridViewComboBoxColumn dirCol = new DataGridViewComboBoxColumn
                {
                    DataPropertyName = "方向",
                    HeaderText = "方向",
                    Items = { "FL", "FR", "RL", "RR" }, // 预设方向选项
                  
                };
                dgvEmbControl.Columns.Add(dirCol);

                // 样式设置
                dgvEmbControl.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
                //dgvEmbControl.ColumnHeadersHeight = 70;
                //dgvEmbControl.RowTemplate.Height = 70;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载EMB配置失败: {ex.Message}");
            }
        }

        public void SaveEMBControlsToXML(DataGridView dgvEmbControl)
        {
            try
            {
                DataTable dt = (DataTable)dgvEmbControl.DataSource;
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
                    string direction = row["方向"].ToString().Trim();
                    if (!validDirections.Contains(direction))
                    {
                        MessageBox.Show($"无效的方向值: {direction}");
                        return;
                    }

                    names.Add(row["名称"].ToString().Trim());
                }

                // 验证名称
                var requiredNames = new HashSet<string> { "EMB1"};

                if (names.Count != 1 ||
                    names.Distinct().Count() != 1 ||
                    !names.All(n => requiredNames.Contains(n)))
                {
                    MessageBox.Show("名称必须包含且仅包含EMB1");
                    return;
                }

                // 构建XML结构
                XElement root = new XElement("EMBControl",
                    from row in dt.AsEnumerable()
                    select new XElement("EMB",
                        new XElement("名称", row["名称"]),
                        new XElement("型号", row["型号"]),
                        new XElement("产品编号", row["产品编号"]),
                        new XElement("方向", row["方向"])
                    )
                );

                // 保存文件
                string xmlPath = Path.Combine(Environment.CurrentDirectory, @"Config\EMBControl.XML");
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
                    DataTable dt = new DataTable();
                    dt.Columns.Add("序号", typeof(int));
                    dt.Columns.Add("物理通道", typeof(string));
                    dt.Columns.Add("参数名", typeof(string));
                    dt.Columns.Add("单位", typeof(string));
                    dt.Columns.Add("变换斜率", typeof(double));
                    dt.Columns.Add("变换截距", typeof(double));
                    dt.Columns.Add("参数类型", typeof(string));
                    dt.Columns.Add("是否启用", typeof(bool));
                    dt.Columns.Add("零位漂移", typeof(string));

                    XmlDocument xmlDoc = new XmlDocument();
                    xmlDoc.Load(filePath);
                    XmlNodeList records = xmlDoc.SelectNodes("//Records");

                    foreach (XmlNode record in records)
                    {
                        DataRow row = dt.NewRow();
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
                    DataGridViewComboBoxColumn comboBoxColumn = new DataGridViewComboBoxColumn();
                    comboBoxColumn.Name = "参数类型";
                    comboBoxColumn.DataPropertyName = "参数类型";
                    comboBoxColumn.HeaderText = "参数类型";
                    comboBoxColumn.Items.Add("电流");
                    comboBoxColumn.Items.Add("扭矩");
                    comboBoxColumn.Items.Add("压力");
                    comboBoxColumn.Items.Add("距离");
                    comboBoxColumn.Items.Add("");
                    // 可根据实际情况添加更多参数类型选项
                    int index = dgvDaqAI.Columns["参数类型"].Index;
                    dgvDaqAI.Columns.RemoveAt(index);
                    dgvDaqAI.Columns.Insert(index, comboBoxColumn);

                    // 设置是否启用列为 CheckBox 列
                    DataGridViewCheckBoxColumn checkBoxColumn = new DataGridViewCheckBoxColumn();
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
            catch(Exception ex)
            {
                MessageBox.Show("读入DAQ AI配置出错：" + ex.Message);
            }
        }

        private void SaveDaqAIToXML(string filePath)
        {
            // string filePath = "AIConfig.XML";
            try
            {
                XmlDocument xmlDoc = new XmlDocument();
                XmlElement root = xmlDoc.CreateElement("AIConfigDetail");
                xmlDoc.AppendChild(root);

                DataTable dt = (DataTable)dgvDaqAI.DataSource;

               



                foreach (DataRow row in dt.Rows)
                {
                    XmlElement record = xmlDoc.CreateElement("Records");

                    XmlElement id = xmlDoc.CreateElement("序号");
                    id.InnerText = row["序号"].ToString();
                    record.AppendChild(id);

                    XmlElement physicalChannel = xmlDoc.CreateElement("物理通道");
                    physicalChannel.InnerText = row["物理通道"].ToString();
                    record.AppendChild(physicalChannel);

                    XmlElement paramName = xmlDoc.CreateElement("参数名");
                    paramName.InnerText = row["参数名"].ToString();
                    record.AppendChild(paramName);

                    XmlElement unit = xmlDoc.CreateElement("单位");
                    unit.InnerText = row["单位"].ToString();
                    record.AppendChild(unit);

                    XmlElement slope = xmlDoc.CreateElement("变换斜率");
                    slope.InnerText = row["变换斜率"].ToString();
                    record.AppendChild(slope);

                    XmlElement intercept = xmlDoc.CreateElement("变换截距");
                    intercept.InnerText = row["变换截距"].ToString();
                    record.AppendChild(intercept);

                    XmlElement paramType = xmlDoc.CreateElement("参数类型");
                    paramType.InnerText = row["参数类型"].ToString();
                    record.AppendChild(paramType);

                    XmlElement isEnabled = xmlDoc.CreateElement("是否启用");
                    isEnabled.InnerText = (bool)row["是否启用"] ? "1" : "0";
                    record.AppendChild(isEnabled);

                    XmlElement ZeroValue = xmlDoc.CreateElement("零位漂移");
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
            SaveDaqAIToXML(System.Environment.CurrentDirectory + @"\Config\AIConfig.xml");
        }

        private void BtnSaveTest_Click(object sender, EventArgs e)
        {
            SaveEMBControlsToXML(dgvEmbControl);



           SaveTestConfigToXml(TxtTestCycle, TxtTestStandard, TxtTestName, TxtTestTarget,
                TxtStoreDir, TxtTestMan, RtbDesc, TxtAlertLimit, ComboTestEnvir);

            int dRateValue;
            if (string.IsNullOrEmpty(TxtDRate.Text) || !int.TryParse(TxtDRate.Text, out dRateValue))
            {
                MessageBox.Show("DRate 输入无效，输入不能为空且必须为数字！");
                return;
            }

            int aRateValue;
            if (string.IsNullOrEmpty(TxtARate.Text) || !int.TryParse(TxtARate.Text, out aRateValue))
            {
                MessageBox.Show("ARate 输入无效，输入不能为空且必须为数字！");
                return;
            }

            int cardNoValue;
            if (string.IsNullOrEmpty(TxtCardNo.Text) || !int.TryParse(TxtCardNo.Text, out cardNoValue))
            {
                MessageBox.Show("CardNo 输入无效，输入不能为空且必须为数字！");
                return;
            }



            int msgIntervalValue;
            if (string.IsNullOrEmpty(TxtMsgInterval.Text) || !int.TryParse(TxtMsgInterval.Text, out msgIntervalValue))
            {
                MessageBox.Show("MsgInterval 输入无效，输入不能为空且必须为数字！");
                return;
            }

            int resistorEnabelValue;
            if (string.IsNullOrEmpty(ChkResistorEnabel.Checked ? "1" : "0") || !int.TryParse(ChkResistorEnabel.Checked ? "1" : "0", out resistorEnabelValue))
            {
                MessageBox.Show("ResistorEnabel 输入无效，输入不能为空且必须为数字！");
                return;
            }

            int protocolValue;
            if (ComProtocol.SelectedIndex < 0)
            {
                MessageBox.Show("Protocol 输入无效，请选择一个选项！");
                return;
            }
            protocolValue = ComProtocol.SelectedIndex;

            int frameTypeValue;
            if (ComFrameType.SelectedIndex < 0)
            {
                MessageBox.Show("FrameType 输入无效，请选择一个选项！");
                return;
            }
            frameTypeValue = ComFrameType.SelectedIndex;

            // 统一进行保存配置和赋值操作
            ConfigOperation.SaveOneItem("DRate", TxtDRate.Text);
            ClsGlobal.DRate = dRateValue;

            ConfigOperation.SaveOneItem("ARate", TxtARate.Text);
            ClsGlobal.ARate = aRateValue;

            ConfigOperation.SaveOneItem("CardNo", TxtCardNo.Text);
            ClsGlobal.CardNo = cardNoValue;

           

            ConfigOperation.SaveOneItem("MsgInterval", TxtMsgInterval.Text);
            ClsGlobal.MsgInterval = msgIntervalValue;

            ConfigOperation.SaveOneItem("ResistorEnabel", resistorEnabelValue.ToString());
            ClsGlobal.ResistorEnabel = resistorEnabelValue;

            ConfigOperation.SaveOneItem("Protocol", protocolValue.ToString());
            ClsGlobal.Protocol = protocolValue;

            ConfigOperation.SaveOneItem("FrameType", frameTypeValue.ToString());
            ClsGlobal.FrameType = frameTypeValue;


            MessageBox.Show("保存成功！");








        }


        public void SaveTestConfigToXml(Sunny.UI.UITextBox txtTestCycle,
                     Sunny.UI.UITextBox txtTestStandard,
                     Sunny.UI.UITextBox txtTestName,
                     Sunny.UI.UITextBox txtTestTarget,
                     Sunny.UI.UITextBox txtStoreDir,
                     Sunny.UI.UITextBox txtTestMan,
                     Sunny.UI.UIRichTextBox rtbDesc,
                     Sunny.UI.UITextBox txtAlertLimit,
                     Sunny.UI.UIComboBox comboTestEnvir)
        {
            var config = new TestConfig
            {
                TestCycle = txtTestCycle.Text,
                TestStandard = txtTestStandard.Text,
                TestName = txtTestName.Text,
                TestTarget = txtTestTarget.Text,
                StoreDir = txtStoreDir.Text,
                TestMan = txtTestMan.Text,
                Description = rtbDesc.Text,
                AlertLimit = txtAlertLimit.Text,
                TestEnvir=comboTestEnvir.Text
            };

            SaveTestConfigToFile(config);
        }

        // 统一保存逻辑
        private void SaveTestConfigToFile(TestConfig config)
        {
            var serializer = new XmlSerializer(typeof(TestConfig));

            string xmlPath = Path.Combine(Environment.CurrentDirectory, @"Config\TestConfig.xml");

            using (var writer = new StreamWriter(xmlPath))
            {
                serializer.Serialize(writer, config);
            }
        }

        // 显式控件参数加载方式
        public void LoadTestConfigFromXml(Sunny.UI.UITextBox txtTestCycle,
                               Sunny.UI.UITextBox txtTestStandard,
                               Sunny.UI.UITextBox txtTestName,
                               Sunny.UI.UITextBox txtTestTarget,
                               Sunny.UI.UITextBox txtStoreDir,
                               Sunny.UI.UITextBox txtTestMan,
                               Sunny.UI.UIRichTextBox rtbDesc,
                               Sunny.UI.UITextBox txtAlertLimit,
                               Sunny.UI.UIComboBox comboTestEnvir)
        {

            string xmlPath = Path.Combine(Environment.CurrentDirectory, @"Config\TestConfig.xml");


            if (!File.Exists(xmlPath)) return;

            var config = LoadTestConfigFromFile();
            if (config == null) return;

            txtTestCycle.Text = config.TestCycle;
            txtTestStandard.Text = config.TestStandard;
            txtTestName.Text = config.TestName;
            txtTestTarget.Text = config.TestTarget;
            txtStoreDir.Text = config.StoreDir;
            txtTestMan.Text = config.TestMan;
            rtbDesc.Text = config.Description;
            txtAlertLimit.Text = config.AlertLimit;
            comboTestEnvir.Text=config.TestEnvir;

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

        private void BtnFindDir_Click(object sender, EventArgs e)
        {
            // 创建文件夹选择对话框
            using (var folderDialog = new FolderBrowserDialog())
            {
                // 对话框基础设置
                folderDialog.Description = "请选择存储目录";
              //  folderDialog.UseDescriptionForTitle = true;  // 将描述作为窗口标题
                folderDialog.ShowNewFolderButton = true;      // 允许新建文件夹

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

        private void ComboTestEnvir_SelectedIndexChanged(object sender, EventArgs e)
        {
            if(ComboTestEnvir.Text=="常温")
            {
                TxtTestCycle.Text = "0.278";
                TxtTestTarget.Text = "500000";
            }
            if (ComboTestEnvir.Text == "高温")
            {
                TxtTestCycle.Text = "0.278";
                TxtTestTarget.Text = "210000";
            }
            if (ComboTestEnvir.Text == "低温")
            {
                TxtTestCycle.Text = "0.167";
                TxtTestTarget.Text = "10000";
            }
        }

        private void dgvEmbControl_DataBindingComplete(object sender, DataGridViewBindingCompleteEventArgs e)
        {
          //  dgvEmbControl.CurrentCell = null;
          //  dgvEmbControl.SelectedIndex = -1;
        }
    }
}
