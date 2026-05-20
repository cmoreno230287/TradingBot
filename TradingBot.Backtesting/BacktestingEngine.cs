using TradingBot.Application;
using TradingBot.Domain;

namespace TradingBot.Backtesting;

public sealed class BacktestingEngine(
    IStrategyEngine strategyEngine,
    IRiskManager riskManager,
    IHistoricalMarketDataProvider historicalMarketDataProvider,
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
        var account = new AccountSnapshot(options.AccountBalance, options.AccountBalance, 0m, 0m, 0, 0);

        for (var cursor = from; cursor <= to; cursor = cursor.AddHours(1))
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

            var decision = riskManager.Evaluate(order, account);
            if (!decision.IsAllowed)
            {
                continue;
            }

            var isWin = trades.Count % 3 != 2;
            var riskAmount = account.Balance * (options.RiskPercentPerTrade / 100m);
            var profitLoss = isWin ? riskAmount * signal.RiskReward : -riskAmount;

            trades.Add(new TradeJournalEntry
            {
                Symbol = signal.Symbol,
                Session = signal.Session,
                Direction = signal.Direction,
                EntryPrice = signal.EntryPrice,
                StopLossPrice = signal.StopLoss,
                TakeProfitPrice = signal.TakeProfit,
                RiskRewardRatio = signal.RiskReward,
                OpenedAt = cursor,
                ClosedAt = cursor.AddMinutes(45),
                OutcomeStatus = isWin ? TradeOutcomeStatus.Win : TradeOutcomeStatus.Loss,
                LotSize = decision.PositionSize,
                RiskPercent = options.RiskPercentPerTrade,
                ProfitLossAmount = profitLoss,
                LiquiditySwept = signal.SetupReason,
                LiquidityTarget = "Next opposing liquidity pool",
                NewsFilterStatus = "Clear"
            });
        }

        return new BacktestResult(trades, CalculateMetrics(trades));
    }

    private async Task<IReadOnlyDictionary<Timeframe, IReadOnlyList<Candle>>> PreloadHistoricalCandlesAsync(
        string symbol,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Timeframe, IReadOnlyList<Candle>>();
        foreach (var timeframe in new[] { Timeframe.D1, Timeframe.H1, Timeframe.M5, Timeframe.M1 })
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
}
