#!/usr/bin/env python3
"""Remember first-party and third-party repository classifications."""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import tempfile
from datetime import datetime, timezone
from pathlib import Path


REGISTRY = Path(
    os.environ.get(
        "GIT_WORKFLOW_REGISTRY",
        Path.home() / ".codex" / "git-workflow" / "repositories.json",
    )
)
WINDOWS_NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0) if os.name == "nt" else 0


def canonical_root(value: str) -> str:
    return os.path.normcase(str(Path(value).expanduser().resolve()))


def origin_for(root: str) -> str | None:
    completed = subprocess.run(
        ["git", "-C", root, "config", "--get", "remote.origin.url"],
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        check=False,
        creationflags=WINDOWS_NO_WINDOW,
    )
    if completed.returncode != 0:
        return None
    value = completed.stdout.decode("utf-8", errors="strict").strip()
    return value or None


def load_registry() -> dict:
    if not REGISTRY.exists():
        return {"version": 1, "repositories": []}
    data = json.loads(REGISTRY.read_text(encoding="utf-8"))
    if data.get("version") != 1 or not isinstance(data.get("repositories"), list):
        raise SystemExit("unsupported repository registry schema")
    return data


def save_registry(data: dict) -> None:
    REGISTRY.parent.mkdir(parents=True, exist_ok=True)
    payload = json.dumps(data, ensure_ascii=False, indent=2) + "\n"
    handle, temporary = tempfile.mkstemp(prefix="repositories-", suffix=".json", dir=REGISTRY.parent)
    try:
        with os.fdopen(handle, "w", encoding="utf-8", newline="\n") as stream:
            stream.write(payload)
        os.replace(temporary, REGISTRY)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def emit(value: dict) -> None:
    print(json.dumps(value, ensure_ascii=True, sort_keys=True))


def lookup(args: argparse.Namespace) -> int:
    root = canonical_root(args.repo)
    origin = origin_for(root)
    record = next((item for item in load_registry()["repositories"] if item.get("root") == root), None)
    if record is None:
        emit({"status": "unknown", "root": root, "origin": origin, "reason": "not-recorded"})
        return 0
    recorded_origin = record.get("origin")
    if recorded_origin and origin and recorded_origin != origin:
        emit({"status": "unknown", "root": root, "origin": origin, "reason": "origin-changed"})
        return 0
    emit({"status": "known", "root": root, "origin": origin, "classification": record["classification"]})
    return 0


def remember(args: argparse.Namespace) -> int:
    root = canonical_root(args.repo)
    origin = origin_for(root)
    data = load_registry()
    data["repositories"] = [item for item in data["repositories"] if item.get("root") != root]
    data["repositories"].append(
        {
            "root": root,
            "origin": origin,
            "classification": args.classification,
            "recorded_at": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        }
    )
    data["repositories"].sort(key=lambda item: item["root"])
    save_registry(data)
    emit({"status": "recorded", "root": root, "origin": origin, "classification": args.classification})
    return 0


def list_records(_args: argparse.Namespace) -> int:
    emit(load_registry())
    return 0


def main() -> int:
    global REGISTRY
    parser = argparse.ArgumentParser()
    parser.add_argument("--registry", type=Path, default=REGISTRY, help=argparse.SUPPRESS)
    subparsers = parser.add_subparsers(dest="action", required=True)
    lookup_parser = subparsers.add_parser("lookup")
    lookup_parser.add_argument("--repo", required=True)
    lookup_parser.set_defaults(func=lookup)
    remember_parser = subparsers.add_parser("remember")
    remember_parser.add_argument("--repo", required=True)
    remember_parser.add_argument("--classification", required=True, choices=("first-party", "third-party"))
    remember_parser.set_defaults(func=remember)
    list_parser = subparsers.add_parser("list")
    list_parser.set_defaults(func=list_records)
    args = parser.parse_args()
    REGISTRY = args.registry.expanduser().resolve()
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
