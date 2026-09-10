# RecoveryGuard 统一物理安全恢复实施记录

日期：2026-09-09。状态：开发中，禁止据此声明现场验收完成或发布可部署包。

## 2026-09-10 独立 Guard 当前源码复核

原会话 77015 已确认原生退出 0，`test-guard-current-review.log` 最终 **101/101**。重复失败场景实际完成 300 次动作失败、3589 步，SimulatedSeconds=215520；这是模拟时钟，不是同等时长真实现场运行。保留人工停止、跨会话迟到动作拒绝等整套结果，不将软件测试通过解释为物理断能/卸压硬件验收、完整安装包交付或 I0050 完成。

重新构建 `Tests/RecoveryGuardTests/RecoveryGuardTests.csproj` Release/AnyCPU，编译日志 `artifacts/guard-physical-safety/build-guard-current-review.log` 为 0 错误。原会话 77015 随后运行独立测试，日志 `test-guard-current-review.log` 本次观察推进至跨进程启动事务、commissioning 授权与期限隔离，仍在运行，尚无最终汇总。没有启动现场 Guard 或执行硬件动作；旧测试通过数字不作为本次终态。

## 目标与授权边界

确认程序真实停滞，排除正常学习、冷却、保压等有进展等待；实际发出断能、卸压，取得动作之后的新鲜电流与压力，全部受影响通道和液压组满足安全阈值并维持规定时长后及时恢复。不能用命令成功、旧采样、UI 心跳或启动成功替代物理安全与真实业务恢复证据。

先本地及授权范围内 JXCQ 验证。wj-epb 部署、停止、切换和真实硬件试验仍等待用户再次明确确认。人工停止优先、实例隔离、数据连续性与正确恢复入口必须保留；I0050 是独立第二步。当前硬件范围不包含 CAN。

## 本次工作树

基线提交：`5d2eb9b273099775d5b19c9d3dfd6d4232b80b1e`。
分支：`codex/guard-physical-safety-recovery`。本记录对应未提交开发修改，不对应正式安装包。

没有修改原冻结候选包；该候选包不包含以下修复，不能用它的既有验证结果证明本修改已验证。

## 已实现的部分

- SafetyAgent 每次未完成的重试重新执行 DO OFF、AO 零输出、电源 OFF，再联合确认电流与压力；不依赖前一次进程留下的动作阶段作为当前物理状态。
- 从不可变 TestConfig/AIConfig 获取启用通道的电流与液压压力映射，按设备共享有限采样任务。每次开启新采样，不把缓存重打时间戳。批次按最大绝对电流、最大压力判断，不用平均数掩盖超限。
- 单调时钟联合窗口拒绝命令前采样、未来时间、重复时间、超龄、缺通道、非有限值、越限及观察间隔过长；中断后重新累计稳定时间，以最慢通道的新证据决定完成。
- 不可变运行快照记录电流阈值；两个主程序快照入口取既有配置与默认 0.1 A 中更严格的值，不放宽配置。
- Handoff 的 `IsSafetyCompleted` 必须同时具有 `CurrentSafe` 和既有安全位。该字段参与监督服务规范化摘要，终态退出证据补充不能改变它。
- 历史 Completed 回执没有电流证据时不会被 SafetyAgent 原地升级或返回成功。无硬件测试代理的模拟证据仅限测试工程，不是物理验收。
- 主程序 StopSafety 已接入动作后的联合确认：复用现有 DAQ，从未经平滑的整批最大绝对电流/最大压力取证。整个批次必须位于安全动作之后；缺通道、超龄、无效质量、饱和输入、换代等不能放行。快速轮询重复缓存不算新证据，任一组越限立即重置稳定窗。
- DAQ 物理证据使用硬件采样时间线的批次首尾时间和代次/序号。回调中不获取 NI Task 切换锁，使用并发更新的代次/序号排序抵御迟到覆盖，读取时再次校验当前代次；旧回调不能覆盖新证据。
- `StopSafetyResult` 新增 `CurrentSafeConfirmed`，物理安全完成和进程内恢复均要求它；Clone 保留该字段。显式重新开始也不再允许丢弃电流、压力物理安全检查。
- 关闭围栏新增 `CurrentSafe`，从真实 StopSafety 结果传播；`IsSafetyTerminal` 要求该位，因此免 Handoff 的 closing 替代恢复路径同样拒绝缺少电流证据的旧记录。
- 协作型存活停滞接管已接入：主程序发布线程在精确匹配 SafeStop 事务后，从后台调用 `StopAllAsync(SystemFault)`，不转换为人工 Stop。动作前后验证安装、授权、Run、配置、Main/Owner 精确身份、会话、接管代次、阶段起点、租约和截止时间；人工停止/暂停和身份变化优先阻止退出。同一阶段只启动一个协作停止任务。
- 协作停止完成后，只有本次请求的新结果同时证明电机断能、电源 OFF、电流和压力安全、已接受数据的持久化边界及 Raw 刷盘且无已知数据缺口，才允许旧主程序自行退出；不要求旧逻辑 owner 全部清场。旧/复用结果、通用超时结果及其他事务结果不放行。该退出不创建启动许可；随后仍走监督服务独立 SafetyAgent、精确退出确认、续测及真实业务提交验证。
- Guard 在观察到精确存活的旧 Main 时返回 `ActiveMainSafeStopPending`，由既有阶段截止约束等待；身份未知仍阻断。不会与存活 Main 并行启动独立 SafetyAgent。
- 修正液压卸压命令假成功：`HydraulicController.ForceReleaseAsync` 及协调器 fallback 不再忽略 DO/AO 的 false/异常，始终尝试两条安全命令，任一未确认则失败。AO 日志明确是压力目标 0，不把标定换算后的电压误写成字面 0 V。

