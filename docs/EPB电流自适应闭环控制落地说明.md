# EPB 电流自适应闭环控制落地说明

## 当前上线策略

本阶段已经把自适应状态机接入正式控制代码，但默认仍采用安全灰度配置：

- `EpbControlMode=LegacyFixedTiming`：DO 控制继续使用旧流程；
- `EpbAdaptiveShadowMode=true`：新状态机并行分析快速采样，只记录预警和学习模型，不改变 DO；
- `EpbAdaptiveChannels=10`：切换到 `AdaptiveCurrent` 后也只允许 EPB10 使用新控制，其余通道保持旧模式。

在 EPB10 完成影子日志核对和单通道台架验证前，不应扩大自适应通道范围。

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
- `LegacyFixedTiming` 回退开关和 EPB10 单通道灰度开关。
- 自适应通道的启动学习圈与正式圈共用同一电流状态机；不会再调用旧的
  `FwdOnLimitMs` / `RevEmptyFixedMs` 固定时序，学习成功圈直接积累项目模型。

## 配置

配置位于主程序 `App.config`：

```xml
<add key="EpbControlMode" value="LegacyFixedTiming" />
<add key="EpbAdaptiveChannels" value="10" />
<add key="EpbAdaptiveShadowMode" value="true" />
```

建议执行顺序：

1. 维持上述默认配置运行影子判定；
2. 确认 EPB10 的状态转换、软预警和模型统计与波形一致；
3. 台架硬故障注入全部通过后，仅把 `EpbControlMode` 改成 `AdaptiveCurrent`；
4. EPB10 连续 100 圈验收通过后，再逐步扩展 `EpbAdaptiveChannels`。

如需立即回退，只需把 `EpbControlMode` 改回 `LegacyFixedTiming`，无需恢复旧 DLL。

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
