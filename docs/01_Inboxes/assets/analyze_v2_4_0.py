#!/usr/bin/env python3
"""Read-only V2.4.0 field evidence audit.

The source folders and SQLite database are opened read-only. Only --output is
written, so the script is safe to rerun against the production share.
"""

from __future__ import annotations

import argparse
import csv
import json
import re
import shutil
import sqlite3
import tempfile
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


FAULT_CODES = (
    "ForwardLoadRiseNotStarted",
    "ForwardCurrentRiseStalled",
    "OffCurrentNotCleared",
    "DaqSampleStale",
    "OpenCircuitOrOutputFault",
    "ForwardPeakOvershoot",
    "OverCurrent3Samples",
    "ForwardAbsoluteOnTimeExceeded",
    "ReverseAbsoluteOnTimeExceeded",
)

TRUE_FIELDS = (
    "CC",
    "VoltageLimited",
    "CurrentLimited",
    "PowerLimited",
    "ProtectionTripped",
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--data-root",
        type=Path,
        default=Path(r"\\wj-epb\EPB_Data\10364-009_V2.4.0.0_1"),
    )
    parser.add_argument(
        "--program-root",
        type=Path,
        default=Path(r"\\wj-epb\debug\2.4.0.0"),
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=Path(__file__).with_name("v2_4_0_analysis_summary.json"),
    )
    return parser.parse_args()


def read_text(path: Path) -> str:
    raw = path.read_bytes()
    for encoding in ("utf-8-sig", "utf-8", "gb18030"):
        try:
            return raw.decode(encoding)
        except UnicodeDecodeError:
            pass
    return raw.decode("utf-8", errors="replace")


def analyze_logs(log_dir: Path) -> dict[str, Any]:
    files: dict[str, dict[str, Any]] = {}
    all_lines: list[str] = []
    lines_by_name: dict[str, list[str]] = {}
    for path in sorted(log_dir.glob("*.log")):
        lines = read_text(path).splitlines()
        files[path.name] = {"line_count": len(lines), "bytes": path.stat().st_size}
        all_lines.extend(lines)
        lines_by_name[path.name] = lines

    code_counts = Counter()
    events: list[dict[str, Any]] = []
    seen: set[tuple[str, str, str]] = set()
    for line in all_lines:
        present = [code for code in FAULT_CODES if code in line]
        for code in present:
            code_counts[code] += 1

    for line in lines_by_name.get("error.log", []):
        if "硬故障，立即停止" not in line:
            continue
        timestamp = line[:23] if len(line) >= 23 else ""
        channel_match = re.search(r"EPB\[(\d+)\]", line)
        channel = int(channel_match.group(1)) if channel_match else None
        code = next((item for item in FAULT_CODES if item in line), "Unknown")
        key = (timestamp, str(channel), code)
        if key in seen:
            continue
        seen.add(key)
        values = {
            name: float(value)
            for name, value in re.findall(
                r"\b(elapsed|deadline|Current|Threshold|I)=(-?\d+(?:\.\d+)?)",
                line,
            )
        }
        events.append(
            {
                "timestamp": timestamp,
                "channel": channel,
                "code": code,
                "values": values,
            }
        )

    batch_timestamps = sorted(
        {
            line[:23]
            for line in all_lines
            if "批量启动异常" in line and len(line) >= 23
        }
    )
    batch_clusters: list[str] = []
    previous: datetime | None = None
    for value in batch_timestamps:
        try:
            current = datetime.strptime(value, "%Y-%m-%d %H:%M:%S.%f")
        except ValueError:
            continue
        if previous is None or (current - previous).total_seconds() > 2:
            batch_clusters.append(value)
        previous = current

    return {
        "files": files,
        "fault_code_line_counts": dict(sorted(code_counts.items())),
        "unique_hard_fault_events": events,
        "unique_hard_fault_event_count": len(events),
        "batch_failure_timestamps_clustered_2s": batch_clusters,
    }


def as_bool(value: str) -> bool:
    return value.strip().lower() == "true"


def as_float(value: str) -> float | None:
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


