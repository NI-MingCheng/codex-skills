[CmdletBinding()]
param(
    [string]$ProxyUrl,
    [switch]$CheckOnly,
    [string]$CodexHome = $(if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE ".codex" })
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$PSStyle.OutputRendering = "PlainText"

$codexHomeFull = [System.IO.Path]::GetFullPath($CodexHome)
$proxyNames = @("CODEX_PROXY_URL", "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY")
$noProxyName = "NO_PROXY"

function ConvertTo-LocalProxyUrl {
    param([Parameter(Mandatory = $true)][string]$Value)

    $candidate = $Value.Trim()
    if (-not $candidate) {
        throw "Proxy URL is empty"
    }
    if ($candidate -notmatch "^[A-Za-z][A-Za-z0-9+.-]*://") {
        $candidate = "http://$candidate"
    }

    $parsed = $null
    if (-not [System.Uri]::TryCreate($candidate, [System.UriKind]::Absolute, [ref]$parsed)) {
        throw "Invalid proxy URL: $Value"
    }
    if ($parsed.Scheme -ne "http") {
        throw "Only a local HTTP or mixed proxy endpoint is supported: $Value"
    }
    if ($parsed.UserInfo) {
        throw "Proxy URLs containing credentials are not allowed"
    }
    if (-not $parsed.IsLoopback) {
        throw "Only a loopback proxy endpoint is accepted automatically: $Value"
    }
    if ($parsed.Port -le 0 -or $parsed.AbsolutePath -ne "/" -or $parsed.Query -or $parsed.Fragment) {
        throw "Proxy URL must contain only scheme, loopback host, and port: $Value"
    }
    return $parsed.GetLeftPart([System.UriPartial]::Authority)
}

function Get-WindowsProxyUrl {
    $settings = Get-ItemProperty -LiteralPath "HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings" -ErrorAction SilentlyContinue
    if (-not $settings -or [int]$settings.ProxyEnable -ne 1 -or -not [string]$settings.ProxyServer) {
        return $null
    }

    $value = [string]$settings.ProxyServer
    if ($value -notmatch "=") {
        return ConvertTo-LocalProxyUrl $value
    }

    $mapped = @{}
    foreach ($entry in $value.Split(";", [System.StringSplitOptions]::RemoveEmptyEntries)) {
        $parts = $entry.Split("=", 2)
        if ($parts.Count -eq 2) {
            $mapped[$parts[0].Trim().ToLowerInvariant()] = $parts[1].Trim()
        }
    }
    $values = @(
        foreach ($protocol in @("https", "http")) {
            if ($mapped.ContainsKey($protocol) -and $mapped[$protocol]) {
                ConvertTo-LocalProxyUrl $mapped[$protocol]
            }
        }
    ) | Select-Object -Unique
    if ($values.Count -eq 0) {
        throw "Windows system proxy has no supported HTTP/HTTPS endpoint"
    }
    if ($values.Count -gt 1) {
        throw "Windows HTTP and HTTPS proxy endpoints disagree; specify -ProxyUrl"
    }
    return $values[0]
}

function Get-EnvironmentProxyUrl {
    param([ValidateSet("Process", "User")][string]$Target)

    $values = @(
        foreach ($name in @("HTTPS_PROXY", "HTTP_PROXY", "ALL_PROXY")) {
            $value = [Environment]::GetEnvironmentVariable($name, $Target)
            if ($value) {
                ConvertTo-LocalProxyUrl $value
            }
        }
    ) | Select-Object -Unique
    if ($values.Count -gt 1) {
        throw "$Target proxy environment variables disagree; specify -ProxyUrl"
    }
    return $values | Select-Object -First 1
}

function Test-LocalPort {
    param([Parameter(Mandatory = $true)][System.Uri]$Uri)

    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $connect = $client.ConnectAsync($Uri.Host, $Uri.Port)
        if (-not $connect.Wait([TimeSpan]::FromSeconds(3))) {
            return $false
        }
        return $client.Connected
    } catch {
        return $false
    } finally {
        $client.Dispose()
    }
}

