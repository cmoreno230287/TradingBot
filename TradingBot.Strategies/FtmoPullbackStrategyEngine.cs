using TradingBot.Application;
using TradingBot.Domain;

namespace TradingBot.Strategies;

public sealed class FtmoPullbackStrategyEngine(IMarketDataProvider marketData, INewsFilter newsFilter,
    ISessionClock sessionClock, TradingBotOptions options) : IStrategyEngine
{
    public async Task<TradeSignal> AnalyzeAsync(string symbol, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var session = sessionClock.GetCurrentSession(now);
        TradeSignal Invalid(string reason) => new(symbol, TradeDirection.Buy, 0, 0, 0, 0, session, false, reason);
        if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || session == SessionName.Closed
            || !options.AllowedSessions.Contains(session.ToString(), StringComparer.OrdinalIgnoreCase)
            || !options.ForexMarketSessions.TradingDays.Contains(now.DayOfWeek.ToString(), StringComparer.OrdinalIgnoreCase))
            return Invalid("Outside weekday entry sessions.");
        if (newsFilter.IsBlocked(now, symbol, out var blocked)) return Invalid(blocked);
        var p = options.FtmoPullback;
        var hourly = (await marketData.GetCandlesAsync(symbol, Timeframe.H1, now.AddDays(-20), now, cancellationToken))
            .Where(c => c.OpenedAt.AddHours(1) <= now).OrderBy(c => c.OpenedAt).ToArray();
        var bars = (await marketData.GetCandlesAsync(symbol, Timeframe.M5, now.AddDays(-3), now, cancellationToken))
            .Where(c => c.OpenedAt.AddMinutes(5) <= now).OrderBy(c => c.OpenedAt).TakeLast(Math.Max(p.SlowEmaPeriod * 3, p.AtrPeriod + p.StopLookback + 2)).ToArray();
        if (hourly.Length < p.SlowEmaPeriod * 3 || bars.Length < p.SlowEmaPeriod * 3)
            return Invalid("Insufficient completed-bar warmup.");
        if (now - hourly[^1].OpenedAt > TimeSpan.FromHours(2) || now - bars[^1].OpenedAt > TimeSpan.FromMinutes(6))
            return Invalid("Stale market data.");
        if (bars.TakeLast(p.AtrPeriod + 2).Zip(bars.TakeLast(p.AtrPeriod + 2).Skip(1))
            .Any(pair => pair.Second.OpenedAt - pair.First.OpenedAt != TimeSpan.FromMinutes(5))
            || bars.Any(c => c.Low <= 0 || c.High < Math.Max(c.Open, c.Close) || c.Low > Math.Min(c.Open, c.Close)))
            return Invalid("Missing or malformed execution candles.");
        var fast = Ema(hourly.Select(c => c.Close), p.FastEmaPeriod);
        var slow = Ema(hourly.Select(c => c.Close), p.SlowEmaPeriod);
        var previousFast = Ema(hourly.SkipLast(1).Select(c => c.Close), p.FastEmaPeriod);
        var buy = fast > slow && fast > previousFast && hourly[^1].Close > fast;
        var sell = fast < slow && fast < previousFast && hourly[^1].Close < fast;
        if (!buy && !sell) return Invalid("H1 trend and slope are not aligned.");
        var direction = buy ? TradeDirection.Buy : TradeDirection.Sell;
        var last = bars[^1];
        var previous = bars[^2];
        var mean = Ema(bars.SkipLast(1).Select(c => c.Close), p.FastEmaPeriod);
        var atr = bars.Select((c, i) => i == 0 ? c.Range : Math.Max(c.Range,
            Math.Max(Math.Abs(c.High - bars[i - 1].Close), Math.Abs(c.Low - bars[i - 1].Close))))
            .TakeLast(p.AtrPeriod).Average() / options.PipSize;
        if (atr < p.MinimumAtrPips || atr > p.MaximumAtrPips) return Invalid("ATR volatility bounds rejected.");
        if (last.Range <= 0 || last.Body / last.Range < p.MinimumBodyRatio) return Invalid("Weak confirmation body.");
        if (buy ? !(previous.Low <= mean && last.IsBullish && last.Close > previous.High && last.Close > mean)
            : !(previous.High >= mean && last.IsBearish && last.Close < previous.Low && last.Close < mean))
            return Invalid("No trend pullback and directional break confirmation.");
        var entry = decimal.Round((last.Open + last.Close) / 2m, 5);
        var extremes = bars.TakeLast(p.StopLookback);
        var stop = buy ? extremes.Min(c => c.Low) - p.StopBufferPips * options.PipSize
            : extremes.Max(c => c.High) + p.StopBufferPips * options.PipSize;
        stop = decimal.Round(stop, 5);
        var risk = (entry - stop) * (buy ? 1 : -1);
        if (risk / options.PipSize < p.MinimumStopPips || risk / options.PipSize > p.MaximumStopPips)
            return Invalid("Stop distance outside configured range.");
        var target = decimal.Round(entry + (buy ? 1 : -1) * risk * p.RewardRisk, 5);
        return new(symbol, direction, entry, stop, target, p.RewardRisk, session, true,
            "Completed H1 EMA trend + M5 EMA pullback and directional break; midpoint limit, structural SL, fixed R target.",
            SetupId: $"FTMO-PB|{symbol}|{direction}|{last.OpenedAt:O}");
    }

    public static decimal Ema(IEnumerable<decimal> values, int period)
    {
        var alpha = 2m / (period + 1m);
        decimal? result = null;
        foreach (var value in values) result = result is null ? value : result + alpha * (value - result);
        return result ?? 0m;
    }
}
