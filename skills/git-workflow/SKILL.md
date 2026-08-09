---
name: git-workflow
description: Classify first-party and third-party Git repositories, remember the user's decision, protect dirty worktrees, validate completed stages, and create scoped local commits. Use after changing a Git repository when global or repository policy calls for committing completed validated work, or whenever Codex must decide whether an automatic commit is allowed.
---

# Git Workflow

Use native Git for ordinary UTF-8 repository work. Invoke Encoding Guard only when command output or a path crosses a real unknown/legacy encoding boundary.

## Classify the repository

1. Resolve the repository root and read its applicable `AGENTS.md` files.
2. Honor an explicit `Repository Ownership` classification there first.
3. Otherwise run `scripts/repository_registry.py lookup --repo ROOT` through conda env `codex`. Use Encoding Guard only if an actual encoding boundary corrupts the output.
4. If the result is unknown, ask whether the repository is `first-party` or `third-party`. Do not infer ownership from its path or remote.
5. Record the answer with `scripts/repository_registry.py remember --repo ROOT --classification VALUE`.

Read [repository-classification.md](references/repository-classification.md) when resolving an override, changed remote, or registry conflict.

## Commit a completed stage

1. Inspect status, diff, staged diff, recent commit style, and applicable repository instructions.
2. Confirm the current stage is complete and the relevant validation actually passed. A waiver or skipped check is not a pass.
3. Continue only for a known `first-party` repository.
4. Stage only files and hunks owned by the current task. Preserve unrelated and pre-existing worktree changes.
5. Reinspect the staged diff and run the repository's required pre-commit checks.
6. Create one local commit describing the logical unit in the repository's established style.
7. Verify the resulting commit and remaining worktree state.

Do not commit when work is incomplete, validation failed, the user declined, ownership is unknown or third-party, or relevant changes cannot be separated safely. Never push unless the user explicitly asks.
