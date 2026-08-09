#!/usr/bin/env python3
"""Check declared Skill runtime prerequisites without installing anything."""

from __future__ import annotations

import argparse
import importlib.metadata
import json
import sys
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--catalog", type=Path, required=True)
    parser.add_argument("--name", action="append", required=True)
    args = parser.parse_args()

    catalog = json.loads(args.catalog.read_text(encoding="utf-8"))
    records = {item["name"]: item for item in catalog["skills"]}
    failures: list[str] = []
    for name in args.name:
        record = records[name]
        for package in record.get("python_packages", []):
            distribution = package["distribution"]
            expected = package["version"]
            try:
                actual = importlib.metadata.version(distribution)
            except importlib.metadata.PackageNotFoundError:
                failures.append(f"{name}: missing Python package {distribution}=={expected}")
                continue
            if actual != expected:
                failures.append(
                    f"{name}: Python package {distribution} must be {expected}, found {actual}"
                )
    if failures:
        for failure in failures:
            print(failure, file=sys.stderr)
        print("No package was installed or changed.", file=sys.stderr)
        return 1
    print("skill prerequisites: PASS")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
