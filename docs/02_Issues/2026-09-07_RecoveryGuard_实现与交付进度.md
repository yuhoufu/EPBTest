# RecoveryGuard 实现与交付进度

日期：2026-09-07。工作分支：`codex/i0046-v2.17.2.0`。

目标保持为：按照[实施方案](../外部恢复兜底检查器_实施方案.md)完成外部恢复兜底，迭代大版本，生成经过验证的安装包并补齐交付文档。**本文是实施中的记录，不是交付完成或现场验收报告。**

## 已实现的基础

新增三个项目，已加入 `TfTest.sln`：

| 项目 | 当前内容 | 当前边界 |
| --- | --- | --- |
| `RecoveryControl` | .NET Framework 4.8 独立契约库；运行意图、观察快照、接管事务、启动预留、状态文件提交与判定策略 | 仅依赖 Framework；尚需接入所有现有执行者 |
| `MTTFTest.RecoveryGuard` | 独立命令行程序；注册、检查、读取状态、按授权与版本执行人工停止 | 自动执行适配器未接入；不会据此进行强杀、硬件操作或自动拉起 |
| `RecoveryGuardTests` | 独立契约回归，包含真实多进程竞争与实际 Windows 启动身份探测 | 不代表主程序、Supervisor、SessionAgent 已完成端到端接入 |

Guard 使用自身交付目录中的 `MTTFTest.RecoveryControl.dll`，不从业务 `Current` 加载 `Watchdog.Protocol`、Controller 或 NI DLL。共享的只是小型契约及提交实现，不引入业务运行组件依赖。

已落地的约束：

- 注册身份、运行授权身份、人工意图版本、进程 PID/启动时间/路径/系统启动 GUID 分别验证。
- 所有权变更与启动预留使用同一安装范围的短互斥；操作系统查询和硬件动作不在提交锁内。
- 权威状态采用校验封装及先写提交头、再原子替换数据的方式。中途断电导致不一致时拒绝旧授权，不读取 `.bak` 作为自动续测授权。注册重试不覆盖已有状态；缺失权威记录不悄悄生成新授权。
- 只有真实逐通道样本及控制/提交推进才能建立监督关系；快照时间和发布序号增长不能替代业务进展。单通道停滞不会被另一通道推进掩盖。
- 长步骤必须有固定阶段期限和真实采样；对同一阶段不断推迟截止时间会被拒绝。DAQ 代次变化后的计数重置本身不算恢复进展。
- 单卡钳永久隔离后，继续判断其余参与通道；已观察到全部完成或无可续测通道后，主程序退出也不能退回较旧的参与集合。
- 监督中断达到 **60 分钟**后拒绝续接旧事务；持续监督下的冷却重试可超过 60 分钟，保留原业务未恢复起点。
- 启动先预留、再一次性消费、再记录创建结果。停止发生在创建过程中时，仍可记录新 PID，但禁止绑定续测和输出授权。
- 接管代次使旧主程序令牌失效；即使旧主程序读到了新代次，也不能借此通过接管期间的输出检查。
- 所有者租约到期不足以接管，必须另外证明旧所有者已退出。续租不刷新阶段截止时间，取消事务也不自动宣告硬件所有权已释放。
- 恢复完成要求每个参与通道的新业务提交证据，不能只依据窗口/进程出现。停用 Guard 不直接撤销当前合法主程序的正常运行权，过期限制作用于后续自动恢复准入。

这些约束目前在共享库与其回归测试中成立；尚不能据此声称现有主程序、看门狗和全部输出入口已经受同一协议约束。

## 主程序与真实进度接入

已加入 `RecoveryGuardRuntime`：在检测到共享契约目录时接入批次授权，后台发布快照，并处理人工停止、暂停和完成。目录存在但权威记录损坏不会退回未注册模式。尚需完成所有输出及启动执行者的统一门禁，不能仅凭此桥接启用自动执行模式。

- 冷启动在控制器建立安全基线后、液压与电源使能前进行外部准入。人工开始、人工继续及自动续接使用不同来源；以控制器实际批次 RunId 绑定，避免沿用传入检查点中的旧子运行身份。
- 人工停止先在原无文件 I/O 屏障发出独立事件并排队持久化；检查点原有落盘路径同时尝试权威撤销，失败仍保留本地停止处理和重试。
- 人工暂停在控制器接受请求时发出独立暂停通知，不等全部卡钳完成当前圈才禁止自动恢复；原物理暂停流程继续执行。同进程人工继续在供电前取得更高意图版本，继续失败重新保持暂停。
- 成功继续后的检查点消费不再被误译为停止；暂停后关闭窗口保留暂停语义。迟到的旧暂停/停止请求不能覆盖后来的继续授权。暂停状态保留原通知版本，允许独立接收者识别随后发出的停止信号。
- `EpbDiskWriter` 从 SQLite 成功提交点发布逐通道不可变缓存。批量事务回滚不发布成功，历史重放不计为本次进程的新进展，读取缓存不取得数据库锁。主程序快照结合真实 DAQ 处理序号、控制完成及正式提交，不在发布线程查询活动数据库。

这部分尚无真实 NI/电源/液压现场验收。当前程序不包含 CAN 硬件，CAN 不属于验收范围。I0050 液压恢复状态发布一致性没有纳入本轮改动。

已加入授权与 Watchdog SessionId 绑定：只有当前授权、实际 RunId 和准确主程序身份才能登记会话，旧会话不能申请新人工试验的授权。主程序在批次准入时读取已经 Attached 的会话进行绑定。

## Supervisor、SessionAgent 与启动门禁

- Supervisor 为注册了 Guard 的安装签发当前接管身份。恢复请求须匹配已绑定的看门狗会话、当前人工意图及监督资格；普通界面启动也须避开未释放所有权的接管事务。
- SessionAgent 启动契约从 schema 7 升为 **schema 8**，使用新的请求标记、管道名和签封熵；恢复用途及外部接管身份随启动凭证签封，旧凭证不能直接混用。看门狗自身协议仍为 schema 7，二者分别记录。
- `Watchdog.Protocol` 只携带有界且签封的契约文本，由使用外部状态的组件解析；没有通过 Protocol 向 SafetyAgent 增加 RecoveryControl 的传递依赖。
- SessionAgent 在创建前重新核对接管身份，恢复启动必须先原子消费共享预留。新主程序在配置/UI 初始化前核对共享创建记录及准确 PID、启动时间、路径和系统启动身份；停止或代次变化后拒绝准入。
- 同一恢复操作已经创建且共享记录完整时，Supervisor 复核原实例并返回已有结果，不重复创建。未消费预留过期可以释放名额，已消费但创建结果不确定不能按过期直接重新启动。同一准确创建回执可以幂等写回，不能替换为另一进程。
- 新增 `RecoveryLaunchReconciler`：共享状态停在 Consumed、本地已有 Started 证据时，核验原始签封、操作/会话/许可/随机数、授权版本/代次、可执行文件身份、创建时间及系统启动身份，再补记创建。停止期间允许补记事实，但不得更改停止意图。证据缺失保持不确定，篡改明确拒绝。
- 已创建实例尚未确认退出时，阻止不同操作编号再次创建。确认退出的进程查询在权威锁外完成，随后按同一准确身份提交 Exited；不能按租约到期、迟到响应或 PID 名称猜测退出。共享创建回执失败时仍尝试保存本地创建证据，供下一次对账。
- 尚未覆盖“进程已经创建，但两份创建结果都未写出”的全部自动对账；此情况继续保持不确定，不能盲目重发。健康任务协作、全部输出门禁和 Guard 执行器也未完成。本节不是端到端自动恢复验收结论。

## 接管互斥与恢复启动完成状态补充

- 普通看门狗在 Guard 尚未交还接管权时，不能通过读取最新代次获得新的启动资格。Guard 专用资格入口校验事务、代次、准确所有者和租约，并限定 Launch 阶段；该入口尚待生产执行器接入。
- 主程序后台观察到属于本授权且高于自身令牌的接管代次后，调用既有控制器外部撤权入口。该动作不改写为人工停止，也不生成断能已确认的凭证。
- 新进程在恢复预检中确认全部授权通道的持久化剩余圈数为零时，即使尚未建立运行租约，也通过自身 Started 创建回执、原 RootRunId/RunId 与配置身份发布 Completed。旧进程、旧令牌、不同运行链及配置不得关闭当前授权；已发生的人工停止不被迟到完成覆盖。
- 上述“业务已完成”使接管进入 Cancelled，不冒充取得新业务提交的恢复成功，也不自动交还硬件所有权。Completed 事务的交还接口已有证据检查，但取消事务收口和实际执行链仍须补齐。
- 此次补充晚于下述独立观察包的构建；该包不包含本节最新修改，不能据其哈希声称已经交付这些改动。

## Supervisor 专用 Guard 启动入口

新增 Framework-only `RecoveryGuardSupervisorProtocol` 和 Supervisor 生产管道分支。Guard 请求仅包含请求、事务、代次、所有者、启动操作及安全权威记录标识，不携带业务 DLL、程序路径或任意命令行。Supervisor 使用 Windows 管道客户端 PID 与准确进程身份核实调用者，随后检查当前接管所有者、Launch 阶段、旧主程序已退出、登记会话及权威安全完成证据。

字段、处理顺序、超时对账及当前边界详见[Supervisor 启动协议](../RecoveryGuard_Supervisor启动协议.md)。

启动规范从既有持久化 LaunchIntent 读取，逐项绑定 RunId、旧 PID/启动时间、会话、许可代次、许可标识/摘要、启动操作及程序哈希；另校验冻结安全配置。通过后复用普通 Supervisor 的同一启动锁、安装级十分钟三次预算、创建回执对账和 SessionAgent，锁内再次检查 Guard 所有权。响应丢失不等同于未创建，仍须按同一操作对账。

接管中的启动预留与最终消费还会重新核对 Launch 阶段、阶段截止时间和所有者租约。不能仅凭稍早签发的凭证在进入冷却、阶段到期或租约到期后继续创建。已有创建事实仍按回执对账，不因动作期限过去就删除或当作未创建。Guard 自身安装维护标志在专用入口及共用启动锁内均检查。

当前范围仅覆盖**已有可信安全完成记录与已准备启动规范**的接管启动入口；尚未让 Guard 自动创建安全交接、修复失联协调者或重新准备失败许可。`--check` 仍未连接完整执行器，不能因本入口存在就开启自动模式。还需完成真实 SessionAgent 正向启动、事务阶段切换和已绑定业务提交的端到端验收；新增真实管道测试目前验证的是伪造客户端身份被拒绝。

## 恢复准入等待与预算调用链复核

新主程序可能先于 Guard 收到启动响应到达批次准入。现在它在绑定准确启动回执及运行身份后，通过异步等待检查持久 Verify 阶段；Launch 阶段不放行设备输出，250 毫秒轮询不占用 UI 或采集线程。停止、意图/进程不匹配、转入其他阶段、阶段或租约到期会拒绝准入，取消令牌可终止等待。正常 sidecar 恢复没有未交还的 Guard 事务时沿用既有准入。本次没有用延长阶段期限或猜测启动耗时规避竞争。

**更正此前的预算缺口判断：** 完整调用链表明，当前 `ShouldScheduleUnattendedProcessRestartRetry` 已不按累计尝试次数取消重试，所以上一轮指出的“预算耗尽进入 Disarm”分支不会因三次失败触发。`ReleasePendingRestartForRetry` 保留 Armed，下一次 `TryRegisterRestart` 返回 `RecoveryCoolingDown`、保存 NextRetryUtc 并保留外部 Run 意图。现已通过这两个真实入口的回归验证，同时验证冷却期间人工停止仍撤权；清除了防御分支中误导性的预算耗尽原因和过时注释。并未新增绕过内部限频或恢复已停止授权的规则。Guard 全链路冷却重试仍需随执行器完成，不能以此项既有内部行为的验证替代它。

## 大版本迭代

统一版本源 `Build/UnattendedVersion.props` 已改为 **3.0.0.0**。主程序、Supervisor、SessionAgent 和 RecoveryControl 的 Debug 文件版本已实际核对为 3.0.0.0。交付脚本同步目标版本，主程序组件清单增加 RecoveryControl，identity 增加 `sessionAgentSchema=8`，E2E 包补齐该 DLL。修改过的构建/校验脚本语法检查通过，尚未生成 3.0.0.0 正式安装包；历史日志仍按其实际执行时点解释。

## 独立观察包与部署脚本

已生成 [RecoveryGuard 3.0.0.0 独立观察包](../../artifacts/RecoveryGuard-3.0.0.0-observe-installer.zip)，SHA-256：`3c3745ff7d849be28b959ea0045d0d9ba0127d8369c0cc124cb6047cc22a34fc`。包身份明确为 `ObserveOnlyCommissioning`、`automaticExecutionReady=false`，源码仍为有改动工作区；它不是完整业务自动恢复的最终安装包。

- `New-MTTFTest-RecoveryGuardPackage.ps1` 独立构建 Release、校验二进制版本、生成文件哈希与 ZIP；不加载业务运行组件。
- `Install-MTTFTest-RecoveryGuard.ps1` 默认只读 Validate；显式 Install/Uninstall 才操作系统。首次注册要求主程序退出、旧会话收口；更新使用独立版本目录，保留配置及共享授权。任务为 SYSTEM、分钟检查、IgnoreNew；有未释放接管权时拒绝切换。
- 程序增加无注册副作用的配置校验入口；已安装检查任务显式指定独立日志目录，记录检查时间、决策及失败，按 8 MiB 当前文件与上一段文件轮换。
- 包校验及 ZIP 内容验证通过，尚未执行真实 Windows 任务安装/卸载或权限变更；不能把 Validate 结果当作安装态验收。目录权限、操作步骤和边界见[独立部署与校验](../RecoveryGuard_独立部署与校验.md)。

## 配置与确认事项

### 持久执行引擎与可取消通信进展

独立客户端服务端身份核验已接入 `RecoverySupervisorPeer`：全部公开管道调用在发出请求前和接受响应后，以 Windows 服务管理器返回的 `MTTFTestSupervisor` 运行中独立服务 PID 核对真实管道服务端 PID，并复核身份稳定。服务缺失、停止、PID 不匹配或核验期间更换均拒绝；同名管道不再足以提供可信安全响应。依赖仅为系统 API，不需要业务 DLL。测试使用真实隔离命名管道和受控服务 PID 查询，覆盖允许、缺失、不匹配及检查期间变化，同时比对实际服务类的服务名；未安装或启动真实服务。

服务端身份核验定向回归 **31/31** 通过，日志 `artifacts/recoveryguard-service-peer-targeted.log`；Debug/x86 构建通过，日志 `artifacts/recoveryguard-service-peer-build.log`。

同轮契约/引擎 **65/65**、磁盘 **70/70**、电源 **19/19**、独立目录 **10/10** 通过，日志分别为 `artifacts/recoveryguard-service-peer-contract.log`、`artifacts/recoveryguard-service-peer-disk.log`、`artifacts/recoveryguard-service-peer-power.log`、`artifacts/recoveryguard-service-peer-isolation.log`；独立性证据 `artifacts/recoveryguard-isolation-0e54390a57ef4a93bed72462dace69c9/results.json`。

服务端身份核验版本的完整控制回归终态 **793/793** 通过，日志 `artifacts/recoveryguard-service-peer-adaptive.log`。完整恢复执行链和最终安装包仍未完成，不以本轮回归代替交付验收。

Guard 安全执行生产入口已接入：`ExecuteSafetyAsync` / `HandleGuardSafetyExecuteConnection` 对当前 Guard 的安全权威进行核验，从可信记录生成 SafetyAgent 参数，调用统一 `SupervisorOwnedSafetyAgent.RegisterOrGet`。创建前回调在启动互斥内复核 Guard 所有权、许可和回执版本；已有进程准确存活时仅接续。`ObserveRecorded` 返回同一次验证得到的进程身份，避免重新读取另一版记录后误判退出。完整安全证明与执行进程确切退出同时成立才返回 Completed，安全回执完成但进程仍活着时保持 Pending。

执行入口目前仍要求准确旧主程序已退出、可信 Approved 许可和本 Guard 事务准备的安全权威。旧 sidecar 交接权威接续、许可自主重建、Program 完整动作适配器和安装任务仍待完成；客户端服务端身份核验已补齐，服务安装态验证另行完成。不能用这一个服务端入口替代完整执行链或现场验收。

本次定向回归 **30/30** 通过，日志 `artifacts/recoveryguard-safety-execute-verified-targeted.log`；Debug/x86 构建通过，日志 `artifacts/recoveryguard-safety-execute-verified-build.log`。包含安全执行请求伪造管道 PID 拒绝、可信启动参数和路径限制、安全完成须等待进程退出，以及使用测试 EXE 的实际隔离子进程单次创建、重复接续、退出身份保留和最终准入拒绝。测试没有运行真实 SafetyAgent 或访问硬件。测试扩展首轮错误引用了 Supervisor 私有进程类型，已改为从真实返回对象读取身份；初始失败日志 `artifacts/recoveryguard-safety-execute-lifecycle-build.log` 保留。

