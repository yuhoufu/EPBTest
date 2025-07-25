using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace MtEmbTest
{
    public partial class AxisRangeDialog: Form
    {
        public double XMin { get; private set; }
        public double XMax { get; private set; }
        public double YMin { get; private set; }
        public double YMax { get; private set; }

      
        public AxisRangeDialog(double currentXMin, double currentXMax, double currentYMin, double currentYMax)
        {
            InitializeComponent();
            // 初始化显示当前值
            txtXMin.Text = currentXMin.ToString("F2");
            txtXMax.Text = currentXMax.ToString("F2");
          
        }

        private bool ValidateInput()
        {
            double xMin= double.MinValue;
            double yMin = double.MinValue;
            double xMax = double.MaxValue;
            double yMax = double.MaxValue;

            bool valid = double.TryParse(txtXMin.Text, out xMin) &&
                         double.TryParse(txtXMax.Text, out xMax);

            if (!valid)
            {
                MessageBox.Show("请输入有效的数字！", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            if (xMin >= xMax || yMin >= yMax)
            {
                MessageBox.Show("最小值必须小于最大值！", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            XMin = xMin;
            XMax = xMax;
            YMin = yMin;
            YMax = yMax;
            return true;
        }

        private void BtnOK_Click(object sender, EventArgs e)
        {
            if (ValidateInput())
            {
                this.DialogResult = DialogResult.OK;
                this.Close();
            }
        }
    }
}
