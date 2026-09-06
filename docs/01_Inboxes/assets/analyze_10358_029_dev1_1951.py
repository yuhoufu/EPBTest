from __future__ import annotations

import json
import math
import re
import sqlite3
import statistics
import struct
from collections import Counter, defaultdict
from datetime import datetime
from pathlib import Path


DATA_ROOT = Path(r"D:\EPB_Data\10358-029_2021_backup")
OUTPUT = Path(__file__).with_name("10358_029_dev1_1951_analysis_summary.json")
REPORT_DB = Path(__file__).with_name("10358_029_dev1_1951_report.sqlite")
RUN_START = datetime(2026, 8, 4, 18, 41, 18)
FAULT_TIME = datetime(2026, 8, 4, 19, 51, 8, 307000)


def percentile(values: list[float], p: float) -> float | None:
    if not values:
        return None
    xs = sorted(values)
    if len(xs) == 1:
        return xs[0]
    pos = (len(xs) - 1) * p
    lo = math.floor(pos)
    hi = math.ceil(pos)
    if lo == hi:
        return xs[lo]
    return xs[lo] + (xs[hi] - xs[lo]) * (pos - lo)


def parse_log_time(line: str) -> datetime | None:
    try:
        return datetime.strptime(line[:23], "%Y-%m-%d %H:%M:%S.%f")
    except (ValueError, IndexError):
        return None


def read_lines(name: str) -> list[str]:
    return (DATA_ROOT / "log" / name).read_text(
        encoding="utf-8-sig", errors="replace"
    ).splitlines()


def backlog_summary(warning_lines: list[str]) -> dict:
    pattern = re.compile(
        r"DAQ后台处理积压：Device=(Dev\d+) QueueDepth=(\d+) "
        r"OldestBatchAge=([0-9.]+)ms"
    )
    events: list[dict] = []
    for line in warning_lines:
        ts = parse_log_time(line)
        match = pattern.search(line)
        if ts is None or ts < RUN_START or match is None:
            continue
        events.append(
            {
                "timestamp": ts.isoformat(timespec="milliseconds"),
                "device": match.group(1),
                "queue_depth": int(match.group(2)),
                "oldest_batch_age_ms": float(match.group(3)),
            }
        )

    by_device: dict[str, dict] = {}
    for device in ("Dev1", "Dev2"):
        rows = [row for row in events if row["device"] == device]
        ages = [row["oldest_batch_age_ms"] for row in rows]
        depths = [row["queue_depth"] for row in rows]
        before_fault = [
            row
            for row in rows
            if datetime.fromisoformat(row["timestamp"]) <= FAULT_TIME
        ]
        last_before_fault = before_fault[-1] if before_fault else None
        if last_before_fault:
            delta = (
                FAULT_TIME - datetime.fromisoformat(last_before_fault["timestamp"])
            ).total_seconds()
            last_before_fault = {**last_before_fault, "seconds_before_fault": delta}
        by_device[device] = {
            "count": len(rows),
            "max_queue_depth": max(depths) if depths else None,
            "median_queue_depth": statistics.median(depths) if depths else None,
            "max_oldest_batch_age_ms": max(ages) if ages else None,
            "median_oldest_batch_age_ms": statistics.median(ages) if ages else None,
            "p95_oldest_batch_age_ms": percentile(ages, 0.95),
            "last_before_fault": last_before_fault,
        }
    return {"events": events, "by_device": by_device}


def outcome_summary(run_lines: list[str]) -> dict:
    pattern = re.compile(
        r"EPB\[(\d+)] 自适应单圈完成：.*?FullRatePeak=([+-]?[0-9.]+)A，"
        r"Target=([+-]?[0-9.]+)A，Error=([+-]?[0-9.]+)A，.*?结果=([^。]+)"
    )
    rows: list[dict] = []
    for line in run_lines:
        ts = parse_log_time(line)
        match = pattern.search(line)
        if ts is None or ts < RUN_START or ts > FAULT_TIME or match is None:
            continue
        rows.append(
            {
                "timestamp": ts.isoformat(timespec="milliseconds"),
                "channel": int(match.group(1)),
                "full_rate_peak_a": float(match.group(2)),
                "target_a": float(match.group(3)),
                "error_a": float(match.group(4)),
                "result": match.group(5),
            }
        )

    by_channel: dict[str, dict] = {}
    for channel in (4, 5, 8, 9, 10, 11):
        items = [row for row in rows if row["channel"] == channel]
        peaks = [row["full_rate_peak_a"] for row in items]
        errors = [row["error_a"] for row in items]
        by_channel[str(channel)] = {
            "completed_outcome_count": len(items),
            "result_counts": dict(Counter(row["result"] for row in items)),
            "peak_median_a": statistics.median(peaks) if peaks else None,
            "peak_min_a": min(peaks) if peaks else None,
            "peak_max_a": max(peaks) if peaks else None,
            "error_abs_p95_a": percentile([abs(value) for value in errors], 0.95),
            "last_five": items[-5:],
        }
    return {"rows": rows, "by_channel": by_channel}