同轮契约/引擎 **65/65**、磁盘 **70/70**、电源 **19/19** 通过，日志分别为 `artifacts/recoveryguard-safety-execute-contract.log`、`artifacts/recoveryguard-safety-execute-disk.log`、`artifacts/recoveryguard-safety-execute-power.log`。独立 Debug 目录验证 **10/10** 通过，证据 `artifacts/recoveryguard-isolation-fba30f90ad484ccc807a11795974ca11/results.json`，日志 `artifacts/recoveryguard-safety-execute-isolation.log`。未安装服务、注册生产任务或生成最终安装包。

安全执行入口及真实隔离子进程测试版本的完整控制回归终态 **792/792** 通过，日志 `artifacts/recoveryguard-safety-execute-adaptive.log`。这证明本轮软件回归通过，不表示完成真实安全硬件执行、安装态恢复或完整交付目标。

SafetyAgent 接续与启动互斥补充：`SupervisorOwnedSafetyAgent.TryAttachPersisted` 原来吞掉所有读取/查询失败，再按“未接续到进程”继续创建，已改为明确拒绝损坏记录、身份绑定变化、未知进程身份和未确认的 LaunchIntent。Started 记录验证准确 PID、启动时间、路径、启动代次及请求绑定后接续；旧记录缺少启动代次时仍验证 PID/启动时间/路径。Supervisor 仍存活且持有准确创建句柄时，可以补记丢失的 Started 写回，不重复启动。Supervisor 已重启且只有未确认 Intent 时，仍需额外创建证据，不能推定没有创建。

新建 SafetyAgent 通过按状态目录派生的全局命名互斥串行化，等待上限 3 秒；持锁后重新对账本操作，并检查其他交接的启动记录。其他进程须准确退出，仍活着或未知均拒绝新建；最多检查 4096 条、每条 64 KiB，超过时明确报告待整理，不无界扫描。该检查不结束任何存量进程；后续需将 Guard 的安全执行入口接到此统一创建路径，并补齐已有交接的权威接续。准备入口与整个 Guard 执行器仍不能据此视为交付完成。

SafetyAgent 接续定向验证：解决方案 Debug/x86 构建通过，日志 `artifacts/recoveryguard-safety-ownership-build.log`；定向测试 **26/26** 通过，日志 `artifacts/recoveryguard-safety-ownership-targeted.log`。新增 `SafetyAgentReconciliationTests` 5 项使用隔离目录及测试进程的实际身份，调用生产接续/存量检查方法，验证准确接续、损坏及未确认记录拒绝、保留创建句柄时补记、PID 复用隔离和跨交接冲突拒绝；未调用生产 Process.Start、未创建真实安全执行者，也未宣称已验证安装态并发互斥或硬件操作。

同轮独立契约/引擎 **65/65**、磁盘 **70/70**、电源 **19/19** 通过，日志分别为 `artifacts/recoveryguard-safety-ownership-contract.log`、`artifacts/recoveryguard-safety-ownership-disk.log`、`artifacts/recoveryguard-safety-ownership-power.log`。

SafetyAgent 接续与跨交接启动检查修改后的完整控制回归终态 **788/788** 通过，日志 `artifacts/recoveryguard-safety-ownership-adaptive.log`。未生成最终安装包，实际安全执行入口及安装态验收继续推进。

新增 Guard 安全准备生产入口：Supervisor 实际管道分派已接入 `HandleGuardSafetyPrepareConnection`，独立客户端提供 `PrepareSafetyAsync`。从登记项目读取冻结种子及可信 Approved 许可，经 SafeStop 所有者/阶段/期限核验、准确旧主程序退出、程序摘要及快照绑定检查后，提交 Accepted 安全权威；重复请求按稳定交接身份核对现有权威，损坏记录不覆盖。该入口不信任客户端传入路径或安全完成位，也不冒充 sidecar。详细协议见[Supervisor 启动协议](../RecoveryGuard_Supervisor启动协议.md)。

本入口尚不启动 SafetyAgent，准备回执明确不包含断能和持久化完成证明。自主创建缺失许可、启动安全执行者及检查其退出、接入 Guard 生产动作接口仍未完成；现有安全准备依赖可信 Approved 许可，不能宣称完整恢复已摆脱旧 sidecar 依赖。首次构建发现旧版 PermitRecord 与当前 AuthorityRecord 类型不一致，已按实际持久权威类型修正，保留初始失败日志 `artifacts/recoveryguard-safety-prepare-build.log`。

安全准备定向验证：Debug/x86 解决方案构建通过，日志 `artifacts/recoveryguard-safety-prepare-verified-build.log`；共享契约/执行引擎 **65/65**，日志 `artifacts/recoveryguard-safety-prepare-contract.log`；主程序/Supervisor/创建对账 **21/21**，日志 `artifacts/recoveryguard-safety-prepare-verified-targeted.log`。新测试包含真实隔离管道拒绝伪造 Guard PID、准备不伪造安全完成、稳定交接标识、错误运行/已撤销许可/快照绑定拒绝，以及 SafeStop 读取的阶段/所有者/期限/停止校验。正向准备策略使用模拟快照，未验证真实安装态 SafetyAgent 执行。

同轮磁盘回归 **70/70**、电源回归 **19/19**、独立目录验证 **10/10** 通过，日志为 `artifacts/recoveryguard-safety-prepare-disk.log`、`artifacts/recoveryguard-safety-prepare-power.log`、`artifacts/recoveryguard-safety-prepare-isolation.log`；独立性证据 `artifacts/recoveryguard-isolation-bc08a9f69c0a457d97517e75161f2c55/results.json`。未注册系统任务或服务，历史观察 ZIP 未重打包。

安全准备入口版本完整控制回归终态 **783/783** 通过，日志 `artifacts/recoveryguard-safety-prepare-adaptive.log`。此结果覆盖本次代码回归，不代替正向安装态安全执行和最终安装包验收。

终态交还补充：正常执行器现在通过 `Advance(..., releaseCompletedOwnership: true)` 在同一权威提交中完成 Verify → Complete 和所有权释放，避免“已 Complete、尚未交还”的中间状态被输出门禁解释为仍由 Guard 接管。不能将业务已经验证成功后的两次独立写入视为原子操作。

对已有 Complete 但未交还的事务，增加 `ReconcileCompletedTakeover`：旧所有者须确切退出且租约到期，当前主程序须准确存活；短事务内重新核对双方身份、事务代次、当前授权、监督时效、已有业务验证证据以及不存在待确认启动消费。该操作不变更接管代次、不授予新执行者动作权，不使当前主程序的运行令牌失效。未知进程状态、人工停止及身份变化均拒绝。Cancelled 及其他未完成阶段的收口仍需另行实现，不能套用该终态入口。

终态交还验证：解决方案 Debug/x86 构建通过，日志 `artifacts/recoveryguard-atomic-handback-build.log`；独立契约/引擎 **64/64**（包括一次提交、保持输出准入及进程/停止拒绝测试）、磁盘 **70/70**、电源 **19/19** 通过，日志分别为 `artifacts/recoveryguard-atomic-handback-contract.log`、`artifacts/recoveryguard-atomic-handback-disk.log`、`artifacts/recoveryguard-atomic-handback-power.log`。

终态交还修改后的完整控制回归 **781/781** 通过，终态日志 `artifacts/recoveryguard-atomic-handback-adaptive.log`。未执行实际服务/计划任务变更或台架硬件动作；最终安装包仍待完整执行链和安装态验证完成后生成。

已增加独立 Guard 内部的 `RecoveryExecutionEngine`，实现接管登记、阶段推进、所有者续租、冷却重试、动作预算和 Verify 业务提交验收。接替已退出所有者时递增代次并转入冷却对账，不把旧代次 Verify 证据直接算作新代次成功；RecoverExited 模式在主程序仍存活时不派发 SafeStop/Retire。成功交还时清除原故障连续确认计数，重复交还不清除后来新故障的确认；已释放的历史事务不阻止下一次接管。

引擎通过内部动作接口测试了完整阶段推进、虚假启动回执拒绝、动作中的人工停止、动作超时、持续监督超过 60 分钟的重试、观察模式、所有者接替及 RecoverExited 边界。`artifacts/recoveryguard-engine-mode-tests.log` 确认 **61/61** 通过。动作接口当前仍为测试适配，尚无完整生产动作实现；Program 的分钟检查仍明确报告 `RecoveryExecutionAdapterNotInstalled`。这些测试不能当作实际硬件恢复或最终执行器交付。

Supervisor 客户端新增 `LaunchAsync(request, cancellationToken)`，以异步管道连接、读写实现取消，保留 5 秒连接和 20 秒交换上限。超时/取消仅终止客户端等待，不能推定启动未发生；后续须对账同一操作。新增真实隔离管道测试验证部分响应停滞时可取消、分段响应可完整解析。定向回归 **19/19** 通过，证据 `artifacts/recoveryguard-cancellable-transport-targeted.log`；对应解决方案 Debug/x86 构建通过，日志 `artifacts/recoveryguard-cancellable-transport-build.log`。未启动真实 Supervisor 服务或改变系统任务。

本次通信改动后，独立契约/执行引擎重跑 **61/61**、磁盘持久化 **70/70**、电源 **19/19** 通过；日志分别为 `artifacts/recoveryguard-cancellable-transport-contract.log`、`artifacts/recoveryguard-cancellable-transport-disk.log`、`artifacts/recoveryguard-cancellable-transport-power.log`。独立 Debug 目录验证 **10/10** 通过，证据 `artifacts/recoveryguard-isolation-7bb7a75086e943a4b928b28e9ec3f029/results.json`。历史观察 ZIP 未在本轮重打包，不包含这些新增实现。

完整控制回归随后终态通过 **781/781**，日志 `artifacts/recoveryguard-cancellable-transport-adaptive.log`。这是上述引擎交还修正与可取消通信代码的最新完整回归；下方历史表中的 779/779 等结果按各自执行时点保留，不代表最终安装态验收。

`MTTFTest.RecoveryGuard/guard-settings.example.json` 是软件试验配置。60 分钟过期阈值来自用户确认；扫描、失联、停滞、首次接管、单步预算与冷却等其他数值是实施阶段的可配置试验初值，不是生产验收上限。配置校验要求普通首次接管窗口覆盖停滞确认与调度等待。

默认 `ObserveOnly`。当前程序即使读取到允许自动执行的配置，也会在需要动作时明确报告 `RecoveryExecutionAdapterNotInstalled`，不将缺失的执行链隐藏为成功。完整交付必须补齐适配器与所有入口的授权复核后，按方案开放对应运行模式。

用户已确认面板按钮只能人工按下，不能作为无人值守自动断能保障；其接线、保持特性和独立反馈能力仍属现场验收事项。没有安全链断能证明时不得强杀占用硬件的主程序；冷却结束只能重新检查条件，不能绕过该限制。该限制不取消其他可以通过已验证安全路径执行的恢复工作。I0050 液压恢复状态一致性修复仍留到用户指定的第二步。

## 已执行验证

| 验证 | 结果与证据 |
| --- | --- |
| 全解决方案 Debug/x86 构建 | 3.0.0.0 创建对账构建通过，日志 `artifacts/recoveryguard-v300-reconcile-final-build.log`。已有 WinForms 未使用变量及 debugger 架构等编译警告仍存在 |
| 独立契约回归 | 最新 **53/53 通过**，包括迟到消费和新主程序准入的阶段/租约/截止时间与撤权检查；日志 `artifacts/recoveryguard-verify-admission-contract-tests.log`。此前阶段测试初始使用五分钟预留而违反既有两分钟上限，已修正测试数据，未放宽生产上限；初始失败日志保留 |
| 主程序桥接、Supervisor 入口与创建对账定向回归 | 最新 **17/17 通过**：桥接/签封/冷却/Verify 等待及停止 9 项、真实隔离子进程创建对账 4 项，以及 Supervisor 协议/安全证据/真实管道身份拒绝 4 项；日志 `artifacts/recoveryguard-verify-admission-tests.log` |
| 完整控制回归 | 最新包含异步 Verify 准入与预算调用链回归的版本 **779/779 通过**，日志 `artifacts/recoveryguard-verify-admission-adaptive-tests.log`，Debug/x86 解决方案构建日志 `artifacts/recoveryguard-verify-admission-build.log`。此前创建对账版本首轮曾出现原 DAQ 跨设备异步 OFF 提交 113.246 ms、超过 20 ms 门限，门限未修改，单独复跑 112/112 和随后全量均通过。保留原失败日志，不将未重现等同于已确认根因 |
| 磁盘持久化回归 | 最新重跑 **70/70 通过**，包含提交缓存、重复提交、回滚及历史重放测试；日志 `artifacts/recoveryguard-verify-admission-disk-tests.log` |
| 电源调试器回归 | 最新重跑 **19/19 通过**；日志 `artifacts/recoveryguard-verify-admission-power-tests.log`，TRX 在 `artifacts/recoveryguard-verify-admission-power-tests/` |
| 新共享协议独立性 | 最新 Debug 独立目录 **10/10 通过**；加入 Supervisor 管道契约后仍不需要业务 Protocol DLL，证据 `artifacts/recoveryguard-isolation-befb62abe8444c48b062be14e301732b/results.json`，日志 `artifacts/recoveryguard-supervisor-isolation-tests.log` |
| 独立目录实际执行 | 独立 Release **10/10 通过**，含业务组件缺失、无副作用配置校验、失败日志及轮换；证据 `artifacts/recoveryguard-isolation-fefe666c32804a769961bf55aadae57e/results.json` |
| 独立包验证 | **8/8 通过**，含 ZIP 与目录逐项哈希一致及 7 个正常/拒绝场景；日志 `artifacts/recoveryguard-package-validation-final-tests.log`。没有执行真实安装 |

独立目录验证通过 `Tools/Test-MTTFTest-RecoveryGuard.ps1` 执行，包括主程序缺失、无业务 DLL、读取状态、重复注册、配置版本不支持、权威状态损坏。未修改本机真实安装、计划任务、服务或现场运行状态。

本轮全量回归发现 `StartupRetryRevocationCannotReauthorize` 的调度假设过窄：控制器先发布 Recovering，再申请 worker 启动。撤权若落在两者之间，会得到明确的“启动许可被拒绝”，而测试仅接受已启动 worker 的取消异常。测试已同时接受这两种特定安全结果，仍验证禁止重新授权，并新增 owner 已释放断言；未修改生产启动重试逻辑来迁就测试。

## 后续必须完成的工作

1. 完成主程序全部输出入口门禁及授权接入的异常路径审查；现有批次准入、暂停/继续与停止桥接不能代替完整入口覆盖。补齐真实控制器调用链和恢复启动验证。
2. 验证快照映射在全部业务阶段的时效与进展语义；已从持久事务成功点提供缓存计数，后续不能退回在发布线程调用数据库查询或以内存完成数冒充提交。
3. 覆盖 Supervisor、sidecar、SessionAgent、现有健康任务、自动启动任务、主程序启动和输出使能入口。核对在途创建结果，并补齐取消/完成后的所有权收口以及失联所有者接替。
4. 接入受监督的安全执行器，完成 Claim → SafeStop → Retire → Launch → Verify；复用既有 SafetyAgent 和安装级重启预算。处理事务各阶段中断、冷却及准确实例重建，不额外建立独立重启次数池。
   - 专用启动入口现已接入，但仍需 Guard 自主准备可信恢复启动规范的路径，不能永久依赖旧 sidecar 已经准备好 LaunchIntent。主程序已增加等待持久 Verify 的准入门禁，仍须将 Guard 执行器实际阶段切换接入并进行端到端验证。
   - 安全交接入口核查：`SupervisorServiceRuntime.BeginSafetyHandoff` 和 `ValidateSafetyAgentRequest` 均要求请求方为已登记 sidecar。独立 Guard 安全准备及执行入口现已另验管道真实客户端、SafeStop 所有权与当前运行，执行器创建复用统一互斥与进程对账，安全完成须等待准确进程退出。已有旧 sidecar 交接权威的选择与接续仍须补齐，不能每次准备都忽略原交接另起执行者。当前准备/执行路径仍依赖 Approved 许可，不能宣称已完全消除旧 sidecar 依赖；Program 完整动作适配器及客户端服务端身份核验仍待接入。
   - 内部三次失败进入冷却的行为已通过真实调用链验证，之前对 Disarm 分支的判断已更正；继续审查其他内部失败及取消路径与人工意图的区别，不能简单把所有 Disarm 都忽略。
5. 完成独立安装/卸载的真实安装态验收、与业务部署的维护/回退联动，并按执行器完成情况开放自动模式；当前已生成观察包及脚本，不能替代这部分验证。
6. 对已迭代为 `3.0.0.0` 的统一版本重新构建、核对所有组件身份、独立 Guard 交付布局及安装测试；不能将基础项目 Debug 输出当作新版本正式包。
7. 完成三套既有回归、Guard 端到端故障注入、正式构建和包身份校验，最终生成安装包及安装说明。真实 NI/电源/液压验收范围单独记录；当前程序不包含 CAN 硬件。

