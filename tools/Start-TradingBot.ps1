param(
    [string]$TradingBotRoot = 'C:\TradingBot',
    [string]$Config,
    [ValidateSet('127.0.0.1','localhost')][string]$BridgeHost = '127.0.0.1',
    [ValidateRange(1,65535)][int]$BridgePort = 5010,
    [string]$TerminalPath = 'C:\Program Files\FTMO Global Markets MT5 Terminal',
    [ValidateSet('tracking')][string]$BotMode = 'tracking',
    [ValidateRange(5,300)][int]$BridgeStartupTimeoutSeconds = 90,
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
$TradingBotRoot = (Resolve-Path -LiteralPath $TradingBotRoot).Path
if (-not $Config) { $Config = Join-Path $TradingBotRoot 'appsettings.json' }
$Config = (Resolve-Path -LiteralPath $Config).Path
$botExe = Join-Path $TradingBotRoot 'TradingBot.CLI.exe'
$pythonExe = Join-Path $TradingBotRoot '.venv-mt5/Scripts/python.exe'
$bridgeScript = Join-Path $TradingBotRoot 'tools/mt5-bridge/mt5_bridge.py'
$supervisor = Join-Path $TradingBotRoot 'tools/run-live-supervised.ps1'
$terminalExe = if ((Split-Path -Leaf $TerminalPath) -ieq 'terminal64.exe') { $TerminalPath } else { Join-Path $TerminalPath 'terminal64.exe' }
foreach ($path in @($botExe,$pythonExe,$bridgeScript,$supervisor,$terminalExe,(Join-Path $TradingBotRoot 'tools/cli-watchdog.ps1'))) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Required file missing: $path" }
    if ($path.Contains('"')) { throw 'Paths containing quotes are unsupported.' }
}
$settings = Get-Content -LiteralPath $Config -Raw | ConvertFrom-Json
$mt5 = $settings.Brokers.MT5
if ($null -eq $mt5) { $mt5 = $settings.MT5 }
$accountId = [long]$mt5.AccountId
if ($accountId -le 0 -or $settings.Broker -ne 'MT5' -or -not $settings.FtmoProtection.Enabled) {
    throw 'Protected MT5 configuration with an explicit account ID is required.'
}
$configuredUrl = if ($mt5.BridgeBaseUrl) { [uri]$mt5.BridgeBaseUrl } else { [uri]'http://localhost:5010' }
if ($configuredUrl.Scheme -ne 'http' -or $configuredUrl.Host -notin @('127.0.0.1','localhost') -or $configuredUrl.Port -ne $BridgePort -or $configuredUrl.AbsolutePath -ne '/') {
    throw 'The configured bridge URL must match the local bridge port and root endpoint.'
}
$healthUrl = "http://${BridgeHost}:${BridgePort}/health"
function Get-BridgeHealth {
    try {
        $response = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 3
        if ($response.isConnected -ne $true) { return $null }
        if ($null -eq $response.account -or [long]$response.account.login -ne $accountId) {
            throw "Bridge account mismatch."
        }
        return $response
    } catch { return $null }
}
function Assert-BridgeAccount($Health) {
    if (-not $Health.isConnected) { throw 'Bridge is not connected to MT5.' }
    if ([long]$Health.account.login -ne $accountId) {
        throw "MT5 account mismatch: expected $accountId; connected $($Health.account.login). No bot was started."
    }
    if (-not $Health.account.trade_allowed -or -not $Health.account.trade_expert) {
        throw 'MT5 account trading or Expert Advisor permissions are disabled.'
    }
}
function Assert-BridgeProcess {
    $listeners = [Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners()
    if (@($listeners | Where-Object { $_.Port -eq $BridgePort }).Count -eq 0) { return }
    $line = netstat -ano | Select-String (":$BridgePort\s+.*LISTENING\s+(\d+)$") | Select-Object -First 1
    if ($null -eq $line) { throw "Cannot identify the process listening on bridge port $BridgePort." }
    $match = [regex]::Match($line.Line, 'LISTENING\s+(\d+)\s*$')
    if (-not $match.Success) { throw "Cannot identify the process listening on bridge port $BridgePort." }
    $owner = Get-Process -Id ([int]$match.Groups[1].Value) -ErrorAction Stop
    if ([string]::IsNullOrWhiteSpace($owner.Path)) {
        if ($owner.ProcessName -ne 'python') { throw "Bridge port $BridgePort is owned by an uninspectable non-Python process '$($owner.ProcessName)'." }
        # Windows may deny process-path inspection even for the same user. The authenticated
        # bridge health/account check above remains the authoritative identity check.
        return
    }
    $ownerPath = [IO.Path]::GetFullPath($owner.Path)
    $expectedPath = [IO.Path]::GetFullPath($pythonExe)
    $allowedPaths = @($expectedPath)
    $venvConfig = Join-Path (Split-Path $pythonExe -Parent) '../pyvenv.cfg'
    if (Test-Path -LiteralPath $venvConfig) {
        $venvHomeLine = (Get-Content -LiteralPath $venvConfig | Where-Object { $_ -match '^home\s*=\s*(.+)$' } | Select-Object -First 1)
        if ($venvHomeLine) { $allowedPaths += Join-Path $Matches[1].Trim() 'python.exe' }
    }
    if (-not ($allowedPaths | Where-Object { [StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath($_), $ownerPath) })) {
        throw "Bridge port $BridgePort is owned by '$ownerPath', but this launcher requires '$expectedPath'. Stop the other bridge process first."
    }
}

# Help validates configuration without connecting or placing orders.
& $botExe help "config=$Config" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'CLI configuration validation failed.' }
if ($ValidateOnly) {
    Write-Output "Startup configuration valid for account $accountId. No bridge, terminal or bot started."
    return
}

$health = Get-BridgeHealth
if ($null -eq $health) {
    # An occupied but unhealthy endpoint must not trigger a competing bridge.
    # Query local listeners rather than interpreting a TCP connection timeout as port occupancy.
    $listeners = [Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners()
    $occupied = @($listeners | Where-Object { $_.Port -eq $BridgePort }).Count -gt 0
    if ($occupied) { throw 'Bridge port is occupied but health failed. Investigate the existing bridge before restarting.' }
    $logRoot = Join-Path $TradingBotRoot 'reports/bridge'
    New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
    $child = Start-Process -FilePath $pythonExe -ArgumentList @('-B',('"' + $bridgeScript + '"'),'--host',$BridgeHost,'--port',$BridgePort,'--terminal-path',('"' + $terminalExe + '"')) `
        -WorkingDirectory $TradingBotRoot -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $logRoot "$stamp-out.log") -RedirectStandardError (Join-Path $logRoot "$stamp-error.log")
    Write-Output "Bridge process $($child.Id) started. Logs: $logRoot"
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        if ($child.HasExited) { throw "Bridge exited with code $($child.ExitCode). Check $logRoot" }
        $health = Get-BridgeHealth
        if ($null -ne $health) { break }
        Start-Sleep -Seconds 1
    } while ($timer.Elapsed.TotalSeconds -lt $BridgeStartupTimeoutSeconds)
    if ($null -eq $health) { throw "Bridge startup timed out. Check $logRoot; the bot was not started." }
}
Assert-BridgeProcess
Assert-BridgeAccount $health
Write-Output "MT5 account $accountId verified. Starting protected CLI supervision."
# Protected execution owns tracking; the legacy mode argument is retained for caller compatibility.
& $supervisor -Executable $botExe -Config $Config
