# RecoveryGuard 已有停止任务衔接审查

状态：续接初版已接入，生产组合验证尚未完成；不是部署批准或验收报告。

## 后续续接初版

新增 `EpbManager.StopForExternalRecoveryAsync` 并接入 Guard 自动停止委托。入口只接受非空 SystemFault Run；最多加入一次已有停止，随后在停止门锁内校验仍无不同活动运行、旧核心已退出，且保留停止状态与最后结果的 Run/epoch/事务/代次一致。停止后的 WatchdogRunId 为空不再直接判定无原运行，但不能使用任意请求 Run 冒充保留身份。

新事务通过 Controller 内部上下文标志跳过历史物理结果缓存；runner 接受经过入口核对的保留 Run/epoch。生产状态继承同 Run 的受影响通道和不倒退的停止周期范围，重新执行安全阶段、采样及持久化边界，不复制旧物理或数据安全完成位。Guard 自退出前的当前授权、身份、新结果时间与安全门禁保持不变。

该初版仍须生产 Manager 组合测试、已有任务/orphan/新运行竞争覆盖及硬件重建验证。不能将下面历史问题说明全部视为已经关闭，也不能据局部身份断言宣布交接完成。

初版验证：`build-fresh-recovery-stop.log` Debug/x86 解决方案构建退出码 0；`test-fresh-recovery-stop.log` 42/42（新增六项保留身份匹配断言）、既有停止生产链定向 `test-fresh-recovery-stop-production.log` 11/11，均退出码 0。后者未新增完整续接组合用例，不能替代该入口整链路验收。此前阶段投影版完整控制回归最终 `test-projection-policy-full.log` 878/878、退出码 0，不覆盖本次续接修改。

后续已新增真实 Manager 入口组合用例：先完成旧 StopAll、确认活动 RunId 清空，再调用 StopForExternalRecoveryAsync；断言新事务 ID/停止代次变化、原 Run/epoch 不变、原 4/10 通道再次收到 OFF 回执、电源关闭和 Raw 刷盘回调重新执行，且新结果 FullyConfirmed、RawStorageFlushed、无已知数据缺口。随后设置不同活动 Run，断言旧请求被拒绝且无额外电源动作。硬件写入、液压反馈和空数据刷盘边界使用明确测试适配器，未执行 NI 实测或真实运行数据恢复。

该组合版本 `build-retained-stop-production-final.log` 构建退出码 0、`test-retained-stop-production-final.log` 12/12、退出码 0。仍需在途旧任务、orphan 核心、授权变化及实际采样重建组合验证，不能据本用例宣布所有续接场景完成。

在途旧任务组合验证已补充：通过可控液压反馈适配器保持旧 StopAll 未完成，再调用外部恢复入口；确认等待期间停止代次不变、反馈与电源调用均仅一次且接管任务未完成。释放旧反馈后，接管建立新的事务/代次，保持原 Run 并使用新 CorrelationId；两次事务分别执行反馈、电源关闭和 Raw 刷盘，第二次结果 FullyConfirmed。`build-join-active-stop.log` Debug/x86 构建退出码 0，`test-join-active-stop.log` 13/13、退出码 0。反馈适配器仍为模拟，不代表真实 NI 采样释放/重建通过；orphan 及授权变化组合验证仍待补齐。

orphan 拒绝组合验证已补充：让真实 Manager/runner 在模拟反馈尚未返回时通过虚拟时钟超时，核实 HasOrphanCore 为真；调用外部恢复入口，必须报 `RecoveryStopPreviousCoreStillActive`，停止代次、反馈调用及电源调用均不增加。最后释放模拟反馈并确认旧核心退出，不遗留测试后台工作。`build-orphan-stop.log` 构建退出码 0，`test-orphan-stop.log` 14/14、退出码 0。此测试证明拒绝竞争，不证明旧核心稍后退出即可及时重试；同阶段重试调度仍需处理，不能以安全拒绝代替用户要求的及时恢复。