未完成以上范围前，保持实施目标进行中，不生成“已完成外部自动恢复”的交付结论。

## 启动准备接口接入（本轮）

新增 Supervisor 的 Guard 启动准备端点，客户端不提供可执行文件、命令行或许可。服务从登记的存活 sidecar 会话、当前授权的业务快照和既有严格许可构建启动规范，通过 `PrepareLaunchIntent` 提交；准备动作不创建主进程。快照中永久隔离、完成、人工排除以及未出现的通道均不进入续测范围，其他 RunId 的快照被拒绝。

准备和实际启动入口均检查安全回执与 SafetyAgent 持久进程记录，要求准确退出。实际启动再次比较登记的 sidecar 身份及通道范围与已冻结参数；会话变化不能直接复用旧参数。重复准备只有在未消费且规范相同时返回相同操作标识，已消费的启动需要结果对账。

本轮仍未完成：Guard 生产动作端口接线、失联 sidecar 重建、自主许可创建与失败重试、旧 sidecar 安全动作与主进程创建的最终并发隔离。安全执行者退出检查不能单独证明这些竞态已经全部解决。原 ObserveOnly 安装 ZIP 未更新，不是本轮代码或最终安装包。
本轮验证：解决方案 Debug/x86 构建通过（`artifacts/recoveryguard-launch-preparation-build3.log`）；定向测试 32/32；Guard 契约 65/65；磁盘回归 70/70；电源调试器回归 19/19。全量 AdaptiveControlTests 已以退出码 0 结束，结果见 `artifacts/recoveryguard-launch-preparation-adaptive.log`。构建保留既有架构不匹配及过时 API 等警告；本轮没有现场硬件测试、服务安装或包生成。
## 生产动作适配器与执行入口（本轮）

新增 `SupervisorRecoveryActions`，通过独立契约的真实 Supervisor 客户端调用安全准备、安全执行、启动准备和启动接口。每次只进行一次有界通信；安全权威和启动操作标识先写入共享接管事务，再进行下一动作。标识不能直接覆盖，跨接管代次的旧动作必须先对账。

Launch 阶段先读取共享 Started 回执，匹配当前授权和准确存活进程后返回创建证据；响应丢失不直接创建第二个进程。引擎仍要求参与通道实际提交后才能完成 Verify。SafeStop 对活动主程序保持阻塞，不能把人工面板按钮或进程存在当作独立安全证明。通信后的绑定和推进仍检查当前人工意图与接管权。

新增 `--execute` 独立执行入口，使用全局进程互斥及现有恢复引擎；生产执行拒绝非安装权威目录，隔离目录仅允许 ObserveOnly。执行失败日志将动作结果标为需对账，不再一律声称未执行动作。

尚未交付：分钟任务自动派发/监督执行进程、无响应执行者的处理、失联会话重建、自主许可重试、旧安全动作接管互斥的完整闭环。安装脚本仍只注册 ObserveOnly 检查任务；`--check` 在需要执行时明确报告 WorkerDispatchNotConfigured。本轮新增执行入口不等于完整无人值守链已启用，不生成最终安装包完成结论。
本轮已完成验证：Debug/x86 解决方案构建通过（`artifacts/recoveryguard-production-actions-build2.log`）；Guard 契约 69/69、磁盘 70/70、电源调试器 19/19、独立 CLI 12/12。隔离证据：`artifacts/recoveryguard-isolation-12f64029ddfa4eee9696d2774ea1fa14/results.json`。上述过程未注册现场任务、未启动真实恢复服务动作，也未生成正式安装包。
全量控制回归已正常结束，退出码 0，结果见 `artifacts/recoveryguard-production-actions-adaptive.log`。

## 独立执行任务部署定义（本轮）

安装器已增加 `MTTFTestRecoveryGuardExecution` 任务定义，与原检查任务分别使用 `--execute` / `--check` 和不同日志目录。两者均配置 SYSTEM、IgnoreNew、开机及分钟触发、StartWhenAvailable；检查任务 45 秒有界，执行任务不受累计调度期限强杀。现阶段执行任务默认禁用，原包仍为 ObserveOnlyCommissioning，不能据此宣称自动执行已交付。

安装/升级/卸载先取得与执行入口相同的全局 Mutex，再检查接管所有权，防止检查完成后出现新 Claim。卸载同步处理两项任务，并去掉以 Stop-ScheduledTask 强制结束执行者的路径；忙碌执行者或未收口事务拒绝切换。

新增 `Tools/Test-MTTFTest-RecoveryGuardTaskDefinition.ps1`，只提取安装器的纯任务 XML 函数，不运行安装器主体或注册任务。6/6 通过，日志 `artifacts/recoveryguard-task-definition.log`，覆盖两种任务行为及相对路径、引号、换行、尾反斜杠拒绝。尚无真实任务注册、升级、卸载或无响应执行者监督的验证。本轮仅改动部署脚本及文档，未改 C#；上一轮构建与回归结果继续适用于未变的二进制源码。
## 主程序与安全执行者创建互斥（本轮）

新增并接入 `SupervisorHardwareLaunchGate`：主程序实际启动与所有 Supervisor SafetyAgent 创建共用原 SafetyLaunch 安装级全局互斥。主程序在锁内核对安全执行者库存均已准确退出，并重读启动权；旧 sidecar 的创建回调也在锁内核对 Guard 接管及最新人工意图。

未释放接管权时，旧自动恢复安全请求不得创建另一占用者。人工停止/暂停/完成或独立通知确认后仍可执行 Forbidden 安全停机；单凭旧回执自称 Forbidden 不能覆盖 Run 意图。交接完成后，旧主进程身份的迟到安全请求仍被拒绝。

Debug/x86 构建通过，日志 `artifacts/recoveryguard-hardware-launch-gate-build3.log`；定向回归 34/34，包含竞争创建互斥和人工停止/旧实例边界，日志 `artifacts/recoveryguard-hardware-launch-gate-targeted.log`。本轮未测试真实 NI/电源/液压，未生成正式安装包；CAN 不在当前程序硬件范围内。

尚须完成旧安全交接复用、失联执行者监督、自主许可重试、旧事务收口、失联会话重建和正式安装态验证。共用创建锁不能替代这些剩余链路。
本轮完整回归已结束：控制 796/796、磁盘 70/70、电源调试器 19/19，均退出码 0。日志分别为 `artifacts/recoveryguard-hardware-launch-gate-adaptive.log`、`artifacts/recoveryguard-hardware-launch-gate-disk.log`、`artifacts/recoveryguard-hardware-launch-gate-power.log`。

## 不确定动作的冷却续接（本轮）

修复已绑定动作在通信失败后退回错误阶段的问题。共享事务记录待对账的 SafeStop/Retire/Launch 阶段及代次；同一所有者、同一代次且冷却结束后，恢复引擎回到原动作阶段，重新核对持久结果。恢复阶段预算不会刷新实质进展或业务提交时间。人工停止、未到冷却期限及代次变化不能通过续接入口。

新增回归将共享启动记录写成 Started 后模拟通信异常：冷却后返回 Launch，生产动作适配器读取原记录进入 Verify，不重复请求启动或重新准备安全交接。另验证冷却期间与人工停止后的准入拒绝。Debug/x86 构建通过（`artifacts/recoveryguard-action-resume-build.log`）；Guard 契约 71/71（`artifacts/recoveryguard-action-resume-contract.log`）。

本项解决已创建结果的响应丢失续接，不等于已完成新一次重启许可创建。过期 Reserved、SessionAgent 已写消费文件但未完成共享消费、已退出的失败启动及跨所有者旧动作仍需分别收口后生成合法的新许可；不能仅延长旧预约期限或删除消费凭据来重复启动。这些继续纳入完整重试闭环。
补充代码核查：Supervisor 现有 `SupervisorOwnedSession.MonitorLoopAsync` 已每秒检查会话存活，并在非维护且未被 `TryRetireSession` 判为终态时调用 `StartFromRecord` 重建退出的 sidecar，失败后等待 5 秒重试。因此“失联组件重建”不能笼统记成全部缺失；待补的是 Guard 与该既有重建链的身份/终态协调、已退休会话的合法新会话建立，以及 Supervisor 自身失联等边界。不得为普通 sidecar 退出再新增一个不共享权威的重复启动者。
本轮三套回归均正常结束：控制回归日志 `artifacts/recoveryguard-action-resume-adaptive.log`，磁盘 70/70（`artifacts/recoveryguard-action-resume-disk.log`），电源 19/19（`artifacts/recoveryguard-action-resume-power.log`）；均退出码 0。

## Guard 退出故障登记与许可签发（本轮）

安全准备不再只接受外部预先签发的 Approved：对已确认主进程退出、无在途启动、同一有效 SafeStop 接管权，可复用严格权威登记退出故障并签发许可。支持 None/Failed，以及进程身份准确匹配的 Attached/Committed；未收口 LaunchIntent/Started、Blocked/Revoked/CircuitOpen 保持拒绝。报告来源标为 RecoveryGuard，报告者字段使用实际 Guard 进程。

RegisterFailureAndDecide 新增带预期 Revision/SHA 的调用形式，在锁内发现并发变化时不提交新故障计数。既有无版本约束调用保持原行为；Guard 使用版本约束，防止与旧看门狗并发签发时额外消耗预算。签发后重读共享意图及许可结果。

Debug/x86 构建通过（`artifacts/recoveryguard-exit-permit-build2.log`），定向测试 35/35（`artifacts/recoveryguard-exit-permit-targeted.log`）。新增用例使用真实临时 Bootstrap/严格文件存储验证签发、准确退出要求、在途消费拦截和过期版本拒绝，不是单纯模拟一个 Approved 返回值。

重要剩余差距：安装级十分钟三次限频与严格权威的累计失败熔断是两层机制。后者来自 Bootstrap 冻结预算，默认创建接口为 8，且 Committed 不清空累计失败计数。因此不能仅依据安装级冷却就宣称满足用户的持续冷却重试要求。本轮保留历史熔断，后续须完成有效运行授权下的新会话/许可链协调，并保留旧熔断历史；不得把原 Blocked 记录直接改回 Approved 或删除消费凭据。
本轮三套回归全部退出码 0：全量控制结果见 `artifacts/recoveryguard-exit-permit-adaptive.log`；磁盘 70/70（`artifacts/recoveryguard-exit-permit-disk.log`）；电源 19/19（`artifacts/recoveryguard-exit-permit-power.log`）。没有现场硬件动作或最终安装包生成。

## 新会话交接记录与检查点准入（本轮）

新增共享 SessionRollover 的预留/激活流程，冻结旧会话、权威摘要、旧动作、原运行及根运行、配置与旧主进程。重复预留保持操作身份，激活保留用户授权和业务运行身份，旧严格权威不被清空。下一次交接归档前一记录，归档不作为授权来源。

主程序检查点消费保持同会话默认要求；跨会话必须通过共享权威的 Activated 记录、原运行/根运行和配置匹配、当前创建实例 Started 记录、Launch/Verify 阶段与监督/人工意图核验。通过后才保存新的检查点会话绑定，项目和 Armed 校验继续保留。

Debug/x86 构建通过（`artifacts/recoveryguard-session-rollover-build2.log`）。Guard 契约 73/73（`artifacts/recoveryguard-session-rollover-contract2.log`），新增重复预留、停止先于激活、没有 Started 证明、错误运行标识及停止后的准入边界。磁盘 70/70、电源 19/19 已通过。

尚未接通 Supervisor 旧会话退休、新会话创建、配置种子迁移与新许可签发的执行流程。执行接线还必须隔离跨会话切换前已发出的迟到动作响应，不能只因 TransactionId/Owner 未变而沿用旧 SafetyAuthorityId。本轮不构成连续熔断重试已完成的证明，最终安装包未生成。
本轮全量控制回归已退出码 0 结束，结果见 `artifacts/recoveryguard-session-rollover-adaptive.log`。磁盘与电源回归日志分别为 `artifacts/recoveryguard-session-rollover-disk.log`、`artifacts/recoveryguard-session-rollover-power.log`。

## 会话切换的迟到动作隔离（本轮）

Guard 生产动作回写和恢复引擎的动作后阶段推进增加 expectedSessionId，在共享锁内拒绝 Reserved 交接期间或会话已改变后的旧响应。该特定拒绝转为继续对账，不结束仍合法的执行者。Supervisor 安全准备收尾、实际创建安全执行者前及 Guard 许可登记复核也使用会话绑定。

Debug/x86 构建通过（`artifacts/recoveryguard-session-fence-build.log`）；Guard 契约 75/75（`artifacts/recoveryguard-session-fence-contract.log`），新增用例覆盖预留/已激活切换后的旧安全回执，以及旧 Completed 响应不能推进新会话阶段。磁盘、电源回归已通过。

下一步执行接线必须为 Reserved 交接提供独立续办入口，不能依赖已被隔离的普通安全动作推进。同时核查发现旧 sidecar 的两处 RegisterFailureAndDecide 调用位于 StrictHostRelaunchOrchestrator，现有 AuthorizeWatchdogSession 只核验会话和运行授权，未包含接管排他检查；在启用新会话持续恢复前还需核对并补齐该许可写入入口的接管互斥。创建锁与迟到回执隔离不能替代许可写入权的协调。
本轮三套完整回归均退出码 0：全量控制日志 `artifacts/recoveryguard-session-fence-adaptive.log`，磁盘 70/70（`artifacts/recoveryguard-session-fence-disk.log`），电源 19/19（`artifacts/recoveryguard-session-fence-power.log`）。

## 旧看门狗故障登记与 Claim 互斥

`RecoveryControlStore.RunLegacyRecoveryAuthorityMutation` 使用安装根目录对应的独立命名锁，把旧看门狗故障登记与 Guard Claim 串行化。登记前核验当前运行授权、监督有效性、停止信号、会话和 Run，以及接管所有权；持有该锁期间不持有共享状态锁，因此严格许可文件 I/O 不阻塞人工停止写入。此入口只允许许可存储操作，进程创建与硬件动作仍须通过各自最新授权检查。

`StrictHostRelaunchOrchestrator` 的两处故障登记均接入该入口。未安装 Guard 时保留原行为；已注册或注册中的共享权威读取/准入失败返回只读 Busy 决策，不新增失败计数、不打开永久熔断、不调用严格登记。回调本身发生的异常继续沿原路径传播，不能伪装成“尚未登记”。已经获准的登记与随后发生的人工停止可能并发完成；许可不是输出授权，后续启动和输出仍必须拒绝已停止的运行。

Debug/x86 整库构建通过（`artifacts/recoveryguard-permit-gate-solution.log`）；契约 77/77（`artifacts/recoveryguard-permit-gate-contract.log`）。新增测试验证过期会话/错误 Run/已有接管权拒绝且不调用登记，以及真实线程持锁时 Claim 有界等待、人工停止独立完成。磁盘 70/70（`artifacts/recoveryguard-permit-gate-disk.log`）、电源 19/19（`artifacts/recoveryguard-permit-gate-power.log`）均退出码 0；全量控制回归结果将在进程结束后补录。

这次修补覆盖故障登记入口，不等同于所有严格权威状态转换都已完成交接审计。下一步仍需核对旧启动准备及迟到提交，并接通新会话退休、创建和 Reserved 续办流程；累计软件失败后的持续冷却恢复尚未完成。最终安装包未生成，I0050 根因修复仍为第二步。

本轮首次全量控制回归退出码 1（`artifacts/recoveryguard-permit-gate-adaptive.log`）：`CancelPartialResponseAsync` 在对端保持连接、仅发送部分响应时，取消后客户端未在 3 秒内退出。现有协议只把取消令牌传给异步读，未显式关闭本地管道句柄。已补充取消注册以关闭本地客户端管道，使已发出的读结束；将取消引发的 I/O/已释放异常统一映射为调用方取消或原有“超时须对账”错误。不会终止服务端执行，也不会把取消解释为未启动。修复后的构建及回归待完成，此失败不能由此前的通过记录替代。

### 当前源码复核更正：既有自动半开机制

前文由默认累计预算 8 推导“必须换新会话才能持续重试”的结论不完整，应以本节更正为准。当前源码已有 `WatchdogHost.ScheduleNextCircuitHalfOpen`、`TryBeginAutomaticCircuitHalfOpen`、`StrictHostRelaunchOrchestrator.TryAutomaticHalfOpen` 与 `DurableRelaunchAuthorityFileStore.TryOpenAutomaticHalfOpen`。后者在严格存储锁内核验 Blocked/CircuitOpen、非永久/非身份冲突、指纹与失败次数匹配后，保留失败次数并增加代次，生成新的许可身份；不是普通 RegisterFailureAndDecide 的路径。旧看门狗还持久保存下一次重试时间并执行安全交接。

