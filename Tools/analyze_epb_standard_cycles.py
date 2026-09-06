#!/usr/bin/env python3
"""Analyze standard EPB cycle CSV/BIN files and generate reproducible evidence.

Structural data errors return a non-zero exit code. Waveform-quality findings
are recorded in the metrics CSV and printed as warnings, but do not prevent
report artifacts from being generated.
"""

from __future__ import annotations

import argparse
import re
import sqlite3
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable

import matplotlib

matplotlib.use("Agg")

import matplotlib.pyplot as plt
import numpy as np
import pandas as pd
from matplotlib import font_manager


REQUIRED_COLUMNS = [
    "Timestamp",
    "RelativeTimeSeconds",
    "Cycle",
    "SampleIndex",
    "EpbCurrent",
    "GroupPressure",
]
BIN_RECORD_BYTES = 32
POWERED_THRESHOLD_A = 0.1
FORWARD_TRIGGER_A = 13.0
REVERSE_DECAY_A = 3.0
OVERCURRENT_ALARM_A = 18.0
REGION_GAP_S = 0.2
TAIL_EVIDENCE_S = 0.2

PALETTE = {
    "baseline": "#2F6BFF",
    "first": "#E68619",
    "last": "#D64550",
    "pressure": "#179C8C",
    "grid": "#D9DEE8",
    "threshold": "#7B61A8",
    "alarm": "#C83232",
    "text": "#263238",
}


class AnalysisError(RuntimeError):
    """Raised when source data has a structural error."""


@dataclass
class CycleData:
    cycle: int
    csv_path: Path
    bin_path: Path
    frame: pd.DataFrame
    bin_record_count: int


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Analyze EPB standard-cycle CSV/BIN data and generate PNG/CSV evidence."
    )
    parser.add_argument("--input-dir", type=Path, required=True, help="Directory containing cycle CSV/BIN pairs.")
    parser.add_argument("--index-db", type=Path, help="Optional SQLite index.db containing epb_cycles.")
    parser.add_argument("--epb-id", type=int, required=True, help="EPB channel number, for example 10.")
    parser.add_argument("--output-dir", type=Path, required=True, help="Directory for generated PNG and CSV files.")
    return parser.parse_args()


def configure_chinese_font() -> str:
    candidates = [
        "Microsoft YaHei",
        "Microsoft JhengHei",
        "SimHei",
        "Noto Sans CJK SC",
        "Source Han Sans CN",
        "Arial Unicode MS",
        "DejaVu Sans",
    ]
    installed = {font.name for font in font_manager.fontManager.ttflist}
    selected = next((name for name in candidates if name in installed), "DejaVu Sans")
    plt.rcParams.update(
        {
            "font.family": "sans-serif",
            "font.sans-serif": [selected, "DejaVu Sans"],
            "axes.unicode_minus": False,
            "axes.edgecolor": "#9AA4B2",
            "axes.labelcolor": PALETTE["text"],
            "axes.titlecolor": PALETTE["text"],
            "xtick.color": "#4F5B66",
            "ytick.color": "#4F5B66",
            "figure.facecolor": "white",
            "axes.facecolor": "white",
        }
    )
    return selected


def cycle_number_from_name(path: Path, epb_id: int) -> int:
    match = re.fullmatch(rf"EPB{epb_id}_Cycle_(\d{{6}})\.csv", path.name, flags=re.IGNORECASE)
    if not match:
        raise AnalysisError(f"CSV 文件名不符合 EPB{epb_id}_Cycle_XXXXXX.csv：{path.name}")
    return int(match.group(1))


