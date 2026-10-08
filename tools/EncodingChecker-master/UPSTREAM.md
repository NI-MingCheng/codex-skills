# 上游与本仓库改动

- 上游：[amrali-eg/EncodingChecker](https://github.com/amrali-eg/EncodingChecker)。
- 基线 tag：`v3.15.1`，commit：`56550ed97401aef47bdc1e83ca10ccfcc80a18ae`。
- 运行时第三方包：`UTF.Unknown` 2.7.0，版本保持上游原值。
- Agent 接口：1.0.0；snapshot schema：2。`--version` 同时报告上游与 agent 版本。

原 GUI、原 CLI、转换/校验/plan/apply/metadata 和 shared detector 保留。新增 `Agent*.cs`、Console host project、行为测试与 `docs/AGENT-CLI.md`，对已确认快照使用上游转换决策和执行路径。确认 codec 与字节候选分开记录，不让 BOM-less UTF-8 兼容性否决已知 GBK 原意。

上游 `Program` 有两处小修正：UTF-8 console 使用 no-BOM writer，避免某些 host 的 Console.OutputEncoding 携带 UTF-8 preamble；未能附着 parent console 时也显式连接 stdout/stderr。前者由三个 console-output 回归测试暴露，进程管道与发布 exe 的版本、检测接口单独验证；其他编码的 console 行为保持原逻辑。

解决方案增加 agent project；vendored `.gitattributes` 最末保留原 source 字节，不让父仓库归一化上游 BOM/EOL。发布 App/ 中的两个 exe 从本仓库源码构建，采用 framework-dependent single-file，无调试符号。没有上传 SDK、NuGet cache 或测试临时文件。

上游许可证及 THIRD-PARTY-NOTICES.txt 保留。此目录内的 agent 改动随工具使用 MPL-2.0；根目录技能与仓库说明的 MIT 不替代工具许可。
