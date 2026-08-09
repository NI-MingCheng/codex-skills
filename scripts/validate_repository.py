#!/usr/bin/env python3
"""Validate the public Skills repository without modifying it."""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path

import yaml


ROOT = Path(__file__).resolve().parents[1]
REQUIRED = {
    ".editorconfig",
    ".gitattributes",
    ".github/workflows/validate.yml",
    ".gitignore",
    "AGENTS.md",
    "LICENSE",
    "README.md",
    "SECURITY.md",
    "catalog.json",
    "docs/installation.md",
    "docs/security.md",
    "docs/updating.md",
    "runtime/requirements-codex.txt",
    "scripts/bootstrap.ps1",
    "scripts/check_prerequisites.py",
    "scripts/common.ps1",
    "scripts/proxy-preflight.ps1",
    "scripts/sync-skills.ps1",
    "scripts/test-sync.ps1",
    "scripts/validate.ps1",
    "scripts/validate_repository.py",
}
FORBIDDEN_NAMES = {
    "auth.json",
    "config.toml",
    "history.jsonl",
    "repositories.json",
    "codex-text.runtime",
}
TEXT_NAMES = {"codex-text"}
TEXT_SUFFIXES = {
    ".json",
    ".md",
    ".ps1",
    ".py",
    ".txt",
    ".yaml",
    ".yml",
    ".cs",
}


def fail(message: str) -> None:
    raise AssertionError(message)


def files() -> list[Path]:
    result: list[Path] = []
    for path in ROOT.rglob("*"):
        if ".git" in path.parts:
            continue
        if path.is_symlink():
            fail(f"symlink is forbidden: {path.relative_to(ROOT)}")
        if path.is_file():
            result.append(path)
    return result


def is_text(path: Path) -> bool:
    return path.name in TEXT_NAMES or path.suffix.lower() in TEXT_SUFFIXES or path.name.startswith(".")


def validate_paths(all_files: list[Path]) -> None:
    relative = {path.relative_to(ROOT).as_posix() for path in all_files}
    missing = sorted(REQUIRED - relative)
    if missing:
        fail(f"required files missing: {', '.join(missing)}")
    for path in all_files:
        rel = path.relative_to(ROOT)
        if path.name in FORBIDDEN_NAMES:
            fail(f"device-local file is forbidden: {rel}")
        if "__pycache__" in path.parts or path.suffix.lower() in {".pyc", ".pyo"}:
            fail(f"generated Python file is forbidden: {rel}")
        if any(part.lower() in {"plugin", "plugins", ".codex-plugin"} for part in rel.parts):
            fail(f"Plugin content is forbidden: {rel}")


def validate_text(all_files: list[Path]) -> None:
    forbidden = (
        "BEGIN " + "PRIVATE KEY",
        "Authorization: " + "Bearer ",
        "api_" + "token",
        "D:/Programs/" + "miniconda3",
        "D:\\Programs\\" + "miniconda3",
        "C:\\Users\\" + "nmc",
    )
    for path in all_files:
        if not is_text(path):
            continue
        data = path.read_bytes()
        rel = path.relative_to(ROOT)
        if data.startswith(b"\xef\xbb\xbf"):
            fail(f"UTF-8 BOM is forbidden: {rel}")
        try:
            text = data.decode("utf-8")
        except UnicodeDecodeError as exc:
            fail(f"text is not UTF-8: {rel}: {exc}")
        if "\r" in text:
            fail(f"repository text must use LF: {rel}")
        if text and not text.endswith("\n"):
            fail(f"text lacks final newline: {rel}")
        if ("[" + "TODO:") in text:
            fail(f"placeholder remains: {rel}")
        for value in forbidden:
            if value in text:
                fail(f"sensitive, branded, or machine-local content found in {rel}")


