#!/usr/bin/env python3
# -*- coding: utf-8 -*-

"""ThresholdExporter

从 EPB ErrorLog 文本中提取所有“阈值记录”行，并导出为 CSV。
默认输出列与运行日志目录中的 `CB32ms_异常清单_ErrorLog-1228-1.csv` 保持一致：

LineNo, DateTime, Hour, Channel, ThresholdA, CutoffA, CbMs, ArrivalDelayMs, Dev, N, FsHz, ImaxA

也支持用 `--template` 读取模板 CSV 的表头，确保输出列名/顺序完全一致。

注意：脚本仅解析“阈值：xxA ... 断电触发点 ... Imax=... Samples=...”这一类行；
像“正向峰值捕获（全数据）已开始”这类状态行会被跳过。
"""

from __future__ import annotations

import argparse
import csv
import os
import re
from dataclasses import dataclass
from datetime import datetime
from typing import Iterable, List, Optional, Sequence


DEFAULT_HEADER: List[str] = [
    "LineNo",
    "DateTime",
    "Hour",
    "Channel",
    "ThresholdA",
    "CutoffA",
    "CbMs",
    "ArrivalDelayMs",
    "Dev",
    "N",
    "FsHz",
    "ImaxA",
]

# 解析阈值行（基于当前日志格式）
# 示例：
# 0005507\t2025-12-28:10:18:04.049>  ,EPB  ,EPB[3]，阈值：10A,... 断电触发点：8.240A，触发时回调间隔≈32.02ms 到达延迟≈0.68ms (Dev1 N=20 Fs=1000Hz), 正向段峰值：Imax=9.676A @ 10:18:03.057，Samples=3220。
THRESHOLD_LINE_RE = re.compile(
    r"^(?P<lineno>\d+)\s+"
    r"(?P<ts>\d{4}-\d{2}-\d{2}:\d{2}:\d{2}:\d{2}\.\d{3})>"
    r".*?EPB\[(?P<ch>\d+)\]，"
    r"阈值[:：](?P<th>[-\d.]+)A"
    r".*?断电触发点[:：](?P<cutoff>[-\d.]+)A，"
    r"触发时回调间隔≈(?P<cb>[-\d.]+)ms\s+"
    r"到达延迟≈(?P<arrival>[-\d.]+)ms\s*"
    r"\((?P<devinfo>[^)]*)\)"
    r".*?Imax=(?P<imax>[-\d.]+)A\s*@\s*(?P<imax_time>\d{2}:\d{2}:\d{2}\.\d{3})"
    r".*?$",
    re.IGNORECASE,
)

DEVINFO_RE = re.compile(r"Dev(?P<dev>\d+)\s+N=(?P<n>\d+)\s+Fs=(?P<fs>\d+)Hz", re.IGNORECASE)


@dataclass(frozen=True)
class ThresholdRecord:
    line_no: int
    dt: datetime
    channel: int
    threshold_a: float
    cutoff_a: float
    cb_ms: float
    arrival_delay_ms: float
    dev: int
    n: int
    fs_hz: int
    imax_a: float

    def to_row(self, header: Sequence[str]) -> dict:
        """按给定表头输出一行 dict（列名 -> 值）。"""
        # DateTime 列格式对齐 PowerShell Export-Csv 的默认显示风格：yyyy/MM/dd H:mm:ss（小时不补零）
        date_str = f"{self.dt:%Y/%m/%d} {self.dt.hour}:{self.dt:%M:%S}"
        hour_str = self.dt.strftime("%Y-%m-%d %H:00")

        values = {
            "LineNo": str(self.line_no),
            "DateTime": date_str,
            "Hour": hour_str,
            "Channel": str(self.channel),
            "ThresholdA": _format_float(self.threshold_a),
            "CutoffA": _format_float(self.cutoff_a),
            "CbMs": _format_float(self.cb_ms),
            "ArrivalDelayMs": _format_float(self.arrival_delay_ms),
            "Dev": str(self.dev),
            "N": str(self.n),
            "FsHz": str(self.fs_hz),
            "ImaxA": _format_float(self.imax_a),
        }

        # 若 header 里还有额外列，默认留空
        return {col: values.get(col, "") for col in header}


