# EPB 存储恢复与独立进程看门狗实施方案

## 结论

- 当前存储问题必须先从主进程内部修复：统一冻结边界、消除动态 Finalizer 边界、完整清理恢复任务、修正 StopAll 持久化闭合判定。
- 无人值守长稳运行需要新增独立进程看门狗，但它只负责进程监督、授权和受控拉起，不能直接操作 DAQ、DO、电源、液压或 SQLite。
- 软件看门狗不能保证主进程完全卡死时的物理断能；若要求此时仍满足确定的断能时间，必须另配默认断开的硬件安全继电器。
- 全量更新整个解决方案，不绑定或写死任何产品版本号，不保留旧版/新版双行为分支。发布门禁只检查同一发布包的文件、配置和清单哈希一致性，版本号由用户统一维护。

## 存储与恢复 P0 修改

1. 建立唯一冻结边界：
   - 新增不可变 `DaqCutoffSnapshot`，包含 Device、RunId、RunEpoch、RecoveryEpoch、CorrelationId、CutoffUtc、FrozenBoundary、Generation、事故圈身份及连续性空洞。
   - DAQ 故障时只生成一次 Snapshot；`SuppressAfter` 与该 Snapshot 同次提交。
   - 强制不变量：
     - `Persisted >= FrozenBoundary`
     - `FirstSuppressedSequence > FrozenBoundary`
     - `CycleFinalizeBoundary <= FrozenBoundary`
     - 所有 `Sequence <= FrozenBoundary` 的已接纳批次必须真实写入，不能被 suppression 排除。
2. 统一所有圈收尾：
   - 移除恢复期间通用 Finalizer 对 `GetLastDiskPublishedSequence` 的动态读取。
   - 正常圈首次收尾时冻结一次边界；存在 DAQ 恢复上下文时必须复用 `DaqCutoffSnapshot` 和同一个 Finalization Task。
   - 若调用方提出的边界高于 FrozenBoundary，立即产生 `RecoveryBoundaryContradiction`，事故圈只提交一次 `AbortedBySoftwareRecovery`，不计成功、不进入第三次重试。
   - 保持现有 CSV、BIN、SQLite/index 格式不变，不增加业务数据迁移。
3. 修正 StopAll 闭合逻辑：
   - 顺序固定为：撤销 RunEpoch → 非阻塞提交全部 OFF/电源 Disable → 取消全部恢复 Scope → 有界等待 → 停止 DAQ → 排空冻结前缀 → 提交事故圈终态 → 获取逻辑清场快照。
   - 新增统一 `RecoveryTaskRegistry`，覆盖 DAQ、液压、电源、Timer、隔离恢复、活动圈上限、报警收尾、持久化重试和单通道重启任务。
   - 所有任务使用 RunEpoch 校验和 exact-remove；超时任务不得伪装成已清理，也不得修改新 Run。
   - StopAll 成功条件为：
     - `DaqRecovery=0`
     - `SoftwareRecovery=0`
     - `RecoveryOwners=0`
     - 无 Timer、Runner、Lease、开放圈和旧 Run 任务。
   - 持久化闭合改用实际证据，不再要求状态枚举必须为 `Recovered`。若 FrozenPrefix 已落盘、suppression 尾段有明确终态、无 DurabilityBlocked、Head/InFlight 已越过边界，即使状态仍为 `Paused` 也允许闭合。
4. 双 DAQ 恢复：
   - Dev1、Dev2 各自保存冻结边界，但共享一个 BatchContext、CorrelationId、总期限、失败计数和恢复所有权。
   - 两台设备均完成前缀证明和新鲜度验证后才能共同重入，禁止单边提前恢复。
   - 前缀已闭合且无边界内 Head/InFlight 时，不得继续停留在 `RecoveryOwnership`。
5. 存储保护与归档：
   - 正式运行期间维护 `RUNNING.lock` 和只读 IPC 状态，记录 PID、StartTicks、RunId、数据卷 ID。
   - 提供受支持的归档入口：运行中源、目标位于同一物理卷时直接拒绝。
   - 在线归档只允许 VSS/快照后复制到不同物理盘或网络存储，并限制带宽/IOPS。
   - 记录 `ArchiveCorrelationId`、工具/PID、源目标、卷 ID、开始结束时间和限速参数。
   - 任意资源管理器手工复制无法由应用彻底禁止，作为现场操作红线保留。

## 独立进程看门狗

1. 部署形式：
   - 新增 `MTTFTest.Watchdog.exe`，采用 .NET Framework 4.8 AnyCPU，不引用 NI、Controller 或 DataOperation。
   - 正式桌面快捷方式启动 Watchdog，由 Watchdog 拉起 x86 主程序；首版不采用 Windows Service，避免 Session 0 启动 WinForms 的权限和交互问题。
   - 直接启动主程序只允许人工/开发模式，禁用无人值守进程重启。
   - 启用 Watchdog 后，删除主进程自行 `Process.Start` 的无人值守分支，避免两套进程所有者。
