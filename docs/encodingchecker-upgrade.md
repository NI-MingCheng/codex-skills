# EncodingChecker v3.15.1 + agent 接口

以 `amrali-eg/EncodingChecker` 的 v3.15.1、commit `56550ed97401aef47bdc1e83ca10ccfcc80a18ae` 为基线。原 v2.0 fork 已替换，运行依赖改为预装 .NET 10 Desktop Runtime。

新增 Console host 复用上游程序集，提供 JSON probe/scan/confirm/convert/apply/rollback。默认只读扫描，转换与回滚默认预览。编码确认绑定完整文件 hash、BOM 和严格 round-trip；已确认 GBK 不会因无 BOM UTF-8 兼容性被否决，普通 UTF-8 项目仍原生编辑。

转换使用上游 plan/apply、完整输出核验、原字节备份和 recovery metadata，不另写转换引擎。shared detector 和 UTF.Unknown 2.7.0 未改。小范围 console writer 修正及其他源码变化见 [UPSTREAM.md](../tools/EncodingChecker-master/UPSTREAM.md)。

## 验证

- Release solution build：0 warnings、0 errors。
- 完整 xUnit 回归：1022 passed、0 failed、0 skipped；包含原上游 987 项及新增 35 项。
- 最终 single-file exe 的独立进程验证：81 项通过，0 失败；包括 52 个只读样本、32 个双合法候选、50 个已知 codec 文件的完整文本/EOL 保留及原字节恢复。
- 原 GUI 程序的版本与只读 CLI 实际调用通过；没有执行真实窗口 GUI smoke，也不将这些 CLI 检查当作 GUI 验收。
- 本机 VSTest 与 testhost 的 loopback 连接报 10060，两次启动失败。改用官方 xUnit 2.9.3 Console Runner 在测试输出目录执行同一程序集，未删测试或改断言，未修改系统网络。
- 两个 project 分别发布到 staging，随后打包；build-agent.ps1 实际调用版本接口，防止普通 apphost 覆盖 single-file bundle。

同一个 1000 文件、约 15 MB 的 warm-cache 样本，最终 agent scan 五轮中位数约 319 ms，旧 CLI 为 783 ms。两者都输出完整快照，但候选集合和实现不同；此数值用于说明本机实测，不是跨环境性能保证。

## 安全范围

此前对固定上游版本的源码、发布包程序集引用与 UTF.Unknown 依赖检查未发现隐蔽联网、代码上传或恶意持久化迹象。新增 agent 层只有本地文件操作与上游 API 调用，没有网络或 shell 执行入口；本仓库的 exe 从本地已检查源码构建，不直接使用上游下载的 exe。

完整 .NET/Windows runtime、native apphost 的形式化证明、网络抓包及所有 GUI 行为不在此次验证范围内。不能把静态检查或 hash 一致写成“绝对无后门”。

## 使用变化

- snapshot schema 升级为 2，旧 CLI 1.x 快照重新生成。
- 恢复材料改为相邻 `.bak`、`.ecmeta.json` 和明确的 journal；已有材料不自动覆写。
- 转换与恢复是逐文件安装，失败以 journal/rollback report 判断实际进度。
- skill 的 `references/conversion.md` 已更新为 plan/apply 和默认预览恢复流程。
- 本地 checkout 与预装工具可以分开放置；云端修改 GitHub 仓库后，本机安装的 skill 和 exe 需要部署更新。
- Windows CI 已加入，使用固定 SHA 的 checkout/setup-dotnet Actions，在 push/PR 时构建、测试及验证 single-file 打包。
