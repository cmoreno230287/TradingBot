# FTMO analysis and design (before implementation)

## Observed existing behavior

The solution consists of .NET 10 projects: CLI (composition root and commands), Application (interfaces, options, validation and risk), Domain (immutable market/trade records), Strategies (three engines), Infrastructure (MT5 HTTP/Python and cTrader OAuth/WebSocket adapters, CSV history cache, sessions and news), Backtesting, Reporting and Shared. Tests are an executable assertion harness. Angular 20/Material is a mock dashboard without an execution API. Persistence is JSON configuration, CSV history/trades and JSONL operation logs; there is no database.

`Program.cs` loads and normalizes options, validates them and builds services manually. `start` connects the configured broker and repeats `ExecuteAnalyzeAndCreateOrderAsync`. This checks permissions, market hours and daily win/loss stop; obtains a strategy signal; invalidates pending orders; gets account state; calls `RiskManager.Evaluate`; checks spread, entry distance, stale orders and active count; then calls the broker-specific order adapter. Tracking records completed MT5 positions to rotating CSV. Broker SL/TP closes trades; there is no continuous protective flattening loop. `stop` only prints a message. Ctrl+C stops the running process.

Signals contain direction, entry, SL, TP, RR, reason and optional setup ID. Legacy SmartMoney uses H1 (optionally D1) swing bias, M5 sweep, structure break and FVG. HourlySweepM1Fvg uses completed H1 extremes, M1 reclaim/MSS and FVG. Active SmcLiquiditySweepChoch uses H1 bias, D1 optional alignment, M5/M1 sweep, M1 CHOCH/BOS, optional displacement, FVG first then order block, sweep/zone SL plus buffer and configured R target before opposing liquidity. The V2 engine hardcodes H1/D1/M5/M1 and applies liquidity-target room even when UseLiquidityTargetFilter is false. No profitability is established by this analysis.

Risk sizing is balance times configured percent divided by pip stop distance and configured pip value, rounded to two decimals. Existing guards check current daily/weekly realized losses, consecutive losses/losing days, and funded challenge equity loss and profit buffer. The active source config specifies 1% risk, 2R, one active trade, daily stop after one win/two losses. MT5 pending entry limit is 12 pips; distance cancellation is 16 pips; invalidation/direction/age cancellation are disabled. These are existing user settings, not proposed defaults.

Risk gaps: no proposed-risk reservation against daily/total headroom; no account-wide aggregate SL exposure; pip value and volume granularity are assumed; rounding may increase risk; manual order commands bypass the main risk engine; cTrader account-risk data is a configured placeholder; MT5 daily accounting uses UTC and exit deals (omitting entry commissions); no durable duplicate-submission protection; no kill switch/monitor before session or invalid-signal returns. Open positions from other bots must count toward account risk even when this bot must not close them.

Legacy backtesting loads cached/provider D1/H1/M5/M1, evaluates every five minutes and resolves an entire future outcome per signal. It can backdate entry to FVG creation, expose unfinished bars and book future P&L early. Candle fallback uses SL-first if both boundaries are touched, but does not model floating equity, costs or daily FTMO reset. These results cannot validate FTMO compliance. A chronological simulator is necessary for the new strategy; legacy behavior will remain available for existing workflows.

Error handling uses Result for adapters, exceptions for unavailable candles, a resilient start loop, and JSONL cycle IDs. Reporting provides journal CSV, closed-trade CSV, recent setups, SVG and a minimal PDF generator. News filtering uses manually configured UTC windows, not an economic calendar. Existing 18 offline tests and solution build passed before this work. Uncommitted user changes in options/CLI/MT5 pending controls are preserved.

## Supplied evidence and unknowns

Image: `C:/Codex/Codex_Agents/Expert_Trader/Resources/FTMO_AccountChallenge_Info.png`.

| Rule observed in image | Value | Control planned |
| --- | ---: | --- |
| Minimum trading days | 2 | Distinct Prague calendar entry days; target completion needs flat account |
| Maximum daily loss | USD 5,000 | Account equity versus Prague midnight balance; preflight includes all residual SL/pending risk and costs |
| Maximum loss | USD 10,000 | Static initial balance floor; configurable safety buffer and latched protection |
| Profit target | USD 5,000 | Closed balance profit, flat account, minimum days; never equate a buffered stop to passing |

Starting balance is NOT printed in the image. Existing configuration specifies USD 100,000; use that only as an explicitly labelled provisional simulation input until confirmed. Account variant, challenge start, deadline, news/overnight/instrument restrictions are not shown and must not be invented. A user clarification is pending.

Supplementary source, not a replacement for the image: https://ftmo.com/en/trading-objectives/ and https://academy.ftmo.com/lesson/maximum-daily-loss/ describe CE(S)T midnight reset and equity including floating P&L, commissions and swaps. Prague time must follow DST. Static maximum loss is the selected model; 1-Step trailing rules are not assumed supported.

## Deterministic proposed strategy

New opt-in `FtmoPullback` engine, EURUSD only initially: completed H1 EMA fast/slow alignment and fast-EMA slope; completed M5 pullback to fast EMA followed by directional close breaking the preceding bar extreme, with minimum body/range and ATR volatility bounds. Limit entry at confirmation body midpoint, SL outside recent pullback extremes plus pip buffer, target fixed 2R, stop-distance limits, no entry when the current price has already crossed the limit, short pending lifetime, one active account trade, no averaging, no trailing stop. Configurable NY sessions, weekday filter and explicit news blackout windows. Every signal has a deterministic completed-bar setup ID. Missing/stale/noncontiguous execution data produces no signal.

Conservative profile: 0.25% per trade, 0.5% aggregate risk ceiling, 1% internal daily loss stop, 3 trades/day, two consecutive losses/day, 60-minute loss cooldown, USD 500 buffers below supplied daily/total limits. Risk and strategy stay separate. Proposed SL risk includes commission and slippage reserve; volume floors to broker step and rejects below minimum. Unsupported account currency/broker or unknown exposure fails closed. Monitoring runs before signal/session exits, cancels owned pending orders and closes owned positions at protective thresholds/kill switch; foreign positions are counted but never liquidated automatically. Total-loss/kill-switch protection persists across restart. Network outages remain a limitation; broker SL is mandatory.

## Validation plan

Add critical risk, DST, sizing, duplicate/restart, invalid-data, strategy and chronological simulation tests. Use existing cached EURUSD history offline, split by date before viewing results, report costs, mark-to-market/adverse-bar equity, violations, target/minimum days, rejections and data gaps. OHLC ordering and historical spread/news quality must be reported as limitations. No tuning to make the holdout pass. Do not deploy or enable live orders as part of offline validation.
