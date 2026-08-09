# Runtime and shell execution

## Runtime

- The installer writes the resolved `codex` environment interpreter to the device-local `scripts/codex-text.runtime` pointer. Launchers prefer `CODEX_TEXT_PYTHON`, then this pointer, then a conventional user-level conda path.
- Legacy detection requires `charset-normalizer==3.4.8` in conda env `codex`.
- After launcher or dependency changes, run `scripts/self_test.py` with env `codex`, followed by `python -m pip check`.

## Commands

- Prefer direct process execution with `run`.
- Use `--shell powershell|cmd|git-bash|wsl --script-file` only for syntax that requires a shell.
- Never pipe Unicode stdin through the outer shell; use `--stdin-file`.
- Preserve the child exit code. Do not retry failed decoding with guessed encodings.

Add explicit `--stdout-encoding` or `--stderr-encoding` only after confirmation. For repeatable output use a narrow `policy add-command` rule keyed by shell, executable basename, stream, and encoding.

The current machine policy intentionally decodes direct `MSBuild.exe` stdout/stderr as GB18030 and direct CMake/CTest output as configured in `%USERPROFILE%\.codex\encoding-guard\policies.json`. Repository compiler `/utf-8` flags do not change console output encoding.
