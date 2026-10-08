# 一次扫描与确认

CLI 的 `scan` 只读取项目文件；`--output` 写入快照 JSON，不改项目源码。默认跳过常见构建目录、vendor/third_party/generated 文件和 reparse points；binary 文件记录 hash 后不参与转换。`--include`/`--exclude` 使用逗号分隔的文件名或相对路径 wildcard；上游硬排除的构建目录不会因 include 而进入。

```powershell
& $cli scan --root $root --output $snapshot
& $cli scan --root $root --default-encoding gbk --output $snapshot
& $cli scan --root $root --policy $policyFile --output $snapshot
```

第二条命令仅用于已确认默认 GBK 的工程，不是因为文件包含中文就选择 GBK。`--policy` 的 JSON 形状：

```json
{
  "default_encoding": "gbk",
  "files": {
    "src/new-module.cpp": "utf-8",
    "config/unicode.ini": "utf-16-le"
  }
}
```

默认编码是 fallback，BOM 优先；明确文件规则和 BOM 不兼容时不继续写入。候选不存在、严格解码失败或源 hash 改变时，记录异常并确认相关文件。不要把 detector 的建议自动升级为用户确认。

`confirm --snapshot ... --path ... --encoding ...` 只更改指定快照条目，核验当前源 hash 和严格解码，不改变原文件。路径使用快照 root 下的相对路径。

快照 schema 为 2，旧 CLI 1.x 快照需要重新生成。状态包括 `confirmed`、`ascii-compatible`、`ambiguous`、`needs-confirmation`、`unknown`、`binary` 或 `error`。普通编辑使用确认过的 encoding；ASCII-only 内容可原生修改 ASCII 字符，加入中文则遵循已确认项目编码约定。快照中的 `candidates` 和 `suggested_encoding` 供决策，不是保存授权。

检查 `complete`、`coverage`、`warnings` 和退出码。`complete=false` 表示选定范围存在读取失败，不能用它执行转换；排除目录在 coverage 中说明。EOL 仅在确认编码或 ASCII-compatible 后有明确含义，未确认文件不凭候选 EOL 写回。

完成扫描后在聊天中简述确认数量、待确认范围和快照路径即可，无须再创建长篇报告。快照默认位于仓库根目录 `docs/encoding-snapshot.json`。
