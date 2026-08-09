---
name: python-env
description: Run Python scripts and tests, inspect packages, and install or repair dependencies only in the conda environment named codex. Use for every Python command, test, package operation, notebook helper, or Python-based tool invocation on this machine.
---

# Python Environment

Run Python through conda env `codex`; never use system Python or conda `base`. Use native output for ordinary UTF-8 commands and invoke Encoding Guard only for a real encoding boundary.

## Execute Python

- Prefer `conda run -n codex python ...` for portable commands.
- An absolute interpreter is acceptable only after confirming it resolves to the `codex` environment.
- Run tests, scripts, build helpers, and package inspection in the same environment.

## Manage dependencies

1. Inspect the project's declared requirements, lockfiles, and existing environment before changing packages.
2. Confirm the package or compatible version is actually missing.
3. Prefer the project's declared installer. If none exists, use `python -m pip` in env `codex`.
4. Dry-run the smallest install that satisfies the task. Do not add broad upgrade flags.
5. Stop if the resolver would remove, downgrade, or incompatibly upgrade existing packages.
6. After installation, verify the import or command and run `python -m pip check`.

Read [dependency-management.md](references/dependency-management.md) before installing, removing, upgrading, or repairing packages.