def _format_float(x: float) -> str:
    """输出更贴近现有 CSV：尽量保持短小，不强制固定小数位。"""
    # 直接用 Python 的默认格式会有科学计数法风险，这里用去尾零的方式
    s = f"{x:.6f}".rstrip("0").rstrip(".")
    return s if s else "0"


def read_template_header(template_csv: str) -> List[str]:
    """读取模板 CSV 的首行表头。"""
    with open(template_csv, "r", encoding="utf-8-sig", newline="") as f:
        reader = csv.reader(f)
        header = next(reader, None)
    if not header:
        raise RuntimeError(f"模板CSV未读取到表头: {template_csv}")
    return [h.strip() for h in header]


def collect_txt_files(path: str) -> List[str]:
    """支持传入单个 txt 或目录（目录下递归收集 *.txt）。"""
    if os.path.isdir(path):
        out: List[str] = []
        for root, _, files in os.walk(path):
            for fn in files:
                if fn.lower().endswith(".txt"):
                    out.append(os.path.join(root, fn))
        out.sort()
        return out
    return [path]


def try_parse_threshold_line(line: str) -> Optional[ThresholdRecord]:
    m = THRESHOLD_LINE_RE.match(line.strip())
    if not m:
        return None

    line_no = int(m.group("lineno"))
    dt = datetime.strptime(m.group("ts"), "%Y-%m-%d:%H:%M:%S.%f")
    channel = int(m.group("ch"))

    threshold_a = float(m.group("th"))
    cutoff_a = float(m.group("cutoff"))
    cb_ms = float(m.group("cb"))
    arrival_delay_ms = float(m.group("arrival"))
    imax_a = float(m.group("imax"))

    dev = 0
    n = 0
    fs_hz = 0
    devinfo = m.group("devinfo") or ""
    m2 = DEVINFO_RE.search(devinfo)
    if m2:
        dev = int(m2.group("dev"))
        n = int(m2.group("n"))
        fs_hz = int(m2.group("fs"))

    return ThresholdRecord(
        line_no=line_no,
        dt=dt,
        channel=channel,
        threshold_a=threshold_a,
        cutoff_a=cutoff_a,
        cb_ms=cb_ms,
        arrival_delay_ms=arrival_delay_ms,
        dev=dev,
        n=n,
        fs_hz=fs_hz,
        imax_a=imax_a,
    )


def export_thresholds(
    log_paths: Sequence[str],
    out_csv: str,
    template_csv: Optional[str] = None,
) -> dict:
    if template_csv:
        header = read_template_header(template_csv)
    else:
        header = list(DEFAULT_HEADER)

    total_lines = 0
    matched = 0
    records: List[ThresholdRecord] = []

    for path in log_paths:
        with open(path, "r", encoding="utf-8", errors="ignore") as f:
            for line in f:
                total_lines += 1
                rec = try_parse_threshold_line(line)
                if not rec:
                    continue
                matched += 1
                records.append(rec)

    os.makedirs(os.path.dirname(os.path.abspath(out_csv)) or ".", exist_ok=True)
    with open(out_csv, "w", encoding="utf-8-sig", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=header, quoting=csv.QUOTE_ALL)
        writer.writeheader()
        for rec in sorted(records, key=lambda r: r.dt):
            writer.writerow(rec.to_row(header))

    return {
        "total_lines": total_lines,
        "matched": matched,
        "output": out_csv,
        "header": header,
    }


def main() -> None:
    ap = argparse.ArgumentParser(
        description="从 ErrorLog 文本中提取阈值记录并导出CSV（支持模板CSV表头对齐）"
    )
    ap.add_argument(
        "--log",
        required=True,
        help="日志 txt 文件路径，或包含 txt 的目录（会递归收集）",
    )
    ap.add_argument("--out", required=True, help="输出 CSV 文件路径")
    ap.add_argument(
        "--template",
        default=None,
        help="模板 CSV 路径（用于对齐表头/列顺序；不传则用默认表头）",
    )

    args = ap.parse_args()
    log_paths = collect_txt_files(args.log)
    if not log_paths:
        raise SystemExit(f"未找到任何 txt: {args.log}")

    info = export_thresholds(log_paths, args.out, template_csv=args.template)
    print(f"[OK] 扫描行数: {info['total_lines']}  命中阈值记录: {info['matched']}")
    print(f"[OK] 输出CSV: {info['output']}")


if __name__ == "__main__":
    main()
