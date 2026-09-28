using TradingBot.Application;
using TradingBot.Domain;

namespace TradingBot.Strategies;

/// <summary>H1 last confirmed structure break, M5 displaced internal BOS and fresh FVG retracement.</summary>
public sealed class FtmoSmcContinuationStrategyEngine(IMarketDataProvider marketData, INewsFilter newsFilter,
    ISessionClock sessionClock, TradingBotOptions options) : IStrategyEngine
{
    public async Task<TradeSignal> AnalyzeAsync(string symbol, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var session = sessionClock.GetCurrentSession(now);
        TradeSignal Invalid(string reason) => new(symbol, TradeDirection.Buy, 0, 0, 0, 0, session, false, reason);
        if (session == SessionName.Closed || now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
            || !options.AllowedSessions.Contains(session.ToString(), StringComparer.OrdinalIgnoreCase)
            || !options.ForexMarketSessions.TradingDays.Contains(now.DayOfWeek.ToString(), StringComparer.OrdinalIgnoreCase))
            return Invalid("Outside configured SMC continuation sessions.");
        if (newsFilter.IsBlocked(now, symbol, out var news)) return Invalid(news);
        var p = options.FtmoSmc;
        var count = Math.Max(p.AtrPeriod + 3, p.LiquidityLookback + p.SwingStrength * 2 + 3);
        var h1 = (await marketData.GetCandlesAsync(symbol, Timeframe.H1, now.AddDays(-20), now, cancellationToken))
            .Where(c => c.OpenedAt.AddHours(1) <= now).OrderBy(c => c.OpenedAt).TakeLast(p.H1RangeLookback).ToArray();
        var m5 = (await marketData.GetCandlesAsync(symbol, Timeframe.M5, now.AddDays(-3), now, cancellationToken))
            .Where(c => c.OpenedAt.AddMinutes(5) <= now).OrderBy(c => c.OpenedAt).TakeLast(count).ToArray();
        if (h1.Length < p.H1RangeLookback || m5.Length < count) return Invalid("Insufficient continuation history.");
        if (now - h1[^1].OpenedAt > TimeSpan.FromHours(2) || now - m5[^1].OpenedAt > TimeSpan.FromMinutes(6))
            return Invalid("Stale continuation data.");
        if (h1.Concat(m5).Any(c => c.Low <= 0 || c.High < Math.Max(c.Open, c.Close) || c.Low > Math.Min(c.Open, c.Close))
            || m5.Zip(m5.Skip(1)).Any(x => x.Second.OpenedAt - x.First.OpenedAt != TimeSpan.FromMinutes(5)))
            return Invalid("Malformed or missing continuation candles.");
        var bias = LastStructureBreak(h1, p.SwingStrength);
        if (bias is null) return Invalid("No confirmed H1 structure break in lookback.");
        var buy = bias == TradeDirection.Buy;
        var impulse = m5.Length - 2;
        var lower = buy ? m5[^3].High : m5[^1].High;
        var upper = buy ? m5[^1].Low : m5[^3].Low;
        if ((upper - lower) / options.PipSize < p.MinimumFvgPips) return Invalid("No fresh continuation FVG.");
        var prior = m5.Take(impulse).ToArray();
        var pivots = FtmoSmcStrategyEngine.ConfirmedSwings(prior, p.SwingStrength, buy);
        if (pivots.Count == 0) return Invalid("No confirmed M5 internal structure pivot.");
        var pivot = pivots[^1];
        var bar = m5[impulse];
        bool Beyond(decimal close) => buy ? close > pivot.Price : close < pivot.Price;
        if (!Beyond(bar.Close) || prior.Skip(pivot.Index + 1).Any(c => Beyond(c.Close)))
            return Invalid("FVG impulse is not the first M5 structure break.");
        var atr = Enumerable.Range(impulse - p.AtrPeriod, p.AtrPeriod).Select(i => Math.Max(m5[i].Range,
            Math.Max(Math.Abs(m5[i].High - m5[i - 1].Close), Math.Abs(m5[i].Low - m5[i - 1].Close)))).Average();
        if (bar.Range <= 0 || bar.Body / bar.Range < p.MinimumDisplacementBodyRatio
            || bar.Range < atr * p.DisplacementAtrMultiplier || (buy ? !bar.IsBullish : !bar.IsBearish))
            return Invalid("Continuation displacement rejected.");
        var leg = m5.Skip(Math.Max(0, impulse - p.LiquidityLookback)).Take(p.LiquidityLookback + 1).ToArray();
        var extreme = buy ? leg.Min(c => c.Low) : leg.Max(c => c.High);
        if (buy ? m5[^1].Low < extreme : m5[^1].High > extreme) return Invalid("Continuation origin invalidated.");
        var entry = decimal.Round((lower + upper) / 2m, 5);
        var midpoint = (h1.Max(c => c.High) + h1.Min(c => c.Low)) / 2m;
        if (p.UsePremiumDiscountFilter && (buy ? entry >= midpoint : entry <= midpoint))
            return Invalid("Continuation premium/discount rejected.");
        var stop = decimal.Round(extreme + (buy ? -1 : 1) * p.StopBufferPips * options.PipSize, 5);
        var risk = (entry - stop) * (buy ? 1 : -1);
        if (risk / options.PipSize < p.MinimumStopPips || risk / options.PipSize > p.MaximumStopPips)
            return Invalid("Continuation stop distance outside bounds.");
        var target = decimal.Round(entry + (buy ? 1 : -1) * risk * p.RewardRisk, 5);
        if (p.RequireLiquidityTargetRoom)
        {
            var targets = FtmoSmcStrategyEngine.ConfirmedSwings(h1, p.SwingStrength, buy)
                .Where(x => buy ? x.Price > entry : x.Price < entry).Select(x => x.Price).ToArray();
            if (targets.Length == 0 || (buy ? target > targets.Min() : target < targets.Max()))
                return Invalid("Insufficient continuation opposing liquidity room.");
        }
        return new(symbol, bias.Value, entry, stop, target, p.RewardRisk, session, true,
            "H1 last structure break + first displaced M5 internal BOS + fresh FVG midpoint continuation",
            new FairValueGap(bias.Value, lower, upper, m5[^1].OpenedAt),
            $"FTMO-SMC-CONT|{symbol}|{bias}|break:{bar.OpenedAt:O}");
    }

    public static TradeDirection? LastStructureBreak(IReadOnlyList<Candle> candles, int strength)
    {
        var highs = FtmoSmcStrategyEngine.ConfirmedSwings(candles, strength, true);
        var lows = FtmoSmcStrategyEngine.ConfirmedSwings(candles, strength, false);
        TradeDirection? direction = null;
        for (var i = 1; i < candles.Count; i++)
        {
            // A pivot is usable only after its right wing has closed before the breaking bar.
            var high = highs.LastOrDefault(x => x.Index + strength < i, (Index: -1, Price: 0m));
            var low = lows.LastOrDefault(x => x.Index + strength < i, (Index: -1, Price: 0m));
            if (high.Index >= 0 && candles[i].Close > high.Price
                && !candles.Skip(high.Index + 1).Take(i - high.Index - 1).Any(c => c.Close > high.Price))
                direction = TradeDirection.Buy;
            if (low.Index >= 0 && candles[i].Close < low.Price
                && !candles.Skip(low.Index + 1).Take(i - low.Index - 1).Any(c => c.Close < low.Price))
                direction = TradeDirection.Sell;
        }
        return direction;
    }
}
