param([string]$CliPath = (Join-Path $PSScriptRoot '..\..\App\EncodingChecker.Cli.exe'))
$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding($false, $true)
$cli = (Get-Item -LiteralPath $CliPath).FullName
$testParent = Join-Path $PSScriptRoot 'test-work'
if (!(Test-Path -LiteralPath $testParent)) { New-Item -ItemType Directory -Path $testParent | Out-Null }
$work = Join-Path $testParent ([Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $work) { throw 'Fresh test path unexpectedly exists.' }
New-Item -ItemType Directory -Path $work | Out-Null
$script:checks = 0
$script:junction = $null
function Assert($Condition, [string]$Message) {
    if (!$Condition) { throw "FAIL: $Message" }
    $script:checks++
}
function Bytes([string]$Path, [byte[]]$Data) { [IO.File]::WriteAllBytes($Path, $Data) }
function JsonFile([string]$Path, $Value) { Bytes $Path ($utf8.GetBytes(($Value | ConvertTo-Json -Depth 30 -Compress) + "`n")) }
function Run([string[]]$Arguments, [int]$Expected) {
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = $cli
    $psi.Arguments = (($Arguments | ForEach-Object {
        if ($_.Contains('"')) { throw 'Test command quoting does not permit literal quotes.' }
        '"' + ($_ -replace '(\\+)$', '$1$1') + '"'
    }) -join ' ')
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardErrorEncoding = $utf8
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $psi
    if (!$process.Start()) { throw 'CLI failed to start.' }
    $buffer = New-Object IO.MemoryStream
    $process.StandardOutput.BaseStream.CopyTo($buffer)
    $diagnostics = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    $output = $buffer.ToArray()
    Assert ($process.ExitCode -eq $Expected) ("exit {0}, expected {1}: {2}; stderr: {3}" -f $process.ExitCode, $Expected, ($Arguments -join ' '), $diagnostics)
    Assert (!($output.Length -ge 3 -and $output[0] -eq 239 -and $output[1] -eq 187 -and $output[2] -eq 191)) 'stdout must not have UTF-8 BOM'
    $value = $utf8.GetString($output) | ConvertFrom-Json
    $process.Dispose()
    $buffer.Dispose()
    return $value
}
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

try {
    $version = Run @('--version') 0
    Assert ($version.schema_version -eq 1) 'version schema'
    $null = Run @('--help') 0
    foreach ($command in @('probe','scan','confirm','convert','rollback')) { Assert ((Run @($command,'--help') 0).command -eq $command) "subcommand help $command" }
    $null = Run @('invalid-command','--help') 1
    $null = Run @('probe', '--nonsense', 'x') 1
    $probe = Join-Path $work 'probe.txt'
    Bytes $probe ([byte[]]@(194, 169))
    $ambiguous = Run @('probe', '--path', $probe) 2
    Assert ($ambiguous.status -eq 'ambiguous' -and $null -eq $ambiguous.encoding) 'UTF-8 / GBK differing text stays ambiguous'
    Assert ($ambiguous.candidates.encoding -contains 'utf-8' -and $ambiguous.candidates.encoding -contains 'gbk') 'strict dual candidates'
    Assert (($ambiguous.candidates | Where-Object encoding -eq 'utf-8').preview -ne ($ambiguous.candidates | Where-Object encoding -eq 'gbk').preview) 'human previews differ'
    $explicit = Run @('probe', '--path', $probe, '--encoding', 'cp936') 0
    Assert ($explicit.encoding -eq 'gbk' -and $explicit.status -eq 'confirmed') 'CP936 canonical confirmation'
    $null = Run @('probe', '--path', $probe, '--encoding', 'utf-7') 1
    Bytes $probe ([byte[]]@(214, 208, 206, 196))
    $compatible = Run @('probe', '--path', $probe) 2
    Assert ($compatible.status -eq 'needs-confirmation' -and $null -eq $compatible.encoding) 'GBK / GB18030 does not claim uniqueness'
    Assert ($compatible.candidates.encoding -contains 'gbk' -and $compatible.candidates.encoding -contains 'gb18030') 'GBK / GB18030 compatible candidates'
    Bytes $probe ([byte[]]@(226, 130, 172))
    $uniqueUtf8 = Run @('probe', '--path', $probe) 2
    Assert ($uniqueUtf8.status -eq 'needs-confirmation' -and $uniqueUtf8.suggested_encoding -eq 'utf-8') 'UTF-8 only is still not confirmed'
    Bytes $probe ($utf8.GetBytes("ascii`r`nline`nlast`r"))
    $ascii = Run @('probe', '--path', $probe) 0
    Assert ($ascii.status -eq 'ascii-compatible' -and $ascii.encoding -eq 'ascii' -and $ascii.eol.kind -eq 'mixed') 'ASCII compatibility and mixed EOL'
    Bytes $probe ([byte[]]@())
    $empty = Run @('probe', '--path', $probe) 0
    Assert ($empty.status -eq 'ascii-compatible' -and $empty.bytes -eq 0) 'empty file'
    $repairRoot = Join-Path $work 'repair fallback error'
    New-Item -ItemType Directory -Path $repairRoot | Out-Null
    $repairSource = Join-Path $repairRoot 'utf8-exception.txt'
    Bytes $repairSource ([byte[]]@(226,130,172))
    $repairSnapshot = Join-Path $repairRoot 'docs\snapshot.json'
    $repair = Run @('scan','--root',$repairRoot,'--output',$repairSnapshot,'--default-encoding','gbk') 1
    $repairEntry = $repair.files | Where-Object path -eq 'utf8-exception.txt'
    Assert ($repairEntry.status -eq 'error' -and $repairEntry.sha256 -eq (Hash $repairSource) -and $repairEntry.bytes -eq 3) 'decode error preserves byte hash/count'
    $repaired = Run @('confirm','--snapshot',$repairSnapshot,'--path','utf8-exception.txt','--encoding','utf-8') 0
    Assert ($repaired.file.status -eq 'confirmed' -and $repaired.file.encoding -eq 'utf-8') 'explicit confirmation repairs fallback decode error without rescan'
    $missingRoot = Join-Path $work 'io error'
    New-Item -ItemType Directory -Path $missingRoot | Out-Null
    $null = Run @('probe','--path',(Join-Path $missingRoot 'missing.txt')) 1
    $wideText = 'wide ' + [char]0x4E2D + "`r`nline"
    foreach ($code in @('utf-8','utf-16-le','utf-16-be','utf-32-le','utf-32-be')) {
        switch ($code) {
            'utf-8' { $codec = New-Object Text.UTF8Encoding($true, $true) }
            'utf-16-le' { $codec = New-Object Text.UnicodeEncoding($false, $true, $true) }
            'utf-16-be' { $codec = New-Object Text.UnicodeEncoding($true, $true, $true) }
            'utf-32-le' { $codec = New-Object Text.UTF32Encoding($false, $true, $true) }
            'utf-32-be' { $codec = New-Object Text.UTF32Encoding($true, $true, $true) }
        }
        Bytes $probe ($codec.GetPreamble() + $codec.GetBytes($wideText))
        $bom = Run @('probe','--path',$probe) 0
        Assert ($bom.status -eq 'confirmed' -and $bom.encoding -eq $code -and $bom.bom -eq $code) "BOM $code"
    }
    Bytes $probe ([byte[]]@(239,187,191,255))
    $null = Run @('probe','--path',$probe) 1
    Bytes $probe ([byte[]]@(255,254,0,216))
    $null = Run @('probe','--path',$probe) 1
    $wide = New-Object Text.UnicodeEncoding($false,$false,$true)
    Bytes $probe ($wide.GetBytes("no bom wide ascii`r`n"))
    $structural = Run @('probe','--path',$probe) 2
    Assert ($structural.status -eq 'needs-confirmation' -and $null -eq $structural.encoding) 'BOMless UTF-16 structure needs confirmation'
    $wideExplicit = Run @('probe','--path',$probe,'--encoding','utf-16-le') 0
    Assert ($wideExplicit.status -eq 'confirmed') 'explicit UTF-16 takes priority over NUL'
    Bytes $probe ([byte[]]@(137,80,78,71,13,10,26,10,0))
    Assert ((Run @('probe','--path',$probe) 0).status -eq 'binary') 'binary signature'
    $binary = Join-Path $work 'binary.exe'
    Bytes $binary ($utf8.GetBytes('looks like text'))
    Assert ((Run @('probe','--path',$binary) 0).status -eq 'binary') 'binary extension'

    $common = Join-Path $work 'explicit common codecs'
    New-Item -ItemType Directory -Path $common | Out-Null
    $commonPolicy = @{files=@{}}
    $commonOriginals = @{}
    $commonTexts = @{}
    foreach ($case in @(
        @{name='windows-1252';alias='cp1252';page=1252;text=([string][char]0x201C)+'Euro '+[char]0x20AC+[char]0x201D+"`r`n"},
        @{name='big5';alias='cp950';page=950;text=([string][char]0x4E2D)+[char]0x6587+"`nlast"},
        @{name='shift-jis';alias='Shift_JIS';page=932;text=([string][char]0x65E5)+[char]0x672C+[char]0x8A9E+"`r`nlast"}
    )) {
        $codec = [Text.Encoding]::GetEncoding($case.page,[Text.EncoderFallback]::ExceptionFallback,[Text.DecoderFallback]::ExceptionFallback)
        $name = $case.name + '.txt'
        $file = Join-Path $common $name
        $commonOriginals[$name] = $codec.GetBytes($case.text)
        $commonTexts[$name] = $case.text
        $commonPolicy.files[$name] = $case.name
        Bytes $file $commonOriginals[$name]
        $selected = Run @('probe','--path',$file,'--encoding',$case.alias) 0
        Assert ($selected.status -eq 'confirmed' -and $selected.encoding -eq $case.name) "strict explicit codec $($case.name)"
    }
    $commonPolicyPath = Join-Path $common 'policy.json'
    JsonFile $commonPolicyPath $commonPolicy
    $commonSnapshot = Join-Path $common 'docs\encoding-snapshot.json'
    $null = Run @('scan','--root',$common,'--policy',$commonPolicyPath,'--output',$commonSnapshot) 0
    $commonApplied = Run @('convert','--snapshot',$commonSnapshot,'--apply') 0
    foreach ($name in $commonTexts.Keys) { Assert ($utf8.GetString([IO.File]::ReadAllBytes((Join-Path $common $name))) -eq $commonTexts[$name]) "common codec Unicode preserved $name" }
    $null = Run @('rollback','--log',$commonApplied.log,'--apply') 0
    foreach ($name in $commonOriginals.Keys) { Assert ((Hash (Join-Path $common $name)) -eq (([Security.Cryptography.SHA256]::Create().ComputeHash($commonOriginals[$name]) | ForEach-Object ToString x2) -join '')) "common codec exact rollback $name" }

    $supplementary = ('a' * 119) + [char]0xD83D + [char]0xDE00 + 'tail'
    Bytes $probe ($utf8.GetBytes($supplementary))
    $previewResult = Run @('probe','--path',$probe) 2
    Assert (!((($previewResult.candidates | Where-Object encoding -eq 'utf-8').preview).EndsWith([string][char]0xD83D))) 'preview preserves surrogate boundary'

    $flow = Join-Path $work 'flow with spaces'
    New-Item -ItemType Directory -Path $flow | Out-Null
    $source = Join-Path $flow 'source.txt'
    Bytes $source ([byte[]]@(194,169))
    $snapshotPath = Join-Path $flow 'snapshot.json'
    $null = Run @('scan','--root',$flow,'--output',$snapshotPath) 2
    $initial = Hash $source
    $refused = Run @('convert','--snapshot',$snapshotPath,'--apply') 2
    Assert ($refused.blocked.Count -eq 1 -and (Hash $source) -eq $initial -and !(Test-Path -LiteralPath $refused.backup_dir)) 'whole batch refusal creates no backup and changes no source'
    $null = Run @('confirm','--snapshot',$snapshotPath,'--path','../outside.txt','--encoding','utf-8') 1
    $null = Run @('confirm','--snapshot',$snapshotPath,'--path','source.txt','--encoding','utf-8','--reason','test-selected') 0
    Assert ((Hash $source) -eq $initial) 'confirm leaves source untouched'
    Bytes $source ($utf8.GetBytes('changed after scan'))
    $null = Run @('convert','--snapshot',$snapshotPath,'--apply') 1
    $null = Run @('confirm','--snapshot',$snapshotPath,'--path','source.txt','--encoding','utf-8') 1
    Bytes $source ([byte[]]@(194,169))
    $malicious = [IO.File]::ReadAllText($snapshotPath,$utf8) | ConvertFrom-Json
    $malicious.files[0].path = '../escape.txt'
    $escapeSnapshot = Join-Path $work 'escape-snapshot.json'
    JsonFile $escapeSnapshot $malicious
    $null = Run @('convert','--snapshot',$escapeSnapshot,'--apply') 1

    $flow2 = Join-Path $work 'transaction'
    New-Item -ItemType Directory -Path $flow2 | Out-Null
    $text = ([string][char]0x4E2D) + [char]0x6587 + "`r`nsecond`nthird`rfinal"
    $gbk = [Text.Encoding]::GetEncoding(936, [Text.EncoderFallback]::ExceptionFallback, [Text.DecoderFallback]::ExceptionFallback)
    Bytes (Join-Path $flow2 'gbk.txt') ($gbk.GetBytes($text))
    $bom8 = New-Object Text.UTF8Encoding($true,$true)
    Bytes (Join-Path $flow2 'utf8-bom.txt') ($bom8.GetPreamble() + $bom8.GetBytes($text + "`r`n"))
    $codecs = @((New-Object Text.UnicodeEncoding($false,$true,$true)),(New-Object Text.UnicodeEncoding($true,$true,$true)),(New-Object Text.UTF32Encoding($false,$true,$true)),(New-Object Text.UTF32Encoding($true,$true,$true)))
    $number = 0
    foreach ($codec in $codecs) { Bytes (Join-Path $flow2 ("wide{0}.txt" -f $number)) ($codec.GetPreamble()+$codec.GetBytes($text)); $number++ }
    Bytes (Join-Path $flow2 'ascii.txt') ($utf8.GetBytes("ascii`r`n"))
    Bytes (Join-Path $flow2 'already-utf8.txt') ($utf8.GetBytes($text))
    Bytes (Join-Path $flow2 'sig.dat') ([byte[]]@(80,75,3,4,0,1,2))
    $policyPath = Join-Path $flow2 'policy.json'
    JsonFile $policyPath @{default_encoding='gbk';files=@{'ascii.txt'='ascii';'already-utf8.txt'='utf-8'}}
    $snap2 = Join-Path $flow2 'snapshot.json'
    $scanned = Run @('scan','--root',$flow2,'--policy',$policyPath,'--output',$snap2) 0
    Assert (($scanned.files | Where-Object path -eq 'policy.json').status -eq 'excluded') 'policy metadata excluded'
    $null = Run @('scan','--root',$flow2,'--policy',$policyPath,'--output',$snap2) 0
    $snapObj = [IO.File]::ReadAllText($snap2,$utf8) | ConvertFrom-Json
    Assert (($snapObj.files | Where-Object path -eq 'snapshot.json').status -eq 'excluded') 'snapshot metadata excluded'
    $originals = @{}
    Get-ChildItem -LiteralPath $flow2 -File | ForEach-Object { $originals[$_.Name] = [IO.File]::ReadAllBytes($_.FullName) }
    $dry = Run @('convert','--snapshot',$snap2) 0
    Assert (!$dry.apply -and !(Test-Path -LiteralPath $dry.backup_dir)) 'dry run creates no backups'
    foreach ($name in $originals.Keys) { Assert ((Hash (Join-Path $flow2 $name)) -eq (([Security.Cryptography.SHA256]::Create().ComputeHash($originals[$name]) | ForEach-Object ToString x2) -join '')) "dry-run preserves $name" }
    $readonlyPath = Join-Path $flow2 'gbk.txt'
    [IO.File]::SetAttributes($readonlyPath,[IO.FileAttributes]::ReadOnly)
    $null = Run @('convert','--snapshot',$snap2,'--apply') 1
    Assert (([IO.File]::GetAttributes($readonlyPath) -band [IO.FileAttributes]::ReadOnly) -ne 0) 'read-only attribute preserved on refusal'
    [IO.File]::SetAttributes($readonlyPath,[IO.FileAttributes]::Normal)
    [IO.File]::SetAttributes((Join-Path $flow2 'wide0.txt'),[IO.FileAttributes]::Hidden)
    $applied = Run @('convert','--snapshot',$snap2,'--apply') 0
    Assert ($applied.success -and $applied.source_snapshot_stale -and (Test-Path -LiteralPath $applied.log)) 'successful persisted log and stale snapshot notice'
    $logValue = [IO.File]::ReadAllText($applied.log,$utf8) | ConvertFrom-Json
    Assert ($logValue.state -eq 'completed') 'complete log'
    Assert (($logValue.files | Where-Object path -eq 'already-utf8.txt').state -eq 'unchanged') 'already UTF-8 no-BOM is a no-op'
    foreach ($entry in $logValue.files) {
        $current = [IO.File]::ReadAllBytes((Join-Path $flow2 $entry.path))
        Assert ((Hash (Join-Path $flow2 $entry.path)) -eq $entry.converted_sha256) "converted hash $($entry.path)"
        Assert (!($current.Length -ge 3 -and $current[0] -eq 239 -and $current[1] -eq 187 -and $current[2] -eq 191)) "BOM removed $($entry.path)"
        $decoded = $utf8.GetString($current)
        if ($entry.path -eq 'ascii.txt') { Assert ($decoded -eq "ascii`r`n") 'ASCII EOL/final newline preserved' }
        elseif ($entry.path -eq 'utf8-bom.txt') { Assert ($decoded -eq ($text+"`r`n")) 'UTF8 mixed EOL/final newline preserved' }
        else { Assert ($decoded -eq $text) "Unicode and mixed EOL preserved $($entry.path)" }
    }
    Assert (([IO.File]::GetAttributes((Join-Path $flow2 'wide0.txt')) -band [IO.FileAttributes]::Hidden) -ne 0) 'hidden attribute preserved'
    $null = Run @('convert','--snapshot',$snap2,'--apply') 1
    $gbkCurrent = [IO.File]::ReadAllBytes($readonlyPath)
    Bytes $readonlyPath ($utf8.GetBytes('external modification'))
    $beforeOther = Hash (Join-Path $flow2 'wide0.txt')
    $null = Run @('rollback','--log',$applied.log,'--apply') 1
    Assert ((Hash (Join-Path $flow2 'wide0.txt')) -eq $beforeOther) 'rollback preflight prevents partial writes for changed source'
    Bytes $readonlyPath $gbkCurrent
    $backupEntry = $logValue.files | Where-Object path -eq 'gbk.txt'
    $backupOriginal = [IO.File]::ReadAllBytes($backupEntry.backup_path)
    Bytes $backupEntry.backup_path ($utf8.GetBytes('corrupt backup'))
    $null = Run @('rollback','--log',$applied.log,'--apply') 1
    Assert ((Hash (Join-Path $flow2 'wide0.txt')) -eq $beforeOther) 'rollback preflight rejects damaged backup before writes'
    Bytes $backupEntry.backup_path $backupOriginal
    $rolled = Run @('rollback','--log',$applied.log,'--apply') 0
    Assert ($rolled.success) 'rollback succeeds'
    foreach ($name in $originals.Keys) { Assert ((Hash (Join-Path $flow2 $name)) -eq (([Security.Cryptography.SHA256]::Create().ComputeHash($originals[$name]) | ForEach-Object ToString x2) -join '')) "rollback restores original bytes $name" }
    Assert ((Run @('rollback','--log',$applied.log,'--apply') 0).success) 'repeated rollback is safe'
    $incompleteSnapshot = Join-Path $work 'incomplete-snapshot.json'
    JsonFile $incompleteSnapshot @{schema_version=1;root=$flow2}
    $null = Run @('convert','--snapshot',$incompleteSnapshot,'--apply') 1
    JsonFile $incompleteSnapshot @{root=$flow2;files=@()}
    $null = Run @('convert','--snapshot',$incompleteSnapshot,'--apply') 1
    $incompleteBackup = Join-Path $work 'incomplete-backup'
    New-Item -ItemType Directory -Path $incompleteBackup | Out-Null
    $incompleteLog = Join-Path $incompleteBackup 'conversion-log.json'
    JsonFile $incompleteLog @{schema_version=1;root=$flow2;backup_dir=$incompleteBackup}
    $null = Run @('rollback','--log',$incompleteLog,'--apply') 1
    $after = Run @('scan','--root',$flow2,'--policy',$policyPath) 0
    Assert (($after.files | Where-Object path -like 'docs/encoding-backups').status -eq 'excluded') 'default backup directory excluded from scanning'
    $conflictPolicy = Join-Path $work 'conflict-policy.json'
    JsonFile $conflictPolicy @{default_encoding='gbk';files=@{'utf8-bom.txt'='gbk'}}
    $conflictScan = Run @('scan','--root',$flow2,'--policy',$conflictPolicy) 1
    Assert (($conflictScan.files | Where-Object path -eq 'utf8-bom.txt').status -eq 'error') 'explicit policy conflicts with BOM'

    if (!('EncodingCheckerSelftestWatcher' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text;
using System.Threading;
public sealed class EncodingCheckerSelftestWatcher {
    private readonly string log;
    private readonly string target;
    private Thread thread;
    public volatile bool Changed;
    public Exception Error;
    public EncodingCheckerSelftestWatcher(string log, string target) { this.log = log; this.target = target; }
    public void Start() {
        thread = new Thread(() => {
            DateTime deadline = DateTime.UtcNow.AddSeconds(20);
            string lastWait = null;
            try {
                while (DateTime.UtcNow < deadline) {
                    try {
                        string text;
                        using (var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (var reader = new StreamReader(stream, new UTF8Encoding(false, true))) text = reader.ReadToEnd();
                        if (text.Contains("\"state\":\"converted\"")) {
                            File.SetAttributes(target, File.GetAttributes(target) | FileAttributes.ReadOnly);
                            Changed = true;
                            return;
                        }
                    } catch (FileNotFoundException missing) { lastWait = missing.Message; }
                      catch (DirectoryNotFoundException missing) { lastWait = missing.Message; }
                    Thread.Sleep(1);
                }
                throw new TimeoutException("No converted entry observed for failure injection. Last wait: " + lastWait);
            } catch (Exception error) { Error = error; }
        });
        thread.IsBackground = true;
        thread.Start();
    }
    public bool Join(int milliseconds) { return thread.Join(milliseconds); }
}
'@
    }
    $failureRoot = Join-Path $work 'automatic rollback'
    New-Item -ItemType Directory -Path $failureRoot | Out-Null
    $failureHashes = @{}
    for ($index=0; $index -lt 24; $index++) {
        $name = 'a{0:d2}.txt' -f $index
        $file = Join-Path $failureRoot $name
        Bytes $file ($gbk.GetBytes($text))
        $failureHashes[$name] = Hash $file
    }
    $lateSource = Join-Path $failureRoot 'zz-late.txt'
    Bytes $lateSource ($gbk.GetBytes($text))
    $failureHashes['zz-late.txt'] = Hash $lateSource
    $failureSnapshot = Join-Path $failureRoot 'docs\snapshot.json'
    $null = Run @('scan','--root',$failureRoot,'--output',$failureSnapshot,'--default-encoding','gbk') 0
    $failureBackup = Join-Path $work 'automatic-failure-backup'
    $failureLog = Join-Path $failureBackup 'conversion-log.json'
    $watcher = New-Object EncodingCheckerSelftestWatcher($failureLog,$lateSource)
    $watcher.Start()
    $failedApply = Run @('convert','--snapshot',$failureSnapshot,'--backup-dir',$failureBackup,'--apply') 1
    Assert ($watcher.Join(30000) -and $watcher.Changed -and $null -eq $watcher.Error) 'deterministic post-preflight read-only failure injected'
    Assert (!$failedApply.success -and ($failedApply.files.state -contains 'rolled-back')) 'failed apply automatically rolls back converted entries'
    foreach ($name in $failureHashes.Keys) { Assert ((Hash (Join-Path $failureRoot $name)) -eq $failureHashes[$name]) "automatic rollback restores $name" }
    $failedLog = [IO.File]::ReadAllText($failureLog,$utf8) | ConvertFrom-Json
    Assert ($failedLog.state -eq 'failed-sources-restored' -and $failedLog.errors.Count -gt 0) 'failure and restoration results durably logged'
    Assert (([IO.File]::GetAttributes($lateSource) -band [IO.FileAttributes]::ReadOnly) -ne 0) 'late read-only attribute never cleared by CLI'
    $null = Run @('rollback','--log',$failureLog,'--apply') 0

    $excludeRoot = Join-Path $work 'exclude'
    $vendor = Join-Path $excludeRoot 'vendor'
    New-Item -ItemType Directory -Path $vendor -Force | Out-Null
    Bytes (Join-Path $vendor 'text.txt') ($utf8.GetBytes('vendor text'))
    Assert (((Run @('scan','--root',$excludeRoot) 0).files | Where-Object path -eq 'vendor').status -eq 'excluded') 'default vendor exclusion'
    Assert (((Run @('scan','--root',$excludeRoot,'--exclude','.git') 0).files | Where-Object path -eq 'vendor/text.txt').status -eq 'ascii-compatible') 'exclude override'
    $newOutput = Join-Path $excludeRoot 'docs\new\encoding-snapshot.json'
    Assert (!(Test-Path -LiteralPath (Split-Path $newOutput))) 'output ancestors initially absent'
    $null = Run @('scan','--root',$excludeRoot,'--output',$newOutput) 0
    Assert (Test-Path -LiteralPath $newOutput) 'output safely creates missing parent directories'
    $customBackup = Join-Path $excludeRoot 'custom-backup'
    $customConversion = Run @('convert','--snapshot',$newOutput,'--backup-dir',$customBackup,'--apply') 0
    Bytes $customConversion.log ($utf8.GetBytes('intentionally damaged metadata'))
    $customScan = Run @('scan','--root',$excludeRoot,'--output',$newOutput) 0
    Assert (($customScan.files | Where-Object path -eq 'custom-backup').status -eq 'excluded') 'backup marker excludes custom directory even with damaged log'
    $outside = Join-Path $work 'outside'
    New-Item -ItemType Directory -Path $outside | Out-Null
    Bytes (Join-Path $outside 'untouched.txt') ($utf8.GetBytes('outside'))
    $script:junction = Join-Path $excludeRoot 'linked'
    New-Item -ItemType Junction -Path $script:junction -Target $outside | Out-Null
    $junctionScanPath = Join-Path $work 'junction-snapshot.json'
    $junctionScan = Run @('scan','--root',$excludeRoot,'--output',$junctionScanPath) 0
    Assert (($junctionScan.files | Where-Object path -eq 'linked').status -eq 'excluded') 'junction excluded without traversal'
    Assert (((Run @('convert','--snapshot',$junctionScanPath) 0).files | Where-Object path -eq 'linked').state -eq 'skipped-excluded') 'excluded junction does not block dry-run'
    $null = Run @('probe','--path',(Join-Path $script:junction 'untouched.txt'),'--encoding','ascii') 1
    $null = Run @('scan','--root',$script:junction) 1
    Write-Output ("PASS: {0} assertions; fixtures: {1}" -f $script:checks,$work)
} finally {
    if ($script:junction -and (Test-Path -LiteralPath $script:junction)) { [IO.Directory]::Delete($script:junction, $false) }
    Get-ChildItem -LiteralPath $work -Recurse -File -Force | ForEach-Object { if (($_.Attributes -band [IO.FileAttributes]::ReadOnly) -ne 0) { $_.Attributes = $_.Attributes -band (-bnot [IO.FileAttributes]::ReadOnly) } }
    $resolvedWork = (Get-Item -LiteralPath $work).FullName
    $resolvedParent = (Get-Item -LiteralPath $testParent).FullName.TrimEnd('\') + '\'
    if (!$resolvedWork.StartsWith($resolvedParent, [StringComparison]::OrdinalIgnoreCase)) { throw 'Cleanup path escaped the test workspace.' }
    Remove-Item -LiteralPath $resolvedWork -Recurse -Force
}
