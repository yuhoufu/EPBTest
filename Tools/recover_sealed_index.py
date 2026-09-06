#!/usr/bin/env python3
"""Rebuild a readable point-in-time index beside, never over, a sealed EPB run."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import sqlite3
import tempfile
from datetime import datetime, timezone
from pathlib import Path


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("source_database", type=Path)
    parser.add_argument("cutoff", help="ISO-8601 local cutoff, inclusive")
    parser.add_argument("output_directory", type=Path)
    args = parser.parse_args()

    # Validate early; stored start_time values are fixed-width ISO strings with +08:00.
    datetime.fromisoformat(args.cutoff)
    output = args.output_directory.resolve()
    if output.exists():
        raise FileExistsError(f"refusing to overwrite recovery directory: {output}")
    output.parent.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory(prefix="epb-index-recovery-") as scratch_text:
        scratch = Path(scratch_text)
        database = scratch / "index.db"
        shutil.copy2(args.source_database, database)
        source_hash = sha256(database)

        with sqlite3.connect(database) as connection:
            before = connection.execute("SELECT COUNT(*) FROM epb_cycles").fetchone()[0]
            connection.execute(
                "DELETE FROM epb_cycles WHERE start_time > ?",
                (args.cutoff,),
            )
            after = connection.execute("SELECT COUNT(*) FROM epb_cycles").fetchone()[0]
            maximum_id = connection.execute(
                "SELECT COALESCE(MAX(id), 0) FROM epb_cycles"
            ).fetchone()[0]
            connection.execute(
                "UPDATE sqlite_sequence SET seq=? WHERE name='epb_cycles'",
                (maximum_id,),
            )
            connection.commit()
            connection.execute("VACUUM")
            integrity = connection.execute("PRAGMA integrity_check").fetchone()[0]
            statuses = dict(connection.execute(
                "SELECT status, COUNT(*) FROM epb_cycles GROUP BY status"
            ))
            unfinished = [
                {
                    "epbId": row[0],
                    "cycleNumber": row[1],
                    "status": row[2],
                    "sampleCount": row[3],
                    "startTime": row[4],
                    "endTime": row[5],
                }
                for row in connection.execute(
                    "SELECT epb_id,cycle_number,status,sample_count,start_time,end_time "
                    "FROM epb_cycles WHERE lower(status)='running' "
                    "ORDER BY epb_id,cycle_number"
                )
            ]
        if integrity != "ok":
            raise sqlite3.DatabaseError(f"recovered database integrity failed: {integrity}")

        target_staging = output.parent / (
            f".{output.name}.tmp-{os.getpid()}-{datetime.now().strftime('%H%M%S')}"
        )
        target_staging.mkdir()
        shutil.copy2(database, target_staging / "index.db")
        recovered_hash = sha256(target_staging / "index.db")
        manifest = {
            "createdUtc": datetime.now(timezone.utc).isoformat(),
            "sourceDatabase": str(args.source_database),
            "sourceSnapshotSha256": source_hash,
            "cutoffInclusive": args.cutoff,
            "rowsBeforeCutoffFilter": before,
            "rowsAfterCutoffFilter": after,
            "rowsRemovedAfterCutoff": before - after,
            "integrityCheck": integrity,
            "statusCounts": statuses,
            "unfinishedCyclesPreservedAsForensicEvidence": unfinished,
            "recoveredIndexSha256": recovered_hash,
            "originalSealedDirectoryModified": False,
        }
        (target_staging / "recovery-manifest.json").write_text(
            json.dumps(manifest, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )
        (target_staging / "README.txt").write_text(
            "本目录是从原项目可读索引按封存截止时间重建的旁路恢复副本。\n"
            "原封存目录及其损坏 index.db 未被修改。\n"
            "running 资格圈为事故证据，故意保留，不应改写为 completed。\n",
            encoding="utf-8",
        )
        target_staging.replace(output)
        print(json.dumps(manifest, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
