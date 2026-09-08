# RecoveryGuard 与 Supervisor 的接管启动协议

日期：2026-09-07。适用：当前 3.0.0.0 实现中的 Guard 启动协议 schema 1。

本文说明已经接入 Supervisor 的启动入口及其限制。独立 Guard 的完整执行器尚未接入，默认仍为 ObserveOnly；本文不是自动恢复或安装态验收报告。整体进度见[实现与交付进度](02_Issues/2026-09-07_RecoveryGuard_实现与交付进度.md)。

## 组件边界

协议定义位于 [RecoveryGuardSupervisorProtocol.cs](../RecoveryControl/RecoveryGuardSupervisorProtocol.cs)，只使用 .NET Framework。独立 Guard 不从业务 Current 加载 Watchdog.Protocol。Supervisor 的接收与安全证据校验位于 [SupervisorGuardLaunch.cs](../MTTFTest.Watchdog/SupervisorGuardLaunch.cs)。

使用已有 Supervisor 控制管道，新增独立请求标识 `MTTF-RECOVERY-GUARD-LAUNCH-V1`，不修改旧 sidecar 请求的身份校验。请求和响应以长度限定的 UTF-8 JSON 承载，最大 64 KiB；客户端连接预算 5 秒、连接后交换预算 20 秒。`LaunchAsync` 使用异步管道读写并接受取消令牌，执行器可用剩余动作预算进一步限制等待；同步 `Launch` 保留兼容入口。内部超时返回明确的通信不确定错误，调用方取消保留取消异常。两者均结束本次客户端连接，但不证明服务端尚未执行，必须按原 OperationId 对账，不能据此创建新操作重试。

独立客户端通过 `RecoverySupervisorPeer` 验证服务端：发送请求前和接收响应后，调用 Windows 服务管理器读取 `MTTFTestSupervisor` 的正在运行的独立服务进程 PID，再与 `GetNamedPipeServerProcessId` 得到的真实管道服务端 PID 比对，并复核服务身份未在检查期间变化。服务不存在、未运行、PID 不匹配或发生替换均拒绝；不会仅凭管道名称或服务端自报身份接受安全完成响应。该实现只依赖系统 API，不加载业务 Protocol DLL。私有备用管道入口仅用于隔离传输测试，所有公开客户端方法均使用生产端点并执行核验。

## 请求与响应

| 请求字段 | 约束 |
| --- | --- |
| SchemaVersion | 必须为 1 |
| RequestId | 本次交换的非空 GUID N |
| TransactionId、Epoch | 当前持久接管事务及代次 |
| Owner | PID、启动时间、可执行路径、系统启动身份；还须与 Windows 管道实际客户端相符 |
| OperationId | 已准备的持久启动操作，重试不能任意更换 |
| SafetyAuthorityId | Supervisor 保存的安全权威记录标识，不是项目证据镜像路径 |

客户端不能指定可执行文件、工作目录、命令行、许可或任意项目路径。Supervisor 从登记会话、安全权威与既有 LaunchIntent 推导这些内容。

响应包含 SchemaVersion、RequestId、OperationId、Accepted、Process 和 Detail。成功响应必须带准确进程身份。**Accepted 仅说明启动或已有创建结果得到确认，不代表学习、续测或业务提交成功。**

## 实际准入与启动顺序

1. 校验报文结构，以 Windows 管道实际客户端 PID 和准确进程身份确认调用者。
2. 从共享权威核对当前 Guard 所有者、事务代次和 Launch 阶段，检查租约、阶段期限及监督有效期；维护禁止优先。
3. 核对当前运行对应的 Supervisor 登记会话，确认准确旧主程序已退出。
4. 读取 Supervisor 的安全权威，验证完整安全完成条件、原运行、原进程、会话、程序身份以及已批准的恢复许可绑定。验证冻结的安全配置快照。
5. 读取对应持久化 LaunchIntent。缺失、不可信、已撤销、运行不匹配或操作/许可不一致时拒绝；不由 Guard 的普通请求临时拼装替代。
6. 进入和普通启动共用的 Supervisor 启动锁，再核对维护标志和 Guard 所有权，沿用安装级十分钟三次限频。
7. 对账未确认消费及既有创建记录；同一操作已经创建时返回准确结果，旧创建未确认退出时禁止第二次创建。
8. 由 SessionAgent 消费共享预留并创建进程。最终消费再次检查当前 Launch 阶段、阶段期限、所有者租约和人工意图；进入冷却或过期后，迟到请求不能继续消费。
9. 主程序仍须通过启动身份与运行准入检查。若它先于 Guard 的启动响应到达准入，会在准确绑定后异步等待持久 Verify，期间不放行输出；停止、阶段变化、阶段或租约到期均拒绝，取消令牌可以结束等待。Guard 执行器须实际提交 Verify，再使用参与通道的业务提交判断结果；该执行器的完整接入仍未完成。

