# WJ-EPB V4 故障恢复与 FallbackGuard 修复

## 现场事件

现场会话在六个运行通道（4、5、7、8、9、12）进入“系统故障”后未续测。检查时主程序 PID 17036 存活，但恢复账本为 `RecoveryBlocked=True`、`RelaunchState=Blocked`，因此进程存活不代表恢复成功。

关键证据已按有界方式封存于 `Codex/i0018-v401/`：恢复状态、Watchdog 日志尾部、进程拓扑及 `index.db` 身份/进度摘要。未回传全量现场数据；没有检查 CAN。

## 已证实链路

1. 原主进程已完成安全关闭，Watchdog 已成功启动恢复进程。
2. 恢复进程随后记录 `ApplicationExitIntentIdentityMismatch`、`RecoveryReconnectPermitIdentityMismatch` 和 `RecoveryBatchCommitDeferred: ContextMissing`。
3. 系统将该恢复进程判为未提交恢复批次并进入 `RecoveryBlocked`；实际进程仍在，输出安全关闭、试验未推进。
4. FallbackGuard 读到 `DatabaseStalled:4,5,7,8,9,12`，但其数据库意图仍绑定旧 PID/RunId/RunEpoch。主 Watchdog 又禁止被阻塞恢复状态重绑定，因此守护程序只能报告而不能完成接管。

初始保护触发处存在 `ControlProcessingStale` 和 Dev2 持久化恢复记录，但现有关键证据不能将首次采集回调延迟唯一归因给持久化。

## 本次现场恢复

在取证完成后，按 PID 和启动时间核验关闭卡死恢复进程，启动正常主程序并经实际界面点击开始。数据库随后显示六通道完成计数继续增加，未重置计数。该动作只恢复现场进度，并不构成对恢复缺陷已修复的验收。

## 代码修订方向

本次先修复账本重绑定的阻断：当新的正式运行已由当前 Watchdog 心跳认证，且没有人工停止或会话撤销时，允许 `FallbackLedger` 从旧 RunId/RunEpoch 绑定到恢复后的活动批次；不再因 `RecoveryBlocked` 永久保留旧身份。

后续验收必须覆盖：恢复进程重连、许可附着、恢复批次提交、FallbackGuard 在数据库三次确认停滞后的安全接管、重启后数据库持续推进。任何接管仍必须经过既有安全停机、压力/电流确认、PID/启动时间核验和恢复许可，不允许守护程序直接驱动硬件或绕过联锁。
