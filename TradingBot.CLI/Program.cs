using System.Text.Json;
using System.Text.Json.Nodes;
using TradingBot.Application;
using TradingBot.Backtesting;
using TradingBot.Domain;
using TradingBot.Infrastructure.Broker;
using TradingBot.Infrastructure.Configuration;
using TradingBot.Infrastructure.Filters;
using TradingBot.Infrastructure.MarketData;
using TradingBot.Infrastructure.Sessions;
using TradingBot.Reporting;
using TradingBot.Shared;
using TradingBot.Strategies;

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
var appsettingsPath = ResolveAppsettingsPath();
var options = JsonOptionsLoader.Load(appsettingsPath);
var validation = OptionsValidator.Validate(options);

if (!validation.IsSuccess)
{
    Console.Error.WriteLine($"Configuration invalid: {validation.Error}");
    return 2;
}

var services = BuildServices(appsettingsPath, options);

return command switch
{
    "start" => await StartAsync(services, args, cancellationToken),
    "stop" => Stop(),
    "analyze" => await AnalyzeAsync(services, cancellationToken),
    "backtest" => await BacktestAsync(services, args, cancellationToken),
    "backtest-learning" => await BacktestLearningAsync(services, args, cancellationToken),
    "download-history" => await DownloadHistoryAsync(services, args, cancellationToken),
    "ctrader-connect" => await CTraderConnectAsync(services, cancellationToken),
    "ctrader-authorize" => await CTraderAuthorizeAsync(services, cancellationToken),
    "ctrader-request-token" => await CTraderRequestTokenAsync(services, cancellationToken),
    "ctrader-refresh-token" => await CTraderRefreshTokenAsync(services, cancellationToken),
    "ctrader-accounts-list" => await CTraderAccountsListAsync(services, cancellationToken),
    "ctrader-account-details" => await CTraderAccountDetailsAsync(services, cancellationToken),
    "ctrader-symbols" => await CTraderSymbolsAsync(services, args, cancellationToken),
    "ctrader-createorder" => await CTraderCreateOrderAsync(services, args, cancellationToken),
    "analyze-and-createorder" => await AnalyzeAndCreateOrderAsync(services, args, cancellationToken),
    _ => Help()
};

static string ResolveAppsettingsPath()
{
    var projectAppsettings = Path.Combine(Directory.GetCurrentDirectory(), "TradingBot.CLI", "appsettings.json");
    return File.Exists(projectAppsettings)
        ? projectAppsettings
        : Path.Combine(AppContext.BaseDirectory, "appsettings.json");
}

static ServiceRegistry BuildServices(string appsettingsPath, TradingBotOptions options)
{
    var cTraderTester = new CTraderConnectionTester(options);
    var cTraderJsonApi = new CTraderJsonApiClient(options);
    var marketData = new CTraderMarketDataProvider(cTraderJsonApi, options);
    var newsFilter = new NewsFilter(options);
    var sessionClock = new NewYorkSessionClock();
    var strategy = new SmartMoneyStrategyEngine(marketData, newsFilter, sessionClock, options);
    var risk = new RiskManager(options);
    var historicalProvider = BuildHistoricalProvider(options, cTraderJsonApi);

    return new ServiceRegistry(
        appsettingsPath,
        options,
        new CTraderBrokerClient(options),
        cTraderTester,
        cTraderJsonApi,
        historicalProvider,
        strategy,
        risk,
        new BacktestingEngine(strategy, risk, historicalProvider, options),
        new CsvJournalWriter(options));
}

static IHistoricalMarketDataProvider BuildHistoricalProvider(
    TradingBotOptions options,
    CTraderJsonApiClient cTraderJsonApi)
{
    var providers = new IHistoricalMarketDataProvider[]
    {
        new CachedHistoricalMarketDataProvider(new CTraderHistoricalMarketDataProvider(cTraderJsonApi, options), options)
    };

    return new HistoricalMarketDataProviderFactory(providers).Resolve(options.Backtesting.DataSource);
}

static async Task<int> StartAsync(ServiceRegistry services, string[] args, CancellationToken cancellationToken)
{
    var runOnce = HasStartOnceArg(args);
    var connectResult = await CaptureConsoleOutputAsync(() => CTraderConnectAsync(services, cancellationToken));
    if (connectResult.ExitCode != 0)
    {
        ClearConsoleFully();
        WriteCapturedOutput(connectResult);
        Console.Error.WriteLine("Startup stopped because cTrader connection failed.");
        return connectResult.ExitCode;
    }

    var connectedOptions = JsonOptionsLoader.Load(services.AppsettingsPath);
    services = BuildServices(services.AppsettingsPath, connectedOptions);

    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(services.Options.AnalysisExecutionIntervalSeconds));
    var authorizationPromptShown = false;
    while (!cancellationToken.IsCancellationRequested)
    {
        try
        {
            ClearConsoleFully();
            var cycleResult = await CaptureConsoleOutputAsync(() => ExecuteAnalyzeAndCreateOrderAsync(
                    services,
                    requireLiveConfirmation: false,
                    formattedOutput: true,
                    showTokenRecoveryHint: !authorizationPromptShown,
                    cancellationToken));
            ClearConsoleFully();
            WriteCapturedOutput(cycleResult);

            var cycleExitCode = cycleResult.ExitCode;
            authorizationPromptShown = cycleExitCode == 3 || authorizationPromptShown && cycleExitCode == 3;
            if (runOnce)
            {
                Console.WriteLine();
                Console.WriteLine("One-time start execution completed.");
                return cycleExitCode;
            }

            PrintNextRun(services.Options.AnalysisExecutionIntervalSeconds);

            if (!await timer.WaitForNextTickAsync(cancellationToken))
            {
                break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            break;
        }
        catch (Exception exception)
        {
            ClearConsoleFully();
            PrintCycleHeader(new TradeSignal(
                services.Options.Symbol,
                TradeDirection.Buy,
                0m,
                0m,
                0m,
                0m,
                SessionName.Closed,
                false,
                "Analysis cycle failed."));
            PrintFailure("Analysis Cycle", exception.Message);

            if (runOnce)
            {
                Console.WriteLine();
                Console.WriteLine("One-time start execution failed.");
                return 5;
            }

            PrintNextRun(services.Options.AnalysisExecutionIntervalSeconds);

            if (!await timer.WaitForNextTickAsync(cancellationToken))
            {
                break;
            }
        }
    }

    ClearConsoleFully();
    Console.WriteLine("TradingBot start stopped.");
    return 0;
}

