# Agent CLI

基于 EncodingChecker v3.15.1，使用上游转换、核验、plan/apply 和恢复元数据实现。独立 Console host 为 `App/EncodingChecker.Cli.exe`；上游 `App/EncodingChecker.exe` 的 GUI 和原 CLI 仍可使用。

运行依赖预装的 Windows .NET 10 Desktop Runtime。stdout 固定为 UTF-8 no-BOM JSON，stderr 为诊断信息。无参数及 `--help`/`--version` 输出版本与命令说明。

```powershell
& $cli probe --path $file
& $cli scan --root $root --output $snapshot
& $cli scan --root $root --default-encoding gbk --output $snapshot
& $cli scan --root $root --policy $policy --output $snapshot
& $cli confirm --snapshot $snapshot --path 'src/legacy.cpp' --encoding gbk --reason '已确认工程约定'
& $cli convert --snapshot $snapshot --plan $plan
& $cli apply --plan $plan --log $journal
& $cli rollback --log $journal
& $cli rollback --log $journal --apply --output $restoreReport
```

`scan`/`probe` 不改变源文件。`convert` 默认预览；`--plan` 保存上游格式的审核计划。确认授权后可 `apply`，或使用 `convert --snapshot ... --apply --log ...`。`rollback` 默认预览，只有 `--apply` 才恢复。

## 编码依据

快照 schema 2，kind 为 `encodingchecker-agent-snapshot`，包含 root、complete、coverage、warnings、files。文件条目记录 path、status、encoding、suggested_encoding、sha256、bytes、bom、eol、candidates 和 evidence。旧 CLI 1.x 快照不兼容，重新扫描生成。

兼容解码不能证明历史编码：无 BOM UTF-8 也可能是 GBK。工具保留 `ambiguous` 或 `needs-confirmation`，用户/工程默认值和文件例外可确认 codec。BOM 优先于项目默认值，明确文件规则与 BOM 冲突时报错。候选检查、hash 与 EOL 分块进行，不缓存整份文件或整批字节。

政策文件示例：

```json
{
  "default_encoding": "gbk",
  "files": { "src/new.cpp": "utf-8" }
}
```

确认的文件会按完整文件严格解码、重新编码，并核对原字节 round-trip。无 BOM 的合法 UTF-8 不会覆盖已确认的 GBK 原意；BOM 冲突、非法字节、非文本控制字符和过期 hash 仍拒绝。上游 shared UnicodeDetector/TextValidation 没有修改。

ASCII-compatible 仅说明当前字节兼容，不证明未来中文应使用何种编码。普通已确认 UTF-8 项目直接原生编辑；快照只在必要时维护。

## 扫描范围与完整性

`--include`/`--exclude` 接受逗号分隔的 wildcard；含分隔符的模式匹配相对路径，其余匹配任意深度文件名。额外排除 vendor、third_party、generated 的文件。上游固定排除常见版本控制、构建和依赖目录，并跳过 hidden/system/reparse-point 文件和目录。完整性针对当前选择范围，不表示排除的文件已检查。

目录读取失败在结构化 coverage/warnings 中披露，`complete=false` 并非成功覆盖。当前 snapshot/policy 输出路径不作为源扫描。全部源变化都通过 hash 检查，外部新增文件或分支切换需局部重新确认。

## 转换与恢复

默认目标为 UTF-8 no-BOM，计划保存 codec、BOM、源 hash 与已审核动作。`apply` 复用上游计划执行，不重新推断编码。完整性或确认缺失、过期文件、路径越界、链接路径、已有恢复备份和已有 journal 均拒绝。

每个转换文件的恢复材料是 `<file>.bak` 和 `<file>.ecmeta.json`；journal 记录实际安装结果。报告、plan、journal 应保留在扫描范围外，或显式排除。转换逐文件安装，可能部分完成；不承诺整批事务。

回滚校验当前输出、备份和 metadata，再分块复制备份原字节并替换。所有待恢复条目先预检，后续失败按 rollback report 判断进度。输出报告不能覆盖源、journal 或恢复材料；已有备份保留，重复回滚按原 hash 判断为已恢复。

退出码：0 完成；2 待确认/转换被未确认范围阻止；3 参数、读取、完整性或处理失败；4 取消。上游 apply 的 1/5 退出码仍透传，以 JSON success 与 journal 一起判断，不只检查 exit 0。

## 构建和测试

需要 .NET 10 SDK，不自动安装或修改 PATH。

```powershell
dotnet build sources/EncodingChecker.sln -c Release
dotnet test sources/EncodingChecker.sln -c Release --no-build
pwsh -NoProfile -File build-agent.ps1
```

Console host 把 GUI 程序集当作 API library 引用，不启动它的 apphost；其 SDK executable-reference packaging check 在该 façade project 中关闭，运行依赖仍明确为预装 .NET 10 Desktop Runtime。GUI 原发布配置保留。

构建脚本将两种 exe 发布到独立 staging，再复制到 App/ 并实际调用版本接口，避免 ProjectReference 的普通 apphost 覆盖已生成的 single-file bundle。SDK 不在 PATH 时可以传 `-DotnetPath` 指定绝对路径。
