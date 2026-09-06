#!/usr/bin/env python3
"""Reproduce the V2.3.0.0 control/data-quality statistics and report figures."""

from __future__ import annotations

import argparse
import csv
import json
import re
import sqlite3
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path

import matplotlib.pyplot as plt
import numpy as np


DEFAULT_DATA_ROOT = Path(r"\\wj-epb\EPB_Data\10364-009_V2.3.0.0_2")
CHANNELS = (8, 9, 10)
COLORS = {8: "#3267A8", 9: "#D28E1D", 10: "#B64D63"}
LINE_STYLES = {8: "-", 9: "--", 10: "-."}
COMPLETION_RE = re.compile(
    r"EPB\[(?P<channel>\d+)\] 自适应单圈完成："
    r"Fwd=(?P<fwd>\d+)ms，Rev=(?P<rev>\d+)ms，.*?"
    r"Error=(?P<error>[+-]?\d+(?:\.\d+)?)A，.*?结果=(?P<result>[^。\s]+)"
)
FORMAL_START_RE = re.compile(
    r"正式阶段启动 .*?EPB=(?P<channel>\d+).*?Cycle=(?P<cycle>\d+)"
)
LOG_TIMESTAMP_RE = re.compile(r"^(?P<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--data-root", type=Path, default=DEFAULT_DATA_ROOT)
    parser.add_argument(
        "--output-dir",
        type=Path,
        default=Path(__file__).resolve().parent,
    )
    return parser.parse_args()


def find_run_log(root: Path) -> Path:
    preferred = root / "log" / "run.20260730.001.log"
    if preferred.exists():
        return preferred
    candidates = sorted((root / "log").glob("run*.log"), key=lambda p: p.stat().st_size)
    if not candidates:
        raise FileNotFoundError("No run log found")
    return candidates[-1]


def parse_formal_completions(log_path: Path) -> list[dict]:
    current_cycle: dict[int, int] = {}
    rows: list[dict] = []
    with log_path.open("r", encoding="utf-8-sig", errors="replace") as handle:
        for line in handle:
            start = FORMAL_START_RE.search(line)
            if start:
                channel = int(start.group("channel"))
                if channel in CHANNELS:
                    current_cycle[channel] = int(start.group("cycle"))
                continue
            completion = COMPLETION_RE.search(line)
            if not completion:
                continue
            channel = int(completion.group("channel"))
            cycle = current_cycle.get(channel)
            if channel not in CHANNELS or cycle is None:
                continue
            rows.append(
                {
                    "channel": channel,
                    "cycle": cycle,
                    "fwd_ms": int(completion.group("fwd")),
                    "rev_ms": int(completion.group("rev")),
                    "error_a": float(completion.group("error")),
                    "result": completion.group("result"),
                }
            )
    return rows


def percentile(values: list[float], q: float) -> float:
    return float(np.percentile(np.asarray(values, dtype=float), q))


def summarize_precision(rows: list[dict]) -> dict:
    result = {}
    for channel in CHANNELS:
        items = sorted(
            (row for row in rows if row["channel"] == channel),
            key=lambda row: row["cycle"],
        )
        errors = [row["error_a"] for row in items]
        absolute = [abs(value) for value in errors]
        result[str(channel)] = {
            "completed_formal_cycles": len(items),
            "success": sum(row["result"] == "Success" for row in items),
            "success_with_warning": sum(
                row["result"] == "SuccessWithWarning" for row in items
            ),
            "min_error_a": min(errors),
            "max_error_a": max(errors),
            "mean_error_a": float(np.mean(errors)),
            "mae_a": float(np.mean(absolute)),
            "p95_absolute_error_a": percentile(absolute, 95),
            "within_0_8_a": sum(value <= 0.8 for value in absolute),
            "forward_median_ms": percentile([row["fwd_ms"] for row in items], 50),
            "forward_p95_ms": percentile([row["fwd_ms"] for row in items], 95),
            "forward_max_ms": max(row["fwd_ms"] for row in items),
            "reverse_median_ms": percentile([row["rev_ms"] for row in items], 50),
            "reverse_p95_ms": percentile([row["rev_ms"] for row in items], 95),
            "reverse_max_ms": max(row["rev_ms"] for row in items),
        }
    return result


