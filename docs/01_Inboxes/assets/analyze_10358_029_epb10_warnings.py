from __future__ import annotations

import bisect
import csv
import json
import math
import re
import sqlite3
import statistics
from collections import Counter, defaultdict
from datetime import datetime, timedelta, timezone
from pathlib import Path


DATA_ROOT = Path(r"D:\EPB_Data\10358-029_2021_backup")
ASSET_DIR = Path(__file__).resolve().parent
JSON_OUTPUT = ASSET_DIR / "10358_029_epb10_warning_analysis.json"
DB_OUTPUT = ASSET_DIR / "10358_029_epb10_warning_report.sqlite"
RUN_ID = "2f5c391c75944807a38a72a9659ef1d3"


def percentile(values: list[float], p: float) -> float:
    values = sorted(values)
    pos = (len(values) - 1) * p
    lo = math.floor(pos)
    hi = math.ceil(pos)
    if lo == hi:
        return values[lo]
    return values[lo] + (values[hi] - values[lo]) * (pos - lo)


def read_log(name: str) -> list[str]:
    return (DATA_ROOT / "log" / name).read_text(
        encoding="utf-8-sig", errors="replace"
    ).splitlines()


def parse_outcomes(run_lines: list[str]) -> list[dict]:
    pattern = re.compile(
        r"^(?P<timestamp>.{23}).*EPB\[(?P<channel>\d+)\] 自适应单圈完成："
        r"Fwd=(?P<forward_ms>\d+)ms，Rev=(?P<reverse_ms>\d+)ms，.*?"
        r"FullRatePeak=(?P<peak_a>[\d.]+)A，Target=(?P<target_a>[\d.]+)A，"
        r"Error=(?P<error_a>[+-]?[\d.]+)A，.*?结果=(?P<result>[^。]+)"
    )
    rows: list[dict] = []
    for line in run_lines:
        match = pattern.search(line)
        if not match:
            continue
        item = match.groupdict()
        rows.append(
            {
                "timestamp": item["timestamp"],
                "channel": int(item["channel"]),
                "forward_ms": int(item["forward_ms"]),
                "reverse_ms": int(item["reverse_ms"]),
                "peak_a": float(item["peak_a"]),
                "target_a": float(item["target_a"]),
                "error_a": float(item["error_a"]),
                "result": item["result"],
            }
        )
    return rows