static int Stop()
{
    Log("shutdown", "Stop requested. No long-running daemon state is active in this CLI instance.");
    return 0;
}

static async Task<int> AnalyzeAsync(ServiceRegistry services, CancellationToken cancellationToken)
{
    var tokenResult = await RefreshCTraderTokenAsync(services, cancellationToken);
    if (!tokenResult.IsSuccess)
    {
        Console.Error.WriteLine(tokenResult.Error);
        return 3;
    }

    var signal = await services.Strategy.AnalyzeAsync(services.Options.Symbol, DateTimeOffset.UtcNow, cancellationToken);
    Console.WriteLine(JsonSerializer.Serialize(ToDto(signal), new JsonSerializerOptions { WriteIndented = true }));
    return signal.IsValidSetup ? 0 : 1;
}

static async Task<int> BacktestAsync(ServiceRegistry services, string[] args, CancellationToken cancellationToken)
{
    var from = ReadDate(args, "--from") ?? DateTimeOffset.UtcNow.AddYears(-1);
    var to = ReadDate(args, "--to") ?? DateTimeOffset.UtcNow;

    if (from > to)
    {
        Console.Error.WriteLine("--from must be before --to.");
        return 2;
    }

    if (string.Equals(services.Options.Backtesting.DataSource, "cTrader", StringComparison.OrdinalIgnoreCase))
    {
        var tokenResult = await services.CTraderTester.TestTokenEndpointAsync(cancellationToken);
        if (!tokenResult.IsSuccess)
        {
            Console.Error.WriteLine($"Backtest failed: {tokenResult.Error}");
            return 4;
        }

        if (!string.IsNullOrWhiteSpace(tokenResult.Value!.AccessToken) && !string.IsNullOrWhiteSpace(tokenResult.Value.RefreshToken))
        {
            await SaveCTraderTokensAsync(services.AppsettingsPath, tokenResult.Value.AccessToken, tokenResult.Value.RefreshToken, cancellationToken);
            services.Options.CTrader.AccessToken = tokenResult.Value.AccessToken;
            services.Options.CTrader.RefreshToken = tokenResult.Value.RefreshToken;
        }
    }

    BacktestResult result;
    try
    {
        result = await services.Backtesting.RunAsync(services.Options.Symbol, from, to, cancellationToken);
    }
    catch (InvalidOperationException exception)
    {
        Console.Error.WriteLine($"Backtest failed: {exception.Message}");
        return 4;
    }

    var path = await services.Journal.WriteTradesAsync(result.Trades, "backtest-trades", cancellationToken);

    Console.WriteLine(JsonSerializer.Serialize(result.Metrics, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"CSV report: {path}");
    return 0;
}

static async Task<int> BacktestLearningAsync(ServiceRegistry services, string[] args, CancellationToken cancellationToken)
{
    var date = ReadDate(args, "--date");
    if (date is null)
    {
        Console.Error.WriteLine("Provide --date. Example: backtest-learning --date 2026-05-19");
        return 2;
    }

    var tokenResult = await RefreshCTraderTokenAsync(services, cancellationToken);
    if (!tokenResult.IsSuccess)
    {
        Console.Error.WriteLine($"Backtest learning failed: {tokenResult.Error}");
        return 3;
    }

    var start = new DateTimeOffset(date.Value.Date, date.Value.Offset);
    var end = start.AddDays(1);
    TradeSignal? validSignal = null;
    RiskDecision? validRisk = null;
    DateTimeOffset validAt = start;

    for (var cursor = start; cursor < end; cursor = cursor.AddMinutes(30))
    {
        var signal = await services.Strategy.AnalyzeAsync(services.Options.Symbol, cursor, cancellationToken);
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
            services.Options.RiskPercentPerTrade);

        var account = new AccountSnapshot(services.Options.AccountBalance, services.Options.AccountBalance, 0m, 0m, 0, 0);
        var riskDecision = services.Risk.Evaluate(order, account);
        if (!riskDecision.IsAllowed)
        {
            continue;
        }

        validSignal = signal;
        validRisk = riskDecision;
        validAt = cursor;
        break;
    }

    if (validSignal is null || validRisk is null)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            isValidSetupFound = false,
            symbol = services.Options.Symbol,
            date = start.ToString("yyyy-MM-dd"),
            message = "No valid setup was found for the requested date."
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 1;
    }

    var candles = await services.HistoricalMarketData.GetCandlesAsync(
        new HistoricalDataRequest(services.Options.Symbol, Timeframe.M5, validAt.AddHours(-3), validAt.AddHours(1)),
        cancellationToken);

    var generator = new LearningReportGenerator(services.Options);
    var report = await generator.WriteAsync(validSignal, validRisk, candles, validAt, cancellationToken);

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        isValidSetupFound = true,
        symbol = validSignal.Symbol,
        analyzedAt = validAt,
        direction = validSignal.Direction.ToString().ToUpperInvariant(),
        entry = validSignal.EntryPrice,
        stopLoss = validSignal.StopLoss,
        takeProfit = validSignal.TakeProfit,
        riskReward = validSignal.RiskReward,
        lotSize = validRisk.PositionSize,
        image = Path.GetFullPath(report.ImagePath),
        pdf = Path.GetFullPath(report.PdfPath)
    }, new JsonSerializerOptions { WriteIndented = true }));

    return 0;
}

