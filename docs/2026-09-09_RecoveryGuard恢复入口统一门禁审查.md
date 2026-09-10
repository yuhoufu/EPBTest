# RecoveryGuard 恢复入口统一门禁审查

## 当前结论

统一安全反馈已经接入全局 StopAll 与独立 SafetyAgent，但尚未覆盖全部进程内恢复入口。不得把 Guard 策略测试通过表述为“所有恢复条件已统一”。本记录依据当前工作树源码，不代表现场硬件验收。

用户要求的共同条件是：确认真实停滞，排除正常学习、冷却、保压等待；实际发出断能及卸压，取得命令之后的新鲜电流和压力，覆盖全部受影响通道/液压组并稳定满足安全阈值；安全后及时完成旧实例收敛、匹配入口续测，以业务提交验证恢复。命令返回成功不能替代反馈，界面置零不能替代物理安全。

## 已核查入口及缺口

| 入口 | 当前源码证据 | 待处理项 |
| --- | --- | --- |
| 外部 Guard 协作停止 | `RecoveryGuardRuntime` → `StopForExternalRecoveryAsync` → StopAll → `ConfirmJointPhysicalSafetyForStopAsync` | 已接联合安全窗口；完整 Store/退出/独立安全/重启/业务提交仍待验证 |
| 独立 SafetyAgent | `ProductionSafetyHardware` 联合确认；`SafetyPhysicalFeedbackProbe` 新建有限采样任务 | 使用持久化标定；真实硬件占用、断连、采样验收待完成 |
| 批次继续 `ResumeBatchAsync` | 等待 DAQ 恢复、配置/模型/数据/外部准入检查后 `EnsurePowerSupplyReadyBeforeStartAsync` | 该入口未显式调用统一动作后联合窗口；不能只依赖历史暂停完成 |
| 液压报警组人工继续 `ResumeHydraulicAlarmGroupAsync` | DAQ 预检后上电，再预释放定位与资格复核 | 需在第一次可能上电动作之前完成受影响组统一安全证明，不能用上电后资格复核代替 |
| 暂停 `CompletePauseSafetyBoundaryAsync` | EPB OFF、液压释放、DisableAll、Raw/SQLite 边界 | 当前阶段没有显式动作后的联合电流/压力窗口；后续继续不能把这份历史状态直接当新证据 |
| 受影响组自动恢复 `RecoveryCoordination` | 只读 DAQ rearm 后 ForceRelease，再耐久清场与 rejoin | 目前 ForceRelease 调用默认压力读取；需继续核对联合电流窗口、反馈原始时间和实际 rejoin 上电位置 |

此表是已检查的入口，不是完整枚举；DAQ 重建、启动续测和其他局部恢复仍须逐项查证。备份 `.cs` 不作为生产调用链证据。

## 实施约束

1. 从全局停止的联合窗口提取受影响范围、命令完成时刻、取消及所有权检查，不能直接调用全局 StopAll 来替代局部恢复；否则会扩大现场停机范围。
2. 每个入口在实际 OFF/卸压任务完成后开启新窗口，在首次允许上电前再次核验运行身份和取消状态。
3. 液压组、电源组共享范围需要闭包展开，不能只校验触发故障的单通道；也不能无依据停止不相关组。
4. 原始批次反馈使用持久化标定，不使用动态置零偏移。保留现有采样时间、代次、序号、防重放与稳定时长要求。
5. 不绕过原有数据完整性、配置匹配或独占执行条件；同时不得让重复发布进展延长硬截止。
6. 逐入口增加真实生产路径的无硬件故障注入测试，再做本地完整回归和 JXCQ 同产物验证。硬件证明及正式包验收另列，wj-epb 部署须用户新确认。

## AO 命令差异的已有契约

`docs/02_Issues/2026-08-31_V2.13.0.43_两项残余问题彻底修复实施及验收.md` 第 3.4 节明确独立安全程序集 AO 写字面 `0.0 V`，不经压力标定；对应 `V213043RecoveryTests` 已有契约测试。因此本轮不把 SafetyAgent 改成业务压力换算来追求表面一致。

主程序 `WritePressure(0)` 是标定后的零压力命令，不应在日志或验收中称作字面零电压。两种命令策略的现场等效性尚无硬件证据；共同恢复放行必须依赖命令后的实际电流和压力，不能以命令数值相同作为验收目标。

## 受影响组 OFF 失败门禁修复中

在 `EpbManager.RecoveryCoordination.cs` 受影响组清场路径发现 `try { CommandEpbOffHighPriority(...) } catch { }` 同时忽略 false 和异常。现改为逐通道收集失败，仍继续尝试该路径的电源 OFF、液压释放；在耐久收敛和 rejoin 之前拒绝继续。已有异常分支再次尝试 EPB OFF 并发布隔离故障，不把失败标记为恢复成功。

该改动尚待构建及生产路径故障注入回归，不证明联合反馈入口改造完成。当前仍在运行修复前产物的 Guard 回归，避免在测试占用期间重建覆盖产物。磁盘回归已终态 76/76、退出码 0；六通道负载隔离复测等待其他测试终态后执行，不能提前归因为并行负载。

后续验证：原 Guard 和隔离写盘复测结束后，`build-recovery-off-gate.log` 完整 Debug/x86 构建与 `test-recovery-off-gate.log` 恢复协调定向测试均退出码 0，49/49。新增用例使用生产的 `CollectRecoveryOffFailure`，验证 false、抛异常、成功、缺失命令四种情况，不中断后续命令且保留失败。这是命令结果收集层测试，尚不是完整受影响组 NI/电源/卸压/rejoin 故障注入验收。新产物已启动单独完整回归 `test-recovery-off-gate-full-serial.log`，期间不并行运行其他测试或构建。

## 局部联合反馈入口提取与接入

从全局停止提取 `ConfirmJointPhysicalSafetyAsync(channels, hydraulics, authorityCurrent, token)`：只读取现有 DAQ 原始批次，不打开额外 NI Task、不执行全局停止。原 StopAll 委托该入口，保留停止代次核验；局部调用可提供运行身份/执行许可核验和取消令牌。命令之后重新建立稳定窗口，反馈阈值与防重放逻辑不变。

受影响液压组清场在 OFF 结果检查、电源 OFF 和 ForceRelease 后调用该入口；缺少电源协调器或唯一启用液压组配置时拒绝。窗口结束前再次核验 Run/Epoch/执行许可，失败不得进入后续 rejoin。此改动尚未覆盖人工继续等其余入口，也尚未证明受影响组配置闭包和整条生产路径故障注入均完成。

`build-scoped-physical-safety.log` 完整 Debug/x86 构建退出码 0；三个相关定向测试按顺序运行，日志前缀 `test-scoped-`，结果待终态。先前串行完整回归终态退出码 1，失败于 `WatchdogJournalStorageTests.EmergencySpoolIsBoundedAndReplayed` 的 10 秒 Flush（“故障盘写入未转入应急缓冲”），不是全套通过。需另查应急缓冲失败，不能将其忽略或延长测试阈值来过关。

