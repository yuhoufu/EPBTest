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
            this.BtnChoiseFolder = new System.Windows.Forms.Button();
            this.LbFileList = new Sunny.UI.UIListBox();
            this.RtbTestInfo = new Sunny.UI.UIRichTextBox();
            this.tableLayoutPanel5 = new System.Windows.Forms.TableLayoutPanel();
            this.BtnExportFile = new System.Windows.Forms.Button();
            this.ChkForce = new Sunny.UI.UICheckBox();
            this.ChkCurrent = new Sunny.UI.UICheckBox();
            this.BtnPanRight = new System.Windows.Forms.Button();
            this.BtnPanLeft = new System.Windows.Forms.Button();
            this.uiPanel1 = new Sunny.UI.UIPanel();
            this.ProgressShow = new Sunny.UI.UIProgressIndicator();
            this.zedGraphControlHistory = new ZedGraph.ZedGraphControl();
            this.ChkDaqCurrent = new Sunny.UI.UICheckBox();
            this.ChkDaqTorque = new Sunny.UI.UICheckBox();
            this.tableLayoutPanel4.SuspendLayout();
            this.tableLayoutPanel5.SuspendLayout();
            this.uiPanel1.SuspendLayout();
            this.SuspendLayout();
            // 
            // tableLayoutPanel4
            // 
            this.tableLayoutPanel4.ColumnCount = 4;
            this.tableLayoutPanel4.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.tableLayoutPanel4.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 80F));
            this.tableLayoutPanel4.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 20F));
            this.tableLayoutPanel4.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.tableLayoutPanel4.Controls.Add(this.LbFileList, 2, 2);
            this.tableLayoutPanel4.Controls.Add(this.RtbTestInfo, 2, 1);
            this.tableLayoutPanel4.Controls.Add(this.tableLayoutPanel5, 1, 4);
            this.tableLayoutPanel4.Controls.Add(this.uiPanel1, 1, 1);
            this.tableLayoutPanel4.Dock = System.Windows.Forms.DockStyle.Fill;
            this.tableLayoutPanel4.Location = new System.Drawing.Point(0, 0);
            this.tableLayoutPanel4.Name = "tableLayoutPanel4";
            this.tableLayoutPanel4.RowCount = 5;
            this.tableLayoutPanel4.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.tableLayoutPanel4.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 20F));
            this.tableLayoutPanel4.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 80F));
            this.tableLayoutPanel4.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 40F));
            this.tableLayoutPanel4.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 80F));
            this.tableLayoutPanel4.Size = new System.Drawing.Size(1883, 1130);
            this.tableLayoutPanel4.TabIndex = 1;
            // 
            // BtnChoiseFolder
            // 
            this.BtnChoiseFolder.Dock = System.Windows.Forms.DockStyle.Fill;
            this.BtnChoiseFolder.Location = new System.Drawing.Point(1297, 3);
            this.BtnChoiseFolder.Name = "BtnChoiseFolder";
            this.BtnChoiseFolder.Size = new System.Drawing.Size(242, 68);
            this.BtnChoiseFolder.TabIndex = 5;
            this.BtnChoiseFolder.Text = "选择文件夹";
            this.BtnChoiseFolder.UseVisualStyleBackColor = true;
            this.BtnChoiseFolder.Click += new System.EventHandler(this.BtnChoiseFolder_Click);
            // 
            // LbFileList
            // 
            this.LbFileList.Dock = System.Windows.Forms.DockStyle.Fill;
            this.LbFileList.Font = new System.Drawing.Font("宋体", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.LbFileList.HoverColor = System.Drawing.Color.FromArgb(((int)(((byte)(155)))), ((int)(((byte)(200)))), ((int)(((byte)(255)))));
            this.LbFileList.ItemSelectForeColor = System.Drawing.Color.White;
            this.LbFileList.Location = new System.Drawing.Point(1498, 223);
            this.LbFileList.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.LbFileList.MinimumSize = new System.Drawing.Size(1, 1);
            this.LbFileList.Name = "LbFileList";
            this.LbFileList.Padding = new System.Windows.Forms.Padding(2);
            this.LbFileList.ShowText = false;
            this.LbFileList.Size = new System.Drawing.Size(360, 782);
            this.LbFileList.TabIndex = 6;
            this.LbFileList.Text = "uiListBox1";
            this.LbFileList.DoubleClick += new System.EventHandler(this.LbFileList_DoubleClick);
            // 
            // RtbTestInfo
            // 
            this.RtbTestInfo.Dock = System.Windows.Forms.DockStyle.Fill;
            this.RtbTestInfo.FillColor = System.Drawing.Color.White;
            this.RtbTestInfo.Font = new System.Drawing.Font("宋体", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.RtbTestInfo.Location = new System.Drawing.Point(1498, 25);
            this.RtbTestInfo.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.RtbTestInfo.MinimumSize = new System.Drawing.Size(1, 1);
            this.RtbTestInfo.Name = "RtbTestInfo";
            this.RtbTestInfo.Padding = new System.Windows.Forms.Padding(2);
            this.RtbTestInfo.ShowText = false;
            this.RtbTestInfo.Size = new System.Drawing.Size(360, 188);
            this.RtbTestInfo.TabIndex = 7;
            this.RtbTestInfo.TextAlignment = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // tableLayoutPanel5
            // 
            this.tableLayoutPanel5.ColumnCount = 17;
            this.tableLayoutPanel4.SetColumnSpan(this.tableLayoutPanel5, 2);
            this.tableLayoutPanel5.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.tableLayoutPanel5.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 14F));
            this.tableLayoutPanel5.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.tableLayoutPanel5.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 14F));
            this.tableLayoutPanel5.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.tableLayoutPanel5.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 14F));
            this.tableLayoutPanel5.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.tableLayoutPanel5.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 14F));
            this.tableLayoutPanel5.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.tableLayoutPanel5.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 7F));
            this.tableLayoutPanel5.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.tableLayoutPanel5.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 7F));
            this.tableLayoutPanel5.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.tableLayoutPanel5.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 15F));
            this.tableLayoutPanel5.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.tableLayoutPanel5.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 15F));
            this.tableLayoutPanel5.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.tableLayoutPanel5.Controls.Add(this.ChkForce, 1, 0);
            this.tableLayoutPanel5.Controls.Add(this.ChkCurrent, 3, 0);
            this.tableLayoutPanel5.Controls.Add(this.BtnPanRight, 11, 0);
            this.tableLayoutPanel5.Controls.Add(this.BtnPanLeft, 9, 0);
            this.tableLayoutPanel5.Controls.Add(this.BtnChoiseFolder, 13, 0);
            this.tableLayoutPanel5.Controls.Add(this.BtnExportFile, 15, 0);
            this.tableLayoutPanel5.Controls.Add(this.ChkDaqCurrent, 5, 0);
            this.tableLayoutPanel5.Controls.Add(this.ChkDaqTorque, 7, 0);
            this.tableLayoutPanel5.Dock = System.Windows.Forms.DockStyle.Fill;
            this.tableLayoutPanel5.Location = new System.Drawing.Point(23, 1053);
            this.tableLayoutPanel5.Name = "tableLayoutPanel5";
            this.tableLayoutPanel5.RowCount = 1;
            this.tableLayoutPanel5.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.tableLayoutPanel5.Size = new System.Drawing.Size(1836, 74);
            this.tableLayoutPanel5.TabIndex = 1;
            // 
            // BtnExportFile
            // 
            this.BtnExportFile.Dock = System.Windows.Forms.DockStyle.Fill;
            this.BtnExportFile.Location = new System.Drawing.Point(1565, 3);
            this.BtnExportFile.Name = "BtnExportFile";
            this.BtnExportFile.Size = new System.Drawing.Size(242, 68);
            this.BtnExportFile.TabIndex = 1;
            this.BtnExportFile.Text = "导出";
            this.BtnExportFile.UseVisualStyleBackColor = true;
            this.BtnExportFile.Click += new System.EventHandler(this.BtnExportFile_Click);
            // 
            // ChkForce
            // 
            this.ChkForce.CheckBoxSize = 24;
            this.ChkForce.Checked = true;
            this.ChkForce.Cursor = System.Windows.Forms.Cursors.Hand;
            this.ChkForce.Dock = System.Windows.Forms.DockStyle.Fill;
            this.ChkForce.Font = new System.Drawing.Font("宋体", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.ChkForce.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(48)))), ((int)(((byte)(48)))), ((int)(((byte)(48)))));
            this.ChkForce.Location = new System.Drawing.Point(23, 3);
            this.ChkForce.MinimumSize = new System.Drawing.Size(1, 1);
            this.ChkForce.Name = "ChkForce";
            this.ChkForce.Size = new System.Drawing.Size(225, 68);
            this.ChkForce.TabIndex = 2;
            this.ChkForce.Text = "Act_Force";
            this.ChkForce.CheckedChanged += new System.EventHandler(this.ChkForce_CheckedChanged);
            // 
            // ChkCurrent
            // 
            this.ChkCurrent.CheckBoxSize = 24;
            this.ChkCurrent.Checked = true;
            this.ChkCurrent.Cursor = System.Windows.Forms.Cursors.Hand;
            this.ChkCurrent.Dock = System.Windows.Forms.DockStyle.Fill;
            this.ChkCurrent.Font = new System.Drawing.Font("宋体", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.ChkCurrent.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(48)))), ((int)(((byte)(48)))), ((int)(((byte)(48)))));
            this.ChkCurrent.Location = new System.Drawing.Point(274, 3);
            this.ChkCurrent.MinimumSize = new System.Drawing.Size(1, 1);
            this.ChkCurrent.Name = "ChkCurrent";
            this.ChkCurrent.Size = new System.Drawing.Size(225, 68);
            this.ChkCurrent.TabIndex = 3;
            this.ChkCurrent.Text = "Act_Current";
            this.ChkCurrent.CheckedChanged += new System.EventHandler(this.ChkCurrent_CheckedChanged);
            // 
            // BtnPanRight
            // 
            this.BtnPanRight.Dock = System.Windows.Forms.DockStyle.Fill;
            this.BtnPanRight.Location = new System.Drawing.Point(1162, 3);
            this.BtnPanRight.Name = "BtnPanRight";
            this.BtnPanRight.Size = new System.Drawing.Size(109, 68);
            this.BtnPanRight.TabIndex = 4;
            this.BtnPanRight.Text = ">>";
            this.BtnPanRight.UseVisualStyleBackColor = true;
            this.BtnPanRight.Click += new System.EventHandler(this.BtnPanRight_Click);
            // 
            // BtnPanLeft
            // 
            this.BtnPanLeft.Dock = System.Windows.Forms.DockStyle.Fill;
            this.BtnPanLeft.Location = new System.Drawing.Point(1027, 3);
            this.BtnPanLeft.Name = "BtnPanLeft";
            this.BtnPanLeft.Size = new System.Drawing.Size(109, 68);
            this.BtnPanLeft.TabIndex = 5;
            this.BtnPanLeft.Text = "<<";
            this.BtnPanLeft.UseVisualStyleBackColor = true;
            this.BtnPanLeft.Click += new System.EventHandler(this.BtnPanLeft_Click);
            // 
            // uiPanel1
            // 
            this.uiPanel1.Controls.Add(this.ProgressShow);
            this.uiPanel1.Controls.Add(this.zedGraphControlHistory);
            this.uiPanel1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.uiPanel1.Font = new System.Drawing.Font("宋体", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.uiPanel1.Location = new System.Drawing.Point(24, 25);
            this.uiPanel1.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.uiPanel1.MinimumSize = new System.Drawing.Size(1, 1);
            this.uiPanel1.Name = "uiPanel1";
            this.tableLayoutPanel4.SetRowSpan(this.uiPanel1, 2);
            this.uiPanel1.Size = new System.Drawing.Size(1466, 980);
            this.uiPanel1.TabIndex = 2;
            this.uiPanel1.Text = null;
            this.uiPanel1.TextAlignment = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // ProgressShow
            // 
            this.ProgressShow.Active = true;
            this.ProgressShow.BackColor = System.Drawing.Color.Transparent;
            this.ProgressShow.Font = new System.Drawing.Font("宋体", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.ProgressShow.Location = new System.Drawing.Point(660, 437);
            this.ProgressShow.MinimumSize = new System.Drawing.Size(1, 1);
            this.ProgressShow.Name = "ProgressShow";
            this.ProgressShow.Size = new System.Drawing.Size(147, 106);
            this.ProgressShow.TabIndex = 4;
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
            this.zedGraphControlHistory.Size = new System.Drawing.Size(1466, 980);
            this.zedGraphControlHistory.TabIndex = 0;
            this.zedGraphControlHistory.UseExtendedPrintDialog = true;
            // 
            // ChkDaqCurrent
            // 
            this.ChkDaqCurrent.CheckBoxSize = 24;
            this.ChkDaqCurrent.Checked = true;
            this.ChkDaqCurrent.Cursor = System.Windows.Forms.Cursors.Hand;
            this.ChkDaqCurrent.Dock = System.Windows.Forms.DockStyle.Fill;
            this.ChkDaqCurrent.Font = new System.Drawing.Font("宋体", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.ChkDaqCurrent.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(48)))), ((int)(((byte)(48)))), ((int)(((byte)(48)))));
            this.ChkDaqCurrent.Location = new System.Drawing.Point(525, 3);
            this.ChkDaqCurrent.MinimumSize = new System.Drawing.Size(1, 1);
            this.ChkDaqCurrent.Name = "ChkDaqCurrent";
            this.ChkDaqCurrent.Size = new System.Drawing.Size(225, 68);
            this.ChkDaqCurrent.TabIndex = 6;
            this.ChkDaqCurrent.Text = "DAQ_Current";
            this.ChkDaqCurrent.CheckedChanged += new System.EventHandler(this.ChkDaqCurrent_CheckedChanged_1);
            // 
            // ChkDaqTorque
            // 
            this.ChkDaqTorque.CheckBoxSize = 24;
            this.ChkDaqTorque.Checked = true;
            this.ChkDaqTorque.Cursor = System.Windows.Forms.Cursors.Hand;
            this.ChkDaqTorque.Dock = System.Windows.Forms.DockStyle.Fill;
            this.ChkDaqTorque.Font = new System.Drawing.Font("宋体", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.ChkDaqTorque.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(48)))), ((int)(((byte)(48)))), ((int)(((byte)(48)))));
            this.ChkDaqTorque.Location = new System.Drawing.Point(776, 3);
            this.ChkDaqTorque.MinimumSize = new System.Drawing.Size(1, 1);
            this.ChkDaqTorque.Name = "ChkDaqTorque";
            this.ChkDaqTorque.Size = new System.Drawing.Size(225, 68);
            this.ChkDaqTorque.TabIndex = 7;
            this.ChkDaqTorque.Text = "DAQ_Torque";
            this.ChkDaqTorque.CheckedChanged += new System.EventHandler(this.ChkDaqTorque_CheckedChanged_1);
            // 
            // FrmRawPlayBack
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(13F, 26F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(1883, 1130);
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
        private Sunny.UI.UICheckBox ChkForce;
        private Sunny.UI.UICheckBox ChkCurrent;
        private System.Windows.Forms.Button BtnPanRight;
        private System.Windows.Forms.Button BtnPanLeft;
        private Sunny.UI.UIPanel uiPanel1;
        private System.Windows.Forms.Button BtnChoiseFolder;
        private Sunny.UI.UIListBox LbFileList;
        private Sunny.UI.UIRichTextBox RtbTestInfo;
        private Sunny.UI.UIProgressIndicator ProgressShow;
        private Sunny.UI.UICheckBox ChkDaqCurrent;
        private Sunny.UI.UICheckBox ChkDaqTorque;
    }
}