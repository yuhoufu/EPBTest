# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 项目概述

万向EPB测试系统：一个用于电子驻车制动器(EPB)疲劳测试的Windows桌面应用。系统控制12个EPB卡钳通过液压和电控系统进行自动化循环测试。

## 构建和运行命令

### Visual Studio 解决方案构建
```bash
# 构建整个解决方案（Debug配置使用x86平台）
msbuild TfTest.sln /p:Configuration=Debug /p:Platform=x86

# 构建发布版本（Release配置使用Any CPU）
msbuild TfTest.sln /p:Configuration=Release /p:Platform="Any CPU"

# 仅构建主应用程序项目
msbuild MTTfTest\MTTfTest.csproj /p:Configuration=Debug /p:Platform=x86

# 清理构建输出
msbuild TfTest.sln /t:Clean
```

### 运行应用程序
```bash
# 从构建输出目录运行（Debug配置）
cd MTTfTest\bin\Debug
MTTFTest.exe

# 从构建输出目录运行（Release配置）
cd MTTfTest\bin\Release
MTTFTest.exe

# 或者从 Visual Studio 启动 (F5)
```

## 核心架构

### 项目结构
- **MTTfTest**: 主应用程序，包含WinForms界面和核心逻辑
- **Controller**: 控制器层，包含EPB管理器、液压控制器、循环运行器
- **IO.NI**: National Instruments设备I/O封装（AI/AO/DO控制）
- **DataOperation**: 数据处理、配置管理、CAN通信解析
- **Config**: 配置文件读写和数据模型
- **ZlgCanComm**: 周立功CAN卡通信
- **Timing**: 高精度定时器实现
- **Utils**: 通用工具类

### 关键控制逻辑

#### EPB控制架构
- **EpbManager**: 12个卡钳的统一编排管理器，实现同组错峰启动
- **EpbCycleRunner**: 单个EPB的循环运行控制器
- **HydraulicController**: 液压系统控制（2个气缸分别控制1-6和7-12卡钳）
- **HydraulicGroupCoordinator**: 液压组协调器

#### 供电分组策略
- 4个程控电源，每个供3个卡钳
- 同组卡钳启动需要错峰延时
- 不同组卡钳可以同时启动

#### 数据采集与控制
- **TwoDeviceAiAcquirer**: 双设备AI数据采集
- **DoController**: 数字输出控制
- **AoController**: 模拟输出控制
- 实时电流监控和限流保护

### 配置系统
- `Config/TestConfig.xml`: 测试参数配置
- `Config/AIConfig.xml`: 模拟输入通道配置
- `Config/AOConfig.xml`: 模拟输出配置
- `Config/DOConfig.xml`: 数字输出配置
- `Config/UIConfig.xml`: 界面配置

## 开发环境要求

### 必需组件
- .NET Framework 4.8
- Visual Studio 2017+ (支持C# 9.0语法)
- National Instruments DAQmx驱动
- DevExpress v24.2控件库

### 第三方依赖
- **SunnyUI 3.7.0**: 现代UI控件库
- **ZedGraph 5.1.7**: 图表控件
- **DevExpress 24.2**: 企业级控件套件
- **National Instruments**: DAQ硬件驱动

## 代码约定

### 项目特性
- 目标平台：x86 (Debug)，Any CPU (Release)
- 支持不安全代码块 (AllowUnsafeBlocks=true)
- 中文注释和变量名并存

### 关键接口
- `IAppLogger`: 统一日志接口
- `EpbCycleRunner.ReadCurrentDelegate`: 电流读取回调
- `Func<int, double>`: 压力读取委托

### 并发模式
- 使用`HighPrecisionTimer`进行精确定时，支持暂停/恢复/停止操作
- `ConcurrentDictionary`管理并发状态
- `TaskCompletionSource`实现异步等待
- `ManualResetEventSlim`实现暂停门控机制
- 每个EPB使用独立的高精度定时器实现精确循环控制

### 数据落盘机制
- **实时数据流**：AI采集数据实时写入原始文件（.raw格式）
- **缓冲策略**：使用内存缓冲区批量写入，避免频繁I/O操作
- **数据压缩**：支持数据压缩存储以节省磁盘空间
- **异步落盘**：`FlushRawToDiskAsync`方法实现异步数据持久化
- **故障恢复**：系统重启后可从最后保存点恢复数据

## 测试和调试

### 调试配置
- Debug模式自动启用符号调试和详细日志
- 应用程序配置文件：`MTTfTest\App.config`
- 日志级别可在运行时动态调整

### 硬件测试模式
- **仿真模式**：可在无硬件情况下运行，用于界面和逻辑测试
- **硬件模式**：连接实际设备进行完整功能测试

### 单元测试
目前项目中未包含专门的测试项目。建议针对核心控制逻辑添加单元测试。

### 硬件依赖
测试时需要连接：
- National Instruments数据采集卡
- 液压控制系统
- EPB卡钳硬件
- 程控电源

## 常见开发任务

### 开发工作流程
1. **代码修改**：在相应模块中进行功能开发
2. **构建验证**：使用msbuild命令构建项目
3. **配置更新**：如需要，更新XML配置文件
4. **测试验证**：在仿真模式下测试功能逻辑
5. **硬件测试**：连接硬件进行完整功能验证

### 添加新的EPB控制特性
1. 在`Controller/EpbManager.cs`中扩展管理逻辑
2. 在`Controller/EpbCycleRunner.cs`中实现具体循环控制
3. 更新相应的配置文件结构

### 修改数据采集配置
1. 编辑`Config/AIConfig.xml`添加新通道
2. 在`IO.NI/TwoDeviceAiAcquirer.cs`中更新采集逻辑
3. 在`DataOperation/ClsTestConfig.cs`中添加数据处理

### 界面修改
1. **主监控界面**：`MTTfTest/FrmEpbMainMonitor.cs` - 实时状态显示和控制
2. **测试设置界面**：`MTTfTest/FrmTestSetting.cs` - 测试参数配置
3. **数据回放界面**：`MTTfTest/FrmPlayBack.cs` - 历史数据分析
4. **原始数据回放**：`MTTfTest/FrmRawPlayBack.cs` - 原始数据查看

### 调试和故障排除
- **日志文件位置**：应用程序目录下的日志文件夹
- **配置验证**：启动时会自动验证XML配置文件完整性
- **硬件连接检查**：系统启动时检测NI设备和CAN卡连接状态
- **内存监控**：长时间运行时注意监控内存使用情况，特别是数据采集缓冲区