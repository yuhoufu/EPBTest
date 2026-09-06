# UI 实时曲线性能联调说明（WinForms + ZedGraph）

> 适用范围：本仓库当前 `MTTfTest/FrmEpbMainMonitor.cs` 的实时曲线显示链路。
>
> 目标：在“全通道显示 + 长时间运行 + 前后台切换”的场景下，保证 UI 不出现明显卡顿、阶梯/平台伪影、以及调试期的 `ContextSwitchDeadlock`。

---

## 1. 背景与现象（为什么要做这套优化）

在采集回调频率较高（例如 20Hz~200Hz）且曲线点数不断增长的情况下，ZedGraph 的开销主要来自：

- **UI 线程被高频更新淹没**：每批数据都 `BeginInvoke/Invoke`，消息队列堆积。
- **重绘过于频繁**：每次追加点就 `AxisChange/Invalidate`，导致 UI 线程持续占用。
- **点数爆炸**：全通道 15 条曲线 × 高频采样 × 长时间运行，点数与 GC 压力飙升。
- **前后台切换触发重绘/布局抖动**：窗口被遮挡/失去焦点后再激活时，控件会发生额外重绘；如果堆积的 UI 工作未消化，会表现为“阶梯/平台”。
- **调试期 MDA：`ContextSwitchDeadlock`**：STA（UI 线程）长时间不泵消息，调试助手会提示潜在死锁（通常是 UI 线程被同步等待/长任务阻塞）。

典型外观问题：

- **卡顿**：鼠标拖动、按钮点击延迟明显。
- **阶梯/平台**：本应平滑变化的曲线，在某些段落出现“水平平台 + 突变”。
- **后段变形**：曲线前段正常，后段出现“时间轴压缩/波形被挤在一起”。

---

## 2. 总体策略（做了什么，不做什么）

实时曲线链路的原则：

1. **采集线程只做“入队/缓存”**，不直接驱动 UI 重绘。
2. **UI 线程只做“批处理 + 节流 + 统一重绘”**，避免同步等待与高频刷新。
3. **显示层允许“抽稀/降帧”**：显示与控制/落盘解耦，显示丢部分点是允许的（但要避免因丢批次导致的“时间轴压缩”）。
4. **时间轴一致性优先**：当 UI 丢弃中间批次时，通过批次时间戳做 gap 补偿，避免后段变形。

对应到当前实现：

- OnEngBatch 到来时：进入 UI 侧“每设备小队列”，并触发“单任务排队”的 UI 消费。
- UI 消费时：从队列取一批批次，追加点、做 gap 补偿与桥接、对显示点数进行抽稀。
- 重绘：由 UI 定时器统一进行 `AxisChange/Invalidate`（避免每批重绘）。
- 前后台切换：在 `Activated` 事件中重新应用 ZedGraph 的“快速渲染设置”（关闭抗锯齿/平滑，尝试启用 FastLine）。

---

## 3. 关键实现位置（从哪里看代码）

- 实时曲线核心：`MTTfTest/FrmEpbMainMonitor.cs`
  - 批次入口（采集→UI）：`Acq_OnEngBatch(...)`
  - UI 调度：`ScheduleEngBatchUiWork()` / `ProcessPendingEngBatches()`
  - 追加点与 gap 补偿：`ApplyEngBatchToCurves(...)` / `AppendChannelBatchFromMatrix(...)`（名称以实际文件为准）
  - 统一重绘：`StartUiRedrawTimer()`
  - 快速渲染设置：`ApplyZedGraphFastRenderSettings()`

> 说明：本篇只讨论“采集批次→UI 曲线显示”链路。测试启动（批量启动）主入口在 `Controller/EpbManager.BatchStart.cs` 的 `EpbManager.StartBatchSynchronizedAsync(...)`。

---

## 4. 可调参数解释（只影响显示层）

> 这些参数只影响 UI 画图的性能与观感，不影响控制逻辑、不影响数据落盘。

### 4.1 `UI_TARGET_FPS`

- 含义：UI 统一重绘定时器的目标帧率。
- 影响：越高越“跟手/平滑”，但 CPU 占用更高，且更容易在全通道时卡顿。
- 建议：`5~15`。
  - 全通道：优先 `8~12`
  - 单通道/少量曲线：可提高到 `15~25`

### 4.2 `UiMaxPlotHz`

- 含义：显示层抽稀上限（每秒最多绘制多少“可见点”）。
- 作用：降低点数增长速度，减少 GC 与重绘成本。
- 建议：`80~150`。
  - 若仍卡顿：下调到 `60~100`
  - 若曲线太“稀”：上调到 `150~200`

### 4.3 `MaxPendingEngBatchesPerDev`

- 含义：每个设备在 UI 侧允许积压的批次数量。
- 作用：短时间 UI 跟不上时，通过“小队列”减少“只保留最新一批”造成的阶梯。
- 风险：过大可能导致 UI 一次性处理时间过长；过小会更容易出现阶梯。
- 当前版本：已提高到 `64`，减少丢批次 → 减少时间轴 gap。
- 建议：现场若 CPU 仍有余量，可以保持 64；若 UI 卡顿可下调到 32~48。

### 4.4 `MaxEngBatchesPerUiRun`

