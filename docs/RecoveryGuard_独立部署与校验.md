# RecoveryGuard 独立部署与校验

日期：2026-09-07。目标版本：3.0.0.0。

当前独立包是 **ObserveOnly 调试交付物**，并非整套自动恢复的最终安装包。Guard 自动执行器、全部输出门禁及健康任务协作尚未完成；不能修改 Mode 绕过此边界。整体状态见[实现与交付进度](02_Issues/2026-09-07_RecoveryGuard_实现与交付进度.md)。

## 文件与状态边界

包中只包含 `MTTFTest.RecoveryGuard.exe`、`MTTFTest.RecoveryControl.dll`、`guard-settings.json`、独立安装器和 `guard-identity.json`。检查器不从业务 Current 目录加载 DLL。版本、大小、SHA-256、模式及时间参数关系均需通过验证。

| 位置 | 内容及权限 |
| --- | --- |
| `%ProgramFiles%\MTTFTestRecoveryGuard\versions\…` | 独立版本目录；安装不覆盖旧版本。SYSTEM/管理员完全控制，普通登录用户读取执行 |
| `%ProgramData%\MTTFTestRecoveryGuard` | 安装记录、Guard 配置、独立维护标志及日志。SYSTEM/管理员写入，普通登录用户读取 |
| `%ProgramData%\MTTFTest\RecoveryControl` | 主程序与 Guard 共用授权、停止、快照及接管记录；允许登录用户的主程序写入，版本和意图约束由契约 API 执行 |

当前共享目录 ACL 尚未将不同种类的写入者隔离为不同 Windows 身份；不能将 API 的写入约束宣称为操作系统已完成角色隔离。

正常记录写入 `journal\guard-events.jsonl`，包含观察 UTC 时间、进程判定、原因及权威版本；失败同样记录。当前文件达到 8 MiB 后轮换到 `guard-events.previous.jsonl`，保留最近两段历史，不承诺固定天数。日志只由显式指定 `--journal` 的运行写入，临时验证不会默认写入真实安装目录。

## 构建和只读验证

在仓库根目录执行：

```powershell
.\Tools\New-MTTFTest-RecoveryGuardPackage.ps1
.\Tools\Test-MTTFTest-RecoveryGuardPackage.ps1 -PackageDirectory '<生成的独立包目录>'
```

构建脚本默认编译独立 Release 项目，输出目录与 ZIP 均使用新路径，记录源码提交、工作区是否有改动和包 SHA-256。`gitDirty=true` 的观察包不能当作经过正式发布审批的业务安装包。

解压后执行以下命令只校验内容，不创建目录、注册授权或安装任务，也不要求管理员：

```powershell
.\Install-MTTFTest-RecoveryGuard.ps1 -Mode Validate
```

## 安装与升级

首次注册必须配合主程序维护窗口：先完成原试验及看门狗恢复会话收口，并退出主程序。首次注册不根据旧项目 Armed 文件推导新的恢复授权。已存在的权威记录必须可读，且没有未释放的接管所有权；注册重试不撤销停止记录，也不重新授权旧试验。

在管理员 PowerShell 中，从已校验包目录执行：

```powershell
.\Install-MTTFTest-RecoveryGuard.ps1 -Mode Install `
    -BenchId '<台架标识>' `
    -MainExecutable '<匹配版本主程序的绝对路径>'
```

主程序文件存在时检查其版本匹配；文件暂时缺失不妨碍独立 Guard 安装和后续观察。完整业务组件一致性仍由主程序安装流程验证。

安装器创建独立版本目录和两项任务定义：`MTTFTestRecoveryGuard` 默认启用，以 SYSTEM 身份每分钟检查，重叠时 IgnoreNew，期限 45 秒；`MTTFTestRecoveryGuardExecution` 承载 `--execute`，当前调试交付阶段默认禁用。执行任务无调度器累计执行时限，不允许调度器强制中断安全动作；步骤期限和冷却仍由恢复引擎约束。两项任务均有开机触发与错过后补跑配置。当前包模式固定为 ObserveOnly；现有配置保留，不通过覆盖配置消除错误。

升级复用同一共享授权，保留旧程序文件。独立维护标志存在时 Guard 不取得新的接管权；如果安装异常中断留下该标志，需先核对安装与接管状态，不能按时间自动删除以强行恢复。

## 卸载

在管理员 PowerShell 中执行：

```powershell
.\Install-MTTFTest-RecoveryGuard.ps1 -Mode Uninstall
```

卸载禁用并删除独立检查和执行任务，保留版本文件、安装记录、日志以及共享授权、停止、预算和接管记录。未收口的接管事务会阻止卸载；不能用删除共享文件代替收口。卸载 Guard 不向主程序发送停止命令，也不强制终止正在运行的执行者。

## 验证范围

已执行：独立 Release 构建、主程序及业务 DLL 缺失时运行、配置只读验证、错误记录与日志轮换，以及损坏二进制、非法文件清单、版本不匹配、未开放模式、过期阈值和时间关系错误的拒绝测试。

