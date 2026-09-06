# EPBTest AI Coding Instructions

## 项目定位
- Windows 桌面应用：.NET Framework 4.8 + WinForms（主程序 `MTTfTest/`），硬件依赖 NI DAQmx。
- Debug 默认 x86；Release 多为 Any CPU（以 `TfTest.sln` 配置为准）。

## 一句话架构（先看入口）
- `Controller/EpbManager.BatchStart.cs`：批量启动主入口 `EpbManager.StartBatchSynchronizedAsync(...)`，负责“压力组锚点 + 组内错峰相位 + 12 路定时器锁相”。
- `Controller/EpbCycleRunner.cs`：单通道一圈的状态机/电流判定（可触发 `AlarmRaised`）。
- `IO.NI/TwoDeviceAiAcquirer.cs`：双设备 AI 连续采集（快/慢快照分离）。
- `DataOperation/EpbDiskWriter.cs`：内存映射 .dat + SQLite `index.db` 的圈级索引与“最新 N 圈”留存。
- 报警硬件（RS-485，非 NI DO）：`Controller/Alarm/AlarmManager.cs` + `M7055dSerialClient.cs`。

## 关键工作流
- 构建：`msbuild TfTest.sln /p:Configuration=Debug /p:Platform=x86`；Release：`/p:Platform="Any CPU"`。
- UI 点击“开始试验”：`MTTfTest/FrmEpbMainMonitor.cs` → `await _epb.StartBatchSynchronizedAsync(...)`。
- 注意链路区分：`TwoDeviceAiAcquirer.OnEngBatch` 主要用于“采集批次→UI 曲线”，不是“批量启动”。
- 现场日志提取阈值记录：`py -3 .\Tools\ThresholdExporter\export_thresholds.py --log .\运行日志 --template <csv> --out <csv>`（见 `Tools/ThresholdExporter/README.md`）。

## 本项目最容易踩坑的约束（务必遵守）
- 定时/同步：正式阶段使用 `HighPrecisionTimer`（`OverrunPolicy.AlignToWallClock`），按“压力组 t0 + k*Period + phase”触发（见 `StartFormalPhaseTimers`）。
- 供电错峰：同电源组通道需相位错峰（`IndexInPowerGroup(ch) * StaggerDeltaMs`），不要改成同刻启动。
- 控制读数 vs UI/统计：控制侧读电流用 fast（未滤波、低时延）；UI/统计用 filtered（见 `TwoDeviceAiAcquirer` 的双快照设计）。
- 封圈一致性：`EpbManager` 在每圈会调用 `Recorder.BeginCycle(...)`，结束必须二选一：
  - 正常：`Recorder.CompleteCycle(ch, cycleNumber, finalN, endUtc)`
  - 报警停机：`Recorder.AlarmCycle(ch, cycleNumber, finalN, endUtc)`（避免遗留 `status='running'` 导致计数漂移）
- RunCount 权威口径来自 `index.db`：只计 `status in ('completed','alarm')`（见仓库 `readMe.md` 的口径说明）。

## 配置与约定
- XML 配置目录：`MTTfTest/Config/`（`TestConfig.xml`、`AIConfig.xml`、`AOConfig.xml`、`DOConfig.xml`、`AlarmConfig.xml`）。
- 日志统一走 `Config/IAppLogger.cs`（常见 tag："EPB"/"液压"/"报警"）。
- 命名：WinForms 用 `Frm*`；老式数据类常见 `Cls*`（多在 `DataOperation/`）。
- 报警快照目录根：`StoreDir\TestName\AlarmSnapshots\yyyyMMdd_HHmmss-EPBxx\...`（现场排障常用）。

## 外部依赖（影响可运行性）
- NI DAQmx 驱动（`NationalInstruments.DAQmx`）+ DevExpress（项目引用），无硬件/无授权时仅能做有限联调。
