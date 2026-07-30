using System.Text.Json;
using System.Text.Json.Nodes;
using TradingBot.Application;
using TradingBot.Backtesting;
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
    "find-recent-setups" => await FindRecentSetupsAsync(services, args, cancellationToken),
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
    "mt5-test-connection" => await MT5TestConnectionAsync(services, cancellationToken),
    "mt5-account-details" => await MT5AccountDetailsAsync(services, cancellationToken),
    "mt5-symbols" => await MT5SymbolsAsync(services, args, cancellationToken),
    "mt5-createorder" => await MT5CreateOrderAsync(services, args, cancellationToken),
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
    var mt5Bridge = new MT5BridgeClient(options);
    var isMt5 = IsBroker(options, "MT5");
    IMarketDataProvider marketData = isMt5
        ? new MT5MarketDataProvider(mt5Bridge)
        : new CTraderMarketDataProvider(cTraderJsonApi, options);
    var newsFilter = new NewsFilter(options);
    var sessionClock = new NewYorkSessionClock(options);
    var strategy = BuildStrategyEngine(options, marketData, newsFilter, sessionClock);
    var risk = new RiskManager(options);
    var historicalProvider = BuildHistoricalProvider(options, cTraderJsonApi, mt5Bridge);
    IHistoricalTickDataProvider? tickProvider = string.Equals(options.ActiveBacktestingDataSource.DataSource, "MT5", StringComparison.OrdinalIgnoreCase)
        ? new MT5HistoricalTickDataProvider(mt5Bridge)
        : null;

    return new ServiceRegistry(
        appsettingsPath,
        options,
        isMt5 ? new MT5BrokerClient(mt5Bridge, options) : new CTraderBrokerClient(options),
        cTraderTester,
        cTraderJsonApi,
        mt5Bridge,
        historicalProvider,
        strategy,
        risk,
        new BacktestingEngine(
            marketDataProvider => BuildStrategyEngine(options, marketDataProvider, newsFilter, sessionClock),
            risk,
            historicalProvider,
            tickProvider,
            options),
        new CsvJournalWriter(options),
        new RecentValidSetupCsvWriter(options),
        new OperationalLogWriter(options),
        new ClosedTradeTrackingWriter(options));
}

static IStrategyEngine BuildStrategyEngine(
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
        _ => throw new InvalidOperationException($"Unsupported strategy engine '{options.ActiveStrategy.Engine}'.")
    };
}

static IHistoricalMarketDataProvider BuildHistoricalProvider(
    TradingBotOptions options,
    CTraderJsonApiClient cTraderJsonApi,
    MT5BridgeClient mt5Bridge)
{
    var providers = new IHistoricalMarketDataProvider[]
    {
        new CachedHistoricalMarketDataProvider(new CTraderHistoricalMarketDataProvider(cTraderJsonApi, options), options),
        new CachedHistoricalMarketDataProvider(new MT5HistoricalMarketDataProvider(mt5Bridge, options), options)
    };

    return new HistoricalMarketDataProviderFactory(providers).Resolve(options.ActiveBacktestingDataSource.DataSource);
}