## 批次继续接入（待生产路径验收）

`ResumeBatchAsync` 在既有外部准入之后、首次电源上电之前调用新的 `ConfirmBatchResumePhysicalSafetyAsync`：重新提交启用/冻结通道 EPB OFF、全局电源 OFF、启用液压组 ForceRelease，然后建立新的联合电流/压力窗口。全局断电范围沿用批次暂停已有 DisableAll 边界，不将此方法用于局部液压组恢复。

EPB false/异常、电源或卸压失败均保留，仍尝试其余安全命令，最终聚合失败不得上电。每次安全动作前核验暂停预检身份；联合窗口和窗口后、上电前再次核验身份及取消。该实现不复用历史暂停样本，也不增加“暂停久了必须重新学习”的资格要求。

初次 `build-batch-resume-safety.log` 构建退出码 0；补全逐阶段身份检查后启动 `build-batch-resume-safety-final.log` 及串行 `test-batch-resume-safety-full.log`。新入口的完整无硬件生产路径故障注入仍待补充，现有全套测试并不自动证明它被覆盖；人工液压组继续等入口也仍未完成。

## 人工液压组继续接入（尚未构建验证）

已在 `ResumeHydraulicAlarmGroupAsync` 首次上电前接入局部安全命令和联合反馈。范围检查要求该液压组、电源组全部启用成员均在当前恢复集合中；否则不能假设自己有权关闭共享输出。首次范围检查位于进入原失败回退逻辑之前，以免“拒绝恢复”分支本身 ForceRelease 波及其他通道。

范围成立后，逐通道 EPB OFF、逐电源组 OFF、指定液压组 ForceRelease；命令失败保留且不允许上电，联合窗口持续校验当前 Run/Epoch 和恢复取消令牌，窗口后再次核验。未扩展到全局 DisableAll，也未改变正常运行组。

当前共享范围不完整时明确拒绝局部恢复，不等于该类拓扑已支持自动恢复。后续仍需确认配置是否存在跨组共享并设计对应的完整所有权取得，不能将此保护性拒绝算作所有恢复入口完成。新代码等待正在运行的上一产物完整回归终态后构建；生产路径测试、配置变化/共享成员/旧运行迟到回退的故障注入仍待完成。

### 旧运行迟到回退隔离

进一步发现原人工液压恢复 catch 分支没有 Run/Epoch 核验，会执行 ForceRelease、EPB OFF 并用当前 `_activeBatchId` 发布报警。现将入口 Run/Epoch 冻结于 try 外，获取液压所有权后先校验；回退之前以及逐通道回退前要求同一 Run/Epoch 且液压所有权未取消，状态关联使用冻结 RunId。旧身份不再执行回退，保留原异常由调用方处理。

新增 `CanRollbackHydraulicResume` 门禁单元用例覆盖当前身份允许、不同 Run、不同 Epoch、无所有权、空 Run 和零 Epoch 拒绝。此单元测试尚未执行，也不替代真实异步换代间隙的生产链验证；当前仍等待上一完整回归终态，未并行构建。

### 后续验证与命令顺序测试

上一产物 `test-batch-resume-safety-full.log` 串行完整回归终态退出码 0，888/888。人工液压恢复新代码随后构建成功，`test-hydraulic-resume-lifecycle.log` 15/15、退出码 0；这些结果的产物边界不同，不能合并宣称最新产物全套通过。

覆盖检索未找到直接调用 `ResumeBatchAsync` 或 `ResumeInfrastructureAlarmGroupsAsync` 的现有测试。现从批次继续提取实际使用的 `RunRecoverySafetyBoundaryAsync`，注入安全命令、所有权校验及联合反馈委托；保留逐命令身份检查、失败后继续尝试安全命令、反馈前拒绝命令失败、反馈后再次验证身份的顺序。

新增命令执行层测试：断能抛错仍尝试卸压且不进入反馈；断能完成后身份失效不执行后续输出；反馈 false 拒绝、true 才完成，均验证调用顺序。这覆盖批次继续使用的生产命令执行器，不是完整 Manager/硬件/续测提交端到端测试。`build-resume-command-boundary.log` 与 `test-resume-command-boundary.log` 正在执行，结果待终态；人工液压分支尚未复用该执行器。

后续：批次继续命令执行器构建和恢复协调测试终态退出码 0，50/50。人工液压组继续随后也改为调用同一执行器，保留局部组范围验证；新增反馈已成功返回但所有权随即失效时仍必须拒绝的测试分支。`build-unified-resume-boundary.log`、`test-unified-resume-boundary.log` 终态退出码 0，恢复协调 50/50（在已有测试内增加矩阵分支，不增加计数）。最新产物开始串行完整回归 `test-unified-resume-full.log`，结果待核实。命令执行器覆盖不代表所有 Manager 分支或真实硬件验收完成。

## 后续自动恢复路径核查

- `EpbManager.cs` 液压软件自愈：`TryEnsureSoftwareRecoveryOutputOff` 成功仅表示 `CommandEpbOffSafetyImmediate` 返回 true；失败才触发电源组应急关闭。后续 `HydraulicForceRelease` 使用默认压力读取，再耐久封圈、`EnsureMotorReleasedBeforeFormalRejoinAsync`、重入。该段未调用新联合反馈入口，也未在成功路径统一执行电源 OFF。
- `RecoveryCoordination` 的 `HandleActiveCycleDataLimitExceededBodyAsync`：单通道暂停、EPB OFF 忽略 false/异常、封圈后重置整个液压组，再按单通道机械预释放及重入。当前不能证明组内其他成员已断能，也没有新联合电流/压力窗口。现有“容量上限不触发 DAQ 重建”测试不能证明物理安全条件已统一。
- `EnsureMotorReleasedBeforeFormalRejoinAsync` 捕获执行许可并等待旧圈退出，继而执行 `PreReleaseBatchWithPlanAsync`。新增电源 OFF 时需要同时证明后续供电重启在安全证明之后、并受同一身份/许可控制；不能直接替换 ForceRelease 就假设供电状态仍满足原调用方预期。

这两项尚未修改，须按共享范围及完整断能→反馈→供电→业务重入链实现和验证；不将保护性拒绝或单通道反馈充当完整组恢复支持。当前最新串行完整回归仍在原会话观察，不重复启动。

## 用户更新后恢复工作

用户明确“继续”后检查：暂停前完整回归日志 `test-unified-resume-full.log` 末尾为 890/890；旧会话句柄已不存在，未发现 AdaptiveControlTests、RecoveryGuardTests 或 MSBuild 进程。未取得原会话退出码，不补造该证据，也未重复启动原测试。

液压软件自愈已将原 ForceRelease 阶段接入局部安全命令及联合反馈；在数据边界成立、非暂停且有可重入通道时，先经有界 `HydraulicRecoveryPowerReady` 调用现有供电准备，再机械预释放、重入。供电准备返回硬件隔离通道时不将它们当作已正常恢复。暂停分支保持电源 OFF，不额外上电。

