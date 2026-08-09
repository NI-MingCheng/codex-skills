---
name: encoding-guard
description: Diagnose and safely handle unknown or legacy text encodings, exact BOM/EOL preservation, mojibake, and Unicode transport across Windows PowerShell, cmd, Git Bash, or WSL. Use only when encoding risk is real; do not use for ordinary UTF-8 repository files, apply_patch, rg, Git, builds, tests, or normal commands.
---

# Encoding Guard

Use the native fast path for normal UTF-8 work. Route only encoding-sensitive operations through `scripts/encoding_guard.py`.

## Native fast path

- Use native `rg`, Git, build tools, tests, and ordinary shell commands when no encoding boundary is involved.
- Read repository files natively when `.gitattributes`, `.editorconfig`, repository validation, or a previous probe establishes UTF-8.
- Edit verified UTF-8 no-BOM files with `apply_patch`; do not wrap it in Encoding Guard.
- If native output is readable and correct, do not repeat the command through `codex-text`.
- On Windows, launch non-interactive guarded commands and tests with `CREATE_NO_WINDOW`. If PowerShell must use `Start-Process`, include `-WindowStyle Hidden` unless the user explicitly requests a visible interactive program.

## Select the launcher

- PowerShell or cmd: `scripts\codex-text.exe`
- Git Bash on Windows: `scripts/codex-text.exe`
- Native WSL: `scripts/codex-text`

Resolve the launcher relative to this skill. If the Windows launcher reports `launcher_missing`, bootstrap only with `conda run -n codex python scripts/encoding_guard.py`, repair the launcher, and run `scripts/self_test.py`. Do not bypass the guard for encoding-sensitive task text.

## Read and edit

1. Run `probe PATH --json` when encoding is unknown, legacy encoding is plausible, or exact BOM/EOL preservation matters.
2. Run `read PATH`, optionally with bounded line arguments, only when native reading is unsafe or already produced mojibake.
3. Stop on `ambiguous_encoding`; ask for the correct candidate and record it with `policy add-file`.
4. Edit verified UTF-8 no-BOM files directly. Run `verify PATH` when the task is encoding-sensitive or repository validation does not already cover the file.
5. For every other encoding or BOM, use `checkout PATH`, edit only the returned UTF-8 work file, then `commit TOKEN`; use `abort TOKEN` to discard it.

Read [legacy-encoding.md](references/legacy-encoding.md) before diagnosing ambiguity, adding file policies, or editing legacy text.

## Run commands

Run commands natively by default. Use `run --cwd DIR -- EXECUTABLE ARG...` only when Unicode stdin/stdout crosses incompatible shells, native output is corrupted, or exact stream decoding is required. When guarded shell syntax is required, create a UTF-8 script file and use `--shell ... --script-file SCRIPT`. Pass Unicode stdin with `--stdin-file`; use `start`, `poll`, and `stop` only for guarded long-running commands.

Read [runtime-and-shells.md](references/runtime-and-shells.md) before using shell-specific execution, changing command policies, or handling MSBuild/CMake/CTest output.

## Repository text policy

Read [repository-text-policy.md](references/repository-text-policy.md) when initializing a first-party C/C++ repository, adding `.gitattributes` or `.editorconfig`, diagnosing EOL-only diffs, or preparing staged text checks.

Treat PDF, DOCX, images, archives, and executables as binary. Never change system locale, terminal choice, PowerShell profiles, or code pages.