def validate_and_load(input_dir: Path, epb_id: int) -> list[CycleData]:
    if not input_dir.is_dir():
        raise AnalysisError(f"输入目录不存在：{input_dir}")

    csv_paths = sorted(input_dir.glob(f"EPB{epb_id}_Cycle_*.csv"))
    bin_paths = sorted(input_dir.glob(f"EPB{epb_id}_Cycle_*.bin"))
    if not csv_paths:
        raise AnalysisError(f"未找到 EPB{epb_id} 的 CSV 文件：{input_dir}")
    if len(csv_paths) != len(bin_paths):
        raise AnalysisError(f"CSV/BIN 数量不一致：CSV={len(csv_paths)}，BIN={len(bin_paths)}")

    cycles: list[CycleData] = []
    seen_numbers: set[int] = set()
    for csv_path in csv_paths:
        cycle = cycle_number_from_name(csv_path, epb_id)
        if cycle in seen_numbers:
            raise AnalysisError(f"圈号重复：{cycle}")
        seen_numbers.add(cycle)

        bin_path = csv_path.with_suffix(".bin")
        if not bin_path.is_file():
            raise AnalysisError(f"缺少配对 BIN：{bin_path}")
        bin_size = bin_path.stat().st_size
        if bin_size % BIN_RECORD_BYTES:
            raise AnalysisError(
                f"BIN 大小不是 {BIN_RECORD_BYTES} 字节记录的整数倍：{bin_path.name}={bin_size}"
            )

        frame = pd.read_csv(csv_path)
        missing = [column for column in REQUIRED_COLUMNS if column not in frame.columns]
        if missing:
            raise AnalysisError(f"{csv_path.name} 缺少字段：{', '.join(missing)}")
        frame = frame[REQUIRED_COLUMNS].copy()
        if frame.empty:
            raise AnalysisError(f"{csv_path.name} 为空")
        if frame.isna().any().any():
            details = frame.isna().sum()
            details = details[details > 0].to_dict()
            raise AnalysisError(f"{csv_path.name} 存在空值：{details}")

        for column in ["RelativeTimeSeconds", "Cycle", "SampleIndex", "EpbCurrent", "GroupPressure"]:
            frame[column] = pd.to_numeric(frame[column], errors="raise")

        expected_index = np.arange(len(frame), dtype=np.int64)
        actual_index = frame["SampleIndex"].to_numpy(dtype=np.int64)
        if not np.array_equal(actual_index, expected_index):
            raise AnalysisError(f"{csv_path.name} 的 SampleIndex 不是从 0 开始的连续序列")

        cycle_values = frame["Cycle"].astype(int).unique()
        if len(cycle_values) != 1 or int(cycle_values[0]) != cycle:
            raise AnalysisError(
                f"{csv_path.name} 的 Cycle 字段与文件圈号不一致：{cycle_values.tolist()}"
            )

        relative_time = frame["RelativeTimeSeconds"].to_numpy(dtype=float)
        if np.any(np.diff(relative_time) < 0):
            raise AnalysisError(f"{csv_path.name} 的 RelativeTimeSeconds 存在回退")

        bin_record_count = bin_size // BIN_RECORD_BYTES
        if bin_record_count != len(frame):
            raise AnalysisError(
                f"{csv_path.name} 与 {bin_path.name} 样本数不一致：CSV={len(frame)}，BIN={bin_record_count}"
            )

        cycles.append(CycleData(cycle, csv_path, bin_path, frame, bin_record_count))

    numbers = [item.cycle for item in cycles]
    expected_numbers = list(range(min(numbers), max(numbers) + 1))
    if numbers != expected_numbers:
        raise AnalysisError(f"圈号不连续：实际={numbers}，期望={expected_numbers}")
    return cycles


def powered_regions(time_s: np.ndarray, current_a: np.ndarray) -> list[tuple[int, int]]:
    powered_idx = np.flatnonzero(current_a > POWERED_THRESHOLD_A)
    if powered_idx.size == 0:
        return []
    breaks = np.flatnonzero(np.diff(time_s[powered_idx]) > REGION_GAP_S)
    starts = np.r_[0, breaks + 1]
    ends = np.r_[breaks, powered_idx.size - 1]
    return [(int(powered_idx[start]), int(powered_idx[end])) for start, end in zip(starts, ends)]


