"""Read-only cycle/accounting comparison; never migrates archives or invents curves."""
import argparse
import hashlib
import json
import sqlite3
from pathlib import Path
import xml.etree.ElementTree as ET


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def reconcile(root):
    sources = [root / "index.db", root / "Config/TestConfig.xml",
               root / "Recovery/unattended-run-checkpoint.json"]
    before = {str(p.relative_to(root)): sha(p) for p in sources}
    checkpoint = json.loads(sources[2].read_text(encoding="utf-8-sig"))
    records = {int(r.findtext("Id")): r for r in ET.parse(sources[1]).findall(".//EpbRecords/Record")}
    with sqlite3.connect(sources[0].as_uri() + "?mode=ro&immutable=1", uri=True) as db:
        db.row_factory = sqlite3.Row
        integrity = db.execute("PRAGMA quick_check").fetchone()[0]
        rows = [dict(r) for r in db.execute("""SELECT epb_id,COUNT(*) AS attempts,
COALESCE(SUM(mechanical_completed),0) AS mechanical_receipts,
SUM(CASE WHEN status='completed' AND cycle_number>0 THEN 1 ELSE 0 END) AS valid_formal,
SUM(CASE WHEN status IN ('learning_completed','qualification_completed') THEN 1 ELSE 0 END) AS learning_qualification,
SUM(CASE WHEN status='running' THEN 1 ELSE 0 END) AS open_attempts,
SUM(CASE WHEN status NOT IN ('completed','learning_completed','qualification_completed','running') THEN 1 ELSE 0 END) AS abnormal,
MAX(cycle_number) AS maximum_attempt_number FROM epb_cycles GROUP BY epb_id""")]
    for row in rows:
        channel = row["epb_id"]
        config = records.get(channel)
        row["xml_mechanical"] = int(config.findtext("MechanicalCycleCount", "0")) if config is not None else None
        row["xml_run_count"] = int(config.findtext("RunCount", "0")) if config is not None else None
        target = int(config.findtext("TotalCount", "0")) if config is not None else None
        remaining = checkpoint.get("RemainingFormalCycles", {}).get(str(channel))
        row["checkpoint_remaining"] = remaining
        row["checkpoint_implied_mechanical"] = target - remaining if target is not None and remaining is not None else None
        row["checkpoint_minus_db_receipts"] = row["checkpoint_implied_mechanical"] - row["mechanical_receipts"] if row["checkpoint_implied_mechanical"] is not None else None
    after = {str(p.relative_to(root)): sha(p) for p in sources}
    if after != before:
        raise RuntimeError("Source changed during read-only reconciliation: " + str(root))
    return {"archive": str(root), "sqlite_quick_check": integrity, "source_sha256": before,
            "source_unchanged": True, "channels": rows}


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--backups", type=Path, default=Path(r"D:\EPB_Data\Backups"))
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    names = ["10243-028_V2.16.0.0_I0037_0905_1653", "10243-028_V2.16.0.0_I0038_0905_1902",
             "10243-028_V2.16.0.0_I0039_0905_1950", "10243-028_V2.14.2.11_I0040_0905_2258"]
    result = {"method": "SQLite mode=ro immutable; input SHA-256 before/after; no archive migration",
              "interpretation": "DB counts reflect retained evidence only; differences are NOT automatic correction instructions",
              "archives": [reconcile((args.backups / name).resolve()) for name in names]}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print("PASS ReadOnlyArchiveReconciliation 4/4; output=" + str(args.output))
