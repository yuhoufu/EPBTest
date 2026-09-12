# RecoveryGuard 300次重复失败验证

本轮补充实施方案第11节“长期重试”和“成功判定”的软件证据，不修改生产代码或已交付安装包，不操作wj-epb。

## 场景

Tests/RecoveryGuardTests/Program.cs新增RepeatedFailuresPreserveBusinessOrigin。使用实际RecoveryExecutionEngine和RecoveryControlStore，以及模拟时钟、失败动作替身。保留默认600秒冷却；每60秒刷新仅发布时间/序号变化的快照，重新创建存储读取器和执行器，按生产状态机实际调度动作。

每一步核对最后已验证业务提交时间及各通道采样、控制、持久化进展时间均保持原值；持续监督不使授权过期，失败不能变成Complete。达到300次实际动作调用后设置人工停止，验证执行者结束且动作数不再增加。

读取器/执行器重建不等于真实进程退出或系统重启；动作替身也不代表实际设备断能。场景不能替代所有事务阶段的进程故障注入、真实安装级重启限频或现场硬件验收。

## 当前结果

JXCQ第二轮通过Guard全套92/92，退出码0。新增场景记录300次失败、3589个分钟推进步骤、模拟跨度215520秒（59小时52分），不是实际运行了近60小时。证据为artifacts/recoveryguard-repeat-jxcq.json，远程目录为C:\RecoveryGuard-3.0-Validation\RepeatFailures-20260908-r2；测试EXE SHA256为95178A8A63F30604DBFB7DE819F0C774EF881970A040A92AE47096085ADF26ED。

首轮测试上限固定2000步，不足以在默认600秒冷却下产生300次动作，故达到测试上限后失败，不计通过。第二轮按默认冷却推导4500步上限，未缩短生产冷却来让用例通过。初轮记录保留在recoveryguard-repeat-initial-bound.log及recoveryguard-repeat-jxcq-initial-bound.json。

本地第二轮同样通过92/92，进程退出码0，记录300次失败、3589步及215520秒模拟跨度，证据为artifacts/recoveryguard-repeat-tests.log。已交付ZIP不因新增测试而改写；自动恢复包生成流程及现场验收仍未完成。
