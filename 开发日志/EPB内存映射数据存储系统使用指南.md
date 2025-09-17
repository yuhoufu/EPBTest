# EPB内存映射数据存储系统 - 使用指南

## 概述

EPB内存映射数据存储系统是为万向EPB测试系统设计的高性能数据存储解决方案。采用内存映射文件和滑动窗口技术，实现了高效的数据写入和查询功能。

## 系统架构

```
EPB测试数据采集
        ↓
EpbMemoryMappedStorage (主管理器)
        ↓
┌─────────────────┬─────────────────┐
│   CircularBuffer│   EpbIndexManager│
│   (内存映射文件)  │   (SQLite索引)   │
└─────────────────┴─────────────────┘
        ↓                    ↓
   EPB1~12.dat            index.db
   (384MB×12)             (~10MB)
```

## 核心特性

- **高性能数据写入**：>1,000,000样本/秒，零拷贝操作
- **自动数据管理**：固定大小滑动窗口，自动覆盖旧数据
- **灵活查询**：支持按循环、时间范围、最新N条记录查询
- **崩溃安全**：内存映射文件确保数据持久化
- **低内存占用**：操作系统按需加载，实际占用500MB-1GB

## 快速开始

### 1. 基本初始化

```csharp
using DataOperation;

// 使用默认配置
var storage = new EpbMemoryMappedStorage();

// 或者自定义配置
var storage = new EpbMemoryMappedStorage(
    dataStorePath: "D:\\EPBData",      // 数据存储路径
    retainCycles: 10,                  // 保留最新10个循环
    samplesPerCycle: 300000            // 每循环30万采样点
);
```

### 2. 写入数据

```csharp
// 单个样本写入
long position = storage.WriteSample(
    epbId: 1,              // EPB编号（1-12）
    epbCurrent: 15.5,      // EPB电流值
    groupPressure: 8.2     // 对应组压力值
);

// 批量写入
var samples = new EpbSampleData[]
{
    new EpbSampleData(1, 15.5, 8.2),
    new EpbSampleData(2, 16.1, 8.2),
    new EpbSampleData(3, 14.8, 8.3)
};
int writtenCount = storage.WriteSamples(samples);
```

### 3. 查询数据

```csharp
// 获取最新N条记录
SampleRecord[] latestSamples = storage.GetLatestSamples(epbId: 1, count: 1000);

// 获取指定循环的数据
SampleRecord[] cycleData = storage.GetCycleData(epbId: 1, cycleNumber: 5);

// 按时间范围查询
SampleRecord[] timeRangeData = storage.GetDataByTimeRange(
    epbId: 1,
    startTime: DateTime.Now.AddHours(-1),
    endTime: DateTime.Now
);

// 获取统计信息
EpbStatistics stats = storage.GetStatistics(epbId: 1);
Console.WriteLine($"EPB1统计：{stats}");
```

## 配置管理

### 1. XML配置文件

```xml
<?xml version="1.0" encoding="UTF-8"?>
<DataRetentionPolicy>
    <StorageMode>MemoryMapped</StorageMode>
    <RetainLatestCycles>10</RetainLatestCycles>
    <FileSize>384</FileSize>
    <SampleRecordSize>128</SampleRecordSize>
    <DataStorePath>DataStore</DataStorePath>
    <SamplesPerCycle>300000</SamplesPerCycle>
    <EnableAutoFlush>true</EnableAutoFlush>
    <FlushIntervalSeconds>30</FlushIntervalSeconds>
</DataRetentionPolicy>
```

### 2. 程序化配置

```csharp
// 加载配置文件
var config = MemoryMappedStorageConfig.LoadFromXml("Config\\DataRetentionPolicy.xml");

// 创建预定义配置
var config = MemoryMappedStorageConfig.CreateHighPerformance(); // 高性能配置
var config = MemoryMappedStorageConfig.CreateSpaceSaving();     // 节省空间配置

// 使用配置创建存储管理器
var storage = new EpbMemoryMappedStorage(
    config.DataStorePath,
    config.RetainLatestCycles,
    config.SamplesPerCycle
);

// 配置验证
if (!config.IsValid(out string errorMessage))
{
    Console.WriteLine($"配置错误：{errorMessage}");
}
```

## 数据结构

### SampleRecord结构 (128字节)
```csharp
struct SampleRecord
{
    long Timestamp;         // 时间戳 (DateTime.ToBinary())
    int CycleNumber;        // 循环编号
    int SampleIndex;        // 样本索引
    double EpbCurrent;      // EPB电流值
    double GroupPressure;   // 组压力值
    double Reserved1;       // 预留字段1
    double Reserved2;       // 预留字段2
    byte[88] Reserved;      // 88字节预留空间
}
```

### 使用SampleRecord
```csharp
// 创建记录
var record = new SampleRecord(DateTime.Now, 1, 0, 15.5, 8.2);

// 获取时间戳
DateTime timestamp = record.GetDateTime();

// 序列化和反序列化
byte[] data = record.ToBytes();
SampleRecord restored = SampleRecord.FromBytes(data);
```

