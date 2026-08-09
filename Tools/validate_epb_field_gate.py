#!/usr/bin/env python3
"""Validate a sealed EPB project directory against V2.12.0.23 field red lines."""

from __future__ import annotations

import argparse
import csv
import json
import os
import re
import shutil
import sqlite3
import sys
import tempfile
import uuid
from collections import Counter, defaultdict
from dataclasses import asdict, dataclass
from datetime import datetime
from pathlib import Path
from typing import Iterable


LOG_TIMESTAMP = re.compile(r"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})")
PEAK_LAG = re.compile(r"峰值完整数据处理滞后.*?滞后=([0-9.]+)ms", re.IGNORECASE)
VERSION = re.compile(r"V?\d+\.\d+\.\d+\.\d+", re.IGNORECASE)


@dataclass
class Check:
    name: str
    passed: bool
    evidence: str
    required: bool = True


@dataclass
class ValidationSession:
    run_id: str
    started: datetime
    ended: datetime
    start_record: dict[str, str]
    terminal_record: dict[str, str]
    terminal_count: int

    @property
    def duration_hours(self) -> float:
        return max(0.0, (self.ended - self.started).total_seconds() / 3600.0)


def iter_log_paths(root: Path) -> Iterable[Path]:
    seen: set[Path] = set()
    for base in (root / "log", root):
        if not base.is_dir():
            continue
        for path in sorted(base.glob("*.log")):
            name = path.name.lower()
            if not any(
                name == prefix + ".log" or name.startswith(prefix + ".")
                for prefix in ("run", "warning", "error", "ui-info")
            ):
                continue
            if path not in seen:
                seen.add(path)
                yield path


def read_quick(path: Path, window_bytes: int = 512 * 1024) -> str:
    with path.open("rb") as stream:
        head = stream.read(window_bytes)
        stream.seek(0, 2)
        length = stream.tell()
        if length <= window_bytes:
            data = head
        else:
            stream.seek(max(0, length - window_bytes))
            data = head + b"\n" + stream.read(window_bytes)
    return data.decode("utf-8-sig", errors="replace")


def read_logs(
    root: Path,
    scan_mode: str,
) -> tuple[dict[str, str], datetime | None, datetime | None]:
    logs: dict[str, str] = {}
    first: datetime | None = None
    last: datetime | None = None
    for path in iter_log_paths(root):
        text = (
            read_quick(path)
            if scan_mode == "quick" and path.stat().st_size > 2 * 1024 * 1024
            else path.read_text(encoding="utf-8-sig", errors="replace")
        )
        logs[str(path)] = text
        for line in text.splitlines():
            match = LOG_TIMESTAMP.match(line)
            if not match:
                continue
            try:
                value = datetime.strptime(match.group(1), "%Y-%m-%d %H:%M:%S.%f")
            except ValueError:
                continue
            first = value if first is None or value < first else first
            last = value if last is None or value > last else last
    return logs, first, last


def sum_occurrences(logs: dict[str, str], pattern: str) -> int:
    expression = re.compile(pattern, re.IGNORECASE)
    return sum(len(expression.findall(text)) for text in logs.values())


def parse_key_value_records(logs: dict[str, str], marker: str) -> list[dict[str, str]]:
    records: list[dict[str, str]] = []
    for text in logs.values():
        for line in text.splitlines():
            position = line.find(marker)
            if position < 0:
                continue
            payload = line[position + len(marker):].strip()
            fields: dict[str, str] = {}
            parts = payload.split()
            if marker == "FieldMetric " and parts:
                fields["Kind"] = parts[0]
                parts = parts[1:]
            for part in parts:
                if "=" not in part:
                    continue
                key, value = part.split("=", 1)
                fields[key] = value.rstrip(";,。")
            if fields:
                timestamp = LOG_TIMESTAMP.match(line)
                if timestamp:
                    fields["_Timestamp"] = timestamp.group(1)
                records.append(fields)
    return records


def record_timestamp(record: dict[str, str]) -> datetime | None:
    try:
        return datetime.strptime(record["_Timestamp"], "%Y-%m-%d %H:%M:%S.%f")
    except (KeyError, TypeError, ValueError):
        return None


def normalized_version(value: str | None) -> str:
    return (value or "").strip().upper().lstrip("V")


def normalized_guid(value: str | None) -> str | None:
    text = (value or "").strip()
    if not text:
        return None
    try:
        return uuid.UUID(text).hex
    except (ValueError, AttributeError):
        return None


def select_validation_session(
    logs: dict[str, str],
    expected_version: str,
) -> tuple[ValidationSession | None, list[dict[str, str]]]:
    field = parse_key_value_records(logs, "FieldMetric ")
    sessions = [item for item in field if item.get("Kind") == "SESSION"]
    starts = [
        item for item in sessions
        if item.get("Phase", "").lower() == "start"
        and item.get("RunId")
        and record_timestamp(item) is not None
    ]
    terminals = [
        item for item in sessions
        if item.get("Phase", "").lower() in {"stop", "complete", "channelstop", "alarmstop"}
        and item.get("RunId")
        and record_timestamp(item) is not None
    ]
    if not starts:
        return None, sessions

    # 正式放行必须验收数据目录中“最新一次”启动，不能因为最新运行崩溃、
    # 未闭合或版本不符而悄悄退回更早的一次成功运行。
    start = max(starts, key=lambda item: record_timestamp(item))
    if normalized_version(start.get("ProductVersion")) != normalized_version(expected_version):
        return None, sessions

    started = record_timestamp(start)
    run_id = normalized_guid(start.get("RunId"))
    if run_id is None:
        return None, sessions
    matching = sorted(
        (
            item for item in terminals
            if normalized_guid(item.get("RunId")) == run_id
            and record_timestamp(item) >= started
        ),
        key=lambda item: record_timestamp(item),
    )
    if not matching:
        return None, sessions
    return ValidationSession(
        run_id=run_id,
        started=started,
        ended=record_timestamp(matching[0]),
        start_record=start,
        terminal_record=matching[0],
        terminal_count=len(matching),
    ), sessions