static async Task<int> DownloadHistoryAsync(ServiceRegistry services, string[] args, CancellationToken cancellationToken)
{
    var from = ReadDate(args, "--from") ?? DateTimeOffset.UtcNow.AddDays(-30);
    var to = ReadDate(args, "--to") ?? DateTimeOffset.UtcNow;
    var timeframeText = ReadKeyValueArg(args, "timeframe") ?? "M5";

    if (!Enum.TryParse<Timeframe>(timeframeText, ignoreCase: true, out var timeframe))
    {
        Console.Error.WriteLine("Invalid timeframe. Supported values: M1, M5, H1, D1.");
        return 2;
    }

    var candles = await services.HistoricalMarketData.GetCandlesAsync(
        new HistoricalDataRequest(services.Options.Symbol, timeframe, from, to),
        cancellationToken);

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        provider = services.HistoricalMarketData.ProviderName,
        symbol = services.Options.Symbol,
        timeframe,
        from,
        to,
        candles = candles.Count,
        first = candles.FirstOrDefault()?.OpenedAt,
        last = candles.LastOrDefault()?.OpenedAt
    }, new JsonSerializerOptions { WriteIndented = true }));

    return candles.Count > 0 ? 0 : 1;
}

static async Task<int> CTraderRequestTokenAsync(ServiceRegistry services, CancellationToken cancellationToken)
{
    var result = await RefreshCTraderTokenAsync(services, cancellationToken);
    if (!result.IsSuccess)
    {
        Console.Error.WriteLine(result.Error);
        return 3;
    }

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        isConnected = result.Value!.IsConnected,
        message = result.Value.Message,
        hasAccessToken = result.Value.HasAccessToken,
        hasRefreshToken = result.Value.HasRefreshToken,
        expiresInSeconds = result.Value.ExpiresInSeconds,
        ctidTraderAccountIdConfigured = services.Options.CTrader.CtidTraderAccountId > 0,
        accountMode = services.Options.CTrader.IsDemo ? "Demo" : "Live"
    }, new JsonSerializerOptions { WriteIndented = true }));

    return result.Value.IsConnected ? 0 : 4;
}

static async Task<int> CTraderRefreshTokenAsync(ServiceRegistry services, CancellationToken cancellationToken)
{
    var result = await RefreshCTraderTokenAsync(services, cancellationToken);
    if (!result.IsSuccess)
    {
        Console.Error.WriteLine(result.Error);
        return 3;
    }

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        isRefreshed = true,
        message = "cTrader token refreshed and saved to appsettings.json.",
        hasAccessToken = result.Value!.HasAccessToken,
        hasRefreshToken = result.Value.HasRefreshToken,
        expiresInSeconds = result.Value.ExpiresInSeconds,
        refreshedAt = DateTimeOffset.Now
    }, new JsonSerializerOptions { WriteIndented = true }));

    return 0;
}

static async Task<Result<CTraderTokenTestResult>> RefreshCTraderTokenAsync(ServiceRegistry services, CancellationToken cancellationToken)
{
    var result = await services.CTraderTester.TestTokenEndpointAsync(cancellationToken);
    if (!result.IsSuccess)
    {
        return result;
    }

    if (!string.IsNullOrWhiteSpace(result.Value!.AccessToken) && !string.IsNullOrWhiteSpace(result.Value.RefreshToken))
    {
        await SaveCTraderTokensAsync(services.AppsettingsPath, result.Value.AccessToken, result.Value.RefreshToken, cancellationToken);
        services.Options.CTrader.AccessToken = result.Value.AccessToken;
        services.Options.CTrader.RefreshToken = result.Value.RefreshToken;
    }

    return result;
}

static async Task<int> CTraderConnectAsync(ServiceRegistry services, CancellationToken cancellationToken)
{
    var authorizeExitCode = await CTraderAuthorizeAsync(services, cancellationToken);
    if (authorizeExitCode != 0)
    {
        return authorizeExitCode;
    }

    var refreshedOptions = JsonOptionsLoader.Load(services.AppsettingsPath);
    var refreshedServices = BuildServices(services.AppsettingsPath, refreshedOptions);
    return await CTraderRequestTokenAsync(refreshedServices, cancellationToken);
}

