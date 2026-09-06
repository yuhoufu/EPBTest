# EPB 电流自适应闭环控制落地说明

## 当前上线策略

主程序 `MTTfTest/App.config` 当前采用全通道正式闭环配置：

```xml
<add key="EpbSafetyMarginControlMode" value="Legacy20251010" />
<add key="EpbControlMode" value="AdaptiveCurrent" />
<add key="EpbAdaptiveChannels" value="1,2,3,4,5,6,7,8,9,10,11,12" />
<add key="EpbAdaptiveShadowMode" value="false" />
```

这意味着 EPB1--EPB12 的学习圈和正式圈均由完整电流阶段状态机判定并直接执行 DO 控制；状态机的过流、开路、DAQ 断流和绝对上电时限等硬保护均生效。配置变更在程序重新启动后读取并生效。

## 已实现

- `Inrush → EmptyTravel → LoadRise → ClampReached → Hold → ReleaseDecay → Released` 电流状态机；
- 正向事件驱动夹紧判定，Adaptive 模式不再以 `FwdOnLimitMs` 作为正常断电条件；
- 正向历史软时限：最近 30 个有效样本的 `median + max(1000ms, 4×MAD)`；
- 反向低负载稳定 150ms、继续确认 200ms 后自动断电；
- 正反向绝对最长上电时间按周期自动计算；
- 三个快速样本过流、DAQ 超过 100ms 无样本、开路、绝对时限和异常高电流平台硬保护；
- 硬故障复用 `AlarmRaised → StopChannelOnAlarm → AlarmCycle → 最近10圈快照`；
- `Success / SuccessWithWarning / HardFault / Canceled` 结构化单圈结果；
- 失败/取消圈写为 `failed`/`canceled`，不再误写为 `completed`，报警圈也不再增加成功计数；
- 周期超限后滚动到下一个未来边界，同时保持实际完成圈号连续；
- 项目级 `Config/EpbAdaptiveProfiles.xml` 原子保存、损坏文件备份和空模型回退；
- 前 5 个有效圈形成稳定模型；连续 3 圈偏差超过 30%时预警并渐进更新；
- `LegacyFixedTiming` 总体回退开关，以及可通过通道白名单实施的单通道灰度能力（历史上用于 EPB10）。
- 自适应通道的启动学习圈与正式圈共用同一电流状态机；不会再调用旧的
  `FwdOnLimitMs` / `RevEmptyFixedMs` 固定时序，学习成功圈直接积累项目模型。
- 批量启动采用 UI 按钮锁和控制层会话锁双重防重复；停止、报警、自然结束或
  启动异常都会清理会话，异常日志保留完整堆栈。

## 配置

配置位于主程序 `App.config`：

```xml
<add key="EpbSafetyMarginControlMode" value="Legacy20251010" />
<add key="EpbControlMode" value="AdaptiveCurrent" />
<add key="EpbAdaptiveChannels" value="1,2,3,4,5,6,7,8,9,10,11,12" />
<add key="EpbAdaptiveShadowMode" value="false" />
```

### 参数详解

| 参数 | 当前值 | 可选值/格式 | 生效含义 |
| --- | --- | --- | --- |
| `EpbSafetyMarginControlMode` | `Legacy20251010` | `Legacy20251010`、`FreezeA20260101` | 选择学习阶段的正向阈值裕量更新算法。当前值采用 2025-10-10 的固定增益、单步限幅策略，正式圈保持学习完成时得到的裕量，不再按每圈峰值继续自调。 |
| `EpbControlMode` | `AdaptiveCurrent` | `AdaptiveCurrent`、`LegacyFixedTiming` | 全局控制总开关。`AdaptiveCurrent` 允许已选通道使用状态机直接控制；`LegacyFixedTiming` 时所有通道退回旧固定时序控制。 |
| `EpbAdaptiveChannels` | `1,2,3,4,5,6,7,8,9,10,11,12` | 1--12 的逗号、分号或空格分隔列表 | 自适应控制的通道白名单。只有全局模式为 `AdaptiveCurrent` 且通道位于此列表中，该通道才使用完整电流曲线控制；其余通道自动按旧固定时序运行。 |
| `EpbAdaptiveShadowMode` | `false` | `true`、`false` | 影子开关。`true` 时状态机只观察、记录预警和学习结果，不直接保护或改变 DO；`false` 时状态机直接参与控制并执行硬保护。正式试验必须为 `false`。 |