static async Task<int> StartAsync(ServiceRegistry services, string[] args, CancellationToken cancellationToken)
{
    var runOnce = HasStartOnceArg(args);
    var trackingMode = IsTrackingMode(args);
    var connectResult = await CaptureConsoleOutputAsync(() => ConnectConfiguredBrokerAsync(services, cancellationToken));
    if (connectResult.ExitCode != 0)
    {
        ClearConsoleFully();
        WriteCapturedOutput(connectResult);
        Console.Error.WriteLine("Startup stopped because broker connection failed.");
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
            var cycleResult = await CaptureConsoleOutputAsync(() => ExecuteAnalyzeAndCreateOrderAsync(
                    services,
                    requireLiveConfirmation: false,
                    formattedOutput: true,
                    showTokenRecoveryHint: !authorizationPromptShown,
                    cancellationToken));

            CapturedConsoleOutput? trackingOutput = null;
            if (trackingMode)
            {
                var trackingMarketSession = BuildForexMarketSessionStatus(services.Options, DateTimeOffset.UtcNow);
                trackingOutput = await CaptureConsoleOutputAsync(async () =>
                {
                    if (!trackingMarketSession.IsOpen)
                    {
                        PrintTradeTrackingSkipped(trackingMarketSession);
                        return 0;
                    }

                    var trackingResult = await TrackClosedTradesAsync(services, cancellationToken);
                    if (trackingResult.IsSuccess)
                    {
                        PrintTradeTracking(trackingResult.Value!.FetchedTrades, trackingResult.Value.WrittenTrades, trackingResult.Value.Directory);
                        return 0;
                    }

                    PrintFailure("Trade Tracking", trackingResult.Error!);
                    return 4;
                });
            }

            ClearConsoleFully();
            WriteCapturedOutput(cycleResult);
            if (trackingOutput is not null)
            {
                WriteCapturedOutput(trackingOutput);
            }

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
            PrintCycleHeader(services.Options, new TradeSignal(
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

static async Task<int> ConnectConfiguredBrokerAsync(ServiceRegistry services, CancellationToken cancellationToken)
{
    if (IsBroker(services.Options, "MT5"))
    {
        var result = await services.Broker.ValidateConnectionAsync(cancellationToken);
        if (!result.IsSuccess)
        {
            Console.Error.WriteLine(result.Error);
            return 3;
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            isConnected = true,
            broker = "MT5",
            bridgeBaseUrl = services.Options.MT5.BridgeBaseUrl
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    return await CTraderConnectAsync(services, cancellationToken);
}

static int Stop()
{
    Log("shutdown", "Stop requested. No long-running daemon state is active in this CLI instance.");
    return 0;
}

static async Task<int> AnalyzeAsync(ServiceRegistry services, CancellationToken cancellationToken)
{
    var brokerResult = await PrepareConfiguredBrokerForMarketDataAsync(services, cancellationToken);
    if (!brokerResult.IsSuccess)
    {
        Console.Error.WriteLine(brokerResult.Error);
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

    var sourceValidation = await ValidateHistoricalMarketDataSourceAsync(services, "Backtest", cancellationToken);
    if (!sourceValidation.IsSuccess)
    {
        Console.Error.WriteLine(sourceValidation.Error);
        return 4;
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

static async Task<int> FindRecentSetupsAsync(ServiceRegistry services, string[] args, CancellationToken cancellationToken)
{
    if (!TryReadIntArg(args, "count", out var requestedCount))
    {
        Console.Error.WriteLine("Provide count=[number]. Example: find-recent-setups count=10");
        return 2;
    }

    if (requestedCount <= 0 || requestedCount > RecentValidSetupCsvWriter.MaxRows)
    {
        Console.Error.WriteLine($"count must be between 1 and {RecentValidSetupCsvWriter.MaxRows}.");
        return 2;
    }

    var maxDays = TryReadIntArg(args, "max_days", out var configuredMaxDays) ? configuredMaxDays : 30;
    if (maxDays < 0)
    {
        Console.Error.WriteLine("max_days must be zero or greater. Use max_days=0 for an unbounded scan.");
        return 2;
    }

    var sourceValidation = await ValidateHistoricalMarketDataSourceAsync(services, "Find recent setups", cancellationToken);
    if (!sourceValidation.IsSuccess)
    {
        Console.Error.WriteLine(sourceValidation.Error);
        return 4;
    }

    var historicalProvider = services.HistoricalMarketData;
    var rows = new List<RecentValidSetupRow>();
    var seenSetups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var now = DateTimeOffset.Now;
    var firstSearchDay = PreviousTradingDayStart(now);
    var currentDay = firstSearchDay;
    var oldestScannedDay = firstSearchDay;
    var scannedDays = 0;

    while (rows.Count < requestedCount && rows.Count < RecentValidSetupCsvWriter.MaxRows && (maxDays == 0 || scannedDays < maxDays))
    {
        var dayStart = currentDay;
        var dayEnd = currentDay.AddDays(1).AddMinutes(-5);
        scannedDays++;

        IReadOnlyDictionary<Timeframe, IReadOnlyList<Candle>> candles;
        try
        {
            candles = await PreloadRecentSetupCandlesAsync(
                historicalProvider,
                services.Options.Symbol,
                dayStart,
                dayEnd,
                services.Options.UseDailyBiasFilter,
                requireCandles: currentDay == firstSearchDay,
                cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine($"Find recent setups failed: {exception.Message}");
            return 4;
        }

        if (candles.Count == 0 || !candles.TryGetValue(Timeframe.M5, out var m5Candles) || m5Candles.Count == 0)
        {
            break;
        }

        var marketData = new InMemoryMarketDataProvider(candles);
        var strategy = BuildStrategyEngine(services.Options, marketData, new NewsFilter(services.Options), new NewYorkSessionClock(services.Options));

        var cursorStep = ResolveAnalysisCursorStep(services.Options);
        for (var cursor = dayEnd; cursor >= dayStart && rows.Count < requestedCount && rows.Count < RecentValidSetupCsvWriter.MaxRows; cursor = cursor.Subtract(cursorStep))
        {
            var signal = await strategy.AnalyzeAsync(services.Options.Symbol, cursor, cancellationToken);
            if (!signal.IsValidSetup)
            {
                continue;
            }

            var setupKey = BuildSetupKey(signal);
            if (!seenSetups.Add(setupKey))
            {
                continue;
            }

            rows.Add(new RecentValidSetupRow(
                services.Options.ActiveStrategy.Id,
                services.Options.ActiveStrategy.Name,
                signal.Symbol,
                cursor.ToUniversalTime(),
                cursor.ToLocalTime(),
                signal.Session.ToString(),
                signal.Direction.ToString().ToUpperInvariant(),
                signal.Session != SessionName.Closed,
                signal.EntryPrice,
                signal.StopLoss,
                signal.TakeProfit,
                signal.RiskReward,
                signal.SetupReason,
                historicalProvider.ProviderName));
        }

        oldestScannedDay = currentDay;
        currentDay = PreviousTradingDayStart(currentDay);
    }

    var csvPath = await services.RecentSetups.WriteAsync(rows, cancellationToken);
    PrintRecentSetupSearchSummary(services.Options, historicalProvider.ProviderName, oldestScannedDay, firstSearchDay.AddDays(1).AddMinutes(-5), requestedCount, rows.Count, scannedDays, maxDays, csvPath);
    return rows.Count > 0 ? 0 : 1;
}

static TimeSpan ResolveAnalysisCursorStep(TradingBotOptions options) =>
    string.Equals(options.ActiveStrategy.ExecutionTimeframe, "M1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(options.ActiveStrategy.EntryTimeframe, "M1", StringComparison.OrdinalIgnoreCase)
        ? TimeSpan.FromMinutes(1)
        : TimeSpan.FromMinutes(5);

static string BuildSetupKey(TradeSignal signal)
{
    if (!string.IsNullOrWhiteSpace(signal.SetupId))
    {
        return signal.SetupId;
    }

    return string.Join('|', signal.Symbol, signal.Direction, signal.EntryPrice, signal.StopLoss, signal.TakeProfit, signal.SetupReason);
}

static async Task<Result> ValidateHistoricalMarketDataSourceAsync(
    ServiceRegistry services,
    string commandName,
    CancellationToken cancellationToken)
{
    var source = services.Options.ActiveBacktestingDataSource.DataSource;
    if (string.Equals(source, "cTrader", StringComparison.OrdinalIgnoreCase))
    {
        var tokenResult = await services.CTraderTester.TestTokenEndpointAsync(cancellationToken);
        if (!tokenResult.IsSuccess)
        {
            return Result.Failure($"{commandName} failed: {tokenResult.Error}");
        }

        if (!string.IsNullOrWhiteSpace(tokenResult.Value!.AccessToken) && !string.IsNullOrWhiteSpace(tokenResult.Value.RefreshToken))
        {
            await SaveCTraderTokensAsync(services.AppsettingsPath, tokenResult.Value.AccessToken, tokenResult.Value.RefreshToken, cancellationToken);
            services.Options.CTrader.AccessToken = tokenResult.Value.AccessToken;
            services.Options.CTrader.RefreshToken = tokenResult.Value.RefreshToken;
        }

        return Result.Success();
    }

    if (string.Equals(source, "MT5", StringComparison.OrdinalIgnoreCase))
    {
        var health = await services.MT5Bridge.GetHealthAsync(cancellationToken);
        return health.IsSuccess
            ? Result.Success()
            : Result.Failure($"{commandName} failed: {health.Error}");
    }

    return Result.Failure($"{commandName} failed: unsupported enabled market-data source '{source}'.");
}

static async Task<IReadOnlyDictionary<Timeframe, IReadOnlyList<Candle>>> PreloadRecentSetupCandlesAsync(
    IHistoricalMarketDataProvider provider,
    string symbol,
    DateTimeOffset dayStart,
    DateTimeOffset to,
    bool includeDaily,
    bool requireCandles,
    CancellationToken cancellationToken)
{
    var requests = includeDaily
        ? new[]
        {
            new HistoricalDataRequest(symbol, Timeframe.D1, dayStart.AddDays(-60), to),
            new HistoricalDataRequest(symbol, Timeframe.H1, dayStart.AddDays(-10), to),
            new HistoricalDataRequest(symbol, Timeframe.M1, dayStart.AddHours(-6), to),
            new HistoricalDataRequest(symbol, Timeframe.M5, dayStart.AddHours(-12), to)
        }
        : new[]
        {
            new HistoricalDataRequest(symbol, Timeframe.H1, dayStart.AddDays(-10), to),
            new HistoricalDataRequest(symbol, Timeframe.M1, dayStart.AddHours(-6), to),
            new HistoricalDataRequest(symbol, Timeframe.M5, dayStart.AddHours(-12), to)
        };
    var result = new Dictionary<Timeframe, IReadOnlyList<Candle>>();

    foreach (var request in requests)
    {
        var candles = await provider.GetCandlesAsync(request, cancellationToken);
        if (candles.Count == 0)
        {
            if (!requireCandles)
            {
                return new Dictionary<Timeframe, IReadOnlyList<Candle>>();
            }

            throw new InvalidOperationException($"Historical provider '{provider.ProviderName}' returned no {request.Timeframe} candles for {symbol}.");
        }

        result[request.Timeframe] = candles.OrderBy(candle => candle.OpenedAt).ToArray();
    }

    return result;
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
    var brokerResult = await PrepareConfiguredBrokerForMarketDataAsync(services, cancellationToken);
    if (!brokerResult.IsSuccess)
    {
        Console.Error.WriteLine($"Download history failed: {brokerResult.Error}");
        return 3;
    }

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

static async Task<Result> PrepareConfiguredBrokerForMarketDataAsync(ServiceRegistry services, CancellationToken cancellationToken)
{
    if (IsBroker(services.Options, "MT5"))
    {
        return await services.Broker.ValidateConnectionAsync(cancellationToken);
    }

    var tokenResult = await RefreshCTraderTokenAsync(services, cancellationToken);
    return tokenResult.IsSuccess ? Result.Success() : Result.Failure(tokenResult.Error!);
}

static bool IsBroker(TradingBotOptions options, string broker) =>
    string.Equals(options.Broker, broker, StringComparison.OrdinalIgnoreCase);

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
        Guid.NewGuid().ToString("N"),
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

static async Task<int> MT5TestConnectionAsync(ServiceRegistry services, CancellationToken cancellationToken)
{
    var result = await services.MT5Bridge.GetHealthAsync(cancellationToken);
    if (!result.IsSuccess)
    {
        Console.Error.WriteLine(result.Error);
        return 3;
    }

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        isConnected = true,
        broker = "MT5",
        bridgeBaseUrl = services.Options.MT5.BridgeBaseUrl,
        health = result.Value
    }, new JsonSerializerOptions { WriteIndented = true }));

    return 0;
}

static async Task<int> MT5AccountDetailsAsync(ServiceRegistry services, CancellationToken cancellationToken)
{
    var result = await services.MT5Bridge.GetAccountAsync(cancellationToken);
    if (!result.IsSuccess)
    {
        Console.Error.WriteLine(result.Error);
        return 3;
    }

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        broker = "MT5",
        configuredAccountId = services.Options.MT5.AccountId,
        account = result.Value
    }, new JsonSerializerOptions { WriteIndented = true }));

    return 0;
}

static async Task<int> MT5SymbolsAsync(ServiceRegistry services, string[] args, CancellationToken cancellationToken)
{
    var symbolName = ReadKeyValueArg(args, "symbol") ?? services.Options.Symbol;
    var result = await services.MT5Bridge.GetSymbolAsync(symbolName, cancellationToken);
    if (!result.IsSuccess)
    {
        Console.Error.WriteLine(result.Error);
        return 3;
    }

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        broker = "MT5",
        searchedSymbol = symbolName,
        symbol = result.Value
    }, new JsonSerializerOptions { WriteIndented = true }));

    return 0;
}

static async Task<int> MT5CreateOrderAsync(ServiceRegistry services, string[] args, CancellationToken cancellationToken)
{
    if (!TryReadDecimalArg(args, "entry_point", out var entryPoint) || !TryReadDecimalArg(args, "quantity", out var quantity))
    {
        Console.Error.WriteLine("Usage: TradingBot.CLI mt5-createorder entry_point=[value] quantity=[value] [confirm_live_order=true]");
        return 2;
    }

    if (!services.Options.MT5.AllowLiveOrderCreation && !HasFlagArg(args, "confirm_live_order", "true"))
    {
        Console.Error.WriteLine("MT5 order creation requires MT5:AllowLiveOrderCreation=true or confirm_live_order=true.");
        return 2;
    }

    var signal = new TradeSignal(
        services.Options.Symbol,
        TradeDirection.Buy,
        entryPoint,
        entryPoint - (services.Options.PipSize * 20m),
        entryPoint + (services.Options.PipSize * 60m),
        3m,
        SessionName.Closed,
        true,
        "Manual MT5 order test.");

    var cycleId = Guid.NewGuid().ToString("N");
    await services.Monitor.WriteAsync(cycleId, "manual_mt5_order_started", new
    {
        entryPoint,
        quantity
    }, cancellationToken);

    var marketGuard = await ValidateMarketExecutionGuardAsync(services, cycleId, formattedOutput: false, cancellationToken);
    if (!marketGuard.IsSuccess)
    {
        Console.Error.WriteLine(marketGuard.Error);
        return 4;
    }

    var staleCleanup = await CleanupStalePendingOrdersAsync(services, cycleId, formattedOutput: false, cancellationToken);
    if (!staleCleanup.IsSuccess)
    {
        Console.Error.WriteLine(staleCleanup.Error);
        return 4;
    }

    var activeTradeValidation = await ValidateActiveTradeLimitAsync(
        services,
        cycleId,
        formattedOutput: false,
        cancellationToken);
    if (!activeTradeValidation.IsSuccess)
    {
        Console.Error.WriteLine(activeTradeValidation.Error);
        return 4;
    }

    var result = await services.MT5Bridge.CreateLimitOrderAsync(signal, quantity, cancellationToken);
    if (!result.IsSuccess)
    {
        Console.Error.WriteLine(result.Error);
        return 4;
    }

    Console.WriteLine(JsonSerializer.Serialize(result.Value, new JsonSerializerOptions { WriteIndented = true }));
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
    string cycleId,
    bool formattedOutput,
    CancellationToken cancellationToken,
    ActiveTradeSummary? knownSummary = null,
    bool printSummary = true)
{
    var summaryResult = knownSummary is not null
        ? Result<ActiveTradeSummary>.Success(knownSummary)
        : await GetActiveTradeSummaryAsync(services, cancellationToken);
    if (!summaryResult.IsSuccess)
    {
        await services.Monitor.WriteAsync(cycleId, "active_trade_check_failed", new
        {
            summaryResult.Error
        }, cancellationToken);

        if (formattedOutput)
        {
            PrintFailure("Active Trade Check", summaryResult.Error!);
        }

        return Result.Failure(summaryResult.Error!);
    }

    var summary = summaryResult.Value!;
    await services.Monitor.WriteAsync(cycleId, "active_trade_checked", new
    {
        summary.OpenPositions,
        summary.PendingOrders,
        summary.TotalActiveTrades,
        maxActiveTrades = GetMaxActiveTrades(services.Options)
    }, cancellationToken);

    if (formattedOutput && printSummary)
    {
        PrintActiveTradeSummary(summary, GetMaxActiveTrades(services.Options));
    }

    var maxActiveTrades = GetMaxActiveTrades(services.Options);
    if (summary.TotalActiveTrades >= maxActiveTrades)
    {
        await services.Monitor.WriteAsync(cycleId, "active_trade_limit_reached", new
        {
            summary.TotalActiveTrades,
            maxActiveTrades
        }, cancellationToken);

        return Result.Failure($"Active trade limit reached. Active={summary.TotalActiveTrades}, MaxAllowed={maxActiveTrades}.");
    }

    return Result.Success();
}

static async Task<Result<ActiveTradeSummary>> ReportBrokerOrderStatusAsync(
    ServiceRegistry services,
    string cycleId,
    bool formattedOutput,
    CancellationToken cancellationToken)
{
    var summaryResult = await GetActiveTradeSummaryAsync(services, cancellationToken);
    if (!summaryResult.IsSuccess)
    {
        await services.Monitor.WriteAsync(cycleId, "broker_order_status_failed", new
        {
            summaryResult.Error
        }, cancellationToken);

        if (formattedOutput)
        {
            PrintFailure("Broker Orders", summaryResult.Error!);
        }

        return Result<ActiveTradeSummary>.Failure(summaryResult.Error!);
    }

    var summary = summaryResult.Value!;
    await services.Monitor.WriteAsync(cycleId, "broker_order_status_checked", new
    {
        summary.OpenPositions,
        summary.PendingOrders,
        summary.TotalActiveTrades,
        maxActiveTrades = GetMaxActiveTrades(services.Options)
    }, cancellationToken);

    if (formattedOutput)
    {
        PrintBrokerOrderStatus(summary, GetMaxActiveTrades(services.Options));
    }

    return Result<ActiveTradeSummary>.Success(summary);
}

static async Task<Result> ValidateMarketExecutionGuardAsync(
    ServiceRegistry services,
    string cycleId,
    bool formattedOutput,
    CancellationToken cancellationToken)
{
    if (!IsBroker(services.Options, "MT5"))
    {
        return Result.Success();
    }

    var snapshot = await services.MT5Bridge.GetMarketExecutionSnapshotAsync(services.Options.Symbol, cancellationToken);
    if (!snapshot.IsSuccess)
    {
        await services.Monitor.WriteAsync(cycleId, "market_guard_failed", new
        {
            snapshot.Error
        }, cancellationToken);

        if (formattedOutput)
        {
            PrintFailure("Market Execution Guard", snapshot.Error!);
        }

        return Result.Failure(snapshot.Error!);
    }

    var value = snapshot.Value!;
    await services.Monitor.WriteAsync(cycleId, "market_guard_checked", new
    {
        value.Bid,
        value.Ask,
        value.SpreadPips,
        maxSpreadPips = services.Options.MaxSpreadPips,
        maxSlippagePoints = services.Options.MT5.MaxSlippagePoints
    }, cancellationToken);

    if (formattedOutput)
    {
        PrintMarketExecutionGuard(value, services.Options.MaxSpreadPips, services.Options.MT5.MaxSlippagePoints);
    }

    if (value.Bid <= 0m || value.Ask <= 0m || value.Ask < value.Bid)
    {
        await services.Monitor.WriteAsync(cycleId, "market_guard_rejected", new
        {
            reason = "Invalid MT5 quote.",
            value.Bid,
            value.Ask
        }, cancellationToken);

        return Result.Failure($"Invalid MT5 quote. Bid={value.Bid}, Ask={value.Ask}.");
    }

    if (value.SpreadPips > services.Options.MaxSpreadPips)
    {
        await services.Monitor.WriteAsync(cycleId, "market_guard_rejected", new
        {
            reason = "Spread exceeded maximum.",
            value.SpreadPips,
            maxSpreadPips = services.Options.MaxSpreadPips
        }, cancellationToken);

        return Result.Failure($"Spread guard blocked order creation. Spread={decimal.Round(value.SpreadPips, 2)} pips, MaxAllowed={services.Options.MaxSpreadPips} pips.");
    }

    return Result.Success();
}

static async Task<Result> ValidatePendingLimitOrderPlacementAsync(
    ServiceRegistry services,
    string cycleId,
    TradeSignal signal,
    bool formattedOutput,
    CancellationToken cancellationToken)
{
    if (!IsBroker(services.Options, "MT5"))
    {
        return Result.Success();
    }

    var snapshot = await services.MT5Bridge.GetMarketExecutionSnapshotAsync(signal.Symbol, cancellationToken);
    if (!snapshot.IsSuccess)
    {
        await services.Monitor.WriteAsync(cycleId, "pending_limit_guard_failed", new
        {
            snapshot.Error
        }, cancellationToken);

        if (formattedOutput)
        {
            PrintFailure("Pending Limit Guard", snapshot.Error!);
        }

        return Result.Failure(snapshot.Error!);
    }

    var value = snapshot.Value!;
    await services.Monitor.WriteAsync(cycleId, "pending_limit_guard_checked", new
    {
        signal.Symbol,
        direction = signal.Direction.ToString(),
        entryPrice = signal.EntryPrice,
        value.Bid,
        value.Ask
    }, cancellationToken);

    string? rejectionReason = signal.Direction switch
    {
        TradeDirection.Buy when signal.EntryPrice >= value.Ask =>
            $"BUY LIMIT entry is no longer valid. Entry must be below current ask. Entry={signal.EntryPrice}, Ask={value.Ask}. No order created.",
        TradeDirection.Sell when signal.EntryPrice <= value.Bid =>
            $"SELL LIMIT entry is no longer valid. Entry must be above current bid. Entry={signal.EntryPrice}, Bid={value.Bid}. No order created.",
        _ => null
    };

    if (rejectionReason is null)
    {
        return Result.Success();
    }

    await services.Monitor.WriteAsync(cycleId, "pending_limit_guard_rejected", new
    {
        reason = rejectionReason,
        signal.Symbol,
        direction = signal.Direction.ToString(),
        entryPrice = signal.EntryPrice,
        value.Bid,
        value.Ask
    }, cancellationToken);

    if (formattedOutput)
    {
        PrintFailure("Pending Limit Guard", rejectionReason);
    }

    return Result.Failure(rejectionReason);
}

static async Task<Result> CleanupStalePendingOrdersAsync(
    ServiceRegistry services,
    string cycleId,
    bool formattedOutput,
    CancellationToken cancellationToken)
{
    if (!IsBroker(services.Options, "MT5") || !services.Options.MT5.CancelStalePendingOrders)
    {
        return Result.Success();
    }

    var cleanup = await services.MT5Bridge.CancelStalePendingOrdersAsync(cancellationToken);
    if (!cleanup.IsSuccess)
    {
        await services.Monitor.WriteAsync(cycleId, "stale_order_cleanup_failed", new
        {
            cleanup.Error
        }, cancellationToken);

        if (formattedOutput)
        {
            PrintFailure("Stale Order Cleanup", cleanup.Error!);
        }

        return Result.Failure(cleanup.Error!);
    }

    if (formattedOutput)
    {
        PrintStaleOrderCleanup(cleanup.Value!);
    }

    await services.Monitor.WriteAsync(cycleId, "stale_order_cleanup_completed", new
    {
        cleanup.Value!.CheckedOrders,
        cleanup.Value.CancelledOrders,
        cleanup.Value.CancelledOrderIds
    }, cancellationToken);

    return Result.Success();
}

static async Task<Result<ActiveTradeSummary>> GetActiveTradeSummaryAsync(ServiceRegistry services, CancellationToken cancellationToken)
{
    if (IsBroker(services.Options, "MT5"))
    {
        return await services.MT5Bridge.GetActiveTradesAsync(cancellationToken);
    }

    var accessToken = services.Options.CTrader.AccessToken;
    if (string.IsNullOrWhiteSpace(accessToken))
    {
        return Result<ActiveTradeSummary>.Failure("cTrader access token is required for active trade check.");
    }

    var result = await services.CTraderJsonApi.GetActiveTradeSummaryAsync(accessToken, cancellationToken);
    return result.IsSuccess
        ? Result<ActiveTradeSummary>.Success(new ActiveTradeSummary(result.Value!.OpenPositions, result.Value.PendingOrders))
        : Result<ActiveTradeSummary>.Failure(result.Error!);
}

static int GetMaxActiveTrades(TradingBotOptions options) =>
    options.LowRiskRollout.Enabled
        ? Math.Min(IsBroker(options, "MT5") ? options.MT5.MaxActiveTrades : options.MaxActiveTrades, options.LowRiskRollout.MaxActiveTrades)
        : IsBroker(options, "MT5") ? options.MT5.MaxActiveTrades : options.MaxActiveTrades;

static async Task<Result<TradeTrackingCycleResult>> TrackClosedTradesAsync(
    ServiceRegistry services,
    CancellationToken cancellationToken)
{
    if (!IsBroker(services.Options, "MT5"))
    {
        return Result<TradeTrackingCycleResult>.Success(new TradeTrackingCycleResult(0, 0, services.Options.TradeTracking.Directory));
    }

    var to = DateTimeOffset.UtcNow;
    var from = to.AddDays(-services.Options.TradeTracking.LookbackDays);
    var trades = await services.MT5Bridge.GetClosedTradesAsync(from, to, cancellationToken);
    if (!trades.IsSuccess)
    {
        return Result<TradeTrackingCycleResult>.Failure(trades.Error!);
    }

    var written = await services.TradeTracker.AppendAsync(trades.Value!, cancellationToken);
    return Result<TradeTrackingCycleResult>.Success(new TradeTrackingCycleResult(
        trades.Value!.Count,
        written,
        services.Options.TradeTracking.Directory));
}

static async Task<Result<DailyTradingStopStatus>> ValidateDailyTradingStopAsync(
    ServiceRegistry services,
    string cycleId,
    CancellationToken cancellationToken)
{
    var options = services.Options.DailyTradingStop;
    var now = DateTimeOffset.Now;
    var dayStart = new DateTimeOffset(now.Date, now.Offset);
    var dayEnd = dayStart.AddDays(1);

    if (!options.Enabled)
    {
        return Result<DailyTradingStopStatus>.Success(new DailyTradingStopStatus(
            false,
            0,
            0,
            options.MaxWinningTradesPerDay,
            options.MaxLosingTradesPerDay,
            dayStart,
            dayEnd,
            "Daily trading stop is disabled."));
    }

    if (!IsBroker(services.Options, "MT5"))
    {
        return Result<DailyTradingStopStatus>.Success(new DailyTradingStopStatus(
            false,
            0,
            0,
            options.MaxWinningTradesPerDay,
            options.MaxLosingTradesPerDay,
            dayStart,
            dayEnd,
            "Daily trading stop currently checks MT5 closed trades only."));
    }

    var trades = await services.MT5Bridge.GetClosedTradesAsync(dayStart, dayEnd, cancellationToken);
    if (!trades.IsSuccess)
    {
        return Result<DailyTradingStopStatus>.Failure(trades.Error!);
    }

    var todaysTrades = trades.Value!
        .Where(trade => trade.ClosedAt >= dayStart && trade.ClosedAt < dayEnd)
        .ToArray();
    var winningTrades = todaysTrades.Count(trade => trade.NetProfit > 0m);
    var losingTrades = todaysTrades.Count(trade => trade.NetProfit < 0m);
    var isWinLimitReached = winningTrades >= options.MaxWinningTradesPerDay;
    var isLossLimitReached = losingTrades >= options.MaxLosingTradesPerDay;
    var reason = isWinLimitReached
        ? $"Daily trading stopped after {winningTrades} winning trade(s)."
        : isLossLimitReached
            ? $"Daily trading stopped after {losingTrades} losing trade(s)."
            : "Daily trading stop limits not reached.";

    await services.Monitor.WriteAsync(cycleId, "daily_trading_stop_checked", new
    {
        winningTrades,
        losingTrades,
        options.MaxWinningTradesPerDay,
        options.MaxLosingTradesPerDay,
        dayStart,
        now,
        isHalted = isWinLimitReached || isLossLimitReached
    }, cancellationToken);

    return Result<DailyTradingStopStatus>.Success(new DailyTradingStopStatus(
        isWinLimitReached || isLossLimitReached,
        winningTrades,
        losingTrades,
        options.MaxWinningTradesPerDay,
        options.MaxLosingTradesPerDay,
        dayStart,
        dayEnd,
        reason));
}

static async Task<ForexMarketSessionStatus> ValidateForexMarketSessionAsync(
    ServiceRegistry services,
    string cycleId,
    DateTimeOffset now,
    CancellationToken cancellationToken)
{
    var options = services.Options.ForexMarketSessions;
    var status = BuildForexMarketSessionStatus(services.Options, now);

    await services.Monitor.WriteAsync(cycleId, "forex_market_session_checked", new
    {
        status.IsOpen,
        session = status.Session.ToString(),
        status.NewYorkTime,
        status.Reason,
        options.TradingDays,
        options.LondonSessionNYTime,
        options.NewYorkSessionNYTime
    }, cancellationToken);

    return status;
}

static ForexMarketSessionStatus BuildForexMarketSessionStatus(TradingBotOptions botOptions, DateTimeOffset now)
{
    var options = botOptions.ForexMarketSessions;
    var newYorkTime = ToNewYorkTime(now);
    if (!options.Enabled)
    {
        return new ForexMarketSessionStatus(
            true,
            SessionName.Closed,
            newYorkTime,
            "Forex market session guard is disabled.");
    }

    var tradingDays = options.TradingDays
        .Select(day => Enum.Parse<DayOfWeek>(day, ignoreCase: true))
        .ToHashSet();
    var isTradingDay = tradingDays.Contains(newYorkTime.DayOfWeek);
    var london = ParseSessionWindow(options.LondonSessionNYTime, "ForexMarketSessions:LondonSessionNYTime");
    var newYork = ParseSessionWindow(options.NewYorkSessionNYTime, "ForexMarketSessions:NewYorkSessionNYTime");
    var time = newYorkTime.TimeOfDay;
    var isLondonOpen = london.Contains(time);
    var isNewYorkOpen = newYork.Contains(time);
    var session = isLondonOpen && isNewYorkOpen
        ? SessionName.LondonNewYorkOverlap
        : isLondonOpen
            ? SessionName.London
            : isNewYorkOpen
                ? SessionName.NewYork
                : SessionName.Closed;
    var isOpen = isTradingDay && session != SessionName.Closed;
    var reason = isOpen
        ? $"Forex market session is open: {session}."
        : !isTradingDay
            ? $"Forex market session is closed because {newYorkTime.DayOfWeek} is not configured as a trading day."
            : "Forex market session is closed because current New York time is outside London and New York global sessions.";

    return new ForexMarketSessionStatus(isOpen, session, newYorkTime, reason);
}

static DateTimeOffset ToNewYorkTime(DateTimeOffset timestamp)
{
    var nyZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    return TimeZoneInfo.ConvertTime(timestamp, nyZone);
}

static SessionWindow ParseSessionWindow(string value, string optionName)
{
    var parts = value.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length != 2
        || !TimeSpan.TryParse(parts[0], out var start)
        || !TimeSpan.TryParse(parts[1], out var end))
    {
        throw new InvalidOperationException($"{optionName} must use HH:mm-HH:mm format.");
    }

    return new SessionWindow(start, end);
}

static async Task<int> ExecuteAnalyzeAndCreateOrderAsync(
    ServiceRegistry services,
    bool requireLiveConfirmation,
    bool formattedOutput,
    bool showTokenRecoveryHint,
    CancellationToken cancellationToken)
{
    var cycleId = Guid.NewGuid().ToString("N");
    await services.Monitor.WriteAsync(cycleId, "cycle_started", new
    {
        requireLiveConfirmation,
        formattedOutput,
        analysisTimeUtc = DateTimeOffset.UtcNow,
        maxActiveTrades = GetMaxActiveTrades(services.Options),
        riskPercent = services.Options.RiskPercentPerTrade
    }, cancellationToken);

    if (IsBroker(services.Options, "MT5"))
    {
        if (!services.Options.MT5.AllowLiveOrderCreation && !requireLiveConfirmation)
        {
            await services.Monitor.WriteAsync(cycleId, "live_order_permission_rejected", new
            {
                reason = "MT5 order creation requires MT5:AllowLiveOrderCreation=true or confirm_live_order=true."
            }, cancellationToken);
            Console.Error.WriteLine("MT5 order creation requires MT5:AllowLiveOrderCreation=true or confirm_live_order=true.");
            return 2;
        }
    }
    else if (!services.Options.CTrader.IsDemo && !services.Options.CTrader.AllowLiveOrderCreation && !requireLiveConfirmation)
    {
        await services.Monitor.WriteAsync(cycleId, "live_order_permission_rejected", new
        {
            reason = "Live-environment order creation requires CTrader:AllowLiveOrderCreation=true or confirm_live_order=true."
        }, cancellationToken);
        Console.Error.WriteLine("Live-environment order creation requires CTrader:AllowLiveOrderCreation=true or confirm_live_order=true.");
        return 2;
    }

    var analysisTime = DateTimeOffset.UtcNow;
    var forexMarketSession = await ValidateForexMarketSessionAsync(services, cycleId, analysisTime, cancellationToken);
    if (!forexMarketSession.IsOpen)
    {
        await services.Monitor.WriteAsync(cycleId, "forex_market_session_rejected", new
        {
            session = forexMarketSession.Session.ToString(),
            forexMarketSession.NewYorkTime,
            forexMarketSession.Reason
        }, cancellationToken);

        if (formattedOutput)
        {
            PrintCycleHeader(services.Options, new TradeSignal(
                services.Options.Symbol,
                TradeDirection.Buy,
                0m,
                0m,
                0m,
                0m,
                forexMarketSession.Session,
                false,
                forexMarketSession.Reason));
            PrintForexMarketSession(forexMarketSession);
            PrintInvalidSetup(new TradeSignal(
                services.Options.Symbol,
                TradeDirection.Buy,
                0m,
                0m,
                0m,
                0m,
                forexMarketSession.Session,
                false,
                forexMarketSession.Reason));
        }
        else
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                isOrderCreated = false,
                reason = forexMarketSession.Reason,
                forexMarketSession
            }, new JsonSerializerOptions { WriteIndented = true }));
        }

        await services.Monitor.WriteAsync(cycleId, "cycle_completed", new
        {
            isOrderCreated = false,
            reason = forexMarketSession.Reason,
            exitCode = 1
        }, cancellationToken);
        return 1;
    }

    var brokerResult = await PrepareConfiguredBrokerForMarketDataAsync(services, cancellationToken);
    if (!brokerResult.IsSuccess)
    {
        await services.Monitor.WriteAsync(cycleId, "broker_validation_failed", new
        {
            brokerResult.Error
        }, cancellationToken);

        if (formattedOutput)
        {
            PrintCycleHeader(services.Options, new TradeSignal(
                services.Options.Symbol,
                TradeDirection.Buy,
                0m,
                0m,
                0m,
                0m,
                SessionName.Closed,
                false,
                "Broker session validation failed."));
            PrintFailure("Broker Session", brokerResult.Error!);
            if (!IsBroker(services.Options, "MT5") && showTokenRecoveryHint)
            {
                PrintTokenRecoveryHint(brokerResult.Error!);
            }
            else if (!IsBroker(services.Options, "MT5"))
            {
                PrintAuthorizationAlreadyRequested();
            }
        }
        else
        {
            Console.Error.WriteLine(brokerResult.Error);
        }

        await services.Monitor.WriteAsync(cycleId, "cycle_completed", new
        {
            isOrderCreated = false,
            reason = brokerResult.Error,
            exitCode = 3
        }, cancellationToken);
        return 3;
    }

    await services.Monitor.WriteAsync(cycleId, "broker_validation_succeeded", new { }, cancellationToken);

    var dailyTradingStop = await ValidateDailyTradingStopAsync(services, cycleId, cancellationToken);
    if (!dailyTradingStop.IsSuccess)
    {
        await services.Monitor.WriteAsync(cycleId, "daily_trading_stop_check_failed", new
        {
            dailyTradingStop.Error
        }, cancellationToken);

        if (formattedOutput)
        {
            PrintCycleHeader(services.Options, new TradeSignal(
                services.Options.Symbol,
                TradeDirection.Buy,
                0m,
                0m,
                0m,
                0m,
                SessionName.Closed,
                false,
                "Daily trading stop check failed."));
            PrintFailure("Daily Trading Stop", dailyTradingStop.Error!);
        }
        else
        {
            Console.Error.WriteLine(dailyTradingStop.Error);
        }

        await services.Monitor.WriteAsync(cycleId, "cycle_completed", new
        {
            isOrderCreated = false,
            reason = dailyTradingStop.Error,
            exitCode = 4
        }, cancellationToken);
        return 4;
    }

    if (dailyTradingStop.Value!.IsHalted)
    {
        await services.Monitor.WriteAsync(cycleId, "daily_trading_halted", new
        {
            dailyTradingStop.Value.WinningTrades,
            dailyTradingStop.Value.LosingTrades,
            dailyTradingStop.Value.MaxWinningTrades,
            dailyTradingStop.Value.MaxLosingTrades,
            dailyTradingStop.Value.TradingDayStart,
            dailyTradingStop.Value.TradingDayEnd,
            dailyTradingStop.Value.Reason
        }, cancellationToken);

        if (formattedOutput)
        {
            PrintCycleHeader(services.Options, new TradeSignal(
                services.Options.Symbol,
                TradeDirection.Buy,
                0m,
                0m,
                0m,
                0m,
                SessionName.Closed,
                false,
                dailyTradingStop.Value.Reason));
            PrintDailyTradingStop(dailyTradingStop.Value);
        }
        else
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                isOrderCreated = false,
                reason = dailyTradingStop.Value.Reason,
                dailyTradingStop = dailyTradingStop.Value
            }, new JsonSerializerOptions { WriteIndented = true }));
        }

        await services.Monitor.WriteAsync(cycleId, "cycle_completed", new
        {
            isOrderCreated = false,
            reason = dailyTradingStop.Value.Reason,
            exitCode = 1
        }, cancellationToken);
        return 1;
    }

    var signal = await services.Strategy.AnalyzeAsync(services.Options.Symbol, analysisTime, cancellationToken);
    await services.Monitor.WriteAsync(cycleId, "strategy_analyzed", new
    {
        signal.Symbol,
        direction = signal.Direction.ToString(),
        session = signal.Session.ToString(),
        signal.IsValidSetup,
        signal.SetupReason,
        signal.EntryPrice,
        signal.StopLoss,
        signal.TakeProfit,
        signal.RiskReward
    }, cancellationToken);

    ActiveTradeSummary? cycleActiveTradeSummary = null;
    if (formattedOutput)
    {
        PrintCycleHeader(services.Options, signal);
        PrintForexMarketSession(forexMarketSession);
        var brokerOrderStatus = await ReportBrokerOrderStatusAsync(services, cycleId, formattedOutput, cancellationToken);
        if (brokerOrderStatus.IsSuccess)
        {
            cycleActiveTradeSummary = brokerOrderStatus.Value;
        }
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

        await services.Monitor.WriteAsync(cycleId, "cycle_completed", new
        {
            isOrderCreated = false,
            reason = signal.SetupReason,
            exitCode = 1
        }, cancellationToken);
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

    var accountSnapshot = await GetAccountSnapshotAsync(services, cancellationToken);
    if (!accountSnapshot.IsSuccess)
    {
        await services.Monitor.WriteAsync(cycleId, "account_snapshot_failed", new
        {
            accountSnapshot.Error
        }, cancellationToken);

        if (formattedOutput)
        {
            PrintFailure("Account Risk State", accountSnapshot.Error!);
        }
        else
        {
            Console.Error.WriteLine(accountSnapshot.Error);
        }

        await services.Monitor.WriteAsync(cycleId, "cycle_completed", new
        {
            isOrderCreated = false,
            reason = accountSnapshot.Error,
            exitCode = 4
        }, cancellationToken);
        return 4;
    }

    var account = accountSnapshot.Value!;
    await services.Monitor.WriteAsync(cycleId, "account_snapshot_loaded", new
    {
        account.Balance,
        account.Equity,
        account.DailyRealizedProfitLoss,
        account.WeeklyRealizedProfitLoss,
        account.ConsecutiveLosses,
        account.ConsecutiveLosingDays,
        account.DailyStartingBalance,
        account.InitialBalance,
        account.TradingDays
    }, cancellationToken);

    var riskDecision = services.Risk.Evaluate(order, account);
    if (!riskDecision.IsAllowed)
    {
        await services.Monitor.WriteAsync(cycleId, "risk_rejected", new
        {
            riskDecision.Reason
        }, cancellationToken);

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

        await services.Monitor.WriteAsync(cycleId, "cycle_completed", new
        {
            isOrderCreated = false,
            reason = riskDecision.Reason,
            exitCode = 1
        }, cancellationToken);
        return 1;
    }

    await services.Monitor.WriteAsync(cycleId, "risk_accepted", new
    {
        riskDecision.PositionSize,
        riskDecision.Reason,
        services.Options.RiskPercentPerTrade
    }, cancellationToken);

    if (formattedOutput)
    {
        PrintRiskAccepted(riskDecision, services.Options.RiskPercentPerTrade);
    }

    var marketGuard = await ValidateMarketExecutionGuardAsync(services, cycleId, formattedOutput, cancellationToken);
    if (!marketGuard.IsSuccess)
    {
        if (!formattedOutput)
        {
            Console.Error.WriteLine(marketGuard.Error);
        }

        await services.Monitor.WriteAsync(cycleId, "cycle_completed", new
        {
            isOrderCreated = false,
            reason = marketGuard.Error,
            exitCode = 4
        }, cancellationToken);
        return 4;
    }

    var pendingLimitGuard = await ValidatePendingLimitOrderPlacementAsync(services, cycleId, signal, formattedOutput, cancellationToken);
    if (!pendingLimitGuard.IsSuccess)
    {
        if (!formattedOutput)
        {
            Console.Error.WriteLine(pendingLimitGuard.Error);
        }

        await services.Monitor.WriteAsync(cycleId, "cycle_completed", new
        {
            isOrderCreated = false,
            reason = pendingLimitGuard.Error,
            exitCode = 4
        }, cancellationToken);
        return 4;
    }

    var staleCleanup = await CleanupStalePendingOrdersAsync(services, cycleId, formattedOutput, cancellationToken);
    if (!staleCleanup.IsSuccess)
    {
        if (!formattedOutput)
        {
            Console.Error.WriteLine(staleCleanup.Error);
        }

        await services.Monitor.WriteAsync(cycleId, "cycle_completed", new
        {
            isOrderCreated = false,
            reason = staleCleanup.Error,
            exitCode = 4
        }, cancellationToken);
        return 4;
    }

    var activeTradeValidation = await ValidateActiveTradeLimitAsync(
        services,
        cycleId,
        formattedOutput,
        cancellationToken,
        cycleActiveTradeSummary,
        printSummary: cycleActiveTradeSummary is null);
    if (!activeTradeValidation.IsSuccess)
    {
        if (!formattedOutput)
        {
            Console.Error.WriteLine(activeTradeValidation.Error);
        }

        await services.Monitor.WriteAsync(cycleId, "cycle_completed", new
        {
            isOrderCreated = false,
            reason = activeTradeValidation.Error,
            exitCode = 4
        }, cancellationToken);
        return 4;
    }

    var orderResult = await CreateConfiguredBrokerOrderAsync(services, signal, riskDecision.PositionSize, cancellationToken);

    if (!orderResult.IsSuccess)
    {
        await services.Monitor.WriteAsync(cycleId, "order_creation_failed", new
        {
            orderResult.Error
        }, cancellationToken);

        if (formattedOutput)
        {
            PrintFailure("Order Creation", orderResult.Error!);
        }
        else
        {
            Console.Error.WriteLine(orderResult.Error);
        }

        await services.Monitor.WriteAsync(cycleId, "cycle_completed", new
        {
            isOrderCreated = false,
            reason = orderResult.Error,
            exitCode = 4
        }, cancellationToken);
        return 4;
    }

    if (formattedOutput)
    {
        PrintOrderCreated(orderResult.Value!, riskDecision);
    }
    else
    {
        var createdOrder = orderResult.Value!;
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
                isAccepted = true,
                createdOrder.ClientOrderId,
                createdOrder.BrokerOrderId,
                createdOrder.AccountId,
                createdOrder.SymbolId,
                createdOrder.TradeSide,
                lots = createdOrder.Lots,
                createdOrder.Volume,
                createdOrder.LimitPrice,
                createdOrder.StopLoss,
                createdOrder.TakeProfit,
                createdOrder.RiskReward
            }
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    await services.Monitor.WriteAsync(cycleId, "order_created", new
    {
        orderResult.Value!.ClientOrderId,
        orderResult.Value.BrokerOrderId,
        orderResult.Value.AccountId,
        orderResult.Value.TradeSide,
        orderResult.Value.Lots,
        orderResult.Value.Volume,
        orderResult.Value.LimitPrice,
        orderResult.Value.StopLoss,
        orderResult.Value.TakeProfit,
        orderResult.Value.RiskReward
    }, cancellationToken);

    await services.Monitor.WriteAsync(cycleId, "cycle_completed", new
    {
        isOrderCreated = true,
        exitCode = 0
    }, cancellationToken);

    return 0;
}