恢复工作时删除了该路径在统一执行器之前重复的 EPB OFF 提前失败段，避免 OFF 失败中断后续电源 OFF/卸压尝试。初次构建和定向测试命令终态退出码 0；该删除后的最终构建/定向验证日志为 `build-auto-hydraulic-safety-final.log`、`test-auto-hydraulic-coordination-final.log`，结果待终态。

活动圈容量上限恢复仍未统一；共享范围不完整拓扑、自动液压恢复整条生产链故障注入及所有新增路径完整验收仍待完成。上述代码修改不是现场部署，wj-epb 未改动。

后续终态：`build-auto-hydraulic-safety-final.log` 与 `test-auto-hydraulic-coordination-final.log` 命令退出码 0，恢复协调 50/50。JXCQ 增量工具增加可选 `IncludeRecoveryResume`，在既有三组之外运行恢复协调和生命周期测试；已启动同产物远端验证 `test-jxcq-unified-recovery-resume.log`，结果待核实。

活动圈上限路径先修正明确的假成功：使用已测试的 `CollectRecoveryOffFailure` 保留 OFF 的 false/异常，尝试既有 ForceRelease 后若仍存在命令失败，不允许继续机械预释放及 rejoin。该小改动尚未构建，且不代表整组电源断能、联合反馈及数据清场顺序已统一；JXCQ 此次产物不包含此后源码变更。

后续 `build-active-cycle-off-evidence.log` 构建终态退出码 0。另发现活动圈恢复异常分支同样存在旧运行迟到 OFF/状态写入风险，已冻结 RunId/Epoch，在异常回退 OFF 前及状态发布前复用所有权门禁核验，旧运行或已失去液压所有权时不再操作。`build-active-cycle-rollback-scope.log` 及 `test-active-cycle-rollback-scope.log` 正在构建/验证；当前门禁单元测试不等于完整活动圈生产路径已验收。

## 本次继续后的核验

- 活动圈旧运行回退修改后的生命周期测试 15/15、构建退出码 0；串行完整回归 `test-active-cycle-safety-full.log` 为 890/890，本次取得进程终态退出码 0。
- 受影响组重置路径实际已有 `AffectedGroupPowerEnable`：联合物理安全通过、耐久数据边界闭合后，才调用 `PrepareAndEnableAsync`，再进入机械预释放。此前“是否缺少恢复送电”的疑问已由源码排除；共享电气/液压范围覆盖和送电后失败清场仍需验证。
- 新增异步安全边界回归，使用受控任务验证 OFF 未完成时不能进入后续命令/反馈，以及在 OFF 或反馈等待期间撤销所有权后不能放行。该测试覆盖公共执行器，不等于活动圈或整组恢复生产链验收。
- 活动圈路径仍是单通道封圈配整组卸压，未完成全部恢复入口统一；不能把上述完整回归结果称为可部署版本。wj-epb 未部署、未切换，部署仍须用户确认。

异步执行器新增测试构建成功，`test-async-boundary.log` 为 51/51，命令退出码 0。

继续审查活动圈任务身份时发现：登记函数已捕获 RunId/Epoch，但执行体会在延后启动时重新读取当前身份。现已将登记身份显式传入执行体，在入口及取得液压所有权后校验，避免旧登记任务自行采用继任批次身份。`build-active-cycle-frozen-identity.log` 构建成功，`test-active-cycle-frozen-identity.log` 生命周期测试 15/15，命令退出码 0；尚未针对排队换批运行完整生产故障注入，也未重跑此修改后的全部测试。

下一项实现需要同时改变活动圈 incident 的受影响通道契约与清场范围；仅替换单通道 ForceRelease 不足以完成整组恢复。现有封圈调用还忽略返回值，且耐久等待在卸压之前，必须在整组重构中明确失败处理及安全命令先后，不能通过延长超时掩盖。

## 活动圈卸压与耐久等待顺序修正

已将活动圈 `ForceReleaseAsync` 移到 `TryFinalizeSoftwareRecoveryCyclesAfterDurableCutoffAsync` 等待之前，不再因耐久前缀等待拖延卸压。保存封圈返回值：即使封圈返回 false，也先尝试卸压，随后拒绝继续恢复；OFF 失败同样不放行。封圈与耐久提交均增加冻结 RunId/Epoch 和液压 lease 取消状态的写入许可检查。

`build-active-cycle-release-order.log` 构建成功，`test-active-cycle-release-order.log` 恢复协调 51/51，退出码 0。已启动串行完整控制回归→磁盘测试→电源测试，日志前缀 `test-active-cycle-release-order-`，结果待进程终态，未启动重复构建或并行测试。

边界：本次仅消除了明确的异步耐久等待前置。`EpbDiskWriter.SealCycleWindowAtDaqBoundary` 仍先获取 `state.Gate`；尚不能证明锁等待有界，不能据此声称卸压绝不被封圈拖延。整组断能/反馈及任务范围统一仍未完成；此次测试也未覆盖真实硬件。没有现场部署。

后续源码已将记录器封圈本身移至 ForceRelease 之后：在卸压前仅通过 `CaptureCycleDaqBoundaries` 冻结原截止序号/代次，卸压后把该快照显式传给封圈，避免把卸压期间的新序号作为截止边界。保留原 `update.TimestampUtc`。这消除了该路径调用记录器 `state.Gate` 先于卸压的问题，但不声称其他调用无阻塞，也尚未解决整组恢复范围。

当前串行回归仍使用上一构建产物运行，未覆盖这次封圈移动。为避免覆盖活动测试产物，最新源码尚未重新构建；须在原会话终态后验证。进程尚存活时不重复启动测试。

进一步检查复用入口：`ExecuteAffectedGroupResetIncidentAsync` 当前用传入通道登记不可变 incident 契约，但 `ExecuteAffectedGroupResetAsync` 在登记之后会拼接 timers/runners/hydraulicParticipants 的同组通道。这意味着直接把活动圈单通道事件转交该入口，仍不能证明扩大的通道范围已被原 incident 覆盖。整组实现须先冻结完整范围、用该范围登记任务，再由执行体遵循同一范围；不能仅更换调用目标即宣称所有权覆盖已解决。

### 清场执行体调用方覆盖检查

源码检索确认以下调用路径，不能仅改一个 wrapper 后删除执行体扩组：

| 调用路径 | 当前登记/范围入口 | 待统一事项 |
| --- | --- | --- |
| TimerRuntimeHealth | `ExecuteAffectedGroupResetIncidentAsync` | 登记之前冻结整组范围 |
| 基础设施自愈重试 | 直接调用执行体，传入 eligible | 核对既有任务契约与扩大范围一致 |
| 液压软件恢复硬期限 | 释放旧液压 lease 后直接调用执行体 | 核对任务交接与同组完整范围 |
| 电源软件恢复硬期限 | 释放各旧液压 lease 后直接调用执行体 | 核对跨液压组的电气共享范围 |
| 单通道报警恢复硬期限 | 释放恢复 gate/lease 后以单通道直接调用 | 不可把单通道任务当作全组授权 |

