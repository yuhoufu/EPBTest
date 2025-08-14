using Sunny.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DevExpress.XtraEditors;

namespace MtEmbTest
{

    [Serializable]
    public class ClsEMBControler
    {
        public int EmbNo;
        public string EmbName;
        //public int Cycles;
        public bool IsEnabel;
        //public int CanChannel;

        //public UICheckBox CtrlJoinTest; // 之前是UICheckBox，现在是CheckEdit
        public CheckEdit CtrlJoinTest; 
        
        // public UIRadioButton CtrlCurrentEmb; // 界面没有，暂时注释
        public UISwitch CtrlRunning;
        public UISwitch CtrlPower;


        // public UILight CtrlAlert; // 界面没有，暂时注释
        public UILabel CtrlCycles;
    }
}
