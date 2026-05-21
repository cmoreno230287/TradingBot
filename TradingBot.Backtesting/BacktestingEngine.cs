using TradingBot.Application;
using TradingBot.Domain;

namespace TradingBot.Backtesting;

public sealed class BacktestingEngine(
    IStrategyEngine strategyEngine,
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
            throw new InvalidOperationException($"Historical data provider '{options.Backtesting.DataSource}' returned no M5 candles for {symbol}.");
        }

        var trades = new List<TradeJournalEntry>();
        var balance = options.AccountBalance;
        var initialBalance = options.FTMOChallenge.InitialBalance > 0m ? options.FTMOChallenge.InitialBalance : options.AccountBalance;

        for (var cursor = from; cursor <= to; cursor = cursor.AddMinutes(5))
        {
            var signal = await strategyEngine.AnalyzeAsync(symbol, cursor, cancellationToken);
            if (!signal.IsValidSetup)
            {
                continue;
            }

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

            var outcome = await SimulateTradeOutcomeAsync(signal, historicalCandles[Timeframe.M5], cursor, account.Balance, cancellationToken);
            if (outcome.Status is TradeOutcomeStatus.Open or TradeOutcomeStatus.Cancelled)
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

        return new BacktestResult(trades, CalculateMetrics(trades));
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
            LongestTrailingStreak(trades, winning: false),
            ConsecutiveLosingDaysToDate(trades, cursor),
            balance - dailyProfitLoss,
            initialBalance,
            TradingDaysToDate(trades, cursor));
    }

    private async Task<SimulatedTradeOutcome> SimulateTradeOutcomeAsync(
        TradeSignal signal,
        IReadOnlyList<Candle> m5Candles,
        DateTimeOffset signalTime,
        decimal accountBalance,
        CancellationToken cancellationToken)
    {
        var expirationTime = signalTime.AddMinutes(options.PendingOrderExpirationCandlesM5 * 5);
        if (historicalTickDataProvider is not null)
        {
            var tickOutcome = await SimulateTradeOutcomeFromTicksAsync(
                signal,
                signalTime,
                expirationTime,
                accountBalance,
                cancellationToken);

            if (tickOutcome.Status != TradeOutcomeStatus.Open)
            {
                return tickOutcome;
            }
        }

        var futureCandles = m5Candles
            .Where(candle => candle.OpenedAt >= signalTime && candle.OpenedAt <= expirationTime)
            .OrderBy(candle => candle.OpenedAt)
            .ToArray();
        var fillCandle = futureCandles.FirstOrDefault(candle => IsEntryTouched(signal, candle));
        if (fillCandle is null)
        {
            return new SimulatedTradeOutcome(TradeOutcomeStatus.Cancelled, signalTime, expirationTime, 0m, "Candles");
        }

        var monitoringCandles = m5Candles
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

    private async Task<IReadOnlyDictionary<Timeframe, IReadOnlyList<Candle>>> PreloadHistoricalCandlesAsync(
        string symbol,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Timeframe, IReadOnlyList<Candle>>();
        foreach (var timeframe in new[] { Timeframe.D1, Timeframe.H1, Timeframe.M5 })
        {
            result[timeframe] = await historicalMarketDataProvider.GetCandlesAsync(
                new HistoricalDataRequest(symbol, timeframe, from, to),
                cancellationToken);
        }

        return result;
    }

    public static BacktestMetrics CalculateMetrics(IReadOnlyList<TradeJournalEntry> trades)
    {
        if (trades.Count == 0)
        {
            return new BacktestMetrics(0, 0m, 0m, 0m, 0m, 0, 0, 0m);
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
            decimal.Round(expectancy, 2));
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

    private static int LongestTrailingStreak(IEnumerable<TradeJournalEntry> trades, bool winning)
    {
        var count = 0;
        foreach (var trade in trades.OrderByDescending(trade => trade.ClosedAt ?? trade.OpenedAt))
        {
            var matches = winning ? trade.ProfitLossAmount > 0 : trade.ProfitLossAmount < 0;
            if (!matches)
            {
                break;
            }

            count++;
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
}
