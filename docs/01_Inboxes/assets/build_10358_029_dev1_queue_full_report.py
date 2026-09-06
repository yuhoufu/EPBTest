#!/usr/bin/env python3
"""Build the canonical Data Analytics report artifact for the Dev1 incident."""

from __future__ import annotations

import json
import sqlite3
from pathlib import Path


HERE = Path(__file__).resolve().parent
ANALYSIS_PATH = HERE / "10358_029_dev1_queue_full_analysis.json"
OUTPUT_PATH = HERE / "10358_029_dev1_queue_full_artifact.json"
DB_PATH = HERE / "10358_029_dev1_queue_full_report.sqlite"
GENERATED_AT = "2026-08-04T22:20:30+08:00"


def source(source_id: str, label: str, path: str, description: str, tables: list[str]) -> dict:
    return {
        "id": source_id,
        "label": label,
        "path": path,
        "query": {
            "engine": "local-file-analysis",
            "language": "python",
            "description": description,
            "executed_at": GENERATED_AT,
            "tables_used": tables,
            "filters": {
                "test": "10358-029",
                "run_start_local": "2026-08-04 20:57:10.863",
                "incident_local": "2026-08-04 21:36:16.203",
            },
            "metric_definitions": {
                "processing_lag_event": "warning.log 中 OldestBatchAge > 100 ms 的限频告警；不是全部慢批次。",
                "paired_within_50ms": "Dev1 告警与最近 Dev2 告警时间差不超过 50 ms，且一对一匹配。",
                "queue_time_lower_bound_ms": "队列容量 × 每批预期间隔；64 × (20/2000 s) = 640 ms。",
            },
        },
    }


def dataset_source(source_id: str, table: str, description: str) -> dict:
    return {
        "id": source_id,
        "label": f"Reviewed incident dataset: {table}",
        "path": "docs/01_Inboxes/assets/10358_029_dev1_queue_full_report.sqlite",
        "query": {
            "engine": "sqlite",
            "language": "sql",
            "sql": f'SELECT * FROM "{table}"',
            "description": description,
            "executed_at": GENERATED_AT,
            "tables_used": [table],
            "filters": {
                "test": "10358-029",
                "run_start_local": "2026-08-04 20:57:10.863",
                "incident_local": "2026-08-04 21:36:16.203",
            },
        },
    }


def write_sqlite_tables(datasets: dict[str, list[dict]]) -> None:
    connection = sqlite3.connect(DB_PATH)
    try:
        for table, rows in datasets.items():
            if not rows:
                continue
            columns = list(rows[0])
            types = {}
            for column in columns:
                values = [row.get(column) for row in rows if row.get(column) is not None]
                if values and all(isinstance(value, bool) or isinstance(value, int) for value in values):
                    types[column] = "INTEGER"
                elif values and all(isinstance(value, (int, float)) for value in values):
                    types[column] = "REAL"
                else:
                    types[column] = "TEXT"
            connection.execute(f'DROP TABLE IF EXISTS "{table}"')
            column_sql = ", ".join(f'"{column}" {types[column]}' for column in columns)
            connection.execute(f'CREATE TABLE "{table}" ({column_sql})')
            placeholders = ", ".join("?" for _ in columns)
            connection.executemany(
                f'INSERT INTO "{table}" VALUES ({placeholders})',
                [[row.get(column) for column in columns] for row in rows],
            )
        connection.commit()
    finally:
        connection.close()


