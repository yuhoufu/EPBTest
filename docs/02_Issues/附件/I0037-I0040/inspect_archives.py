"""Read-only investigation of the four 2026-09-05 EPB snapshots.

Standard library only. Source archives are never modified. JSON outputs contain
selected diagnostic fields, not authentication nonces or complete runtime logs.
Run with Python 3.11+; optional first argument overrides the backup parent path.
"""
import collections
import datetime as dt
import hashlib
import json
from pathlib import Path
import re
import sqlite3
import sys

BASE = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(r"D:\EPB_Data\Backups")
OUT = Path(__file__).resolve().parent
TZ = dt.timezone(dt.timedelta(hours=8))


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def local(value):
    if not value:
        return None
    if isinstance(value, int):
        return (dt.datetime(1, 1, 1, tzinfo=dt.timezone.utc)
                + dt.timedelta(microseconds=value // 10)).astimezone(TZ).isoformat()
    return dt.datetime.fromisoformat(value.replace("Z", "+00:00")).astimezone(TZ).isoformat()


def pick(value, keys):
    return {key: value.get(key) for key in keys.split()}


def fingerprint(path, root):
    with path.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    return {"file": path.relative_to(root).as_posix(), "bytes": path.stat().st_size,
            "sha256": digest}


def group_events(events, fields):
    groups = {}
    for event in events:
        key = tuple(str(event.get(f, "")) for f in fields)
        if key not in groups:
            groups[key] = {**{f: event.get(f) for f in fields}, "count": 0,
                           "first": event, "last": event}
        group = groups[key]
        group["count"] += 1
        if event["time"] < group["first"]["time"]:
            group["first"] = event
        if event["time"] > group["last"]["time"]:
            group["last"] = event
    return sorted(groups.values(), key=lambda g: g["first"]["time"])


def log_category(line):
    patterns = {
        "Startup": "\t启动构建身份\t", "SequenceGap": "写盘连续 10000ms 未恢复",
        "LivenessTrip": "DAQ_LIVENESS Result=Trip", "SlotFailure": "ERROR\t周期屏障",
        "PermitMissing": "ERROR\tEPB\tEPB[", "OwnerRegistration": "registration failed:",
        "WatchdogTransport": "WatchdogTransportLost", "DisposedOwner": "Error=无法访问已释放的对象",
        "HydraulicStale": "ERROR\t液压协调\t", "StateMismatch": "DaqRecoveryTerminalWithoutRejoin",
    }
    for category, pattern in patterns.items():
        if pattern in line:
            return category
    return None


result = {"method": "UTF-8; logs exact-line dedup; sidecar SessionId+EventId dedup; SQLite read-only immutable; UTC+8",
          "archives": [], "log_events": [], "incidents": [], "exits": [], "dumps": [], "parse_errors": []}
seen_logs, seen_sidecar = set(), {}
for seq in (37, 38, 39, 40):
    matches = list(BASE.glob(f"*_I00{seq}_*"))
    roots = [p for p in matches if p.is_dir()]
    if len(roots) != 1:
        raise RuntimeError(f"Expected one directory for I00{seq}, found {roots}")
    root = roots[0]
    manifest = read_json(root / "backup-manifest.json")
    ident = read_json(root / "Config/runtime-build-identity.json")
    record = {"sequence": seq, "path": str(root), "manifest": pick(manifest,
              "backup_id previous_backup_id base_backup_id backup_kind captured_utc snapshot_verified"),
              "identity": pick(ident, "productVersion processId processBitness executableSha256 gitCommit configSha256"),
              "files": [], "logs": {}}
    for rel in ("backup-manifest.json", "Config/runtime-build-identity.json", "index.db",
                "Recovery/unattended-run-checkpoint.json", "Config/EpbProgramSafetyEffective.xml"):
        record["files"].append(fingerprint(root / rel, root))
    for log in ("error.log", "warning.log", "run.log", "ui-info.log"):
        path = root / "log" / log
        record["files"].append(fingerprint(path, root))
        lines = path.read_text(encoding="utf-8-sig").splitlines()
        record["logs"][log] = {"lines": len(lines), "first": lines[0][:23], "last": lines[-1][:23]}
        for number, line in enumerate(lines, 1):
            category = log_category(line)
            if category and line not in seen_logs:
                seen_logs.add(line)
                result["log_events"].append({"archive": seq, "file": "log/" + log,
                    "line": number, "time": line[:23], "category": category,
                    "text": line.split(" | ")[0]})
    connection = sqlite3.connect((root / "index.db").as_uri() + "?mode=ro&immutable=1", uri=True)
    connection.row_factory = sqlite3.Row
    record["sqlite_check"] = connection.execute("PRAGMA quick_check").fetchone()[0]
    record["db_total"] = connection.execute("SELECT count(*) FROM epb_cycles").fetchone()[0]
    record["db_status"] = [dict(row) for row in connection.execute(
        "SELECT status,count(*) AS rows,sum(mechanical_completed) AS mechanical FROM epb_cycles GROUP BY status")]
    record["db_channels"] = [dict(row) for row in connection.execute(
        "SELECT epb_id,max(cycle_number) AS last_formal_number,sum(mechanical_completed) AS mechanical,"
        "sum(case when status='completed' then 1 else 0 end) AS formal_completed,"
        "sum(case when status='learning_completed' then 1 else 0 end) AS learning_completed "
        "FROM epb_cycles GROUP BY epb_id")]
    record["running_rows"] = [dict(row) for row in connection.execute("SELECT * FROM epb_cycles WHERE status='running'")]
    record["checkpoint"] = pick(read_json(root / "Recovery/unattended-run-checkpoint.json"),
        "Armed RunId RunEpoch LastReason UpdatedUtc RemainingFormalCycles")
    if seq == 39:
        record["quiet_window_new_cycles"] = connection.execute(
            "SELECT count(*) FROM epb_cycles WHERE start_time >= '2026-09-05T19:24:35' "
            "AND start_time < '2026-09-05T19:31:12'").fetchone()[0]
        all_lines = (root / "log/run.log").read_text(encoding="utf-8-sig").splitlines()
        window = [line for line in all_lines if "19:24:35" <= line[11:19] < "19:31:12"]
        record["quiet_window_log_counts"] = {key: sum(key in line for line in window)
            for key in ("\tEPB-DO\t", "学习阶段启动", "FieldMetric STATE", "FieldMetric DAQ_RECOVERY", "HostRuntime")}
    connection.close()
    for path in sorted((root / "IncidentSnapshots").rglob("incident.json")):
        data = read_json(path)
        result["incidents"].append({"archive": seq, "file": path.relative_to(root).as_posix(),
            **pick(data, "capturedUtc device correlationId runId runEpoch faultCode reason result queueDepth "
                   "oldestBatchAgeMs beforeCallbackAgeMs beforeLastCallbackGapMs afterCallbackAgeMs "
                   "validationPhase firstVerifiedSequence lastVerifiedSequence affectedChannels")})
    for path in sorted((root / "WatchdogSessions").glob("*.application-exit.json")):
        data = read_json(path)
        result["exits"].append({"archive": seq, "file": path.relative_to(root).as_posix(),
            **pick(data, "SessionId State MainProcessId Detail StopSafetyTransactionId MotorsOff PowerOff "
                   "PressureSafe PersistenceDrained LogicalQuiescent DataContinuityVerified"),
            **{key: local(data.get(key)) for key in ("RequestedUtcTicks", "HardDeadlineUtcTicks", "UpdatedUtcTicks")}})
    for path in sorted((root / "WatchdogSessions").glob("*sidecar-events*.jsonl")):
        for number, line in enumerate(path.read_text(encoding="utf-8-sig").splitlines(), 1):
            try:
                data = json.loads(line)
            except json.JSONDecodeError:
                result["parse_errors"].append({"archive": seq, "file": path.name, "line": number})
                continue
            key = (data.get("SessionId"), data.get("EventId"))
            # Include rotated direct sidecar files; exclude LegacySealed and mirrors.
            if key not in seen_sidecar:
                seen_sidecar[key] = {"archive": seq, "file": path.relative_to(root).as_posix(),
                    "line": number, "time": local(data["Utc"]), **pick(data,
                    "SessionId EventType State Reason ProcessId RelaunchProcessId RelaunchState ManualStopRequested")}
    for path in (root / "WatchdogSessions/dumps").rglob("*.dmp"):
        result["dumps"].append({"archive": seq, "file": path.relative_to(root).as_posix(), "bytes": path.stat().st_size})
    result["archives"].append(record)

result["log_events"].sort(key=lambda row: row["time"])
events = list(seen_sidecar.values())
result["sidecar_unique_events"] = len(events)
result["sidecar_types"] = dict(collections.Counter(row["EventType"] for row in events))
result["sidecar_groups"] = group_events(events, ("SessionId", "EventType", "Reason"))
result["sidecar_session_summary"] = []
for session in sorted({row["SessionId"] for row in events}):
    subset = [row for row in events if row["SessionId"] == session]
    counts = collections.Counter(row["EventType"] for row in subset)
    result["sidecar_session_summary"].append({"session": session, "first": min(row["time"] for row in subset),
        "last": max(row["time"] for row in subset), "events": len(subset),
        "types": dict(counts), "pids": sorted({row["ProcessId"] for row in subset})})
(OUT / "evidence-summary.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
print(json.dumps({"archives": [{"seq": a["sequence"], "sqlite": a["sqlite_check"], "rows": a["db_total"]}
                 for a in result["archives"]], "sidecar_events": len(events), "log_events": len(result["log_events"]),
                 "incidents": len(result["incidents"]), "exits": len(result["exits"]),
                 "parse_errors": result["parse_errors"]}, ensure_ascii=False))