- 含义：单次 UI 调度最多消费多少批次（全设备合计）。
- 作用：避免 UI 线程一次处理太久导致更严重卡顿。
- 当前版本：已提高到 `64`，让积压的批次尽快被消费，降低 gap 概率。
- 建议：按机器性能在 `32~64` 间调整；如 UI 卡顿，可下调。

### 4.5 Gap 处理策略（视觉断线阈值）

- 逻辑：当批次时间差 `gapSec` 大于阈值才断线（插入 NaN），小 gap 直接平移时间继续画，避免“虚线感”。
- 断线阈值：`0.3s`；gap 可视化上限：`1s`（超过也只显示 1s 断口，防止长空窗）。
- 如仍觉得断线多：可调高阈值到 `0.5s`；如仍有尖峰，可适度调低阈值，但可能会看到更多短断线。

### 4.5 `InstantUiUpdateMinIntervalMs`

- 含义：瞬时值（文本框）刷新节流间隔。
- 作用：避免每批数据都刷新大量控件导致 UI 抖动。
- 建议：`150~300ms`。

### 4.6 ZedGraph “快速渲染设置”

当前做法：

- 控件级：`zedGraphRealChart.IsAntiAlias = false`
- 曲线级：`LineItem.Line.IsAntiAlias = false`、`IsSmooth = false`
- 若版本支持：通过反射尝试启用 `IsFastLine`（可能是 `Line` 或 `LineItem` 上的属性）

判据：程序启动或窗口重新激活后，会打印一次性日志：

- `IsFastLine=ON`：当前 ZedGraph 版本支持并成功置位
- `IsFastLine=N/A`：该版本无该属性，已自动跳过（仍关闭 AntiAlias/Smooth）

---

## 5. 现场验证步骤（推荐顺序）

1. **基准验证（少曲线）**
   - 只勾选 1~2 条曲线运行 30s：应无明显卡顿。
2. **全通道验证（压力测试）**
   - 勾选 EPB1..12 + P1/P2/F，连续运行 1~2 分钟。
3. **前后台切换验证**
   - Alt+Tab 来回切换 5~10 次；观察是否出现“阶梯/平台”。
4. **日志判据**
   - 观察是否出现：`ZedGraph 快速渲染设置已应用... IsFastLine=ON/N/A ...`。

---

## 6. 排查套路（按症状定位）

### 6.1 症状：UI 卡顿明显（CPU 高）

优先检查：

- `UI_TARGET_FPS` 是否过高 → 先降到 `10` 左右。
- `UiMaxPlotHz` 是否过高 → 先降到 `80~120`。
- 是否存在“每批就 AxisChange/Invalidate”的旧逻辑 → 应由 `StartUiRedrawTimer()` 统一重绘。
- 是否存在同步等待：`Invoke()`、`Task.Wait()`、`Thread.Sleep()` 在 UI 线程 → 需要避免（会放大 `ContextSwitchDeadlock` 风险）。

### 6.2 症状：前后台切换后出现阶梯/平台

常见原因：

- 切回前台时发生额外重绘/布局，叠加待处理队列，导致“批次显示节奏被拉成阶梯”。

排查/处理：

- 确认 `Activated` 会调用 `ApplyZedGraphFastRenderSettings()`。
- 适度提高 `MaxPendingEngBatchesPerDev`（例如 16→24）减少丢批次。
- 若 CPU 允许，适度提高 `UI_TARGET_FPS`（例如 10→12）。

### 6.3 症状：曲线后段变形（前段正常）

特征：后段像“被挤压/压缩”，通常是 UI 丢弃了中间批次但 X 轴按“样本点数”推进。

处理要点：

- X 轴推进应参考批次时间戳：当发现 `gapSec`（当前批次时间 - 上一批次时间）明显大于正常周期时，进行时间轴补偿。
- 若 gap 处断线观感差，可做桥接连线；但要避免重复 X 导致竖线。

### 6.4 症状：调试期出现 `ContextSwitchDeadlock`

根因通常是：UI 线程长时间不响应 COM/消息泵。

排查列表：

- UI 线程是否有长循环/大批量处理（一次消费太多批次） → 限制 `MaxEngBatchesPerUiRun`。
- 是否在 UI 线程做同步等待（`Wait/Result/Invoke`） → 改为异步/BeginInvoke。
- Dispose/Stop 流程是否在 UI 线程同步等待后台线程退出 → 需要避免。

---

## 7. 建议的默认配置（可作为现场基线）

- 全通道显示：
  - `UI_TARGET_FPS = 10`
  - `UiMaxPlotHz = 100`
  - `MaxPendingEngBatchesPerDev = 16`
  - `MaxEngBatchesPerUiRun = 16`
  - `InstantUiUpdateMinIntervalMs = 200`
  - FastRender：AntiAlias OFF / Smooth OFF / FastLine（若支持）

---

## 8. 常见构建/调试坑

- **无法复制 MTTFTest.exe（文件被占用）**：
  - 结束 `MTTFTest.exe` 或 `msvsmon.exe` 后再编译。

---

## 9. 版本差异说明

- ZedGraph 的 `IsFastLine` 并非所有版本都有。
- 当前实现通过反射“有则启用，无则跳过”，不应因为属性缺失导致编译失败。

---

如需进一步深挖（例如把每次 UI 消费耗时、队列深度、丢批次数量打点统计），建议增加轻量级性能计数日志，但应避免在高频路径写大量日志以免反向拖慢 UI。
