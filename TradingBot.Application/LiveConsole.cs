using TradingBot.Domain;

namespace TradingBot.Application;

public static class LiveConsole
{
    private static readonly object Sync = new();
    private static LiveProtectionSnapshot? latestProtection;
    private static TimeSpan latestLatency;
    private static (int Fetched, int Written, string Directory)? latestTracking;

    public static void ClearBeforeAnalysis()
    {
        if (Console.IsOutputRedirected) return;
        try { Console.Write("\u001b[2J\u001b[3J\u001b[H"); }
        catch { try { Console.Clear(); } catch { } }
    }

    public static void WriteProtection(LiveProtectionSnapshot snapshot, TimeSpan latency)
    {
        lock (Sync) { latestProtection = snapshot; latestLatency = latency; }
    }

    public static void WriteTracking(int fetched, int written, string directory) { lock (Sync) latestTracking = (fetched, written, directory); }

    private static void RenderTracking() { var t = latestTracking; if (t is null) { Console.WriteLine("Trade Tracking: WAITING"); return; } Console.WriteLine(); Console.WriteLine("Trade Tracking: CHECKED"); Console.WriteLine("Fetched Closed: " + t.Value.Fetched); Console.WriteLine("New CSV Rows  : " + t.Value.Written); Console.WriteLine("Directory     : " + t.Value.Directory); }

    private static void RenderProtection()
    {
        var snapshot = latestProtection;
        if (snapshot is null) { Console.WriteLine("Protection    : STARTING"); return; }
        Console.WriteLine("Protection    : " + (snapshot.Halted ? "HALTED" : "HEALTHY"));
        Console.WriteLine();
        Console.WriteLine("FTMO Protection: " + (snapshot.Halted ? "HALTED" : "HEALTHY"));
        Console.WriteLine("Account Equity : " + snapshot.Account.Equity.ToString("N2") + " " + snapshot.Account.Currency);
        Console.WriteLine("Active Trades  : " + snapshot.Account.ActiveTrades);
        Console.WriteLine("Daily Start    : " + snapshot.Account.DailyStartingBalance.ToString("N2"));
        Console.WriteLine("Existing Risk  : " + snapshot.Account.ExistingRisk.ToString("N2"));
        Console.WriteLine("Trades Today   : " + snapshot.Account.TradesToday);
        Console.WriteLine("Latency        : " + latestLatency.TotalMilliseconds.ToString("N0") + " ms");
        if (!string.IsNullOrWhiteSpace(snapshot.Reason)) Console.WriteLine("Reason         : " + snapshot.Reason);
    }

    public static void WriteAnalysis(TradingBotOptions options, TradeSignal signal, LiveProtectionSnapshot? protection)
    {
        lock (Sync)
        {
            Console.WriteLine(); Console.WriteLine("============================================================");
            Console.WriteLine(" TradingBot Analysis Cycle - " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz")); Console.WriteLine("============================================================");
            Console.WriteLine("Broker        : " + options.Broker); Console.WriteLine("Strategy      : " + options.ActiveStrategy.Name); Console.WriteLine("Engine        : " + options.ActiveStrategy.Engine); Console.WriteLine("Symbol        : " + signal.Symbol); Console.WriteLine("Session       : " + signal.Session);
            RenderProtection();
            Console.WriteLine(); Console.WriteLine("Setup Status  : " + (signal.IsValidSetup ? "VALID" : "INVALID")); Console.WriteLine("Reason        : " + signal.SetupReason);
            if (signal.IsValidSetup) { Console.WriteLine("Direction     : " + signal.Direction.ToString().ToUpperInvariant()); Console.WriteLine("Entry         : " + signal.EntryPrice); Console.WriteLine("Stop Loss     : " + signal.StopLoss); Console.WriteLine("Take Profit   : " + signal.TakeProfit); Console.WriteLine("Risk/Reward   : " + signal.RiskReward.ToString("N2")); } else Console.WriteLine("Action        : No order created");
            Console.WriteLine("Next Analysis : " + DateTimeOffset.Now.AddSeconds(options.FtmoProtection.EntryAnalysisIntervalSeconds).ToString("yyyy-MM-dd HH:mm:ss zzz"));
        }
    }

    public static void WriteBlocked(TradingBotOptions options, string reason)
    {
        lock (Sync)
        {
            Console.WriteLine(); Console.WriteLine("TradingBot Analysis Cycle - " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            RenderProtection(); RenderTracking(); Console.WriteLine("Setup Status  : BLOCKED"); Console.WriteLine("Reason        : " + reason); Console.WriteLine("Action        : No order created"); Console.WriteLine("Next Analysis : " + DateTimeOffset.Now.AddSeconds(options.FtmoProtection.EntryAnalysisIntervalSeconds).ToString("yyyy-MM-dd HH:mm:ss zzz"));
        }
    }
}