def first_sustained_above(
    time_s: np.ndarray,
    values: np.ndarray,
    start_s: float,
    threshold: float,
    lookahead_s: float,
    quantile: float,
) -> int | None:
    start_idx = int(np.searchsorted(time_s, start_s, side="left"))
    for index in range(start_idx, len(time_s)):
        if values[index] <= threshold:
            continue
        end_idx = int(np.searchsorted(time_s, time_s[index] + lookahead_s, side="right"))
        if end_idx > index and float(np.quantile(values[index:end_idx], quantile)) > threshold * 0.8:
            return index
    return None


def first_sustained_below(
    time_s: np.ndarray,
    values: np.ndarray,
    start_s: float,
    threshold: float,
    lookahead_s: float,
    max_allowed: float,
) -> int | None:
    start_idx = int(np.searchsorted(time_s, start_s, side="left"))
    for index in range(start_idx, len(time_s)):
        if values[index] > threshold:
            continue
        end_idx = int(np.searchsorted(time_s, time_s[index] + lookahead_s, side="right"))
        if end_idx > index and float(np.max(values[index:end_idx])) < max_allowed:
            return index
    return None


def first_index(values: np.ndarray, predicate: np.ndarray, start: int, end: int) -> int | None:
    local = np.flatnonzero(predicate[start : end + 1])
    return int(start + local[0]) if local.size else None


def safe_median(values: np.ndarray, start: int, end: int) -> float:
    if end <= start:
        return float("nan")
    return float(np.median(values[start:end]))


