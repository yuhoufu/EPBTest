namespace MTEmbTest
{
    partial class FrmRawPlayBack
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
            this.tableLayoutPanel4 = new System.Windows.Forms.TableLayoutPanel();
            this.tableLayoutPanel5 = new System.Windows.Forms.TableLayoutPanel();
            this.CmbEpbNo = new Sunny.UI.UIComboBox();
            this.BtnChoiseFolder = new System.Windows.Forms.Button();
            this.ChkDaqCurrent = new Sunny.UI.UICheckBox();
            this.ChkPressure = new Sunny.UI.UICheckBox();
            this.BtnExportFile = new System.Windows.Forms.Button();
            this.BtnPanLeft = new System.Windows.Forms.Button();
            this.BtnPanRight = new System.Windows.Forms.Button();
            this.uiPanel1 = new Sunny.UI.UIPanel();
            this.ProgressShow = new Sunny.UI.UIProgressIndicator();
            this.zedGraphControlHistory = new ZedGraph.ZedGraphControl();
            this.LbFileList = new Sunny.UI.UIListBox();
            this.RtbTestInfo = new Sunny.UI.UIRichTextBox();
            this.ChkForce = new Sunny.UI.UICheckBox();
            this.tableLayoutPanel4.SuspendLayout();
            this.tableLayoutPanel5.SuspendLayout();
            this.uiPanel1.SuspendLayout();
            this.SuspendLayout();
            // 
            // tableLayoutPanel4
            // 
            this.tableLayoutPanel4.ColumnCount = 4;
            this.tableLayoutPanel4.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 18F));
            this.tableLayoutPanel4.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 80F));
            this.tableLayoutPanel4.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 20F));
            this.tableLayoutPanel4.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 18F));
            this.tableLayoutPanel4.Controls.Add(this.tableLayoutPanel5, 1, 4);
            this.tableLayoutPanel4.Controls.Add(this.uiPanel1, 1, 1);
            this.tableLayoutPanel4.Controls.Add(this.LbFileList, 2, 2);
            this.tableLayoutPanel4.Controls.Add(this.RtbTestInfo, 2, 1);
            this.tableLayoutPanel4.Dock = System.Windows.Forms.DockStyle.Fill;
            this.tableLayoutPanel4.Location = new System.Drawing.Point(0, 0);
            this.tableLayoutPanel4.Name = "tableLayoutPanel4";
            this.tableLayoutPanel4.RowCount = 5;
            this.tableLayoutPanel4.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 18F));
            this.tableLayoutPanel4.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 20F));
            this.tableLayoutPanel4.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 80F));
            this.tableLayoutPanel4.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 18F));
            this.tableLayoutPanel4.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 74F));
            this.tableLayoutPanel4.Size = new System.Drawing.Size(1738, 1043);
            this.tableLayoutPanel4.TabIndex = 1;
            // 
            // tableLayoutPanel5
            // 
            this.tableLayoutPanel5.ColumnCount = 16;
            this.tableLayoutPanel4.SetColumnSpan(this.tableLayoutPanel5, 2);
            this.tableLayoutPanel5.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 18F));
            this.tableLayoutPanel5.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 12.5F));
            this.tableLayoutPanel5.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 18F));
            this.tableLayoutPanel5.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 12.5F));
            this.tableLayoutPanel5.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 18F));
            this.tableLayoutPanel5.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 12.5F));
            this.tableLayoutPanel5.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 18F));
            this.tableLayoutPanel5.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 12.5F));
            this.tableLayoutPanel5.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 18F));
            this.tableLayoutPanel5.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 12.5F));
            this.tableLayoutPanel5.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 18F));
            this.tableLayoutPanel5.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 12.5F));
            this.tableLayoutPanel5.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 18F));
            this.tableLayoutPanel5.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 12.5F));
            this.tableLayoutPanel5.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 18F));
            this.tableLayoutPanel5.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 12.5F));
            this.tableLayoutPanel5.Controls.Add(this.ChkForce, 7, 0);
            this.tableLayoutPanel5.Controls.Add(this.CmbEpbNo, 1, 0);
            this.tableLayoutPanel5.Controls.Add(this.BtnChoiseFolder, 13, 0);
            this.tableLayoutPanel5.Controls.Add(this.ChkDaqCurrent, 3, 0);
            this.tableLayoutPanel5.Controls.Add(this.ChkPressure, 5, 0);
            this.tableLayoutPanel5.Controls.Add(this.BtnExportFile, 15, 0);
            this.tableLayoutPanel5.Controls.Add(this.BtnPanLeft, 9, 0);
            this.tableLayoutPanel5.Controls.Add(this.BtnPanRight, 11, 0);
            this.tableLayoutPanel5.Dock = System.Windows.Forms.DockStyle.Fill;
            this.tableLayoutPanel5.Location = new System.Drawing.Point(21, 971);
            this.tableLayoutPanel5.Name = "tableLayoutPanel5";
            this.tableLayoutPanel5.RowCount = 1;
            this.tableLayoutPanel5.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.tableLayoutPanel5.Size = new System.Drawing.Size(1695, 69);
            this.tableLayoutPanel5.TabIndex = 1;
            // 
            // CmbEpbNo
            // 
            this.CmbEpbNo.DataSource = null;
            this.CmbEpbNo.Dock = System.Windows.Forms.DockStyle.Fill;
            this.CmbEpbNo.FillColor = System.Drawing.Color.White;
            this.CmbEpbNo.Font = new System.Drawing.Font("宋体", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.CmbEpbNo.ItemHoverColor = System.Drawing.Color.FromArgb(((int)(((byte)(155)))), ((int)(((byte)(200)))), ((int)(((byte)(255)))));
            this.CmbEpbNo.Items.AddRange(new object[] {
            "EPB1",
            "EPB2",
            "EPB3",
            "EPB4",
            "EPB5",
            "EPB6",
            "EPB7",
            "EPB8",
            "EPB9",
            "EPB10",
            "EPB11",
            "EPB12"});
            this.CmbEpbNo.ItemSelectForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(235)))), ((int)(((byte)(243)))), ((int)(((byte)(255)))));
            this.CmbEpbNo.Location = new System.Drawing.Point(22, 5);
            this.CmbEpbNo.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.CmbEpbNo.MinimumSize = new System.Drawing.Size(58, 0);
            this.CmbEpbNo.Name = "CmbEpbNo";
            this.CmbEpbNo.Padding = new System.Windows.Forms.Padding(0, 0, 30, 2);
            this.CmbEpbNo.Size = new System.Drawing.Size(185, 59);
            this.CmbEpbNo.SymbolSize = 24;
            this.CmbEpbNo.TabIndex = 2;
            this.CmbEpbNo.Text = "EPB1";
            this.CmbEpbNo.TextAlignment = System.Drawing.ContentAlignment.MiddleCenter;
            this.CmbEpbNo.Watermark = "";
            this.CmbEpbNo.SelectedIndexChanged += new System.EventHandler(this.CmbEmbNo_SelectedIndexChanged);
            // 
            // BtnChoiseFolder
            // 
            this.BtnChoiseFolder.Dock = System.Windows.Forms.DockStyle.Fill;
            this.BtnChoiseFolder.Location = new System.Drawing.Point(1287, 3);
            this.BtnChoiseFolder.Name = "BtnChoiseFolder";
            this.BtnChoiseFolder.Size = new System.Drawing.Size(187, 63);
            this.BtnChoiseFolder.TabIndex = 4;
            this.BtnChoiseFolder.Text = "选择文件夹";
            this.BtnChoiseFolder.UseVisualStyleBackColor = true;
            this.BtnChoiseFolder.Click += new System.EventHandler(this.BtnChoiseFolder_Click);
            // 
            // ChkDaqCurrent
            // 
            this.ChkDaqCurrent.CheckBoxSize = 24;
            this.ChkDaqCurrent.Checked = true;
            this.ChkDaqCurrent.Cursor = System.Windows.Forms.Cursors.Hand;
            this.ChkDaqCurrent.Dock = System.Windows.Forms.DockStyle.Fill;
            this.ChkDaqCurrent.Font = new System.Drawing.Font("宋体", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.ChkDaqCurrent.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(48)))), ((int)(((byte)(48)))), ((int)(((byte)(48)))));
            this.ChkDaqCurrent.Location = new System.Drawing.Point(232, 3);
            this.ChkDaqCurrent.MinimumSize = new System.Drawing.Size(1, 1);
            this.ChkDaqCurrent.Name = "ChkDaqCurrent";
            this.ChkDaqCurrent.Size = new System.Drawing.Size(187, 63);
            this.ChkDaqCurrent.TabIndex = 4;
            this.ChkDaqCurrent.Text = "DAQ_Current";
            this.ChkDaqCurrent.CheckedChanged += new System.EventHandler(this.ChkDaqCurrent_CheckedChanged);
            // 
            // ChkPressure
            // 
            this.ChkPressure.CheckBoxSize = 24;
            this.ChkPressure.Checked = true;
            this.ChkPressure.Cursor = System.Windows.Forms.Cursors.Hand;
            this.ChkPressure.Dock = System.Windows.Forms.DockStyle.Fill;
            this.ChkPressure.Font = new System.Drawing.Font("宋体", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.ChkPressure.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(48)))), ((int)(((byte)(48)))), ((int)(((byte)(48)))));
            this.ChkPressure.Location = new System.Drawing.Point(443, 3);
            this.ChkPressure.MinimumSize = new System.Drawing.Size(1, 1);
            this.ChkPressure.Name = "ChkPressure";
            this.ChkPressure.Size = new System.Drawing.Size(187, 63);
            this.ChkPressure.TabIndex = 3;
            this.ChkPressure.Text = "DAQ_Pressure";
            this.ChkPressure.CheckedChanged += new System.EventHandler(this.ChkCurrent_CheckedChanged);
            // 
            // BtnExportFile
            // 
            this.BtnExportFile.Dock = System.Windows.Forms.DockStyle.Fill;
            this.BtnExportFile.Location = new System.Drawing.Point(1498, 3);
            this.BtnExportFile.Name = "BtnExportFile";
            this.BtnExportFile.Size = new System.Drawing.Size(194, 63);
            this.BtnExportFile.TabIndex = 1;
            this.BtnExportFile.Text = "导出";
            this.BtnExportFile.UseVisualStyleBackColor = true;
            this.BtnExportFile.Click += new System.EventHandler(this.BtnExportFile_Click);
            // 
            // BtnPanLeft
            // 
            this.BtnPanLeft.Dock = System.Windows.Forms.DockStyle.Fill;
            this.BtnPanLeft.Location = new System.Drawing.Point(865, 3);
            this.BtnPanLeft.Name = "BtnPanLeft";
            this.BtnPanLeft.Size = new System.Drawing.Size(187, 63);
            this.BtnPanLeft.TabIndex = 5;
            this.BtnPanLeft.Text = "曲线左移";
            this.BtnPanLeft.UseVisualStyleBackColor = true;
            this.BtnPanLeft.Click += new System.EventHandler(this.BtnPanLeft_Click);
            // 
            // BtnPanRight
            // 
            this.BtnPanRight.Dock = System.Windows.Forms.DockStyle.Fill;
            this.BtnPanRight.Location = new System.Drawing.Point(1076, 3);
            this.BtnPanRight.Name = "BtnPanRight";
            this.BtnPanRight.Size = new System.Drawing.Size(187, 63);
            this.BtnPanRight.TabIndex = 6;
            this.BtnPanRight.Text = "曲线右移";
            this.BtnPanRight.UseVisualStyleBackColor = true;
            this.BtnPanRight.Click += new System.EventHandler(this.BtnPanRight_Click);
            // 
            // uiPanel1
            // 
            this.uiPanel1.Controls.Add(this.ProgressShow);
            this.uiPanel1.Controls.Add(this.zedGraphControlHistory);
            this.uiPanel1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.uiPanel1.Font = new System.Drawing.Font("宋体", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.uiPanel1.Location = new System.Drawing.Point(22, 23);
            this.uiPanel1.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.uiPanel1.MinimumSize = new System.Drawing.Size(1, 1);
            this.uiPanel1.Name = "uiPanel1";
            this.tableLayoutPanel4.SetRowSpan(this.uiPanel1, 2);
            this.uiPanel1.Size = new System.Drawing.Size(1353, 922);
            this.uiPanel1.TabIndex = 2;
            this.uiPanel1.Text = null;
            this.uiPanel1.TextAlignment = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // ProgressShow
            // 
            this.ProgressShow.Active = true;
            this.ProgressShow.BackColor = System.Drawing.Color.Transparent;
            this.ProgressShow.Font = new System.Drawing.Font("宋体", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.ProgressShow.Location = new System.Drawing.Point(828, 350);
            this.ProgressShow.MinimumSize = new System.Drawing.Size(1, 1);
            this.ProgressShow.Name = "ProgressShow";
            this.ProgressShow.Size = new System.Drawing.Size(147, 106);
            this.ProgressShow.TabIndex = 3;
            this.ProgressShow.Visible = false;
            // 
            // zedGraphControlHistory
            // 
            this.zedGraphControlHistory.Dock = System.Windows.Forms.DockStyle.Fill;
            this.zedGraphControlHistory.Font = new System.Drawing.Font("宋体", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.zedGraphControlHistory.IsEnableVZoom = false;
            this.zedGraphControlHistory.Location = new System.Drawing.Point(0, 0);
            this.zedGraphControlHistory.Margin = new System.Windows.Forms.Padding(6);
            this.zedGraphControlHistory.Name = "zedGraphControlHistory";
            this.zedGraphControlHistory.ScrollGrace = 0D;
            this.zedGraphControlHistory.ScrollMaxX = 0D;
            this.zedGraphControlHistory.ScrollMaxY = 0D;
            this.zedGraphControlHistory.ScrollMaxY2 = 0D;
            this.zedGraphControlHistory.ScrollMinX = 0D;
            this.zedGraphControlHistory.ScrollMinY = 0D;
            this.zedGraphControlHistory.ScrollMinY2 = 0D;
            this.zedGraphControlHistory.Size = new System.Drawing.Size(1353, 922);
            this.zedGraphControlHistory.TabIndex = 0;
            this.zedGraphControlHistory.UseExtendedPrintDialog = true;
            // 
            // LbFileList
            // 
            this.LbFileList.Dock = System.Windows.Forms.DockStyle.Fill;
            this.LbFileList.Font = new System.Drawing.Font("宋体", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.LbFileList.HoverColor = System.Drawing.Color.FromArgb(((int)(((byte)(155)))), ((int)(((byte)(200)))), ((int)(((byte)(255)))));
            this.LbFileList.ItemSelectForeColor = System.Drawing.Color.White;
            this.LbFileList.Location = new System.Drawing.Point(1383, 209);
            this.LbFileList.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.LbFileList.MinimumSize = new System.Drawing.Size(1, 1);
            this.LbFileList.Name = "LbFileList";
            this.LbFileList.Padding = new System.Windows.Forms.Padding(2);
            this.LbFileList.ShowText = false;
            this.LbFileList.Size = new System.Drawing.Size(332, 736);
            this.LbFileList.TabIndex = 5;
            this.LbFileList.Text = "uiListBox1";
            this.LbFileList.DoubleClick += new System.EventHandler(this.LbFileList_DoubleClick);
            // 
            // RtbTestInfo
            // 
            this.RtbTestInfo.Dock = System.Windows.Forms.DockStyle.Fill;
            this.RtbTestInfo.FillColor = System.Drawing.Color.White;
            this.RtbTestInfo.Font = new System.Drawing.Font("宋体", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.RtbTestInfo.Location = new System.Drawing.Point(1383, 23);
            this.RtbTestInfo.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.RtbTestInfo.MinimumSize = new System.Drawing.Size(1, 1);
            this.RtbTestInfo.Name = "RtbTestInfo";
            this.RtbTestInfo.Padding = new System.Windows.Forms.Padding(2);
            this.RtbTestInfo.ShowText = false;
            this.RtbTestInfo.Size = new System.Drawing.Size(332, 176);
            this.RtbTestInfo.TabIndex = 6;
            this.RtbTestInfo.TextAlignment = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // ChkForce
            // 
            this.ChkForce.CheckBoxSize = 24;
            this.ChkForce.Checked = true;
            this.ChkForce.Cursor = System.Windows.Forms.Cursors.Hand;
            this.ChkForce.Dock = System.Windows.Forms.DockStyle.Fill;
            this.ChkForce.Font = new System.Drawing.Font("宋体", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.ChkForce.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(48)))), ((int)(((byte)(48)))), ((int)(((byte)(48)))));
            this.ChkForce.Location = new System.Drawing.Point(654, 3);
            this.ChkForce.MinimumSize = new System.Drawing.Size(1, 1);
            this.ChkForce.Name = "ChkForce";
            this.ChkForce.Size = new System.Drawing.Size(187, 63);
            this.ChkForce.TabIndex = 7;
            this.ChkForce.Text = "Act_Force";
            this.ChkForce.Visible = false;
            // 
            // FrmRawPlayBack
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(12F, 24F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(1738, 1043);
            this.ControlBox = false;
            this.Controls.Add(this.tableLayoutPanel4);
            this.Name = "FrmRawPlayBack";
            this.Text = "原始数据回放";
            this.Load += new System.EventHandler(this.FrmPlayBack_Load);
            this.tableLayoutPanel4.ResumeLayout(false);
            this.tableLayoutPanel5.ResumeLayout(false);
            this.uiPanel1.ResumeLayout(false);
            this.ResumeLayout(false);

        }

        #endregion

        private System.Windows.Forms.TableLayoutPanel tableLayoutPanel4;
        private ZedGraph.ZedGraphControl zedGraphControlHistory;
        private System.Windows.Forms.TableLayoutPanel tableLayoutPanel5;
        private System.Windows.Forms.Button BtnExportFile;
        private Sunny.UI.UICheckBox ChkPressure;
        private Sunny.UI.UICheckBox ChkDaqCurrent;
        private Sunny.UI.UIComboBox CmbEpbNo;
        private Sunny.UI.UIPanel uiPanel1;
        private Sunny.UI.UIProgressIndicator ProgressShow;
        private System.Windows.Forms.Button BtnChoiseFolder;
        private Sunny.UI.UIListBox LbFileList;
        private Sunny.UI.UIRichTextBox RtbTestInfo;
        private System.Windows.Forms.Button BtnPanLeft;
        private System.Windows.Forms.Button BtnPanRight;
        private Sunny.UI.UICheckBox ChkForce;
    }
}