def plot_precision(rows: list[dict], output_path: Path) -> None:
    fig, ax = plt.subplots(figsize=(12, 5.8))
    ax.axhspan(-0.8, 0.8, color="#3267A8", alpha=0.09, label="±0.8 A 平衡带")
    ax.axhline(0, color="#4B5563", linewidth=1)
    for channel in CHANNELS:
        items = sorted(
            (row for row in rows if row["channel"] == channel),
            key=lambda row: row["cycle"],
        )
        ax.plot(
            [row["cycle"] for row in items],
            [row["error_a"] for row in items],
            color=COLORS[channel],
            linestyle=LINE_STYLES[channel],
            marker="o",
            markersize=3,
            linewidth=1.3,
            label=f"EPB{channel}",
        )
    ax.axvline(63, color="#374151", linestyle=":", linewidth=1.2)
    ax.text(63.7, 1.22, "EPB9/10 第63圈报警", color="#374151", fontsize=9)
    ax.set_title("V2.3.0.0 正式圈峰值电流误差")
    ax.set_xlabel("正式圈号")
    ax.set_ylabel("Peak − Target (A)")
    ax.set_xlim(1, 101)
    ax.set_ylim(-1.35, 1.55)
    ax.grid(axis="y", color="#D1D5DB", linewidth=0.7)
    ax.legend(ncol=4, frameon=False, loc="lower right")
    fig.tight_layout()
    fig.savefig(output_path, dpi=180, bbox_inches="tight")
    plt.close(fig)


def parse_local_timestamp(value: str) -> datetime:
    return datetime.fromisoformat(value.strip())


def read_cycle_csv(path: Path, stride: int = 20) -> list[dict]:
    rows = []
    with path.open("r", encoding="utf-8-sig", newline="") as handle:
        reader = csv.DictReader(handle)
        for index, row in enumerate(reader):
            if index % stride:
                continue
            rows.append(
                {
                    "time": parse_local_timestamp(row["Timestamp"]),
                    "current": float(row["EpbCurrent"]),
                    "pressure": float(row["GroupPressure"]),
                }
            )
    return rows


def find_cycle_csv(root: Path, channel: int) -> Path:
    if channel == 9:
        pattern = "AlarmSnapshots/*-EPB09/EPB09_ALARM/EPB9_Cycle_000063.csv"
    elif channel == 10:
        pattern = "AlarmSnapshots/*-EPB10/EPB10_ALARM/EPB10_Cycle_000063.csv"
    else:
        pattern = "AlarmSnapshots/*/EPB08/EPB8_Cycle_000063.csv"
    candidates = list(root.glob(pattern))
    if not candidates:
        raise FileNotFoundError(pattern)
    return max(candidates, key=lambda path: path.stat().st_size)


def parse_cycle63_events(log_path: Path) -> list[dict]:
    events = []
    with log_path.open("r", encoding="utf-8-sig", errors="replace") as handle:
        for line in handle:
            ts_match = LOG_TIMESTAMP_RE.match(line)
            if not ts_match:
                continue
            timestamp = datetime.strptime(ts_match.group("time"), "%Y-%m-%d %H:%M:%S.%f")
            if "Cycle=63" in line and "EPB=8" in line and "Command=Reverse" in line:
                events.append({"time": timestamp, "label": "EPB8反转", "color": COLORS[8]})
            elif "Cycle=63" in line and "EPB=10" in line and "Command=Reverse" in line:
                events.append({"time": timestamp, "label": "EPB10反转", "color": COLORS[10]})
            elif "液压[2] 本轮所有成员已到电压释放点" in line:
                events.append({"time": timestamp, "label": "液压释放命令", "color": "#6B7280"})
            elif (
                "Cycle=63" in line
                and "Stage=HandleAdaptiveDecision" in line
                and "Command=OffHighPriority" in line
            ):
                match = re.search(r"EPB=(\d+)", line)
                if match and int(match.group(1)) in (9, 10):
                    channel = int(match.group(1))
                    events.append(
                        {
                            "time": timestamp,
                            "label": f"EPB{channel}硬故障",
                            "color": COLORS[channel],
                        }
                    )
    events.sort(key=lambda item: item["time"])
    merged = []
    for event in events:
        if (
            merged
            and abs((event["time"] - merged[-1]["time"]).total_seconds()) <= 0.05
            and {event["label"], merged[-1]["label"]}
            == {"EPB9硬故障", "液压释放命令"}
        ):
            merged[-1] = {
                "time": min(event["time"], merged[-1]["time"]),
                "label": "EPB9硬故障 / 液压释放",
                "color": "#6B7280",
            }
        else:
            merged.append(event)
    return merged


