# 首次安装

## 前置条件

- Windows PowerShell 7 与 Git 已安装。
- 本机已有可用的 credential-free loopback HTTP 或 mixed proxy。
- conda 已安装，且环境 `codex` 已存在。
- 所选 Skill 在 `catalog.json` 声明的 Python package 已存在且版本匹配。

恢复配置不构成软件安装授权。脚本只读检查这些条件；缺失时停止并给出精确报告。

## 代理 bootstrap

仓库无法在完全离线且尚未配置代理时自行下载自身，因此第一次 clone 前需要把 README 中的“一句话安装”交给 Codex。Codex 先用本机信息完成代理预检，再通过公开 HTTPS 地址 clone。

clone 后，`scripts/bootstrap.ps1` 再次执行相同的确定性检查，并在写入 user environment 前把旧值保存到：

```text
%USERPROFILE%\.codex\backups\<timestamp>-proxy\
```

脚本会设置 `CODEX_PROXY_URL`、`HTTP_PROXY`、`HTTPS_PROXY`、`ALL_PROXY` 和 loopback-safe `NO_PROXY`。如果值发生变化，需要重新启动 Codex。

## 安装目标

默认安装到：

```text
%CODEX_HOME%\skills\<name>
```

未设置 `CODEX_HOME` 时使用 `%USERPROFILE%\.codex\skills\<name>`。可以用 `-CodexHome` 为隔离测试指定其他根目录。

安装过程先验证全部源 Skill，再逐个 staging、备份和替换。任何 post-install test 失败都会恢复该 Skill 的安装前状态。

## Conda 发现

脚本依次检查 `-CondaExe`、`CODEX_CONDA_EXE`、`CONDA_EXE`、PATH、Windows uninstall registry 和常见用户级安装目录。无法唯一定位时停止；它不会扫描磁盘、修改 `.condarc`、创建环境或安装 package。
