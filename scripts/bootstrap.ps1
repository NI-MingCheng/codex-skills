[CmdletBinding()]
param(
    [string]$ProxyUrl,
    [string]$CondaExe,
    [string]$CodexHome = $(if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE ".codex" })
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$PSStyle.OutputRendering = "PlainText"

$arguments = @("-NoProfile", "-File", (Join-Path $PSScriptRoot "sync-skills.ps1"), "-All", "-CodexHome", $CodexHome)
if ($ProxyUrl) { $arguments += @("-ProxyUrl", $ProxyUrl) }
if ($CondaExe) { $arguments += @("-CondaExe", $CondaExe) }

& (Get-Command pwsh.exe -ErrorAction Stop).Source @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Codex Skills bootstrap failed"
}

Write-Output "Codex Skills bootstrap: PASS"
Write-Output "Restart Codex and create a new task so the installed Skills are rediscovered."