## 已核实的实质缺项

| 范围 | 当前证据与下一步 |
| --- | --- |
| 存活主程序停滞 | 协作停止、自退出和 Guard 有界等待初版已实现并有本地定向覆盖；监督服务 crash safety 仍要求旧 Main 已退出。尚未完成从真实子进程接管到续测提交的整链路/JXCQ 验证，更未完成现场硬件验证。完全无响应而无法取得物理安全证据的实例仍不得先强杀。 |
| Main 接管桥接 | `ApplyTakeoverFence` 已连接精确事务的后台 StopAll 和协作退出策略。仍需验证全局停止已在进行、DAQ/控制线程停滞、重复接管及失败重试等情形。 |
| 其他恢复入口 | 主程序 StopSafety、进程内重启、显式重启历史检查、closing 替代路径已补电流门禁与主程序取证初版；仍须完整回归以及暂停/继续、液压内部恢复等入口的逐项审查。不能据局部修改声明所有恢复路径已完成。 |
| 反馈实现验证 | 纯窗口测试不等于 NI 实际采样验收；仍需配置/校准/硬件异常覆盖、采样资源占用处理和真实动作反馈验证。 |
| 快速恢复剩余约束 | StopAll 的 ForceRelease 已改用 DAQ 原始批次压力证据，不再依赖控制消费线程刷新后的压力值；活动液压代次异常路径及竞争场景仍须核实。持久化本身停滞且无法证明已接受数据安全时，当前协作退出保持阻断；不能声称已实现用户期望的全部“物理安全成立即及时恢复”场景，也不能用丢弃数据冒充完成。 |
| 协议与升级 | 新字段改变规范化摘要。现阶段旧权威回执可能被严格摘要拒绝；必须在完整同版本包、旧实例安全收尾及授权重新建立方案中验证，不允许混装或修改旧回执哈希以放行。 |
| 发布与现场 | 新正式完整包尚未生成；JXCQ 隔离开发测试已通过下述用例，但不是完整包、真实硬件或业务恢复验收。wj-epb 未部署本修改，须由用户决定是否开始部署测试。 |

## 验证记录

本地输出位于 `artifacts/guard-physical-safety/`，不作为源码提交，不把测试成功称为现场验收。

