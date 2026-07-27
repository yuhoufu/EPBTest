# 万向 EPBTest

万向 EPB 测试系统：Windows 桌面应用（.NET Framework 4.8 + WinForms），用于控制 12 路 EPB 卡钳进行液压/电控自动化疲劳测试，并进行数据采集、落盘与报警联动。

完整的功能、架构、控制时序、配置、数据格式与维护入口说明见：
[docs/项目全功能详解.md](docs/项目全功能详解.md)。

安全、数据一致性与工程稳定性的可执行整改任务、迁移规格和验收矩阵见：
[docs/安全与数据整改实施方案.md](docs/安全与数据整改实施方案.md)。

## 目录速览

- `MTTfTest/`：主界面与交互（`FrmEpbMainMonitor`）。
- `Controller/`：控制编排（`EpbManager`、`EpbCycleRunner`、液压协调）。
- `DataOperation/`：数据处理与落盘（`EpbDiskWriter`）。
- `Config/`：配置加载与运行状态模型（`GlobalConfig`、`EpbTestRecord`）。

## 测试主入口（重要）

本工程“开始试验/批量启动”的控制主入口为：

- `Controller/EpbManager.BatchStart.cs`：`EpbManager.StartBatchSynchronizedAsync(int[] channels, int learnCycles, CancellationToken token)`

UI 侧（WinForms）点击“开始试验”按钮后，会在：

- `MTTfTest/FrmEpbMainMonitor.cs` 中 `await _epb.StartBatchSynchronizedAsync(...)`

> 说明：`Acq_OnEngBatch(...)` / `TwoDeviceAiAcquirer.OnEngBatch` 属于“采集批次→UI 曲线显示”的入口，与“启动测试/批量启动”不是同一条链路。

## 配置文件

配置位于 `MTTfTest/Config/`：

- `TestConfig.xml`：测试参数（周期、目标圈数、电控分组等）。
- `AIConfig.xml` / `DOConfig.xml` / `AOConfig.xml`：NI 采集与输出通道。
- `AlarmConfig.xml`：泓格 M-7055D（RS-485）报警灯/蜂鸣器配置与行为参数。

报警系统设计说明：见 [开发日志/报警系统（泓格M-7055D_RS-485）设计与联调.md](开发日志/报警系统（泓格M-7055D_RS-485）设计与联调.md)

## 日志位置（现场排障常用）

在主界面点击：运行日志 / 警告日志 / 错误日志，会导出到当前运行目录：

- `RunLog.txt`
- `WarnLog.txt`
- `ErrorLog.txt`

## 报警快照导出

报警发生时会导出“当前圈 + 前 N-1 圈”（默认 10 圈），并同时导出所有正在运行的通道用于对比。

目录根：`StoreDir\TestName\AlarmSnapshots\yyyyMMdd_HHmmss-EPBxx\EPBxx(_ALARM)\...`

## 落盘圈状态（重要口径）

`EpbDiskWriter` 的圈级索引（SQLite `cycles` 表）使用 `status` 字段表示圈的最终状态：

- `running`：圈已开始但尚未封圈（用于快照可选包含）。
- `completed`：正常封圈。
- `alarm`：报警触发导致该圈中断封圈（该圈仍计数，且可作为“最近 N 圈”导出）。

> 说明：报警停机必须避免遗留 `running` 悬挂圈，否则会导致“落盘圈号/次数”与 UI 计数漂移。

## RunCount 权威口径

`EpbTestRecord.RunCount` 在界面启动时会从项目目录下的 `index.db` 查询：
`COUNT(status IN ('completed','alarm'))`，确保 UI 与落盘的“累计圈数”始终一致。

- 仅当项目已有 `index.db` 时才回填，避免首次创建项目时把 XML 中的进度意外压成 0。
- 回填完成后立即写回项目 `Config/TestConfig.xml`，所见即所得。无论任意启动/报警，并未完成的圈都不会被计入。
- 这个行为让数据库成为 RunCount 的权威口径；UI 迭代和 XML 保存都依赖此数值，而不再依赖运行中的计数事件。

## 参考文档

- [开发日志/新版“数据落盘逻辑”整合说明（含新增需求）_0916_2.md](开发日志/新版“数据落盘逻辑”整合说明（含新增需求）_0916_2.md)
- [开发日志/报警系统（泓格M-7055D_RS-485）设计与联调.md](开发日志/报警系统（泓格M-7055D_RS-485）设计与联调.md)

