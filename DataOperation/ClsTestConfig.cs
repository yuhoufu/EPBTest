using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;

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

    public class TestConfigOperation
    {
        public static string ParseTestConfig(string filename, out TestConfig config)
        {
            config = null;

            try
            {
                // 创建XML文档对象
                XmlDocument doc = new XmlDocument();
                doc.Load(filename);

                // 获取根节点
                XmlNode root = doc.DocumentElement;
                if (root == null || root.Name != "TestConfig")
                {
                    return "错误：无效的XML根节点";
                }

                // 创建配置对象
                config = new TestConfig();

                // 遍历所有子节点并赋值
                foreach (XmlNode node in root.ChildNodes)
                {
                    if (node.NodeType != XmlNodeType.Element) continue;

                    string tag = node.Name;
                    string value = node.InnerText.Trim();

                    switch (tag)
                    {
                        case "TestCycle":
                          
                                config.TestCycle = value;
                            break;
                        case "TestStandard":
                            config.TestStandard = value;
                            break;
                        case "TestName":
                            config.TestName = value;
                            break;
                        case "TestTarget":
                        
                                config.TestTarget = value;
                            break;
                        case "StoreDir":
                            config.StoreDir = value;
                            break;
                        case "TestMan":
                            config.TestMan = value;
                            break;
                        case "Description":
                            config.Description = value;
                            break;
                        case "AlertLimit":
                          
                                config.AlertLimit = value;
                            break;
                        case "TestEnvir":
                            config.TestEnvir = value;
                            break;
                        case "TestSpan":
                            if (double.TryParse(value, out double testSpan))
                                config.TestSpan = testSpan;
                            break;
                    }
                }

                return "OK";
            }
            catch (Exception ex)
            {
                return $"错误：{ex.Message}";
            }
        }

        public static string SaveTestConfigToFile(string xmlPath,TestConfig config)
        {
            try
            {
                var serializer = new XmlSerializer(typeof(TestConfig));

              

                using (var writer = new StreamWriter(xmlPath))
                {
                    serializer.Serialize(writer, config);
                }
                return "OK";
            }
            catch(Exception ex)
            {
                return ex.Message;
            }
        }




    }


}