- 联合窗口初版：独立 15 场景通过；首轮完整控制测试 820/820、磁盘 76/76、电源 19/19 通过。
- 加入 CurrentSafe 门禁后的定向测试：监督服务 16/16、安全代理 24/24、Guard 集成 39/39，进程退出码均为 0。
- 随后补充电流证据摘要断言与两个阈值快照入口，控制工程重新编译退出码 0。最新完整回归输出为 `test-current-receipt-full.log`，磁盘和电源为 `test-current-receipt-disk.log`、`test-current-receipt-power.log`；最终结果必须核实进程退出码及日志后补记。
- 最新磁盘 76/76、电源 19/19 已确认退出码 0。完整控制回归仍在运行；另启动 Debug/x86 解决方案构建（`build-solution-current-receipt.log`）以核实主程序快照入口编译，尚待最终退出结果。鉴于解决方案包含测试项目，交付前还须在构建全部结束后进行最终完整测试，不把与构建重叠的这一轮作为冻结构建验收。

### 后续核实与主程序联合反馈修改

- 上述完整控制测试最终 820/820、退出码 0。重叠构建退出码 1，原因是测试 PID 占用 DLL 导致 MSB3021/MSB3027；这是本地执行排序问题，不能记为完整构建通过。
- 等测试退出后，主程序联合反馈版及 closing 电流门禁版分别完成 Debug/x86 全解决方案构建，退出码 0（`build-main-joint-feedback-final.log`、`build-main-closing-current.log`）。
- 联合反馈定向 27/27 通过，StopSafety 单元 12/12、生产链路模拟 11/11 通过，退出码均为 0。模拟液压适配器已明确改为联合电流/压力契约，不代表真实硬件闭环验收。
- 随后将 DAQ 回调发布改为无 Task 切换锁的代次/序号有序更新，并增加迟到覆盖回归；最新构建为 `build-main-physical-final.log`。该最终版本仍须等待构建结束、重新测试并核实结果。
- 该构建现已确认退出码 0；新反馈测试 28/28、监督服务 16/16、关闭安全 7/7 均退出码 0（`test-main-physical-feedback.log`、`test-main-current-host.log`、`test-main-current-closing.log`）。最新磁盘 76/76、电源 19/19 也已通过；完整控制与独立 Guard 测试分别仍需核实 `test-main-current-full.log`、`test-main-current-guard.log` 的终态。此后未进行并发构建。
- 独立 Guard 测试最终 97/97，退出码 0。完整控制测试退出码 1：`BackgroundSoftFlushIsPeriodicAndCoalesced` 在文件可见后立即读取刷新计数得到 0；生产代码 `ExecuteFlush` 在 `store.Flush` 返回后才递增计数，测试观察存在竞争。已改为在原有 3000 ms 内同时等待文件内容和计数增加，保留 1～3 次刷新及 ≥9990 合并数要求，没有修改生产日志逻辑。此轮不记为完整通过；新增 `--project-log` 定向入口以复核，随后须完整重跑。
- 日志测试观察竞争修复后，Debug/x86 全解决方案构建退出码 0（`build-main-current-log-regression.log`），日志定向 21/21、退出码 0（`test-main-current-project-log.log`）。完整回归已在全部构建结束后重新启动，输出 `test-main-current-full-rerun.log`，结果待核实。
- 该完整回归最终 833/833、退出码 0，覆盖的是协作退出接入之前的版本。

### 协作退出与卸压动作证明

- `build-live-stop-final.log`：Debug/x86 全解决方案构建退出码 0。
- `test-live-stop-final.log`：24/24、退出码 0，包含自动来源、逻辑 owner 未清场、旧结果、缺物理/数据证据、人工意图/身份变化、owner 失联，以及 DO/AO 失败不得假成功。
- `test-live-main-action.log`：1/1、退出码 0，精确存活 Main 等待且无 SafetyAgent/启动调用，未知身份阻断。
- `test-live-stop-hydraulic.log`：30/30、退出码 0。
- 本版本完整控制、独立 Guard、磁盘及电源回归输出分别为 `test-live-stop-full.log`、`test-live-stop-guard-full.log`、`test-live-stop-disk.log`、`test-live-stop-power.log`，最终退出结果待核实。上述测试中的停止结果和输出委托是明确的无硬件模拟证据，不作为实际动作及传感器验收。

### 原始压力解耦与最新回归

