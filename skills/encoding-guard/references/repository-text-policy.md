# Repository text policy

## Existing repository

Inspect `.gitattributes`, `.editorconfig`, applicable `AGENTS.md`, and representative tracked files before editing. Preserve existing encoding, BOM, and EOL. Do not normalize vendor, generated, binary, or unrelated dirty files.

Before a one-time cleanup, report the exact scope and separate it from functional changes.

## New first-party C/C++ repository

Before substantive edits, add repository-owned LF policy when no contrary tool requirement exists:

```gitattributes
* text=auto eol=lf
*.bat text eol=crlf
*.cmd text eol=crlf
```

```editorconfig
root = true

[*]
charset = utf-8
end_of_line = lf
insert_final_newline = true

[*.{bat,cmd}]
end_of_line = crlf
```

Add other CRLF exceptions only for confirmed tool requirements.

## Staged checks

- Run `git diff --cached --check`.
- Flag BOM changes, mixed EOL, and changes whose only byte difference is CRLF versus LF.
- Review `.gitattributes` and `.editorconfig` changes together with affected files.
- Do not auto-fix staged files; report violations and let the task choose the narrow correction.