#### `EpbSafetyMarginControlMode=Legacy20251010`

正向夹紧判据为 `current + SafetyMarginA >= ForwardA`，因此裕量越大，越早断开正向 DO。`Legacy20251010` 以 `Imax - ForwardA` 为误差：误差绝对值不超过 `0.05 A` 时不调整；其余情况按 `0.60 × 误差` 调整，单圈最大变化为 `±0.50 A`，并将裕量限制在 `0.20--5.00 A`。该版本使用固定、对称的调整规则，当前被选为较保守的基线策略。

`FreezeA20260101` 是替代策略：上调和下调使用不同增益，并在过冲后冻结/放缓后续下调，减少轻微欠冲导致的快速回拉。它会在正式圈峰值封口后继续调整正式裕量；启用前应按变更流程完成专项验证，不能与“当前更保守基线”这一假设混用。

#### 正式模式的组合校验

启动正式试验时，系统会拒绝以下任一情况：程控电源闭环要求已启用但未初始化；选中通道未实际落入 `AdaptiveCurrent`；或 `EpbAdaptiveShadowMode=true`。此外，每一路必须先形成稳定基线（至少 5 个完整有效学习圈），否则不能进入正式试验。

当前四项配置满足上述正式模式条件：全局启用自适应、1--12 路均在白名单内、影子模式关闭，且裕量使用 `Legacy20251010` 基线算法。

### 配置变更与回退

推荐将配置变更作为受控变更执行：先停止当前批次，修改 `App.config`，重启程序，确认启动日志中的控制模式、灰度通道和影子模式，再开始新的学习/正式批次。

- 如需整体回退至旧控制：将 `EpbControlMode` 改为 `LegacyFixedTiming`。即使白名单仍包含 1--12 路，也不会启用新控制。
- 如需只缩小自适应范围：保持 `EpbControlMode=AdaptiveCurrent`，仅从 `EpbAdaptiveChannels` 删除对应通道；被删除通道将回退为旧固定时序。正式批次仍要求所有被选中通道都在白名单中。
- 如需观察而不直接控 DO：设 `EpbAdaptiveShadowMode=true`。该组合仅用于调试/核对，系统会阻止启动正式试验。
- 如需切换裕量算法：仅修改 `EpbSafetyMarginControlMode` 为 `FreezeA20260101` 并重新验证学习和正式圈表现；不能把该切换当作无验证的运行时微调。

不应通过只打开 `AdaptiveCurrent`、却遗漏目标通道白名单或保持影子模式的方式尝试进入正式控制；控制层会将未满足条件的通道视为旧模式并阻止正式启动。

### 历史灰度路径（仅供追溯）

早期灰度配置为 `LegacyFixedTiming + EpbAdaptiveChannels=10 + EpbAdaptiveShadowMode=true`，仅用于 EPB10 的并行波形核对。该组合不是当前生产配置，且不可用于启动正式试验。

## 验证

合成波形测试工程：

```powershell
& .\Tests\AdaptiveControlTests\bin\Release\AdaptiveControlTests.exe
```

现场 CSV 回放：

```powershell
& .\Tests\AdaptiveControlTests\bin\Release\AdaptiveControlTests.exe --replay "<EPB单圈CSV>"
```

当前自动测试覆盖正常夹紧、长空行程软预警、反向动态释放、保持阶段、过流、堵转、开路、
DAQ 断流、噪声尖峰、绝对上电时限、模型原子保存、损坏回退和周期不追赶。

`10364-009` 的 EPB10 现场快照 `Cycle_017156` 回放结果：

- 正向在 `13.033A` 触发夹紧（阈值 `15A`、裕量 `2A`）；
- 反向在低负载稳定区识别释放，判定点 `1.784A`，窗口中位数 `2.047A`；
- 无硬故障。

## 尚需现场完成

- 核对至少一班次影子日志；
- EPB10 模拟 DO 验证每个成功圈只有一次正向、一次反向；
- 逐项执行过流、开路、DAQ 断流和最长上电故障注入；
- EPB10 自适应模式连续 100 圈验收；
- 将软预警从现有 UI 警告日志进一步映射为独立黄色状态灯；
- 验收通过后再创建正式 `v1.3.0` 标签。
