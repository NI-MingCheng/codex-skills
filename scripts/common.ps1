Set-StrictMode -Version Latest

function Resolve-CondaExecutable {
    param([string]$CondaExe)

    if ($CondaExe) {
        $resolved = [System.IO.Path]::GetFullPath($CondaExe)
        if (-not [System.IO.File]::Exists($resolved)) {
            throw "Specified conda executable does not exist: $resolved"
        }
        return $resolved
    }

    foreach ($value in @($env:CODEX_CONDA_EXE, $env:CONDA_EXE)) {
        if ($value -and [System.IO.File]::Exists($value)) {
            return [System.IO.Path]::GetFullPath($value)
        }
    }

    foreach ($commandName in @("conda.exe", "conda.bat", "conda")) {
        $command = Get-Command $commandName -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($command -and $command.Source -and [System.IO.File]::Exists($command.Source)) {
            return [System.IO.Path]::GetFullPath($command.Source)
        }
    }

    $candidates = [System.Collections.Generic.List[string]]::new()
    $environmentRegistry = Join-Path $env:USERPROFILE ".conda\environments.txt"
    if (Test-Path -LiteralPath $environmentRegistry) {
        foreach ($line in Get-Content -LiteralPath $environmentRegistry) {
            $environmentPath = $line.Trim()
            if (-not $environmentPath) {
                continue
            }
            $possibleRoots = @($environmentPath)
            if ((Split-Path -Leaf $environmentPath) -ieq "codex" -and (Split-Path -Leaf (Split-Path -Parent $environmentPath)) -ieq "envs") {
                $possibleRoots += Split-Path -Parent (Split-Path -Parent $environmentPath)
            }
            foreach ($root in $possibleRoots) {
                foreach ($relative in @("condabin\conda.bat", "Scripts\conda.exe", "bin/conda")) {
                    $candidate = Join-Path $root $relative
                    if ([System.IO.File]::Exists($candidate)) {
                        $full = [System.IO.Path]::GetFullPath($candidate)
                        if (-not $candidates.Contains($full)) {
                            $candidates.Add($full)
                        }
                    }
                }
            }
        }
    }

    foreach ($root in @(
        (Join-Path $env:USERPROFILE "miniconda3"),
        (Join-Path $env:USERPROFILE "anaconda3"),
        (Join-Path $env:LOCALAPPDATA "miniconda3"),
        (Join-Path $env:LOCALAPPDATA "anaconda3"),
        "C:\ProgramData\miniconda3",
        "C:\ProgramData\anaconda3"
    )) {
        foreach ($relative in @("condabin\conda.bat", "Scripts\conda.exe")) {
            $candidate = Join-Path $root $relative
            if ([System.IO.File]::Exists($candidate)) {
                $full = [System.IO.Path]::GetFullPath($candidate)
                if (-not $candidates.Contains($full)) {
                    $candidates.Add($full)
                }
            }
        }
    }

    if ($candidates.Count -eq 1) {
        return $candidates[0]
    }
    if ($candidates.Count -gt 1) {
        throw "Multiple conda installations were discovered; specify -CondaExe or CODEX_CONDA_EXE"
    }
    throw "Conda was not found. Install or expose conda manually, then rerun; this repository will not install it."
}

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments
    )

    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $Executable $($Arguments -join ' ')"
    }
}

function Resolve-CodexPython {
    param([Parameter(Mandatory = $true)][string]$ResolvedCondaExe)

    $output = @(& $ResolvedCondaExe run -n codex python -c "import sys; print(sys.executable)" 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Conda environment 'codex' is missing or unusable. Create or repair it manually; no environment was created.`n$($output -join [Environment]::NewLine)"
    }
    $candidate = $output | Where-Object { $_ -and [System.IO.File]::Exists([string]$_) } | Select-Object -Last 1
    if (-not $candidate) {
        throw "Could not resolve Python from conda environment 'codex'"
    }
    return [System.IO.Path]::GetFullPath([string]$candidate)
}

function Write-Utf8Lf {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )

    $normalized = $Content.Replace("`r`n", "`n").Replace("`r", "`n")
    if (-not $normalized.EndsWith("`n")) {
        $normalized += "`n"
    }
    [System.IO.File]::WriteAllText($Path, $normalized, [System.Text.UTF8Encoding]::new($false))
}
