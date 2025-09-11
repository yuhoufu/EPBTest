using DataOperation;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
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

        

        #region 对外映射方法（保持你的原方法名，新增可选参数类型过滤）

        /// <summary>
        /// 将“规范化后的参数键（如 EPB1、Pressure_1、Force）”映射到 UsedChannel 数组中的索引（0..N-1）。
        /// - UsedChannel 通常为本设备实际使用的“物理通道列表”（如 "Dev1/ai0","Dev1/ai1",...），其顺序定义索引。
        /// - 本方法不再只限“电流”；默认读取所有类型，也可通过 paramTypeFilter 指定类型子集。
        /// - 对“电流”类型自动去掉 "_current" 后缀以兼容旧逻辑。
        /// </summary>
        /// <param name="filePath">AIConfigDetail.xml 路径</param>
        /// <param name="devName">设备名前缀（如 "Dev1"、"Dev2"）</param>
        /// <param name="usedChannel">本设备采集任务使用到的物理通道（完全限定名），顺序决定索引</param>
        /// <param name="channelMapping">输出：参数键 -> usedChannel 中的索引</param>
        /// <param name="paramTypeFilter">可选：限制参数类型（如 "电流"、"管路压力"、"夹紧力"...）</param>
        /// <returns>OK 或错误信息</returns>
        public static string GetDaqAIChannelMapping(
            string filePath,
            string devName,
            string[] usedChannel,
            out ConcurrentDictionary<string, int> channelMapping,
            params string[] paramTypeFilter)
        {
            channelMapping = new ConcurrentDictionary<string, int>();
            try
            {
                var records = ReadRecords(filePath, devName, paramTypeFilter);

                // 用“物理通道”建立映射，便于按 usedChannel 定位
                var byPhysical = records
                    .GroupBy(r => r.Physical, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                var missing = new List<string>();
                for (int i = 0; i < usedChannel.Length; i++)
                {
                    var phy = usedChannel[i]?.Trim() ?? string.Empty;
                    if (string.IsNullOrEmpty(phy)) continue;

                    if (!byPhysical.TryGetValue(phy, out var rec))
                    {
                        // 未在配置中找到该物理通道
                        missing.Add(phy);
                        continue;
                    }

                    var key = rec.NormalizedKey;
                    if (!channelMapping.TryAdd(key, i))
                    {
                        return $"参数键重复：{key}。请检查配置中参数名/类型是否冲突。";
                    }
                }

                if (missing.Count > 0)
                    return $"以下物理通道未在配置（或未启用/被过滤）中找到：{string.Join(", ", missing)}";

                return "OK";
            }
            catch (FileNotFoundException)
            {
                return $"{filePath} 文件未找到，请检查文件路径！";
            }
            catch (XmlException)
            {
                return $"{filePath} XML 文件格式有误！";
            }
            catch (Exception ex)
            {
                return $"{filePath} 发生未知错误: {ex.Message}";
            }
        }

        /// <summary>
        /// 读取“规范化参数键 -> 变换斜率”映射。
        /// 默认包含所有类型；可通过 paramTypeFilter 过滤到特定类型集合。
        /// </summary>
        public static string GetDaqScaleMapping(
            string filePath,
            string devName,
            out ConcurrentDictionary<string, double> scaleMapping,
            params string[] paramTypeFilter)
        {
            scaleMapping = new ConcurrentDictionary<string, double>();
            try
            {
                var records = ReadRecords(filePath, devName, paramTypeFilter);

                foreach (var r in records)
                {
                    var key = r.NormalizedKey;
                    if (!scaleMapping.TryAdd(key, r.Slope))
                    {
                        return $"重复的参数键（斜率）：{key}，请检查配置。";
                    }
                }

                return "OK";
            }
            catch (Exception ex)
            {
                return $"读取斜率配置失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 读取“规范化参数键 -> 变换截距（Offset）”映射。
        /// 默认包含所有类型；可通过 paramTypeFilter 过滤到特定类型集合。
        /// </summary>
        public static string GetDaqOffsetMapping(
            string filePath,
            string devName,
            out ConcurrentDictionary<string, double> offsetMapping,
            params string[] paramTypeFilter)
        {
            offsetMapping = new ConcurrentDictionary<string, double>();
            try
            {
                var records = ReadRecords(filePath, devName, paramTypeFilter);

                foreach (var r in records)
                {
                    var key = r.NormalizedKey;
                    if (!offsetMapping.TryAdd(key, r.Offset))
                    {
                        return $"重复的参数键（截距）：{key}，请检查配置。";
                    }
                }

                return "OK";
            }
            catch (Exception ex)
            {
                return $"读取截距配置失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 读取“规范化参数键 -> 零位漂移（ZeroDrift）”映射。
        /// 默认包含所有类型；可通过 paramTypeFilter 过滤到特定类型集合。
        /// </summary>
        public static string GetDaqZeroValueMapping(
            string filePath,
            string devName,
            out ConcurrentDictionary<string, double> zeroValueMapping,
            params string[] paramTypeFilter)
        {
            zeroValueMapping = new ConcurrentDictionary<string, double>();
            try
            {
                var records = ReadRecords(filePath, devName, paramTypeFilter);

                foreach (var r in records)
                {
                    var key = r.NormalizedKey;
                    if (!zeroValueMapping.TryAdd(key, r.ZeroDrift))
                    {
                        return $"重复的参数键（零位漂移）：{key}，请检查配置。";
                    }
                }

                return "OK";
            }
            catch (Exception ex)
            {
                return $"读取零位漂移配置失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 读取“物理通道 -> 参数名（原名，不去后缀）”映射。
        /// 若希望获取“规范化后的参数键”，请使用 GetDaqPhyChToNormalizedKeyMapping。
        /// </summary>
        public static string GetDaqPhyChanelToNameMapping(
            string filePath,
            string devName,
            out ConcurrentDictionary<string, string> phyChannelNameMapping,
            params string[] paramTypeFilter)
        {
            phyChannelNameMapping = new ConcurrentDictionary<string, string>();
            try
            {
                var records = ReadRecords(filePath, devName, paramTypeFilter);

                foreach (var r in records)
                {
                    if (!phyChannelNameMapping.TryAdd(r.Physical, r.ParamName))
                    {
                        return $"重复的物理通道：{r.Physical}，请检查配置。";
                    }
                }

                return "OK";
            }
            catch (Exception ex)
            {
                return $"参数通道和名称失败: {ex.Message}";
            }
        }

        /// <summary>
        /// （可选辅助）读取“物理通道 -> 规范化参数键”映射。
        /// </summary>
        public static string GetDaqPhyChToNormalizedKeyMapping(
            string filePath,
            string devName,
            out ConcurrentDictionary<string, string> phyToKeyMapping,
            params string[] paramTypeFilter)
        {
            phyToKeyMapping = new ConcurrentDictionary<string, string>();
            try
            {
                var records = ReadRecords(filePath, devName, paramTypeFilter);

                foreach (var r in records)
                {
                    if (!phyToKeyMapping.TryAdd(r.Physical, r.NormalizedKey))
                    {
                        return $"重复的物理通道：{r.Physical}，请检查配置。";
                    }
                }

                return "OK";
            }
            catch (Exception ex)
            {
                return $"物理通道到规范化键映射失败: {ex.Message}";
            }
        }

        #endregion




        #region 内部模型与工具

        /// <summary>
        /// 单条 <Records> 的强类型表示。
        /// </summary>
        private sealed class AiRecord
        {
            public int Index { get; set; }                 // 序号
            public string Physical { get; set; }           // 物理通道，如 Dev1/ai0
            public string ParamName { get; set; }          // 参数名，如 EPB1_current / Pressure_1 / Force
            public string Unit { get; set; }               // 单位，如 A/Bar/N
            public double Slope { get; set; }              // 变换斜率
            public double Offset { get; set; }             // 变换截距
            public string ParamType { get; set; }          // 参数类型，如 电流/管路压力/夹紧力
            public bool Enabled { get; set; }              // 是否启用
            public double ZeroDrift { get; set; }          // 零位漂移

            /// <summary>
            /// 规范化后的“参数键”。对“电流”类型去掉 _current 后缀，其它类型保持原样。
            /// </summary>
            public string NormalizedKey
            {
                get
                {
                    if (string.Equals(ParamType, "电流", StringComparison.OrdinalIgnoreCase) &&
                        ParamName != null &&
                        ParamName.EndsWith("_current", StringComparison.OrdinalIgnoreCase))
                    {
                        return ParamName.Substring(0, ParamName.Length - "_current".Length);
                    }
                    return ParamName ?? string.Empty;
                }
            }
        }

        /// <summary>
        /// 尝试将字符串解析为 double；先用 InvariantCulture，再用 zh-CN 兜底。
        /// </summary>
        private static bool TryParseDouble(string text, out double value)
        {
            var styles = NumberStyles.Float | NumberStyles.AllowThousands | NumberStyles.AllowLeadingSign;
            if (double.TryParse(text, styles, CultureInfo.InvariantCulture, out value))
                return true;

            var zh = CultureInfo.GetCultureInfo("zh-CN");
            return double.TryParse(text, styles, zh, out value);
        }

        /// <summary>
        /// 从 XML 文件读取全部 <Records> 项，并可按设备名与参数类型进行过滤。
        /// </summary>
        /// <param name="filePath">AIConfigDetail.xml 路径</param>
        /// <param name="devName">设备名前缀，如 "Dev1"、"Dev2"</param>
        /// <param name="paramTypeFilter">
        /// 可选的参数类型过滤（大小写不敏感）。为空或不传时表示不过滤（读取所有类型）。
        /// 例如：new[]{"电流"}、new[]{"管路压力","夹紧力"}、new[]{"电流","管路压力"}
        /// </param>
        private static List<AiRecord> ReadRecords(string filePath, string devName, params string[] paramTypeFilter)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"配置文件未找到：{filePath}");

            var typeSet = (paramTypeFilter == null || paramTypeFilter.Length == 0)
                ? null
                : new HashSet<string>(paramTypeFilter, StringComparer.OrdinalIgnoreCase);

            var doc = XDocument.Load(filePath);
            var q = from r in doc.Descendants("Records")
                    let enabled = (string)r.Element("是否启用")
                    let phy = ((string)r.Element("物理通道") ?? string.Empty).Trim()
                    let ptype = ((string)r.Element("参数类型") ?? string.Empty).Trim()
                    where enabled == "1" &&
                          // 设备过滤：要求物理通道以 DevName/ 开头，避免 Dev1 匹配到 Dev10
                          phy.StartsWith(devName + "/", StringComparison.OrdinalIgnoreCase) &&
                          (typeSet == null || typeSet.Contains(ptype))
                    select new AiRecord
                    {
                        Index = SafeParseInt((string)r.Element("序号")),
                        Physical = phy,
                        ParamName = ((string)r.Element("参数名") ?? string.Empty).Trim(),
                        Unit = ((string)r.Element("单位") ?? string.Empty).Trim(),
                        Slope = SafeParseDouble((string)r.Element("变换斜率")),
                        Offset = SafeParseDouble((string)r.Element("变换截距")),
                        ParamType = ptype,
                        Enabled = true,
                        ZeroDrift = SafeParseDouble((string)r.Element("零位漂移"))
                    };

            return q.ToList();

            // —— 局部安全解析 —— //
            static int SafeParseInt(string s)
            {
                if (int.TryParse((s ?? string.Empty).Trim(), out var v)) return v;
                return 0;
            }
            static double SafeParseDouble(string s)
            {
                return TryParseDouble((s ?? string.Empty).Trim(), out var v) ? v : 0.0;
            }
        }

        #endregion
    }



}