static async Task<int> CTraderAccountDetailsAsync(ServiceRegistry services, CancellationToken cancellationToken)
{
    var tokenResult = await services.CTraderTester.TestTokenEndpointAsync(cancellationToken);
    if (!tokenResult.IsSuccess)
    {
        Console.Error.WriteLine(tokenResult.Error);
        return 3;
    }

    if (!string.IsNullOrWhiteSpace(tokenResult.Value!.AccessToken) && !string.IsNullOrWhiteSpace(tokenResult.Value.RefreshToken))
    {
        await SaveCTraderTokensAsync(services.AppsettingsPath, tokenResult.Value.AccessToken, tokenResult.Value.RefreshToken, cancellationToken);
        services.Options.CTrader.AccessToken = tokenResult.Value.AccessToken;
        services.Options.CTrader.RefreshToken = tokenResult.Value.RefreshToken;
    }

    var accountResult = await services.CTraderJsonApi.TestAccountAsync(tokenResult.Value.AccessToken ?? services.Options.CTrader.AccessToken, cancellationToken);
    if (!accountResult.IsSuccess)
    {
        Console.Error.WriteLine(accountResult.Error);
        return 4;
    }

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        isConnected = accountResult.Value!.IsConnected,
        host = accountResult.Value.Host,
        port = accountResult.Value.Port,
        ctidTraderAccountId = accountResult.Value.CtidTraderAccountId,
        grantedAccountIds = accountResult.Value.GrantedAccountIds,
        accountMode = services.Options.CTrader.IsDemo ? "Demo" : "Live",
        trader = accountResult.Value.TraderPayload
    }, new JsonSerializerOptions { WriteIndented = true }));

    return 0;
}

static async Task<int> CTraderAccountsListAsync(ServiceRegistry services, CancellationToken cancellationToken)
{
    var tokenResult = await services.CTraderTester.TestTokenEndpointAsync(cancellationToken);
    if (!tokenResult.IsSuccess)
    {
        Console.Error.WriteLine(tokenResult.Error);
        return 3;
    }

    if (!string.IsNullOrWhiteSpace(tokenResult.Value!.AccessToken) && !string.IsNullOrWhiteSpace(tokenResult.Value.RefreshToken))
    {
        await SaveCTraderTokensAsync(services.AppsettingsPath, tokenResult.Value.AccessToken, tokenResult.Value.RefreshToken, cancellationToken);
    }

    var accountsResult = await services.CTraderJsonApi.GetGrantedAccountsAsync(tokenResult.Value.AccessToken ?? services.Options.CTrader.AccessToken, cancellationToken);
    if (!accountsResult.IsSuccess)
    {
        Console.Error.WriteLine(accountsResult.Error);
        return 4;
    }

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        host = accountsResult.Value!.Host,
        port = accountsResult.Value.Port,
        configuredCtidTraderAccountId = services.Options.CTrader.CtidTraderAccountId,
        configuredMode = services.Options.CTrader.IsDemo ? "Demo" : "Live",
        accounts = accountsResult.Value.Accounts.Select(account => new
        {
            account.CtidTraderAccountId,
            mode = account.IsLive ? "Live" : "Demo",
            account.TraderLogin,
            account.BrokerTitleShort,
            isConfiguredAccount = account.CtidTraderAccountId == services.Options.CTrader.CtidTraderAccountId
        })
    }, new JsonSerializerOptions { WriteIndented = true }));

    return 0;
}

static async Task<int> CTraderCreateOrderAsync(ServiceRegistry services, string[] args, CancellationToken cancellationToken)
{
    if (!TryReadDecimalArg(args, "entry_point", out var entryPoint) || !TryReadDecimalArg(args, "quantity", out var quantity))
    {
        Console.Error.WriteLine("Usage: TradingBot.CLI ctrader-createorder entry_point=[value] quantity=[value] [confirm_live_order=true]");
        return 2;
    }

    if (!services.Options.CTrader.IsDemo && !HasFlagArg(args, "confirm_live_order", "true"))
    {
        Console.Error.WriteLine("Live-environment order creation requires confirm_live_order=true.");
        return 2;
    }

    var tokenResult = await services.CTraderTester.TestTokenEndpointAsync(cancellationToken);
    if (!tokenResult.IsSuccess)
    {
        Console.Error.WriteLine(tokenResult.Error);
        return 3;
    }

    if (!string.IsNullOrWhiteSpace(tokenResult.Value!.AccessToken) && !string.IsNullOrWhiteSpace(tokenResult.Value.RefreshToken))
    {
        await SaveCTraderTokensAsync(services.AppsettingsPath, tokenResult.Value.AccessToken, tokenResult.Value.RefreshToken, cancellationToken);
        services.Options.CTrader.AccessToken = tokenResult.Value.AccessToken;
        services.Options.CTrader.RefreshToken = tokenResult.Value.RefreshToken;
    }

    var activeTradeValidation = await ValidateActiveTradeLimitAsync(
        services,
        tokenResult.Value.AccessToken ?? services.Options.CTrader.AccessToken,
        formattedOutput: false,
        cancellationToken);
    if (!activeTradeValidation.IsSuccess)
    {
        Console.Error.WriteLine(activeTradeValidation.Error);
        return 4;
    }

    var orderResult = await services.CTraderJsonApi.CreateLimitOrderAsync(
        entryPoint,
        quantity,
        tokenResult.Value.AccessToken ?? services.Options.CTrader.AccessToken,
        cancellationToken);

    if (!orderResult.IsSuccess)
    {
        Console.Error.WriteLine(orderResult.Error);
        return 4;
    }

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        isAccepted = orderResult.Value!.IsAccepted,
        orderType = services.Options.CTrader.DefaultOrderType,
        tradeSide = orderResult.Value.TradeSide,
        ctidTraderAccountId = orderResult.Value.CtidTraderAccountId,
        symbolId = orderResult.Value.SymbolId,
        quantityLots = quantity,
        volume = orderResult.Value.Volume,
        limitPrice = orderResult.Value.LimitPrice,
        stopLoss = orderResult.Value.StopLoss,
        takeProfit = orderResult.Value.TakeProfit,
        riskReward = orderResult.Value.RiskReward,
        clientOrderId = orderResult.Value.ClientOrderId,
        execution = orderResult.Value.ExecutionPayload
    }, new JsonSerializerOptions { WriteIndented = true }));

    return 0;
}