后续同阶段重试调度已接入：Controller 对旧核心未退出抛出专用 `RecoveryStopPendingException`；Guard 仅在该异常且失败阶段 Key 仍等于当前 Key 时释放本阶段尝试标记。既有发布循环下一次检查仍验证精确进程、授权、租约、阶段截止与任务是否已结束；不延长任何截止，不把错误消息字符串当成重试授权，不对一般超时或物理/数据失败盲目重试。

验证新增重试筛选断言，并扩展生产 orphan 用例：释放旧核心后再次调用专用入口，确实建立更高代次的新事务并得到新的 FullyConfirmed/RawStorageFlushed 结果，不复用旧超时结果。`build-pending-stop-retry-final.log` 构建退出码 0；`test-pending-stop-retry.log` 43/43、`test-pending-stop-retry-production.log` 14/14，均退出码 0。当前验证覆盖筛选策略和 Manager 重试，仍需发布线程到实际 Store 的调度组合测试及真实硬件闭环，不能宣称现场自动重试已经验收。

## 已确认的调用契约冲突

2026-09-09 基于当前未提交工作树逐项检查：

| 入口 | 当前行为 | 对自动恢复的影响 |
| --- | --- | --- |
| `Controller/EpbManager.cs` / `StopAllAsync` | 有未完成停止任务时加入原任务；存在 orphan core 时返回原任务 | 返回结果可能属于较早请求，不能归属于 Guard 本次接管 |
| 同入口的历史结果分支 | 无活动批次且满足逻辑清场等条件时返回 `Clone(reused: true)` | 即使历史物理安全成立，也没有本次命令后的新反馈 |
| `MTTfTest/RecoveryGuardLiveStop.cs` / `RunAsync` | 拒绝复用、较早起点、不同来源或 CorrelationId 的停止结果 | 正确拒绝旧证据，但没有取得新证据的后续路径 |
| `MTTfTest/RecoveryGuardRuntime.cs` / `ApplyTakeoverFence` | `_lastAutomaticStop` 相同 Key 时不再调度；Key 包含事务、代次、阶段开始时间 | 同一阶段首次收到旧结果后不能再次尝试，可能等到阶段截止 |
| `Controller/EpbManager.FieldMetrics.cs` / `ShouldStopAcquisitionBeforeFinalPersistence` | 所有停止来源均返回 true | 已停止实例未必还保留可取新物理反馈的 DAQ |
| `Controller/EpbManager.StopSafetyOperations.cs` / `ExecuteStopAcquisitionStage` | 停止采集并固定持久化边界 | 不能简单重新执行联合窗口并期待已经停止的采集提供新样本 |

该结论是静态调用契约证据，不是现场复现。现有 859/859 回归不包含真实已有停止任务到 Guard 退出、再启动及业务提交的完整链路，不能据此否定此缺口。

补充资源检查：`TwoDeviceAiAcquirer.StopDevice` 先将 `_task1/_task2` 置空，再由后台任务执行 NI Stop/Dispose，并等待读取线程与 dispatcher 静默；等待超时会保留 `_quiescingReads`。因此不能只检查任务字段为空就启动独立采样；必须同时证明旧代次 StopTask 和读线程退出，且阻止并发 Start/恢复。

### 已修复：重复停止误报资源释放完成

原 `StopDevice` 没有核实 `_quiescingReads`，重复调用可能因 live task 已为空而直接成功返回。现将旧代次静默检查抽为 `EnsurePreviousReadQuiesced`，由 StartDevice 和 StopDevice 共同调用：读循环已静默、dispatcher 已静默、读线程不存活且 StopTask 成功完成，才移除旧代次记录。未完成、异常、取消或缺少释放任务均拒绝。未改变 NI 释放等待时长，也没有授予独立采样或恢复启动权限。

该修复是安全交接的前置条件，不等于 Guard 已有停止任务的完整续接方案。新增测试包含生产 StopDevice 入口的无硬件隔离调用，验证旧代次记录不会被绕过；其余断言覆盖释放任务状态和读线程状态。

### 双设备停止异常处理

