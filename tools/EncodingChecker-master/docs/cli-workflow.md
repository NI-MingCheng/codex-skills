# EncodingChecker Console CLI

CLI 位于本工具目录的 `App/EncodingChecker.Cli.exe`；在此仓库中的相对位置是 `tools/EncodingChecker-master/App/EncodingChecker.Cli.exe`。调用时使用实际保存位置的绝对路径。

这是独立 Console project，直接链接仓库现有 `UtfUnknown/**/*.cs` 和 `Utf16Detector.cs`，不启动 GUI，不需要 NuGet。它适用于需要明确 encoding、BOM 和 EOL 边界的检查与迁移。统计检测只能给出建议，无法证明无标记文件原来的 encoding。

## 命令与退出码

```text
probe --path FILE [--encoding CODEC]
scan --root DIR [--output JSON] [--default-encoding CODEC] [--policy JSON] [--exclude dir1,dir2]
confirm --snapshot JSON --path RELATIVE --encoding CODEC [--reason TEXT]
convert --snapshot JSON [--backup-dir DIR] [--apply]
rollback --log JSON --apply
--help
--version
```

各子命令也支持独立 `--help`，例如 `scan --help`，返回 JSON 且不访问 source。

stdout 是 UTF-8 no-BOM JSON；stderr 是 UTF-8 诊断。CLI 使用自己的 stream writer，不更改系统 locale、console code page、PowerShell profile 或全局 encoding policy。

| exit code | 含义 |
| --- | --- |
| `0` | 当前操作成功；`convert` 未带 `--apply` 时仅表示计划有效 |
| `2` | 存在 `ambiguous`、`needs-confirmation`、`unknown` 或整批转换被未解决项阻止 |
| `1` | 参数、unsupported encoding、BOM 冲突、严格解码、I/O、hash 或写入/回滚错误 |

支持 `utf-8`、`ascii`、`gbk`、`gb18030`、`windows-1252`、`big5`、`shift-jis`、`utf-16-le`、`utf-16-be`、`utf-32-le`、`utf-32-be`。例如 `cp936` / `gb2312` canonicalize 为 `gbk`，`54936` 为 `gb18030`，`cp1252` 为 `windows-1252`，`cp950` 为 `big5`，`Shift_JIS` / `cp932` / `windows-31j` 为 `shift-jis`（Windows CodePage 932）。未列出的 codec 会报错，不做猜测。UTF-7 BOM 和 GB18030 的特殊 BOM convention 会明确报 unsupported。

## 检测结果与确认

| status | 含义 | 可以转换 |
| --- | --- | --- |
| `confirmed` | BOM，显式 `--encoding`，已声明 policy 或 `confirm` 已指定 codec，且整份字节严格 round-trip | 是 |
| `ascii-compatible` | 只有合法 ASCII 文本字节；这同时兼容多种 codec，并非唯一识别 | 是 |
| `ambiguous` | 至少两个严格合法 codec 产生不同 Unicode 文本 | 否，先确认 |
| `needs-confirmation` | 存在严格兼容候选，但证据不能证明原 encoding | 否，先确认 |
| `unknown` | 没有自动候选范围内的严格文本候选；可提供已知 codec 进行确认 | 否 |
| `binary` | 二进制 extension/signature 或非文本 control/NUL 字节 | 跳过 |
| `excluded` | 已排除目录、reparse point、当前 snapshot output 或 policy input | 跳过 |
| `error` | 扫描或解码失败，扫描不完整 | 阻止整批转换 |

例如 `C2 A9` 在 UTF-8 下是 `©`，在 GBK 下是另一段文本；CLI 必须返回 `ambiguous` 和 `encoding: null`。GBK 与 GB18030 即使产生相同文本，也只返回兼容候选和 `needs-confirmation`。仅有严格 UTF-8 候选时，仍需确认；`suggested_encoding` 和 `confidence` 不改变 status。

UTF-8/16/32 BOM 会优先确定 codec。BOM 与明确文件 policy 冲突时会报错。无 BOM UTF-16 结构可以成为候选，需要确认；BOM 或已指定 UTF-16/32 会优先于基于 NUL 的 binary 分类。所有候选都要求 exception fallback 的 decoder 和 encoder，以及整份 payload 的 byte round-trip。扫描中的 binary extension/signature 仍然会排除文件。

自动 round-trip 候选范围为 UTF-8、GBK、GB18030，以及检测到的 UTF-16/32 结构候选。Windows-1252、Big5 和 Shift-JIS 支持显式 `--encoding`、policy 和 `confirm`；支持 codec 并不表示自动识别它。

`probe` 的候选包含最多 120 个 UTF-16 code units 的 preview，供人工选择；截断不会拆开 surrogate pair。`scan` 不输出 preview 内容。`eol` 记录 CRLF、单独 LF、单独 CR 的次数、`kind` 和 `final_newline`；未确认时仅在全部候选一致的情况下输出 EOL。启发式结果只作为建议。

