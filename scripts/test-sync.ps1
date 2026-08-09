[CmdletBinding()]
param([string]$CondaExe)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$PSStyle.OutputRendering = "PlainText"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
. (Join-Path $PSScriptRoot "common.ps1")
$resolvedConda = Resolve-CondaExecutable $CondaExe
$temporaryBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd("\") + "\"
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("codex-skills-sync-test-" + [System.Guid]::NewGuid().ToString("N"))
$testRoot = [System.IO.Path]::GetFullPath($testRoot)
if (-not $testRoot.StartsWith($temporaryBase, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe temporary test path: $testRoot"
}

function Invoke-TestGit {
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments
    )
    $output = @(& git -C $Directory @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "Test Git command failed: $($Arguments -join ' ')`n$($output -join [Environment]::NewLine)"
    }
    return $output
}

function Write-TestFile {
    param([string]$Path, [string]$Content)
    [System.IO.Directory]::CreateDirectory((Split-Path -Parent $Path)) | Out-Null
    [System.IO.File]::WriteAllText($Path, $Content.Replace("`r`n", "`n"), [System.Text.UTF8Encoding]::new($false))
}

[System.IO.Directory]::CreateDirectory($testRoot) | Out-Null
$previousBypass = $env:CODEX_SKILLS_TEST_BYPASS_PROXY
$previousFailure = $env:CODEX_SKILLS_TEST_FAIL_AFTER_DEPLOY
try {
    $seed = Join-Path $testRoot "seed"
    $remote = Join-Path $testRoot "remote.git"
    $source = Join-Path $testRoot "source"
    $codexHome = Join-Path $testRoot "codex-home"
    [System.IO.Directory]::CreateDirectory($seed) | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $repoRoot -Force | Where-Object { $_.Name -ne ".git" }) {
        Copy-Item -LiteralPath $item.FullName -Destination $seed -Recurse
    }
    & git -C $seed init -b main | Out-Null
    Invoke-TestGit $seed config user.name "Codex Skills Test" | Out-Null
    Invoke-TestGit $seed config user.email "codex-skills-test@invalid.example" | Out-Null
    Invoke-TestGit $seed config commit.gpgsign false | Out-Null
    Invoke-TestGit $seed add --all | Out-Null
    Invoke-TestGit $seed commit -m "test: seed repository" | Out-Null
    & git init --bare $remote | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not create temporary bare repository" }
    Invoke-TestGit $seed remote add origin $remote | Out-Null
    Invoke-TestGit $seed push -u origin main | Out-Null
    & git --git-dir $remote symbolic-ref HEAD refs/heads/main
    if ($LASTEXITCODE -ne 0) { throw "Could not set temporary remote HEAD" }
    & git clone $remote $source | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not clone temporary test repository" }

    Write-TestFile (Join-Path $codexHome "AGENTS.md") "protected agents`n"
    Write-TestFile (Join-Path $codexHome "config.toml") "model = 'protected'`n"
    Write-TestFile (Join-Path $codexHome "hooks.json") "{}`n"
    Write-TestFile (Join-Path $codexHome "hooks\sentinel.txt") "protected hook`n"
    Write-TestFile (Join-Path $codexHome "plugins\sentinel.txt") "protected Plugin`n"
    Write-TestFile (Join-Path $codexHome "skills\git-workflow\sentinel.txt") "other Skill`n"
    Write-TestFile (Join-Path $codexHome "skills\encoding-guard\sentinel.txt") "old Encoding Guard`n"

    $protectedPaths = @(
        (Join-Path $codexHome "AGENTS.md"),
        (Join-Path $codexHome "config.toml"),
        (Join-Path $codexHome "hooks.json"),
        (Join-Path $codexHome "hooks\sentinel.txt"),
        (Join-Path $codexHome "plugins\sentinel.txt"),
        (Join-Path $codexHome "skills\git-workflow\sentinel.txt")
    )
    $before = @($protectedPaths | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }) -join "|"

    $env:CODEX_SKILLS_TEST_BYPASS_PROXY = "1"
    & (Join-Path $source "scripts\sync-skills.ps1") -Name encoding-guard -CodexHome $codexHome -CondaExe $resolvedConda -ExpectedRepository $remote
    if ($LASTEXITCODE -ne 0) { throw "Named Skill installation test failed" }
    if (-not (Test-Path -LiteralPath (Join-Path $codexHome "skills\encoding-guard\SKILL.md"))) {
        throw "encoding-guard was not installed"
    }
    $after = @($protectedPaths | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }) -join "|"
    if ($before -cne $after) { throw "Named sync modified protected configuration" }
    $installedHash = (Get-FileHash -LiteralPath (Join-Path $codexHome "skills\encoding-guard\SKILL.md") -Algorithm SHA256).Hash

    $skillFile = Join-Path $seed "skills\encoding-guard\SKILL.md"
    [System.IO.File]::AppendAllText($skillFile, "`n<!-- rollback test revision -->`n", [System.Text.UTF8Encoding]::new($false))
    Invoke-TestGit $seed add skills/encoding-guard/SKILL.md | Out-Null
    Invoke-TestGit $seed commit -m "test: update encoding guard" | Out-Null
    Invoke-TestGit $seed push origin main | Out-Null

    $env:CODEX_SKILLS_TEST_FAIL_AFTER_DEPLOY = "encoding-guard"
    $failed = $false
    $failureMessage = ""
    try {
        & (Join-Path $source "scripts\sync-skills.ps1") -Name encoding-guard -CodexHome $codexHome -CondaExe $resolvedConda -ExpectedRepository $remote
        if ($LASTEXITCODE -ne 0) { $failed = $true; $failureMessage = "exit code $LASTEXITCODE" }
    } catch {
        $failed = $true
        $failureMessage = $_.Exception.Message
    }
    if (-not $failed) { throw "Injected failure did not fail the sync" }
    if ($failureMessage -notmatch "Injected post-deploy failure") {
        throw "Sync failed before the post-deploy rollback checkpoint: $failureMessage"
    }
    $restoredHash = (Get-FileHash -LiteralPath (Join-Path $codexHome "skills\encoding-guard\SKILL.md") -Algorithm SHA256).Hash
    if ($restoredHash -ne $installedHash) { throw "Rollback did not restore the previous Skill" }
    $afterRollback = @($protectedPaths | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }) -join "|"
    if ($before -cne $afterRollback) { throw "Rollback test modified protected configuration" }

    Remove-Item Env:CODEX_SKILLS_TEST_FAIL_AFTER_DEPLOY -ErrorAction SilentlyContinue
    $coreProtectedPaths = $protectedPaths[0..4]
    $coreBeforeAll = @($coreProtectedPaths | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }) -join "|"
    & (Join-Path $source "scripts\sync-skills.ps1") -All -CodexHome $codexHome -CondaExe $resolvedConda -ExpectedRepository $remote
    foreach ($skillName in @("encoding-guard", "git-workflow", "python-env")) {
        if (-not (Test-Path -LiteralPath (Join-Path $codexHome "skills\$skillName\SKILL.md"))) {
            throw "Full sync did not install $skillName"
        }
    }
    $runtimePointer = Join-Path $codexHome "skills\encoding-guard\scripts\codex-text.runtime"
    if (-not (Test-Path -LiteralPath $runtimePointer)) { throw "Encoding Guard runtime pointer is missing" }
    $coreAfterAll = @($coreProtectedPaths | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }) -join "|"
    if ($coreBeforeAll -cne $coreAfterAll) { throw "Full sync modified protected configuration" }

    Write-Output "sync integration test: PASS"
    Write-Output "Named scope, full install, backup, protected config, post-install verification, and rollback passed."
} finally {
    if ($null -eq $previousBypass) { Remove-Item Env:CODEX_SKILLS_TEST_BYPASS_PROXY -ErrorAction SilentlyContinue } else { $env:CODEX_SKILLS_TEST_BYPASS_PROXY = $previousBypass }
    if ($null -eq $previousFailure) { Remove-Item Env:CODEX_SKILLS_TEST_FAIL_AFTER_DEPLOY -ErrorAction SilentlyContinue } else { $env:CODEX_SKILLS_TEST_FAIL_AFTER_DEPLOY = $previousFailure }
    if (Test-Path -LiteralPath $testRoot) {
        $resolvedTestRoot = [System.IO.Path]::GetFullPath($testRoot)
        if (-not $resolvedTestRoot.StartsWith($temporaryBase, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove unsafe test path: $resolvedTestRoot"
        }
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
