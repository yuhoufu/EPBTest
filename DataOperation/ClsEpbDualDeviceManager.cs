using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Config;
using DataOperation;

namespace DataOperation
{
    /// <summary>
    /// EPB双设备数据管理器 - 协调Dev1/Dev2的数据收集和存盘
    /// 与现有FrmEpbMainMonitor的Acq_OnRawBatch集成
    /// </summary>
    public class EpbDualDeviceManager
    {
        private readonly EpbDeviceContext _dev1Context;
        private readonly EpbDeviceContext _dev2Context;

        /// <summary>
        /// 初始化EPB双设备管理器
        /// </summary>
        /// <param name="storePath">数据存储路径</param>
        /// <param name="dev1ChannelMapping">Dev1的参数名->通道索引映射</param>
        /// <param name="dev2ChannelMapping">Dev2的参数名->通道索引映射</param>
        /// <param name="maxLens">队列最大长度，默认100</param>
        /// <param name="storeTimeMinutes">文件切换间隔（分钟），默认5分钟</param>
        public EpbDualDeviceManager(
            string storePath,
            Dictionary<string, int> dev1ChannelMapping,
            Dictionary<string, int> dev2ChannelMapping,
            int maxLens = 100,
            double storeTimeMinutes = 5.0)
        {
            // Dev1配置：通常包含EPB1-6电流 + 压力1
            var dev1EpbChannels = GetEpbChannelsForDevice(dev1ChannelMapping);
            _dev1Context = new EpbDeviceContext(
                "Dev1",
                maxLens,
                storeTimeMinutes,
                storePath,
                dev1EpbChannels,
                dev1ChannelMapping,
                hasPressure1: dev1ChannelMapping.ContainsKey("Pressure_1"),
                hasPressure2: false, // Dev1通常只有压力1
                hasForce: false      // Dev1通常没有夹紧力
            );

            // Dev2配置：通常包含EPB7-12电流 + 压力2 + 夹紧力
            var dev2EpbChannels = GetEpbChannelsForDevice(dev2ChannelMapping);
            _dev2Context = new EpbDeviceContext(
                "Dev2",
                maxLens,
                storeTimeMinutes,
                storePath,
                dev2EpbChannels,
                dev2ChannelMapping,
                hasPressure1: false, // Dev2通常没有压力1
                hasPressure2: dev2ChannelMapping.ContainsKey("Pressure_2"),
                hasForce: dev2ChannelMapping.ContainsKey("Force")
            );
        }

        /// <summary>
        /// 从通道映射中提取EPB通道号列表
        /// </summary>
        /// <param name="channelMapping">通道映射</param>
        /// <returns>EPB通道号列表</returns>
        private static List<int> GetEpbChannelsForDevice(Dictionary<string, int> channelMapping)
        {
            var epbChannels = new List<int>();

            foreach (var key in channelMapping.Keys)
            {
                if (key.StartsWith("EPB") && key.EndsWith("_current"))
                {
                    // 从"EPB1_current"中提取数字1
                    var epbNumStr = key.Replace("EPB", "").Replace("_current", "");
                    if (int.TryParse(epbNumStr, out var epbNum))
                    {
                        epbChannels.Add(epbNum);
                    }
                }
            }

            return epbChannels.OrderBy(x => x).ToList();
        }

        /// <summary>
        /// 处理原始数据批次（从FrmEpbMainMonitor.Acq_OnRawBatch调用）
        /// </summary>
        /// <param name="device">设备名称 ("Dev1"/"Dev2")</param>
        /// <param name="raw">原始数据矩阵 [通道, 样本]</param>
        /// <param name="current">当前时间戳</param>
        /// <param name="last">上次时间戳</param>
        public void OnRawBatch(string device, double[,] raw, DateTime current, DateTime last)
        {
            if (device.Equals("Dev1", StringComparison.OrdinalIgnoreCase))
            {
                _dev1Context?.EnqueueRawData(raw, current, last);
            }
            else if (device.Equals("Dev2", StringComparison.OrdinalIgnoreCase))
            {
                _dev2Context?.EnqueueRawData(raw, current, last);
            }
        }

        /// <summary>
        /// 增加EPB循环计数（当EPB完成一次循环时调用）
        /// </summary>
        /// <param name="epbChannel">EPB通道号 (1-12)</param>
        public void IncrementEpbCycleCounter(int epbChannel)
        {
            // 根据EPB通道号决定由哪个设备处理
            if (epbChannel >= 1 && epbChannel <= 6)
            {
                _dev1Context?.IncrementEpbCycleCounter(epbChannel);
            }
            else if (epbChannel >= 7 && epbChannel <= 12)
            {
                _dev2Context?.IncrementEpbCycleCounter(epbChannel);
            }
        }

