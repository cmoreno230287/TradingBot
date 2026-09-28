# SMC continuation strategy — 2026-09-21

Added engine `FtmoSmcContinuation`, strategy ID `EURUSD_FTMO_SMC_CONTINUATION_V1`, with profile `TradingBot.CLI/appsettings.ftmo-smc-continuation.example.json`. It is an additional strategy, not a replacement for legacy V2 or the strict protected sweep strategy. Live execution remains disabled and the published configuration is unchanged.

## Signal rules

- Only completed H1/M5 bars in configured London/New York entry windows; reject stale/malformed data and gaps in the M5 analysis window.
- H1 direction is the most recent first close through a confirmed swing high/low within 48 bars. Pivots need two completed bars on each side, and must be confirmed before the breaking candle. The latest opposing break changes direction. This does not require two successive higher highs and higher lows.
- M5 must make its first close through the latest confirmed internal swing in the H1 direction. That candle needs at least 50% body/range and range at least the prior 14-bar ATR.
- The break must be the middle candle of a fresh three-candle FVG of at least 0.5 pip. Entry is its midpoint. A liquidity sweep is not mandatory for this continuation model.
- Stop is beyond the extreme of the six bars before the impulse plus the impulse itself, with a one-pip buffer. Stop distance must be 4–25 pips. Reject invalidated origin on the confirming bar. Target is 2R.
- The profile disables premium/discount and opposing-liquidity target-room filters. They remain supported options. This increases opportunities and permits targets beyond prior H1 liquidity; it does not imply better expectancy.
- IDs use symbol, direction and break timestamp, with a separate continuation namespace. Repeated analysis cannot create another protected order for the same break.

Parameters use the shared `FtmoSmc` section. `LiquidityLookback` controls the continuation stop leg; `MaximumSweepAgeBars` and `MaximumFvgDelayAfterBreakBars` apply only to the separate sweep engine. This engine always requires the fresh FVG middle candle to be the break candle.

## Protective policy

Risk stays at 0.25% including cost reserves, with one account-wide exposure, maximum three trades per Prague day, two-loss daily stop, 60-minute loss cooldown, 15-minute pending expiry and 240-minute maximum holding time. Existing balance/equity headroom checks and live gates remain required. More technical setups do not override these limits. Account variant and the supplied numeric objectives remain provisional/unconfirmed.

## Results

One parameter set was defined before testing; it was not retuned to the comparison results. Existing local EURUSD H1/M5 data was used. The 2026 period is previously seen comparison data, not a new holdout.

| Period / costs | Unique valid setups | Orders | Fills | Net USD | Profit factor | Max equity drawdown USD |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 2025 development | 285 | 190 | 85 | -9,429.97 | 0.411 | 9,783.54 |
| Jan–May 2026 comparison | 105 | 104 | 39 | -3,276.35 | 0.524 | 3,276.35 |
| Same comparison, higher costs | 107 | 106 | 36 | -4,663.60 | 0.318 | 4,663.60 |

Strict sweep baseline counts were 2 setups/1 fill in development and 3 setups/1 fill in comparison. Thus frequency increased substantially, but the new strategy has negative measured expectancy. Neither period reached the supplied $5,000 target. No modeled external daily/total loss threshold was breached; development exhausted most available loss headroom and rejected 92 subsequent proposals for insufficient total-loss headroom. That is not evidence of Challenge suitability. This profile should not be activated for a Challenge on these results.

Normal costs: one-pip spread, $7 per lot commission, one-pip slippage reserve. Stress: 1.5 pips, $10, two pips. Different fills and exposure durations alter which later bars can be analyzed; setup counts are unique IDs considered while the simulator is eligible to analyze, not an unrestricted signal scan. Cooldown/headroom rejections explain differences between valid setups and submitted orders. Targets are gross 2R, so net realized reward/risk is lower after costs and forced exits.

Development contains 70,201 M5 bars with 792 missing weekday bars; comparison has 28,598 with 200 missing. OHLC fills, spread assumptions, incomplete history and absent populated historical news blackouts limit inference. No live or demo orders were sent. Automated tests establish software behavior, not profitability.

## Reproduce

From `C:/Projects/TradingBot`:

```powershell
dotnet run --project TradingBot.CLI -- ftmo-backtest config=TradingBot.CLI/appsettings.ftmo-smc-continuation.example.json from=2025-01-01T00:00:00Z to=2026-01-01T00:00:00Z output=reports/ftmo/continuation/development.json
dotnet run --project TradingBot.CLI --no-build -- ftmo-backtest config=TradingBot.CLI/appsettings.ftmo-smc-continuation.example.json from=2026-01-01T00:00:00Z to=2026-05-22T00:00:00Z output=reports/ftmo/continuation/comparison.json
```

Full trade outputs are local generated artifacts under `reports/ftmo/continuation/`. Summaries and profile fingerprint are preserved in [continuation-results.json](continuation-results.json).
