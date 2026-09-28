using System.Text.Json;
using TradingBot.Application;
using TradingBot.Infrastructure.MetaTrader;

internal static class FtmoCommands
{
    public static async Task<int> StartAsync(TradingBotOptions options, IStrategyEngine strategy, MT5BridgeClient bridge,
        bool once, CancellationToken token)
    {
        if (!options.FtmoProtection.Enabled || options.MT5.AccountId <= 0)
        {
            Console.Error.WriteLine("Protected live execution requires an explicit MT5 account ID and FtmoProtection.Enabled.");
            return 2;
        }
        // A named kernel object enforces one CLI per account without writable storage.
        using var lease = new Mutex(false, $"Local\\TradingBot.Account.{options.MT5.AccountId}", out var created);
        if (!created) { Console.Error.WriteLine("An account process is already running."); return 2; }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var processHealth = new LiveProcessHealth();
        await using var endpoint = new LiveHealthEndpoint(processHealth, options.MT5.AccountId,
            Environment.GetEnvironmentVariable("TRADINGBOT_STARTUP_ID") ?? "", () => lifetime.Cancel());
        Console.WriteLine(JsonSerializer.Serialize(new { engine = options.ActiveStrategy.Engine,
            accountId = options.MT5.AccountId, riskPercent = options.FtmoProtection.RiskPercent,
            bridgeClientAccountId = bridge.ConfiguredAccountId,
            entryBlock = LiveEntryPolicy.BlockReason(options), protection = "starting", once }));
        using var events = new LiveTradingEvents(options, bridge);
        var coordinator = new LiveTradingCoordinator(options, strategy, new MT5LiveTradingBroker(bridge), events, processHealth: processHealth);
        try { await coordinator.RunAsync(once, lifetime.Token); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 4; }
        return 0;
    }

    public static async Task<int> StatusAsync(TradingBotOptions options, MT5BridgeClient bridge, CancellationToken token)
    {
        var snapshot = await bridge.FtmoRequestAsync("status", null, 0, token);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
        string? calendarError = null;
        try { await new TradingBot.Infrastructure.Filters.HttpNewsCalendar(options, client).RefreshAsync(DateTimeOffset.UtcNow, token); }
        catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested) { calendarError = error.Message; }
        var newsBlocked = new TradingBot.Infrastructure.Filters.NewsFilter(options).IsBlocked(DateTimeOffset.UtcNow, options.Symbol, out var newsReason);
        var permission = LiveEntryPolicy.BlockReason(options);
        var healthy = snapshot.IsSuccess && snapshot.Value.GetProperty("entryEligible").GetBoolean();
        Console.WriteLine(JsonSerializer.Serialize(new { engine = options.ActiveStrategy.Engine,
            options.LiveTradingEnabled, options.MT5.AllowLiveOrderCreation,
            entryBlock = permission ?? (newsBlocked ? newsReason : !healthy ? "Protection/reconciliation is not ready." : null),
            entryEligible = permission is null && !newsBlocked && healthy,
            options.FtmoProtection.AccountVariant,
            options.FtmoProtection.RiskPercent,
            news = NewsCalendarState.Current(options, DateTimeOffset.UtcNow), newsBlocked, newsReason, calendarError,
            brokerAvailable = snapshot.IsSuccess, error = snapshot.Error,
            health = snapshot.IsSuccess ? (JsonElement?)snapshot.Value : null }));
        return snapshot.IsSuccess ? 0 : 4;
    }
}