def database_summary() -> dict:
    connection = sqlite3.connect(DATA_ROOT / "index.db")
    connection.row_factory = sqlite3.Row
    try:
        rows = [
            dict(row)
            for row in connection.execute(
                """
                SELECT epb_id, cycle_number, start_time, end_time, sample_count, status
                FROM epb_cycles
                WHERE start_time >= ?
                ORDER BY start_time, epb_id
                """,
                ("2026-08-04T18:41:18",),
            )
        ]
    finally:
        connection.close()

    by_channel: dict[str, dict] = {}
    for channel in (4, 5, 8, 9, 10, 11):
        items = [row for row in rows if row["epb_id"] == channel]
        by_channel[str(channel)] = {
            "row_count": len(items),
            "status_counts": dict(Counter(row["status"] for row in items)),
            "last_rows": items[-5:],
        }
    return {"row_count": len(rows), "by_channel": by_channel}


def parse_bin(path: Path) -> dict:
    data = path.read_bytes()
    if len(data) % 32:
        raise ValueError(f"Unexpected binary length: {path} ({len(data)})")
    currents: list[float] = []
    pressures: list[float] = []
    cycles: set[int] = set()
    for offset in range(0, len(data), 32):
        _timestamp_binary, cycle, _sample_index, current, pressure = struct.unpack_from(
            "<qii dd", data, offset
        )
        cycles.add(cycle)
        currents.append(current)
        pressures.append(pressure)
    return {
        "path": str(path),
        "record_count": len(currents),
        "cycles": sorted(cycles),
        "current_min_a": min(currents),
        "current_max_a": max(currents),
        "pressure_min_bar": min(pressures),
        "pressure_max_bar": max(pressures),
    }


def last_completed_bins() -> dict:
    targets = {
        "EPB4_cycle601": DATA_ROOT
        / "HistoricalSnapshots"
        / "EPB04"
        / "Cycle_000601"
        / "EPB4_Cycle_000601.bin",
        "EPB5_cycle524": DATA_ROOT
        / "HistoricalSnapshots"
        / "EPB05"
        / "Cycle_000524"
        / "EPB5_Cycle_000524.bin",
    }
    return {name: parse_bin(path) for name, path in targets.items() if path.exists()}


def historical_alarm_metadata() -> list[dict]:
    rows: list[dict] = []
    for path in sorted((DATA_ROOT / "AlarmSnapshots").glob("*/alarm-metadata.json")):
        metadata = json.loads(path.read_text(encoding="utf-8-sig"))
        if metadata.get("alarmChannel") not in (4, 5):
            continue
        rows.append(
            {
                "directory": path.parent.name,
                "alarm_utc": metadata.get("alarmUtc"),
                "channel": metadata.get("alarmChannel"),
                "cycle": metadata.get("alarmCycleNumber"),
                "reason": metadata.get("reason"),
                "evidence_sample_count": metadata.get("evidenceSampleCount"),
                "physical_power_state": metadata.get("physicalPowerState"),
            }
        )
    return rows


def fault_timeline(warning_lines: list[str], error_lines: list[str], run_lines: list[str]) -> list[dict]:
    wanted = (
        "DAQ安全恢复开始 Device=Dev1",
        "DaqSampleStale>100ms",
        "电源组2触发失效安全联锁",
        "电源组 2 已关闭",
        "DAQ安全恢复完成 Device=Dev1",
        "周期 602 返回失败",
        "周期 525 返回失败",
    )
    events: list[dict] = []
    for source, lines in (
        ("warning.log", warning_lines),
        ("error.log", error_lines),
        ("run.log", run_lines),
    ):
        for line in lines:
            ts = parse_log_time(line)
            if ts is None or not (datetime(2026, 8, 4, 19, 51, 7) <= ts <= datetime(2026, 8, 4, 19, 51, 10)):
                continue
            if any(token in line for token in wanted):
                events.append(
                    {
                        "timestamp": ts.isoformat(timespec="milliseconds"),
                        "source": source,
                        "message": line.split("\t")[-1],
                    }
                )
    return sorted(events, key=lambda row: row["timestamp"])


def diagnostic_file_summary() -> dict:
    names = ("daq_timing.csv", "daq_runtime.csv")
    found = [
        str(path)
        for name in names
        for path in DATA_ROOT.rglob(name)
    ]
    latest_alarm_directory = DATA_ROOT / "AlarmSnapshots" / "20260804_195108-EPB04"
    return {
        "files_found": found,
        "expected_latest_alarm_directory": str(latest_alarm_directory),
        "latest_alarm_directory_exists": latest_alarm_directory.exists(),
    }


