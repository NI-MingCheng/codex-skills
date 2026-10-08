# 可选转换和回滚

默认计划命令不修改源文件：

```powershell
& $cli convert --snapshot $snapshot
```

展示处理范围、目标 UTF-8 no-BOM、待确认项和备份位置。只有已确认编码的文本参与转换；binary、excluded、vendor 和 generated 不属于默认范围。歧义或源发生变化时先修复快照，不跳过未知错误后声称全部成功。

如果用户已经明确授权转换指定范围，不重复询问；否则先让用户决定是否改变编码。CLI 的 apply 参数不会替代用户授权：

```powershell
& $cli convert --snapshot $snapshot --apply
```

CLI 会在修改前严格解码、核验源 hash 和路径边界，保留原 Unicode 文本、换行和末尾换行，备份原字节后原子保存。只读文件拒绝覆写，不清除只读属性。默认备份和转换日志位于 `docs/encoding-backups/` 下独立目录。原有 UTF-8 no-BOM 文件可以是 no-op。

检查 JSON 中的实际结果及退出码，按项目需要验证编译器、资源、脚本或其他消费者。`source_snapshot_stale=true` 表示旧映射不能再用于读取或保存；转换成功后将它归档到本次备份目录，维护指定范围的项目 UTF-8 约定，而不继续维护旧编码逐文件报告。保留转换日志和回滚备份。

用户要求恢复时：

```powershell
& $cli rollback --log $conversionLog --apply
```

回滚校验转换后的文件 hash 和备份 hash；转换后已被用户编辑的文件不能直接覆盖。失败或 partial 结果应报告具体文件及可恢复状态，不重试覆盖未知改动。
