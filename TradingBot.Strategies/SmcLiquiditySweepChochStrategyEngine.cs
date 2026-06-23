using TradingBot.Application;
using TradingBot.Domain;

namespace TradingBot.Strategies;

public sealed class SmcLiquiditySweepChochStrategyEngine(
    IMarketDataProvider marketData,
    INewsFilter newsFilter,
    ISessionClock sessionClock,
    TradingBotOptions options) : IStrategyEngine
{
    public async Task<TradeSignal> AnalyzeAsync(string symbol, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var session = sessionClock.GetCurrentSession(now);
        if (session is not (SessionName.London or SessionName.NewYork or SessionName.LondonNewYorkOverlap)
            && !options.TradeOutKillZoneTime)
        {
            return Invalid(symbol, TradeDirection.Buy, session, "V2 trades only London and New York liquidity sessions.");
        }

        if (newsFilter.IsBlocked(now, symbol, out var newsReason))
        {
            return Invalid(symbol, TradeDirection.Buy, session, newsReason);
        }

        var h1 = (await marketData.GetCandlesAsync(symbol, Timeframe.H1, now.AddDays(-10), now, cancellationToken))
            .OrderBy(candle => candle.OpenedAt)
            .ToArray();
        var d1 = (await marketData.GetCandlesAsync(symbol, Timeframe.D1, now.AddDays(-60), now, cancellationToken))
            .OrderBy(candle => candle.OpenedAt)
            .ToArray();
        var ltf = (await marketData.GetCandlesAsync(symbol, Timeframe.M5, now.AddHours(-12), now, cancellationToken))
            .OrderBy(candle => candle.OpenedAt)
            .TakeLast(options.SetupLookbackCandlesM5)
            .ToArray();
        var entryTimeframe = (await marketData.GetCandlesAsync(symbol, Timeframe.M1, now.AddHours(-12), now, cancellationToken))
            .OrderBy(candle => candle.OpenedAt)
            .TakeLast(options.SetupLookbackCandlesM5 * 5)
            .ToArray();

        if (ltf.Length < options.LiquiditySweepLookbackCandles + 5)
        {
            return Invalid(symbol, TradeDirection.Buy, session, "Not enough LTF candles for V2 sweep/CHOCH analysis.");
        }

        if (entryTimeframe.Length < (options.LiquiditySweepLookbackCandles * 2) + 5)
        {
            return Invalid(symbol, TradeDirection.Buy, session, "Not enough M1 candles for V2 execution confirmation.");
        }

        var h1Bias = SmartMoneyStrategyEngine.CalculateBias(h1, options.BiasSwingStrength);
        if (options.UseDailyBiasFilter && h1Bias != MarketBias.Neutral)
        {
            var d1Bias = SmartMoneyStrategyEngine.CalculateBias(d1, options.BiasSwingStrength);
            if (d1Bias == MarketBias.Neutral || d1Bias != h1Bias)
            {
                return Invalid(symbol, TradeDirection.Buy, session, "D1/H1 bias is unclear or not aligned.");
            }
        }

        var directions = h1Bias switch
        {
            MarketBias.Bullish => [TradeDirection.Buy],
            MarketBias.Bearish => [TradeDirection.Sell],
            _ => new[] { TradeDirection.Buy, TradeDirection.Sell }
        };
        foreach (var direction in directions)
        {
            var signal = TryBuildSignal(symbol, session, ltf, entryTimeframe, direction);
            if (signal is not null)
            {
                return signal;
            }
        }

        return Invalid(symbol, TradeDirection.Buy, session, "No complete V2 liquidity sweep + CHOCH/BOS + displacement setup.");
    }

    private TradeSignal? TryBuildSignal(
        string symbol,
        SessionName session,
        IReadOnlyList<Candle> contextCandles,
        IReadOnlyList<Candle> entryCandles,
        TradeDirection direction)
    {
        var entryLookback = Math.Max(6, options.LiquiditySweepLookbackCandles * 2);
        foreach (var candidate in DetectSweepCandidates(contextCandles, entryCandles, direction, options.LiquiditySweepLookbackCandles, entryLookback))
        {
            var choch = DetectChochOrBos(entryCandles, direction, candidate.EntrySweepIndex, entryLookback);
            if (choch is null)
            {
                continue;
            }

            if (entryCandles.Count - 1 - choch.Value.Index > options.MaxSetupAgeCandlesM5 * 5)
            {
                continue;
            }

            if (options.RequireDisplacement && !HasDisplacement(entryCandles, choch.Value.Index))
            {
                continue;
            }

            var entryZone = ResolveEntryZone(entryCandles, direction, choch.Value.Index);
            if (entryZone is null)
            {
                continue;
            }

            var entry = ResolveEntry(entryZone.Value, direction);
            if (options.UsePremiumDiscountFilter && !IsInPremiumDiscount(contextCandles, direction, entry))
            {
                continue;
            }

            var stopLoss = direction == TradeDirection.Buy
                ? Math.Min(candidate.Sweep.ExtremePrice, entryZone.Value.LowerPrice) - (options.PipSize * options.NormalStopLossBufferPips)
                : Math.Max(candidate.Sweep.ExtremePrice, entryZone.Value.UpperPrice) + (options.PipSize * options.NormalStopLossBufferPips);
            var risk = Math.Abs(entry - stopLoss);
            if (risk <= 0m)
            {
                continue;
            }

            var liquidityTarget = FindLiquidityTarget(entryCandles, direction, candidate.EntrySweepIndex);
            if (liquidityTarget is null)
            {
                continue;
            }

            var configuredReward = Math.Max(options.MinRiskReward, options.PreferredRiskReward);
            var takeProfit = direction == TradeDirection.Buy
                ? entry + (risk * configuredReward)
                : entry - (risk * configuredReward);
            var liquidityReward = direction == TradeDirection.Buy ? liquidityTarget.Value - entry : entry - liquidityTarget.Value;
            if (liquidityReward <= 0m || liquidityReward < risk * configuredReward)
            {
                continue;
            }

            var rr = Math.Abs(takeProfit - entry) / risk;
            if (rr < options.MinRiskReward)
            {
                continue;
            }

            var fvg = entryZone.Value.Source == EntryZoneSource.Fvg
                ? new FairValueGap(direction, entryZone.Value.LowerPrice, entryZone.Value.UpperPrice, entryZone.Value.CreatedAt)
                : null;
            var setupId = string.Join(
                '|',
                symbol,
                "SMC-V2",
                direction,
                $"sweep:{candidate.Sweep.OccurredAt:O}",
                $"choch:{choch.Value.OccurredAt:O}");

            return new TradeSignal(
                symbol,
                direction,
                entry,
                stopLoss,
                takeProfit,
                decimal.Round(rr, 2),
                session,
                true,
                $"{direction} V2: HTF bias + liquidity sweep + CHOCH/BOS + displacement + {entryZone.Value.Source} entry + {configuredReward:0.##}R target before liquidity",
                fvg,
                setupId);
        }

        return null;
    }

    private static int? FindEntrySweepIndex(IReadOnlyList<Candle> candles, DateTimeOffset sweepOpenedAt)
    {
        for (var i = 0; i < candles.Count; i++)
        {
            if (candles[i].OpenedAt >= sweepOpenedAt)
            {
                return i;
            }
        }

        return null;
    }

    private static IReadOnlyList<SweepCandidate> DetectSweepCandidates(
        IReadOnlyList<Candle> contextCandles,
        IReadOnlyList<Candle> entryCandles,
        TradeDirection direction,
        int contextLookback,
        int entryLookback)
    {
        var candidates = new List<SweepCandidate>();
        foreach (var sweep in DetectLiquiditySweeps(contextCandles, direction, contextLookback))
        {
            var entryIndex = FindEntrySweepIndex(entryCandles, sweep.OccurredAt);
            if (entryIndex is not null)
            {
                candidates.Add(new SweepCandidate(sweep, entryIndex.Value));
            }
        }

        foreach (var sweep in DetectLiquiditySweeps(entryCandles, direction, entryLookback))
        {
            candidates.Add(new SweepCandidate(sweep, sweep.Index));
        }

        return candidates
            .GroupBy(candidate => candidate.Sweep.OccurredAt)
            .Select(group => group.OrderBy(item => item.EntrySweepIndex).First())
            .OrderByDescending(candidate => candidate.Sweep.OccurredAt)
            .ToArray();
    }

    private static IReadOnlyList<SweepDetection> DetectLiquiditySweeps(IReadOnlyList<Candle> candles, TradeDirection direction, int lookback)
    {
        var sweeps = new List<SweepDetection>();
        var start = Math.Max(lookback, candles.Count - (lookback * 4));
        for (var i = start; i < candles.Count - 2; i++)
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
                    sweeps.Add(new SweepDetection(i, priorLow, candle.Low, candle.OpenedAt));
                }
            }
            else
            {
                var priorHigh = prior.Max(item => item.High);
                if (candle.High > priorHigh && candle.Close < priorHigh)
                {
                    sweeps.Add(new SweepDetection(i, priorHigh, candle.High, candle.OpenedAt));
                }
            }
        }

        return sweeps.OrderByDescending(item => item.Index).ToArray();
    }

    private static StructureDetection? DetectChochOrBos(IReadOnlyList<Candle> candles, TradeDirection direction, int sweepIndex, int lookback)
    {
        var prior = candles.Take(sweepIndex).TakeLast(Math.Max(3, lookback)).ToArray();
        if (prior.Length == 0 || sweepIndex >= candles.Count - 1)
        {
            return null;
        }

        var breakLevel = direction == TradeDirection.Buy
            ? prior.Max(candle => candle.High)
            : prior.Min(candle => candle.Low);

        for (var i = sweepIndex + 1; i < candles.Count; i++)
        {
            var candle = candles[i];
            var broke = direction == TradeDirection.Buy
                ? candle.Close > breakLevel
                : candle.Close < breakLevel;
            if (broke)
            {
                return new StructureDetection(i, breakLevel, candle.OpenedAt);
            }
        }

        return null;
    }

    private bool HasDisplacement(IReadOnlyList<Candle> candles, int index)
    {
        if (index <= 0 || index >= candles.Count)
        {
            return false;
        }

        var candle = candles[index];
        var prior = candles.Take(index).TakeLast(Math.Min(index, options.LiquiditySweepLookbackCandles)).ToArray();
        if (prior.Length == 0 || candle.Range <= 0m)
        {
            return false;
        }

        return candle.Body / candle.Range >= options.DisplacementMinBodyToRangeRatio
            && candle.Range >= prior.Average(item => item.Range) * options.DisplacementAtrMultiplier;
    }

    private static EntryZone? ResolveEntryZone(IReadOnlyList<Candle> candles, TradeDirection direction, int displacementIndex)
    {
        var fvg = DetectFairValueGap(candles, direction, displacementIndex);
        if (fvg is not null)
        {
            return fvg;
        }

        return DetectOrderBlock(candles, direction, displacementIndex);
    }

    private static EntryZone? DetectFairValueGap(IReadOnlyList<Candle> candles, TradeDirection direction, int displacementIndex)
    {
        var start = Math.Max(2, displacementIndex);
        for (var i = start; i < Math.Min(candles.Count, displacementIndex + 4); i++)
        {
            var candle1 = candles[i - 2];
            var candle3 = candles[i];
            if (direction == TradeDirection.Buy && candle3.Low > candle1.High)
            {
                return new EntryZone(EntryZoneSource.Fvg, candle1.High, candle3.Low, candle3.OpenedAt);
            }

            if (direction == TradeDirection.Sell && candle3.High < candle1.Low)
            {
                return new EntryZone(EntryZoneSource.Fvg, candle3.High, candle1.Low, candle3.OpenedAt);
            }
        }

        return null;
    }

    private static EntryZone? DetectOrderBlock(IReadOnlyList<Candle> candles, TradeDirection direction, int displacementIndex)
    {
        for (var i = displacementIndex - 1; i >= Math.Max(0, displacementIndex - 8); i--)
        {
            var candle = candles[i];
            if (direction == TradeDirection.Buy && candle.IsBearish)
            {
                return new EntryZone(EntryZoneSource.OrderBlock, candle.Low, candle.High, candle.OpenedAt);
            }

            if (direction == TradeDirection.Sell && candle.IsBullish)
            {
                return new EntryZone(EntryZoneSource.OrderBlock, candle.Low, candle.High, candle.OpenedAt);
            }
        }

        return null;
    }

    private decimal ResolveEntry(EntryZone zone, TradeDirection direction)
    {
        if (zone.Source == EntryZoneSource.OrderBlock)
        {
            return direction == TradeDirection.Buy ? zone.UpperPrice : zone.LowerPrice;
        }

        if (string.Equals(options.FvgEntryMode, "FivePercentBoundary", StringComparison.OrdinalIgnoreCase))
        {
            return SmartMoneyStrategyEngine.ResolveFivePercentBoundaryEntry(
                new FairValueGap(direction, zone.LowerPrice, zone.UpperPrice, zone.CreatedAt),
                direction);
        }

        if (string.Equals(options.FvgEntryMode, "Boundary", StringComparison.OrdinalIgnoreCase))
        {
            return direction == TradeDirection.Buy ? zone.UpperPrice : zone.LowerPrice;
        }

        return (zone.LowerPrice + zone.UpperPrice) / 2m;
    }

    private static bool IsInPremiumDiscount(IReadOnlyList<Candle> candles, TradeDirection direction, decimal entry)
    {
        var high = candles.Max(candle => candle.High);
        var low = candles.Min(candle => candle.Low);
        var equilibrium = (high + low) / 2m;
        return direction == TradeDirection.Buy
            ? entry < equilibrium
            : entry > equilibrium;
    }

    private static decimal? FindLiquidityTarget(IReadOnlyList<Candle> candles, TradeDirection direction, int sweepIndex)
    {
        var external = candles
            .Skip(sweepIndex + 1)
            .Concat(candles.Take(sweepIndex).TakeLast(48))
            .ToArray();
        if (external.Length == 0)
        {
            return null;
        }

        return direction == TradeDirection.Buy
            ? external.Max(candle => candle.High)
            : external.Min(candle => candle.Low);
    }

    private static TradeSignal Invalid(string symbol, TradeDirection direction, SessionName session, string reason) =>
        new(symbol, direction, 0m, 0m, 0m, 0m, session, false, reason);

    private readonly record struct SweepDetection(int Index, decimal LiquidityLevel, decimal ExtremePrice, DateTimeOffset OccurredAt);

    private readonly record struct SweepCandidate(SweepDetection Sweep, int EntrySweepIndex);

    private readonly record struct StructureDetection(int Index, decimal BreakLevel, DateTimeOffset OccurredAt);

    private readonly record struct EntryZone(EntryZoneSource Source, decimal LowerPrice, decimal UpperPrice, DateTimeOffset CreatedAt);

    private enum EntryZoneSource
    {
        Fvg,
        OrderBlock
    }
}