def analyze_cycle(item: CycleData) -> tuple[dict[str, object], list[str]]:
    frame = item.frame
    time_s = frame["RelativeTimeSeconds"].to_numpy(dtype=float)
    current_a = np.abs(frame["EpbCurrent"].to_numpy(dtype=float))
    pressure_bar = frame["GroupPressure"].to_numpy(dtype=float)

    regions = powered_regions(time_s, current_a)
    if len(regions) < 2:
        raise AnalysisError(f"{item.csv_path.name} 无法识别正向和反向两个上电段")
    forward_start, forward_end = regions[0]
    reverse_start, reverse_end = regions[1]

    load_idx = first_sustained_above(
        time_s,
        current_a,
        time_s[forward_start] + 0.35,
        threshold=1.0,
        lookahead_s=0.1,
        quantile=0.1,
    )
    if load_idx is None or load_idx > forward_end:
        raise AnalysisError(f"{item.csv_path.name} 无法识别正向负载爬升起点")

    trigger_idx = first_index(
        current_a,
        current_a >= FORWARD_TRIGGER_A,
        load_idx,
        forward_end,
    )
    if trigger_idx is None:
        raise AnalysisError(f"{item.csv_path.name} 未达到 {FORWARD_TRIGGER_A:g} A 提前断电触发线")

    forward_peak_local = int(np.argmax(current_a[forward_start : forward_end + 1]))
    forward_peak_idx = forward_start + forward_peak_local
    reverse_inrush_end = int(
        min(reverse_end + 1, np.searchsorted(time_s, time_s[reverse_start] + 0.2, side="right"))
    )
    reverse_inrush_peak = float(np.max(current_a[reverse_start:reverse_inrush_end]))
    forward_inrush_end = int(
        min(forward_end + 1, np.searchsorted(time_s, time_s[forward_start] + 0.2, side="right"))
    )
    forward_inrush_peak = float(np.max(current_a[forward_start:forward_inrush_end]))

    reverse_decay_idx = first_sustained_below(
        time_s,
        current_a,
        time_s[reverse_start] + 0.2,
        threshold=REVERSE_DECAY_A,
        lookahead_s=0.1,
        max_allowed=3.2,
    )
    if reverse_decay_idx is None or reverse_decay_idx > reverse_end:
        raise AnalysisError(f"{item.csv_path.name} 无法识别反向衰减到 {REVERSE_DECAY_A:g} A")

    forward_empty_start = int(np.searchsorted(time_s, time_s[forward_start] + 0.3, side="left"))
    forward_empty_end = int(np.searchsorted(time_s, time_s[load_idx] - 0.1, side="left"))
    reverse_empty_start = int(np.searchsorted(time_s, time_s[reverse_decay_idx] + 0.2, side="left"))
    reverse_empty_end = int(np.searchsorted(time_s, time_s[reverse_end] - 0.1, side="left"))

    timestamp = pd.to_datetime(frame["Timestamp"], errors="raise")
    timestamp_step_ms = timestamp.diff().dt.total_seconds().mul(1000.0)
    timestamp_backsteps = int((timestamp_step_ms < 0).sum())
    timestamp_min_step_ms = (
        float(timestamp_step_ms[timestamp_step_ms < 0].min()) if timestamp_backsteps else 0.0
    )

    pressure_start_idx = first_index(
        pressure_bar,
        pressure_bar > 5.0,
        0,
        len(pressure_bar) - 1,
    )
    pressure_release_idx = first_index(
        pressure_bar,
        pressure_bar < 70.0,
        forward_end,
        len(pressure_bar) - 1,
    )
    pressure_zero_idx = first_index(
        pressure_bar,
        pressure_bar < 1.0,
        forward_end,
        len(pressure_bar) - 1,
    )

    tail_duration_s = float(time_s[-1] - time_s[reverse_end])
    evidence_complete = bool(
        tail_duration_s >= TAIL_EVIDENCE_S
        and float(np.max(current_a[reverse_end + 1 :])) <= POWERED_THRESHOLD_A
    )
    warnings: list[str] = []
    if timestamp_backsteps:
        warnings.append(f"wall_timestamp_backstep:{timestamp_backsteps}")
    if not evidence_complete:
        warnings.append("tail_evidence_incomplete")

    result: dict[str, object] = {
        "cycle": item.cycle,
        "csv_file": item.csv_path.name,
        "rows": len(frame),
        "bin_record_count": item.bin_record_count,
        "record_duration_s": float(time_s[-1] - time_s[0]),
        "forward_start_s": float(time_s[forward_start]),
        "forward_inrush_peak_a": forward_inrush_peak,
        "forward_empty_current_a": safe_median(current_a, forward_empty_start, forward_empty_end),
        "load_rise_start_s": float(time_s[load_idx]),
        "cutoff_13a_s": float(time_s[trigger_idx]),
        "forward_peak_a": float(current_a[forward_peak_idx]),
        "forward_peak_s": float(time_s[forward_peak_idx]),
        "forward_off_s": float(time_s[forward_end]),
        "hold_duration_s": float(time_s[reverse_start] - time_s[forward_end]),
        "reverse_start_s": float(time_s[reverse_start]),
        "reverse_inrush_peak_a": reverse_inrush_peak,
        "reverse_decay_3a_s": float(time_s[reverse_decay_idx]),
        "reverse_decay_duration_s": float(time_s[reverse_decay_idx] - time_s[reverse_start]),
        "reverse_empty_current_a": safe_median(current_a, reverse_empty_start, reverse_empty_end),
        "reverse_end_s": float(time_s[reverse_end]),
        "reverse_on_duration_s": float(time_s[reverse_end] - time_s[reverse_start]),
        "tail_zero_duration_s": tail_duration_s,
        "evidence_complete": evidence_complete,
        "final_current_a": float(current_a[-1]),
        "pressure_start_s": float(time_s[pressure_start_idx]) if pressure_start_idx is not None else np.nan,
        "pressure_peak_bar": float(np.max(pressure_bar)),
        "pressure_release_70bar_s": (
            float(time_s[pressure_release_idx]) if pressure_release_idx is not None else np.nan
        ),
        "pressure_zero_s": float(time_s[pressure_zero_idx]) if pressure_zero_idx is not None else np.nan,
        "timestamp_backsteps": timestamp_backsteps,
        "timestamp_min_step_ms": timestamp_min_step_ms,
        "db_status": "",
        "db_sample_count": np.nan,
        "db_start_interval_s": np.nan,
        "db_active_s": np.nan,
        "db_off_to_next_s": np.nan,
        "quality_flags": ";".join(warnings),
    }
    return result, warnings


