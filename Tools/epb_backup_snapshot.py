#!/usr/bin/env python3
"""Create and verify consistent EPB incremental-backup snapshots.

Only Python's standard library is used so this helper is deployable on the
field host without adding a package-management dependency.
"""

from __future__ import annotations

import argparse
import contextlib
import hashlib
import json
import os
import sqlite3
import sys
from pathlib import Path


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(4 * 1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def sqlite_integrity(path: Path) -> str:
    uri = path.resolve().as_uri() + "?mode=ro"
    with contextlib.closing(sqlite3.connect(uri, uri=True, timeout=5.0)) as connection:
        row = connection.execute("PRAGMA integrity_check").fetchone()
    return "" if row is None else str(row[0])


def snapshot_sqlite(source: Path, destination: Path) -> dict[str, object]:
    if not source.is_file():
        raise FileNotFoundError(source)
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_name(destination.name + ".sqlite-backup-tmp")
    if temporary.exists():
        temporary.unlink()
    try:
        source_uri = source.resolve().as_uri() + "?mode=ro"
        with contextlib.closing(
            sqlite3.connect(source_uri, uri=True, timeout=10.0)
        ) as source_db:
            with contextlib.closing(sqlite3.connect(temporary, timeout=10.0)) as target_db:
                source_db.backup(target_db, pages=256, sleep=0.01)
                target_db.execute("PRAGMA wal_checkpoint(TRUNCATE)")
                target_db.commit()
                target_db.execute("PRAGMA journal_mode=DELETE")
                target_db.commit()
        integrity = sqlite_integrity(temporary)
        if integrity.lower() != "ok":
            raise RuntimeError(f"SQLite integrity_check failed: {integrity}")
        os.replace(temporary, destination)
        for suffix in ("-wal", "-shm"):
            sidecar = destination.with_name(destination.name + suffix)
            if sidecar.exists():
                sidecar.unlink()
        return {
            "source_length": source.stat().st_size,
            "snapshot_length": destination.stat().st_size,
            "sha256": sha256(destination),
            "sqlite_integrity": integrity,
        }
    finally:
        if temporary.exists():
            temporary.unlink()


def verify_tree(root: Path, manifest_path: Path) -> dict[str, object]:
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    errors: list[str] = []
    checked = 0
    for entry in manifest.get("files", []):
        relative = str(entry.get("relative_path", ""))
        path = (root / relative).resolve()
        try:
            path.relative_to(root.resolve())
        except ValueError:
            errors.append(f"path escapes snapshot root: {relative}")
            continue
        if not path.is_file():
            errors.append(f"missing: {relative}")
            continue
        checked += 1
        actual_length = path.stat().st_size
        if actual_length != int(entry.get("snapshot_length", -1)):
            errors.append(f"length mismatch: {relative}")
        if sha256(path).lower() != str(entry.get("sha256", "")).lower():
            errors.append(f"sha256 mismatch: {relative}")
        lowered = relative.lower()
        if lowered.endswith(("-wal", "-shm", ".wal", ".shm")):
            errors.append(f"active sqlite sidecar included: {relative}")
        if lowered.endswith((".db", ".sqlite", ".sqlite3")):
            try:
                integrity = sqlite_integrity(path)
                if integrity.lower() != "ok":
                    errors.append(f"sqlite integrity failed: {relative}: {integrity}")
            except Exception as exc:  # verification must aggregate every defect
                errors.append(f"sqlite open failed: {relative}: {exc}")
        if lowered.endswith(".csv") and not bool(entry.get("in_progress", False)):
            if actual_length > 0:
                with path.open("rb") as stream:
                    stream.seek(-1, os.SEEK_END)
                    if stream.read(1) not in (b"\n", b"\r"):
                        errors.append(f"sealed csv has incomplete line tail: {relative}")
        if lowered.endswith(".csv.tmp") and not bool(entry.get("in_progress", False)):
            errors.append(f"active csv is not marked InProgress: {relative}")
    return {"verified": not errors, "checked_files": checked, "errors": errors}


def main() -> int:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)
    snapshot = subparsers.add_parser("snapshot-sqlite")
    snapshot.add_argument("--source", type=Path, required=True)
    snapshot.add_argument("--destination", type=Path, required=True)
    verify = subparsers.add_parser("verify-tree")
    verify.add_argument("--root", type=Path, required=True)
    verify.add_argument("--manifest", type=Path, required=True)
    args = parser.parse_args()

    try:
        if args.command == "snapshot-sqlite":
            result = snapshot_sqlite(args.source, args.destination)
        else:
            result = verify_tree(args.root, args.manifest)
        print(json.dumps(result, ensure_ascii=False, sort_keys=True))
        return 0 if result.get("verified", True) else 2
    except Exception as exc:
        print(json.dumps({"verified": False, "error": str(exc)}, ensure_ascii=False))
        return 2


if __name__ == "__main__":
    sys.exit(main())
