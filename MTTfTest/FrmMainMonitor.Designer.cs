namespace MtEmbTest
{
    partial class FrmMainMonitor
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            System.Windows.Forms.DataGridViewCellStyle dataGridViewCellStyle1 = new System.Windows.Forms.DataGridViewCellStyle();
            System.Windows.Forms.DataGridViewCellStyle dataGridViewCellStyle2 = new System.Windows.Forms.DataGridViewCellStyle();
            System.Windows.Forms.DataGridViewCellStyle dataGridViewCellStyle3 = new System.Windows.Forms.DataGridViewCellStyle();
            System.Windows.Forms.DataGridViewCellStyle dataGridViewCellStyle4 = new System.Windows.Forms.DataGridViewCellStyle();
            System.Windows.Forms.DataGridViewCellStyle dataGridViewCellStyle5 = new System.Windows.Forms.DataGridViewCellStyle();
            this.uiStyleManager1 = new Sunny.UI.UIStyleManager(this.components);
            this.RadEmb1 = new Sunny.UI.UIRadioButton();
            this.LabEmb1 = new Sunny.UI.UILabel();
            this.AlertEmb1 = new Sunny.UI.UILight();
            this.uiTableLayoutPanel2 = new Sunny.UI.UITableLayoutPanel();
            this.TxtTargetCycles = new Sunny.UI.UITextBox();
            this.TxtTestCycleTime = new Sunny.UI.UITextBox();
            this.uiLabel7 = new Sunny.UI.UILabel();
            this.uiLabel9 = new Sunny.UI.UILabel();
            this.uiLabel10 = new Sunny.UI.UILabel();
            this.TxtTestName = new Sunny.UI.UITextBox();
            this.uiLabel8 = new Sunny.UI.UILabel();
            this.TxtTestStandard = new Sunny.UI.UITextBox();
            this.uiLabel11 = new Sunny.UI.UILabel();
            this.ChkEmb1 = new Sunny.UI.UICheckBox();
            this.BtnCancel = new Sunny.UI.UIButton();
            this.BtnApply = new Sunny.UI.UIButton();
            this.BtnSettingDetail = new Sunny.UI.UIButton();
            this.uiGroupBox1 = new Sunny.UI.UIGroupBox();
            this.zedGraphRealChart = new ZedGraph.ZedGraphControl();
            this.uiGroupAllControl = new Sunny.UI.UIGroupBox();
            this.uiTableLayoutPanel7 = new Sunny.UI.UITableLayoutPanel();
            this.BtnStop = new Sunny.UI.UIButton();
            this.BtnStartTest = new Sunny.UI.UIButton();
            this.BtnAutoLearn = new Sunny.UI.UIButton();
            this.BtnPause = new Sunny.UI.UIButton();
            this.uiGroupInfo = new Sunny.UI.UIGroupBox();
            this.z = new Sunny.UI.UITableLayoutPanel();
            this.BtnRunLog = new Sunny.UI.UIButton();
            this.RtbInfo = new Sunny.UI.UIRichTextBox();
            this.BtnErrorLog = new Sunny.UI.UIButton();
            this.dgvRealData = new Sunny.UI.UIDataGridView();
            this.uiGroupCurve = new Sunny.UI.UIGroupBox();
            this.uiTableLayoutPanel6 = new Sunny.UI.UITableLayoutPanel();
            this.LedRunTime = new Sunny.UI.UILedDisplay();
            this.uiTableLayoutPanel4 = new Sunny.UI.UITableLayoutPanel();
            this.uiTableLayoutPanel9 = new Sunny.UI.UITableLayoutPanel();
            this.LedRunCycles = new Sunny.UI.UILedDisplay();
            this.uiTableLayoutPanel10 = new Sunny.UI.UITableLayoutPanel();
            this.LedLastCycles = new Sunny.UI.UILedDisplay();
            this.ProcBar = new Sunny.UI.UIProcessBar();
            this.timerProgressDisp = new System.Windows.Forms.Timer(this.components);
            this.uiTableLayoutPanelMain = new Sunny.UI.UITableLayoutPanel();
            this.uiLabel1 = new Sunny.UI.UILabel();
            this.uiLabel2 = new Sunny.UI.UILabel();
            this.uiLabel3 = new Sunny.UI.UILabel();
            this.DiReadTimer = new System.Windows.Forms.Timer(this.components);
            this.uiTableLayoutPanel2.SuspendLayout();
            this.uiGroupBox1.SuspendLayout();
            this.uiGroupAllControl.SuspendLayout();
            this.uiTableLayoutPanel7.SuspendLayout();
            this.uiGroupInfo.SuspendLayout();
            this.z.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.dgvRealData)).BeginInit();
            this.uiGroupCurve.SuspendLayout();
            this.uiTableLayoutPanel6.SuspendLayout();
            this.uiTableLayoutPanel4.SuspendLayout();
            this.uiTableLayoutPanel9.SuspendLayout();
            this.uiTableLayoutPanel10.SuspendLayout();
            this.uiTableLayoutPanelMain.SuspendLayout();
            this.SuspendLayout();
            // 
            // RadEmb1
            // 
            this.RadEmb1.Checked = true;
            this.RadEmb1.Cursor = System.Windows.Forms.Cursors.Hand;
            this.RadEmb1.Font = new System.Drawing.Font("Arial", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.RadEmb1.Location = new System.Drawing.Point(659, 98);
            this.RadEmb1.MinimumSize = new System.Drawing.Size(1, 1);
            this.RadEmb1.Name = "RadEmb1";
            this.RadEmb1.RadioButtonSize = 32;
            this.RadEmb1.Size = new System.Drawing.Size(44, 39);
            this.RadEmb1.TabIndex = 10;
            this.RadEmb1.Text = "EMB1";
            this.RadEmb1.Visible = false;
            // 
            // LabEmb1
            // 
            this.LabEmb1.Font = new System.Drawing.Font("Arial Narrow", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.LabEmb1.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(48)))), ((int)(((byte)(48)))), ((int)(((byte)(48)))));
            this.LabEmb1.Location = new System.Drawing.Point(709, 296);
            this.LabEmb1.Name = "LabEmb1";
            this.LabEmb1.Size = new System.Drawing.Size(7, 45);
            this.LabEmb1.TabIndex = 11;
            this.LabEmb1.Text = "0";
            this.LabEmb1.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            this.LabEmb1.Visible = false;
            // 
            // AlertEmb1
            // 
            this.AlertEmb1.CenterColor = System.Drawing.Color.FromArgb(((int)(((byte)(110)))), ((int)(((byte)(190)))), ((int)(((byte)(40)))));
            this.AlertEmb1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.AlertEmb1.Font = new System.Drawing.Font("宋体", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.AlertEmb1.Interval = 2000;
            this.AlertEmb1.Location = new System.Drawing.Point(1935, 820);
            this.AlertEmb1.Margin = new System.Windows.Forms.Padding(12, 3, 3, 3);
            this.AlertEmb1.MinimumSize = new System.Drawing.Size(1, 1);
            this.AlertEmb1.Name = "AlertEmb1";
            this.AlertEmb1.OffCenterColor = System.Drawing.Color.FromArgb(((int)(((byte)(140)))), ((int)(((byte)(140)))), ((int)(((byte)(140)))));
            this.AlertEmb1.OnCenterColor = System.Drawing.Color.FromArgb(((int)(((byte)(110)))), ((int)(((byte)(190)))), ((int)(((byte)(40)))));
            this.AlertEmb1.Radius = 0;
            this.AlertEmb1.Shape = Sunny.UI.UIShape.Square;
            this.AlertEmb1.Size = new System.Drawing.Size(63, 51);
            this.AlertEmb1.State = Sunny.UI.UILightState.Off;
            this.AlertEmb1.TabIndex = 12;
            this.AlertEmb1.Text = "uiLight1";
            this.AlertEmb1.Click += new System.EventHandler(this.AlertEmb1_Click);
            // 
            // uiTableLayoutPanel2
            // 
            this.uiTableLayoutPanel2.BackColor = System.Drawing.Color.Transparent;
            this.uiTableLayoutPanel2.ColumnCount = 8;
            this.uiTableLayoutPanel2.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 2.040816F));
            this.uiTableLayoutPanel2.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 40.81633F));
            this.uiTableLayoutPanel2.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 80F));
            this.uiTableLayoutPanel2.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 30.61224F));
            this.uiTableLayoutPanel2.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 80F));
            this.uiTableLayoutPanel2.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 24.4898F));
            this.uiTableLayoutPanel2.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 50F));
            this.uiTableLayoutPanel2.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 2.040816F));
            this.uiTableLayoutPanel2.Controls.Add(this.TxtTargetCycles, 3, 7);
            this.uiTableLayoutPanel2.Controls.Add(this.LabEmb1, 6, 9);
            this.uiTableLayoutPanel2.Controls.Add(this.TxtTestCycleTime, 3, 5);
            this.uiTableLayoutPanel2.Controls.Add(this.uiLabel7, 1, 1);
            this.uiTableLayoutPanel2.Controls.Add(this.uiLabel9, 1, 5);
            this.uiTableLayoutPanel2.Controls.Add(this.uiLabel10, 1, 7);
            this.uiTableLayoutPanel2.Controls.Add(this.TxtTestName, 3, 1);
            this.uiTableLayoutPanel2.Controls.Add(this.uiLabel8, 1, 3);
            this.uiTableLayoutPanel2.Controls.Add(this.TxtTestStandard, 3, 3);
            this.uiTableLayoutPanel2.Controls.Add(this.uiLabel11, 4, 5);
            this.uiTableLayoutPanel2.Controls.Add(this.ChkEmb1, 6, 1);
            this.uiTableLayoutPanel2.Controls.Add(this.RadEmb1, 6, 3);
            this.uiTableLayoutPanel2.Controls.Add(this.BtnCancel, 1, 9);
            this.uiTableLayoutPanel2.Controls.Add(this.BtnApply, 4, 9);
            this.uiTableLayoutPanel2.Controls.Add(this.BtnSettingDetail, 3, 9);
            this.uiTableLayoutPanel2.Dock = System.Windows.Forms.DockStyle.Fill;
            this.uiTableLayoutPanel2.Location = new System.Drawing.Point(0, 32);
            this.uiTableLayoutPanel2.Name = "uiTableLayoutPanel2";
            this.uiTableLayoutPanel2.RowCount = 11;
            this.uiTableLayoutPanel2.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 8.5F));
            this.uiTableLayoutPanel2.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 12F));
            this.uiTableLayoutPanel2.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 5F));
            this.uiTableLayoutPanel2.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 12F));
            this.uiTableLayoutPanel2.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 5F));
            this.uiTableLayoutPanel2.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 12F));
            this.uiTableLayoutPanel2.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 5F));
            this.uiTableLayoutPanel2.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 12F));
            this.uiTableLayoutPanel2.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 8F));
            this.uiTableLayoutPanel2.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 12F));
            this.uiTableLayoutPanel2.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 8.5F));
            this.uiTableLayoutPanel2.Size = new System.Drawing.Size(719, 378);
            this.uiTableLayoutPanel2.TabIndex = 1;
            this.uiTableLayoutPanel2.TagString = null;
            // 
            // TxtTargetCycles
            // 
            this.TxtTargetCycles.Cursor = System.Windows.Forms.Cursors.IBeam;
            this.TxtTargetCycles.Dock = System.Windows.Forms.DockStyle.Fill;
            this.TxtTargetCycles.DoubleValue = 100000D;
            this.TxtTargetCycles.Font = new System.Drawing.Font("Arial", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.TxtTargetCycles.IntValue = 100000;
            this.TxtTargetCycles.Location = new System.Drawing.Point(301, 226);
            this.TxtTargetCycles.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.TxtTargetCycles.MinimumSize = new System.Drawing.Size(1, 16);
            this.TxtTargetCycles.Name = "TxtTargetCycles";
            this.TxtTargetCycles.Padding = new System.Windows.Forms.Padding(5);
            this.TxtTargetCycles.ShowText = false;
            this.TxtTargetCycles.Size = new System.Drawing.Size(147, 35);
            this.TxtTargetCycles.TabIndex = 44;
            this.TxtTargetCycles.Text = "100000";
            this.TxtTargetCycles.TextAlignment = System.Drawing.ContentAlignment.MiddleLeft;
            this.TxtTargetCycles.Watermark = "";
            // 
            // TxtTestCycleTime
            // 
            this.TxtTestCycleTime.Cursor = System.Windows.Forms.Cursors.IBeam;
            this.TxtTestCycleTime.Dock = System.Windows.Forms.DockStyle.Fill;
            this.TxtTestCycleTime.DoubleValue = 3D;
            this.TxtTestCycleTime.Font = new System.Drawing.Font("Arial", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.TxtTestCycleTime.IntValue = 3;
            this.TxtTestCycleTime.Location = new System.Drawing.Point(301, 163);
            this.TxtTestCycleTime.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.TxtTestCycleTime.MinimumSize = new System.Drawing.Size(1, 16);
            this.TxtTestCycleTime.Name = "TxtTestCycleTime";
            this.TxtTestCycleTime.Padding = new System.Windows.Forms.Padding(5);
            this.TxtTestCycleTime.ShowText = false;
            this.TxtTestCycleTime.Size = new System.Drawing.Size(147, 35);
            this.TxtTestCycleTime.TabIndex = 44;
            this.TxtTestCycleTime.Text = "3";
            this.TxtTestCycleTime.TextAlignment = System.Drawing.ContentAlignment.MiddleLeft;
            this.TxtTestCycleTime.Watermark = "";
            // 
            // uiLabel7
            // 
            this.uiTableLayoutPanel2.SetColumnSpan(this.uiLabel7, 2);
            this.uiLabel7.Dock = System.Windows.Forms.DockStyle.Fill;
            this.uiLabel7.Font = new System.Drawing.Font("Arial Narrow", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.uiLabel7.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(48)))), ((int)(((byte)(48)))), ((int)(((byte)(48)))));
            this.uiLabel7.Location = new System.Drawing.Point(13, 32);
            this.uiLabel7.Name = "uiLabel7";
            this.uiLabel7.Size = new System.Drawing.Size(281, 45);
            this.uiLabel7.TabIndex = 33;
            this.uiLabel7.TagString = "试验名称";
            this.uiLabel7.Text = "试验名称";
            this.uiLabel7.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // uiLabel9
            // 
            this.uiTableLayoutPanel2.SetColumnSpan(this.uiLabel9, 2);
            this.uiLabel9.Dock = System.Windows.Forms.DockStyle.Fill;
            this.uiLabel9.Font = new System.Drawing.Font("Arial Narrow", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.uiLabel9.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(48)))), ((int)(((byte)(48)))), ((int)(((byte)(48)))));
            this.uiLabel9.Location = new System.Drawing.Point(13, 158);
            this.uiLabel9.Name = "uiLabel9";
            this.uiLabel9.Size = new System.Drawing.Size(281, 45);
            this.uiLabel9.TabIndex = 35;
            this.uiLabel9.Text = "频   率";
            this.uiLabel9.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // uiLabel10
            // 
            this.uiTableLayoutPanel2.SetColumnSpan(this.uiLabel10, 2);
            this.uiLabel10.Dock = System.Windows.Forms.DockStyle.Fill;
            this.uiLabel10.Font = new System.Drawing.Font("Arial Narrow", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.uiLabel10.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(48)))), ((int)(((byte)(48)))), ((int)(((byte)(48)))));
            this.uiLabel10.Location = new System.Drawing.Point(13, 221);
            this.uiLabel10.Name = "uiLabel10";
            this.uiLabel10.Size = new System.Drawing.Size(281, 45);
            this.uiLabel10.TabIndex = 36;
            this.uiLabel10.Text = "目标次数";
            this.uiLabel10.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // TxtTestName
            // 
            this.uiTableLayoutPanel2.SetColumnSpan(this.TxtTestName, 3);
            this.TxtTestName.Cursor = System.Windows.Forms.Cursors.IBeam;
            this.TxtTestName.Dock = System.Windows.Forms.DockStyle.Fill;
            this.TxtTestName.Font = new System.Drawing.Font("Arial", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.TxtTestName.Location = new System.Drawing.Point(301, 37);
            this.TxtTestName.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.TxtTestName.MinimumSize = new System.Drawing.Size(1, 16);
            this.TxtTestName.Name = "TxtTestName";
            this.TxtTestName.Padding = new System.Windows.Forms.Padding(5);
            this.TxtTestName.ShowText = false;
            this.TxtTestName.Size = new System.Drawing.Size(351, 35);
            this.TxtTestName.TabIndex = 43;
            this.TxtTestName.Text = "Long Time1";
            this.TxtTestName.TextAlignment = System.Drawing.ContentAlignment.MiddleLeft;
            this.TxtTestName.Watermark = "";
            // 
            // uiLabel8
            // 
            this.uiTableLayoutPanel2.SetColumnSpan(this.uiLabel8, 2);
            this.uiLabel8.Dock = System.Windows.Forms.DockStyle.Fill;
            this.uiLabel8.Font = new System.Drawing.Font("Arial Narrow", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.uiLabel8.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(48)))), ((int)(((byte)(48)))), ((int)(((byte)(48)))));
            this.uiLabel8.Location = new System.Drawing.Point(13, 95);
            this.uiLabel8.Name = "uiLabel8";
            this.uiLabel8.Size = new System.Drawing.Size(281, 45);
            this.uiLabel8.TabIndex = 45;
            this.uiLabel8.Text = "试验标准";
            this.uiLabel8.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // TxtTestStandard
            // 
            this.uiTableLayoutPanel2.SetColumnSpan(this.TxtTestStandard, 3);
            this.TxtTestStandard.Cursor = System.Windows.Forms.Cursors.IBeam;
            this.TxtTestStandard.Dock = System.Windows.Forms.DockStyle.Fill;
            this.TxtTestStandard.Font = new System.Drawing.Font("Arial", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.TxtTestStandard.Location = new System.Drawing.Point(301, 100);
            this.TxtTestStandard.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.TxtTestStandard.MinimumSize = new System.Drawing.Size(1, 16);
            this.TxtTestStandard.Name = "TxtTestStandard";
            this.TxtTestStandard.Padding = new System.Windows.Forms.Padding(5);
            this.TxtTestStandard.ShowText = false;
            this.TxtTestStandard.Size = new System.Drawing.Size(351, 35);
            this.TxtTestStandard.TabIndex = 46;
            this.TxtTestStandard.Text = "QRYM-2022 ";
            this.TxtTestStandard.TextAlignment = System.Drawing.ContentAlignment.MiddleLeft;
            this.TxtTestStandard.Watermark = "";
            // 
            // uiLabel11
            // 
            this.uiLabel11.Dock = System.Windows.Forms.DockStyle.Fill;
            this.uiLabel11.Font = new System.Drawing.Font("Arial Narrow", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.uiLabel11.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(48)))), ((int)(((byte)(48)))), ((int)(((byte)(48)))));
            this.uiLabel11.Location = new System.Drawing.Point(455, 158);
            this.uiLabel11.Name = "uiLabel11";
            this.uiLabel11.Size = new System.Drawing.Size(74, 45);
            this.uiLabel11.TabIndex = 48;
            this.uiLabel11.Text = "Hz";
            this.uiLabel11.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // ChkEmb1
            // 
            this.ChkEmb1.CheckBoxSize = 32;
            this.ChkEmb1.Checked = true;
            this.ChkEmb1.Cursor = System.Windows.Forms.Cursors.Hand;
            this.ChkEmb1.Font = new System.Drawing.Font("Arial", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.ChkEmb1.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(48)))), ((int)(((byte)(48)))), ((int)(((byte)(48)))));
            this.ChkEmb1.Location = new System.Drawing.Point(659, 35);
            this.ChkEmb1.MinimumSize = new System.Drawing.Size(1, 1);
            this.ChkEmb1.Name = "ChkEmb1";
            this.ChkEmb1.Size = new System.Drawing.Size(44, 39);
            this.ChkEmb1.TabIndex = 37;
            this.ChkEmb1.Text = "EMB1";
            this.ChkEmb1.Visible = false;
            // 
            // BtnCancel
            // 
            this.BtnCancel.Cursor = System.Windows.Forms.Cursors.Hand;
            this.BtnCancel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.BtnCancel.FillDisableColor = System.Drawing.Color.FromArgb(((int)(((byte)(115)))), ((int)(((byte)(179)))), ((int)(((byte)(255)))));
            this.BtnCancel.Font = new System.Drawing.Font("Arial", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.BtnCancel.Location = new System.Drawing.Point(40, 299);
            this.BtnCancel.Margin = new System.Windows.Forms.Padding(30, 3, 3, 3);
            this.BtnCancel.MinimumSize = new System.Drawing.Size(1, 1);
            this.BtnCancel.Name = "BtnCancel";
            this.BtnCancel.RectDisableColor = System.Drawing.Color.LightBlue;
            this.BtnCancel.Size = new System.Drawing.Size(174, 39);
            this.BtnCancel.TabIndex = 50;
            this.BtnCancel.Text = "取消";
            this.BtnCancel.TipsFont = new System.Drawing.Font("宋体", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.BtnCancel.Click += new System.EventHandler(this.BtnCancel_Click);
            // 
            // BtnApply
            // 
            this.uiTableLayoutPanel2.SetColumnSpan(this.BtnApply, 2);
            this.BtnApply.Cursor = System.Windows.Forms.Cursors.Hand;
            this.BtnApply.Dock = System.Windows.Forms.DockStyle.Fill;
            this.BtnApply.Font = new System.Drawing.Font("Arial", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.BtnApply.Location = new System.Drawing.Point(535, 299);
            this.BtnApply.Margin = new System.Windows.Forms.Padding(3, 3, 30, 3);
            this.BtnApply.MinimumSize = new System.Drawing.Size(1, 1);
            this.BtnApply.Name = "BtnApply";
            this.BtnApply.Size = new System.Drawing.Size(141, 39);
            this.BtnApply.TabIndex = 49;
            this.BtnApply.Text = "确认";
            this.BtnApply.TipsFont = new System.Drawing.Font("宋体", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.BtnApply.Click += new System.EventHandler(this.BtnApply_Click);
            // 
            // BtnSettingDetail
            // 
            this.uiTableLayoutPanel2.SetColumnSpan(this.BtnSettingDetail, 2);
            this.BtnSettingDetail.Cursor = System.Windows.Forms.Cursors.Hand;
            this.BtnSettingDetail.FillDisableColor = System.Drawing.Color.FromArgb(((int)(((byte)(115)))), ((int)(((byte)(179)))), ((int)(((byte)(255)))));
            this.BtnSettingDetail.Font = new System.Drawing.Font("Arial", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.BtnSettingDetail.Location = new System.Drawing.Point(300, 299);
            this.BtnSettingDetail.MinimumSize = new System.Drawing.Size(1, 1);
            this.BtnSettingDetail.Name = "BtnSettingDetail";
            this.BtnSettingDetail.RectDisableColor = System.Drawing.Color.LightBlue;
            this.BtnSettingDetail.Size = new System.Drawing.Size(167, 39);
            this.BtnSettingDetail.TabIndex = 47;
            this.BtnSettingDetail.Text = "详细";
            this.BtnSettingDetail.TipsFont = new System.Drawing.Font("宋体", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.BtnSettingDetail.Visible = false;
            this.BtnSettingDetail.Click += new System.EventHandler(this.BtnSettingDetail_Click);
            // 
            // uiGroupBox1
            // 
            this.uiGroupBox1.Controls.Add(this.uiTableLayoutPanel2);
            this.uiGroupBox1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.uiGroupBox1.Font = new System.Drawing.Font("Arial Narrow", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.uiGroupBox1.Location = new System.Drawing.Point(22, 23);
            this.uiGroupBox1.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.uiGroupBox1.MinimumSize = new System.Drawing.Size(1, 1);
            this.uiGroupBox1.Name = "uiGroupBox1";
            this.uiGroupBox1.Padding = new System.Windows.Forms.Padding(0, 32, 0, 0);
            this.uiTableLayoutPanelMain.SetRowSpan(this.uiGroupBox1, 4);
            this.uiGroupBox1.Size = new System.Drawing.Size(719, 410);
            this.uiGroupBox1.TabIndex = 2;
            this.uiGroupBox1.Text = "试验设置";
            this.uiGroupBox1.TextAlignment = System.Drawing.ContentAlignment.MiddleLeft;
            // 
            // zedGraphRealChart
            // 
            this.zedGraphRealChart.Dock = System.Windows.Forms.DockStyle.Fill;
            this.zedGraphRealChart.IsEnableHZoom = false;
            this.zedGraphRealChart.IsEnableVZoom = false;
            this.zedGraphRealChart.IsEnableWheelZoom = false;
            this.zedGraphRealChart.Location = new System.Drawing.Point(28, 18);
            this.zedGraphRealChart.Margin = new System.Windows.Forms.Padding(8);
            this.zedGraphRealChart.Name = "zedGraphRealChart";
            this.zedGraphRealChart.ScrollGrace = 0D;
            this.zedGraphRealChart.ScrollMaxX = 0D;
            this.zedGraphRealChart.ScrollMaxY = 0D;
            this.zedGraphRealChart.ScrollMaxY2 = 0D;
            this.zedGraphRealChart.ScrollMinX = 0D;
            this.zedGraphRealChart.ScrollMinY = 0D;
            this.zedGraphRealChart.ScrollMinY2 = 0D;
            this.zedGraphRealChart.Size = new System.Drawing.Size(1174, 428);
            this.zedGraphRealChart.TabIndex = 4;
            this.zedGraphRealChart.UseExtendedPrintDialog = true;
            this.zedGraphRealChart.ContextMenuBuilder += new ZedGraph.ZedGraphControl.ContextMenuBuilderEventHandler(this.zedGraphRealChart_ContextMenuBuilder);
            // 
            // uiGroupAllControl
            // 
            this.uiTableLayoutPanelMain.SetColumnSpan(this.uiGroupAllControl, 4);
            this.uiGroupAllControl.Controls.Add(this.uiTableLayoutPanel7);
            this.uiGroupAllControl.Dock = System.Windows.Forms.DockStyle.Fill;
            this.uiGroupAllControl.Font = new System.Drawing.Font("宋体", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.uiGroupAllControl.Location = new System.Drawing.Point(767, 897);
            this.uiGroupAllControl.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.uiGroupAllControl.MinimumSize = new System.Drawing.Size(1, 1);
            this.uiGroupAllControl.Name = "uiGroupAllControl";
            this.uiGroupAllControl.Padding = new System.Windows.Forms.Padding(0, 32, 0, 0);
            this.uiGroupAllControl.Size = new System.Drawing.Size(1230, 86);
            this.uiGroupAllControl.TabIndex = 6;
            this.uiGroupAllControl.Text = null;
            this.uiGroupAllControl.TextAlignment = System.Drawing.ContentAlignment.MiddleLeft;
            // 
            // uiTableLayoutPanel7
            // 
            this.uiTableLayoutPanel7.BackColor = System.Drawing.Color.Transparent;
            this.uiTableLayoutPanel7.ColumnCount = 7;
            this.uiTableLayoutPanel7.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.uiTableLayoutPanel7.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 16F));
            this.uiTableLayoutPanel7.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 26F));
            this.uiTableLayoutPanel7.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 16F));
            this.uiTableLayoutPanel7.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 26F));
            this.uiTableLayoutPanel7.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 16F));
            this.uiTableLayoutPanel7.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.uiTableLayoutPanel7.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.uiTableLayoutPanel7.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.uiTableLayoutPanel7.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.uiTableLayoutPanel7.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.uiTableLayoutPanel7.Controls.Add(this.BtnStop, 5, 1);
            this.uiTableLayoutPanel7.Controls.Add(this.BtnStartTest, 3, 1);
            this.uiTableLayoutPanel7.Controls.Add(this.BtnAutoLearn, 2, 1);
            this.uiTableLayoutPanel7.Controls.Add(this.BtnPause, 1, 1);
            this.uiTableLayoutPanel7.Dock = System.Windows.Forms.DockStyle.Fill;
            this.uiTableLayoutPanel7.Location = new System.Drawing.Point(0, 32);
            this.uiTableLayoutPanel7.Name = "uiTableLayoutPanel7";
            this.uiTableLayoutPanel7.RowCount = 3;
            this.uiTableLayoutPanel7.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 10F));
            this.uiTableLayoutPanel7.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 75F));
            this.uiTableLayoutPanel7.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 15F));
            this.uiTableLayoutPanel7.Size = new System.Drawing.Size(1230, 54);
            this.uiTableLayoutPanel7.TabIndex = 35;
            this.uiTableLayoutPanel7.TagString = null;
            // 
            // BtnStop
            // 
            this.BtnStop.Cursor = System.Windows.Forms.Cursors.Hand;
            this.BtnStop.Dock = System.Windows.Forms.DockStyle.Fill;
            this.BtnStop.FillColor = System.Drawing.Color.IndianRed;
            this.BtnStop.FillHoverColor = System.Drawing.Color.Red;
            this.BtnStop.FillPressColor = System.Drawing.Color.Red;
            this.BtnStop.FillSelectedColor = System.Drawing.Color.Red;
            this.BtnStop.Font = new System.Drawing.Font("Arial", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.BtnStop.Location = new System.Drawing.Point(1021, 8);
            this.BtnStop.MinimumSize = new System.Drawing.Size(1, 1);
            this.BtnStop.Name = "BtnStop";
            this.BtnStop.RectColor = System.Drawing.Color.Red;
            this.BtnStop.RectHoverColor = System.Drawing.Color.Red;
            this.BtnStop.RectPressColor = System.Drawing.Color.Red;
            this.BtnStop.RectSelectedColor = System.Drawing.Color.Red;
            this.BtnStop.Size = new System.Drawing.Size(184, 34);
            this.BtnStop.TabIndex = 49;
            this.BtnStop.Text = "停止试验";
            this.BtnStop.TipsFont = new System.Drawing.Font("宋体", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.BtnStop.Click += new System.EventHandler(this.BtnStop_Click);
            // 
            // BtnStartTest
            // 
            this.BtnStartTest.Cursor = System.Windows.Forms.Cursors.Hand;
            this.BtnStartTest.Dock = System.Windows.Forms.DockStyle.Fill;
            this.BtnStartTest.FillDisableColor = System.Drawing.Color.FromArgb(((int)(((byte)(115)))), ((int)(((byte)(179)))), ((int)(((byte)(255)))));
            this.BtnStartTest.Font = new System.Drawing.Font("Arial", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.BtnStartTest.Location = new System.Drawing.Point(522, 8);
            this.BtnStartTest.MinimumSize = new System.Drawing.Size(1, 1);
            this.BtnStartTest.Name = "BtnStartTest";
            this.BtnStartTest.RectDisableColor = System.Drawing.Color.LightBlue;
            this.BtnStartTest.Size = new System.Drawing.Size(184, 34);
            this.BtnStartTest.TabIndex = 50;
            this.BtnStartTest.Text = "开始试验";
            this.BtnStartTest.TipsFont = new System.Drawing.Font("宋体", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.BtnStartTest.Click += new System.EventHandler(this.BtnStartTest_Click);
            // 
            // BtnAutoLearn
            // 
            this.BtnAutoLearn.Cursor = System.Windows.Forms.Cursors.Hand;
            this.BtnAutoLearn.FillDisableColor = System.Drawing.Color.FromArgb(((int)(((byte)(115)))), ((int)(((byte)(179)))), ((int)(((byte)(255)))));
            this.BtnAutoLearn.Font = new System.Drawing.Font("Arial", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.BtnAutoLearn.Location = new System.Drawing.Point(213, 8);
            this.BtnAutoLearn.MinimumSize = new System.Drawing.Size(1, 1);
            this.BtnAutoLearn.Name = "BtnAutoLearn";
            this.BtnAutoLearn.Radius = 1;
            this.BtnAutoLearn.RectDisableColor = System.Drawing.Color.LightBlue;
            this.BtnAutoLearn.Size = new System.Drawing.Size(200, 34);
            this.BtnAutoLearn.TabIndex = 52;
            this.BtnAutoLearn.Text = "自学习";
            this.BtnAutoLearn.TipsFont = new System.Drawing.Font("宋体", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.BtnAutoLearn.Visible = false;
            this.BtnAutoLearn.Click += new System.EventHandler(this.BtnAutoLearn_Click);
            // 
            // BtnPause
            // 
            this.BtnPause.Cursor = System.Windows.Forms.Cursors.Hand;
            this.BtnPause.Dock = System.Windows.Forms.DockStyle.Fill;
            this.BtnPause.FillDisableColor = System.Drawing.Color.FromArgb(((int)(((byte)(115)))), ((int)(((byte)(179)))), ((int)(((byte)(255)))));
            this.BtnPause.Font = new System.Drawing.Font("Arial", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.BtnPause.Location = new System.Drawing.Point(23, 8);
            this.BtnPause.MinimumSize = new System.Drawing.Size(1, 1);
            this.BtnPause.Name = "BtnPause";
            this.BtnPause.Radius = 1;
            this.BtnPause.RectDisableColor = System.Drawing.Color.LightBlue;
            this.BtnPause.Size = new System.Drawing.Size(184, 34);
            this.BtnPause.TabIndex = 53;
            this.BtnPause.Text = "暂停";
            this.BtnPause.TipsFont = new System.Drawing.Font("宋体", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.BtnPause.Click += new System.EventHandler(this.BtnPause_Click);
            // 
            // uiGroupInfo
            // 
            this.uiGroupInfo.Controls.Add(this.z);
            this.uiGroupInfo.Dock = System.Windows.Forms.DockStyle.Fill;
            this.uiGroupInfo.Font = new System.Drawing.Font("Arial Narrow", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.uiGroupInfo.Location = new System.Drawing.Point(22, 443);
            this.uiGroupInfo.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.uiGroupInfo.MinimumSize = new System.Drawing.Size(1, 1);
            this.uiGroupInfo.Name = "uiGroupInfo";
            this.uiGroupInfo.Padding = new System.Windows.Forms.Padding(0, 32, 0, 0);
            this.uiTableLayoutPanelMain.SetRowSpan(this.uiGroupInfo, 10);
            this.uiGroupInfo.Size = new System.Drawing.Size(719, 540);
            this.uiGroupInfo.TabIndex = 7;
            this.uiGroupInfo.Text = "信息";
            this.uiGroupInfo.TextAlignment = System.Drawing.ContentAlignment.MiddleLeft;
            // 
            // z
            // 
            this.z.BackColor = System.Drawing.Color.Transparent;
            this.z.ColumnCount = 5;
            this.z.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.z.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 30F));
            this.z.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 40F));
            this.z.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 30F));
            this.z.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.z.Controls.Add(this.BtnRunLog, 1, 3);
            this.z.Controls.Add(this.RtbInfo, 1, 1);
            this.z.Controls.Add(this.BtnErrorLog, 3, 3);
            this.z.Dock = System.Windows.Forms.DockStyle.Fill;
            this.z.Location = new System.Drawing.Point(0, 32);
            this.z.Name = "z";
            this.z.RowCount = 5;
            this.z.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 10F));
            this.z.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 91F));
            this.z.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.z.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 9F));
            this.z.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.z.Size = new System.Drawing.Size(719, 508);
            this.z.TabIndex = 37;
            this.z.TagString = null;
            // 
            // BtnRunLog
            // 
            this.BtnRunLog.Cursor = System.Windows.Forms.Cursors.Hand;
            this.BtnRunLog.Dock = System.Windows.Forms.DockStyle.Fill;
            this.BtnRunLog.Font = new System.Drawing.Font("Arial", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.BtnRunLog.Location = new System.Drawing.Point(23, 449);
            this.BtnRunLog.MinimumSize = new System.Drawing.Size(1, 1);
            this.BtnRunLog.Name = "BtnRunLog";
            this.BtnRunLog.Radius = 4;
            this.BtnRunLog.Size = new System.Drawing.Size(197, 35);
            this.BtnRunLog.TabIndex = 52;
            this.BtnRunLog.Text = "运行日志";
            this.BtnRunLog.TipsFont = new System.Drawing.Font("宋体", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.BtnRunLog.Click += new System.EventHandler(this.BtnRunLog_Click);
            // 
            // RtbInfo
            // 
            this.z.SetColumnSpan(this.RtbInfo, 3);
            this.RtbInfo.Dock = System.Windows.Forms.DockStyle.Fill;
            this.RtbInfo.FillColor = System.Drawing.Color.White;
            this.RtbInfo.Font = new System.Drawing.Font("Arial Narrow", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.RtbInfo.Location = new System.Drawing.Point(24, 15);
            this.RtbInfo.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.RtbInfo.MinimumSize = new System.Drawing.Size(1, 1);
            this.RtbInfo.Name = "RtbInfo";
            this.RtbInfo.Padding = new System.Windows.Forms.Padding(2);
            this.RtbInfo.Radius = 1;
            this.RtbInfo.ShowText = false;
            this.RtbInfo.Size = new System.Drawing.Size(669, 406);
            this.RtbInfo.TabIndex = 0;
            this.RtbInfo.TextAlignment = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // BtnErrorLog
            // 
            this.BtnErrorLog.Cursor = System.Windows.Forms.Cursors.Hand;
            this.BtnErrorLog.Dock = System.Windows.Forms.DockStyle.Fill;
            this.BtnErrorLog.Font = new System.Drawing.Font("Arial", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.BtnErrorLog.Location = new System.Drawing.Point(497, 449);
            this.BtnErrorLog.MinimumSize = new System.Drawing.Size(1, 1);
            this.BtnErrorLog.Name = "BtnErrorLog";
            this.BtnErrorLog.Radius = 4;
            this.BtnErrorLog.Size = new System.Drawing.Size(197, 35);
            this.BtnErrorLog.TabIndex = 53;
            this.BtnErrorLog.Text = "错误日志";
            this.BtnErrorLog.TipsFont = new System.Drawing.Font("宋体", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.BtnErrorLog.Click += new System.EventHandler(this.BtnErrorLog_Click);
            // 
            // dgvRealData
            // 
            this.dgvRealData.AllowUserToAddRows = false;
            this.dgvRealData.AllowUserToDeleteRows = false;
            dataGridViewCellStyle1.Alignment = System.Windows.Forms.DataGridViewContentAlignment.MiddleCenter;
            dataGridViewCellStyle1.BackColor = System.Drawing.Color.WhiteSmoke;
            dataGridViewCellStyle1.Font = new System.Drawing.Font("Arial Narrow", 8.872038F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.dgvRealData.AlternatingRowsDefaultCellStyle = dataGridViewCellStyle1;
            this.dgvRealData.BackgroundColor = System.Drawing.Color.White;
            this.dgvRealData.ColumnHeadersBorderStyle = System.Windows.Forms.DataGridViewHeaderBorderStyle.Single;
            dataGridViewCellStyle2.Alignment = System.Windows.Forms.DataGridViewContentAlignment.MiddleCenter;
            dataGridViewCellStyle2.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(80)))), ((int)(((byte)(160)))), ((int)(((byte)(255)))));
            dataGridViewCellStyle2.Font = new System.Drawing.Font("Arial", 8.872038F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            dataGridViewCellStyle2.ForeColor = System.Drawing.Color.White;
            dataGridViewCellStyle2.SelectionBackColor = System.Drawing.SystemColors.Highlight;
            dataGridViewCellStyle2.SelectionForeColor = System.Drawing.SystemColors.HighlightText;
            dataGridViewCellStyle2.WrapMode = System.Windows.Forms.DataGridViewTriState.True;
            this.dgvRealData.ColumnHeadersDefaultCellStyle = dataGridViewCellStyle2;
            this.dgvRealData.ColumnHeadersHeight = 35;
            dataGridViewCellStyle3.Alignment = System.Windows.Forms.DataGridViewContentAlignment.MiddleLeft;
            dataGridViewCellStyle3.BackColor = System.Drawing.SystemColors.Window;
            dataGridViewCellStyle3.Font = new System.Drawing.Font("宋体", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            dataGridViewCellStyle3.ForeColor = System.Drawing.SystemColors.ControlText;
            dataGridViewCellStyle3.SelectionBackColor = System.Drawing.SystemColors.Highlight;
            dataGridViewCellStyle3.SelectionForeColor = System.Drawing.SystemColors.HighlightText;
            dataGridViewCellStyle3.WrapMode = System.Windows.Forms.DataGridViewTriState.False;
            this.dgvRealData.DefaultCellStyle = dataGridViewCellStyle3;
            this.dgvRealData.Dock = System.Windows.Forms.DockStyle.Fill;
            this.dgvRealData.EnableHeadersVisualStyles = false;
            this.dgvRealData.Font = new System.Drawing.Font("宋体", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.dgvRealData.GridColor = System.Drawing.Color.FromArgb(((int)(((byte)(80)))), ((int)(((byte)(160)))), ((int)(((byte)(255)))));
            this.dgvRealData.Location = new System.Drawing.Point(766, 537);
            this.dgvRealData.Name = "dgvRealData";
            dataGridViewCellStyle4.Alignment = System.Windows.Forms.DataGridViewContentAlignment.MiddleCenter;
            dataGridViewCellStyle4.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(235)))), ((int)(((byte)(243)))), ((int)(((byte)(255)))));
            dataGridViewCellStyle4.Font = new System.Drawing.Font("Arial", 8.872038F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            dataGridViewCellStyle4.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(48)))), ((int)(((byte)(48)))), ((int)(((byte)(48)))));
            dataGridViewCellStyle4.SelectionBackColor = System.Drawing.Color.FromArgb(((int)(((byte)(80)))), ((int)(((byte)(160)))), ((int)(((byte)(255)))));
            dataGridViewCellStyle4.SelectionForeColor = System.Drawing.Color.White;
            dataGridViewCellStyle4.WrapMode = System.Windows.Forms.DataGridViewTriState.True;
            this.dgvRealData.RowHeadersDefaultCellStyle = dataGridViewCellStyle4;
            this.dgvRealData.RowHeadersWidth = 30;
            dataGridViewCellStyle5.Alignment = System.Windows.Forms.DataGridViewContentAlignment.MiddleCenter;
            dataGridViewCellStyle5.BackColor = System.Drawing.Color.Lavender;
            dataGridViewCellStyle5.Font = new System.Drawing.Font("Arial", 8.872038F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.dgvRealData.RowsDefaultCellStyle = dataGridViewCellStyle5;
            this.uiTableLayoutPanelMain.SetRowSpan(this.dgvRealData, 5);
            this.dgvRealData.RowTemplate.Height = 35;
            this.dgvRealData.SelectedIndex = -1;
            this.dgvRealData.Size = new System.Drawing.Size(682, 249);
            this.dgvRealData.StripeEvenColor = System.Drawing.Color.Lavender;
            this.dgvRealData.StripeOddColor = System.Drawing.Color.WhiteSmoke;
            this.dgvRealData.TabIndex = 0;
            this.dgvRealData.CellContentClick += new System.Windows.Forms.DataGridViewCellEventHandler(this.dgvRealData_CellContentClick);
            // 
            // uiGroupCurve
            // 
            this.uiTableLayoutPanelMain.SetColumnSpan(this.uiGroupCurve, 4);
            this.uiGroupCurve.Controls.Add(this.uiTableLayoutPanel6);
            this.uiGroupCurve.Dock = System.Windows.Forms.DockStyle.Fill;
            this.uiGroupCurve.Font = new System.Drawing.Font("宋体", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.uiGroupCurve.Location = new System.Drawing.Point(767, 23);
            this.uiGroupCurve.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.uiGroupCurve.MinimumSize = new System.Drawing.Size(1, 1);
            this.uiGroupCurve.Name = "uiGroupCurve";
            this.uiGroupCurve.Padding = new System.Windows.Forms.Padding(0, 32, 0, 0);
            this.uiTableLayoutPanelMain.SetRowSpan(this.uiGroupCurve, 5);
            this.uiGroupCurve.Size = new System.Drawing.Size(1230, 506);
            this.uiGroupCurve.TabIndex = 36;
            this.uiGroupCurve.Text = null;
            this.uiGroupCurve.TextAlignment = System.Drawing.ContentAlignment.MiddleLeft;
            // 
            // uiTableLayoutPanel6
            // 
            this.uiTableLayoutPanel6.BackColor = System.Drawing.Color.Transparent;
            this.uiTableLayoutPanel6.ColumnCount = 3;
            this.uiTableLayoutPanel6.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.uiTableLayoutPanel6.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.uiTableLayoutPanel6.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.uiTableLayoutPanel6.Controls.Add(this.zedGraphRealChart, 1, 1);
            this.uiTableLayoutPanel6.Dock = System.Windows.Forms.DockStyle.Fill;
            this.uiTableLayoutPanel6.Location = new System.Drawing.Point(0, 32);
            this.uiTableLayoutPanel6.Name = "uiTableLayoutPanel6";
            this.uiTableLayoutPanel6.RowCount = 3;
            this.uiTableLayoutPanel6.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 10F));
            this.uiTableLayoutPanel6.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.uiTableLayoutPanel6.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.uiTableLayoutPanel6.Size = new System.Drawing.Size(1230, 474);
            this.uiTableLayoutPanel6.TabIndex = 36;
            this.uiTableLayoutPanel6.TagString = null;
            // 
            // LedRunTime
            // 
            this.LedRunTime.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(0)))), ((int)(((byte)(70)))), ((int)(((byte)(0)))));
            this.LedRunTime.BorderColor = System.Drawing.Color.FromArgb(((int)(((byte)(0)))), ((int)(((byte)(70)))), ((int)(((byte)(0)))));
            this.LedRunTime.BorderInColor = System.Drawing.Color.FromArgb(((int)(((byte)(0)))), ((int)(((byte)(70)))), ((int)(((byte)(0)))));
            this.LedRunTime.CharCount = 11;
            this.LedRunTime.Dock = System.Windows.Forms.DockStyle.Fill;
            this.LedRunTime.Font = new System.Drawing.Font("微软雅黑", 10.5782F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.LedRunTime.ForeColor = System.Drawing.Color.Lime;
            this.LedRunTime.ImeMode = System.Windows.Forms.ImeMode.NoControl;
            this.LedRunTime.IntervalIn = 2;
            this.LedRunTime.IntervalV = 2;
            this.LedRunTime.LedBackColor = System.Drawing.Color.FromArgb(((int)(((byte)(0)))), ((int)(((byte)(70)))), ((int)(((byte)(0)))));
            this.LedRunTime.Location = new System.Drawing.Point(11, 6);
            this.LedRunTime.Name = "LedRunTime";
            this.LedRunTime.Size = new System.Drawing.Size(402, 48);
            this.LedRunTime.TabIndex = 0;
            this.LedRunTime.Text = "00D 00H 00M";
            // 
            // uiTableLayoutPanel4
            // 
            this.uiTableLayoutPanel4.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(0)))), ((int)(((byte)(70)))), ((int)(((byte)(0)))));
            this.uiTableLayoutPanel4.ColumnCount = 3;
            this.uiTableLayoutPanelMain.SetColumnSpan(this.uiTableLayoutPanel4, 2);
            this.uiTableLayoutPanel4.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 2F));
            this.uiTableLayoutPanel4.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 96F));
            this.uiTableLayoutPanel4.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 2F));
            this.uiTableLayoutPanel4.Controls.Add(this.LedRunTime, 1, 1);
            this.uiTableLayoutPanel4.Dock = System.Windows.Forms.DockStyle.Fill;
            this.uiTableLayoutPanel4.Location = new System.Drawing.Point(1572, 537);
            this.uiTableLayoutPanel4.Name = "uiTableLayoutPanel4";
            this.uiTableLayoutPanel4.RowCount = 3;
            this.uiTableLayoutPanel4.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 5F));
            this.uiTableLayoutPanel4.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 90F));
            this.uiTableLayoutPanel4.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 5F));
            this.uiTableLayoutPanel4.Size = new System.Drawing.Size(426, 61);
            this.uiTableLayoutPanel4.TabIndex = 37;
            this.uiTableLayoutPanel4.TagString = null;
            // 
            // uiTableLayoutPanel9
            // 
            this.uiTableLayoutPanel9.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(0)))), ((int)(((byte)(70)))), ((int)(((byte)(0)))));
            this.uiTableLayoutPanel9.ColumnCount = 3;
            this.uiTableLayoutPanelMain.SetColumnSpan(this.uiTableLayoutPanel9, 2);
            this.uiTableLayoutPanel9.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 2F));
            this.uiTableLayoutPanel9.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 96F));
            this.uiTableLayoutPanel9.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 2F));
            this.uiTableLayoutPanel9.Controls.Add(this.LedRunCycles, 1, 1);
            this.uiTableLayoutPanel9.Dock = System.Windows.Forms.DockStyle.Fill;
            this.uiTableLayoutPanel9.Location = new System.Drawing.Point(1572, 631);
            this.uiTableLayoutPanel9.Name = "uiTableLayoutPanel9";
            this.uiTableLayoutPanel9.RowCount = 3;
            this.uiTableLayoutPanel9.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 5F));
            this.uiTableLayoutPanel9.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 90F));
            this.uiTableLayoutPanel9.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 5F));
            this.uiTableLayoutPanel9.Size = new System.Drawing.Size(426, 61);
            this.uiTableLayoutPanel9.TabIndex = 38;
            this.uiTableLayoutPanel9.TagString = null;
            // 
            // LedRunCycles
            // 
            this.LedRunCycles.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(0)))), ((int)(((byte)(70)))), ((int)(((byte)(0)))));
            this.LedRunCycles.BorderColor = System.Drawing.Color.FromArgb(((int)(((byte)(0)))), ((int)(((byte)(70)))), ((int)(((byte)(0)))));
            this.LedRunCycles.BorderInColor = System.Drawing.Color.FromArgb(((int)(((byte)(0)))), ((int)(((byte)(70)))), ((int)(((byte)(0)))));
            this.LedRunCycles.CharCount = 8;
            this.LedRunCycles.Dock = System.Windows.Forms.DockStyle.Fill;
            this.LedRunCycles.Font = new System.Drawing.Font("微软雅黑", 16.03791F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.LedRunCycles.ForeColor = System.Drawing.Color.Lime;
            this.LedRunCycles.ImeMode = System.Windows.Forms.ImeMode.NoControl;
            this.LedRunCycles.IntervalIn = 2;
            this.LedRunCycles.IntervalV = 2;
            this.LedRunCycles.LedBackColor = System.Drawing.Color.FromArgb(((int)(((byte)(0)))), ((int)(((byte)(70)))), ((int)(((byte)(0)))));
            this.LedRunCycles.Location = new System.Drawing.Point(11, 6);
            this.LedRunCycles.Name = "LedRunCycles";
            this.LedRunCycles.Size = new System.Drawing.Size(402, 48);
            this.LedRunCycles.TabIndex = 0;
            this.LedRunCycles.Text = "0";
            // 
            // uiTableLayoutPanel10
            // 
            this.uiTableLayoutPanel10.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(0)))), ((int)(((byte)(70)))), ((int)(((byte)(0)))));
            this.uiTableLayoutPanel10.ColumnCount = 3;
            this.uiTableLayoutPanelMain.SetColumnSpan(this.uiTableLayoutPanel10, 2);
            this.uiTableLayoutPanel10.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 2F));
            this.uiTableLayoutPanel10.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 96F));
            this.uiTableLayoutPanel10.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 2F));
            this.uiTableLayoutPanel10.Controls.Add(this.LedLastCycles, 1, 1);
            this.uiTableLayoutPanel10.Dock = System.Windows.Forms.DockStyle.Fill;
            this.uiTableLayoutPanel10.Location = new System.Drawing.Point(1572, 724);
            this.uiTableLayoutPanel10.Name = "uiTableLayoutPanel10";
            this.uiTableLayoutPanel10.RowCount = 3;
            this.uiTableLayoutPanel10.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 5F));
            this.uiTableLayoutPanel10.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 90F));
            this.uiTableLayoutPanel10.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 5F));
            this.uiTableLayoutPanel10.Size = new System.Drawing.Size(426, 62);
            this.uiTableLayoutPanel10.TabIndex = 39;
            this.uiTableLayoutPanel10.TagString = null;
            // 
            // LedLastCycles
            // 
            this.LedLastCycles.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(0)))), ((int)(((byte)(70)))), ((int)(((byte)(0)))));
            this.LedLastCycles.BorderColor = System.Drawing.Color.FromArgb(((int)(((byte)(0)))), ((int)(((byte)(70)))), ((int)(((byte)(0)))));
            this.LedLastCycles.BorderInColor = System.Drawing.Color.FromArgb(((int)(((byte)(0)))), ((int)(((byte)(70)))), ((int)(((byte)(0)))));
            this.LedLastCycles.CharCount = 8;
            this.LedLastCycles.Dock = System.Windows.Forms.DockStyle.Fill;
            this.LedLastCycles.Font = new System.Drawing.Font("微软雅黑", 16.03791F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.LedLastCycles.ForeColor = System.Drawing.Color.Gold;
            this.LedLastCycles.ImeMode = System.Windows.Forms.ImeMode.NoControl;
            this.LedLastCycles.IntervalIn = 2;
            this.LedLastCycles.IntervalV = 2;
            this.LedLastCycles.LedBackColor = System.Drawing.Color.FromArgb(((int)(((byte)(0)))), ((int)(((byte)(70)))), ((int)(((byte)(0)))));
            this.LedLastCycles.Location = new System.Drawing.Point(11, 6);
            this.LedLastCycles.Name = "LedLastCycles";
            this.LedLastCycles.Size = new System.Drawing.Size(402, 49);
            this.LedLastCycles.TabIndex = 0;
            this.LedLastCycles.Text = "1000000";
            // 
            // ProcBar
            // 
            this.uiTableLayoutPanelMain.SetColumnSpan(this.ProcBar, 3);
            this.ProcBar.Dock = System.Windows.Forms.DockStyle.Fill;
            this.ProcBar.FillColor = System.Drawing.Color.FromArgb(((int)(((byte)(235)))), ((int)(((byte)(243)))), ((int)(((byte)(255)))));
            this.ProcBar.Font = new System.Drawing.Font("Arial Narrow", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.ProcBar.Location = new System.Drawing.Point(766, 820);
            this.ProcBar.MinimumSize = new System.Drawing.Size(3, 3);
            this.ProcBar.Name = "ProcBar";
            this.ProcBar.Size = new System.Drawing.Size(1154, 51);
            this.ProcBar.Style = Sunny.UI.UIStyle.Custom;
            this.ProcBar.TabIndex = 41;
            this.ProcBar.Text = "uiProcessBar1";
            // 
            // timerProgressDisp
            // 
            this.timerProgressDisp.Interval = 1000;
            this.timerProgressDisp.Tick += new System.EventHandler(this.timerProgressDisp_Tick);
            // 
            // uiTableLayoutPanelMain
            // 
            this.uiTableLayoutPanelMain.ColumnCount = 8;
            this.uiTableLayoutPanelMain.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 18F));
            this.uiTableLayoutPanelMain.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 37F));
            this.uiTableLayoutPanelMain.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 18F));
            this.uiTableLayoutPanelMain.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 35F));
            this.uiTableLayoutPanelMain.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 6F));
            this.uiTableLayoutPanelMain.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 18F));
            this.uiTableLayoutPanelMain.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 4F));
            this.uiTableLayoutPanelMain.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 18F));
            this.uiTableLayoutPanelMain.Controls.Add(this.uiTableLayoutPanel9, 5, 8);
            this.uiTableLayoutPanelMain.Controls.Add(this.dgvRealData, 3, 6);
            this.uiTableLayoutPanelMain.Controls.Add(this.uiGroupAllControl, 3, 14);
            this.uiTableLayoutPanelMain.Controls.Add(this.uiGroupCurve, 3, 1);
            this.uiTableLayoutPanelMain.Controls.Add(this.uiGroupBox1, 1, 1);
            this.uiTableLayoutPanelMain.Controls.Add(this.uiGroupInfo, 1, 5);
            this.uiTableLayoutPanelMain.Controls.Add(this.uiTableLayoutPanel4, 5, 6);
            this.uiTableLayoutPanelMain.Controls.Add(this.ProcBar, 3, 12);
            this.uiTableLayoutPanelMain.Controls.Add(this.uiTableLayoutPanel10, 5, 10);
            this.uiTableLayoutPanelMain.Controls.Add(this.uiLabel1, 4, 6);
            this.uiTableLayoutPanelMain.Controls.Add(this.uiLabel2, 4, 8);
            this.uiTableLayoutPanelMain.Controls.Add(this.uiLabel3, 4, 10);
            this.uiTableLayoutPanelMain.Controls.Add(this.AlertEmb1, 6, 12);
            this.uiTableLayoutPanelMain.Dock = System.Windows.Forms.DockStyle.Fill;
            this.uiTableLayoutPanelMain.Location = new System.Drawing.Point(0, 0);
            this.uiTableLayoutPanelMain.Name = "uiTableLayoutPanelMain";
            this.uiTableLayoutPanelMain.RowCount = 16;
            this.uiTableLayoutPanelMain.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 18F));
            this.uiTableLayoutPanelMain.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 11.01064F));
            this.uiTableLayoutPanelMain.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 11.01064F));
            this.uiTableLayoutPanelMain.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 11.01064F));
            this.uiTableLayoutPanelMain.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 11.01064F));
            this.uiTableLayoutPanelMain.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 10.00967F));
            this.uiTableLayoutPanelMain.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 7.00677F));
            this.uiTableLayoutPanelMain.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 2.901354F));
            this.uiTableLayoutPanelMain.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 7.059961F));
            this.uiTableLayoutPanelMain.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 2.804642F));
            this.uiTableLayoutPanelMain.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 7.156673F));
            this.uiTableLayoutPanelMain.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 3.002901F));
            this.uiTableLayoutPanelMain.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 6.005803F));
            this.uiTableLayoutPanelMain.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 18F));
            this.uiTableLayoutPanelMain.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 10.00967F));
            this.uiTableLayoutPanelMain.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 9F));
            this.uiTableLayoutPanelMain.Size = new System.Drawing.Size(2021, 1007);
            this.uiTableLayoutPanelMain.TabIndex = 42;
            this.uiTableLayoutPanelMain.TagString = null;
            // 
            // uiLabel1
            // 
            this.uiLabel1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.uiLabel1.Font = new System.Drawing.Font("宋体", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.uiLabel1.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(48)))), ((int)(((byte)(48)))), ((int)(((byte)(48)))));
            this.uiLabel1.Location = new System.Drawing.Point(1454, 534);
            this.uiLabel1.Name = "uiLabel1";
            this.uiLabel1.Size = new System.Drawing.Size(112, 67);
            this.uiLabel1.TabIndex = 42;
            this.uiLabel1.Text = "运行时间";
            this.uiLabel1.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // uiLabel2
            // 
            this.uiLabel2.Dock = System.Windows.Forms.DockStyle.Fill;
            this.uiLabel2.Font = new System.Drawing.Font("宋体", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.uiLabel2.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(48)))), ((int)(((byte)(48)))), ((int)(((byte)(48)))));
            this.uiLabel2.Location = new System.Drawing.Point(1454, 628);
            this.uiLabel2.Name = "uiLabel2";
            this.uiLabel2.Size = new System.Drawing.Size(112, 67);
            this.uiLabel2.TabIndex = 43;
            this.uiLabel2.Text = "完成次数";
            this.uiLabel2.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // uiLabel3
            // 
            this.uiLabel3.Dock = System.Windows.Forms.DockStyle.Fill;
            this.uiLabel3.Font = new System.Drawing.Font("宋体", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.uiLabel3.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(48)))), ((int)(((byte)(48)))), ((int)(((byte)(48)))));
            this.uiLabel3.Location = new System.Drawing.Point(1454, 721);
            this.uiLabel3.Name = "uiLabel3";
            this.uiLabel3.Size = new System.Drawing.Size(112, 68);
            this.uiLabel3.TabIndex = 44;
            this.uiLabel3.Text = "剩余次数";
            this.uiLabel3.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // DiReadTimer
            // 
            this.DiReadTimer.Interval = 50;
            this.DiReadTimer.Tick += new System.EventHandler(this.DiReadTimer_Tick);
            // 
            // FrmMainMonitor
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(12F, 24F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(243)))), ((int)(((byte)(249)))), ((int)(((byte)(255)))));
            this.ClientSize = new System.Drawing.Size(2021, 1007);
            this.ControlBox = false;
            this.Controls.Add(this.uiTableLayoutPanelMain);
            this.Name = "FrmMainMonitor";
            this.Text = "实时监视";
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.FrmMainMonitor_FormClosing);
            this.Load += new System.EventHandler(this.FrmMainMonitor_Load);
            this.uiTableLayoutPanel2.ResumeLayout(false);
            this.uiGroupBox1.ResumeLayout(false);
            this.uiGroupAllControl.ResumeLayout(false);
            this.uiTableLayoutPanel7.ResumeLayout(false);
            this.uiGroupInfo.ResumeLayout(false);
            this.z.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.dgvRealData)).EndInit();
            this.uiGroupCurve.ResumeLayout(false);
            this.uiTableLayoutPanel6.ResumeLayout(false);
            this.uiTableLayoutPanel4.ResumeLayout(false);
            this.uiTableLayoutPanel9.ResumeLayout(false);
            this.uiTableLayoutPanel10.ResumeLayout(false);
            this.uiTableLayoutPanelMain.ResumeLayout(false);
            this.ResumeLayout(false);

        }

        #endregion
        private Sunny.UI.UIStyleManager uiStyleManager1;
        private Sunny.UI.UIRadioButton RadEmb1;
        private Sunny.UI.UILabel LabEmb1;
        private Sunny.UI.UILight AlertEmb1;
        private Sunny.UI.UITableLayoutPanel uiTableLayoutPanel2;
        private Sunny.UI.UIGroupBox uiGroupBox1;
        private Sunny.UI.UILabel uiLabel7;
        private Sunny.UI.UILabel uiLabel9;
        private Sunny.UI.UILabel uiLabel10;
        private Sunny.UI.UITextBox TxtTargetCycles;
        private Sunny.UI.UITextBox TxtTestCycleTime;
        private Sunny.UI.UICheckBox ChkEmb1;
        private Sunny.UI.UITextBox TxtTestName;
        private Sunny.UI.UILabel uiLabel8;
        private Sunny.UI.UITextBox TxtTestStandard;
        private Sunny.UI.UIButton BtnSettingDetail;
        private Sunny.UI.UILabel uiLabel11;
        private ZedGraph.ZedGraphControl zedGraphRealChart;
        private Sunny.UI.UIGroupBox uiGroupAllControl;
        private Sunny.UI.UIGroupBox uiGroupInfo;
        private Sunny.UI.UIGroupBox uiGroupCurve;
        private Sunny.UI.UITableLayoutPanel uiTableLayoutPanel7;
        private Sunny.UI.UITableLayoutPanel z;
        private Sunny.UI.UITableLayoutPanel uiTableLayoutPanel6;
        private Sunny.UI.UIRichTextBox RtbInfo;
        private Sunny.UI.UIDataGridView dgvRealData;
        private Sunny.UI.UILedDisplay LedRunTime;
        private Sunny.UI.UITableLayoutPanel uiTableLayoutPanel4;
        private Sunny.UI.UITableLayoutPanel uiTableLayoutPanel9;
        private Sunny.UI.UILedDisplay LedRunCycles;
        private Sunny.UI.UITableLayoutPanel uiTableLayoutPanel10;
        private Sunny.UI.UILedDisplay LedLastCycles;
        private Sunny.UI.UIProcessBar ProcBar;
        private System.Windows.Forms.Timer timerProgressDisp;
        private Sunny.UI.UIButton BtnStop;
        private Sunny.UI.UIButton BtnStartTest;
        private Sunny.UI.UITableLayoutPanel uiTableLayoutPanelMain;
        private Sunny.UI.UIButton BtnApply;
        private Sunny.UI.UIButton BtnCancel;
        private Sunny.UI.UIButton BtnRunLog;
        private Sunny.UI.UIButton BtnErrorLog;
        private Sunny.UI.UIButton BtnAutoLearn;
        private Sunny.UI.UILabel uiLabel1;
        private Sunny.UI.UILabel uiLabel2;
        private Sunny.UI.UILabel uiLabel3;
        private System.Windows.Forms.Timer DiReadTimer;
        private Sunny.UI.UIButton BtnPause;
    }
}