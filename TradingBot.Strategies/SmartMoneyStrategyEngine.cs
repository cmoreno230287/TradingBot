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

        var daily = await marketData.GetCandlesAsync(symbol, Timeframe.D1, now.AddDays(-10), now, cancellationToken);
        var hourly = await marketData.GetCandlesAsync(symbol, Timeframe.H1, now.AddDays(-2), now, cancellationToken);
        var execution = await marketData.GetCandlesAsync(symbol, Timeframe.M5, now.AddHours(-8), now, cancellationToken);

        var dailyBias = CalculateBias(daily);
        var h1Bias = CalculateBias(hourly);

        if (dailyBias == MarketBias.Neutral || h1Bias == MarketBias.Neutral || dailyBias != h1Bias)
        {
            return Invalid(symbol, TradeDirection.Buy, session, "Daily and H1 bias are not aligned.");
        }

        var direction = dailyBias == MarketBias.Bullish ? TradeDirection.Buy : TradeDirection.Sell;
        var sweep = DetectLiquiditySweep(execution, direction);
        if (sweep is null)
        {
            return Invalid(symbol, direction, session, "No valid liquidity sweep.");
        }

        if (!HasMarketStructureShift(execution, direction))
        {
            return Invalid(symbol, direction, session, "No candle-close MSS/BOS confirmation.");
        }

        var fvg = DetectFairValueGap(execution, direction);
        if (fvg is null)
        {
            return Invalid(symbol, direction, session, "No valid fair value gap.");
        }

        if (!HasDisplacement(execution))
        {
            return Invalid(symbol, direction, session, "Displacement is below threshold.");
        }

        var entry = fvg.Midpoint;
        var stopLoss = direction == TradeDirection.Buy
            ? sweep.SweptPrice - (options.PipSize * 2m)
            : sweep.SweptPrice + (options.PipSize * 2m);
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
            fvg);
    }

    public static MarketBias CalculateBias(IReadOnlyList<Candle> candles)
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

    public static FairValueGap? DetectFairValueGap(IReadOnlyList<Candle> candles, TradeDirection direction)
    {
        for (var i = candles.Count - 1; i >= 2; i--)
        {
            var candle1 = candles[i - 2];
            var candle3 = candles[i];

            if (direction == TradeDirection.Buy && candle3.Low > candle1.High)
            {
                return new FairValueGap(direction, candle1.High, candle3.Low, candle3.OpenedAt);
            }

            if (direction == TradeDirection.Sell && candle3.High < candle1.Low)
            {
                return new FairValueGap(direction, candle3.High, candle1.Low, candle3.OpenedAt);
            }
        }

        return null;
    }

    private static LiquiditySweep? DetectLiquiditySweep(IReadOnlyList<Candle> candles, TradeDirection direction)
    {
        if (candles.Count < 3)
        {
            return null;
        }

        var prior = candles.Take(candles.Count - 1).ToArray();
        var last = candles[^1];

        if (direction == TradeDirection.Buy)
        {
            var priorLow = prior.Min(c => c.Low);
            var swept = prior.FirstOrDefault(c => c.Low <= priorLow && c.Close > c.Low);
            return swept is null ? null : new LiquiditySweep(direction, "Sell-side liquidity", priorLow, swept.OpenedAt);
        }

        var priorHigh = prior.Max(c => c.High);
        var bearishSweep = prior.FirstOrDefault(c => c.High >= priorHigh && c.Close < c.High);
        return bearishSweep is null ? null : new LiquiditySweep(direction, "Buy-side liquidity", priorHigh, bearishSweep.OpenedAt);
    }

    private static bool HasMarketStructureShift(IReadOnlyList<Candle> candles, TradeDirection direction)
    {
        if (candles.Count < 4)
        {
            return false;
        }

        var previous = candles.Take(candles.Count - 1).ToArray();
        var last = candles[^1];

        return direction == TradeDirection.Buy
            ? last.Close > previous.Max(c => c.High)
            : last.Close < previous.Min(c => c.Low);
    }

    private bool HasDisplacement(IReadOnlyList<Candle> candles)
    {
        if (candles.Count < 4)
        {
            return false;
        }

        var latest = candles[^1];
        var averageRange = candles.Take(candles.Count - 1).Average(c => c.Range);
        var bodyToRange = latest.Range == 0 ? 0 : latest.Body / latest.Range;

        return bodyToRange >= options.DisplacementMinBodyToRangeRatio
            && latest.Range >= averageRange * options.DisplacementAtrMultiplier;
    }

    private static TradeSignal Invalid(string symbol, TradeDirection direction, SessionName session, string reason) =>
        new(symbol, direction, 0m, 0m, 0m, 0m, session, false, reason);
}
