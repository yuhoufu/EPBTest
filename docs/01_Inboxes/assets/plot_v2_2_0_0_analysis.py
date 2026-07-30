#!/usr/bin/env python3
"""Reproduce the V2.2.0.0 peak-error and timing-precision figures."""

from __future__ import annotations

import argparse
import csv
import json
import re
from collections import defaultdict
from pathlib import Path

import matplotlib.pyplot as plt
from matplotlib import font_manager
from matplotlib.ticker import MultipleLocator


PEAK_PATTERN = re.compile(
    r"EPB\[(8|9|10)\] 自适应单圈完成：.*?"
    r"Peak=([+-]?\d+(?:\.\d+)?)A，Target=([+-]?\d+(?:\.\d+)?)A，"
    r"Error=([+-]?\d+(?:\.\d+)?)A"
)
START_PATTERN = re.compile(
    r"正式阶段启动 .*?EPB=(8|9|10).*?Cycle=(\d+).*?"
    r"DeviationMs=([+-]?\d+(?:\.\d+)?)"
)


def configure_chinese_font() -> None:
    candidates = [
        "Microsoft YaHei",
        "Microsoft YaHei UI",
        "SimHei",
        "Noto Sans CJK SC",
    ]
    installed = {item.name for item in font_manager.fontManager.ttflist}
    for candidate in candidates:
        if candidate in installed:
            plt.rcParams["font.sans-serif"] = [candidate]
            break
    plt.rcParams["axes.unicode_minus"] = False


def parse_run_log(path: Path):
    errors: dict[int, list[float]] = defaultdict(list)
    starts: dict[int, list[tuple[int, float]]] = defaultdict(list)
    for line in path.read_text(encoding="utf-8").splitlines():
        peak_match = PEAK_PATTERN.search(line)
        if peak_match:
            epb = int(peak_match.group(1))
            errors[epb].append(float(peak_match.group(4)))

        start_match = START_PATTERN.search(line)
        if start_match:
            epb = int(start_match.group(1))
            starts[epb].append(
                (int(start_match.group(2)), float(start_match.group(3)))
            )

    for epb in (8, 9, 10):
        if len(errors[epb]) != 30:
            raise ValueError(f"EPB{epb} expected 30 completed cycles, got {len(errors[epb])}")
        if len(starts[epb]) != 20:
            raise ValueError(f"EPB{epb} expected 20 formal starts, got {len(starts[epb])}")
        starts[epb].sort()
    return errors, starts


def csv_timestamp_quality(latest_root: Path):
    absolute_total = absolute_duplicates = 0
    relative_total = relative_duplicates = 0
    csv_count = 0

    for path in sorted(latest_root.glob("EPB*/**/EPB*_Cycle_*.csv")):
        csv_count += 1
        previous_absolute = None
        previous_relative = None
        with path.open("r", encoding="utf-8-sig", newline="") as stream:
            for row in csv.DictReader(stream):
                absolute = row["Timestamp"]
                relative = float(row["RelativeTimeSeconds"])
                if previous_absolute is not None:
                    absolute_total += 1
                    relative_total += 1
                    if absolute == previous_absolute:
                        absolute_duplicates += 1
                    if relative == previous_relative:
                        relative_duplicates += 1
                previous_absolute = absolute
                previous_relative = relative

    if csv_count != 30:
        raise ValueError(f"Expected 30 latest CSV files, got {csv_count}")
    return {
        "csv_count": csv_count,
        "absolute_duplicate_pct": absolute_duplicates * 100.0 / absolute_total,
        "relative_duplicate_pct": relative_duplicates * 100.0 / relative_total,
    }


def plot_peak_errors(errors: dict[int, list[float]], output_path: Path) -> None:
    colors = {8: "#2563EB", 9: "#E11D48", 10: "#059669"}
    fig, ax = plt.subplots(figsize=(16, 9))
    ax.axhspan(-0.8, 0.8, color="#DCFCE7", alpha=0.65, label="允许带 ±0.8 A")
    ax.axhline(0, color="#475569", linewidth=1)
    ax.axvline(10.5, color="#64748B", linestyle="--", linewidth=1.5)

    for epb in (8, 9, 10):
        cycles = list(range(1, 31))
        ax.plot(
            cycles,
            errors[epb],
            marker="o",
            markersize=5,
            linewidth=2,
            color=colors[epb],
            label=f"EPB{epb}",
        )

    warning_x = next(
        index + 1 for index, value in enumerate(errors[9]) if abs(value) > 0.8
    )
    warning_y = errors[9][warning_x - 1]
    ax.scatter(
        [warning_x],
        [warning_y],
        s=170,
        facecolor="#FDE047",
        edgecolor="#991B1B",
        linewidth=2,
        zorder=6,
    )
    ax.annotate(
        f"唯一警告：EPB9 学习第{warning_x}圈  {warning_y:+.3f} A",
        xy=(warning_x, warning_y),
        xytext=(warning_x + 2, 1.18),
        arrowprops={"arrowstyle": "->", "color": "#991B1B"},
        color="#991B1B",
        fontsize=12,
        fontweight="bold",
    )

    ax.text(5.5, -1.15, "学习阶段（10圈）", ha="center", fontsize=12)
    ax.text(20.5, -1.15, "正式阶段（20圈）", ha="center", fontsize=12)
    ax.set(
        title="V2.2.0.0 三卡钳 30 圈峰值误差",
        xlabel="各卡钳完成圈序号",
        ylabel="峰值误差（A）",
        xlim=(0.5, 30.5),
        ylim=(-1.3, 1.35),
    )
    ax.xaxis.set_major_locator(MultipleLocator(1))
    ax.grid(axis="y", color="#CBD5E1", alpha=0.7)
    ax.legend(loc="lower right", ncol=4)
    fig.tight_layout()
    fig.savefig(output_path, dpi=160, bbox_inches="tight")
    plt.close(fig)


