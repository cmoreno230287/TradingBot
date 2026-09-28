# FTMO implementation and validation report

**Historical report for the EMA candidate.** The user subsequently requested SMC. The current FTMO example selects that strategy; see [smc-strategy.md](smc-strategy.md). Reproduce the EMA results below with `appsettings.ftmo-pullback.example.json`.

Date: 2026-09-16. **Research candidate rejected for deployment on profitability evidence.** The implementation and offline evaluation are delivered; a profitable or challenge-passing strategy has not been demonstrated. No runtime deployment, broker connection, order creation or live configuration change was performed.

## Observed existing behavior

See [analysis-and-design.md](analysis-and-design.md) for the analysis recorded before implementation: project boundaries, entry points, all three prior strategies, full execution lifecycle, account/risk gaps, reporting, sessions, broker adapters and legacy simulation flaws. Existing executable tests passed before changes. The user's uncommitted pending-order changes remain intact. The Angular shell is unchanged. Configuration names such as partial-close and breakeven do not by themselves establish that those actions are implemented; the new strategy explicitly uses fixed broker SL/TP and timed/protective exits.

## Evidence and assumptions

The supplied image shows $5,000 maximum daily loss, $10,000 maximum loss, $5,000 profit target and two minimum trading days. It does not show initial capital, variant, deadline, instrument/news/overnight restrictions. See [source-rules.json](source-rules.json) for the structured transcription.

USD 100,000 is a **provisional simulation input from the existing project configuration**, not extracted from the image. The account clarification requested during implementation remains unanswered. `RulesConfirmed=false`, `AccountId=0` and `AllowLiveOrderCreation=false` prevent live use of the example profile. The January 1 challenge start is a placeholder, not an assertion about the user's account.