def filter_logs_to_session(
    logs: dict[str, str],
    session: ValidationSession,
) -> dict[str, str]:
    filtered: dict[str, str] = {}
    for path, text in logs.items():
        retained: list[str] = []
        current_in_range = False
        for line in text.splitlines():
            match = LOG_TIMESTAMP.match(line)
            if match:
                try:
                    timestamp = datetime.strptime(match.group(1), "%Y-%m-%d %H:%M:%S.%f")
                    current_in_range = session.started <= timestamp <= session.ended
                except ValueError:
                    current_in_range = False
            if current_in_range:
                retained.append(line)
        if retained:
            filtered[path] = "\n".join(retained)
    return filtered


def numeric(records: list[dict[str, str]], key: str) -> list[float]:
    values: list[float] = []
    for record in records:
        try:
            values.append(float(str(record[key]).strip().rstrip("%")))
        except (KeyError, TypeError, ValueError):
            pass
    return values


def boolean(value: str | None) -> bool | None:
    if value is None:
        return None
    normalized = value.strip().lower()
    if normalized == "true":
        return True
    if normalized == "false":
        return False
    return None


def positive_integer(value: object) -> bool:
    try:
        return int(value) > 0
    except (TypeError, ValueError):
        return False


def add_limit_check(
    checks: list[Check],
    name: str,
    values: list[float],
    required: bool,
    predicate,
    evidence: str,
) -> None:
    checks.append(Check(
        name,
        bool(values) and predicate(values),
        f"samples={len(values)}; {evidence}" if values else "samples=0; 缺少结构化指标",
        required=required,
    ))