权威短事务锁不包裹进程查询、硬件动作或管道交换。已创建事实可以在之后补记；不得把动作期限结束解释为进程未创建，也不得因完成/取消状态出现就直接删除所有权记录。

## 失败与接续边界

### Guard 安全交接准备入口

新增请求标识 `MTTF-RECOVERY-GUARD-SAFETY-PREPARE-V1`，独立客户端通过 `PrepareSafetyAsync` 调用，共用有界、可取消的传输。请求仅含 schema、RequestId、TransactionId、Epoch 和 Owner；不接受项目目录、程序命令行、安全完成位或客户端拼装的安全回执。Supervisor 以实际管道 PID 校验调用者，核对当前 SafeStop 阶段、所有者租约、阶段期限、运行授权和维护状态。

`SupervisorGuardSafetyPreparation.cs` 从 Supervisor 登记的项目及原主程序身份读取冻结种子和现有可信 Approved 许可，确认准确原主程序已退出后，复制并验证安全配置快照。快照须与会话、许可及程序摘要一致。交接标识由安装、接管事务、代次及许可身份确定；已有同一权威须重新核对，损坏主记录或仅存备份均拒绝当作新交接覆盖。提交前再次核对接管权和许可版本。

成功响应仅返回准备好的安全权威 ID。新建回执为 Accepted，电机关闭、电源关闭、压力安全和持久化排空均未完成，不伪造旧 sidecar 身份，也不启动 SafetyAgent。本入口当前覆盖“已有可信 Approved 许可、原主程序已退出”的准备路径；后续使用下述执行入口。缺失许可时自主建立许可及 Guard 完整动作适配器仍须接入，不能将准备成功视为 SafeStop 完成。

Supervisor 的既有 SafetyAgent 创建路径已增加跨交接、跨进程的启动互斥与存量启动记录检查：未知或未确认的创建不能被当成“没有进程”。同一操作可按准确 Started 身份接续；当前 Supervisor 持有创建句柄时允许补写 Started。只有 Intent 且 Supervisor 已重启的情形仍需进一步证据对账。下述 Guard 执行入口复用该路径；真实安装态的多请求竞争和硬件执行仍待验收。

### Guard 安全执行入口

新增 `MTTF-RECOVERY-GUARD-SAFETY-EXECUTE-V1`，客户端使用 `ExecuteSafetyAsync`。请求绑定当前 Guard 所有者、事务/代次及安全权威 ID，不携带可执行文件或命令行。`SupervisorGuardSafetyExecution.cs` 检查实际管道调用者、SafeStop 所有权、期限、维护状态、原主程序确切退出、当前 Approved 许可、冻结配置和对应 Guard 安全权威。SafetyAgent 路径须为 Supervisor 同目录的固定程序且摘要匹配；命令行由可信权威生成。

入口先对账安全执行者。已有准确存活进程时返回 Pending，不重复创建；需新建时进入统一启动互斥，在持锁后的最终回调中重新检查 Guard 授权、许可版本和安全回执是否已变化。启动成功仍返回 Pending。以后轮询时，只有既有完整安全证明与准确执行进程退出同时满足，才返回 Completed；证明缺失、执行失败或身份不明返回 Blocked。崩溃数据按既有 CrashRepairRequired 等口径保留，不能将断能完成写成零丢失或伪造持久化排空。

此入口当前只接续当前 Guard 事务准备的安全权威。旧 sidecar 交接权威的选择/迁移、缺失许可自主建立、Supervisor 失联重建，以及 Program 到完整动作适配器的接入仍须完成。客户端服务端身份核验已接入，实际服务安装态验证仍待完成。默认观察模式未开放自动执行；不能仅凭此入口存在宣称完成无人值守恢复。

