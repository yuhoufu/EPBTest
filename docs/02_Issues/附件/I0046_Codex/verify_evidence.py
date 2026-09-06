"""Read-only I0046 evidence extraction. Python standard library only.

Usage: python verify_evidence.py BACKUP_DIRECTORY --repo REPO --output result.json
Only --output is written; source logs and databases are never modified.
"""
import argparse
import collections
import csv
import hashlib
import json
import math
from pathlib import Path
import re
import subprocess
import xml.etree.ElementTree as ET


def distribution(values):
    values = sorted(float(v) for v in values)
    if not values:
        return {"n": 0}
    return {
        "n": len(values), "min": values[0], "max": values[-1],
        "p50": values[math.ceil(len(values) * .50) - 1],
        "p95": values[math.ceil(len(values) * .95) - 1],
        "p99": values[math.ceil(len(values) * .99) - 1],
        "zero_count": values.count(0),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("backup", type=Path)
    parser.add_argument("--repo", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    root = args.backup.resolve()
    output = args.output.resolve()
    if output == root or root in output.parents:
        raise ValueError("Output must be outside the evidence archive")
    sources = {}

    def read(relative):
        data = (root / relative).read_bytes()
        sources[str(relative).replace('\\', '/')] = hashlib.sha256(data).hexdigest()
        return data.decode("utf-8-sig")

    def git(*argv):
        return subprocess.check_output(["git", "-C", str(args.repo), *argv])

    logs = {name: read(Path("log") / (name + ".log"))
            for name in ("run", "warning", "error", "ui-info")}
    rows = []
    for line_no, line in enumerate(logs["run"].splitlines(), 1):
        if "FieldMetric DAQ Phase=" in line:
            row = dict(re.findall(r"(\w+)=([^\s]+)", line))
            row.update(line=line_no, time=line[:23])
            rows.append(row)
    metrics = ("PersistenceDepth", "PersistenceOldestMs", "CallbackAgeMs",
               "ControlProcessedAgeMs")
    windows = {}
    for label, lo in (("resumed", "13:31:12.754"), ("formal", "13:33:57.106")):
        windows[label] = {"start_local": lo, "end_local": "13:47:10.092", "devices": {}}
        for device in ("Dev1", "Dev2"):
            selected = [r for r in rows if lo <= r["time"][11:] <= "13:47:10.092"
                        and r["Device"] == device and r["Phase"] == "Running"]
            assert selected, (label, device, "No samples")
            windows[label]["devices"][device] = {
                key: distribution(r[key] for r in selected) for key in metrics}
    incident_rows = [{k: r[k] for k in ("line", "time", "Device", "Persisted", "Accepted",
                                      "PersistenceDepth", "PersistenceOldestMs", "ProcessingDepth")}
                     for r in rows if "13:47:09" <= r["time"][11:] <= "13:47:18"
                     or "13:30:15" <= r["time"][11:] <= "13:30:25"]
    timings = {}
    for path in sorted(root.rglob("daq_timing.csv")):
        records = list(csv.DictReader(read(path.relative_to(root)).splitlines()))
        persist = [r for r in records if r["Kind"] == "Persistence"]
        baseline = [r for r in persist if "05:47:07" <= r["TimestampUtc"][11:23] < "05:47:10.5"]
        timings[str(path.relative_to(root)).replace('\\', '/')] = {
            "kinds": dict(collections.Counter(r["Kind"] for r in records)),
            "baseline_utc": "[05:47:07,05:47:10.5)",
            "baseline_write_call_ms": distribution(r["ProcessingMs"] for r in baseline),
            "slowest_writes": [{k: r[k] for k in ("TimestampUtc", "Device", "ProcessingMs",
                                                    "PersistenceWaitMs", "QueueDepth")}
                               for r in sorted(persist, key=lambda r: float(r["ProcessingMs"]),
                                               reverse=True)[:6]],
            "nonzero_sqlite_or_ring_fields": sum(float(r["SqliteMs"]) != 0 or
                                                  float(r["RingWriteMs"]) != 0 for r in persist),
        }
    event_files = {}
    for path in sorted((root / "WatchdogSessions").glob("*events.jsonl")):
        events = []
        for line_no, line in enumerate(read(path.relative_to(root)).splitlines(), 1):
            event = json.loads(line)
            if event["Utc"] >= "2026-09-06T05:47:00" or event["EventType"] == "SidecarHelperLaunched":
                selected = {"line": line_no, **{k: event.get(k) for k in
                    ("Utc", "EventType", "Reason", "ManualStopRequested", "RelaunchState")}}
                # Helper role is proven by PID/event type; do not export its nonce.
                if event["EventType"] == "SidecarHelperLaunched":
                    selected["Reason"] = selected["Reason"].split(";Nonce=", 1)[0]
                events.append(selected)
        event_files[path.name] = events
    configs = {}
    for rev in ("6ed4d47", "3a954943", "c67bbff"):
        raw = git("show", rev + ":MTTfTest/App.config")
        settings = {e.attrib["key"]: e.attrib["value"] for e in ET.fromstring(raw).find("appSettings")
                    if e.tag == "add"}
        configs[rev] = {"count": len(settings), "values": settings,
                        "sha256_crlf": hashlib.sha256(raw.replace(b"\r\n", b"\n").replace(b"\n", b"\r\n")).hexdigest()}
    old, new = configs["6ed4d47"]["values"], configs["3a954943"]["values"]
    changed = {k: {"old": old.get(k), "deployed": new.get(k)} for k in sorted(old.keys() | new.keys())
               if old.get(k) != new.get(k)}
    for config in configs.values():
        del config["values"]
    warning_count = sum("持久化延迟。" in line and "13:31:12.754" <= line[11:23] <= "13:47:10.700"
                        for line in logs["warning"].splitlines())
    result = {
        "archive": str(root), "head": git("rev-parse", "HEAD").decode().strip(),
        "method": "nearest-rank percentiles of recorded snapshots, not per-batch or time-weighted; local UTC+08",
        "source_sha256": sources, "windows": windows, "incident_rows": incident_rows,
        "timings": timings, "events": event_files, "config_comparison": configs,
        "changed_appsettings": changed,
        "commits_after_deployed": git("log", "--oneline", "3a954943..HEAD").decode("utf-8").splitlines(),
        "warning_lag_count_second_run_before_pause": warning_count,
        "run_liveness_records": [{"line": i, "text": line} for i, line in enumerate(logs["run"].splitlines(), 1)
                                 if "FieldMetric DAQ_LIVENESS" in line],
        "log_token_counts": {token: {name: text.count(token) for name, text in logs.items()}
                             for token in ("DAQ_FRESHNESS_SAFETY_CUTOFF", "DaqReadDispatchOverflow")},
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(str(output))


if __name__ == "__main__":
    main()
