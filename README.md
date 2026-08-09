# codex-skills

可公开读取、可独立验证、可精确同步的个人 Codex Skills 权威源。仓库采用全新历史，不包含 `codex-dotfiles` 的提交历史或设备状态。

## 包含内容

- `encoding-guard`：仅在编码风险真实存在时处理 legacy encoding、BOM/EOL、mojibake 与跨 shell Unicode transport。
- `git-workflow`：识别仓库归属、保护 dirty worktree，并在策略允许时创建 scoped commit。
- `python-env`：约束所有 Python 工作只使用 conda 环境 `codex`。
- 通用本地代理预检、全量首次安装、单 Skill 同步、验证和回滚脚本。

本仓库不管理全局 `AGENTS.md`、Hooks、`config.toml` 或任何 Plugin，也不会同步 token、认证状态、sessions、memories、logs、代理地址和机器路径。

## 新电脑一句话安装

在 Codex 中发送：

> 请先自动发现并稳定配置本机已有的 credential-free loopback HTTP/mixed proxy，验证 GitHub、ChatGPT 和 OpenAI 连通；然后从 https://github.com/NI-MingCheng/codex-skills.git 安装全部个人 Skills。缺少 Git、PowerShell 7、conda、codex 环境或依赖时只报告，不要安装任何软件、环境或 package；不要修改 AGENTS.md、Hooks、config.toml 或任何 Plugin。

代理软件不限定品牌。Codex 应优先读取显式地址、`CODEX_PROXY_URL`、Windows system proxy 和一致的 proxy environment；仍未找到时才检测 loopback listeners，并且只能接受实际通过 HTTPS 检查的唯一候选。自动发现有歧义时停止，不猜测、不安装、不启动或重配代理软件。

代理可用后，使用 HTTPS clone；公开读取不需要 GitHub 登录或 SSH：

```powershell
git clone https://github.com/NI-MingCheng/codex-skills.git D:\Code\Codex\codex-skills
Set-Location D:\Code\Codex\codex-skills
pwsh -NoProfile -File .\scripts\bootstrap.ps1
```

`bootstrap.ps1` 会先运行代理预检，再检查前置条件、验证源包、备份已有同名 Skill、安装三个 Skill 并执行 post-install tests。它不会安装缺失项，也不会修改 user PATH。

## 只更新一个 Skill

```powershell
Set-Location D:\Code\Codex\codex-skills
pwsh -NoProfile -File .\scripts\sync-skills.ps1 -Name encoding-guard
```

对应的一句话请求：

> 请从我的 codex-skills 云端源同步最新版 encoding-guard；先自动验证本机代理，只更新该 Skill，验证、备份并在失败时回滚，除此之外不要改变任何配置。

运行结束后重新启动 Codex，确保新的任务重新发现 Skill。详细说明见 [首次安装](docs/installation.md)、[更新](docs/updating.md)和[安全模型](docs/security.md)。

## 开发验证

所有 Python 命令必须在 conda 环境 `codex` 中运行：

```powershell
pwsh -NoProfile -File .\scripts\validate.ps1
```

如果 conda 不在 PATH 且无法从 conda 的 environment registry 自动定位，可显式传入本机的 `condabin\conda.bat` 路径。

用户设备上的脚本绝不会安装软件或 Python package。GitHub Actions 的临时 runner 可以创建隔离的测试环境并安装仓库锁定的测试依赖。