static async Task<Result<BrokerOrderCreationResult>> CreateConfiguredBrokerOrderAsync(
    ServiceRegistry services,
    TradeSignal signal,
    decimal lots,
    CancellationToken cancellationToken)
{
    if (IsBroker(services.Options, "MT5"))
    {
        return await services.MT5Bridge.CreateLimitOrderAsync(signal, lots, cancellationToken);
    }

    var accessToken = services.Options.CTrader.AccessToken;
    if (string.IsNullOrWhiteSpace(accessToken))
    {
        return Result<BrokerOrderCreationResult>.Failure("cTrader access token is required for order creation.");
    }

    var result = await services.CTraderJsonApi.CreateLimitOrderAsync(
        signal.EntryPrice,
        lots,
        signal.Direction,
        signal.StopLoss,
        signal.TakeProfit,
        accessToken,
        cancellationToken);

    if (!result.IsSuccess)
    {
        return Result<BrokerOrderCreationResult>.Failure(result.Error!);
    }

    var value = result.Value!;
    return Result<BrokerOrderCreationResult>.Success(new BrokerOrderCreationResult(
        value.ClientOrderId,
        "",
        value.CtidTraderAccountId,
        value.SymbolId,
        value.TradeSide,
        lots,
        value.Volume,
        value.LimitPrice,
        value.StopLoss,
        value.TakeProfit,
        value.RiskReward));
}