## 与现有系统集成

### 1. 集成到数据采集流程

```csharp
public class EpbDataCollector
{
    private EpbMemoryMappedStorage _storage;

    public void Initialize()
    {
        _storage = new EpbMemoryMappedStorage("DataStore", 10, 300000);
    }

    // 在现有的AI数据采集回调中添加
    private void OnAIDataReceived(int epbId, double current, double pressure)
    {
        try
        {
            _storage.WriteSample(epbId, current, pressure);
        }
        catch (Exception ex)
        {
            // 记录错误但不影响采集流程
            Console.WriteLine($"数据存储失败: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _storage?.Dispose();
    }
}
```

### 2. 集成到测试设置界面

```csharp
public partial class FrmTestSetting : Form
{
    private void LoadStorageConfig()
    {
        try
        {
            var config = MemoryMappedStorageConfig.LoadFromXml("Config\\DataRetentionPolicy.xml");

            txtRetainCycles.Text = config.RetainLatestCycles.ToString();
            txtSamplesPerCycle.Text = config.SamplesPerCycle.ToString();
            txtDataPath.Text = config.DataStorePath;

            // 显示资源要求
            lblSystemRequirements.Text = config.CheckSystemRequirements();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"加载存储配置失败：{ex.Message}", "配置错误");
        }
    }

    private void SaveStorageConfig()
    {
        var config = new MemoryMappedStorageConfig
        {
            RetainLatestCycles = int.Parse(txtRetainCycles.Text),
            SamplesPerCycle = int.Parse(txtSamplesPerCycle.Text),
            DataStorePath = txtDataPath.Text
        };

        config.SaveToXml("Config\\DataRetentionPolicy.xml");
    }
}
```

### 3. 集成到数据回放界面

```csharp
public partial class FrmRawPlayBack : Form
{
    private EpbMemoryMappedStorage _storage;

    private void LoadHistoryData(int epbId, DateTime startTime, DateTime endTime)
    {
        try
        {
            var samples = _storage.GetDataByTimeRange(epbId, startTime, endTime);

            // 转换为图表数据
            var chartData = samples.Select(s => new
            {
                Time = s.GetDateTime(),
                Current = s.EpbCurrent,
                Pressure = s.GroupPressure,
                Cycle = s.CycleNumber
            }).ToArray();

            // 更新图表显示
            UpdateChart(chartData);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"加载历史数据失败：{ex.Message}", "数据错误");
        }
    }
}
```

## 性能优化建议

### 1. 写入性能优化

```csharp
// 使用批量写入而不是单个写入
var batchSamples = new List<EpbSampleData>();

// 收集一批数据
for (int i = 0; i < batchSize; i++)
{
    batchSamples.Add(new EpbSampleData(epbId, current[i], pressure[i]));
}

// 批量写入
storage.WriteSamples(batchSamples.ToArray());
```

### 2. 内存管理

```csharp
// 定期刷新到磁盘
await storage.FlushAsync();

// 监控内存使用
foreach (int epbId in Enumerable.Range(1, 12))
{
    var stats = storage.GetStatistics(epbId);
    if (stats.BufferIsFull)
    {
        Console.WriteLine($"EPB{epbId}缓冲区已满，开始循环覆盖");
    }
}
```

### 3. 查询优化

```csharp
// 优先使用索引查询
var recentCycles = storage.GetLatestSamples(epbId, 1000); // 快速

// 避免大范围时间查询
var limitedTimeRange = storage.GetDataByTimeRange(
    epbId,
    DateTime.Now.AddMinutes(-10),  // 限制查询范围
    DateTime.Now
);
```

## 错误处理和诊断

### 1. 常见错误处理

```csharp
try
{
    var storage = new EpbMemoryMappedStorage();
}
catch (UnauthorizedAccessException ex)
{
    Console.WriteLine("权限不足，请以管理员身份运行");
}
catch (DirectoryNotFoundException ex)
{
    Console.WriteLine($"数据存储目录不存在：{ex.Message}");
}
catch (InsufficientMemoryException ex)
{
    Console.WriteLine($"内存不足：{ex.Message}");
}
```

### 2. 诊断信息

```csharp
// 获取系统状态
foreach (int epbId in Enumerable.Range(1, 12))
{
    var stats = storage.GetStatistics(epbId);
    Console.WriteLine($"""
        EPB{epbId}状态报告：
        - 总循环数：{stats.TotalCycles}
        - 总样本数：{stats.TotalSamples:N0}
        - 当前循环：{stats.CurrentCycleNumber}
        - 当前样本：{stats.CurrentSampleIndex}
        - 缓冲区状态：{(stats.BufferIsFull ? "已满" : "未满")}
        - 写入位置：{stats.BufferWritePosition:N0}
        """);
}

// 配置诊断
var config = MemoryMappedStorageConfig.LoadFromXml("Config\\DataRetentionPolicy.xml");
Console.WriteLine(config.GetSummary());
Console.WriteLine(config.CheckSystemRequirements());
```

