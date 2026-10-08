# 一次扫描与确认

CLI 的 `scan` 只读取项目文件；`--output` 写入快照 JSON，不改项目源码。默认排除构建目录、vendor、generated 和二进制，不跟随 reparse points。若用户要求检查这些目录，可用 `--exclude` 明确调整范围。

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

状态包括 `confirmed`、`ascii-compatible`、`ambiguous`、`needs-confirmation`、`unknown`、`binary`、`excluded` 或 `error`。普通编辑使用确认过的 encoding；ASCII-only 内容可原生修改 ASCII 字符，加入中文则遵循已确认项目编码约定。快照中的 `candidates` 和 `suggested_encoding` 供决策，不是保存授权。

完成扫描后在聊天中简述确认数量、待确认范围和快照路径即可，无须再创建长篇报告。快照默认位于仓库根目录 `docs/encoding-snapshot.json`。
