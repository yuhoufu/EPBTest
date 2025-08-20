using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

namespace Config
{
    /// <summary>AIConfigDetail 的单条记录（中文字段名）。</summary>
    public class AiConfigDetailRecord
    {
        public int 序号 { get; set; }
        public string 物理通道 { get; set; }        // 如 Dev1/ai0
        public string 参数名 { get; set; }          // 如 EPB1_current / Pressure_1
        public string 单位 { get; set; }            // A, Bar, N ...
        public double 变换斜率 { get; set; }        // scale
        public double 变换截距 { get; set; }        // offset
        public string 参数类型 { get; set; }        // 电流/管路压力/夹紧力...
        public int 是否启用 { get; set; }           // 1 启用
        public double 零位漂移 { get; set; }        // 零漂
    }

    [XmlRoot("AIConfigDetail")]
    public class AiConfigDetail
    {
        [XmlElement("Records")]
        public List<AiConfigDetailRecord> Records { get; set; } = new();
    }

    public static class AiConfigLoader
    {
        /// <summary>从 XML 读 AIConfigDetail，并按 启用/设备名 分组。</summary>
        public static AiConfigDetail Load(string path)
        {
            using var fs = File.OpenRead(path);
            var ser = new XmlSerializer(typeof(AiConfigDetail));
            return (AiConfigDetail)ser.Deserialize(fs);
        }

        /// <summary>返回已启用的记录，保持原文件顺序（决定 DAQ 返回矩阵的通道顺序）。</summary>
        public static List<AiConfigDetailRecord> Enabled(this AiConfigDetail cfg) =>
            cfg.Records.Where(r => r.是否启用 == 1).OrderBy(r => r.序号).ToList();
    }
}