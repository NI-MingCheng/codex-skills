#!/usr/bin/env python3
"""Terminal-independent text and command encoding guard for Codex."""

from __future__ import annotations

import argparse
import base64
import codecs
import ctypes
import fnmatch
import hashlib
import json
import locale
import os
import re
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import uuid
from dataclasses import dataclass
from pathlib import Path
from types import SimpleNamespace
from typing import Any

try:
    import charset_normalizer
    from charset_normalizer import from_bytes
except ImportError:  # Reported cleanly only when heuristic detection is needed.
    charset_normalizer = None
    from_bytes = None


VERSION = 1
CHARSET_NORMALIZER_VERSION = "3.4.8"
MIN_COHERENCE = 60.0
MIN_LEAD = 15.0
SHORT_NON_ASCII = 32
UTF8_BOM = codecs.BOM_UTF8
BOMS = (
    (codecs.BOM_UTF32_LE, "utf-32-le", "utf-32-le"),
    (codecs.BOM_UTF32_BE, "utf-32-be", "utf-32-be"),
    (UTF8_BOM, "utf-8", "utf-8"),
    (codecs.BOM_UTF16_LE, "utf-16-le", "utf-16-le"),
    (codecs.BOM_UTF16_BE, "utf-16-be", "utf-16-be"),
)
BOM_NAMES = {bom.hex(): name for bom, _, name in BOMS}
NAME_TO_BOM = {name: bom for bom, _, name in BOMS}

CODEX_HOME = Path(os.environ.get("CODEX_HOME", Path.home() / ".codex"))
POLICY_HOME = Path(os.environ.get("ENCODING_GUARD_HOME", CODEX_HOME / "encoding-guard"))
POLICY_PATH = POLICY_HOME / "policies.json"
STATE_HOME = Path(
    os.environ.get(
        "ENCODING_GUARD_STATE",
        Path(tempfile.gettempdir()) / "codex-encoding-guard",
    )
)
TRANSACTION_DIR = STATE_HOME / "transactions"
JOB_DIR = STATE_HOME / "jobs"
FAILURE_DIR = STATE_HOME / "failures"
WINDOWS_NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0) if os.name == "nt" else 0


class GuardError(Exception):
    def __init__(self, code: str, message: str, **details: Any) -> None:
        super().__init__(message)
        self.code = code
        self.message = message
        self.details = details


@dataclass
class Decoded:
    text: str
    encoding: str
    bom: bytes
    source: str
    coherence: float = 100.0
    candidates: list[dict[str, Any]] | None = None


def _write_utf8(stream: str, text: str) -> None:
    handle = sys.stdout.buffer if stream == "stdout" else sys.stderr.buffer
    handle.write(text.encode("utf-8"))
    handle.flush()


def _ascii_json(value: Any, *, indent: int | None = None) -> str:
    return json.dumps(value, ensure_ascii=True, indent=indent, sort_keys=True)


def _sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _canonical_encoding(name: str) -> str:
    clean = name.strip().lower().replace("_", "-")
    aliases = {
        "auto": "auto",
        "utf8": "utf-8",
        "utf-8-sig": "utf-8",
        "utf16": "utf-16",
        "utf16le": "utf-16-le",
        "utf16be": "utf-16-be",
        "utf32": "utf-32",
        "utf32le": "utf-32-le",
        "utf32be": "utf-32-be",
        "cp936": "gbk",
        "ms936": "gbk",
        "cp950": "big5",
        "shift-jis": "cp932",
        "shiftjis": "cp932",
        "sjis": "cp932",
    }
    clean = aliases.get(clean, clean)
    if clean == "auto":
        return clean
    try:
        return codecs.lookup(clean).name.replace("_", "-")
    except LookupError as exc:
        raise GuardError("unknown_encoding", f"Unknown encoding: {name}") from exc


def _encoding_family(name: str) -> str:
    name = _canonical_encoding(name)
    if name.startswith("utf-16"):
        return "utf-16"
    if name.startswith("utf-32"):
        return "utf-32"
    return name


def _compatible_encoding(left: str, right: str) -> bool:
    left_c = _canonical_encoding(left)
    right_c = _canonical_encoding(right)
    return left_c == right_c or (
        _encoding_family(left_c) == _encoding_family(right_c)
        and (left_c in {"utf-16", "utf-32"} or right_c in {"utf-16", "utf-32"})
    )


def _detect_bom(data: bytes) -> tuple[bytes, str] | None:
    for bom, encoding, _ in BOMS:
        if data.startswith(bom):
            return bom, encoding
    return None


def _bom_name(bom: bytes) -> str | None:
    return BOM_NAMES.get(bom.hex()) if bom else None


def _newline_style(text: str) -> str:
    counts = _newline_counts(text)
    present = [name for name, count in counts.items() if count]
    if not present:
        return "none"
    return present[0] if len(present) == 1 else "mixed"


def _newline_counts(text: str) -> dict[str, int]:
    crlf = text.count("\r\n")
    return {
        "crlf": crlf,
        "lf": text.count("\n") - crlf,
        "cr": text.count("\r") - crlf,
    }


def _newline_distribution_shift(before: str, after: str) -> float:
    before_counts = _newline_counts(before)
    after_counts = _newline_counts(after)
    before_total = sum(before_counts.values())
    after_total = sum(after_counts.values())
    if not before_total or not after_total:
        return 1.0 if before_total != after_total else 0.0
    return sum(
        abs(before_counts[name] / before_total - after_counts[name] / after_total)
        for name in before_counts
    ) / 2.0


def _normalize_newlines(text: str, style: str) -> str:
    normalized = text.replace("\r\n", "\n").replace("\r", "\n")
    if style == "crlf":
        return normalized.replace("\n", "\r\n")
    if style == "cr":
        return normalized.replace("\n", "\r")
    return normalized


