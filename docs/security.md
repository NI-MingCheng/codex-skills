# 安全模型

## 公开边界

仓库只包含公开可审计的 Skill 内容、固定版本依赖声明和安装工具。禁止提交 token、SSH key、GitHub auth、sessions、memories、logs、repository registry、实际 proxy endpoint、设备绝对路径及任何 Plugin 状态。

`codex-text.runtime` 是安装器在目标设备生成的本机文件，已被 `.gitignore` 排除。

## 网络边界

- 只自动接受无 credentials 的 loopback HTTP/mixed proxy。
- 候选必须实际通过 GitHub、ChatGPT 和 OpenAI HTTPS 请求。
- 多个来源冲突或多个自动发现候选同时可用时停止。
- 不安装、启动、停止或重配任何代理软件。
- 不向 `config.toml` 写入代理设置。

## 安装边界

- 不安装 Git、PowerShell、conda、Python、conda environment、Python package 或 Plugin。
- 不修改 PATH、全局 `AGENTS.md`、Hooks 或 `config.toml`。
- 已有同名 Skill 在替换前完整备份。
- deployment 和 post-install verification 是一个事务；失败即恢复。
- 源仓库必须具有预期 origin、clean worktree，并且只能 fast-forward 更新。

## 供应链

公开仓库的首次读取不需要 GitHub 身份验证；push 仍需要仓库写权限。发布前执行 repository validation、Skill 格式验证、runtime tests、敏感内容扫描和 Git whitespace 检查。
