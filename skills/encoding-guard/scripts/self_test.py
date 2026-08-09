#!/usr/bin/env python3
"""Runnable regression check for Encoding Guard."""

from __future__ import annotations

import json
import os
import stat
import subprocess
import sys
import tempfile
import time
from pathlib import Path

import encoding_guard as guard


WINDOWS_NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0) if os.name == "nt" else 0


def run_cli(tool: Path, env: dict[str, str], *args: str) -> subprocess.CompletedProcess[bytes]:
    return subprocess.run(
        [sys.executable, str(tool), *args],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        env=env,
        check=False,
        creationflags=WINDOWS_NO_WINDOW,
    )


def cli_json(result: subprocess.CompletedProcess[bytes]) -> dict[str, object]:
    assert result.returncode == 0, result.stderr
    return json.loads(result.stdout.decode("ascii"))


def error_code(result: subprocess.CompletedProcess[bytes]) -> str:
    assert result.returncode == 125, (result.returncode, result.stdout, result.stderr)
    return str(json.loads(result.stderr.decode("ascii"))["error"])


def main() -> int:
    tool = Path(guard.__file__).resolve()
    with tempfile.TemporaryDirectory(prefix="encoding-guard-test-") as temporary:
        root = Path(temporary)
        state = root / "state"
        home = root / "home"
        env = os.environ.copy()
        env["ENCODING_GUARD_STATE"] = str(state)
        env["ENCODING_GUARD_HOME"] = str(home)
        env["PYTHONUTF8"] = "1"
        env["PYTHONIOENCODING"] = "utf-8"

        guard.STATE_HOME = state
        guard.TRANSACTION_DIR = state / "transactions"
        guard.JOB_DIR = state / "jobs"
        guard.FAILURE_DIR = state / "failures"
        guard.POLICY_HOME = home
        guard.POLICY_PATH = home / "policies.json"

        text = "中文 Encoding Guard — line 1\r\nline 2\r\n"
        fixtures: dict[str, tuple[str, bytes]] = {
            "ascii.txt": ("ASCII line 1\nline 2\n", b"ASCII line 1\nline 2\n"),
            "utf8.txt": (text, text.encode("utf-8")),
            "utf8-bom.txt": (text, guard.UTF8_BOM + text.encode("utf-8")),
            "utf16le.txt": (text, guard.codecs.BOM_UTF16_LE + text.encode("utf-16-le")),
            "utf16be.txt": (text, guard.codecs.BOM_UTF16_BE + text.encode("utf-16-be")),
            "utf32le.txt": (text, guard.codecs.BOM_UTF32_LE + text.encode("utf-32-le")),
            "utf32be.txt": (text, guard.codecs.BOM_UTF32_BE + text.encode("utf-32-be")),
            "gb18030.txt": (text, text.encode("gb18030")),
            "big5.txt": ("繁體中文編碼測試\r\n第二行\r\n", "繁體中文編碼測試\r\n第二行\r\n".encode("big5")),
            "shift-jis.txt": ("日本語の文字コード確認\r\n二行目\r\n", "日本語の文字コード確認\r\n二行目\r\n".encode("cp932")),
        }
        unicode_root = root / "中文路径"
        unicode_root.mkdir()
        for name, (_, payload) in fixtures.items():
            (unicode_root / name).write_bytes(payload)

        for name, expected_encoding in (
            ("ascii.txt", "utf-8"),
            ("utf8.txt", "utf-8"),
            ("utf8-bom.txt", "utf-8"),
            ("utf16le.txt", "utf-16-le"),
            ("utf16be.txt", "utf-16-be"),
            ("utf32le.txt", "utf-32-le"),
            ("utf32be.txt", "utf-32-be"),
        ):
            data, decoded = guard._file_decoded(unicode_root / name, "auto")
            assert decoded.encoding == expected_encoding, (name, decoded.encoding)
            assert decoded.text == fixtures[name][0]
            expected_newline = "lf" if name == "ascii.txt" else "crlf"
            assert guard._metadata(unicode_root / name, data, decoded)["newline"] == expected_newline

        for name, encoding in (
            ("gb18030.txt", "gb18030"),
            ("big5.txt", "big5"),
            ("shift-jis.txt", "cp932"),
        ):
            _, decoded = guard._file_decoded(unicode_root / name, encoding)
            assert decoded.encoding == guard._canonical_encoding(encoding)
            assert decoded.text == fixtures[name][0]

        sliced = run_cli(
            tool,
            env,
            "read",
            str(unicode_root / "utf8.txt"),
            "--start-line",
            "2",
            "--max-lines",
            "1",
        )
        assert sliced.returncode == 0 and sliced.stdout.decode("utf-8") == "line 2\r\n"
        assert error_code(run_cli(tool, env, "read", str(unicode_root / "utf8.txt"), "--max-lines", "-1")) == "invalid_range"

        fake_declaration = root / "fake-declaration.cmd"
        fake_declaration.write_bytes(b'set "PYTHONIOENCODING=utf-8"\r\n')
        _, fake_decoded = guard._file_decoded(fake_declaration, "auto")
        assert fake_decoded.source == "strict-utf8"
        conflict = root / "conflict.py"
        conflict.write_bytes(guard.UTF8_BOM + b"# coding: cp1252\n")
        assert error_code(run_cli(tool, env, "probe", str(conflict), "--json")) == "encoding_conflict"

        try:
            guard._file_decoded(unicode_root / "gb18030.txt", "auto")
        except guard.GuardError as exc:
            assert exc.code == "ambiguous_encoding"
        else:
            raise AssertionError("short legacy text must not be guessed")

        added = cli_json(
            run_cli(
                tool,
                env,
                "policy",
                "add-file",
                "--root",
                str(unicode_root),
                "--glob",
                "*.txt",
                "--encoding",
                "gb18030",
            )
        )
        rule_id = str(dict(added["added"])["id"])
        _, policy_decoded = guard._file_decoded(unicode_root / "gb18030.txt", "auto")
        assert policy_decoded.text == text
        assert cli_json(run_cli(tool, env, "policy", "remove", rule_id))["removed"] == rule_id

        direct = unicode_root / "direct.txt"
        direct.write_text("直接编辑后验证\n", encoding="utf-8", newline="")
        verified = cli_json(run_cli(tool, env, "verify", str(direct)))
        assert verified["encoding"] == "utf-8" and verified["bom"] is None

        transactional = (
            ("utf8-bom.txt", "auto", guard.UTF8_BOM),
            ("utf16le.txt", "auto", guard.codecs.BOM_UTF16_LE),
            ("utf32be.txt", "auto", guard.codecs.BOM_UTF32_BE),
            ("gb18030.txt", "gb18030", b""),
            ("big5.txt", "big5", b""),
            ("shift-jis.txt", "cp932", b""),
        )
        for name, encoding, expected_bom in transactional:
            target = unicode_root / name
            before_mode = stat.S_IMODE(target.stat().st_mode)
            checkout_args = ["checkout", str(target)]
            if encoding != "auto":
                checkout_args.extend(["--encoding", encoding])
            transaction = cli_json(run_cli(tool, env, *checkout_args))
            work_file = Path(str(transaction["work_file"]))
            before_text = work_file.read_text(encoding="utf-8")
            after_text = before_text.replace("line 2", "修改 line 2").replace("第二行", "修改行").replace("二行目", "変更行")
            work_file.write_text(after_text, encoding="utf-8", newline="")
            committed = cli_json(run_cli(tool, env, "commit", str(transaction["token"])))
            assert committed["newline"] == "crlf"
            payload = target.read_bytes()
            assert payload.startswith(expected_bom)
            _, decoded = guard._file_decoded(target, encoding)
            expected_text = guard._normalize_newlines(after_text, "crlf")
            assert decoded.text == expected_text, (name, repr(decoded.text), repr(expected_text))
            assert stat.S_IMODE(target.stat().st_mode) == before_mode
            assert not work_file.exists()

        mixed = root / "mixed.txt"
        mixed.write_bytes(("\r\n".join(f"crlf-{index}" for index in range(50)) + "\r\n" + "\n".join(f"lf-{index}" for index in range(50)) + "\n").encode("utf-8"))
        transaction = cli_json(run_cli(tool, env, "checkout", str(mixed)))
        work_file = Path(str(transaction["work_file"]))
        mixed_text = work_file.read_text(encoding="utf-8").replace("\r\n", "\n")
        mixed_text = mixed_text.replace("\n", "\r\n", 1)
        work_file.write_text(mixed_text, encoding="utf-8", newline="")
        assert error_code(run_cli(tool, env, "commit", str(transaction["token"]))) == "newline_normalized"
        run_cli(tool, env, "abort", str(transaction["token"]))

        source_changed = root / "source-changed.txt"
        source_changed.write_text("before", encoding="utf-8")
        transaction = cli_json(run_cli(tool, env, "checkout", str(source_changed)))
        source_changed.write_text("external", encoding="utf-8")
        assert error_code(run_cli(tool, env, "commit", str(transaction["token"]))) == "source_changed"
        run_cli(tool, env, "abort", str(transaction["token"]))

        if os.name == "nt":
            locked = root / "locked.txt"
            locked.write_text("locked\n", encoding="utf-8", newline="")
            transaction = cli_json(run_cli(tool, env, "checkout", str(locked)))
            handle = guard.ctypes.windll.kernel32.CreateFileW(
                str(locked),
                0x80000000,
                0x00000001,
                None,
                3,
                0x00000080,
                None,
            )
            assert handle not in {0, -1}
            try:
                assert error_code(run_cli(tool, env, "commit", str(transaction["token"]))) == "commit_failed"
                assert Path(str(transaction["work_file"])).exists()
            finally:
                guard.ctypes.windll.kernel32.CloseHandle(handle)
            assert run_cli(tool, env, "commit", str(transaction["token"])).returncode == 0

        stdout_text = "标准输出中文"
        stderr_text = "错误输出中文"
        stdout_hex = stdout_text.encode("gbk").hex()
        stderr_hex = stderr_text.encode("utf-16-le").hex()
        emitted = run_cli(
            tool,
            env,
            "run",
            "--stdout-encoding",
            "gbk",
            "--stderr-encoding",
            "utf-16-le",
            "--",
            sys.executable,
            "-c",
            f"import sys;sys.stdout.buffer.write(bytes.fromhex('{stdout_hex}'));sys.stderr.buffer.write(bytes.fromhex('{stderr_hex}'));raise SystemExit(9)",
        )
        assert emitted.returncode == 9
        assert emitted.stdout.decode("utf-8") == stdout_text
        assert emitted.stderr.decode("utf-8") == stderr_text

        stdin_file = root / "标准输入.txt"
        stdin_payload = "Unicode stdin 输入\n".encode("utf-8")
        stdin_file.write_bytes(stdin_payload)
        echoed = run_cli(
            tool,
            env,
            "run",
            "--stdin-file",
            str(stdin_file),
            "--",
            sys.executable,
            "-c",
            "import sys;sys.stdout.buffer.write(sys.stdin.buffer.read())",
        )
        assert echoed.returncode == 0 and echoed.stdout == stdin_payload

        command_rule = cli_json(
            run_cli(
                tool,
                env,
                "policy",
                "add-command",
                "--shell",
                "none",
                "--executable",
                Path(sys.executable).name,
                "--stream",
                "stdout",
                "--encoding",
                "gbk",
            )
        )
        rule_id = str(dict(command_rule["added"])["id"])
        policy_output = run_cli(
            tool,
            env,
            "run",
            "--",
            sys.executable,
            "-c",
            f"import sys;sys.stdout.buffer.write(bytes.fromhex('{stdout_hex}'))",
        )
        assert policy_output.returncode == 0 and policy_output.stdout.decode("utf-8") == stdout_text
        run_cli(tool, env, "policy", "remove", rule_id)

        if os.name == "nt":
            launcher = tool.with_name("codex-text.exe")
            launcher_argument = 'meta | & ( ) "quoted" trailing\\'
            launched = subprocess.run(
                [
                    str(launcher),
                    "run",
                    "--",
                    sys.executable,
                    "-c",
                    "import sys;print(sys.argv[1])",
                    launcher_argument,
                ],
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                env=env,
                check=False,
                creationflags=WINDOWS_NO_WINDOW,
            )
            assert launched.returncode == 0, launched.stderr
            assert launched.stdout.decode("utf-8").rstrip("\r\n") == launcher_argument

            cmd_script = root / "cp936-script.cmd"
            cmd_script.write_bytes(
                (
                    '@echo off\r\nset "MSG=脚本中文"\r\n'
                    f'"{sys.executable}" -c "import os;print(os.environ[\'MSG\'])"\r\n'
                ).encode("gbk")
            )
            script_result = run_cli(
                tool,
                env,
                "run",
                "--shell",
                "cmd",
                "--script-file",
                str(cmd_script),
                "--script-encoding",
                "gbk",
                "--stdout-encoding",
                "utf-8",
            )
            assert script_result.returncode == 0, script_result.stderr
            script_stdout = script_result.stdout.decode("utf-8").replace("\r\n", "\n")
            assert script_stdout == "脚本中文\n", repr(script_stdout)

        background_text = "开始—结束\n"
        background_hex = background_text.encode("utf-8").hex()
        background = cli_json(
            run_cli(
                tool,
                env,
                "start",
                "--stdout-encoding",
                "utf-8",
                "--",
                sys.executable,
                "-c",
                f"import sys,time;b=bytes.fromhex('{background_hex}');[(sys.stdout.buffer.write(bytes([x])),sys.stdout.flush(),time.sleep(.02)) for x in b]",
            )
        )
        job_id = str(background["job_id"])
        collected = b""
        for _ in range(100):
            polled = run_cli(tool, env, "poll", job_id)
            assert polled.returncode in {0}, (polled.returncode, polled.stdout, polled.stderr)
            collected += polled.stdout
            collected.decode("utf-8", errors="strict")
            if b"encoding-guard job=" in polled.stderr:
                assert polled.returncode == 0
                break
            time.sleep(0.03)
        else:
            status_path = guard.JOB_DIR / job_id / "status.json"
            status = status_path.read_text(encoding="ascii") if status_path.exists() else "missing"
            raise AssertionError(f"background job did not finish: {status}")
        assert collected.decode("utf-8") == background_text
        assert not (guard.JOB_DIR / job_id).exists()

        stoppable = cli_json(
            run_cli(
                tool,
                env,
                "start",
                "--",
                sys.executable,
                "-c",
                "import sys,time;print('ready');sys.stdout.flush();time.sleep(30)",
            )
        )
        stop_job = str(stoppable["job_id"])
        for _ in range(50):
            polled = run_cli(tool, env, "poll", stop_job)
            if b"ready" in polled.stdout:
                break
            time.sleep(0.05)
        else:
            raise AssertionError("stoppable job did not start")
        assert cli_json(run_cli(tool, env, "stop", stop_job))["stop_requested"] is True
        for _ in range(100):
            polled = run_cli(tool, env, "poll", stop_job)
            if b"state=stopped" in polled.stderr:
                break
            time.sleep(0.05)
        else:
            raise AssertionError("background job did not stop")
        assert not (guard.JOB_DIR / stop_job).exists()

        binary = root / "binary.bin"
        binary.write_bytes(bytes(range(32)) * 4)
        assert error_code(run_cli(tool, env, "probe", str(binary), "--json")) == "binary_input"
        invalid = root / "invalid.bin"
        invalid.write_bytes(b"\x81\x8d\x8f\x90\x9d" * 10)
        assert error_code(run_cli(tool, env, "probe", str(invalid), "--json")) in {
            "ambiguous_encoding",
            "binary_input",
        }

        policies = cli_json(run_cli(tool, env, "policy", "list"))
        assert policies["file_rules"] == [] and policies["command_rules"] == []

    print("encoding-guard self-test: PASS")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
