#!/usr/bin/env python3
"""Validate a sealed EPB project directory against V2.12.0.27 field red lines."""

from __future__ import annotations

import argparse
import csv
import json
import math
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


def valid_incident_timestamp(value: object) -> bool:
    if not isinstance(value, str) or not value.strip():
        return False
    normalized = value.strip()
    if normalized.endswith("Z"):
        normalized = normalized[:-1] + "+00:00"
    try:
        datetime.fromisoformat(normalized)
        return True
    except ValueError:
        return False


def classify_incident_phase(data: dict, directory_name: str) -> str | None:
    """Return trigger/terminal/phase only for a production-shaped incident phase."""
    correlation = normalized_guid(str(data.get("correlationId") or ""))
    run_id = normalized_guid(str(data.get("runId") or ""))
    device = str(data.get("device") or "").strip().lower()
    affected = data.get("affectedChannels")
    common_valid = (
        correlation not in {None, "0" * 32}
        and run_id not in {None, "0" * 32}
        and device in {"dev1", "dev2"}
        and isinstance(affected, list)
        and bool(affected)
        and all(isinstance(channel, int) and 1 <= channel <= 12 for channel in affected)
        and valid_incident_timestamp(data.get("capturedUtc"))
    )
    if not common_valid:
        return None

    phase_directory = directory_name.strip().lower()
    result_value = data.get("result")
    if isinstance(result_value, str) and result_value.strip():
        result = result_value.strip().lower()
        if re.fullmatch(r"\d{2}-[a-z0-9]+(?:-[a-z0-9]+)*", result) is None:
            return None
        if re.fullmatch(
            re.escape(result) + r"-\d{3}-\d{6}_\d{3}",
            phase_directory,
        ) is None:
            return None
        terminal = result.startswith("90-")
        timing = result == "00-trigger" or terminal
        if (
            not str(data.get("faultCode") or "").strip()
            or data.get("fullEvidenceIncluded") is not terminal
            or data.get("timingEvidenceIncluded") is not timing
            or not isinstance(data.get("recentCycleCopiesIncluded"), bool)
        ):
            return None
        if result == "00-trigger":
            return "trigger"
        return "terminal" if terminal else "phase"

    # Confirmed hardware faults use a distinct terminal schema and directory format.
    if re.fullmatch(r"90-terminal-\d{6}_\d{3}-[0-9a-f]{32}", phase_directory) is None:
        return None
    if (
        not str(data.get("primaryFault") or "").strip()
        or not valid_incident_timestamp(data.get("firstSeenUtc"))
        or not valid_incident_timestamp(data.get("lastSeenUtc"))
    ):
        return None
    return "terminal"


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


def nonnegative_integer_argument(value: str) -> int:
    try:
        parsed = int(value)
    except (TypeError, ValueError) as exc:
        raise argparse.ArgumentTypeError("必须是非负整数") from exc
    if parsed < 0:
        raise argparse.ArgumentTypeError("必须是非负整数")
    return parsed


def bounded_positive_float_argument(value: str, hard_maximum: float) -> float:
    try:
        parsed = float(value)
    except (TypeError, ValueError) as exc:
        raise argparse.ArgumentTypeError("必须是大于0的数") from exc
    if not math.isfinite(parsed) or parsed <= 0 or parsed > hard_maximum:
        raise argparse.ArgumentTypeError(f"必须位于(0,{hard_maximum}]")
    return parsed


def coverage_argument(value: str) -> float:
    try:
        parsed = float(value)
    except (TypeError, ValueError) as exc:
        raise argparse.ArgumentTypeError("覆盖率必须位于[0.95,1]") from exc
    if not math.isfinite(parsed) or parsed < 0.95 or parsed > 1:
        raise argparse.ArgumentTypeError("覆盖率必须位于[0.95,1]")
    return parsed


def integer_value(record: dict[str, str], key: str) -> int | None:
    try:
        return int(str(record[key]).strip())
    except (KeyError, TypeError, ValueError):
        return None