本轮尚未改动上述调用关系。原串行回归会话仍存活且日志推进，继续观察同一会话，不按观察等待超时认定停滞或重启。

后续原串行回归终态退出码 0：控制 891/891、磁盘 76/76、电源 19/19。该产物不包含后续封圈后移与登记范围修改。

`ExecuteAffectedGroupResetIncidentAsync` 已在登记前按现有执行体同样的运行成员来源及筛选条件扩展当前液压组通道，使用这一快照登记、执行和终态释放。此修改覆盖 Timer 调用的 wrapper，其他直接调用方以及登记后新成员加入导致的动态扩组问题尚未解决，不能称为完整范围授权修复。

原测试结束后启动 `build-seal-after-release-cohort.log` / `test-seal-after-release-cohort.log` 验证封圈后移及 wrapper 快照修改；终态待核实。

上述构建及恢复协调验证终态退出码 0，51/51。登记接口审查确认 `TryBeginRecoveryIncident` 在 admission gate 内校验运行身份及送电撤销状态，通过 coordinator 创建不可变契约；现有接口不支持直接修改已登记范围。后续不能以修改列表绕过范围交接。

已启动同产物 JXCQ 五组隔离测试，日志 `test-jxcq-seal-after-release-cohort.log`，种子为此前已验证的 `policy-incremental-9126f4f086c04ab9ab0ff8da4e2b3346`。本次仅复制隔离测试文件、逐文件校验并执行测试，不安装主程序，不改服务或桌面入口。远端终态待核实，期间不覆盖本地产物。

远端终态已确认退出码 0，五组均通过，详见 JXCQ 增量记录。进一步源码证据：`RecoveryIncidentCoordinator.TryBegin` 对不同范围但通道重叠的已有任务，在 Reserve/OFF/发布之前直接返回 Rejected；`CompleteAfterTerminal` 走 coordinator 的终态提交机制。因此不能在原任务执行体内无条件调用登记 wrapper 来扩组，否则会触发重叠拒绝，而非完成交接。必须先明确旧执行体退出/终态与新任务接管的顺序，并验证拒绝窗口中的安全输出保持。

已扩展 `RecoveryProductionSeamTests.RecoveryChannelOverlapIsAtomic`：等待旧 worker 结束、registry/contract 均清除后，验证覆盖两通道的新任务可以登记；再调用旧 incident 的终态回调，断言回调不执行且新登记仍存在。该测试针对真实 coordinator 与测试端口，不等同于 Manager 五条恢复路径已完成交接。构建/测试日志 `build-cohort-handoff-seam.log`、`test-cohort-handoff-seam.log`，终态待核实。

上述构建成功，恢复生产接口测试 32/32，终态退出码 0。

活动圈有效事件入口已改为登记前冻结当前同液压组运行成员，登记 `AffectedGroupRecovery` 契约，并调用已有 `ExecuteAffectedGroupResetAsync`。登记前与 worker 进入时均校验触发通道的圈号，执行体继续使用登记时冻结的 RunId/Epoch。有效事件不再走单通道清场/整组卸压的旧执行体；旧方法目前仅由无效通道分支调用，尚待删除其不可达的有效通道实现，避免维护歧义。

此路由接入已有整组电源 OFF、卸压、联合物理反馈和耐久收口，但不代表已完成：共用执行体仍有封圈先于安全动作、登记后动态扩组及跨电气共享范围待修正；需要实际 Manager 故障注入确认容量上限恢复与正常 DAQ 代次保留。构建/定向验证日志 `build-active-cycle-group-route.log`、`test-active-cycle-group-route.log`，本次改动不在上一 JXCQ 验证产物中。

上述活动圈整组路由构建成功，恢复协调 51/51，命令退出码 0。

共用 `ExecuteAffectedGroupResetAsync` 已把记录器封圈移到断能、卸压及联合物理反馈通过之后；清场前冻结 `cutoffUtc`、圈号和 DAQ 边界，封圈显式使用该边界，避免只读采样重启后采用新代次截止。封圈许可同时检查 RunId/Epoch、执行凭证及恢复取消状态；失败不进入耐久提交或送电。构建/定向测试日志为 `build-group-seal-after-safety.log`、`test-group-seal-after-safety.log`，终态待核实。电源异常提前退出、采样恢复先于卸压、跨组范围与真实生产故障注入仍需后续处理。

上述构建及恢复协调终态退出码 0，51/51。

共用整组断能循环已在运行身份、执行凭证及恢复取消状态仍有效时收集电源 OFF 异常，继续尝试后续电源组和原后续安全流程；异常汇总为 `AffectedGroupSafeCommandsUnconfirmed`，不允许后续送电。撤权/换批不走该继续分支。`build-group-power-off-failure.log`、`test-group-power-off-failure.log` 正在验证。尚未解决 DAQ 预检失败先于卸压退出，故此项不能作为“任何断能失败都已确保卸压”的验收证据。

上述电源异常修改构建成功，恢复协调 51/51，退出码 0。

共用清场在采样恢复之前新增 `AffectedGroupReleaseOutputBeforeSensing`，调用协调器 `IssueRecoveryReleaseOutputAsync`，只执行已有 DO/AO 卸压输出，不读取压力、不声明代次退休或物理安全成立。输出失败保留到命令失败集合；后续仍执行原 ForceRelease 和联合反馈门禁。该修改构建成功，恢复协调 51/51，日志 `build-release-before-sensing.log`、`test-release-before-sensing.log`。

补充测试验证压力读取抛异常时仍能独立发出输出，且输出异常原样传播；日志 `build-release-output-regression.log`、`test-release-output-regression.log`，运行 `--incident-10358-029`，终态待核实。仍需核对旧液压代次异步输出与该早期输出的并发，以及全部受影响资源范围；新接口不能作为独立的安全确认或重启许可。

新增测试首次构建失败：遗漏 NewCoordinator 必填 stableMs/timeoutMs；已补齐。修正构建成功，但 `test-release-output-regression-fixed.log` 组合测试退出码 1，先在 `RecoveryHardeningTests.RecoveryBudgetBoundaryIgnoresTokenChange` 的固定两次预算断言失败，未到液压新增测试。当前源码 `RecoveryFailurePolicy.Classify` 使用 `ContinuousRecoveryPolicy.MaximumRestarts`，其值为 3；此为已定位的不一致，不把失败归因于机器负载，也不直接改断言数字冒充语义验证。

随后同一构建单独运行 `--hydraulic-coordination`：`test-release-output-hydraulic.log` 为 33/33、退出码 0，包含新增输出不读取压力、失败原样传播测试。组合回归失败仍未关闭；连续恢复预算窗口及旧测试语义待单独核对。

