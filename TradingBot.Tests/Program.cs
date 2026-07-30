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
    ("Writes CSV journal", WritesCsvJournal),
    ("Resolves active strategy", ResolvesActiveStrategy),
    ("Resolves first enabled backtesting source", ResolvesFirstEnabledBacktestingSource),
    ("Resolves first enabled funded challenge", ResolvesFirstEnabledFundedChallenge),
    ("Serializes appsettings without legacy duplicates", SerializesAppsettingsWithoutLegacyDuplicates),
    ("Uses configured New York kill-zone session times", UsesConfiguredNewYorkKillZoneSessionTimes),
    ("Writes recent setup CSV with row cap", WritesRecentSetupCsvWithRowCap)
    ,("Detects H1 high sweep M1 sell FVG strategy", DetectsH1HighSweepM1SellFvgStrategy)
    ,("Places H1 sell entry at five percent FVG lower boundary", PlacesH1SellEntryAtFivePercentFvgLowerBoundary)
    ,("Detects H1 low sweep M1 buy FVG strategy", DetectsH1LowSweepM1BuyFvgStrategy)
    ,("Detects V2 SMC liquidity sweep CHOCH buy strategy", DetectsV2SmcLiquiditySweepChochBuyStrategy)
    ,("Places V2 buy entry at five percent FVG upper boundary", PlacesV2BuyEntryAtFivePercentFvgUpperBoundary)
    ,("Rejects V2 order block fallback when disabled", RejectsV2OrderBlockFallbackWhenDisabled)
    ,("Keeps V2 setup id stable across repeated analysis", KeepsV2SetupIdStableAcrossRepeatedAnalysis)
    ,("Allows V2 setup when displacement requirement is disabled", AllowsV2SetupWhenDisplacementRequirementIsDisabled)
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
    Assert(metrics.TotalValidSetups == 0, "Expected default valid setup count.");
    Assert(metrics.WinRate == 66.67m, "Unexpected win rate.");
    Assert(metrics.ProfitFactor == 5.00m, "Unexpected profit factor.");

    var metricsWithSetups = BacktestingEngine.CalculateMetrics(trades, validSetups: 5);
    Assert(metricsWithSetups.TotalValidSetups == 5, "Expected valid setup count.");
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

static void ResolvesActiveStrategy()
{
    var options = new TradingBotOptions
    {
        Strategies = new StrategySelectionOptions
        {
            ActiveStrategyId = "second",
            Items =
            [
                new StrategyDefinitionOptions { Id = "first", Enabled = true, Symbol = "GBPUSD" },
                new StrategyDefinitionOptions { Id = "second", Enabled = true, Symbol = "EURUSD", UseDailyBiasFilter = false }
            ]
        }
    };

    options.Normalize();
    Assert(options.ActiveStrategy.Id == "second", "Expected configured active strategy.");
    Assert(options.Symbol == "EURUSD", "Expected strategy symbol to be applied.");
    Assert(!options.UseDailyBiasFilter, "Expected strategy daily-bias setting to be applied.");
}

static void ResolvesFirstEnabledBacktestingSource()
{
    var options = new TradingBotOptions
    {
        Backtesting = new BacktestingOptions
        {
            DataSources =
            [
                new BacktestingDataSourceOptions { Name = "cTrader", DataSource = "cTrader", Enabled = false },
                new BacktestingDataSourceOptions { Name = "MT5", DataSource = "MT5", Enabled = true, CacheDirectory = "data/mt5" }
            ]
        }
    };

    options.Normalize();
    Assert(options.ActiveBacktestingDataSource.DataSource == "MT5", "Expected first enabled backtesting source.");
    Assert(options.ActiveBacktestingDataSource.DataSource == "MT5", "Expected active backtesting source to be normalized.");
    Assert(options.Backtesting.CacheDirectory == "data/mt5", "Expected active cache directory to be applied.");
}

