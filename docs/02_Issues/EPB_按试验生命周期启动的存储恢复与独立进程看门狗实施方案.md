# EPB 按试验生命周期启动的存储恢复与独立进程看门狗实施方案

## 1. 文档定位

本文是后续实现和现场验收依据。原《EPB 存储恢复与独立进程看门狗实施方案》保留为架构分析记录；本方案采用现场确认的运行方式：用户仍直接启动 `MTTFTest.exe`，只在点击“开始试验”后由主程序自动、隐藏启动 `MTTFTest.Watchdog.exe`，人工停止、正常完成或关闭软件后 Watchdog 退出。

本次更新不包含固定产品版本判断，不以 EXE 哈希、GitCommit、BuildUtc 或发布状态作为恢复硬门禁。用户负责全量替换发布目录，程序始终恢复本轮开始时记录的同一绝对 EXE 路径。

## 2. 已落地的总体链路

```text
用户启动 MTTFTest.exe（无 Watchdog）
  → 点击“开始试验”
  → 主程序生成 SessionId，隐藏启动 Watchdog 并通过 Named Pipe 握手
  → 学习、正式试验；主程序每秒发送心跳
  → 进程内恢复优先处理 DAQ、存储、电源、液压和 Timer 故障
  → 进程内恢复明确升级 / 恢复60秒无进展 / 主进程5秒失活
  → Watchdog 请求主程序整批 StopAll
  → 15秒仍未退出则终止旧 PID
  → 按 5/15/30/60秒无限退避启动同一路径主程序
  → 新进程先执行 SafetyTakeover，再作废事故圈、计算剩余圈
  → 排除永久报警、人工禁用/停止和已完成通道
  → 剩余健康通道完整学习后续测
```

Watchdog 是 Named Pipe 服务端，主程序是客户端。恢复子进程重新连接原 Session，因此 Watchdog 在旧进程退出和新进程附着之间仍保持唯一的生命周期所有权。每个 Session 使用命名互斥量防止重复 Watchdog。

## 3. 启动、停止与退出

### 3.1 开始试验

人工开始入口在旧批次安全清场、报警提示复位后执行：

1. 收集当前选中通道；
2. 生成新的 `WatchdogSessionId` 和 PipeName；
3. 启动隐藏进程：

   ```text
   MTTFTest.Watchdog.exe
   --parent-pid <PID>
   --parent-start-ticks <StartTicks>
   --session <SessionId>
   --pipe <PipeName>
   --executable <MTTFTest绝对路径>
   ```

4. 最多等待 5 秒完成 `Attach/Attached`；失败后再重试一次；
5. 两次都失败时显示“本轮仅使用进程内恢复”，但不阻止人工试验；
6. 握手成功后把 SessionId 绑定到主程序检查点，再启动学习和正式试验。

打开软件但没有开始试验时，不存在后台 Watchdog。再次开始会建立全新 Session，旧 Session 消息不能控制新 Run。

### 3.2 人工停止

人工停止先发送 `ManualStopRequested`，Watchdog 立即把本轮标记为不可自动拉起；主程序随后执行现有 StopAll。StopAll 返回后发送 `RunStopped`，不论 Persistence/Logical 是否完全确认，Watchdog 都退出，主程序允许用户关闭或再次完整学习。

人工停止与自动恢复发生竞态时，Watchdog Journal 的 `ManualStopRequested=true` 优先，任何已排队的接管、Kill 或 Process.Start 均不得继续。

### 3.3 正常完成和关闭

- 所有检查点通道达到 `Completed`：发送 `RunCompleted`，Watchdog 退出。
- 窗口正常关闭：发送 `ApplicationClosing`，撤销恢复资格并退出 Watchdog。
- Watchdog 接管要求旧主进程主动退出时使用专门的接管标记，不把它误当人工关闭而撤权。

## 4. Watchdog 的权限边界

Watchdog 只监视 PID、启动时间、心跳和恢复阶段；发送一次幂等 StopAll 请求；在旧进程无响应时终止该 PID；按原绝对路径拉起主程序；保存 Session Journal 和恢复次数。

Watchdog 不引用 `Controller`、`IO.NI` 或 `DataOperation`，不打开 NI 任务，不操作 DO/AO、电源、液压，不写 SQLite/CSV/BIN，不修改正式圈数，也不根据 UI 文本推断安全。所有硬件动作仍由唯一的主程序控制站完成。

## 5. 接管判定

只使用三类信号：