def validate_performance_metrics(
    logs: dict[str, str],
    checks: list[Check],
    metrics: dict,
    required: bool,
    expected_run_id: str | None,
) -> None:
    field = parse_key_value_records(logs, "FieldMetric ")
    daq = [item for item in field if item.get("Kind") == "DAQ" and item.get("Phase") == "Running"]
    do_off = [item for item in field if item.get("Kind") == "DO_OFF"]
    ui = [item for item in field if item.get("Kind") == "UI"]
    stop = [item for item in field if item.get("Kind") == "STOP_PERSISTENCE"]
    cycle_candidates = [
        item for item in field
        if item.get("Kind") == "CYCLE" and item.get("Phase") == "Formal"
        and item.get("Result", "").lower() == "success"
    ]
    foreign_cycle_run_ids = sorted({
        normalized_guid(item.get("RunId")) or f"invalid:{item.get('RunId', '')}"
        for item in cycle_candidates
        if normalized_guid(item.get("RunId")) != expected_run_id
    })
    cycles = [
        item for item in cycle_candidates
        if normalized_guid(item.get("RunId")) == expected_run_id
    ]
    host = parse_key_value_records(logs, "HostRuntime ")

    devices = sorted({item.get("Device", "") for item in daq if item.get("Device")})
    metrics["field_metric_counts"] = {
        "daq": len(daq),
        "do_off": len(do_off),
        "ui": len(ui),
        "stop_persistence": len(stop),
        "formal_cycle": len(cycles),
        "host_runtime": len(host),
    }
    metrics["field_metric_devices"] = devices
    checks.append(Check(
        "结构化性能证据覆盖Dev1/Dev2",
        set(devices) == {"Dev1", "Dev2"},
        f"devices={devices}, daqSamples={len(daq)}",
        required=required,
    ))
    checks.append(Check(
        "正式圈证据全部属于当前RunId",
        expected_run_id is not None and bool(cycle_candidates) and not foreign_cycle_run_ids,
        f"expectedRunId={expected_run_id}, candidates={len(cycle_candidates)}, "
        f"foreign={foreign_cycle_run_ids}",
        required=required,
    ))

    callback_age = numeric(daq, "CallbackAgeMs")
    subscriber = numeric(daq, "SubscriberMaxMs")
    persistence_age = numeric(daq, "PersistenceOldestMs")
    persistence_depth = numeric(daq, "PersistenceDepth")
    control_discontinuities = numeric(daq, "Discontinuities")
    metrics.update(
        daq_callback_age_p99_ms=percentile(callback_age, 0.99),
        daq_callback_age_max_ms=max(callback_age) if callback_age else None,
        subscriber_p99_ms=percentile(subscriber, 0.99),
        subscriber_max_ms=max(subscriber) if subscriber else None,
        persistence_age_p99_ms=percentile(persistence_age, 0.99),
        persistence_age_max_ms=max(persistence_age) if persistence_age else None,
        persistence_depth_p99=percentile(persistence_depth, 0.99),
        persistence_depth_max=max(persistence_depth) if persistence_depth else None,
        control_discontinuity_max=max(control_discontinuities) if control_discontinuities else None,
    )
    add_limit_check(
        checks,
        "DAQ回调最大空窗<100ms",
        callback_age,
        required,
        lambda values: max(values) < 100.0,
        f"p99={percentile(callback_age, 0.99)}, max={max(callback_age) if callback_age else None}",
    )
    add_limit_check(
        checks,
        "控制订阅者P99<5ms且最大<20ms",
        subscriber,
        required,
        lambda values: percentile(values, 0.99) < 5.0 and max(values) < 20.0,
        f"p99={percentile(subscriber, 0.99)}, max={max(subscriber) if subscriber else None}",
    )
    add_limit_check(
        checks,
        "持久化Oldest P99<100ms且最大<250ms",
        persistence_age,
        required,
        lambda values: percentile(values, 0.99) < 100.0 and max(values) < 250.0,
        f"p99={percentile(persistence_age, 0.99)}, max={max(persistence_age) if persistence_age else None}",
    )
    add_limit_check(
        checks,
        "持久化Depth P99<20",
        persistence_depth,
        required,
        lambda values: percentile(values, 0.99) < 20.0,
        f"p99={percentile(persistence_depth, 0.99)}, max={max(persistence_depth) if persistence_depth else None}",
    )
    checks.append(Check(
        "DAQ控制序号无不连续",
        bool(control_discontinuities) and max(control_discontinuities) == 0,
        f"samples={len(control_discontinuities)}, max=" +
        (str(max(control_discontinuities)) if control_discontinuities else "missing"),
        required=required,
    ))
    unhealthy_persistence = [
        item for item in daq
        if item.get("PersistenceState", "").lower() not in {"recovered", "healthy"}
    ]
    durability_blocked = [
        item for item in daq
        if boolean(item.get("DurabilityBlocked")) is not False
    ]
    over_capacity_dropped = numeric(daq, "OverCapacityDropped")
    checks.append(Check(
        "运行期持久化无Paused/Failed",
        bool(daq) and not unhealthy_persistence,
        f"samples={len(daq)}, unhealthy={len(unhealthy_persistence)}",
        required=required,
    ))
    checks.append(Check(
        "运行期无未解决持久化缺口",
        bool(daq) and not durability_blocked,
        f"samples={len(daq)}, blockedOrMissing={len(durability_blocked)}",
        required=required,
    ))
    checks.append(Check(
        "运行期持久化容量满丢批为0",
        len(over_capacity_dropped) == len(daq) and
        bool(over_capacity_dropped) and max(over_capacity_dropped) == 0,
        f"samples={len(over_capacity_dropped)}/{len(daq)}, max=" +
        (str(max(over_capacity_dropped)) if over_capacity_dropped else "missing"),
        required=required,
    ))

    do_total = numeric(do_off, "TotalMs")
    failed_do = [item for item in do_off if boolean(item.get("Result")) is not True]
    late_do = [item for item in do_off if boolean(item.get("Late")) is True]
    metrics.update(
        do_off_p99_ms=percentile(do_total, 0.99),
        do_off_max_ms=max(do_total) if do_total else None,
        do_off_failed_count=len(failed_do),
        do_off_late_count=len(late_do),
    )
    add_limit_check(
        checks,
        "安全断电P99<50ms且最大<100ms",
        do_total,
        required,
        lambda values: percentile(values, 0.99) < 50.0 and max(values) < 100.0,
        f"p99={percentile(do_total, 0.99)}, max={max(do_total) if do_total else None}",
    )
    checks.append(Check(
        "安全断电无失败或迟到完成",
        bool(do_off) and not failed_do and not late_do,
        f"samples={len(do_off)}, failed={len(failed_do)}, late={len(late_do)}",
        required=required,
    ))

    ui_delay_p95 = numeric(ui, "DelayP95Ms")
    ui_flush_max = numeric(ui, "FlushMaxMs")
    ui_append_max = numeric(ui, "AppendMaxMs")
    ui_trim_max = numeric(ui, "TrimMaxMs")
    ui_scroll_max = numeric(ui, "ScrollMaxMs")
    ui_dropped = numeric(ui, "Dropped")
    ui_file_dropped = numeric(ui, "FileDropped")
    metrics.update(
        ui_window_p95_delay_max_ms=max(ui_delay_p95) if ui_delay_p95 else None,
        ui_flush_absolute_max_ms=max(ui_flush_max) if ui_flush_max else None,
        ui_append_absolute_max_ms=max(ui_append_max) if ui_append_max else None,
        ui_trim_absolute_max_ms=max(ui_trim_max) if ui_trim_max else None,
        ui_scroll_absolute_max_ms=max(ui_scroll_max) if ui_scroll_max else None,
        ui_dropped_max=max(ui_dropped) if ui_dropped else None,
        ui_file_dropped_max=max(ui_file_dropped) if ui_file_dropped else None,
    )
    add_limit_check(
        checks,
        "UI消息泵窗口P95延迟<200ms",
        ui_delay_p95,
        required,
        lambda values: max(values) < 200.0,
        f"maximumWindowP95={max(ui_delay_p95) if ui_delay_p95 else None}",
    )
    checks.append(Check(
        "UI单次刷新及分阶段操作均<200ms",
        len(ui_flush_max) == len(ui) and
        len(ui_append_max) == len(ui) and
        len(ui_trim_max) == len(ui) and
        len(ui_scroll_max) == len(ui) and
        bool(ui_flush_max) and
        max(ui_flush_max) < 200.0 and
        max(ui_append_max) < 200.0 and
        max(ui_trim_max) < 200.0 and
        max(ui_scroll_max) < 200.0,
        f"samples=flush:{len(ui_flush_max)}/{len(ui)}, "
        f"append:{len(ui_append_max)}/{len(ui)}, trim:{len(ui_trim_max)}/{len(ui)}, "
        f"scroll:{len(ui_scroll_max)}/{len(ui)}; "
        f"max=flush:{max(ui_flush_max) if ui_flush_max else None}, "
        f"append:{max(ui_append_max) if ui_append_max else None}, "
        f"trim:{max(ui_trim_max) if ui_trim_max else None}, "
        f"scroll:{max(ui_scroll_max) if ui_scroll_max else None}",
        required=required,
    ))
    checks.append(Check(
        "UI显示队列Dropped=0",
        bool(ui_dropped) and max(ui_dropped) == 0,
        f"samples={len(ui_dropped)}, max={max(ui_dropped) if ui_dropped else None}",
        required=required,
    ))
    checks.append(Check(
        "UI文件队列FileDropped=0",
        len(ui_file_dropped) == len(ui) and
        bool(ui_file_dropped) and max(ui_file_dropped) == 0,
        f"samples={len(ui_file_dropped)}/{len(ui)}, "
        f"max={max(ui_file_dropped) if ui_file_dropped else None}",
        required=required,
    ))

    stop_devices = {
        item.get("Device") for item in stop
        if boolean(item.get("Closed")) is True and boolean(item.get("RawDrained")) is True
    }
    stop_failures = [
        item for item in stop
        if boolean(item.get("Closed")) is not True or
        boolean(item.get("RawDrained")) is not True or
        item.get("State", "").lower() != "recovered"
    ]
    checks.append(Check(
        "停止Raw发布与持久化边界闭合",
        stop_devices == {"Dev1", "Dev2"} and not stop_failures,
        f"closedDevices={sorted(value for value in stop_devices if value)}, failures={len(stop_failures)}",
        required=required,
    ))

    cycle_channels: dict[str, dict[str, float]] = {}
    for channel in sorted({item.get("Channel", "") for item in cycles if item.get("Channel")}):
        items = [item for item in cycles if item.get("Channel") == channel]
        qualified = sum(1 for item in items if boolean(item.get("Qualified")) is True)
        peaks = numeric(items, "Peak")
        cycle_channels[channel] = {
            "count": len(items),
            "qualified": qualified,
            "qualified_ratio": qualified / len(items) if items else 0.0,
            "maximum_peak_a": max(peaks) if peaks else 0.0,
        }
    metrics["formal_cycle_quality_by_channel"] = cycle_channels
    insufficient_quality = {
        channel: value for channel, value in cycle_channels.items()
        if value["qualified_ratio"] < 0.99 or value["maximum_peak_a"] > 17.0
    }
    checks.append(Check(
        "正式圈各通道≥99%在合格带且峰值≤17A",
        bool(cycle_channels) and not insufficient_quality,
        f"channels={len(cycle_channels)}, formalCycles={len(cycles)}, "
        f"failedChannels={sorted(insufficient_quality)}",
        required=required,
    ))

    system_cpu = numeric(host, "SystemCpu")
    process_cpu = numeric(host, "ProcessCpu")
    metrics.update(
        system_cpu_p95_percent=percentile(system_cpu, 0.95),
        system_cpu_max_percent=max(system_cpu) if system_cpu else None,
        process_cpu_p95_percent=percentile(process_cpu, 0.95),
        process_cpu_max_percent=max(process_cpu) if process_cpu else None,
    )
    add_limit_check(
        checks,
        "系统CPU P95<80%",
        system_cpu,
        required,
        lambda values: percentile(values, 0.95) < 80.0,
        f"p95={percentile(system_cpu, 0.95)}, max={max(system_cpu) if system_cpu else None}",
    )