def plot_cycle63(root: Path, log_path: Path, output_path: Path) -> None:
    series = {channel: read_cycle_csv(find_cycle_csv(root, channel)) for channel in CHANNELS}
    base = min(row["time"] for values in series.values() for row in values)
    events = parse_cycle63_events(log_path)

    fig, (pressure_ax, current_ax) = plt.subplots(
        2,
        1,
        figsize=(12, 7.4),
        sharex=True,
        gridspec_kw={"height_ratios": [0.85, 1.35]},
    )
    pressure_rows = series[8]
    pressure_ax.plot(
        [(row["time"] - base).total_seconds() for row in pressure_rows],
        [row["pressure"] for row in pressure_rows],
        color="#3267A8",
        linewidth=1.5,
    )
    pressure_ax.axhline(5, color="#374151", linestyle=":", linewidth=1, label="5 bar安全阈值")
    pressure_ax.set_title("第63圈共享液压压力")
    pressure_ax.set_ylabel("Pressure (bar)")
    pressure_ax.grid(axis="y", color="#D1D5DB", linewidth=0.7)
    pressure_ax.legend(frameon=False, loc="upper right")

    for channel in CHANNELS:
        values = series[channel]
        current_ax.plot(
            [(row["time"] - base).total_seconds() for row in values],
            [abs(row["current"]) for row in values],
            color=COLORS[channel],
            linestyle=LINE_STYLES[channel],
            linewidth=1.2,
            label=f"EPB{channel}",
        )
    current_ax.axhline(9, color="#374151", linestyle=":", linewidth=1, label="9 A高平台阈值")
    current_ax.set_title("第63圈支路电流绝对值")
    current_ax.set_xlabel(f"相对时间 (s)，起点 {base:%Y-%m-%d %H:%M:%S}")
    current_ax.set_ylabel("|Current| (A)")
    current_ax.grid(axis="y", color="#D1D5DB", linewidth=0.7)
    current_ax.legend(ncol=4, frameon=False, loc="upper right")

    annotations = []
    for event in events:
        x_value = (event["time"] - base).total_seconds()
        if x_value < -0.5 or x_value > 18:
            continue
        for axis in (pressure_ax, current_ax):
            axis.axvline(x_value, color=event["color"], alpha=0.5, linewidth=0.9)
        if event["label"] not in {item["label"] for item in annotations}:
            annotations.append({"x": x_value, **event})
    y_levels = (0.96, 0.84, 0.72, 0.60, 0.48)
    for index, event in enumerate(annotations):
        pressure_ax.text(
            event["x"] + 0.05,
            y_levels[index % len(y_levels)],
            event["label"],
            color=event["color"],
            fontsize=8,
            rotation=90,
            va="top",
            transform=pressure_ax.get_xaxis_transform(),
        )

    current_ax.set_xlim(0, 16)
    fig.tight_layout()
    fig.savefig(output_path, dpi=180, bbox_inches="tight")
    plt.close(fig)