def summarize_channels(outcomes: list[dict]) -> list[dict]:
    result: list[dict] = []
    for channel in sorted({row["channel"] for row in outcomes}):
        rows = [row for row in outcomes if row["channel"] == channel]
        peaks = [row["peak_a"] for row in rows]
        warning_count = sum(row["result"] == "SuccessWithWarning" for row in rows)
        edge_count = min(40, len(rows) // 2)
        first = rows[:edge_count]
        last = rows[-edge_count:]
        first_peak = statistics.median(row["peak_a"] for row in first)
        last_peak = statistics.median(row["peak_a"] for row in last)
        result.append(
            {
                "channel": f"EPB{channel}",
                "completed_cycles": len(rows),
                "warning_cycles": warning_count,
                "warning_rate_pct": round(100 * warning_count / len(rows), 1),
                "peak_median_a": round(statistics.median(peaks), 4),
                "peak_p10_a": round(percentile(peaks, 0.10), 4),
                "peak_min_a": min(peaks),
                "peak_max_a": max(peaks),
                "first_40_peak_median_a": round(first_peak, 4),
                "last_40_peak_median_a": round(last_peak, 4),
                "peak_change_a": round(last_peak - first_peak, 4),
                "first_40_forward_ms": statistics.median(row["forward_ms"] for row in first),
                "last_40_forward_ms": statistics.median(row["forward_ms"] for row in last),
            }
        )
    return result


def epb10_time_bins(outcomes: list[dict]) -> list[dict]:
    groups: dict[datetime, list[dict]] = defaultdict(list)
    for row in outcomes:
        if row["channel"] != 10:
            continue
        timestamp = datetime.strptime(row["timestamp"], "%Y-%m-%d %H:%M:%S.%f")
        bucket = timestamp.replace(
            minute=(timestamp.minute // 10) * 10,
            second=0,
            microsecond=0,
        )
        groups[bucket].append(row)
    return [
        {
            "time_bin": bucket.strftime("%H:%M"),
            "cycle_count": len(rows),
            "median_peak_a": round(statistics.median(row["peak_a"] for row in rows), 4),
            "mean_peak_a": round(statistics.mean(row["peak_a"] for row in rows), 4),
            "warning_cycles": sum(row["result"] == "SuccessWithWarning" for row in rows),
            "warning_rate_pct": round(
                100 * sum(row["result"] == "SuccessWithWarning" for row in rows) / len(rows),
                1,
            ),
        }
        for bucket, rows in sorted(groups.items())
    ]


def warning_analysis(warning_lines: list[str], ui_lines: list[str]) -> dict:
    categories: dict[int, Counter] = defaultdict(Counter)
    plateau_rows: list[dict] = []
    below_floor_rows: list[dict] = []
    for line in warning_lines:
        channel_match = re.search(r"EPB\[(\d+)\]", line)
        if not channel_match:
            continue
        channel = int(channel_match.group(1))
        if "ClampReachedNearTargetPlateau" in line:
            category = "NearTargetPlateau"
            match = re.search(
                r"Peak=([\d.]+)A I=([\d.]+)A Floor=([\d.]+)A "
                r"Target=([\d.]+)A slope=([\d.]+)A/ms confirm=(\d+)ms",
                line,
            )
            if match:
                values = match.groups()
                plateau_rows.append(
                    {
                        "timestamp": line[:23],
                        "channel": channel,
                        "peak_a": float(values[0]),
                        "instant_current_a": float(values[1]),
                        "acceptable_floor_a": float(values[2]),
                        "target_a": float(values[3]),
                        "slope_a_per_ms": float(values[4]),
                        "confirm_ms": int(values[5]),
                    }
                )
        elif "正向低于合格下限的平台停滞" in line:
            category = "BelowFloorStall"
            match = re.search(
                r"Peak=([\d.]+)A.*Floor=([\d.]+)A.*Slope=([\d.]+)A/ms.*Window=(\d+)ms",
                line,
            )
            if match:
                values = match.groups()
                below_floor_rows.append(
                    {
                        "timestamp": line[:23],
                        "channel": channel,
                        "peak_a": float(values[0]),
                        "acceptable_floor_a": float(values[1]),
                        "slope_a_per_ms": float(values[2]),
                        "window_ms": int(values[3]),
                    }
                )
        elif "正向实际峰值低于目标" in line:
            category = "PeakBelowTarget"
        elif "单圈超出平衡带" in line:
            category = "PeakOvershoot"
        elif "快速峰值与完整数据峰值偏差超限" in line:
            category = "PeakEvidenceMismatch"
        elif "控流观测无效" in line:
            category = "ObservationInvalid"
        else:
            category = "Other"
        categories[channel][category] += 1

    generic_text = "系统检测到异常，已执行安全保护；详细诊断信息已写入日志。"
    generic_counts: Counter = Counter()
    for line in ui_lines:
        if generic_text not in line:
            continue
        match = re.search(r"卡钳(\d+)", line)
        if match:
            generic_counts[int(match.group(1))] += 1

    epb10_plateau = [row for row in plateau_rows if row["channel"] == 10]
    epb10_below = [row for row in below_floor_rows if row["channel"] == 10]
    return {
        "categories_by_channel": {
            str(channel): dict(counts) for channel, counts in sorted(categories.items())
        },
        "generic_ui_message_counts": {
            f"EPB{channel}": count for channel, count in sorted(generic_counts.items())
        },
        "generic_ui_message_total": sum(generic_counts.values()),
        "near_target_plateau_rows": plateau_rows,
        "below_floor_stall_rows": below_floor_rows,
        "epb10_near_target_plateau_summary": {
            "count": len(epb10_plateau),
            "first": epb10_plateau[0]["timestamp"],
            "last": epb10_plateau[-1]["timestamp"],
            "peak_median_a": statistics.median(row["peak_a"] for row in epb10_plateau),
            "peak_min_a": min(row["peak_a"] for row in epb10_plateau),
            "peak_max_a": max(row["peak_a"] for row in epb10_plateau),
            "instant_current_median_a": statistics.median(
                row["instant_current_a"] for row in epb10_plateau
            ),
            "slope_median_a_per_ms": statistics.median(
                row["slope_a_per_ms"] for row in epb10_plateau
            ),
        },
        "epb10_below_floor_summary": {
            "count": len(epb10_below),
            "first": epb10_below[0]["timestamp"],
            "last": epb10_below[-1]["timestamp"],
            "peak_median_a": statistics.median(row["peak_a"] for row in epb10_below),
            "peak_min_a": min(row["peak_a"] for row in epb10_below),
            "peak_max_a": max(row["peak_a"] for row in epb10_below),
            "slope_median_a_per_ms": statistics.median(
                row["slope_a_per_ms"] for row in epb10_below
            ),
        },
    }


def power_supply_analysis(plateau_rows: list[dict]) -> dict:
    telemetry_path = (
        DATA_ROOT
        / "PowerSupplyTelemetry"
        / f"20260804_184118_{RUN_ID}.csv"
    )
    telemetry: list[dict] = []
    with telemetry_path.open(encoding="utf-8-sig", newline="") as handle:
        for row in csv.DictReader(handle):
            if row["ElectricalGroup"] != "4":
                continue
            telemetry.append(
                {
                    "timestamp": datetime.fromisoformat(row["Utc"].replace("Z", "+00:00")),
                    "voltage_v": float(row["VOut"]),
                    "current_a": float(row["IOut"]),
                    "cc": row["CC"] == "True",
                    "voltage_limited": row["VoltageLimited"] == "True",
                    "current_limited": row["CurrentLimited"] == "True",
                    "power_limited": row["PowerLimited"] == "True",
                    "protection_tripped": row["ProtectionTripped"] == "True",
                }
            )
    telemetry_times = [row["timestamp"] for row in telemetry]
    matches: list[dict] = []
    for event in plateau_rows:
        if event["channel"] != 10:
            continue
        local_time = datetime.strptime(event["timestamp"], "%Y-%m-%d %H:%M:%S.%f")
        utc_time = (local_time - timedelta(hours=8)).replace(tzinfo=timezone.utc)
        index = bisect.bisect_left(telemetry_times, utc_time)
        candidates = telemetry[max(0, index - 2) : min(len(telemetry), index + 3)]
        nearest = min(
            candidates,
            key=lambda row: abs((row["timestamp"] - utc_time).total_seconds()),
        )
        delta_ms = abs((nearest["timestamp"] - utc_time).total_seconds()) * 1000
        if delta_ms <= 100:
            matches.append(
                {
                    "event_time": event["timestamp"],
                    "daq_current_a": event["instant_current_a"],
                    "supply_current_a": nearest["current_a"],
                    "supply_voltage_v": nearest["voltage_v"],
                    "match_delta_ms": delta_ms,
                }
            )

    daq_values = [row["daq_current_a"] for row in matches]
    supply_values = [row["supply_current_a"] for row in matches]
    daq_mean = statistics.mean(daq_values)
    supply_mean = statistics.mean(supply_values)
    covariance = sum(
        (daq - daq_mean) * (supply - supply_mean)
        for daq, supply in zip(daq_values, supply_values)
    )
    denominator = math.sqrt(
        sum((value - daq_mean) ** 2 for value in daq_values)
        * sum((value - supply_mean) ** 2 for value in supply_values)
    )
    return {
        "telemetry_path": str(telemetry_path),
        "group4_sample_count": len(telemetry),
        "group4_limit_or_trip_counts": {
            key: sum(row[key] for row in telemetry)
            for key in (
                "cc",
                "voltage_limited",
                "current_limited",
                "power_limited",
                "protection_tripped",
            )
        },
        "plateau_event_matches": len(matches),
        "plateau_voltage_median_v": statistics.median(
            row["supply_voltage_v"] for row in matches
        ),
        "plateau_voltage_min_v": min(row["supply_voltage_v"] for row in matches),
        "plateau_voltage_max_v": max(row["supply_voltage_v"] for row in matches),
        "plateau_supply_current_median_a": statistics.median(supply_values),
        "plateau_daq_current_median_a": statistics.median(daq_values),
        "current_median_absolute_difference_a": statistics.median(
            abs(daq - supply)
            for daq, supply in zip(daq_values, supply_values)
        ),
        "current_correlation": covariance / denominator,
        "matches": matches,
    }


def write_sqlite(result: dict) -> None:
    if DB_OUTPUT.exists():
        DB_OUTPUT.unlink()
    with sqlite3.connect(DB_OUTPUT) as conn:
        conn.executescript(
            """
            CREATE TABLE channel_summary (
                channel TEXT, completed_cycles INTEGER, warning_cycles INTEGER,
                warning_rate_pct REAL, peak_median_a REAL, first_40_peak_median_a REAL,
                last_40_peak_median_a REAL, peak_change_a REAL
            );
            CREATE TABLE epb10_time_bins (
                bin_order INTEGER, time_bin TEXT, cycle_count INTEGER,
                median_peak_a REAL, mean_peak_a REAL, warning_cycles INTEGER,
                warning_rate_pct REAL, target_a REAL, acceptable_floor_a REAL
            );
            CREATE TABLE message_mapping (
                channel TEXT, generic_message_count INTEGER,
                underlying_reason TEXT, interpretation TEXT
            );
            CREATE TABLE cause_ranking (
                priority INTEGER, candidate TEXT, judgement TEXT, evidence TEXT
            );
            """
        )
        conn.executemany(
            "INSERT INTO channel_summary VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
            [
                (
                    row["channel"], row["completed_cycles"], row["warning_cycles"],
                    row["warning_rate_pct"], row["peak_median_a"],
                    row["first_40_peak_median_a"], row["last_40_peak_median_a"],
                    row["peak_change_a"],
                )
                for row in result["channel_summary"]
            ],
        )
        conn.executemany(
            "INSERT INTO epb10_time_bins VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)",
            [
                (
                    index, row["time_bin"], row["cycle_count"], row["median_peak_a"],
                    row["mean_peak_a"], row["warning_cycles"], row["warning_rate_pct"],
                    15.0, 14.2,
                )
                for index, row in enumerate(result["epb10_time_bins"], start=1)
            ],
        )
        generic = result["warning_analysis"]["generic_ui_message_counts"]
        conn.executemany(
            "INSERT INTO message_mapping VALUES (?, ?, ?, ?)",
            [
                ("EPB10", generic.get("EPB10", 0), "ClampReachedNearTargetPlateau", "高负载平台接近目标，软预警并主动断电"),
                ("EPB5", generic.get("EPB5", 0), "ClampReachedNearTargetPlateau", "同一机制，发生次数较少"),
            ],
        )
        conn.executemany(
            "INSERT INTO cause_ranking VALUES (?, ?, ?, ?)",
            [
                (1, "EPB10 热态可达平台电流下降，15 A 目标与热态能力不匹配", "高", "前40圈15.024 A降至末40圈14.508 A；提示率随时间升高"),
                (2, "EPB10 通道电阻随温升增加：电机绕组、线束、端子或继电器触点", "中高", "12 V稳定且实测电流真实下降；需四线压降与温度确认具体位置"),
                (3, "卡钳机构负载/摩擦特性热漂移", "中", "平台斜率接近零说明已进入机械高负载区，但没有位移/夹紧力证据"),
                (4, "Dev2 采集或电源组4共因", "低", "同设备同电源组的EPB11正常；电源无压降、限流或保护"),
                (5, "EPB10电流采样比例错误", "低", "DAQ电流与电源IOut中位差0.0495 A，相关系数0.901"),
            ],
        )


def main() -> None:
    run_lines = read_log("run.log")
    warning_lines = read_log("warning.log")
    ui_lines = read_log("ui-info.log")
    outcomes = parse_outcomes(run_lines)
    warnings = warning_analysis(warning_lines, ui_lines)
    power = power_supply_analysis(warnings["near_target_plateau_rows"])
    result = {
        "generated_at": datetime.now().isoformat(timespec="seconds"),
        "data_root": str(DATA_ROOT),
        "run_id": RUN_ID,
        "analysis_window": {"start": "2026-08-04 18:41", "end": "2026-08-04 20:16"},
        "channel_summary": summarize_channels(outcomes),
        "epb10_time_bins": epb10_time_bins(outcomes),
        "warning_analysis": warnings,
        "power_supply_analysis": power,
        "separate_historical_hard_alarm": {
            "timestamp": "2026-08-04 12:07:48",
            "reason": "AdaptiveHardFault ForwardLoadRiseNotStarted elapsed=3004ms deadline=3000ms I=1.714A Target=15.000A",
            "interpretation": "早期独立启动/负载上升故障；同一分钟EPB5和EPB9也发生，不是18:41后重复通用提示的主机制。",
        },
        "effective_thresholds": {
            "target_a": 15.0,
            "acceptable_undershoot_a": 0.8,
            "acceptable_floor_a": 14.2,
            "minimum_rise_slope_a_per_ms": 0.001,
            "near_target_confirm_ms": 200,
        },
        "source_files": [
            str(DATA_ROOT / "log" / "run.log"),
            str(DATA_ROOT / "log" / "warning.log"),
            str(DATA_ROOT / "log" / "ui-info.log"),
            power["telemetry_path"],
            str(DATA_ROOT / "Config" / "TestConfig.xml"),
            str(DATA_ROOT / "Config" / "EpbProgramSafetyEffective.xml"),
        ],
    }
    JSON_OUTPUT.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    write_sqlite(result)
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