预算源码核对：`ContinuousRecoveryPolicy` 是 10 分钟/3 次滚动限频，`NextAllowedUtc` 在窗口到期时释放额度；`RecoveryFailurePolicy.Evaluate` 则仍有 alreadyBlocked、samePointBlocked、budgetExhausted 的持久阻塞语义，两者不能混为一个门禁。测试移除过时固定数字 2，改为检查分类额度与共享策略一致，并新增满额等待窗口、窗口准确到期放行的边界检查；原指纹/进度令牌预算断言保留，不改生产策略来适配测试。

`build-budget-window-regression.log`、`test-budget-window-regression.log` 正在构建及运行原组合测试。此修改本身不证明持续软件恢复已经接通，仍需审查两层门禁在真实恢复入口的衔接。

预算边界修改构建成功且该测试通过，组合测试随后在 Journal 迁移断言失败。当前格式常量已为 V6，测试仍检查 V5；同时发现生产 `MigrateJournalJson` 接受 V2/V3/V4 却漏掉 V5，导致 V5 返回 null。已补 V5 分支，并对 V2/V3/V4/V5 逐一验证迁移至当前格式、RecoveryBlocked/失败指纹/失败计数/重拉代次保留及重复读取幂等。不改变阻塞状态含义。`build-journal-v5-migration.log`、`test-journal-v5-migration.log` 正在验证，尚未证明整个版本混用/安装升级链通过。

上述构建及组合测试终态退出码 0，88/88。同模块 `MigrateEvent` 已接受 V2—V5，无需同步修改该分支。已启动最新产物完整控制→磁盘→电源→独立 Guard 串行回归，日志前缀 `test-v6-migration-`，结果待终态；运行期间不覆盖产物。该组合通过不代替安装包升级、现场硬件或所有恢复入口验收。

提前卸压并发审查发现 `HydraulicController.BuildAndQualifyAsync` 原先在首次 DO/AO 建压输出之前不检查 token，仅进入反馈循环后检查。现已在首次 DO 前以及 DO 成功后/AO 前增加取消检查；后者触发时沿用原 outputArmed 清场逻辑。该修改尚未构建，不在当前运行的回归产物中。检查与实际输出之间仍非原子互斥，不能将两处取消检查称为旧代次输出竞态已完全关闭。

已补 `CancelledBuildDoesNotTouchOutputs` 回归：使用无硬件配置的 DO/AO 控制器及预取消 token，调用真实 BuildAndQualifyAsync，要求返回相同 token 的取消异常；若继续到建压输出会先失败，压力读取委托也明确抛错。该测试仅覆盖入口已取消的情况，不覆盖写入过程中取消。原完整回归仍在推进，尚未重建覆盖运行产物，新测试待原会话结束后验证。

同类入口检查扩展：`RunOnceAsync` 在入口已取消时直接返回 false，不进入会写回零的 finally；进入运行后在 AO 建压写入前再检查取消，沿用原 finally 清场。`FallbackHoldLoopAsync` 入口已取消时直接返回。新增单次运行预取消返回 false 的测试检查。以上源码仍待构建，且 RunOnce 原 finally 忽略输出失败、Fallback 中途取消与输出互斥仍待审查，不能推定完整清场已可靠。

`RunOnceAsync` 的 finally 已复用 `ForceReleaseAsync`，消除吞掉 DO/AO false/异常后无条件记录“停止并回零”的行为。清场失败现在传播异常，即使此前业务体准备返回 true 也不报告成功；此处仍只证明命令结果，不证明压力安全。当前源码需构建验证。

原 `test-v6-migration-full.log` 已到 892/892，串行命令尚在独立 Guard 阶段运行；整个组合未取得终态，不能提前宣布所有套件通过。该产物不含后续取消与 RunOnce 清场修改。

已读取对应磁盘 76/76、电源 19/19 日志；Guard 当前输出 `RepeatedFailures Attempts=100 Steps=1189`，原会话仍存活。新增 `RunOnceRejectsUnconfirmedRelease`，以空 DO/AO 映射验证真实 RunOnceAsync 清场返回明确 `HydraulicReleaseOutputUnconfirmed` 异常而非布尔完成；不连接现场硬件。测试及后续取消/清场源码尚待构建，不能沿用前述旧产物回归结果。

Guard 对应场景源码要求实际执行 300 次失败，持续保留业务及各通道进展起点，最后验证人工停止阻止下一次动作；当前 100 次进度不等于完成。

Fallback 保压启动及释放闭包已捕获本次 CancellationTokenSource，避免延迟回调读取后续替换的 latch.Cts。释放输出复用 ExecuteReleaseOutputAsync，消除 DO/AO 失败被吞掉的问题。保留原等待 10 ms 尚不能证明 worker 已退出，完整旧任务排空仍未实现；这些改动未构建，不在当前回归产物内。

后续已移除固定 10 ms 等待。Fallback 释放闭包捕获本次 holdTask：取消后先尝试卸压，等待确切旧 writer 终态，再重发卸压，收集取消/任务/两次输出异常后抛出汇总失败。任务不结束时不能返回清场成功；不伪造物理反馈。最新源码尚待构建，且仍需验证上层阶段超时后对该未完成任务的所有权保留与跨运行隔离。

原串行回归终态退出码 0：控制 892/892、磁盘 76/76、电源 19/19、Guard 98/98。后续取消/RunOnce 清场/Fallback task join 修改构建成功，`test-hydraulic-cancel-join.log` 35/35、退出码 0。

检查上层 `MarkVoltageReleaseAsync` 发现其冻结了旧 completion.Task，却在异步返回后通过可变 latch.ReleaseCompletion 写结果。现已一并冻结本次 completion source，迟到成功/异常只写回原任务；无 releaseAction 分支也复用严格输出检查。该最新修改日志 `build-release-completion-identity.log`、`test-release-completion-identity.log`，终态待核实。上层 generation 资源/输出权限的跨运行互斥仍需独立验证。

上述修改构建成功，液压 35/35，退出码 0。继续检查发现 EnterElectricalPhaseAsync 在无需等待上一轮释放时没有取消检查，已在入口及每轮成员登记前检查 token，防止预取消请求登记成员/启动建压，并扩展预取消回归。日志 `build-latch-entry-cancellation.log`、`test-latch-entry-cancellation.log`，结果待核实。锁内实际状态变更与外部撤权仍需要整体生命周期隔离验证。

成员登记取消修改构建成功，液压 35/35，退出码 0。正常 BuildAndHold 分支现已捕获本次 linked cancellation 与 task；释放时取消并发送 Release，等待本次任务终态，finally 再执行严格卸压输出。取消可覆盖保持任务尚未登记导致 Release 信号丢失的窗口。不再用仅发信号的兼容委托覆盖该等待逻辑。日志 `build-controller-hold-join.log`、`test-controller-hold-join.log` 正在验证；真实控制器迟到写入和释放超时仍需故障注入，不能将普通液压单元测试当作该并发链的完整验证。