- 无响应、响应丢失或创建结果写回失败：保留原 OperationId 并对账。既无准确创建证据也无确定失败证据时，保持不确定，不盲目重发新操作。
- 缺少断能证明：不得通过面板按钮的存在推断可自动断能。用户已确认按钮仅能人工按下。
- 普通 sidecar：不能借用 Guard 最新代次启动；本入口另行检查实际 Guard 所有者。
- 冷却或租约/阶段到期：拒绝尚未完成的启动消费；已创建进程由身份和输出准入继续约束，不伪造退出结论。
- 主程序启动入口仍只支持已有可信安全完成凭证和已准备启动规范。安全准备与安全执行是两个独立入口；恢复失联 Supervisor、重新准备失败许可、跨系统启动会话重建及剩余事务收口仍须继续实施，不能把缺少这些步骤视为最终方案允许永久省略。

## 验证范围

`RecoveryGuardSupervisorTests` 包含报文大小/前导长度拒绝、生产处理器的真实 Windows 管道客户端身份拒绝、安全证据和运行/许可/操作绑定、缺失及撤销启动权威拒绝，并通过独立命名管道验证半条响应停滞时取消等待及分段响应的完整解析。它不启动真实 Supervisor 服务，不接触硬件，也没有宣称完成正向 SessionAgent 安装态启动。

共享契约回归另外覆盖接管中消费时的冷却、阶段和租约边界。完整回归与具体日志以实现进度记录为准，不能用局部协议测试代替自动执行器与现场验证。

## Guard 启动准备接口

新增 `PrepareLaunchAsync`：请求仅包含请求身份、接管事务/代次、准确所有者和安全权威 ID。服务通过命名管道内核 PID 验证请求者，读取 Launch 阶段的有效接管权及已登记 sidecar，不接受客户端传入路径或命令行。准备成功返回持久化的 OperationId，不代表已创建进程。

服务从当前授权快照冻结续测通道，使用严格权威现有 `PrepareLaunchIntent` 完成 Approved → LaunchIntent。快照的接管代次可能早于当前 Claim，因此通道绑定检查使用安装、授权、意图版本、RunId、配置和准确主进程身份；监督有效性由共享权威的阶段准入核验。未消费且规范一致的重复请求保持同一操作身份，已消费的结果走创建对账。

准备和实际启动均要求安全执行者的持久记录证明准确退出；实际启动还核对 sidecar 身份与冻结命令行。此接口仍依赖已有有效许可和可用登记会话，尚不覆盖失联会话重建或许可自主重试；旧安全动作与启动之间的并发闭环仍待补齐。
## 安全执行者与主程序创建互斥

Supervisor 的实际 SessionAgent 启动调用与 SafetyAgent 创建共用安装级 `SupervisorHardwareLaunchGate`，沿用原 SafetyLaunch 全局互斥名称。主程序创建前在同一把锁内核对全部安全执行者的持久进程记录，要求准确退出，并重新验证当前启动/接管权。缺失明确创建结果、损坏记录、存活或身份不明的安全执行者均不能通过该检查。等待互斥有 3 秒期限，未取得时返回可对账的拒绝，不强杀已有执行者。

旧 sidecar 在实际创建 SafetyAgent 前重读共享权威。未释放的 Guard 接管权阻止旧自动恢复请求创建新的硬件占用者；已提交的停止/暂停/完成意图，或独立停止/暂停通知确认后，仍允许 Forbidden 类型的安全停机。单凭 sidecar 的 Forbidden 回执不能覆盖仍有效的 Run 意图。运行中的当前主进程身份已变化时，旧主进程对应的安全请求也被拒绝，避免接管完成后迟到请求干扰新实例。

本项不等于所有恢复路径已完成：已有旧 SafetyAgent 的安全交接复用、执行者失联监督、旧事务收口及安装态硬件验证仍须继续完成。已存在进程的只读重附着不创建第二个实例；主程序最终输出仍受人工意图及 Verify 门禁控制。
## 冷却后的原动作对账

共享接管事务新增 `RetryActionStage` / `RetryActionEpoch`。已绑定安全权威的动作进入 Cooldown/Blocked 时保存原动作阶段；同一代次的冷却结束后通过共享权威的 `ResumeRecoveryAction` 回到该阶段。此入口重查所有者、人工意图、监督时效与冷却期限，不直接开放设备输出，也不将重新获得阶段预算视为业务进展。