def analyze_telemetry(directory: Path) -> dict[str, Any]:
    files: list[dict[str, Any]] = []
    total_rows = 0
    true_counts = Counter()
    nonempty_errors = 0
    max_iout: float | None = None
    max_pout: float | None = None

    for path in sorted(directory.glob("*.csv")):
        row_count = 0
        file_true_counts = Counter()
        file_errors = 0
        file_max_iout: float | None = None
        with path.open("r", encoding="utf-8-sig", newline="") as handle:
            for row in csv.DictReader(handle):
                row_count += 1
                for field in TRUE_FIELDS:
                    if as_bool(row.get(field, "")):
                        true_counts[field] += 1
                        file_true_counts[field] += 1
                if row.get("Error", "").strip():
                    nonempty_errors += 1
                    file_errors += 1
                iout = as_float(row.get("IOut", ""))
                pout = as_float(row.get("POut", ""))
                if iout is not None:
                    file_max_iout = iout if file_max_iout is None else max(file_max_iout, iout)
                    max_iout = iout if max_iout is None else max(max_iout, iout)
                if pout is not None:
                    max_pout = pout if max_pout is None else max(max_pout, pout)

        total_rows += row_count
        files.append(
            {
                "name": path.name,
                "bytes": path.stat().st_size,
                "row_count": row_count,
                "max_iout_a": file_max_iout,
                "true_flag_counts": dict(file_true_counts),
                "nonempty_error_count": file_errors,
            }
        )

    return {
        "file_count": len(files),
        "nonempty_file_count": sum(item["row_count"] > 0 for item in files),
        "files": files,
        "total_rows": total_rows,
        "max_iout_a": max_iout,
        "max_pout_w": max_pout,
        "true_flag_counts": {field: true_counts[field] for field in TRUE_FIELDS},
        "nonempty_error_count": nonempty_errors,
    }


def analyze_sqlite(path: Path) -> dict[str, Any]:
    # sqlite3 URI mode rejects a UNC authority on Windows. Copy the database
    # and optional WAL/SHM sidecars locally, then query that immutable snapshot.
    with tempfile.TemporaryDirectory(prefix="epb_v240_sqlite_") as scratch:
        local_db = Path(scratch) / "index.db"
        shutil.copy2(path, local_db)
        for suffix in ("-wal", "-shm"):
            sidecar = Path(str(path) + suffix)
            if sidecar.exists():
                shutil.copy2(sidecar, Path(str(local_db) + suffix))
        return analyze_local_sqlite_snapshot(local_db, path)


def analyze_local_sqlite_snapshot(local_db: Path, source_path: Path) -> dict[str, Any]:
    uri = local_db.resolve().as_uri() + "?mode=ro"
    connection = sqlite3.connect(uri, uri=True)
    try:
        integrity = connection.execute("PRAGMA integrity_check").fetchone()[0]
        tables = [
            row[0]
            for row in connection.execute(
                "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name"
            )
        ]
        result: dict[str, Any] = {
            "path": str(source_path),
            "integrity_check": integrity,
            "tables": tables,
        }
        if "epb_cycles" in tables:
            result["epb_cycles_total"] = connection.execute(
                "SELECT COUNT(*) FROM epb_cycles"
            ).fetchone()[0]
            result["status_counts"] = dict(
                connection.execute(
                    "SELECT status,COUNT(*) FROM epb_cycles GROUP BY status ORDER BY status"
                )
            )
            result["running_count"] = connection.execute(
                "SELECT COUNT(*) FROM epb_cycles WHERE status='running'"
            ).fetchone()[0]
        return result
    finally:
        connection.close()


def analyze_artifacts(root: Path, program_root: Path) -> dict[str, Any]:
    suffix_counts = Counter(
        path.suffix.lower() or "<none>" for path in root.rglob("*") if path.is_file()
    )
    learning_pairs = len(list((root / "LearningCycles").glob("**/*.csv")))
    alarm_pairs = len(list((root / "AlarmSnapshots").glob("**/*.csv")))
    legacy_dat = list((program_root / "DataStore").glob("EPB*_sliding.dat"))
    return {
        "source_file_suffix_counts": dict(sorted(suffix_counts.items())),
        "learning_csv_count": learning_pairs,
        "alarm_snapshot_csv_count": alarm_pairs,
        "legacy_program_dat_count": len(legacy_dat),
        "legacy_program_dat_latest_mtime": (
            max(path.stat().st_mtime for path in legacy_dat) if legacy_dat else None
        ),
    }


def main() -> int:
    args = parse_args()
    if not args.data_root.is_dir():
        raise FileNotFoundError(args.data_root)
    if not args.program_root.is_dir():
        raise FileNotFoundError(args.program_root)

    summary = {
        "generated_utc": datetime.now(timezone.utc).isoformat(),
        "read_only_sources": {
            "data_root": str(args.data_root),
            "program_root": str(args.program_root),
        },
        "logs": analyze_logs(args.data_root / "log"),
        "telemetry": analyze_telemetry(args.data_root / "PowerSupplyTelemetry"),
        "sqlite": analyze_sqlite(args.data_root / "index.db"),
        "artifacts": analyze_artifacts(args.data_root, args.program_root),
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(
        json.dumps(summary, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    print(args.output)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
