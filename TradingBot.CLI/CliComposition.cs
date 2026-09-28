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


internal static partial class CliCommands
{
    internal static string ResolveAppsettingsPath()
    {
        var projectAppsettings = Path.Combine(Directory.GetCurrentDirectory(), "TradingBot.CLI", "appsettings.json");
        return File.Exists(projectAppsettings)
            ? projectAppsettings
            : Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    }

    internal static ServiceRegistry BuildServices(string appsettingsPath, TradingBotOptions options)
    {
        var cTraderTester = new CTraderConnectionTester(options);
        var cTraderJsonApi = new CTraderJsonApiClient(options);
        var mt5Bridge = new MT5BridgeClient(options);
        var isMt5 = IsBroker(options, "MT5");
        IMarketDataProvider marketData = isMt5
            ? new MT5MarketDataProvider(mt5Bridge)
            : new CTraderMarketDataProvider(cTraderJsonApi, options);
        var newsFilter = new NewsFilter(options);
        var sessionClock = new NewYorkSessionClock(options);
        var strategy = BuildStrategyEngine(options, marketData, newsFilter, sessionClock);
        var risk = new RiskManager(options);
        return new ServiceRegistry(
            appsettingsPath,
            options,
            isMt5 ? new MT5BrokerClient(mt5Bridge, options) : new CTraderBrokerClient(options),
            cTraderTester,
            cTraderJsonApi,
            mt5Bridge,
            strategy,
            risk,
            new CsvJournalWriter(options),
            new OperationalLogWriter(options),
            new ClosedTradeTrackingWriter(options));
    }

    internal static IStrategyEngine BuildStrategyEngine(
        TradingBotOptions options,
        IMarketDataProvider marketData,
        INewsFilter newsFilter,
        ISessionClock sessionClock)
    {
        return options.ActiveStrategy.Engine.ToLowerInvariant() switch
        {
            "smartmoney" => new SmartMoneyStrategyEngine(marketData, newsFilter, sessionClock, options),
            "hourlysweepm1fvg" => new HourlySweepM1FvgStrategyEngine(marketData, newsFilter, sessionClock, options),
            "smcliquiditysweepchoch" => new SmcLiquiditySweepChochStrategyEngine(marketData, newsFilter, sessionClock, options),
            "ftmopullback" => new FtmoPullbackStrategyEngine(marketData, newsFilter, sessionClock, options),
            "ftmosmc" => new FtmoSmcStrategyEngine(marketData, newsFilter, sessionClock, options),
            "ftmosmccontinuation" => new FtmoSmcContinuationStrategyEngine(marketData, newsFilter, sessionClock, options),
            _ => throw new InvalidOperationException($"Unsupported strategy engine '{options.ActiveStrategy.Engine}'.")
        };
    }

    internal static bool IsBroker(TradingBotOptions options, string broker) =>
        string.Equals(options.Broker, broker, StringComparison.OrdinalIgnoreCase);

}