例如 Launch 响应丢失而共享记录已为 Started：冷却后回到 Launch，动作适配器核对准确进程后交给引擎进入 Verify，仍需实际业务提交才能判定成功。不会先重跑 SafeStop，亦不重复创建操作。跨所有者/代次的动作不走这一续接捷径，仍需要独立对账收口。
## Guard 确认退出后的故障登记

安全准备入口在没有 Approved 许可时，可对严格权威的 None/Failed 或准确匹配旧主进程的 Attached/Committed 状态登记退出故障。登记前要求主进程准确退出、当前 SafeStop 接管权有效，且共享权威没有 Reserved/Consumed/Started 启动待收口。记录的来源为 RecoveryGuard；旧协议中名为 SidecarProcessId/SidecarProcessStartUtcTicks 的报告者字段填实际 Guard 身份，不冒用注册 sidecar。SessionNonce 使用该接管事务身份，不盗用 sidecar 实例 nonce。

调用既有 RegisterFailureAndDecide 时带入预期 Revision/SHA，锁内发现权威已变化则只返回重试结果，不增加失败计数或替换并发许可。许可仍由既有严格 CAS 签发，预算沿用 Bootstrap 冻结值，Blocked/Revoked/CircuitOpen 不重置。签发后重新核对共享意图和回读；许可本身不替代安全交接或输出准入。

此入口补齐首次/已收口退出故障的登记，不负责跳过未结束的启动。预算熔断可沿用现有严格半开机制，受控接线见文末；不能把更换会话视为每次预算熔断后的必要步骤。
## 会话交接与检查点绑定

共享权威新增 SessionRollover：预留操作与 NextSessionId，保留原会话、源权威摘要、原运行/根运行、配置、旧主进程和旧动作标识。同一预留的重试保持同一新会话，激活只切换协调会话身份，保留用户授权及业务运行身份。原严格权威不在此接口内修改。此前的交接记录在建立下一份交接时归档到 session-history，归档不作为重新授权来源。

Reserve/Activate 只在当前有效 SafeStop 所有权、无 Reserved/Consumed/Started 启动时允许。激活要求服务提供已验证存活的新会话进程；人工停止先于激活提交时拒绝切换。激活后原动作标识不能被当作新会话的安全许可继续使用。

主程序通常仍要求检查点会话完全相同。仅在有效 Activated 交接记录、相同原运行与根运行、当前配置/授权匹配，且当前主实例有对应 Started 创建记录、接管处于 Launch/Verify 时，才允许更新检查点的会话标识。此过程保留项目与 Armed 核验，不能让普通旧检查点跨会话自动恢复。

当前实现了共享交接契约和主程序检查点准入；Supervisor 的旧会话退休、新会话创建、配置种子迁移及首次许可接线尚未接通，因此尚不能据此宣称熔断后的持续恢复已完成。
## 会话切换期间的迟到响应

Guard 生产动作在 ReadOwnedStage、BindRecoveryAction 和动作完成后的 Advance 中带入发起时的 WatchdogSessionId。共享权威在锁内核对该绑定；当前授权存在 Reserved 会话交接或会话已切换时，旧动作不能绑定安全权威、启动标识或推进新会话阶段。该特定拒绝返回待对账状态，执行者继续读取当前会话，不因旧响应退出后制造新的所有者接替。

Supervisor 安全准备、实际安全创建前及 Guard 故障登记的复核也带入原会话身份。后续会话交接执行器必须有独立的交接续办分支，不能将 Reserved 交接直接当普通安全动作推进；旧响应丢弃不等于交接自身已经完成。

## 当前接管者的预算熔断半开

Guard 的 SafetyPrepare 可在当前 SafeStop 所有权下调用严格半开存储。来源必须是当前会话及 Run 的软件预算耗尽，旧主实例准确退出、没有在途启动、没有永久故障或未知消费身份；其它 Blocked 原因不借此重新签发。Claim 冻结 CircuitCooldownSeconds，以持久化 LastFailureDecisionUtcTicks 计算冷却，并保留既有普通/LastKnownGood 最低等待。旧事务缺少冻结值时拒绝此操作。

存储调用带预期 Revision/SHA，在严格存储锁内拒绝过期来源。成功产生新的许可代次和身份，保留累计失败次数；写后重新验证共享意图、会话及新记录摘要。半开只批准后续安全交接，不代表安全执行或续测完成。原 sidecar 的半开、故障登记和启动准备通过共享许可准入锁，与 Guard Claim 互斥；结果提交仍需按实际恢复实例继续核验。