def validate_recovery_stability(
    logs: dict[str, str],
    checks: list[Check],
    metrics: dict,
    required: bool,
    maximum_correlations_per_ten_minutes: int,
) -> None:
    field = parse_key_value_records(logs, "FieldMetric ")
    states = [item for item in field if item.get("Kind") == "STATE"]
    recovering = [
        item for item in states
        if item.get("State", "").lower() == "recovering"
        and record_timestamp(item) is not None
    ]
    expected_channels = {
        value
        for item in field
        if item.get("Kind") == "SESSION" and item.get("Phase", "").lower() == "start"
        for value in item.get("Channels", "").split(",")
        if value.isdigit()
    }
    session_run_ids = {
        normalized
        for item in field
        if item.get("Kind") == "SESSION" and item.get("Phase", "").lower() == "start"
        and (normalized := normalized_guid(item.get("RunId"))) is not None
    }
    observed_channels = {item.get("Channel") for item in states if item.get("Channel")}
    foreign_state_run_ids = sorted({
        normalized_guid(item.get("RunId")) or f"invalid:{item.get('RunId', '')}"
        for item in states
        if normalized_guid(item.get("RunId")) not in session_run_ids
    })
    checks.append(Check(
        "通道状态证据覆盖本次运行通道",
        bool(states) and expected_channels.issubset(observed_channels),
        f"expected={sorted(expected_channels)}, observed={sorted(observed_channels)}",
        required=required,
    ))
    checks.append(Check(
        "状态迁移全部属于当前RunId",
        bool(states) and len(session_run_ids) == 1 and not foreign_state_run_ids,
        f"sessionRunIds={sorted(session_run_ids)}, foreign={foreign_state_run_ids}",
        required=required,
    ))

    last_by_channel: dict[str, dict[str, str]] = {}
    for item in sorted(states, key=lambda value: record_timestamp(value) or datetime.min):
        channel = item.get("Channel")
        if channel:
            last_by_channel[channel] = item
    unresolved = sorted(
        channel for channel, item in last_by_channel.items()
        if item.get("State", "").lower() == "recovering"
    )
    checks.append(Check(
        "会话终态无通道滞留自维护",
        bool(states) and not unresolved,
        f"stateSamples={len(states)}, unresolvedChannels={unresolved}",
        required=required,
    ))

    correlation_events: dict[tuple[str, str], datetime] = {}
    correlation_entries: Counter[tuple[str, str]] = Counter()
    uncorrelated_recovering: list[str] = []
    for item in recovering:
        correlation = normalized_guid(item.get("CorrelationId"))
        channel = item.get("Channel", "")
        device = item.get("Device") or ("Dev1" if channel.isdigit() and int(channel) <= 6 else "Dev2")
        if not correlation or set(correlation) == {"0"}:
            uncorrelated_recovering.append(
                f"{item.get('_Timestamp', 'unknown')}/EPB{channel}/{item.get('Reason', '')}"
            )
            continue
        timestamp = record_timestamp(item)
        key = (device, correlation)
        if key not in correlation_events or timestamp < correlation_events[key]:
            correlation_events[key] = timestamp
        correlation_entries[(channel, correlation)] += 1

    maximum_in_window = 0
    window_details: dict[str, int] = {}
    for device in ("Dev1", "Dev2"):
        timestamps = sorted(
            timestamp for (event_device, _), timestamp in correlation_events.items()
            if event_device == device
        )
        device_max = 0
        right = 0
        for left, started in enumerate(timestamps):
            right = max(right, left)
            while right < len(timestamps) and (timestamps[right] - started).total_seconds() <= 600:
                right += 1
            device_max = max(device_max, right - left)
        window_details[device] = device_max
        maximum_in_window = max(maximum_in_window, device_max)
    metrics.update(
        recovery_state_transition_count=len(recovering),
        recovery_uncorrelated_transition_count=len(uncorrelated_recovering),
        recovery_unique_correlation_count=len(correlation_events),
        recovery_maximum_correlations_per_device_per_10m=maximum_in_window,
        recovery_correlations_per_device_per_10m=window_details,
    )
    checks.append(Check(
        "自维护状态必须携带非空根事故关联号",
        not uncorrelated_recovering,
        f"uncorrelated={uncorrelated_recovering}",
        required=required,
    ))
    checks.append(Check(
        f"自维护无风暴（每设备10分钟根事故≤{maximum_correlations_per_ten_minutes}）",
        maximum_in_window <= maximum_correlations_per_ten_minutes,
        f"uniqueCorrelations={len(correlation_events)}, windows={window_details}",
        required=required,
    ))
    duplicate_entries = {
        f"EPB{channel}/{correlation}": count
        for (channel, correlation), count in correlation_entries.items()
        if count > 1
    }
    checks.append(Check(
        "同一根事故每通道只进入一次自维护",
        not duplicate_entries,
        f"duplicates={duplicate_entries}",
        required=required,
    ))


