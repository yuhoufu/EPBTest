# 新任务交接：RecoveryGuard 与 EPB 3.0 完整交付

## 接手结论

尚未完成正式完整安装包，不能通知用户已经可以现场部署。用户要求重新起任务以缩短上下文、尽快完成，不是重新开始开发或放宽安全条件。旧任务交接后停止改代码，由新任务单独接管。

唯一权威工作目录：`C:\Users\19812\.codex\worktrees\d046\EPBTest`。大量已修改和未跟踪文件均是待交付工作，必须保留。不要在保存项目 `D:\Github\wanxiang\EPBTest` 的默认分支另起实现，不要 reset/clean、覆盖、擅自提交或为通过发布检查绕过干净工作区规则。交接时已重新查看 git status，确有这些修改。

## 原始目标与授权

完成 EPB 3.0.0.0 与独立 RecoveryGuard 剩余修复、正式完整安装包、可审计文档。先本地/JXCQ隔离验证。wj-epb 只有用户再次明确确认部署后才可切换、做真实硬件自学习/正式运行/自动恢复验收。I0050 液压恢复状态发布一致性作为独立第二步，不得消失或混入首步完成声明。

所有恢复入口统一条件：确认真实停滞，排除正常学习、冷却、保压等待；实际发出断能和卸压；使用动作之后的新鲜电流及压力确认受影响通道/液压组全部安全并保持规定时间；命令发送成功不等于安全。安全后收敛旧实例、匹配入口续测，以真实业务提交证明恢复。主程序完全卡死时，用户已允许“强制结束旧进程 → 独立断能、卸压 → 新鲜电流压力确认安全 → 续测”。不得跳过物理安全门禁。

保留数据库、学习数据和封存证据；回传可与恢复运行并行。当前无 CAN 硬件，不检查或以 CAN 作为门禁。未经明确要求不派生代理。读取本仓库 AGENTS.md。

## 已验证基线（先读日志，不必无意义全量重跑）

日志目录 `artifacts/guard-physical-safety/`。下列为原任务取得终态的结果；新任务应核对文件，变更相应源代码后重新验证。

| 范围 | 证据与结果 |
| --- | --- |
| Adaptive Release | `test-release-attempt-marker-full-v2.log`，985/985，退出 0 |
| Disk Release 重编译 | `build-disk-attempt-marker-current.log`、`test-disk-attempt-marker-current.log`，76/76，退出 0 |
| 电源调试器 | `test-power-attempt-marker-current.log`，19/19，无跳过；本轮 --no-build，不宣称本轮重编译 |
| 独立 Guard | `build-guard-current-review.log`、`test-guard-current-review.log`，101/101；模拟不是硬件验收 |
| JXCQ 九组 | `jxcq-attempt-marker-result-v2.log`，43/43、37/37、14/14、56/56、15/15、46/46、36/36、27/27、8/8；85 文件哈希验证，LocalInputsReverified=true |

Controller.dll 基线 SHA256：`EBD71080349B78E4B7644BA452BBA5670D3B4ED674BB6518C2928DE2C97F3C10`。JXCQ 根 `D:\EPB_Validation\policy-incremental-8ee9b41f65ba467ba66ab70bf182075b`；明确 DeployablePackage=false、PhysicalHardwareVerified=false。

首轮完整/JXCQ曾因 RecoveryClosureV2171Tests 反射夹具旧字典类型失败，随后修正并完成上述 v2；失败日志必须保留。交接时没有已知仍运行的构建/测试会话，不把历史文档“运行中”当作当前状态。

## 已落地的关键实现

1. `Controller/EpbManager.WarningSnapshots.cs`：待恢复字典改为不可变 FormalPendingCycle(Cycle, AttemptId)；精确原尝试清理；业务提交前后核对原计数及 pending 对象，避免吞后继故障。
2. `Controller/EpbManager.cs`：软件恢复作废标记增加 AttemptId；真实 DAQ 事故保留整圈无效标记。四处消费入口传原尝试，两种标记并存不误解除 DAQ 保护。生命周期及 v2171 夹具已同步。
3. `Tools/Install-MTTFTest-Unattended.ps1`：Current/LKG 局部回滚、配置/维护标记/结果原子发布、桌面快捷方式备份与局部恢复、停止服务和任务失败关闭、任务禁用后验证真实实例归零、换包前再次确认主程序及恢复运行空闲。
4. 任务备份 schema 3 保存三个精确根目录任务的存在性、XML、OWNER/GROUP/DACL，绑定机器、安装根及事务 ID；保护目录为 ProgramData/MTTFTestDeploymentEvidence，仅 SYSTEM/Administrators；发布返回哈希，读取校验身份、哈希和 XML/SDDL。旧 schema 1/2 不自动信任。
5. 最新 Write-DeploymentTaskPreparation 在禁用任务前持久化 TaskBackupPrepared 和备份哈希。它只是准备阶段，绝不是完整安装事务或已接线任务还原。

## 真正未完成项：按交付主线处理

