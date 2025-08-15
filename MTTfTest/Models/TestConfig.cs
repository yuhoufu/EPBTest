using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Xml.Serialization;

namespace MTEmbTest.Models
{
    #region Models

    [XmlRoot("TestConfig")]
    public class TestConfig
    {
        // 原有字段
        public double TestCycle { get; set; }
        public string TestStandard { get; set; }
        public string TestName { get; set; }
        public int TestTarget { get; set; }
        public string StoreDir { get; set; }
        public string TestMan { get; set; }
        public string Description { get; set; }
        public int AlertLimit { get; set; }
        public string TestEnvir { get; set; }
        public int TestSpan { get; set; }

        // 新增：试验参数
        public TestParams TestParams { get; set; } = new TestParams();
    }

    public class TestParams
    {
        // 12 个 EPB 的正/反向电流限制
        [XmlArray("EPBCurrentLimits")]
        [XmlArrayItem("EPB")]
        public List<EPBCurrentLimit> EPBCurrentLimits { get; set; } = new List<EPBCurrentLimit>();

        // 2 个压力限制
        [XmlArray("PressureLimits")]
        [XmlArrayItem("Pressure")]
        public List<PressureLimit> PressureLimits { get; set; } = new List<PressureLimit>();

        // 2 个气缸百分比能力
        [XmlArray("CylinderCapacity")]
        [XmlArrayItem("Cylinder")]
        public List<CylinderCapacity> CylinderCapacity { get; set; } = new List<CylinderCapacity>();
    }

    public class EPBCurrentLimit
    {
        [XmlAttribute] public int ID { get; set; }
        [XmlAttribute] public double ForwardLimit { get; set; }
        [XmlAttribute] public double ReverseLimit { get; set; }
    }

    public class PressureLimit
    {
        [XmlAttribute] public int ID { get; set; }
        [XmlAttribute] public double Limit { get; set; }
    }

    public class CylinderCapacity
    {
        [XmlAttribute] public int ID { get; set; }
        [XmlAttribute] public int Percent { get; set; } // 0~100
    }

    #endregion

    #region Serializer helpers

    public static class TestConfigIO
    {
        private static readonly XmlSerializer _serializer = new XmlSerializer(typeof(TestConfig));

        public static TestConfig Load(string path)
        {
            using (var fs = File.OpenRead(path))
            {
                return (TestConfig)_serializer.Deserialize(fs);
            }
        }

        public static void Save(string path, TestConfig cfg, bool omitXmlNs = true)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            using (var fs = File.Create(path))
            {
                if (omitXmlNs)
                {
                    var ns = new XmlSerializerNamespaces();
                    ns.Add("", ""); // 去掉默认命名空间，生成更干净的 XML
                    _serializer.Serialize(fs, cfg, ns);
                }
                else
                {
                    _serializer.Serialize(fs, cfg);
                }
            }
        }
    }

    #endregion

    #region Initialization & Validation (optional but handy)

    public static class TestConfigFactory
    {
        /// <summary>
        /// 用你给的基础信息 + 默认参数构建一个完整配置
        /// </summary>
        public static TestConfig CreateDefault()
        {
            var cfg = new TestConfig
            {
                TestCycle = 0.2,
                TestStandard = "QC/T XXX-XXXX",
                TestName = "MTTest",
                TestTarget = 500000,
                StoreDir = @"D:\EPBTEST",
                TestMan = "yu",
                Description = "测试使用",
                AlertLimit = 32000,
                TestEnvir = "磨合",
                TestSpan = 5,
            };

            // 12 路 EPB：示例默认值（按需改）
            for (int i = 1; i <= 12; i++)
            {
                cfg.TestParams.EPBCurrentLimits.Add(new EPBCurrentLimit
                {
                    ID = i,
                    ForwardLimit = 10.0,
                    ReverseLimit = 8.0
                });
            }

            // 2 个压力限制：示例默认值（按需改）
            cfg.TestParams.PressureLimits.Add(new PressureLimit { ID = 1, Limit = 250.0 });
            cfg.TestParams.PressureLimits.Add(new PressureLimit { ID = 2, Limit = 200.0 });

            // 2 个气缸能力：示例默认值（按需改）
            cfg.TestParams.CylinderCapacity.Add(new CylinderCapacity { ID = 1, Percent = 80 });
            cfg.TestParams.CylinderCapacity.Add(new CylinderCapacity { ID = 2, Percent = 90 });

            return cfg;
        }
    }

    public static class TestConfigValidator
    {
        public static void Validate(TestConfig cfg)
        {
            if (cfg.TestParams == null) throw new InvalidOperationException("TestParams 不能为空。");

            var epb = cfg.TestParams.EPBCurrentLimits;
            if (epb.Count != 12) throw new InvalidOperationException($"EPBCurrentLimits 需要 12 条，当前 {epb.Count} 条。");
            if (epb.Select(x => x.ID).Distinct().Count() != 12)
                throw new InvalidOperationException("EPBCurrentLimits 中存在重复 ID。");
            if (epb.Any(x => x.ForwardLimit < 0 || x.ReverseLimit < 0))
                throw new InvalidOperationException("EPB 电流限制不能为负。");

            var pres = cfg.TestParams.PressureLimits;
            if (pres.Count != 2) throw new InvalidOperationException($"PressureLimits 需要 2 条，当前 {pres.Count} 条。");
            if (pres.Any(x => x.Limit < 0)) throw new InvalidOperationException("压力限制不能为负。");

            var cyl = cfg.TestParams.CylinderCapacity;
            if (cyl.Count != 2) throw new InvalidOperationException($"CylinderCapacity 需要 2 条，当前 {cyl.Count} 条。");
            if (cyl.Any(x => x.Percent < 0 || x.Percent > 100))
                throw new InvalidOperationException("气缸百分比能力应在 0~100 区间。");
        }
    }

    #endregion

    #region Usage examples

    public static class Examples
    {
        public static void CreateAndSave()
        {
            var cfg = TestConfigFactory.CreateDefault();

            // 快速修改某一路 EPB 的限制
            SetEpbCurrentLimit(cfg, epbId: 3, forward: 11.5, reverse: 9.0);

            // 保存
            TestConfigIO.Save(@"D:\EPBTEST\TestConfig.xml", cfg);
        }

        public static void LoadAndUse()
        {
            var cfg = TestConfigIO.Load(@"D:\EPBTEST\TestConfig.xml");
            TestConfigValidator.Validate(cfg);

            // 获取压力 1 的上限
            double p1Limit = cfg.TestParams.PressureLimits.First(p => p.ID == 1).Limit;

            // 在程序里使用……
            Console.WriteLine($"Pressure#1 Limit = {p1Limit}");
        }

        public static void SetEpbCurrentLimit(TestConfig cfg, int epbId, double forward, double reverse)
        {
            var item = cfg.TestParams.EPBCurrentLimits.FirstOrDefault(x => x.ID == epbId);
            if (item == null) throw new KeyNotFoundException($"未找到 EPB ID={epbId}");
            item.ForwardLimit = forward;
            item.ReverseLimit = reverse;
        }
    }

    #endregion
}