        /// <summary>
        /// 获取EPB循环计数
        /// </summary>
        /// <param name="epbChannel">EPB通道号 (1-12)</param>
        /// <returns>循环计数</returns>
        public int GetEpbCycleCounter(int epbChannel)
        {
            if (epbChannel >= 1 && epbChannel <= 6)
            {
                return _dev1Context?.GetEpbCycleCounter(epbChannel) ?? 0;
            }
            else if (epbChannel >= 7 && epbChannel <= 12)
            {
                return _dev2Context?.GetEpbCycleCounter(epbChannel) ?? 0;
            }
            return 0;
        }

        /// <summary>
        /// 执行数据落盘（通常在定时器中调用）
        /// </summary>
        public async Task FlushAllToDiskAsync()
        {
            // 并行执行两个设备的落盘操作
            var dev1Task = _dev1Context?.FlushRawToDiskAsync() ?? Task.CompletedTask;
            var dev2Task = _dev2Context?.FlushRawToDiskAsync() ?? Task.CompletedTask;

            await Task.WhenAll(dev1Task, dev2Task);
        }

        /// <summary>
        /// 单独执行Dev1落盘
        /// </summary>
        public async Task FlushDev1ToDiskAsync()
        {
            if (_dev1Context != null)
                await _dev1Context.FlushRawToDiskAsync();
        }

        /// <summary>
        /// 单独执行Dev2落盘
        /// </summary>
        public async Task FlushDev2ToDiskAsync()
        {
            if (_dev2Context != null)
                await _dev2Context.FlushRawToDiskAsync();
        }

        /// <summary>
        /// 获取所有EPB的循环计数状态
        /// </summary>
        /// <returns>EPB通道号到循环计数的映射</returns>
        public Dictionary<int, int> GetAllEpbCycleCounters()
        {
            var result = new Dictionary<int, int>();

            // 从Dev1获取EPB1-6的计数
            for (int i = 1; i <= 6; i++)
            {
                result[i] = GetEpbCycleCounter(i);
            }

            // 从Dev2获取EPB7-12的计数
            for (int i = 7; i <= 12; i++)
            {
                result[i] = GetEpbCycleCounter(i);
            }

            return result;
        }
    }
}

/* 使用示例（在FrmEpbMainMonitor中集成）：

public partial class FrmEpbMainMonitor : Form
{
    private EpbDualDeviceManager _epbDataManager;

    private void FrmEpbMainMonitor_Load(object sender, EventArgs e)
    {
        // ... 现有初始化代码 ...

        // 初始化EPB双设备数据管理器
        _epbDataManager = new EpbDualDeviceManager(
            _dataStorePath,                    // 数据存储路径
            Dev1DaqChannel,                   // Dev1通道映射
            Dev2DaqChannel,                   // Dev2通道映射
            100,                              // 队列最大长度
            ClsGlobal.FileChangeMinutes       // 文件切换间隔
        );

        // ... 其他初始化代码 ...
    }

    // 修改现有的原始数据批次处理方法
    private void Acq_OnRawBatch(string device, double[,] raw, DateTime current, DateTime last)
    {
        if (_isClosing) return;

        // 原有的数据落盘逻辑（保持兼容）
        if (device.Equals("Dev1", StringComparison.OrdinalIgnoreCase))
        {
            _daqDev1?.EnqueueRawData(raw, current, last);
            _daqDev1?.EnqueueStatData(raw, current);
        }
        else if (device.Equals("Dev2", StringComparison.OrdinalIgnoreCase))
        {
            _daqDev2?.EnqueueRawData(raw, current, last);
            _daqDev2?.EnqueueStatData(raw, current);
        }

        // 新增：EPB专用数据处理
        _epbDataManager?.OnRawBatch(device, raw, current, last);
    }

    // 在EpbManager的循环完成回调中增加计数
    private void OnEpbCycleCompleted(int epbChannel)
    {
        _epbDataManager?.IncrementEpbCycleCounter(epbChannel);
    }

    // 修改定时器，增加EPB数据落盘
    private void InitDaqLogTimer(int logSpanMs)
    {
        // ... 现有定时器代码 ...

        // 新增：EPB数据落盘定时器
        var epbFlushTimer = new Timer(async _ =>
        {
            try
            {
                await _epbDataManager?.FlushAllToDiskAsync();
            }
            catch
            {
                // 忽略落盘错误
            }
        }, null, logSpanMs, logSpanMs);
    }
}

*/