- 控制恢复：原 RunId/epoch/session/attempt 的硬件准入和退役是否原子；正式故障报告仍有仅按 channel 取消/移除 Timer/Runner 的路径；旧回调不得操作新运行。StartChannelAsync 在安全准入前发布共享运行身份/启动 DAQ 电源，需核对并发启动及整组所有权。
- pending 后续：四个终态调用忽略精确删除失败；故障在计数消费后迟到可重新登记；TryCommit 发布回调重入后返回 false 不回滚已经发生的发布副作用。不能把已有身份迁移称为全链路原子化。
- 共享液压/电源组恢复所有权、正常电源/启动恢复真实业务提交证明、学习/资格阶段联合反馈门禁及旧告警清理作用域仍需依据当前代码闭环。不要把审查项盲目当作已证实缺陷，先定位再完成。
- **完整安装事务尚未完成**：主流程没有完整失败回滚 catch，仅 mutex finally。必须贯通服务原状态、任务 XML/权限/启用及运行状态、ACL、配置、维护/抑制标记、Current、快捷方式和结果发布；持久事务阶段与崩溃重入。现有任务准备记录尚未接实际还原，也没有与所有安装阶段统一。
- LKG 晋升：SoakEvidencePath 未形成真实证据门禁；不要擅自把历史示例 168h 当作已实施规则。参照专门缺口文档。
- 正式全包构建、依赖完整性和可回退安装验收未完成。Tools/Build-Release.ps1 要求干净工作区和厂商 SDK，不伪造、不绕过；如需用户决定提交/打包基线，应明确提出具体决策。
- wj-epb 真实硬件验收未授权，不能为了完成目标自行部署。I0050 独立第二步保留。

## 最近安装测试与证据边界

`Test-RuntimeTaskStopAdmission.ps1` 12 场景；最新引用清理断言 PS7 已通过，最新这处测试修改尚未再跑 PS5。`Test-RuntimeTaskBackup.ps1` 11 发布场景、7 非法条目场景，PS7/PS5 曾通过。本地非管理员测试对保护目录创建有明确替身，不能声称真实 ACL 验证。

`Test-ProtectedEvidenceDirectoryOnJxcq.ps1` 最新真实提升权限隔离验证退出 0：保护目录创建/重复接受、拒绝不可信既有目录且不改其 ACL、真实无任务备份、发布哈希读取、准备记录、重复准备拒绝、注入准备文件损坏拒绝。根 `D:\EPB_Validation\protected-evidence-7fbd79d22ba04d6e8a1bedd9b26361f2`；原备份/准备 ID `517891d4b5ba4776a88b0aa84d18b57d`，故障备份 ID `f5933de3aedf4cd2a8bf60f02fc087d0`。证据保留，未改生产任务。本项最新结果尚未补入旧安装流水文档，以本交接说明定位后核验。仍不证明路径 TOCTOU 消除、完整任务还原或完整事务回滚。

## 阅读入口与执行方法

优先阅读本文件，再按需要看：

- `docs/2026-09-09_完整安装包交付入口核对.md`
- `docs/2026-09-10_正式圈待恢复记录尝试身份迁移审查.md`
- `docs/2026-09-09_RecoveryGuard统一物理安全恢复_实施记录.md`
- `docs/2026-09-09_RecoveryGuard完全卡死主程序兜底.md`
- `docs/2026-09-10_LastKnownGood晋升证据缺口.md`
- `docs/2026-09-09_wj-epb_3.0部署测试与最小影响回退方案.md`

旧文档是逆序流水，含历史“待验证/运行中/未实现”，须以最新源代码和终态证据为准。不要逐段重复验证早已完成的历史状态。

MSBuild：`C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe`；直接 csproj Release 的 Platform=AnyCPU，解决方案 Any CPU，Debug x86。不要在测试运行中重建共享 DLL。PS5 可使用进程级 ExecutionPolicy Bypass，不改全局策略。

JXCQ 隔离入口 `Tools/Test-RecoveryGuardPolicyOnJxcq.ps1`，Release 并启用 IncludeCycleLifecycle、IncludeStopProductionSeam、IncludeRecoveryResume、IncludeRecoveryProductionSeam、IncludeHydraulicCoordination。只操作 D:\EPB_Validation 授权隔离目录。

## 现场历史，禁止误报为实时状态

9月9日故障处置已完成回传两份校验封存、可恢复日志移走、原版 2.14.2.11 重启及真实业务续测记录，见 `docs/2026-09-09_wj-epb_2303故障停机处置.md`。未部署 3.0，Guard 任务当时仍禁用。这不证明此刻现场健康，不要重复故障操作。

## 新任务工作要求

首先核验工作目录及关键证据，列出有限的交付阻塞清单，随后按完整链路实现和验证。不要再次陷入“一个微小辅助测试→状态汇报→仍未完成”的循环。优先形成可审查的完整恢复/安装事务闭环，再集中回归、整理完整包和验收入口。诚实报告剩余项，不以通过数量替代可部署版本，不承诺未经评估的完成时间。完成授权内工作后通知用户，由用户决定现场部署。
