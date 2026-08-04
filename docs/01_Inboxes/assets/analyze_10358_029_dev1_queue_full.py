#!/usr/bin/env python3
"""Reproduce the 2026-08-04 21:36 Dev1 queue-full incident metrics.

The source dataset is opened read-only.  The script writes one JSON summary next
to itself unless an explicit output path is supplied.
"""

from __future__ import annotations

import csv
import json
import math
import re
import sqlite3
import sys
from collections import defaultdict
from datetime import datetime
from pathlib import Path


DATA_ROOT = Path(r"D:\EPB_Data\10358-029_2157_backup")
RUN_START = datetime.fromisoformat("2026-08-04 20:57:10.863")
FAULT_TIME = datetime.fromisoformat("2026-08-04 21:36:16.203")
QUEUE_CAPACITY_BATCHES = 64

LOG_TS = "%Y-%m-%d %H:%M:%S.%f"
LAG_RE = re.compile(
    r"^(?P<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}).*"
    r"Device=(?P<device>Dev[12]) QueueDepth=(?P<depth>\d+) "
    r"OldestBatchAge=(?P<age>[0-9.]+)ms"
)
AI_START_RE = re.compile(r"Fs=(?P<fs>\d+)Hz N=(?P<n>\d+)")


def percentile(values: list[float], p: float) -> float | None:
    if not values:
        return None
    ordered = sorted(values)
    if len(ordered) == 1:
        return ordered[0]
    pos = (len(ordered) - 1) * p
    lo = math.floor(pos)
    hi = math.ceil(pos)
    if lo == hi:
        return ordered[lo]
    return ordered[lo] + (ordered[hi] - ordered[lo]) * (pos - lo)


def summarize(values: list[float]) -> dict[str, float | int | None]:
    return {
        "count": len(values),
        "median": round(percentile(values, 0.50), 3) if values else None,
        "p95": round(percentile(values, 0.95), 3) if values else None,
        "max": round(max(values), 3) if values else None,
    }


def parse_sampling_config(run_log: Path) -> dict[str, float | int]:
    for line in run_log.read_text(encoding="utf-8-sig", errors="replace").splitlines():
        if "AI 采集启动" not in line:
            continue
        ts = datetime.strptime(line[:23], LOG_TS)
        if ts < RUN_START:
            continue
        match = AI_START_RE.search(line)
        if match:
            fs = int(match.group("fs"))
            batch_size = int(match.group("n"))
            interval_ms = 1000.0 * batch_size / fs
            return {
                "sample_rate_hz": fs,
                "batch_size_samples": batch_size,
                "expected_batch_interval_ms": interval_ms,
                "queue_capacity_batches": QUEUE_CAPACITY_BATCHES,
                "queue_time_coverage_lower_bound_ms": interval_ms * QUEUE_CAPACITY_BATCHES,
            }
    raise RuntimeError("Current-run AI sampling configuration was not found")


def parse_lag_events(warning_log: Path) -> list[dict[str, object]]:
    events: list[dict[str, object]] = []
    for line in warning_log.read_text(encoding="utf-8-sig", errors="replace").splitlines():
        match = LAG_RE.search(line)
        if not match:
            continue
        ts = datetime.strptime(match.group("ts"), LOG_TS)
        if ts < RUN_START:
            continue
        events.append(
            {
                "timestamp": ts,
                "device": match.group("device"),
                "queue_depth_batches": int(match.group("depth")),
                "oldest_batch_age_ms": float(match.group("age")),
            }
        )
    return events


def pair_lag_events(events: list[dict[str, object]], window_ms: float = 50.0) -> list[dict[str, object]]:
    dev1 = [event for event in events if event["device"] == "Dev1"]
    dev2 = [event for event in events if event["device"] == "Dev2"]
    used: set[int] = set()
    pairs: list[dict[str, object]] = []
    for left in dev1:
        candidates = []
        for index, right in enumerate(dev2):
            if index in used:
                continue
            delta_ms = abs((left["timestamp"] - right["timestamp"]).total_seconds() * 1000.0)
            if delta_ms <= window_ms:
                candidates.append((delta_ms, index, right))
        if not candidates:
            continue
        delta_ms, index, right = min(candidates, key=lambda item: item[0])
        used.add(index)
        pairs.append(
            {
                "dev1_timestamp": left["timestamp"].isoformat(sep=" ", timespec="milliseconds"),
                "dev2_timestamp": right["timestamp"].isoformat(sep=" ", timespec="milliseconds"),
                "delta_ms": round(delta_ms, 3),
                "dev1_age_ms": left["oldest_batch_age_ms"],
                "dev2_age_ms": right["oldest_batch_age_ms"],
            }
        )
    return pairs