static async Task<int> CTraderSymbolsAsync(ServiceRegistry services, string[] args, CancellationToken cancellationToken)
{
    var symbolName = ReadKeyValueArg(args, "symbol") ?? services.Options.Symbol;
    var tokenResult = await services.CTraderTester.TestTokenEndpointAsync(cancellationToken);
    if (!tokenResult.IsSuccess)
    {
        Console.Error.WriteLine(tokenResult.Error);
        return 3;
    }

    if (!string.IsNullOrWhiteSpace(tokenResult.Value!.AccessToken) && !string.IsNullOrWhiteSpace(tokenResult.Value.RefreshToken))
    {
        await SaveCTraderTokensAsync(services.AppsettingsPath, tokenResult.Value.AccessToken, tokenResult.Value.RefreshToken, cancellationToken);
    }

    var symbolsResult = await services.CTraderJsonApi.GetSymbolsAsync(
        tokenResult.Value.AccessToken ?? services.Options.CTrader.AccessToken,
        symbolName,
        cancellationToken);

    if (!symbolsResult.IsSuccess)
    {
        Console.Error.WriteLine(symbolsResult.Error);
        return 4;
    }

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        host = symbolsResult.Value!.Host,
        port = symbolsResult.Value.Port,
        ctidTraderAccountId = symbolsResult.Value.CtidTraderAccountId,
        searchedSymbol = symbolName,
        configuredSymbolId = symbolsResult.Value.ConfiguredSymbolId,
        configuredSymbolIdFound = symbolsResult.Value.Symbols.Any(symbol => symbol.SymbolId == symbolsResult.Value.ConfiguredSymbolId),
        symbols = symbolsResult.Value.Symbols.Select(symbol => new
        {
            symbol.SymbolId,
            symbol.SymbolName,
            symbol.Enabled,
            symbol.BaseAssetId,
            symbol.QuoteAssetId,
            isConfiguredSymbol = symbol.SymbolId == symbolsResult.Value.ConfiguredSymbolId
        })
    }, new JsonSerializerOptions { WriteIndented = true }));

    return 0;
}

static async Task<int> AnalyzeAndCreateOrderAsync(ServiceRegistry services, string[] args, CancellationToken cancellationToken)
{
    return await ExecuteAnalyzeAndCreateOrderAsync(
        services,
        requireLiveConfirmation: HasFlagArg(args, "confirm_live_order", "true"),
        formattedOutput: false,
        showTokenRecoveryHint: true,
        cancellationToken);
}

static async Task<Result> ValidateActiveTradeLimitAsync(
    ServiceRegistry services,
    string accessToken,
    bool formattedOutput,
    CancellationToken cancellationToken)
{
    var summaryResult = await services.CTraderJsonApi.GetActiveTradeSummaryAsync(accessToken, cancellationToken);
    if (!summaryResult.IsSuccess)
    {
        if (formattedOutput)
        {
            PrintFailure("Active Trade Check", summaryResult.Error!);
        }

        return Result.Failure(summaryResult.Error!);
    }

    var summary = summaryResult.Value!;
    if (formattedOutput)
    {
        PrintActiveTradeSummary(summary, services.Options.MaxActiveTrades);
    }

    if (summary.TotalActiveTrades >= services.Options.MaxActiveTrades)
    {
        return Result.Failure($"Active trade limit reached. Active={summary.TotalActiveTrades}, MaxAllowed={services.Options.MaxActiveTrades}.");
    }

    return Result.Success();
}