def plot_timing(
    starts: dict[int, list[tuple[int, float]]],
    quality: dict[str, float],
    output_path: Path,
) -> None:
    colors = {8: "#2563EB", 9: "#E11D48", 10: "#059669"}
    fig, (ax_start, ax_quality) = plt.subplots(
        2,
        1,
        figsize=(16, 10),
        gridspec_kw={"height_ratios": [1.6, 1]},
    )

    for epb, label in ((8, "EPB8 / EPB10（同相位）"), (9, "EPB9")):
        ax_start.plot(
            [item[0] for item in starts[epb]],
            [item[1] for item in starts[epb]],
            marker="o",
            linewidth=2,
            color=colors[epb],
            label=label,
        )
    max_deviation = max(value for rows in starts.values() for _, value in rows)
    ax_start.set(
        title=f"正式阶段启动计划偏差（最大 {max_deviation:.3f} ms）",
        xlabel="正式圈号",
        ylabel="Actual − Planned（ms）",
        xlim=(0.5, 20.5),
    )
    ax_start.xaxis.set_major_locator(MultipleLocator(1))
    ax_start.grid(axis="y", color="#CBD5E1", alpha=0.7)
    ax_start.legend(ncol=2)

    labels = ["绝对 Timestamp", "RelativeTimeSeconds"]
    values = [
        quality["absolute_duplicate_pct"],
        quality["relative_duplicate_pct"],
    ]
    bars = ax_quality.barh(labels, values, color=["#F59E0B", "#EF4444"], height=0.55)
    ax_quality.set(
        title=f"现有最近10圈 CSV 相邻样本重复率（{quality['csv_count']} 个文件）",
        xlabel="与前一样本时间相同的比例（%）",
        xlim=(0, 65),
    )
    ax_quality.grid(axis="x", color="#CBD5E1", alpha=0.7)
    for bar, value in zip(bars, values):
        ax_quality.text(
            value + 1,
            bar.get_y() + bar.get_height() / 2,
            f"{value:.2f}%",
            va="center",
            fontweight="bold",
        )
    ax_quality.text(
        63,
        -0.72,
        "根因：旧实现按 1 ms 量化；2000 Hz 需要 0.5 ms",
        ha="right",
        color="#991B1B",
        fontsize=11,
    )

    fig.suptitle("V2.2.0.0 启动节拍与采样时间分辨率", fontsize=18, fontweight="bold")
    fig.tight_layout()
    fig.savefig(output_path, dpi=160, bbox_inches="tight")
    plt.close(fig)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--data-root",
        type=Path,
        default=Path(r"D:\EPB_Data\10364-009_V2.2.0.0_1"),
    )
    parser.add_argument(
        "--output-dir",
        type=Path,
        default=Path(__file__).resolve().parent,
    )
    args = parser.parse_args()

    configure_chinese_font()
    args.output_dir.mkdir(parents=True, exist_ok=True)
    errors, starts = parse_run_log(args.data_root / "log" / "run.log")
    quality = csv_timestamp_quality(args.data_root / "Latest")

    peak_path = args.output_dir / "v2_2_0_0_peak_error_30cycles.png"
    timing_path = args.output_dir / "v2_2_0_0_timing_precision.png"
    plot_peak_errors(errors, peak_path)
    plot_timing(starts, quality, timing_path)

    formal_stats = {}
    for epb in (8, 9, 10):
        formal = errors[epb][10:]
        formal_stats[str(epb)] = {
            "min_error_a": min(formal),
            "max_error_a": max(formal),
            "mean_error_a": sum(formal) / len(formal),
            "mae_a": sum(abs(value) for value in formal) / len(formal),
        }

    print(
        json.dumps(
            {
                "peak_figure": str(peak_path),
                "timing_figure": str(timing_path),
                "formal_stats": formal_stats,
                "timestamp_quality": quality,
            },
            ensure_ascii=False,
            indent=2,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
