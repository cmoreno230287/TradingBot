using TradingBot.Application;
using TradingBot.Backtesting;
using TradingBot.Domain;
using TradingBot.Infrastructure.Filters;
using TradingBot.Infrastructure.MarketData;
using TradingBot.Infrastructure.Sessions;
using TradingBot.Reporting;
using TradingBot.Strategies;

var tests = new (string Name, Action Test)[]
{
    ("Detects bullish FVG", DetectsBullishFvg),
    ("Rejects excessive risk", RejectsExcessiveRisk),
    ("Calculates backtest metrics", CalculatesBacktestMetrics),
    ("Writes CSV journal", WritesCsvJournal)
};

foreach (var test in tests)
{
    test.Test();
    Console.WriteLine($"PASS {test.Name}");
}

static void DetectsBullishFvg()
{
    var candles = new[]
    {
        new Candle("EURUSD", Timeframe.M5, DateTimeOffset.UtcNow.AddMinutes(-10), 1.1000m, 1.1010m, 1.0990m, 1.1005m, 100),
        new Candle("EURUSD", Timeframe.M5, DateTimeOffset.UtcNow.AddMinutes(-5), 1.1005m, 1.1030m, 1.1003m, 1.1028m, 100),
        new Candle("EURUSD", Timeframe.M5, DateTimeOffset.UtcNow, 1.1028m, 1.1040m, 1.1015m, 1.1035m, 100)
    };

    var fvg = SmartMoneyStrategyEngine.DetectFairValueGap(candles, TradeDirection.Buy);
    Assert(fvg is not null, "Expected bullish FVG.");
    Assert(fvg!.LowerPrice == 1.1010m, "Unexpected lower FVG boundary.");
    Assert(fvg.UpperPrice == 1.1015m, "Unexpected upper FVG boundary.");
}

static void RejectsExcessiveRisk()
{
    var options = new TradingBotOptions();
    var riskManager = new RiskManager(options);
    var request = new OrderRequest("EURUSD", TradeDirection.Buy, OrderType.Limit, 1.1000m, 1.0990m, 1.1030m, 0m, 2.0m);
    var decision = riskManager.Evaluate(request, new AccountSnapshot(10_000m, 10_000m, 0m, 0m, 0, 0));
    Assert(!decision.IsAllowed, "Expected risk rejection.");
}

static void CalculatesBacktestMetrics()
{
    var trades = new[]
    {
        new TradeJournalEntry { ProfitLossAmount = 100m, RiskRewardRatio = 2m },
        new TradeJournalEntry { ProfitLossAmount = -50m, RiskRewardRatio = 2m },
        new TradeJournalEntry { ProfitLossAmount = 150m, RiskRewardRatio = 3m }
    };

    var metrics = BacktestingEngine.CalculateMetrics(trades);
    Assert(metrics.TotalTrades == 3, "Expected three trades.");
    Assert(metrics.WinRate == 66.67m, "Unexpected win rate.");
    Assert(metrics.ProfitFactor == 5.00m, "Unexpected profit factor.");
}

static void WritesCsvJournal()
{
    var options = new TradingBotOptions { ReportsDirectory = Path.Combine(Path.GetTempPath(), "TradingBot.Tests") };
    var writer = new CsvJournalWriter(options);
    var path = writer.WriteTradesAsync(
        new[] { new TradeJournalEntry { Symbol = "EURUSD", Direction = TradeDirection.Buy, EntryPrice = 1.1000m } },
        "test-journal",
        CancellationToken.None).GetAwaiter().GetResult();

    Assert(File.Exists(path), "Expected CSV file.");
    var csv = File.ReadAllText(path);
    Assert(csv.Contains("StrategyId,Symbol,Session"), "Expected CSV header.");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