def validate_catalog() -> list[str]:
    catalog = json.loads((ROOT / "catalog.json").read_text(encoding="utf-8"))
    if catalog.get("schema_version") != 1:
        fail("unsupported catalog schema")
    if catalog.get("repository") != "https://github.com/NI-MingCheng/codex-skills.git":
        fail("catalog repository is not the canonical public source")
    records = catalog.get("skills")
    if not isinstance(records, list) or not records:
        fail("catalog has no Skills")
    names = [record.get("name") for record in records]
    if len(names) != len(set(names)):
        fail("catalog contains duplicate Skill names")
    folders = sorted(path.name for path in (ROOT / "skills").iterdir() if path.is_dir())
    if sorted(names) != folders:
        fail("catalog and skills/ folders disagree")
    for record in records:
        name = record["name"]
        expected_path = f"skills/{name}"
        if record.get("path") != expected_path:
            fail(f"catalog path mismatch: {name}")
        if not re.fullmatch(r"\d+\.\d+\.\d+", str(record.get("version", ""))):
            fail(f"invalid semantic version: {name}")
        if record.get("requires_conda_codex") is not True:
            fail(f"Skill must explicitly declare codex runtime requirement: {name}")
        packages = record.get("python_packages")
        if not isinstance(packages, list):
            fail(f"python_packages must be a list: {name}")
        for package in packages:
            if set(package) != {"distribution", "version"}:
                fail(f"invalid Python package declaration: {name}")
    return names


def validate_skills(names: list[str]) -> None:
    for name in names:
        root = ROOT / "skills" / name
        skill_path = root / "SKILL.md"
        agent_path = root / "agents" / "openai.yaml"
        if not skill_path.is_file() or not agent_path.is_file():
            fail(f"Skill metadata missing: {name}")
        text = skill_path.read_text(encoding="utf-8")
        match = re.match(r"^---\n(.*?)\n---\n", text, re.DOTALL)
        if not match:
            fail(f"invalid Skill frontmatter: {name}")
        frontmatter = yaml.safe_load(match.group(1))
        if set(frontmatter) != {"name", "description"}:
            fail(f"Skill frontmatter may contain only name and description: {name}")
        if frontmatter["name"] != name or not frontmatter["description"]:
            fail(f"Skill name or description invalid: {name}")
        agent = yaml.safe_load(agent_path.read_text(encoding="utf-8"))
        interface = agent.get("interface", {})
        if set(interface) != {"display_name", "short_description", "default_prompt"}:
            fail(f"agents/openai.yaml interface invalid: {name}")
        if f"${name}" not in interface["default_prompt"]:
            fail(f"default prompt does not name Skill: {name}")


def validate_safety() -> None:
    scripts = {
        path.name: path.read_text(encoding="utf-8")
        for path in (ROOT / "scripts").glob("*.ps1")
    }
    install_patterns = (
        r"\bwinget\s+install\b",
        r"\bchoco\s+install\b",
        r"\bscoop\s+install\b",
        r"\bconda\s+(?:create|install|update|remove)\b",
        r"\bpip\s+install\b",
        r"\bInstall-Package\b",
    )
    for name, text in scripts.items():
        for pattern in install_patterns:
            if re.search(pattern, text, flags=re.IGNORECASE):
                fail(f"user-device script contains installer behavior: {name}: {pattern}")
    proxy = scripts["proxy-preflight.ps1"]
    for marker in (
        "Proxy URLs containing credentials are not allowed",
        "Only a loopback proxy endpoint is accepted automatically",
        "https://github.com/",
        "https://chatgpt.com/",
        "https://api.openai.com/v1/models",
        "User proxy environment changed",
    ):
        if marker not in proxy:
            fail(f"proxy safety marker missing: {marker}")
    sync = scripts["sync-skills.ps1"]
    for marker in (
        "pull --ff-only",
        "proxy-preflight.ps1",
        "config.toml",
        "hooks.json",
        "Plugin",
        "Restore-Skill",
    ):
        if marker not in sync:
            fail(f"sync safety marker missing: {marker}")
    if sync.find("proxy-preflight.ps1") > sync.find("pull --ff-only"):
        fail("sync does not run proxy preflight before pull")


def main() -> int:
    all_files = files()
    validate_paths(all_files)
    validate_text(all_files)
    names = validate_catalog()
    validate_skills(names)
    validate_safety()
    print(f"repository validation: PASS ({len(all_files)} files, {len(names)} Skills)")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except AssertionError as exc:
        print(f"repository validation: FAIL: {exc}", file=sys.stderr)
        raise SystemExit(1)
