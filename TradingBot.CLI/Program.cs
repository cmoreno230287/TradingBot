using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
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
    "backtest-learning" => await BacktestLearningAsync(services, args, cancellationToken),
    "download-history" => await DownloadHistoryAsync(services, args, cancellationToken),
    "find-signal" => await FindSignalAsync(services, args, cancellationToken),
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
    var sessionClock = new NewYorkSessionClock();
    var strategy = new SmartMoneyStrategyEngine(marketData, newsFilter, sessionClock, options);
    var risk = new RiskManager(options);
    var historicalProvider = BuildHistoricalProvider(options, cTraderJsonApi, mt5Bridge);
    IHistoricalTickDataProvider? tickProvider = string.Equals(options.Backtesting.DataSource, "MT5", StringComparison.OrdinalIgnoreCase)
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
        new BacktestingEngine(strategy, risk, historicalProvider, tickProvider, options),
        new CsvJournalWriter(options),
        new OperationalLogWriter(options),
        new ClosedTradeTrackingWriter(options),
        new SignalTrackingWriter(options));
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

    return new HistoricalMarketDataProviderFactory(providers).Resolve(options.Backtesting.DataSource);
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
            ClearConsoleFully();
            var cycleResult = await CaptureConsoleOutputAsync(() => ExecuteAnalyzeAndCreateOrderAsync(
                    services,
                    requireLiveConfirmation: false,
                    formattedOutput: true,
                    showTokenRecoveryHint: !authorizationPromptShown,
                    cancellationToken));
            ClearConsoleFully();
            WriteCapturedOutput(cycleResult);

            if (trackingMode)
            {
                var trackingResult = await TrackClosedTradesAsync(services, cancellationToken);
                if (trackingResult.IsSuccess)
                {
                    PrintTradeTracking(trackingResult.Value!.FetchedTrades, trackingResult.Value.WrittenTrades, trackingResult.Value.Directory);
                }
                else
                {
                    PrintFailure("Trade Tracking", trackingResult.Error!);
                }
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
    else if (string.Equals(services.Options.Backtesting.DataSource, "MT5", StringComparison.OrdinalIgnoreCase))
    {
        var brokerResult = await services.Broker.ValidateConnectionAsync(cancellationToken);
        if (!brokerResult.IsSuccess)
        {
            Console.Error.WriteLine($"Backtest failed: {brokerResult.Error}");
            return 4;
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

static async Task<int> FindSignalAsync(ServiceRegistry services, string[] args, CancellationToken cancellationToken)
{
    var symbol = ReadKeyValueArg(args, "symbol") ?? services.Options.Symbol;
    var screenshots = await CaptureTradingViewScreenshotsAsync(services.Options, symbol, cancellationToken);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        symbol,
        screenshots = screenshots.Select(item => new { item.Timeframe, item.Path, item.IsCaptured, item.Message })
    }, new JsonSerializerOptions { WriteIndented = true }));

    var brokerResult = await PrepareConfiguredBrokerForMarketDataAsync(services, cancellationToken);
    if (!brokerResult.IsSuccess)
    {
        Console.Error.WriteLine(brokerResult.Error);
        return 3;
    }

    var signal = await services.Strategy.AnalyzeAsync(symbol, DateTimeOffset.UtcNow, cancellationToken);
    if (!signal.IsValidSetup)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            isSignalFound = false,
            signal.SetupReason,
            signal.Session
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 1;
    }

    var report = new SignalReport(
        $"signal-{symbol}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}",
        signal.Symbol,
        signal.Session.ToString(),
        signal.Direction.ToString().ToUpperInvariant(),
        signal.EntryPrice,
        signal.StopLoss,
        signal.TakeProfit,
        signal.RiskReward,
        DateTimeOffset.UtcNow,
        signal.SetupReason,
        screenshots.FirstOrDefault(item => item.Timeframe == "1h")?.Path ?? "",
        screenshots.FirstOrDefault(item => item.Timeframe == "5m")?.Path ?? "",
        screenshots.FirstOrDefault(item => item.Timeframe == "1m")?.Path ?? "");

    var written = await services.SignalTracker.AppendAsync([report], cancellationToken);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        isSignalFound = true,
        signal = report,
        csvRowsWritten = written
    }, new JsonSerializerOptions { WriteIndented = true }));

    return 0;
}