def merge_database(metrics: pd.DataFrame, db_path: Path, epb_id: int) -> pd.DataFrame:
    if not db_path.is_file():
        raise AnalysisError(f"index.db 不存在：{db_path}")
    try:
        with sqlite3.connect(db_path) as connection:
            rows = pd.read_sql_query(
                """
                SELECT cycle_number, start_time, end_time, sample_count, status
                FROM epb_cycles
                WHERE epb_id = ?
                ORDER BY cycle_number
                """,
                connection,
                params=(epb_id,),
            )
    except sqlite3.Error as exc:
        raise AnalysisError(f"读取 index.db 失败：{exc}") from exc

    wanted = set(metrics["cycle"].astype(int))
    rows = rows[rows["cycle_number"].isin(wanted)].copy()
    if set(rows["cycle_number"].astype(int)) != wanted:
        missing = sorted(wanted - set(rows["cycle_number"].astype(int)))
        raise AnalysisError(f"index.db 缺少 EPB{epb_id} 圈记录：{missing}")
    if rows["cycle_number"].duplicated().any():
        duplicated = rows.loc[rows["cycle_number"].duplicated(), "cycle_number"].tolist()
        raise AnalysisError(f"index.db 存在重复圈记录：{duplicated}")

    rows["start_dt"] = pd.to_datetime(rows["start_time"], errors="raise")
    rows["end_dt"] = pd.to_datetime(rows["end_time"], errors="raise")
    rows["db_start_interval_s"] = rows["start_dt"].diff().dt.total_seconds()
    rows["db_active_s"] = (rows["end_dt"] - rows["start_dt"]).dt.total_seconds()
    rows["db_off_to_next_s"] = (
        rows["start_dt"].shift(-1) - rows["end_dt"]
    ).dt.total_seconds()
    db_columns = rows[
        [
            "cycle_number",
            "status",
            "sample_count",
            "db_start_interval_s",
            "db_active_s",
            "db_off_to_next_s",
        ]
    ].rename(
        columns={
            "cycle_number": "cycle",
            "status": "db_status_value",
            "sample_count": "db_sample_count_value",
        }
    )
    placeholder_columns = [
        "db_status",
        "db_sample_count",
        "db_start_interval_s",
        "db_active_s",
        "db_off_to_next_s",
    ]
    merged = metrics.drop(columns=placeholder_columns).merge(
        db_columns,
        on="cycle",
        how="left",
        validate="one_to_one",
    )
    if (merged["rows"] != merged["db_sample_count_value"]).any():
        bad = merged.loc[
            merged["rows"] != merged["db_sample_count_value"],
            ["cycle", "rows", "db_sample_count_value"],
        ].to_dict("records")
        raise AnalysisError(f"CSV 与 index.db 样本数不一致：{bad}")
    merged["db_status"] = merged.pop("db_status_value")
    merged["db_sample_count"] = merged.pop("db_sample_count_value")

    warning_mask = merged["db_status"].astype(str).str.lower() != "completed"
    for index in merged.index[warning_mask]:
        flag = f"db_status:{merged.at[index, 'db_status']}"
        existing = str(merged.at[index, "quality_flags"])
        merged.at[index, "quality_flags"] = ";".join(filter(None, [existing, flag]))
    return merged


