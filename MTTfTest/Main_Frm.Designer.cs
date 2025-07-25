namespace MtEmbTest
{
    partial class Main_Frm
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
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(Main_Frm));
            this.menuStripMain = new System.Windows.Forms.MenuStrip();
            this.TsmDAQ = new System.Windows.Forms.ToolStripMenuItem();
            this.TsmRealMinitor = new System.Windows.Forms.ToolStripMenuItem();
            this.TsmAdjustTorque = new System.Windows.Forms.ToolStripMenuItem();
            this.TsmPlayBack = new System.Windows.Forms.ToolStripMenuItem();
            this.TsmCharacterPlayBack = new System.Windows.Forms.ToolStripMenuItem();
            this.TsmRawPlayBack = new System.Windows.Forms.ToolStripMenuItem();
            this.TsmSetting = new System.Windows.Forms.ToolStripMenuItem();
            this.TsmDAQCalibrate = new System.Windows.Forms.ToolStripMenuItem();
            this.TsmWindows = new System.Windows.Forms.ToolStripMenuItem();
            this.TsmHorizon = new System.Windows.Forms.ToolStripMenuItem();
            this.TsmVertical = new System.Windows.Forms.ToolStripMenuItem();
            this.TsmLayout = new System.Windows.Forms.ToolStripMenuItem();
            this.关于ToolStripMenuItem1 = new System.Windows.Forms.ToolStripMenuItem();
            this.TsmPower = new System.Windows.Forms.ToolStripMenuItem();
            this.menuStripMain.SuspendLayout();
            this.SuspendLayout();
            // 
            // menuStripMain
            // 
            this.menuStripMain.Font = new System.Drawing.Font("Microsoft YaHei UI", 8.872038F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(134)));
            this.menuStripMain.GripMargin = new System.Windows.Forms.Padding(2, 2, 0, 2);
            this.menuStripMain.ImageScalingSize = new System.Drawing.Size(80, 80);
            this.menuStripMain.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.TsmDAQ,
            this.TsmPlayBack,
            this.TsmSetting,
            this.TsmDAQCalibrate,
            this.TsmWindows,
            this.TsmPower,
            this.关于ToolStripMenuItem1});
            this.menuStripMain.LayoutStyle = System.Windows.Forms.ToolStripLayoutStyle.HorizontalStackWithOverflow;
            this.menuStripMain.Location = new System.Drawing.Point(0, 0);
            this.menuStripMain.Name = "menuStripMain";
            this.menuStripMain.Padding = new System.Windows.Forms.Padding(13, 0, 0, 0);
            this.menuStripMain.Size = new System.Drawing.Size(2308, 42);
            this.menuStripMain.Stretch = false;
            this.menuStripMain.TabIndex = 0;
            this.menuStripMain.Text = "Menu";
            // 
            // TsmDAQ
            // 
            this.TsmDAQ.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.TsmRealMinitor,
            this.TsmAdjustTorque});
            this.TsmDAQ.Name = "TsmDAQ";
            this.TsmDAQ.Size = new System.Drawing.Size(89, 42);
            this.TsmDAQ.Text = "试验";
            this.TsmDAQ.Click += new System.EventHandler(this.TsmDAQ_Click);
            // 
            // TsmRealMinitor
            // 
            this.TsmRealMinitor.Name = "TsmRealMinitor";
            this.TsmRealMinitor.Size = new System.Drawing.Size(266, 48);
            this.TsmRealMinitor.Text = "实时监视";
            this.TsmRealMinitor.Click += new System.EventHandler(this.TsmRealMinitor_Click);
            // 
            // TsmAdjustTorque
            // 
            this.TsmAdjustTorque.Name = "TsmAdjustTorque";
            this.TsmAdjustTorque.Size = new System.Drawing.Size(266, 48);
            this.TsmAdjustTorque.Text = "扭矩调节";
            this.TsmAdjustTorque.Click += new System.EventHandler(this.TsmAdjustTorque_Click);
            // 
            // TsmPlayBack
            // 
            this.TsmPlayBack.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.TsmCharacterPlayBack,
            this.TsmRawPlayBack});
            this.TsmPlayBack.Name = "TsmPlayBack";
            this.TsmPlayBack.Size = new System.Drawing.Size(141, 42);
            this.TsmPlayBack.Text = "数据回放";
            this.TsmPlayBack.Click += new System.EventHandler(this.TsmPlayBack_Click);
            // 
            // TsmCharacterPlayBack
            // 
            this.TsmCharacterPlayBack.Name = "TsmCharacterPlayBack";
            this.TsmCharacterPlayBack.Size = new System.Drawing.Size(266, 48);
            this.TsmCharacterPlayBack.Text = "特征值";
            this.TsmCharacterPlayBack.Click += new System.EventHandler(this.TsmCharacterPlayBack_Click);
            // 
            // TsmRawPlayBack
            // 
            this.TsmRawPlayBack.Name = "TsmRawPlayBack";
            this.TsmRawPlayBack.Size = new System.Drawing.Size(266, 48);
            this.TsmRawPlayBack.Text = "原始数据";
            this.TsmRawPlayBack.Click += new System.EventHandler(this.TsmRawPlayBack_Click);
            // 
            // TsmSetting
            // 
            this.TsmSetting.Name = "TsmSetting";
            this.TsmSetting.Size = new System.Drawing.Size(89, 42);
            this.TsmSetting.Text = "设置";
            this.TsmSetting.Click += new System.EventHandler(this.TsmSetting_Click);
            // 
            // TsmDAQCalibrate
            // 
            this.TsmDAQCalibrate.Name = "TsmDAQCalibrate";
            this.TsmDAQCalibrate.Size = new System.Drawing.Size(167, 42);
            this.TsmDAQCalibrate.Text = "数采卡校准";
            this.TsmDAQCalibrate.Click += new System.EventHandler(this.TsmDAQCalibrate_Click);
            // 
            // TsmWindows
            // 
            this.TsmWindows.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.TsmHorizon,
            this.TsmVertical,
            this.TsmLayout});
            this.TsmWindows.Name = "TsmWindows";
            this.TsmWindows.Size = new System.Drawing.Size(202, 42);
            this.TsmWindows.Text = "窗口管理（&W)";
            // 
            // TsmHorizon
            // 
            this.TsmHorizon.Name = "TsmHorizon";
            this.TsmHorizon.Size = new System.Drawing.Size(304, 48);
            this.TsmHorizon.Text = "水平平铺(&H)";
            this.TsmHorizon.Click += new System.EventHandler(this.TsmHorizon_Click);
            // 
            // TsmVertical
            // 
            this.TsmVertical.Name = "TsmVertical";
            this.TsmVertical.Size = new System.Drawing.Size(304, 48);
            this.TsmVertical.Text = "垂直平铺(&V)";
            this.TsmVertical.Click += new System.EventHandler(this.TsmVertical_Click);
            // 
            // TsmLayout
            // 
            this.TsmLayout.Name = "TsmLayout";
            this.TsmLayout.Size = new System.Drawing.Size(304, 48);
            this.TsmLayout.Text = "层    叠(&L）";
            this.TsmLayout.Click += new System.EventHandler(this.TsmLayout_Click);
            // 
            // 关于ToolStripMenuItem1
            // 
            this.关于ToolStripMenuItem1.Name = "关于ToolStripMenuItem1";
            this.关于ToolStripMenuItem1.Size = new System.Drawing.Size(107, 42);
            this.关于ToolStripMenuItem1.Text = "关于...";
            this.关于ToolStripMenuItem1.Visible = false;
            // 
            // TsmPower
            // 
            this.TsmPower.AutoSize = false;
            this.TsmPower.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Image;
            this.TsmPower.Image = global::MTEmbTest.Properties.Resources.P5;
            this.TsmPower.Name = "TsmPower";
            this.TsmPower.Size = new System.Drawing.Size(52, 35);
            this.TsmPower.Text = "1-OFF";
            this.TsmPower.Click += new System.EventHandler(this.TsmPower_Click);
            // 
            // Main_Frm
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(13F, 26F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(243)))), ((int)(((byte)(249)))), ((int)(((byte)(255)))));
            this.ClientSize = new System.Drawing.Size(2308, 1553);
            this.Controls.Add(this.menuStripMain);
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.IsMdiContainer = true;
            this.MainMenuStrip = this.menuStripMain;
            this.Name = "Main_Frm";
            this.Text = "MT 扭转疲劳测试 V1.0.0.0";
            this.WindowState = System.Windows.Forms.FormWindowState.Maximized;
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.Main_Frm_FormClosing);
            this.Load += new System.EventHandler(this.Main_Frm_Load);
            this.menuStripMain.ResumeLayout(false);
            this.menuStripMain.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.MenuStrip menuStripMain;
        private System.Windows.Forms.ToolStripMenuItem TsmDAQ;
        private System.Windows.Forms.ToolStripMenuItem TsmPlayBack;
        private System.Windows.Forms.ToolStripMenuItem TsmWindows;
        private System.Windows.Forms.ToolStripMenuItem TsmHorizon;
        private System.Windows.Forms.ToolStripMenuItem TsmVertical;
        private System.Windows.Forms.ToolStripMenuItem TsmLayout;
        private System.Windows.Forms.ToolStripMenuItem 关于ToolStripMenuItem1;
        private System.Windows.Forms.ToolStripMenuItem TsmSetting;
        private System.Windows.Forms.ToolStripMenuItem TsmDAQCalibrate;
        private System.Windows.Forms.ToolStripMenuItem TsmCharacterPlayBack;
        private System.Windows.Forms.ToolStripMenuItem TsmRawPlayBack;
        private System.Windows.Forms.ToolStripMenuItem TsmRealMinitor;
        private System.Windows.Forms.ToolStripMenuItem TsmAdjustTorque;
        private System.Windows.Forms.ToolStripMenuItem TsmPower;
    }
}