2026-09-08 已在 JXCQ 隔离管理员环境实际安装/卸载 SYSTEM 任务并修改测试 ProgramData 权限：ObserveOnly 任务生命周期 4/4，通过真实任务完成故障回退、首次安装、升级和卸载 4/4；随后由正式 SYSTEM 扫描任务经生产调度路径拉起执行任务，无硬件 RecoverExited 整链 10/10。故障扫描由测试脚本手工触发同一 SYSTEM 任务以缩短等待，时间触发器已独立运行验证。现场 NI、电源和液压验收仍不能由这些结果替代；当前程序不包含 CAN 硬件，CAN 不属于验收范围。

## 持续执行入口的实现状态

`MTTFTest.RecoveryGuard.exe --execute` 已接入生产 Supervisor 动作适配器及恢复引擎。该入口持有全局执行互斥，动作许可绑定持久化到接管事务。非 ObserveOnly 模式仅接受默认安装级共享权威根目录，避免隔离测试记录指向真实服务；ObserveOnly 可使用隔离目录且不会派发动作。

安装器已加入默认禁用的独立执行任务定义；当前包尚未启用自动执行，分钟检查也不直接创建执行进程。执行任务有自己的分钟/开机触发，后续启用后由调度器创建唯一执行实例，持续运行期间 IgnoreNew。不得把 45 秒检查任务直接改成长恢复动作的承载者。执行进程失联监督、接管代次变化后的旧动作收口、失联组件重建与硬件互斥验证仍须完成后才能启用。

安装、升级和卸载持有与 `--execute` 相同的全局执行互斥，再检查共享接管所有权。执行者仍存活占用互斥或所有权未收口时拒绝切换；卸载不以强制结束执行者作为清理手段。检查与执行采用独立日志目录，避免两个进程并发轮转同一日志。JXCQ 已完成 ObserveOnly 真实安装态验证；XML 合同测试和该观察模式结果都不代表自动执行任务已经启用。

隔离 CLI 校验已新增 ObserveOnly 执行无动作，以及非安装目录禁止连接生产恢复服务两项用例。其通过仅证明这些入口约束，不代表已完成安装态硬件恢复验收。

### 分钟扫描的任务请求与失联报告

扫描入口已实现向固定执行任务请求运行的能力。实际请求前要求安装登记 `executionEnabled=true`、登记目录与扫描 EXE 一致，并核对任务 XML 的 SYSTEM 主体、路径参数、启用状态、IgnoreNew 和执行期限。Windows 导出已启用任务时可能省略默认的 Enabled 节点；校验现在接受“省略或 true”，仍拒绝显式 false。该修复已由 JXCQ 接管场景和 26/26 XML 回归验证。当前安装器写入 `executionEnabled=false` 并禁用执行任务，因此本期观察包不会因为新增该入口而开放自动执行。禁止手工修改标志绕过尚未完成的硬件验收。

扫描结果新增 Worker：执行者存活且租约有效为 `ExecutionWorkerRunning`；身份无法核实为 `ExecutionOwnerIdentityUnproven`，NeedsAttention 且退出码为 2。对活动事务中准确存活但租约过期的独立 Guard，在正式安装/任务配置有效、意图和维护复核通过后，先持久写入退休标记，再使用准确进程句柄结束旧 Guard。该处理不终止主程序或 SafetyAgent。没有活动事务等未覆盖场景仍报告 `ExecutionWorkerLeaseExpired`，不据此假定已恢复。

退休成功为 `ExecutionWorkerRetired;AwaitingNextDispatch`，尚未确认退出为 `ExecutionWorkerRetirementPending`。准确退出且租约结束后才允许下一次请求任务，并继续旧动作对账；“已请求任务”不是“已续测”。任务查询/请求有时间和输出大小限制，超时按结果不确定处理，后续须核对当前事务。安装器仍禁用执行任务，故这些能力尚未通过真实安装态验收。

四个含中文的 Guard 安装、打包及包/任务定义校验脚本使用 UTF-8 BOM，兼容 Windows PowerShell 5.1；编辑或再次打包时应保留此编码。2026-09-08 在 Windows PowerShell 5.1 完成任务定义及二进制配对 8/8、Debug 兼容性包校验 8/8。此验证没有注册任务或实际安装，最终 Release 自动恢复安装包仍待完成。

### 隔离 SYSTEM 任务生命周期验收

新增脚本 `Tools/Test-MTTFTest-RecoveryGuardInstalledTask.ps1`，用于真实任务注册、重复运行、日志和退出码、任务清理验收。脚本只接受经过包校验的 ObserveOnly 包，复制到新的证据目录，使用随机独立任务名和明确的隔离共享根目录；不存在业务 EXE，也不连接正式 Supervisor。任务保留安装器的 SYSTEM、IgnoreNew 和期限配置，但移除所有自动触发器，改为由测试手工触发；因此不代表已经验证开机触发、正式安装器、升级回退或设备恢复。

