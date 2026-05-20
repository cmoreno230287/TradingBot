# Strategy_EURUSD_Forex_V3
# Smart Money Strategy for EURUSD Based on Liquidity Sweeps, BOS/MSS, FVG and Institutional Context

> **Market:** EURUSD Forex  
> **Style:** Scalping / Intraday  
> **Core Model:** Smart Money Concepts + Liquidity Sweep + Displacement + BOS/MSS + FVG Entry  
> **Version:** V3  
> **Primary Goal:** Improve the probability of profitable execution by adding institutional context, objective filters, session precision, liquidity targeting, and robust risk management.

---

## 1. Objective

The objective of this strategy is to identify high-probability EURUSD scalping and intraday trade opportunities by combining:

- higher timeframe institutional bias,
- previous day/week liquidity levels,
- liquidity sweeps above previous highs or below previous lows,
- objective displacement confirmation,
- market structure shift or break of structure,
- Fair Value Gap retracement entries,
- premium/discount context,
- session timing,
- news filtering,
- strict risk management,
- and consistent journaling/backtesting.

This strategy is designed for semi-automation or full automation, so the rules must be as objective as possible.

---

## 2. Strategy Overview

The strategy follows this sequence:

1. Define Daily and H1 directional bias.
2. Identify key liquidity targets.
3. Wait for price to sweep a relevant liquidity level.
4. Confirm rejection with strong displacement.
5. Confirm BOS or MSS on M5.
6. Identify a valid FVG created by displacement.
7. Confirm that price is in a premium/discount area.
8. Wait for retracement into the FVG.
9. Execute the trade if RR, session, spread, and news filters are valid.
10. Manage the trade using predefined partials, break-even, invalidation, and daily risk limits.

---

## 3. Timeframes

### 3.1 Primary Timeframes

- **Macro Bias Timeframe:** D1
- **Bias Timeframe:** H1
- **Execution Timeframe:** M5
- **Optional Entry Refinement:** M1

### 3.2 Timeframe Usage

| Timeframe | Purpose |
|---|---|
| D1 | Macro bias, previous day/week high/low, premium/discount, large liquidity targets |
| H1 | Intraday bias, structural direction, institutional zones |
| M5 | Main execution, sweep, BOS/MSS, displacement, FVG identification |
| M1 | Optional refined entry after M5 setup is confirmed |

---

## 4. Key Definitions

## 4.1 Previous Day High / Low

- **PDH:** Previous Day High
- **PDL:** Previous Day Low

These are primary liquidity levels. Price often seeks these levels before reversing or continuing.

## 4.2 Previous Week High / Low

- **PWH:** Previous Week High
- **PWL:** Previous Week Low

These are stronger liquidity levels than daily levels and should be considered major targets or reversal zones.

## 4.3 Session High / Low

Session liquidity levels include:

- Asian session high/low
- London session high/low
- New York session high/low

For EURUSD, Asian range liquidity is especially important before London expansion.

## 4.4 Liquidity Sweep

### Bullish Sweep

A bullish sweep occurs when:

- price breaks below a previous low,
- takes sell-side liquidity,
- fails to continue lower,
- and closes back above the swept level or shows strong bullish rejection.

### Bearish Sweep

A bearish sweep occurs when:

- price breaks above a previous high,
- takes buy-side liquidity,
- fails to continue higher,
- and closes back below the swept level or shows strong bearish rejection.

## 4.5 Break of Structure (BOS)

### Bullish BOS

A bullish BOS occurs when price closes above a valid recent swing high.

### Bearish BOS

A bearish BOS occurs when price closes below a valid recent swing low.

## 4.6 Market Structure Shift (MSS)

A Market Structure Shift is preferred after a liquidity sweep.

### Bullish MSS

After taking sell-side liquidity, price shifts bullish when it closes above the most recent lower high or internal swing high.

### Bearish MSS

After taking buy-side liquidity, price shifts bearish when it closes below the most recent higher low or internal swing low.

## 4.7 Fair Value Gap (FVG)

### Bullish FVG

A bullish FVG exists when:

- Candle 3 low is above Candle 1 high.

Formula:

```text
Candle3.Low > Candle1.High
```

### Bearish FVG

A bearish FVG exists when:

- Candle 3 high is below Candle 1 low.

Formula:

```text
Candle3.High < Candle1.Low
```

## 4.8 Displacement

Displacement is a strong aggressive candle or candle sequence that confirms institutional participation.

A valid displacement should include at least 3 of the following:

- candle body is greater than recent average body size,
- candle closes near its high for bullish setups or near its low for bearish setups,
- candle breaks structure with conviction,
- candle creates an FVG,
- candle range is greater than a configurable ATR threshold,
- move happens during London, New York, or overlap session.

---

## 5. Higher Timeframe Bias

## 5.1 Daily Bias

The D1 bias is used as macro context.

### Bullish Daily Bias

Daily bias is bullish when at least 2 of the following are true:

- price is forming higher highs and higher lows,
- price has broken a previous D1 swing high,
- price is trading above the D1 equilibrium of the recent range,
- current daily candle shows bullish displacement,
- price is targeting buy-side liquidity above PDH/PWH.

### Bearish Daily Bias

Daily bias is bearish when at least 2 of the following are true:

- price is forming lower highs and lower lows,
- price has broken a previous D1 swing low,
- price is trading below the D1 equilibrium of the recent range,
- current daily candle shows bearish displacement,
- price is targeting sell-side liquidity below PDL/PWL.

### Neutral Daily Bias

Daily bias is neutral when:

- price is inside a tight range,
- there is no clear structure,
- price is near the middle of the daily range,
- or major high-impact news is pending.

No trade should be taken when Daily and H1 context are unclear or conflicting.

---

## 5.2 H1 Bias

### Bullish H1 Bias

H1 bias is bullish when:

- price forms higher highs and higher lows,
- price breaks an H1 swing high,
- price respects bullish demand or bullish order block zones,
- price is above the H1 equilibrium of the active dealing range.

### Bearish H1 Bias

H1 bias is bearish when:

- price forms lower highs and lower lows,
- price breaks an H1 swing low,
- price respects bearish supply or bearish order block zones,
- price is below the H1 equilibrium of the active dealing range.

### Bias Alignment Rule

- Best trades occur when D1 and H1 bias are aligned.
- If D1 and H1 conflict, reduce risk or skip the trade.
- If bias is neutral, do not trade.

---

## 6. Premium and Discount Filter

Use the most recent valid dealing range from the higher timeframe.

### Bullish Trades

Prefer buys when price is in:

- discount zone below 50% equilibrium,
- higher timeframe bullish order block,
- sell-side liquidity sweep area,
- or a discount FVG.

### Bearish Trades

Prefer sells when price is in:

- premium zone above 50% equilibrium,
- higher timeframe bearish order block,
- buy-side liquidity sweep area,
- or a premium FVG.

### Rule

Avoid buying in premium and avoid selling in discount unless there is a very strong continuation model with confirmed liquidity target and displacement.

---

## 7. Liquidity Targeting

Before entering any trade, define the expected destination of price.

## 7.1 Valid Buy-Side Liquidity Targets

For long trades, valid targets include:

- previous day high,
- previous week high,
- Asian session high,
- London session high,
- equal highs,
- recent M5/M15 swing high,
- unmitigated bearish FVG,
- higher timeframe liquidity pool.

## 7.2 Valid Sell-Side Liquidity Targets

For short trades, valid targets include:

- previous day low,
- previous week low,
- Asian session low,
- London session low,
- equal lows,
- recent M5/M15 swing low,
- unmitigated bullish FVG,
- higher timeframe liquidity pool.

## 7.3 Liquidity Rule

Do not take a trade unless there is a clear liquidity target with enough distance to provide at least 1:2 RR.

---

## 8. Buy Setup

## 8.1 Conditions

A valid buy setup requires:

1. D1 and/or H1 bias is bullish.
2. Price is preferably in discount relative to the active dealing range.
3. Price sweeps a valid sell-side liquidity level:
   - previous day low,
   - previous week low,
   - Asian low,
   - equal lows,
   - or recent M5/M15 swing low.
4. Price rejects the swept level.
5. A strong bullish displacement occurs on M5.
6. A bullish MSS or BOS occurs on M5, confirmed by candle close.
7. A valid bullish FVG is created by the displacement leg.
8. Price retraces into the FVG.
9. Entry price offers minimum RR of 1:2.
10. Spread and news filters are valid.
11. A clear buy-side liquidity target exists.

## 8.2 Entry

Preferred entry options:

1. **Balanced entry:** Buy Limit at 50% of the bullish FVG.
2. **Aggressive entry:** Buy Limit at the upper boundary of the bullish FVG when displacement is very strong.
3. **Conservative entry:** Wait for M1 bullish confirmation inside the M5 FVG.

## 8.3 Stop Loss

Place stop loss below:

- sweep low + buffer,
- or below the M5 structural low created after the sweep.

Recommended buffer for EURUSD:

- 1 to 3 pips during normal volatility,
- 3 to 5 pips during high volatility.

