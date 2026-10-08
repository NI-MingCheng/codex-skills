---
name: project-encoding
description: Scan or diagnose unknown and mixed project encodings using the installed EncodingChecker CLI, establish a confirmed file snapshot, and support explicitly authorized conversion or rollback. Use for initial legacy-project confirmation, actual decoding failures, or requested encoding operations. Established UTF-8 projects and edits with a current confirmed encoding map use native tools directly.
---

# 项目编码

普通已确认 UTF-8 项目直接使用原生工具。只有出现解码异常、接手已知 legacy/混合编码工程，或用户要求检查/转换编码时才使用本技能。默认只读扫描，默认不转码。

预置环境是 pwsh 7、.NET 10 Desktop Runtime 和 EncodingChecker agent CLI；不自动安装或修改 shell、locale、profile。本仓库的工具位于 `tools/EncodingChecker-master/App/EncodingChecker.Cli.exe`，使用上游 v3.15.1 的转换引擎。按实际安装位置设置 `$cli` 的绝对路径；技能独立安装时，使用预装工具的实际路径。

```powershell
# $repoRoot 是本仓库的实际路径；独立安装时直接设置预装 CLI 的绝对路径。
$cli = Join-Path $repoRoot 'tools/EncodingChecker-master/App/EncodingChecker.Cli.exe'
& $cli scan --root $root --output (Join-Path $root 'docs/encoding-snapshot.json')
```

快照 schema 为 2，记录文件的编码、BOM、EOL、hash 和确认状态。检查 complete、coverage 和 warnings；读取失败不能当作已完整扫描。BOM、明确项目约定或用户选择可确认编码；合法 UTF-8 字节及检测器建议只是候选。ASCII 兼容多种编码；GBK 与 UTF-8 也可能把同一字节读成不同文本。

需要确认时，用 `probe` 查看候选，向用户集中询问项目/目录约定或少量例外，不要逐个文件重复提问。已在对话中明确编码的文件无需再询问。CLI 提供：

```powershell
& $cli probe --path $file
& $cli confirm --snapshot $snapshot --path $relativePath --encoding gbk --reason '已确认的工程编码约定'
```

确认整个项目的默认编码及例外后，可以传 `--default-encoding gbk`，或使用 [snapshot.md](references/snapshot.md) 中的 policy 文件重新扫描。BOM 优先于默认编码；明确文件规则与 BOM 冲突应处理冲突。

后续直接按已确认编码读取和局部修改，保留原 BOM、EOL、末尾换行和无关内容，不反复调用 CLI 或加载本技能。Codex 按该编码完成并核验修改后，可批量更新受影响记录的 hash/bytes；外部变化、版本切换或新增 legacy 文件则局部重新确认。歧义、未知和二进制文件不能凭猜测改写；写入超出原编码范围的字符时需要明确的编码变更授权。

需要转换或回滚时，读取 [conversion.md](references/conversion.md)。先给出 dry-run 计划；若当前对话尚未明确授权改变指定范围的编码，再询问用户。授权转换并验证消费者兼容后，停止维护旧编码逐文件快照，保留项目编码约定及简短转换/回滚记录。