snapshot 的 JSON 形状如下，`files[].path` 始终是 root 内的相对 slash 路径：

```json
{
  "schema_version": 1,
  "kind": "encodingchecker-snapshot",
  "root": "E:\\ExampleProject\\",
  "created_utc": "2026-10-08T00:00:00.0000000Z",
  "state": "scanned",
  "files": [
    {
      "path": "src/example.txt",
      "encoding": null,
      "status": "ambiguous",
      "sha256": "<64 lowercase hex characters>",
      "bytes": 2,
      "bom": null,
      "eol": { "kind": "none", "crlf": 0, "lf": 0, "cr": 0, "final_newline": false },
      "candidates": [
        { "encoding": "utf-8", "roundtrip": true, "text_sha256": "<64 hex characters>", "preview": null },
        { "encoding": "gbk", "roundtrip": true, "text_sha256": "<64 hex characters>", "preview": null },
        { "encoding": "gb18030", "roundtrip": true, "text_sha256": "<64 hex characters>", "preview": null }
      ],
      "evidence": ["complete-strict-decoder-encoder-byte-roundtrip", "valid-encodings-produce-different-text"],
      "suggested_encoding": null,
      "confidence": null
    }
  ]
}
```

`schema_version`、`root`、`files` 必须存在且有效。缺失 `files` 的 snapshot 或 conversion log 会被拒绝，不能等同于显式声明的空列表。

`probe` 直接输出同形状的单个 file record，path 是 absolute path。文件读取失败时输出错误 JSON；扫描会保留 `error` entry 并通过 stderr 报告。读取原字节成功但 fallback 解码或 BOM/file-policy 校验失败时，`error` entry 仍保留可靠的 SHA-256、byte count 和已识别 BOM，可以直接用 `confirm` 修正 codec，无需重扫。真正的 I/O 读取失败不会伪造 hash。

## 扫描、policy 和转换流程

默认排除目录名为 `.git,.svn,bin,obj,build,dist,node_modules,.venv,venv,vendor,third_party,generated`。`--exclude` 覆盖这组目录名。任何 reparse point 都不会被跟随。`docs/encoding-backups` 及带本工具 backup marker 或有效 conversion log 的 backup directory 始终排除；当前 `--output` 与 `--policy` 文件也排除。backup marker 在备份开始前写入，即使 custom backup 的日志缺失或损坏，也不会再次扫描备份。扫描输出父目录不存在时，在验证已有祖先没有 reparse point 后创建所需目录。`--output` 不得与 policy input 是同一个文件。

policy 必须是 UTF-8 JSON：

```json
{
  "default_encoding": "gbk",
  "files": {
    "src/known-utf8.cpp": "utf-8",
    "resources/known-gbk.txt": "gbk"
  }
}
```

`files` 仅接受 root 内的 relative path；它声明了已知的实际 codec。`default_encoding` 或 `--default-encoding` 是用户已确认的 fallback，只有无 BOM 且没有文件级声明时才使用。BOM 始终优先于 fallback。不要为了消除歧义而随意指定 default。

下面使用 PowerShell；示例 root 需要替换为实际项目目录：

```powershell
# $toolRoot 是已经验证的实际工具目录；$root 是实际工程目录。
$cli = Join-Path $toolRoot 'App/EncodingChecker.Cli.exe'
$root = 'E:\ExampleProject'
$snapshot = Join-Path $root 'docs\encoding-snapshot.json'
& $cli probe --path (Join-Path $root 'resources\example.txt')
& $cli scan --root $root --output $snapshot
# 已确认实际 codec 后，仅更新 snapshot；源文件保持原字节。
& $cli confirm --snapshot $snapshot --path 'resources/example.txt' --encoding gbk --reason 'confirmed from producing application'
# 默认 dry-run：不创建 backup，也不改源文件。
& $cli convert --snapshot $snapshot
# 上层工作流取得用户对具体计划的授权后才执行。
& $cli convert --snapshot $snapshot --apply
```

`confirm` 会校验源 SHA-256 未变、目标路径在 root 内、没有 reparse point、指定 codec 严格 round-trip 成功。它只更新对应 snapshot entry，不修改源文件。

`convert --apply` 会拒绝存在非空 unresolved 文件或 `error` entry 的整批转换，不会默默完成其中一部分。全部 text source 在写入前预检 hash、byte count、BOM、strict decoding、路径和 read-only attribute。read-only source 会被拒绝，CLI 不清除此属性。binary file 的 hash 也预检，excluded entry 跳过。

