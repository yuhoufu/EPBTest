using DataOperation;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace DataOperation
{
   public class ClsXmlOperation
    {

        public static string UpdateZeroDriftInXml(string xmlFilePath,
                                    ConcurrentDictionary<string, double> paraNameToZeroValue)
        {
            try
            {
                // 参数校验
                if (string.IsNullOrWhiteSpace(xmlFilePath))
                    throw new ArgumentException("XML文件路径不能为空", nameof(xmlFilePath));

                if (paraNameToZeroValue == null)
                    throw new ArgumentNullException(nameof(paraNameToZeroValue));

                // 加载XML文档
                XDocument doc = XDocument.Load(xmlFilePath);

                // 遍历所有Records节点
                foreach (var record in doc.Descendants("Records"))
                {
                    var paramNameElement = record.Element("参数名");
                    if (paramNameElement == null) continue;

                    string paramName = paramNameElement.Value.Trim();

                    // 查找匹配的零漂值
                    if (paraNameToZeroValue.TryGetValue(paramName, out double zeroValue))
                    {
                        var zeroDriftElement = record.Element("零位漂移");
                        if (zeroDriftElement != null)
                        {
                            // 更新现有节点值
                            zeroDriftElement.Value = zeroValue.ToString("F4"); // 保留1位小数
                        }
                        else
                        {
                            // 创建新节点（如果需要）
                            record.Add(new XElement("零位漂移", zeroValue.ToString("F4")));
                        }
                    }
                }

                // 保存修改（保留原始格式）
                var settings = new XmlWriterSettings
                {
                    Indent = true,
                    Encoding = Encoding.UTF8,
                    OmitXmlDeclaration = doc.Declaration == null  // 保持与原始文件一致
                };

                using (var writer = XmlWriter.Create(xmlFilePath, settings))
                {
                    doc.Save(writer);
                }
                return "OK";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
        
        public static string  ReadCanNameChannelToDictionary(string filePath,out ConcurrentDictionary<string,int> NameToChannnel)
        {
            NameToChannnel = new ConcurrentDictionary<string, int>();
      

            try
            {
                XmlDocument xmlDoc = new XmlDocument();
                xmlDoc.Load(filePath);

                XmlNodeList channelNodes = xmlDoc.SelectNodes("//Channel");
                foreach (XmlNode channelNode in channelNodes)
                {
                   
                    XmlNode controlTargetNode = channelNode.SelectSingleNode("ControlTarget");
                    XmlNode channelNoNode = channelNode.SelectSingleNode("ChannelNo");

                    if (channelNoNode != null && controlTargetNode != null)
                    {
                        int channelNo = int.Parse(channelNoNode.InnerText);
                        string controlTarget = controlTargetNode.InnerText.Trim();
                    

                        if (!string.IsNullOrEmpty(controlTarget))
                        {
                             NameToChannnel[controlTarget] = channelNo;
                        }
                    }
                }
            }
            catch (FileNotFoundException)
            {
                return filePath+" 文件未找到，请检查文件路径！";
            }
            catch (XmlException)
            {
                return filePath + " XML 文件格式有误！";
            }
            catch (Exception ex)
            {
                return filePath + "发生未知错误: "+ex.Message;
            }
         
            return "OK";
        }

        public static string ReadPhysicalChannelScaleToDictionary(string filePath, out ConcurrentDictionary<string, double> PhysicalChannelToSlope)
        {
            PhysicalChannelToSlope = new ConcurrentDictionary<string, double>();

            try
            {
                XmlDocument xmlDoc = new XmlDocument();
                xmlDoc.Load(filePath);

                XmlNodeList recordsNodes = xmlDoc.SelectNodes("//Records");
                foreach (XmlNode recordsNode in recordsNodes)
                {
                    XmlNode enabledNode = recordsNode.SelectSingleNode("是否启用");
                    if (enabledNode != null && enabledNode.InnerText.Trim() == "1")
                    {
                        XmlNode physicalChannelNode = recordsNode.SelectSingleNode("物理通道");
                        XmlNode slopeNode = recordsNode.SelectSingleNode("变换斜率");

                        if (physicalChannelNode != null && slopeNode != null)
                        {
                            string physicalChannel = physicalChannelNode.InnerText.Trim();
                            double slope = double.Parse(slopeNode.InnerText.Trim());

                            PhysicalChannelToSlope[physicalChannel] = slope;
                        }
                    }
                }
            }
            catch (FileNotFoundException)
            {
                return filePath + " 文件未找到，请检查文件路径！";
            }
            catch (XmlException)
            {
                return filePath + " XML 文件格式有误！";
            }
            catch (Exception ex)
            {
                return filePath + " 发生未知错误: " + ex.Message;
            }

            return "OK";
        }

        public static string GetDaqAIUsedChannels(string filePath, string DevName,out string[] Channels)
        {
            Channels = null;
            try
            {
                List<string> ChannelList = new List<string>();


                XmlDocument xmlDoc = new XmlDocument();
                xmlDoc.Load(filePath);

                XmlNodeList recordsNodes = xmlDoc.SelectNodes("//Records");
                foreach (XmlNode recordsNode in recordsNodes)
                {
                    XmlNode enabledNode = recordsNode.SelectSingleNode("是否启用");
                    if (enabledNode != null && enabledNode.InnerText.Trim() == "1")
                    {
                        XmlNode physicalChannelNode = recordsNode.SelectSingleNode("物理通道");
                        if (physicalChannelNode != null)
                        {
                            string physicalChannel = physicalChannelNode.InnerText.Trim();

                            if (physicalChannel.Contains(DevName))
                            {
                                ChannelList.Add(physicalChannel);
                            }
                        }
                    }
                }

                ChannelList.Sort();          //按照升序排列
                Channels = ChannelList.ToArray();
            }
            catch (FileNotFoundException)
            {
                return filePath + " 文件未找到，请检查文件路径！";
            }
            catch (XmlException)
            {
                return filePath + " XML 文件格式有误！";
            }
            catch (Exception ex)
            {
                return filePath + " 发生未知错误: " + ex.Message;
            }

            return "OK";
        }
        
        public static string GetDaqAIChannelMapping(string filePath, string DevName, string[] UsedChannel,out ConcurrentDictionary<string, int> channelMapping)
        {
            channelMapping = new ConcurrentDictionary<string, int>();
            try
            {
                // List<(string PhysicalChannel, string ParamName)> recordList = new List<(string PhysicalChannel, string ParamName)>();

                ConcurrentDictionary<string, string> recordList = new ConcurrentDictionary<string, string>();


                XmlDocument xmlDoc = new XmlDocument();
                xmlDoc.Load(filePath);

                XmlNodeList recordsNodes = xmlDoc.SelectNodes("//Records");
                foreach (XmlNode recordsNode in recordsNodes)
                {
                    XmlNode enabledNode = recordsNode.SelectSingleNode("是否启用");
                    if (enabledNode != null && enabledNode.InnerText.Trim() == "1")
                    {
                        XmlNode physicalChannelNode = recordsNode.SelectSingleNode("物理通道");
                        XmlNode paramNameNode = recordsNode.SelectSingleNode("参数名");
                        XmlNode paramTypeNode = recordsNode.SelectSingleNode("参数类型");
                        if (physicalChannelNode != null && paramNameNode != null && paramTypeNode != null)
                        {
                            string physicalChannel = physicalChannelNode.InnerText.Trim();
                            string paramName = paramNameNode.InnerText.Trim();
                            string paramType = paramTypeNode.InnerText.Trim();

                            // 增加参数类型为电流的限制
                            if (physicalChannel.Contains(DevName) && paramType == "电流")
                            {
                                recordList.TryAdd(physicalChannel, paramName);
                            }
                        }
                    }
                }

                // 按照物理通道升序排序
                // recordList.Sort((x, y) => x.PhysicalChannel.CompareTo(y.PhysicalChannel));

                //for (int i = 0; i < recordList.Count; i++)
                //{
                //    string paramName = recordList[i].ParamName;
                //    // 从参数名中提取 EMB+序号
                //    string pattern = @"EMB\d+";
                //    Match match = Regex.Match(paramName, pattern);
                //    if (match.Success)
                //    {
                //        string embKey = match.Value;
                //        if (!channelMapping.ContainsKey(embKey))
                //        {
                //            channelMapping[embKey] = i;
                //        }
                //    }
                //}

                for (int i = 0; i < UsedChannel.Length; i++)
                {
                    string ParaName = recordList[UsedChannel[i]].Replace("_current","");
                    channelMapping[ParaName] = i;
                }



            }
            catch (FileNotFoundException)
            {
                return filePath + " 文件未找到，请检查文件路径！";
            }
            catch (XmlException)
            {
                return filePath + " XML 文件格式有误！";
            }
            catch (Exception ex)
            {
                return filePath + " 发生未知错误: " + ex.Message;
            }

            return "OK";
        }
        
        public static string GetDaqScaleMapping(string filePath, string DevName, out ConcurrentDictionary<string, double> scaleMapping)
        {
            scaleMapping = new ConcurrentDictionary<string, double>();
            try
            {
                XDocument doc = XDocument.Load(filePath);
                var enabledRecords = doc.Descendants("Records")

                      .Where(r =>
                    (string)r.Element("是否启用") == "1" &&          // 是否启用为1
                   ((string)r.Element("物理通道") ?? "").Contains(DevName)) // 物理通道包含Dev1
                    .Select(r => new
                    {
                        Param = (string)r.Element("参数名"),
                        Slope = (string)r.Element("变换斜率")
                    });

                foreach (var record in enabledRecords)
                {
                    if (double.TryParse(record.Slope, out double slope))
                    {
                        scaleMapping.TryAdd(record.Param.Replace("_current",""), slope);
                    }
                    else
                    {
                        return $"无效斜率值: {record.Param.Replace("_current", "")}={record.Slope}";
                    }
                }
                return "OK";
            }
            catch (Exception ex)
            {
                return $"读取斜率配置失败: {ex.Message}";
            }
        }

        public static string GetDaqOffsetMapping(string filePath, string DevName, out ConcurrentDictionary<string, double> offsetMapping)
        {
            offsetMapping = new ConcurrentDictionary<string, double>();
            try
            {
                XDocument doc = XDocument.Load(filePath);
                var enabledRecords = doc.Descendants("Records")
                      .Where(r =>
                    (string)r.Element("是否启用") == "1" &&          // 是否启用为1
                   ((string)r.Element("物理通道") ?? "").Contains(DevName)) // 物理通道包含Dev1
                    .Select(r => new
                    {
                        Param = (string)r.Element("参数名"),
                        Offset = (string)r.Element("变换截距")
                    });

                foreach (var record in enabledRecords)
                {
                    if (double.TryParse(record.Offset, out double offset))
                    {
                        offsetMapping.TryAdd(record.Param.Replace("_current", ""), offset);
                    }
                    else
                    {
                        return $"无效截距值: {record.Param.Replace("_current", "")}={record.Offset}";
                    }
                }
                return "OK";
            }
            catch (Exception ex)
            {
                return $"读取截距配置失败: {ex.Message}";
            }
        }

        public static string GetDaqZeroValueMapping(string filePath, string DevName, out ConcurrentDictionary<string, double> zeroValueMapping)
        {
            zeroValueMapping = new ConcurrentDictionary<string, double>();
            try
            {
                XDocument doc = XDocument.Load(filePath);
                var enabledRecords = doc.Descendants("Records")
                      .Where(r =>
                    (string)r.Element("是否启用") == "1" &&          // 是否启用为1
                   ((string)r.Element("物理通道") ?? "").Contains(DevName)) // 物理通道包含Dev1
                    .Select(r => new
                    {
                        Param = (string)r.Element("参数名"),
                        Offset = (string)r.Element("零位漂移")
                    });

                foreach (var record in enabledRecords)
                {
                    if (double.TryParse(record.Offset, out double offset))
                    {
                        zeroValueMapping.TryAdd(record.Param.Replace("_current", ""), offset);
                    }
                    else
                    {
                        return $"无效零位漂移值: {record.Param.Replace("_current", "")}={record.Offset}";
                    }
                }
                return "OK";
            }
            catch (Exception ex)
            {
                return $"读取零位漂移配置失败: {ex.Message}";
            }
        }
        
        public static string GetDaqPhyChanelToNameMapping(string filePath, string DevName, out ConcurrentDictionary<string, string> phyChannelNameMapping)
        {
            phyChannelNameMapping = new ConcurrentDictionary<string, string>();
            try
            {
                XDocument doc = XDocument.Load(filePath);
                var enabledRecords = doc.Descendants("Records")
                    .Where(r =>
                    (string)r.Element("是否启用") == "1" &&          // 是否启用为1
                   ((string)r.Element("物理通道") ?? "").Contains(DevName)) // 物理通道包含Dev1
                    .Select(r => new
                    {
                        Param = (string)r.Element("参数名"),
                        PhyChannel = (string)r.Element("物理通道")
                    });

                foreach (var record in enabledRecords)
                {
                    phyChannelNameMapping.TryAdd(record.PhyChannel, record.Param);
                }
                return "OK";
            }
            catch (Exception ex)
            {
                return $"参数通道和名称失败: {ex.Message}";
            }
        }
        
    }
}
