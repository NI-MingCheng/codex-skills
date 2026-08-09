[CmdletBinding(DefaultParameterSetName = "Named")]
param(
    [Parameter(ParameterSetName = "Named", Mandatory = $true)]
    [ValidatePattern("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    [string[]]$Name,

    [Parameter(ParameterSetName = "All", Mandatory = $true)]
    [switch]$All,

    [switch]$CheckOnly,
    [string]$ProxyUrl,
    [string]$CondaExe,
    [string]$CodexHome = $(if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE ".codex" }),
    [string]$ExpectedRepository = "https://github.com/NI-MingCheng/codex-skills.git"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$PSStyle.OutputRendering = "PlainText"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
. (Join-Path $PSScriptRoot "common.ps1")

function Invoke-GitText {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)

    $output = @(& git -C $repoRoot @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Git command failed: git -C $repoRoot $($Arguments -join ' ')`n$($output -join [Environment]::NewLine)"
    }
    return ($output -join "`n").Trim()
}

function ConvertTo-RepositoryIdentity {
    param([Parameter(Mandatory = $true)][string]$Value)

    $candidate = $Value.Trim().TrimEnd("/")
    if ($candidate -match '^git@github\.com:(?<path>[^?#]+?)(?:\.git)?$') {
        $repositoryPath = $Matches.path
        if ($repositoryPath.EndsWith(".git", [System.StringComparison]::OrdinalIgnoreCase)) {
            $repositoryPath = $repositoryPath.Substring(0, $repositoryPath.Length - 4)
        }
        return "github.com/" + $repositoryPath.ToLowerInvariant()
    }
    $uri = $null
    if ([System.Uri]::TryCreate($candidate, [System.UriKind]::Absolute, [ref]$uri) -and $uri.Scheme -in @("https", "ssh")) {
        if ($uri.UserInfo -and $uri.Scheme -eq "https") {
            throw "Repository URL must not contain credentials"
        }
        if ($uri.Host -ieq "github.com") {
            $repositoryPath = $uri.AbsolutePath.Trim("/")
            if ($repositoryPath.EndsWith(".git", [System.StringComparison]::OrdinalIgnoreCase)) {
                $repositoryPath = $repositoryPath.Substring(0, $repositoryPath.Length - 4)
            }
            return "github.com/" + $repositoryPath.ToLowerInvariant()
        }
    }
    if (Test-Path -LiteralPath $candidate) {
        return [System.IO.Path]::GetFullPath($candidate).TrimEnd("\", "/").ToLowerInvariant()
    }
    if ($candidate.EndsWith(".git", [System.StringComparison]::OrdinalIgnoreCase)) {
        $candidate = $candidate.Substring(0, $candidate.Length - 4)
    }
    return $candidate.ToLowerInvariant()
}

function Assert-ExpectedRepository {
    $topLevel = Invoke-GitText rev-parse --show-toplevel
    if ([System.IO.Path]::GetFullPath($topLevel).TrimEnd("\") -ine $repoRoot.TrimEnd("\")) {
        throw "Script is not running from the repository root: $topLevel"
    }
    $origin = Invoke-GitText remote get-url origin
    if ((ConvertTo-RepositoryIdentity $origin) -ne (ConvertTo-RepositoryIdentity $ExpectedRepository)) {
        throw "Unexpected origin. Expected $ExpectedRepository, found $origin"
    }
}

function Assert-CleanWorktree {
    $status = Invoke-GitText status --porcelain
    if ($status) {
        throw "Source worktree is not clean; no update or installation was performed:`n$status"
    }
}

function Assert-ChildPath {
    param(
        [Parameter(Mandatory = $true)][string]$Parent,
        [Parameter(Mandatory = $true)][string]$Child
    )

    $parentFull = [System.IO.Path]::GetFullPath($Parent).TrimEnd("\", "/") + [System.IO.Path]::DirectorySeparatorChar
    $childFull = [System.IO.Path]::GetFullPath($Child)
    if (-not $childFull.StartsWith($parentFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe path outside managed root: $childFull"
    }
    return $childFull
}

function Get-ProtectedState {
    param(
        [Parameter(Mandatory = $true)][string]$ManagedHome,
        [Parameter(Mandatory = $true)][string[]]$SelectedNames
    )

    $records = [System.Collections.Generic.List[string]]::new()
    foreach ($relative in @("AGENTS.md", "config.toml", "hooks.json")) {
        $path = Join-Path $ManagedHome $relative
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $records.Add("file|$relative|$((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash)")
        } else {
            $records.Add("missing|$relative")
        }
    }
    foreach ($relative in @("hooks", "plugins")) {
        $path = Join-Path $ManagedHome $relative
        if (Test-Path -LiteralPath $path -PathType Container) {
            foreach ($item in Get-ChildItem -LiteralPath $path -File -Recurse -Force | Sort-Object FullName) {
                $child = [System.IO.Path]::GetRelativePath($ManagedHome, $item.FullName)
                $records.Add("tree|$child|$($item.Length)|$($item.LastWriteTimeUtc.Ticks)")
            }
        } else {
            $records.Add("missing|$relative/")
        }
    }
    $skillsRoot = Join-Path $ManagedHome "skills"
    if (Test-Path -LiteralPath $skillsRoot -PathType Container) {
        foreach ($directory in Get-ChildItem -LiteralPath $skillsRoot -Directory -Force | Where-Object { $_.Name -notin $SelectedNames } | Sort-Object Name) {
            foreach ($item in Get-ChildItem -LiteralPath $directory.FullName -File -Recurse -Force | Sort-Object FullName) {
                $child = [System.IO.Path]::GetRelativePath($ManagedHome, $item.FullName)
                $records.Add("skill|$child|$($item.Length)|$($item.LastWriteTimeUtc.Ticks)")
            }
        }
    }
    return ($records -join "`n")
}

function Test-InstalledSkill {
    param(
        [Parameter(Mandatory = $true)][string]$SkillName,
        [Parameter(Mandatory = $true)][string]$SkillRoot,
        [Parameter(Mandatory = $true)][string]$Python
    )

    switch ($SkillName) {
        "encoding-guard" {
            $previous = $env:CODEX_TEXT_PYTHON
            try {
                $env:CODEX_TEXT_PYTHON = $Python
                Invoke-CheckedCommand $Python (Join-Path $SkillRoot "scripts\self_test.py")
            } finally {
                if ($null -eq $previous) { Remove-Item Env:CODEX_TEXT_PYTHON -ErrorAction SilentlyContinue } else { $env:CODEX_TEXT_PYTHON = $previous }
            }
        }
        "git-workflow" {
            $temporary = Join-Path ([System.IO.Path]::GetTempPath()) ("git-workflow-test-" + [System.Guid]::NewGuid().ToString("N"))
            [System.IO.Directory]::CreateDirectory($temporary) | Out-Null
            try {
                Invoke-CheckedCommand $Python (Join-Path $SkillRoot "scripts\repository_registry.py") --registry (Join-Path $temporary "repositories.json") list
            } finally {
                Remove-Item -LiteralPath $temporary -Recurse -Force
            }
        }
        "python-env" {
            Invoke-CheckedCommand $Python -c "import sys; assert sys.prefix != sys.base_prefix or 'conda' in sys.version.lower() or bool(__import__('os').environ.get('CONDA_PREFIX'))"
        }
        default { throw "No post-install test is defined for $SkillName" }
    }
}

function Restore-Skill {
    param(
        [Parameter(Mandatory = $true)][string]$Target,
        [string]$Rollback,
        [bool]$Existed
    )

    if (Test-Path -LiteralPath $Target) {
        Remove-Item -LiteralPath $Target -Recurse -Force
    }
    if ($Existed -and $Rollback -and (Test-Path -LiteralPath $Rollback)) {
        Move-Item -LiteralPath $Rollback -Destination $Target
    }
}

$testProxyBypass = $env:CODEX_SKILLS_TEST_BYPASS_PROXY -eq "1"
if ($testProxyBypass) {
    $expectedFull = if (Test-Path -LiteralPath $ExpectedRepository) { [System.IO.Path]::GetFullPath($ExpectedRepository) } else { $null }
    $temporaryBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd("\") + "\"
    $homeFullForTest = [System.IO.Path]::GetFullPath($CodexHome)
    if (-not $expectedFull -or -not $homeFullForTest.StartsWith($temporaryBase, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Proxy bypass is restricted to an isolated local integration test"
    }
    Write-Output "Codex proxy preflight: TEST BYPASS (isolated local repository only)"
} else {
    $preflightArguments = @("-NoProfile", "-File", (Join-Path $PSScriptRoot "proxy-preflight.ps1"), "-CodexHome", $CodexHome)
    if ($ProxyUrl) { $preflightArguments += @("-ProxyUrl", $ProxyUrl) }
    if ($CheckOnly) { $preflightArguments += "-CheckOnly" }
    & (Get-Command pwsh.exe -ErrorAction Stop).Source @preflightArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Proxy preflight failed"
    }
}

if (-not (Get-Command git.exe -ErrorAction SilentlyContinue)) {
    throw "Git is missing. Install it manually and rerun; this repository will not install software."
}
if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "PowerShell 7 or newer is required"
}

Assert-ExpectedRepository
Assert-CleanWorktree
if (-not $CheckOnly) {
    Invoke-GitText pull --ff-only | Write-Output
    Assert-CleanWorktree
}

$catalogPath = Join-Path $repoRoot "catalog.json"
$catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
$records = @($catalog.skills)
$selectedNames = if ($All) { @($records.name) } else { @($Name | Select-Object -Unique) }
foreach ($selectedName in $selectedNames) {
    if ($selectedName -notin @($records.name)) {
        throw "Unknown Skill '$selectedName'. Available: $(@($records.name) -join ', ')"
    }
}

$resolvedConda = Resolve-CondaExecutable $CondaExe
$python = Resolve-CodexPython $resolvedConda
$prerequisiteArguments = @((Join-Path $PSScriptRoot "check_prerequisites.py"), "--catalog", $catalogPath)
foreach ($selectedName in $selectedNames) {
    $prerequisiteArguments += @("--name", $selectedName)
}
Invoke-CheckedCommand $python @prerequisiteArguments

& (Join-Path $PSScriptRoot "validate.ps1") -CondaExe $resolvedConda -Name $selectedNames

if ($CheckOnly) {
    Write-Output "Skill sync check: PASS ($($selectedNames -join ', '))"
    exit 0
}

$codexHomeFull = [System.IO.Path]::GetFullPath($CodexHome)
$skillsRoot = Join-Path $codexHomeFull "skills"
[System.IO.Directory]::CreateDirectory($skillsRoot) | Out-Null
$protectedBefore = Get-ProtectedState $codexHomeFull $selectedNames
$timestamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmssfff")
$backupRoot = Join-Path $codexHomeFull "backups\$timestamp-skills"
$stagingRoot = Join-Path $codexHomeFull ".skill-sync-$([System.Guid]::NewGuid().ToString('N'))"
[System.IO.Directory]::CreateDirectory($stagingRoot) | Out-Null
$installed = [System.Collections.Generic.List[string]]::new()
$deploymentStates = [System.Collections.Generic.List[object]]::new()

try {
    foreach ($selectedName in $selectedNames) {
        $record = $records | Where-Object { $_.name -eq $selectedName } | Select-Object -First 1
        $source = Assert-ChildPath $repoRoot (Join-Path $repoRoot ([string]$record.path))
        $target = Assert-ChildPath $skillsRoot (Join-Path $skillsRoot $selectedName)
        $stage = Assert-ChildPath $stagingRoot (Join-Path $stagingRoot "$selectedName-new")
        $rollback = Assert-ChildPath $stagingRoot (Join-Path $stagingRoot "$selectedName-old")
        $existed = Test-Path -LiteralPath $target
        if ($existed -and ((Get-Item -LiteralPath $target -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing to replace reparse-point Skill directory: $target"
        }

        Copy-Item -LiteralPath $source -Destination $stage -Recurse
        $runtimePointer = Join-Path $stage "scripts\codex-text.runtime"
        if ($selectedName -eq "encoding-guard") {
            Write-Utf8Lf $runtimePointer $python
        } elseif (Test-Path -LiteralPath $runtimePointer) {
            Remove-Item -LiteralPath $runtimePointer -Force
        }

        if ($existed) {
            $backup = Join-Path $backupRoot "skills\$selectedName"
            [System.IO.Directory]::CreateDirectory((Split-Path -Parent $backup)) | Out-Null
            Copy-Item -LiteralPath $target -Destination $backup -Recurse
            Move-Item -LiteralPath $target -Destination $rollback
        }

        $deploymentStates.Add([pscustomobject]@{
            Name = $selectedName
            Target = $target
            Rollback = $rollback
            Existed = $existed
        })
        Move-Item -LiteralPath $stage -Destination $target
        if ($env:CODEX_SKILLS_TEST_FAIL_AFTER_DEPLOY -eq $selectedName) {
            throw "Injected post-deploy failure for rollback testing"
        }
        Test-InstalledSkill $selectedName $target $python
        $installed.Add($selectedName)
    }

    Invoke-CheckedCommand $python -m pip check
    $protectedAfter = Get-ProtectedState $codexHomeFull $selectedNames
    if ($protectedBefore -cne $protectedAfter) {
        throw "Protected Codex configuration changed. config.toml, AGENTS.md, hooks.json, Hooks, Plugins, and unselected Skills must remain untouched."
    }
    foreach ($state in $deploymentStates) {
        if (Test-Path -LiteralPath $state.Rollback) {
            Remove-Item -LiteralPath $state.Rollback -Recurse -Force
        }
    }
} catch {
    $failure = $_.Exception.Message
    $restoreErrors = [System.Collections.Generic.List[string]]::new()
    for ($index = $deploymentStates.Count - 1; $index -ge 0; $index--) {
        $state = $deploymentStates[$index]
        try {
            Restore-Skill -Target $state.Target -Rollback $state.Rollback -Existed $state.Existed
        } catch {
            $restoreErrors.Add("$($state.Name): $($_.Exception.Message)")
        }
    }
    if ($restoreErrors.Count -gt 0) {
        throw "Skill deployment failed and rollback was incomplete. Failure: $failure. Rollback errors: $($restoreErrors -join '; ')"
    }
    throw "Skill deployment failed; every selected Skill was restored to its prior state. Failure: $failure"
} finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}

$head = Invoke-GitText rev-parse HEAD
Write-Output "Skill sync: PASS"
Write-Output "Installed Skills: $($installed -join ', ')"
Write-Output "Source HEAD: $head"
Write-Output "Install root: $skillsRoot"
Write-Output "Backup root: $(if (Test-Path -LiteralPath $backupRoot) { $backupRoot } else { '<none; no prior Skill existed>' })"
Write-Output "Protected config unchanged: True"
Write-Output "No software, environment, package, PATH, Hook, config.toml, or Plugin changes were made."
