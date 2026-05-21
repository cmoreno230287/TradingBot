using TradingBot.Application;
using TradingBot.Domain;

namespace TradingBot.Strategies;

public sealed class SmartMoneyStrategyEngine(
    IMarketDataProvider marketData,
    INewsFilter newsFilter,
    ISessionClock sessionClock,
    TradingBotOptions options) : IStrategyEngine
{
    public async Task<TradeSignal> AnalyzeAsync(string symbol, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var session = sessionClock.GetCurrentSession(now);
        if (session == SessionName.Closed && !options.TradeOutKillZoneTime)
        {
            return Invalid(symbol, TradeDirection.Buy, session, "Outside configured kill zones.");
        }

        if (newsFilter.IsBlocked(now, symbol, out var newsReason))
        {
            return Invalid(symbol, TradeDirection.Buy, session, newsReason);
        }

        var daily = await marketData.GetCandlesAsync(symbol, Timeframe.D1, now.AddDays(-60), now, cancellationToken);
        var hourly = await marketData.GetCandlesAsync(symbol, Timeframe.H1, now.AddDays(-10), now, cancellationToken);
        var execution = (await marketData.GetCandlesAsync(symbol, Timeframe.M5, now.AddHours(-12), now, cancellationToken))
            .OrderBy(candle => candle.OpenedAt)
            .TakeLast(options.SetupLookbackCandlesM5)
            .ToArray();

        var dailyBias = CalculateBias(daily, options.BiasSwingStrength);
        var h1Bias = CalculateBias(hourly, options.BiasSwingStrength);

        if (dailyBias == MarketBias.Neutral || h1Bias == MarketBias.Neutral || dailyBias != h1Bias)
        {
            return Invalid(symbol, TradeDirection.Buy, session, "Daily and H1 swing bias are not aligned.");
        }

        var direction = dailyBias == MarketBias.Bullish ? TradeDirection.Buy : TradeDirection.Sell;
        var sweep = DetectLiquiditySweep(execution, direction, options.LiquiditySweepLookbackCandles);
        if (sweep is null)
        {
            return Invalid(symbol, direction, session, "No recent valid liquidity sweep.");
        }

        if (execution.Length - 1 - sweep.Index > options.MaxSetupAgeCandlesM5)
        {
            return Invalid(symbol, direction, session, "Liquidity sweep is older than configured setup age.");
        }

        var structureShift = DetectMarketStructureShift(execution, direction, sweep.Index);
        if (structureShift is null)
        {
            return Invalid(symbol, direction, session, "No MSS/BOS confirmation after liquidity sweep.");
        }

        var fvg = DetectFairValueGap(execution, direction, Math.Max(2, sweep.Index), structureShift.Index);
        if (fvg is null)
        {
            return Invalid(symbol, direction, session, "No valid fair value gap after sweep/MSS.");
        }

        if (!HasMinimumFvgSize(fvg.Gap))
        {
            return Invalid(symbol, direction, session, "Fair value gap is smaller than configured minimum.");
        }

        if (options.RequireDisplacement && !HasDisplacement(execution, structureShift.Index))
        {
            return Invalid(symbol, direction, session, "Displacement candle is below threshold.");
        }

        var entry = ResolveEntry(fvg.Gap, direction);
        if (options.UsePremiumDiscountFilter && !IsInPremiumDiscount(execution, direction, entry))
        {
            return Invalid(symbol, direction, session, "Entry is not in the required premium/discount area.");
        }

        var stopLoss = direction == TradeDirection.Buy
            ? sweep.SweptPrice - (options.PipSize * options.NormalStopLossBufferPips)
            : sweep.SweptPrice + (options.PipSize * options.NormalStopLossBufferPips);
        var risk = Math.Abs(entry - stopLoss);
        var takeProfit = direction == TradeDirection.Buy
            ? entry + (risk * options.PreferredRiskReward)
            : entry - (risk * options.PreferredRiskReward);
        var rr = risk == 0 ? 0 : Math.Abs(takeProfit - entry) / risk;

        if (rr < options.MinRiskReward)
        {
            return Invalid(symbol, direction, session, "Risk reward is below configured minimum.");
        }

        return new TradeSignal(
            symbol,
            direction,
            entry,
            stopLoss,
            takeProfit,
            decimal.Round(rr, 2),
            session,
            true,
            $"{sweep.LevelName} sweep + MSS/BOS + {direction.ToString().ToLowerInvariant()} FVG",
            fvg.Gap);
    }

    public static MarketBias CalculateBias(IReadOnlyList<Candle> candles) =>
        CalculateBias(candles, swingStrength: 2);

    public static MarketBias CalculateBias(IReadOnlyList<Candle> candles, int swingStrength)
    {
        var ordered = candles.OrderBy(candle => candle.OpenedAt).ToArray();
        if (ordered.Length < (swingStrength * 2) + 5)
        {
            return CalculateCloseBias(ordered);
        }

        var swingHighs = FindSwingHighs(ordered, swingStrength).TakeLast(2).ToArray();
        var swingLows = FindSwingLows(ordered, swingStrength).TakeLast(2).ToArray();
        if (swingHighs.Length < 2 || swingLows.Length < 2)
        {
            return CalculateCloseBias(ordered);
        }

        var higherHigh = swingHighs[1].Price > swingHighs[0].Price;
        var higherLow = swingLows[1].Price > swingLows[0].Price;
        var lowerHigh = swingHighs[1].Price < swingHighs[0].Price;
        var lowerLow = swingLows[1].Price < swingLows[0].Price;

        if (higherHigh && higherLow)
        {
            return MarketBias.Bullish;
        }

        return lowerHigh && lowerLow ? MarketBias.Bearish : MarketBias.Neutral;
    }

    public static FairValueGap? DetectFairValueGap(IReadOnlyList<Candle> candles, TradeDirection direction)
    {
        var detection = DetectFairValueGap(candles.OrderBy(candle => candle.OpenedAt).ToArray(), direction, 2, 0);
        return detection?.Gap;
    }

    private static MarketBias CalculateCloseBias(IReadOnlyList<Candle> candles)
    {
        if (candles.Count < 3)
        {
            return MarketBias.Neutral;
        }

        var lastThree = candles.TakeLast(3).ToArray();
        var higherCloses = lastThree[2].Close > lastThree[1].Close && lastThree[1].Close > lastThree[0].Close;
        var lowerCloses = lastThree[2].Close < lastThree[1].Close && lastThree[1].Close < lastThree[0].Close;
        if (higherCloses)
        {
            return MarketBias.Bullish;
        }

        return lowerCloses ? MarketBias.Bearish : MarketBias.Neutral;
    }

    private static LiquiditySweepDetection? DetectLiquiditySweep(IReadOnlyList<Candle> candles, TradeDirection direction, int lookback)
    {
        if (candles.Count < lookback + 2)
        {
            return null;
        }

        var start = Math.Max(lookback, candles.Count - lookback);
        for (var i = candles.Count - 1; i >= start; i--)
        {
            var prior = candles.Skip(Math.Max(0, i - lookback)).Take(Math.Min(lookback, i)).ToArray();
            if (prior.Length == 0)
            {
                continue;
            }

            var candle = candles[i];
            if (direction == TradeDirection.Buy)
            {
                var priorLow = prior.Min(item => item.Low);
                if (candle.Low < priorLow && candle.Close > priorLow)
                {
                    return new LiquiditySweepDetection(i, new LiquiditySweep(direction, "Sell-side liquidity", candle.Low, candle.OpenedAt));
                }
            }
            else
            {
                var priorHigh = prior.Max(item => item.High);
                if (candle.High > priorHigh && candle.Close < priorHigh)
                {
                    return new LiquiditySweepDetection(i, new LiquiditySweep(direction, "Buy-side liquidity", candle.High, candle.OpenedAt));
                }
            }
        }

        return null;
    }

    private static StructureShiftDetection? DetectMarketStructureShift(IReadOnlyList<Candle> candles, TradeDirection direction, int sweepIndex)
    {
        if (sweepIndex <= 0 || sweepIndex >= candles.Count - 1)
        {
            return null;
        }

        var prior = candles.Take(sweepIndex).ToArray();
        if (prior.Length == 0)
        {
            return null;
        }

        var breakLevel = direction == TradeDirection.Buy
            ? prior.Max(candle => candle.High)
            : prior.Min(candle => candle.Low);

        for (var i = sweepIndex + 1; i < candles.Count; i++)
        {
            var candle = candles[i];
            var brokeStructure = direction == TradeDirection.Buy
                ? candle.Close > breakLevel
                : candle.Close < breakLevel;
            if (brokeStructure)
            {
                return new StructureShiftDetection(i, breakLevel);
            }
        }

        return null;
    }

    private static FairValueGapDetection? DetectFairValueGap(
        IReadOnlyList<Candle> candles,
        TradeDirection direction,
        int startIndex,
        int preferredMinimumIndex)
    {
        var start = Math.Max(2, Math.Min(startIndex, candles.Count - 1));
        for (var i = candles.Count - 1; i >= start; i--)
        {
            if (i < preferredMinimumIndex)
            {
                continue;
            }

            var candle1 = candles[i - 2];
            var candle3 = candles[i];

            if (direction == TradeDirection.Buy && candle3.Low > candle1.High)
            {
                return new FairValueGapDetection(i, new FairValueGap(direction, candle1.High, candle3.Low, candle3.OpenedAt));
            }

            if (direction == TradeDirection.Sell && candle3.High < candle1.Low)
            {
                return new FairValueGapDetection(i, new FairValueGap(direction, candle3.High, candle1.Low, candle3.OpenedAt));
            }
        }

        return null;
    }

    private bool HasMinimumFvgSize(FairValueGap fvg) =>
        options.MinFvgSizePips <= 0m || fvg.Size >= options.PipSize * options.MinFvgSizePips;

    private bool HasDisplacement(IReadOnlyList<Candle> candles, int index)
    {
        if (candles.Count < 4 || index <= 0 || index >= candles.Count)
        {
            return false;
        }

        var latest = candles[index];
        var previous = candles.Take(index).TakeLast(Math.Min(index, options.LiquiditySweepLookbackCandles)).ToArray();
        if (previous.Length == 0)
        {
            return false;
        }

        var averageRange = previous.Average(c => c.Range);
        var bodyToRange = latest.Range == 0 ? 0 : latest.Body / latest.Range;

        return bodyToRange >= options.DisplacementMinBodyToRangeRatio
            && latest.Range >= averageRange * options.DisplacementAtrMultiplier;
    }

    private decimal ResolveEntry(FairValueGap fvg, TradeDirection direction)
    {
        if (string.Equals(options.FvgEntryMode, "Boundary", StringComparison.OrdinalIgnoreCase))
        {
            return direction == TradeDirection.Buy ? fvg.LowerPrice : fvg.UpperPrice;
        }

        return fvg.Midpoint;
    }

    private static bool IsInPremiumDiscount(IReadOnlyList<Candle> candles, TradeDirection direction, decimal entry)
    {
        if (candles.Count < 3)
        {
            return false;
        }

        var dealingRangeHigh = candles.Max(candle => candle.High);
        var dealingRangeLow = candles.Min(candle => candle.Low);
        var equilibrium = (dealingRangeHigh + dealingRangeLow) / 2m;
        return direction == TradeDirection.Buy
            ? entry <= equilibrium
            : entry >= equilibrium;
    }

    private static IReadOnlyList<SwingPoint> FindSwingHighs(IReadOnlyList<Candle> candles, int strength)
    {
        var swings = new List<SwingPoint>();
        for (var i = strength; i < candles.Count - strength; i++)
        {
            var high = candles[i].High;
            var isSwing = true;
            for (var offset = 1; offset <= strength; offset++)
            {
                if (high <= candles[i - offset].High || high <= candles[i + offset].High)
                {
                    isSwing = false;
                    break;
                }
            }

            if (isSwing)
            {
                swings.Add(new SwingPoint(i, high));
            }
        }

        return swings;
    }

    private static IReadOnlyList<SwingPoint> FindSwingLows(IReadOnlyList<Candle> candles, int strength)
    {
        var swings = new List<SwingPoint>();
        for (var i = strength; i < candles.Count - strength; i++)
        {
            var low = candles[i].Low;
            var isSwing = true;
            for (var offset = 1; offset <= strength; offset++)
            {
                if (low >= candles[i - offset].Low || low >= candles[i + offset].Low)
                {
                    isSwing = false;
                    break;
                }
            }

            if (isSwing)
            {
                swings.Add(new SwingPoint(i, low));
            }
        }

        return swings;
    }

    private static TradeSignal Invalid(string symbol, TradeDirection direction, SessionName session, string reason) =>
        new(symbol, direction, 0m, 0m, 0m, 0m, session, false, reason);

    private sealed record SwingPoint(int Index, decimal Price);

    private sealed record LiquiditySweepDetection(int Index, LiquiditySweep Sweep)
    {
        public string LevelName => Sweep.LevelName;
        public decimal SweptPrice => Sweep.SweptPrice;
    }

    private sealed record StructureShiftDetection(int Index, decimal BreakLevel);

    private sealed record FairValueGapDetection(int Index, FairValueGap Gap);
}
