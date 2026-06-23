using TradingBot.Application;
using TradingBot.Domain;

namespace TradingBot.Backtesting;

public sealed class BacktestingEngine(
    Func<IMarketDataProvider, IStrategyEngine> strategyFactory,
    IRiskManager riskManager,
    IHistoricalMarketDataProvider historicalMarketDataProvider,
    IHistoricalTickDataProvider? historicalTickDataProvider,
    TradingBotOptions options) : IBacktestingEngine
{
    public async Task<BacktestResult> RunAsync(string symbol, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var historicalCandles = await PreloadHistoricalCandlesAsync(symbol, from, to, cancellationToken);
        if (historicalCandles[Timeframe.M5].Count == 0)
        {
            throw new InvalidOperationException($"Historical data provider '{options.ActiveBacktestingDataSource.DataSource}' returned no M5 candles for {symbol}.");
        }

        var simulationTimeframe = ResolveSimulationTimeframe();
        if (!historicalCandles.TryGetValue(simulationTimeframe, out var simulationCandles) || simulationCandles.Count == 0)
        {
            throw new InvalidOperationException($"Historical data provider '{options.ActiveBacktestingDataSource.DataSource}' returned no {simulationTimeframe} candles for {symbol}.");
        }

        ValidateBacktestCoverage(historicalCandles, from, simulationTimeframe);

        var strategyEngine = strategyFactory(new HistoricalBacktestMarketDataProvider(historicalCandles));
        var trades = new List<TradeJournalEntry>();
        var seenSetups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var validSetups = 0;
        var challengeInitialBalance = options.ActiveFundedAccountChallenge?.InitialBalance ?? 0m;
        var initialBalance = challengeInitialBalance > 0m ? challengeInitialBalance : options.AccountBalance;
        var balance = initialBalance;

        for (var cursor = from; cursor <= to; cursor = cursor.AddMinutes(5))
        {
            var signal = await strategyEngine.AnalyzeAsync(symbol, cursor, cancellationToken);
            if (!signal.IsValidSetup)
            {
                continue;
            }

            if (HasActiveTradeAt(trades, cursor))
            {
                continue;
            }

            var setupKey = BuildSetupKey(signal);
            if (!seenSetups.Add(setupKey))
            {
                continue;
            }

            validSetups++;
            var order = new OrderRequest(
                signal.Symbol,
                signal.Direction,
                OrderType.Limit,
                signal.EntryPrice,
                signal.StopLoss,
                signal.TakeProfit,
                0m,
                options.RiskPercentPerTrade);

            var account = BuildBacktestAccountSnapshot(trades, cursor, balance, initialBalance);
            var decision = riskManager.Evaluate(order, account);
            if (!decision.IsAllowed)
            {
                continue;
            }

            var signalTime = ResolveSignalTime(signal, cursor);
            var outcome = await SimulateTradeOutcomeAsync(signal, simulationCandles, simulationTimeframe, signalTime, account.Balance, cancellationToken);
            if (outcome.Status is TradeOutcomeStatus.Open or TradeOutcomeStatus.Cancelled)
            {
                continue;
            }

            if (HasOverlappingTrade(trades, outcome.OpenedAt, outcome.ClosedAt))
            {
                continue;
            }

            balance += outcome.ProfitLoss;

            trades.Add(new TradeJournalEntry
            {
                Symbol = signal.Symbol,
                Session = signal.Session,
                Direction = signal.Direction,
                EntryPrice = signal.EntryPrice,
                StopLossPrice = signal.StopLoss,
                TakeProfitPrice = signal.TakeProfit,
                RiskRewardRatio = signal.RiskReward,
                OpenedAt = outcome.OpenedAt,
                ClosedAt = outcome.ClosedAt,
                OutcomeStatus = outcome.Status,
                LotSize = decision.PositionSize,
                RiskPercent = options.RiskPercentPerTrade,
                ProfitLossAmount = outcome.ProfitLoss,
                LiquiditySwept = signal.SetupReason,
                LiquidityTarget = "Next opposing liquidity pool",
                NewsFilterStatus = "Clear"
            });
        }

        return new BacktestResult(trades, CalculateMetrics(trades, validSetups));
    }

    private AccountSnapshot BuildBacktestAccountSnapshot(
        IReadOnlyList<TradeJournalEntry> trades,
        DateTimeOffset cursor,
        decimal balance,
        decimal initialBalance)
    {
        var dailyProfitLoss = trades
            .Where(trade => trade.ClosedAt.HasValue && trade.ClosedAt.Value.Date == cursor.Date)
            .Sum(trade => trade.ProfitLossAmount);
        var daysSinceMonday = ((int)cursor.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        var startOfWeek = cursor.Date.AddDays(-daysSinceMonday);
        var weeklyProfitLoss = trades
            .Where(trade => trade.ClosedAt.HasValue && trade.ClosedAt.Value.Date >= startOfWeek && trade.ClosedAt.Value.Date <= cursor.Date)
            .Sum(trade => trade.ProfitLossAmount);

        return new AccountSnapshot(
            balance,
            balance,
            dailyProfitLoss,
            weeklyProfitLoss,
            ConsecutiveLossesForDate(trades, cursor.Date),
            ConsecutiveLosingDaysToDate(trades, cursor),
            balance - dailyProfitLoss,
            initialBalance,
            TradingDaysToDate(trades, cursor));
    }

    private static string BuildSetupKey(TradeSignal signal)
    {
        if (!string.IsNullOrWhiteSpace(signal.SetupId))
        {
            return signal.SetupId;
        }

        return string.Join('|', signal.Symbol, signal.Direction, signal.EntryPrice, signal.StopLoss, signal.TakeProfit, signal.SetupReason);
    }

    private bool HasActiveTradeAt(IReadOnlyList<TradeJournalEntry> trades, DateTimeOffset cursor) =>
        options.MaxActiveTrades > 0
        && trades.Count(trade => trade.OpenedAt <= cursor && (!trade.ClosedAt.HasValue || trade.ClosedAt.Value > cursor)) >= options.MaxActiveTrades;

    private bool HasOverlappingTrade(IReadOnlyList<TradeJournalEntry> trades, DateTimeOffset openedAt, DateTimeOffset? closedAt) =>
        options.MaxActiveTrades > 0
        && trades.Count(trade => IntervalsOverlap(trade.OpenedAt, trade.ClosedAt, openedAt, closedAt)) >= options.MaxActiveTrades;

    private static bool IntervalsOverlap(DateTimeOffset leftOpen, DateTimeOffset? leftClose, DateTimeOffset rightOpen, DateTimeOffset? rightClose)
    {
        var leftEnd = leftClose ?? DateTimeOffset.MaxValue;
        var rightEnd = rightClose ?? DateTimeOffset.MaxValue;
        return leftOpen < rightEnd && rightOpen < leftEnd;
    }

    private async Task<SimulatedTradeOutcome> SimulateTradeOutcomeAsync(
        TradeSignal signal,
        IReadOnlyList<Candle> candles,
        Timeframe timeframe,
        DateTimeOffset signalTime,
        decimal accountBalance,
        CancellationToken cancellationToken)
    {
        var expirationTime = signalTime.AddMinutes(ResolveExpirationMinutes(timeframe));
        if (historicalTickDataProvider is not null)
        {
            SimulatedTradeOutcome tickOutcome;
            try
            {
                tickOutcome = await SimulateTradeOutcomeFromTicksAsync(
                    signal,
                    signalTime,
                    expirationTime,
                    accountBalance,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                tickOutcome = new SimulatedTradeOutcome(TradeOutcomeStatus.Open, signalTime, null, 0m, $"TicksUnavailable:{exception.Message}");
            }

            if (tickOutcome.Status != TradeOutcomeStatus.Open)
            {
                return tickOutcome;
            }
        }

        var futureCandles = candles
            .Where(candle => candle.OpenedAt >= signalTime && candle.OpenedAt <= expirationTime)
            .OrderBy(candle => candle.OpenedAt)
            .ToArray();
        var fillCandle = futureCandles.FirstOrDefault(candle => IsEntryTouched(signal, candle));
        if (fillCandle is null)
        {
            return new SimulatedTradeOutcome(TradeOutcomeStatus.Cancelled, signalTime, expirationTime, 0m, "Candles");
        }

        var monitoringCandles = candles
            .Where(candle => candle.OpenedAt >= fillCandle.OpenedAt)
            .OrderBy(candle => candle.OpenedAt);
        var riskAmount = accountBalance * (options.RiskPercentPerTrade / 100m);

        foreach (var candle in monitoringCandles)
        {
            var stopHit = IsStopLossTouched(signal, candle);
            var targetHit = IsTakeProfitTouched(signal, candle);
            if (stopHit)
            {
                return new SimulatedTradeOutcome(TradeOutcomeStatus.Loss, fillCandle.OpenedAt, candle.OpenedAt, -riskAmount, "Candles");
            }

            if (targetHit)
            {
                return new SimulatedTradeOutcome(TradeOutcomeStatus.Win, fillCandle.OpenedAt, candle.OpenedAt, riskAmount * signal.RiskReward, "Candles");
            }
        }

        return new SimulatedTradeOutcome(TradeOutcomeStatus.Open, fillCandle.OpenedAt, null, 0m, "Candles");
    }

    private async Task<SimulatedTradeOutcome> SimulateTradeOutcomeFromTicksAsync(
        TradeSignal signal,
        DateTimeOffset signalTime,
        DateTimeOffset expirationTime,
        decimal accountBalance,
        CancellationToken cancellationToken)
    {
        var ticks = await historicalTickDataProvider!.GetTicksAsync(
            new HistoricalTickDataRequest(signal.Symbol, signalTime, expirationTime.AddDays(5)),
            cancellationToken);
        if (ticks.Count == 0)
        {
            return new SimulatedTradeOutcome(TradeOutcomeStatus.Open, signalTime, null, 0m, "TicksUnavailable");
        }

        var fillTick = ticks
            .Where(tick => tick.Timestamp >= signalTime && tick.Timestamp <= expirationTime)
            .FirstOrDefault(tick => IsEntryTouched(signal, tick));
        if (fillTick is null)
        {
            return new SimulatedTradeOutcome(TradeOutcomeStatus.Cancelled, signalTime, expirationTime, 0m, "Ticks");
        }

        var riskAmount = accountBalance * (options.RiskPercentPerTrade / 100m);
        foreach (var tick in ticks.Where(tick => tick.Timestamp >= fillTick.Timestamp))
        {
            if (IsStopLossTouched(signal, tick))
            {
                return new SimulatedTradeOutcome(TradeOutcomeStatus.Loss, fillTick.Timestamp, tick.Timestamp, -riskAmount, "Ticks");
            }

            if (IsTakeProfitTouched(signal, tick))
            {
                return new SimulatedTradeOutcome(TradeOutcomeStatus.Win, fillTick.Timestamp, tick.Timestamp, riskAmount * signal.RiskReward, "Ticks");
            }
        }

        return new SimulatedTradeOutcome(TradeOutcomeStatus.Open, fillTick.Timestamp, null, 0m, "Ticks");
    }

    private static bool IsEntryTouched(TradeSignal signal, Candle candle) =>
        signal.Direction == TradeDirection.Buy
            ? candle.Low <= signal.EntryPrice
            : candle.High >= signal.EntryPrice;

    private static bool IsEntryTouched(TradeSignal signal, MarketTick tick) =>
        signal.Direction == TradeDirection.Buy
            ? tick.Ask <= signal.EntryPrice
            : tick.Bid >= signal.EntryPrice;

    private static bool IsStopLossTouched(TradeSignal signal, Candle candle) =>
        signal.Direction == TradeDirection.Buy
            ? candle.Low <= signal.StopLoss
            : candle.High >= signal.StopLoss;

    private static bool IsStopLossTouched(TradeSignal signal, MarketTick tick) =>
        signal.Direction == TradeDirection.Buy
            ? tick.Bid <= signal.StopLoss
            : tick.Ask >= signal.StopLoss;

    private static bool IsTakeProfitTouched(TradeSignal signal, Candle candle) =>
        signal.Direction == TradeDirection.Buy
            ? candle.High >= signal.TakeProfit
            : candle.Low <= signal.TakeProfit;

    private static bool IsTakeProfitTouched(TradeSignal signal, MarketTick tick) =>
        signal.Direction == TradeDirection.Buy
            ? tick.Bid >= signal.TakeProfit
            : tick.Ask <= signal.TakeProfit;

    private Timeframe ResolveSimulationTimeframe() =>
        string.Equals(options.ActiveStrategy.ExecutionTimeframe, "M1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(options.ActiveStrategy.EntryTimeframe, "M1", StringComparison.OrdinalIgnoreCase)
            ? Timeframe.M1
            : Timeframe.M5;

    private static DateTimeOffset ResolveSignalTime(TradeSignal signal, DateTimeOffset cursor) =>
        signal.FairValueGap?.CreatedAt is { } createdAt && createdAt < cursor
            ? createdAt
            : cursor;

    private int ResolveExpirationMinutes(Timeframe timeframe) =>
        timeframe == Timeframe.M1
            ? options.PendingOrderExpirationCandlesM1
            : options.PendingOrderExpirationCandlesM5 * 5;

    private void ValidateBacktestCoverage(
        IReadOnlyDictionary<Timeframe, IReadOnlyList<Candle>> candles,
        DateTimeOffset requestedFrom,
        Timeframe simulationTimeframe)
    {
        ValidateTimeframeCoverage(candles, Timeframe.M5, requestedFrom, TimeSpan.FromMinutes(5));
        ValidateTimeframeCoverage(candles, simulationTimeframe, requestedFrom, TimeSpan.FromMinutes(simulationTimeframe == Timeframe.M1 ? 1 : 5));
    }

    private void ValidateTimeframeCoverage(
        IReadOnlyDictionary<Timeframe, IReadOnlyList<Candle>> candles,
        Timeframe timeframe,
        DateTimeOffset requestedFrom,
        TimeSpan tolerance)
    {
        if (!candles.TryGetValue(timeframe, out var timeframeCandles) || timeframeCandles.Count == 0)
        {
            throw new InvalidOperationException($"Backtest cannot start at {requestedFrom:O}: no {timeframe} candles were loaded.");
        }

        var first = timeframeCandles[0].OpenedAt;
        if (first > requestedFrom.Add(tolerance))
        {
            throw new InvalidOperationException(
                $"Backtest requested from {requestedFrom:O}, but {options.ActiveBacktestingDataSource.DataSource} {timeframe} data starts at {first:O}. " +
                $"Download/import {timeframe} history for the requested period or start the backtest at/after {first:yyyy-MM-dd HH:mm:ss zzz}.");
        }
    }

    private async Task<IReadOnlyDictionary<Timeframe, IReadOnlyList<Candle>>> PreloadHistoricalCandlesAsync(
        string symbol,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Timeframe, IReadOnlyList<Candle>>();
        result[Timeframe.D1] = await historicalMarketDataProvider.GetCandlesAsync(
            new HistoricalDataRequest(symbol, Timeframe.D1, from.AddDays(-60), to),
            cancellationToken);
        result[Timeframe.H1] = await historicalMarketDataProvider.GetCandlesAsync(
            new HistoricalDataRequest(symbol, Timeframe.H1, from.AddDays(-10), to),
            cancellationToken);
        result[Timeframe.M1] = await historicalMarketDataProvider.GetCandlesAsync(
            new HistoricalDataRequest(symbol, Timeframe.M1, from.AddHours(-6), to),
            cancellationToken);
        result[Timeframe.M5] = await historicalMarketDataProvider.GetCandlesAsync(
            new HistoricalDataRequest(symbol, Timeframe.M5, from.AddHours(-12), to),
            cancellationToken);

        return result;
    }

    public static BacktestMetrics CalculateMetrics(IReadOnlyList<TradeJournalEntry> trades, int validSetups = 0)
    {
        if (trades.Count == 0)
        {
            return new BacktestMetrics(0, 0m, 0m, 0m, 0m, 0, 0, 0m, validSetups);
        }

        var wins = trades.Count(t => t.ProfitLossAmount > 0);
        var losses = trades.Count(t => t.ProfitLossAmount < 0);
        var grossProfit = trades.Where(t => t.ProfitLossAmount > 0).Sum(t => t.ProfitLossAmount);
        var grossLoss = Math.Abs(trades.Where(t => t.ProfitLossAmount < 0).Sum(t => t.ProfitLossAmount));
        var profitFactor = grossLoss == 0 ? grossProfit : grossProfit / grossLoss;
        var expectancy = trades.Average(t => t.ProfitLossAmount);

        decimal equity = 0;
        decimal peak = 0;
        decimal maxDrawdown = 0;
        foreach (var trade in trades)
        {
            equity += trade.ProfitLossAmount;
            peak = Math.Max(peak, equity);
            maxDrawdown = Math.Max(maxDrawdown, peak - equity);
        }

        return new BacktestMetrics(
            trades.Count,
            decimal.Round(wins / (decimal)trades.Count * 100m, 2),
            decimal.Round(profitFactor, 2),
            decimal.Round(maxDrawdown, 2),
            decimal.Round(trades.Average(t => t.RiskRewardRatio), 2),
            LongestStreak(trades, false),
            LongestStreak(trades, true),
            decimal.Round(expectancy, 2),
            validSetups);
    }

    private static int LongestStreak(IEnumerable<TradeJournalEntry> trades, bool winning)
    {
        var best = 0;
        var current = 0;
        foreach (var trade in trades)
        {
            var matches = winning ? trade.ProfitLossAmount > 0 : trade.ProfitLossAmount < 0;
            current = matches ? current + 1 : 0;
            best = Math.Max(best, current);
        }

        return best;
    }

    private static int ConsecutiveLossesForDate(IEnumerable<TradeJournalEntry> trades, DateTime date)
    {
        var count = 0;
        foreach (var trade in trades
            .Where(trade => (trade.ClosedAt ?? trade.OpenedAt).Date == date)
            .OrderByDescending(trade => trade.ClosedAt ?? trade.OpenedAt))
        {
            if (trade.ProfitLossAmount < 0m)
            {
                count++;
                continue;
            }

            if (trade.ProfitLossAmount > 0m)
            {
                break;
            }
        }

        return count;
    }

    private static int ConsecutiveLosingDaysToDate(IEnumerable<TradeJournalEntry> trades, DateTimeOffset cursor)
    {
        var days = trades
            .Where(trade => trade.ClosedAt.HasValue && trade.ClosedAt.Value.Date <= cursor.Date)
            .GroupBy(trade => trade.ClosedAt!.Value.Date)
            .Select(group => new { Date = group.Key, ProfitLoss = group.Sum(trade => trade.ProfitLossAmount) })
            .OrderByDescending(day => day.Date);

        var count = 0;
        foreach (var day in days)
        {
            if (day.ProfitLoss < 0m)
            {
                count++;
                continue;
            }

            if (day.ProfitLoss > 0m)
            {
                break;
            }
        }

        return count;
    }

    private static int TradingDaysToDate(IEnumerable<TradeJournalEntry> trades, DateTimeOffset cursor) =>
        trades
            .Where(trade => trade.ClosedAt.HasValue && trade.ClosedAt.Value.Date <= cursor.Date)
            .Select(trade => trade.ClosedAt!.Value.Date)
            .Distinct()
            .Count();

    private sealed record SimulatedTradeOutcome(
        TradeOutcomeStatus Status,
        DateTimeOffset OpenedAt,
        DateTimeOffset? ClosedAt,
        decimal ProfitLoss,
        string DataSource);

    private sealed class HistoricalBacktestMarketDataProvider(IReadOnlyDictionary<Timeframe, IReadOnlyList<Candle>> candles) : IMarketDataProvider
    {
        public Task<IReadOnlyList<Candle>> GetCandlesAsync(
            string symbol,
            Timeframe timeframe,
            DateTimeOffset from,
            DateTimeOffset to,
            CancellationToken cancellationToken)
        {
            if (!candles.TryGetValue(timeframe, out var timeframeCandles))
            {
                return Task.FromResult<IReadOnlyList<Candle>>([]);
            }

            var filtered = timeframeCandles
                .Where(candle => string.Equals(candle.Symbol, symbol, StringComparison.OrdinalIgnoreCase)
                    && candle.OpenedAt >= from
                    && candle.OpenedAt <= to)
                .OrderBy(candle => candle.OpenedAt)
                .ToArray();

            return Task.FromResult<IReadOnlyList<Candle>>(filtered);
        }
    }
}
