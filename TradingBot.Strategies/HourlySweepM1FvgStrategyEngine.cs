using TradingBot.Application;
using TradingBot.Domain;

namespace TradingBot.Strategies;

public sealed class HourlySweepM1FvgStrategyEngine(
    IMarketDataProvider marketData,
    INewsFilter newsFilter,
    ISessionClock sessionClock,
    TradingBotOptions options) : IStrategyEngine
{
    private HourlyLevel? savedHourlyHigh;
    private HourlyLevel? savedHourlyLow;

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

        var hourly = (await marketData.GetCandlesAsync(symbol, Timeframe.H1, now.AddHours(-6), now, cancellationToken))
            .OrderBy(candle => candle.OpenedAt)
            .ToArray();
        var completedHourly = hourly
            .Where(candle => candle.OpenedAt.AddHours(1) <= now)
            .LastOrDefault();
        if (completedHourly is null)
        {
            return Invalid(symbol, TradeDirection.Buy, session, "No completed H1 candle available.");
        }

        savedHourlyHigh = new HourlyLevel(completedHourly.High, completedHourly.OpenedAt);
        savedHourlyLow = new HourlyLevel(completedHourly.Low, completedHourly.OpenedAt);

        var m1 = (await marketData.GetCandlesAsync(symbol, Timeframe.M1, completedHourly.OpenedAt, now, cancellationToken))
            .OrderBy(candle => candle.OpenedAt)
            .ToArray();
        if (m1.Length < 5)
        {
            return Invalid(symbol, TradeDirection.Buy, session, "Not enough M1 candles for H1 sweep confirmation.");
        }

        var sellSignal = TryBuildSignal(symbol, session, m1, TradeDirection.Sell, savedHourlyHigh);
        if (sellSignal is not null)
        {
            return sellSignal;
        }

        var buySignal = TryBuildSignal(symbol, session, m1, TradeDirection.Buy, savedHourlyLow);
        return buySignal ?? Invalid(symbol, TradeDirection.Buy, session, "No H1 high/low sweep and M1 FVG setup.");
    }

    private TradeSignal? TryBuildSignal(
        string symbol,
        SessionName session,
        IReadOnlyList<Candle> m1,
        TradeDirection direction,
        HourlyLevel? level)
    {
        if (level is null)
        {
            return null;
        }

        var reclaim = direction == TradeDirection.Sell
            ? FindSellReclaim(m1, level.Price)
            : FindBuyReclaim(m1, level.Price);
        if (reclaim is null)
        {
            return null;
        }

        var structureShift = DetectMarketStructureShift(m1, direction, reclaim.Value.Index, level.Price);
        if (structureShift is null)
        {
            return null;
        }

        var fvg = DetectFirstFairValueGap(m1, direction, structureShift.Value.Index + 2);
        if (fvg is null || !HasMinimumFvgSize(fvg.Value.Gap))
        {
            return null;
        }

        var entry = ResolveEntry(fvg.Value.Gap, direction);
        var stopLoss = direction == TradeDirection.Buy
            ? reclaim.Value.ExtremePrice - (options.PipSize * options.NormalStopLossBufferPips)
            : reclaim.Value.ExtremePrice + (options.PipSize * options.NormalStopLossBufferPips);
        var risk = Math.Abs(entry - stopLoss);
        if (risk <= 0m)
        {
            return null;
        }

        var takeProfit = direction == TradeDirection.Buy
            ? entry + (risk * options.PreferredRiskReward)
            : entry - (risk * options.PreferredRiskReward);
        var rr = Math.Abs(takeProfit - entry) / risk;
        if (rr < options.MinRiskReward)
        {
            return null;
        }

        var levelName = direction == TradeDirection.Buy ? "H1 low" : "H1 high";
        return new TradeSignal(
            symbol,
            direction,
            entry,
            stopLoss,
            takeProfit,
            decimal.Round(rr, 2),
            session,
            true,
            $"{levelName} sweep/reclaim + M1 MSS + first {direction.ToString().ToLowerInvariant()} FVG",
            fvg.Value.Gap);
    }

    private static ReclaimDetection? FindSellReclaim(IReadOnlyList<Candle> candles, decimal h1High)
    {
        var swept = false;
        var sweptHigh = 0m;
        for (var i = 0; i < candles.Count; i++)
        {
            var candle = candles[i];
            if (candle.High > h1High)
            {
                swept = true;
                sweptHigh = Math.Max(sweptHigh, candle.High);
            }

            if (swept && candle.Close < h1High)
            {
                return new ReclaimDetection(i, sweptHigh);
            }
        }

        return null;
    }

    private static ReclaimDetection? FindBuyReclaim(IReadOnlyList<Candle> candles, decimal h1Low)
    {
        var swept = false;
        var sweptLow = decimal.MaxValue;
        for (var i = 0; i < candles.Count; i++)
        {
            var candle = candles[i];
            if (candle.Low < h1Low)
            {
                swept = true;
                sweptLow = Math.Min(sweptLow, candle.Low);
            }

            if (swept && candle.Close > h1Low)
            {
                return new ReclaimDetection(i, sweptLow);
            }
        }

        return null;
    }

    private static StructureShiftDetection? DetectMarketStructureShift(
        IReadOnlyList<Candle> candles,
        TradeDirection direction,
        int reclaimIndex,
        decimal hourlyLevel)
    {
        if (reclaimIndex <= 0 || reclaimIndex >= candles.Count - 1)
        {
            return null;
        }

        var prior = candles.Take(reclaimIndex + 1).TakeLast(Math.Min(reclaimIndex + 1, 10)).ToArray();
        var breakLevel = direction == TradeDirection.Buy
            ? prior.Max(candle => candle.High)
            : prior.Min(candle => candle.Low);

        for (var i = reclaimIndex + 1; i < candles.Count; i++)
        {
            var candle = candles[i];
            var shifted = direction == TradeDirection.Buy
                ? candle.Close > breakLevel && candle.Close > hourlyLevel
                : candle.Close < breakLevel && candle.Close < hourlyLevel;
            if (shifted)
            {
                return new StructureShiftDetection(i, breakLevel);
            }
        }

        return null;
    }

    private static FairValueGapDetection? DetectFirstFairValueGap(
        IReadOnlyList<Candle> candles,
        TradeDirection direction,
        int startIndex)
    {
        var start = Math.Max(2, startIndex);
        for (var i = start; i < candles.Count; i++)
        {
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

    private decimal ResolveEntry(FairValueGap fvg, TradeDirection direction)
    {
        if (string.Equals(options.FvgEntryMode, "FivePercentBoundary", StringComparison.OrdinalIgnoreCase))
        {
            return SmartMoneyStrategyEngine.ResolveFivePercentBoundaryEntry(fvg, direction, options.FVGPercentBoundary);
        }

        if (string.Equals(options.FvgEntryMode, "Boundary", StringComparison.OrdinalIgnoreCase))
        {
            return direction == TradeDirection.Buy ? fvg.LowerPrice : fvg.UpperPrice;
        }

        return fvg.Midpoint;
    }

    private static TradeSignal Invalid(string symbol, TradeDirection direction, SessionName session, string reason) =>
        new(symbol, direction, 0m, 0m, 0m, 0m, session, false, reason);

    private sealed record HourlyLevel(decimal Price, DateTimeOffset OpenedAt);

    private readonly record struct ReclaimDetection(int Index, decimal ExtremePrice);

    private readonly record struct StructureShiftDetection(int Index, decimal BreakLevel);

    private readonly record struct FairValueGapDetection(int Index, FairValueGap Gap);
}
