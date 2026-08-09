[CmdletBinding()]
param(
    [ValidatePattern("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    [string[]]$Name,
    [string]$CondaExe
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$PSStyle.OutputRendering = "PlainText"
$env:PYTHONDONTWRITEBYTECODE = "1"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
. (Join-Path $PSScriptRoot "common.ps1")

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "PowerShell 7 or newer is required"
}
if (-not (Get-Command git.exe -ErrorAction SilentlyContinue)) {
    throw "Git is required for validation; no software was installed"
}

$catalogPath = Join-Path $repoRoot "catalog.json"
$catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
$selectedNames = if ($Name) { @($Name | Select-Object -Unique) } else { @($catalog.skills.name) }
foreach ($selectedName in $selectedNames) {
    if ($selectedName -notin @($catalog.skills.name)) {
        throw "Unknown Skill '$selectedName'"
    }
}

$resolvedConda = Resolve-CondaExecutable $CondaExe
$python = Resolve-CodexPython $resolvedConda
Invoke-CheckedCommand $python -c "import yaml; assert yaml.__version__ == '6.0.3', yaml.__version__"

$prerequisiteArguments = @((Join-Path $PSScriptRoot "check_prerequisites.py"), "--catalog", $catalogPath)
foreach ($selectedName in $selectedNames) {
    $prerequisiteArguments += @("--name", $selectedName)
}
Invoke-CheckedCommand $python @prerequisiteArguments
Invoke-CheckedCommand $python (Join-Path $PSScriptRoot "validate_repository.py")

foreach ($selectedName in $selectedNames) {
    $skillRoot = Join-Path $repoRoot "skills\$selectedName"
    switch ($selectedName) {
        "encoding-guard" {
            $previous = $env:CODEX_TEXT_PYTHON
            try {
                $env:CODEX_TEXT_PYTHON = $python
                Invoke-CheckedCommand $python (Join-Path $skillRoot "scripts\self_test.py")
            } finally {
                if ($null -eq $previous) { Remove-Item Env:CODEX_TEXT_PYTHON -ErrorAction SilentlyContinue } else { $env:CODEX_TEXT_PYTHON = $previous }
            }
        }
        "git-workflow" {
            $temporary = Join-Path ([System.IO.Path]::GetTempPath()) ("git-workflow-validation-" + [System.Guid]::NewGuid().ToString("N"))
            [System.IO.Directory]::CreateDirectory($temporary) | Out-Null
            try {
                Invoke-CheckedCommand $python (Join-Path $skillRoot "scripts\repository_registry.py") --registry (Join-Path $temporary "repositories.json") list
            } finally {
                Remove-Item -LiteralPath $temporary -Recurse -Force
            }
        }
        "python-env" {
            Invoke-CheckedCommand $python -c "import os,sys; assert os.environ.get('CONDA_PREFIX') or 'conda' in sys.version.lower() or sys.prefix != sys.base_prefix"
        }
    }
}

Invoke-CheckedCommand $python -m pip check
& git -C $repoRoot diff --check
if ($LASTEXITCODE -ne 0) {
    throw "Git whitespace validation failed"
}

Write-Output "repository and runtime validation: PASS"
Write-Output "Validated Skills: $($selectedNames -join ', ')"
Write-Output "Conda executable: $resolvedConda"
Write-Output "Codex Python: $python"