def _portable_path(path: Path | str) -> str:
    raw = str(path).replace("\\", "/")
    match = re.match(r"^/mnt/([A-Za-z])/(.*)$", raw)
    if match:
        raw = f"{match.group(1)}:/{match.group(2)}"
    if re.match(r"^[A-Za-z]:/", raw):
        return raw.casefold()
    return os.path.normpath(raw).replace("\\", "/")


def _default_policy() -> dict[str, Any]:
    return {"version": VERSION, "file_rules": [], "command_rules": []}


def _atomic_json(path: Path, value: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temp_path = path.with_name(f".{path.name}.{uuid.uuid4().hex}.tmp")
    payload = (_ascii_json(value, indent=2) + "\n").encode("ascii")
    with temp_path.open("wb") as handle:
        handle.write(payload)
        handle.flush()
        os.fsync(handle.fileno())
    os.replace(temp_path, path)


def _load_json(path: Path, default: dict[str, Any] | None = None) -> dict[str, Any]:
    if not path.exists():
        if default is None:
            raise GuardError("state_missing", f"State file does not exist: {path}")
        return default
    try:
        value = json.loads(path.read_text(encoding="ascii"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise GuardError("state_invalid", f"Invalid state file: {path}") from exc
    if not isinstance(value, dict):
        raise GuardError("state_invalid", f"State file is not an object: {path}")
    return value


def _load_policy() -> dict[str, Any]:
    policy = _load_json(POLICY_PATH, _default_policy())
    if policy.get("version") != VERSION:
        raise GuardError("policy_version", "Unsupported policy version", found=policy.get("version"))
    policy.setdefault("file_rules", [])
    policy.setdefault("command_rules", [])
    return policy


def _file_policy(path: Path) -> tuple[str, str] | None:
    key = _portable_path(path.resolve())
    matches: list[tuple[int, int, dict[str, Any]]] = []
    for index, rule in enumerate(_load_policy()["file_rules"]):
        root = str(rule.get("root", ""))
        if not root or not (key == root or key.startswith(root.rstrip("/") + "/")):
            continue
        relative = key[len(root) :].lstrip("/") or path.name
        pattern = str(rule.get("glob", "**/*")).replace("\\", "/")
        matched = fnmatch.fnmatch(relative, pattern)
        if pattern.startswith("**/"):
            matched = matched or fnmatch.fnmatch(relative, pattern[3:])
        if matched:
            matches.append((len(root), index, rule))
    if not matches:
        return None
    rule = max(matches, key=lambda item: (item[0], item[1]))[2]
    return _canonical_encoding(str(rule["encoding"])), str(rule["id"])


def _command_policy(shell: str, executable: str, stream: str) -> tuple[str, str] | None:
    executable_name = Path(executable).name.casefold()
    result: tuple[str, str] | None = None
    for rule in _load_policy()["command_rules"]:
        if rule.get("stream") != stream:
            continue
        if rule.get("shell") not in {shell, "*"}:
            continue
        if fnmatch.fnmatch(executable_name, str(rule.get("executable", "*")).casefold()):
            result = (_canonical_encoding(str(rule["encoding"])), str(rule["id"]))
    return result


def _git_encoding(path: Path) -> str | None:
    if not shutil.which("git"):
        return None
    try:
        result = subprocess.run(
            ["git", "-C", str(path.parent), "check-attr", "-z", "working-tree-encoding", "--", str(path)],
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            timeout=5,
            check=False,
            creationflags=WINDOWS_NO_WINDOW,
        )
    except (OSError, subprocess.SubprocessError):
        return None
    parts = result.stdout.split(b"\0")
    if len(parts) < 3:
        return None
    value = parts[2].decode("ascii", errors="ignore").strip()
    if value in {"", "unspecified", "unset", "set"}:
        return None
    return _canonical_encoding(value)


def _declared_encodings(data: bytes, path: Path | None = None) -> list[tuple[str, str]]:
    head = data[:8192]
    declarations: list[tuple[str, str]] = []
    patterns = (
        (rb"<\?xml[^>]{0,256}?encoding\s*=\s*['\"]\s*([A-Za-z0-9._:-]+)", "xml"),
        (rb"<meta[^>]{0,512}?charset\s*=\s*['\"]?\s*([A-Za-z0-9._:-]+)", "html"),
    )
    for pattern, source in patterns:
        for match in re.finditer(pattern, head, flags=re.IGNORECASE):
            try:
                declarations.append((_canonical_encoding(match.group(1).decode("ascii")), source))
            except (UnicodeError, GuardError):
                continue
    if path and path.suffix.casefold() in {".py", ".pyi", ".pyw"}:
        python_head = head[len(UTF8_BOM) :] if head.startswith(UTF8_BOM) else head
        first_two_lines = b"\n".join(python_head.splitlines()[:2])
        python_match = re.search(
            rb"(?:^|\n)[ \t\f]*\#[^\n]*?coding[ \t]*[:=][ \t]*([-\w.]+)",
            first_two_lines,
            flags=re.IGNORECASE,
        )
        if python_match:
            try:
                declarations.append((_canonical_encoding(python_match.group(1).decode("ascii")), "python"))
            except (UnicodeError, GuardError):
                pass
    return declarations


def _structural_unicode(data: bytes) -> str | None:
    if len(data) >= 8 and len(data) % 4 == 0:
        groups = [data[offset::4] for offset in range(4)]
        zero = [group.count(0) / len(group) for group in groups]
        if zero[1] > 0.6 and zero[2] > 0.6 and zero[3] > 0.6:
            return "utf-32-le"
        if zero[0] > 0.6 and zero[1] > 0.6 and zero[2] > 0.6:
            return "utf-32-be"
    if len(data) >= 4 and len(data) % 2 == 0:
        even = data[0::2]
        odd = data[1::2]
        even_zero = even.count(0) / len(even)
        odd_zero = odd.count(0) / len(odd)
        if odd_zero > 0.35 and even_zero < 0.1:
            return "utf-16-le"
        if even_zero > 0.35 and odd_zero < 0.1:
            return "utf-16-be"
    return None


def _system_candidates() -> list[str]:
    names = ["gb18030", "gbk", "big5", "cp932", "cp1252"]
    names.append(locale.getpreferredencoding(False))
    if os.name == "nt":
        try:
            names.extend([f"cp{ctypes.windll.kernel32.GetACP()}", f"cp{ctypes.windll.kernel32.GetOEMCP()}"])
        except (AttributeError, OSError):
            pass
    result: list[str] = []
    for name in names:
        try:
            canonical = _canonical_encoding(name)
        except GuardError:
            continue
        if canonical not in result:
            result.append(canonical)
    return result


def _decode_known(data: bytes, encoding: str, bom: bytes = b"") -> str:
    encoding = _canonical_encoding(encoding)
    payload = data[len(bom) :] if bom and data.startswith(bom) else data
    if encoding == "utf-16":
        if bom == codecs.BOM_UTF16_LE:
            encoding = "utf-16-le"
        elif bom == codecs.BOM_UTF16_BE:
            encoding = "utf-16-be"
        else:
            raise GuardError("ambiguous_utf", "UTF-16 requires a BOM or explicit byte order")
    if encoding == "utf-32":
        if bom == codecs.BOM_UTF32_LE:
            encoding = "utf-32-le"
        elif bom == codecs.BOM_UTF32_BE:
            encoding = "utf-32-be"
        else:
            raise GuardError("ambiguous_utf", "UTF-32 requires a BOM or explicit byte order")
    try:
        text = payload.decode(encoding, errors="strict")
    except UnicodeDecodeError as exc:
        raise GuardError(
            "decode_failed",
            f"Bytes are not valid {encoding}",
            offset=exc.start,
        ) from exc
    if _has_excess_controls(text):
        raise GuardError("binary_input", "Decoded text contains too many control characters")
    return text


def _has_excess_controls(text: str) -> bool:
    controls = sum(ord(char) < 32 and char not in "\t\r\n\f" for char in text)
    return bool(text) and controls / len(text) > 0.02


def _heuristic_decode(data: bytes) -> Decoded:
    if from_bytes is None:
        raise GuardError(
            "dependency_missing",
            "charset-normalizer 3.4.8 is required for legacy encoding detection",
        )
    if getattr(charset_normalizer, "__version__", None) != CHARSET_NORMALIZER_VERSION:
        raise GuardError(
            "dependency_version",
            f"charset-normalizer {CHARSET_NORMALIZER_VERSION} is required for legacy encoding detection",
            found=getattr(charset_normalizer, "__version__", None),
        )
    isolated = [name.replace("-", "_") for name in _system_candidates()]
    matches = from_bytes(data, cp_isolation=isolated, enable_fallback=True)
    candidates: list[dict[str, Any]] = []
    decoded: dict[str, str] = {}
    for match in matches:
        encoding = _canonical_encoding(match.encoding)
        try:
            text = _decode_known(data, encoding)
        except GuardError:
            continue
        coherence = float(match.percent_coherence)
        candidates.append(
            {
                "encoding": encoding,
                "coherence": coherence,
                "chaos": float(match.percent_chaos),
            }
        )
        decoded[encoding] = text
    candidates.sort(key=lambda item: (item["coherence"], -item["chaos"]), reverse=True)
    non_ascii = sum(byte >= 0x80 for byte in data)
    lead = candidates[0]["coherence"] - candidates[1]["coherence"] if len(candidates) > 1 else 100.0
    if (
        not candidates
        or candidates[0]["coherence"] < MIN_COHERENCE
        or lead < MIN_LEAD
        or non_ascii < SHORT_NON_ASCII
    ):
        raise GuardError(
            "ambiguous_encoding",
            "Encoding confidence is too low; add an explicit policy before using this text",
            candidates=candidates[:5],
            non_ascii_bytes=non_ascii,
        )
    best = candidates[0]
    return Decoded(
        decoded[best["encoding"]],
        best["encoding"],
        b"",
        "heuristic",
        best["coherence"],
        candidates[:5],
    )


def decode_bytes(
    data: bytes,
    *,
    explicit: str = "auto",
    path: Path | None = None,
    policy: tuple[str, str] | None = None,
) -> Decoded:
    explicit = _canonical_encoding(explicit)
    bom_info = _detect_bom(data)
    bom = bom_info[0] if bom_info else b""
    authorities: list[tuple[str, str]] = []
    if explicit != "auto":
        authorities.append((explicit, "explicit"))
    elif policy:
        authorities.append((policy[0], f"policy:{policy[1]}"))
    if bom_info:
        authorities.append((bom_info[1], "bom"))
    if path:
        git_encoding = _git_encoding(path)
        if git_encoding:
            authorities.append((git_encoding, "git"))
    for declaration, declaration_source in _declared_encodings(data, path):
        authorities.append((declaration, f"declaration:{declaration_source}"))

    if authorities:
        chosen, source = authorities[0]
        conflicts = [
            {"encoding": encoding, "source": item_source}
            for encoding, item_source in authorities[1:]
            if not _compatible_encoding(chosen, encoding)
        ]
        if conflicts:
            raise GuardError(
                "encoding_conflict",
                "Authoritative encoding declarations conflict",
                selected={"encoding": chosen, "source": source},
                conflicts=conflicts,
            )
        if bom_info and _encoding_family(chosen) in {"utf-16", "utf-32"}:
            chosen = bom_info[1]
        text = _decode_known(data, chosen, bom)
        return Decoded(text, _canonical_encoding(chosen), bom, source)

    structural = _structural_unicode(data)
    if structural:
        try:
            return Decoded(_decode_known(data, structural), structural, b"", "structure", 95.0)
        except GuardError:
            pass
    try:
        return Decoded(_decode_known(data, "utf-8"), "utf-8", b"", "strict-utf8")
    except GuardError as utf8_error:
        if utf8_error.code == "binary_input":
            raise
    return _heuristic_decode(data)


def _file_decoded(path: Path, encoding: str) -> tuple[bytes, Decoded]:
    try:
        data = path.read_bytes()
    except OSError as exc:
        raise GuardError("read_failed", f"Cannot read file: {path}", os_error=str(exc)) from exc
    policy = None if encoding != "auto" else _file_policy(path)
    return data, decode_bytes(data, explicit=encoding, path=path, policy=policy)


def _metadata(path: Path, data: bytes, decoded: Decoded) -> dict[str, Any]:
    return {
        "path": str(path.resolve()),
        "bytes": len(data),
        "sha256": _sha256(data),
        "encoding": decoded.encoding,
        "bom": _bom_name(decoded.bom),
        "newline": _newline_style(decoded.text),
        "source": decoded.source,
        "coherence": decoded.coherence,
        "candidates": decoded.candidates or [],
    }


def cmd_probe(args: argparse.Namespace) -> int:
    path = Path(args.path)
    data, decoded = _file_decoded(path, args.encoding)
    metadata = _metadata(path, data, decoded)
    if args.json:
        print(_ascii_json(metadata, indent=2))
    else:
        print(_ascii_json(metadata))
    return 0


def cmd_read(args: argparse.Namespace) -> int:
    path = Path(args.path)
    _, decoded = _file_decoded(path, args.encoding)
    text = decoded.text
    if args.start_line is not None or args.max_lines is not None:
        if args.start_line is not None and args.start_line < 1:
            raise GuardError("invalid_range", "start-line must be at least 1")
        if args.max_lines is not None and args.max_lines < 0:
            raise GuardError("invalid_range", "max-lines must not be negative")
        lines = text.splitlines(keepends=True)
        start = (args.start_line or 1) - 1
        end = None if args.max_lines is None else start + args.max_lines
        text = "".join(lines[start:end])
    if args.json:
        print(
            _ascii_json(
                {
                    "encoding": decoded.encoding,
                    "source": decoded.source,
                    "text": text,
                },
                indent=2,
            )
        )
    else:
        _write_utf8("stdout", text)
    return 0


def _transaction_paths(token: str) -> tuple[Path, Path]:
    if not re.fullmatch(r"[0-9a-f]{32}", token):
        raise GuardError("invalid_token", "Invalid transaction token")
    return TRANSACTION_DIR / f"{token}.json", TRANSACTION_DIR / f"{token}.utf8"


def cmd_checkout(args: argparse.Namespace) -> int:
    path = Path(args.path).resolve()
    data, decoded = _file_decoded(path, args.encoding)
    TRANSACTION_DIR.mkdir(parents=True, exist_ok=True)
    token = uuid.uuid4().hex
    meta_path, work_path = _transaction_paths(token)
    work_path.write_bytes(decoded.text.encode("utf-8"))
    stat = path.stat()
    metadata = {
        "version": VERSION,
        "token": token,
        "target": str(path),
        "work_file": str(work_path),
        "original_sha256": _sha256(data),
        "encoding": decoded.encoding,
        "bom_hex": decoded.bom.hex(),
        "newline": _newline_style(decoded.text),
        "mode": stat.st_mode,
        "created_at": time.time(),
    }
    _atomic_json(meta_path, metadata)
    print(_ascii_json(metadata, indent=2))
    return 0


def _encode_with_bom(text: str, encoding: str, bom: bytes) -> bytes:
    encoding = _canonical_encoding(encoding)
    try:
        payload = text.encode(encoding, errors="strict")
    except UnicodeEncodeError as exc:
        raise GuardError(
            "encode_failed",
            f"Edited text cannot be represented as {encoding}",
            character=repr(exc.object[exc.start : exc.end]),
            offset=exc.start,
        ) from exc
    return bom + payload


def _cleanup_transaction(meta_path: Path, work_path: Path) -> None:
    work_path.unlink(missing_ok=True)
    meta_path.unlink(missing_ok=True)


def cmd_commit(args: argparse.Namespace) -> int:
    meta_path, expected_work_path = _transaction_paths(args.token)
    metadata = _load_json(meta_path)
    target = Path(metadata["target"])
    work_path = Path(metadata.get("work_file", expected_work_path))
    try:
        original = target.read_bytes()
    except OSError as exc:
        raise GuardError("read_failed", f"Cannot re-read target: {target}", os_error=str(exc)) from exc
    if _sha256(original) != metadata["original_sha256"]:
        raise GuardError(
            "source_changed",
            "Target changed after checkout; refusing to overwrite it",
            target=str(target),
        )
    try:
        text = work_path.read_bytes().decode("utf-8", errors="strict")
    except (OSError, UnicodeDecodeError) as exc:
        raise GuardError("work_file_invalid", "UTF-8 work file is missing or invalid") from exc
    original_style = metadata["newline"]
    current_style = _newline_style(text)
    if original_style in {"lf", "crlf", "cr"}:
        text = _normalize_newlines(text, original_style)
    elif original_style == "mixed":
        original_text = _decode_known(
            original,
            metadata["encoding"],
            bytes.fromhex(metadata.get("bom_hex", "")),
        )
        shift = _newline_distribution_shift(original_text, text)
        if current_style != "mixed" or shift > 0.20:
            raise GuardError(
                "newline_normalized",
                "Mixed line endings changed substantially; refusing to commit without an explicit conversion",
                distribution_shift=round(shift, 4),
            )
    bom = bytes.fromhex(metadata.get("bom_hex", ""))
    encoded = _encode_with_bom(text, metadata["encoding"], bom)
    temp_path: Path | None = None
    try:
        with tempfile.NamedTemporaryFile(
            mode="wb",
            prefix=f".{target.name}.encoding-guard.",
            suffix=".tmp",
            dir=target.parent,
            delete=False,
        ) as handle:
            temp_path = Path(handle.name)
            handle.write(encoded)
            handle.flush()
            os.fsync(handle.fileno())
        shutil.copystat(target, temp_path)
        os.replace(temp_path, target)
    except OSError as exc:
        if temp_path:
            temp_path.unlink(missing_ok=True)
        raise GuardError(
            "commit_failed",
            "Atomic replacement failed; original file and transaction were preserved",
            os_error=str(exc),
        ) from exc
    _cleanup_transaction(meta_path, work_path)
    print(
        _ascii_json(
            {
                "committed": str(target),
                "encoding": metadata["encoding"],
                "bom": _bom_name(bom),
                "newline": _newline_style(text),
                "sha256": _sha256(encoded),
            }
        )
    )
    return 0


def cmd_abort(args: argparse.Namespace) -> int:
    meta_path, work_path = _transaction_paths(args.token)
    if meta_path.exists():
        metadata = _load_json(meta_path)
        work_path = Path(metadata.get("work_file", work_path))
    _cleanup_transaction(meta_path, work_path)
    print(_ascii_json({"aborted": args.token}))
    return 0


def cmd_verify(args: argparse.Namespace) -> int:
    args.json = True
    return cmd_probe(args)


def cmd_policy(args: argparse.Namespace) -> int:
    policy = _load_policy()
    if args.policy_action == "list":
        print(_ascii_json(policy, indent=2))
        return 0
    if args.policy_action == "remove":
        before = len(policy["file_rules"]) + len(policy["command_rules"])
        policy["file_rules"] = [rule for rule in policy["file_rules"] if rule.get("id") != args.id]
        policy["command_rules"] = [
            rule for rule in policy["command_rules"] if rule.get("id") != args.id
        ]
        after = len(policy["file_rules"]) + len(policy["command_rules"])
        if before == after:
            raise GuardError("policy_not_found", f"Policy rule not found: {args.id}")
        _atomic_json(POLICY_PATH, policy)
        print(_ascii_json({"removed": args.id}))
        return 0
    rule_id = uuid.uuid4().hex[:12]
    if args.policy_action == "add-file":
        rule = {
            "id": rule_id,
            "root": _portable_path(Path(args.root).resolve()),
            "glob": args.glob.replace("\\", "/"),
            "encoding": _canonical_encoding(args.encoding),
        }
        policy["file_rules"].append(rule)
    elif args.policy_action == "add-command":
        rule = {
            "id": rule_id,
            "shell": args.shell,
            "executable": args.executable.casefold(),
            "stream": args.stream,
            "encoding": _canonical_encoding(args.encoding),
        }
        policy["command_rules"].append(rule)
    else:
        raise GuardError("policy_action", "Unsupported policy action")
    _atomic_json(POLICY_PATH, policy)
    print(_ascii_json({"added": rule}, indent=2))
    return 0


def _locate_powershell() -> str:
    return shutil.which("pwsh") or shutil.which("powershell") or "powershell.exe"


def _locate_git_bash() -> str:
    override = os.environ.get("GIT_BASH")
    candidates = [Path(override)] if override else []
    git = shutil.which("git")
    if git:
        git_path = Path(git).resolve()
        candidates.extend(
            [
                git_path.parent.parent / "bin" / "bash.exe",
                git_path.parent.parent / "usr" / "bin" / "bash.exe",
            ]
        )
    candidates.extend(
        [
            Path(r"C:\Program Files\Git\bin\bash.exe"),
            Path(r"D:\Program Files\Git\bin\bash.exe"),
        ]
    )
    for candidate in candidates:
        if candidate and candidate.exists():
            return str(candidate)
    raise GuardError("shell_missing", "Git Bash was requested but bash.exe was not found")


def _read_script(path: str, encoding: str = "auto") -> str:
    _, decoded = _file_decoded(Path(path), encoding)
    return decoded.text


def _strip_separator(command: list[str]) -> list[str]:
    return command[1:] if command and command[0] == "--" else command


def _windows_to_wsl(path: Path) -> str:
    resolved = str(path.resolve())
    match = re.match(r"^([A-Za-z]):[\\/](.*)$", resolved)
    if not match:
        raise GuardError("path_unsupported", "WSL script path must be on a Windows drive")
    tail = match.group(2).replace("\\", "/")
    return f"/mnt/{match.group(1).lower()}/{tail}"


def _cmd_inline_script(script: str) -> str:
    script = re.sub(r"\^(?:\r\n|\r|\n)", "", script)
    commands: list[str] = []
    for line in script.replace("\r\n", "\n").replace("\r", "\n").split("\n"):
        stripped = line.strip()
        if not stripped or stripped.startswith("::") or re.match(r"(?i)^rem(?:\s|$)", stripped):
            continue
        commands.append(line)
    return " & ".join(commands)


def _materialize_bash_script(script: str) -> Path:
    script_dir = STATE_HOME / "scripts"
    script_dir.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(
        mode="wb",
        prefix="encoding-guard-",
        suffix=".sh",
        dir=script_dir,
        delete=False,
    ) as handle:
        path = Path(handle.name)
        normalized = script.replace("\r\n", "\n").replace("\r", "\n")
        handle.write(normalized.encode("utf-8"))
        handle.flush()
        os.fsync(handle.fileno())
    return path


def _process_spec(
    args: argparse.Namespace,
) -> tuple[list[str], str, str, bytes | None, dict[str, str], Path | None]:
    shell = args.shell or "none"
    command = _strip_separator(list(args.command or []))
    env = os.environ.copy()
    env.setdefault("PYTHONUTF8", "1")
    env.setdefault("PYTHONIOENCODING", "utf-8")
    if shell in {"git-bash", "wsl"} or os.name != "nt":
        env.setdefault("LANG", "C.UTF-8")
        env.setdefault("LC_ALL", "C.UTF-8")
    stdin_data = Path(args.stdin_file).read_bytes() if args.stdin_file else None
    if shell == "none":
        if args.script_file or not command:
            raise GuardError("command_invalid", "Direct mode requires an executable after --")
        return command, shell, command[0], stdin_data, env, None
    if command:
        raise GuardError("command_invalid", "Shell mode uses --script-file, not trailing arguments")
    if not args.script_file:
        raise GuardError("command_invalid", "Shell mode requires --script-file")
    script = _read_script(args.script_file, args.script_encoding)
    if shell == "powershell":
        prefix = (
            "try { [Console]::InputEncoding=[Text.UTF8Encoding]::new($false); "
            "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); "
            "$OutputEncoding=[Text.UTF8Encoding]::new($false) } catch {}\n"
        )
        encoded = base64.b64encode((prefix + script).encode("utf-16-le")).decode("ascii")
        executable = _locate_powershell()
        return [executable, "-NoLogo", "-NoProfile", "-EncodedCommand", encoded], shell, executable, stdin_data, env, None
    if shell == "cmd":
        script = _cmd_inline_script(script)
        if len(script) > 7000:
            raise GuardError("command_too_long", "cmd script exceeds the safe Unicode command-line limit")
        executable = os.environ.get("COMSPEC", "cmd.exe")
        env["ENCODING_GUARD_CMD_SCRIPT"] = script
        return [executable, "/d", "/s", "/c", "%ENCODING_GUARD_CMD_SCRIPT%"], shell, executable, stdin_data, env, None
    if shell == "git-bash":
        executable = _locate_git_bash()
        materialized = _materialize_bash_script(script)
        return [executable, str(materialized)], shell, executable, stdin_data, env, materialized
    if shell == "wsl":
        materialized = _materialize_bash_script(script)
        if os.name == "nt":
            executable = shutil.which("wsl") or "wsl.exe"
            return [executable, "--", "bash", _windows_to_wsl(materialized)], shell, executable, stdin_data, env, materialized
        executable = shutil.which("bash") or "/bin/bash"
        return [executable, str(materialized)], shell, executable, stdin_data, env, materialized
    raise GuardError("shell_invalid", f"Unsupported shell: {shell}")


def _save_failure(stream: str, data: bytes) -> str:
    FAILURE_DIR.mkdir(parents=True, exist_ok=True)
    path = FAILURE_DIR / f"{uuid.uuid4().hex}.{stream}.bin"
    path.write_bytes(data)
    return str(path)


def _decode_command_output(
    data: bytes,
    *,
    explicit: str,
    shell: str,
    executable: str,
    stream: str,
) -> Decoded:
    if not data:
        return Decoded("", "utf-8", b"", "empty")
    policy = None if explicit != "auto" else _command_policy(shell, executable, stream)
    return decode_bytes(data, explicit=explicit, policy=policy)


def _emit_captured(
    stdout: bytes,
    stderr: bytes,
    *,
    stdout_encoding: str,
    stderr_encoding: str,
    shell: str,
    executable: str,
) -> None:
    try:
        decoded_stdout = _decode_command_output(
            stdout,
            explicit=stdout_encoding,
            shell=shell,
            executable=executable,
            stream="stdout",
        )
        decoded_stderr = _decode_command_output(
            stderr,
            explicit=stderr_encoding,
            shell=shell,
            executable=executable,
            stream="stderr",
        )
    except GuardError as exc:
        raw = {}
        if stdout:
            raw["stdout"] = _save_failure("stdout", stdout)
        if stderr:
            raw["stderr"] = _save_failure("stderr", stderr)
        exc.details["raw_output"] = raw
        raise
    _write_utf8("stdout", decoded_stdout.text)
    _write_utf8("stderr", decoded_stderr.text)


def cmd_run(args: argparse.Namespace) -> int:
    command, shell, executable, stdin_data, env, materialized = _process_spec(args)
    try:
        try:
            result = subprocess.run(
                command,
                cwd=args.cwd,
                env=env,
                input=stdin_data,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                timeout=args.timeout,
                check=False,
                creationflags=WINDOWS_NO_WINDOW,
            )
        except subprocess.TimeoutExpired as exc:
            raise GuardError("command_timeout", "Command timed out", timeout=args.timeout) from exc
        except OSError as exc:
            raise GuardError("command_start_failed", "Command could not start", os_error=str(exc)) from exc
    finally:
        if materialized:
            materialized.unlink(missing_ok=True)
    _emit_captured(
        result.stdout,
        result.stderr,
        stdout_encoding=args.stdout_encoding,
        stderr_encoding=args.stderr_encoding,
        shell=shell,
        executable=executable,
    )
    return int(result.returncode)


def _job_paths(job_id: str) -> dict[str, Path]:
    if not re.fullmatch(r"[0-9a-f]{32}", job_id):
        raise GuardError("invalid_job", "Invalid job id")
    root = JOB_DIR / job_id
    return {
        "root": root,
        "spec": root / "spec.json",
        "status": root / "status.json",
        "cursor": root / "cursor.json",
        "stdout": root / "stdout.bin",
        "stderr": root / "stderr.bin",
        "stop": root / "stop",
    }


def _job_spec(args: argparse.Namespace) -> dict[str, Any]:
    return {
        "cwd": args.cwd,
        "shell": args.shell,
        "script_file": args.script_file,
        "script_encoding": args.script_encoding,
        "stdin_file": args.stdin_file,
        "stdout_encoding": args.stdout_encoding,
        "stderr_encoding": args.stderr_encoding,
        "timeout": args.timeout,
        "command": _strip_separator(list(args.command or [])),
    }


def cmd_start(args: argparse.Namespace) -> int:
    # Validate before detaching so obvious mistakes fail synchronously.
    *_, materialized = _process_spec(args)
    if materialized:
        materialized.unlink(missing_ok=True)
    job_id = uuid.uuid4().hex
    paths = _job_paths(job_id)
    paths["root"].mkdir(parents=True)
    _atomic_json(paths["spec"], _job_spec(args))
    _atomic_json(paths["status"], {"state": "queued", "created_at": time.time()})
    _atomic_json(paths["cursor"], {"stdout_chars": 0, "stderr_chars": 0})
    worker = [sys.executable, str(Path(__file__).resolve()), "_worker", job_id]
    kwargs: dict[str, Any] = {
        "stdin": subprocess.DEVNULL,
        "stdout": subprocess.DEVNULL,
        "stderr": subprocess.DEVNULL,
        "close_fds": True,
    }
    if os.name == "nt":
        kwargs["creationflags"] = (
            subprocess.CREATE_NEW_PROCESS_GROUP | subprocess.DETACHED_PROCESS | WINDOWS_NO_WINDOW
        )
    else:
        kwargs["start_new_session"] = True
    subprocess.Popen(worker, **kwargs)
    print(_ascii_json({"job_id": job_id, "status": "queued"}))
    return 0


def _terminate_process(process: subprocess.Popen[bytes]) -> None:
    if process.poll() is not None:
        return
    if os.name == "nt":
        subprocess.run(
            ["taskkill", "/PID", str(process.pid), "/T", "/F"],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            check=False,
            creationflags=WINDOWS_NO_WINDOW,
        )
    else:
        try:
            os.killpg(process.pid, signal.SIGTERM)
        except (ProcessLookupError, PermissionError):
            process.terminate()


def cmd_worker(args: argparse.Namespace) -> int:
    paths = _job_paths(args.job_id)
    spec = _load_json(paths["spec"])
    namespace = SimpleNamespace(**spec)
    command, shell, executable, stdin_data, env, materialized = _process_spec(namespace)
    start = time.time()
    try:
        with paths["stdout"].open("wb") as stdout_handle, paths["stderr"].open("wb") as stderr_handle:
            try:
                process = subprocess.Popen(
                    command,
                    cwd=namespace.cwd,
                    env=env,
                    stdin=subprocess.PIPE if stdin_data is not None else subprocess.DEVNULL,
                    stdout=stdout_handle,
                    stderr=stderr_handle,
                    start_new_session=os.name != "nt",
                    creationflags=WINDOWS_NO_WINDOW,
                )
            except OSError as exc:
                _atomic_json(
                    paths["status"],
                    {"state": "failed", "error": str(exc), "finished_at": time.time()},
                )
                return 1
            if stdin_data is not None and process.stdin:
                process.stdin.write(stdin_data)
                process.stdin.close()
            _atomic_json(
                paths["status"],
                {
                    "state": "running",
                    "pid": process.pid,
                    "shell": shell,
                    "executable": executable,
                    "started_at": start,
                },
            )
            state = "finished"
            while process.poll() is None:
                if paths["stop"].exists():
                    state = "stopped"
                    _terminate_process(process)
                    break
                if namespace.timeout is not None and time.time() - start > namespace.timeout:
                    state = "timed_out"
                    _terminate_process(process)
                    break
                time.sleep(0.2)
            return_code = process.wait()
    finally:
        if materialized:
            materialized.unlink(missing_ok=True)
    _atomic_json(
        paths["status"],
        {
            "state": state,
            "pid": process.pid,
            "shell": shell,
            "executable": executable,
            "exit_code": return_code,
            "finished_at": time.time(),
        },
    )
    return 0


def _decode_partial(
    data: bytes,
    *,
    explicit: str,
    policy: tuple[str, str] | None,
    finished: bool,
) -> Decoded | None:
    chosen = _canonical_encoding(explicit)
    if chosen == "auto" and policy:
        chosen = policy[0]
    bom_info = _detect_bom(data)
    bom = bom_info[0] if bom_info else b""
    if bom_info:
        if chosen == "auto":
            chosen = bom_info[1]
        elif not _compatible_encoding(chosen, bom_info[1]):
            raise GuardError(
                "encoding_conflict",
                "Explicit or policy encoding conflicts with the output BOM",
                selected=chosen,
                bom=bom_info[1],
            )
        elif _encoding_family(chosen) in {"utf-16", "utf-32"}:
            chosen = bom_info[1]
    if chosen == "auto":
        structural = _structural_unicode(data)
        if structural:
            chosen = structural
    if chosen != "auto":
        payload = data[len(bom) :] if bom else data
        decoder = codecs.getincrementaldecoder(chosen)(errors="strict")
        try:
            text = decoder.decode(payload, final=finished)
            if _has_excess_controls(text):
                if not finished:
                    return None
                raise GuardError("binary_input", "Decoded job output contains too many control characters")
            return Decoded(text, chosen, bom, "partial")
        except UnicodeDecodeError:
            if not finished:
                return None
            raise GuardError("decode_failed", f"Job output is not valid {chosen}")
    try:
        decoder = codecs.getincrementaldecoder("utf-8")(errors="strict")
        text = decoder.decode(data, final=finished)
        if _has_excess_controls(text):
            if not finished:
                return None
            raise GuardError("binary_input", "Decoded job output contains too many control characters")
        return Decoded(text, "utf-8", b"", "partial-utf8")
    except UnicodeDecodeError:
        if not finished:
            return None
    return decode_bytes(data)


def _remove_tree(path: Path) -> None:
    shutil.rmtree(path, ignore_errors=True)


def cmd_poll(args: argparse.Namespace) -> int:
    paths = _job_paths(args.job_id)
    spec = _load_json(paths["spec"])
    status = _load_json(paths["status"])
    cursor = _load_json(paths["cursor"], {"stdout_chars": 0, "stderr_chars": 0})
    finished = status.get("state") in {"finished", "failed", "stopped", "timed_out"}
    shell = spec.get("shell") or "none"
    executable = status.get("executable") or (
        spec.get("command", ["unknown"])[0] if spec.get("command") else shell
    )
    outputs: dict[str, Decoded | None] = {}
    for stream in ("stdout", "stderr"):
        data = paths[stream].read_bytes() if paths[stream].exists() else b""
        policy = _command_policy(shell, executable, stream)
        try:
            outputs[stream] = _decode_partial(
                data,
                explicit=spec.get(f"{stream}_encoding", "auto"),
                policy=policy,
                finished=finished,
            )
        except GuardError as exc:
            exc.details["raw_output"] = {stream: str(paths[stream])}
            raise
    delivered = dict(cursor)
    for stream in ("stdout", "stderr"):
        decoded = outputs[stream]
        if decoded is None:
            continue
        start = int(cursor.get(f"{stream}_chars", 0))
        chunk = decoded.text[start:]
        _write_utf8(stream, chunk)
        delivered[f"{stream}_chars"] = len(decoded.text)
    _atomic_json(paths["cursor"], delivered)
    if not finished:
        if any(value is None for value in outputs.values()):
            print(_ascii_json({"job_id": args.job_id, "status": status.get("state"), "output": "waiting-for-encoding"}))
        return 0
    exit_code = int(status.get("exit_code", 1 if status.get("state") == "failed" else 0))
    _write_utf8(
        "stderr",
        f"\n[encoding-guard job={args.job_id} state={status.get('state')} exit={exit_code}]\n",
    )
    _remove_tree(paths["root"])
    return exit_code


def cmd_stop(args: argparse.Namespace) -> int:
    paths = _job_paths(args.job_id)
    if not paths["root"].exists():
        raise GuardError("job_missing", f"Job not found: {args.job_id}")
    paths["stop"].touch()
    print(_ascii_json({"job_id": args.job_id, "stop_requested": True}))
    return 0


def _add_file_options(parser: argparse.ArgumentParser, *, json_flag: bool = False) -> None:
    parser.add_argument("path")
    parser.add_argument("--encoding", default="auto")
    if json_flag:
        parser.add_argument("--json", action="store_true")


def _add_run_options(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--cwd", default=None)
    parser.add_argument("--shell", choices=["powershell", "cmd", "git-bash", "wsl"], default=None)
    parser.add_argument("--script-file")
    parser.add_argument("--script-encoding", default="auto")
    parser.add_argument("--stdin-file")
    parser.add_argument("--stdout-encoding", default="auto")
    parser.add_argument("--stderr-encoding", default="auto")
    parser.add_argument("--timeout", type=float, default=None)
    parser.add_argument("command", nargs=argparse.REMAINDER)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="codex-text", description=__doc__)
    sub = parser.add_subparsers(
        dest="action",
        required=True,
        metavar="{probe,read,checkout,commit,abort,verify,run,start,poll,stop,policy}",
    )

    probe = sub.add_parser("probe")
    _add_file_options(probe, json_flag=True)
    probe.set_defaults(func=cmd_probe)

    read = sub.add_parser("read")
    _add_file_options(read, json_flag=True)
    read.add_argument("--start-line", type=int)
    read.add_argument("--max-lines", type=int)
    read.set_defaults(func=cmd_read)

    checkout = sub.add_parser("checkout")
    _add_file_options(checkout)
    checkout.set_defaults(func=cmd_checkout)

    commit = sub.add_parser("commit")
    commit.add_argument("token")
    commit.set_defaults(func=cmd_commit)

    abort = sub.add_parser("abort")
    abort.add_argument("token")
    abort.set_defaults(func=cmd_abort)

    verify = sub.add_parser("verify")
    _add_file_options(verify)
    verify.set_defaults(func=cmd_verify)

    run = sub.add_parser("run")
    _add_run_options(run)
    run.set_defaults(func=cmd_run)

    start = sub.add_parser("start")
    _add_run_options(start)
    start.set_defaults(func=cmd_start)

    poll = sub.add_parser("poll")
    poll.add_argument("job_id")
    poll.set_defaults(func=cmd_poll)

    stop = sub.add_parser("stop")
    stop.add_argument("job_id")
    stop.set_defaults(func=cmd_stop)

    policy = sub.add_parser("policy")
    policy_sub = policy.add_subparsers(dest="policy_action", required=True)
    add_file = policy_sub.add_parser("add-file")
    add_file.add_argument("--root", required=True)
    add_file.add_argument("--glob", required=True)
    add_file.add_argument("--encoding", required=True)
    add_command = policy_sub.add_parser("add-command")
    add_command.add_argument("--shell", required=True, choices=["*", "none", "powershell", "cmd", "git-bash", "wsl"])
    add_command.add_argument("--executable", required=True)
    add_command.add_argument("--stream", required=True, choices=["stdout", "stderr"])
    add_command.add_argument("--encoding", required=True)
    policy_sub.add_parser("list")
    remove = policy_sub.add_parser("remove")
    remove.add_argument("id")
    for child in (add_file, add_command, policy_sub.choices["list"], remove):
        child.set_defaults(func=cmd_policy)

    worker = sub.add_parser("_worker")
    worker.add_argument("job_id")
    worker.set_defaults(func=cmd_worker)
    return parser


def main(argv: list[str] | None = None) -> int:
    try:
        args = build_parser().parse_args(argv)
        return int(args.func(args))
    except GuardError as exc:
        diagnostic = {"error": exc.code, "message": exc.message, **exc.details}
        sys.stderr.write(_ascii_json(diagnostic, indent=2) + "\n")
        return 125
    except KeyboardInterrupt:
        sys.stderr.write(_ascii_json({"error": "interrupted", "message": "Operation interrupted"}) + "\n")
        return 130


if __name__ == "__main__":
    raise SystemExit(main())