## 数据导出

### 1. 导出指定循环数据

```csharp
public void ExportCycleData(int epbId, int cycleNumber, string outputPath)
{
    var cycleData = storage.GetCycleData(epbId, cycleNumber);

    using (var writer = new StreamWriter(outputPath))
    {
        writer.WriteLine("Timestamp,CycleNumber,SampleIndex,Current,Pressure");

        foreach (var record in cycleData)
        {
            writer.WriteLine($"{record.GetDateTime():yyyy-MM-dd HH:mm:ss.fff}," +
                           $"{record.CycleNumber}," +
                           $"{record.SampleIndex}," +
                           $"{record.EpbCurrent:F3}," +
                           $"{record.GroupPressure:F3}");
        }
    }
}
```

### 2. 导出时间范围数据

```csharp
public void ExportTimeRangeData(int epbId, DateTime startTime, DateTime endTime, string outputPath)
{
    var timeRangeData = storage.GetDataByTimeRange(epbId, startTime, endTime);

    // 导出为二进制格式（高效）
    using (var stream = new FileStream(outputPath, FileMode.Create))
    using (var writer = new BinaryWriter(stream))
    {
        writer.Write(timeRangeData.Length); // 记录数量

        foreach (var record in timeRangeData)
        {
            var bytes = record.ToBytes();
            writer.Write(bytes);
        }
    }
}
```

## 维护和监控

### 1. 定期维护任务

```csharp
public class StorageMaintenanceTask
{
    private readonly EpbMemoryMappedStorage _storage;
    private readonly Timer _maintenanceTimer;

    public StorageMaintenanceTask(EpbMemoryMappedStorage storage)
    {
        _storage = storage;

        // 每小时执行一次维护
        _maintenanceTimer = new Timer(PerformMaintenance, null,
            TimeSpan.FromHours(1), TimeSpan.FromHours(1));
    }

    private void PerformMaintenance(object state)
    {
        try
        {
            // 强制刷新数据
            _storage.Flush();

            // 检查磁盘空间
            CheckDiskSpace();

            // 记录统计信息
            LogStatistics();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"维护任务失败：{ex.Message}");
        }
    }

    private void CheckDiskSpace()
    {
        var drive = new DriveInfo(Path.GetPathRoot(_storage.DataStorePath));
        long availableGB = drive.AvailableFreeSpace / 1024 / 1024 / 1024;

        if (availableGB < 5) // 少于5GB时警告
        {
            Console.WriteLine($"警告：磁盘空间不足，剩余{availableGB}GB");
        }
    }
}
```

### 2. 性能监控

```csharp
public class StoragePerformanceMonitor
{
    private readonly Dictionary<int, DateTime> _lastWriteTime = new();
    private readonly Dictionary<int, long> _writeCount = new();

    public void RecordWrite(int epbId)
    {
        _lastWriteTime[epbId] = DateTime.Now;
        _writeCount[epbId] = _writeCount.GetValueOrDefault(epbId, 0) + 1;
    }

    public double GetWriteRate(int epbId, TimeSpan duration)
    {
        if (!_writeCount.ContainsKey(epbId))
            return 0;

        return _writeCount[epbId] / duration.TotalSeconds;
    }
}
```

## 故障排除

### 1. 常见问题和解决方案

**问题：内存不足错误**
```
解决方案：
1. 减少RetainLatestCycles配置值
2. 减少SamplesPerCycle配置值
3. 增加系统物理内存
```

**问题：文件访问权限错误**
```
解决方案：
1. 以管理员身份运行程序
2. 修改DataStorePath到有写权限的目录
3. 检查防病毒软件设置
```

**问题：数据丢失**
```
解决方案：
1. 检查配置文件RetainLatestCycles设置
2. 确认是否为正常的循环覆盖行为
3. 增加保留循环数
```

### 2. 调试模式

```csharp
#if DEBUG
// 启用详细日志
public class DebugStorage : EpbMemoryMappedStorage
{
    protected override void OnSampleWritten(int epbId, long position)
    {
        Console.WriteLine($"[DEBUG] EPB{epbId} 写入位置 {position}");
    }
}
#endif
```

## 总结

EPB内存映射数据存储系统提供了一个高性能、可靠的数据存储解决方案。通过合理配置和正确使用，可以满足EPB测试系统的所有数据存储需求。

关键要点：
1. **性能优先**：使用批量写入和适当的刷新策略
2. **内存管理**：监控缓冲区状态，合理配置容量
3. **错误处理**：妥善处理各种异常情况
4. **定期维护**：监控系统状态，执行维护任务
5. **配置管理**：根据实际需求调整配置参数

## 技术支持

如遇到问题，请检查：
1. 系统日志和错误信息
2. 配置文件有效性
3. 磁盘空间和内存状态
4. 文件权限设置

更多技术细节请参考源码注释和设计文档。