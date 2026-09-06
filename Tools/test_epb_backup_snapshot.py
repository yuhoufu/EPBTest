import json
import sqlite3
import tempfile
import unittest
from pathlib import Path

import epb_backup_snapshot as backup


class EpbBackupSnapshotTests(unittest.TestCase):
    def test_online_backup_reads_live_wal_without_copying_sidecars(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            source = root / "active" / "index.db"
            source.parent.mkdir()
            writer = sqlite3.connect(source)
            writer.execute("PRAGMA journal_mode=WAL")
            writer.execute("CREATE TABLE cycles(id INTEGER PRIMARY KEY, value TEXT)")
            writer.execute("INSERT INTO cycles(value) VALUES ('sealed')")
            writer.commit()
            destination = root / "stage" / "index.db"
            try:
                result = backup.snapshot_sqlite(source, destination)
            finally:
                writer.close()
            self.assertEqual("ok", result["sqlite_integrity"])
            import contextlib
            with contextlib.closing(sqlite3.connect(destination)) as reader:
                self.assertEqual("sealed", reader.execute("SELECT value FROM cycles").fetchone()[0])
            self.assertFalse((destination.parent / "index.db-wal").exists())
            self.assertFalse((destination.parent / "index.db-shm").exists())

    def test_manifest_marks_active_csv_and_detects_hash_tamper(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            active = root / "running.csv.tmp"
            active.write_bytes(b"1,partial")
            manifest = root / "backup-manifest.json"
            manifest.write_text(json.dumps({"files": [{
                "relative_path": active.name,
                "snapshot_length": active.stat().st_size,
                "sha256": backup.sha256(active),
                "in_progress": True,
            }]}), encoding="utf-8")
            self.assertTrue(backup.verify_tree(root, manifest)["verified"])
            active.write_bytes(b"tampered")
            verification = backup.verify_tree(root, manifest)
            self.assertFalse(verification["verified"])
            self.assertTrue(any("sha256 mismatch" in item for item in verification["errors"]))

    def test_sealed_csv_requires_complete_line_tail(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            sealed = root / "sealed.csv"
            sealed.write_bytes(b"a,b\n1,unfinished")
            manifest = root / "backup-manifest.json"
            manifest.write_text(json.dumps({"files": [{
                "relative_path": sealed.name,
                "snapshot_length": sealed.stat().st_size,
                "sha256": backup.sha256(sealed),
                "in_progress": False,
            }]}), encoding="utf-8")
            verification = backup.verify_tree(root, manifest)
            self.assertFalse(verification["verified"])
            self.assertTrue(any("incomplete line tail" in item for item in verification["errors"]))


if __name__ == "__main__":
    unittest.main()
