# Repository Instructions

## Repository Ownership

- Classification: first-party

## Scope

- Store only portable, public Codex Skills plus their bootstrap, synchronization, validation, and documentation.
- Never commit credentials, tokens, authentication state, sessions, memories, logs, repository classification state, proxy values, or machine-local absolute paths.
- Never install, update, remove, or configure Codex Plugins from this repository.

## Workflow

- Keep repository-owned text UTF-8 without BOM and LF unless `.gitattributes` declares an exception.
- Use native tools for ordinary UTF-8 work and `apply_patch` for text edits. Invoke Encoding Guard only for a real unknown or legacy encoding boundary.
- Validate a completed stage before creating a scoped commit.
- GitHub Actions may create an isolated conda environment and install locked test dependencies; user-device scripts may not.

## User-device safety

- Restore, install, update, sync, bootstrap, and validation requests do not authorize software installation, environment creation, package installation, or PATH changes.
- If Git, PowerShell 7, conda, the `codex` environment, or a declared package is missing, stop and report the exact prerequisite.
- Back up an existing destination Skill before replacement and restore it if deployment or post-install verification fails.
- A named sync may modify only that Skill. Never touch global `AGENTS.md`, Hooks, `config.toml`, Plugins, or another Skill.

## Network bootstrap

- On a new Windows device, resolve and validate a credential-free loopback HTTP or mixed proxy before GitHub clone, fetch, pull, dependency checks, or long validation.
- Prefer an explicit URL, then `CODEX_PROXY_URL`, enabled Windows system proxy, consistent proxy environment, and finally verified loopback listeners. Stop when discovery is ambiguous.
- Verify the local listener and HTTPS connectivity to GitHub, ChatGPT, and OpenAI. Back up user proxy environment values before persisting changes.
- Never install, start, stop, or reconfigure proxy software. Never write proxy settings to Codex `config.toml`.

## Windows process UX

- Non-interactive checks, tests, and automation must not open visible console windows.
- Python subprocesses use `CREATE_NO_WINDOW`; PowerShell `Start-Process` uses `-WindowStyle Hidden` unless the user explicitly requests an interactive window.
