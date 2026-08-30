# codex-skills

用于公开发布和跨设备同步个人 Codex Skills 的仓库。每个 Skill 均可独立安装、更新和验证。

## 可安装 Skills

- `encoding-guard`：按需安全处理未知或 legacy encoding、BOM/EOL、mojibake 和跨 shell Unicode transport。
- `git-workflow`：识别仓库归属、保护 dirty worktree，并为已验证的阶段创建 scoped commit。
- `python-env`：确保所有 Python 工作只在 conda 环境 `codex` 中执行。

本仓库只发布可移植 Skills，不安装或配置 Plugins、MCP Servers、Hooks、Codex 主配置以及系统软件。安装器会先执行 prerequisite 与冲突检查；缺少依赖时只报告，不自行安装软件、runtime、conda environment 或 package。

## 全局规则模板

- [`docs/global-agents.md`](docs/global-agents.md)：可公开复用的全局 `AGENTS.md` 模板，包含 Windows path 与 Markdown escape 的安全验证规则。
- 出于用户设备安全边界，Skill 安装和更新脚本不会自动覆盖全局 `AGENTS.md`；需要时由用户明确授权后人工同步。

## 复制给 Codex 的两句指令

### 新设备全量或指定安装

```text
参考 https://github.com/NI-MingCheng/codex-skills.git，请在这台新设备安装 Codex Skills：默认全量安装，若我另外给出 Skill 名称则只安装指定项；先验证本机代理、Git、PowerShell 7、conda codex 环境和仓库声明的依赖，缺少 prerequisite 时只报告，不要安装软件、runtime、environment 或 package。
```

### 已有设备更新

```text
参考 https://github.com/NI-MingCheng/codex-skills.git，请更新这台设备已安装的 Codex Skills：默认更新全部已安装项，若我另外给出 Skill 名称则只更新指定项；完成验证，并保持未选择的 Skills、Plugins、MCP、Hooks、AGENTS.md 与 config.toml 不变。
```
