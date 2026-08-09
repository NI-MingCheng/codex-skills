# 更新 Skills

同步一个 Skill：

```powershell
pwsh -NoProfile -File .\scripts\sync-skills.ps1 -Name encoding-guard
```

同步多个 Skill：

```powershell
pwsh -NoProfile -File .\scripts\sync-skills.ps1 -Name encoding-guard,git-workflow
```

同步全部目录清单中的 Skill：

```powershell
pwsh -NoProfile -File .\scripts\sync-skills.ps1 -All
```

只检查、不 pull、不安装：

```powershell
pwsh -NoProfile -File .\scripts\sync-skills.ps1 -Name encoding-guard -CheckOnly
```

正常同步固定执行以下顺序：代理预检、origin 校验、clean worktree 校验、`git pull --ff-only`、源验证、前置检查、备份、原子替换、post-install test。

`-Name` 是严格作用域。除指定 Skill 外，脚本不会改动其他 Skill、全局 `AGENTS.md`、Hooks、`config.toml`、Plugin 或 user PATH。备份默认位于 `%USERPROFILE%\.codex\backups\<timestamp>-skills\`。

更新后重新启动 Codex 并在新任务中确认 Skill 已重新发现。
