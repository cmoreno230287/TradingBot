param([string]$Destination)
$ErrorActionPreference = 'Stop'
$projectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $Destination) {
    $Destination = Join-Path $projectRoot ('reports/live-release/' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$releaseRoot = [System.IO.Path]::GetFullPath($Destination)
$allowedRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'reports')) + [System.IO.Path]::DirectorySeparatorChar
if (-not $releaseRoot.StartsWith($allowedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Package destination must be under this project reports directory. This script does not deploy.'
}
if (Test-Path -LiteralPath $releaseRoot) { throw 'Choose a new package directory to avoid stale artifacts.' }
& dotnet publish (Join-Path $projectRoot 'TradingBot.CLI/TradingBot.CLI.csproj') -c Release -o $releaseRoot --nologo
if ($LASTEXITCODE -ne 0) { throw 'Live publish failed.' }
if (Get-ChildItem -LiteralPath $releaseRoot -Filter '*Backtesting*') { throw 'Unexpected simulation artifact.' }
foreach ($relative in @('TradingBot.CLI.dll', 'tools/mt5-bridge/mt5_bridge.py', 'tools/mt5-bridge/ftmo_guard.py',
    'tools/mt5-bridge/live_gate.py', 'tools/mt5-bridge/live_state.py', 'tools/mt5-bridge/live_reconciliation.py',
    'tools/mt5-bridge/live_metrics.py', 'tools/mt5-bridge/live_storage.py', 'tools/mt5-bridge/requirements.txt',
    'tools/mt5-bridge/supervise_bridge.py', 'tools/run-live-supervised.ps1', 'tools/cli-watchdog.ps1')) {
    if (-not (Test-Path -LiteralPath (Join-Path $releaseRoot $relative))) { throw "Missing release component: $relative" }
}
$files = @(Get-ChildItem -LiteralPath $releaseRoot -File -Recurse | Sort-Object FullName | ForEach-Object {
    [ordered]@{ path=$_.FullName.Substring($releaseRoot.Length + 1); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
[ordered]@{ createdUtc=[DateTime]::UtcNow.ToString('O'); mode='live-only'; framework='net10.0'; files=$files } |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $releaseRoot 'release-manifest.json') -Encoding UTF8
Write-Output "Live-only package: $releaseRoot"
