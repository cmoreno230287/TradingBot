# FTMO research commands and operational notes

Archived research runbook: simulation commands below were removed in the live-only implementation. Use [live-only operations](live-only-operations.md) for current commands and deployment.

Run from `C:/Projects/TradingBot`. The supplied profile is separate from the existing live configuration and now selects [Smart Money Concepts](smc-strategy.md). The earlier EMA candidate and its results are documented in [validation-report.md](validation-report.md) and remain reproducible with `appsettings.ftmo-pullback.example.json`.

## Offline validation

See [the protected SMC upgrade](protected-upgrade.md) for the current correctness fixes, execution checks and comparison results. Initial balance USD 100,000 is now user-confirmed; account variant remains unconfirmed. Live execution remains disabled.

The additional [SMC continuation strategy](smc-continuation.md) uses `appsettings.ftmo-smc-continuation.example.json`. It finds more technical setups but lost money in both tested periods; keep it research-only. It uses the same protected backtest command and requires `FtmoProtection`.

The optional `appsettings.ftmo-smc-research.example.json` enables the frequency experiment described in [improvement-review.md](improvement-review.md). It improved development setup counts but failed to improve the 2026 comparison. The strict example remains the default reference. To reproduce the research candidate, substitute that configuration in the backtest commands below; keep live execution disabled.

```powershell
dotnet build TradingBot.sln --no-restore
dotnet run --project TradingBot.Tests --no-build
.\.venv-mt5\Scripts\python.exe -B -m unittest discover -s tools/mt5-bridge -p test_ftmo_guard.py -v
dotnet run --project TradingBot.CLI --no-build -- help config=TradingBot.CLI/appsettings.ftmo.example.json
dotnet run --project TradingBot.CLI --no-build -- ftmo-backtest config=TradingBot.CLI/appsettings.ftmo.example.json from=2025-01-01T00:00:00Z to=2026-01-01T00:00:00Z output=reports/ftmo/smc-development.json
dotnet run --project TradingBot.CLI --no-build -- ftmo-backtest config=TradingBot.CLI/appsettings.ftmo.example.json from=2026-01-01T00:00:00Z to=2026-05-22T00:00:00Z output=reports/ftmo/smc-comparison.json
```

Optional `data=[path]` selects an offline cache root containing `EURUSD/H1/*.csv` and `EURUSD/M5/*.csv`. The command never falls back to online data. A supplied nonexistent `config` fails rather than creating defaults. Results contain per-trade records, objective status, rejection counts and limitations.

## Configuration ownership

New strategy settings live under `FtmoSmc` (`FtmoPullback` for the archived EMA profile); protective policy under `FtmoProtection`. `Strategies.ActiveStrategyId` selects the strategy. The protected path uses its own per-trade risk, cooldown/day limits and holding rules; legacy `DailyTradingStop` and funded-challenge options are not used by this path. Session/news/pip/spread options remain shared. `backtest` refuses a protected profile; use `ftmo-backtest`.

Before any future demo integration, independently confirm the account variant, starting balance/currency, start timestamp, image limits and additional restrictions. Set a specific `Brokers.MT5.AccountId`, dedicated magic number and accurate costs. `RulesConfirmed` and `AllowLiveOrderCreation` are deliberate live gates; changing them is not a profitability endorsement. This task did not enable them.

Both `mt5_bridge.py` and its new sibling `ftmo_guard.py` are needed in a deployed bridge. Existing `C:/TradingBot` was not changed. Publishing the .NET app alone does not establish that these Python files/configuration were deployed.

## Protection and restart state

The running protected loop checks for `kill.switch` inside configured `FtmoProtection.StateDirectory` each cycle. Creating that file requests protective cancellation and flattening of this magic number's exposure and persists a halt. The bridge writes account-specific state under **its working directory** at `data/ftmo-bridge-state/<accountId>.json`; the client lease/kill file uses its own configured state directory. Use stable working directories across restarts.

Do not delete state as a way to restart: it contains submitted/uncertain setup IDs. After an investigated total-loss/kill halt, an operator must reconcile the actual account and orders before resetting the halt fields while retaining the attempts ledger. Daily halts expire automatically at the next supplied Prague-day boundary. Corrupted/unwritable state prevents protected submission.

`start once` performs one guarded cycle and exits; it is not continuous protection. `start` without `once` is the monitoring loop. Ctrl+C stops monitoring and leaves broker-native SL/TP/expiry in place; it does not promise account flattening. The legacy `stop` command does not control another running process. Use the monitored kill file for a flatten request while connectivity is available.

Foreign magic-number trades are included in account exposure/counts and can block the bot. They are never automatically closed. One dedicated bridge process and account are assumed; external traders/EAs can invalidate risk state after any check. Keep bridge endpoints local as in the existing deployment.
