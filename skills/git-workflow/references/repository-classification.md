# Repository classification

## Precedence

1. An applicable repository `AGENTS.md` may declare:

   ```md
   ## Repository Ownership
   - Classification: first-party
   ```

   The other valid value is `third-party`.
2. Otherwise use the exact canonical repository-root record in the personal registry.
3. If no record exists, or the recorded remote no longer matches the current remote, ask the user and remember the answer.

Never infer ownership from a directory such as `D:\Codes`, repository contents, author names, or a writable remote.

## Registry

The helper stores UTF-8 no-BOM/LF JSON at `%USERPROFILE%\.codex\git-workflow\repositories.json`. Each record contains the canonical root, current `origin` URL when available, classification, and recorded timestamp.

An exact root match with a changed non-empty remote returns unknown. Reconfirm it before committing. Repository instructions override the personal registry but do not rewrite it automatically.

## Safe commit boundary

- Treat existing staged changes as user-owned until proven otherwise.
- Prefer path- or hunk-scoped staging.
- Do not use destructive reset or checkout commands to manufacture a clean worktree.
- A successful build does not imply unrelated tests passed; use the validation actually required and authorized for the repository.
