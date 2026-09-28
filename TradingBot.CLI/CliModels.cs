using System.Text.Json;
using System.Text.Json.Nodes;
using TradingBot.Application;
using TradingBot.Domain;
using TradingBot.Infrastructure.Broker;
using TradingBot.Infrastructure.Configuration;
using TradingBot.Infrastructure.Filters;
using TradingBot.Infrastructure.MarketData;
using TradingBot.Infrastructure.MetaTrader;
using TradingBot.Infrastructure.Sessions;
using TradingBot.Reporting;
using TradingBot.Shared;
using TradingBot.Strategies;

internal sealed record ServiceRegistry(
    string AppsettingsPath,
    TradingBotOptions Options,
    IBrokerClient Broker,
    CTraderConnectionTester CTraderTester,
    CTraderJsonApiClient CTraderJsonApi,
    MT5BridgeClient MT5Bridge,
    IStrategyEngine Strategy,
    RiskManager Risk,
    CsvJournalWriter Journal,
    OperationalLogWriter Monitor,
    ClosedTradeTrackingWriter TradeTracker);

internal sealed record TradeTrackingCycleResult(
    int FetchedTrades,
    int WrittenTrades,
    string Directory);

internal sealed record PendingOrderInvalidationResult(
    int CheckedOrders,
    int CancelledOrders,
    IReadOnlyList<PendingOrderCancellation> Cancellations);

internal sealed record PendingOrderCancellation(
    string Ticket,
    string Reason,
    decimal? EntryDistancePips);

internal sealed record DailyTradingStopStatus(
    bool IsHalted,
    int WinningTrades,
    int LosingTrades,
    int MaxWinningTrades,
    int MaxLosingTrades,
    DateTimeOffset TradingDayStart,
    DateTimeOffset TradingDayEnd,
    string Reason);

internal sealed record ForexMarketSessionStatus(
    bool IsOpen,
    SessionName Session,
    DateTimeOffset NewYorkTime,
    string Reason);

internal readonly record struct SessionWindow(TimeSpan Start, TimeSpan End)
{
    public bool Contains(TimeSpan value) =>
        Start <= End
            ? value >= Start && value <= End
            : value >= Start || value <= End;
}
