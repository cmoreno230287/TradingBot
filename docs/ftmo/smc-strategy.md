# FTMO Smart Money Concepts strategy

The FTMO example profile now selects `EURUSD_FTMO_SMC_V1` / `FtmoSmc`, following the user's request to use SMC. The previously evaluated EMA profile is retained separately as `TradingBot.CLI/appsettings.ftmo-pullback.example.json` so its historical results remain reproducible. Existing production appsettings and the legacy V2 SMC engine are unchanged.

## Rules fixed before evaluation

- EURUSD, completed H1 structure and M5 execution candles. Strict confirmed pivots require two completed candles on each side; no fallback from unclear structure to EMA or consecutive closes.
- H1 direction requires both higher highs/higher lows for buys, or lower highs/lower lows for sells, using the two most recent confirmed swing highs and lows within 48 completed H1 candles. Mixed structure rejects entries.
- M5 sweep must wick beyond the preceding 12-candle liquidity extreme and close back inside. Sweep must precede the displacement candle by at most six M5 bars.
- The displacement candle must be the first close after the sweep beyond the most recent opposing M5 swing already confirmed before the sweep (CHOCH/BOS proxy). At least 60% body/range and range at least 1.2 times prior ATR14 are required. A new breach of the swept extreme invalidates the setup.
- The most recent three completed M5 candles must form a directional FVG at least 0.5 pips wide, with displacement as the middle candle. Entry is the FVG midpoint. Generating only on fresh FVG confirmation avoids recycling previously mitigated gaps.
- Buy below / sell above the midpoint of the last 48 H1 candles by default. Stop beyond the sweep extreme plus one pip, with a 4–25 pip stop distance. Target fixed 2R and must fit before the latest opposing confirmed H1 swing. Premium/discount and target-room filters have explicit configurable switches.
- Configured weekday/session/news filters apply. Missing recent M5 candles, malformed OHLC, stale candles or incomplete structure produce no signal. The setup ID derives from symbol, direction, sweep time and structure-break time.
- FTMO protection remains independent: 0.25% per-trade risk, account-wide exposure checks, loss headroom, costs/volume flooring, daily limits/cooldown, broker-native SL/TP and pending expiry, timed/pre-reset/protective exits, and durable duplicate suppression. Pending orders retain the existing 15-minute protected expiry; they are not dynamically reanalyzed for premium/discount changes while pending.

Parameters are under `FtmoSmc` in the example profile. No EMA is used for direction or entry. ATR measures displacement strength only. This is a precise algorithmic interpretation of SMC, not a claim that discretionary SMC labels have a unique universal definition.

## Evaluation procedure

Use the same development and comparison periods as the earlier strategy, with no parameter tuning after results. The 2026 period was already inspected for the EMA candidate, so it must not be presented as a newly untouched holdout for this research iteration. A future release decision needs additional unseen data and actual demo integration.

```powershell
dotnet run --project TradingBot.CLI --no-build -- ftmo-backtest config=TradingBot.CLI/appsettings.ftmo.example.json from=2025-01-01T00:00:00Z to=2026-01-01T00:00:00Z output=reports/ftmo/smc-development.json
dotnet run --project TradingBot.CLI --no-build -- ftmo-backtest config=TradingBot.CLI/appsettings.ftmo.example.json from=2026-01-01T00:00:00Z to=2026-05-22T00:00:00Z output=reports/ftmo/smc-comparison.json
```

The image's missing starting balance/account variant remain unconfirmed. The example continues to use provisional USD 100,000 for simulation with live execution disabled. Prior data-quality, execution and compliance limitations in `validation-report.md` apply equally to SMC evaluation.

## Results (2026-09-16)

| Metric | Development 2025 | Comparison Jan–May 2026 |
| --- | ---: | ---: |
| Filled trades | 1 | 1 |
| Wins / losses | 0 / 1 | 0 / 1 |
| Net result | -$249.26 | -$249.60 |
| Profit factor | 0 | 0 |
| Expectancy / average loss | -$249.26 | -$249.60 |
| Maximum equity drawdown | $249.26 | $249.60 |
| Maximum daily loss | $249.26 | $249.60 |
| Planned reward/risk | 2 | 2 |
| Trading days | 1 | 1 |
| Unfilled expired orders | 1 | 2 |
| Profit target reached | No | No |
| Minimum two days met | No | No |
| Daily / total loss breach in OHLC model | No / No | No / No |

Both runs return `TARGET_NOT_REACHED`. The sample is far too small to establish profitability or suitability for the challenge. Defaults are highly selective: H1 structure, premium/discount, sweep, first displaced structure break, fresh FVG and liquidity room must all align. Parameters were not loosened after seeing these results. This implementation is not ready for live deployment.

All 48 .NET tests passed, including 12 new SMC cases covering buys/sells, missing sweep, missing CHOCH, weak displacement, tiny FVG, premium/discount and liquidity-room toggles, unfinished candles, unconfirmed pivots, gaps/staleness and configuration guards. Solution build passed with zero warnings/errors. The existing Python guard tests remain applicable to this strategy-independent execution path.

Machine-readable results and profile hash: [smc-validation-results.json](smc-validation-results.json). Full local trade records: `reports/ftmo/smc-development.json` and `reports/ftmo/smc-comparison.json`. Data coverage remains 70,201 / 28,598 M5 candles with 792 / 200 missing weekday slots and zero conflicting cache rows. No claim of continuous compliance can be made from that incomplete OHLC history.
