# Legacy encoding workflow

## Ambiguous input

Do not summarize, parse, edit, or retry ambiguous bytes under random encodings. Ask the user to select the reported candidate, then add the narrowest file policy:

```text
policy add-file --root ROOT --glob GLOB --encoding ENCODING
```

Prefer an exact repository root and narrow relative glob. Do not create machine-wide wildcard policies.

## Transaction

For BOM, UTF-16, or legacy encodings:

```text
checkout PATH
# Edit only the returned UTF-8 work_file.
commit TOKEN
```

The commit step verifies that the source bytes are unchanged since checkout and writes back using the original encoding, BOM, and line endings. A failed commit preserves both the original and transaction. Use `abort TOKEN` when abandoning the edit.
