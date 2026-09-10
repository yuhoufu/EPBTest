# RecoveryGuard 自动发布验收报告契约

2026-09-08新增软件校验模块Tools/RecoveryGuard-Acceptance.ps1，随后接入独立自动发布器New-MTTFTest-RecoveryGuardAutomaticPackage.ps1和安装器schema 3准入。原New-MTTFTest-RecoveryGuardPackage.ps1仍负责生成观察基础包，已交付ZIP不变。目前有结构及准入隔离测试，尚未完成真实自动包发布/安装验收，也没有现场合格报告。

## 身份和范围

报告使用schemaVersion=1、kind=RecoveryGuardFieldAcceptance、productVersion=3.0.0.0，记录benchId、machineName、operatorName以及UTC completedUtc。testOnly必须为布尔false，passed和physicalHardwareSafetyVerified必须为布尔true；字符串形式的真假不接受。缺失范围信息、未来时间、无UTC标识的时间拒绝。

mainIdentitySha256绑定完整主程序build-identity.json；guardExecutableSha256和recoveryControlSha256绑定实际Guard EXE和共享核心DLL。预期哈希由调用者从实际候选文件取得，不能直接使用报告自己的哈希作为预期值。

approvedModes先包含RecoverExited；开放RecoverStalled还须同时包含对应模式和额外停滞恢复验收。报告不能用于未获准的模式。监督中断60分钟是运行授权规则，不挪作报告有效期；这里以不可替换的候选文件绑定防止旧版本验收误用，现场工况改变后的重新验收另行处理。

## 验收条目

checks必须覆盖且仅覆盖以下标识，每项包含布尔passed=true、相对evidencePath及evidenceSha256，不接受重复、缺失或失败项：

independence、oldState、primaryBackupConflict、snapshotFreshness、operatorPriority、persistenceFailure、singleInstance、sessionRollover、repeatedFailures、cooldownAndExpiry、powerLossBoundary、panelStop、transactionInterruption、coordinatorLoss、maintenanceAndClock、persistentBudget、dataReconciliation、businessRecovery，对应实施方案第11节18类场景；另有exitedRecovery、physicalSafety。获准RecoverStalled时追加stalledRecovery、forcedStalledRecovery、forcedStalledRecoveryInterlocks。

2026-09-09 完全卡死兜底接入后新增的两个条目必须单独覆盖：

- `forcedStalledRecovery`：完全停滞及协作宽限证据、精确旧进程身份与退出、退出后的独立断能和卸压实际动作、动作后的全受影响范围新鲜电流/压力及安全保持窗口、匹配入口续测和真实业务提交。仅协作停止成功不满足该项。
- `forcedStalledRecoveryInterlocks`：正常学习/冷却/保压不误杀；终止前停止/暂停/维护/授权撤销优先；PID 重用不误杀；独立输出失败、陈旧或不安全反馈、续测数据边界不确定、新进程无业务提交时不判恢复成功。现场不可安全注入的反例必须明确未测，不可用合成测试冒充现场通过。

旧报告只有 stalledRecovery 时不能晋级当前 RecoverStalled 包。即使请求 RecoverExited，只要报告同时声明批准 RecoverStalled，也会要求三个条目齐全。仅声明 RecoverExited 的报告要求保持不变。所有报告仍须绑定当前候选包的实际哈希；不自动改写历史报告。

证据路径限制在调用者指定的证据目录内，拒绝父路径跳转、绝对路径、数据流路径和任何路径段的重解析链接；文件必须存在且SHA256匹配。各验收项的“通过”应表示满足方案相应判据，包括无法证明安全时保持Blocked、面板按钮能力限制如实记录，不能把人工按钮描述成独立自动断能能力。

## 能力边界及后续接线

校验器证明结构、范围、文件一致性和结果声明满足要求，不能独立鉴定证据是否真实、替代现场验收人员判断或签署验收。测试中的合格对象是内存中的明确合成夹具；不会输出可用于现场放行的合格报告。

自动发布器现已接线：要求当前源码干净、观察基础包来自同一提交且为实际构建的Release；校验正式主程序及共享核心；从实际候选计算预期哈希；冻结报告字节并复制证据；复核暂存内容、源报告、主程序和源码状态后才形成输出。schema 3清单包含报告、校验模块、证据及获准模式。安装器重验报告和附件，在自动任务启用前匹配台架、机器、主程序清单，并继续执行既有主组件校验。基础配置仍为ObserveOnly，自动模式需显式选择。

当前验证不等于整个发布事务验证：还需验证干净输入下的完整正向发布、源输入替换/中断故障及真实安装事务。尚未生成可供现场使用的AutomaticRecovery ZIP，不能依据局部测试启用任务。schema 2自动标志现在被拒绝；已经交付的schema 2观察包继续兼容。

## 验证结果

本地PowerShell和JXCQ Windows PowerShell 5.1均通过18/18。覆盖合格结构、模拟报告、缺失硬件声明、字符串真假、三种哈希不匹配、模式越级、条目缺失/重复/失败、时间无UTC/未来、路径越界、证据缺失/篡改，以及停滞模式的额外条目要求。重解析链接拒绝代码目前未取得单独的链接注入测试证据，不把18项数量当作所有分支覆盖。

实际历史JXCQ无硬件E2E报告被拒绝，原因AcceptanceRequiresPassedPhysicalFieldReport；该报告哈希为33AED1D02BDD34F7B8C977F83B6383BB3C1FFD3C89B2C7812E82DE96C615E3BD。证据分别为artifacts/guard-acceptance-tests.log、guard-acceptance-jxcq.json、guard-acceptance-e2e-rejection.json。

未执行现场设备操作或检测CAN。I0050根因修复继续保留在第二步。

接线补录：报告校验18/18与新包读取/目标绑定6/6在本地及JXCQ Windows PowerShell 5.1通过；任务XML和模式准入29项在本地32位Windows PowerShell通过；既有观察包兼容11项通过（该次不含独立ZIP解压项）。原任务测试运行器在PowerShell 7加载旧Debug程序集失败，改用匹配的32位.NET Framework运行器后完成；测试中的旧schema 2自动模式夹具同步更新为schema 3并补充旧格式拒绝、模式越级及脏构建拒绝。发布入口当前工作区脏状态被拒绝且输出不存在。证据为artifacts/guard-auto-admission-tests.log、guard-auto-admission-jxcq.json、guard-auto-task-compat.log、guard-auto-observe-compat.log和guard-auto-publisher-rejection.json。
