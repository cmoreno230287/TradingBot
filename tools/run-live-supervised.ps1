param(
    [Parameter(Mandatory=$true)][string]$Executable,
    [string]$Config,
    [ValidateRange(5,300)][int]$StartupGraceSeconds = 60,
    [ValidateRange(20,300)][int]$MaximumStallSeconds = 45,
    [ValidateRange(1,20)][int]$ConsecutiveFailures = 3,
    [ValidateRange(1,30)][int]$PollSeconds = 5,
    [ValidateRange(100,10000)][int]$HealthTimeoutMilliseconds = 1500,
    [ValidateRange(1,60)][int]$ShutdownGraceSeconds = 10,
    [ValidateRange(0,100)][int]$MaximumRestarts = 5
)
$ErrorActionPreference = 'Stop'
if (-not $Config) {
    $Config = Join-Path $PSScriptRoot '../appsettings.json'
    if (-not (Test-Path -LiteralPath $Config)) { $Config = Join-Path $PSScriptRoot '../TradingBot.CLI/appsettings.json' }
}
$cliPath = (Resolve-Path -LiteralPath $Executable).Path
$configPath = (Resolve-Path -LiteralPath $Config).Path
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'cli-watchdog.ps1')
$configuration = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$accountId = $configuration.Brokers.MT5.AccountId
if (-not $accountId) { $accountId = $configuration.MT5.AccountId }
if (-not $accountId -or $accountId -le 0) { throw 'A specific account ID is required.' }
if ($cliPath.Contains('"') -or $configPath.Contains('"')) { throw 'Invalid quoted path.' }
$logRoot = Join-Path $projectRoot 'reports/supervisor'
New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
$created = $false
$lease = [Threading.Mutex]::new($false, "Local\TradingBot.Supervisor.$accountId", [ref]$created)
if (-not $created) { $lease.Dispose(); throw 'An account supervisor is already running.' }
$child = $null
$backoffSeconds = 2
$restarts = 0
$previousStartupId = $env:TRADINGBOT_STARTUP_ID
try {
    while ($true) {
        $started = [DateTime]::UtcNow
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
        $startupId = [Guid]::NewGuid().ToString('N')
        $env:TRADINGBOT_STARTUP_ID = $startupId
        $child = Start-Process -FilePath $cliPath -ArgumentList @('start', ('"config=' + $configPath + '"')) `
            -WorkingDirectory $projectRoot -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput (Join-Path $logRoot ($stamp + '-stdout.log')) `
            -RedirectStandardError (Join-Path $logRoot ($stamp + '-stderr.log'))
        $uptime = [Diagnostics.Stopwatch]::StartNew()
        $failures = 0
        $displayedOutputLines = 0
        $restartForStall = $false
        $lastDecision = ''
        while (-not $child.HasExited) {
            try { $lines = @(Get-Content -LiteralPath (Join-Path $logRoot ($stamp + '-stdout.log')) -ErrorAction SilentlyContinue); if ($lines.Count -gt $displayedOutputLines) { foreach ($line in $lines[$displayedOutputLines..($lines.Count - 1)]) { if ($line -like '*TradingBot Analysis Cycle*') { Write-Output ("$([char]27)[2J$([char]27)[3J$([char]27)[H") }; Write-Output $line }; $displayedOutputLines = $lines.Count } } catch { }
            $health = $null
            try { $health = Read-CliHealth $accountId $HealthTimeoutMilliseconds } catch { }
            $decision = Get-CliWatchdogDecision $health $child.Id $startupId $uptime.Elapsed.TotalSeconds $StartupGraceSeconds $MaximumStallSeconds
            if ($decision -ne $lastDecision) { Write-Output "CLI watchdog: $decision"; $lastDecision = $decision }
            if ($decision -in @('Unreachable','WrongInstance','Stalled')) { $failures++ } else { $failures = 0 }
            if ($failures -ge $ConsecutiveFailures) {
                $restartForStall = $true
                # Only the child we started is terminated. Nonce-authenticated shutdown cannot stop another instance.
                if ($decision -ne 'WrongInstance') { try { $null = Read-CliHealth $accountId $HealthTimeoutMilliseconds "stop $startupId" } catch { } }
                if (-not $child.WaitForExit($ShutdownGraceSeconds * 1000)) { $child.Kill(); if (-not $child.WaitForExit(10000)) { throw 'Owned child did not terminate; refusing to start a competing process.' } }
                break
            }
            $null = $child.WaitForExit($PollSeconds * 1000)
        }
        if ($child.ExitCode -eq 0 -and -not $restartForStall) { break }
        if ($child.ExitCode -eq 2) { throw 'Configuration rejected; automatic restart stopped.' }
        if ($restarts -ge $MaximumRestarts) { throw 'CLI restart limit reached; operator investigation required.' }
        $restarts++
        if (([DateTime]::UtcNow - $started).TotalMinutes -ge 5) { $backoffSeconds = 2 }
        Write-Warning "Live process exited with code $($child.ExitCode); restarting in $backoffSeconds seconds."
        Start-Sleep -Seconds $backoffSeconds
        $backoffSeconds = [Math]::Min(60, $backoffSeconds * 2)
    }
}
finally {
    if ($null -ne $child -and -not $child.HasExited) { Stop-Process -Id $child.Id }
    $env:TRADINGBOT_STARTUP_ID = $previousStartupId
    $lease.Dispose()
}





