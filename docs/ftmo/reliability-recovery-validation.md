# Reliability recovery implementation — 2026-09-24

Implemented in the source project only. Economic-calendar integration remains deferred; existing news settings, account confirmation gates, protected SMC parameters, live defaults and risk limits are unchanged. No terminal was started and no real orders were submitted.

## Behavior

- Protection reads account identity and current exposure independently of deal history. The kill switch bypasses history and daily-boundary validation. A history read failure during ordinary protection produces an explicit `accountingAvailable=false` snapshot, blocks entries and conservatively cancels/closes owned exposure. Foreign magic numbers are left untouched. Partial closes are retried using the broker's remaining volume on the next cycle.
- Degraded snapshots never refresh the bridge's entry-authorizing heartbeat. Fresh accounting must return before entries can resume. Persistent kill/total-loss halts still require their existing recovery procedure.
- Event persistence has a configurable `FtmoProtection.EvidenceTimeoutSeconds` deadline (default 2 seconds, allowed 1–10). Exceptions and timeouts pause entries without terminating protection. Only one event write can remain in flight, including a write that ignores cancellation. A bounded stderr fallback reports failures.
- Recovery requires a successful write to the execution-evidence file, followed by a fresh protection/reconciliation cycle. Ordinary successful log messages do not clear the failure. A failed log after an accepted order does not cause resubmission.
- Durable submissions store account, magic and symbol identity. A lost response can recover a unique historical order by exact stable comment, symbol, magic and submission-time window on the validated account. Existing deal evidence takes precedence. Cancelled/rejected/expired orders become terminal only when there is no unresolved fill evidence. Ambiguous or absent evidence remains unknown and blocks new exposure.
- The .NET broker adapter consumes explicit accounting availability. Deploy the CLI and Python bridge from the same package.

## Verification

- **80 .NET checks passed**, including protection continuity during event failures, bounded noncooperative writes, recovery ordering, accepted-order duplicate suppression and accounting recovery.
- **66 Python tests passed**, including history-outage cancellation, kill-switch history bypass, wrong-account rejection, partial protective closes, historical ticket recovery across journal reloads, ambiguity and identity mismatch rejection, and unresolved partial fills.
- Release publish succeeded at `reports/source-validation/reliability-release/`. Packaged help succeeded with the sanitized example profile; removed `ftmo-backtest` returned exit code 2. Required bridge files are present, no Backtesting assembly is present, and the checksum manifest contains 27 files.
- `git diff --check` passed, with line-ending notices only.

Tests used fake broker calls and the existing Python interpreter with `-B` and source-local timezone dependencies. The runtime directory was not modified. These checks establish failure handling, not live profitability, win rate, or challenge-pass probability. Current account-confirmation and missing-news-coverage entry blocks still apply.

```powershell
dotnet run --project TradingBot.Tests --no-restore
python -B -m unittest discover -s tools/mt5-bridge -p 'test_*.py'
dotnet publish TradingBot.CLI/TradingBot.CLI.csproj -c Release --no-restore --nologo -o reports/source-validation/reliability-release
```

The local package is a build artifact, not a runtime deployment. As with other publish output, treat its configuration as private.