上述构建成功，液压 35/35，退出码 0。进一步检查 BuildAndHoldAsync：重复保持直接返回 true 并不等待旧 writer，已改为在不操作旧输出的前提下抛出 HydraulicHoldAlreadyOwned；非取消建压异常不再转成 false，而是记录后传播。正常取消保持返回 false 的原语义不变。`build-hold-failure-propagation.log`、`test-hold-failure-propagation.log` 正在验证；重复保持与真实输出错误的生产级故障注入尚待补足。

上述构建及液压测试退出码 0，35/35。新发现保持任务最终卸压失败时旧 hold 记录永久留存；已记录精确失败 hold，并仅在后续 ForceRelease 输出确认成功后按键和值匹配移除该旧占用，正常退出同样按精确值删除，避免删除替代任务。失败本身仍保留占用且传播异常。日志 `build-failed-hold-release-retry.log`、`test-failed-hold-release-retry.log` 正在验证，尚需专门故障注入覆盖并发重试。

此前 JXCQ 六组验证会话已退出码 0，目录 `policy-incremental-70a1df29035e45709f5febdbcf39ade7`，末组液压 35/35；最新 failed-hold 重试清理不在该远端产物中，其余明细应以远端汇总日志复核。

failed-hold 初版重试清理构建成功，液压 35/35，退出码 0。审查发现必须在卸压命令前冻结失败 owner；若命令成功后才读取，可能把命令期间新产生的失败错误视为已由本次成功覆盖。已将读取移至命令之前，成功后仍按精确键值清理，最新日志 `build-release-retry-frozen-owner.log`、`test-release-retry-frozen-owner.log` 待终态。普通液压测试仍不能替代并发重试故障注入。

冻结 owner 修改构建成功，液压 35/35，退出码 0。现把生产精确清理操作提取为 RemoveExactHoldEntry，增加旧身份不可删除替代任务、空身份拒绝、精确身份成功、重复清理幂等测试。`build-exact-hold-cleanup-test.log`、`test-exact-hold-cleanup.log` 待终态；该检查不是完整输出重试并发故障注入。

上述构建成功，液压 36/36，退出码 0。已启动同产物完整控制→磁盘→电源串行回归，日志前缀 `test-hold-cleanup-`，终态待核实。尚缺的生产验证明确为：真实保持任务迟到写入/释放信号先于登记、卸压失败并发重试的新旧 owner 隔离、整组清场对共享电气/液压范围的完整覆盖，以及恢复后真实业务提交。单独的 helper、无硬件入口取消测试不能替代这些证据。

已删除活动圈有效事件不再调用的旧单通道恢复执行体，将其无效通道诊断留在入口；有效事件仍只走整组登记/清场。删除的是旧源代码实现，不是数据或日志。旧 `_activeCycleLimitRecoveries` 计数引用仍待与统一任务登记收敛，不能将该旧计数作为新路径真实进度证据。源码尚未构建，原串行测试产物不含本次删除。

已移除旧 `_activeCycleLimitRecoveries` 字典及永远不生效的 ContainsKey 检查；CaptureLogicalQuiescenceSnapshotLocked 在既有 _recoveryContractGate 下按尚未终态的 ActiveCycleDataLimitRecovery 契约计数。重复 incident 继续由统一登记范围门禁处理，不另造第二套字典。源码检索确认旧字典及旧执行体名称无剩余引用；尚未构建验证，不把当前运行的旧产物测试结果套用到此修改。

扩展 HealthyActiveCyclesAreNotSoftwareRecovery 检查：健康活动圈不算软件恢复；同样活动圈存在一个恢复时计数为一；活动圈已清理但恢复尚未终态时仍计数为一。此测试验证计数合成语义，不是生产契约枚举的完整集成测试。新断言未构建，原串行回归会话仍在运行。

仓库 TestConfig 默认电源组为 1—3、4—6、7—9、10—12，液压组为 1—6、7—12，未跨液压；这仅是仓库默认配置事实，不代表现场配置或暂停成员的范围覆盖已验证。

## 继续后的回归与失败清场补强

`test-hold-cleanup-` 串行回归已确认退出码 0：控制 895/895、磁盘 76/76、电源 19/19。活动圈旧执行体/计数收敛后的源码重新构建成功，`test-active-cycle-unification.log` 专项 88/88、退出码 0；不能将前一产物的完整回归算作后一修改的完整回归。

检查整组恢复发现，重新送电后机械释放或重新加入失败时，原 catch 仅尝试 EPB OFF 并吞掉异常。现改为在原运行、执行许可及液压 lease 仍有效时，复用统一安全执行器：逐通道 OFF、逐电气组断能、液压卸压输出全部尝试；命令失败阻止安全确认，命令全部成功后再验证新鲜电流及压力。安全结果附在故障详情中，不能把失败收尾成功当作恢复业务成功；失去所有权的旧 worker 不再发布覆盖继任者的终态。未取得 lease 时不操作共享输出。

本次构建/专项日志为 `build-group-failure-cleanup.log`、`test-group-failure-cleanup.log`，终态待核实。共同安全执行器已有测试不能替代此完整 Manager 失败分支的故障注入；超时迟到输出、共享范围完整性及现场物理验收仍未闭环。未部署 wj-epb。

上述构建及恢复协调专项已确认退出码 0，51/51。该结果只覆盖本地专项，尚不能声明最新版本全量验证通过或已具备现场部署条件。

进一步将失败清场命令组装提取为生产直接调用的 `RunAffectedGroupFailureSafetyAsync`，增加逐通道/电气组/液压组命令故障矩阵、重复目标去重、反馈拒绝及执行中撤权检查。电气组映射缺失作为必须保留的命令错误，仍尝试其他可执行的 OFF 和卸压，不因映射异常跳过全部安全输出；不以此放行安全确认。`build-group-failure-matrix.log` 构建成功，`test-group-failure-matrix.log` 52/52，退出码 0。

这覆盖真实生产调用的命令组装与统一安全边界，但并未实例化完整 Manager 的重新送电→机械失败链，也未证明硬件实际动作。已启动 JXCQ 隔离目录六组增量验证，日志 `test-jxcq-group-failure-matrix.log`；终态、目录和哈希结果待核实，未部署 wj-epb。

JXCQ 六组已全部通过，详细目录、哈希和计数见同日增量验证记录。最新同产物本地完整控制→磁盘→电源串行回归 `test-group-failure-*.log` 仍在执行，不能提前报告通过。

后续源码审查发现 `RecoveryStageDeadline` 排队的委托没有调度取消令牌，而且动作成功分支没有重新检查调用者取消。已冻结阶段 token，用于任务调度和动作调用前检查，并在动作返回后检查调用者 token；新增预取消不执行、动作完成同时撤权不得返回成功的回归。这不解决已开始且不响应取消的硬件调用：旧动作实际终态与资源接管仍是独立未闭环项。为避免覆盖运行中的测试产物，该修改及新测试尚未重新构建。

上述串行回归已确认全部终态退出码 0：`test-group-failure-full.log` 896/896、`test-group-failure-disk.log` 76/76、`test-group-failure-power.log` 19/19。之后才重新构建取消竞态修改，`build-stage-cancellation-race.log` 成功，`test-stage-cancellation-race.log` 53/53、退出码 0。后者不是完整回归，也未更新 JXCQ 产物；没有在测试进程运行期间覆盖二进制。

