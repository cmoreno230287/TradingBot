# Unattended live trading improvements

Implemented in source only. No runtime deployment, process startup, external alert delivery or broker order is part of this change. Live defaults remain enabled; simulation remains removed. These engineering changes do not establish a profitable edge or guarantee passing a challenge.

## Protection and recovery

Protection continues after individual cancellation/close failures, refreshes positions after cancellation to catch racing fills, and reconciles after actions. Partial closes retry against remaining broker volume next cycle. Journal failures block entries while protection still attempts to flatten owned exposure. Corrupt journals are preserved, not replaced with empty state.

The bridge gate prioritizes queued protection and bounds queue waits to two seconds. Native MT5 calls cannot be safely interrupted by Python threads. A client timeout does not cancel an order. The optional bridge watchdog restarts a stalled worker after three failed health probes beyond its startup grace period, preserving the journal. Reconnects and restarts use backoff. Native SL/TP and expiration remain essential during outages.

Final broker preflight is followed by quote/spread and deadline checks. Deadlines include protection freshness, SMC expiry, news coverage, the next blackout and reset window. Protection cancels unrecognized or inadequately protected owned orders and closes inadequately protected owned positions.

Durable lifecycle: `reserved`, `accepted`, `reconciled`, `filled`, `closed`, `cancelled`, `rejected`, `unknown`. Tickets, comments, deals and position identifiers drive recovery. An order disappearing does not prove cancellation. Definitive non-execution may retry only for explicitly retryable causes, bounded by `MaximumSubmissionAttempts` (3) and `SubmissionRetryDelaySeconds` (15). Uncertain execution remains blocked pending evidence; there is no guessed reset.

## Challenge policy and cadence

Supported rule semantics are `FtmoProtection.LossModel=Static` and `ResetTimeZone=Europe/Prague`. Other models/timezones are rejected. Account amounts, margins, minimum days, start date and currency remain explicit configuration; account variant names do not verify commercial rules.

`EntryAnalysisIntervalSeconds` defaults to 15; protection defaults to 5 seconds. Periodic schedules avoid adding a full interval after execution. Timed-out analysis cannot overlap a call that ignores cancellation. A fatal background-loop error cancels sibling loops. Pausing entries preserves protection.

Risk decisions report daily, total and aggregate headroom. Profit attainment requires a flat, reconciled account and required trading days. Reaching the profit amount before minimum days pauses entries instead of forcing trades.

## Automatic calendar contract

Enable `NewsCalendar.Enabled` and set an HTTPS `FeedUrl` returning this normalized JSON from a trusted provider/integration, filtered for the traded currencies and required impact levels:

```json
{
  "generatedAtUtc": "2026-09-23T12:00:00Z",
  "coverageFromUtc": "2026-09-23T00:00:00Z",
  "coverageUntilUtc": "2026-09-24T00:00:00Z",
  "blackoutWindowsUtc": ["2026-09-23T14:00:00Z/2026-09-23T14:00:00Z"],
  "source": "Your verified calendar provider"
}
```

These dates illustrate the schema, not real events. Empty windows mean explicitly reviewed coverage with no relevant events. Configured before/after buffers apply. Refresh defaults to 300 seconds and maximum data age to 60 minutes. Failed refreshes never extend previous coverage. Restart requires a successful refresh. Automatic mode never silently falls back to static coverage.

No real provider URL, subscription or event mapping has been supplied. The adapter is implemented; a real provider connection remains configuration/integration work. Rules and news coverage in local source configuration have not been guessed or confirmed.

## Health, alerts and evidence

`status` reads bridge status without calling MT5. It reports protection age, bridge operation age, persisted halts, unresolved submissions, lifecycle counts and realized performance. Account data comes from the last protection result; its age matters. Entry eligibility is a health/configuration gate, not a promise that session, signal, spread or risk checks approve an order.

Files under `FtmoProtection.StateDirectory`:

- `health-<accountId>.json`: periodic health/performance report. Consumers must check its timestamp.
- `alerts.jsonl`: protection/failure alerts, also on stderr; controlled by `LocalAlertsEnabled` and `AlertRepeatMinutes`. No email, Slack or webhook delivery occurs.
- `execution-evidence-YYYYMMDD.jsonl`: candidate/rejection, risk and submission records, independent of optional operational logging.

Strategy versions include a parameter hash. SMC diagnostics include ATR, FVG size, displacement body ratio and stop distance; journal submissions include spread, lots and reserved risk. Fully reconciled positions contribute net expectancy, win rate, profit factor, realized R, realized drawdown and losing streaks, grouped by version/session/direction/setup type/ATR/spread. ATR groups are below 5, 5 to below 10, and 10+ pips; spread groups are up to 1 and above 1 pip. Entry and exit costs are included. Partial positions are excluded. Realized drawdown is not intratrade equity drawdown; old records without sizing are excluded from R calculations.

SMC filters and risk percentage were not loosened. Faster polling reduces scheduling-related misses; it does not prove a higher win rate. Parameter optimization still requires live evidence. There is no automatic optimizer or simulation mode.

## Explicit startup

After configuring the intended account/feed, an operator may explicitly run:

```powershell
python -B tools/mt5-bridge/supervise_bridge.py --terminal-path "<path to terminal64.exe>"
```

Install source `tools/mt5-bridge/requirements.txt` in the intended Python environment first; `tzdata` supplies Prague DST rules on Windows. Keep the working directory stable for `data/ftmo-bridge-state/<accountId>.json`. Do not start a second bridge on the same port.

`tools/run-live-supervised.ps1 -Executable <CLI exe> -Config <config path>` supervises the CLI with hidden child windows, backoff, and no retries for configuration errors. These scripts are not installed as OS services. Stop the supervisor to stop restart behavior. Stopping monitoring does not flatten positions.

## Structure and validation limits

`Program.cs` now handles configuration and routing. Composition, broker commands, lifecycle, legacy execution, console formatting and arguments have separate partial-class files. Protected execution uses its own coordinator and event adapter. Python separates gate scheduling, journal persistence, reconciliation and performance calculation.

Automated tests use broker/feed doubles. Real terminal reconnection, feed completeness, broker timing, unattended longevity and profitability remain unverified. Unsupported challenges remain blocked.

See [source validation results](unattended-validation.md) for executed checks and remaining external configuration.
