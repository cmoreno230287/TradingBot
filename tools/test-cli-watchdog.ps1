$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'cli-watchdog.ps1')
function Assert-Decision($Health, $Age, $Expected) {
    $actual = Get-CliWatchdogDecision $Health 123 'test' $Age 60 45
    if ($actual -ne $Expected) { throw "Expected $Expected; got $actual" }
}
$healthy = @{processId=123;startupId='test';operationAgeSeconds=$null;cycleAgeSeconds=1;protectionAgeSeconds=1;degraded=$false}
Assert-Decision $null 10 'Starting'
Assert-Decision $null 70 'Unreachable'
Assert-Decision $healthy 70 'Healthy'
$healthy.processId=456
Assert-Decision $healthy 70 'WrongInstance'
$healthy.processId=123
$healthy.startupId='old'
Assert-Decision $healthy 70 'WrongInstance'
$healthy.startupId='test'
$healthy.operationAgeSeconds=46
Assert-Decision $healthy 70 'Stalled'
$healthy.operationAgeSeconds=$null
$healthy.cycleAgeSeconds=46
Assert-Decision $healthy 70 'Stalled'
$healthy.cycleAgeSeconds=1
$healthy.protectionAgeSeconds=100
Assert-Decision $healthy 70 'Stalled'
$healthy.degraded=$true
Assert-Decision $healthy 70 'Degraded'
$healthy.cycleAgeSeconds=$null
Assert-Decision $healthy 10 'Starting'
Assert-Decision $healthy 70 'Stalled'
Write-Output 'PASS 11 CLI watchdog decision cases'
