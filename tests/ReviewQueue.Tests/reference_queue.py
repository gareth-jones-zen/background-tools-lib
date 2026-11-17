"""The Python reference for ReviewQueue's file format: the read, write, id, add, replace and update of a Python
review-queue module (pending.py, 10 Nov 2026), copied with the file and the lock's name given on the command line
instead of fixed (so the broken and temporary files are named from the file's own name rather than "pending").
Nothing else is changed. win_background's exclusive() is the lock, as in that module.

    python reference_queue.py <win-background src> <file> <lock> add <source> <type> <fields json>
    ... replace <source> <items json>          ... update <source> <match json> <fields json>
    ... items [<source>]                        ... rewrite   (read the file and write it straight back)
    ... hold                                    (take the lock, print "held", release on a line from stdin)
    ... try                                     (print "free" or "busy" without waiting)
    ... many <source> <count> <prefix>          (add <count> items, each its own write)
"""

import datetime as dt
import hashlib
import json
import os
import pathlib
import sys

sys.path.insert(0, sys.argv[1])
from win_background import Lock, exclusive  # noqa: E402

FILE = pathlib.Path(sys.argv[2])
LOCAL = FILE.parent
NAME = sys.argv[3]


def _read() -> dict:
    try:
        return json.loads(FILE.read_text(encoding="utf-8"))
    except FileNotFoundError:
        return {"items": []}
    except json.JSONDecodeError:
        broken = FILE.with_name(f"{FILE.stem}.broken-{dt.datetime.now():%Y%m%d-%H%M%S}{FILE.suffix}")
        FILE.replace(broken)
        return {"items": []}


def _write(data: dict) -> None:
    LOCAL.mkdir(parents=True, exist_ok=True)
    temp = FILE.with_name(f"{FILE.stem}.{os.getpid()}.tmp")
    temp.write_text(json.dumps(data, indent=2, ensure_ascii=False), encoding="utf-8")
    temp.replace(FILE)


def _locked():
    return exclusive(NAME, 60, LOCAL, what="queue write")


def _id(item: dict) -> str:
    basis = json.dumps({k: v for k, v in item.items() if k not in ("id", "added")}, sort_keys=True, default=str)
    return hashlib.sha256(basis.encode()).hexdigest()[:8]


def add(source: str, type: str, **fields) -> str:
    item = {"source": source, "type": type, **fields}
    item["id"] = _id(item)
    with _locked():
        data = _read()
        if not any(i["id"] == item["id"] for i in data["items"]):
            data["items"].append({"id": item["id"], "source": source, "type": type,
                                  "added": dt.datetime.now().isoformat(timespec="seconds"), **fields})
            _write(data)
    return item["id"]


def replace(source: str, items: list) -> None:
    with _locked():
        data = _read()
        data["items"] = [i for i in data["items"] if i["source"] != source]
        now = dt.datetime.now().isoformat(timespec="seconds")
        for fields in items:
            item = {"source": source, **fields}
            data["items"].append({"id": _id(item), "added": now, **item})
        _write(data)


def update(source: str, match: dict, **fields) -> int:
    with _locked():
        data = _read()
        changed = 0
        for item in data["items"]:
            if item["source"] == source and all(item.get(k) == v for k, v in match.items()):
                item.update(fields)
                changed += 1
        if changed:
            _write(data)
    return changed


def items(source=None) -> list:
    return [i for i in _read()["items"] if source is None or i["source"] == source]


def out(value) -> None:
    sys.stdout.buffer.write(json.dumps(value, ensure_ascii=True).encode() + b"\n")
    sys.stdout.flush()


command, rest = sys.argv[4], sys.argv[5:]
if command == "add":
    out(add(rest[0], rest[1], **json.loads(rest[2])))
elif command == "replace":
    replace(rest[0], json.loads(rest[1]))
    out(None)
elif command == "update":
    out(update(rest[0], json.loads(rest[1]), **json.loads(rest[2])))
elif command == "items":
    out(items(rest[0] if rest else None))
elif command == "rewrite":
    with _locked():
        _write(_read())
    out(None)
elif command == "hold":
    with _locked():
        out("held")
        sys.stdin.readline()
    out("released")
elif command == "try":
    lock = Lock(NAME, LOCAL)
    got = lock.acquire(0)
    out("free" if got else "busy")
    if got:
        lock.release()
elif command == "many":
    for n in range(int(rest[1])):
        add(rest[0], "t", title=f"{rest[2]} {n}")
    out(None)
