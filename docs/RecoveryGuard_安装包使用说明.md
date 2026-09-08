# RecoveryGuard 独立安装包使用说明

本说明随包以 README.md 交付。以 guard-identity.json 中的版本、configuration 和 deliveryStage 判定本包身份；文件版本为 3.0.0.0 不等于已通过现场自动恢复验收。

ObserveOnlyCommissioning包只观察，执行任务禁用，automaticExecutionReady=false。schema 3的AutomaticRecovery包须携带匹配的现场验收报告与证据，安装时还要核对台架、机器、主程序清单及获准模式；默认配置仍只观察。不得仅编辑清单或配置启用自动执行。主程序、Supervisor、SessionAgent和安全组件需要另外交付匹配版本，本包不能代替主程序整套安装包。当前已交付的现场观察ZIP不因后续代码增加自动发布入口而改变阶段。

## 包内容与校验

schema 2 清单覆盖独立 EXE、RecoveryControl DLL、两份 PDB、配置、安装脚本和本说明。安装器逐项校验长度、SHA256、组件版本和参数关系；缺失或损坏时中止。PDB 用于后续故障定位，保留与本包二进制一起存档。校验不能代替可信分发来源，也不证明设备安全状态。

新清单的 sourceSnapshot 记录实际 Compile 文件、版本源、配置、打包/安装脚本及本说明的逐文件 SHA256。builtFromVerifiedInputs=true 表示打包过程执行了 Rebuild，且构建前后输入指纹一致；为 false 时是跳过构建的包装验证，不能据此证明源码对应二进制。该字段不代表现场验收通过，也不替代源代码归档。

在解压目录使用 Windows PowerShell 5.1 或兼容版本：

```powershell
.\Install-MTTFTest-RecoveryGuard.ps1 -Mode Validate -SourceDirectory $PWD.Path
```

Validate 不要求管理员，不注册任务。若本机执行策略阻止脚本加载，应按现场管理规则使用允许执行已核验脚本的 PowerShell 会话；不要改动包内文件以跳过校验。

## 安装与卸载

在维护窗口使用管理员 PowerShell，主程序和旧恢复会话应先收口。以下台架名和主程序路径是占位示例，应替换为现场实际值：

```powershell
.\Install-MTTFTest-RecoveryGuard.ps1 -Mode Install -BenchId '现场台架标识' -MainExecutable 'D:\实际安装目录\MTTFTest.exe'
.\Install-MTTFTest-RecoveryGuard.ps1 -Mode Uninstall
```

默认独立程序目录为 `%ProgramFiles%\MTTFTestRecoveryGuard`，Guard 状态目录为 `%ProgramData%\MTTFTestRecoveryGuard`；共享授权位于 `%ProgramData%\MTTFTest\RecoveryControl`。指定过 InstallRoot 时，后续升级/卸载需使用同一根目录。

安装器核验主程序版本、现有包、安装登记及两个根目录计划任务的实际归属；同名外部任务、未收口接管事务或正在执行的 Guard 都会阻止切换。卸载移除本产品任务，保留共享授权、运行数据、版本目录和诊断记录。

## 失败与回退

任务变更前在 install-transactions 中保存原任务 XML 和安装登记字节。更新或卸载失败时尝试逆序回退；回退报错或回退证据无法持久化时，保留 maintenance-inhibit.json 阻断自动接管。

若安装进程退出或电脑断电后维护文件仍存在，先收集 installation.json、事务证据、两个任务实际 XML、相关日志及原版本目录。核实现场状态后再恢复，不能只按文件年龄删除维护标记；本包没有自动清理未确认安装事务的入口。

## 恢复边界

后续自动恢复版本也必须遵守：人工停止、暂停和完成优先；监督中断达到 60 分钟不恢复旧试验；单通道永久故障按原逻辑隔离；冷却不能替代安全证明。面板按钮只能人工按下，不能作为自动断能能力。没有有效安全证据时保持阻塞，不强杀占用硬件的主程序。I0050 液压恢复状态发布一致性的根因修复仍属于第二步。

故障取证应保留包身份、原始日志和安装事务证据，分享前检查是否含现场路径或敏感信息。2026-09-08 已在 JXCQ 隔离管理员环境完成 ObserveOnly SYSTEM 任务运行/清理、真实任务故障回退/首次安装/升级/卸载，以及 SYSTEM 扫描任务调度执行任务的无硬件 RecoverExited 整链。最后一项报告明确 `physicalHardwareSafetyVerified=false`；当前观察包仍不覆盖设备断能、真实通道隔离和现场业务圈数对账。

## 首次受控恢复验收入口

`Invoke-MTTFTest-RecoveryGuardCommissioning.ps1` 用于生成生产自动恢复报告前的受监督验证，不等于开启无人值守自动恢复。只有清洁 Release、配套 Main 已安装、当前运行授权存在且常驻自动执行任务关闭时才可进入；旧观察包不具有此入口。

管理员 PowerShell 先运行 `-Mode Plan`，明确指定 `-GuardPackageDirectory`、`-MainReleaseDirectory`、`-EvidenceDirectory`。检查生成的 `commissioning-plan.json` 后，用相同路径运行 `-Mode Execute`。计划绑定机器、安装身份、试验授权版本和组件哈希；默认10分钟，最多15分钟，从生成计划时计算，不因执行重试延长。每份计划最多启动一次执行器，不确定结果必须对账后使用新的计划。

只开放 RecoverExited：主程序仍在运行时不停止或强杀它；发生符合授权的退出后仍调用既有 Supervisor 安全、持久化和启动准入链。人工停止、授权替换或过期后禁止新的动作，晚到结果不能推进事务。超时保留未完成事务，必须对账，不自动删除事务或释放控制权；不以退出码0作为业务恢复或物理安全通过。

该工具不安装或开启常驻自动执行任务，不修改运行意图，不制造程序崩溃，不生成通过的现场报告。报告需要真实的逐通道业务提交和安全证据。自动包仍由原自动发布器检查真实验收报告后生成。JXCQ夹具测试不能替代硬件验收。