def read_cycle_csv(path: Path) -> dict[str, object]:
    row_count = 0
    first_ts = None
    last_ts = None
    previous_ts = None
    intervals_ms: list[float] = []
    currents: list[float] = []
    pressures: list[float] = []
    monotonic = True
    contiguous_indices = True
    with path.open("r", encoding="utf-8-sig", newline="") as handle:
        for expected_index, row in enumerate(csv.DictReader(handle)):
            ts = datetime.fromisoformat(row["Timestamp"])
            index = int(row["SampleIndex"])
            current = float(row["EpbCurrent"])
            pressure = float(row["GroupPressure"])
            if first_ts is None:
                first_ts = ts
            if previous_ts is not None:
                delta_ms = (ts - previous_ts).total_seconds() * 1000.0
                intervals_ms.append(delta_ms)
                if delta_ms <= 0:
                    monotonic = False
            if index != expected_index:
                contiguous_indices = False
            previous_ts = ts
            last_ts = ts
            currents.append(current)
            pressures.append(pressure)
            row_count += 1
    return {
        "file": path.name,
        "rows": row_count,
        "first_timestamp": first_ts.isoformat(sep=" ", timespec="microseconds"),
        "last_timestamp": last_ts.isoformat(sep=" ", timespec="microseconds"),
        "duration_s": round((last_ts - first_ts).total_seconds(), 6),
        "sample_indices_contiguous": contiguous_indices,
        "timestamps_strictly_increasing": monotonic,
        "interval_ms": summarize(intervals_ms),
        "max_abs_current_a": round(max(abs(value) for value in currents), 3),
        "max_pressure_bar": round(max(pressures), 3),
    }


def query_cycles(db_path: Path) -> dict[str, object]:
    uri = "file:///" + str(db_path).replace("\\", "/") + "?mode=ro"
    connection = sqlite3.connect(uri, uri=True)
    connection.row_factory = sqlite3.Row
    cursor = connection.cursor()
    latest = {}
    after_fault = {}
    for epb in (4, 5, 8, 9, 10, 11):
        row = cursor.execute(
            """
            SELECT epb_id, cycle_number, start_time, end_time, sample_count, status
              FROM epb_cycles
             WHERE epb_id=? AND cycle_number>0
             ORDER BY cycle_number DESC LIMIT 1
            """,
            (epb,),
        ).fetchone()
        latest[str(epb)] = dict(row) if row else None
        count = cursor.execute(
            """
            SELECT COUNT(*)
              FROM epb_cycles
             WHERE epb_id=? AND cycle_number>0 AND status='completed' AND start_time>?
            """,
            (epb, FAULT_TIME.isoformat()),
        ).fetchone()[0]
        after_fault[str(epb)] = count
    connection.close()
    return {"latest_positive_cycle": latest, "completed_cycles_started_after_fault": after_fault}


def main() -> None:
    output_path = (
        Path(sys.argv[1])
        if len(sys.argv) > 1
        else Path(__file__).with_name("10358_029_dev1_queue_full_analysis.json")
    )
    sampling = parse_sampling_config(DATA_ROOT / "log" / "run.log")
    lag_events = parse_lag_events(DATA_ROOT / "log" / "warning.log")
    by_device = defaultdict(list)
    for event in lag_events:
        by_device[event["device"]].append(event)
    pairs = pair_lag_events(lag_events)
    event_rows = [
        {
            **event,
            "timestamp": event["timestamp"].isoformat(sep=" ", timespec="milliseconds"),
        }
        for event in lag_events
    ]
    summary = {
        "analysis_scope": {
            "run_start_local": RUN_START.isoformat(sep=" ", timespec="milliseconds"),
            "fault_time_local": FAULT_TIME.isoformat(sep=" ", timespec="milliseconds"),
            "source_dataset": str(DATA_ROOT),
        },
        "sampling": sampling,
        "processing_lag": {
            "by_device": {
                device: {
                    "queue_depth_batches": summarize(
                        [float(event["queue_depth_batches"]) for event in device_events]
                    ),
                    "oldest_batch_age_ms": summarize(
                        [float(event["oldest_batch_age_ms"]) for event in device_events]
                    ),
                }
                for device, device_events in sorted(by_device.items())
            },
            "paired_within_50ms": {
                "count": len(pairs),
                "dev1_fraction": round(len(pairs) / len(by_device["Dev1"]), 4)
                if by_device["Dev1"]
                else None,
                "pairs": pairs,
            },
            "events": event_rows,
        },
        "last_dev1_cycles": {
            "EPB4_cycle_748": read_cycle_csv(
                DATA_ROOT
                / "Latest"
                / "EPB4"
                / "20260804_213616"
                / "EPB4_Cycle_000748.csv"
            ),
            "EPB5_cycle_671": read_cycle_csv(
                DATA_ROOT
                / "Latest"
                / "EPB5"
                / "20260804_213616"
                / "EPB5_Cycle_000671.csv"
            ),
        },
        "cycle_index": query_cycles(DATA_ROOT / "index.db"),
        "derived": {
            "queue_full_requires_continued_producer_callbacks": True,
            "queue_backlog_time_lower_bound_ms": sampling["queue_time_coverage_lower_bound_ms"],
            "dev1_channels_started_after_fault": 0,
            "dev2_channels_continued_after_fault": [8, 9, 10, 11],
            "alarm_snapshot_created_for_fault": False,
            "epb5_latest_export_error": "Cycle 667 already existed during export",
        },
    }
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
    print(output_path)


if __name__ == "__main__":
    main()
