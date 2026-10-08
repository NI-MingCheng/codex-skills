param([string] $DotnetPath = 'dotnet')
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 is required.' }
$sdkTool = (Get-Command $DotnetPath -CommandType Application).Source
Get-Item -LiteralPath $sdkTool | Out-Null
if ((& $sdkTool --version) -notmatch '^10\.') { throw 'Use the .NET 10 SDK.' }
$toolRoot = (Get-Item -LiteralPath $PSScriptRoot).FullName
$guiProject = Join-Path $toolRoot 'sources/EncodingChecker/EncodingChecker.csproj'
$cliProject = Join-Path $toolRoot 'sources/EncodingChecker.Agent/EncodingChecker.Agent.csproj'
Get-Item -LiteralPath $guiProject,$cliProject | Out-Null
$guiStage = Join-Path $toolRoot 'sources/EncodingChecker/bin/Release/package-gui'
$cliStage = Join-Path $toolRoot 'sources/EncodingChecker.Agent/bin/Release/package-cli'
$common = @('-c','Release','-r','win-x64','-p:SelfContained=false','-p:PublishSingleFile=true','-p:DebugType=none','-p:DebugSymbols=false')
# Separate outputs prevent a ProjectReference apphost copy from replacing a bundle.
& $sdkTool publish $guiProject @common --output $guiStage --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw 'GUI publish failed.' }
& $sdkTool publish $cliProject @common --output $cliStage --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw 'Agent publish failed.' }
$app = Join-Path $toolRoot 'App'
Test-Path -LiteralPath $app | Out-Null
New-Item -ItemType Directory -Path $app -Force | Out-Null
foreach ($item in @(@{Directory=$guiStage;Name='EncodingChecker.exe'},@{Directory=$cliStage;Name='EncodingChecker.Cli.exe'})) {
    $source = Join-Path $item.Directory $item.Name
    $target = Join-Path $app $item.Name
    Get-Item -LiteralPath $source | Out-Null
    Test-Path -LiteralPath $target | Out-Null
    Copy-Item -LiteralPath $source -Destination $target -Force
    $start = [Diagnostics.ProcessStartInfo]::new($target)
    $start.ArgumentList.Add('--version')
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.CreateNoWindow = $true
    $process = [Diagnostics.Process]::Start($start)
    try {
        $version = $process.StandardOutput.ReadToEnd()
        $errors = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0 -or -not $version.Trim()) { throw ('Published program failed: ' + $errors) }
        [pscustomobject]@{File=$item.Name;SHA256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash;Version=$version.Trim()}
    } finally { $process.Dispose() }
}
