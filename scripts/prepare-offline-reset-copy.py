#!/usr/bin/env python3
"""Explicit journal preparation of a stopped COPY only; never called on app startup."""
import argparse
import hashlib
import json
import pathlib
import sqlite3
import sys


def prepare(database: str, confirmed: bool) -> dict:
    path = pathlib.Path(database)
    if not confirmed or not path.is_absolute() or path.is_symlink() or not path.is_file():
        raise ValueError("An existing absolute DB and --confirm-offline-copy are required.")
    for suffix in ("-wal", "-shm", "-journal"):
        sidecar = pathlib.Path(str(path) + suffix)
        if sidecar.exists() and sidecar.stat().st_size:
            raise ValueError("Pending SQLite sidecar: stop and review the copy; do not remove sidecars.")
    # mode=rw never creates a missing DB. A bounded lock timeout prevents indefinite waits.
    connection = sqlite3.connect(path.as_uri() + "?mode=rw", uri=True, timeout=1)
    try:
        if connection.execute("PRAGMA integrity_check").fetchall() != [("ok",)]:
            raise ValueError("SQLite integrity check failed.")
        if connection.execute("PRAGMA foreign_key_check").fetchall():
            raise ValueError("SQLite foreign-key check failed.")
        before = row_digest(connection)
        mode = connection.execute("PRAGMA journal_mode=DELETE").fetchone()[0]
        if mode != "delete":
            raise ValueError("Could not prepare journal mode; keep the original archive.")
        if before != row_digest(connection):
            raise ValueError("Row verification failed; keep the original archive.")
    finally:
        connection.close()
    return {"status": "offline-copy-prepared", "journalMode": mode,
            "databaseSha256": hashlib.sha256(path.read_bytes()).hexdigest()}


def row_digest(connection: sqlite3.Connection) -> str:
    """Internal only; never print personal rows. Sorting repr supports binary key bytes."""
    digest = hashlib.sha256()
    tables = connection.execute(
        "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name").fetchall()
    for (table,) in tables:
        identifier = '"' + table.replace('"', '""') + '"'
        digest.update(table.encode())
        for row in sorted(repr(row) for row in connection.execute("SELECT * FROM " + identifier)):
            digest.update(row.encode())
            digest.update(b"\n")
    return digest.hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--database", required=True)
    parser.add_argument("--confirm-offline-copy", action="store_true")
    args = parser.parse_args()
    try:
        print(json.dumps(prepare(args.database, args.confirm_offline_copy)))
        return 0
    except (ValueError, sqlite3.Error, OSError):
        print("Offline copy preparation refused/failed. Keep the original archive; inspect offline.", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())