1. 主程序发布 `ExternalRecoveryRequired`。现有进程内无人值守恢复到达进程重启边界时，若独立 Watchdog 已附着，不再由主进程自行 `Process.Start`，而是交给 Watchdog。
2. 心跳仍在，但 `RecoveryActive=true`、存在健康候选通道、进度版本连续 60 秒不变，且不是人工优雅暂停。Watchdog发布一次 `ExternalRecoveryStageStalled`。
3. 心跳每秒一次；3 秒未收到时发送 Ping，5 秒仍无心跳或 PID 已退出时进入 `Unresponsive`。

不使用“圈数多久没增长”作为判据，以免把长周期、等待液压或正常暂停误判为进程失活。

## 6. 整批恢复和 SafetyTakeover

主程序仍可响应时，Watchdog 发送 `RequestStopAll`；主程序执行 `PrepareForFreshRestartAsync`，发送 `StopCompleted` 后主动退出。主程序完全无响应时，Watchdog等待 15 秒后核对 PID+StartTicks，终止旧进程并进入重启循环。

恢复启动参数：

```text
MTTFTest.exe
--watchdog-recover <SessionId>
--watchdog-pipe <PipeName>
--previous-pid <PID>
--recovery-attempt <AttemptId>
--exclude-channels <持久禁用、人工禁用/停止、已完成通道>
```

新进程先验证主程序检查点仍为 Armed 且 `WatchdogSessionId` 一致；该路径不检查版本、哈希、构建时间和既有三次重启预算。随后打开实时监视控制站并按顺序执行：

1. `DoController.AllOff()`，失败立即禁止学习；
2. `AOController.ResetAll()`；
3. `EpbManager.StopAllAsync(SystemFault/WatchdogSafetyTakeover)`，由原控制层关闭电源、压力输出、DAQ并核对物理证据；
4. 只有 MotorOff、PowerOff、PressureSafe 三项确认才继续；日志、持久化或 Logical 观察项不单独阻止安全接管；
5. 显式调用 `AbortInterruptedCyclesForSoftwareRecovery`，把旧 PID 的全部 `running` 圈一次性更新为 `AbortedBySoftwareRecovery`；重复调用影响零行；
6. 从 SQLite 已提交成功正式圈重新计算剩余次数；
7. 排除 Watchdog 最后心跳中的持久禁用、人工禁用/停止和已完成通道；普通 `AlarmStopped/InterlockStopped` 不能仅凭旧运行态永久排除，仍由新进程预检判断是否已经持久禁用；
8. 对剩余健康通道执行不少于五圈完整学习，然后进入正式续测。

恢复进程连接失败或 SafetyTakeover 失败时主动退出，由 Watchdog采用第1次5秒、第2次15秒、第3次30秒、之后60秒的非终止退避继续尝试，直到人工停止本轮 Session。

## 7. 存储恢复整改

### 7.1 唯一冻结边界

DAQ 恢复生成不可变 `DaqCutoffSnapshot(Device, CutoffUtc, FrozenBoundary, Cycles)`。快照与 `SuppressAfter(FrozenBoundary)` 在同一截止动作中安装，之后的恢复、自维护和 Finalizer 只能读取该快照，不得扩大边界。

通用圈 Finalizer 也先冻结本次需要证明的 Published 边界，再执行定界 Raw 排空；若相关圈已进入 DAQ 恢复，必须复用恢复快照的 FrozenBoundary，禁止在排空之后动态读取一个更高 PublishedSequence。

### 7.2 矛盾终态

以下任一条件产生结构化 `RecoveryBoundaryContradiction`：

- FrozenSnapshot 缺失；
- Context 的旧边界与不可变快照不同；
- `SuppressAfterSequence != FrozenBoundary`；
- `FirstSuppressedSequence <= FrozenBoundary`。

矛盾不再进入普通持久化自维护重试，而是立即升级外部整批恢复。事故圈由原 Finalizer/启动接管的一次性终态规则作废，不计正式成功。

### 7.3 StopAll 恢复任务清场

新增 `RecoveryTaskRegistry`，自动纳入操作名包含 Recovery、SelfHealing、Finalize、Restart 或 FaultHandling 的受监督任务。StopAll 先递增 RunEpoch 撤权、完成全组 OFF 和电源 Disable，再有界等待 DAQ 恢复所有权与统一恢复任务表。超时任务记录到停止证据，旧 RunEpoch 的提交继续被拒绝。

StopAll 状态不再无条件发布 `ManualStopped`：人工/关闭仍显示人工停止，报警联锁显示 `InterlockStopped`，系统及 Watchdog 接管显示 `SystemFault`。

SQLite、CSV、BIN 和 index 结构均未改变，不需要数据迁移。

## 8. IPC 与 Journal

