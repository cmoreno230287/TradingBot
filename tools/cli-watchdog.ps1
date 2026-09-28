# Pure watchdog decision plus bounded local-pipe transport. No processes start on import.
function Get-CliWatchdogDecision($Health, [int]$ExpectedProcessId, [string]$StartupId, [double]$UptimeSeconds, [double]$StartupGraceSeconds, [double]$MaximumStallSeconds) {
    if ($null -eq $Health) { if ($UptimeSeconds -lt $StartupGraceSeconds) { return 'Starting' }; return 'Unreachable' }
    if ($Health.processId -ne $ExpectedProcessId -or $Health.startupId -ne $StartupId) { return 'WrongInstance' }
    if ($null -ne $Health.operationAgeSeconds -and $Health.operationAgeSeconds -gt $MaximumStallSeconds) { return 'Stalled' }
    if ($null -ne $Health.cycleAgeSeconds -and $Health.cycleAgeSeconds -gt $MaximumStallSeconds) { return 'Stalled' }
    if ($null -eq $Health.cycleAgeSeconds) { if ($UptimeSeconds -lt $StartupGraceSeconds) { return 'Starting' }; return 'Stalled' }
    if ($Health.degraded) { return 'Degraded' }
    if ($null -eq $Health.protectionAgeSeconds -or $Health.protectionAgeSeconds -gt $MaximumStallSeconds) { return 'Stalled' }
    return 'Healthy'
}

function Read-CliHealth([long]$AccountId, [int]$TimeoutMilliseconds, [string]$Command = 'health') {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', "TradingBot.Health.$AccountId", [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $pipe.Connect($TimeoutMilliseconds)
        $writer = [IO.StreamWriter]::new($pipe)
        $writer.AutoFlush = $true
        $write = $writer.WriteLineAsync($Command)
        if (-not $write.Wait($TimeoutMilliseconds)) { throw 'Health request write timed out' }
        $reader = [IO.StreamReader]::new($pipe)
        $read = $reader.ReadLineAsync()
        if (-not $read.Wait($TimeoutMilliseconds)) { throw 'Health response timed out' }
        return ($read.Result | ConvertFrom-Json)
    } finally { $pipe.Dispose() }
}
