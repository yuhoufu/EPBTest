# EPBTest 项目 AI 编码指南

## 项目概述
万向EPB测试系统：Windows桌面应用，控制12个EPB卡钳通过液压和电控系统进行自动化疲劳测试。基于.NET Framework 4.8 + WinForms + National Instruments DAQmx。

## 核心架构

### 控制层级关系
```
EpbManager (12卡钳编排)
  ├─ EpbCycleRunner × 12 (单卡钳循环控制, 独立高精度定时器)
  ├─ HydraulicController (液压: 2气缸控制1-6/7-12卡钳)
  └─ HydraulicGroupCoordinator (液压组协调)
```

### 关键模块职责
- **Controller/**: `EpbManager`统一编排，`EpbCycleRunner`实现单卡钳状态机和电流判断逻辑
- **IO.NI/**: `TwoDeviceAiAcquirer`双设备AI采集，`DoController`数字输出，`AoController`模拟输出
- **（新增）报警子系统**：泓格 M-7055D 通过 RS-485 控制报警灯/蜂鸣器（不走 `DoController`）
- **DataOperation/**: 数据处理、CAN解析（`ClsDbcParser`）、落盘（`EpbDiskWriter`）
- **Config/**: XML配置加载，`GlobalConfig`聚合所有配置，`EpbTestRecord`单卡钳状态
- **MTTfTest/**: WinForms界面，`FrmEpbMainMonitor`主监控，`FrmTestSetting`参数设置

### 供电分组策略（重要）
- 4个程控电源，每个供3个卡钳
- **同组卡钳启动需要错峰延时**，不同组可同时启动
- 参见 `EpbManager.BatchStart.cs` 中的错峰实现

## 构建命令
```powershell
# Debug构建 (x86平台)
msbuild TfTest.sln /p:Configuration=Debug /p:Platform=x86

# Release构建
msbuild TfTest.sln /p:Configuration=Release /p:Platform="Any CPU"
```

## 代码规范

### 文档注释（新增）
- **落地代码要求**：所有新增/修改的业务方法必须写“详细的 XML 文档注释”，至少包含：`<summary>`、`<param>`、`<returns>`（有返回值时）、必要时补充 `<remarks>`（说明触发条件/线程模型/异常/边界）。
- 目标：让后续联调与现场问题复盘时，能直接从方法注释定位“为什么这么做/怎么用/注意什么”。

### 命名与结构
- 类名前缀`Cls`表示旧式数据操作类（如`ClsDataFilter`、`ClsDiskProc`）
- 配置类在`Config/`，模型类在`Config/Models/`
- 界面代码使用`Frm`前缀

### 并发模式
- `HighPrecisionTimer`（Timing/）用于精确循环控制
- `ConcurrentDictionary`管理并发状态
- `TaskCompletionSource`实现异步等待
- `ManualResetEventSlim`实现暂停门控

### 日志接口
统一使用`IAppLogger`（定义在Config/），示例：
```csharp
_log.Info($"EPB[{_channel}] 正向完成", "电控");
_log.Error($"液压[{hydId}] 超时", "液压");
```

### 电流读取模式
- **控制用**：`TwoDeviceAiAcquirer.ReadCurrentFast()`，低时延未滤波
- **UI/统计**：`ReadCurrentFiltered()`，滤波后数据

## 配置文件结构
XML配置位于`MTTfTest/Config/`：
- `TestConfig.xml` - 测试参数（周期、目标圈数、电控分组）
- `AIConfig.xml` - 模拟输入通道定义
- `DOConfig.xml` - 数字输出（EPB正/反向、液压开关）
- `AOConfig.xml` - 模拟输出（液压压力设定）

报警相关（新增，独立于 NI/DO）：
- `MTTfTest/Config/AlarmConfig.xml` - 泓格 M-7055D 串口参数、EPB→DO 映射、单点/全关指令帧（含 CRC）
- 设计与联调说明：`开发日志/报警系统（泓格M-7055D_RS-485）设计与联调.md`

报警触发数据快照（新增需求）：
- 报警发生时立即导出“当前圈 + 前 9 圈”，并同时导出所有正在运行通道
- 目录根：`StoreDir\TestName\AlarmSnapshots\yyyyMMdd_HHmmss-EPBxx\EPBxx(_ALARM)\...`

## 第三方依赖
- **NationalInstruments.DAQmx** - 必须安装NI驱动
- **DevExpress v24.2** - 企业控件（需授权）
- **SunnyUI 3.7.0** - 现代UI控件
- **ZedGraph 5.1.7** - 图表绘制

## 调试技巧
- 仿真模式：无硬件时可运行界面和逻辑测试
- 日志文件：应用目录下的日志文件夹
- 内存监控：长时间运行注意`TwoDeviceAiAcquirer`缓冲区

## 开发日志
项目变更记录在`开发日志/`目录，包含架构决策和需求演变说明。
