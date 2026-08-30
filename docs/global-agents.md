# Codex 全局规则模板

此文件是可公开复用的全局 `AGENTS.md` 模板。应用到设备时，应先备份现有文件并合并规则，不得直接覆盖用户已有配置。仓库的 Skill 安装和更新脚本不会自动修改全局 `AGENTS.md`。

```markdown
# Codex Global Instructions

## Response Language

- 默认使用中文回复，常用 technical terms 保留原文。

## Documentation Routing

- 项目文档写入仓库根目录 docs/。

## Documentation Language

- 项目文档默认使用中文；technical terms、identifiers、commands、paths 和原始 logs/errors 保留英文。

## Git Workflow

- 对已确认为 first-party 的仓库，完成独立阶段且相关验证通过后，使用 $git-workflow 创建 scoped commit；第三方、不完整、验证未通过或无法隔离用户改动时不提交。

## Python Environment

- 所有 Python 工作使用 $python-env，并在 conda 环境 codex 中执行；不得使用 system Python 或 conda base。

## Text Policy

- 所有文本读写和文本型命令使用 $encoding-guard；保留原 encoding、BOM 和 EOL，编码不明确时停止。
- 新建 first-party C/C++ 仓库默认采用 UTF-8 no-BOM 和 LF，并用仓库配置声明；不得归一化 vendor、generated 或无关文件。

## Windows Path Safety

- 普通 Markdown 文本中的 `\_`、`\*`、`\[` 等可能是转义表示；不得未经验证就把转义反斜杠当作 Windows 路径分隔符。
- 执行文件系统写入、复制、移动或删除前，使用 `Test-Path -LiteralPath` 或 `Get-Item -LiteralPath` 验证用户提供的每个源路径和目标路径。
- 若 literal path 与去除疑似 Markdown escape 后的候选路径不同，以实际存在的路径、地址栏文本和截图证据交叉验证；多个候选同时存在或证据仍有歧义时先询问用户。
- 不得仅因疑似 Markdown escape 而创建新的目录层级；向用户回显命令时使用 code block 保留最终路径的 literal form。
```
