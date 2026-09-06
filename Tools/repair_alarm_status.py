#!/usr/bin/env python3
"""Safely repair the known batch-cycle alarm-status corruption in index.db.

The faulty records are limited to the affected channels and historical window,
and have a full-cycle-like duration.  Dry-run is the default.  Applying the
repair requires the candidate count to match the reviewed value exactly.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import sqlite3
import sys
from datetime import datetime
from pathlib import Path


EXPECTED_CANDIDATES = 34_289
WINDOW_START = "2026-07-24T00:00:00+08:00"
WINDOW_END = "2026-07-28T00:00:00+08:00"

CANDIDATE_WHERE = """
    epb_id IN (8, 9, 10, 12)
    AND status = 'alarm'
    AND end_time IS NOT NULL
    AND julianday(start_time) >= julianday(?)
    AND julianday(start_time) < julianday(?)
    AND (julianday(end_time) - julianday(start_time)) * 86400.0 >= 9.0
    AND (julianday(end_time) - julianday(start_time)) * 86400.0 < 14.0
"""


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("database", type=Path, help="Path to index.db")
    parser.add_argument(
        "--apply",
        action="store_true",
        help="Create an online backup and apply the repair (default: dry-run)",
    )
    parser.add_argument(
        "--expected-count",
        type=int,
        default=EXPECTED_CANDIDATES,
        help="Abort unless this exact number of candidates is found",
    )
    return parser.parse_args()


def connect(database: Path) -> sqlite3.Connection:
    connection = sqlite3.connect(str(database), timeout=30)
    connection.execute("PRAGMA busy_timeout = 30000")
    return connection


def quick_check(connection: sqlite3.Connection) -> str:
    row = connection.execute("PRAGMA quick_check").fetchone()
    return str(row[0]) if row else "no result"


def candidate_summary(connection: sqlite3.Connection) -> dict:
    params = (WINDOW_START, WINDOW_END)
    rows = connection.execute(
        f"""
        SELECT epb_id,
               COUNT(*) AS candidate_count,
               MIN(start_time) AS first_start,
               MAX(start_time) AS last_start,
               ROUND(MIN((julianday(end_time)-julianday(start_time))*86400.0), 3),
               ROUND(MAX((julianday(end_time)-julianday(start_time))*86400.0), 3)
        FROM epb_cycles
        WHERE {CANDIDATE_WHERE}
        GROUP BY epb_id
        ORDER BY epb_id
        """,
        params,
    ).fetchall()
    by_channel = [
        {
            "epb_id": row[0],
            "count": row[1],
            "first_start": row[2],
            "last_start": row[3],
            "min_duration_seconds": row[4],
            "max_duration_seconds": row[5],
        }
        for row in rows
    ]
    return {
        "candidate_count": sum(row["count"] for row in by_channel),
        "by_channel": by_channel,
    }


def status_summary(connection: sqlite3.Connection) -> dict:
    rows = connection.execute(
        """
        SELECT status, COUNT(*)
        FROM epb_cycles
        GROUP BY status
        ORDER BY status
        """
    ).fetchall()
    return {str(status): count for status, count in rows}


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def create_online_backup(source: sqlite3.Connection, database: Path) -> Path:
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    backup = database.with_name(f"{database.name}.before_alarm_repair_{stamp}.bak")
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


def main() -> int:
    args = parse_args()
    database = args.database.resolve()
    if not database.is_file():
        raise FileNotFoundError(database)
    if database.name.lower() != "index.db":
        raise ValueError(f"Refusing unexpected database filename: {database.name}")

    connection = connect(database)
    try:
        integrity_before = quick_check(connection)
        if integrity_before != "ok":
            raise RuntimeError(f"Database quick_check failed: {integrity_before}")

        before = status_summary(connection)
        candidates = candidate_summary(connection)
        receipt = {
            "mode": "apply" if args.apply else "dry-run",
            "database": str(database),
            "integrity_before": integrity_before,
            "status_before": before,
            **candidates,
        }
        if candidates["candidate_count"] != args.expected_count:
            receipt["result"] = (
                f"ABORTED: expected {args.expected_count} candidates, "
                f"found {candidates['candidate_count']}"
            )
            print(json.dumps(receipt, ensure_ascii=False, indent=2))
            return 2

        if not args.apply:
            receipt["result"] = "DRY RUN ONLY: no database changes made"
            print(json.dumps(receipt, ensure_ascii=False, indent=2))
            return 0

        backup = create_online_backup(connection, database)
        receipt["backup"] = str(backup)
        receipt["backup_sha256"] = sha256_file(backup)

        connection.execute("BEGIN IMMEDIATE")
        try:
            rechecked = candidate_summary(connection)["candidate_count"]
            if rechecked != args.expected_count:
                raise RuntimeError(
                    f"Candidate count changed after lock: {rechecked} "
                    f"(expected {args.expected_count})"
                )
            cursor = connection.execute(
                f"UPDATE epb_cycles SET status = 'completed' WHERE {CANDIDATE_WHERE}",
                (WINDOW_START, WINDOW_END),
            )
            if cursor.rowcount != args.expected_count:
                raise RuntimeError(
                    f"Updated {cursor.rowcount} rows (expected {args.expected_count})"
                )
            connection.commit()
        except Exception:
            connection.rollback()
            raise

        receipt["status_after"] = status_summary(connection)
        receipt["remaining_candidates"] = candidate_summary(connection)[
            "candidate_count"
        ]
        receipt["integrity_after"] = quick_check(connection)
        if receipt["integrity_after"] != "ok":
            raise RuntimeError(
                f"Database quick_check failed after repair: "
                f"{receipt['integrity_after']}"
            )
        receipt["result"] = f"REPAIRED: {args.expected_count} rows updated"
        print(json.dumps(receipt, ensure_ascii=False, indent=2))
        return 0
    finally:
        connection.close()


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        print(f"ERROR: {error}", file=sys.stderr)
        sys.exit(1)