目标是 UTF-8 no-BOM；Unicode 文本、混合 EOL 和 final newline 均保留。原本已是目标字节的文件记录为 `unchanged`。每个改变的文件先备份原字节，备份 hash 通过后保存初始 log，再使用同目录临时文件和原子 `File.Replace`。每次替换前重新检查源 hash，写后验证目标 hash、Unicode 文本和属性。日志保存成功之后才报告成功。

默认 backup directory 为 `root/docs/encoding-backups/<UTC timestamp>-<fresh GUID>`；可用 `--backup-dir DIR` 指定新的或空的目录，不能覆盖已有 backup。日志存于 `backup-dir/conversion-log.json`。备份不得包含本次转换 source。发生应用错误时，CLI 尝试按 hash 保护自动回滚已经处理的文件，并记录恢复结果；无法恢复或无法持久化日志时通过返回 JSON、exit `1` 和 stderr 明确报告。不要将失败输出当作完成。

转换结果的主要字段为：

```json
{
  "schema_version": 1,
  "operation": "convert",
  "root": "<absolute root>",
  "apply": true,
  "target_encoding": "utf-8",
  "backup_dir": "<absolute backup directory>",
  "log": "<absolute conversion-log.json>",
  "success": true,
  "source_snapshot_stale": true,
  "files": [
    {
      "path": "resources/example.txt",
      "original_encoding": "gbk",
      "original_bom": null,
      "original_sha256": "<original hash>",
      "converted_sha256": "<converted hash>",
      "backup_path": "<absolute backup/original/resources/example.txt>",
      "state": "converted",
      "error": null
    }
  ],
  "blocked": [],
  "errors": []
}
```

conversion log 额外包含 `kind: encodingchecker-conversion-log`、`snapshot`、`created_utc`、`updated_utc` 和整批 `state`，以及相同的逐文件 original/converted hash、codec、BOM、backup path 和 state。原 snapshot 不会被自动重写；成功结果会设置 `source_snapshot_stale: true`。转换完成后废弃旧映射即可，已经统一为 UTF-8 的项目无需继续生成或维护 snapshot。只有将来再次使用混合编码 snapshot 流程时，才需要先扫描取得当时的状态。旧 hash 不能用于已经改变的 source。

## 回滚

```powershell
& $cli rollback --log 'E:\ExampleProject\docs\encoding-backups\<id>\conversion-log.json' --apply
```

回滚先预检整批 current source hash 与 backup hash。source 必须仍然是日志中的 converted bytes，或已恢复的 original bytes；外部改动不会被覆盖。全部通过后原子恢复原字节，保留 log 并更新 state。重复回滚是安全的。log 应留在原 recorded backup directory，backup tree 不能移出或包含 reparse point。

## 构建与验证

```powershell
& $msbuild `
  (Join-Path $toolRoot 'sources/EncodingChecker.Cli/EncodingChecker.Cli.csproj') `
  /nologo /t:Build /p:Configuration=Release /verbosity:minimal
& (Join-Path $toolRoot 'sources/EncodingChecker.Cli/self-test.ps1')
```

`$msbuild` 指向本机已安装的 MSBuild。构建依赖现有 .NET Framework 4.8 reference assemblies，只输出独立 `App/EncodingChecker.Cli.exe`，不重建 GUI exe；仓库分发配置关闭 debug symbols，避免包含本机构建路径。process-local .NET path switches 和嵌入 manifest 支持长路径，没有更改系统 registry。

最终 Release 自测通过 **276 assertions**。覆盖 strict UTF-8/GBK ambiguity、GBK/GB18030 compatibility、UTF-8-only suggestion、ASCII/empty、UTF-8/16/32 BOM、invalid UTF-8/surrogate、无 BOM UTF-16 explicit/structural result、Windows-1252/Big5/Shift-JIS 显式 codec 与转换/回滚、binary exclusion、policy/output exclusion、缺失 output ancestor 的安全创建、读取成功但解码失败的 metadata 保留与 `confirm` 修正、各子命令 help、缺失 manifest fields 的拒绝、source hash 变化、path escape、read-only 拒绝、hidden attribute 保留、整批拒绝、dry-run、UTF-8 no-BOM no-op、BOM/EOL/final newline 保留、日志、损坏备份、外部修改后的回滚拒绝、恢复原字节、重复回滚、custom backup 损坏日志后的排除和 junction 排除。另有真实 mid-batch 只读失败注入，验证已转换 source 自动恢复原字节、只读属性未清除，失败与恢复状态持久化到 log。自测以 hidden child process 运行，fixture 在本 project 的临时 test-work 中创建并清理。

未验证断电、进程强杀、磁盘耗尽、网络 filesystem 的原子替换语义，或其他进程在最后一次 hash 检查与原子替换之间并发改写 source 的情形。一次只运行一个转换或回滚 batch，并在处理期间暂停对该 batch 文件的编辑。