def write_report_database(result: dict) -> None:
    """Write the bounded, reviewed rows used by the interactive report."""
    if REPORT_DB.exists():
        REPORT_DB.unlink()
    with sqlite3.connect(REPORT_DB) as conn:
        conn.executescript(
            """
            CREATE TABLE last_peaks (
                sequence INTEGER NOT NULL,
                channel TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                peak_a REAL NOT NULL,
                result TEXT NOT NULL
            );
            CREATE TABLE backlog_age (
                stat_order INTEGER NOT NULL,
                stat TEXT NOT NULL,
                device TEXT NOT NULL,
                age_ms REAL NOT NULL
            );
            CREATE TABLE fault_timeline (
                event_order INTEGER NOT NULL,
                time TEXT NOT NULL,
                event TEXT NOT NULL,
                evidence TEXT NOT NULL,
                meaning TEXT NOT NULL
            );
            CREATE TABLE root_causes (
                priority INTEGER NOT NULL,
                candidate TEXT NOT NULL,
                judgement TEXT NOT NULL,
                basis TEXT NOT NULL
            );
            """
        )

        by_channel = result["cycle_outcomes_before_fault"]["by_channel"]
        for channel in (4, 5):
            rows = by_channel[str(channel)]["last_five"]
            conn.executemany(
                "INSERT INTO last_peaks VALUES (?, ?, ?, ?, ?)",
                [
                    (index - 5, f"EPB{channel}", row["timestamp"], row["full_rate_peak_a"], row["result"])
                    for index, row in enumerate(rows)
                ],
            )

        by_device = result["daq_background_backlog"]["by_device"]
        stat_fields = (
            (1, "中位数", "median_oldest_batch_age_ms"),
            (2, "P95", "p95_oldest_batch_age_ms"),
            (3, "最大值", "max_oldest_batch_age_ms"),
        )
        conn.executemany(
            "INSERT INTO backlog_age VALUES (?, ?, ?, ?)",
            [
                (order, label, device, round(float(by_device[device][field]), 3))
                for order, label, field in stat_fields
                for device in ("Dev1", "Dev2")
            ],
        )

        timeline = [
            (1, "19:51:08.179", "EPB4 反转命令", "最近电流 0.006808 A", "进入反转阶段"),
            (2, "19:51:08.304", "EPB4 高优先级关闭", "DaqSampleStale>100ms", "直接触发为采样过期"),
            (3, "19:51:08.307", "电源组 2 失效安全联锁", "AgeMs=671.3；组电流 3.758 A；电源遥测年龄 32.5 ms", "Dev1 采样过期，电源遥测仍新鲜"),
            (4, "19:51:08.312", "Dev1 安全恢复启动", "触发 EPB4；影响 EPB4/EPB5", "故障域集中在 Dev1"),
            (5, "19:51:08.316", "EPB5 第 525 周期取消", "设备联锁取消", "受牵连通道，不是独立卡钳故障"),
            (6, "19:51:08.517", "电源组 2 关闭", "失效安全动作完成", "安全链路工作正常"),
            (7, "19:51:08.815", "EPB4 第 602 周期硬故障", "DaqSampleStale", "无堵转、开路或过流代码"),
            (8, "19:51:08.993", "Dev1 采集恢复", "FreshCallbacks=10/10；ElapsedMs=676", "重建采集后恢复"),
        ]
        conn.executemany("INSERT INTO fault_timeline VALUES (?, ?, ?, ?, ?)", timeline)

        root_causes = [
            (1, "Dev1 回调/有效样本提交链路短时停止", "高", "671.3 ms 无有效样本；重建 DAQ 后恢复；历史报警也集中在 Dev1"),
            (2, "旧版本软件在持续负载下的 DAQ 处理或调度抖动", "中高", "存在持续积压；但 Dev2 更重却未硬故障，不能单独定案"),
            (3, "快速分支异常被吞掉，样本提交心跳不刷新", "中", "当前源码有空 catch；当时二进制是否相同尚不确定"),
            (4, "Dev1 NI-DAQmx 任务、驱动、板卡或总线短时异常", "中", "多次异常均落在 Dev1；需双心跳和换板/换口试验"),
            (5, "EPB4/EPB5 卡钳本体或电源硬件", "低", "波形正常、两通道共因、无卡钳硬故障码，电源遥测仍新鲜"),
        ]
        conn.executemany("INSERT INTO root_causes VALUES (?, ?, ?, ?)", root_causes)


def main() -> None:
    warning_lines = read_lines("warning.log")
    error_lines = read_lines("error.log")
    run_lines = read_lines("run.log")

    result = {
        "generated_at": datetime.now().isoformat(timespec="seconds"),
        "data_root": str(DATA_ROOT),
        "analysis_window": {
            "start": RUN_START.isoformat(timespec="seconds"),
            "fault": FAULT_TIME.isoformat(timespec="milliseconds"),
        },
        "fault_timeline": fault_timeline(warning_lines, error_lines, run_lines),
        "daq_background_backlog": backlog_summary(warning_lines),
        "cycle_outcomes_before_fault": outcome_summary(run_lines),
        "database": database_summary(),
        "last_completed_binary_evidence": last_completed_bins(),
        "historical_dev1_alarm_metadata": historical_alarm_metadata(),
        "diagnostic_exports": diagnostic_file_summary(),
        "source_files": [
            str(DATA_ROOT / "log" / "warning.log"),
            str(DATA_ROOT / "log" / "error.log"),
            str(DATA_ROOT / "log" / "run.log"),
            str(DATA_ROOT / "index.db"),
        ],
    }
    OUTPUT.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    write_report_database(result)
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