## 已换代且未绑定启动的旧安全动作

SafetyPrepare 也用于对账旧 ActionEpoch 的安全动作，此时请求仍携带当前事务代次和当前所有者，不允许旧所有者发送新代次请求。仅处理 SafeStop 且 LaunchOperationId 为空的事务：按旧 ActionEpoch 派生并验证原 HandoffId，核对原会话、严格 Approved 许可、主实例及安全回执，再在硬件启动锁下确认完整安全完成证明、准确执行者退出和安全执行者清单没有未退出项。

成功后 `ReconcileAdoptedSafetyAction` 在共享权威锁内重新检查意图、监督时效、当前所有者/会话、阶段期限和无在途启动，归档旧动作并清除绑定。接口返回 `Accepted=false`、Detail 为 `RecoveryAdoptedSafetyRetired;PrepareNewSafety`，明确没有准备新的安全权威。Guard 以共享状态为准，确认绑定已清除后返回 Pending，下一轮重新准备当前代次安全动作。不能仅凭响应文本跳到 Retire/Launch，也不能把此路径用于已绑定主程序启动或未知安全执行结果。

安全执行者创建与对账统一使用“硬件启动锁→执行者锁”的顺序，避免创建者持有执行者锁等待硬件锁、对账者反向等待的情况。

## Verify 的证据窗口与失联交接

Claim 冻结 VerificationEvidenceMaxAgeSeconds，Verify 使用独立 VerificationTimeoutSeconds（试验初值 300 秒，大于三个扫描周期）。这给新实例建立基线和取得两次有效提交留出时间；后续观察不延长阶段截止时间。

正常 Complete 提交和旧 Guard 退出后的 Verify 交接均检查当前快照及逐参与通道的实际进展，不能只依据累计提交次数或文件时间。合法有界保压阶段允许提交暂时不增加，但仍要求新鲜采样和本次验证期内已有提交；期限结束后不能继续享受等待豁免。旧事务没有冻结窗口时不借新配置放行。

旧 Guard 已准确退出、租约结束且主实例准确存活时，如果上述证据仍然有效，可以在旧 Verify 动作期限之后完成交接，保留主实例原代次和运行令牌。这里关闭的是已有验证，不是重新执行动作、延长旧动作期限或允许过期授权恢复。停止、监督过期、身份冲突及未知在途启动仍阻断。

证据尚未累积够时，可通过共享权威的 TryObserveOrphanVerification 登记一次有界观察窗口。它要求当前主实例的 Started 创建证明、当前授权和可信快照，并排除未知在途启动。窗口使用原 Verify 预算，存入 OrphanVerificationUntilUtcTicks；不更换所有者/代次，不续租或延长原动作期限。观察者重启不重置窗口。

窗口内完成证据齐全后才允许原子交接；达到截止时间则结束等待并进入普通换代、冷却和对账，不能用迟到证据延长窗口。已创建实例及其旧令牌记录保持原样，超时不意味着安全处置完成或可以创建另一个实例。

### 接管换代后的终态启动核销

Guard 接管换代不会恢复历史启动令牌的效力。仅在当前事务明确绑定旧 ActionEpoch 与 LaunchOperationId、授权及意图仍有效，而且启动已准确退出或存在预留到期未消费证明时，可以沿 SafetyPrepare 核销这一次历史动作。当前所有者、阶段时限、安全工作者退出、严格许可版本/SHA 以及没有在途或存活启动仍需逐项核验。成功后归档旧动作、递增代次并重新准备安全动作；核销本身不启动主程序或输出。

未知消费结果、存活实例、不同操作/授权/意图、未来代次均不适用此路径。新增准备路径在严格 LaunchIntent 写入前，先以 ReserveAndBindRecoveryLaunch 原子登记核心预留与事务操作 ID；准备响应丢失后可以按同一绑定接续，重放不延长预留期限。实际启动服务仍复核并消费该预留。

若准备在严格意图写入前中断，需等核心预留到期且证明未消费。仅当严格许可仍为 Approved、没有启动意图/消费/进程，并验证同一安全凭据、所有工作者退出及无在途启动后，才能归档旧动作并重新准备安全动作。启动准备与安全核销共用 Supervisor 内有界串行入口。历史严格意图若缺失核心绑定或预留，仍不得推定未启动。