因此持续冷却重试应优先审计、接入并验证这条已有路径，不能把新会话切换认定为每次预算熔断后的必要步骤。已增加的会话交接契约仍未形成生产执行能力，也不应仅为绕过计数而启用。现有半开入口尚未使用本轮许可协调锁，是接管期间另一个必须处理的许可写入入口。生产启用前必须验证其冷却、安装级限频、人工停止、Guard 所有权、准确进程退出与安全交接共同约束；不能仅凭存在半开方法宣称持续恢复已完成。

管道取消修复后 Debug/x86 构建通过（`artifacts/recoveryguard-permit-gate-solution2.log`），定向 35/35（`artifacts/recoveryguard-permit-gate-targeted2.log`）、契约 77/77（`artifacts/recoveryguard-permit-gate-contract2.log`）通过。磁盘 70/70（`artifacts/recoveryguard-permit-gate-disk2.log`）、电源 19/19（`artifacts/recoveryguard-permit-gate-power2.log`）退出码 0；全量控制回归已确认退出码 0、797/797（`artifacts/recoveryguard-permit-gate-adaptive2.log`）。

## 半开重试及旧启动准备的接管互斥

`StrictHostV4AuthorityAdapter` 将故障登记、`TryAutomaticHalfOpen` 和 `BeginLaunch` 统一通过 `WithLegacyPermitAdmission` 调用共享准入。半开包括严格文件更新、重新打开权威及清理旧能力缓存；启动准备包括生成新的 LaunchIntent。Guard Claim 必须等待已获准的上述调用完成；接管后新调用返回 Busy，不改写严格权威。对当前恢复实例的 Started/Attached/Committed 结果提交没有一刀切禁止，仍需按实例及接管阶段继续审计。

构建 `artifacts/recoveryguard-halfopen-gate-build2.log` 通过。首次构建发现新增测试从兼容投影读取不存在的 AuthorityRevision，已改为从真实文件记录读取，保留首次失败日志。定向 `artifacts/recoveryguard-halfopen-gate-targeted.log` 为 36/36，新增测试使用真实临时 Bootstrap、严格文件存储和共享控制存储，覆盖普通半开/启动准备成功与接管后两种写入均被拒绝，且持久修订号、状态及失败次数不变；不创建业务进程或操作硬件。

完整控制回归已确认退出码 0、798/798（`artifacts/recoveryguard-halfopen-gate-adaptive.log`）；磁盘 70/70（`artifacts/recoveryguard-halfopen-gate-disk.log`）、电源 19/19（`artifacts/recoveryguard-halfopen-gate-power.log`）均退出码 0。

剩余关键工作：旧 sidecar 的半开受接管权阻断后，Guard 必须有经过冷却、当前运行/安全证据及版本核验的半开入口，否则会出现双方等待。不能把旧看门狗排除成功等同于 Guard 已具备持续恢复能力；也不能直接去掉共享排他来恢复旧写入。应复用严格半开存储机制并补足版本绑定和冷却依据，而不是为了绕过累计计数强制切换新会话。最终安装包仍未生成。

## Guard 持有接管权时的受控半开

Supervisor 的 Guard 安全准备入口现在可处理当前授权 Run 的 `RelaunchBudgetExhausted` 软件熔断，调用已有严格半开机制产生新代次、新许可身份并保留累计失败次数。半开前要求 SafeStop 的当前所有者/会话、准确主进程退出、无 Reserved/Consumed/Started 启动记录、软件故障分类、无永久故障、严格权威可信及运行身份一致。来源含已消费却无准确进程身份的启动继续阻断，不清理未知消费凭据。

Claim 将配置的 `CooldownSeconds` 冻结到事务 `CircuitCooldownSeconds`，防止事务中途通过改配置缩短此次半开的等待。冷却起点使用严格记录的 `LastFailureDecisionUtcTicks`，并保留旧看门狗普通重试 300 秒及 LastKnownGood 1800 秒的最低等待；Guard 冻结值更长时取更长值。这些是沿用现有代码及实验配置的约束，不表示生产时间参数已经现场定稿。旧事务缺少冻结值时拒绝半开，不能凭字段缺失推定冷却完成。

严格存储新增带预期 Revision/SHA 的半开重载，锁内发现来源变化即拒绝且不写入。Supervisor 写后复核共享授权/会话并核对写入版本；取消、停止或响应丢失不表示没有签发，下一次先读现有 Approved。许可签发之后仍须完成原安全快照、安全执行者、启动互斥和真实业务提交验证，不能直接输出。

首次定向测试发现通用 `LastTransitionUtcTicks` 不保证由故障登记更新，已更正为实际持久化失败决策时间。新增测试覆盖冷却前一 tick、准确退出、人工停止、永久故障、在途/未知消费阻断、旧版本与摘要拒绝，以及半开保留累计计数、重复旧来源不再签发。首次失败日志为 `artifacts/recoveryguard-owned-halfopen-targeted.log`，最终验证结果待补录；契约 77/77（`artifacts/recoveryguard-owned-halfopen-contract.log`）已通过。

未完成事项仍包括失败/过期启动对账、安全交接复用、执行者失联与取消后所有权收口、最终安装与完整链路验证。当前改动不是持续数百次恢复或现场硬件验证已完成的证明，最终安装包尚未生成。

本轮构建修正后通过（`artifacts/recoveryguard-owned-halfopen-build2.log`），定向 37/37（`artifacts/recoveryguard-owned-halfopen-targeted2.log`）、磁盘 70/70（`artifacts/recoveryguard-owned-halfopen-disk.log`）、电源 19/19（`artifacts/recoveryguard-owned-halfopen-power.log`）退出码 0。首次全量回归退出码 1：`AdaptiveTerminalOffDeadlineCommitsExactlyOnce` 的调用方提交耗时为 29.152 ms，超过既有 20 ms 断言（`artifacts/recoveryguard-owned-halfopen-adaptive.log`）；此断言包含 TryPostHi 的调用与线程调度时间，未改阈值或实现。在磁盘、电源均结束后单独重新启动全量回归，日志为 `artifacts/recoveryguard-owned-halfopen-adaptive2.log`，尚待终态；不能把定向通过代替完整回归通过，也不能在复跑前将首次失败归结为环境问题。

上述单独全量复跑现已确认退出码 0、799/799（`artifacts/recoveryguard-owned-halfopen-adaptive2.log`）。保留首次超限证据；单次复跑通过不证明首次失败的根因已确定。

## 2026-09-08：失败启动的退出对账与严格许可收口

Supervisor 将既有消费回执对账及准确进程退出确认提取为 `RecoveryLaunchReconciler.ReconcileOutstanding`，在 Guard 安全准备入口提前执行。缺少回执或结果未知仍保留 Consumed；已取得 Started 身份后只通过实际 OS 退出探测改为 Exited，不以响应超时、租约过期或停止意图代替进程退出。

安全准备可对严格 Started 许可进行受限收口：必须处于当前 SafeStop 所有权、相同会话、相同启动 OperationId，且共享记录为当前授权的 Exited，PID/启动时间/可执行路径完全匹配；服务再次探测该实例准确退出。新增带预期 Revision/SHA 的 CloseCurrentAsFailed 重载，在严格权威锁内拒绝旧证据关闭新许可。收口本身不额外增加失败次数，随后沿正常故障登记路径计数和批准；写后复核共享意图及严格记录。其它未知、旧代次或在途启动仍阻断。

Debug/x86 构建通过（`artifacts/recoveryguard-failed-launch-build.log`），定向 37/37（`artifacts/recoveryguard-failed-launch-targeted.log`）。测试覆盖真实隔离子进程退出收口，以及严格文件上的相同操作身份、停止/在途拒绝、旧版本不能关闭新许可、收口不重复计数。策略测试使用声明的退出记录验证匹配规则，不代替服务的真实 OS 退出探测；未进行现场硬件或完整安装态验证。

后续仍须完成事务中旧安全动作和启动操作的归档/新尝试切换。目前某些 Launch/Verify 失败后仍保留旧 SafetyAuthorityId、LaunchOperationId，不能仅凭本轮收口认为恢复已经重新进入新一轮 SafeStop。清理这些绑定前必须确认安全执行者退出，并持久保存旧操作证据，不能删除消费凭据或直接清空所有权。最终安装包未生成。

本轮磁盘 70/70（`artifacts/recoveryguard-failed-launch-disk.log`）、电源 19/19（`artifacts/recoveryguard-failed-launch-power.log`）退出码 0；两者结束后单独启动全量控制回归（`artifacts/recoveryguard-failed-launch-adaptive.log`），尚待终态。

上述全量控制回归已确认退出码 0、799/799（`artifacts/recoveryguard-failed-launch-adaptive.log`）。

## 2026-09-08：退出尝试归档与接管代次切换

新增 `RestartExitedRecoveryAttempt`：在当前有效 SafeStop/Launch 所有权下，要求当前动作对应的启动已为 Exited、没有其它在途启动，且 Supervisor 提供经过持久记录核验的已退出安全执行者。先把旧事务、安全执行者和启动记录写入 `action-history`，再递增接管代次并清除当前动作绑定、回到 SafeStop。保留用户授权、运行意图、启动历史、累计业务无进展时间；归档不是授权来源。

Supervisor 的安全准备入口可以接收 Launch 阶段的已退出尝试对账。其读取旧安全权威并确认对应安全执行者退出，对准确匹配的严格 Started 许可做带版本约束的收口，将原操作 ID 写入失败原因以支持中断后重读。只有这个收口标记与当前旧启动相符时才换代；其它失败/未知/在途状态不借此清空。服务返回需重读当前代次的结果，下一轮才创建新的安全动作。

Guard 在旧启动被确认退出后请求此对账；准备响应、动作返回或传输异常之后发现接管代次改变，转为读取新代次，不把旧结果绑定到新尝试。人工停止仍由共享权威的提交检查阻断新尝试；不通过按钮复位或重试生成新用户授权。

契约 78/78（`artifacts/recoveryguard-attempt-rollover-contract.log`）、定向 37/37（`artifacts/recoveryguard-attempt-rollover-targeted.log`）通过。新增用例覆盖未退休启动/未知安全退出拒绝、人工停止优先、旧动作归档、保留业务时间及启动历史、迟到准备响应不启动输出。完整构建与共享回归结果待补录；现场进程/硬件链路尚未验证。

剩余工作包括无确定创建结果的消费记录、过期预留、安全交接复用与旧协调者缓存/结果提交的完整交接，以及执行者失联、取消后所有权收口、最终安装包和安装态验收。不能把当前新增归档路径等同于所有故障阶段已能无限续接。

最终 Debug/x86 构建已通过（`artifacts/recoveryguard-attempt-rollover-build2.log`）；磁盘 70/70（`artifacts/recoveryguard-attempt-rollover-disk.log`）、电源 19/19（`artifacts/recoveryguard-attempt-rollover-power.log`）退出码 0。两者结束后单独启动全量控制测试（`artifacts/recoveryguard-attempt-rollover-adaptive.log`），仍待进程终态。

上述全量控制测试现已确认退出码 0、799/799（`artifacts/recoveryguard-attempt-rollover-adaptive.log`）。

## 2026-09-08：Guard 创建实例与旧看门狗附着衔接

旧 Host 附着前原先只读内存 Snapshot，Supervisor 签发或半开后的新许可与能力不一定进入旧实例缓存。新增受限刷新：仅对 Guard 当前 Launch/Verify 事务绑定的 Started 创建记录，核验会话、许可代次/身份、PID/启动时间、可执行路径及当前授权/租约/阶段；再从可信严格存储恢复 Started/Attached 能力。不会导入尚未消费的 LaunchIntent 供旧看门狗再次启动。当前事务缺少创建证明、停止、身份错误或证据不可读时只拒绝本次附着，不改写当前严格许可为未知结果熔断。

同时补齐旧 Host 的 replacement 阶段记录：Supervisor 在核验旧主退出、安全执行者完成且退出、严格启动意图持久化后，依次补记 Approved 到 MainLaunchIntent 的已发生事实。附着刷新只在已有前置历史的基础上，根据准确 Started 创建证据补记 MainStarted；缺少前置安全历史时拒绝，不由附着方伪造安全完成。严格 Attached 已成功、阶段记录写入失败的重试也须补写 Attached，避免之后 CheckpointCommitted 因缺阶段一直失败。此历史补记不作为新增业务提交或硬件安全动作。

定向测试新增真实临时严格存储与共享控制存储之间的外部写入场景：旧适配器缓存不自动更新，必须先取得共享 Started 和前置阶段历史；错误 PID、缺历史/创建证据、人工停止均拒绝；合法刷新后可提交 Attached，且刷新不改严格权威修订号。首次定向暴露 InvalidDataException 未映射为暂缓，已修正，保留 `artifacts/recoveryguard-host-attachment-targeted.log`。契约 78/78（`artifacts/recoveryguard-host-attachment-contract.log`）通过，最终构建和完整回归结果待补录。

尚未进行真实安装态 Host 握手和现场硬件恢复验收，不能把适配器与存储测试等同于无人值守闭环验收。其余未知消费、过期预留、失联与取消收口、安装交付仍须完成。

最终 Debug/x86 构建通过（`artifacts/recoveryguard-host-attachment-build3.log`），修正后定向 37/37（`artifacts/recoveryguard-host-attachment-targeted2.log`）；磁盘 70/70（`artifacts/recoveryguard-host-attachment-disk.log`）、电源 19/19（`artifacts/recoveryguard-host-attachment-power.log`）退出码 0。两者结束后单独启动全量控制回归（`artifacts/recoveryguard-host-attachment-adaptive.log`），尚待终态。

上述全量控制回归现已确认退出码 0、799/799（`artifacts/recoveryguard-host-attachment-adaptive.log`）。

## 2026-09-08：过期且未消费的启动预留收口

新增 `ConfirmReservationExpiredBeforeConsumption`，与 ConsumeLaunch 在同一共享状态锁内竞争。只有仍为 Reserved、没有进程身份且确已到期的记录，才能变为 StartFailed，并记录 `FailureEvidence=ReservationExpiredBeforeConsumption`。迟到消费无法再成功；已为 Consumed 的记录无论等待多久都不能由这个接口生成“没有创建”的证明。原自动过期路径也记录同一证明，未带证明的旧 StartFailed 不自动视为可重试。

Guard 和 Supervisor 会先完成此对账，再根据准确操作 ID、当前授权与严格 LaunchIntent 身份，使用版本绑定的关闭接口收口未创建的许可。严格许可自身可能已先被消费，这与 SessionAgent 的共享消费是不同步骤：只有共享记录明确证明没有消费时，才允许本路径收口。所有旧消费文件、能力文件和失败记录保留，不删除后重用同一操作。

尝试换代接口更名为 `RestartTerminalRecoveryAttempt`，接受已准确退出的实例，或带上述未消费证明且没有进程身份的失败预留；两种情况都仍要求旧安全执行者退出、当前停止意图有效核验。新尝试递增代次，旧操作归档，不能靠延长原过期凭证续用。

生产入口核对还发现，旧 GuardedProcessLauncher 在正式部署标记缺失时可直接 Process.Start。现改为只要 Guard 已注册或正在注册，就走受控 SessionAgent 启动；这保证共享消费检查是创建之前的必要步骤，不能由标记文件缺失绕过。

Debug/x86 构建通过（`artifacts/recoveryguard-reservation-expiry-build.log`）。新增/扩展测试覆盖消费与过期只允许一方获胜、未知消费永不转为未创建、过期证明下的尝试换代、人工停止阻断、没有证明的 StartFailed 拒绝，以及严格已消费但共享明确未消费的区别。验证结果待进程结束后补录。无确定结果的 Consumed 仍阻断，不能把本轮改动解释成所有未知启动都已能自动重试；完整安装与硬件验收仍未完成。

本轮契约 79/79（`artifacts/recoveryguard-reservation-expiry-contract.log`）、定向 37/37（`artifacts/recoveryguard-reservation-expiry-targeted.log`）、磁盘 70/70（`artifacts/recoveryguard-reservation-expiry-disk.log`）、电源 19/19（`artifacts/recoveryguard-reservation-expiry-power.log`）均已退出码 0。其它测试结束后单独启动全量控制回归（`artifacts/recoveryguard-reservation-expiry-adaptive.log`），尚待终态。

上述全量控制回归已确认退出码 0、799/799（`artifacts/recoveryguard-reservation-expiry-adaptive.log`）。

## 2026-09-08：停止/过期事务的保守所有权收口

Supervisor 新增每 15 秒执行一次的后台对账，在停止、暂停、完成或已持久标记监督过期时检查旧接管事务。只在租约已过期、旧 Guard 和当前主实例均被准确确认退出时继续；在与进程创建相同的硬件启动锁下，收口已有回执和过期预留，确认没有活动/未知安全执行者，并验证当前绑定安全权威的完成与执行者退出。安全回执必须对应当前主进程 PID/启动时间，不能用上一主实例的完成回执释放新实例的所有权。该循环不启动安全动作、不终止进程、不复位停止信号；相同阻断原因只在变化时记录。