def validate_database(root: Path, checks: list[Check], metrics: dict) -> None:
    database = root / "index.db"
    if not database.is_file():
        checks.append(Check("SQLite数据库存在", False, str(database)))
        return
    local_copy: Path | None = None
    try:
        # UNC 上直接 integrity_check 会产生大量高延迟随机读取；先复制到本机临时文件。
        with tempfile.NamedTemporaryFile(prefix="epb-field-gate-", suffix=".db", delete=False) as temp:
            local_copy = Path(temp.name)
        shutil.copy2(database, local_copy)
        connection = sqlite3.connect(f"file:{local_copy.as_posix()}?mode=ro", uri=True)
        try:
            integrity = connection.execute("PRAGMA integrity_check").fetchone()[0]
            checks.append(Check("SQLite完整性", integrity == "ok", str(integrity)))
            tables = {
                row[0]
                for row in connection.execute(
                    "SELECT name FROM sqlite_master WHERE type='table'"
                )
            }
            if "epb_cycles" not in tables:
                checks.append(Check("epb_cycles存在", False, "table missing"))
                return
            statuses = Counter(
                dict(connection.execute(
                    "SELECT status, COUNT(*) FROM epb_cycles GROUP BY status"
                ))
            )
            running = int(statuses.get("running", 0))
            metrics["cycle_status_counts"] = dict(statuses)
            metrics["running_cycle_count"] = running
            checks.append(Check("停止后无running圈", running == 0, f"running={running}"))
        finally:
            connection.close()
    except Exception as exc:  # noqa: BLE001 - report exact field failure
        checks.append(Check("SQLite可读取", False, f"{type(exc).__name__}: {exc}"))
    finally:
        if local_copy is not None:
            try:
                local_copy.unlink(missing_ok=True)
            except OSError:
                pass


def validate_incidents(
    root: Path,
    checks: list[Check],
    metrics: dict,
    scan_mode: str,
) -> list[dict]:
    incident_root = root / "IncidentSnapshots"
    if not incident_root.is_dir():
        metrics["incident_count"] = 0
        checks.append(Check("事故目录可审计", True, "无事故目录（0事故）", required=False))
        return []
    metrics["artifact_scan_mode"] = scan_mode
    if scan_mode == "none":
        checks.append(Check(
            "事故目录深度审计",
            True,
            "已显式跳过；仅允许历史UNC快速诊断，不可用于正式放行",
            required=False,
        ))
        return []

    correlations: dict[str, dict[str, int]] = defaultdict(lambda: {"trigger": 0, "terminal": 0})
    root_sizes: dict[str, int] = defaultdict(int)
    identities: list[dict] = []
    identity_records: list[tuple[Path, dict]] = []
    incident_directories: set[Path] = set()
    identity_directories: set[Path] = set()
    queue_depth: list[float] = []
    queue_age: list[float] = []
    subscriber: list[float] = []
    malformed = 0
    for directory, _, files in os.walk(incident_root):
        directory_path = Path(directory)
        try:
            relative = directory_path.relative_to(incident_root)
            top = relative.parts[0] if relative.parts else ""
        except ValueError:
            top = ""
        for name in files:
            path = directory_path / name
            if top:
                try:
                    root_sizes[top] += path.stat().st_size
                except OSError:
                    pass
            lowered_name = name.lower()
            if lowered_name == "incident.json":
                try:
                    data = json.loads(path.read_text(encoding="utf-8-sig", errors="replace"))
                    incident_directories.add(path.parent)
                    correlation = str(data.get("correlationId") or "").replace("-", "").lower()
                    if not correlation:
                        malformed += 1
                        continue
                    lowered = str(path.parent).lower()
                    if "00-trigger" in lowered:
                        correlations[correlation]["trigger"] += 1
                    if "90-terminal" in lowered or "90-hardware-confirmed" in lowered:
                        correlations[correlation]["terminal"] += 1
                except (OSError, json.JSONDecodeError):
                    malformed += 1
            elif lowered_name == "build-identity.json":
                try:
                    identity = json.loads(
                        path.read_text(encoding="utf-8-sig", errors="replace")
                    )
                    identities.append(identity)
                    identity_records.append((path.parent, identity))
                    identity_directories.add(path.parent)
                except (OSError, json.JSONDecodeError):
                    malformed += 1
            elif lowered_name == "daq_timing.csv":
                try:
                    with path.open("r", encoding="utf-8-sig", errors="replace", newline="") as stream:
                        for row in csv.DictReader(stream):
                            for key, target in (
                                ("QueueDepth", queue_depth),
                                ("QueueAgeMs", queue_age),
                                ("SubscriberMaxMs", subscriber),
                            ):
                                try:
                                    target.append(float(row.get(key, "")))
                                except (TypeError, ValueError):
                                    pass
                except OSError:
                    pass

    duplicate_trigger = sum(1 for value in correlations.values() if value["trigger"] > 1)
    duplicate_terminal = sum(1 for value in correlations.values() if value["terminal"] > 1)
    missing_terminal = sum(
        1 for value in correlations.values() if value["trigger"] > 0 and value["terminal"] == 0
    )
    metrics.update(
        incident_count=len(correlations),
        malformed_incident_json=malformed,
        duplicate_trigger_correlations=duplicate_trigger,
        duplicate_terminal_correlations=duplicate_terminal,
        missing_terminal_correlations=missing_terminal,
    )
    checks.append(Check(
        "事故JSON可解析",
        malformed == 0,
        f"correlations={len(correlations)}, malformed={malformed}",
    ))
    checks.append(Check(
        "每根事故单trigger/terminal",
        duplicate_trigger == 0 and duplicate_terminal == 0 and missing_terminal == 0,
        f"duplicateTrigger={duplicate_trigger}, duplicateTerminal={duplicate_terminal}, "
        f"missingTerminal={missing_terminal}",
    ))
    hash_pattern = re.compile(r"^[0-9a-fA-F]{64}$")
    commit_pattern = re.compile(r"^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$")
    invalid_identities = [
        identity for directory, identity in identity_records
        if directory in incident_directories
        and (
            not str(identity.get("productVersion") or "").strip()
            or not str(identity.get("assemblyVersion") or "").strip()
            or str(identity.get("executablePath") or "").strip().lower() in {"", "unknown"}
            or hash_pattern.fullmatch(str(identity.get("executableSha256") or "")) is None
            or not positive_integer(identity.get("processId"))
            or commit_pattern.fullmatch(str(identity.get("gitCommit") or "")) is None
            or boolean(identity.get("gitDirty")) is not False
            or str(identity.get("buildUtc") or "").strip().lower() in {"", "unknown"}
        )
    ]
    missing_identity_directories = sorted(
        str(path.relative_to(incident_root))
        for path in incident_directories - identity_directories
    )
    checks.append(Check(
        "每个事故阶段构建身份完整",
        not missing_identity_directories and not invalid_identities,
        f"incidentPhases={len(incident_directories)}, identities={len(identities)}, "
        f"missing={missing_identity_directories}, invalid={len(invalid_identities)}",
    ))

    max_size = max(root_sizes.values(), default=0)
    metrics["maximum_incident_bytes"] = max_size
    checks.append(Check(
        "单事故目录小于10MiB",
        max_size < 10 * 1024 * 1024,
        f"max={max_size / 1024 / 1024:.2f}MiB",
    ))
    metrics["timing_sample_count"] = len(queue_depth)
    for name, values in (
        ("queue_depth", queue_depth),
        ("queue_age_ms", queue_age),
        ("subscriber_ms", subscriber),
    ):
        metrics[f"{name}_p99"] = percentile(values, 0.99)
        metrics[f"{name}_max"] = max(values) if values else None
    return identities