def plot_all_cycles(cycles: Iterable[CycleData], output_path: Path) -> None:
    cycle_list = list(cycles)
    fig, ax = plt.subplots(figsize=(13.5, 7.2))
    baseline_label_added = False
    for item in cycle_list:
        frame = item.frame
        if item.cycle == cycle_list[0].cycle:
            color, width, alpha, linestyle, label = PALETTE["first"], 2.0, 0.95, "--", "第1圈（初始位置差异）"
        elif item.cycle == cycle_list[-1].cycle:
            color, width, alpha, linestyle, label = PALETTE["last"], 2.2, 0.95, "--", "第10圈（尾段证据缺口）"
        else:
            color, width, alpha, linestyle = PALETTE["baseline"], 1.25, 0.46, "-"
            label = "第2～9圈稳定基线" if not baseline_label_added else "_nolegend_"
            baseline_label_added = True
        ax.plot(
            frame["RelativeTimeSeconds"],
            np.abs(frame["EpbCurrent"]),
            color=color,
            linewidth=width,
            alpha=alpha,
            linestyle=linestyle,
            label=label,
        )

    ax.set_title("EPB10 十圈电流幅值叠加")
    ax.set_xlabel("圈内相对时间（s）")
    ax.set_ylabel("电流幅值（A）")
    ax.set_xlim(left=0)
    ax.set_ylim(0, 16.5)
    ax.grid(True, color=PALETTE["grid"], linewidth=0.8)
    ax.legend(loc="upper right", frameon=True, framealpha=0.95)
    ax.text(
        0.01,
        0.98,
        "CSV 保存电流幅值；正/反方向由 DO 命令决定。",
        transform=ax.transAxes,
        va="top",
        ha="left",
        fontsize=10,
        color="#52606D",
    )
    fig.tight_layout()
    fig.savefig(output_path, dpi=180, bbox_inches="tight")
    plt.close(fig)


def plot_representative_cycle(
    item: CycleData,
    metric: pd.Series,
    output_path: Path,
) -> None:
    frame = item.frame
    time_s = frame["RelativeTimeSeconds"].to_numpy(dtype=float)
    current_a = np.abs(frame["EpbCurrent"].to_numpy(dtype=float))
    pressure_bar = frame["GroupPressure"].to_numpy(dtype=float)

    fig, (ax_current, ax_pressure) = plt.subplots(
        2,
        1,
        figsize=(13.5, 9.0),
        sharex=True,
        constrained_layout=True,
        gridspec_kw={"height_ratios": [1.35, 1.0], "hspace": 0.12},
    )
    ax_current.plot(time_s, current_a, color=PALETTE["baseline"], linewidth=1.7, label="电流幅值")
    ax_current.axhline(
        FORWARD_TRIGGER_A,
        color=PALETTE["threshold"],
        linewidth=1.3,
        linestyle="--",
        label="13 A 提前断电触发线",
    )
    ax_current.axhline(
        OVERCURRENT_ALARM_A,
        color=PALETTE["alarm"],
        linewidth=1.3,
        linestyle=":",
        label="18 A 过流报警线",
    )
    ax_current.set_ylabel("电流幅值（A）")
    ax_current.set_ylim(0, 19.2)
    ax_current.grid(True, color=PALETTE["grid"], linewidth=0.8)
    ax_current.legend(loc="upper right", ncol=3, fontsize=9, framealpha=0.95)
    ax_current.set_title("EPB10 第5圈电流与液压管压分段")

    boundaries = [
        0.0,
        float(metric["forward_start_s"]),
        min(float(metric["forward_start_s"]) + 0.20, float(metric["load_rise_start_s"])),
        float(metric["load_rise_start_s"]),
        float(metric["forward_off_s"]),
        float(metric["reverse_start_s"]),
        min(float(metric["reverse_start_s"]) + 0.20, float(metric["reverse_decay_3a_s"])),
        float(metric["reverse_decay_3a_s"]),
        float(time_s[-1]),
    ]
    stage_names = ["①", "②", "③", "④", "⑤", "⑥", "⑦", "⑧"]
    stage_colors = ["#F3F5F8", "#FFF0D9", "#EAF2FF", "#FDE9E7", "#F3F5F8", "#FFF0D9", "#EAF2FF", "#F3F5F8"]
    for stage, left, right, color in zip(stage_names, boundaries[:-1], boundaries[1:], stage_colors):
        if right <= left:
            continue
        ax_current.axvspan(left, right, color=color, alpha=0.50, zorder=0)
        ax_current.text(
            (left + right) / 2,
            17.2,
            stage,
            ha="center",
            va="center",
            fontsize=12,
            fontweight="bold",
            color=PALETTE["text"],
        )

    ax_pressure.plot(time_s, pressure_bar, color=PALETTE["pressure"], linewidth=1.7, label="液压组2管压")
    ax_pressure.axhline(70, color="#5B8E7D", linewidth=1.1, linestyle="--", label="70 bar 释压观察线")
    ax_pressure.set_xlabel("圈内相对时间（s）")
    ax_pressure.set_ylabel("管压（bar）")
    ax_pressure.set_ylim(-3, 85)
    ax_pressure.set_xlim(0, float(time_s[-1]))
    ax_pressure.grid(True, color=PALETTE["grid"], linewidth=0.8)
    ax_pressure.legend(loc="upper right", fontsize=9, framealpha=0.95)
    ax_pressure.text(
        0.01,
        0.04,
        "压力先建压并保持，正向夹紧完成后释压；压力回零后进入反向释放。",
        transform=ax_pressure.transAxes,
        va="bottom",
        fontsize=10,
        color="#52606D",
    )
    fig.savefig(output_path, dpi=180, bbox_inches="tight")
    plt.close(fig)


