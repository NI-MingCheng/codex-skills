# Dependency management

## Inspect first

- Check `pyproject.toml`, lockfiles, requirements files, conda environment files, and the installed version.
- Use the project's existing package manager and constraints when present.
- Capture `python -m pip check` before a repair when dependency health is uncertain.

## Install minimally

- Use a dry run before mutation when the installer supports it.
- Install only the missing package or the narrowest compatible version range.
- Do not use `--upgrade`, `--force-reinstall`, resolver bypasses, or environment-wide updates unless the user explicitly requests that operation.
- Do not modify system Python or conda `base`.

## Stop conditions

Stop and report the proposed dependency changes when the resolver would remove packages, downgrade unrelated packages, cross declared constraints, or cannot produce a consistent environment.

After a safe installation, verify the requested import or executable and run `python -m pip check`.