def finite_float_value(record: dict[str, str], key: str) -> float | None:
    try:
        value = float(str(record[key]).strip().rstrip("%"))
        return value if math.isfinite(value) else None
    except (KeyError, TypeError, ValueError):
        return None


def session_channels(session: ValidationSession | None) -> set[str]:
    if session is None:
        return set()
    return {
        value
        for value in session.start_record.get("Channels", "").split(",")
        if value.isdigit()
    }


def heartbeat_summary(
    records: list[dict[str, str]],
    started: datetime,
    ended: datetime,
    expected_interval_seconds: float,
) -> dict[str, float | int | None]:
    timestamps = sorted({
        timestamp
        for record in records
        if (timestamp := record_timestamp(record)) is not None
        and started <= timestamp <= ended
    })
    duration_seconds = max(0.0, (ended - started).total_seconds())
    expected_samples = max(1, int(math.ceil(duration_seconds / expected_interval_seconds)))
    coverage = min(1.0, len(timestamps) / expected_samples)
    gaps = [duration_seconds]
    if timestamps:
        gaps = [
            max(0.0, (timestamps[0] - started).total_seconds()),
            max(0.0, (ended - timestamps[-1]).total_seconds()),
        ]
        gaps.extend(
            max(0.0, (current - previous).total_seconds())
            for previous, current in zip(timestamps, timestamps[1:])
        )
    return {
        "samples": len(timestamps),
        "records": len(records),
        "expected_samples": expected_samples,
        "coverage": coverage,
        "maximum_gap_seconds": max(gaps),
    }


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
    session: ValidationSession | None,
    minimum_formal_cycles_per_channel: int,
    minimum_heartbeat_coverage: float,
    daq_heartbeat_max_gap_seconds: float,
    ui_heartbeat_max_gap_seconds: float,
    host_heartbeat_max_gap_seconds: float,
) -> None:
    expected_run_id = session.run_id if session else None
    expected_channels = session_channels(session)
    field = parse_key_value_records(logs, "FieldMetric ")
    daq = [item for item in field if item.get("Kind") == "DAQ" and item.get("Phase") == "Running"]
    do_off = [item for item in field if item.get("Kind") == "DO_OFF"]
    ui = [item for item in field if item.get("Kind") == "UI"]
    stop = [item for item in field if item.get("Kind") == "STOP_PERSISTENCE"]
    cycle_candidates = [
        item for item in field
        if item.get("Kind") == "CYCLE" and item.get("Phase") == "Formal"
        and item.get("Result", "").lower() in {"success", "successwithwarning"}
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

    if session:
        heartbeat_specs = (
            (
                "DAQ",
                {device: [item for item in daq if item.get("Device") == device]
                 for device in ("Dev1", "Dev2")},
                2.0,
                daq_heartbeat_max_gap_seconds,
            ),
            ("UI", {"UI": ui}, 10.0, ui_heartbeat_max_gap_seconds),
            ("HostRuntime", {"Host": host}, 1.0, host_heartbeat_max_gap_seconds),
        )
        heartbeat_metrics: dict[str, dict[str, dict[str, float | int | None]]] = {}
        for kind, streams, interval_seconds, maximum_gap_seconds in heartbeat_specs:
            summaries = {
                stream: heartbeat_summary(
                    records,
                    session.started,
                    session.ended,
                    interval_seconds,
                )
                for stream, records in streams.items()
            }
            heartbeat_metrics[kind] = summaries
            failing = {
                stream: summary
                for stream, summary in summaries.items()
                if summary["coverage"] < minimum_heartbeat_coverage
                or summary["maximum_gap_seconds"] > maximum_gap_seconds
                or summary["samples"] != summary["records"]
            }
            checks.append(Check(
                f"{kind}心跳覆盖率与最大缺口达标",
                bool(summaries) and not failing,
                f"interval={interval_seconds:.1f}s, minimumCoverage="
                f"{minimum_heartbeat_coverage:.3f}, maxGap={maximum_gap_seconds:.1f}s, "
                f"streams={json.dumps(summaries, ensure_ascii=False, sort_keys=True)}, "
                f"failed={sorted(failing)}",
                required=required,
            ))
        metrics["heartbeat_evidence"] = heartbeat_metrics
        metrics["heartbeat_thresholds"] = {
            "minimum_coverage": minimum_heartbeat_coverage,
            "daq_expected_interval_seconds": 2.0,
            "daq_maximum_gap_seconds": daq_heartbeat_max_gap_seconds,
            "ui_expected_interval_seconds": 10.0,
            "ui_maximum_gap_seconds": ui_heartbeat_max_gap_seconds,
            "host_expected_interval_seconds": 1.0,
            "host_maximum_gap_seconds": host_heartbeat_max_gap_seconds,
        }
    else:
        for kind in ("DAQ", "UI", "HostRuntime"):
            checks.append(Check(
                f"{kind}心跳覆盖率与最大缺口达标",
                False,
                "缺少可闭合的正式SESSION，无法计算覆盖率",
                required=required,
            ))

    pipeline_required_fields = (
        "ProcessingDepth", "ProcessingCapacity", "ProcessingOldestMs",
        "ProcessingInFlight", "RawDepth", "RawCapacity", "RawInFlight",
        "Allocated", "Accepted", "Observed", "Published", "RawTransferred",
        "Persisted", "TerminallyHandled", "SuppressBoundary", "SuppressThrough", "Suppressed",
        "SuppressedCumulative", "SuppressedFirst", "SuppressedLast",
        "SuppressedRanges", "FirstPermanentGap", "PendingProcessingGap",
        "PendingRawGap", "AsyncLogDropped",
    )
    pipeline_integer_fields = tuple(
        key for key in pipeline_required_fields if key != "ProcessingOldestMs"
    )
    sequence_watermarks = (
        "Allocated", "Accepted", "Observed", "Published", "RawTransferred", "Persisted",
        "TerminallyHandled", "SuppressedCumulative", "SuppressedFirst",
        "SuppressedLast", "SuppressedRanges",
    )
    missing_pipeline_fields: list[str] = []
    invalid_pipeline_values: list[str] = []
    invalid_depths: list[str] = []
    observed_violations: list[str] = []
    suppression_ledger_violations: list[str] = []
    permanent_gaps: list[str] = []
    monotonicity_violations: list[str] = []
    by_device: dict[str, list[dict[str, str]]] = defaultdict(list)
    for item in daq:
        label = f"{item.get('_Timestamp', 'unknown')}/{item.get('Device', 'missing')}"
        missing = [key for key in pipeline_required_fields if key not in item]
        if missing:
            missing_pipeline_fields.append(f"{label}:{','.join(missing)}")
        integers = {key: integer_value(item, key) for key in pipeline_integer_fields}
        invalid_integers = [key for key, value in integers.items() if value is None or value < 0]
        oldest = finite_float_value(item, "ProcessingOldestMs")
        if invalid_integers or oldest is None or oldest < 0:
            invalid_pipeline_values.append(
                f"{label}:integer={invalid_integers},ProcessingOldestMs="
                f"{item.get('ProcessingOldestMs', 'missing')}"
            )
        processing_depth = integers["ProcessingDepth"]
        processing_capacity = integers["ProcessingCapacity"]
        raw_depth = integers["RawDepth"]
        raw_capacity = integers["RawCapacity"]
        if (
            processing_depth is None or processing_capacity is None or
            processing_capacity <= 0 or processing_depth < 0 or
            processing_depth >= processing_capacity or
            raw_depth is None or raw_capacity is None or
            raw_capacity <= 0 or raw_depth < 0 or raw_depth >= raw_capacity
        ):
            invalid_depths.append(
                f"{label}:processing={processing_depth}/{processing_capacity},"
                f"raw={raw_depth}/{raw_capacity}"
            )
        allocated = integers["Allocated"]
        accepted = integers["Accepted"]
        observed = integers["Observed"]
        if (
            allocated is None or accepted is None or observed is None or
            observed < allocated or observed < accepted
        ):
            observed_violations.append(
                f"{label}:Observed={observed},Allocated={allocated},Accepted={accepted}"
            )
        persisted = integers["Persisted"]
        handled = integers["TerminallyHandled"]
        suppressed = integers["Suppressed"]
        cumulative = integers["SuppressedCumulative"]
        suppress_boundary = integers["SuppressBoundary"]
        suppress_through = integers["SuppressThrough"]
        first_suppressed = integers["SuppressedFirst"]
        last_suppressed = integers["SuppressedLast"]
        suppressed_ranges = integers["SuppressedRanges"]
        suppression_ledger_valid = (
            persisted is not None and handled is not None and handled >= persisted and
            suppress_boundary is not None and suppress_through is not None and
            ((suppress_boundary == 0 and suppress_through == 0) or
             suppress_through >= suppress_boundary) and
            suppressed is not None and cumulative is not None and cumulative >= suppressed and
            first_suppressed is not None and last_suppressed is not None and
            suppressed_ranges is not None and
            (
                (cumulative == 0 and first_suppressed == 0 and
                 last_suppressed == 0 and suppressed_ranges == 0) or
                (cumulative > 0 and first_suppressed > 0 and
                 last_suppressed >= first_suppressed and
                 suppressed_ranges > 0 and cumulative >= suppressed_ranges)
            )
        )
        if not suppression_ledger_valid:
            suppression_ledger_violations.append(
                f"{label}:Persisted={persisted},Handled={handled},"
                f"Window={suppress_boundary}-{suppress_through},"
                f"Suppressed={suppressed}/{cumulative},"
                f"Range={first_suppressed}-{last_suppressed},Count={suppressed_ranges}"
            )
        first_gap = integers["FirstPermanentGap"]
        if first_gap != 0:
            permanent_gaps.append(f"{label}:FirstPermanentGap={first_gap}")
        device = item.get("Device")
        if device:
            by_device[device].append(item)

    final_pending: dict[str, dict[str, int | None]] = {}
    for device, records in by_device.items():
        ordered = sorted(records, key=lambda value: record_timestamp(value) or datetime.min)
        previous: dict[str, int] = {}
        for item in ordered:
            for key in sequence_watermarks:
                current = integer_value(item, key)
                if current is None:
                    continue
                if key in previous and current < previous[key]:
                    monotonicity_violations.append(
                        f"{item.get('_Timestamp', 'unknown')}/{device}/{key}:"
                        f"{current}<{previous[key]}"
                    )
                previous[key] = current
        if ordered:
            final = ordered[-1]
            final_pending[device] = {
                "PendingProcessingGap": integer_value(final, "PendingProcessingGap"),
                "PendingRawGap": integer_value(final, "PendingRawGap"),
            }

    expected_pipeline_devices = {"Dev1", "Dev2"}
    pending_failures = {
        device: values
        for device, values in final_pending.items()
        if values["PendingProcessingGap"] != 0 or values["PendingRawGap"] != 0
    }
    metrics["daq_pipeline_validation"] = {
        "required_fields": list(pipeline_required_fields),
        "missing_field_records": len(missing_pipeline_fields),
        "invalid_value_records": len(invalid_pipeline_values),
        "invalid_depth_records": len(invalid_depths),
        "monotonicity_violations": len(monotonicity_violations),
        "observed_invariant_violations": len(observed_violations),
        "suppression_ledger_violations": len(suppression_ledger_violations),
        "permanent_gap_records": len(permanent_gaps),
        "final_pending": final_pending,
    }
    checks.append(Check(
        "DAQ管线指标字段完整且数值合法",
        bool(daq) and not missing_pipeline_fields and not invalid_pipeline_values,
        f"records={len(daq)}, missing={missing_pipeline_fields[:10]}, "
        f"invalid={invalid_pipeline_values[:10]}",
        required=required,
    ))
    checks.append(Check(
        "DAQ处理与Raw队列深度严格低于容量",
        bool(daq) and not invalid_depths,
        f"records={len(daq)}, invalid={invalid_depths[:10]}",
        required=required,
    ))
    checks.append(Check(
        "DAQ耐久水位按设备非递减",
        set(by_device) == expected_pipeline_devices and not monotonicity_violations,
        f"devices={sorted(by_device)}, violations={monotonicity_violations[:10]}",
        required=required,
    ))
    checks.append(Check(
        "DAQ Observed始终覆盖Allocated和Accepted",
        bool(daq) and not observed_violations,
        f"violations={observed_violations[:10]}",
        required=required,
    ))
    checks.append(Check(
        "DAQ显式排除账本与物理持久化水位一致",
        bool(daq) and not suppression_ledger_violations,
        f"violations={suppression_ledger_violations[:10]}",
        required=required,
    ))
    checks.append(Check(
        "DAQ无永久空洞且终态无待决gap",
        set(final_pending) == expected_pipeline_devices and
        not permanent_gaps and not pending_failures,
        f"permanent={permanent_gaps[:10]}, finalPending={final_pending}, "
        f"failed={pending_failures}",
        required=required,
    ))

    session_metrics = [item for item in field if item.get("Kind") == "SESSION"]
    async_log_records = session_metrics + daq
    invalid_async_log_dropped = [
        f"{item.get('Kind', 'unknown')}/{item.get('_Timestamp', 'unknown')}:"
        f"{item.get('AsyncLogDropped', 'missing')}"
        for item in async_log_records
        if integer_value(item, "AsyncLogDropped") != 0
    ]
    metrics["async_log_dropped_evidence_count"] = len(async_log_records)
    metrics["async_log_dropped_violation_count"] = len(invalid_async_log_dropped)
    checks.append(Check(
        "SESSION与DAQ异步项目日志丢弃始终为0",
        bool(session_metrics) and bool(daq) and not invalid_async_log_dropped,
        f"sessionRecords={len(session_metrics)}, daqRecords={len(daq)}, "
        f"violations={invalid_async_log_dropped[:10]}",
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

    def stop_boundary_failure(item: dict[str, str]) -> str | None:
        device = item.get("Device", "missing")
        boundary = integer_value(item, "Boundary")
        final_boundary = integer_value(item, "FinalBoundary")
        published = integer_value(item, "Published")
        persisted = integer_value(item, "Persisted")
        depth = integer_value(item, "Depth")
        discarded = integer_value(item, "Discarded")
        over_capacity = integer_value(item, "OverCapacityDropped")
        require_recovered = boolean(item.get("RequireRecovered"))
        reasons: list[str] = []
        if boolean(item.get("Closed")) is not True:
            reasons.append("Closed!=True")
        if boolean(item.get("RawDrained")) is not True:
            reasons.append("RawDrained!=True")
        if boundary is None or boundary < 0:
            reasons.append("Boundary缺失或<0")
        if final_boundary is None or final_boundary != boundary:
            reasons.append("FinalBoundary!=Boundary")
        if boolean(item.get("BoundaryStable")) is not True:
            reasons.append("BoundaryStable!=True")
        if published is None or boundary is None or published < boundary:
            reasons.append("Published<Boundary或缺失")
        if persisted is None or boundary is None or persisted < boundary:
            reasons.append("Persisted<Boundary或缺失")
        if depth != 0:
            reasons.append("Depth!=0或缺失")
        if require_recovered is None:
            reasons.append("RequireRecovered缺失")
        elif require_recovered and item.get("State", "").lower() != "recovered":
            reasons.append("要求Recovered但State不符")
        if boolean(item.get("DurabilityBlocked")) is not False:
            reasons.append("DurabilityBlocked!=False")
        if discarded != 0:
            reasons.append("Discarded!=0或缺失")
        if over_capacity != 0:
            reasons.append("OverCapacityDropped!=0或缺失")
        return f"{device}:" + ",".join(reasons) if reasons else None

    stop_devices = {item.get("Device") for item in stop if item.get("Device")}
    stop_failures = [failure for item in stop if (failure := stop_boundary_failure(item))]
    checks.append(Check(
        "停止Raw发布与持久化边界闭合",
        stop_devices == {"Dev1", "Dev2"} and not stop_failures,
        f"devices={sorted(stop_devices)}, records={len(stop)}, failures={stop_failures}",
        required=required,
    ))

    cycle_channels: dict[str, dict[str, float]] = {}
    invalid_cycle_records: list[str] = []
    duplicate_cycle_numbers: dict[str, list[int]] = {}
    for channel in sorted({item.get("Channel", "") for item in cycles if item.get("Channel")}):
        items = [item for item in cycles if item.get("Channel") == channel]
        qualified = sum(1 for item in items if boolean(item.get("Qualified")) is True)
        peaks = numeric(items, "Peak")
        numbers = [integer_value(item, "Cycle") for item in items]
        invalid = [value for value in numbers if value is None or value <= 0]
        counts = Counter(value for value in numbers if value is not None and value > 0)
        duplicates = sorted(value for value, count in counts.items() if count > 1)
        if invalid:
            invalid_cycle_records.append(channel)
        if duplicates:
            duplicate_cycle_numbers[channel] = duplicates
        cycle_channels[channel] = {
            "count": len(counts),
            "records": len(items),
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
    insufficient_cycle_count = {
        channel: int(cycle_channels.get(channel, {}).get("count", 0))
        for channel in sorted(expected_channels)
        if int(cycle_channels.get(channel, {}).get("count", 0))
        < minimum_formal_cycles_per_channel
    }
    metrics["minimum_formal_cycles_per_channel"] = minimum_formal_cycles_per_channel
    checks.append(Check(
        "正式耐久完成圈数达到每通道门槛",
        bool(expected_channels) and
        set(cycle_channels) == expected_channels and
        not insufficient_cycle_count and
        not invalid_cycle_records and
        not duplicate_cycle_numbers,
        f"required={minimum_formal_cycles_per_channel}, expectedChannels="
        f"{sorted(expected_channels)}, counts="
        f"{json.dumps({key: int(value['count']) for key, value in cycle_channels.items()}, sort_keys=True)}, "
        f"insufficient={insufficient_cycle_count}, invalid={invalid_cycle_records}, "
        f"duplicates={duplicate_cycle_numbers}",
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
    terminal_allowed_states = {"manualstopped", "completed"}
    invalid_terminal_states = {
        channel: item.get("State", "missing")
        for channel, item in last_by_channel.items()
        if item.get("State", "").lower() not in terminal_allowed_states
    }
    non_quiescent_terminal = {
        channel: {
            "Formal": item.get("Formal"),
            "Timer": item.get("Timer"),
            "Runner": item.get("Runner"),
            "Energized": item.get("Energized"),
        }
        for channel, item in last_by_channel.items()
        if any(boolean(item.get(key)) is not False for key in (
            "Formal", "Timer", "Runner", "Energized"
        ))
    }
    checks.append(Check(
        "会话终态通道严格闭合",
        bool(expected_channels) and
        set(last_by_channel) == expected_channels and
        not invalid_terminal_states and
        not non_quiescent_terminal,
        f"allowed={sorted(terminal_allowed_states)}, expected={sorted(expected_channels)}, "
        f"observed={sorted(last_by_channel)}, invalidStates={invalid_terminal_states}, "
        f"nonQuiescent={non_quiescent_terminal}",
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


def validate_database(
    root: Path,
    checks: list[Check],
    metrics: dict,
    expected_channels: set[str],
    minimum_formal_cycles_per_channel: int,
    required: bool,
) -> None:
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
            completed_by_channel = {
                str(row[0]): int(row[1])
                for row in connection.execute(
                    "SELECT epb_id, COUNT(DISTINCT cycle_number) "
                    "FROM epb_cycles WHERE cycle_number > 0 AND status='completed' "
                    "GROUP BY epb_id"
                )
            }
            insufficient = {
                channel: completed_by_channel.get(channel, 0)
                for channel in sorted(expected_channels)
                if completed_by_channel.get(channel, 0) < minimum_formal_cycles_per_channel
            }
            metrics["sqlite_completed_cycles_by_channel"] = completed_by_channel
            checks.append(Check(
                "SQLite正式完成圈数达到每通道门槛",
                bool(expected_channels) and not insufficient,
                f"required={minimum_formal_cycles_per_channel}, expected="
                f"{sorted(expected_channels)}, completed={completed_by_channel}, "
                f"insufficient={insufficient}",
                required=required,
            ))
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
    selected_session_identity: dict[str, str] | None,
) -> list[dict]:
    incident_root = root / "IncidentSnapshots"
    if not incident_root.is_dir():
        metrics["incident_count"] = 0
        checks.append(Check("事故目录可审计", True, "无事故目录（0事故）", required=False))
        checks.append(Check(
            "事故身份与所选正式会话一致",
            True,
            "无事故身份（0事故）",
        ))
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
    invalid_phase_schema = 0
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
                    if not isinstance(data, dict):
                        invalid_phase_schema += 1
                        continue
                    correlation = normalized_guid(str(data.get("correlationId") or ""))
                    phase_kind = classify_incident_phase(data, path.parent.name)
                    if correlation is None or phase_kind is None:
                        invalid_phase_schema += 1
                        continue
                    if phase_kind == "trigger":
                        correlations[correlation]["trigger"] += 1
                    elif phase_kind == "terminal":
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
        invalid_incident_phase_schema=invalid_phase_schema,
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
        "事故phase schema有效",
        invalid_phase_schema == 0,
        f"invalid={invalid_phase_schema}",
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
            or hash_pattern.fullmatch(str(identity.get("configSha256") or "")) is None
            or hash_pattern.fullmatch(str(identity.get("releaseConfigSha256") or "")) is None
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

    def identity_mismatch(identity: dict) -> list[str]:
        expected = selected_session_identity or {}
        mismatches: list[str] = []
        comparisons = (
            (
                "productVersion",
                normalized_version(identity.get("productVersion")),
                normalized_version(expected.get("ProductVersion")),
            ),
            ("assemblyVersion", str(identity.get("assemblyVersion") or ""),
             str(expected.get("AssemblyVersion") or "")),
            ("executableSha256", str(identity.get("executableSha256") or "").lower(),
             str(expected.get("ExeSha256") or "").lower()),
            ("releaseConfigSha256", str(identity.get("releaseConfigSha256") or "").lower(),
             str(expected.get("ConfigSha256") or "").lower()),
            ("gitCommit", str(identity.get("gitCommit") or "").lower(),
             str(expected.get("GitCommit") or "").lower()),
            ("buildUtc", str(identity.get("buildUtc") or ""),
             str(expected.get("BuildUtc") or "")),
            ("processId", str(identity.get("processId") or ""),
             str(expected.get("ProcessId") or "")),
        )
        for name, actual, wanted in comparisons:
            if not wanted or actual != wanted:
                mismatches.append(f"{name}:{actual or 'missing'}!={wanted or 'missing'}")
        if boolean(str(identity.get("gitDirty") or "")) is not False:
            mismatches.append("gitDirty!=False")
        return mismatches

    mismatched_identity_directories = {
        str(directory.relative_to(incident_root)): identity_mismatch(identity)
        for directory, identity in identity_records
        if directory in incident_directories and identity_mismatch(identity)
    }
    metrics["incident_identity_mismatch_count"] = len(mismatched_identity_directories)
    checks.append(Check(
        "事故身份与所选正式会话一致",
        selected_session_identity is not None and not mismatched_identity_directories,
        f"identities={len(identity_records)}, mismatches="
        f"{json.dumps(mismatched_identity_directories, ensure_ascii=False, sort_keys=True)}",
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
    stage = result.get("acceptance_stage", "Staged")
    stage_title = "最终生产验收" if stage == "FinalProduction" else "阶段性现场验证"
    lines = [
        f"# EPB V{result.get('expected_version', 'Unknown')} {stage_title}：{result['status']}",
        "",
        f"- 验收阶段：`{stage}`",
        "- 生产放行结论：" + (
            "通过" if result.get("production_release_approved") else
            "不通过/不适用（阶段性结果不得作为最终生产放行）"
        ),
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
    parser.add_argument("--expected-version", default="V2.12.0.27")
    parser.add_argument("--expected-exe-sha256")
    parser.add_argument("--expected-config-sha256")
    parser.add_argument("--expected-git-commit")
    parser.add_argument("--expected-build-utc")
    parser.add_argument("--minimum-hours", type=float, default=0.0)
    parser.add_argument(
        "--acceptance-stage",
        choices=("Staged", "FinalProduction"),
        default="Staged",
        help="默认仅为阶段性验证；最终生产验收还要求>=72h且每通道>=100000正式完成圈",
    )
    parser.add_argument(
        "--minimum-formal-cycles-per-channel",
        type=nonnegative_integer_argument,
        default=1,
        help="所选连续SESSION内每个配置通道至少完成的正式圈数；最终耐久放行显式设为100000",
    )
    parser.add_argument(
        "--minimum-heartbeat-coverage",
        type=coverage_argument,
        default=0.95,
        help="DAQ(2s)、UI(10s)、HostRuntime(1s)相对SESSION时长的最小唯一时间戳覆盖率",
    )
    parser.add_argument(
        "--daq-heartbeat-max-gap-seconds",
        type=lambda value: bounded_positive_float_argument(value, 10.0),
        default=10.0,
    )
    parser.add_argument(
        "--ui-heartbeat-max-gap-seconds",
        type=lambda value: bounded_positive_float_argument(value, 30.0),
        default=30.0,
    )
    parser.add_argument(
        "--host-heartbeat-max-gap-seconds",
        type=lambda value: bounded_positive_float_argument(value, 5.0),
        default=5.0,
    )
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

    if args.acceptance_stage == "FinalProduction":
        if args.minimum_hours < 72.0:
            parser.error("FinalProduction要求--minimum-hours>=72")
        if args.minimum_formal_cycles_per_channel < 100000:
            parser.error(
                "FinalProduction要求--minimum-formal-cycles-per-channel>=100000"
            )
        if args.performance_gates == "optional":
            parser.error("FinalProduction禁止--performance-gates=optional")
        if (
            args.log_scan != "full" or
            args.artifact_scan != "full" or
            args.database_scan != "full"
        ):
            parser.error("FinalProduction要求log/artifact/database全部使用full扫描")

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
        "minimum_formal_cycles_per_channel": args.minimum_formal_cycles_per_channel,
        "minimum_heartbeat_coverage": args.minimum_heartbeat_coverage,
        "acceptance_stage": args.acceptance_stage,
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
        package_verified = (
            boolean(identity.get("PackageVerified")) is True
            and identity.get("PackageCode") == "Verified"
            and positive_integer(identity.get("PackageFiles"))
        )
        checks.append(Check(
            "正式会话从完整已批准发布包启动",
            package_verified,
            f"verified={identity.get('PackageVerified')}, "
            f"code={identity.get('PackageCode')}, files={identity.get('PackageFiles')}",
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
    identities = validate_incidents(
        root,
        checks,
        metrics,
        args.artifact_scan,
        session.start_record if session else None,
    )
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
        "永久数据连续性缺口": r"DaqDataContinuityGap|DaqRecoveryDataContinuityGap|BackgroundWorkerFault|RawPersistencePermanentFault|DaqPersistenceWriteStall",
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
        session,
        args.minimum_formal_cycles_per_channel,
        args.minimum_heartbeat_coverage,
        args.daq_heartbeat_max_gap_seconds,
        args.ui_heartbeat_max_gap_seconds,
        args.host_heartbeat_max_gap_seconds,
    )
    validate_recovery_stability(
        scoped_logs,
        checks,
        metrics,
        performance_required,
        max(0, args.max_recovery_correlations_per_10m),
    )

    if args.database_scan == "full":
        validate_database(
            root,
            checks,
            metrics,
            session_channels(session),
            args.minimum_formal_cycles_per_channel,
            performance_required,
        )
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
        "acceptance_stage": args.acceptance_stage,
        "production_release_approved": (
            args.acceptance_stage == "FinalProduction" and not required_failures
        ),
        "expected_version": args.expected_version,
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