def plot_metric_consistency(metrics: pd.DataFrame, output_path: Path) -> None:
    panels = [
        ("forward_peak_a", "正向峰值", "A"),
        ("hold_duration_s", "正向断电至反向上电", "s"),
        ("reverse_decay_duration_s", "反向衰减至 3 A", "s"),
        ("pressure_peak_bar", "压力峰值", "bar"),
    ]
    fig, axes = plt.subplots(2, 2, figsize=(13.5, 8.5), sharex=True)
    baseline = metrics[metrics["cycle"].between(2, 9)]
    for ax, (column, title, unit) in zip(axes.flat, panels):
        median = float(baseline[column].median())
        low = float(baseline[column].min())
        high = float(baseline[column].max())
        ax.axhspan(low, high, color=PALETTE["baseline"], alpha=0.10, label="第2～9圈范围")
        ax.axhline(median, color=PALETTE["baseline"], linewidth=1.2, linestyle="--", alpha=0.75)
        for _, row in metrics.iterrows():
            if row["cycle"] == metrics["cycle"].min():
                color, marker, label = PALETTE["first"], "D", "第1圈"
            elif row["cycle"] == metrics["cycle"].max():
                color, marker, label = PALETTE["last"], "X", "第10圈"
            else:
                color, marker, label = PALETTE["baseline"], "o", "第2～9圈"
            ax.scatter(row["cycle"], row[column], color=color, marker=marker, s=55, zorder=3, label=label)
        handles, labels = ax.get_legend_handles_labels()
        unique = dict(zip(labels, handles))
        ax.legend(unique.values(), unique.keys(), fontsize=8, loc="best", framealpha=0.92)
        ax.set_title(f"{title}（{unit}）")
        ax.set_ylabel(unit)
        ax.set_xticks(metrics["cycle"])
        ax.grid(True, color=PALETTE["grid"], linewidth=0.8)

    axes[1, 0].set_xlabel("圈号")
    axes[1, 1].set_xlabel("圈号")
    fig.suptitle("EPB10 十圈关键指标一致性", fontsize=16, color=PALETTE["text"], y=0.995)
    fig.tight_layout(rect=(0, 0, 1, 0.975))
    fig.savefig(output_path, dpi=180, bbox_inches="tight")
    plt.close(fig)