## 8.4 Take Profit

Primary target:

- next buy-side liquidity pool.

Alternative target:

- fixed RR of 1:2 minimum,
- 1:3 preferred.

## 8.5 Invalidation

Cancel or invalidate the buy setup if:

- price breaks below sweep low before entry,
- bullish FVG is fully mitigated before order placement,
- M5 structure turns bearish again,
- session window closes,
- high-impact news becomes active,
- spread becomes too high,
- price reaches target before entry.

---

## 9. Sell Setup

## 9.1 Conditions

A valid sell setup requires:

1. D1 and/or H1 bias is bearish.
2. Price is preferably in premium relative to the active dealing range.
3. Price sweeps a valid buy-side liquidity level:
   - previous day high,
   - previous week high,
   - Asian high,
   - equal highs,
   - or recent M5/M15 swing high.
4. Price rejects the swept level.
5. A strong bearish displacement occurs on M5.
6. A bearish MSS or BOS occurs on M5, confirmed by candle close.
7. A valid bearish FVG is created by the displacement leg.
8. Price retraces into the FVG.
9. Entry price offers minimum RR of 1:2.
10. Spread and news filters are valid.
11. A clear sell-side liquidity target exists.

## 9.2 Entry

Preferred entry options:

1. **Balanced entry:** Sell Limit at 50% of the bearish FVG.
2. **Aggressive entry:** Sell Limit at the lower boundary of the bearish FVG when displacement is very strong.
3. **Conservative entry:** Wait for M1 bearish confirmation inside the M5 FVG.

## 9.3 Stop Loss

Place stop loss above:

- sweep high + buffer,
- or above the M5 structural high created after the sweep.

Recommended buffer for EURUSD:

- 1 to 3 pips during normal volatility,
- 3 to 5 pips during high volatility.

## 9.4 Take Profit

Primary target:

- next sell-side liquidity pool.

Alternative target:

- fixed RR of 1:2 minimum,
- 1:3 preferred.

## 9.5 Invalidation

Cancel or invalidate the sell setup if:

- price breaks above sweep high before entry,
- bearish FVG is fully mitigated before order placement,
- M5 structure turns bullish again,
- session window closes,
- high-impact news becomes active,
- spread becomes too high,
- price reaches target before entry.

---

## 10. Entry Rules

## 10.1 Default Entry

Default entry is the FVG midpoint, also known as 50% FVG entry.

## 10.2 Dynamic Entry Selection

Use dynamic entry logic depending on market behavior:

| Market Condition | Entry Mode |
|---|---|
| Strong displacement, high momentum | FVG boundary |
| Normal displacement | FVG midpoint |
| Choppy or unclear reaction | Wait for M1 confirmation |
| Poor RR | Reject trade |

## 10.3 Reject Trade If

Reject the trade if:

- spread is too high,
- RR is below 1:2,
- target is too close,
- high-impact news is active,
- price is in a range,
- no clear liquidity target exists,
- displacement is weak,
- FVG is too small or too large,
- trade is against both D1 and H1 bias.

---

## 11. Stop Loss Rules

## 11.1 Buy Stop Loss

For buy trades:

- SL below sweep low + buffer.

## 11.2 Sell Stop Loss

For sell trades:

- SL above sweep high + buffer.

## 11.3 Buffer Rules

Default buffer:

- 1 to 3 pips.

Dynamic buffer:

- use ATR-based buffer during volatile conditions.

Example:

```text
Buffer = max(1 pip, ATR(14) on M5 * 0.10)
```

---

## 12. Take Profit Rules

## 12.1 Target Options

Valid take profit options:

- opposite liquidity level,
- previous day high/low,
- previous week high/low,
- Asian session high/low,
- equal highs/lows,
- fixed RR.

## 12.2 Minimum RR

- Minimum RR: 1:2
- Preferred RR: 1:3

## 12.3 Partial Take Profit

Recommended model:

- close 50% at 1:1,
- move SL to break-even after 1:1,
- let remaining position target 1:2 to 1:3 or next liquidity pool.

## 12.4 Alternative Advanced Management

For strong trend days:

- partial at 1:1,
- second partial at 1:2,
- runner to major liquidity target.

---

## 13. Risk Management

## 13.1 Risk Per Trade

Recommended risk:

- 0.5% per trade for live testing,
- 1% per trade only after strategy is proven profitable.

## 13.2 Daily Risk Limits

- Max daily loss: 2% to 3%.
- Stop trading after max daily loss is reached.
- Max consecutive losses: 2.
- Stop trading after 2 consecutive losses.

## 13.3 Position Rules