Daily reset uses Europe/Prague midnight with DST. Equity-based daily accounting is supported by [FTMO daily-loss documentation](https://academy.ftmo.com/lesson/maximum-daily-loss/). The implemented maximum-loss model is static initial capital, consistent with the documented [2-Step objectives](https://ftmo.com/en/trading-objectives/); **1-Step trailing loss rules are unsupported**. The supplied image's numeric rules take precedence over generic website numbers. Unknown account-specific restrictions remain unverified.

## Newly implemented strategy

`FtmoPullbackStrategyEngine` implements EURUSD H1/M5 trend pullback:

1. Only completed candles; 150-bar EMA warmup with default slow period 50. H1 EMA20 above/below EMA50, EMA20 slope aligned, and last H1 close on the trend side.
2. Weekday entries in configured New York-time London/New York sessions. Explicit allowed-session/day lists and configured news blackout intervals apply.
3. Previous M5 candle touches/crosses EMA20. Latest completed M5 candle closes beyond the preceding high/low in the trend direction, on the correct side of EMA20, with body at least 50% of range.
4. M5 ATR14 between 2 and 20 pips. Limit at confirmation-body midpoint. Stop beyond five-bar extreme plus one pip; stop distance 4–25 pips. Target 2R.
5. Deterministic symbol/direction/confirmation-time setup ID; no pyramiding, averaging, trailing stop or partial exits. Reject missing/stale/gapped recent execution data and crossed limit entries. Pending expiry 15 minutes; position timeout 240 minutes; flatten 15 minutes before the daily reset.

Strategy parameters were chosen before results and were not tuned against either period. EMA/ATR are implemented locally with decimal arithmetic; no indicator framework or new package dependency was added.

## Rule-to-control mapping

| Requirement | Configuration | Enforcement |
| --- | --- | --- |
| $5,000 daily maximum | MaximumDailyLossAmount, DailySafetyBufferAmount, InternalDailyLossPercent | Prague midnight balance minus account equity, including floating P&L/fees; prospective exposure checked in C# and again at MT5 submission |
| $10,000 total maximum | InitialBalance, MaximumLossAmount, TotalSafetyBufferAmount | Static equity floor, proposed and existing risk reservation; total-loss halt persists |
| $5,000 target | ProfitTargetAmount | Closed balance profit; no additional entries once reached; completed objective also requires flat account and minimum days |
| Two trading days | MinimumTradingDays, ChallengeStartUtc | Distinct Prague dates of entry deals, not exit dates; no forced trades to manufacture days |
| Capital preservation | RiskPercent=0.25, MaximumAggregateRiskPercent=0.5 | Broker-calculated loss per lot, commissions/slippage reserve, flooring to volume step, reject below minimum |
| Avoid overtrading | MaximumTradesPerDay=3, MaximumConsecutiveLosses=2, LossCooldownMinutes=60 | Account-wide deal reconstruction, pending reservations, one concurrent exposure |
| Exposure after restart | MaximumActiveTrades=1 | Reload all broker positions and pending orders, including other magic numbers; unknown/unprotected exposure blocks trading |
| Emergency protection | KillSwitch, MonitorIntervalSeconds=5, StateDirectory | Protection before signal/session decisions; cancel owned pending orders and close owned positions; persisted daily/total/kill halts |
| Duplicate/uncertain requests | Bridge account state file | Serialized final submissions; atomic, flushed reservation before broker send; no retry after ambiguous response |
| Broker volume/SL/TP/margin | Instrument metadata and final request | Directional SL/TP and RR validation, broker volume step, fresh quote, spread, order_calc_profit and order_check; no fallback to GTC for protected orders |
| Account-specific news/holding restrictions | Explicit session/news windows | Not shown in source; no claim of complete enforcement until account details/calendar supplied |

The internal daily threshold is 1% ($1,000), below the image's $5,000 daily limit. Both image loss limits also have $500 configured buffers. Remaining risk is measured from current equity to stops, including pending orders and cost reserves. Non-owned exposure counts but is never liquidated by this bot. Non-EURUSD exposure is treated as unknown rather than assigned an invented pip value.

## Architecture and execution

The new profile routes `start` and `analyze-and-createorder` through `FtmoCommands`. `FtmoRiskEngine` in Application is shared with simulation. MT5 infrastructure adds `ftmo/snapshot`, `ftmo/protect`, and `ftmo/orders`. Python repeats critical final checks against fresh broker data inside a lock; this small duplication is deliberate to close the time gap between client sizing and submission.

The protection loop runs outside entry sessions, during cooldown and without a signal. Account snapshots include all deal cash effects, including entry commissions; daily entry counts deduplicate partial fills by position ID. Broker SL/TP remains active when the application is unavailable. Manual order commands and the legacy MT5 submission method are blocked while protection is enabled. The legacy `backtest` command refuses protected profiles so its results cannot be mistaken for chronological FTMO validation.

Logs include protection actions/failures, account balance/equity, risk decisions, monetary risk, sizing, signal prices, objectives and order results. Tracking reuses the existing CSV writer. State files and account IDs remain runtime data, excluded from source control.

New files: `TradingBot.Application/FtmoProtection.cs`, `TradingBot.Strategies/FtmoPullbackStrategyEngine.cs`, `TradingBot.Infrastructure/MarketData/OfflineCsvMarketDataProvider.cs`, `TradingBot.Backtesting/FtmoBacktestingEngine.cs`, `TradingBot.CLI/FtmoCommands.cs`, `TradingBot.CLI/appsettings.ftmo.example.json`, `TradingBot.Tests/FtmoTests.cs`, `tools/mt5-bridge/ftmo_guard.py`, `tools/mt5-bridge/test_ftmo_guard.py`, and `docs/ftmo/*`.

Modified for this requirement: `TradingBotOptions.cs`, `OptionsValidator.cs`, CLI `Program.cs`, `MT5BridgeClient.cs`, test `Program.cs`, and `mt5_bridge.py`. Other changes shown by Git predate this work.

## Backtest method and results

Local cached MT5 EURUSD H1/M5 CSV only; no historical download or broker call. The loader merges caches deterministically; **zero conflicting duplicate rows** were found. Development period chosen as calendar 2025; holdout January 1–May 22, 2026. Each simulation starts a fresh provisional $100,000 account.

The chronological simulator makes decisions at completed-bar time, permits fills only in later bars, updates realized P&L only on exit and observes adverse intrabar equity. It assumes one-pip spread, $7 round-trip commission/lot charged on entry, and one-pip adverse stop/forced-exit slippage. Both-side hits resolve stop first; entry-bar TP is deferred because OHLC cannot prove event ordering. Price gaps can exceed stop loss and cause a failed challenge. Pending reservations, daily limits, cooldown, time exits, reset boundaries and floating-equity objectives are evaluated.

| Metric | Development 2025 | Holdout Jan–May 2026 |
| --- | ---: | ---: |
| Actual first observation | Jan 21, 11:50 UTC | Jan 2, 00:05 UTC |
| Actual final close | Dec 31, 23:55 UTC | May 21, 23:55 UTC |
| M5 candles | 70,201 | 28,598 |
| Missing weekday slots between observations | 792 | 200 |
| Trades | 92 | 100 |
| Wins / losses | 21 / 71 | 28 / 72 |
| Win rate | 22.83% | 28.00% |
| Net result | **-$9,458.29** | **-$7,365.84** |
| Profit factor | 0.4445 | 0.5696 |
| Average win | $360.44 | $348.18 |
| Average loss | -$239.82 | -$237.71 |
| Planned reward/risk | 2.00 | 2.00 |
| Maximum equity drawdown from peak | $9,466.83 | $8,810.06 |
| Maximum daily equity loss | $496.12 | $498.56 |
| Maximum loss from initial capital | $9,458.29 | $8,810.06 |
| Longest win / loss streak | 3 / 13 | 3 / 9 |
| Expectancy per trade | -$102.81 | -$73.66 |
| Distinct entry days | 68 | 71 |
| Expired/cancelled unfilled orders | 53 | 35 |
| Risk per trade, including reserve | 0.25% | 0.25% |

Streaks in the table span the whole sample; the daily two-loss cap resets each Prague day. Development eventually rejected 316 otherwise valid signals because proposed risk exceeded total-loss headroom; loss limits were not loosened to generate more trades.

Both simulations: **TARGET_NOT_REACHED**. Neither reached the target in its requested period; minimum days were met. No supplied daily/total loss violation was observed **within the OHLC model**. This is not a passed challenge and not proof of continuous compliance. Complete machine-readable summaries, rejection counts and limitations are in [validation-results.json](validation-results.json); full trade records are in local `reports/ftmo/development.json` and `holdout.json`.

## Validation performed

- `dotnet build TradingBot.sln --no-restore -v minimal`: zero warnings/errors.
- `dotnet run --project TradingBot.Tests --no-build`: 36 tests passed (18 existing, 18 new).
- `.venv-mt5/Scripts/python.exe -B -m unittest discover -s tools/mt5-bridge -p test_ftmo_guard.py -v`: 19 tests passed.
- Protected profile `help`: configuration validated successfully.
- Protected profile `start once`: refused before contacting the bridge, as expected for an unconfirmed offline profile.
- Development and holdout commands in [runbook.md](runbook.md) executed successfully with fixed parameters.

Coverage includes volume flooring/costs/minimums, proposed daily/total loss, aggregate exposure, malformed prices, active-trade limits, daily counts, streaks, cooldown, kill switch, minimum days, DST 23/25-hour resets, completed-bar signals, missing/high-volatility data, chronological fills, gap breaches, final account-state changes, broker rejection, ambiguous send failure, corrupted state, duplicate/restart persistence, cancellation ownership, failed protective close, entry commissions, partial fills, missing history, stale quotes and account mismatch.

## Assumptions / limitations and release decision

1. The image does not establish account size/variant or additional restrictions. Live use remains gated. Unknown restrictions cannot be certified through configuration guesses.
2. Cached data begins after the requested development start and has gaps/holidays/rollovers. Broker tick history and variable spread/news archives were not available in the offline dataset. No continuous intraday compliance claim is made.
3. The simulator uses conservative OHLC ordering and assumed USD pip value/lot constraints; live uses broker monetary calculations. Daily/time exits are sampled at M5 boundaries in simulation and by the configured monitor interval live. Broker latency, slippage, liquidity and process/network outages cannot be eliminated by preflight checks.
4. Actual MT5 order_check, live fills, cancellation/flattening support and account history completeness were not exercised against the terminal. Fault tests use controlled fake broker responses. A dedicated demo integration test remains necessary before release.
5. A bridge process serializes protected requests, and the client takes an account lease; this does not lock manual terminal trades, other EAs, separate bridge processes, or other machines. Use one dedicated bridge/account process and preserve its state directory. Legacy endpoints are retained for existing profiles and are not a security boundary against an independent client.
6. A missing broker/history connection prevents reliable account reconstruction or liquidation; no new trade is sent, errors are logged, and monitoring retries. Broker SL/TP and native pending expiry are the remaining protection during outages. Currency/symbol mismatches and unprotected foreign exposure block new trades.
7. Total-loss and kill-switch halts require deliberate operator reconciliation. Daily protection automatically expires at the next Prague boundary. Profit target reached without minimum days stops new risk but is reported incomplete; the bot does not manufacture trading days.
8. Both test periods have negative expectancy and fewer than the legacy recommended 500 trades. The strategy is **not approved for deployment**. Its poor results are retained rather than hidden by optimizing against the holdout. A separate research iteration and new untouched validation data would be needed to establish a better candidate.

Implementation, automated fault testing and available offline backtests are complete. Account-specific rule confirmation, terminal integration and any claim of profitable FTMO suitability remain explicitly unvalidated. Historical success, if later obtained, would not guarantee future profitability or passing a challenge.
