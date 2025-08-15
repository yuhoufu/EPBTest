using System;
using System.Collections.Generic;
using System.Xml;
using NationalInstruments.DAQmx;

// 避免与 System.Threading.Tasks.Task 混淆，做别名
using NIDaqTask = NationalInstruments.DAQmx.Task;
using NIChannelLineGrouping = NationalInstruments.DAQmx.ChannelLineGrouping;
using NITaskAction = NationalInstruments.DAQmx.TaskAction;

namespace IO.NI
{
    public class DoController : IDisposable
    {
        private readonly object _doTaskLock = new object();

        private NIDaqTask _doTask;
        private DigitalMultiChannelWriter _writer;

        // “每线一通道”的顺序表（与状态缓存一一对应）
        private readonly List<string> _physicalLines = new List<string>();
        private bool[] _currentStates;

        // EPB: 通道号 -> (正索引, 反索引)
        private readonly Dictionary<int, (int posIdx, int negIdx)> _epbIndex
            = new Dictionary<int, (int, int)>();

        // 压力: 编号 -> 索引
        private readonly Dictionary<int, int> _pressureIndex
            = new Dictionary<int, int>();

        // 配置路径
        private string _configPath;

        // 日志
        private readonly IAppLogger _log;

        public DoController(IAppLogger logger = null)
        {
            _log = logger ?? NullLogger.Instance;
        }

        public void SetConfigPath(string xmlPath) => _configPath = xmlPath;

        #region 初始化
        public bool Initialize()
        {
            lock (_doTaskLock)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(_configPath))
                    {
                        LogError("DO 初始化失败：未设置配置路径", "DO初始化");
                        return false;
                    }

                    // 清理旧
                    TryDisposeTask();
                    _doTask = null; _writer = null;
                    _physicalLines.Clear();
                    _epbIndex.Clear(); _pressureIndex.Clear();

                    // 读 XML
                    var doc = new XmlDocument();
                    doc.Load(_configPath);

                    _doTask = new NIDaqTask("DO_Task");

                    var defaultStates = new List<bool>();

                    // ========== EPB ==========
                    var epbNodes = doc.SelectNodes("//DOConfig/EPB/Record");
                    if (epbNodes != null)
                    {
                        foreach (XmlNode n in epbNodes)
                        {
                            string enabled = n.SelectSingleNode("是否启用")?.InnerText?.Trim() ?? "1";
                            if (enabled != "1") continue;

                            if (!int.TryParse(n.SelectSingleNode("通道号")?.InnerText?.Trim(), out int ch))
                                continue;

                            string pos = n.SelectSingleNode("正")?.InnerText?.Trim();
                            string neg = n.SelectSingleNode("反")?.InnerText?.Trim();
                            if (string.IsNullOrEmpty(pos) || string.IsNullOrEmpty(neg)) continue;

                            string def = n.SelectSingleNode("默认")?.InnerText?.Trim() ?? "全关";

                            // 创建“正”通道
                            _doTask.DOChannels.CreateChannel(pos, $"EPB{ch}-正", NIChannelLineGrouping.OneChannelForEachLine);
                            int posIdx = _physicalLines.Count;
                            _physicalLines.Add(pos);
                            defaultStates.Add(def == "正");

                            // 创建“反”通道
                            _doTask.DOChannels.CreateChannel(neg, $"EPB{ch}-反", NIChannelLineGrouping.OneChannelForEachLine);
                            int negIdx = _physicalLines.Count;
                            _physicalLines.Add(neg);
                            defaultStates.Add(def == "反");

                            // 默认=全关
                            if (def == "全关")
                            {
                                defaultStates[posIdx] = false;
                                defaultStates[negIdx] = false;
                            }

                            _epbIndex[ch] = (posIdx, negIdx);
                        }
                    }

                    // ========== 压力 ==========
                    var pNodes = doc.SelectNodes("//DOConfig/Pressure/Record");
                    if (pNodes != null)
                    {
                        foreach (XmlNode n in pNodes)
                        {
                            string enabled = n.SelectSingleNode("是否启用")?.InnerText?.Trim() ?? "1";
                            if (enabled != "1") continue;

                            if (!int.TryParse(n.SelectSingleNode("编号")?.InnerText?.Trim(), out int id))
                                continue;

                            string phy = n.SelectSingleNode("物理通道")?.InnerText?.Trim();
                            if (string.IsNullOrEmpty(phy)) continue;

                            string defStr = n.SelectSingleNode("默认值")?.InnerText?.Trim() ?? "0";
                            bool defVal = defStr == "1";

                            _doTask.DOChannels.CreateChannel(phy, $"Pressure-{id}", NIChannelLineGrouping.OneChannelForEachLine);
                            int idx = _physicalLines.Count;
                            _physicalLines.Add(phy);
                            defaultStates.Add(defVal);

                            _pressureIndex[id] = idx;
                        }
                    }

                    if (_physicalLines.Count == 0)
                    {
                        LogError("DO 初始化失败：无有效通道", "DO初始化");
                        TryDisposeTask();
                        _doTask = null;
                        return false;
                    }

                    // 验证
                    _doTask.Control(NITaskAction.Verify);

                    _writer = new DigitalMultiChannelWriter(_doTask.Stream);
                    _currentStates = defaultStates.ToArray();