## 超时在途动作与停止接管：当前源码缺口

已核实以下生产调用关系，不能再把“旧 RunEpoch 撤权”直接当作硬件调用退出证据：

1. `RecoveryStageDeadline.RunAsync` 超时后取消 token 并观察迟到异常，随后返回异常；已经开始且不响应取消的 actionTask 仍可能运行。新增排队取消检查只防止尚未开始的动作，不终止正在执行的动作。
2. `ExecuteStopRecoveryOwnerStageAsync` 等待 owner/任务登记退出；超时后调用 `HydraulicRecoveryOwnershipCoordinator.SupersedeAll` 和任务表隔离，并返回 `StopRecoveryOwnersSuperseded` 成功。
3. `SupersedeAll` 清空 owner 字典并完成其 Completion；这证明软件登记已隔离，不证明外部硬件调用已经退出。单纯延迟 Dispose 也不足以修复该路径，因为字典已经清空。
4. 整组路径包含限时的 `PrepareAndEnableAsync`、机械释放及安全输出。因此在途动作不能全部按无副作用的数据任务处理；当前缺少贯穿超时、停止和重新启动门禁的真实动作生命周期证据。

下一项修复必须追踪真实在途硬件任务，并把“软件 owner 撤权”和“硬件写入退出”分开验证；保持正常软件任务可隔离退出，同时拒绝未终结硬件调用与同进程重新送电重叠。外部 Guard 的旧进程退出及重新取得独立安全证据应一并纳入完整恢复验证，不能用永久禁止恢复替代所要求的及时安全恢复。

本节是源码缺口确认，不是修复完成或现场故障复现。当前没有新测试进程在运行，后续可直接修改和构建；wj-epb 未操作。

### 第一段实现：整组真实动作保留

`RecoveryStageDeadline` 可登记实际 actionTask；新增 `RecoveryStageTaskRegistry` 在 wrapper 超时之后仍保留未完成任务，迟到成功或异常终态后才回收。整组恢复八个阶段均接入按液压组保留的登记表；同组入口发现旧在途动作不进入新的硬件恢复。失败清场反馈阶段拒绝仍有在途动作的安全确认；停止 owner 阶段不再在这些动作未结束时返回隔离成功。

`build-pending-group-actions.log` 构建成功，`test-pending-group-actions.log` 54/54、退出码 0，含真实异步任务超时后迟到成功/失败保留回收测试。停止生产接口和 Guard live-stop 回归日志为 `test-pending-group-stop.log`、`test-pending-group-live-stop.log`，终态待核实。

当前覆盖限于整组路径；其他硬件入口、手工恢复与同组在途动作的交叉门禁，以及停止失败后外部 Guard 确认旧进程退出并重新取物理安全证据的完整可用性链仍未证明。不能将此改动表述为全部在途硬件动作已受控或完整自恢复已完成。

停止生产接口 14/14、Guard live-stop 43/43，串行会话终态退出码 0。上述是既有接口回归，不等价于带未终结硬件任务的完整停止→外部接管集成测试。

### 共享物理反馈门的交叉入口检查

`ConfirmJointPhysicalSafetyAsync` 现按通道液压映射及显式液压范围，检查上述整组在途动作登记；在采样前、每轮采样及最终放行前检查，拒绝 `PhysicalSafetyHardwareActionPending`。因此调用该共享反馈门的手工液压恢复、批量续测等不能仅凭瞬时安全采样绕过仍在执行的已登记整组动作。`build-shared-pending-feedback.log` 构建成功，`test-shared-pending-feedback.log` 54/54、退出码 0；尚缺完整 Manager 交叉入口注入测试。

继续检索发现其他阶段执行器调用包含整段 `AlarmResumeFreshBatch` 工作流，其内部会重新进入启动与安全检查。后续覆盖必须区分实际输出动作与嵌套流程，不能简单将所有外层工作流登记成硬件 writer，否则流程会把自身当作未结束动作而阻塞。当前只覆盖已接入的整组动作，不能声称其他入口全部登记完成。

报警恢复已将 `AlarmResumePowerReady` 和 `AlarmResumeMechanicalRelease` 两个阶段接入同组动作表，并在取得 ownership 后检查旧动作仍在途的情况。未把 `AlarmResumeFreshBatch`、完整学习和资格复核等外层流程作为硬件动作登记，避免递归安全检查把自身判成遗留 writer。`build-alarm-pending-actions.log` 构建成功，`test-alarm-pending-actions.log` 54/54、退出码 0；现有协调测试不能替代完整报警恢复故障注入。

另外确认整组的 `EnsureMotorReleasedBeforeFormalRejoinAsync` 经旧圈退出等待调用 `PreReleaseBatchWithPlanAsync`，没有直接递归调用共享物理反馈门。所有路径仍需在真实动作层逐项核对；报警恢复当前的断能/卸压/新鲜反馈完整顺序、共享组成员所有权和长学习等待并未因此全部验证。

### 恢复电源预检返回值

确认 `EnsurePowerSupplyReadyBeforeStartAsync` 返回本次被禁用的通道列表，不能以 Task 完成代替选定通道通过。PauseResume 中四个调用此前忽略该列表；现改用恢复专用 `EnsureRecoveryPowerSupplyReadyAsync`，电源缺失、结果缺失或任一本次通道被禁用即拒绝继续，并在返回后检查取消。初始批量启动原有部分成功逻辑不变。

新增结果门禁测试，`build-recovery-power-result.log` 构建成功，`test-recovery-power-result.log` 55/55、退出码 0。覆盖返回结果检查，不等价于实际电源保护故障的整条恢复集成测试。最新同产物 JXCQ 六组验证已启动，日志 `test-jxcq-pending-and-power-result.log`，终态待核实；未操作 wj-epb。

进一步追踪电源预检发现其内部调用 `RunPowerReadyRecoveryAttemptIncidentAsync`，不是纯硬件动作。已移除报警 `AlarmResumePowerReady` 外层任务登记，将登记下移到 `EnsurePowerSupplyReadyBeforeStartCoreAsync` 每次实际 `_powerSupply.PrepareAndEnableAsync` 返回的任务，并登记所有相关液压组。这样共享启动/恢复预检都追踪实际供电任务，不把含内部恢复的整个流程当作 writer。机械释放登记保留。

本次源码修改尚未构建。原固定产物完整控制日志已出现 899/899，但串行会话仍在执行后续磁盘/电源测试，未报告整体完成；不覆盖正在测试的二进制。

该串行会话现已终态退出码 1：控制 899/899 通过，磁盘 `ReactivatedRootPreservesOwnedStaging` 在 `Tests/EpbDiskWriterTests/Program.cs` 的“第一删除批次后未重建Root或未保留staging”断言失败，后续电源测试未运行。保留 `test-pending-power-full.log` 和 `test-pending-power-disk.log`，不得引用旧版 76/76 覆盖本次失败。当前仅读到用例通过目录枚举/剩余文件数触发 Root 重新激活；具体失败根因尚未确认，不预判为机器负载或忽略不处理。