核心 `ReconcileInactiveTakeover` 重新核验预期意图版本、事务/代次、准确进程身份、安全权威 ID、租约与所有在途启动。满足后只标记旧事务 Cancelled 且 OwnershipReleased，保留安全权威和启动历史。原停止意图不改变；因监督过期且仍为 Run 的旧意图转为 Stopped 并递增版本，原因明确记录为过期，不声称人工点击停止。迟到收口不能修改新的人工开始授权。

本路径仍有明确边界：当前主程序活着、旧安全动作未完成/无法证明退出、缺少安全权威、回执对应其它主实例或存在未知消费时，保持原有阻断。它不是所有取消场景的完整自动收口，也不允许跳过安全证明以解锁人工开始。15 秒为当前内部对账周期，不代表现场安全处置时限已定稿。

Debug/x86 构建通过（`artifacts/recoveryguard-inactive-cleanup-build.log`）。新增契约用例覆盖停止及监督过期、活动/未知进程拒绝、在途消费拒绝、精确退出后收口、保留停止与历史、释放后明确人工开始，以及旧收口不能覆盖新授权。最终测试结果待进程终态补录。真实服务安装和现场硬件验收尚未完成。

本轮契约 80/80（`artifacts/recoveryguard-inactive-cleanup-contract.log`）、定向 37/37（`artifacts/recoveryguard-inactive-cleanup-targeted.log`）、磁盘 70/70（`artifacts/recoveryguard-inactive-cleanup-disk.log`）、电源 19/19（`artifacts/recoveryguard-inactive-cleanup-power.log`）已退出码 0。其它测试结束后单独启动全量控制回归（`artifacts/recoveryguard-inactive-cleanup-adaptive.log`），尚待终态。

上述全量控制回归已确认退出码 0、799/799（`artifacts/recoveryguard-inactive-cleanup-adaptive.log`）。

## 2026-09-08：验证阶段 Guard 退出后的正常运行交接

发现原执行器对外来所有者一律先 Adopt 并增加代次；若新主程序已经产生有效业务提交、旧 Guard 恰好在 Verify 写入 Complete 前退出，该行为会使正常主程序的原输出许可失效。新增 `ReconcileVerifiedTakeover`，在接管换代之前核对旧 Guard 已准确退出、租约已过期、当前主程序准确存活、Verify 阶段未超时、当前授权的 Started 启动记录与主实例一致，以及已有不少于两次有效业务提交。共享权威锁内再次核验意图、监督有效性、事务身份和无未知在途启动，随后原子写入 Complete 并释放所有权。

该交接保留原代次和主程序运行令牌，不执行安全处置、不创建进程，也不重授权。人工停止、进程身份不明、仅有进程存活而无业务提交、验证超时等情况不能使用此路径。其他在途动作的所有者换代仍需另外完善，不能据此宣称 Guard 任意退出场景均已解决。

新增契约回归覆盖上述正常交接、无业务证据拒绝、旧所有者仍存活、错误所有者、验证截止时间及人工停止。最终验证结果待本轮构建及测试终态补录。完整安装包及自动执行部署验收仍未完成。

当前试验初值为阶段 120 秒、所有者租约 180 秒，因此默认配置不能在租约过期后利用这个限期内交接分支；不能把新增能力宣传为默认配置已解决 Verify 所有者退出。正常交接回归显式使用 300 秒阶段期限，另测到期拒绝，生产参数未因此修改。首轮契约失败记录保留在 `artifacts/recoveryguard-verified-handback-contract.log`：用例原本在阶段已超时后期待所有者身份拒绝，实际先被验证期限拒绝；修正测试时间配置后重新运行。所有者退出后的完整恢复仍是后续执行链路工作。

本轮 Debug/x86 全方案构建通过（`artifacts/recoveryguard-verified-handback-solution.log`），定向恢复 37/37、磁盘 70/70、电源 19/19 均退出码 0，对应日志前缀为 `artifacts/recoveryguard-verified-handback-`。契约最终重跑及全量控制回归待补录。

契约最终重跑已退出码 0、81/81（`artifacts/recoveryguard-verified-handback-contract2.log`）。随后单独启动全量控制回归（`artifacts/recoveryguard-verified-handback-adaptive.log`），仍待进程终态。

上述全量控制回归已退出码 0、799/799（`artifacts/recoveryguard-verified-handback-adaptive.log`）。

## 2026-09-08：换代后的已完成安全动作对账及锁顺序修正

原执行器对 `ActionEpoch != Epoch` 一律阻断，导致 Guard 确认退出后即使已换代、旧安全动作已经完成且执行者退出，也不能继续。现对“SafeStop、保留旧 SafetyAuthorityId、尚未绑定主程序启动操作”的场景，通过现有 SafetyPrepare 接口请求对账。Supervisor 校验旧动作代次派生的 HandoffId、原会话/许可/主实例身份及完整安全回执，取得硬件启动锁后确认旧 SafetyAgent 精确退出和所有安全执行者已退出，再调用共享契约原子归档、清除旧动作绑定。

归档保留当前接管代次、授权和业务进度时钟，不修改停止意图，不等于安全阶段成功；下次执行必须准备并执行新代次安全动作。共享契约在提交前重查当前所有者、会话、阶段期限、旧动作代次和没有 Reserved/Consumed/Started 启动。旧所有者的迟到响应仍被隔离。主程序启动已绑定、旧安全动作无完整完成证明、执行者仍活着或身份未知等场景不由此路径放行。

同时修正 `SupervisorOwnedSafetyAgent.RegisterOrGet` 的锁顺序：原顺序为执行者锁→硬件启动锁，而对账使用硬件启动锁→执行者锁，存在相互等待直至超时的风险。现统一为硬件启动锁→执行者锁。新增隔离并发回归，在持有硬件锁时让创建者等待，验证对账仍能进入执行者锁，之后在创建前明确拒绝，不启动实际子进程。

本轮构建和测试结果待进程终态补录。此处解决部分已完成动作的换代接续；仍需补齐其它在途启动对账、自动执行部署及完整安装包验收，I0050 状态一致性根因修复仍为第二步。

本轮 Debug/x86 构建通过（`artifacts/recoveryguard-adopted-safety-build.log`），契约 82/82、定向恢复 38/38、磁盘 70/70、电源 19/19 均退出码 0，对应日志前缀为 `artifacts/recoveryguard-adopted-safety-`。上述测试结束后单独启动全量控制回归（`artifacts/recoveryguard-adopted-safety-adaptive.log`），仍待进程终态。

上述全量控制回归已退出码 0、800/800（`artifacts/recoveryguard-adopted-safety-adaptive.log`）。

## 2026-09-08：扫描与独立执行任务衔接、Windows PowerShell 5.1 兼容性

`--check` 删除固定的 `RecoveryExecutionWorkerDispatchNotConfigured` 拒绝，接入独立任务调度。ObserveOnly、维护、停止和监督过期不请求执行；有效候选或尚未收口事务，在没有存活所有者且旧租约已结束时，才请求已安装执行任务。存活且租约有效时报告 ExecutionWorkerRunning；存活但租约过期或进程身份未知时报告 NeedsAttention，退出码 2，不把 IgnoreNew 的调度响应误认为新执行者已经接管。

调度限定正式共享根目录和正式设置路径；核对 installation.json 的执行启用标志、固定任务名和当前 EXE 所在版本目录，然后读取任务 XML，要求单一 SYSTEM/ServiceAccount/最高权限主体、准确 EXE/工作目录/参数、启用、IgnoreNew、无限执行时间且禁止任务计划程序强制终止。每次 schtasks 客户端调用最多等待 5 秒，输出有 64 KiB 上限；请求结果仅记录“已请求任务，尚未验证恢复”。调度超时或不确定失败记录 ReconcileRequired，不声称没有执行。只可能结束本次短时 schtasks 客户端，不终止 Guard 执行者、主程序或 SafetyAgent。

独立安装器仍只开放 ObserveOnly，执行任务和登记的 executionEnabled 仍为 false；本轮没有绕过交付阶段门槛，也未在本机注册或运行 Guard 生产任务。存活但失联的执行者自动退休、更多在途动作接续以及最终自动执行安装验收仍未完成。定期触发与新的请求入口都依赖同一个执行任务及执行互斥，不产生第二种主程序启动入口。

用 Windows PowerShell 5.1 运行交付脚本测试时发现：四个含中文的 Guard 安装/打包/校验脚本缺少 UTF-8 BOM，脚本解析失败；修正为 UTF-8 BOM。另修正任务路径校验顺序，先拒绝引号和换行再调用旧框架 IsPathRooted，避免框架异常抢先覆盖明确拒绝原因。任务 XML 校验增加与实际 Guard 二进制的对照，支持启用任务并拒绝安装器当前生成的禁用执行任务；测试不调用实际任务注册。

Debug 独立构建通过（`artifacts/recoveryguard-dispatch-build.log`），契约 84/84、隔离 CLI 12/12、任务定义及二进制配对校验 8/8 均退出码 0，分别见 `recoveryguard-dispatch-contract.log`、`recoveryguard-dispatch-cli.log`、`recoveryguard-dispatch-tasks4.log`。初次 Windows PowerShell 5.1 解析失败和后续路径/反射测试适配失败日志均保留，不以旧的成功记录代替新脚本验证。Debug 兼容性包的 Windows PowerShell 5.1 打包及篡改校验正在进行；它是交付链测试产物，不是最终自动恢复安装包。

Windows PowerShell 5.1 打包及包校验已退出码 0、8/8，通过 ZIP 与目录逐文件哈希比对和篡改拒绝；见 `artifacts/recoveryguard-dispatch-package-build.log` 与 `artifacts/recoveryguard-dispatch-package-test.log`。对应 `artifacts/RecoveryGuard-3.0.0.0-dispatch-compat.zip` 为 Debug/ObserveOnly 兼容性测试产物，未执行安装，不作为最终交付。

## 2026-09-08：隔离 SYSTEM 任务验收脚本与权限实证

新增 `Tools/Test-MTTFTest-RecoveryGuardInstalledTask.ps1`，以经过校验的 ObserveOnly 包生成独立副本、随机扫描/执行任务名和隔离授权目录。实际执行模式要求管理员令牌，准备 SYSTEM 任务，各执行两次并验证零退出码、日志和无动作，最后核对任务身份并清理本次任务。只使用手工测试触发，不遗留开机/分钟触发；保留证据文件。计划模式不注册任务、不得写入真实运行通过标记。

当前令牌 `Administrator=false`、任务计划服务 Running，因此本轮不能执行 SYSTEM 注册。已在 Windows PowerShell 5.1 完成计划生成和非管理员拒绝验证；两个生成 XML 经检查均为 SYSTEM、没有自动触发器、带明确隔离根目录。日志为 `artifacts/recoveryguard-installed-task-plan2.log` 和 `artifacts/recoveryguard-installed-task-admin-gate.log`，证据为 `artifacts/recoveryguard-installed-task-9153331251fd46afb3d571effe0ba7e2/results.json`，其中 `actualTaskLifecycleVerified=false`、`taskRegistrationPerformed=false`。真实注册、运行与清理需要管理员环境补验；该权限限制不影响继续补齐其他软件链路，当前总体目标仍未完成。

## 2026-09-08：活动事务中失联 Guard 执行者的受控退休

扫描入口对准确存活但租约过期、且仍有活动事务的 Guard，可在正式安装/任务配置核验通过后请求退休。共享权威新增 WorkerRetirementRequestedUtcTicks，提交前重查当前运行意图、监督时效、事务代次、准确所有者和租约；同一请求幂等。退休标记使旧所有者无法续租、进入新动作或消费启动准入，即便时钟小幅回调，也不能借租约比较重新获得资格。标记不增加接管代次、不清除旧安全/启动记录，也不重置业务进度。

只允许目标 EXE 为当前版本的独立 MTTFTest.RecoveryGuard.exe，禁止结束扫描器自身。终止采用同一个 Windows 内核进程句柄完成启动时间、镜像路径核对及 TerminateProcess，避免核对后重新按 PID 打开导致误伤复用 PID 的进程。终止前再次核对维护标志和共享退休授权；最多等待退出 2 秒，未知或失败结果保留标记并报告。主程序、SafetyAgent、看门狗不属于本入口终止范围。

准确退休后报告 ExecutionWorkerRetired;AwaitingNextDispatch，不在同一次扫描立即请求仍可能处于 IgnoreNew 清理中的任务。下一次调度确认旧所有者退出后，仍须经 Adopt 新代次和旧动作对账；退休标记不是退出证明，也不直接允许主程序恢复。已有正常输出授权不因这个软件退休标记单独撤销。

本轮仍有边界：没有活动接管事务时的执行者失联不由此路径处理；已创建主实例及其他旧动作的完整换代接续仍需完善，尤其正常 Verify 业务证据与阶段超时的交接关系。安装器仍未启用执行任务，真实 SYSTEM 场景未验收。不得把本轮代码解释成所有执行者失联都已自动恢复。

Debug/x86 构建通过（`artifacts/recoveryguard-worker-retirement-build2.log`）。契约 86/86、定向恢复 38/38、磁盘 70/70、电源 19/19 均退出码 0，对应日志前缀 `artifacts/recoveryguard-worker-retirement-`。新增测试覆盖退休幂等、停止拒绝、回调时钟下旧续租仍被隔离、退休标记不能代替旧所有者退出，以及真实隔离 Guard 子进程的错误身份不终止、最终授权拒绝不终止、准确句柄终止。CLI 和全量控制回归结果待补录。

隔离 CLI 已退出码 0、12/12（`artifacts/recoveryguard-worker-retirement-cli.log`）；上述其他测试结束后单独启动全量控制回归（`artifacts/recoveryguard-worker-retirement-adaptive.log`），仍待进程终态。上文“回调时钟”指测试中的时钟小幅回退，不是业务回调提供了新的监督证据。

上述全量控制回归已退出码 0、800/800（`artifacts/recoveryguard-worker-retirement-adaptive.log`）。

## 2026-09-08：Verify 证据时效、失联交接与默认扫描预算修正

本轮纠正此前“孤立 Verify 必须仍在旧阶段期限内”的限制。主程序在 Verify 已取得运行授权后，正常输出不会仅因 Guard 失联而停止；若业务仍在真实推进，旧 Guard 的动作期限不应单独导致新 Guard 换代并使正常主实例令牌失效。交接改为核对冻结的证据时效、当前快照和逐通道实际进展，旧所有者仍须准确退出、租约已结束，且存在当前令牌对应的 Started 主实例、有效意图及无未知在途启动。该交接不延长旧阶段期限、不发出新动作，也不重授权。

Claim 冻结 VerificationEvidenceMaxAgeSeconds，来源为当时的 SnapshotMaxAgeSeconds。`HasFreshVerificationEvidence` 要求至少两次验证期内真实提交、当前身份/配置相符的新鲜可用快照、每个参与通道的可靠时钟，以及符合阶段的新鲜进展。正常阶段检查采样、控制和保存共同进展；有明确期限的合法保压阶段允许暂时没有新提交，但仍需新鲜真实采样，且之前提交发生在本次验证开始之后。窗口缺失、单通道证据缺失、仅重发文件、源不可用或保压期限已过都不能放行。临时扩大当前设置不能刷新已冻结的证据窗口。普通所有者的 Complete 提交也复用同一检查，防止历史计数配合新文件时间伪报恢复成功。

还发现原 120 秒通用阶段预算与默认 60 秒扫描节奏不相容：新主实例先建立一次基线，再取得两次提交观测，通常需要第三次扫描。新增独立 VerificationTimeoutSeconds，代码和示例配置的试验初值为 300 秒，校验要求严格大于三个扫描周期且不超过 1800 秒；其他动作阶段仍使用 StageTimeoutSeconds。Verify 的绝对截止时间只在进入该阶段时设置，不随续租、扫描或发布延长。300 秒属于软件试验初值，尚非现场参数定稿。

上述修正取代此前进度记录中的限期内交接限制及通过加长测试阶段预算规避默认配置关系的做法。新增/扩展回归按真实默认 60 秒间隔验证基线及两次提交、旧阶段超时后的新鲜证据交接、正常所有者拒绝陈旧证据、保压采样与期满拒绝以及冻结窗口。构建与测试结果待终态补录。

本轮 Debug/x86 构建通过（`artifacts/recoveryguard-verification-window-build2.log`），契约 88/88、定向恢复 38/38、磁盘 70/70、电源 19/19 均退出码 0，对应日志前缀 `artifacts/recoveryguard-verification-window-`。CLI 配置检查及全量控制回归待补录。Guard 退出时尚未累积足够验证证据的主实例仍需补充有界观察接续，不能把本轮充分证据交接等同于所有 Verify 中断都已解决。

CLI 配置与隔离运行检查已退出码 0、12/12（`artifacts/recoveryguard-verification-window-cli.log`）。其它测试结束后单独启动全量控制回归（`artifacts/recoveryguard-verification-window-adaptive.log`），仍待进程终态。

