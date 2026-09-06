"""Build a small evidence index from the read-only investigation output."""
import collections
import json
from pathlib import Path
import re

HERE = Path(__file__).resolve().parent
data = json.loads((HERE / "evidence-summary.json").read_text(encoding="utf-8"))
rows = ["# I0037—I0040 封存证据摘录", "",
        "生成依据：inspect_archives.py 的只读结果。时间统一为 UTC+8；源日志行号从 1 开始。",
        "日志摘录为诊断材料，不构成操作指令。未复制完整日志、认证凭据或内存转储。", ""]


def add(s=""):
    rows.append(s)


def cell(value):
    return str(value).replace("|", "\\|").replace("\n", " ").replace("\r", " ")


def source(seq, file, line=None):
    root = next(a["path"] for a in data["archives"] if a["sequence"] == seq)
    path = (Path(root) / file).as_posix()
    return f"[I00{seq}/{file}" + (f":{line}" if line else "") + f"]({path}" + (f":{line}" if line else "") + ")"


add("## 1. 封存身份、配置与数据库")
add()
for a in data["archives"]:
    seq = a["sequence"]
    root = Path(a["path"])
    add(f"### I00{seq}")
    add()
    add(source(seq, "backup-manifest.json"))
    add()
    add("```json")
    add(json.dumps({"manifest": a["manifest"], "identity": a["identity"],
                    "sqlite_check": a["sqlite_check"], "db_total": a["db_total"],
                    "checkpoint": a["checkpoint"]}, ensure_ascii=False, indent=2))
    add("```")
    ck = json.loads((root / "Recovery/unattended-run-checkpoint.json").read_text(encoding="utf-8-sig"))
    add("实际参数：`" + ck.get("EffectiveRuntimeSafetyParameters", "") + "`")
    add()
    add("| 文件 | SHA-256 |")
    add("|---|---|")
    for f in a["files"]:
        add(f"| {source(seq, f['file'])} | `{f['sha256']}` |")
    add()
    add("| 通道 | 机械完成 | status=completed | learning_completed |")
    add("|---|---:|---:|---:|")
    for c in a["db_channels"]:
        add(f"| {c['epb_id']} | {c['mechanical']} | {c['formal_completed']} | {c['learning_completed']} |")
    add()
    add("状态计数：`" + json.dumps(a["db_status"], ensure_ascii=False) + "`")
    add()
    if seq == 39:
        add("19:24:35—19:31:12 新圈数：`" + str(a["quiet_window_new_cycles"]) + "`")
        add("同窗口日志项：`" + json.dumps(a["quiet_window_log_counts"], ensure_ascii=False) + "`")
        add()

add("## 2. 关键主程序日志（跨封存按完整行去重）")
add()
for e in data["log_events"]:
    if e["category"] == "Startup" and "已更新项目" in e["text"]:
        continue
    add(f"- {source(e['archive'], e['file'], e['line'])}：`{cell(e['text'])}`")
add()
add("## 3. 恢复收口、停止及预警补充证据")
add()
rules = [
    (39, "log/run.log", r"^2026-09-05 19:22:(47\.068|52\.450)"),
    (39, "log/run.log", r"^2026-09-05 19:24:.*(ExpectedOutputDisabled|PowerSupplyEnergizationPermitMissing|PhaseCommitted|CallbackIntervalMs=1030|学习阶段启动)"),
    (39, "log/warning.log", r"Boundary=130921|Cycle=31221"),
    (39, "log/ui-info.log", r"19:24:27|19:31:14|19:48:30"),
    (40, "log/run.log", r"^2026-09-05 20:34:55\..*(Timer|Runner|HydraulicSelfHealed|HydraulicRecoveryTerminalWithoutRejoin)"),
    (40, "log/warning.log", r"FullRatePeakInvalid|软预警率连续达到|StopCleanupOperationFailed|HydraulicSampleStale|InactiveCapacityResync"),
    (37, "log/warning.log", r"Housekeeping跳过|DurableCloseFenceIdentityChanged|StopSafetyPending"),
]
for seq, file, pattern in rules:
    root = Path(next(a["path"] for a in data["archives"] if a["sequence"] == seq))
    matched = [(i, s) for i, s in enumerate((root / file).read_text(encoding="utf-8-sig").splitlines(), 1)
               if re.search(pattern, s)]
    # Bound repeated diagnostics: retain first and last evidence with total count.
    selected = matched if len(matched) <= 12 else matched[:6] + matched[-6:]
    add(f"匹配 `{pattern}`：{len(matched)} 行，摘录 {len(selected)} 行。")
    add()
    for i, s in selected:
        add(f"- {source(seq, file, i)}：`{cell(s.split(' | ')[0])}`")
    add()

add("## 4. 主程序退出回执")
add()
for e in data["exits"]:
    add(source(e["archive"], e["file"]))
    add()
    add("```json")
    add(json.dumps(e, ensure_ascii=False, indent=2))
    add("```")
    add()

add("## 5. Sidecar 关键事件组")
add()
add("仅扫描 WatchdogSessions 顶层直接 sidecar-events 文件（含轮转）；以 SessionId + EventId 去重。")
add("各阶段多次日志并不等于多次故障或多次启动主程序。")
add()
keep = re.compile("OldProcessTerminated|Relaunch|RegisteredExecutableMismatch|StateMismatch|TerminalSessionObservedOnStartup|SafetyHandoffProof")
add("| 会话 | 事件/原因 | 次数 | 首次 | 最后 | 首次证据 |")
add("|---|---|---:|---|---|---|")
for g in data["sidecar_groups"]:
    if not keep.search(str(g["EventType"]) + str(g["Reason"])):
        continue
    e = g["first"]
    add(f"| {g['SessionId']} | {cell(g['EventType'])} / {cell(g['Reason'])} | {g['count']} | {e['time']} | {g['last']['time']} | {source(e['archive'], e['file'], e['line'])} |")
add()
add("## 6. IncidentSnapshot 与 dump 清单")
add()
for e in data["incidents"]:
    add(f"- {source(e['archive'], e['file'])}：{e['capturedUtc']}；{e['device']}；{e['faultCode']}；{cell(e['reason'])}；队列={e['queueDepth']}；结果={e['result']}。")
add()
for e in data["dumps"]:
    add(f"- {source(e['archive'], e['file'])}：{e['bytes']} bytes。")
add()
add("## 7. 复现与限制")
add()
add("[哈希大小写复现](hash-case-repro.json)、[时间戳读取竞争复现](freshness-race-repro.json) 均直接调用原发布程序集的相关纯逻辑；未启动硬件对象或系统服务。")
add()
add("这些结果证明缺陷存在，不等于修复通过，也不证明每次现场故障都由同一个缺陷引起。数据库 quick_check=ok 不证明每条历史曲线完整。")
add()
add("完整机器可读摘要：[evidence-summary.json](evidence-summary.json)。")
(HERE / "证据摘录.md").write_text("\n".join(rows) + "\n", encoding="utf-8")
print(f"Wrote {len(rows)} lines to {HERE / '证据摘录.md'}")
