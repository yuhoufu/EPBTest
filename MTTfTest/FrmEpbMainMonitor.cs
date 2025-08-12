using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MTEmbTest
{
    public partial class FrmEpbMainMonitor : Form
    {
        public FrmEpbMainMonitor()
        {
            InitializeComponent();

            // 创建自定义标题栏
            Panel titleBar = new Panel
            {
                Height = 30,
                Dock = DockStyle.Top,
                BackColor = Color.AliceBlue
            };

            // 添加自定义按钮
            Button btnClose = new Button
            {
                Text = @"×",
                Size = new Size(50, 50),
                Dock = DockStyle.Right
            };
            btnClose.Click += (s, e) => this.Close();

            titleBar.Controls.Add(btnClose);
            this.Controls.Add(titleBar);

            // 添加拖拽功能
            titleBar.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    //ReleaseCapture();
                    //SendMessage(Handle, 0xA1, 0x2, 0);
                }
            };
        }

        private void uiButton1_Click(object sender, EventArgs e)
        {

        }

        private void uiGroupBox6_Click(object sender, EventArgs e)
        {

        }

        private void LabEmb1_Click(object sender, EventArgs e)
        {

        }

        private void uiRadioButton3_CheckedChanged(object sender, EventArgs e)
        {

        }
    }
}
