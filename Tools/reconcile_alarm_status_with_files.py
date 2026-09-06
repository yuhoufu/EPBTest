#!/usr/bin/env python3
"""Keep status='alarm' only when the same EPB cycle has CSV and BIN evidence."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sqlite3
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path


EXPECTED_ALARMS_BEFORE = 34_322
EXPECTED_ALARMS_WITH_FILES = 11
SNAPSHOT_DIR_RE = re.compile(r"^(\d{8}_\d{6})-EPB(\d{2})$")
CYCLE_FILE_RE = re.compile(r"^EPB(\d+)_Cycle_(\d+)\.(csv|bin)$", re.IGNORECASE)
PROJECT_TIMEZONE = timezone(timedelta(hours=8))


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("project_dir", type=Path)
    parser.add_argument("--apply", action="store_true", help="Back up and update index.db")
    parser.add_argument(
        "--expected-before", type=int, default=EXPECTED_ALARMS_BEFORE
    )
    parser.add_argument(
        "--expected-keep", type=int, default=EXPECTED_ALARMS_WITH_FILES
    )
    return parser.parse_args()


def connect(database: Path) -> sqlite3.Connection:
    connection = sqlite3.connect(str(database), timeout=30)
    connection.execute("PRAGMA busy_timeout = 30000")
    return connection


def quick_check(connection: sqlite3.Connection) -> str:
    row = connection.execute("PRAGMA quick_check").fetchone()
    return str(row[0]) if row else "no result"


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def create_online_backup(source: sqlite3.Connection, database: Path) -> Path:
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    backup = database.with_name(f"{database.name}.before_file_alarm_reconcile_{stamp}.bak")
    if backup.exists():
        raise RuntimeError(f"Backup path already exists: {backup}")
    destination = sqlite3.connect(str(backup))
    try:
        source.backup(destination)
        if quick_check(destination) != "ok":
            raise RuntimeError("Backup quick_check failed")
    except Exception:
        destination.close()
        backup.unlink(missing_ok=True)
        raise
    destination.close()
    return backup


def scan_snapshot_evidence(snapshot_root: Path) -> list[dict]:
    evidence: list[dict] = []
    if not snapshot_root.is_dir():
        return evidence

    for snapshot_dir in snapshot_root.iterdir():
        if not snapshot_dir.is_dir():
            continue
        match = SNAPSHOT_DIR_RE.match(snapshot_dir.name)
        if not match:
            continue
        snapshot_time = datetime.strptime(match.group(1), "%Y%m%d_%H%M%S").replace(
            tzinfo=PROJECT_TIMEZONE
        )
        alarm_channel = int(match.group(2))
        alarm_dir = snapshot_dir / f"EPB{alarm_channel:02d}_ALARM"
        if not alarm_dir.is_dir():
            continue

        extensions_by_key: dict[tuple[int, int], set[str]] = {}
        paths_by_key: dict[tuple[int, int], dict[str, str]] = {}
        for file in alarm_dir.iterdir():
            if not file.is_file():
                continue
            file_match = CYCLE_FILE_RE.match(file.name)
            if not file_match:
                continue
            channel = int(file_match.group(1))
            cycle = int(file_match.group(2))
            extension = file_match.group(3).lower()
            key = (channel, cycle)
            extensions_by_key.setdefault(key, set()).add(extension)
            paths_by_key.setdefault(key, {})[extension] = str(file)

        for (channel, cycle), extensions in extensions_by_key.items():
            if channel != alarm_channel or extensions != {"csv", "bin"}:
                continue
            evidence.append(
                {
                    "epb_id": channel,
                    "cycle_number": cycle,
                    "snapshot_time": snapshot_time,
                    "snapshot_dir": str(snapshot_dir),
                    "csv": paths_by_key[(channel, cycle)]["csv"],
                    "bin": paths_by_key[(channel, cycle)]["bin"],
                }
            )
    return evidence


def parse_db_time(value: str | None) -> datetime | None:
    if not value:
        return None
    parsed = datetime.fromisoformat(value)
    return parsed if parsed.tzinfo else parsed.replace(tzinfo=PROJECT_TIMEZONE)


def match_alarm_rows(connection: sqlite3.Connection, evidence: list[dict]) -> dict:
    alarm_rows = connection.execute(
        """
        SELECT epb_id, cycle_number, start_time, end_time, sample_count
        FROM epb_cycles
        WHERE status = 'alarm'
        ORDER BY epb_id, cycle_number
        """
    ).fetchall()
    evidence_by_key: dict[tuple[int, int], list[dict]] = {}
    for item in evidence:
        evidence_by_key.setdefault(
            (item["epb_id"], item["cycle_number"]), []
        ).append(item)

    keep: list[dict] = []
    remove: list[dict] = []
    for epb_id, cycle, start_time, end_time, sample_count in alarm_rows:
        row_time = parse_db_time(end_time) or parse_db_time(start_time)
        matches = [
            item
            for item in evidence_by_key.get((epb_id, cycle), [])
            # Reject recycled cycle numbers whose snapshot predates this DB row.
            if row_time is not None
            and item["snapshot_time"] + timedelta(seconds=60) >= row_time
        ]
        row = {
            "epb_id": epb_id,
            "cycle_number": cycle,
            "start_time": start_time,
            "end_time": end_time,
            "sample_count": sample_count,
        }
        if matches:
            row["snapshot_dirs"] = sorted({item["snapshot_dir"] for item in matches})
            keep.append(row)
        else:
            remove.append(row)
    return {"alarm_count": len(alarm_rows), "keep": keep, "remove": remove}


def status_summary(connection: sqlite3.Connection) -> dict:
    return {
        str(status): count
        for status, count in connection.execute(
            "SELECT status, COUNT(*) FROM epb_cycles GROUP BY status ORDER BY status"
        )
    }


def main() -> int:
    args = parse_args()
    project_dir = args.project_dir.resolve()
    database = project_dir / "index.db"
    snapshot_root = project_dir / "AlarmSnapshots"
    if not database.is_file():
        raise FileNotFoundError(database)

    evidence = scan_snapshot_evidence(snapshot_root)
    connection = connect(database)
    try:
        integrity_before = quick_check(connection)
        if integrity_before != "ok":
            raise RuntimeError(f"Database quick_check failed: {integrity_before}")
        matched = match_alarm_rows(connection, evidence)
        receipt = {
            "mode": "apply" if args.apply else "dry-run",
            "database": str(database),
            "snapshot_root": str(snapshot_root),
            "integrity_before": integrity_before,
            "status_before": status_summary(connection),
            "paired_snapshot_records": len(evidence),
            "alarm_count_before": matched["alarm_count"],
            "alarm_count_with_files": len(matched["keep"]),
            "alarm_count_without_files": len(matched["remove"]),
            "kept_alarms": matched["keep"],
        }
        if (
            matched["alarm_count"] != args.expected_before
            or len(matched["keep"]) != args.expected_keep
        ):
            receipt["result"] = (
                "ABORTED: reviewed counts changed; "
                f"expected before/keep={args.expected_before}/{args.expected_keep}"
            )
            print(json.dumps(receipt, ensure_ascii=False, indent=2, default=str))
            return 2
        if not args.apply:
            receipt["result"] = "DRY RUN ONLY: no database changes made"
            print(json.dumps(receipt, ensure_ascii=False, indent=2, default=str))
            return 0

        backup = create_online_backup(connection, database)
        receipt["backup"] = str(backup)
        receipt["backup_sha256"] = sha256_file(backup)

        keep_keys = [(row["epb_id"], row["cycle_number"]) for row in matched["keep"]]
        connection.execute("BEGIN IMMEDIATE")
        try:
            rechecked = match_alarm_rows(connection, evidence)
            if (
                rechecked["alarm_count"] != args.expected_before
                or len(rechecked["keep"]) != args.expected_keep
            ):
                raise RuntimeError("Alarm/file match counts changed after lock")

            connection.execute(
                "CREATE TEMP TABLE keep_alarm(epb_id INTEGER, cycle_number INTEGER, "
                "PRIMARY KEY(epb_id, cycle_number))"
            )
            connection.executemany(
                "INSERT INTO keep_alarm(epb_id, cycle_number) VALUES (?, ?)", keep_keys
            )
            cursor = connection.execute(
                """
                UPDATE epb_cycles
                SET status = 'completed'
                WHERE status = 'alarm'
                  AND NOT EXISTS (
                      SELECT 1
                      FROM keep_alarm k
                      WHERE k.epb_id = epb_cycles.epb_id
                        AND k.cycle_number = epb_cycles.cycle_number
                  )
                """
            )
            expected_updates = args.expected_before - args.expected_keep
            if cursor.rowcount != expected_updates:
                raise RuntimeError(
                    f"Updated {cursor.rowcount} rows (expected {expected_updates})"
                )
            connection.commit()
        except Exception:
            connection.rollback()
            raise

        remaining = match_alarm_rows(connection, evidence)
        receipt["status_after"] = status_summary(connection)
        receipt["remaining_alarm_count"] = remaining["alarm_count"]
        receipt["remaining_alarm_without_files"] = len(remaining["remove"])
        receipt["integrity_after"] = quick_check(connection)
        if receipt["integrity_after"] != "ok":
            raise RuntimeError(
                f"Database quick_check failed after reconcile: "
                f"{receipt['integrity_after']}"
            )
        receipt["result"] = (
            f"RECONCILED: {args.expected_before - args.expected_keep} rows updated; "
            f"{args.expected_keep} file-backed alarms kept"
        )
        print(json.dumps(receipt, ensure_ascii=False, indent=2, default=str))
        return 0
    finally:
        connection.close()


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        print(f"ERROR: {error}", file=sys.stderr)
        sys.exit(1)