static void ResolvesFirstEnabledFundedChallenge()
{
    var options = new TradingBotOptions
    {
        FundedAccountChallenges = new FundedAccountChallengesOptions
        {
            Items =
            [
                new FundedAccountChallengeOptions { Name = "Disabled", Enabled = false, InitialBalance = 50000m },
                new FundedAccountChallengeOptions { Name = "Enabled", Enabled = true, InitialBalance = 200000m }
            ],
            FTMO = new FundedAccountChallengeOptions { Name = "FTMO", Enabled = true, InitialBalance = 100000m }
        }
    };

    options.Normalize();
    Assert(options.ActiveFundedAccountChallenge?.Name == "Enabled", "Expected first enabled funded challenge.");
    Assert(options.ActiveFundedAccountChallenge?.InitialBalance == 200000m, "Expected active funded challenge to be applied to risk rules.");
}

static void SerializesAppsettingsWithoutLegacyDuplicates()
{
    var options = new TradingBotOptions();
    options.Normalize();

    var json = System.Text.Json.JsonSerializer.Serialize(options, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

    using var document = System.Text.Json.JsonDocument.Parse(json);
    var root = document.RootElement;
    Assert(!root.TryGetProperty("Symbol", out _), "Expected root Symbol to be owned by Strategies.");
    Assert(!root.TryGetProperty("MacroBiasTimeframe", out _), "Expected strategy timeframe settings to be owned by Strategies.");
    Assert(!root.TryGetProperty("MinRiskReward", out _), "Expected risk/reward strategy settings to be owned by Strategies.");
    Assert(!root.TryGetProperty("AllowedSessions", out _), "Expected session settings to be owned by TradingSessions.");
    Assert(!root.TryGetProperty("FTMOChallenge", out _), "Expected challenge settings to be owned by FundedAccountChallenges.");
    Assert(root.GetProperty("Backtesting").TryGetProperty("DataSources", out _), "Expected backtesting sources to be configured as a list.");
    Assert(!root.GetProperty("Backtesting").TryGetProperty("DataSource", out _), "Expected legacy backtesting source field to be hidden.");
}

static void UsesConfiguredNewYorkKillZoneSessionTimes()
{
    var options = new TradingBotOptions
    {
        TradingSessions = new TradingSessionOptions
        {
            LondonKillZoneNYTime = "04:00-05:00",
            NewYorkKillZoneNYTime = "09:30-10:30",
            LondonNewYorkOverlapNYTime = "11:00-12:00"
        }
    };

    options.Normalize();
    var clock = new NewYorkSessionClock(options);

    var london = clock.GetCurrentSession(DateTimeOffset.Parse("2026-07-06T08:30:00+00:00"));
    var closed = clock.GetCurrentSession(DateTimeOffset.Parse("2026-07-06T07:30:00+00:00"));

    Assert(london == SessionName.London, "Expected configured London kill-zone session.");
    Assert(closed == SessionName.Closed, "Expected old hardcoded London time to be closed after config change.");
}

static void WritesRecentSetupCsvWithRowCap()
{
    var directory = Path.Combine(Path.GetTempPath(), "TradingBot.Tests", Guid.NewGuid().ToString("N"));
    var options = new TradingBotOptions { ReportsDirectory = directory };
    var writer = new RecentValidSetupCsvWriter(options);
    var rows = Enumerable.Range(0, RecentValidSetupCsvWriter.MaxRows + 1)
        .Select(index => new RecentValidSetupRow(
            "strategy",
            "Strategy",
            "EURUSD",
            DateTimeOffset.UtcNow.AddMinutes(-index),
            DateTimeOffset.Now.AddMinutes(-index),
            index % 2 == 0 ? SessionName.LondonNewYorkOverlap.ToString() : SessionName.Closed.ToString(),
            index % 2 == 0 ? "BUY" : "SELL",
            index % 2 == 0,
            1.1m,
            1.0m,
            1.3m,
            2m,
            "Valid setup",
            "MT5"));

    var path = writer.WriteAsync(rows, CancellationToken.None).GetAwaiter().GetResult();
    var lines = File.ReadAllLines(path);

    Assert(lines.Length == RecentValidSetupCsvWriter.MaxRows + 1, "Expected recent setup CSV to cap data rows at 2000 plus header.");
    Assert(lines[0].Contains("IsIntoKillZone"), "Expected recent setup CSV header.");
    Assert(lines.Any(line => line.Contains("\"YES\"")), "Expected kill-zone YES value.");
    Assert(lines.Any(line => line.Contains("\"NO\"")), "Expected kill-zone NO value.");
}

static void DetectsH1HighSweepM1SellFvgStrategy()
{
    var now = new DateTimeOffset(2026, 5, 26, 11, 10, 0, TimeSpan.Zero);
    var h1 = new[]
    {
        new Candle("EURUSD", Timeframe.H1, now.AddHours(-1), 1.0950m, 1.1000m, 1.0940m, 1.0980m, 100)
    };
    var m1 = new[]
    {
        M1(now, -9, 1.0988m, 1.0990m, 1.0980m, 1.0986m),
        M1(now, -8, 1.0986m, 1.1005m, 1.0982m, 1.1002m),
        M1(now, -7, 1.1002m, 1.1004m, 1.0994m, 1.0998m),
        M1(now, -6, 1.0998m, 1.0999m, 1.0976m, 1.0978m),
        M1(now, -5, 1.0978m, 1.0980m, 1.0972m, 1.0975m),
        M1(now, -4, 1.0975m, 1.0975m, 1.0970m, 1.0972m),
        M1(now, -3, 1.0972m, 1.0974m, 1.0968m, 1.0970m),
        M1(now, -2, 1.0970m, 1.0972m, 1.0966m, 1.0968m),
        M1(now, -1, 1.0968m, 1.0970m, 1.0964m, 1.0966m)
    };

    var strategy = new HourlySweepM1FvgStrategyEngine(
        new TestMarketDataProvider(new Dictionary<Timeframe, IReadOnlyList<Candle>>
        {
            [Timeframe.H1] = h1,
            [Timeframe.M1] = m1
        }),
        new TestNewsFilter(),
        new TestSessionClock(),
        new TradingBotOptions { TradeOutKillZoneTime = true, MinFvgSizePips = 0m });

    var signal = strategy.AnalyzeAsync("EURUSD", now, CancellationToken.None).GetAwaiter().GetResult();

    Assert(signal.IsValidSetup, "Expected valid sell setup.");
    Assert(signal.Direction == TradeDirection.Sell, "Expected sell direction.");
    Assert(signal.FairValueGap is not null, "Expected sell FVG.");
    Assert(signal.EntryPrice == signal.FairValueGap!.Midpoint, "Expected FVG midpoint entry.");
}

static void PlacesH1SellEntryAtFivePercentFvgLowerBoundary()
{
    var now = new DateTimeOffset(2026, 5, 26, 11, 10, 0, TimeSpan.Zero);
    var h1 = new[]
    {
        new Candle("EURUSD", Timeframe.H1, now.AddHours(-1), 1.0950m, 1.1000m, 1.0940m, 1.0980m, 100)
    };
    var m1 = new[]
    {
        M1(now, -9, 1.0988m, 1.0990m, 1.0980m, 1.0986m),
        M1(now, -8, 1.0986m, 1.1005m, 1.0982m, 1.1002m),
        M1(now, -7, 1.1002m, 1.1004m, 1.0994m, 1.0998m),
        M1(now, -6, 1.0998m, 1.0999m, 1.0976m, 1.0978m),
        M1(now, -5, 1.0978m, 1.0980m, 1.0972m, 1.0975m),
        M1(now, -4, 1.0975m, 1.0975m, 1.0970m, 1.0972m),
        M1(now, -3, 1.0972m, 1.0974m, 1.0968m, 1.0970m),
        M1(now, -2, 1.0970m, 1.0972m, 1.0966m, 1.0968m),
        M1(now, -1, 1.0968m, 1.0970m, 1.0964m, 1.0966m)
    };

    var strategy = new HourlySweepM1FvgStrategyEngine(
        new TestMarketDataProvider(new Dictionary<Timeframe, IReadOnlyList<Candle>>
        {
            [Timeframe.H1] = h1,
            [Timeframe.M1] = m1
        }),
        new TestNewsFilter(),
        new TestSessionClock(),
        new TradingBotOptions
        {
            TradeOutKillZoneTime = true,
            MinFvgSizePips = 0m,
            FvgEntryMode = "FivePercentBoundary",
            FVGPercentBoundary = 5m
        });

    var signal = strategy.AnalyzeAsync("EURUSD", now, CancellationToken.None).GetAwaiter().GetResult();

    Assert(signal.IsValidSetup, "Expected valid sell setup.");
    Assert(signal.Direction == TradeDirection.Sell, "Expected sell direction.");
    Assert(signal.FairValueGap is not null, "Expected sell FVG.");
    var expectedEntry = signal.FairValueGap!.LowerPrice + (signal.FairValueGap.Size * 0.05m);
    Assert(signal.EntryPrice == expectedEntry, "Expected sell entry at FVG lower + 5%.");
}

static void DetectsH1LowSweepM1BuyFvgStrategy()
{
    var now = new DateTimeOffset(2026, 5, 26, 11, 10, 0, TimeSpan.Zero);
    var h1 = new[]
    {
        new Candle("EURUSD", Timeframe.H1, now.AddHours(-1), 1.1050m, 1.1060m, 1.1000m, 1.1020m, 100)
    };
    var m1 = new[]
    {
        M1(now, -9, 1.1012m, 1.1016m, 1.1008m, 1.1014m),
        M1(now, -8, 1.1014m, 1.1015m, 1.0995m, 1.0998m),
        M1(now, -7, 1.0998m, 1.1006m, 1.0996m, 1.1002m),
        M1(now, -6, 1.1002m, 1.1018m, 1.1001m, 1.1017m),
        M1(now, -5, 1.1017m, 1.1020m, 1.1015m, 1.1019m),
        M1(now, -4, 1.1019m, 1.1024m, 1.1020m, 1.1022m),
        M1(now, -3, 1.1022m, 1.1030m, 1.1025m, 1.1028m),
        M1(now, -2, 1.1028m, 1.1032m, 1.1024m, 1.1030m),
        M1(now, -1, 1.1030m, 1.1034m, 1.1028m, 1.1032m)
    };

    var strategy = new HourlySweepM1FvgStrategyEngine(
        new TestMarketDataProvider(new Dictionary<Timeframe, IReadOnlyList<Candle>>
        {
            [Timeframe.H1] = h1,
            [Timeframe.M1] = m1
        }),
        new TestNewsFilter(),
        new TestSessionClock(),
        new TradingBotOptions { TradeOutKillZoneTime = true, MinFvgSizePips = 0m });

    var signal = strategy.AnalyzeAsync("EURUSD", now, CancellationToken.None).GetAwaiter().GetResult();

    Assert(signal.IsValidSetup, "Expected valid buy setup.");
    Assert(signal.Direction == TradeDirection.Buy, "Expected buy direction.");
    Assert(signal.FairValueGap is not null, "Expected buy FVG.");
    Assert(signal.EntryPrice == signal.FairValueGap!.Midpoint, "Expected FVG midpoint entry.");
}

static void DetectsV2SmcLiquiditySweepChochBuyStrategy()
{
    var now = new DateTimeOffset(2026, 5, 26, 11, 10, 0, TimeSpan.Zero);
    var signal = AnalyzeV2BuyFixture(now);

    Assert(signal.IsValidSetup, $"Expected valid V2 setup. Reason: {signal.SetupReason}");
    Assert(signal.Direction == TradeDirection.Buy, "Expected V2 buy direction.");
    Assert(signal.RiskReward == 2m, "Expected V2 setup to use configured 1/2 risk/reward.");
    Assert(!string.IsNullOrWhiteSpace(signal.SetupId), "Expected stable V2 setup id.");
}

static void PlacesV2BuyEntryAtFivePercentFvgUpperBoundary()
{
    var signal = AnalyzeV2BuyFixture(
        new DateTimeOffset(2026, 5, 26, 11, 10, 0, TimeSpan.Zero),
        fvgEntryMode: "FivePercentBoundary",
        fvgPercentBoundary: 5m);

    Assert(signal.IsValidSetup, $"Expected valid V2 setup. Reason: {signal.SetupReason}");
    Assert(signal.Direction == TradeDirection.Buy, "Expected V2 buy direction.");
    Assert(signal.FairValueGap is not null, "Expected V2 buy FVG.");
    var expectedEntry = signal.FairValueGap!.UpperPrice - (signal.FairValueGap.Size * 0.05m);
    Assert(signal.EntryPrice == expectedEntry, "Expected buy entry at FVG upper - 5%.");
}

static void RejectsV2OrderBlockFallbackWhenDisabled()
{
    var now = new DateTimeOffset(2026, 7, 15, 13, 59, 0, TimeSpan.Zero);
    var d1 = new[]
    {
        new Candle("EURUSD", Timeframe.D1, now.AddDays(-3), 1.1300m, 1.1380m, 1.1280m, 1.1360m, 100),
        new Candle("EURUSD", Timeframe.D1, now.AddDays(-2), 1.1360m, 1.1410m, 1.1340m, 1.1390m, 100),
        new Candle("EURUSD", Timeframe.D1, now.AddDays(-1), 1.1390m, 1.1460m, 1.1370m, 1.1440m, 100)
    };
    var h1 = new[]
    {
        new Candle("EURUSD", Timeframe.H1, now.AddHours(-3), 1.1360m, 1.1400m, 1.1350m, 1.1390m, 100),
        new Candle("EURUSD", Timeframe.H1, now.AddHours(-2), 1.1390m, 1.1430m, 1.1380m, 1.1420m, 100),
        new Candle("EURUSD", Timeframe.H1, now.AddHours(-1), 1.1420m, 1.1460m, 1.1410m, 1.1450m, 100)
    };
    var m5 = new[]
    {
        M5(now, -55, 1.1420m, 1.1430m, 1.1410m, 1.1425m),
        M5(now, -50, 1.1425m, 1.1440m, 1.1415m, 1.1435m),
        M5(now, -45, 1.1435m, 1.1445m, 1.1412m, 1.1420m),
        M5(now, -40, 1.1420m, 1.1450m, 1.1400m, 1.1440m),
        M5(now, -35, 1.1440m, 1.1460m, 1.1430m, 1.1450m),
        M5(now, -30, 1.1450m, 1.1470m, 1.1440m, 1.1460m),
        M5(now, -25, 1.1460m, 1.1480m, 1.1450m, 1.1470m)
    };
    var m1 = new[]
    {
        M1(now, -16, 1.1420m, 1.1430m, 1.1415m, 1.1425m),
        M1(now, -15, 1.1425m, 1.1435m, 1.1418m, 1.1430m),
        M1(now, -14, 1.1430m, 1.1438m, 1.1420m, 1.1434m),
        M1(now, -13, 1.1434m, 1.1440m, 1.1410m, 1.1430m),
        M1(now, -12, 1.1430m, 1.1445m, 1.1420m, 1.1440m),
        M1(now, -11, 1.1440m, 1.1450m, 1.1435m, 1.1448m),
        M1(now, -10, 1.1448m, 1.1460m, 1.1440m, 1.1455m),
        M1(now, -9, 1.1455m, 1.1470m, 1.1450m, 1.1465m),
        M1(now, -8, 1.1465m, 1.1480m, 1.1460m, 1.1475m)
    };

    var strategy = new SmcLiquiditySweepChochStrategyEngine(
        new TestMarketDataProvider(new Dictionary<Timeframe, IReadOnlyList<Candle>>
        {
            [Timeframe.D1] = d1,
            [Timeframe.H1] = h1,
            [Timeframe.M5] = m5,
            [Timeframe.M1] = m1
        }),
        new TestNewsFilter(),
        new TestSessionClock(),
        new TradingBotOptions
        {
            TradeOutKillZoneTime = true,
            LiquiditySweepLookbackCandles = 3,
            SetupLookbackCandlesM5 = 20,
            MinRiskReward = 2m,
            PreferredRiskReward = 2m,
            RequireDisplacement = false,
            AllowOrderBlockEntry = false
        });

    var signal = strategy.AnalyzeAsync("EURUSD", now, CancellationToken.None).GetAwaiter().GetResult();

    Assert(!signal.IsValidSetup, "Expected V2 setup to reject order block fallback when disabled.");
}

static void KeepsV2SetupIdStableAcrossRepeatedAnalysis()
{
    var first = AnalyzeV2BuyFixture(new DateTimeOffset(2026, 5, 26, 11, 10, 0, TimeSpan.Zero));
    var second = AnalyzeV2BuyFixture(new DateTimeOffset(2026, 5, 26, 11, 15, 0, TimeSpan.Zero));

    Assert(first.IsValidSetup && second.IsValidSetup, "Expected repeated V2 analysis to find the same setup.");
    Assert(first.SetupId == second.SetupId, "Expected repeated V2 analysis to reuse the same setup id.");
}

static void AllowsV2SetupWhenDisplacementRequirementIsDisabled()
{
    var signal = AnalyzeV2BuyFixture(
        new DateTimeOffset(2026, 5, 26, 11, 10, 0, TimeSpan.Zero),
        requireDisplacement: false,
        displacementAtrMultiplier: 100m);

    Assert(signal.IsValidSetup, $"Expected V2 setup when displacement is disabled. Reason: {signal.SetupReason}");
}

static TradeSignal AnalyzeV2BuyFixture(
    DateTimeOffset analysisTime,
    bool requireDisplacement = true,
    decimal displacementAtrMultiplier = 1.0m,
    string fvgEntryMode = "Midpoint",
    decimal fvgPercentBoundary = 5m)
{
    var now = new DateTimeOffset(2026, 5, 26, 11, 10, 0, TimeSpan.Zero);
    var d1 = new[]
    {
        new Candle("EURUSD", Timeframe.D1, now.AddDays(-3), 1.0800m, 1.0900m, 1.0750m, 1.0850m, 100),
        new Candle("EURUSD", Timeframe.D1, now.AddDays(-2), 1.0850m, 1.0950m, 1.0820m, 1.0910m, 100),
        new Candle("EURUSD", Timeframe.D1, now.AddDays(-1), 1.0910m, 1.1040m, 1.0880m, 1.0990m, 100)
    };
    var h1 = new[]
    {
        new Candle("EURUSD", Timeframe.H1, now.AddHours(-3), 1.1000m, 1.1030m, 1.0980m, 1.1010m, 100),
        new Candle("EURUSD", Timeframe.H1, now.AddHours(-2), 1.1010m, 1.1050m, 1.1000m, 1.1030m, 100),
        new Candle("EURUSD", Timeframe.H1, now.AddHours(-1), 1.1030m, 1.1090m, 1.1020m, 1.1070m, 100)
    };
    var m5 = new[]
    {
        M5(now, -55, 1.1010m, 1.1040m, 1.1005m, 1.1030m),
        M5(now, -50, 1.1030m, 1.1050m, 1.1010m, 1.1040m),
        M5(now, -45, 1.1040m, 1.1120m, 1.1015m, 1.1050m),
        M5(now, -40, 1.1050m, 1.1055m, 1.1012m, 1.1020m),
        M5(now, -35, 1.1020m, 1.1035m, 1.1018m, 1.1028m),
        M5(now, -30, 1.1028m, 1.1030m, 1.0995m, 1.1012m),
        M5(now, -25, 1.1012m, 1.1140m, 1.1006m, 1.1130m),
        M5(now, -20, 1.1130m, 1.1140m, 1.1025m, 1.1135m),
        M5(now, -15, 1.1135m, 1.1148m, 1.1080m, 1.1142m)
    };
    var m1 = new[]
    {
        M1(now, -34, 1.1012m, 1.1018m, 1.1008m, 1.1010m),
        M1(now, -33, 1.1010m, 1.1015m, 1.1005m, 1.1012m),
        M1(now, -32, 1.1012m, 1.1016m, 1.1009m, 1.1014m),
        M1(now, -31, 1.1014m, 1.1018m, 1.1007m, 1.1015m),
        M1(now, -30, 1.1015m, 1.1017m, 1.0995m, 1.1012m),
        M1(now, -29, 1.1012m, 1.1022m, 1.1009m, 1.1020m),
        M1(now, -28, 1.1020m, 1.1028m, 1.1018m, 1.1025m),
        M1(now, -27, 1.1025m, 1.1032m, 1.1022m, 1.1029m),
        M1(now, -26, 1.1029m, 1.1035m, 1.1026m, 1.1032m),
        M1(now, -25, 1.1032m, 1.1085m, 1.1030m, 1.1080m),
        M1(now, -24, 1.1080m, 1.1092m, 1.1083m, 1.1088m),
        M1(now, -23, 1.1088m, 1.1100m, 1.1090m, 1.1095m),
        M1(now, -22, 1.1095m, 1.1110m, 1.1102m, 1.1106m),
        M1(now, -21, 1.1106m, 1.1120m, 1.1112m, 1.1117m),
        M1(now, -20, 1.1117m, 1.1130m, 1.1120m, 1.1125m),
        M1(now, -19, 1.1125m, 1.1134m, 1.1122m, 1.1130m)
    };

    var strategy = new SmcLiquiditySweepChochStrategyEngine(
        new TestMarketDataProvider(new Dictionary<Timeframe, IReadOnlyList<Candle>>
        {
            [Timeframe.D1] = d1,
            [Timeframe.H1] = h1,
            [Timeframe.M5] = m5,
            [Timeframe.M1] = m1
        }),
        new TestNewsFilter(),
        new TestSessionClock(),
        new TradingBotOptions
        {
            TradeOutKillZoneTime = true,
            LiquiditySweepLookbackCandles = 4,
            SetupLookbackCandlesM5 = 20,
            MinRiskReward = 2m,
            PreferredRiskReward = 2m,
            DisplacementMinBodyToRangeRatio = 0.60m,
            DisplacementAtrMultiplier = displacementAtrMultiplier,
            RequireDisplacement = requireDisplacement,
            UsePremiumDiscountFilter = true,
            FvgEntryMode = fvgEntryMode,
            FVGPercentBoundary = fvgPercentBoundary
        });

    return strategy.AnalyzeAsync("EURUSD", analysisTime, CancellationToken.None).GetAwaiter().GetResult();
}

static Candle M1(DateTimeOffset now, int minutesOffset, decimal open, decimal high, decimal low, decimal close) =>
    new("EURUSD", Timeframe.M1, now.AddMinutes(minutesOffset), open, high, low, close, 100);

static Candle M5(DateTimeOffset now, int minutesOffset, decimal open, decimal high, decimal low, decimal close) =>
    new("EURUSD", Timeframe.M5, now.AddMinutes(minutesOffset), open, high, low, close, 100);

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed class TestMarketDataProvider(IReadOnlyDictionary<Timeframe, IReadOnlyList<Candle>> candles) : IMarketDataProvider
{
    public Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        if (!candles.TryGetValue(timeframe, out var source))
        {
            return Task.FromResult<IReadOnlyList<Candle>>([]);
        }

        var filtered = source
            .Where(candle => string.Equals(candle.Symbol, symbol, StringComparison.OrdinalIgnoreCase)
                && candle.OpenedAt >= from
                && candle.OpenedAt <= to)
            .OrderBy(candle => candle.OpenedAt)
            .ToArray();
        return Task.FromResult<IReadOnlyList<Candle>>(filtered);
    }
}

internal sealed class TestNewsFilter : INewsFilter
{
    public bool IsBlocked(DateTimeOffset timestamp, string symbol, out string reason)
    {
        reason = "";
        return false;
    }
}

internal sealed class TestSessionClock : ISessionClock
{
    public SessionName GetCurrentSession(DateTimeOffset timestamp) => SessionName.London;
}