2. IPC 与授权：
   - 使用仅本机访问的 Named Pipe，ACL 限制到当前用户或安装服务 SID。
   - 增加 `IProcessSupervisor`、`WatchdogHeartbeat`、`WatchdogLease`、`RestartTicket`。
   - 每条消息校验 ProtocolVersion、SessionId、PID、ProcessStartTicks、ExecutableSha256、ConfigSha256、RunId、RunEpoch、AuthorizationEpoch 和 nonce。
   - Checkpoint 写入改用跨进程 Named Mutex 加版本 CAS；旧的恢复票据首次启动时失效，但不删除既有试验数据。
3. 心跳与状态机：
   - 心跳周期 1 秒，Watchdog 每 250 ms 检查；3 秒进入 `Suspect`，5 秒进入 `Unresponsive`。
   - 心跳包含运行阶段、StopSource、带电通道、恢复阶段及阶段年龄、双 DAQ callback gap、持久化 Frozen/Persisted/Head/InFlight/Depth，以及 StopAll 七项结果。
   - 状态机：
     `Stopped → Starting → Attached → Running → Suspect → SafetyStopRequested → RestartCandidate → Launching → ChildAttached → Running`
   - 人工停止、正常关闭进入 `StoppingExpected`，撤销授权且不自动拉起。
   - 包身份不一致、授权无效、预算耗尽或安全证据不足进入 `TerminalSafeStop`。
4. 重启边界：
   - Watchdog 只发送一次幂等 `SafetyStopRequest`，由主进程唯一执行 StopAll。
   - 只有 Motor、Power、Pressure、Raw、Persistence、Continuity、Logical 全部确认，且恢复任务/所有权为零，才允许拉起新进程。
   - 沿用 10 分钟最多 3 次预算；父进程退出期限 15 秒，子进程 Attach 期限 5 秒。
   - 主进程在运行带电状态完全无响应时，不根据旧心跳推定安全，不盲目重启；没有独立硬件断能证据时进入 `TerminalSafeStop`。
   - Watchdog 永远不创建 RootRunId、不直接 Arm、不打开 NI 任务、不写试验数据库。

## 发布身份与状态语义

- 正式无人值守启动必须同时满足：Watchdog 已连接、发布清单完整、主程序/Watchdog/依赖/配置哈希一致。
- 不设置固定版本号白名单；ProductVersion 仅需与本次发布清单自洽。
- `PackageIdentityMismatch` 在正式启动前拒绝，而不是故障发生后才发现。
- 将 Phase、Severity、StopSource 分开；只有 `StopSource=ManualUi` 显示人工停止，系统故障和 Watchdog 停机显示系统安全停止。

## luna_worker 执行任务划分

- 存储核心任务：负责冻结 Snapshot、Finalizer 统一边界、suppression 断言、StopAll 持久化闭合；所有权限定在 Controller 持久化与恢复模块。
- 生命周期任务：负责 `RecoveryTaskRegistry`、双 DAQ BatchContext、RunEpoch 隔离和状态语义；不得修改 Watchdog 项目。
- 看门狗与验证任务：负责 Watchdog 项目、Named Pipe、跨进程票据、发布脚本和进程集成测试；不得直接操作 NI/SQLite。
- 各任务均基于同一全量代码更新，不建立版本条件分支，不回退其他任务修改；由 Sol 主代理完成接口审查、合并和最终验收。

## 测试与验收

当前基线已验证：

- AdaptiveControlTests：383/383 通过。
- EpbDiskWriterTests：35/35 通过。

新增门禁：

- 冻结 100、Published 130 时，Finalizer 只能等待 100；首个 suppression 必须为 101 或更大。
- 边界矛盾只产生一个 `RecoveryBoundaryContradiction` 和一个事故圈终态。
- 写盘阻塞、容量满、generation 切换后，所有已接纳序号 exactly-once 落盘。
- StopAll 首次超时不得假闭合；阻塞解除后可重试并最终全部计数归零。
- 双 DAQ 同时、错开 0.5～1 秒故障均不得单边重入或产生恢复风暴。
- 同盘运行中归档请求必须拒绝；快照到异盘归档可运行并保持数据库完整。
- Watchdog 覆盖进程退出、心跳丢失、旧 PID/RunEpoch 迟到、重复拉起、撤权竞态、包身份不匹配和重启预算耗尽。
- 每次事故后执行 SQLite `integrity_check`，无 `running/open` 遗留，事故圈不计正式成功。
- 完成 2 h、8 h、24 h、72 h 分阶段长稳；至少一次快照异盘归档。任一永久数据空洞、恢复任务残留、PackageIdentityMismatch、人工拼接 Run 或安全未确认均阻止晋级。

## 假设

- 用户负责统一更新版本号；实现不包含任何固定版本限制。
- 整个解决方案和发布目录一次性全量更新，不支持混用旧 DLL、旧 Watchdog 或旧恢复票据。
- 软件 Watchdog 解决进程存活和受控交接；需要主进程完全失效后的确定物理断能时，硬件安全继电器作为独立项目实施。