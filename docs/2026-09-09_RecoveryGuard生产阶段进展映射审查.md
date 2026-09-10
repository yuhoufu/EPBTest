# RecoveryGuard 生产阶段进展映射审查

状态：存在未闭合项，非验收通过。2026-09-09 当前开发工作树静态审查。

## 已核实的发布链

`FrmEpbMainMonitor.UnattendedRecovery.cs` 从稳定 aggregate、通道逻辑进展、DAQ 已处理序号与磁盘正式提交计数构造 `RecoveryChannelProgress`，再交给 `RecoveryGuardRuntime.Offer`。不是仅靠 UI 心跳推进业务时间。

当前通道映射：

| 字段 | 生产来源 |
| --- | --- |
| SampleGeneration / SampleSequence | 对应 DAQ 设备 Generation / LastProcessedSequence |
| ControlSequence | MechanicalCompletedCount |
| PersistedSequence | CaptureCommittedCycleProgress().FormalCommits |
| Stage | ChannelRuntimeState 字符串 |
| StageStartedUtcTicks | max(StateSinceUtcTicks, LastMechanicalCompletedUtcTicks) |
| StageDeadlineUtcTicks | Learning/Running/WarningRunning/WaitingForSlotBarrier 的起点 + PeriodMs + 10 秒；其他状态为 0 |

重复发布不使用当前时间移动上述截止，这是应保留的约束。但正常学习、冷却、保压的实际最长合法时长是否都落入该边界，尚无逐项证据。

## 已确认的缺口

Controller 的 `InfrastructureRecoverySource` 已包含 ActiveRecovery、RunId/RunEpoch、CorrelationId、Stage、StageStartedUtcTicks、HardDeadlineUtcTicks、RecoveryHardDeadlineUtcTicks 及恢复参与通道。当前外部通道映射未使用这些恢复阶段字段。

`RecoveryDecisionPolicy.EffectiveProgressAnchor` 只对有固定未过期截止的阶段允许仅凭新采样继续等待；无截止时需要控制或持久化进展配合。因此内部恢复阶段持续推进、但尚未恢复机械周期/正式提交时，现有发布可能不能表达这一合法进展。这是静态契约缺口，不等于已在现场复现误接管。

## 下一步实现约束

后续实现：已在生产快照入口接入 `RecoveryGuardRuntime.TryProjectRecoveryStage`。只对当前明确所有者、同 Run/epoch、相同 CorrelationId、同时属于预计及正在恢复范围且非 orphan 的 Recovering 通道投影。阶段身份包含事务和阶段，起点沿用 Controller，截止取两个固定截止中更早者；仅 ProgressVersion 增长不移动阶段或截止。无有效证据时保持原映射，不放大等待时间。

这仅接入已有可信恢复字段，尚不证明各 Controller 生产者都完整发布这些字段。学习/冷却/保压仍需逐项审查；也未改变安全恢复的断能、电流、压力和数据门禁。

验证：`build-recovery-stage-projection.log` Debug/x86 全解决方案构建退出码 0；`test-recovery-stage-projection.log` 36/36（新增九个映射场景）、`test-recovery-stage-runtime.log` 39/39，均退出码 0。此前双设备停止版完整控制 869/869、独立 Guard 98/98、磁盘 76/76、电源 19/19，退出码均为 0；这些完整结果不覆盖本次新增映射。日志位于 `artifacts/guard-physical-safety/`。

组合验证已补充：将生产投影方法返回的阶段、起点和截止传入独立临时 `RecoveryControlStore`，执行真实 Observe 策略（不启动硬件或接管动作）。仅采样推进且截止未到时保持 Healthy；仅发布序号推进而采样停滞时允许判定停滞；采样仍推进但固定截止已过且没有控制/提交进展时也允许判定停滞。三种序列使用压缩的合法测试时间配置，不修改生产配置，不使用实际等待延长测试。`build-projection-policy.log` 构建退出码 0，`test-projection-policy.log` 36/36、退出码 0；三种组合序列包含在有效映射用例内部，未虚增顶层用例数。

1. 只接受匹配当前 Run/epoch、明确恢复所有者和参与通道的阶段证据；不能把任意全局恢复广播为所有通道健康。
2. 恢复截止取已有固定截止，不使用发布时刻重算，不用 heartbeat 或仅 ProgressVersion 增长延长等待。
3. 同阶段身份保持固定，真实阶段切换才能更新阶段起点；跨事务证据不能拼接。
4. 新采样缺失、阶段超期、所有者失效时仍进入停滞判定；恢复进展不是物理安全放行证据。
5. 分别验证学习、冷却、保压、共享槽等待和内部恢复；不直接放大 BusinessStallSeconds 掩盖发布缺口。

需要生产映射定向测试和 Guard 策略组合测试，之后再做隔离端到端及现场验证。现有窗口、压力等待策略测试不能替代这些证据。wj-epb 未改动，部署仍待用户确认。