普通 Windows PowerShell 5.1 可先生成计划：

```powershell
.\Tools\Test-MTTFTest-RecoveryGuardInstalledTask.ps1 `
  -PackageDirectory .\artifacts\RecoveryGuard-3.0.0.0-dispatch-compat -PlanOnly
```

在具备管理员权限的 PowerShell 中去掉 `-PlanOnly` 执行真实验收。扫描和执行任务各运行两次；清理前再次核对准确任务名、EXE 和隔离参数，只清理本次准备注册的任务，保留全部文件及 results.json。成功标准是四次执行均通过、`actualTaskLifecycleVerified=true` 且 `tasksRemoved=true`，不以 XML 生成成功代替运行结果。

2026-09-08 先在非管理员本机验证计划生成与拒绝路径，随后在 JXCQ 管理员环境实际运行四次任务并完成清理，报告 `actualTaskLifecycleVerified=true`、`tasksRemoved=true`，证据为 `artifacts/RemoteGuardSystemTask/20260908-1120-pass`。该脚本使用 ObserveOnly 和隔离授权根目录，`automaticRecoveryVerified=false`。

### Verify 参数的当前软件初值

新增 `VerificationTimeoutSeconds`，缺省及示例值为 300 秒，必须大于 `3 × ScanSeconds` 且不超过 1800 秒。默认每 60 秒扫描一次时，新实例建立基线并取得两次提交观测通常需要第三次扫描；原 120 秒通用阶段预算不足以覆盖该过程。其他动作仍使用 `StageTimeoutSeconds`，Verify 截止时间不会随观察延长。这些仍是软件试验初值，真实学习、保压与保存时长需现场测量后定稿。

Claim 同时冻结当时的快照时效窗口，用于正常恢复完成及旧 Guard 退出后的交接。缺少新鲜通道证据时，不能仅凭累计提交次数完成；有足够新鲜证据的已运行主实例可在旧 Verify 动作期限之后交接，保留原运行令牌，不因此发出新恢复动作。

### 计划任务归属核验

安装、覆盖升级及卸载前，安装器核验现有安装登记和版本目录的包文件，再检查根任务目录下的产品任务 XML。命令、工作目录、settings/journal 参数、SYSTEM 主体及执行上下文必须与登记安装一致，且只能有一个 Exec 动作。同名任务缺少登记、归属不匹配或查询失败时中止，保留任务供人工核查；不能通过 -Force 绕过该检查。任务 XML 测试已覆盖 16 项，未据此声称实际 SYSTEM 安装已通过。

此检查解决任务误覆盖/误卸载问题。两项任务和安装登记现已通过同一安装事务更新：修改前持久保存旧任务 XML 与登记原始字节，失败时逆序回退；回退报错或证据写入失败保留维护阻断。生产事务函数的模拟调度边界测试通过 8/8；JXCQ 真实任务故障回退、首次安装、升级和卸载 4/4，证据为 `artifacts/RemoteGuardInstallLifecycle/20260908-1150-pass/results.json`。当前发行阶段仍为 ObserveOnlyCommissioning，自动执行任务保持禁用。

install-transactions 目录保留 Prepared、Committed、RolledBack 或 RollbackFailed 证据。安装进程被终止或断电后，维护文件可能仍存在；必须先核对任务实际 XML、安装登记及旧目录后处理，不能仅按时间删除维护文件。脚本不会为了回退删除共享 RecoveryControl 授权、运行数据或版本目录。

### 没有活动事务时的执行者失联

执行任务取得互斥后向共享权威登记 ExecutionWorker 身份，每次执行步骤返回后更新进度，不通过独立后台线程刷活。使用 OwnerLeaseSeconds 作为进度租期，用户试验的 60 分钟监督期限不受这些写入续期。

扫描存在恢复需求且无未释放事务时，可对准确存活、租期已过的执行者登记退休，并通过准确进程句柄回收 Guard；正式安装归属、维护和停止门禁仍生效。退休先提交则旧执行者不能 Claim，Claim 先提交则由活动事务退休入口处理。重新调度在后续扫描进行。未知身份、首份记录缺失或存储不可用时不猜测 PID，也不终止主程序、安全代理或看门狗。

### 自动执行前的主程序包预检

后续正式自动恢复包使用恢复模式时，安装器要求已批准的 FORMAL_RELEASE 主程序包，核对九个恢复相关组件的版本、程序集名称、哈希及两侧 RecoveryControl 字节一致性，并检查协议版本和正式启动标记。此项检查发生在共享授权注册之前。暂存构建或 dirty 候选不能直接用于自动执行开放。

这只证明磁盘文件的身份关系，不证明运行中的 Supervisor、SessionAgent 已完成匹配启动；真实安装态身份和安全交接仍需验证。目前打包器仍输出观察阶段，不能通过改写本说明或仅修改 JSON 就视作正式批准。