def main() -> None:
    analysis = json.loads(ANALYSIS_PATH.read_text(encoding="utf-8"))
    lag_stats = analysis["processing_lag"]["by_device"]
    paired = analysis["processing_lag"]["paired_within_50ms"]
    sampling = analysis["sampling"]
    after_fault = analysis["cycle_index"]["completed_cycles_started_after_fault"]
    dev2_completed = sum(after_fault[str(epb)] for epb in (8, 9, 10, 11))

    lag_summary = []
    for device in ("Dev1", "Dev2"):
        for metric, key in (("中位数", "median"), ("P95", "p95"), ("最大值", "max")):
            lag_summary.append(
                {
                    "device": device,
                    "metric": metric,
                    "oldest_batch_age_ms": lag_stats[device]["oldest_batch_age_ms"][key],
                    "event_count": lag_stats[device]["oldest_batch_age_ms"]["count"],
                }
            )

    lag_events = analysis["processing_lag"]["events"]
    timeline = [
        {
            "time": "21:36:10.256 / 10.757",
            "component": "EPB4 / EPB5",
            "event": "最后正常圈结束",
            "evidence": "Cycle 748 / 671 均 completed；CSV 行号连续、0.5 ms 等间隔。",
            "interpretation": "报警前两卡钳最后一圈完整。",
        },
        {
            "time": "21:36:15.026–15.027",
            "component": "液压组 1 / 2",
            "event": "新周期同时建压",
            "evidence": "HydraulicQualificationStarted Generation=154。",
            "interpretation": "进入下一周期准备阶段。",
        },
        {
            "time": "21:36:15.902",
            "component": "Dev2",
            "event": "液压组 2 合格",
            "evidence": "73.339 bar；EPB8/10 随后启动。",
            "interpretation": "Dev2 采集、液压和控制链正常。",
        },
        {
            "time": "21:36:16.203",
            "component": "Dev1",
            "event": "后台队列满",
            "evidence": "BackgroundQueueFull，Generation=2，容量 64 批。",
            "interpretation": "生产者继续回调，但消费链至少落后约 640 ms。",
        },
        {
            "time": "21:36:16.207–16.253",
            "component": "EPB4 / EPB5",
            "event": "设备级安全停机",
            "evidence": "Affected=[4,5]；EPB4 主报警、EPB5 联锁/取消。",
            "interpretation": "一项 Dev1 故障在 UI 上表现为两个卡钳报警。",
        },
        {
            "time": "21:36:16.224–16.241",
            "component": "液压协调",
            "event": "取消异常被二次上报",
            "evidence": "TaskCanceledException 被显示为“压力不足/控制系统故障”。",
            "interpretation": "这是安全停机的级联结果，不是首发根因。",
        },
        {
            "time": "21:36:16.714 后",
            "component": "Dev2",
            "event": "EPB9/11 启动并持续运行",
            "evidence": f"备份结束前 Dev2 四路又完成 {dev2_completed} 圈。",
            "interpretation": "排除整机卡死和共同液压硬件故障。",
        },
    ]

    channel_outcomes = [
        {
            "channel": "EPB4",
            "device": "Dev1",
            "last_cycle": 748,
            "status": "completed",
            "last_peak_a": 14.877,
            "fault_current_a": 0.007,
            "post_fault_completed_cycles": 0,
            "conclusion": "主报警显示通道；不是卡钳自身过流。",
        },
        {
            "channel": "EPB5",
            "device": "Dev1",
            "last_cycle": 671,
            "status": "completed",
            "last_peak_a": 15.121,
            "fault_current_a": 0.008,
            "post_fault_completed_cycles": 0,
            "conclusion": "同设备联锁停机；不是第二个独立故障。",
        },
        {
            "channel": "EPB8/9/10/11",
            "device": "Dev2",
            "last_cycle": "875/878/878/878",
            "status": "completed",
            "last_peak_a": None,
            "fault_current_a": None,
            "post_fault_completed_cycles": dev2_completed,
            "conclusion": "故障后继续运行，设备隔离生效。",
        },
    ]

    root_causes = [
        {
            "level": "已证实的直接原因",
            "assessment": "Dev1 后台处理队列达到 64 批，触发 BackgroundQueueFull 失效安全。",
            "evidence": "error.log 21:36:16.203；入队拒绝点 TwoDeviceAiAcquirer.cs:2141–2162。",
            "confidence": "确定",
        },
        {
            "level": "已证实的机制",
            "assessment": "DAQ 回调生产仍在继续，后台 ProcessLoop 消费不足或被同步工作阻塞。",
            "evidence": "只有继续入队才会达到容量；未出现后台线程异常日志。",
            "confidence": "确定",
        },
        {
            "level": "高置信架构根因",
            "assessment": "ProcessLoop 把滤波、峰值、落盘、UI 和诊断串成单一同步消费链，非实时任务可阻塞设备队列。",
            "evidence": "TwoDeviceAiAcquirer.cs:1622–1833；OnDiskBatch 同步调用位于 1792。",
            "confidence": "高",
        },
        {
            "level": "首要性能热点",
            "assessment": "落盘仍逐样本写内存映射文件，并逐样本执行 SQLite UPDATE；两设备还共用一个 SQLiteConnection。",
            "evidence": "EpbDiskWriter.cs:748–825、1827–1838；六路活动时理论上最高约 12,000 次 UPDATE/s。",
            "confidence": "高（具体故障栈未留存）",
        },
        {
            "level": "可能放大因素",
            "assessment": "数组/字典副本、磁盘抖动、SQLite 竞争或线程池调度会放大停顿。",
            "evidence": "两设备 97.6% 的 Dev1 积压告警在 50 ms 内同步出现。",
            "confidence": "中",
        },
        {
            "level": "低可能性",
            "assessment": "两个卡钳同时硬件故障、液压泄漏、NI 回调停止或整机冻结。",
            "evidence": "卡钳当时未上电；队列满需要生产继续；Dev2 后续继续完成 42 圈。",
            "confidence": "已基本排除",
        },
    ]

    software_issues = [
        {
            "id": "P0-1",
            "problem": "实时/准实时消费链与同步落盘耦合",
            "impact": "慢 I/O 或数据库更新可直接把 DAQ 后台队列推满并停机。",
            "code_location": "TwoDeviceAiAcquirer.cs:1792；EpbManager.cs:566；EpbDiskWriter.cs:804",
        },
        {
            "id": "P0-2",
            "problem": "逐样本更新 SQLite 周期进度",
            "impact": "活动圈期间产生数量级过高的命令创建与 UPDATE。",
            "code_location": "EpbDiskWriter.cs:797、1827–1838",
        },
        {
            "id": "P0-3",
            "problem": "无活动圈时不生成报警快照",
            "impact": "本次硬故障没有 AlarmSnapshots、daq_timing.csv 或 daq_runtime.csv。",
            "code_location": "EpbManager.cs:1643–1651",
        },
        {
            "id": "P0-4",
            "problem": "诊断 CSV 过滤条件写反",
            "impact": "daq_timing.csv 只输出 ManagedMemoryBytes>0 的记录，丢掉大多数 Callback/Processing 行。",
            "code_location": "TwoDeviceAiAcquirer.cs:528–556",
        },
        {
            "id": "P0-5",
            "problem": "故障码和 UI 语义被错误归一",
            "impact": "BackgroundQueueFull 被显示成 DaqSampleStale；取消异常又显示成液压压力不足。",
            "code_location": "EpbManager.cs:1231–1305；液压协调取消链",
        },
        {
            "id": "P1-1",
            "problem": "队列满路径只锁存，不进入 DAQ 恢复流程",
            "impact": "无人值守试验无法自动排空/重建；但直接重启 DAQ 也不能解决慢消费端。",
            "code_location": "EpbManager.cs:1231–1249 对比 1210–1227、1312 后",
        },
        {
            "id": "P1-2",
            "problem": "EPB5 Latest 导出存在重复文件错误",
            "impact": "Cycle 667 导出报“文件已存在”，降低证据包完整性。",
            "code_location": "Latest/EPB5/20260804_213616/export_errors.txt",
        },
    ]

    actions = [
        {
            "priority": "P0",
            "action": "拆分采样处理与持久化",
            "implementation": "ProcessLoop 只保留工程转换、控制快照、压力/峰值等安全关键工作；把不可变 DiskBatch 投递到独立有界写盘队列。",
            "acceptance": "人为阻塞磁盘 1 s 时，控制新鲜度仍满足门限；仅产生 DaqPersistenceLag，不触发 DaqSampleStale。",
        },
        {
            "priority": "P0",
            "action": "批量化写盘与数据库",
            "implementation": "每批一次内存映射写入；sample_count 每批或封圈时更新；SQLite 使用单写者/显式串行化和事务，不让两设备并发操作同一连接。",
            "acceptance": "六路 2 kHz、N=20 稳态下不再逐点 UPDATE；队列年龄 P99 < 50 ms、最大 < 100 ms。",
        },
        {
            "priority": "P0",
            "action": "设备级故障快照独立于活动圈",
            "implementation": "先创建 IncidentSnapshots/<timestamp>-Dev1，再附加最近圈；无当前圈也必须写 timing/runtime、队列计数和阶段耗时。",
            "acceptance": "在建压等待窗口注入队列满，仍生成完整设备级快照。",
        },
        {
            "priority": "P0",
            "action": "修正诊断导出过滤",
            "implementation": "daq_timing.csv 输出全部 TimingRecord；runtime.csv 仅输出 Runtime 记录或保留全量并明确 Kind。",
            "acceptance": "快照含 Callback、Processing、HardFault；故障前 60 s 可重建每批阶段耗时。",
        },
        {
            "priority": "P0",
            "action": "保留故障分类与级联关系",
            "implementation": "ControlFault.Code 保持 BackgroundQueueFull；EPB5 显示“Dev1 联锁”；取消异常标记 CascadeCanceledByDaqFault，禁止提示液压泄漏。",
            "acceptance": "UI/日志只有一个首发故障，两个受影响通道共享同一 CorrelationId。",
        },
        {
            "priority": "P1",
            "action": "设计可控恢复",
            "implementation": "安全断电后清空旧 generation 队列；仅在消费能力恢复、连续新鲜批次达标后允许人工复位。队列满不得用单纯扩容掩盖。",
            "acceptance": "恢复前后无旧批次倒灌；报警锁存策略符合现场安全要求。",
        },
        {
            "priority": "P1",
            "action": "降低分配与导出冲突",
            "implementation": "复用批次数组/ArrayPool；Latest 导出使用唯一临时目录后原子发布，遇到已存在文件采用确定性覆盖策略。",
            "acceptance": "2 h 压测无重复导出错误，GC/内存与队列深度无持续上升。",
        },
    ]

    validation = [
        {
            "test": "稳态吞吐",
            "setup": "6 路卡钳、2 kHz、20 点/批、15 s 周期，至少 2 h 或 1,000 圈。",
            "pass_gate": "两设备 BackgroundQueueFull=0；队列年龄 P99<50 ms、max<100 ms；导出失败=0。",
        },
        {
            "test": "磁盘/SQLite 故障注入",
            "setup": "分别阻塞写盘 0.5 s、1 s，并模拟 SQLite busy。",
            "pass_gate": "控制快照持续新鲜；持久化告警独立；卡钳不得误报采集过期或液压不足。",
        },
        {
            "test": "DAQ 回调断流",
            "setup": "仅停止 Dev1 回调。",
            "pass_gate": "触发 DaqCallbackStale，Dev1 4/5 停机，Dev2 不受影响；与 BackgroundQueueFull 分类不同。",
        },
        {
            "test": "无活动圈故障快照",
            "setup": "在建压等待阶段注入 BackgroundQueueFull。",
            "pass_gate": "IncidentSnapshots 必含 timing/runtime/queue/stage；不依赖 CurrentCycle。",
        },
        {
            "test": "报警语义",
            "setup": "复现一个 Dev1 设备故障。",
            "pass_gate": "一个首发故障、EPB4/5 同一关联号；EPB5 为联锁；无“压力不足”误导。",
        },
    ]

    analysis_source = source(
        "incident_analysis",
        "10358-029 Dev1 queue-full reproducible analysis",
        "docs/01_Inboxes/assets/10358_029_dev1_queue_full_analysis.json",
        "Parses the current-run logs, reads the SQLite cycle index in read-only mode, and validates the final EPB4/EPB5 CSV cycles.",
        [
            "10358-029_2157_backup/log/warning.log",
            "10358-029_2157_backup/log/error.log",
            "10358-029_2157_backup/log/run.log",
            "10358-029_2157_backup/index.db",
            "10358-029_2157_backup/Latest/EPB4",
            "10358-029_2157_backup/Latest/EPB5",
        ],
    )
    code_source = source(
        "code_review",
        "EPBTest V2.8.0 acquisition and persistence code review",
        "docs/01_Inboxes/assets/analyze_10358_029_dev1_queue_full.py",
        "Static review of the bounded DAQ queues, synchronous ProcessLoop subscribers, cycle persistence, fault latching, and snapshot export paths at commit 90f5332.",
        [
            "IO.NI/TwoDeviceAiAcquirer.cs",
            "Controller/EpbManager.cs",
            "DataOperation/EpbDiskWriter.cs",
            "MTTfTest/App.config",
        ],
    )

    dataset_sources = [
        dataset_source(
            "headline_metrics_sql",
            "headline_metrics",
            "Returns the four reviewed headline incident metrics used by the report cards.",
        ),
        dataset_source(
            "lag_summary_sql",
            "lag_summary",
            "Returns median, P95, and maximum logged processing-lag age by DAQ device.",
        ),
        dataset_source(
            "lag_events_sql",
            "lag_events",
            "Returns every rate-limited processing-lag warning in the current run.",
        ),
        dataset_source(
            "timeline_sql",
            "timeline",
            "Returns the reviewed incident timeline derived from run, error, and UI logs.",
        ),
        dataset_source(
            "channel_outcomes_sql",
            "channel_outcomes",
            "Returns final-cycle and post-fault outcomes for the affected and unaffected channels.",
        ),
        dataset_source(
            "root_causes_sql",
            "root_causes",
            "Returns the evidence-ranked root-cause assessment from log and source-code review.",
        ),
        dataset_source(
            "software_issues_sql",
            "software_issues",
            "Returns the actionable software issues found in the V2.8.0 incident path.",
        ),
        dataset_source(
            "actions_sql",
            "actions",
            "Returns prioritized remediation actions and measurable acceptance criteria.",
        ),
        dataset_source(
            "validation_sql",
            "validation",
            "Returns the proposed regression and fault-injection release gates.",
        ),
    ]

    manifest_sources = [
        {"id": item["id"], "label": item["label"], "path": item["path"]}
        for item in (analysis_source, code_source, *dataset_sources)
    ]

    artifact = {
        "surface": "report",
        "manifest": {
            "version": 1,
            "surface": "report",
            "title": "10358-029：V2.8.0 Dev1 双卡钳报警技术复盘",
            "description": "2026-08-04 21:36:16 BackgroundQueueFull 的证据链、根因分级、软件问题和可验收整改方案。",
            "generatedAt": GENERATED_AT,
            "cards": [
                {
                    "id": "queue_capacity",
                    "description": "Dev1 后台处理队列的硬上限。",
                    "dataset": "headline_metrics",
                    "sourceId": "headline_metrics_sql",
                    "metrics": [{"label": "队列容量（批）", "field": "queue_capacity_batches"}],
                },
                {
                    "id": "backlog_lower_bound",
                    "description": "达到容量时覆盖的最小未消费采样时间。",
                    "dataset": "headline_metrics",
                    "sourceId": "headline_metrics_sql",
                    "metrics": [{"label": "积压下限（ms）", "field": "queue_time_lower_bound_ms"}],
                },
                {
                    "id": "paired_lag",
                    "description": "Dev1 积压告警中 50 ms 内伴随 Dev2 告警的比例。",
                    "dataset": "headline_metrics",
                    "sourceId": "headline_metrics_sql",
                    "metrics": [{"label": "同步积压比例", "field": "paired_fraction", "format": "percent"}],
                },
                {
                    "id": "dev2_continuation",
                    "description": "故障后至备份结束，Dev2 四路新启动并完成的圈数。",
                    "dataset": "headline_metrics",
                    "sourceId": "headline_metrics_sql",
                    "metrics": [{"label": "Dev2 后续完成圈数", "field": "dev2_completed_cycles"}],
                },
            ],
            "charts": [
                {
                    "id": "lag_distribution_chart",
                    "title": "两设备后台积压年龄分布",
                    "subtitle": "正常运行期两设备的 P95 都已超过 130 ms，最大值约 353 ms。",
                    "type": "bar",
                    "dataset": "lag_summary",
                    "sourceId": "lag_summary_sql",
                    "valueFormat": "number",
                    "encodings": {
                        "x": {"field": "device", "type": "nominal", "label": "设备"},
                        "y": {"field": "oldest_batch_age_ms", "type": "quantitative", "label": "最老批次年龄（ms）"},
                        "color": {"field": "metric", "type": "nominal", "label": "统计量"},
                        "tooltip": [
                            {"field": "metric", "type": "nominal", "label": "统计量"},
                            {"field": "event_count", "type": "quantitative", "label": "告警次数"},
                        ],
                    },
                },
                {
                    "id": "lag_depth_scatter",
                    "title": "队列深度与最老批次年龄",
                    "subtitle": "两设备点云高度重合，且年龄大致随队列深度按 10 ms/批增长。",
                    "type": "scatter",
                    "dataset": "lag_events",
                    "sourceId": "lag_events_sql",
                    "encodings": {
                        "x": {"field": "queue_depth_batches", "type": "quantitative", "label": "队列深度（批）"},
                        "y": {"field": "oldest_batch_age_ms", "type": "quantitative", "label": "最老批次年龄（ms）"},
                        "color": {"field": "device", "type": "nominal", "label": "设备"},
                        "tooltip": [
                            {"field": "timestamp", "type": "temporal", "label": "时间"},
                            {"field": "device", "type": "nominal", "label": "设备"},
                        ],
                    },
                },
            ],
            "tables": [
                {
                    "id": "timeline_table",
                    "title": "21:36 故障时间线",
                    "subtitle": "首发故障与级联报警分离展示。",
                    "dataset": "timeline",
                    "sourceId": "timeline_sql",
                    "columns": [
                        {"field": "time", "label": "本地时间", "type": "text"},
                        {"field": "component", "label": "对象", "type": "text"},
                        {"field": "event", "label": "事件", "type": "text"},
                        {"field": "evidence", "label": "证据", "type": "text"},
                        {"field": "interpretation", "label": "判读", "type": "text"},
                    ],
                },
                {
                    "id": "channel_outcomes_table",
                    "title": "卡钳与设备结果对照",
                    "subtitle": "EPB4/5 是同一设备故障的受影响通道。",
                    "dataset": "channel_outcomes",
                    "sourceId": "channel_outcomes_sql",
                    "columns": [
                        {"field": "channel", "label": "通道", "type": "text"},
                        {"field": "device", "label": "设备", "type": "text"},
                        {"field": "last_cycle", "label": "末次圈", "type": "text"},
                        {"field": "status", "label": "末次状态", "type": "text"},
                        {"field": "last_peak_a", "label": "末圈峰值 A", "format": "number"},
                        {"field": "fault_current_a", "label": "故障时电流 A", "format": "number"},
                        {"field": "post_fault_completed_cycles", "label": "故障后完成圈", "format": "number"},
                        {"field": "conclusion", "label": "结论", "type": "text"},
                    ],
                },
                {
                    "id": "root_causes_table",
                    "title": "根因证据分级",
                    "subtitle": "将确定事实、架构根因和待补证因素分开。",
                    "dataset": "root_causes",
                    "sourceId": "root_causes_sql",
                    "columns": [
                        {"field": "level", "label": "层级", "type": "text"},
                        {"field": "assessment", "label": "判断", "type": "text"},
                        {"field": "evidence", "label": "证据", "type": "text"},
                        {"field": "confidence", "label": "置信度", "type": "text"},
                    ],
                },
                {
                    "id": "software_issues_table",
                    "title": "软件问题清单",
                    "subtitle": "包含主故障链、证据链和报警语义问题。",
                    "dataset": "software_issues",
                    "sourceId": "software_issues_sql",
                    "columns": [
                        {"field": "id", "label": "编号", "type": "text"},
                        {"field": "problem", "label": "问题", "type": "text"},
                        {"field": "impact", "label": "影响", "type": "text"},
                        {"field": "code_location", "label": "位置", "type": "text"},
                    ],
                },
                {
                    "id": "actions_table",
                    "title": "落地整改方案",
                    "subtitle": "P0 先解除实时链路与持久化耦合，再谈恢复与优化。",
                    "dataset": "actions",
                    "sourceId": "actions_sql",
                    "columns": [
                        {"field": "priority", "label": "优先级", "type": "text"},
                        {"field": "action", "label": "动作", "type": "text"},
                        {"field": "implementation", "label": "实现要点", "type": "text"},
                        {"field": "acceptance", "label": "验收", "type": "text"},
                    ],
                },
                {
                    "id": "validation_table",
                    "title": "验证与上线闸门",
                    "subtitle": "必须同时覆盖吞吐、故障注入、快照和报警语义。",
                    "dataset": "validation",
                    "sourceId": "validation_sql",
                    "columns": [
                        {"field": "test", "label": "测试", "type": "text"},
                        {"field": "setup", "label": "方法", "type": "text"},
                        {"field": "pass_gate", "label": "通过条件", "type": "text"},
                    ],
                },
            ],
            "sources": manifest_sources,
            "blocks": [
                {
                    "id": "title",
                    "type": "markdown",
                    "body": "# 10358-029：V2.8.0 Dev1 双卡钳报警技术复盘",
                },
                {
                    "id": "technical_summary",
                    "type": "markdown",
                    "sourceId": "incident_analysis",
                    "body": (
                        "## 技术结论\n\n"
                        "**这不是 EPB4、EPB5 两个卡钳同时发生硬件故障，而是一项 Dev1 设备级后台消费故障。** "
                        "21:36:16.203，Dev1 的 64 批后台处理队列达到容量，V2.8.0 新增的“禁止静默丢弃”保护按设计触发，随后 EPB4 被选为主报警通道，EPB5 被联锁停机。\n\n"
                        "直接原因已经确定为 **BackgroundQueueFull**。进一步代码审查表明，后台 ProcessLoop 仍同步串联滤波、峰值、写盘和 UI，其中落盘路径逐样本写记录并逐样本更新 SQLite，是当前最主要的确定性性能热点。不过，本次故障恰好发生在无活动圈窗口，现有快照逻辑直接返回，加上 timing CSV 过滤错误，导致没有留下故障瞬间的阶段耗时和线程栈；因此不能把某一次具体停顿武断归结为单一 SQLite 调用。"
                    ),
                },
                {"id": "headline_metrics", "type": "metric-strip", "cardIds": ["queue_capacity", "backlog_lower_bound", "paired_lag", "dev2_continuation"]},
                {
                    "id": "safety_assessment",
                    "type": "markdown",
                    "sourceId": "incident_analysis",
                    "body": (
                        "## 安全保护是有效的，但告警表达需要修正\n\n"
                        "队列达到上限后没有静默丢批，Dev1 的 EPB4/5 被安全断电，故障时电流仅约 0.007 A 和 0.008 A；Dev2 四路继续运行。"
                        "因此应保留这道硬停机保护，同时把界面从“两个卡钳报警 + 压力不足”改为“一个 Dev1 后台队列故障，EPB4/5 受影响，EPB5 为联锁停机”。"
                    ),
                },
                {"id": "lag_distribution", "type": "chart", "chartId": "lag_distribution_chart", "layout": "half"},
                {"id": "lag_scatter", "type": "chart", "chartId": "lag_depth_scatter", "layout": "half"},
                {
                    "id": "lag_interpretation",
                    "type": "markdown",
                    "sourceId": "incident_analysis",
                    "body": (
                        "两设备的普通积压告警在时间和幅度上高度同步：Dev1 41 次告警中有 40 次在 50 ms 内匹配到 Dev2 告警。"
                        "这说明“每设备一个队列/一个 ProcessLoop”尚未消除共同资源影响；日志也显示积压并非 21:36 才突然出现，而是从 20:58 起持续反复。"
                    ),
                },
                {"id": "timeline", "type": "table", "tableId": "timeline_table"},
                {"id": "channel_outcomes", "type": "table", "tableId": "channel_outcomes_table"},
                {
                    "id": "last_cycle_quality",
                    "type": "markdown",
                    "sourceId": "incident_analysis",
                    "body": (
                        "## 报警前卡钳数据没有异常\n\n"
                        "EPB4 第 748 圈和 EPB5 第 671 圈均为 completed：分别 18,840 与 18,280 个样本，样本索引连续，时间戳严格递增且全部为 0.5 ms 间隔；峰值 14.877 A 与 15.121 A，压力约 70 bar。"
                        "它们在 21:36:10 前已经安全结束，下一圈尚未开始，因此不能用卡钳过流、堵转或液压不足解释 21:36:16 的报警。"
                    ),
                },
                {
                    "id": "root_cause_narrative",
                    "type": "markdown",
                    "sourceId": "code_review",
                    "body": (
                        "## 根因链：生产者正常，消费端被非实时工作拖住\n\n"
                        "DAQ 回调在 `OnAiBatch` 中持续生产批次；`EnqueueForProcessing` 只有在计数已达容量时才发布 BackgroundQueueFull。后台线程没有记录未处理异常，因此本次不是线程崩溃，而是消费能力不足或同步阻塞。"
                        "在 `ProcessLoop` 中，`OnDiskBatch` 是同步调用；其上层逐通道调用 `WriteBatch`，而 `WriteBatch` 再逐样本调用 `WriteSample`。正式圈内每个样本都会创建 SQLite 命令并更新 sample_count。六路 2 kHz 同时活动时，理论上可达到约 12,000 次 UPDATE/s，并且两个设备共享一个 SQLiteConnection。"
                    ),
                },
                {"id": "root_causes", "type": "table", "tableId": "root_causes_table"},
                {"id": "software_issues", "type": "table", "tableId": "software_issues_table"},
                {
                    "id": "do_not_do",
                    "type": "markdown",
                    "sourceId": "code_review",
                    "body": (
                        "## 不建议的临时处理\n\n"
                        "不要只把队列容量从 64 调大。当前每批约 10 ms，64 批已经允许约 640 ms 的滞后；继续扩容只会延后报警、积累更多过期数据并掩盖消费端吞吐问题。"
                        "也不要把 BackgroundQueueFull 一律当作 NI 采集卡断流并直接重启 DAQ：队列能填满恰恰说明回调仍在生产，若慢消费端未修复，重启后仍会再次填满。"
                    ),
                },
                {"id": "actions", "type": "table", "tableId": "actions_table"},
                {"id": "validation", "type": "table", "tableId": "validation_table"},
                {
                    "id": "methodology_limits",
                    "type": "markdown",
                    "sourceId": "incident_analysis",
                    "body": (
                        "## 范围、方法与限制\n\n"
                        "本报告使用 V2.8.0 运行日志、warning/error/ui-info、SQLite 周期索引、EPB4/5 最新 CSV/BIN 清单和当前 HEAD 90f5332 的采集/落盘代码。积压统计仅覆盖本次 20:57:10.863 启动后的限频告警，因此 41/62 次是“可见告警次数”，不是全部慢批次数。"
                        "快照状态标记为 partial：本次无设备级 IncidentSnapshot，无法恢复 21:36:16 前 60 s 的 Callback、Processing、GC、线程池和分阶段耗时。根因到“消费链阻塞”是确定的；把具体一次停顿唯一归因于 SQLite、磁盘、锁或线程池，仍需按整改方案补齐阶段计时后再次复现。"
                    ),
                },
                {
                    "id": "further_questions",
                    "type": "markdown",
                    "body": (
                        "## 需要项目决策的两个问题\n\n"
                        "1. BackgroundQueueFull 后是否允许自动恢复采集，还是必须人工复位？建议默认安全锁存，只有在旧 generation 已清空且连续新鲜批次达标后开放人工复位。\n"
                        "2. 原始/工程数据允许多大持久化延迟和最多丢失多少？这个边界决定独立写盘队列容量、落盘告警阈值以及磁盘故障时的降级策略。"
                    ),
                },
            ],
        },
        "snapshot": {
            "version": 1,
            "generatedAt": GENERATED_AT,
            "status": "partial",
            "datasets": {
                "headline_metrics": [
                    {
                        "queue_capacity_batches": sampling["queue_capacity_batches"],
                        "queue_time_lower_bound_ms": sampling["queue_time_coverage_lower_bound_ms"],
                        "paired_fraction": paired["dev1_fraction"],
                        "dev2_completed_cycles": dev2_completed,
                    }
                ],
                "lag_summary": lag_summary,
                "lag_events": lag_events,
                "timeline": timeline,
                "channel_outcomes": channel_outcomes,
                "root_causes": root_causes,
                "software_issues": software_issues,
                "actions": actions,
                "validation": validation,
            },
            "accessIssues": [
                {
                    "id": "incident_diagnostics_missing",
                    "dataset": "incident_stage_timing",
                    "message": "21:36 queue-full occurred without an active Dev1 cycle, so the current alarm snapshot path returned before exporting DAQ timing/runtime diagnostics.",
                }
            ],
        },
        "sources": [analysis_source, code_source, *dataset_sources],
    }

    write_sqlite_tables(artifact["snapshot"]["datasets"])
    OUTPUT_PATH.write_text(json.dumps(artifact, ensure_ascii=False, indent=2), encoding="utf-8")
    print(OUTPUT_PATH)


if __name__ == "__main__":
    main()
