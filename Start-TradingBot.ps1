param(
    [string]$TradingBotRoot = "C:\TradingBot",
    [string]$BridgeHost = "127.0.0.1",
    [int]$BridgePort = 5010,
    [string]$TerminalPath = "C:\Program Files\FTMO Global Markets MT5 Terminal",
    [string]$BotMode = "tracking",
    [int]$BridgeStartupTimeoutSeconds = 90
)

$ErrorActionPreference = "Stop"

function Assert-PathExists {
    param(
        [string]$Path,
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Description was not found: $Path"
    }
}

function Test-BridgeHealth {
    param([string]$HealthUrl)

    try {
        $response = Invoke-RestMethod -Uri $HealthUrl -Method Get -TimeoutSec 3
        return $null -ne $response -and $response.isConnected -eq $true
    }
    catch {
        return $false
    }
}

$botExe = Join-Path $TradingBotRoot "TradingBot.CLI.exe"
$pythonExe = Join-Path $TradingBotRoot ".venv-mt5\Scripts\python.exe"
$bridgeScript = Join-Path $TradingBotRoot "tools\mt5-bridge\mt5_bridge.py"
$healthUrl = "http://${BridgeHost}:${BridgePort}/health"
$terminalExe = if ((Split-Path -Leaf $TerminalPath) -ieq "terminal64.exe") {
    $TerminalPath
}
else {
    Join-Path $TerminalPath "terminal64.exe"
}

Assert-PathExists -Path $TradingBotRoot -Description "TradingBot root folder"
Assert-PathExists -Path $botExe -Description "TradingBot executable"
Assert-PathExists -Path $pythonExe -Description "MT5 bridge Python executable"
Assert-PathExists -Path $bridgeScript -Description "MT5 bridge script"
Assert-PathExists -Path $terminalExe -Description "MT5 terminal executable"

Set-Location -LiteralPath $TradingBotRoot
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force

if (-not (Test-BridgeHealth -HealthUrl $healthUrl)) {
    Write-Host "Starting MT5 bridge at $healthUrl..."

    $bridgeCommand = @"
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
cd "$TradingBotRoot"
& "$pythonExe" "$bridgeScript" --host "$BridgeHost" --port "$BridgePort" --terminal-path "$terminalExe"
"@

    $encodedBridgeCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($bridgeCommand))
    Start-Process powershell.exe -ArgumentList @(
        "-NoExit",
        "-ExecutionPolicy",
        "Bypass",
        "-EncodedCommand",
        $encodedBridgeCommand
    ) -WorkingDirectory $TradingBotRoot

    $deadline = (Get-Date).AddSeconds($BridgeStartupTimeoutSeconds)
    do {
        Start-Sleep -Seconds 2
        if (Test-BridgeHealth -HealthUrl $healthUrl) {
            Write-Host "MT5 bridge is ready."
            break
        }
    } while ((Get-Date) -lt $deadline)

    if (-not (Test-BridgeHealth -HealthUrl $healthUrl)) {
        throw "MT5 bridge did not become healthy within $BridgeStartupTimeoutSeconds seconds. Check the bridge PowerShell window and MT5 terminal login."
    }
}
else {
    Write-Host "MT5 bridge is already running at $healthUrl."
}

Write-Host "Starting TradingBot with mode=$BotMode..."
& $botExe start "mode=$BotMode"
