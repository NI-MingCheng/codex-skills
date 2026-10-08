# 安装项目编码技能

此仓库当前只有 `project-encoding` 一个技能，工具单独放在 `tools/`。前提是已经安装 PowerShell 7 和 .NET 10 Desktop Runtime；不自动安装依赖或修改其他 Codex 设置。

## 安装目录

将整个 `skills/project-encoding` 目录复制到 Codex 的 skills 目录：配置了 `CODEX_HOME` 时使用该目录下的 `skills/`，否则通常是用户目录下的 `.codex/skills/`。不要只复制 SKILL.md，agents 和 references 也需要保留。

目的技能目录已经存在时，先备份，再替换该技能；不要改其他技能、全局规则、hooks、MCP 或 config.toml。卸载旧技能或调整全局规则需要用户另外明确授权。

将 `tools/EncodingChecker-master` 保留在本仓库内，或者放到自己的预装工具目录。调用时按实际安装位置设置 CLI 的绝对路径；不需要加入 PATH。

```powershell
# $repoRoot 是已验证的仓库实际路径，$projectRoot 是要扫描的实际工程路径。
$cli = Join-Path $repoRoot 'tools/EncodingChecker-master/App/EncodingChecker.Cli.exe'
& $cli --version
& $cli scan --root $projectRoot --output (Join-Path $projectRoot 'docs/encoding-snapshot.json')
```

扫描默认只读，不转换源码。编码未确认时可以集中确认项目默认值与例外；如果拥有者已明确指定整个工程默认 GBK，可以传 `--default-encoding gbk`。确认快照后直接用原生工具按指定编码读取、修改和保存，不每次调用 CLI。

## 转码

`convert --snapshot ...` 默认只生成计划。仅在用户已经授权具体范围的编码变化后才执行 `--apply`，并验证项目的实际消费者。转换成功后旧快照变为过期记录，将其归档，后续已统一 UTF-8 的范围不必继续维护编码映射。

## 构建与验证

CLI project 位于 `tools/EncodingChecker-master/sources/EncodingChecker.Agent/EncodingChecker.Agent.csproj`，使用 .NET 10 SDK，复用上游应用程序集。构建和测试命令见 [工具文档](../tools/EncodingChecker-master/docs/AGENT-CLI.md)。

GitHub 是发布和同步来源；本地 checkout 可放在自己选择的源码目录，exe 可独立放到预装工具目录。云端任务可以直接修改 GitHub 仓库，不要求本机保留 checkout；本机运行仍需要安装 skill 和 exe。云端环境与本机安装分开维护。仓库的 Windows CI 在 push/PR 时构建、运行测试并验证两个 exe 的打包接口；本机安装内容需要另行部署更新。

第三方现有 source 的 encoding、BOM 和 EOL 保持原样，Git attributes 不对 tools/ 做换行归一化。
