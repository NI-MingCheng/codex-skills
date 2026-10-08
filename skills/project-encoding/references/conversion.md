# 可选转换和回滚

默认计划命令不修改源文件：

```powershell
& $cli convert --snapshot $snapshot --plan $plan
```

展示处理范围、目标 UTF-8 no-BOM、待确认项和相邻 `.bak` 备份。只有已确认编码或 ASCII-compatible 的文本参与转换；binary、vendor 和 generated 不属于默认范围。歧义、扫描不完整或源发生变化时先修复快照。

如果用户已经明确授权转换指定范围，不重复询问；否则先让用户决定是否改变编码。CLI 的 apply 参数不会替代用户授权：

```powershell
& $cli apply --plan $plan --log $conversionLog
```

也可在已授权的同一会话中使用 `convert --snapshot ... --apply --log ...`。CLI 复用上游 plan/apply：严格解码、核验 hash 和路径边界，保留 Unicode 文本及换行，写入相邻 `.bak`/`.ecmeta.json` 并核验输出。现有备份或 journal 不自动覆写，使用新日志路径，重复转换前保留旧恢复材料。原有 UTF-8 no-BOM 文件可以是 no-op。

检查 JSON 的 `success`、journal 实际 status 和退出码，再验证项目消费者。转换后旧快照的 hash 已过期，应归档并维护该范围的 UTF-8 约定。保留 plan、journal、`.bak` 和 `.ecmeta.json`。转换逐文件安装，不承诺整批事务；失败可能已完成一部分，按 journal 判断恢复范围。

用户要求恢复时：

```powershell
& $cli rollback --log $conversionLog
& $cli rollback --log $conversionLog --apply --output $rollbackLog
```

回滚默认只预览；apply 前核验所有待恢复文件、备份及 metadata 的 hash，转换后被编辑的文件拒绝覆盖。恢复直接复制原字节，保留备份，并记录完成范围。失败或 incomplete 结果应按恢复日志检查，不覆盖未知改动。