function Test-HttpConnectProxy {
    param([Parameter(Mandatory = $true)][string]$Candidate)

    $uri = [System.Uri]$Candidate
    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $connect = $client.ConnectAsync($uri.Host, $uri.Port)
        if (-not $connect.Wait([TimeSpan]::FromMilliseconds(800))) {
            return $false
        }
        $stream = $client.GetStream()
        $stream.ReadTimeout = 1200
        $stream.WriteTimeout = 1200
        $request = [System.Text.Encoding]::ASCII.GetBytes("CONNECT github.com:443 HTTP/1.1`r`nHost: github.com:443`r`nConnection: close`r`n`r`n")
        $stream.Write($request, 0, $request.Length)
        $buffer = [byte[]]::new(256)
        $count = $stream.Read($buffer, 0, $buffer.Length)
        if ($count -le 0) {
            return $false
        }
        $statusLine = [System.Text.Encoding]::ASCII.GetString($buffer, 0, $count).Split("`n")[0].Trim()
        return $statusLine -match '^HTTP/\d(?:\.\d)? 200(?:\s|$)'
    } catch {
        return $false
    } finally {
        $client.Dispose()
    }
}

function Get-VerifiedLoopbackCandidates {
    $listeners = [System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners()
    $candidates = [System.Collections.Generic.List[string]]::new()
    foreach ($listener in $listeners) {
        if ($listener.Port -le 0) {
            continue
        }
        $address = $listener.Address
        if ($address.Equals([System.Net.IPAddress]::Any) -or $address.Equals([System.Net.IPAddress]::Loopback)) {
            $candidate = "http://127.0.0.1:$($listener.Port)"
        } elseif ($address.Equals([System.Net.IPAddress]::IPv6Any) -or $address.Equals([System.Net.IPAddress]::IPv6Loopback)) {
            $candidate = "http://[::1]:$($listener.Port)"
        } else {
            continue
        }
        if (-not $candidates.Contains($candidate)) {
            $candidates.Add($candidate)
        }
    }
    if ($candidates.Count -gt 128) {
        throw "Too many loopback listeners for safe automatic discovery; specify -ProxyUrl"
    }
    return @($candidates | Where-Object { Test-HttpConnectProxy $_ })
}

function Invoke-ProxyRequest {
    param(
        [Parameter(Mandatory = $true)][string]$ResolvedProxyUrl,
        [Parameter(Mandatory = $true)][string]$Uri
    )

    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.Proxy = [System.Net.WebProxy]::new($ResolvedProxyUrl)
    $handler.UseProxy = $true
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(15)
    try {
        for ($attempt = 1; $attempt -le 2; $attempt++) {
            $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Head, $Uri)
            $request.Headers.UserAgent.ParseAdd("codex-skills-proxy-preflight/1")
            try {
                $response = $client.SendAsync($request, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
                try {
                    return [int]$response.StatusCode
                } finally {
                    $response.Dispose()
                }
            } catch {
                if ($attempt -eq 2) {
                    throw "Proxy request failed after two attempts: $Uri ($($_.Exception.Message))"
                }
            } finally {
                $request.Dispose()
            }
        }
    } finally {
        $client.Dispose()
        $handler.Dispose()
    }
}

function Resolve-ProxyUrl {
    if ($ProxyUrl) {
        return [pscustomobject]@{ Url = ConvertTo-LocalProxyUrl $ProxyUrl; Source = "parameter" }
    }

    foreach ($target in @("Process", "User")) {
        $value = [Environment]::GetEnvironmentVariable("CODEX_PROXY_URL", $target)
        if ($value) {
            return [pscustomobject]@{ Url = ConvertTo-LocalProxyUrl $value; Source = "$target CODEX_PROXY_URL" }
        }
    }

    $windowsProxy = Get-WindowsProxyUrl
    if ($windowsProxy) {
        return [pscustomobject]@{ Url = $windowsProxy; Source = "Windows system proxy" }
    }

    foreach ($target in @("User", "Process")) {
        $environmentProxy = Get-EnvironmentProxyUrl $target
        if ($environmentProxy) {
            return [pscustomobject]@{ Url = $environmentProxy; Source = "$target proxy environment" }
        }
    }

    $available = @(Get-VerifiedLoopbackCandidates | Select-Object -Unique)
    if ($available.Count -eq 1) {
        return [pscustomobject]@{ Url = $available[0]; Source = "verified loopback listener" }
    }
    if ($available.Count -gt 1) {
        throw "Multiple working loopback HTTP proxies were discovered; specify -ProxyUrl"
    }
    throw "No working loopback HTTP/mixed proxy was discovered. Configure an existing proxy manually and rerun; no software was installed or started."
}

function Merge-NoProxy {
    param([string]$Existing)

    $items = [System.Collections.Generic.List[string]]::new()
    foreach ($item in @($Existing -split ",") + @("localhost", "127.0.0.1", "::1")) {
        $trimmed = $item.Trim()
        if ($trimmed -and -not ($items | Where-Object { $_ -ieq $trimmed })) {
            $items.Add($trimmed)
        }
    }
    return $items -join ","
}

function Set-UserProxyEnvironment {
    param([Parameter(Mandatory = $true)][string]$ResolvedProxyUrl)

    $desired = [ordered]@{}
    foreach ($name in $proxyNames) {
        $desired[$name] = $ResolvedProxyUrl
    }
    $desired[$noProxyName] = Merge-NoProxy ([Environment]::GetEnvironmentVariable($noProxyName, "User"))

    $previous = [ordered]@{}
    $changed = $false
    foreach ($name in $desired.Keys) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name, "User")
        if ($previous[$name] -ne $desired[$name]) {
            $changed = $true
        }
    }

    $backupDirectory = $null
    if ($changed) {
        $backupDirectory = Join-Path $codexHomeFull ((Get-Date).ToUniversalTime().ToString("'backups/'yyyyMMdd-HHmmssfff'-proxy'"))
        [System.IO.Directory]::CreateDirectory($backupDirectory) | Out-Null
        $backupPath = Join-Path $backupDirectory "user-proxy-environment.json"
        $payload = [ordered]@{
            schema_version = 1
            created_at_utc = (Get-Date).ToUniversalTime().ToString("o")
            previous = $previous
        } | ConvertTo-Json -Depth 4
        [System.IO.File]::WriteAllText($backupPath, $payload.Replace("`r`n", "`n") + "`n", [System.Text.UTF8Encoding]::new($false))

        try {
            foreach ($name in $desired.Keys) {
                [Environment]::SetEnvironmentVariable($name, $desired[$name], "User")
            }
        } catch {
            foreach ($name in $previous.Keys) {
                [Environment]::SetEnvironmentVariable($name, $previous[$name], "User")
            }
            throw
        }
    }

    foreach ($name in $desired.Keys) {
        Set-Item -LiteralPath "Env:$name" -Value $desired[$name]
    }
    return [pscustomobject]@{ Changed = $changed; BackupDirectory = $backupDirectory }
}