                    // 下发默认安全状态
                    _writer.WriteSingleSampleSingleLine(true, _currentStates);

                    LogInfo($"DO 初始化完成：总线数={_physicalLines.Count}，EPB组={_epbIndex.Count}，压力点={_pressureIndex.Count}", "DO初始化");
                    return true;
                }
                catch (DaqException ex)
                {
                    LogError($"DO 初始化失败（DAQ）：{ex.Message}", "DO初始化", ex);
                    TryDisposeTask();
                    _doTask = null; _writer = null; _currentStates = null;
                    return false;
                }
                catch (Exception ex)
                {
                    LogError($"DO 初始化失败（未知）：{ex}", "DO初始化", ex);
                    TryDisposeTask();
                    _doTask = null; _writer = null; _currentStates = null;
                    return false;
                }
            }
        }
        #endregion

        #region 写入：EPB 与 压力
        /// <summary>
        /// EPB：通道号 + 方向（true=正，false=反），自动互斥
        /// </summary>
        public bool SetEpb(int channelNo, bool directionIsForward)
        {
            lock (_doTaskLock)
            {
                const int maxRetries = 1;
                int attempts = 0;

                while (attempts <= maxRetries)
                {
                    try
                    {
                        if (!EnsureReady())
                        {
                            attempts++;
                            continue;
                        }

                        if (!_epbIndex.TryGetValue(channelNo, out var pair))
                        {
                            LogError($"EPB 通道号未找到：{channelNo}", "DO操作");
                            return false;
                        }

                        var toWrite = (bool[])_currentStates.Clone();

                        // 互斥：正=1/反=0 或 正=0/反=1
                        toWrite[pair.posIdx] = directionIsForward;
                        toWrite[pair.negIdx] = !directionIsForward;

                        _writer.WriteSingleSampleSingleLine(true, toWrite);
                        _currentStates = toWrite;

                        LogInfo($"EPB[{channelNo}] => {(directionIsForward ? "正" : "反")}", "DO操作");
                        return true;
                    }
                    catch (DaqException ex)
                    {
                        attempts++;
                        LogError($"EPB 写入失败（第{attempts}/{maxRetries + 1}次）：{ex.Message}", "DO操作", ex);
                        if (attempts <= maxRetries) ResetTask();
                    }
                    catch (Exception ex)
                    {
                        LogError($"EPB 写入异常：{ex}", "DO操作", ex);
                        break;
                    }
                }

                LogError("EPB 写入失败：超过最大重试次数", "DO操作");
                return false;
            }
        }

        /// <summary>
        /// 压力：编号 + 是否启动
        /// </summary>
        public bool SetPressure(int id, bool start)
        {
            lock (_doTaskLock)
            {
                const int maxRetries = 1;
                int attempts = 0;

                while (attempts <= maxRetries)
                {
                    try
                    {
                        if (!EnsureReady())
                        {
                            attempts++;
                            continue;
                        }

                        if (!_pressureIndex.TryGetValue(id, out int idx))
                        {
                            LogError($"压力编号未找到：{id}", "DO操作");
                            return false;
                        }

                        var toWrite = (bool[])_currentStates.Clone();
                        toWrite[idx] = start;

                        _writer.WriteSingleSampleSingleLine(true, toWrite);
                        _currentStates = toWrite;

                        LogInfo($"压力[{id}] => {(start ? "启动" : "停止")}", "DO操作");
                        return true;
                    }
                    catch (DaqException ex)
                    {
                        attempts++;
                        LogError($"压力写入失败（第{attempts}/{maxRetries + 1}次）：{ex.Message}", "DO操作", ex);
                        if (attempts <= maxRetries) ResetTask();
                    }
                    catch (Exception ex)
                    {
                        LogError($"压力写入异常：{ex}", "DO操作", ex);
                        break;
                    }
                }

                LogError("压力写入失败：超过最大重试次数", "DO操作");
                return false;
            }
        }
        #endregion

        #region 便捷方法
        public bool AllOff()
        {
            lock (_doTaskLock)
            {
                try
                {
                    if (!EnsureReady()) return false;
                    var zeros = new bool[_currentStates.Length];
                    _writer.WriteSingleSampleSingleLine(true, zeros);
                    _currentStates = zeros;
                    LogInfo("DO 全部关闭", "DO操作");
                    return true;
                }
                catch (Exception ex)
                {
                    LogError($"全部关闭失败：{ex.Message}", "DO操作", ex);
                    return false;
                }
            }
        }
        #endregion

        #region 内部工具
        private bool EnsureReady()
        {
            if (_doTask != null && _writer != null && _currentStates != null) return true;
            return Initialize();
        }

        private void ResetTask()
        {
            TryDisposeTask();
            _doTask = null; _writer = null; _currentStates = null;
        }

        private void TryDisposeTask()
        {
            try { _doTask?.Dispose(); } catch { /* ignore */ }
        }

        private void LogInfo(string message, string category = null)
            => _log?.Info(message, category ?? "DO");

        private void LogError(string message, string category = null, Exception ex = null)
            => _log?.Error(message, category ?? "DO", ex);

        public void Dispose()
        {
            lock (_doTaskLock)
            {
                TryDisposeTask();
                _doTask = null;
                _writer = null;
                _currentStates = null;
            }
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}
