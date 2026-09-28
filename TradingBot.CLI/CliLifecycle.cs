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
    internal static async Task<int> StartAsync(ServiceRegistry services, string[] args, CancellationToken cancellationToken)
    {
        if (services.Options.FtmoProtection.Enabled)
            return await FtmoCommands.StartAsync(services.Options, services.Strategy, services.MT5Bridge, HasStartOnceArg(args), cancellationToken);
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

    internal static int Stop()
    {
        Log("shutdown", "Stop requested. No long-running daemon state is active in this CLI instance.");
        return 0;
    }

    internal static async Task<int> AnalyzeAsync(ServiceRegistry services, CancellationToken cancellationToken)
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

    internal static async Task<int> AnalyzeAndCreateOrderAsync(ServiceRegistry services, string[] args, CancellationToken cancellationToken)
    {
        if (services.Options.FtmoProtection.Enabled)
            return await FtmoCommands.StartAsync(services.Options, services.Strategy, services.MT5Bridge, true, cancellationToken);
        return await ExecuteAnalyzeAndCreateOrderAsync(
            services,
            requireLiveConfirmation: HasFlagArg(args, "confirm_live_order", "true"),
            formattedOutput: false,
            showTokenRecoveryHint: true,
            cancellationToken);
    }

}
