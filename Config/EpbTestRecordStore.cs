using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Xml;

namespace Config
{
    /// <summary>
    /// 轻量级静态封装：针对 EpbTestRecord 的序列化/反序列化与简单的文件持久化操作。
    /// 设计原则：简单、可直接通过实例调用、同一进程内线程安全、写入采用临时文件+原子替换。
    /// </summary>
    public static class EpbTestRecordStore
    {
        private static readonly object _fileLock = new object();

        /// <summary>
        /// 默认配置文件路径：{AppBase}\Config\TestConfig.xml
        /// </summary>
        public static string DefaultConfigPath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? Environment.CurrentDirectory, "Config", "TestConfig.xml");

        /// <summary>
        /// 读取指定路径（或默认路径）下的 &lt;EpbRecords&gt; 节点，返回解析出的记录列表。
        /// 若文件不存在或节点缺失，返回空列表（不会抛异常）。
        /// </summary>
        public static List<EpbTestRecord> LoadAll(string xmlPath = null)
        {
            var path = string.IsNullOrWhiteSpace(xmlPath) ? DefaultConfigPath : xmlPath;
            var list = new List<EpbTestRecord>();

            if (!File.Exists(path)) return list;

            try
            {
                var doc = new XmlDocument();
                doc.Load(path);
                var nodes = doc.SelectNodes("/TestConfig/EpbRecords/Record");
                if (nodes == null) return list;

                foreach (XmlNode n in nodes)
                {
                    try
                    {
                        var id = int.TryParse(n.SelectSingleNode("./Id")?.InnerText?.Trim(), out var idv) ? idv : -1;
                        if (id <= 0) continue;

                        DateTime? ParseDt(string s)
                        {
                            if (string.IsNullOrWhiteSpace(s)) return null;
                            if (DateTime.TryParseExact(s.Trim(), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                                return dt;
                            if (DateTime.TryParse(s.Trim(), out dt))
                                return dt;
                            return null;
                        }

                        var rec = new EpbTestRecord
                        {
                            Id = id,
                            StartTime = ParseDt(n.SelectSingleNode("./StartTime")?.InnerText),
                            LatestStartTime = ParseDt(n.SelectSingleNode("./LatestStartTime")?.InnerText),
                            RunTime = n.SelectSingleNode("./RunTime")?.InnerText ?? EpbTestRecord.CreateDefault(id).RunTime,
                            TotalCount = int.TryParse(n.SelectSingleNode("./TotalCount")?.InnerText, out var tc) ? tc : 0,
                            RunCount = int.TryParse(n.SelectSingleNode("./RunCount")?.InnerText, out var rc) ? rc : 0,
                            Status = Enum.TryParse<EpbTestStatus>(n.SelectSingleNode("./Status")?.InnerText, out var st) ? st : EpbTestStatus.NotStarted
                        };
                        list.Add(rec);
                    }
                    catch
                    {
                        // 忽略单条解析错误，继续下一条
                    }
                }
            }
            catch
            {
                // 如果你希望暴露异常可以抛出；目前保持静默并返回已解析的内容
            }

            return list;
        }

        /// <summary>
        /// 将给定记录集合写入到指定路径（或默认路径）的 &lt;EpbRecords&gt; 节点，原有节点将被替换。
        /// 全过程同一进程内锁定，写入采用 tmp 保存再原子替换目标文件。
        /// </summary>
        public static void SaveAll(IEnumerable<EpbTestRecord> records, string xmlPath = null)
        {
            var path = string.IsNullOrWhiteSpace(xmlPath) ? DefaultConfigPath : xmlPath;
            var doc = new XmlDocument();

            lock (_fileLock)
            {
                // 加载现有文件或创建基础结构
                if (File.Exists(path))
                {
                    doc.Load(path);
                }
                else
                {
                    var decl = doc.CreateXmlDeclaration("1.0", "utf-8", null);
                    doc.AppendChild(decl);
                    var root = doc.CreateElement("TestConfig");
                    doc.AppendChild(root);
                }

                var rootNode = doc.DocumentElement ?? doc.AppendChild(doc.CreateElement("TestConfig"));

                // 移除旧的 EpbRecords 节点（如果存在）
                var existing = rootNode.SelectSingleNode("./EpbRecords");
                if (existing != null)
                    rootNode.RemoveChild(existing);

                // 构建新的节点
                var recordsNode = doc.CreateElement("EpbRecords");

                foreach (var r in records)
                {
                    var rec = doc.CreateElement("Record");

                    void addChild(string name, string value)
                    {
                        var n = doc.CreateElement(name);
                        n.AppendChild(doc.CreateTextNode(value ?? ""));
                        rec.AppendChild(n);
                    }

                    addChild("Id", r.Id.ToString(CultureInfo.InvariantCulture));
                    addChild("StartTime", r.StartTime.HasValue ? r.StartTime.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "");
                    addChild("LatestStartTime", r.LatestStartTime.HasValue ? r.LatestStartTime.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "");
                    addChild("RunTime", r.RunTime ?? "");
                    addChild("TotalCount", r.TotalCount.ToString(CultureInfo.InvariantCulture));
                    addChild("RunCount", r.RunCount.ToString(CultureInfo.InvariantCulture));
                    addChild("Status", r.Status.ToString());

                    recordsNode.AppendChild(rec);
                }

                rootNode.AppendChild(recordsNode);

                // 保存到临时文件然后替换（确保目录存在）
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var tmp = path + ".tmp";
                var bak = path + ".bak";

                doc.Save(tmp);

                if (File.Exists(path))
                {
                    // File.Replace 会在同一卷上做替换并生成备份
                    File.Replace(tmp, path, bak, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tmp, path);
                }
            }
        }

        /// <summary>
        /// 把单条记录写入到文件：若文件中存在相同 Id 的记录则覆盖，否则追加。实现上会读取全部、替换/追加、然后 SaveAll。
        /// </summary>
        public static void SaveSingle(EpbTestRecord record, string xmlPath = null)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));

            lock (_fileLock)
            {
                var list = LoadAll(xmlPath);
                var found = list.FindIndex(x => x.Id == record.Id);
                if (found >= 0) list[found] = record;
                else list.Add(record);

                // 可选：保持按 Id 升序
                list.Sort((a, b) => a.Id.CompareTo(b.Id));

                SaveAll(list, xmlPath);
            }
        }

        /// <summary>
        /// 读取单条记录（按 Id），不存在返回 null。
        /// </summary>
        public static EpbTestRecord LoadSingle(int id, string xmlPath = null)
        {
            var list = LoadAll(xmlPath);
            return list.Find(x => x.Id == id);
        }
    }

    /// <summary>
    /// 为了方便，你也可以给 EpbTestRecord 添加简单的实例方法来直接调用上面的静态方法（例如 Save）。
    /// 以下为示例扩展（复制到 EpbTestRecord 类中）：
    /// </summary>
    /*
    public void Save(string xmlPath = null)
    {
        EpbTestRecordStore.SaveSingle(this, xmlPath);
    }

    public static List<EpbTestRecord> LoadAll(string xmlPath = null)
    {
        return EpbTestRecordStore.LoadAll(xmlPath);
    }

    public static EpbTestRecord Load(int id, string xmlPath = null)
    {
        return EpbTestRecordStore.LoadSingle(id, xmlPath);
    }
    */
}