- StopAll 的卸压等待使用专用原始批次压力读取入口；保留采样时间线的批次起点 UTC 和单调时间，不在读取时重新打时间戳。无效原始证据不能回退到控制缓存中的安全值。
- `build-raw-stop-pressure.log`：Debug/x86 全解决方案构建退出码 0。
- `test-raw-stop-pressure-hydraulic.log`：32/32；`test-raw-stop-pressure-feedback.log`：28/28；均退出码 0。
- `test-raw-stop-pressure-full.log`：859/859、退出码 0；磁盘 `test-raw-stop-pressure-disk.log`：76/76、退出码 0；电源 `test-raw-stop-pressure-power.log`：19/19、退出码 0。
- 上一协作退出版完整控制最终 857/857，独立 Guard 98/98，磁盘 76/76，电源 19/19，均退出码 0。

### JXCQ 隔离开发验证

- 独立 Guard 隔离目录：`D:\EPB_Validation\guard-physical-safety-wip-20260909-114527`。六个测试文件的名称、长度和 SHA256 与本地逐项一致；完整测试 98/98、退出码 0。证据清单为本地 `artifacts/guard-physical-safety/jxcq-guard-20260909-114527-evidence.json`。
- 主程序侧策略隔离目录：`D:\EPB_Validation\main-policy-wip-20260909-115024`。回传日志显示协作停止策略 24/24、反馈窗口 28/28。日志分别为本地 `main-policy-wip-20260909-115024-guard-live-stop-jxcq.log` 和 `main-policy-wip-20260909-115024-safety-feedback-jxcq.log`（位于上述 artifacts 目录）。
- 仅执行无硬件测试入口，没有运行主试验程序，没有替换已有安装、服务、任务或快捷方式。这些未提交 Debug 开发产物不是部署包，不证明 NI 动作、电流/压力实测或续测业务提交成功。

### 本地隔离子进程退出门禁

- 新增三项真实子进程测试：模拟停止证据完整时由策略执行 `Environment.Exit`；缺电流安全证据时不执行退出动作；父进程启动时间身份不匹配时不调用停止。父子进程身份通过真实进程探测核验，父进程检查具体退出码。
- 停止结果和权威状态仍是明确模拟对象。本测试不覆盖生产 Store、真实 StopAll、NI 动作、数据刷盘或重启后的业务提交，不能称为完整恢复验收。
- 直接构建测试 csproj 的 Debug/x86 组合未配置，首次命令失败；改按仓库规定构建 `TfTest.sln` Debug/x86 后退出码 0（`build-live-stop-child-solution.log`）。定向测试 `test-live-stop-child.log` 最终 27/27、退出码 0。
- 已有停止任务与 Guard 新证据契约冲突另见 [衔接审查](2026-09-09_RecoveryGuard已有停止任务衔接审查.md)，仍未修复，不被本定向结果覆盖。

以上为阶段实施记录。完成通知必须等统一恢复路径、回归验证、验收准备与交付状态真实满足要求后再发出。完成通知不等于部署授权；收到用户后续明确确认之前，不进行 wj-epb 部署测试。

### 安全反馈标定一致性修复（后续审查）

发现主程序新加的原始批次安全证据扣除了 `_zeroOffsets`，而独立 SafetyAgent 只使用 AIConfig 持久化标定。该运行时字典由界面 `ZeroEpbChannel` / `ZeroByParamName` 等置零入口写入，可作用于电流和压力，不能作为证明物理安全的标定依据。

已从 `PhysicalSafetyBatchSample.Capture` 移除动态置零参数，并从 DAQ 安全证据生产路径移除该字典读取。安全反馈统一按 `(raw - 零位漂移) * 变换斜率 + 变换截距` 计算；原显示、控制快值和试验数据的动态置零行为未修改。新增测试约束该接口不接受动态置零参数，并核对非零标定结果。传感器真实零位若偏离安全阈值，须校验标定，不能靠界面置零解除安全门禁。

卸压命令另有待审项：主程序 `WritePressure(0)` 经过 AO 标定，独立 SafetyAgent 则写字面 0 V。仓库 AO 配置的负截距使两组主程序零压力命令输出约 0.134 V / 0.152 V；本轮未擅自修改 AO 输出策略，后续需结合已有硬件安全契约核定。两者均须以动作后的实际压力和电流反馈完成安全证明。

本节后的构建、定向回归日志为 `build-safety-calibration-final.log` 和 `test-safety-calibration.log`，完整回归需针对本次新产物重新执行；此前 886/886 等结果属于修复前产物，不能直接沿用。