上述全量控制回归已退出码 0、800/800（`artifacts/recoveryguard-verification-window-adaptive.log`）。

## 2026-09-08：Verify 证据尚不足时的有界观察接续

原执行器只对已充分验证的孤立 Verify 保留主令牌；新主程序刚启动、旧 Guard 已退出而尚未取得两次提交观测时，会立即 Adopt 换代并使正在恢复的主程序令牌失效。本轮新增 TryObserveOrphanVerification：旧 Guard 准确退出且租约结束、当前主实例准确存活、有匹配 Started 记录、没有未知或其他在途实例、当前授权和快照可信时，先记录 OrphanVerificationUntilUtcTicks 并继续观察。

窗口时长取原 Verify 阶段已经固定的预算，只在首次进入时登记，观察者重启、快照刷新和重复请求都不延长它。该操作不更换事务所有者或代次，不续租旧 Guard，不改变原动作截止时间，也不给未完成冷启动准入的实例额外输出资格；已经合法运行的主实例可保留原令牌继续推进。人工停止、维护和监督过期仍阻断。

窗口内累积足够新鲜业务证据后，使用既有原子交接关闭事务并释放所有权。到达窗口截止时间时不再等待，也不接受截止时才取得的完成证据，转入原有 Adopt、Cooldown 和旧动作对账；Started 记录不删除、不伪造失败或安全完成。下一次合法启动进入新的 Verify 时才清除旧观察窗口，因此更换观察进程不能反复取得新的等待预算。

新增回归覆盖零次/一次提交继续等待、观察进程更换不延期、两次真实提交后保持令牌交接、截止边界拒绝迟到完成并保留启动记录、人工停止结束观察。构建及最终测试结果待补录。没有活动事务的执行者失联以及其他在途启动接续仍需继续完善，最终自动执行安装和管理员验收尚未完成。

Debug/x86 构建通过（`artifacts/recoveryguard-orphan-observation-build.log`），契约 89/89、定向恢复 38/38、磁盘 70/70、电源 19/19 均退出码 0，对应日志前缀 `artifacts/recoveryguard-orphan-observation-`。CLI 与全量控制回归待补录。

CLI 已退出码 0、12/12（`artifacts/recoveryguard-orphan-observation-cli.log`）；其它测试结束后单独启动全量控制回归（`artifacts/recoveryguard-orphan-observation-adaptive.log`），仍待进程终态。

## 2026-09-08：交接测试竞态与历史终态动作核销

上一轮 `artifacts/recoveryguard-orphan-observation-adaptive.log` 全量回归终态为失败，断言位于 `HigherRecoveryPreemptsAndWaitsForHydraulic`。核查发现测试在 Dispose 释放液压所有权后才设置测试“已退出”标记，新所有者可能在两者之间取得所有权。生产协调器等待的是 Dispose 发出的 Completion，不保证调用者在 Dispose 后的任意记账已完成。本轮仅调整测试：显式阻塞清理，断言清理期间 DAQ 仍等待且旧所有者仍持权，再允许清理完成并释放；不修改液压协调器或第二步 I0050 根因修复范围。定向回归退出码 0、48/48（`artifacts/recoveryguard-coordination-test-targeted.log`）。全量复核使用 `artifacts/recoveryguard-coordination-order-adaptive.log`，待终态。

另核查出旧 Guard 退出、Adopt 更换代次之后，已绑定的终态启动动作因旧令牌代次不匹配而无法核销。本轮增加仅供终态核销使用的 MatchesBoundTerminalLaunch：当前事务、授权、意图与当前代次必须有效，历史 ActionEpoch 必须与绑定的启动令牌代次一致，操作 ID 必须匹配，且只接受准确退出记录或预留到期且证明未消费的记录。普通 Matches 启动/输出授权判断保持不变，不能借核销恢复旧令牌能力。Supervisor 仍须核实安全工作者退出、真实进程退出及严格许可版本/SHA；无在途或存活启动后才归档、换代并要求新的安全动作。

本轮代码和回归尚待构建验证；新增用例覆盖接管后两类终态、停止优先、操作身份不符、意图不符以及未来动作代次。启动预留已经写入但响应丢失、尚未绑定 LaunchOperationId 的窗口仍未由本修正解决；未知消费结果也不据此假定未启动。最终安装包与真实安装验收尚未完成。

交接测试修正后的全量回归已退出码 0、800/800（`artifacts/recoveryguard-coordination-order-adaptive.log`）。随后历史终态核销代码 Debug/x86 构建通过（`artifacts/recoveryguard-adopted-terminal-build.log`）；定向恢复 38/38、磁盘 70/70、电源 19/19 均退出码 0（同名前缀 targeted/disk/power 日志）。契约测试和本批代码的全量回归仍待终态补录。

补充源码定位：PrepareGuardLaunch 写入的是严格 LaunchIntent；RecoveryControlStore.ReserveLaunch 在 SupervisorServiceHost 实际启动路径才调用。因此上述未绑定窗口应精确表述为“严格启动意图已持久化但响应丢失、事务尚未绑定”，不能假定同时已有核心预留。本次核销范围仅限存在明确事务绑定和核心终态证明的动作。

历史终态核销契约测试已退出码 0、89/89（`artifacts/recoveryguard-adopted-terminal-core.log`），原终态测试扩展为正常/换代、已退出/未消费、运行/停止组合，并补充严格许可策略的历史绑定检查。其它测试结束后启动本批全量回归 `artifacts/recoveryguard-adopted-terminal-adaptive.log`；当前仍在执行，不能用上一批 800/800 代替本批终态。

CLI 首次以 Windows PowerShell 默认执行策略调用时脚本未获加载（`artifacts/recoveryguard-adopted-terminal-cli.log`），没有执行测试。随后仅对本次 PowerShell 子进程指定 ExecutionPolicy Bypass，未修改系统策略；隔离 CLI 检查退出码 0、12/12（`artifacts/recoveryguard-adopted-terminal-cli2.log`）。

## 2026-09-08：启动准备前原子绑定与未准备动作核销

上一批历史终态核销的全量回归已退出码 0、800/800（`artifacts/recoveryguard-adopted-terminal-adaptive.log`）。本轮继续处理准备响应丢失窗口：新增 ReserveAndBindRecoveryLaunch，在同一共享权威提交内验证当前所有者、阶段、会话、安全绑定并写入核心预留及 LaunchOperationId；Supervisor 在写严格 LaunchIntent 前调用此入口。重复准备保留原到期时间，不为重试延长预留；调用失败不能留下半个新绑定。

因此现在准备阶段即可留下核心预留，不再只在实际启动服务入口预留。若在严格意图写入前中断，预留到期后，Supervisor 仅在严格许可仍为 Approved、无启动意图/消费/进程、核心存在同一绑定的未消费到期证明且没有在途或存活启动时，允许核销；同时核对原安全凭据的代次/许可/nonce/运行及安全工作者退出。核销保留严格批准许可，归档旧动作并要求新安全动作，不把没有创建进程的准备中断伪装为进程崩溃。

PrepareGuardLaunch 与 SafetyPrepare 共用有界串行入口，防止当前 Supervisor 内旧准备请求穿过核销交错写入。人工停止、旧所有者隔离和预留消费的最新授权检查仍保留。新增契约测试覆盖新读者看到预留与绑定同时存在、错误安全绑定不留记录、重放不续期、替换操作被拒及停止优先；严格策略测试覆盖 Approved 未准备状态、换代终态及未知消费拒绝。

Debug/x86 构建已通过（`artifacts/recoveryguard-atomic-preparation-build.log`），本轮测试终态待补录。上述是新准备路径的实现，不等于接受任何历史缺失记录；已有严格意图但缺失核心绑定/预留仍不能推定未启动。最终自动执行安装包及真实管理员安装验收尚未完成。

首批测试终态均退出码 0：契约 90/90、定向恢复 38/38、磁盘 70/70、电源 19/19，对应日志 `artifacts/recoveryguard-atomic-preparation-{core,targeted,disk,power}.log`。随后补齐终态核销的硬件创建互斥区及完整安全工作者退休检查，避免检查到归档之间出现新安全实例。二次构建通过（`artifacts/recoveryguard-atomic-preparation-build2.log`），补充后的定向回归、CLI 与全量回归待终态。

补充互斥检查后的定向恢复已退出码 0、38/38（`artifacts/recoveryguard-atomic-preparation-targeted2.log`）；隔离 CLI 已退出码 0、12/12（`artifacts/recoveryguard-atomic-preparation-cli.log`）。其它测试结束后启动本轮全量控制回归 `artifacts/recoveryguard-atomic-preparation-adaptive.log`，当前仍待终态。没有活动事务的执行者失联监督、最终自动执行配置与安装器验收仍需继续，不将本批单项通过作为安装包已可交付的结论。

## 2026-09-08：安装/卸载前核验计划任务归属

安装器原先直接按固定任务名覆盖、禁用和卸载任务，不能排除同名任务属于其他安装或执行其他程序。本轮先读取并校验 schema 2 安装登记、限定目录为指定 Guard 根目录下 versions 子目录并验证已安装包；随后只枚举 TaskPath 为根目录的两个产品任务。任务查询/导出失败直接中止；发现同名任务但没有安装登记时拒绝操作。

新增纯 XML 核验入口 Assert-GuardTaskOwnership，禁止 DTD 并限制输入大小，要求只有一个 Exec、SYSTEM/ServiceAccount/HighestAvailable 主体及匹配执行上下文，命令、工作目录、settings/journal 参数必须与登记安装完全一致。所有修改明确指定根任务路径，不再把其他文件夹中的同名任务纳入目标。该核验不是声称能够对抗另一个管理员同时修改任务，也不代替升级失败的回退事务；后者仍须补齐和验收。

Windows PowerShell 5.1 任务契约测试退出码 0、16/16（`artifacts/recoveryguard-task-ownership-definition.log`），包含两种正常任务、危险路径拒绝、执行入口匹配以及外部命令/参数/目录/账户/额外动作/上下文拒绝。未注册、禁用或卸载真实任务。

为验证当前安装脚本复制与清单校验，生成隔离 Debug/ObserveOnly 测试包 `artifacts/RecoveryGuard-3.0.0.0-task-ownership-validation.zip`，SHA256 为 `26cf3f94a9161d09b9df7a2008bd491055ddf126e3c04d1686ae74d59a39397e`。该包仅用于安装器校验，不是最终自动恢复安装包；打包日志 `artifacts/recoveryguard-task-ownership-package-build.log`，后续包验证结果待补录。

隔离包完整性/参数拒绝及 ZIP 一致性验证已退出码 0、8/8（`artifacts/recoveryguard-task-ownership-package-test.log`），installationPerformed=false。当前仍没有真实安装态验收。

## 2026-09-08：两任务与安装登记的失败回退事务

原子启动准备批次的全量控制回归已退出码 0、800/800（`artifacts/recoveryguard-atomic-preparation-adaptive.log`）。本轮修改安装脚本，未更改 C# 二进制。

安装器现在在修改计划任务之前，持久保存两项任务原 XML、安装登记原始字节以及目标 XML，事务证据位于 Guard 状态目录的 install-transactions。顺序更新执行任务和扫描任务后，再以写入 Flush(true) 的临时文件原子替换安装登记，最后持久标记 Committed。卸载复用同一事务，仅移除已确认归属的任务，保留共享授权与安装文件。

任一步失败都按已尝试的动作逆序请求恢复原任务；任务写入返回失败也算已尝试，防止遗漏实际已应用的修改。首次安装回退移除本次创建的任务，原登记存在则逐字节恢复、不存在则恢复为不存在。任务或登记回退报错、回退证据写入失败时，保留 maintenance-inhibit.json，记录 RollbackFailed 或保留原 Prepared 证据，不能解除维护后声称恢复完成。进程被强制终止或断电时，现有维护文件和 Prepared 证据保留，尚无自动清理该阻断的能力，需依据现场状态恢复。

新增 Tools/Test-MTTFTest-RecoveryGuardInstallTransaction.ps1，只加载生产事务函数，在临时目录使用真实原子文件及模拟计划任务边界；不调用真实任务注册。8/8 场景通过，退出码 0（`artifacts/recoveryguard-install-transaction-test5.log`）：首次安装、升级、首/次任务已修改但返回失败、登记写入失败、回退响应失败、卸载成功和卸载中断。前四轮日志保留了测试发现的空字节数组转换与 Windows PowerShell 5.1 File.Replace 空备份参数问题，已修正后通过。任务定义/归属复核 16/16、退出码 0（`artifacts/recoveryguard-install-transaction-definition.log`）。

这些验证证明生产事务在注入边界下的行为，不是实际 SYSTEM 任务导入/导出兼容、真实管理员升级/卸载或现场设备恢复验收。当前包仍为 ObserveOnlyCommissioning；执行者无活动事务时失联监督、正式自动执行配置与最终 Release 交付仍未完成。

安装事务脚本的隔离 Debug/ObserveOnly 包已生成并通过 8/8 包清单、参数拒绝及 ZIP 对账，退出码 0（`artifacts/recoveryguard-install-transaction-package-build.log`、`artifacts/recoveryguard-install-transaction-package-test.log`）。包路径为 `artifacts/RecoveryGuard-3.0.0.0-install-transaction-validation.zip`，仅为本轮脚本验证产物，不能作为最终自动恢复安装包。

## 2026-09-08：3.0.0.0 完整 Release 构建与专项复核

首次完整 Release/Any CPU 构建失败（`artifacts/recoveryguard-v3000-release-build.log`）：PowerSupplyDebugger 的资产文件缺少项目 Release 配置要求的 net8.0-windows/win-x86 目标。按现有项目属性执行 dotnet restore /p:Configuration=Release 后恢复，未改变运行时选择。二次构建通过；Guard csproj 补充 Release pdbonly，使独立执行组件也生成调试符号。

严格 Host 专项中的版本测试仍固定 V2.17.1.1，已更新为统一大版本 V3.0.0.0，并增加 RecoveryControl 版本检查。首次运行进一步暴露旧单进程启动测试依赖本机未安装环境：Guard 注册存在时生产启动器会联系 Supervisor，测试进程被请求者身份检查拒绝（`artifacts/recoveryguard-v3000-release-strict.log`），未借此放宽身份校验。新增内部进程启动委托用于该测试，在真实严格授权验证/持久消费及二次哈希校验之后启动测试自身子进程；生产构造默认仍使用原 SessionAgent/Supervisor 路由。该用例证明启动边界的一次性与真实子进程行为，不替代真实安装态启动验收。

三次构建已退出码 0（`artifacts/recoveryguard-v3000-release-build3.log`）。Release 契约 90/90、磁盘 70/70 已通过；补充测试隔离后严格 Host 22/22、恢复专项 38/38、电源 19/19 均退出码 0，日志前缀为 `artifacts/recoveryguard-v3000-release-`（strict2、targeted2、power）。其它测试结束后启动 Release 全量控制回归 `artifacts/recoveryguard-v3000-release-adaptive.log`，仍待终态。

`artifacts/recoveryguard-v3000-release-components.json` 记录九个主要组件的程序集版本、文件版本、二进制和 PDB 的 SHA256，全部版本为 3.0.0.0 且 PDB 存在。这是当前构建预检记录，不是冻结源码的最终包清单，也不单凭文件存在声称已校验 PDB 内部签名。最终自动执行包、无活动事务的执行者失联监督及真实管理员安装态验证仍需继续完成。

## 2026-09-08：无活动接管事务的执行者监督

上一批 Release 全量回归已退出码 0、800/800（`artifacts/recoveryguard-v3000-release-adaptive.log`）。本轮新增安装级 ExecutionWorker 记录，保存精确进程身份、执行循环最近返回时间、租期和退休标记。程序取得执行互斥后登记身份，在每次执行步骤返回时更新进度；不是独立后台心跳，卡住的步骤不会靠另一个线程继续刷活。该记录不刷新试验授权、可信业务提交或 60 分钟监督期限，ObserveOnly 不登记执行者。

扫描器存在恢复需求且没有未释放接管事务时，先检查该执行者：未知身份保持阻塞，租期内存活则等待；准确存活且租期过期才请求持久退休，随后复用准确内核句柄验证/终止 Guard 的既有实现。仍要求正式安装任务校验和维护门禁；人工停止、监督过期不走该自动动作。退休成功后等下一次扫描调度，不在同次扫描同时请求启动。

退休标记与 Claim 共用共享权威锁。退休先提交则旧执行者不能再 Pulse、Claim 或以旧身份执行所有者动作；Claim 先提交则无事务退休入口拒绝，转交原活动事务处理。替换执行者必须提交与旧记录同一进程的准确退出证据，旧退出观察不能覆盖后来登记的另一个执行者。已合法运行主程序的业务令牌不因执行者进度记录变化而更换。

