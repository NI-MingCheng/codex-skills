# codex-skills

个人 Codex 全局规则与技能的存储仓库。目前只发布 **项目编码** 技能及它使用的 EncodingChecker 工具；后续可按需要增加其他 skills、hooks、MCP 配置或全局规则。

## 目录

```text
codex-skills/
├── skills/
│   └── project-encoding/          # 显示名称：项目编码
│       ├── SKILL.md
│       ├── agents/openai.yaml
│       └── references/
├── tools/
│   └── EncodingChecker-master/
│       ├── App/
│       │   ├── EncodingChecker.exe
│       │   └── EncodingChecker.Cli.exe
│       ├── sources/
│       ├── docs/cli-workflow.md
│       └── LICENSE
└── docs/
    └── installation.md
```

`skills/<skill-name>/SKILL.md` 使用 Codex 的技能目录形式。目录和 frontmatter 的 name 保持 `project-encoding`，中文名称“项目编码”由 UI metadata 提供。当前不包含旧 `encoding-guard`、`git-workflow`、`python-env` 或旧安装框架。

## 项目编码

- 普通已确认 UTF-8 项目，以及已有当前确认编码映射的编辑，直接使用原生工具。
- 接手 legacy/混合编码项目、遇到实际解码异常或用户要求检查时，只读扫描一次，集中确认默认编码和例外，之后按快照读写。
- 默认不改变源码编码。明确授权后才能统一为 UTF-8，并保留转换日志与原字节回滚备份。

GBK 与 UTF-8 可以把同一字节解释成不同文本。例如 `C2 A9` 在 GBK 中是“漏”，在 UTF-8 中是“©”。CLI 会保留 `ambiguous`，不能仅凭合法解码或统计 confidence 确认作者的编码选择。

## EncodingChecker CLI

**CLI 已加入**，是独立 Console project，原 GUI 仍保留。`App/EncodingChecker.Cli.exe` 输出 UTF-8 JSON，支持：

```text
probe     检查单个文件，展示候选
scan      批量只读扫描，保存编码快照
confirm   按已确认的约定更新快照，不改变原文件
convert   默认 dry-run；--apply 执行已授权的 UTF-8 转换
rollback  校验 hash 后恢复原字节
```

完整接口与行为边界见 [CLI 文档](tools/EncodingChecker-master/docs/cli-workflow.md)。工具源码、原检测库和许可证都保留，构建缓存、测试工作目录和调试文件不入库。

## 使用

依赖预先安装的 PowerShell 7；运行 CLI 需要 Windows .NET Framework 4.8。不会自动安装系统依赖、修改 PATH、覆盖全局 AGENTS.md 或配置 hooks/MCP。

安装或更新步骤见 [安装说明](docs/installation.md)。可直接给 Codex 这条指令，并指定本机仓库和工具路径：

```text
参考此仓库，安装 skills/project-encoding，使用 tools/EncodingChecker-master/App/EncodingChecker.Cli.exe。普通 UTF-8 工作使用原生工具；混合编码只读扫描并确认一次后复用快照，默认不转码。
```

## 许可证

仓库自行维护的技能与说明使用根目录 [MIT License](LICENSE)。`tools/EncodingChecker-master` 使用其 [原项目许可证](tools/EncodingChecker-master/LICENSE)，检测库各文件的既有许可证声明保持原样；根目录 MIT 不替代第三方许可。