def count_csv_rows(path: Path) -> int:
    newline_count = 0
    last_byte = b""
    with path.open("rb") as handle:
        while True:
            chunk = handle.read(1024 * 1024)
            if not chunk:
                break
            newline_count += chunk.count(b"\n")
            last_byte = chunk[-1:]
    physical_lines = newline_count + (1 if last_byte and last_byte != b"\n" else 0)
    return max(0, physical_lines - 1)


def inspect_alarm_pairs(root: Path) -> dict:
    checked = 0
    mismatches = []
    for bin_path in (root / "AlarmSnapshots").glob("**/*.bin"):
        csv_path = bin_path.with_suffix(".csv")
        checked += 1
        bin_records = bin_path.stat().st_size // 32
        csv_rows = count_csv_rows(csv_path) if csv_path.exists() else -1
        if bin_path.stat().st_size % 32 or csv_rows != bin_records:
            mismatches.append(
                {
                    "bin": str(bin_path),
                    "bin_records": bin_records,
                    "csv_rows": csv_rows,
                }
            )
    return {"pairs_checked": checked, "pair_mismatches": mismatches}


def inspect_alarm_database_alignment(root: Path, database: dict) -> list[dict]:
    rows = []
    for db_row in database["cycle63_database"]:
        channel = db_row["channel"]
        pattern = (
            f"AlarmSnapshots/*-EPB{channel:02d}/"
            f"EPB{channel:02d}_ALARM/EPB{channel}_Cycle_000063.bin"
        )
        candidates = list(root.glob(pattern))
        if not candidates:
            rows.append(
                {
                    "channel": channel,
                    "database_samples": db_row["sample_count"],
                    "evidence_samples": None,
                    "difference": None,
                }
            )
            continue
        path = max(candidates, key=lambda item: item.stat().st_mtime)
        evidence_samples = path.stat().st_size // 32
        rows.append(
            {
                "channel": channel,
                "database_samples": db_row["sample_count"],
                "evidence_samples": evidence_samples,
                "difference": db_row["sample_count"] - evidence_samples,
                "evidence_bin": str(path),
            }
        )
    return rows


def inspect_database(root: Path) -> dict:
    db_path = root / "index.db"
    # Keep UNC backslashes in the SQLite URI. Converting to //server/share form
    # makes SQLite interpret the server name as a disallowed URI authority.
    connection = sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)
    try:
        integrity = connection.execute("PRAGMA integrity_check").fetchone()[0]
        duplicate_keys = connection.execute(
            """
            SELECT COUNT(*) FROM (
              SELECT epb_id,cycle_number,COUNT(*) n
              FROM epb_cycles
              GROUP BY epb_id,cycle_number
              HAVING n>1
            )
            """
        ).fetchone()[0]
        statuses = connection.execute(
            """
            SELECT epb_id,status,COUNT(*)
            FROM epb_cycles
            WHERE epb_id IN (8,9,10)
            GROUP BY epb_id,status
            ORDER BY epb_id,status
            """
        ).fetchall()
        cycle63 = connection.execute(
            """
            SELECT epb_id,sample_count,status,end_time
            FROM epb_cycles
            WHERE epb_id IN (9,10) AND cycle_number=63
            ORDER BY epb_id
            """
        ).fetchall()
    finally:
        connection.close()
    return {
        "integrity_check": integrity,
        "duplicate_epb_cycle_keys": duplicate_keys,
        "status_counts": [
            {"channel": row[0], "status": row[1], "count": row[2]} for row in statuses
        ],
        "cycle63_database": [
            {
                "channel": row[0],
                "sample_count": row[1],
                "status": row[2],
                "end_time": row[3],
            }
            for row in cycle63
        ],
    }


def inspect_warning_log(root: Path) -> dict:
    text = (root / "log" / "warning.log").read_text(encoding="utf-8-sig", errors="replace")
    rules = {
        "invalid_cutoff_observation": "控流观测无效，未更新预测模型",
        "peak_soft_warning": "正向实际峰值单圈超出平衡带",
        "undershoot_soft_warning": "正向实际峰值低于目标",
        "forward_soft_limit": "ForwardSoftLimit",
        "peak_capture_cancelled": "峰值捕获",
        "hard_fault": "AdaptiveHardFault",
        "snapshot_exported": "报警快照已导出",
    }
    return {name: text.count(fragment) for name, fragment in rules.items()}