相同未重建产物单独重跑 `test-disk-reactivation-recheck.log` 76/76、退出码 0，当前根保护用例实际触发 `Reason=Current` 并保留 staging。首次日志显示改名/占用错误后断言失败，但既有日志未区分 WriteSidecar、MoveDirectory、MoveSidecar、DeleteStaging。已给 IOException/UnauthorizedAccessException 警告补充 Phase 与 HResult，不改变删除或保护逻辑，不将重跑通过视为根因已修复。

新增磁盘测试参数 `--housekeeping-reactivation-repro`，固定重复该用例 20 次，并补充断言的源目录存在状态、staging 路径及服务计数。`build-reactivation-repro.log` 构建成功，`test-reactivation-repro.log` 20/20、退出码 0，各次均触发 Current 保护。该数字是同一用例的重复次数，不是 20 个新增独立场景；首次失败本轮未复现，仍未关闭。强制结束完全无响应 Main 的顺序变更尚未获用户确认，没有实施。

已启动 `build-power-leaf-housekeeping-diagnostic.log` 构建及 `test-housekeeping-diagnostic.log` 磁盘回归，包含此前未构建的电源实际动作层登记修改；结果待终态。wj-epb 未操作。

上述构建成功，磁盘 76/76、退出码 0；同产物 `test-power-leaf-coordination.log` 55/55、`test-power-leaf-power.log` 19/19，均退出码 0。首次磁盘异常未被复现也未证明根因已解决，保留未关闭状态。该产物未跑完整控制回归，不能套用前一产物的 899/899。已启动独立 Guard 回归 `test-power-leaf-guard.log`，终态待核实。

### 非协作停滞接管的完成边界

当前源码核实：`SupervisorRecoveryActions` 的 SafeStop 对精确存活 Main 返回 `ActiveMainSafeStopPending`，仅 Main 已退出才进入独立 SafetyAgent 安全执行。`RecoveryGuardRuntime` 在 Main 内检测请求，通过 `RecoveryGuardLiveStop.RunAsync` 调用停止回调，并仅在允许退休后调用 `Environment.Exit(0)`。

`test-power-leaf-guard.log` 独立 Guard 回归已终态退出码 0，98/98。包含完成 300 次注入失败、真实业务进展时钟不被心跳刷新、会话换代与迟到响应隔离等测试。它不是 300 次真实设备重启或现场停滞验收。当前无仍在执行的测试会话。

因此当前已证明的协作停机接口测试不能证明“Main 无法完成协作停止”时仍能及时恢复。新增在途动作门禁也不能以无限等待代替最终恢复：须进一步区分可安全退出的旧进程、仍需保留的数据任务和真实未终结硬件写入，验证完整事务里的物理安全证据、旧进程退出和退出后的独立安全复核。不能直接放宽 Pending 为 Completed，不能把主程序仍存活时的旧证据用于启动新实例。本节未新增现场操作或放宽安全条件。

### 完全无响应场景的待确认处置顺序

再次核实服务端 `SupervisorGuardSafetyPreparation` 和 `SupervisorGuardSafetyExecution`：独立安全准备、执行均要求 Main 被精确观察为 Exited，否则拒绝 `RecoveryGuardOldMainExitUnproven`。因此不仅是 Guard 客户端等待，服务端当前也不允许对仍存活的 Main 执行该独立安全链。

若 Main 完全无响应、无法协作发出安全命令，新增“先强制结束旧进程，再独立断能卸压、取得新鲜安全反馈”的兜底会改变用户此前要求的先安全确认再收敛旧实例的顺序，且存在未落盘样本丢失风险。此处需要用户明确决定，当前未实现、未授权、未执行。即使后续获准，身份匹配、实际断能卸压、新鲜反馈、耐久检查点及数据连续性门禁仍不得省略，数据缺口无法证明安全收敛时不得自动续测。

### 用户已授权完全卡死兜底，开始实现

用户随后明确允许仅在“主程序完全卡死、无法协作断能”时采用“强制结束旧进程→独立断能卸压→新鲜电流和压力确认安全→续测”。此授权更新了上一段的待确认状态，不代表批准部署 wj-epb，也不取消数据连续性门禁。

已新增 `RecoveryWorkerRetirement.RetireExactMainProcess`：仅接受主程序文件名和完整身份，拒绝本进程，使用同一内核句柄核实启动时间及映像路径，再执行终止前权限回调、终止及退出等待。既有 Guard worker 入口仍限定 Guard 文件名。该底层入口尚未接入自动触发；后续必须加入完全卡死证据、协作停止宽限、事务/人工停止/维护状态的最后校验，并在精确退出之后走既有独立安全链和提交验证。

`build-exact-main-retirement.log` 构建成功，`test-exact-main-retirement.log` 2/2、退出码 0。测试仅启动隔离的 status-only Guard 副本（其中一份名为 MTTFTest.exe）及 Guard 测试进程；验证旧启动身份不杀当前 PID、最后撤权阻止终止、匹配身份退出及本进程拒绝。没有启动或结束真实试验程序。

新增 `StalledMainRetirementPolicy` 判定门：RecoverStalled 模式、有效 SafeStop 事务、建立过真实观察信任、确认次数满足、协作宽限已过、所有参与通道的采样/控制/持久化时钟均已超过停滞阈值，且无未到期阶段等待。单通道停滞、任何通道仍有进展、停止意图、过期所有权、缺失证据及 RecoverExited 模式均拒绝。默认阶段预算下宽限为 30 秒，尚未进行现场时延验证。

重要：接管后旧快照写入会因旧授权被撤销而拒绝，故不能把无新快照单独当作 Main 完全卡死证据。该门尚未接入自动终止调度，需继续补最终状态/独立停止/维护与执行范围复核，以及持久化的终止意图和退出后的安全链。

`build-stalled-main-gate.log` 构建成功，`test-stalled-main-gate.log` 3/3、退出码 0，包含判定矩阵及隔离进程终止测试。未部署 wj-epb。

### 完全卡死兜底已接入 SafeStop 调度（本地验证）

上述“尚未接入”现已更新：生产 Guard 构造动作执行器时传入 RecoverStalled 设置、维护和 commissioning 最终复核。满足全通道停滞条件后，先写 action-history 终止意图，再通过精确句柄终止；回调重读受租约/授权/停止信号/会话约束的 SafeStop 状态。终止结果始终为 Pending，不越过独立 SafetyAgent 安全门禁，也不直接启动新 Main。

定向验证 4/4、完整 Guard 101/101、磁盘 76/76、电源调试器 19/19 通过。完整控制回归及硬件验收状态见[完全卡死主程序兜底记录](2026-09-09_RecoveryGuard完全卡死主程序兜底.md)。未部署 wj-epb，不构成所有恢复入口和现场硬件均已验收的结论。