static async Task<int> ExecuteAnalyzeAndCreateOrderAsync(
    ServiceRegistry services,
    bool requireLiveConfirmation,
    bool formattedOutput,
    bool showTokenRecoveryHint,
    CancellationToken cancellationToken)
{
    if (!services.Options.CTrader.IsDemo && !services.Options.CTrader.AllowLiveOrderCreation && !requireLiveConfirmation)
    {
        Console.Error.WriteLine("Live-environment order creation requires CTrader:AllowLiveOrderCreation=true or confirm_live_order=true.");
        return 2;
    }

    var tokenResult = await RefreshCTraderTokenAsync(services, cancellationToken);
    if (!tokenResult.IsSuccess)
    {
        if (formattedOutput)
        {
            PrintCycleHeader(new TradeSignal(
                services.Options.Symbol,
                TradeDirection.Buy,
                0m,
                0m,
                0m,
                0m,
                SessionName.Closed,
                false,
                "cTrader token refresh failed."));
            PrintFailure("Broker Token", tokenResult.Error!);
            if (showTokenRecoveryHint)
            {
                PrintTokenRecoveryHint(tokenResult.Error!);
            }
            else
            {
                PrintAuthorizationAlreadyRequested();
            }
        }
        else
        {
            Console.Error.WriteLine(tokenResult.Error);
        }

        return 3;
    }

    var signal = await services.Strategy.AnalyzeAsync(services.Options.Symbol, DateTimeOffset.UtcNow, cancellationToken);
    if (formattedOutput)
    {
        PrintCycleHeader(signal);
    }

    if (!signal.IsValidSetup)
    {
        if (formattedOutput)
        {
            PrintInvalidSetup(signal);
        }
        else
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                isOrderCreated = false,
                reason = "Strategy setup is not valid.",
                signal = ToDto(signal)
            }, new JsonSerializerOptions { WriteIndented = true }));
        }

        return 1;
    }

    if (formattedOutput)
    {
        PrintValidSignal(signal);
    }

    var order = new OrderRequest(
        signal.Symbol,
        signal.Direction,
        OrderType.Limit,
        signal.EntryPrice,
        signal.StopLoss,
        signal.TakeProfit,
        0m,
        services.Options.RiskPercentPerTrade);

    var account = new AccountSnapshot(services.Options.AccountBalance, services.Options.AccountBalance, 0m, 0m, 0, 0);
    var riskDecision = services.Risk.Evaluate(order, account);
    if (!riskDecision.IsAllowed)
    {
        if (formattedOutput)
        {
            PrintRiskRejected(riskDecision);
        }
        else
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                isOrderCreated = false,
                reason = riskDecision.Reason,
                signal = ToDto(signal)
            }, new JsonSerializerOptions { WriteIndented = true }));
        }

        return 1;
    }

    if (formattedOutput)
    {
        PrintRiskAccepted(riskDecision, services.Options.RiskPercentPerTrade);
    }

    var activeTradeValidation = await ValidateActiveTradeLimitAsync(
        services,
        tokenResult.Value!.AccessToken ?? services.Options.CTrader.AccessToken,
        formattedOutput,
        cancellationToken);
    if (!activeTradeValidation.IsSuccess)
    {
        if (!formattedOutput)
        {
            Console.Error.WriteLine(activeTradeValidation.Error);
        }

        return 4;
    }

    var orderResult = await services.CTraderJsonApi.CreateLimitOrderAsync(
        signal.EntryPrice,
        riskDecision.PositionSize,
        signal.Direction,
        signal.StopLoss,
        signal.TakeProfit,
        tokenResult.Value.AccessToken ?? services.Options.CTrader.AccessToken,
        cancellationToken);

    if (!orderResult.IsSuccess)
    {
        if (formattedOutput)
        {
            PrintFailure("Order Creation", orderResult.Error!);
        }
        else
        {
            Console.Error.WriteLine(orderResult.Error);
        }

        return 4;
    }

    if (formattedOutput)
    {
        PrintOrderCreated(orderResult.Value!, riskDecision);
    }
    else
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            isOrderCreated = true,
            signal = ToDto(signal),
            risk = new
            {
                riskPercent = services.Options.RiskPercentPerTrade,
                lotSize = riskDecision.PositionSize,
                reason = riskDecision.Reason
            },
            order = new
            {
                orderResult.Value!.IsAccepted,
                orderResult.Value.ClientOrderId,
                orderResult.Value.CtidTraderAccountId,
                orderResult.Value.SymbolId,
                orderResult.Value.TradeSide,
                orderResult.Value.Volume,
                orderResult.Value.LimitPrice,
                orderResult.Value.StopLoss,
                orderResult.Value.TakeProfit,
                orderResult.Value.RiskReward,
                execution = orderResult.Value.ExecutionPayload
            }
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    return 0;
}

static async Task<int> CTraderAuthorizeAsync(ServiceRegistry services, CancellationToken cancellationToken)
{
    if (!Uri.TryCreate(services.Options.CTrader.RedirectUri, UriKind.Absolute, out var redirectUri))
    {
        Console.Error.WriteLine("CTrader:RedirectUri must be an absolute URI.");
        return 2;
    }

    if (!redirectUri.IsLoopback)
    {
        Console.Error.WriteLine("ctrader-authorize only supports localhost redirect URIs.");
        return 2;
    }

    using var listener = new System.Net.HttpListener();
    var prefix = $"{redirectUri.Scheme}://{redirectUri.Authority}{redirectUri.AbsolutePath.TrimEnd('/')}/";
    listener.Prefixes.Add(prefix);
    listener.Start();

    var authUrl = services.CTraderTester.BuildAuthorizationUrl();
    Console.WriteLine("Open this URL, click Allow Access, and keep this command running:");
    Console.WriteLine(authUrl);

    TryOpenBrowser(authUrl);

    var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(2), cancellationToken);
    var code = context.Request.QueryString["code"];
    var error = context.Request.QueryString["error"];

    await WriteBrowserResponseAsync(context, string.IsNullOrWhiteSpace(code) ? "Authorization failed. You can close this tab." : "Authorization received. You can close this tab.", cancellationToken);

    if (!string.IsNullOrWhiteSpace(error))
    {
        Console.Error.WriteLine($"cTrader authorization failed: {error}");
        return 3;
    }

    if (string.IsNullOrWhiteSpace(code))
    {
        Console.Error.WriteLine("cTrader redirect did not include a code.");
        return 3;
    }

    var exchange = await services.CTraderTester.ExchangeAuthorizationCodeAsync(code, cancellationToken);
    if (!exchange.IsSuccess)
    {
        Console.Error.WriteLine(exchange.Error);
        return 4;
    }

    await SaveCTraderTokensAsync(services.AppsettingsPath, exchange.Value!.AccessToken, exchange.Value.RefreshToken, cancellationToken);

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        isConnected = true,
        message = "cTrader authorization succeeded. AccessToken and RefreshToken were saved to appsettings.json.",
        hasAccessToken = true,
        hasRefreshToken = true,
        expiresInSeconds = exchange.Value.ExpiresInSeconds
    }, new JsonSerializerOptions { WriteIndented = true }));

    return 0;
}

static DateTimeOffset? ReadDate(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    if (index < 0 || index + 1 >= args.Length)
    {
        return null;
    }

    return DateTimeOffset.TryParse(args[index + 1], out var value) ? value : null;
}

static bool TryReadDecimalArg(string[] args, string name, out decimal value)
{
    value = 0m;
    var prefix = $"{name}=";
    var raw = args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    return raw is not null
        && decimal.TryParse(raw[prefix.Length..], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out value);
}

