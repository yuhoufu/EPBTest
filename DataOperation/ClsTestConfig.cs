using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DataOperation
{
    public class TestConfig
    {
        public string TestCycle { get; set; }
        public string TestStandard { get; set; }
        public string TestName { get; set; }
        public string TestTarget { get; set; }
        public string StoreDir { get; set; }
        public string TestMan { get; set; }
        public string Description { get; set; }
        public string AlertLimit { get; set; }
        public string TestEnvir { get; set; }

        public double TestSpan { get; set; }
    }
}
