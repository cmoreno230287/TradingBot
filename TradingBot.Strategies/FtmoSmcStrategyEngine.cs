using TradingBot.Application;
using TradingBot.Domain;

namespace TradingBot.Strategies;

/// <summary>Closed-bar H1 structure, M5 sweep, first structure break with displacement, fresh FVG limit.</summary>
public sealed class FtmoSmcStrategyEngine(IMarketDataProvider marketData, INewsFilter newsFilter,
    ISessionClock sessionClock, TradingBotOptions options) : IStrategyEngine
{
    public async Task<TradeSignal> AnalyzeAsync(string symbol, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var session = sessionClock.GetCurrentSession(now);
        TradeSignal Invalid(string reason) => new(symbol, TradeDirection.Buy, 0, 0, 0, 0, session, false, reason);
        if (session == SessionName.Closed || now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
            || !options.AllowedSessions.Contains(session.ToString(), StringComparer.OrdinalIgnoreCase)
            || !options.ForexMarketSessions.TradingDays.Contains(now.DayOfWeek.ToString(), StringComparer.OrdinalIgnoreCase))
            return Invalid("Outside configured SMC entry sessions.");
        if (newsFilter.IsBlocked(now, symbol, out var news)) return Invalid(news);
        var p = options.FtmoSmc;
        var h1 = (await marketData.GetCandlesAsync(symbol, Timeframe.H1, now.AddDays(-p.H1HistoryDays), now, cancellationToken))
            .Where(c => c.OpenedAt.AddHours(1) <= now).OrderBy(c => c.OpenedAt).TakeLast(p.H1RangeLookback).ToArray();
        var lookback = Math.Max(p.AtrPeriod + p.MaximumSweepAgeBars + 3,
            p.LiquidityLookback + p.MaximumSweepAgeBars + p.SwingStrength * 2 + 3);
        var m5 = (await marketData.GetCandlesAsync(symbol, Timeframe.M5, now.AddDays(-p.M5HistoryDays), now, cancellationToken))
            .Where(c => c.OpenedAt.AddMinutes(5) <= now).OrderBy(c => c.OpenedAt).TakeLast(lookback).ToArray();
        if (h1.Length < p.H1RangeLookback || m5.Length < lookback) return Invalid("Insufficient SMC completed-bar history.");
        if (now - h1[^1].OpenedAt > TimeSpan.FromHours(p.MaximumH1DataAgeHours) || now - m5[^1].OpenedAt > TimeSpan.FromMinutes(p.MaximumM5DataAgeMinutes))
            return Invalid("Stale SMC market data.");
        if (h1.Concat(m5).Any(c => c.Low <= 0 || c.High < Math.Max(c.Open, c.Close) || c.Low > Math.Min(c.Open, c.Close))
            || ForexDataCalendar.HasUnexpectedHourlyGap(h1, options.BrokerMarketClosuresUtc)
            || m5.Zip(m5.Skip(1)).Any(pair => pair.Second.OpenedAt - pair.First.OpenedAt != TimeSpan.FromMinutes(5)))
            return Invalid("Malformed or missing SMC candles.");
        var highs = ConfirmedSwings(h1, p.SwingStrength, true).TakeLast(2).ToArray();
        var lows = ConfirmedSwings(h1, p.SwingStrength, false).TakeLast(2).ToArray();
        if (highs.Length < 2 || lows.Length < 2) return Invalid("H1 structure needs two confirmed swing highs and lows.");
        var buy = highs[1].Price > highs[0].Price && lows[1].Price > lows[0].Price;
        var sell = highs[1].Price < highs[0].Price && lows[1].Price < lows[0].Price;
        if (!buy && !sell) return Invalid("H1 structure is neutral.");
        var direction = buy ? TradeDirection.Buy : TradeDirection.Sell;
        var last = m5.Length - 1;
        var impulse = last - 1;
        var lower = buy ? m5[last - 2].High : m5[last].High;
        var upper = buy ? m5[last].Low : m5[last - 2].Low;
        if ((upper - lower) / options.PipSize < p.MinimumFvgPips) return Invalid("No fresh directional M5 FVG.");
        var rejection = "No recent M5 liquidity sweep and reclaim.";
        var rejectionStage = 0;
        void RejectCandidate(int stage, string reason)
        {
            if (stage > rejectionStage) { rejectionStage = stage; rejection = reason; }
        }
        for (var sweep = impulse - 1; sweep >= Math.Max(p.LiquidityLookback, impulse - p.MaximumSweepAgeBars); sweep--)
        {
            var prior = m5.Skip(sweep - p.LiquidityLookback).Take(p.LiquidityLookback).ToArray();
            var level = buy ? prior.Min(c => c.Low) : prior.Max(c => c.High);
            if (buy ? !(m5[sweep].Low < level && m5[sweep].Close > level)
                : !(m5[sweep].High > level && m5[sweep].Close < level)) continue;
            var pivots = ConfirmedSwings(m5.Take(sweep).ToArray(), p.SwingStrength, buy);
            if (pivots.Count == 0) { RejectCandidate(1, "Sweep has no previously confirmed opposing swing."); continue; }
            var breakLevel = pivots[^1].Price;
            if (m5.Skip(pivots[^1].Index + 1).Take(sweep - pivots[^1].Index - 1)
                .Any(c => buy ? c.Close > breakLevel : c.Close < breakLevel))
            { RejectCandidate(2, "Opposing structure was already broken before the sweep."); continue; }
            // First break must follow the sweep, but its FVG may confirm on a later impulse.
            if (buy ? m5[sweep].Close > breakLevel : m5[sweep].Close < breakLevel)
            { RejectCandidate(2, "Structure was already broken on the sweep candle."); continue; }
            var breakIndex = Enumerable.Range(sweep + 1, impulse - sweep)
                .FirstOrDefault(i => buy ? m5[i].Close > breakLevel : m5[i].Close < breakLevel, -1);
            if (breakIndex < 0) { RejectCandidate(2, "Sweep has no subsequent CHOCH/BOS close."); continue; }
            if (impulse - breakIndex > p.MaximumFvgDelayAfterBreakBars)
            { RejectCandidate(3, "FVG formed outside the configured post-break window."); continue; }
            if (breakIndex <= p.AtrPeriod)
            { RejectCandidate(4, "Insufficient pre-break ATR history."); continue; }
            var displacement = m5[breakIndex];
            var atr = Enumerable.Range(breakIndex - p.AtrPeriod, p.AtrPeriod).Select(i => Math.Max(m5[i].Range,
                Math.Max(Math.Abs(m5[i].High - m5[i - 1].Close), Math.Abs(m5[i].Low - m5[i - 1].Close)))).Average();
            if (displacement.Range <= 0 || displacement.Body / displacement.Range < p.MinimumDisplacementBodyRatio
                || displacement.Range < atr * p.DisplacementAtrMultiplier || (buy ? !displacement.IsBullish : !displacement.IsBearish))
            { RejectCandidate(4, "SMC structure-break displacement rejected."); continue; }
            // A fresh sweep followed by a breach of its extreme is already invalidated.
            if (m5.Skip(sweep + 1).Any(c => buy ? c.Low < m5[sweep].Low : c.High > m5[sweep].High))
            { RejectCandidate(5, "Sweep extreme was breached before entry."); continue; }
            var entry = decimal.Round(buy ? upper - (upper - lower) * p.EntryRetracementFraction
                : lower + (upper - lower) * p.EntryRetracementFraction, 5);
            var midpoint = (h1.Max(c => c.High) + h1.Min(c => c.Low)) / 2m;
            if (p.UsePremiumDiscountFilter && (buy ? entry >= midpoint : entry <= midpoint))
            { RejectCandidate(6, "Entry rejected by H1 premium/discount filter."); continue; }
            var stop = decimal.Round(buy ? m5[sweep].Low - p.StopBufferPips * options.PipSize
                : m5[sweep].High + p.StopBufferPips * options.PipSize, 5);
            var risk = (entry - stop) * (buy ? 1 : -1);
            if (risk / options.PipSize < p.MinimumStopPips || risk / options.PipSize > p.MaximumStopPips)
            { RejectCandidate(7, "SMC stop distance outside configured bounds."); continue; }
            var target = decimal.Round(entry + (buy ? 1 : -1) * risk * p.RewardRisk, 5);
            var liquidityTarget = buy ? highs[^1].Price : lows[^1].Price;
            if (p.RequireLiquidityTargetRoom && (buy ? target > liquidityTarget : target < liquidityTarget))
            { RejectCandidate(8, "Insufficient opposing H1 liquidity room for target."); continue; }
            var gap = new FairValueGap(direction, lower, upper, m5[last].OpenedAt);
            return new(symbol, direction, entry, stop, target, p.RewardRisk, session, true,
                $"H1 {(buy ? "HH/HL" : "LH/LL")} + M5 liquidity sweep/reclaim + first CHOCH/BOS close + displacement + fresh FVG retracement {p.EntryRetracementFraction:P0}; {p.RewardRisk}R",
                gap, $"FTMO-SMC|{symbol}|{direction}|sweep:{m5[sweep].OpenedAt:O}|break:{displacement.OpenedAt:O}",
                new SmcSetupContext(now, m5[sweep].OpenedAt, displacement.OpenedAt,
                    m5[last].OpenedAt.AddMinutes(5), m5[last].OpenedAt.AddMinutes(10), buy ? m5[sweep].Low : m5[sweep].High),
                new SetupDiagnostics(atr / options.PipSize, (upper - lower) / options.PipSize, risk / options.PipSize,
                    displacement.Range > 0 ? displacement.Body / displacement.Range : 0, "SweepBreakFvg"));
        }
        return Invalid(rejection);
    }

    // Both wings must already exist. Equal highs/lows do not become directional swing confirmations.
    public static IReadOnlyList<(int Index, decimal Price)> ConfirmedSwings(IReadOnlyList<Candle> candles, int strength, bool highs) => SmcDetectors.ConfirmedSwings(candles, strength, highs);
}



