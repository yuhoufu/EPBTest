# RecoveryGuard：完全卡死主程序兜底

## 授权与边界

用户明确允许在主程序完全卡死、无法协作断能时使用：

确认停滞 → 强制结束精确旧进程 → 独立断能、卸压 → 动作后的新鲜电流和压力确认安全 → 匹配入口续测 → 真实业务提交确认恢复。

这是实现授权，不是 wj-epb 部署授权。正常学习、冷却、保压和其他有效期限内的阶段等待不应触发强制结束；人工停止、暂停、维护限制不被自动恢复覆盖。

## 本次实现

- 仅 RecoverStalled 的有效 SafeStop 事务允许进入兜底；先给主程序有界协作时间。
- 必须已建立可信观测、满足连续疑似次数，且所有参与通道的采样、控制、持久化进展都停滞；单通道卡顿或单纯快照停止更新不够。
- 复核安装、授权版本、事务 epoch、所有者、主程序身份、会话、租约和阶段期限。快照发布会被接管 epoch 隔离，因此不能把接管后没有新快照直接当作主程序完全卡死的证据。
- 在 action-history 写入绑定事务、会话及精确旧进程的终止意图记录；记录只供审计，不是可重放的终止许可。写入失败不终止。
- 通过同一个 Windows 进程句柄核对启动时间与可执行路径并终止，避免 PID 重用误杀。终止前再次检查取消、维护、试运行授权和最新控制状态。
- 终止后返回 Pending，保持 SafeStop。下一步仍须证明旧实例退出，才能走现有独立 SafetyAgent；不修改独立安全执行和后续启动的证据门禁。

不能确认独立输出成功、反馈新鲜且安全、或续测数据边界时，不允许直接续测。未增加硬件安全阈值，也未以发送成功替代物理安全。

## 已执行验证

- Debug/x86 全解决方案编译通过：`artifacts/guard-physical-safety/build-main-retirement-fences.log`。
- `RecoveryGuardTests.exe --exact-main-retirement`：4/4 通过。
- 定向测试含：全通道停滞及合法等待保护、终止前停止/维护/取消撤销、审计先于终止、终止不越过 SafeStop、退出后仅进入独立安全准备。
- 精确终止测试只使用复制到隔离临时目录的测试子进程，不涉及现场主程序或硬件。

- 完整 Guard 回归：101/101 通过，含 300 次故障重试（3589 个模拟步骤）；日志 `artifacts/guard-physical-safety/test-main-retirement-guard-full.log`。
- 磁盘回归：76/76 通过；电源调试器测试：19/19 通过。日志分别为 `test-main-retirement-disk.log`、`test-main-retirement-power.log`（同上目录）。

- 完整控制回归：899/899 通过，退出码 0；日志 `artifacts/guard-physical-safety/test-main-retirement-control-full.log`。
- 本次变更文件 `git diff --check` 通过。

本次兜底改动已完成本地实现和上述软件回归。真实 NI、电源、液压输出及现场连续续测尚未验证；尚未制作包含本次改动的正式部署包，当前不构成整版可部署验收结论。未部署 wj-epb，需用户另外确认部署测试。

## 后续边界补强：会话与检查期间取消

在上述回归之后继续审查发现：独立安全链需要已注册监督会话，终止门不应允许缺失/无效会话；终止前读取状态及 commissioning 检查期间也可能收到取消。因此新增非空 GUID 会话门禁（策略及审计写入 API 双重检查），并在最终检查结束后重新检查取消。会话格式有效仅是必要条件，不代表 Supervisor 当前注册及硬件能力已验证；既有后续校验仍保留。

`build-main-retirement-session.log` 全解决方案 Debug/x86 编译成功，`test-main-retirement-session.log` 定向 4/4 通过，新增缺失/无效/空 GUID 会话与检查期间取消反例。测试组数量未增加，但组内反例已增加。

当前二进制的串行 Guard、控制、磁盘、电源回归现已全部结束，整体退出码 0：Guard 101/101、控制 899/899、磁盘 76/76、电源 19/19。对应日志前缀为 `artifacts/guard-physical-safety/test-main-retirement-session-`；这些是本次会话与取消补强后的实际结果，而非沿用前一批计数。

另新增 `Tools/Test-IndependentGuardRetirementOnJxcq.ps1`，限定 JXCQ 的新建隔离目录，只传输并校验三个独立 Guard 测试文件，运行 `--exact-main-retirement`，不安装任何组件或进行硬件动作。原验证调用现已正常结束，退出码 0、4/4 通过。目录为 `D:\EPB_Validation\independent-guard-retirement-00c8d1d33a994470841677edc7276dec`，三个文件共 358400 字节，远端哈希通过且运行后本地源哈希未改变。

结果日志为 `artifacts/guard-physical-safety/test-independent-guard-retirement-jxcq-result.log`，完整测试日志已回传到同目录 `independent-guard-retirement-00c8d1d33a994470841677edc7276dec.log`。报告明确 `InstallationPerformed=false`、`PhysicalHardwareVerified=false`、`DeployablePackage=false`。只证明 JXCQ 隔离软件路径，不代表完整包安装、真实断能卸压或现场续测通过。

本次调用回传较慢，但未记录分阶段耗时，不能从总等待推定网络或测试为根因。之后给执行脚本补上连接建目录、传输、远端校验测试、日志回传四段计时字段；已通过语法解析，尚未再次远端执行，不为本次结果补造计时数字。
