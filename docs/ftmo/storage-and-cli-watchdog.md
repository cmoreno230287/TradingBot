# Storage-independent protection and CLI watchdog

Implemented in source on 2026-09-25. Account configuration, news settings and strategy parameters were not changed. No deployment or live trading was performed.

## Emergency storage behavior

The bridge's kill-switch path reads broker identity/exposure, attempts owned-order cancellation and owned-position closing, and only then accesses the execution journal. Journal-directory creation moved into the bounded storage worker. Ordinary protection also tolerates failed journal access and conservatively protects owned exposure while blocking entries.

All bridge journal reads and writes share a single worker lease and a two-second per-call budget, including time waiting for another operation. A call that exceeds its budget keeps the lease until it actually finishes. Subsequent writes cannot overtake it. Writes receive independent state copies, so caller mutations cannot alter a delayed write. Corrupt state is never replaced with an empty journal, including during emergency halt persistence. Failed storage produces a bounded stderr diagnostic.

When storage recovers, protection must read the journal, reconcile broker state and persist successfully before refreshing its entry-authorizing heartbeat. Unknown submissions remain blocked. A permanently stuck storage operation keeps entries paused; protection continues. Killing a process cannot guarantee a durable halt if storage itself is unavailable: retain the configured/file kill switch until storage and reconciliation are verified.

The CLI's account lease is now a named kernel object instead of a file. Its kill-file check has a one-second bounded wait with no overlapping probes. An already-active configured kill switch skips that filesystem check. Missing kill files are normal; access errors/timeouts pause entries while broker protection continues. Neither this nor the bridge can execute a protective trade while MT5 itself is unavailable.

## CLI health and recovery

Each protected CLI exposes a same-user named pipe, `TradingBot.Health.<accountId>`. It reports process ID, startup nonce, instance ID, current protection-operation age, last completed cycle age, last successful protection age, and degraded status. Ages use a monotonic clock. The endpoint has no filesystem or broker dependencies.

`tools/run-live-supervised.ps1` launches and owns one CLI child. The account-specific supervisor lease prevents competing supervisors in the same Windows session. The default watchdog settings are:

| Parameter | Default |
| --- | --- |
| StartupGraceSeconds | 60 |
| MaximumStallSeconds | 45 |
| ConsecutiveFailures | 3 |
| PollSeconds | 5 |
| HealthTimeoutMilliseconds | 1500 per transport stage |
| ShutdownGraceSeconds | 10 |
| MaximumRestarts | 5 total per supervisor run |

Missing/incorrect identity, an overlong operation, or stale cycle progress leads to recovery after the failure threshold. An endpoint that responds while protection is frozen is not considered healthy. Ordinary risk halts remain healthy. A protection loop that keeps completing with bridge/accounting errors is degraded: it reports the condition without repeatedly restarting the CLI to address an external outage.

Recovery sends a startup-nonce-checked shutdown request, waits for graceful exit and then terminates only its owned child if needed. It refuses to launch a replacement until termination is confirmed. Restarts have exponential backoff and a finite cap. A replacement CLI starts without an entry-authorizing snapshot and must complete protection/reconciliation before submitting anything. Bridge durable submission identity remains authoritative.

Example after deployment, under an environment that permits these scripts:

```powershell
.\tools\run-live-supervised.ps1 -Executable .\TradingBot.CLI.exe -Config .\appsettings.json
```

Use the same Windows user/session for CLI and supervisor. Keep the working directory stable across bridge restarts because the existing bridge journal path remains relative to it. Do not start an old CLI alongside this build: older releases use a different account lease mechanism. Stop the old process before deploying the matching CLI, bridge modules and supervisor scripts.

## Validation and limits

- 83 .NET checks passed, including three real child-process named-pipe checks: stalled protection with a responsive endpoint, degraded protection, and a normal risk halt. They also verify process identity and rejection of a shutdown request with the wrong startup nonce.
- 72 Python tests passed, covering permission/disk-full/corrupt-state failures, journal ordering during emergencies, bounded stalled storage, immutable delayed writes, and partial-close retries with unavailable storage.
- Release publish and packaged help succeeded at `reports/source-validation/storage-watchdog-release/`; the checksum manifest covers 30 files including the storage module and both supervisor scripts.
- PowerShell syntax validation passed. `tools/test-cli-watchdog.ps1` contains 11 decision cases, but Windows execution policy blocked running it. The actual PowerShell restart loop, restart cap and force-termination path still require validation in a script-enabled environment. Policy was not changed.
- No real MT5 connection, protective action or order submission was used. The bridge remains offline. The package is local build output, not a runtime deployment.
