namespace SocketClient
{
    partial class Frm_WifiNodule
    {
        /// <summary>
        /// 必需的设计器变量。
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// 清理所有正在使用的资源。
        /// </summary>
        /// <param name="disposing">如果应释放托管资源，为 true；否则为 false。</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows 窗体设计器生成的代码

        /// <summary>
        /// 设计器支持所需的方法 - 不要
        /// 使用代码编辑器修改此方法的内容。
        /// </summary>
        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            this.btnConn = new System.Windows.Forms.Button();
            this.statusStrip1 = new System.Windows.Forms.StatusStrip();
            this.timer1 = new System.Windows.Forms.Timer(this.components);
            this.btnBegin = new System.Windows.Forms.Button();
            this.btnStop = new System.Windows.Forms.Button();
            this.label1 = new System.Windows.Forms.Label();
            this.tbxIP = new System.Windows.Forms.TextBox();
            this.label2 = new System.Windows.Forms.Label();
            this.tbxPort = new System.Windows.Forms.TextBox();
            this.RtbSendMsg = new System.Windows.Forms.RichTextBox();
            this.timerClear = new System.Windows.Forms.Timer(this.components);
            this.tableLayoutPanel1 = new System.Windows.Forms.TableLayoutPanel();
            this.groupBox1 = new System.Windows.Forms.GroupBox();
            this.groupBox2 = new System.Windows.Forms.GroupBox();
            this.RtbRecvMsg = new System.Windows.Forms.RichTextBox();
            this.groupBox3 = new System.Windows.Forms.GroupBox();
            this.RtbRunMsg = new System.Windows.Forms.RichTextBox();
            this.tableLayoutPanel1.SuspendLayout();
            this.groupBox1.SuspendLayout();
            this.groupBox2.SuspendLayout();
            this.groupBox3.SuspendLayout();
            this.SuspendLayout();
            // 
            // btnConn
            // 
            this.btnConn.Dock = System.Windows.Forms.DockStyle.Fill;
            this.btnConn.Location = new System.Drawing.Point(45, 1065);
            this.btnConn.Margin = new System.Windows.Forms.Padding(6);
            this.btnConn.Name = "btnConn";
            this.btnConn.Size = new System.Drawing.Size(462, 44);
            this.btnConn.TabIndex = 4;
            this.btnConn.Text = "建立连接";
            this.btnConn.UseVisualStyleBackColor = true;
            this.btnConn.Click += new System.EventHandler(this.btnConn_Click);
            // 
            // statusStrip1
            // 
            this.statusStrip1.Location = new System.Drawing.Point(0, 1129);
            this.statusStrip1.Name = "statusStrip1";
            this.statusStrip1.Padding = new System.Windows.Forms.Padding(3, 0, 30, 0);
            this.statusStrip1.Size = new System.Drawing.Size(1976, 22);
            this.statusStrip1.TabIndex = 5;
            this.statusStrip1.Text = "statusStrip1";
            // 
            // timer1
            // 
            this.timer1.Interval = 10;
            this.timer1.Tick += new System.EventHandler(this.timer1_Tick);
            // 
            // btnBegin
            // 
            this.btnBegin.Dock = System.Windows.Forms.DockStyle.Fill;
            this.btnBegin.Location = new System.Drawing.Point(990, 1062);
            this.btnBegin.Name = "btnBegin";
            this.btnBegin.Size = new System.Drawing.Size(468, 50);
            this.btnBegin.TabIndex = 7;
            this.btnBegin.Text = "循环发送开始";
            this.btnBegin.UseVisualStyleBackColor = true;
            this.btnBegin.Click += new System.EventHandler(this.btnBegin_Click);
            // 
            // btnStop
            // 
            this.btnStop.Dock = System.Windows.Forms.DockStyle.Fill;
            this.btnStop.Location = new System.Drawing.Point(1464, 1062);
            this.btnStop.Name = "btnStop";
            this.btnStop.Size = new System.Drawing.Size(468, 50);
            this.btnStop.TabIndex = 8;
            this.btnStop.Text = "循环发送停止";
            this.btnStop.UseVisualStyleBackColor = true;
            this.btnStop.Click += new System.EventHandler(this.btnStop_Click);
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Dock = System.Windows.Forms.DockStyle.Right;
            this.label1.Location = new System.Drawing.Point(378, 11);
            this.label1.Margin = new System.Windows.Forms.Padding(6, 0, 6, 0);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(129, 56);
            this.label1.TabIndex = 0;
            this.label1.Text = "服务器IP:";
            this.label1.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // tbxIP
            // 
            this.tbxIP.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(54)))), ((int)(((byte)(80)))), ((int)(((byte)(126)))));
            this.tbxIP.Dock = System.Windows.Forms.DockStyle.Left;
            this.tbxIP.Font = new System.Drawing.Font("微软雅黑", 10.5F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.tbxIP.ForeColor = System.Drawing.Color.White;
            this.tbxIP.Location = new System.Drawing.Point(519, 17);
            this.tbxIP.Margin = new System.Windows.Forms.Padding(6);
            this.tbxIP.Name = "tbxIP";
            this.tbxIP.Size = new System.Drawing.Size(212, 48);
            this.tbxIP.TabIndex = 1;
            this.tbxIP.Text = "127.0.0.1";
            this.tbxIP.TextAlign = System.Windows.Forms.HorizontalAlignment.Center;
            // 
            // label2
            // 
            this.label2.AutoSize = true;
            this.label2.Dock = System.Windows.Forms.DockStyle.Right;
            this.label2.Location = new System.Drawing.Point(1378, 11);
            this.label2.Margin = new System.Windows.Forms.Padding(6, 0, 6, 0);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(77, 56);
            this.label2.TabIndex = 2;
            this.label2.Text = "端口:";
            this.label2.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // tbxPort
            // 
            this.tbxPort.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(54)))), ((int)(((byte)(80)))), ((int)(((byte)(126)))));
            this.tbxPort.Dock = System.Windows.Forms.DockStyle.Left;
            this.tbxPort.Font = new System.Drawing.Font("微软雅黑", 10.5F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.tbxPort.ForeColor = System.Drawing.Color.White;
            this.tbxPort.Location = new System.Drawing.Point(1467, 17);
            this.tbxPort.Margin = new System.Windows.Forms.Padding(6);
            this.tbxPort.Name = "tbxPort";
            this.tbxPort.Size = new System.Drawing.Size(212, 48);
            this.tbxPort.TabIndex = 3;
            this.tbxPort.Text = "9988";
            this.tbxPort.TextAlign = System.Windows.Forms.HorizontalAlignment.Center;
            // 
            // RtbSendMsg
            // 
            this.RtbSendMsg.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(54)))), ((int)(((byte)(80)))), ((int)(((byte)(126)))));
            this.RtbSendMsg.Dock = System.Windows.Forms.DockStyle.Fill;
            this.RtbSendMsg.Font = new System.Drawing.Font("微软雅黑", 10.5F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.RtbSendMsg.ForeColor = System.Drawing.Color.White;
            this.RtbSendMsg.Location = new System.Drawing.Point(4, 34);
            this.RtbSendMsg.Margin = new System.Windows.Forms.Padding(4);
            this.RtbSendMsg.Name = "RtbSendMsg";
            this.RtbSendMsg.Size = new System.Drawing.Size(1880, 518);
            this.RtbSendMsg.TabIndex = 9;
            this.RtbSendMsg.Text = "";
            // 
            // timerClear
            // 
            this.timerClear.Enabled = true;
            this.timerClear.Interval = 600000;
            this.timerClear.Tick += new System.EventHandler(this.timerClear_Tick);
            // 
            // tableLayoutPanel1
            // 
            this.tableLayoutPanel1.ColumnCount = 6;
            this.tableLayoutPanel1.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 2F));
            this.tableLayoutPanel1.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 24F));
            this.tableLayoutPanel1.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 24F));
            this.tableLayoutPanel1.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 24F));
            this.tableLayoutPanel1.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 24F));
            this.tableLayoutPanel1.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 2F));
            this.tableLayoutPanel1.Controls.Add(this.tbxPort, 4, 1);
            this.tableLayoutPanel1.Controls.Add(this.btnConn, 1, 5);
            this.tableLayoutPanel1.Controls.Add(this.label2, 3, 1);
            this.tableLayoutPanel1.Controls.Add(this.btnBegin, 3, 5);
            this.tableLayoutPanel1.Controls.Add(this.tbxIP, 2, 1);
            this.tableLayoutPanel1.Controls.Add(this.label1, 1, 1);
            this.tableLayoutPanel1.Controls.Add(this.btnStop, 4, 5);
            this.tableLayoutPanel1.Controls.Add(this.groupBox1, 1, 2);
            this.tableLayoutPanel1.Controls.Add(this.groupBox2, 1, 3);
            this.tableLayoutPanel1.Controls.Add(this.groupBox3, 1, 4);
            this.tableLayoutPanel1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.tableLayoutPanel1.Location = new System.Drawing.Point(0, 0);
            this.tableLayoutPanel1.Margin = new System.Windows.Forms.Padding(4);
            this.tableLayoutPanel1.Name = "tableLayoutPanel1";
            this.tableLayoutPanel1.RowCount = 7;
            this.tableLayoutPanel1.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 1F));
            this.tableLayoutPanel1.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 5F));
            this.tableLayoutPanel1.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 50F));
            this.tableLayoutPanel1.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 23F));
            this.tableLayoutPanel1.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 15F));
            this.tableLayoutPanel1.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 5F));
            this.tableLayoutPanel1.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 1F));
            this.tableLayoutPanel1.Size = new System.Drawing.Size(1976, 1129);
            this.tableLayoutPanel1.TabIndex = 10;
            // 
            // groupBox1
            // 
            this.tableLayoutPanel1.SetColumnSpan(this.groupBox1, 4);
            this.groupBox1.Controls.Add(this.RtbSendMsg);
            this.groupBox1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.groupBox1.Location = new System.Drawing.Point(43, 71);
            this.groupBox1.Margin = new System.Windows.Forms.Padding(4);
            this.groupBox1.Name = "groupBox1";
            this.groupBox1.Padding = new System.Windows.Forms.Padding(4);
            this.groupBox1.Size = new System.Drawing.Size(1888, 556);
            this.groupBox1.TabIndex = 9;
            this.groupBox1.TabStop = false;
            this.groupBox1.Text = "发送数据";
            // 
            // groupBox2
            // 
            this.tableLayoutPanel1.SetColumnSpan(this.groupBox2, 4);
            this.groupBox2.Controls.Add(this.RtbRecvMsg);
            this.groupBox2.Dock = System.Windows.Forms.DockStyle.Fill;
            this.groupBox2.Location = new System.Drawing.Point(43, 635);
            this.groupBox2.Margin = new System.Windows.Forms.Padding(4);
            this.groupBox2.Name = "groupBox2";
            this.groupBox2.Padding = new System.Windows.Forms.Padding(4);
            this.groupBox2.Size = new System.Drawing.Size(1888, 251);
            this.groupBox2.TabIndex = 10;
            this.groupBox2.TabStop = false;
            this.groupBox2.Text = "接收数据";
            // 
            // RtbRecvMsg
            // 
            this.RtbRecvMsg.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(54)))), ((int)(((byte)(80)))), ((int)(((byte)(126)))));
            this.RtbRecvMsg.Dock = System.Windows.Forms.DockStyle.Fill;
            this.RtbRecvMsg.Font = new System.Drawing.Font("微软雅黑", 10.5F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.RtbRecvMsg.ForeColor = System.Drawing.Color.White;
            this.RtbRecvMsg.Location = new System.Drawing.Point(4, 34);
            this.RtbRecvMsg.Margin = new System.Windows.Forms.Padding(4);
            this.RtbRecvMsg.Name = "RtbRecvMsg";
            this.RtbRecvMsg.Size = new System.Drawing.Size(1880, 213);
            this.RtbRecvMsg.TabIndex = 10;
            this.RtbRecvMsg.Text = "";
            // 
            // groupBox3
            // 
            this.tableLayoutPanel1.SetColumnSpan(this.groupBox3, 4);
            this.groupBox3.Controls.Add(this.RtbRunMsg);
            this.groupBox3.Dock = System.Windows.Forms.DockStyle.Fill;
            this.groupBox3.Location = new System.Drawing.Point(43, 894);
            this.groupBox3.Margin = new System.Windows.Forms.Padding(4);
            this.groupBox3.Name = "groupBox3";
            this.groupBox3.Padding = new System.Windows.Forms.Padding(4);
            this.groupBox3.Size = new System.Drawing.Size(1888, 161);
            this.groupBox3.TabIndex = 11;
            this.groupBox3.TabStop = false;
            this.groupBox3.Text = "运行信息";
            // 
            // RtbRunMsg
            // 
            this.RtbRunMsg.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(54)))), ((int)(((byte)(80)))), ((int)(((byte)(126)))));
            this.RtbRunMsg.Dock = System.Windows.Forms.DockStyle.Fill;
            this.RtbRunMsg.Font = new System.Drawing.Font("微软雅黑", 10.5F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.RtbRunMsg.ForeColor = System.Drawing.Color.White;
            this.RtbRunMsg.Location = new System.Drawing.Point(4, 34);
            this.RtbRunMsg.Margin = new System.Windows.Forms.Padding(4);
            this.RtbRunMsg.Name = "RtbRunMsg";
            this.RtbRunMsg.Size = new System.Drawing.Size(1880, 123);
            this.RtbRunMsg.TabIndex = 11;
            this.RtbRunMsg.Text = "";
            // 
            // Frm_WifiNodule
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(13F, 26F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.AutoSize = true;
            this.ClientSize = new System.Drawing.Size(1976, 1151);
            this.Controls.Add(this.tableLayoutPanel1);
            this.Controls.Add(this.statusStrip1);
            this.Margin = new System.Windows.Forms.Padding(6);
            this.Name = "Frm_WifiNodule";
            this.Text = "EMB2";
            this.WindowState = System.Windows.Forms.FormWindowState.Maximized;
            this.Load += new System.EventHandler(this.Frm_Gprmc_Load);
            this.tableLayoutPanel1.ResumeLayout(false);
            this.tableLayoutPanel1.PerformLayout();
            this.groupBox1.ResumeLayout(false);
            this.groupBox2.ResumeLayout(false);
            this.groupBox3.ResumeLayout(false);
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.Button btnConn;
        private System.Windows.Forms.StatusStrip statusStrip1;
        private System.Windows.Forms.Timer timer1;
        private System.Windows.Forms.Button btnBegin;
        private System.Windows.Forms.Button btnStop;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.TextBox tbxIP;
        private System.Windows.Forms.Label label2;
        private System.Windows.Forms.TextBox tbxPort;
        private System.Windows.Forms.RichTextBox RtbSendMsg;
        private System.Windows.Forms.Timer timerClear;
        private System.Windows.Forms.TableLayoutPanel tableLayoutPanel1;
        private System.Windows.Forms.GroupBox groupBox1;
        private System.Windows.Forms.GroupBox groupBox2;
        private System.Windows.Forms.RichTextBox RtbRecvMsg;
        private System.Windows.Forms.GroupBox groupBox3;
        private System.Windows.Forms.RichTextBox RtbRunMsg;
    }
}