static async Task<Result<AccountSnapshot>> GetAccountSnapshotAsync(ServiceRegistry services, CancellationToken cancellationToken)
{
    var configuredInitialBalance = services.Options.ActiveFundedAccountChallenge?.InitialBalance ?? 0m;

    if (IsBroker(services.Options, "MT5"))
    {
        var snapshot = await services.MT5Bridge.GetAccountSnapshotAsync(cancellationToken);
        if (snapshot.IsSuccess && snapshot.Value!.InitialBalance <= 0m)
        {
            var value = snapshot.Value;
            snapshot = Result<AccountSnapshot>.Success(value with
            {
                InitialBalance = configuredInitialBalance > 0m
                    ? configuredInitialBalance
                    : services.Options.AccountBalance
            });
        }

        return snapshot;
    }

    return Result<AccountSnapshot>.Success(new AccountSnapshot(
        services.Options.AccountBalance,
        services.Options.AccountBalance,
        0m,
        0m,
                0,
                0,
                services.Options.AccountBalance,
                configuredInitialBalance > 0m ? configuredInitialBalance : services.Options.AccountBalance,
                0));
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

static bool TryReadIntArg(string[] args, string name, out int value)
{
    value = 0;
    var prefix = $"{name}=";
    var raw = args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    return raw is not null
        && int.TryParse(raw[prefix.Length..], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value);
}

static bool HasFlagArg(string[] args, string name, string expectedValue)
{
    var prefix = $"{name}=";
    var raw = args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    return raw is not null && string.Equals(raw[prefix.Length..], expectedValue, StringComparison.OrdinalIgnoreCase);
}

static bool HasStartOnceArg(string[] args) =>
    args.Skip(1).Any(arg => string.Equals(arg, "once", StringComparison.OrdinalIgnoreCase));

static bool IsTrackingMode(string[] args)
{
    var mode = ReadKeyValueArg(args, "mode");
    return string.Equals(mode, "traking", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mode, "tracking", StringComparison.OrdinalIgnoreCase);
}

static string? ReadKeyValueArg(string[] args, string name)
{
    var prefix = $"{name}=";
    var raw = args.FirstOrDefault(arg => arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    return raw is null ? null : raw[prefix.Length..];
}

static DateTimeOffset PreviousTradingDayStart(DateTimeOffset timestamp)
{
    var day = new DateTimeOffset(timestamp.Date.AddDays(-1), timestamp.Offset);
    while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
    {
        day = day.AddDays(-1);
    }

    return day;
}

static int Help()
{
    Console.WriteLine("Usage:");
    PrintCommand("start [once] [mode=traking|normal]", "Runs broker connect, then analyze-and-createorder on the configured interval. Use 'start once' for one cycle; mode=traking records closed trades to CSV.");
    PrintCommand("stop", "Logs a safe shutdown request. No daemon state is active yet.");
    PrintCommand("analyze", "Runs one Smart Money strategy analysis cycle and prints the generated signal.");
    PrintCommand("backtest --from 2025-05-15 --to 2026-05-15", "Runs a backtest from cTrader historical candles and writes a CSV report.");
    PrintCommand("find-recent-setups count=10 [max_days=30]", "Finds valid setups starting from the previous trading day and scanning backward by trading day using the first enabled historical source, then writes a CSV report.");
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
    PrintCommand("mt5-test-connection", "Verifies connectivity with the configured local MT5 bridge.");
    PrintCommand("mt5-account-details", "Fetches MT5 account details from the configured local bridge.");
    PrintCommand("mt5-symbols [symbol=EURUSD]", "Fetches MT5 symbol metadata from the configured local bridge.");
    PrintCommand("mt5-createorder entry_point=[value] quantity=[value] [confirm_live_order=true]", "Creates an MT5 limit order through the configured local bridge.");
    PrintCommand("analyze-and-createorder [confirm_live_order=true]", "Analyzes the configured strategy and creates an order only when a valid setup exists.");
    return 0;
}

static void PrintCommand(string command, string description)
{
    Console.WriteLine($"  TradingBot.CLI {command}");
    Console.WriteLine($"      {description}");
}

static void PrintCycleHeader(TradingBotOptions options, TradeSignal signal)
{
    Console.WriteLine();
    Console.WriteLine("============================================================");
    Console.WriteLine($" TradingBot Analysis Cycle - {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
    Console.WriteLine("============================================================");
    Console.WriteLine($"Broker : {options.Broker}");
    Console.WriteLine($"Strategy: {options.ActiveStrategy.Name}");
    Console.WriteLine($"Engine : {options.ActiveStrategy.Engine}");
    Console.WriteLine($"Symbol : {signal.Symbol}");
    Console.WriteLine($"Session: {signal.Session}");
    Console.WriteLine($"Kill Zone    : {(signal.Session == SessionName.Closed ? "Outside configured kill zone" : "Inside configured kill zone")}");
    Console.WriteLine($"Out-of-zone  : {(options.TradeOutKillZoneTime ? "Allowed" : "Blocked")}");
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

static void PrintMarketExecutionGuard(MarketExecutionSnapshot snapshot, decimal maxSpreadPips, int maxSlippagePoints)
{
    Console.WriteLine();
    Console.WriteLine("Market Guard : CHECKED");
    Console.WriteLine($"Bid          : {snapshot.Bid}");
    Console.WriteLine($"Ask          : {snapshot.Ask}");
    Console.WriteLine($"Spread      : {decimal.Round(snapshot.SpreadPips, 2)} pips");
    Console.WriteLine($"Max Spread  : {maxSpreadPips} pips");
    Console.WriteLine($"Max Slippage: {maxSlippagePoints} points");
}

static void PrintStaleOrderCleanup(StaleOrderCleanupResult cleanup)
{
    Console.WriteLine();
    Console.WriteLine("Stale Orders : CHECKED");
    Console.WriteLine($"Checked      : {cleanup.CheckedOrders}");
    Console.WriteLine($"Cancelled    : {cleanup.CancelledOrders}");
    if (cleanup.CancelledOrders > 0)
    {
        Console.WriteLine($"Order IDs    : {string.Join(", ", cleanup.CancelledOrderIds)}");
    }
}

static void PrintTradeTracking(int fetchedTrades, int writtenTrades, string directory)
{
    Console.WriteLine();
    Console.WriteLine("Trade Tracking: CHECKED");
    Console.WriteLine($"Fetched Closed: {fetchedTrades}");
    Console.WriteLine($"New CSV Rows  : {writtenTrades}");
    Console.WriteLine($"Directory     : {directory}");
}

static void PrintTradeTrackingSkipped(ForexMarketSessionStatus status)
{
    Console.WriteLine();
    Console.WriteLine("Trade Tracking: SKIPPED");
    Console.WriteLine($"Reason        : {status.Reason}");
}

static void PrintForexMarketSession(ForexMarketSessionStatus status)
{
    Console.WriteLine();
    Console.WriteLine($"Market Session: {status.Session}");
    Console.WriteLine($"Market Status : {(status.IsOpen ? "Open" : "Closed")}");
    Console.WriteLine($"NY Time       : {status.NewYorkTime:yyyy-MM-dd HH:mm:ss zzz}");
    Console.WriteLine($"Reason        : {status.Reason}");
}

static void PrintDailyTradingStop(DailyTradingStopStatus status)
{
    Console.WriteLine();
    Console.WriteLine("Daily Stop    : HALTED");
    Console.WriteLine($"Reason        : {status.Reason}");
    Console.WriteLine($"Winning Trades: {status.WinningTrades} / {status.MaxWinningTrades}");
    Console.WriteLine($"Losing Trades : {status.LosingTrades} / {status.MaxLosingTrades}");
    Console.WriteLine($"Trading Day   : {status.TradingDayStart:yyyy-MM-dd HH:mm:ss zzz} -> {status.TradingDayEnd:yyyy-MM-dd HH:mm:ss zzz}");
    Console.WriteLine("Action        : No analysis or order creation until the next trading day.");
}

static void PrintRecentSetupSearchSummary(
    TradingBotOptions options,
    string source,
    DateTimeOffset from,
    DateTimeOffset to,
    int requested,
    int found,
    int scannedDays,
    int maxDays,
    string csvPath)
{
    Console.WriteLine();
    Console.WriteLine("Recent Valid Setup Search");
    Console.WriteLine($"Source   : {source}");
    Console.WriteLine($"Strategy : {options.ActiveStrategy.Name}");
    Console.WriteLine($"Symbol   : {options.Symbol}");
    Console.WriteLine($"Window   : {from:yyyy-MM-dd HH:mm:ss zzz} -> {to:yyyy-MM-dd HH:mm:ss zzz}");
    Console.WriteLine($"Requested: {requested}");
    Console.WriteLine($"Found    : {found}");
    Console.WriteLine($"Days Scan: {scannedDays}{(maxDays == 0 ? " (unbounded)" : $" / {maxDays}")}");
    Console.WriteLine($"CSV      : {csvPath}");
    if (found == 0)
    {
        Console.WriteLine("Reason   : No valid setups found in the scanned trading days.");
    }
}

static void PrintActiveTradeSummary(ActiveTradeSummary summary, int maxActiveTrades)
{
    Console.WriteLine();
    Console.WriteLine("Active Trades: CHECKED");
    Console.WriteLine($"Open Pos.    : {summary.OpenPositions}");
    Console.WriteLine($"Pending Ord. : {summary.PendingOrders}");
    Console.WriteLine($"Total Active : {summary.TotalActiveTrades}");
    Console.WriteLine($"Max Allowed  : {maxActiveTrades}");
}

static void PrintBrokerOrderStatus(ActiveTradeSummary summary, int maxActiveTrades)
{
    Console.WriteLine();
    Console.WriteLine("Broker Orders: CHECKED");
    Console.WriteLine($"Open Pos.    : {summary.OpenPositions}");
    Console.WriteLine($"Pending Ord. : {summary.PendingOrders}");
    Console.WriteLine($"Total Active : {summary.TotalActiveTrades}");
    Console.WriteLine($"Max Allowed  : {maxActiveTrades}");
}

static void PrintOrderCreated(BrokerOrderCreationResult orderResult, RiskDecision riskDecision)
{
    Console.WriteLine();
    Console.WriteLine("Order Status : CREATED");
    Console.WriteLine($"Client ID    : {orderResult.ClientOrderId}");
    Console.WriteLine($"Broker ID    : {orderResult.BrokerOrderId}");
    Console.WriteLine($"Account ID   : {orderResult.AccountId}");
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
        // Clear the visible screen, clear scrollback, then move the cursor home.
        // Windows Terminal supports this and it behaves closer to running `cls`.
        Console.Write("\u001b[2J\u001b[3J\u001b[H");
        return;
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
    var brokers = root["Brokers"]?.AsObject();
    var cTrader = brokers?["CTrader"]?.AsObject()
        ?? root["CTrader"]?.AsObject()
        ?? throw new InvalidOperationException("Missing Brokers:CTrader section.");

    cTrader["AuthorizationCode"] = "";
    cTrader["AccessToken"] = accessToken;
    cTrader["RefreshToken"] = refreshToken;

    await File.WriteAllTextAsync(appsettingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
}

internal sealed record ServiceRegistry(
    string AppsettingsPath,
    TradingBotOptions Options,
    IBrokerClient Broker,
    CTraderConnectionTester CTraderTester,
    CTraderJsonApiClient CTraderJsonApi,
    MT5BridgeClient MT5Bridge,
    IHistoricalMarketDataProvider HistoricalMarketData,
    IStrategyEngine Strategy,
    RiskManager Risk,
    BacktestingEngine Backtesting,
    CsvJournalWriter Journal,
    RecentValidSetupCsvWriter RecentSetups,
    OperationalLogWriter Monitor,
    ClosedTradeTrackingWriter TradeTracker);

internal sealed record TradeTrackingCycleResult(
    int FetchedTrades,
    int WrittenTrades,
    string Directory);

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

internal sealed class InMemoryMarketDataProvider(IReadOnlyDictionary<Timeframe, IReadOnlyList<Candle>> candles) : IMarketDataProvider
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