def format_metrics(metrics: pd.DataFrame) -> pd.DataFrame:
    float_columns = list(metrics.select_dtypes(include=[np.floating]).columns)
    three_decimal_columns = [column for column in float_columns if column != "db_start_interval_s"]
    metrics.loc[:, three_decimal_columns] = metrics.loc[:, three_decimal_columns].round(3)
    if "db_start_interval_s" in metrics.columns:
        metrics.loc[:, "db_start_interval_s"] = metrics["db_start_interval_s"].round(6)
    return metrics


def print_summary(metrics: pd.DataFrame, font_name: str, output_dir: Path) -> None:
    baseline = metrics[metrics["cycle"].between(2, 9)]
    intervals = metrics["db_start_interval_s"].dropna()
    print(f"使用字体：{font_name}")
    print(f"输出目录：{output_dir}")
    print(f"圈数：{len(metrics)}；CSV/BIN 样本数全部一致")
    if not intervals.empty:
        print(f"数据库起点间隔中位数：{intervals.median():.6f} s")
    print(
        "第2～9圈："
        f"正向峰值 {baseline['forward_peak_a'].min():.3f}～{baseline['forward_peak_a'].max():.3f} A；"
        f"保持 {baseline['hold_duration_s'].min():.3f}～{baseline['hold_duration_s'].max():.3f} s；"
        f"反向衰减 {baseline['reverse_decay_duration_s'].min():.3f}～"
        f"{baseline['reverse_decay_duration_s'].max():.3f} s；"
        f"压力峰值 {baseline['pressure_peak_bar'].min():.1f}～"
        f"{baseline['pressure_peak_bar'].max():.1f} bar"
    )
    warning_rows = metrics[metrics["quality_flags"].astype(str) != ""]
    if not warning_rows.empty:
        print("波形/时间戳警告（不影响产物生成）：", file=sys.stderr)
        for _, row in warning_rows.iterrows():
            print(f"  Cycle {int(row['cycle'])}: {row['quality_flags']}", file=sys.stderr)


def main() -> int:
    args = parse_args()
    try:
        cycles = validate_and_load(args.input_dir.resolve(), args.epb_id)
        rows: list[dict[str, object]] = []
        for item in cycles:
            result, _ = analyze_cycle(item)
            rows.append(result)
        metrics = pd.DataFrame(rows).sort_values("cycle").reset_index(drop=True)
        if args.index_db:
            metrics = merge_database(metrics, args.index_db.resolve(), args.epb_id)

        args.output_dir.mkdir(parents=True, exist_ok=True)
        font_name = configure_chinese_font()
        epb_label = f"EPB{args.epb_id}"
        metrics_path = args.output_dir / f"{epb_label}_10圈关键指标.csv"
        overlay_path = args.output_dir / f"{epb_label}_10圈电流叠加.png"
        representative_path = args.output_dir / f"{epb_label}_第5圈电流与管压分段.png"
        consistency_path = args.output_dir / f"{epb_label}_关键指标一致性.png"

        format_metrics(metrics.copy()).to_csv(metrics_path, index=False, encoding="utf-8-sig")
        plot_all_cycles(cycles, overlay_path)
        representative_cycle = next((item for item in cycles if item.cycle == 5), None)
        if representative_cycle is None:
            raise AnalysisError("代表圈图要求存在第 5 圈")
        representative_metric = metrics.loc[metrics["cycle"] == 5].iloc[0]
        plot_representative_cycle(representative_cycle, representative_metric, representative_path)
        plot_metric_consistency(metrics, consistency_path)
        print_summary(metrics, font_name, args.output_dir.resolve())
        return 0
    except (AnalysisError, OSError, ValueError, pd.errors.ParserError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
