using Sunny.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MtEmbTest
{

    [Serializable]
    public class ClsEMBControler
    {
        public int  EmbNo;
        public string EmbName;
        //public int Cycles;
        public bool IsEnabel;
        //public int CanChannel;
        public UICheckBox CtrlJoinTest;
        public UIRadioButton CtrlCurrentEmb;
    
        public UILight CtrlAlert;
        public UILabel CtrlCycles;
    }
}
