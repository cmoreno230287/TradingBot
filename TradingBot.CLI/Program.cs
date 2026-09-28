using System.Text.Json;
using TradingBot.Application;
using TradingBot.Infrastructure.Configuration;

using static CliCommands;

using var cancellationTokenSource = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    if (!cancellationTokenSource.IsCancellationRequested)
    {
        cancellationTokenSource.Cancel();
    }
};

var cancellationToken = cancellationTokenSource.Token;
var command = args.Length == 0 ? "help" : args[0].ToLowerInvariant();
if (command is "backtest" or "ftmo-backtest" or "backtest-learning" or "find-recent-setups" or "download-history")
{
    Console.Error.WriteLine("This command was removed. This build supports live trading only.");
    return 2;
}
var appsettingsPath = ReadKeyValueArg(args, "config") ?? ResolveAppsettingsPath();
if (!File.Exists(appsettingsPath)) { Console.Error.WriteLine("Configuration file not found."); return 2; }
TradingBotOptions options;
try { options = JsonOptionsLoader.Load(appsettingsPath); }
catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or ArgumentException)
{
    Console.Error.WriteLine($"Unable to load configuration: {error.Message}");
    return 2;
}
var validation = OptionsValidator.Validate(options);

if (!validation.IsSuccess)
{
    Console.Error.WriteLine($"Configuration invalid: {validation.Error}");
    return 2;
}

var services = BuildServices(appsettingsPath, options);
if (!options.LiveTradingEnabled && command is "mt5-createorder" or "ctrader-createorder")
{
    Console.Error.WriteLine("LiveTradingEnabled=false blocks new orders.");
    return 2;
}

return command switch
{
    "start" => await StartAsync(services, args, cancellationToken),
    "stop" => Stop(),
    "analyze" => await AnalyzeAsync(services, cancellationToken),
    "status" => await FtmoCommands.StatusAsync(options, services.MT5Bridge, cancellationToken),
    "ctrader-connect" => await CTraderConnectAsync(services, cancellationToken),
    "ctrader-authorize" => await CTraderAuthorizeAsync(services, cancellationToken),
    "ctrader-request-token" => await CTraderRequestTokenAsync(services, cancellationToken),
    "ctrader-refresh-token" => await CTraderRefreshTokenAsync(services, cancellationToken),
    "ctrader-accounts-list" => await CTraderAccountsListAsync(services, cancellationToken),
    "ctrader-account-details" => await CTraderAccountDetailsAsync(services, cancellationToken),
    "ctrader-symbols" => await CTraderSymbolsAsync(services, args, cancellationToken),
    "ctrader-createorder" => await CTraderCreateOrderAsync(services, args, cancellationToken),
    "mt5-test-connection" => await MT5TestConnectionAsync(services, cancellationToken),
    "mt5-account-details" => await MT5AccountDetailsAsync(services, cancellationToken),
    "mt5-symbols" => await MT5SymbolsAsync(services, args, cancellationToken),
    "mt5-createorder" => await MT5CreateOrderAsync(services, args, cancellationToken),
    "analyze-and-createorder" => await AnalyzeAndCreateOrderAsync(services, args, cancellationToken),
    _ => Help()
};