`Stop()` 原来顺序停止 Dev1/Dev2，第一侧抛出异常会跳过第二侧。现仍顺序获取各设备生命周期锁，但通过共同停止方法确保两侧均被尝试。只有一侧异常时保留原异常及堆栈，两侧异常时聚合两条异常；不吞掉超时、不发布成功证据。新增四种失败组合断言调用顺序和异常身份。该修改仍需完成编译及回归，不代表现场双设备停止已验收。

验证：`build-stop-quiescence-final.log` 全解决方案 Debug/x86 构建退出码 0；`test-stop-quiescence-final.log` 联合反馈及资源静默定向 31/31、退出码 0。完整控制、磁盘、电源回归日志为 `test-stop-quiescence-full.log`、`test-stop-quiescence-disk.log`、`test-stop-quiescence-power.log`，电源命令已退出 0，控制与磁盘最终结果待核实；所有日志位于 `artifacts/guard-physical-safety/`，不构成现场硬件验收。

为使后续衔接验证可定位，协作退出现已区分并记录以下拒绝原因，未改变退出安全条件或增加重试权限：

- `PreviousStopResultRequiresFreshEvidence`：缓存结果，仍需新物理证据。
- `StopTransactionMismatch` / `StopEvidenceTimeInvalid`：事务或时间不属于当前请求。
- `CurrentPressureOrOffCommandUnconfirmed`：物理条件未确认。
- `AcceptedDataBoundaryUnconfirmed`：已接受数据边界、Raw 刷盘或连续性未确认。
- `AuthorityInvalidBeforeStop` / `AuthorityChangedAfterStop`：停止前后授权不符合当前接管。
- `ProcessIdentityUnprovenBeforeStop` / `ProcessIdentityUnprovenAfterStop`：精确进程身份未确认。
- `StopResultMissing`：停止没有返回结果。

日志仍沿用现有错误节流；这不是每阶段持久化状态协议，也不代表上述缺口已修复。

本次诊断修改验证：Debug/x86 全解决方案构建退出码 0；协作停止定向 27/27、Guard 运行时定向 39/39，退出码均为 0。输出位于 `artifacts/guard-physical-safety/`：`build-live-stop-reasons.log`、`test-live-stop-reasons.log`、`test-live-stop-reasons-runtime.log`。旧结果、时间无效、物理条件缺失与数据条件缺失用例同时断言拒绝分类；未在 wj-epb 执行。

## 修复需要同时满足的条件

1. 继续保持单个物理停止事务所有者，不能在 orphan core 仍运行时启动竞争 NI 任务。
2. 旧任务的完成只用于判断能否交接资源，不能修改其来源、CorrelationId 或采样时间冒充新证据。
3. 对已停止 DAQ 的实例，明确由谁取得硬件所有权、执行新的断能/卸压并采集新反馈；取证失败仍不允许强杀后宣称安全。
4. 重试必须受原接管租约、阶段截止、人工意图和精确进程身份约束；不能无限循环或延长超时掩盖失败。
5. 受影响范围不能从停止后已清空的活动列表推断为空，必须保留原通道和液压组范围。
6. 数据安全处理与物理取证的职责需明确：不能放宽电流/压力门禁，也不能丢弃未确认数据来宣称恢复完成。

## 必须补充的验证

- 已有任务完成后，新的接管物理证据与旧证据分离，且最终只退出一次。
- 缓存结果、仍运行 orphan、已停 DAQ、重复阶段观察分别覆盖；没有忙循环、并发硬件访问或旧反馈放行。
- 等待期间人工停止/暂停、owner 变化、租约/截止失效均取消退出资格。
- 隔离真实子进程验证停止、退出、独立安全确认和匹配续测入口；再以真实业务提交判断恢复，不以进程出现代替成功。

本项仍阻止“所有恢复条件已统一”的完成声明。wj-epb 保持不变，部署测试仍须用户再次明确确认。

## 后续核实

### 修正：已有只读采样重建入口