- Only one position per symbol.
- No martingale.
- No grid recovery.
- No revenge trading.
- No increasing lot size after a loss.

## 13.4 Weekly Risk Control

Stop trading for the week if:

- weekly drawdown reaches 5%,
- three consecutive losing days occur,
- or execution discipline is broken.

---

## 14. Session Rules

## 14.1 Allowed Sessions

Allowed trading sessions:

- London session,
- New York session,
- London/New York overlap.

## 14.2 Preferred EURUSD Scalping Windows

Use broker/server time conversion carefully.

Recommended New York time windows:

| Session | Preferred Time Window |
|---|---|
| London Kill Zone | 02:00 AM - 05:00 AM New York Time |
| New York Kill Zone | 08:30 AM - 11:00 AM New York Time |
| London/New York Overlap | 08:00 AM - 11:00 AM New York Time |

## 14.3 Avoid Trading

Avoid trading during:

- Asian session unless using Asian range breakout logic,
- last 30 minutes before daily close,
- Friday afternoon,
- low liquidity holidays,
- market close/open spread expansion,
- unclear range days.

---

## 15. News Filter

## 15.1 High-Impact News Events to Avoid

For EURUSD, avoid trading around:

- NFP,
- CPI,
- PPI,
- FOMC,
- ECB interest rate decisions,
- Fed interest rate decisions,
- unemployment data,
- GDP releases,
- Powell/ECB president speeches,
- major geopolitical events.

## 15.2 News Timing Rule

Do not open new trades:

- 15 minutes before high-impact news,
- 15 to 30 minutes after high-impact news,
- or longer if spread and volatility remain abnormal.

## 15.3 Currency Impact Rule

For EURUSD, news related to both EUR and USD must be filtered.

---

## 16. Setup Filters

Reject a setup if any of the following conditions are true:

- no clear sweep,
- no strong displacement,
- no valid MSS/BOS,
- no valid FVG,
- FVG already mitigated,
- trade is against both Daily and H1 bias,
- price is in the middle of a range,
- spread is too high,
- poor RR,
- no clear liquidity target,
- high-impact news is active,
- session window is not valid,
- stop loss is too large compared to target,
- price has already reached the intended liquidity target.

---

## 17. Pending Order Cancellation

Cancel pending orders if:

- structure invalidates,
- FVG invalidates,
- session ends,
- high-impact news filter activates,
- opposite setup appears,
- spread becomes too high,
- price reaches the target without triggering entry,
- order remains pending for too long.

Recommended pending order expiration:

- M5 setup: cancel after 3 to 6 candles,
- M1 refined setup: cancel after 5 to 10 candles.

---

## 18. Market Conditions Filter

## 18.1 Best Market Conditions

This strategy performs best when:

- EURUSD has clear directional bias,
- London or New York session creates expansion,
- price sweeps liquidity and strongly rejects,
- spread is low,
- there is clean displacement,
- there is a clear liquidity target.

## 18.2 Bad Market Conditions

Avoid trading when:

- price is ranging tightly,
- ATR is extremely low,
- spread is high,
- news is near,
- price is in the middle of the daily range,
- D1 and H1 bias conflict strongly,
- market creates multiple failed sweeps.

---

## 19. Backtesting Requirements

Before using this strategy live, complete structured backtesting.

## 19.1 Minimum Backtesting Sample

- Minimum: 200 trades.
- Recommended: 500 trades.

## 19.2 Backtesting Metrics

Track:

- win rate,
- average RR,
- max drawdown,
- average daily profit/loss,
- best session,
- worst session,
- best entry type,
- most common invalidation reason,
- performance by day of week,
- performance before/after news,
- performance with Daily + H1 aligned vs not aligned.

## 19.3 Required Edge

Strategy can be considered promising if:

- win rate is at least 40% with 1:2 average RR,
- or win rate is at least 34% with 1:3 average RR,
- max drawdown is acceptable,
- execution rules are repeatable,
- results are consistent across different months.

---

## 20. Trading Journal Requirements

Every trade must be journaled with:

- date,
- session,
- direction,
- D1 bias,
- H1 bias,
- liquidity swept,
- liquidity target,
- BOS/MSS confirmation,
- displacement quality,
- FVG size,
- entry type,
- stop loss size,
- RR,
- result,
- screenshot before entry,
- screenshot after exit,
- reason for win/loss,
- execution mistake, if any.

---

## 21. Automation Logic Summary

Pseudo-flow:

```text
1. Load EURUSD market data.
2. Calculate PDH, PDL, PWH, PWL, session highs/lows.
3. Determine D1 bias.
4. Determine H1 bias.
5. Identify active dealing range and premium/discount zones.
6. Scan M5 for liquidity sweep.
7. Validate rejection.
8. Validate displacement.
9. Validate MSS/BOS by candle close.
10. Detect valid FVG.
11. Confirm clear liquidity target.
12. Validate RR >= 1:2.
13. Validate session window.
14. Validate spread filter.
15. Validate news filter.
16. Place limit order at selected FVG entry level.
17. Place SL beyond sweep level + buffer.
18. Place TP at liquidity target or fixed RR.
19. Manage partial TP and break-even.
20. Cancel pending order if invalidation occurs.
21. Stop trading if daily/weekly risk limits are reached.
```

---

## 22. Configurable Parameters

```json
{
  "Symbol": "EURUSD",
  "MacroBiasTimeframe": "D1",
  "BiasTimeframe": "H1",
  "ExecutionTimeframe": "M5",
  "EntryTimeframe": "M1",
  "UsePreviousDayHighLow": true,
  "UsePreviousWeekHighLow": true,
  "UseSessionHighLow": true,
  "UsePremiumDiscountFilter": true,
  "UseLiquidityTargetFilter": true,
  "SwingStrength": 2,
  "RequireCandleCloseForBos": true,
  "RequireMarketStructureShift": true,
  "RequireDisplacement": true,
  "DisplacementMinBodyToRangeRatio": 0.60,
  "DisplacementAtrMultiplier": 1.20,
  "FvgEntryMode": "Dynamic",
  "DefaultFvgEntryMode": "Midpoint",
  "AllowBoundaryEntryOnStrongDisplacement": true,
  "AllowM1ConfirmationEntry": true,
  "RiskPercentPerTrade": 0.5,
  "MaxRiskPercentPerTrade": 1.0,
  "MinRiskReward": 2.0,
  "PreferredRiskReward": 3.0,
  "DailyDrawdownLimitPercent": 3.0,
  "WeeklyDrawdownLimitPercent": 5.0,
  "MaxConsecutiveLosses": 2,
  "BreakEvenAtRR": 1.0,
  "PartialCloseEnabled": true,
  "PartialClosePercent": 50,
  "PartialCloseAtRR": 1.0,
  "UseNewsFilter": true,
  "MinutesBeforeHighImpactNews": 15,
  "MinutesAfterHighImpactNews": 30,
  "MaxSpreadPips": 1.5,
  "NormalStopLossBufferPips": 2.0,
  "HighVolatilityStopLossBufferPips": 5.0,
  "UseAtrBasedBuffer": true,
  "AtrPeriod": 14,
  "AtrBufferMultiplier": 0.10,
  "AllowedSessions": ["London", "NewYork", "LondonNewYorkOverlap"],
  "LondonKillZoneNYTime": "02:00-05:00",
  "NewYorkKillZoneNYTime": "08:30-11:00",
  "PendingOrderExpirationCandlesM5": 6,
  "PendingOrderExpirationCandlesM1": 10,
  "MinimumBacktestTrades": 200,
  "RecommendedBacktestTrades": 500
}
```

---

## 23. Final Execution Checklist

Before taking any trade, confirm:

- [ ] D1 bias is clear.
- [ ] H1 bias is clear.
- [ ] Trade direction is aligned with bias or has strong reason for counter-trend scalp.
- [ ] Price is in premium/discount area appropriate for the trade.
- [ ] A meaningful liquidity level was swept.
- [ ] Rejection is clear.
- [ ] Displacement is strong.
- [ ] BOS/MSS is confirmed by candle close.
- [ ] Valid FVG exists.
- [ ] FVG has not been fully mitigated.
- [ ] Entry offers at least 1:2 RR.
- [ ] Liquidity target is clear.
- [ ] Spread is acceptable.
- [ ] No high-impact news is active.
- [ ] Session window is valid.
- [ ] Daily loss limit has not been reached.
- [ ] No more than 2 consecutive losses.

---

## 24. Final Professional Notes

This V3 strategy is significantly stronger than the previous version because it adds:

- Daily macro bias,
- premium/discount context,
- objective displacement validation,
- liquidity targeting,
- stricter session timing,
- detailed news filtering,
- dynamic FVG entry logic,
- market condition filtering,
- backtesting requirements,
- and trade journaling.

The strategy has strong profitability potential, but it must be validated through disciplined backtesting and forward testing before risking significant capital.

A good strategy does not guarantee profitability. Profitability comes from the combination of:

- edge,
- execution,
- risk control,
- discipline,
- patience,
- and statistical validation.
