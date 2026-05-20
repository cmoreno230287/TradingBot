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

public interface IHistoricalMarketDataProviderFactory
{
    IHistoricalMarketDataProvider Resolve(string providerName);
}

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