def validate_alarm_snapshots(root: Path, checks: list[Check], metrics: dict) -> None:
    alarm_root = root / "AlarmSnapshots"
    if not alarm_root.is_dir():
        metrics["alarm_snapshot_count"] = 0
        checks.append(Check(
            "硬报警最近圈证据完整",
            True,
            "无硬报警快照（0次硬报警）",
            required=False,
        ))
        return

    snapshot_directories = [path for path in alarm_root.iterdir() if path.is_dir()]
    manifests = sorted(alarm_root.glob("*/snapshot-manifest.json"))
    if not snapshot_directories and not manifests:
        metrics["alarm_snapshot_count"] = 0
        checks.append(Check(
            "硬报警最近圈证据完整",
            True,
            "报警根目录为空（0次硬报警）",
            required=False,
        ))
        return
    malformed = 0
    incomplete: list[str] = []
    for manifest_path in manifests:
        try:
            data = json.loads(manifest_path.read_text(encoding="utf-8-sig", errors="strict"))
            channel = int(data["AlarmChannel"])
            alarm_cycle = int(data["AlarmCycle"])
            requested = max(1, int(data.get("RequestedCycles", 10)))
            declared = max(0, int(data.get("ExportedCycles", 0)))
            evidence_dir = manifest_path.parent / f"EPB{channel:02d}_ALARM"
            csv_stems = {
                path.stem for path in evidence_dir.glob("*.csv")
                if path.is_file() and path.stat().st_size > 0
            }
            bin_stems = {
                path.stem for path in evidence_dir.glob("*.bin")
                if path.is_file() and path.stat().st_size > 0
            }
            pair_count = len(csv_stems & bin_stems)
            expected = min(requested, max(1, alarm_cycle)) if alarm_cycle > 0 else 1
            exported_channels = {int(value) for value in data.get("ExportedCycleEvidenceChannels", [])}
            skipped_channels = {int(value) for value in data.get("SkippedCycleEvidenceChannels", [])}
            if (
                pair_count != declared
                or pair_count < expected
                or channel not in exported_channels
                or channel in skipped_channels
            ):
                incomplete.append(
                    f"{manifest_path.parent.name}: pairs={pair_count}, "
                    f"declared={declared}, expected={expected}, "
                    f"exportedChannels={sorted(exported_channels)}, "
                    f"skippedChannels={sorted(skipped_channels)}"
                )
        except (OSError, UnicodeError, ValueError, KeyError, TypeError, json.JSONDecodeError) as exc:
            malformed += 1
            incomplete.append(f"{manifest_path.parent.name}: {type(exc).__name__}: {exc}")

    metrics["alarm_snapshot_count"] = len(manifests)
    metrics["malformed_alarm_snapshot_count"] = malformed
    metrics["incomplete_alarm_snapshot_count"] = len(incomplete)
    checks.append(Check(
        "硬报警最近圈证据完整",
        bool(manifests) and not incomplete,
        f"snapshots={len(manifests)}, malformed={malformed}, incomplete={len(incomplete)}" +
        (f"; first={incomplete[0]}" if incomplete else ""),
    ))


def percentile(values: list[float], fraction: float) -> float | None:
    if not values:
        return None
    ordered = sorted(values)
    index = min(len(ordered) - 1, max(0, int(round((len(ordered) - 1) * fraction))))
    return ordered[index]


