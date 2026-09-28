# SMC improvement review — 2026-09-16

## Results and decision

The optional `TradingBot.CLI/appsettings.ftmo-smc-research.example.json` contains the development-selected candidate: six-bar liquidity lookback, up to three bars between the displaced structure break and FVG middle candle, and the additional H1 premium/discount filter disabled. Confirmed H1 direction, sweep/reclaim, displaced first break, fresh FVG, opposing liquidity room and all protective limits remain required. The strict profile and running legacy configuration remain unchanged. This candidate is for research, not approved Challenge execution.

| Profile / period | Unique setups | Filled trades | Net USD | Profit factor | Max equity drawdown USD |
| --- | ---: | ---: | ---: | ---: | ---: |
| Strict / 2025 development | 2 | 1 | -249.26 | 0 | 249.26 |
| Sequential / 2025 development | 2 | 1 | -249.26 | 0 | 249.26 |
| Continuation / 2025 development | 8 | 4 | 293.03 | 1.589 | 546.64 |
| Local liquidity / 2025 development | 15 | 8 | 542.80 | 1.544 | 627.54 |
| Strict / 2026 comparison | 3 | 1 | -249.60 | 0 | 249.60 |
| Local liquidity / 2026 comparison | 3 | 1 | -249.60 | 0 | 249.60 |
| Local liquidity / 2026 higher costs | 3 | 1 | -249.66 | 0 | 249.66 |

The local-liquidity candidate was selected on development results before the new comparison runs. Development frequency improved 7.5 times, but comparison frequency did not improve. Every run ended TARGET_NOT_REACHED; no modeled daily/total loss limit was violated. Development candidate return was only 0.543% on the provisional $100,000 account. Eight development trades and one comparison trade cannot establish a reliable edge or a probability of passing. No further tuning to this comparison period was performed.

Higher costs used 1.5-pip spread, $10 commission per lot and 2-pip slippage reserve, versus 1 pip, $7 and 1 pip normally. Cost-aware sizing changes lot size, so higher assumed costs do not necessarily increase each losing trade's cash loss proportionally.

The development cache contains 70,201 M5 bars and 792 missing weekday bars; comparison contains 28,598 bars and 200 missing weekday bars. Comparison ends at exclusive 2026-05-22 UTC and is previously seen data, not an untouched holdout. Existing simulator assumptions and data limitations still apply. Additional unseen data and demo execution evidence are needed before reassessing deployment; increasing risk would not establish an edge.

Machine-readable summaries and configuration hashes are in [improvement-results.json](improvement-results.json). Full local outputs and the four experiment configurations are under `reports/ftmo/improvement/` (ignored generated artifacts). Build and automated strategy/protection checks validate implementation, not profitability.

## Configuration and runtime findings

The normal source `TradingBot.CLI/appsettings.json` and published `C:/TradingBot/appsettings.json` still select legacy `SmcLiquiditySweepChoch` V2, at 1% risk. `FtmoProtection` is absent/disabled there. The separate `appsettings.ftmo.example.json` selects the protected SMC candidate at 0.25% risk. These are different strategies and execution paths; editing the research profile does not update the running published bot.

The runtime log snapshot examined contained 2,151 cycles: 1,594 strategy analyses all rejected with the same generic V2 setup reason, and 557 outside market hours. Those are repeated analyses, not 1,594 independent opportunities. The log lacks individual stage diagnostics, so it cannot tell which technical filter rejected each V2 candidate.

The protected SMC engine likewise had broad final rejection messages. It required the first CHOCH/BOS candle to be exactly the FVG's middle candle, excluding a structure break followed shortly by another FVG-producing impulse. The all-highs/lows 48-hour premium/discount filter overlaps with H1 trend alignment and may unnecessarily exclude continuation setups. These are testable hypotheses, not assumptions of profitability.

## Bounded experiment plan (fixed before new results)

Keep risk, FTMO loss limits, 2R target, mandatory sweep/structure/displacement/FVG, spread costs, sessions, stops and one concurrent trade unchanged. Compare only these four profiles on 2025 development data:

1. Strict baseline, identical parameters.
2. Sequential SMC: allow FVG middle candle up to three bars after the first displaced break.
3. Sequential SMC without the additional H1 premium/discount filter; retain confirmed H1 direction and opposing liquidity room.
4. Same as (3), with liquidity lookback reduced from 12 to 6 bars to recognize nearer intraday liquidity pools.

No grid search or risk increase. Candidate selection must disclose both setup frequency and expectancy/drawdown; a higher setup count alone is not evidence of FTMO suitability. After development selection, evaluate one selected candidate on the already-seen January–May 2026 comparison period and a higher-cost scenario. It is not a new untouched holdout. If results do not establish profitability, retain that limitation and do not deploy.

Changes also add granular candidate rejection reasons, unique valid setup counts and submitted-order counts. A valid setup count refers to analyses performed when the simulation can consider a new order; bars skipped during active exposure or a terminal halt are not counted.

The numeric objectives remain those transcribed from the supplied image, using a provisional $100,000 account. [FTMO's official objectives](https://ftmo.com/en/trading-objectives/) distinguish Challenge, Verification and 1-Step rules; the supplied account variant remains unconfirmed. No change to the image's numbers or any claim of passing is justified without that confirmation.