static bool HasFlagArg(string[] args, string name, string expectedValue)
{
    var prefix = $"{name}=";
    var raw = args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    return raw is not null && string.Equals(raw[prefix.Length..], expectedValue, StringComparison.OrdinalIgnoreCase);
}

static bool HasStartOnceArg(string[] args) =>
    args.Length == 2 && string.Equals(args[1], "once", StringComparison.OrdinalIgnoreCase);

static string? ReadKeyValueArg(string[] args, string name)
{
    var prefix = $"{name}=";
    var raw = args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    return raw is null ? null : raw[prefix.Length..];
}

static int Help()
{
    Console.WriteLine("Usage:");
    PrintCommand("start [once]", "Runs cTrader connect, then runs analyze-and-createorder on the configured interval. Use 'start once' for one cycle.");
    PrintCommand("stop", "Logs a safe shutdown request. No daemon state is active yet.");
    PrintCommand("analyze", "Runs one Smart Money strategy analysis cycle and prints the generated signal.");
    PrintCommand("backtest --from 2025-05-15 --to 2026-05-15", "Runs a backtest from cTrader historical candles and writes a CSV report.");
    PrintCommand("backtest-learning --date 2026-05-19", "Scans one date for a valid setup and writes a learning chart plus PDF report.");
    PrintCommand("download-history --from 2025-05-15 --to 2026-05-15 timeframe=M5", "Downloads/caches historical candles from the configured backtesting provider.");
    PrintCommand("ctrader-connect", "Runs cTrader authorization and then verifies OAuth token connectivity.");
    PrintCommand("ctrader-authorize", "Starts the local OAuth callback, opens cTrader authorization, and saves tokens.");
    PrintCommand("ctrader-request-token", "Refreshes cTrader OAuth tokens and verifies token endpoint connectivity.");
    PrintCommand("ctrader-refresh-token", "Refreshes and saves the cTrader token pair for long-running execution.");
    PrintCommand("ctrader-accounts-list", "Lists cTrader accounts granted to the current token.");
    PrintCommand("ctrader-account-details", "Authenticates the configured cTrader account and fetches account details.");
    PrintCommand("ctrader-symbols [symbol=EURUSD]", "Lists matching cTrader symbols and verifies the configured SymbolId.");
    PrintCommand("ctrader-createorder entry_point=[value] quantity=[value] [confirm_live_order=true]", "Creates a limit order with strategy-based stop loss and take profit.");
    PrintCommand("analyze-and-createorder [confirm_live_order=true]", "Analyzes the configured strategy and creates an order only when a valid setup exists.");
    return 0;
}

static void PrintCommand(string command, string description)
{
    Console.WriteLine($"  TradingBot.CLI {command}");
    Console.WriteLine($"      {description}");
}

