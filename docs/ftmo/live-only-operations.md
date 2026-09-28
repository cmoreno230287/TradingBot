# Live-only MT5 operation

The source default now selects `EURUSD_FTMO_SMC_V1` with FTMO protection and 0.25% risk on the user-confirmed USD 100,000 initial balance. Both live entry switches default to true. Actual orders are sent by `start` when a valid setup and all account/risk checks pass; there is no simulation or paper mode and no per-order confirmation prompt.

The installed `C:/TradingBot` runtime has not been modified or started by this implementation. Source configuration was backed up under `reports/live-migration/` before migration. Existing explicit false switches are preserved by migration; missing switches use the live-enabled defaults.

## Required account configuration

- Set the intended `Brokers.MT5.AccountId`, terminal connection and dedicated magic number. Never infer account identity from the currently connected terminal.
- Confirm `FtmoProtection.AccountVariant` (`Challenge`, `Verification` or `FreeTrial`), `ChallengeStartUtc`, currency and numeric limits, then set `RulesConfirmed=true`. Only the configured static loss model is supported; a name alone does not verify applicable rules. The current configuration remains unconfirmed, so new entries are blocked.
- If `UseNewsFilter=true`, configure the automatic HTTPS calendar adapter or populate reviewed `NewsCoverageFromUtc`/`NewsCoverageUntilUtc` and event windows. An empty event list is valid only inside explicitly reviewed coverage. Stale/missing coverage blocks new entries. See [unattended improvements](unattended-improvements.md) for the feed contract, health reporting, watchdog and recovery behavior.
- Configure `BrokerMarketClosuresUtc` for verified non-weekend market closures. H1 data checks use the standard Friday 17:00 to Sunday 17:00 New York closure plus these intervals. Unexpected missing data blocks entries.

## Commands

```powershell
dotnet run --project TradingBot.CLI -- help
dotnet run --project TradingBot.CLI -- status
dotnet run --project TradingBot.CLI -- analyze
dotnet run --project TradingBot.CLI -- start
```

All commands accept `config=<path>`. `help` reads configuration locally. `status` reads account data from the bridge and has no trading side effects. `analyze` reads market data without submitting. `start` performs real protected execution; `start once` is one cycle and does not provide continuous monitoring. Ctrl+C exits monitoring and does not flatten positions; native broker SL/TP/expiry remain. The legacy `stop` command does not stop another process.

`LiveTradingEnabled=false` or `MT5.AllowLiveOrderCreation=false` blocks new entries while the protected `start` loop continues managing existing owned exposure. Account ID is still required for protection. The `kill.switch` file under `FtmoProtection.StateDirectory` requests owned cancellation/flattening and persists a halt. Do not remove order-attempt history or safety halt fields merely to restart.

## Execution and recovery

Protection, analysis and reporting have independent schedules. New entries require a fresh successful protection cycle in both .NET and Python. Native terminal/network calls can still block; software timing is not a guaranteed hard real-time bound. Reporting runs once per minute independently of protection.

Signals use completed bars. Before submitting, the bridge reads ticks since FVG confirmation, rejects missing/stale coverage and an entry already touched, then validates quote side, broker distance/increments, risk and news coverage. Tick completeness is limited to the broker feed; the bot cannot reconstruct missing ticks or prevent prices changing after the final check.

Reservations are durably written before order submission. Unknown responses are not retried. On later protection cycles, orders/deals with the stable `FTMO<client-id>` comment reconcile the attempt. Unmatched exposure is conservatively cancelled and unresolved attempts pause new entries until reconciled. Brokers may modify comments, requiring an operator to check actual tickets/history. No automatic guessed reset is implemented.

Partial-exit reports allocate entry commission/fees by closed volume. CSVs represent individual exit deals and retain stable deal IDs. Existing historical CSV rows are not rewritten; old rows may omit entry costs. Risk snapshots include account-wide floating exposure and deal costs independently of reporting.

## Package and deployment

```powershell
./tools/package-live.ps1
```

This creates a new versioned directory under `reports/live-release/` containing the CLI, configuration, both Python bridge modules and a SHA-256 manifest. It does not copy to `C:/TradingBot`, start the bridge or submit an order. A compatible .NET 10 runtime, Python environment with MetaTrader5 and configured terminal are still required.

Before a future installation, stop the existing bot/bridge deliberately, back up binaries/configuration and state, and replace .NET and Python components together. Keep the bridge working directory stable: state remains at `data/ftmo-bridge-state/<accountId>.json`. Verify `help`, read-only `status`, account identity and readiness before `start`. Publishing by FolderProfile now also includes the Python modules, but does not restart a running bridge.

Rollback restores binaries/configuration while preserving attempt records and halts. New attempt states include `unknown`, `reconciled` and `filled`; do not downgrade to an older executable/guard that does not understand their semantics without reconciling exposure. Manifest hashes verify package contents; they do not establish profitability.

## Removed features and validation

Backtesting, FTMO simulation, learning reports, historical setup scans and standalone history-download commands were removed. Live startup no longer constructs historical-cache providers. Saved data/reports and archived research documentation are preserved. `Backtesting` settings in older external configurations produce migration diagnostics and are ignored.

Automated strategy, risk, permission, concurrency, recovery and bridge tests remain development-only and use fake adapters. No real broker order was used for validation. Earlier research did not establish a profitable edge; removing the simulator does not change that evidence. Account confirmation and live execution validation remain outstanding.
