#!/usr/bin/env python3
"""Read-only incident summary for an EPB index.db sealed copy."""

from __future__ import annotations

import argparse
import sqlite3
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("database", type=Path)
    args = parser.parse_args()
    uri = args.database.resolve().as_uri() + "?mode=ro"
    with sqlite3.connect(uri, uri=True) as connection:
        connection.row_factory = sqlite3.Row
        tables = [
            row[0]
            for row in connection.execute(
                "select name from sqlite_master where type='table' order by name"
            )
        ]
        print("tables:", ", ".join(tables))
        for table in tables:
            columns = [
                row[1]
                for row in connection.execute(f'pragma table_info("{table}")')
            ]
            print(f"{table}: {', '.join(columns)}")

        cycle_table = next(
            (
                table
                for table in tables
                if {"epb_id", "cycle_number", "status"}.issubset(
                    {
                        row[1]
                        for row in connection.execute(
                            f'pragma table_info("{table}")'
                        )
                    }
                )
            ),
            None,
        )
        if cycle_table is None:
            return 0

        print("\nstatus counts:")
        for row in connection.execute(
            f'''select status, count(*) as count
                from "{cycle_table}" group by status order by count desc'''
        ):
            print(dict(row))

        columns = {
            row[1]
            for row in connection.execute(f'pragma table_info("{cycle_table}")')
        }
        projection = [
            name
            for name in (
                "epb_id",
                "cycle_number",
                "status",
                "sample_count",
                "start_time",
                "end_time",
            )
            if name in columns
        ]
        projection_sql = ", ".join(f'"{name}"' for name in projection)
        print("\nunfinished cycles:")
        rows = connection.execute(
            f'''select {projection_sql} from "{cycle_table}"
                where lower(status) in ('running', 'started', 'active')
                order by epb_id, cycle_number'''
        ).fetchall()
        if not rows:
            print("none")
        for row in rows:
            print(dict(row))

        print("\nlatest negative cycles for affected channels:")
        for epb_id in (4, 5, 9, 10, 11):
            rows = connection.execute(
                f'''select {projection_sql} from "{cycle_table}"
                    where epb_id=? and cycle_number<0
                    order by rowid desc limit 8''',
                (epb_id,),
            ).fetchall()
            for row in rows:
                print(dict(row))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
