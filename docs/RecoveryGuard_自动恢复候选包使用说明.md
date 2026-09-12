# 自动恢复待验收安装包

本包包含Main、Supervisor、独立Guard和八个中文入口。安装及修复入口默认请求 **RecoverExited**，不会默默改装成ObserveOnly。它可以构建和分发，但现场自动执行启用前必须通过真实验收报告校验；未提供报告时明确拒绝，不修改安装状态。这不是已经完成现场自动恢复验收的证明。

异常退出恢复仍检查最新运行授权、人工停止、60分钟过期、事务接管互斥、断能卸压及持久化收口、进程退出和数据提交对账；不开放无独立断能证明的强杀。可显式选择 RecoverStalled，但必须额外通过停滞恢复验收；无独立安全证明时保持阻塞，不强杀。没有CAN检测；I0050修复仍独立进行。

## 使用

1. 完整解压到本机目录，可包含中文和空格。
2. 包完整性检查：`powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-AutomaticRecoveryBundle.ps1 -Mode ValidatePackage`。此操作不安装或启用任务。
3. 完成真实现场验收，报告及附件放在包目录之外，避免改变不可变包文件集合。报告契约见仓库`docs/RecoveryGuard_自动发布验收报告契约.md`，必须绑定Base中的Main和Guard身份，不可使用历史不同包报告或模拟结果。
4. 将报告文件拖到“一键安装正式版.cmd”上，或执行 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-AutomaticRecoveryBundle.ps1 -Mode Install -AcceptanceReportPath D:\验收证据\report.json`。需要时自动申请管理员权限；先校验报告、附件、机器和台架，再调用原Main安全安装器及Guard自动模式安装器。
5. 安装成功后检查`MTTFTestRecoveryGuardExecution`已启用，再通过“启动试验”建立新的人工授权试验。安装不自动开始试验。自动恢复动作只在真实异常且运行时门禁通过时发生。

双击安装但没有报告会明确提示缺项；不会假定现场已安全，也不会创建观察任务来冒充自动恢复任务。修复同样需要报告，可将报告拖到“一键修复.cmd”。如果仅希望提前检查报告，使用`-Mode Validate`，不会修改状态。

## 回退

先通过正常入口停止、等待本次断能卸压和保存完成并退出Main，保留增量数据、配置及最新Current\DataStore。使用经验证的旧程序、匹配服务和任务恢复；不以旧数据库或圈数覆盖最新数据。自动安装产生的已验收Guard材料保存在ProgramData\MTTFTest\AcceptedGuardPackages，失败时保留排查。跨版本回退按现场记录执行，不能用“卸载”代替数据备份。

Base保留已验证的基础组件及校验器。其基础身份中的观察模式是验收绑定来源，不是根目录自动安装入口最终选择的模式。不要进入Base手动执行安装来替代根目录自动恢复安装。

## 可选停滞恢复

仅在真实报告 approvedModes 包含 RecoverStalled 且 stalledRecovery 场景通过后，使用 PowerShell 安装或修复入口追加 `-RecoveryMode RecoverStalled`。该模式仍受运行授权、人工停止优先、60分钟过期、独立安全证明及接管互斥约束。管理员提权会保留所选模式。首次双击安装默认 RecoverExited；修复未显式指定模式时保留已安装的自动恢复模式，已有观察模式则请求 RecoverExited。已安装模式缺失或未知时拒绝覆盖。显式参数可选择报告批准的模式。软件模拟验证不能代替这项现场验收。