共享轻量工程 `Watchdog.Protocol` 只依赖 .NET Framework 基础库，消息为一行一个 JSON，带 `ProtocolVersion`、SessionId 和 CorrelationId。主要消息为 Attach、Heartbeat、Ping/Pong、ExternalRecoveryRequired、RequestStopAll、StopCompleted、ManualStopRequested、RunStopped、RunCompleted 和 ApplicationClosing。

心跳包含 PID/StartTicks、RunId/RunEpoch、阶段、通道集合、恢复阶段和进度版本、双 DAQ generation/callback-gap、Frozen/Persisted/Head/InFlight/Depth、恢复任务和所有权数、停止证据摘要以及 RunActive。

主程序继续独占 `unattended-run-checkpoint.json`；Watchdog 独占：

```text
%LOCALAPPDATA%\MTTFTest\Watchdog\session-<SessionId>.json
```

Journal 使用临时文件加原子替换，记录同一路径 EXE、当前 PID+StartTicks、最后心跳、人工撤权、恢复次数、状态和最后原因。两个进程不并发写同一文件。

## 9. 关键实现文件

- `Watchdog.Protocol/WatchdogProtocol.cs`：IPC DTO 与消息类型。
- `MTTFTest.Watchdog/WatchdogHost.cs`：Named Pipe 服务端、判活、StopAll 请求、Kill 和无限退避拉起。
- `MTTfTest/WatchdogRuntime.cs`：主程序客户端、隐藏启动、心跳和命令接收。
- `MTTfTest/FrmEpbMainMonitor.BatchGuard.cs`：点击开始时创建 Session。
- `MTTfTest/FrmEpbMainMonitor.UnattendedRecovery.cs`：心跳快照、受控接管和 SafetyTakeover。
- `MTTfTest/UnattendedRecovery.cs`：Session 与检查点绑定、进程内恢复升级交给 Watchdog。
- `Controller/EpbManager.cs`：不可变冻结边界、矛盾终态、StopAll 状态语义。
- `Controller/RecoveryTaskRegistry.cs`：恢复任务统一收口。
- `DataOperation/EpbDiskWriter.cs`：Watchdog事故圈一次性作废。

## 10. 构建、发布与现场运行手册

全量发布目录至少必须同时包含：

```text
MTTFTest.exe
MTTFTest.Watchdog.exe
MTTFTest.Watchdog.Protocol.dll
其余现有 DLL 与 Config 目录
```

用户不需要手工启动 Watchdog。现场只按原流程启动主程序、打开实时监视、点击开始。开始日志必须出现“本轮独立进程看门狗已启动并完成握手”；若出现“仅使用进程内恢复”，应记录为本轮降级，但按用户要求不阻止人工试验。

Watchdog 日志/Journal用于核对 PID、恢复次数和最后心跳。演练完全卡死时，应观察旧 PID 在15秒后退出、新进程首先记录 `Watchdog SafetyTakeover 已确认`，事故圈为 `AbortedBySoftwareRecovery`，健康通道重新学习，报警/禁用/完成通道保持停止。

## 11. 已完成自动验证与待现场验收

本次实现验证：

- `MTTfTest`、`Controller`、Watchdog 和协议工程 C# 编译通过；
- AdaptiveControlTests：新增 FrozenBoundary 原子安装用例后 384/384 通过；
- EpbDiskWriterTests：新增 Watchdog事故圈幂等作废后 36/36 通过；
- 普通短期重启保留 running 圈的旧兼容测试继续通过。

仍必须在真实设备执行：

1. 点击开始/停止/再次开始的 Session 生命周期；
2. 主程序有响应的 ExternalRecoveryRequired 整批交接；
3. 冻结 UI/线程或挂起进程，验证3秒 Ping、5秒失活、15秒终止；
4. 验证 DO、电源、AO、压力、DAQ 的 SafetyTakeover 顺序；
5. 永久报警、人工禁用、已完成与健康通道混合恢复；
6. SQLite `integrity_check=ok`、无 running/open、无重复正式成功圈；
7. 依次完成 2h、8h、24h、72h 长稳。

最终验收标准是：不需要用户人工关闭和重开软件；发生进程内无法收口或主进程完全失活后，剩余健康通道能自动重新学习并按 SQLite 耐久进度续测。

## 12. 安全假设

用户已选择强制接管并接受软件无法独立证明的短暂断能空窗。Watchdog 发出 StopAll 后若旧进程完全卡死，只能终止旧进程并由新进程重新取得 NI 所有权后执行全断能；若未来需要在该空窗内也保证确定断能时间，应增加默认断开的外部硬件安全继电器，但不改变本方案的进程监督主流程。