新增回归覆盖进度记录不延长试验监督、未过期拒绝、停止优先、退休与任务请求分离、退休后禁止 Pulse/Claim、未知退出不换人、旧退休请求不能针对新执行者以及活动事务拒绝空闲退休。本批构建与测试待终态。尚未成功写入首份身份记录的执行者不能据此被自动终止，记录缺失或不可信不作为猜测 PID 的依据；这保留了启动/存储失败时的能力边界。

本轮 Release 构建退出码 0（`artifacts/recoveryguard-idle-worker-build.log`），契约 91/91、恢复专项 38/38、磁盘 70/70、电源 19/19、CLI 12/12 均退出码 0（日志前缀 `artifacts/recoveryguard-idle-worker-`）。其它测试结束后启动 Release 全量控制回归 `artifacts/recoveryguard-idle-worker-adaptive.log`，尚待终态。本轮修改后此前的 Release 组件哈希预检不再代表最新二进制；最终打包必须重新冻结源码、生成组件清单并验证。

## 2026-09-08：Release 独立包纳入符号与随包文档

打包清单升级为 schema 2，增加 MTTFTest.RecoveryGuard.pdb、MTTFTest.RecoveryControl.pdb 和 README.md；README 来源为 docs/RecoveryGuard_安装包使用说明.md，包含包身份判读、校验、管理员安装/卸载、失败回退证据及已确认恢复边界。安装器对新清单逐项验证长度/SHA256，也保留对旧 schema 1 四文件清单的支持，便于已有安装的身份核验。

新增拒绝测试覆盖缺失 PDB、README 被改动和未知清单版本。新 Release 包完整性及 ZIP 对账 11/11、旧 schema 1 包兼容 8/8、生产安装事务故障注入 8/8 均退出码 0（`artifacts/recoveryguard-release-symbols-package-test.log`、`artifacts/recoveryguard-release-symbols-schema1-test.log`、`artifacts/recoveryguard-release-symbols-transaction-test.log`）。未修改实际安装目录或计划任务。

验证包为 `artifacts/RecoveryGuard-3.0.0.0-release-symbols-validation.zip`，SHA256 `5a7bd2015f971832b1a1b1cc6c7d679e4cc2dc1420bff328e5dc82012a78dea9`。configuration=Release，但 deliveryStage 仍为 ObserveOnlyCommissioning，automaticExecutionReady=false；该包是本轮包装验证产物，不是最终自动恢复版本，也没有把调试符号存在等同于完整源码冻结和真实安装验收。当前 C# 批次的全量日志 `artifacts/recoveryguard-idle-worker-adaptive.log` 仍待进程终态。

## 2026-09-08：独立打包输入指纹与已测二进制对账

空闲执行者监督批次全量 Release 控制回归已退出码 0、800/800（`artifacts/recoveryguard-idle-worker-adaptive.log`）。本轮只修改打包脚本和文档。

独立包新增 sourceSnapshot，按两个项目实际 Compile 清单、版本 props、配置、打包/安装脚本及随包说明收集输入，包含未被 Git 跟踪的新源码；拒绝未覆盖项目依赖、动态 Compile 和越界路径。每项记录相对路径、长度与 SHA256，再按序生成总指纹。打包前取快照，实际 Rebuild 后复制文件，再复核快照一致才写清单。SkipBuild 包明确 builtFromVerifiedInputs=false，不能将仅版本相同的旧产物声称为本次源码构建。

源码快照测试 5/5、退出码 0（`artifacts/recoveryguard-source-snapshot-test2.log`），覆盖换目录稳定性、源码修改、README 修改、未跟踪 Compile 输入及动态路径拒绝。首轮测试发现新测试脚本含中文路径且缺少 UTF-8 BOM，在 Windows PowerShell 5.1 被错误解码；补齐 BOM 后通过，失败日志保留。

执行实际 Release Rebuild 后，20 个输入的前后指纹为 `cd75f182015c6ec35bdf5f9b8c039cac82c5cfb65db5dabba467b9428e83a8a5`，builtFromVerifiedInputs=true。包 `artifacts/RecoveryGuard-3.0.0.0-source-bound-validation.zip` 的 SHA256 为 `620d4b85900163a7ea453c00c91cfab7e89207df5af6b6dab3d998d2478053a9`。EXE/DLL 与契约测试目录已测文件哈希一致（`artifacts/recoveryguard-source-bound-binary-comparison.json`）。包检查 11/11、CLI 12/12 均退出码 0（`artifacts/recoveryguard-source-bound-package-test.log`、`artifacts/recoveryguard-source-bound-cli.log`）。

该包仍为 ObserveOnlyCommissioning，不能作为最终自动恢复交付。本次输入指纹不是源码内容归档，也不将环境 SDK/Framework 描述为已包含在源码清单内。自动执行配置开放、真实安装态验证与最终整体交付仍未完成。

## 2026-09-08：安装模式开放入口及配置联动回退

本轮补充安装器 RecoveryMode 参数，支持 ObserveOnly、RecoverExited、RecoverStalled；未显式指定时保留已有合法配置。新包默认配置仍要求 ObserveOnly。自动模式仅允许 schema 2、AutomaticRecovery 交付阶段、automaticExecutionReady=true、Release 且 builtFromVerifiedInputs=true 的包；当前打包器没有生成此类正式包，所以现有观察包不能利用参数开放执行。显式请求不被当前包允许的模式，在安装修改和管理员检查之前拒绝。

模式解析后的拟应用配置交给 Guard 自身 validate-settings 检查参数关系。配置、执行任务 Enabled 状态和安装登记 executionEnabled 使用同一个结果，并加入原两任务事务的回退范围。事务证据增加旧配置原始字节，任何后续步骤失败都恢复旧配置；恢复失败保留维护阻断。观察模式不会启用执行任务。此实现只是正式开放所需的安装入口，不代表真实安装态验收或包就绪标记已完成。

Windows PowerShell 5.1 任务/模式检查 24/24、配置联动回退 8/8、已有包校验及未发布模式拒绝 12/12 均退出码 0（`artifacts/recoveryguard-mode-definition-test.log`、`artifacts/recoveryguard-mode-transaction-test.log`、`artifacts/recoveryguard-mode-package-test2.log`）。测试中的 AutomaticRecovery 身份是策略测试输入，不是现场验收记录或已发布包。没有调用真实任务修改。

本轮再次核实当前 Windows 令牌不是管理员，已向用户确认可用的隔离验收环境，尚未收到回复。完整自动恢复交付还需明确正式包生成门槛、匹配组件预检和真实管理员安装态证据；当前不改写包标记为已就绪，也不把已有 ObserveOnly 验证包作为最终目标。

## 2026-09-08：自动模式的主程序组件预检

安装器自动模式新增 Assert-GuardMainComponents，不再只检查主 EXE 的文件版本。要求主程序目录的 build-identity 为 FORMAL_RELEASE、deploymentApproved=true、gitDirty=false，版本匹配且 watchdogSchema=7、sessionAgentSchema=8；核验主 EXE 总身份哈希，并逐项检查主程序、Watchdog、SessionAgent、SafetyAgent、SafetyHardware、Protocol、Client、RecoveryControl、Controller 共九项的清单唯一性、实际文件版本、程序集名称/版本和 SHA256。两侧 RecoveryControl 的文件哈希必须一致，主程序须有正式无人值守启动标记。

磁盘产物预检不能证明正在运行的 Supervisor 已加载这些版本；运行服务与用户会话的真实身份、协议接线仍需安装态验收。测试元数据是临时目录内的 fixtureOnly 输入，不作为正式发布批准。当前没有生成 automaticExecutionReady=true 的包。

预检及拟配置验证已移至共享授权注册之前。首次安装不再先复制配置再测试，而是在配置/任务/登记事务中写入；首次安装失败时可恢复原先不存在的配置文件。已建版本目录和共享历史数据不递归删除。

验证均退出码 0：主组件预检 10/10（`artifacts/recoveryguard-main-components-test2.log`），覆盖代理缺失、dirty/暂存身份、协议不符、重复清单、内容篡改、同版本错误程序集、两侧共享 DLL 不同及启动标记缺失；首次配置与既有配置回退 8/8（`artifacts/recoveryguard-main-components-transaction.log`）；任务/模式 24/24（`artifacts/recoveryguard-main-components-modes.log`）；包校验 12/12（`artifacts/recoveryguard-main-components-package.log`）。本轮未改动 C#，未操作实际计划任务或批准任何真实主程序包。最新安装脚本修改尚未进入最终交付包，管理员环境确认仍待回复。

## 2026-09-08：安装态证据范围与包内容绑定

复核现有验收脚本后确认：Test-MTTFTest-RecoveryGuardInstalledTask.ps1 只验证 SYSTEM 观察扫描/执行任务能够启动、退出并清理；Test-MTTFTest-InstalledRecoveryE2E.ps1 的既有服务/会话代理 E2E 没有 Guard 自动接管场景。因此不能以任一脚本的 passed 或 actualTaskLifecycleVerified 直接开放 AutomaticRecovery。

Guard 任务报告升级为 schema 2，明确 scope=GuardObserveOnlyTaskLifecycle，始终声明 automaticRecoveryVerified=false、installationUpgradeRollbackVerified=false。新增包身份文件、Guard EXE、RecoveryControl DLL、测试脚本及两个任务 XML 的 SHA256 与生成时间，防止同版本不同文件的报告混用。既有服务 E2E 报告也明确 scope=SupervisorSessionAgentInstalledRecovery、recoveryGuardAutomaticRecoveryVerified=false。

使用已核验的源码绑定观察包生成可审查计划：`artifacts/recoveryguard-bound-admin-plan/results.json`。六份绑定文件哈希复核一致，结果为 `artifacts/recoveryguard-bound-admin-plan-verification.json`。planOnly=true，实际任务生命周期、自动恢复和升级回退均未声称通过。没有注册任何 SYSTEM 任务；当前会话仍非管理员，管理员环境选择尚待用户回复。

后续正式交付必须分别补充 Guard 经实际 Supervisor/SafetyAgent/SessionAgent 恢复入口的集成证据、管理员安装任务生命周期/升级回退证据，以及与这些证据绑定的正式包生成条件。不能把本次报告格式完善称为这些运行验收已经完成，也不能用人工改写 ready 标记替代它们。

## 2026-09-08：恢复 E2E 测试宿主的构建断点

继续检查完整 Guard 集成场景时，发现 UnattendedRecoveryTestMain 链接生产 LaunchCapabilityGate.cs，却缺少 RecoveryControl 项目引用。当前源码首次构建报 CS0234（`artifacts/recoveryguard-e2e-fixture-build.log`），说明旧测试宿主没有随着新的启动准入契约一起保持可构建。本轮在测试项目补齐显式 ProjectReference，没有修改生产启动准入限制。

重新构建现有隔离 E2E 宿主包成功，退出码 0（`artifacts/recoveryguard-e2e-foundation-package.log`）；输出 `artifacts/UnattendedRecoveryE2E-3.0.0.0-guard-foundation`。18 个文件与清单 SHA256 一致，主测试程序、无硬件 SafetyAgent、Watchdog、SessionAgent、RecoveryControl 的程序集版本均为 3.0.0.0，核验记录为 `artifacts/recoveryguard-e2e-foundation-verification.json`。testOnly=true、productionRelease=false、automaticRecoveryVerified=false、installationPerformed=false；没有把此测试宿主包部署到实际服务。

代码核查明确下一缺口：TestMainProgram 目前仅发布 WatchdogHeartbeat 和崩溃恢复种子，没有为 Guard 建立共享运行授权、绑定恢复运行、发布逐通道可信快照与持久进度。既有脚本也没有驱动 Guard 接管。因此现有 E2E 构建通过不能作为完整 Guard 自动恢复已通过。后续需补测试主程序桥接与实际 Guard 场景，再在已确认的隔离管理员环境运行；不能用测试计数或伪造权威记录跳过这条链路。

## 2026-09-08：补入测试宿主 Guard 授权与持久进度入口

新增 `Tests/UnattendedRecoveryE2E/GuardTestRun.cs`，仅在测试宿主目录存在 `E2E.Guard.enabled` 时启用。测试包构建脚本默认不放置该标记；隔离集成场景尚需显式接线。入口不自行注册共享授权目录，使用已注册的 RecoveryControl。初次测试启动建立人工运行授权并绑定 Watchdog 会话；恢复进程必须找到当前进程身份匹配的 Started 启动记录，沿用原授权和配置身份，再绑定新的 runId，不通过 BeginManualRun 重建恢复授权。

恢复运行在共享事务进入 Verify 前不提交测试进度。每次测试提交前检查最新输出授权，测试日志追加并 Flush(true) 后再次检查，随后发布通道 4 的测试快照。日志刷盘失败不发布持久进度；看门狗停止请求锁存本进程停止，独立人工停止由共享授权检查阻断。该日志记录的是无硬件测试数据，采样/控制序号为测试模拟值，不能证明现场采集、液压安全或生产周期成功。未启用标记时保留原测试宿主行为。

Release 测试宿主编译成功（`artifacts/recoveryguard-e2e-bridge-build.log`）。新增 `Tools/Test-MTTFTest-RecoveryGuardE2EBridge.ps1`，在随机临时目录中使用真实 Core 存储验证，5/5 通过，退出码 0（`artifacts/recoveryguard-e2e-bridge-test3.log`）：日志与快照持久序号一致、本进程停止阻断、人工停止阻断、写入失败不发布、无 Started 记录不得恢复授权。前两次脚本执行因 PowerShell 反射参数封装问题失败，修正为精确构造函数调用及字符串转换后通过。

本轮没有运行服务、计划任务、设备或完整恢复链路；没有重生成正式安装包。当前新增入口仍须通过真实 Guard→Supervisor→SafetyAgent→SessionAgent 场景验证，尤其是 Launch→Verify 等待及恢复后的连续提交。管理员任务生命周期、安装升级回退与现场设备验收仍未完成。上述局部通过不改变 automaticRecoveryVerified=false 的交付结论。I0050 液压状态发布一致性继续留在第二步。

## 2026-09-08：安装态 E2E 测试输入完整性检查

发现既有安装态脚本只检查 testOnly/version 和若干文件存在性，未验证 e2e-package-identity 中的 SHA256。已在服务、任务及 ProgramData 操作前加入 Assert-E2EPackageFiles：核对全部列出文件的哈希、必要入口是否都在清单、拒绝重复/绝对/越界路径、重解析文件或子目录，以及未列入清单的额外文件。启用 Guard 的测试标记也必须由测试包清单覆盖，不能在既有包上随意加文件后继续冒用原清单。

本轮仅提取并执行纯文件检查函数：现有 guard-foundation 测试包通过，将内存清单一项哈希改成全零后按 E2EPackageHashMismatch 拒绝；PowerShell AST 解析通过，命令退出码 0。没有启动安装态 E2E。下一步仍为生成包含 Guard 测试接线的完整隔离测试包并运行实际恢复场景，当前不具备自动恢复正式验收结论。
## 2026-09-08：生成 Guard 直接执行进程集成场景

Build-UnattendedRecoveryE2EPackage.ps1 新增 -IncludeGuard，构建并加入真实 Guard EXE/PDB、配置及 E2E.Guard.enabled；两侧 Core 哈希必须相同。当前输出为 `artifacts/UnattendedRecoveryE2E-3.0.0.0-guard-direct-worker`，构建退出码 0（`artifacts/recoveryguard-e2e-direct-worker-build.log`），文件清单及 Guard 二进制配置验证通过（`artifacts/recoveryguard-e2e-direct-worker-package-verification.json`）。该包 testOnly=true、productionRelease=false；Mode=RecoverExited、ScanSeconds=2 仅为此隔离试验参数，其他时限沿用示例，不代表现场参数定稿。

安装态脚本识别清单 guardScenario=true 后执行专用分支：隔离注册 Core→通过既有 Supervisor/SessionAgent 启动测试主程序→启动真实 Guard --execute→等待可信刷盘进度→终止无硬件测试主程序→等待 Guard 持久事务 Complete 且释放所有权→检查原授权/rootRun 未变、新主进程及至少两次验证提交→检查完成后继续提交→显式人工停止后终止测试进程并观察 10 秒未再次拉起。旧看门狗自行恢复而没有 Guard Complete 不能满足该分支。原服务/代理故障注入分支保留给未包含 Guard 的包。

报告绑定测试脚本和包清单哈希。guardDirectWorkerRecoveryVerified 仅在专用分支全部完成后为 true；recoveryGuardAutomaticRecoveryVerified、systemGuardTaskVerified、physicalHardwareSafetyVerified 仍为 false。直接执行进程不覆盖 SYSTEM Guard 调度任务；无硬件 SafetyAgent 也不证明设备断能。10 秒停止观察仅是局部检查，不证明跨重启或长期不误恢复。

