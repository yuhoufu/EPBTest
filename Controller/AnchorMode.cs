using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Controller
{
    /// <summary>
    ///     跨组锚点对齐的目标时刻计算模式。
    /// </summary>
    public enum AnchorMode
    {
        /// <summary>当前时间之后的某个固定延迟（毫秒）。</summary>
        AfterMs = 0,

        /// <summary>立刻选择一个未来的可行锚点（自动包含保护时隙）。</summary>
        NowAligned = 1,

        /// <summary>使用绝对时间 <see cref="DateTime" /> 作为锚点（见 EpbManager 调用重载）。</summary>
        AtTime = 2,

        /// <summary>在两周期最小公倍数（LCM）的第 k 个未来整点对齐。</summary>
        AtNextKMultiples = 3
    }

    /// <summary>
    ///     压力组（PG1/PG2），每组由一个 HighPrecisionTimer 驱动。
    /// </summary>
    public enum PressureGroup
    {
        Pg1 = 1,
        Pg2 = 2
    }

    /// <summary>
    ///     学习态（Learn）断点续学的 sidecar 存储。
    ///     不修改 TestConfig.xml 的 <EpbRecords> schema，避免影响既有系统。
    /// </summary>
    public sealed class EpbLearnStateStore
    {
        /// <summary>Sidecar 文件路径。</summary>
        private string FilePath => Path.Combine(_dir, "EpbLearnState.json");

        /// <summary>单例，也可按需在 EpbManager 内部持有一个实例。</summary>
        public static readonly EpbLearnStateStore Instance = new();

        private readonly string _dir;

        private readonly ConcurrentDictionary<int, LearnState> _mem = new();
        private readonly DataContractJsonSerializer _ser = new(typeof(Dictionary<int, LearnState>));

        private EpbLearnStateStore()
        {
            _dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config");
            Directory.CreateDirectory(_dir);
        }

        /// <summary>读取所有学习态（若不存在则返回空）。</summary>
        public void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                using var fs = File.OpenRead(FilePath);
                if (_ser.ReadObject(fs) is Dictionary<int, LearnState> dict)
                {
                    _mem.Clear();
                    foreach (var kv in dict) _mem[kv.Key] = kv.Value;
                }
            }
            catch
            {
                /* 可记录日志 */
            }
        }

        /// <summary>保存全部学习态。</summary>
        public void SaveAll()
        {
            try
            {
                var dict = new Dictionary<int, LearnState>(_mem);
                using var ms = new MemoryStream();
                _ser.WriteObject(ms, dict);
                File.WriteAllText(FilePath, Encoding.UTF8.GetString(ms.ToArray()), Encoding.UTF8);
            }
            catch
            {
                /* 可记录日志与重试 */
            }
        }

        /// <summary>获取或创建某 EPB 的学习态。</summary>
        public LearnState GetOrCreate(int epbId)
        {
            return _mem.GetOrAdd(epbId, _ => new LearnState());
        }

        /// <summary>设置并立即保存单个 EPB 的学习态。</summary>
        public void SetAndSave(int epbId, LearnState st)
        {
            _mem[epbId] = st ?? new LearnState();
            SaveAll();
        }

        /// <summary>删除单个 EPB 的学习态（例如 Stop/E-Stop 后清理）。</summary>
        public void RemoveAndSave(int epbId)
        {
            _mem.TryRemove(epbId, out _);
            SaveAll();
        }

        [DataContract]
        public sealed class LearnState
        {
            [DataMember(Order = 1)]
            public string LearnStatus { get; set; } = "None"; // None/InProgress/Completed/NeedRelearn

            [DataMember(Order = 2)]
            public string LearnStage { get; set; } = "None"; // FwdEmpty/RevEmpty/Polarity/Predict

            [DataMember(Order = 3)] public uint LearnVersion { get; set; }

            [DataMember(Order = 4)] public string LastConfigHash { get; set; } = "";

            // 关键参数（可按需扩展）
            [DataMember(Order = 5)] public double IEmptyFwd { get; set; }
            [DataMember(Order = 6)] public double IEmptyRev { get; set; }
            [DataMember(Order = 7)] public double SlopeFwdAps { get; set; }
            [DataMember(Order = 8)] public DateTime LastLearnAt { get; set; } = DateTime.MinValue;
        }
    }
}