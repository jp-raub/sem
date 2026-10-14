#!/usr/bin/env python3
import importlib.util
import pathlib
import sqlite3
import tempfile

spec = importlib.util.spec_from_file_location("prepare",
    pathlib.Path(__file__).resolve().parents[1] / "scripts/prepare-offline-reset-copy.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
passed = 0


def check(value, label):
    global passed
    assert value, label
    passed += 1
    print("PASS:", label)


def refused(path, confirmed, label):
    before = path.read_bytes() if path.exists() else None
    try:
        module.prepare(str(path), confirmed)
    except (ValueError, sqlite3.Error, OSError):
        check((path.read_bytes() if path.exists() else None) == before, label)
    else:
        raise AssertionError(label)


with tempfile.TemporaryDirectory(prefix="sem-copy-prep-") as directory:
    root = pathlib.Path(directory)
    path = root / "synthetic.db"
    connection = sqlite3.connect(path)
    connection.executescript("CREATE TABLE Test(Id TEXT PRIMARY KEY, KeyBytes BLOB);"
                            "INSERT INTO Test VALUES ('retained',X'01020304');")
    connection.execute("PRAGMA journal_mode=WAL")
    connection.close()
    before = path.read_bytes()
    refused(path, False, "Explicit offline-copy confirmation is mandatory")
    missing = root / "missing.db"
    refused(missing, True, "Missing database is never created")
    link = root / "symlink.db"
    link.symlink_to(path)
    refused(link, True, "Symlink is refused")
    for suffix in ("-wal", "-shm", "-journal"):
        sidecar = pathlib.Path(str(path) + suffix)
        sidecar.write_bytes(b"pending")
        refused(path, True, "Nonempty " + suffix + " is refused without DB changes")
        sidecar.unlink()
    result = module.prepare(str(path), True)
    check(result["journalMode"] == "delete" and path.read_bytes() != before,
          "Stopped WAL copy gets a new DELETE-mode file hash")
    connection = sqlite3.connect(path)
    check(connection.execute("SELECT * FROM Test").fetchall() == [("retained", b"\1\2\3\4")],
          "Journal preparation retains synthetic row and binary key bytes")
    check(connection.execute("PRAGMA integrity_check").fetchall() == [("ok",)],
          "Prepared copy passes integrity check")
    connection.close()
    check(module.prepare(str(path), True)["databaseSha256"] == result["databaseSha256"],
          "Repeated preparation is byte-idempotent")
print("Offline reset copy checks passed:", passed)