尚未实际运行上述安装态场景。后续需在无既有 MTTFTest 服务、任务、进程和 ProgramData 的隔离管理员环境执行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File Tools/Test-MTTFTest-InstalledRecoveryE2E.ps1 -PackageDirectory artifacts/UnattendedRecoveryE2E-3.0.0.0-guard-direct-worker -ConfirmIsolatedEnvironment -EvidenceDirectory artifacts/GuardDirectWorker-InstalledEvidence
```

该命令创建并最终清理专用隔离环境中的实际 Supervisor 服务、SessionAgent 任务和测试数据；不能在生产机执行。当前工作尚不包含此运行结果，若阶段超时或旧看门狗先取得恢复权导致无 Guard Complete，应保留失败证据并修正场景，不得降低通过条件。最终自动恢复安装包、SYSTEM 安装升级回退及硬件验收仍未完成。
## 2026-09-08：安装态验收环境复核

当前机器 L、账户 L\L，进程非管理员；本机已经存在 MTTFTestSupervisor 服务以及 ProgramData\MTTFTest。测试包存在，源码版本为 3.0.0.0。本机不满足安装态脚本要求的“无既有服务/任务/数据的隔离环境”，即使提升权限也不能直接执行现有隔离脚本。不能删除或覆盖本机既有服务、任务和数据以满足测试前提。

下一项关键外部输入是可用的干净 Windows 虚拟机或测试机及其管理员执行环境。待环境确认后运行已准备的直接 Guard 场景，依据真实结果修复，再进行 SYSTEM 任务和安装升级回退验收。当前未运行上述实际验收，最终交付仍未完成。

## 2026-09-08：可转移隔离验收压缩包

已生成 artifacts/RecoveryGuard-3.0.0.0-isolated-acceptance.zip，包含 Package、独立 PowerShell 脚本、中文 README 和逐文件哈希清单。ZIP 内 25 个负载文件逐一校验通过。SHA256：78198F870BEC1C15A9C98E07BA3E3738374CBE87300F254AD5510ACF3D963448。该包无需完整源码仓库即可在符合条件的隔离管理员环境执行；尚未运行安装态验收，不是正式安装包。等待测试环境确认及实际运行证据。

## 2026-09-08：JXCQ 隔离管理员环境实测

用户确认在测试机 `MT-20251206JXCQ` 执行远程测试。测试前先封存原 V2.17.3.0 安装目录、ProgramData、服务及任务状态；安装目录压缩包 SHA256 为 `9907A451FE6A897F59AEABDF383A83BB63B73E06740138B638620479CFD101EF`，`7z t` 通过。随后卸载原环境并在无 MTTFTest 服务、任务、进程和 ProgramData 的隔离状态运行 3.0.0.0 无硬件测试包。

Guard→Supervisor→SessionAgent 直接执行场景的前三次试验依次暴露了测试驱动没有重复检查、`SuspectCount=1` 时尚不能执行，以及 Supervisor 未消费 Guard 严格 V4 启动权威的问题。生产代码已在同一硬件启动全局锁内补入严格权威消费和准确 Started PID/启动时间提交，并绑定操作号、代次、许可、修订、EXE 路径及 SHA256，重复消费失败关闭。定向恢复回归 **39/39** 通过。

修复后，JXCQ 场景在北京时间 **11:10:10—11:10:47** 通过 **7/7**：初始 Supervisor 启动、可信监督、连续 Guard 执行周期、恢复并完成 Verify、恢复后持续提交、仅一个恢复主进程、人工停止后不再拉起。证据位于 `artifacts/RemoteGuardE2E/20260908-1110-strictlaunch-pass`。报告范围是 `GuardSupervisorSessionAgentNoHardwareRecovery`，`guardDirectWorkerRecoveryVerified=true`；它明确保持 `recoveryGuardAutomaticRecoveryVerified=false`、`systemGuardTaskVerified=false`、`physicalHardwareSafetyVerified=false`。

真实 SYSTEM 任务首轮注册发现 Windows 任务 XML 使用 `UserId=S-1-5-18` 时显式 `LogonType=ServiceAccount` 被该机拒绝。安装器改为采用 Windows 导出兼容的省略 LogonType 形式，归属校验仍只接受省略值或 ServiceAccount，并新增 InteractiveToken 篡改拒绝。任务定义回归 **25/25** 通过；修复后的 JXCQ ObserveOnly 真实任务生命周期 **4/4** 通过，证据位于 `artifacts/RemoteGuardSystemTask/20260908-1120-pass`。此项实际注册、运行并清理 SYSTEM 任务，但执行任务处于禁用状态。

随后执行真实安装事务故障注入。最终 `installation.json` 原子提交失败后，生产事务记录为 `RolledBack`，两项真实 SYSTEM 任务回到 0，配置恢复为不存在，共享 RecoveryControl 注册保留为可安全重试的同一安装身份。首次安装和升级均成功，升级切换到新的不可变版本目录。过程中还发现卸载从新 PowerShell 进程调用时，参数默认的 `$PSScriptRoot` 可能为空；已改为进入脚本后初始化默认 SourceDirectory。最终北京时间 **11:32:25—11:32:34** 的真实故障回滚、首次安装、升级、卸载 **4/4** 通过，报告为 `artifacts/RemoteGuardInstallLifecycle/20260908-1150-pass/results.json`，SHA256 `5581727339DACA4A1224AE580BE73F0FC47FD3D87A76089AA56894666A798B0B`。取回的执行脚本与本地脚本 SHA256 均为 `8B1E239E44F032557BB903DBA92EE143E237EA3E649CF27F3DF8664B0CB2F74D`，远程包身份与本地试验包身份 SHA256 均为 `B0AAAABDBD5530B564315B1AC6AD7E5708BD6AC239DA98898F1419FE9F85FD85`。

测试结束后已从封存副本恢复 JXCQ 原 V2.17.3.0。北京时间 11:34 安装脚本成功完成；复核 `build-identity.json` SHA256 为测试前相同的 `1BC3EB7DAE7D80203190EF8DF327087263A70A11DFD7E256D611D921FACB0E6E`，Git 提交为 `0d80572f5fe7da4c004a9471e641055e91b4b615`。Supervisor 为 Running、SessionAgent 进程 1 个，原 AutoStart/RecoveryHealth/SessionAgent 三项任务恢复；Guard 任务、Guard 进程、Guard ProgramData 和 RecoveryControl 测试目录均不存在，主程序没有被测试流程自动启动。测试前后状态及恢复部署结果已取回 `artifacts/RemoteGuardJxcqRestore/20260908-1135`。

本轮远程实测收口了无硬件恢复链、真实 SYSTEM 任务定义以及安装/升级/回退/卸载。仍未完成的是由 SYSTEM 周期任务自动触发 RecoverExited/RecoverStalled 的整链验证，以及 NI/电源/液压的物理安全与业务圈数对账。当前程序不包含 CAN 硬件。因此当前可交付的是经过真实安装验证的 **ObserveOnlyCommissioning** 独立包；在上述两类证据补齐前，不把清单改为 `AutomaticRecovery`，也不启用执行任务。I0050 液压恢复状态发布一致性仍按用户要求留在第二步。

## 2026-09-08：JXCQ 验证后的安装包与最终软件回归

已使用修复后的安装器、Release 重建产物及更新后的随包说明生成 `artifacts/RecoveryGuard-3.0.0.0-ObserveOnly-JXCQ-Validated.zip`，大小 165259 字节，SHA256 `4E62E578690B3C90C48A6A58F0837704F75BE10B68A758F5CA6715BB79A81126`。包身份 SHA256 为 `C061599FCD0BFFC6390B353A8C236A0922338396C1555F055554B77224D46856`，版本 3.0.0.0、Release、`builtFromVerifiedInputs=true`、`deliveryStage=ObserveOnlyCommissioning`、`automaticExecutionReady=false`。当前工作区包含本次尚未提交的实现，清单如实记录 `gitDirty=true`；因此该包是可安装的 JXCQ 验证版观察包，不是 FORMAL_RELEASE 自动恢复包。

最终包校验 **12/12**、独立 CLI **12/12**，任务定义 **25/25**、安装事务模拟故障 **8/8**。最新完整控制回归 **801/801**、磁盘 **70/70**、电源 **19/19** 全部退出码 0，日志分别为 `artifacts/recoveryguard-jxcq-final-adaptive.log`、`artifacts/recoveryguard-jxcq-final-disk.log`、`artifacts/recoveryguard-jxcq-final-power.log`。这些软件结果不改变自动任务和物理硬件尚未验收的边界。

## 2026-09-08：JXCQ 的 SYSTEM 双任务自动恢复整链

在第二个隔离窗口继续验证正式任务名 `MTTFTestRecoveryGuard` 与 `MTTFTestRecoveryGuardExecution`。测试由 JXCQ 的交互会话任务启动，实际恢复扫描和执行任务均以 SYSTEM 运行；扫描任务通过生产 `schtasks /Run` 路径请求执行任务，恢复动作仍经 Supervisor、SessionAgent 和无硬件 SafetyAgent。故障扫描由测试脚本手工触发同一 SYSTEM 任务以缩短等待，任务动作、主体、安装登记及严格校验均保持生产形态；因此本项证明 SYSTEM 任务接管链，不单独证明故障恰好发生后仅靠下一次一分钟时间触发器的时序。

首轮真实接管暴露生产校验缺陷：Windows 导出启用任务时会省略默认值 `<Enabled>true</Enabled>`，`RecoveryWorkerDispatch.ValidateTask` 原先要求该节点必须显式为 true，导致 `RecoveryWorkerTaskDefinitionMismatch`，Guard 正确拒绝派发，既有 Watchdog 约 6 秒后恢复主程序。现改为仅在节点存在且不是 true 时拒绝；显式 false 仍拒绝。新增 Windows 导出形态回归，任务定义测试由 25 项增至 **26/26**，RecoveryGuard 独立回归 **91/91**。

修复后的正式场景在北京时间 **12:43:04—12:43:52** 通过 **10/10**。执行日志依次包含 `ClaimCommitted`、`ActionCompleted:SafeStop`、`ActionCompleted:Launch`、`RecoveryCompleted`，恢复事务 `3e73ee07106f4a63b51af6b2dccd6443` 达到持久 `Complete` 并释放所有权；恢复主进程 PID 5140，随后持续产生新的业务提交。测试写入明确人工停止后，10 秒内没有再次拉起。runner 在会话 1 退出码为 0，报告声明 `recoveryGuardAutomaticRecoveryVerified=true`、`systemGuardTaskVerified=true`、`physicalHardwareSafetyVerified=false`。

通过证据位于 `artifacts/RemoteGuardSystemTaskE2E/20260908-124303-pass`。测试脚本 SHA256 为 `6C7E07F3F7DFA6EDB5015065DA7890088D44FB38F5CF3A34132920E067959057`，测试包身份 SHA256 为 `B3FF69FCA0F2A0BF1F3B13AB35229F32603DD43C1548FC1803EE29A66C1CD1EC`，两者与 runner 报告绑定一致。无硬件测试包仍为 `testOnly=true`、`productionRelease=false`，不能替代 NI/电源/液压断能和真实圈数对账；CAN 不在当前程序硬件范围内。

测试完成后再次恢复 JXCQ 原 V2.17.3.0。北京时间 12:47 复核安装身份 SHA256 `1BC3EB7DAE7D80203190EF8DF327087263A70A11DFD7E256D611D921FACB0E6E`、提交 `0d80572f5fe7da4c004a9471e641055e91b4b615` 和主程序 SHA256 `E67E7A2598290E89E9B93E1FC75B41C899680D917A88F79D587FDD92094CE201` 均与测试前一致。Supervisor Running、SessionAgent 单实例，原三项任务存在；主程序为 0，Guard 任务、进程、状态目录及测试 RecoveryControl 均不存在。恢复报告为 `artifacts/RemoteGuardJxcqRestore/20260908-1248-after-system-task-e2e/restore-verification.json`。

当前软件侧已完成无硬件 SYSTEM 双任务 RecoverExited 接管验证，但正式自动包仍不开放。原因是执行链包含安全处置，而 `physicalHardwareSafetyVerified=false`，尚无真实 NI/电源/液压断能、通道隔离和业务圈数对账证据；按实施方案第 10—12 节，交付继续保持 `ObserveOnlyCommissioning`。当前程序不包含 CAN 硬件。I0050 液压恢复状态发布一致性仍按用户要求留在第二步。

已将上述任务导出兼容修复和更新后的随包说明重新构建为 `artifacts/RecoveryGuard-3.0.0.0-ObserveOnly-JXCQ-SystemTask-Validated.zip`，大小 168611 字节，SHA256 `DBB40568B1FC8C80D49885F68F66AA4E4D99B4438EFDAC71295381751F5C105C`。包身份 SHA256 为 `F154CDAF38BE9836E0C65B254D2D09D683CC70201948F0F751BBAC5DA1DE7002`，Guard EXE SHA256 为 `CC4526B75445C24AA39103FE913D834106D68BE717CBD27E962F6A71F85DD924`。身份为 3.0.0.0、Release、`builtFromVerifiedInputs=true`、`gitDirty=true`、`deliveryStage=ObserveOnlyCommissioning`、`automaticExecutionReady=false`。最终包校验 **12/12**、独立 CLI **12/12**，均退出码 0；本轮受影响的 Guard 回归 **91/91**、任务定义 **26/26**。该 ZIP 是当前可安装交付物，但不会自动续测。

## 2026-09-08：JXCQ 现场硬件验收条件只读盘点

为判断能否继续开放 AutomaticRecovery，对恢复后的 JXCQ 做了只读设备、配置和连通性盘点；未启动主程序、未写数字/模拟输出、未发送串口或电源控制命令。原程序配置使用 `Dev1`、`Dev2`，并配置四台电源 `192.168.1.101`—`192.168.1.104` 的 TCP 2268 端口。

Windows 即插即用层能看到两台状态为 OK 的 `cDAQ-9185`，但四个电源地址均无法 Ping，TCP 2268 连接均超时。只读盘点还枚举到与当前程序无关的遗留 CAN 设备状态；按用户再次确认，当前程序不包含任何 CAN 硬件，该信息不参与后续分析、验收或阻断。原始盘点为 `artifacts/RemoteGuardHardwareInventory/20260908-jxcq-readonly.json`，连通性结果为 `artifacts/RemoteGuardHardwareInventory/20260908-jxcq-connectivity-readonly.json`。

当前状态不能形成“电源已断、液压已泄压、恢复后真实圈数连续”的验收证据。当前不执行带输出的故障注入，也不把无硬件 SafetyAgent 结果升级为物理安全通过。继续验收前需要确认四台电源上电且 2268 端口可达，并由现场人员确认测试件及液压系统处于允许故障注入的工况。CAN 和遗留 CAN 驱动状态明确不属于前置条件、验收项或阻塞原因。该外部条件不影响当前 ObserveOnly 包使用，但电源及液压现场条件仍阻止 `physicalHardwareSafetyVerified=true` 和 AutomaticRecovery 包生成。

## 2026-09-08：首次启动修复后的最终交付复核

本节更新前述阶段记录，历史测试结果仍按各自测试包和验证范围解释。最新完整交付为 `artifacts/deploy/V3.0.0.0_正式版_78f74a6f3a21_QUICKDEPLOY_GUARD.7z`，版本 3.0.0.0，大小 34,333,335 字节。本轮重新计算 SHA256 为 `100FE060362A2D7A78B9152D54426D5CE76B0F42E1EE119E2FC22FD3A4E45A8B`，与交付清单一致。

最新版首次学习失败的软件原因、修复、最终同包 JXCQ 验证与限制，以[首次启动修复交付报告](2026-09-08_V3首次试验启动失败_原因修复与安装包.md)为准。JXCQ 对账文件 `artifacts/field-deploy-20260908-v3/fix-jxcq/comparisons.json` 证明：既有配置安装后及回退后均无变化、回退主程序哈希匹配、测试 Guard/Core 与最终包一致。该结果不能替代真实硬件恢复验收，也不能将历史无硬件 SYSTEM 恢复试验视为最新完整包已经通过真实台架恢复。

总目标审计：大版本迭代、软件修复、完整安装包和 Markdown 交付已有证据；实施方案第 10—12 节的现场学习、业务落盘及物理安全恢复验收仍未完成。交付保持 ObserveOnly、自动执行关闭，不将总体目标标为全部完成。用户最新要求由其判断是否启动新一轮部署测试，因此不部署新包、不停止 wj-epb。下一轮现场授权后，先验证学习和正式运行，再按对应阶段核验恢复；I0050 液压状态一致性仍留第二步。

## 2026-09-08 第二轮修复包现场结果更正

78f74a6 修复包完成安装，但学习再次触发持久化暂停，恢复最终以 RecoveryLegacyAuthoritySessionOrRunSuperseded / PermitNotConsumable 阻断，未进入正式运行。已正常安全停止，保留全部新数据并回退 2.14.2.11；21:56:10 与 21:56:55 两次对账六通道 Running，正式圈和 SQLite 提交各增加3，回退续测通过。详见[第二轮部署记录](2026-09-08_wj-epb_V3修复包_第二轮部署与观察.md)。软件修复仍未完成现场闭环，不能把包交付/JXCQ通过当作总目标完成。
