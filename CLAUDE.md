# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 项目概述

万向EPB测试系统：一个用于电子驻车制动器(EPB)疲劳测试的Windows桌面应用。系统控制12个EPB卡钳通过液压和电控系统进行自动化循环测试。

## 构建和运行命令

### Visual Studio 解决方案构建
```bash
# 构建整个解决方案
msbuild TfTest.sln /p:Configuration=Debug /p:Platform="Any CPU"

# 构建发布版本
msbuild TfTest.sln /p:Configuration=Release /p:Platform="Any CPU"

# 仅构建主应用程序项目
msbuild MTTfTest\MTTfTest.csproj /p:Configuration=Debug
```

### 运行应用程序
```bash
# 从构建输出目录运行
cd MTTfTest\bin\Debug
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
- 使用`HighPrecisionTimer`进行精确定时
- `ConcurrentDictionary`管理并发状态
- `TaskCompletionSource`实现异步等待

## 测试和调试

### 单元测试
目前项目中未包含专门的测试项目。建议针对核心控制逻辑添加单元测试。

### 硬件依赖
测试时需要连接：
- National Instruments数据采集卡
- 液压控制系统
- EPB卡钳硬件
- 程控电源

## 常见开发任务

### 添加新的EPB控制特性
1. 在`Controller/EpbManager.cs`中扩展管理逻辑
2. 在`Controller/EpbCycleRunner.cs`中实现具体循环控制
3. 更新相应的配置文件结构

### 修改数据采集配置
1. 编辑`Config/AIConfig.xml`添加新通道
2. 在`IO.NI/TwoDeviceAiAcquirer.cs`中更新采集逻辑
3. 在`DataOperation/ClsTestConfig.cs`中添加数据处理

### 界面修改
1. 主界面：`MTTfTest/FrmEpbMainMonitor.cs`
2. 测试设置：`MTTfTest/FrmTestSetting.cs`
3. 数据回放：`MTTfTest/FrmPlayBack.cs`