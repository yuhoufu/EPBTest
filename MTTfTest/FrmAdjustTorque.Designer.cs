namespace MtEmbTest
{
    partial class FrmAdjustTorque
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
            this.uiStyleManager1 = new Sunny.UI.UIStyleManager(this.components);
            this.AlertEmb1 = new Sunny.UI.UILight();
            this.zedGraphRealChart = new ZedGraph.ZedGraphControl();
            this.BtnAutoLearn = new Sunny.UI.UIButton();
            this.BtnStartTest = new Sunny.UI.UIButton();
            this.uiGroupInfo = new Sunny.UI.UIGroupBox();
            this.z = new Sunny.UI.UITableLayoutPanel();
            this.BtnRunLog = new Sunny.UI.UIButton();
            this.RtbInfo = new Sunny.UI.UIRichTextBox();
            this.BtnErrorLog = new Sunny.UI.UIButton();
            this.timerProgressDisp = new System.Windows.Forms.Timer(this.components);
            this.uiTableLayoutPanelMain = new Sunny.UI.UITableLayoutPanel();
            this.uiGroupBox2 = new Sunny.UI.UIGroupBox();
            this.uiTableLayoutPanel2 = new Sunny.UI.UITableLayoutPanel();
            this.BtnTarTorque = new Sunny.UI.UIIntegerUpDown();
            this.uiLabel2 = new Sunny.UI.UILabel();
            this.uiLabel1 = new Sunny.UI.UILabel();
            this.BtnAdjustClampForce = new Sunny.UI.UIIntegerUpDown();
            this.BtnAdjustPushVol = new Sunny.UI.UIIntegerUpDown();
            this.uiLabel3 = new Sunny.UI.UILabel();
            this.BtnGetCanID = new Sunny.UI.UIButton();
            this.DiReadTimer = new System.Windows.Forms.Timer(this.components);
            this.uiGroupInfo.SuspendLayout();
            this.z.SuspendLayout();
            this.uiTableLayoutPanelMain.SuspendLayout();
            this.uiGroupBox2.SuspendLayout();
            this.uiTableLayoutPanel2.SuspendLayout();
            this.SuspendLayout();
            // 
            // AlertEmb1
            // 
            this.AlertEmb1.CenterColor = System.Drawing.Color.FromArgb(((int)(((byte)(110)))), ((int)(((byte)(190)))), ((int)(((byte)(40)))));
            this.AlertEmb1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.AlertEmb1.Font = new System.Drawing.Font("宋体", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.AlertEmb1.Interval = 2000;
            this.AlertEmb1.Location = new System.Drawing.Point(862, 285);
            this.AlertEmb1.Margin = new System.Windows.Forms.Padding(13, 3, 3, 3);
            this.AlertEmb1.MinimumSize = new System.Drawing.Size(1, 1);
            this.AlertEmb1.Name = "AlertEmb1";
            this.AlertEmb1.OffCenterColor = System.Drawing.Color.FromArgb(((int)(((byte)(140)))), ((int)(((byte)(140)))), ((int)(((byte)(140)))));
            this.AlertEmb1.OnCenterColor = System.Drawing.Color.FromArgb(((int)(((byte)(110)))), ((int)(((byte)(190)))), ((int)(((byte)(40)))));
            this.AlertEmb1.Radius = 0;
            this.AlertEmb1.Shape = Sunny.UI.UIShape.Square;
            this.AlertEmb1.Size = new System.Drawing.Size(71, 54);
            this.AlertEmb1.State = Sunny.UI.UILightState.Off;
            this.AlertEmb1.TabIndex = 12;
            this.AlertEmb1.Text = "uiLight1";
            this.AlertEmb1.Click += new System.EventHandler(this.AlertEmb1_Click);
            // 
            // zedGraphRealChart
            // 
            this.uiTableLayoutPanelMain.SetColumnSpan(this.zedGraphRealChart, 11);
            this.zedGraphRealChart.Dock = System.Windows.Forms.DockStyle.Fill;
            this.zedGraphRealChart.IsEnableHZoom = false;
            this.zedGraphRealChart.IsEnableVZoom = false;
            this.zedGraphRealChart.IsEnableWheelZoom = false;
            this.zedGraphRealChart.Location = new System.Drawing.Point(26, 26);
            this.zedGraphRealChart.Margin = new System.Windows.Forms.Padding(6, 6, 6, 6);
            this.zedGraphRealChart.Name = "zedGraphRealChart";
            this.uiTableLayoutPanelMain.SetRowSpan(this.zedGraphRealChart, 6);
            this.zedGraphRealChart.ScrollGrace = 0D;
            this.zedGraphRealChart.ScrollMaxX = 0D;
            this.zedGraphRealChart.ScrollMaxY = 0D;
            this.zedGraphRealChart.ScrollMaxY2 = 0D;
            this.zedGraphRealChart.ScrollMinX = 0D;
            this.zedGraphRealChart.ScrollMinY = 0D;
            this.zedGraphRealChart.ScrollMinY2 = 0D;
            this.zedGraphRealChart.Size = new System.Drawing.Size(2130, 594);
            this.zedGraphRealChart.TabIndex = 4;
            this.zedGraphRealChart.UseExtendedPrintDialog = true;
            this.zedGraphRealChart.ContextMenuBuilder += new ZedGraph.ZedGraphControl.ContextMenuBuilderEventHandler(this.zedGraphRealChart_ContextMenuBuilder);
            // 
            // BtnAutoLearn
            // 
            this.uiTableLayoutPanel2.SetColumnSpan(this.BtnAutoLearn, 2);
            this.BtnAutoLearn.Cursor = System.Windows.Forms.Cursors.Hand;
            this.BtnAutoLearn.Dock = System.Windows.Forms.DockStyle.Fill;
            this.BtnAutoLearn.FillDisableColor = System.Drawing.Color.FromArgb(((int)(((byte)(115)))), ((int)(((byte)(179)))), ((int)(((byte)(255)))));
            this.BtnAutoLearn.Font = new System.Drawing.Font("Arial", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.BtnAutoLearn.Location = new System.Drawing.Point(841, 153);
            this.BtnAutoLearn.MinimumSize = new System.Drawing.Size(1, 1);
            this.BtnAutoLearn.Name = "BtnAutoLearn";
            this.BtnAutoLearn.Radius = 1;
            this.BtnAutoLearn.RectDisableColor = System.Drawing.Color.LightBlue;
            this.BtnAutoLearn.Size = new System.Drawing.Size(240, 54);
            this.BtnAutoLearn.TabIndex = 52;
            this.BtnAutoLearn.Text = "自学习";
            this.BtnAutoLearn.TipsFont = new System.Drawing.Font("宋体", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.BtnAutoLearn.Click += new System.EventHandler(this.BtnAutoLearn_Click);
            // 
            // BtnStartTest
            // 
            this.uiTableLayoutPanel2.SetColumnSpan(this.BtnStartTest, 2);
            this.BtnStartTest.Cursor = System.Windows.Forms.Cursors.Hand;
            this.BtnStartTest.Dock = System.Windows.Forms.DockStyle.Fill;
            this.BtnStartTest.FillDisableColor = System.Drawing.Color.FromArgb(((int)(((byte)(115)))), ((int)(((byte)(179)))), ((int)(((byte)(255)))));
            this.BtnStartTest.Font = new System.Drawing.Font("Arial", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.BtnStartTest.Location = new System.Drawing.Point(841, 48);
            this.BtnStartTest.MinimumSize = new System.Drawing.Size(1, 1);
            this.BtnStartTest.Name = "BtnStartTest";
            this.BtnStartTest.RectDisableColor = System.Drawing.Color.LightBlue;
            this.BtnStartTest.Size = new System.Drawing.Size(240, 54);
            this.BtnStartTest.TabIndex = 50;
            this.BtnStartTest.Text = "启动";
            this.BtnStartTest.TipsFont = new System.Drawing.Font("宋体", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.BtnStartTest.Click += new System.EventHandler(this.BtnStartTest_Click);
            // 
            // uiGroupInfo
            // 
            this.uiTableLayoutPanelMain.SetColumnSpan(this.uiGroupInfo, 4);
            this.uiGroupInfo.Controls.Add(this.z);
            this.uiGroupInfo.Dock = System.Windows.Forms.DockStyle.Fill;
            this.uiGroupInfo.Font = new System.Drawing.Font("Arial Narrow", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.uiGroupInfo.Location = new System.Drawing.Point(24, 671);
            this.uiGroupInfo.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.uiGroupInfo.MinimumSize = new System.Drawing.Size(1, 1);
            this.uiGroupInfo.Name = "uiGroupInfo";
            this.uiGroupInfo.Padding = new System.Windows.Forms.Padding(0, 32, 0, 0);
            this.uiTableLayoutPanelMain.SetRowSpan(this.uiGroupInfo, 4);
            this.uiGroupInfo.Size = new System.Drawing.Size(959, 394);
            this.uiGroupInfo.TabIndex = 7;
            this.uiGroupInfo.Text = null;
            this.uiGroupInfo.TextAlignment = System.Drawing.ContentAlignment.MiddleLeft;
            // 
            // z
            // 
            this.z.BackColor = System.Drawing.Color.Transparent;
            this.z.ColumnCount = 8;
            this.z.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.z.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 30F));
            this.z.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.z.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 30F));
            this.z.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.z.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 30F));
            this.z.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 10F));
            this.z.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.z.Controls.Add(this.BtnRunLog, 1, 3);
            this.z.Controls.Add(this.RtbInfo, 1, 1);
            this.z.Controls.Add(this.AlertEmb1, 6, 3);
            this.z.Controls.Add(this.BtnErrorLog, 5, 3);
            this.z.Dock = System.Windows.Forms.DockStyle.Fill;
            this.z.Location = new System.Drawing.Point(0, 32);
            this.z.Name = "z";
            this.z.RowCount = 5;
            this.z.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.z.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.z.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.z.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 60F));
            this.z.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.z.Size = new System.Drawing.Size(959, 362);
            this.z.TabIndex = 37;
            this.z.TagString = null;
            // 
            // BtnRunLog
            // 
            this.BtnRunLog.Cursor = System.Windows.Forms.Cursors.Hand;
            this.BtnRunLog.Dock = System.Windows.Forms.DockStyle.Fill;
            this.BtnRunLog.Font = new System.Drawing.Font("Arial", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.BtnRunLog.Location = new System.Drawing.Point(23, 285);
            this.BtnRunLog.MinimumSize = new System.Drawing.Size(1, 1);
            this.BtnRunLog.Name = "BtnRunLog";
            this.BtnRunLog.Radius = 1;
            this.BtnRunLog.Size = new System.Drawing.Size(257, 54);
            this.BtnRunLog.TabIndex = 52;
            this.BtnRunLog.Text = "运行日志";
            this.BtnRunLog.TipsFont = new System.Drawing.Font("宋体", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.BtnRunLog.Click += new System.EventHandler(this.BtnRunLog_Click);
            // 
            // RtbInfo
            // 
            this.z.SetColumnSpan(this.RtbInfo, 6);
            this.RtbInfo.Dock = System.Windows.Forms.DockStyle.Fill;
            this.RtbInfo.FillColor = System.Drawing.Color.White;
            this.RtbInfo.Font = new System.Drawing.Font("Arial Narrow", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.RtbInfo.Location = new System.Drawing.Point(24, 25);
            this.RtbInfo.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.RtbInfo.MinimumSize = new System.Drawing.Size(1, 1);
            this.RtbInfo.Name = "RtbInfo";
            this.RtbInfo.Padding = new System.Windows.Forms.Padding(2);
            this.RtbInfo.Radius = 1;
            this.RtbInfo.ShowText = false;
            this.RtbInfo.Size = new System.Drawing.Size(908, 232);
            this.RtbInfo.TabIndex = 0;
            this.RtbInfo.TextAlignment = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // BtnErrorLog
            // 
            this.BtnErrorLog.Cursor = System.Windows.Forms.Cursors.Hand;
            this.BtnErrorLog.Dock = System.Windows.Forms.DockStyle.Fill;
            this.BtnErrorLog.Font = new System.Drawing.Font("Arial", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.BtnErrorLog.Location = new System.Drawing.Point(589, 285);
            this.BtnErrorLog.MinimumSize = new System.Drawing.Size(1, 1);
            this.BtnErrorLog.Name = "BtnErrorLog";
            this.BtnErrorLog.Radius = 1;
            this.BtnErrorLog.Size = new System.Drawing.Size(257, 54);
            this.BtnErrorLog.TabIndex = 53;
            this.BtnErrorLog.Text = "错误日志";
            this.BtnErrorLog.TipsFont = new System.Drawing.Font("宋体", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.BtnErrorLog.Click += new System.EventHandler(this.BtnErrorLog_Click);
            // 
            // timerProgressDisp
            // 
            this.timerProgressDisp.Interval = 1000;
            this.timerProgressDisp.Tick += new System.EventHandler(this.timerProgressDisp_Tick);
            // 
            // uiTableLayoutPanelMain
            // 
            this.uiTableLayoutPanelMain.ColumnCount = 13;
            this.uiTableLayoutPanelMain.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.uiTableLayoutPanelMain.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 18F));
            this.uiTableLayoutPanelMain.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 19F));
            this.uiTableLayoutPanelMain.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.uiTableLayoutPanelMain.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 8F));
            this.uiTableLayoutPanelMain.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.uiTableLayoutPanelMain.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 9F));
            this.uiTableLayoutPanelMain.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 9F));
            this.uiTableLayoutPanelMain.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 9F));
            this.uiTableLayoutPanelMain.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 10F));
            this.uiTableLayoutPanelMain.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 9F));
            this.uiTableLayoutPanelMain.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 9F));
            this.uiTableLayoutPanelMain.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.uiTableLayoutPanelMain.Controls.Add(this.zedGraphRealChart, 1, 1);
            this.uiTableLayoutPanelMain.Controls.Add(this.uiGroupInfo, 1, 8);
            this.uiTableLayoutPanelMain.Controls.Add(this.uiGroupBox2, 6, 8);
            this.uiTableLayoutPanelMain.Dock = System.Windows.Forms.DockStyle.Fill;
            this.uiTableLayoutPanelMain.Location = new System.Drawing.Point(0, 0);
            this.uiTableLayoutPanelMain.Name = "uiTableLayoutPanelMain";
            this.uiTableLayoutPanelMain.RowCount = 13;
            this.uiTableLayoutPanelMain.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.uiTableLayoutPanelMain.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 10F));
            this.uiTableLayoutPanelMain.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 10F));
            this.uiTableLayoutPanelMain.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 10F));
            this.uiTableLayoutPanelMain.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 10F));
            this.uiTableLayoutPanelMain.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 10F));
            this.uiTableLayoutPanelMain.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 10F));
            this.uiTableLayoutPanelMain.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 40F));
            this.uiTableLayoutPanelMain.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 10F));
            this.uiTableLayoutPanelMain.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 10F));
            this.uiTableLayoutPanelMain.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 10F));
            this.uiTableLayoutPanelMain.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 10F));
            this.uiTableLayoutPanelMain.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.uiTableLayoutPanelMain.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.uiTableLayoutPanelMain.Size = new System.Drawing.Size(2189, 1091);
            this.uiTableLayoutPanelMain.TabIndex = 42;
            this.uiTableLayoutPanelMain.TagString = null;
            // 
            // uiGroupBox2
            // 
            this.uiTableLayoutPanelMain.SetColumnSpan(this.uiGroupBox2, 6);
            this.uiGroupBox2.Controls.Add(this.uiTableLayoutPanel2);
            this.uiGroupBox2.Dock = System.Windows.Forms.DockStyle.Fill;
            this.uiGroupBox2.Font = new System.Drawing.Font("Arial Narrow", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.uiGroupBox2.Location = new System.Drawing.Point(1011, 671);
            this.uiGroupBox2.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.uiGroupBox2.MinimumSize = new System.Drawing.Size(1, 1);
            this.uiGroupBox2.Name = "uiGroupBox2";
            this.uiGroupBox2.Padding = new System.Windows.Forms.Padding(0, 32, 0, 0);
            this.uiTableLayoutPanelMain.SetRowSpan(this.uiGroupBox2, 4);
            this.uiGroupBox2.Size = new System.Drawing.Size(1147, 394);
            this.uiGroupBox2.TabIndex = 54;
            this.uiGroupBox2.Text = null;
            this.uiGroupBox2.TextAlignment = System.Drawing.ContentAlignment.MiddleLeft;
            // 
            // uiTableLayoutPanel2
            // 
            this.uiTableLayoutPanel2.BackColor = System.Drawing.Color.Transparent;
            this.uiTableLayoutPanel2.ColumnCount = 9;
            this.uiTableLayoutPanel2.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 60F));
            this.uiTableLayoutPanel2.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 25F));
            this.uiTableLayoutPanel2.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 25F));
            this.uiTableLayoutPanel2.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.uiTableLayoutPanel2.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 25F));
            this.uiTableLayoutPanel2.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.uiTableLayoutPanel2.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 15F));
            this.uiTableLayoutPanel2.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 10F));
            this.uiTableLayoutPanel2.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 60F));
            this.uiTableLayoutPanel2.Controls.Add(this.BtnTarTorque, 2, 5);
            this.uiTableLayoutPanel2.Controls.Add(this.uiLabel2, 1, 3);
            this.uiTableLayoutPanel2.Controls.Add(this.uiLabel1, 1, 1);
            this.uiTableLayoutPanel2.Controls.Add(this.BtnAdjustClampForce, 2, 1);
            this.uiTableLayoutPanel2.Controls.Add(this.BtnAdjustPushVol, 2, 3);
            this.uiTableLayoutPanel2.Controls.Add(this.uiLabel3, 1, 5);
            this.uiTableLayoutPanel2.Controls.Add(this.BtnAutoLearn, 6, 3);
            this.uiTableLayoutPanel2.Controls.Add(this.BtnGetCanID, 6, 5);
            this.uiTableLayoutPanel2.Controls.Add(this.BtnStartTest, 6, 1);
            this.uiTableLayoutPanel2.Dock = System.Windows.Forms.DockStyle.Fill;
            this.uiTableLayoutPanel2.Location = new System.Drawing.Point(0, 32);
            this.uiTableLayoutPanel2.Name = "uiTableLayoutPanel2";
            this.uiTableLayoutPanel2.RowCount = 7;
            this.uiTableLayoutPanel2.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 25F));
            this.uiTableLayoutPanel2.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 60F));
            this.uiTableLayoutPanel2.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 25F));
            this.uiTableLayoutPanel2.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 60F));
            this.uiTableLayoutPanel2.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 25F));
            this.uiTableLayoutPanel2.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 60F));
            this.uiTableLayoutPanel2.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 25F));
            this.uiTableLayoutPanel2.Size = new System.Drawing.Size(1147, 362);
            this.uiTableLayoutPanel2.TabIndex = 37;
            this.uiTableLayoutPanel2.TagString = null;
            // 
            // BtnTarTorque
            // 
            this.BtnTarTorque.Dock = System.Windows.Forms.DockStyle.Fill;
            this.BtnTarTorque.Font = new System.Drawing.Font("宋体", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.BtnTarTorque.Location = new System.Drawing.Point(310, 260);
            this.BtnTarTorque.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.BtnTarTorque.Maximum = 3000;
            this.BtnTarTorque.Minimum = 10;
            this.BtnTarTorque.MinimumSize = new System.Drawing.Size(100, 0);
            this.BtnTarTorque.Name = "BtnTarTorque";
            this.BtnTarTorque.ShowText = false;
            this.BtnTarTorque.Size = new System.Drawing.Size(238, 50);
            this.BtnTarTorque.Step = 10;
            this.BtnTarTorque.TabIndex = 43;
            this.BtnTarTorque.Text = "uiIntegerUpDown2";
            this.BtnTarTorque.TextAlignment = System.Drawing.ContentAlignment.MiddleCenter;
            this.BtnTarTorque.Value = 800;
            this.BtnTarTorque.ValueChanged += new Sunny.UI.UIIntegerUpDown.OnValueChanged(this.BtnTarTorque_ValueChanged);
            // 
            // uiLabel2
            // 
            this.uiLabel2.Dock = System.Windows.Forms.DockStyle.Fill;
            this.uiLabel2.Font = new System.Drawing.Font("宋体", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.uiLabel2.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(48)))), ((int)(((byte)(48)))), ((int)(((byte)(48)))));
            this.uiLabel2.Location = new System.Drawing.Point(63, 150);
            this.uiLabel2.Name = "uiLabel2";
            this.uiLabel2.Size = new System.Drawing.Size(240, 60);
            this.uiLabel2.TabIndex = 44;
            this.uiLabel2.Text = "推力等级(%)";
            this.uiLabel2.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // uiLabel1
            // 
            this.uiLabel1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.uiLabel1.Font = new System.Drawing.Font("宋体", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.uiLabel1.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(48)))), ((int)(((byte)(48)))), ((int)(((byte)(48)))));
            this.uiLabel1.Location = new System.Drawing.Point(63, 45);
            this.uiLabel1.Name = "uiLabel1";
            this.uiLabel1.Size = new System.Drawing.Size(240, 60);
            this.uiLabel1.TabIndex = 43;
            this.uiLabel1.Text = "夹紧力(N)";
            this.uiLabel1.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // BtnAdjustClampForce
            // 
            this.BtnAdjustClampForce.Dock = System.Windows.Forms.DockStyle.Fill;
            this.BtnAdjustClampForce.Font = new System.Drawing.Font("宋体", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.BtnAdjustClampForce.Location = new System.Drawing.Point(310, 50);
            this.BtnAdjustClampForce.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.BtnAdjustClampForce.Maximum = 32000;
            this.BtnAdjustClampForce.Minimum = 100;
            this.BtnAdjustClampForce.MinimumSize = new System.Drawing.Size(100, 0);
            this.BtnAdjustClampForce.Name = "BtnAdjustClampForce";
            this.BtnAdjustClampForce.ShowText = false;
            this.BtnAdjustClampForce.Size = new System.Drawing.Size(238, 50);
            this.BtnAdjustClampForce.Step = 500;
            this.BtnAdjustClampForce.TabIndex = 42;
            this.BtnAdjustClampForce.Text = "uiIntegerUpDown2";
            this.BtnAdjustClampForce.TextAlignment = System.Drawing.ContentAlignment.MiddleCenter;
            this.BtnAdjustClampForce.Value = 15000;
            this.BtnAdjustClampForce.ValueChanged += new Sunny.UI.UIIntegerUpDown.OnValueChanged(this.BtnAdjustClampForce_ValueChanged);
            // 
            // BtnAdjustPushVol
            // 
            this.BtnAdjustPushVol.Dock = System.Windows.Forms.DockStyle.Fill;
            this.BtnAdjustPushVol.Font = new System.Drawing.Font("宋体", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.BtnAdjustPushVol.Location = new System.Drawing.Point(310, 155);
            this.BtnAdjustPushVol.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.BtnAdjustPushVol.Maximum = 90;
            this.BtnAdjustPushVol.Minimum = 0;
            this.BtnAdjustPushVol.MinimumSize = new System.Drawing.Size(100, 0);
            this.BtnAdjustPushVol.Name = "BtnAdjustPushVol";
            this.BtnAdjustPushVol.ShowText = false;
            this.BtnAdjustPushVol.Size = new System.Drawing.Size(238, 50);
            this.BtnAdjustPushVol.TabIndex = 41;
            this.BtnAdjustPushVol.Text = "uiIntegerUpDown1";
            this.BtnAdjustPushVol.TextAlignment = System.Drawing.ContentAlignment.MiddleCenter;
            this.BtnAdjustPushVol.Value = 30;
            this.BtnAdjustPushVol.ValueChanged += new Sunny.UI.UIIntegerUpDown.OnValueChanged(this.BtnAdjustPushVol_ValueChanged);
            // 
            // uiLabel3
            // 
            this.uiLabel3.Dock = System.Windows.Forms.DockStyle.Fill;
            this.uiLabel3.Font = new System.Drawing.Font("宋体", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.uiLabel3.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(48)))), ((int)(((byte)(48)))), ((int)(((byte)(48)))));
            this.uiLabel3.Location = new System.Drawing.Point(63, 255);
            this.uiLabel3.Name = "uiLabel3";
            this.uiLabel3.Size = new System.Drawing.Size(240, 60);
            this.uiLabel3.TabIndex = 53;
            this.uiLabel3.Text = "目标扭矩(Nm)";
            this.uiLabel3.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // BtnGetCanID
            // 
            this.uiTableLayoutPanel2.SetColumnSpan(this.BtnGetCanID, 2);
            this.BtnGetCanID.Cursor = System.Windows.Forms.Cursors.Hand;
            this.BtnGetCanID.Dock = System.Windows.Forms.DockStyle.Fill;
            this.BtnGetCanID.FillDisableColor = System.Drawing.Color.FromArgb(((int)(((byte)(115)))), ((int)(((byte)(179)))), ((int)(((byte)(255)))));
            this.BtnGetCanID.Font = new System.Drawing.Font("宋体", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.BtnGetCanID.Location = new System.Drawing.Point(841, 258);
            this.BtnGetCanID.MinimumSize = new System.Drawing.Size(1, 1);
            this.BtnGetCanID.Name = "BtnGetCanID";
            this.BtnGetCanID.Size = new System.Drawing.Size(240, 54);
            this.BtnGetCanID.TabIndex = 45;
            this.BtnGetCanID.Text = "获取CAN通信ID";
            this.BtnGetCanID.TipsFont = new System.Drawing.Font("宋体", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.BtnGetCanID.Click += new System.EventHandler(this.BtnGetCanID_Click);
            // 
            // DiReadTimer
            // 
            this.DiReadTimer.Interval = 50;
            this.DiReadTimer.Tick += new System.EventHandler(this.DiReadTimer_Tick);
            // 
            // FrmAdjustTorque
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(13F, 26F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(243)))), ((int)(((byte)(249)))), ((int)(((byte)(255)))));
            this.ClientSize = new System.Drawing.Size(2189, 1091);
            this.ControlBox = false;
            this.Controls.Add(this.uiTableLayoutPanelMain);
            this.Name = "FrmAdjustTorque";
            this.Text = "扭矩调节";
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.FrmMainMonitor_FormClosing);
            this.Load += new System.EventHandler(this.FrmMainMonitor_Load);
            this.uiGroupInfo.ResumeLayout(false);
            this.z.ResumeLayout(false);
            this.uiTableLayoutPanelMain.ResumeLayout(false);
            this.uiGroupBox2.ResumeLayout(false);
            this.uiTableLayoutPanel2.ResumeLayout(false);
            this.ResumeLayout(false);

        }

        #endregion
        private Sunny.UI.UIStyleManager uiStyleManager1;
        private Sunny.UI.UILight AlertEmb1;
        private ZedGraph.ZedGraphControl zedGraphRealChart;
        private Sunny.UI.UIGroupBox uiGroupInfo;
        private Sunny.UI.UITableLayoutPanel z;
        private System.Windows.Forms.Timer timerProgressDisp;
        private Sunny.UI.UIButton BtnStartTest;
        private Sunny.UI.UITableLayoutPanel uiTableLayoutPanelMain;
        private Sunny.UI.UIButton BtnAutoLearn;
        private System.Windows.Forms.Timer DiReadTimer;
        private Sunny.UI.UIButton BtnRunLog;
        private Sunny.UI.UIRichTextBox RtbInfo;
        private Sunny.UI.UIButton BtnErrorLog;
        private Sunny.UI.UIIntegerUpDown BtnAdjustPushVol;
        private Sunny.UI.UIIntegerUpDown BtnAdjustClampForce;
        private Sunny.UI.UILabel uiLabel1;
        private Sunny.UI.UILabel uiLabel2;
        private Sunny.UI.UIButton BtnGetCanID;
        private Sunny.UI.UIGroupBox uiGroupBox2;
        private Sunny.UI.UITableLayoutPanel uiTableLayoutPanel2;
        private Sunny.UI.UIIntegerUpDown BtnTarTorque;
        private Sunny.UI.UILabel uiLabel3;
    }
}