def parse_utc(value: str) -> datetime:
    return datetime.fromisoformat(value.replace("Z", "+00:00"))


def inspect_power_telemetry(root: Path) -> dict:
    path = max((root / "PowerSupplyTelemetry").glob("*.csv"), key=lambda p: p.stat().st_size)
    group_counts = Counter()
    bad_flags = Counter()
    group4_alarm = datetime(2026, 7, 30, 14, 29, 27, tzinfo=timezone.utc)
    group4_first_off = None
    group4_last_on = None
    with path.open("r", encoding="utf-8-sig", newline="") as handle:
        for row in csv.DictReader(handle):
            group = int(row["ElectricalGroup"])
            group_counts[group] += 1
            for field in (
                "Connected",
                "CC",
                "VoltageLimited",
                "CurrentLimited",
                "PowerLimited",
                "ProtectionTripped",
            ):
                value = row[field].lower()
                bad = value == "false" if field == "Connected" else value == "true"
                if bad:
                    bad_flags[f"group{group}_{field}"] += 1
            if row["Error"]:
                bad_flags[f"group{group}_Error"] += 1
            if group == 4:
                utc = parse_utc(row["Utc"])
                output_on = row["Output"].lower() == "true"
                if utc >= group4_alarm and output_on:
                    group4_last_on = utc
                if utc >= group4_alarm and not output_on and group4_first_off is None:
                    group4_first_off = utc
    duration = None
    if group4_first_off:
        duration = (group4_first_off - group4_alarm).total_seconds()
    return {
        "file": str(path),
        "rows_by_group": dict(sorted(group_counts.items())),
        "bad_flag_counts": dict(sorted(bad_flags.items())),
        "group4_first_output_off_utc": group4_first_off.isoformat() if group4_first_off else None,
        "group4_last_output_on_utc": group4_last_on.isoformat() if group4_last_on else None,
        "group4_alarm_to_output_off_seconds": duration,
    }


def configure_matplotlib() -> None:
    plt.rcParams.update(
        {
            "font.sans-serif": ["Microsoft YaHei", "SimHei", "DejaVu Sans"],
            "axes.unicode_minus": False,
            "axes.edgecolor": "#6B7280",
            "axes.labelcolor": "#374151",
            "text.color": "#1F2937",
            "xtick.color": "#4B5563",
            "ytick.color": "#4B5563",
            "figure.facecolor": "white",
            "axes.facecolor": "white",
        }
    )


def main() -> int:
    args = parse_args()
    root = args.data_root.resolve()
    output_dir = args.output_dir.resolve()
    output_dir.mkdir(parents=True, exist_ok=True)
    configure_matplotlib()

    log_path = find_run_log(root)
    completions = parse_formal_completions(log_path)
    precision = summarize_precision(completions)
    precision_chart = output_dir / "v2_3_0_0_peak_error_cycles.png"
    timeline_chart = output_dir / "v2_3_0_0_cycle63_fault_timeline.png"
    plot_precision(completions, precision_chart)
    plot_cycle63(root, log_path, timeline_chart)

    database = inspect_database(root)
    summary = {
        "source_root": str(root),
        "run_log": str(log_path),
        "generated_at": datetime.now(timezone.utc).isoformat(),
        "precision": precision,
        "database": database,
        "alarm_snapshot_pairs": inspect_alarm_pairs(root),
        "alarm_database_evidence_alignment": inspect_alarm_database_alignment(
            root,
            database,
        ),
        "warning_log": inspect_warning_log(root),
        "power_telemetry": inspect_power_telemetry(root),
        "charts": [str(precision_chart), str(timeline_chart)],
    }
    summary_path = output_dir / "v2_3_0_0_analysis_summary.json"
    summary_path.write_text(
        json.dumps(summary, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    print(json.dumps(summary, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