进一步追踪 `ConfirmPressureSafeForStopCoreAsync` 发现：压力反馈不可用时，现有实现会先确认唯一的电源 OFF 任务，再通过 `SelectPressureEvidenceRearmChannels` 和 `EnsureChannelsReadyAsync` 重建相关 DAQ 采样，取得新鲜批次后重新执行卸压及联合确认。次数受液压组数量约束，阶段受既有 deadline 约束，并记录真实新序号进展。

因此，本审查此前“不能简单重试已停 DAQ”的结论应精确理解为不能仅复用缓存或假定采样仍活着，**不代表项目完全没有重新取证入口**。应优先复用并验证该已有路径，不能仅据本审查引入新的竞争 NI 任务。

仍需解决的衔接点：

- Manager 的历史结果复用分支与当前阶段仅调度一次的组合仍会阻止新事务进入上述取证入口。
- StopSafety runner 的 RunId provider 当前来自 `_activeBatchId`；旧停止结束并清空活动批次后，重新停止必须明确绑定原 Run，不能产生空 Run 或把新事务归到其他运行。
- 新事务需要保留旧停止的受影响范围，同时重新冻结新增采样的最终数据边界；不能把历史持久化位复制到新采样之后。
- orphan core 未退出仍不能开启第二个停止核心。只读采样重建是否保持输出撤权、是否拒绝未释放的旧代次，以及超时后如何收敛，须通过生产链组合测试验证。

### 原运行身份与范围的实际保留位置

已核实 `WatchdogRunId` 只是 `_activeBatchId` 的属性，不能充当停止后的独立保留身份。`EndBatchSession` 将 `_activeBatchId`、活动链和计划通道清空；Guard 的 `_automaticStop` 当前用 WatchdogRunId 做入口匹配，因此已结束批次会在进入 StopAll 前返回 null。

原 RunId、RunEpoch、停止代次、通道集合和停止周期目前保存在 `StopSafetyProductionState`，StopSafetyResult 也保留原事务身份。新事务的生产状态目前从实时 timers/runners/液压参与者/CTS/周期及仍通电通道重建，不能保证与已经清空的原受影响范围相同。

后续续接实现必须显式核对该保留状态与最后停止结果属于同一 Run 和停止代次，在没有新活动运行、没有旧 orphan 核心且接管仍有效时，才能从此来源建立新事务身份/范围。不能简单将 WatchdogRunId 的空值替换为请求参数，也不能修改这个属性使普通运行观察继续误认已结束批次仍活动。新物理证据、重建采样及新数据边界仍须真正执行。

- 重复停止静默检查版本完整回归最终控制 865/865、磁盘 76/76、电源 19/19，命令退出码均为 0。这一版本尚未包含后续双设备异常聚合修改。
- 双设备异常聚合修改已完成 Debug/x86 全解决方案构建，退出码 0（`build-stop-both-devices.log`）；反馈及资源停止定向 35/35（`test-stop-both-devices-feedback.log`）、协作退出 27/27（`test-stop-both-devices-live-stop.log`），均退出码 0。最新修改完整回归仍待完成。
- 双设备异常聚合版完整回归已启动：磁盘 76/76、电源 19/19，退出码均为 0；控制与独立 Guard 尚在运行，分别输出 `test-stop-both-devices-full.log`、`test-stop-both-devices-guard.log`，不能提前记为通过。测试期间没有并发构建。
- 再次检查停滞判断：`RecoveryDecisionPolicy.EffectiveProgressAnchor` 对未过截止的明确阶段只接受真实采样进展，非等待阶段结合采样及控制/持久化进展；同阶段重复发布不能延长截止。独立 Guard 已有 `BoundedWait` 与 `MovingDeadlineRejected` 用例。这只证明判断策略有对应约束，不证明主程序所有学习/冷却/保压阶段都已正确发布该证据，生产入口映射仍须核实。
- 对 Build-Release、New-FormalRelease7z、New-MTTFTest-RecoveryGuardAutomaticPackage、Test-MTTFTest-RecoveryGuardPackage、Test-MTTFTest-RecoveryGuardInstalledLifecycle 五个 PowerShell 脚本只进行了 Parser 语法检查，均无解析错误；未执行安装/发布操作。语法通过不证明包内容或生命周期验收完成。
