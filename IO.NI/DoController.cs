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
        private sealed class DoDevice
        {
            public string Name;
            public NIDaqTask Task;
            public DigitalMultiChannelWriter Writer;

            // ✅ 每个设备实例独立列表（不再共享 static）
            public readonly List<string> Lines = new List<string>();
            public readonly List<bool> DefaultStates = new List<bool>();

            public bool[] States;
        }

        private readonly object _doTaskLock = new object();

        // 每设备上下文
        private readonly Dictionary<string, DoDevice> _devices =
            new Dictionary<string, DoDevice>(StringComparer.OrdinalIgnoreCase);

        // EPB: 通道号 -> (设备名, 正Idx, 反Idx) —— 索引是该设备 DefaultStates/States 的索引
        private readonly Dictionary<int, (string dev, int posIdx, int negIdx)> _epbIndex
            = new Dictionary<int, (string, int, int)>();

        // 压力: 编号 -> (设备名, 索引)
        private readonly Dictionary<int, (string dev, int idx)> _pressureIndex
            = new Dictionary<int, (string, int)>();

        private string _configPath;
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

                    ResetAllDevices(clearMaps: true);

                    var doc = new XmlDocument();
                    doc.Load(_configPath);

                    // ===== EPB =====
                    var epbNodes = doc.SelectNodes("//DOConfig/EPB/Record");
                    if (epbNodes != null)
                    {
                        foreach (XmlNode n in epbNodes)
                        {
                            string enabled = n.SelectSingleNode("是否启用")?.InnerText?.Trim() ?? "1";
                            if (enabled != "1") continue;

                            if (!int.TryParse(n.SelectSingleNode("通道号")?.InnerText?.Trim(), out int ch))
                                continue;

                            string posPhy = n.SelectSingleNode("正")?.InnerText?.Trim();
                            string negPhy = n.SelectSingleNode("反")?.InnerText?.Trim();
                            if (string.IsNullOrEmpty(posPhy) || string.IsNullOrEmpty(negPhy))
                                continue;

                            string devNamePos = GetDeviceName(posPhy);
                            string devNameNeg = GetDeviceName(negPhy);
                            if (!devNamePos.Equals(devNameNeg, StringComparison.OrdinalIgnoreCase))
                            {
                                LogError($"EPB{ch} 的正/反分属不同设备（{devNamePos} / {devNameNeg}），不支持跨设备互斥，已跳过。", "DO初始化");
                                continue;
                            }

                            var dev = EnsureDevice(devNamePos);
                            string def = n.SelectSingleNode("默认")?.InnerText?.Trim() ?? "全关";

                            // 正
                            dev.Task.DOChannels.CreateChannel(posPhy, $"EPB{ch}-正", NIChannelLineGrouping.OneChannelForEachLine);
                            dev.Lines.Add(posPhy);
                            dev.DefaultStates.Add(def == "正");
                            int posIdx = dev.DefaultStates.Count - 1; // ✅ 刚插入的位置

                            // 反
                            dev.Task.DOChannels.CreateChannel(negPhy, $"EPB{ch}-反", NIChannelLineGrouping.OneChannelForEachLine);
                            dev.Lines.Add(negPhy);
                            dev.DefaultStates.Add(def == "反");
                            int negIdx = dev.DefaultStates.Count - 1; // ✅ 刚插入的位置

                            if (def == "全关")
                            {
                                dev.DefaultStates[posIdx] = false;
                                dev.DefaultStates[negIdx] = false;
                            }

                            _epbIndex[ch] = (devNamePos, posIdx, negIdx);
                        }
                    }

                    // ===== Pressure =====
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

                            string devName = GetDeviceName(phy);
                            var dev = EnsureDevice(devName);

                            string defStr = n.SelectSingleNode("默认值")?.InnerText?.Trim() ?? "0";
                            bool defVal = defStr == "1";

                            dev.Task.DOChannels.CreateChannel(phy, $"Pressure-{id}", NIChannelLineGrouping.OneChannelForEachLine);
                            dev.Lines.Add(phy);
                            dev.DefaultStates.Add(defVal);
                            int idx = dev.DefaultStates.Count - 1; // ✅ 刚插入的位置

                            _pressureIndex[id] = (devName, idx);
                        }
                    }

                    if (_devices.Count == 0)
                    {
                        LogError("DO 初始化失败：未创建任何设备任务（配置可能为空或全部禁用）", "DO初始化");
                        ResetAllDevices(clearMaps: true);
                        return false;
                    }

                    // 逐设备 Verify + Writer + 下发默认值
                    int totalLines = 0;
                    foreach (var dev in _devices.Values)
                    {
                        if (dev.Lines.Count == 0) continue;

                        dev.Task.Control(NITaskAction.Verify);

                        dev.Writer = new DigitalMultiChannelWriter(dev.Task.Stream);
                        dev.States = dev.DefaultStates.ToArray();

                        dev.Writer.WriteSingleSampleSingleLine(true, dev.States);
                        totalLines += dev.Lines.Count;
                    }

                    LogInfo($"DO 初始化完成：设备数={_devices.Count}，总线数={totalLines}，EPB组={_epbIndex.Count}，压力点={_pressureIndex.Count}", "DO初始化");
                    return true;
                }
                catch (DaqException ex)
                {
                    LogError("DO 初始化失败（DAQ）：" + ex.Message, "DO初始化", ex);
                    ResetAllDevices(clearMaps: false);
                    return false;
                }
                catch (Exception ex2)
                {
                    LogError($"DO 初始化失败（未知）：{ex2}", "DO初始化", ex2);
                    ResetAllDevices(clearMaps: false);
                    return false;
                }
            }
        }
        #endregion

        #region 写入：EPB 与 压力
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
                        if (!EnsureReady()) { attempts++; continue; }

                        if (!_epbIndex.TryGetValue(channelNo, out var map))
                        {
                            LogError($"EPB 通道号未找到：{channelNo}", "DO操作");
                            return false;
                        }

                        if (!_devices.TryGetValue(map.dev, out var dev))
                        {
                            LogError($"EPB[{channelNo}] 所属设备未就绪：{map.dev}", "DO操作");
                            return false;
                        }

                        var toWrite = (bool[])dev.States.Clone();
                        toWrite[map.posIdx] = directionIsForward;
                        toWrite[map.negIdx] = !directionIsForward;

                        dev.Writer.WriteSingleSampleSingleLine(true, toWrite);
                        dev.States = toWrite;

                        LogInfo($"EPB[{channelNo}]@{map.dev} => {(directionIsForward ? "正" : "反")}", "DO操作");
                        return true;
                    }
                    catch (DaqException ex)
                    {
                        attempts++;
                        LogError($"EPB 写入失败（第{attempts}/{maxRetries + 1}次）：{ex.Message}", "DO操作", ex);
                        if (attempts <= maxRetries) { ResetAllDevices(clearMaps: false); Initialize(); }
                    }
                    catch (Exception ex2)
                    {
                        LogError($"EPB 写入异常：{ex2}", "DO操作", ex2);
                        break;
                    }
                }

                LogError("EPB 写入失败：超过最大重试次数", "DO操作");
                return false;
            }
        }

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
                        if (!EnsureReady()) { attempts++; continue; }

                        if (!_pressureIndex.TryGetValue(id, out var map))
                        {
                            LogError($"压力编号未找到：{id}", "DO操作");
                            return false;
                        }

                        if (!_devices.TryGetValue(map.dev, out var dev))
                        {
                            LogError($"压力[{id}] 所属设备未就绪：{map.dev}", "DO操作");
                            return false;
                        }

                        var toWrite = (bool[])dev.States.Clone();
                        toWrite[map.idx] = start;

                        dev.Writer.WriteSingleSampleSingleLine(true, toWrite);
                        dev.States = toWrite;

                        LogInfo($"压力[{id}]@{map.dev} => {(start ? "启动" : "停止")}", "DO操作");
                        return true;
                    }
                    catch (DaqException ex)
                    {
                        attempts++;
                        LogError($"压力写入失败（第{attempts}/{maxRetries + 1}次）：{ex.Message}", "DO操作", ex);
                        if (attempts <= maxRetries) { ResetAllDevices(clearMaps: false); Initialize(); }
                    }
                    catch (Exception ex2)
                    {
                        LogError($"压力写入异常：{ex2}", "DO操作", ex2);
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

                    foreach (var dev in _devices.Values)
                    {
                        var zeros = new bool[dev.States.Length];
                        dev.Writer.WriteSingleSampleSingleLine(true, zeros);
                        dev.States = zeros;
                    }

                    LogInfo("DO 全部关闭（所有设备）", "DO操作");
                    return true;
                }
                catch (Exception ex)
                {
                    LogError("全部关闭失败：" + ex.Message, "DO操作", ex);
                    return false;
                }
            }
        }
        #endregion

        #region 内部工具
        private DoDevice EnsureDevice(string deviceName)
        {
            if (!_devices.TryGetValue(deviceName, out var dev))
            {
                dev = new DoDevice
                {
                    Name = deviceName,
                    Task = new NIDaqTask("DO_" + deviceName)
                };
                _devices[deviceName] = dev;
            }
            return dev;
        }

        private static string GetDeviceName(string physicalLine)
        {
            if (string.IsNullOrWhiteSpace(physicalLine)) return "";
            int slash = physicalLine.IndexOf('/');
            return slash > 0 ? physicalLine.Substring(0, slash) : physicalLine;
        }

        private bool EnsureReady()
        {
            if (_devices.Count == 0) return Initialize();

            foreach (var dev in _devices.Values)
            {
                if (dev.Task == null || dev.Writer == null || dev.States == null)
                    return Initialize();
            }
            return true;
        }

        private void ResetAllDevices(bool clearMaps)
        {
            foreach (var dev in _devices.Values)
            {
                try { dev.Task?.Dispose(); } catch { /* ignore */ }
                dev.Task = null;
                dev.Writer = null;
                dev.Lines.Clear();
                dev.DefaultStates.Clear();
                dev.States = null;
            }
            if (clearMaps)
            {
                _devices.Clear();
                _epbIndex.Clear();
                _pressureIndex.Clear();
            }
        }

        private void LogInfo(string message, string category = null)
            => _log?.Info(message, category ?? "DO");

        private void LogError(string message, string category = null, Exception ex = null)
            => _log?.Error(message, category ?? "DO", ex);

        public void Dispose()
        {
            lock (_doTaskLock)
            {
                ResetAllDevices(clearMaps: true);
            }
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}