$resolved = Resolve-ProxyUrl
$proxyUri = [System.Uri]$resolved.Url
if (-not (Test-LocalPort $proxyUri)) {
    throw "Local proxy is not listening: $($resolved.Url)"
}

$checks = [ordered]@{
    GitHub = Invoke-ProxyRequest $resolved.Url "https://github.com/"
    ChatGPT = Invoke-ProxyRequest $resolved.Url "https://chatgpt.com/"
    OpenAI = Invoke-ProxyRequest $resolved.Url "https://api.openai.com/v1/models"
}
if ($checks.GitHub -lt 200 -or $checks.GitHub -ge 400) {
    throw "GitHub proxy check failed with HTTP $($checks.GitHub)"
}
foreach ($name in @("ChatGPT", "OpenAI")) {
    $status = [int]$checks[$name]
    if ($status -lt 200 -or $status -ge 500 -or $status -eq 407) {
        throw "$name proxy check failed with HTTP $status"
    }
}

$result = [pscustomobject]@{ Changed = $false; BackupDirectory = $null }
if (-not $CheckOnly) {
    $result = Set-UserProxyEnvironment $resolved.Url
}

Write-Output "Codex proxy preflight: PASS"
Write-Output "Proxy source: $($resolved.Source)"
Write-Output "Proxy URL: $($resolved.Url)"
Write-Output "GitHub HTTP status: $($checks.GitHub)"
Write-Output "ChatGPT HTTP status: $($checks.ChatGPT)"
Write-Output "OpenAI HTTP status: $($checks.OpenAI)"
Write-Output "User proxy environment changed: $($result.Changed)"
if ($result.BackupDirectory) {
    Write-Output "Proxy environment backup: $($result.BackupDirectory)"
}
if ($result.Changed) {
    Write-Output "Restart Codex so the desktop process inherits the proxy environment."
}