static void PrintCycleHeader(TradeSignal signal)
{
    Console.WriteLine();
    Console.WriteLine("============================================================");
    Console.WriteLine($" TradingBot Analysis Cycle - {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
    Console.WriteLine("============================================================");
    Console.WriteLine($"Symbol : {signal.Symbol}");
    Console.WriteLine($"Session: {signal.Session}");
}

static void PrintInvalidSetup(TradeSignal signal)
{
    Console.WriteLine();
    Console.WriteLine("Setup Status : INVALID");
    Console.WriteLine($"Reason       : {signal.SetupReason}");
    Console.WriteLine("Action       : No order created");
}

static void PrintValidSignal(TradeSignal signal)
{
    Console.WriteLine();
    Console.WriteLine("Setup Status : VALID");
    Console.WriteLine($"Reason       : {signal.SetupReason}");
    Console.WriteLine($"Direction    : {signal.Direction.ToString().ToUpperInvariant()}");
    Console.WriteLine($"Entry        : {signal.EntryPrice}");
    Console.WriteLine($"Stop Loss    : {signal.StopLoss}");
    Console.WriteLine($"Take Profit  : {signal.TakeProfit}");
    Console.WriteLine($"RR           : {signal.RiskReward}");
}

static void PrintRiskAccepted(RiskDecision riskDecision, decimal riskPercent)
{
    Console.WriteLine();
    Console.WriteLine("Risk Status  : ACCEPTED");
    Console.WriteLine($"Risk Percent : {riskPercent}%");
    Console.WriteLine($"Lot Size     : {riskDecision.PositionSize}");
    Console.WriteLine($"Reason       : {riskDecision.Reason}");
}

static void PrintRiskRejected(RiskDecision riskDecision)
{
    Console.WriteLine();
    Console.WriteLine("Risk Status  : REJECTED");
    Console.WriteLine($"Reason       : {riskDecision.Reason}");
    Console.WriteLine("Action       : No order created");
}

static void PrintActiveTradeSummary(CTraderActiveTradeSummary summary, int maxActiveTrades)
{
    Console.WriteLine();
    Console.WriteLine("Active Trades: CHECKED");
    Console.WriteLine($"Open Pos.    : {summary.OpenPositions}");
    Console.WriteLine($"Pending Ord. : {summary.PendingOrders}");
    Console.WriteLine($"Total Active : {summary.TotalActiveTrades}");
    Console.WriteLine($"Max Allowed  : {maxActiveTrades}");
}

static void PrintOrderCreated(CTraderCreateOrderResult orderResult, RiskDecision riskDecision)
{
    Console.WriteLine();
    Console.WriteLine("Order Status : CREATED");
    Console.WriteLine($"Client ID    : {orderResult.ClientOrderId}");
    Console.WriteLine($"Account ID   : {orderResult.CtidTraderAccountId}");
    Console.WriteLine($"Symbol ID    : {orderResult.SymbolId}");
    Console.WriteLine($"Side         : {orderResult.TradeSide}");
    Console.WriteLine($"Lots         : {riskDecision.PositionSize}");
    Console.WriteLine($"Volume       : {orderResult.Volume}");
    Console.WriteLine($"Limit Price  : {orderResult.LimitPrice}");
    Console.WriteLine($"Stop Loss    : {orderResult.StopLoss}");
    Console.WriteLine($"Take Profit  : {orderResult.TakeProfit}");
    Console.WriteLine($"RR           : {orderResult.RiskReward}");
}

static void ClearConsoleFully()
{
    if (Console.IsOutputRedirected)
    {
        return;
    }

    try
    {
        if (OperatingSystem.IsWindows())
        {
            if (ConsoleCleaner.ClearLikeCls())
            {
                return;
            }
        }

        Console.Clear();
    }
    catch
    {
        try
        {
            Console.Clear();
        }
        catch
        {
        }
    }
}

static async Task<CapturedConsoleOutput> CaptureConsoleOutputAsync(Func<Task<int>> action)
{
    var originalOut = Console.Out;
    var originalError = Console.Error;
    await using var output = new StringWriter();
    await using var error = new StringWriter();

    try
    {
        Console.SetOut(output);
        Console.SetError(error);
        var exitCode = await action();
        return new CapturedConsoleOutput(exitCode, output.ToString(), error.ToString());
    }
    finally
    {
        Console.SetOut(originalOut);
        Console.SetError(originalError);
    }
}

static void WriteCapturedOutput(CapturedConsoleOutput captured)
{
    if (!string.IsNullOrWhiteSpace(captured.Output))
    {
        Console.Write(captured.Output);
    }

    if (!string.IsNullOrWhiteSpace(captured.Error))
    {
        Console.Error.Write(captured.Error);
    }
}

static void PrintFailure(string area, string error)
{
    Console.WriteLine();
    Console.WriteLine($"{area} Status: FAILED");
    Console.WriteLine($"Reason       : {error}");
}

static void PrintTokenRecoveryHint(string error)
{
    if (!error.Contains("Access denied", StringComparison.OrdinalIgnoreCase)
        && !error.Contains("access token", StringComparison.OrdinalIgnoreCase))
    {
        return;
    }

    Console.WriteLine("Action       : Run 'dotnet run --project TradingBot.CLI -- ctrader-connect' to re-authorize and save a fresh token pair.");
}

static void PrintAuthorizationAlreadyRequested()
{
    Console.WriteLine();
    Console.WriteLine("Broker Token Status: AUTHORIZATION REQUIRED");
    Console.WriteLine("Reason       : cTrader token refresh is still failing.");
    Console.WriteLine("Action       : Authorization was already requested for this start session. Run 'dotnet run --project TradingBot.CLI -- ctrader-connect' once, then restart 'start'.");
}

static void PrintNextRun(int intervalSeconds)
{
    var nextRun = DateTimeOffset.Now.AddSeconds(intervalSeconds);
    Console.WriteLine();
    Console.WriteLine($"Next Run     : {nextRun:yyyy-MM-dd HH:mm:ss zzz}");
    Console.WriteLine("Press Ctrl+C to stop.");
}

static object ToDto(TradeSignal signal) => new
{
    symbol = signal.Symbol,
    direction = signal.Direction.ToString().ToUpperInvariant(),
    entryPrice = signal.EntryPrice,
    stopLoss = signal.StopLoss,
    takeProfit = signal.TakeProfit,
    riskReward = signal.RiskReward,
    session = signal.Session.ToString(),
    isValidSetup = signal.IsValidSetup,
    setupReason = signal.SetupReason
};

static void Log(string area, string message) =>
    Console.WriteLine($"{{\"timestamp\":\"{DateTimeOffset.UtcNow:O}\",\"area\":\"{area}\",\"message\":\"{message}\"}}");

static void TryOpenBrowser(string url)
{
    try
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }
    catch
    {
        // The printed URL is the fallback in headless shells.
    }
}

static async Task WriteBrowserResponseAsync(System.Net.HttpListenerContext context, string message, CancellationToken cancellationToken)
{
    var html = System.Text.Encoding.UTF8.GetBytes($"<html><body><h1>{System.Net.WebUtility.HtmlEncode(message)}</h1></body></html>");
    context.Response.ContentType = "text/html; charset=utf-8";
    context.Response.ContentLength64 = html.Length;
    await context.Response.OutputStream.WriteAsync(html, cancellationToken);
    context.Response.Close();
}

static async Task SaveCTraderTokensAsync(string appsettingsPath, string accessToken, string refreshToken, CancellationToken cancellationToken)
{
    var json = await File.ReadAllTextAsync(appsettingsPath, cancellationToken);
    var root = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidOperationException("Invalid appsettings.json.");
    var cTrader = root["CTrader"]?.AsObject() ?? throw new InvalidOperationException("Missing CTrader section.");

    cTrader["AuthorizationCode"] = "";
    cTrader["AccessToken"] = accessToken;
    cTrader["RefreshToken"] = refreshToken;

    await File.WriteAllTextAsync(appsettingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
}

internal sealed record ServiceRegistry(
    string AppsettingsPath,
    TradingBotOptions Options,
    CTraderBrokerClient Broker,
    CTraderConnectionTester CTraderTester,
    CTraderJsonApiClient CTraderJsonApi,
    IHistoricalMarketDataProvider HistoricalMarketData,
    SmartMoneyStrategyEngine Strategy,
    RiskManager Risk,
    BacktestingEngine Backtesting,
    CsvJournalWriter Journal);