static async Task<IReadOnlyList<ScreenshotCaptureResult>> CaptureTradingViewScreenshotsAsync(
    TradingBotOptions options,
    string symbol,
    CancellationToken cancellationToken)
{
    Directory.CreateDirectory(options.TradingView.ScreenshotDirectory);
    var browser = ResolveBrowserPath(options);
    var requests = new[]
    {
        new { Timeframe = "1h", Interval = "60" },
        new { Timeframe = "5m", Interval = "5" },
        new { Timeframe = "1m", Interval = "1" }
    };
    var results = new List<ScreenshotCaptureResult>();

    foreach (var request in requests)
    {
        var path = Path.GetFullPath(Path.Combine(
            options.TradingView.ScreenshotDirectory,
            $"{symbol}_{request.Timeframe}.{options.TradingView.ScreenshotExtension.TrimStart('.')}"));
        var url = BuildTradingViewChartUrl(options, request.Interval);

        if (browser is null)
        {
            results.Add(new ScreenshotCaptureResult(request.Timeframe, path, false, "No supported browser executable was found for headless screenshot capture."));
            continue;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception)
        {
            results.Add(new ScreenshotCaptureResult(request.Timeframe, path, false, $"Unable to replace existing screenshot: {exception.Message}"));
            continue;
        }

        var processInfo = new ProcessStartInfo
        {
            FileName = browser,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        processInfo.ArgumentList.Add("--headless=new");
        processInfo.ArgumentList.Add("--disable-gpu");
        processInfo.ArgumentList.Add($"--window-size={options.TradingView.ScreenshotWidth},{options.TradingView.ScreenshotHeight}");
        processInfo.ArgumentList.Add($"--screenshot={path}");
        processInfo.ArgumentList.Add(url);

        using var screenshotProcess = Process.Start(processInfo);
        if (screenshotProcess is null)
        {
            results.Add(new ScreenshotCaptureResult(request.Timeframe, path, false, "Unable to start browser screenshot process."));
            continue;
        }

        await screenshotProcess.WaitForExitAsync(cancellationToken);
        results.Add(new ScreenshotCaptureResult(
            request.Timeframe,
            path,
            screenshotProcess.ExitCode == 0 && File.Exists(path),
            screenshotProcess.ExitCode == 0 && File.Exists(path) ? "Captured." : $"Browser exited with code {screenshotProcess.ExitCode}."));
    }

    return results;
}

static string BuildTradingViewChartUrl(TradingBotOptions options, string interval)
{
    var symbol = Uri.EscapeDataString(options.TradingView.Symbol);
    return $"https://www.tradingview.com/chart/?symbol={symbol}&interval={Uri.EscapeDataString(interval)}";
}

static string? ResolveBrowserPath(TradingBotOptions options)
{
    if (!string.IsNullOrWhiteSpace(options.TradingView.BrowserPath) && File.Exists(options.TradingView.BrowserPath))
    {
        return options.TradingView.BrowserPath;
    }

    var candidates = new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe")
    };

    return candidates.FirstOrDefault(File.Exists);
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
    CancellationToken cancellationToken)
{
    var summaryResult = await GetActiveTradeSummaryAsync(services, cancellationToken);
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

    if (formattedOutput)
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

    var signal = await services.Strategy.AnalyzeAsync(services.Options.Symbol, DateTimeOffset.UtcNow, cancellationToken);
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

    if (formattedOutput)
    {
        PrintCycleHeader(services.Options, signal);
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
        cancellationToken);
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
    if (IsBroker(services.Options, "MT5"))
    {
        var snapshot = await services.MT5Bridge.GetAccountSnapshotAsync(cancellationToken);
        if (snapshot.IsSuccess && snapshot.Value!.InitialBalance <= 0m)
        {
            var value = snapshot.Value;
            snapshot = Result<AccountSnapshot>.Success(value with
            {
                InitialBalance = services.Options.FTMOChallenge.InitialBalance > 0m
                    ? services.Options.FTMOChallenge.InitialBalance
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
                services.Options.FTMOChallenge.InitialBalance > 0m ? services.Options.FTMOChallenge.InitialBalance : services.Options.AccountBalance,
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

static int Help()
{
    Console.WriteLine("Usage:");
    PrintCommand("start [once] [mode=traking|normal]", "Runs broker connect, then analyze-and-createorder on the configured interval. Use 'start once' for one cycle; mode=traking records closed trades to CSV.");
    PrintCommand("stop", "Logs a safe shutdown request. No daemon state is active yet.");
    PrintCommand("analyze", "Runs one Smart Money strategy analysis cycle and prints the generated signal.");
    PrintCommand("backtest --from 2025-05-15 --to 2026-05-15", "Runs a backtest from cTrader historical candles and writes a CSV report.");
    PrintCommand("backtest-learning --date 2026-05-19", "Scans one date for a valid setup and writes a learning chart plus PDF report.");
    PrintCommand("download-history --from 2025-05-15 --to 2026-05-15 timeframe=M5", "Downloads/caches historical candles from the configured backtesting provider.");
    PrintCommand("find-signal [symbol=EURUSD]", "Captures TradingView 1h/5m/1m screenshots and reports a valid strategy signal when found.");
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

static void PrintActiveTradeSummary(ActiveTradeSummary summary, int maxActiveTrades)
{
    Console.WriteLine();
    Console.WriteLine("Active Trades: CHECKED");
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
    SmartMoneyStrategyEngine Strategy,
    RiskManager Risk,
    BacktestingEngine Backtesting,
    CsvJournalWriter Journal,
    OperationalLogWriter Monitor,
    ClosedTradeTrackingWriter TradeTracker,
    SignalTrackingWriter SignalTracker);

internal sealed record TradeTrackingCycleResult(
    int FetchedTrades,
    int WrittenTrades,
    string Directory);

internal sealed record ScreenshotCaptureResult(
    string Timeframe,
    string Path,
    bool IsCaptured,
    string Message);
