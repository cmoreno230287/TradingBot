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
    internal static int Help()
    {
        Console.WriteLine("Usage:");
        PrintCommand("status", "Read-only live account, configuration and readiness status. No orders or cancellations.");
        PrintCommand("start [once]", "Runs actual protected trading with independent protection and reporting. 'once' performs one cycle, not continuous monitoring.");
        PrintCommand("stop", "Logs a safe shutdown request. No daemon state is active yet.");
        PrintCommand("analyze", "Runs one Smart Money strategy analysis cycle and prints the generated signal.");
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

    internal static void PrintCommand(string command, string description)
    {
        Console.WriteLine($"  TradingBot.CLI {command}");
        Console.WriteLine($"      {description}");
    }

    internal static void PrintCycleHeader(TradingBotOptions options, TradeSignal signal)
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

    internal static void PrintInvalidSetup(TradeSignal signal)
    {
        Console.WriteLine();
        Console.WriteLine("Setup Status : INVALID");
        Console.WriteLine($"Reason       : {signal.SetupReason}");
        Console.WriteLine("Action       : No order created");
    }

    internal static void PrintValidSignal(TradeSignal signal)
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

    internal static void PrintRiskAccepted(RiskDecision riskDecision, decimal riskPercent)
    {
        Console.WriteLine();
        Console.WriteLine("Risk Status  : ACCEPTED");
        Console.WriteLine($"Risk Percent : {riskPercent}%");
        Console.WriteLine($"Lot Size     : {riskDecision.PositionSize}");
        Console.WriteLine($"Reason       : {riskDecision.Reason}");
    }

    internal static void PrintRiskRejected(RiskDecision riskDecision)
    {
        Console.WriteLine();
        Console.WriteLine("Risk Status  : REJECTED");
        Console.WriteLine($"Reason       : {riskDecision.Reason}");
        Console.WriteLine("Action       : No order created");
    }

    internal static void PrintMarketExecutionGuard(MarketExecutionSnapshot snapshot, decimal maxSpreadPips, int maxSlippagePoints)
    {
        Console.WriteLine();
        Console.WriteLine("Market Guard : CHECKED");
        Console.WriteLine($"Bid          : {snapshot.Bid}");
        Console.WriteLine($"Ask          : {snapshot.Ask}");
        Console.WriteLine($"Spread      : {decimal.Round(snapshot.SpreadPips, 2)} pips");
        Console.WriteLine($"Max Spread  : {maxSpreadPips} pips");
        Console.WriteLine($"Max Slippage: {maxSlippagePoints} points");
    }

    internal static void PrintPendingEntryDistance(decimal entryDistancePips, decimal maxEntryDistancePips)
    {
        Console.WriteLine();
        Console.WriteLine("Entry Distance: CHECKED");
        Console.WriteLine($"Distance      : {decimal.Round(entryDistancePips, 2)} pips");
        Console.WriteLine($"Max Allowed   : {maxEntryDistancePips} pips");
    }

    internal static void PrintPendingOrderInvalidation(PendingOrderInvalidationResult result)
    {
        Console.WriteLine();
        Console.WriteLine("Pending Orders: REVALIDATED");
        Console.WriteLine($"Checked       : {result.CheckedOrders}");
        Console.WriteLine($"Cancelled     : {result.CancelledOrders}");
        foreach (var cancellation in result.Cancellations)
        {
            Console.WriteLine($"Order {cancellation.Ticket}: {cancellation.Reason}");
        }
    }

    internal static void PrintStaleOrderCleanup(StaleOrderCleanupResult cleanup)
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

    internal static void PrintTradeTracking(int fetchedTrades, int writtenTrades, string directory)
    {
        Console.WriteLine();
        Console.WriteLine("Trade Tracking: CHECKED");
        Console.WriteLine($"Fetched Closed: {fetchedTrades}");
        Console.WriteLine($"New CSV Rows  : {writtenTrades}");
        Console.WriteLine($"Directory     : {directory}");
    }

    internal static void PrintTradeTrackingSkipped(ForexMarketSessionStatus status)
    {
        Console.WriteLine();
        Console.WriteLine("Trade Tracking: SKIPPED");
        Console.WriteLine($"Reason        : {status.Reason}");
    }

    internal static void PrintForexMarketSession(ForexMarketSessionStatus status)
    {
        Console.WriteLine();
        Console.WriteLine($"Market Session: {status.Session}");
        Console.WriteLine($"Market Status : {(status.IsOpen ? "Open" : "Closed")}");
        Console.WriteLine($"NY Time       : {status.NewYorkTime:yyyy-MM-dd HH:mm:ss zzz}");
        Console.WriteLine($"Reason        : {status.Reason}");
    }

    internal static void PrintDailyTradingStop(DailyTradingStopStatus status)
    {
        Console.WriteLine();
        Console.WriteLine("Daily Stop    : HALTED");
        Console.WriteLine($"Reason        : {status.Reason}");
        Console.WriteLine($"Winning Trades: {status.WinningTrades} / {status.MaxWinningTrades}");
        Console.WriteLine($"Losing Trades : {status.LosingTrades} / {status.MaxLosingTrades}");
        Console.WriteLine($"Trading Day   : {status.TradingDayStart:yyyy-MM-dd HH:mm:ss zzz} -> {status.TradingDayEnd:yyyy-MM-dd HH:mm:ss zzz}");
        Console.WriteLine("Action        : No analysis or order creation until the next trading day.");
    }

    internal static void PrintActiveTradeSummary(ActiveTradeSummary summary, int maxActiveTrades)
    {
        Console.WriteLine();
        Console.WriteLine("Active Trades: CHECKED");
        Console.WriteLine($"Open Pos.    : {summary.OpenPositions}");
        Console.WriteLine($"Pending Ord. : {summary.PendingOrders}");
        Console.WriteLine($"Total Active : {summary.TotalActiveTrades}");
        Console.WriteLine($"Max Allowed  : {maxActiveTrades}");
    }

    internal static void PrintBrokerOrderStatus(ActiveTradeSummary summary, int maxActiveTrades)
    {
        Console.WriteLine();
        Console.WriteLine("Broker Orders: CHECKED");
        Console.WriteLine($"Open Pos.    : {summary.OpenPositions}");
        Console.WriteLine($"Pending Ord. : {summary.PendingOrders}");
        Console.WriteLine($"Total Active : {summary.TotalActiveTrades}");
        Console.WriteLine($"Max Allowed  : {maxActiveTrades}");
    }

    internal static void PrintOrderCreated(BrokerOrderCreationResult orderResult, RiskDecision riskDecision)
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

    internal static void ClearConsoleFully()
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

    internal static async Task<CapturedConsoleOutput> CaptureConsoleOutputAsync(Func<Task<int>> action)
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

    internal static void WriteCapturedOutput(CapturedConsoleOutput captured)
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

    internal static void PrintFailure(string area, string error)
    {
        Console.WriteLine();
        Console.WriteLine($"{area} Status: FAILED");
        Console.WriteLine($"Reason       : {error}");
    }

    internal static void PrintTokenRecoveryHint(string error)
    {
        if (!error.Contains("Access denied", StringComparison.OrdinalIgnoreCase)
            && !error.Contains("access token", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Console.WriteLine("Action       : Run 'dotnet run --project TradingBot.CLI -- ctrader-connect' to re-authorize and save a fresh token pair.");
    }

    internal static void PrintAuthorizationAlreadyRequested()
    {
        Console.WriteLine();
        Console.WriteLine("Broker Token Status: AUTHORIZATION REQUIRED");
        Console.WriteLine("Reason       : cTrader token refresh is still failing.");
        Console.WriteLine("Action       : Authorization was already requested for this start session. Run 'dotnet run --project TradingBot.CLI -- ctrader-connect' once, then restart 'start'.");
    }

    internal static void PrintNextRun(int intervalSeconds)
    {
        var nextRun = DateTimeOffset.Now.AddSeconds(intervalSeconds);
        Console.WriteLine();
        Console.WriteLine($"Next Run     : {nextRun:yyyy-MM-dd HH:mm:ss zzz}");
        Console.WriteLine("Press Ctrl+C to stop.");
    }

    internal static object ToDto(TradeSignal signal) => new
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

    internal static void Log(string area, string message) =>
        Console.WriteLine($"{{\"timestamp\":\"{DateTimeOffset.UtcNow:O}\",\"area\":\"{area}\",\"message\":\"{message}\"}}");

}