def markdown(result: dict) -> str:
    lines = [
        f"# EPB V2.12.0.23 现场封存验收：{result['status']}",
        "",
        f"- 数据目录：`{result['data_directory']}`",
        f"- 生成时间：{result['generated_at']}",
        f"- 日志运行时长：{result['metrics'].get('duration_hours', 0):.3f} h",
        "",
        "## 机器判定",
        "",
        "| 检查项 | 结果 | 证据 |",
        "|---|---|---|",
    ]
    for check in result["checks"]:
        label = "PASS" if check["passed"] else ("FAIL" if check["required"] else "WARN")
        lines.append(
            f"| {check['name']} | {label} | "
            f"{check['evidence'].replace('|', '/')} |"
        )
    lines.extend([
        "",
        "## 指标",
        "",
        "```json",
        json.dumps(result["metrics"], ensure_ascii=False, indent=2),
        "```",
        "",
        "> 本报告只覆盖封存目录可机器判定的证据。真实 NI 断电 P99/最大值、液压动作、"
        "RichTextBox 操作响应和外部安全继电器必须结合台架步骤人工签字。",
        "",
    ])
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("data_directory", type=Path)
    parser.add_argument("--expected-version", default="V2.12.0.23")
    parser.add_argument("--expected-exe-sha256")
    parser.add_argument("--expected-config-sha256")
    parser.add_argument("--expected-git-commit")
    parser.add_argument("--expected-build-utc")
    parser.add_argument("--minimum-hours", type=float, default=0.0)
    parser.add_argument(
        "--artifact-scan",
        choices=("full", "none"),
        default="full",
        help="正式放行必须用full；none仅用于巨大UNC历史目录快速诊断",
    )
    parser.add_argument(
        "--log-scan",
        choices=("full", "quick"),
        default="full",
        help="正式放行必须用full；quick只读取大日志头尾用于UNC历史快速诊断",
    )
    parser.add_argument(
        "--database-scan",
        choices=("full", "none"),
        default="full",
        help="正式放行必须用full；none仅用于UNC历史快速诊断",
    )
    parser.add_argument(
        "--performance-gates",
        choices=("auto", "required", "optional"),
        default="auto",
        help="auto在minimum-hours>=2且完整日志扫描时强制原始报告定量门槛",
    )
    parser.add_argument(
        "--max-recovery-correlations-per-10m",
        type=int,
        default=3,
        help="同一物理DAQ设备10分钟内允许的最大独立自维护根事故数",
    )
    parser.add_argument("--output-md", type=Path)
    parser.add_argument("--output-json", type=Path)
    args = parser.parse_args()

    root = args.data_directory.resolve()
    if not root.is_dir():
        print(f"数据目录不存在：{root}", file=sys.stderr)
        return 2

    logs, first, last = read_logs(root, args.log_scan)
    performance_required = (
        args.performance_gates == "required" or
        (
            args.performance_gates == "auto" and
            args.minimum_hours >= 2.0 and
            args.log_scan == "full"
        )
    )
    session, session_records = select_validation_session(logs, args.expected_version)
    scoped_logs = filter_logs_to_session(logs, session) if session else logs
    combined = "\n".join(scoped_logs.values())
    duration_hours = (
        session.duration_hours
        if session
        else (last - first).total_seconds() / 3600 if first and last else 0.0
    )
    metrics: dict = {
        "log_file_count": len(logs),
        "first_log_timestamp": first.isoformat(sep=" ") if first else None,
        "last_log_timestamp": last.isoformat(sep=" ") if last else None,
        "duration_hours": duration_hours,
        "log_scan_mode": args.log_scan,
        "performance_gates_required": performance_required,
        "session_marker_count": len(session_records),
        "selected_run_id": session.run_id if session else None,
        "selected_session_started": session.started.isoformat(sep=" ") if session else None,
        "selected_session_ended": session.ended.isoformat(sep=" ") if session else None,
    }
    checks: list[Check] = []
    checks.append(Check("日志存在", bool(logs), f"files={len(logs)}"))
    checks.append(Check(
        "正式放行使用完整日志扫描",
        args.log_scan == "full",
        f"mode={args.log_scan}; quick仅允许历史UNC快速诊断",
        required=performance_required,
    ))
    checks.append(Check(
        "正式放行使用完整事故证据扫描",
        args.artifact_scan == "full",
        f"mode={args.artifact_scan}; none仅允许历史UNC快速诊断",
        required=performance_required,
    ))
    checks.append(Check(
        "正式放行使用完整数据库扫描",
        args.database_scan == "full",
        f"mode={args.database_scan}; none仅允许历史UNC快速诊断",
        required=performance_required,
    ))
    checks.append(Check(
        "运行时长达到门槛",
        duration_hours >= max(0.0, args.minimum_hours),
        f"actual={duration_hours:.3f}h required={args.minimum_hours:.3f}h",
    ))

    checks.append(Check(
        "连续运行会话有唯一Start和终态",
        session is not None,
        f"markers={len(session_records)}, selectedRunId={session.run_id if session else 'none'}",
        required=performance_required,
    ))
    if session:
        overlapping_starts = [
            item for item in session_records
            if item.get("Phase", "").lower() == "start"
            and record_timestamp(item) is not None
            and session.started <= record_timestamp(item) <= session.ended
        ]
        checks.append(Check(
            "验收区间没有重启或第二个RunId",
            len(overlapping_starts) == 1,
            f"startMarkersInWindow={len(overlapping_starts)}, "
            f"runIds={sorted({item.get('RunId', '') for item in overlapping_starts})}",
            required=performance_required,
        ))
        closed = boolean(session.terminal_record.get("Closed")) is True
        checks.append(Check(
            "会话终态已安全闭合",
            closed,
            f"phase={session.terminal_record.get('Phase')}, "
            f"closed={session.terminal_record.get('Closed')}",
            required=performance_required,
        ))
        checks.append(Check(
            "同一RunId只有一个会话终态",
            session.terminal_count == 1,
            f"runId={session.run_id}, terminalMarkers={session.terminal_count}",
            required=performance_required,
        ))
        identity = session.start_record
        hash_pattern = re.compile(r"^[0-9a-fA-F]{64}$")
        commit_pattern = re.compile(r"^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$")
        identity_complete = (
            hash_pattern.fullmatch(identity.get("ExeSha256", "")) is not None
            and hash_pattern.fullmatch(identity.get("ConfigSha256", "")) is not None
            and commit_pattern.fullmatch(identity.get("GitCommit", "")) is not None
            and boolean(identity.get("GitDirty")) is False
            and bool(identity.get("AssemblyVersion"))
            and positive_integer(identity.get("ProcessId"))
            and str(identity.get("ExecutablePath") or "").strip().lower()
            not in {"", "unknown", "none"}
        )
        checks.append(Check(
            "正式会话构建身份完整且GitDirty=false",
            identity_complete,
            f"version={identity.get('ProductVersion')}, exe={identity.get('ExeSha256')}, "
            f"config={identity.get('ConfigSha256')}, commit={identity.get('GitCommit')}, "
            f"dirty={identity.get('GitDirty')}, assembly={identity.get('AssemblyVersion')}, "
            f"pid={identity.get('ProcessId')}, path={identity.get('ExecutablePath')}",
            required=performance_required,
        ))

    expected_identity_values = {
        "ExeSha256": args.expected_exe_sha256,
        "ConfigSha256": args.expected_config_sha256,
        "GitCommit": args.expected_git_commit,
        "BuildUtc": args.expected_build_utc,
    }
    identity_binding_requested = any(expected_identity_values.values())
    expected_hash = re.compile(r"^[0-9a-fA-F]{64}$")
    expected_commit = re.compile(r"^(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64})$")
    identity_binding_complete = (
        expected_hash.fullmatch(args.expected_exe_sha256 or "") is not None
        and expected_hash.fullmatch(args.expected_config_sha256 or "") is not None
        and expected_commit.fullmatch(args.expected_git_commit or "") is not None
        and bool((args.expected_build_utc or "").strip())
    )
    observed_identity = session.start_record if session else {}
    identity_binding_matches = (
        identity_binding_complete
        and (observed_identity.get("ExeSha256") or "").lower()
        == (args.expected_exe_sha256 or "").lower()
        and (observed_identity.get("ConfigSha256") or "").lower()
        == (args.expected_config_sha256 or "").lower()
        and (observed_identity.get("GitCommit") or "").lower()
        == (args.expected_git_commit or "").lower()
        and (observed_identity.get("BuildUtc") or "")
        == (args.expected_build_utc or "")
        and boolean(observed_identity.get("GitDirty")) is False
    )
    binding_required = performance_required or identity_binding_requested
    checks.append(Check(
        "运行身份与已校验正式候选完全一致",
        identity_binding_matches if binding_required else True,
        "expected=" + json.dumps(expected_identity_values, ensure_ascii=False) +
        "; observed=" + json.dumps({
            key: observed_identity.get(key) for key in expected_identity_values
        }, ensure_ascii=False),
        required=binding_required,
    ))
    metrics["formal_release_identity_binding_required"] = binding_required
    metrics["formal_release_identity_binding_complete"] = identity_binding_complete
    metrics["formal_release_identity_binding_matches"] = identity_binding_matches

    versions = sorted(set(VERSION.findall(combined)))
    identities = validate_incidents(root, checks, metrics, args.artifact_scan)
    if args.artifact_scan == "full":
        validate_alarm_snapshots(root, checks, metrics)
    else:
        metrics["alarm_snapshot_scan_mode"] = "none"
        checks.append(Check(
            "硬报警快照深度审计",
            True,
            "已显式跳过；仅允许历史UNC快速诊断，不可用于正式放行",
            required=False,
        ))
    identity_versions = sorted({str(item.get("productVersion")) for item in identities if item.get("productVersion")})
    version_evidence = set(value.upper().lstrip("V") for value in versions)
    if session:
        version_evidence.add(normalized_version(session.start_record.get("ProductVersion")))
    expected = args.expected_version.upper().lstrip("V")
    metrics["versions_in_logs"] = versions
    metrics["identity_versions"] = identity_versions
    checks.append(Check(
        "版本身份匹配",
        version_evidence == {expected},
        f"expected={args.expected_version}, observed={sorted(version_evidence)}",
    ))

    redlines = {
        "内存资源不足": r"内存资源不足|not enough (memory|storage)",
        "已关闭访问器": r"已关闭的访问器|closed accessor|UnmanagedMemoryAccessor.*closed",
        "任一实时队列硬上限": r"DaqPersistenceQueueFull|ControlQueueFull|BackgroundQueueFull|RawPersistenceQueueFull",
        "软预警标量证据丢弃": r"WarningScalarQueueDropped(?:=[1-9]\d*|\b)",
        "DAQ时钟模型无效": r"DaqClockModelInvalid|Kind=ClockInvalid|DAQ时钟模型.*失效",
        "持久化暂停或失败": r"DaqPersistencePaused|DaqPersistenceFailed",
        "未观察后台异常": r"UnobservedTaskException",
        "后台任务失败": r"后台异步操作失败",
        "退出任务未收口": r"后台任务未在\s*\d+\s*秒内收口|后台任务.*未.*收口",
        "退出SetPressure空引用": r"SetPressure.*(NullReference|空引用)|NullReference.*SetPressure",
        "液压屏障超时": r"HydraulicBarrierTimeout",
    }
    redline_counts = {name: sum_occurrences(scoped_logs, pattern) for name, pattern in redlines.items()}
    metrics["redline_counts"] = redline_counts
    for name, count in redline_counts.items():
        checks.append(Check(f"红线：{name}=0", count == 0, f"count={count}"))

    lag_values = [float(value) for value in PEAK_LAG.findall(combined)]
    false_lag = [value for value in lag_values if value <= 100.0]
    metrics["peak_lag_warning_count"] = len(lag_values)
    metrics["peak_lag_false_positive_count"] = len(false_lag)
    checks.append(Check(
        "lag≤100ms峰值滞后误报为0",
        not false_lag,
        f"warnings={len(lag_values)}, falsePositive={len(false_lag)}",
    ))

    validate_performance_metrics(
        scoped_logs,
        checks,
        metrics,
        performance_required,
        session.run_id if session else None,
    )
    validate_recovery_stability(
        scoped_logs,
        checks,
        metrics,
        performance_required,
        max(0, args.max_recovery_correlations_per_10m),
    )

    if args.database_scan == "full":
        validate_database(root, checks, metrics)
    else:
        metrics["database_scan_mode"] = "none"
        checks.append(Check(
            "SQLite深度审计",
            True,
            "已显式跳过；仅允许历史UNC快速诊断，不可用于正式放行",
            required=False,
        ))
    required_failures = [check for check in checks if check.required and not check.passed]
    result = {
        "status": "PASS" if not required_failures else "FAIL",
        "data_directory": str(root),
        "generated_at": datetime.now().isoformat(timespec="seconds"),
        "checks": [asdict(check) for check in checks],
        "metrics": metrics,
    }
    default_output = Path.cwd() / "FieldGateReports"
    output_md = args.output_md or default_output / f"{root.name}-field-gate.md"
    output_json = args.output_json or default_output / f"{root.name}-field-gate.json"
    output_md.parent.mkdir(parents=True, exist_ok=True)
    output_json.parent.mkdir(parents=True, exist_ok=True)
    output_md.write_text(markdown(result), encoding="utf-8-sig")
    output_json.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8-sig")
    print(json.dumps({
        "status": result["status"],
        "failed": [check.name for check in required_failures],
        "output_md": str(output_md),
        "output_json": str(output_json),
    }, ensure_ascii=False, indent=2))
    return 0 if result["status"] == "PASS" else 2


if __name__ == "__main__":
    raise SystemExit(main())
