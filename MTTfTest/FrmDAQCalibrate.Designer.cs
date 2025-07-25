namespace MTEmbTest
{
    partial class FrmDAQCalibrate
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
            this.uiTableLayoutPanel1 = new Sunny.UI.UITableLayoutPanel();
            this.zedGraphDAQCalibrate = new ZedGraph.ZedGraphControl();
            this.dgvRealData = new Sunny.UI.UIDataGridView();
            this.BtnZeroCalibrate = new Sunny.UI.UIButton();
            this.BtnStopCalibrate = new Sunny.UI.UIButton();
            this.RtbInfo = new Sunny.UI.UIRichTextBox();
            this.TimerCalibrate = new System.Windows.Forms.Timer(this.components);
            this.uiTableLayoutPanel1.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.dgvRealData)).BeginInit();
            this.SuspendLayout();
            // 
            // uiTableLayoutPanel1
            // 
            this.uiTableLayoutPanel1.ColumnCount = 7;
            this.uiTableLayoutPanel1.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 5F));
            this.uiTableLayoutPanel1.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 22.5F));
            this.uiTableLayoutPanel1.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 22.5F));
            this.uiTableLayoutPanel1.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 22.5F));
            this.uiTableLayoutPanel1.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 15F));
            this.uiTableLayoutPanel1.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 7.5F));
            this.uiTableLayoutPanel1.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 5F));
            this.uiTableLayoutPanel1.Controls.Add(this.zedGraphDAQCalibrate, 1, 1);
            this.uiTableLayoutPanel1.Controls.Add(this.dgvRealData, 1, 5);
            this.uiTableLayoutPanel1.Controls.Add(this.BtnZeroCalibrate, 5, 5);
            this.uiTableLayoutPanel1.Controls.Add(this.BtnStopCalibrate, 5, 7);
            this.uiTableLayoutPanel1.Controls.Add(this.RtbInfo, 3, 5);
            this.uiTableLayoutPanel1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.uiTableLayoutPanel1.Location = new System.Drawing.Point(0, 0);
            this.uiTableLayoutPanel1.Name = "uiTableLayoutPanel1";
            this.uiTableLayoutPanel1.RowCount = 9;
            this.uiTableLayoutPanel1.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 5F));
            this.uiTableLayoutPanel1.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 22.5F));
            this.uiTableLayoutPanel1.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 22.5F));
            this.uiTableLayoutPanel1.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 22.5F));
            this.uiTableLayoutPanel1.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 40F));
            this.uiTableLayoutPanel1.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 7.5F));
            this.uiTableLayoutPanel1.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 7.5F));
            this.uiTableLayoutPanel1.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 7.5F));
            this.uiTableLayoutPanel1.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 5F));
            this.uiTableLayoutPanel1.Size = new System.Drawing.Size(2028, 1060);
            this.uiTableLayoutPanel1.TabIndex = 0;
            this.uiTableLayoutPanel1.TagString = null;
            // 
            // zedGraphDAQCalibrate
            // 
            this.uiTableLayoutPanel1.SetColumnSpan(this.zedGraphDAQCalibrate, 5);
            this.zedGraphDAQCalibrate.Dock = System.Windows.Forms.DockStyle.Fill;
            this.zedGraphDAQCalibrate.Location = new System.Drawing.Point(107, 57);
            this.zedGraphDAQCalibrate.Margin = new System.Windows.Forms.Padding(6, 6, 6, 6);
            this.zedGraphDAQCalibrate.Name = "zedGraphDAQCalibrate";
            this.uiTableLayoutPanel1.SetRowSpan(this.zedGraphDAQCalibrate, 3);
            this.zedGraphDAQCalibrate.ScrollGrace = 0D;
            this.zedGraphDAQCalibrate.ScrollMaxX = 0D;
            this.zedGraphDAQCalibrate.ScrollMaxY = 0D;
            this.zedGraphDAQCalibrate.ScrollMaxY2 = 0D;
            this.zedGraphDAQCalibrate.ScrollMinX = 0D;
            this.zedGraphDAQCalibrate.ScrollMinY = 0D;
            this.zedGraphDAQCalibrate.ScrollMinY2 = 0D;
            this.zedGraphDAQCalibrate.Size = new System.Drawing.Size(1812, 675);
            this.zedGraphDAQCalibrate.TabIndex = 0;
            this.zedGraphDAQCalibrate.UseExtendedPrintDialog = true;
            // 
            // dgvRealData
            // 
            dataGridViewCellStyle1.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(235)))), ((int)(((byte)(243)))), ((int)(((byte)(255)))));
            this.dgvRealData.AlternatingRowsDefaultCellStyle = dataGridViewCellStyle1;
            this.dgvRealData.BackgroundColor = System.Drawing.Color.White;
            this.dgvRealData.ColumnHeadersBorderStyle = System.Windows.Forms.DataGridViewHeaderBorderStyle.Single;
            dataGridViewCellStyle2.Alignment = System.Windows.Forms.DataGridViewContentAlignment.MiddleCenter;
            dataGridViewCellStyle2.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(80)))), ((int)(((byte)(160)))), ((int)(((byte)(255)))));
            dataGridViewCellStyle2.Font = new System.Drawing.Font("宋体", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            dataGridViewCellStyle2.ForeColor = System.Drawing.Color.White;
            dataGridViewCellStyle2.SelectionBackColor = System.Drawing.SystemColors.Highlight;
            dataGridViewCellStyle2.SelectionForeColor = System.Drawing.SystemColors.HighlightText;
            dataGridViewCellStyle2.WrapMode = System.Windows.Forms.DataGridViewTriState.True;
            this.dgvRealData.ColumnHeadersDefaultCellStyle = dataGridViewCellStyle2;
            this.dgvRealData.ColumnHeadersHeight = 50;
            this.uiTableLayoutPanel1.SetColumnSpan(this.dgvRealData, 2);
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
            this.dgvRealData.Location = new System.Drawing.Point(104, 781);
            this.dgvRealData.Name = "dgvRealData";
            dataGridViewCellStyle4.Alignment = System.Windows.Forms.DataGridViewContentAlignment.MiddleLeft;
            dataGridViewCellStyle4.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(235)))), ((int)(((byte)(243)))), ((int)(((byte)(255)))));
            dataGridViewCellStyle4.Font = new System.Drawing.Font("Arial Narrow", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            dataGridViewCellStyle4.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(48)))), ((int)(((byte)(48)))), ((int)(((byte)(48)))));
            dataGridViewCellStyle4.SelectionBackColor = System.Drawing.Color.FromArgb(((int)(((byte)(80)))), ((int)(((byte)(160)))), ((int)(((byte)(255)))));
            dataGridViewCellStyle4.SelectionForeColor = System.Drawing.Color.White;
            dataGridViewCellStyle4.WrapMode = System.Windows.Forms.DataGridViewTriState.True;
            this.dgvRealData.RowHeadersDefaultCellStyle = dataGridViewCellStyle4;
            this.dgvRealData.RowHeadersWidth = 90;
            dataGridViewCellStyle5.BackColor = System.Drawing.Color.White;
            dataGridViewCellStyle5.Font = new System.Drawing.Font("Arial Narrow", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.dgvRealData.RowsDefaultCellStyle = dataGridViewCellStyle5;
            this.uiTableLayoutPanel1.SetRowSpan(this.dgvRealData, 3);
            this.dgvRealData.RowTemplate.Height = 50;
            this.dgvRealData.SelectedIndex = -1;
            this.dgvRealData.Size = new System.Drawing.Size(906, 222);
            this.dgvRealData.StripeOddColor = System.Drawing.Color.FromArgb(((int)(((byte)(235)))), ((int)(((byte)(243)))), ((int)(((byte)(255)))));
            this.dgvRealData.TabIndex = 1;
            this.dgvRealData.CellContentClick += new System.Windows.Forms.DataGridViewCellEventHandler(this.dgvRealData_CellContentClick);
            // 
            // BtnZeroCalibrate
            // 
            this.BtnZeroCalibrate.Cursor = System.Windows.Forms.Cursors.Hand;
            this.BtnZeroCalibrate.Dock = System.Windows.Forms.DockStyle.Fill;
            this.BtnZeroCalibrate.Font = new System.Drawing.Font("宋体", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.BtnZeroCalibrate.Location = new System.Drawing.Point(1783, 798);
            this.BtnZeroCalibrate.Margin = new System.Windows.Forms.Padding(10, 20, 10, 20);
            this.BtnZeroCalibrate.MinimumSize = new System.Drawing.Size(1, 1);
            this.BtnZeroCalibrate.Name = "BtnZeroCalibrate";
            this.BtnZeroCalibrate.Size = new System.Drawing.Size(132, 36);
            this.BtnZeroCalibrate.TabIndex = 2;
            this.BtnZeroCalibrate.Text = "零位校准";
            this.BtnZeroCalibrate.TipsFont = new System.Drawing.Font("宋体", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.BtnZeroCalibrate.Click += new System.EventHandler(this.BtnZeroCalibrate_Click);
            // 
            // BtnStopCalibrate
            // 
            this.BtnStopCalibrate.Cursor = System.Windows.Forms.Cursors.Hand;
            this.BtnStopCalibrate.Dock = System.Windows.Forms.DockStyle.Fill;
            this.BtnStopCalibrate.Font = new System.Drawing.Font("宋体", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.BtnStopCalibrate.Location = new System.Drawing.Point(1783, 950);
            this.BtnStopCalibrate.Margin = new System.Windows.Forms.Padding(10, 20, 10, 20);
            this.BtnStopCalibrate.MinimumSize = new System.Drawing.Size(1, 1);
            this.BtnStopCalibrate.Name = "BtnStopCalibrate";
            this.BtnStopCalibrate.Size = new System.Drawing.Size(132, 36);
            this.BtnStopCalibrate.TabIndex = 4;
            this.BtnStopCalibrate.Text = "停止校准";
            this.BtnStopCalibrate.TipsFont = new System.Drawing.Font("宋体", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.BtnStopCalibrate.Click += new System.EventHandler(this.BtnStopCalibrate_Click);
            // 
            // RtbInfo
            // 
            this.uiTableLayoutPanel1.SetColumnSpan(this.RtbInfo, 2);
            this.RtbInfo.Dock = System.Windows.Forms.DockStyle.Fill;
            this.RtbInfo.FillColor = System.Drawing.Color.White;
            this.RtbInfo.Font = new System.Drawing.Font("宋体", 10.5782F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.RtbInfo.Location = new System.Drawing.Point(1017, 783);
            this.RtbInfo.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.RtbInfo.MinimumSize = new System.Drawing.Size(1, 1);
            this.RtbInfo.Name = "RtbInfo";
            this.RtbInfo.Padding = new System.Windows.Forms.Padding(2);
            this.uiTableLayoutPanel1.SetRowSpan(this.RtbInfo, 3);
            this.RtbInfo.ShowText = false;
            this.RtbInfo.Size = new System.Drawing.Size(752, 218);
            this.RtbInfo.TabIndex = 5;
            this.RtbInfo.TextAlignment = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // TimerCalibrate
            // 
            this.TimerCalibrate.Interval = 3000;
            this.TimerCalibrate.Tick += new System.EventHandler(this.TimerCalibrate_Tick);
            // 
            // FrmDAQCalibrate
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(13F, 26F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(2028, 1060);
            this.ControlBox = false;
            this.Controls.Add(this.uiTableLayoutPanel1);
            this.Name = "FrmDAQCalibrate";
            this.Text = "数采卡零位校准";
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.FrmDAQCalibrate_FormClosing);
            this.Load += new System.EventHandler(this.FrmDAQCalibrate_Load);
            this.uiTableLayoutPanel1.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.dgvRealData)).EndInit();
            this.ResumeLayout(false);

        }

        #endregion

        private Sunny.UI.UITableLayoutPanel uiTableLayoutPanel1;
        private ZedGraph.ZedGraphControl zedGraphDAQCalibrate;
        private Sunny.UI.UIDataGridView dgvRealData;
        private Sunny.UI.UIButton BtnZeroCalibrate;
        private Sunny.UI.UIButton BtnStopCalibrate;
        private System.Windows.Forms.Timer TimerCalibrate;
        private Sunny.UI.UIRichTextBox RtbInfo;
    }
}