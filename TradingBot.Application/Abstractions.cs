using TradingBot.Domain;
using TradingBot.Shared;

namespace TradingBot.Application;

public interface IMarketDataProvider
{
    Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
}

public sealed record HistoricalDataRequest(
    string Symbol,
    Timeframe Timeframe,
    DateTimeOffset From,
    DateTimeOffset To);

public interface IHistoricalMarketDataProvider
{
    string ProviderName { get; }

    Task<IReadOnlyList<Candle>> GetCandlesAsync(HistoricalDataRequest request, CancellationToken cancellationToken);
}

public sealed record HistoricalTickDataRequest(
    string Symbol,
    DateTimeOffset From,
    DateTimeOffset To);

public interface IHistoricalTickDataProvider
{
    string ProviderName { get; }

    Task<IReadOnlyList<MarketTick>> GetTicksAsync(HistoricalTickDataRequest request, CancellationToken cancellationToken);
}

public interface IHistoricalMarketDataProviderFactory
{
    IHistoricalMarketDataProvider Resolve(string providerName);
}

public sealed record ActiveTradeSummary(int OpenPositions, int PendingOrders)
{
    public int TotalActiveTrades => OpenPositions + PendingOrders;
}

public sealed record BrokerOrderCreationResult(
    string ClientOrderId,
    string BrokerOrderId,
    long? AccountId,
    long? SymbolId,
    string TradeSide,
    decimal Lots,
    long Volume,
    decimal LimitPrice,
    decimal StopLoss,
    decimal TakeProfit,
    decimal RiskReward);

public sealed record MarketExecutionSnapshot(
    string Symbol,
    decimal Bid,
    decimal Ask,
    decimal SpreadPips,
    DateTimeOffset Timestamp);

public sealed record StaleOrderCleanupResult(
    int CheckedOrders,
    int CancelledOrders,
    IReadOnlyList<string> CancelledOrderIds);

public sealed record ClosedTradeReport(
    string TradeId,
    string Symbol,
    string Session,
    string Direction,
    decimal EntryPrice,
    decimal ClosePrice,
    decimal StopLossPrice,
    decimal TakeProfitPrice,
    decimal RiskRewardRatio,
    DateTimeOffset OpenedAt,
    DateTimeOffset ClosedAt,
    decimal Volume,
    decimal Profit,
    decimal Commission,
    decimal Swap,
    decimal NetProfit,
    long MagicNumber,
    string Comment);

public interface IBrokerClient
{
    Task<Result> ValidateConnectionAsync(CancellationToken cancellationToken);

    Task<Result<OrderResult>> PlaceOrderAsync(OrderRequest request, CancellationToken cancellationToken);
}

public interface IStrategyEngine
{
    Task<TradeSignal> AnalyzeAsync(string symbol, DateTimeOffset now, CancellationToken cancellationToken);
}

public interface IRiskManager
{
    RiskDecision Evaluate(OrderRequest request, AccountSnapshot account);
}

public interface IBacktestingEngine
{
    Task<BacktestResult> RunAsync(string symbol, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
}

public interface IJournalWriter
{
    Task<string> WriteTradesAsync(IEnumerable<TradeJournalEntry> trades, string reportName, CancellationToken cancellationToken);
}

public interface INewsFilter
{
    bool IsBlocked(DateTimeOffset timestamp, string symbol, out string reason);
}

public interface ISessionClock
{
    SessionName GetCurrentSession(DateTimeOffset timestamp);
}
