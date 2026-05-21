using System.Text.Json.Serialization;

namespace TradingBot.Application;

public sealed class TradingBotOptions
{
    public string Symbol { get; set; } = "EURUSD";
    public string Broker { get; set; } = "cTrader";
    public bool LiveTradingEnabled { get; set; }
    public int AnalysisExecutionIntervalSeconds { get; set; } = 60;
    public BacktestingOptions Backtesting { get; set; } = new();
    public FTMOChallengeOptions FTMOChallenge { get; set; } = new();
    public LowRiskRolloutOptions LowRiskRollout { get; set; } = new();
    public BrokerOptions Brokers { get; set; } = new();
    [JsonIgnore]
    public CTraderOptions CTrader
    {
        get => Brokers.CTrader;
        set => Brokers.CTrader = value;
    }

    [JsonIgnore]
    public MT5Options MT5
    {
        get => Brokers.MT5;
        set => Brokers.MT5 = value;
    }

    public TradingViewOptions TradingView { get; set; } = new();
    public string MacroBiasTimeframe { get; set; } = "D1";
    public string BiasTimeframe { get; set; } = "H1";
    public string ExecutionTimeframe { get; set; } = "M5";
    public string EntryTimeframe { get; set; } = "M1";
    public bool UsePreviousDayHighLow { get; set; } = true;
    public bool UsePreviousWeekHighLow { get; set; } = true;
    public bool UseSessionHighLow { get; set; } = true;
    public bool UsePremiumDiscountFilter { get; set; } = true;
    public bool UseLiquidityTargetFilter { get; set; } = true;
    public decimal AccountBalance { get; set; } = 10_000m;
    public int MaxActiveTrades { get; set; } = 1;
    public decimal RiskPercentPerTrade { get; set; } = 0.5m;
    public decimal MaxRiskPercentPerTrade { get; set; } = 1.0m;
    public decimal DailyDrawdownLimitPercent { get; set; } = 3.0m;
    public decimal WeeklyDrawdownLimitPercent { get; set; } = 5.0m;
    public int MaxConsecutiveLosses { get; set; } = 2;
    public int MaxConsecutiveLosingDays { get; set; } = 3;
    public decimal MinRiskReward { get; set; } = 2.0m;
    public decimal PreferredRiskReward { get; set; } = 3.0m;
    public decimal MaxSpreadPips { get; set; } = 1.5m;
    public decimal PipValuePerLot { get; set; } = 10m;
    public decimal PipSize { get; set; } = 0.0001m;
    public int SwingStrength { get; set; } = 2;
    public bool RequireCandleCloseForBos { get; set; } = true;
    public bool RequireMarketStructureShift { get; set; } = true;
    public bool RequireDisplacement { get; set; } = true;
    public decimal DisplacementMinBodyToRangeRatio { get; set; } = 0.60m;
    public decimal DisplacementAtrMultiplier { get; set; } = 1.20m;
    public int BiasSwingStrength { get; set; } = 2;
    public int SetupLookbackCandlesM5 { get; set; } = 96;
    public int LiquiditySweepLookbackCandles { get; set; } = 24;
    public int MaxSetupAgeCandlesM5 { get; set; } = 12;
    public decimal MinFvgSizePips { get; set; } = 1.0m;
    public string FvgEntryMode { get; set; } = "Dynamic";
    public string DefaultFvgEntryMode { get; set; } = "Midpoint";
    public bool AllowBoundaryEntryOnStrongDisplacement { get; set; } = true;
    public bool AllowM1ConfirmationEntry { get; set; } = true;
    public decimal BreakEvenAtRR { get; set; } = 1.0m;
    public bool PartialCloseEnabled { get; set; } = true;
    public decimal PartialClosePercent { get; set; } = 50m;
    public decimal PartialCloseAtRR { get; set; } = 1.0m;
    public bool UseNewsFilter { get; set; } = true;
    public string[] NewsBlackoutWindowsUtc { get; set; } = [];
    public int MinutesBeforeHighImpactNews { get; set; } = 15;
    public int MinutesAfterHighImpactNews { get; set; } = 30;
    public decimal NormalStopLossBufferPips { get; set; } = 2.0m;
    public decimal HighVolatilityStopLossBufferPips { get; set; } = 5.0m;
    public bool UseAtrBasedBuffer { get; set; } = true;
    public int AtrPeriod { get; set; } = 14;
    public decimal AtrBufferMultiplier { get; set; } = 0.10m;
    public bool TradeOutKillZoneTime { get; set; }
    public string[] AllowedSessions { get; set; } = ["London", "NewYork", "LondonNewYorkOverlap"];
    public string LondonKillZoneNYTime { get; set; } = "02:00-05:00";
    public string NewYorkKillZoneNYTime { get; set; } = "08:30-11:00";
    public string LondonNewYorkOverlapNYTime { get; set; } = "08:00-11:00";
    public int PendingOrderExpirationCandlesM5 { get; set; } = 6;
    public int PendingOrderExpirationCandlesM1 { get; set; } = 10;
    public int MinimumBacktestTrades { get; set; } = 200;
    public int RecommendedBacktestTrades { get; set; } = 500;
    public string ReportsDirectory { get; set; } = "reports";
    public OperationalMonitoringOptions OperationalMonitoring { get; set; } = new();
    public TradeTrackingOptions TradeTracking { get; set; } = new();
    public SignalTrackingOptions SignalTracking { get; set; } = new();
}

public sealed class OperationalMonitoringOptions
{
    public bool Enabled { get; set; } = true;
    public string Directory { get; set; } = "reports/operations";
}

public sealed class TradeTrackingOptions
{
    public bool Enabled { get; set; } = true;
    public string Directory { get; set; } = "reports/tracking";
    public int MaxRowsPerFile { get; set; } = 2000;
    public int LookbackDays { get; set; } = 30;
}

public sealed class SignalTrackingOptions
{
    public bool Enabled { get; set; } = true;
    public string Directory { get; set; } = "reports/signals";
    public int MaxRowsPerFile { get; set; } = 2000;
}

public sealed class BrokerOptions
{
    public CTraderOptions CTrader { get; set; } = new();
    public MT5Options MT5 { get; set; } = new();
}

public sealed class CTraderOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RedirectUri { get; set; } = "";
    public string Scope { get; set; } = "accounts";
    public string AuthorizationCode { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public long CtidTraderAccountId { get; set; }
    public long SymbolId { get; set; }
    public string DefaultTradeSide { get; set; } = "BUY";
    public string DefaultOrderType { get; set; } = "LIMIT";
    public int UnitsPerLot { get; set; } = 100000;
    public bool AllowLiveOrderCreation { get; set; }
    public bool IsDemo { get; set; } = true;
    public string TokenEndpoint { get; set; } = "https://openapi.ctrader.com/apps/token";
    public string AuthorizationEndpoint { get; set; } = "https://id.ctrader.com/my/settings/openapi/grantingaccess/";
    public string JsonDemoHost { get; set; } = "demo.ctraderapi.com";
    public string JsonLiveHost { get; set; } = "live.ctraderapi.com";
    public int JsonPort { get; set; } = 5036;
}

public sealed class BacktestingOptions
{
    public string DataSource { get; set; } = "cTrader";
    public bool CacheEnabled { get; set; } = true;
    public string CacheDirectory { get; set; } = "data/historical";
    public int HistoricalDataChunkDaysM1 { get; set; } = 7;
    public int HistoricalDataChunkDaysM5 { get; set; } = 30;
    public int HistoricalDataChunkDaysH1 { get; set; } = 180;
    public int HistoricalDataChunkDaysD1 { get; set; } = 365;
}

public sealed class FTMOChallengeOptions
{
    public bool Enabled { get; set; } = true;
    public decimal InitialBalance { get; set; } = 100000m;
    public int MinimumTradingDays { get; set; } = 2;
    public decimal ProfitTargetAmount { get; set; } = 5000m;
    public decimal MaxDailyLossAmount { get; set; } = 5000m;
    public decimal MaxTotalLossAmount { get; set; } = 10000m;
    public decimal ProfitTargetPercent { get; set; } = 10m;
    public decimal MaxDailyLossPercent { get; set; } = 5m;
    public decimal MaxTotalLossPercent { get; set; } = 10m;
    public decimal DailyLossSafetyBufferAmount { get; set; } = 500m;
    public decimal TotalLossSafetyBufferAmount { get; set; } = 500m;
    public decimal StopTradingAtProfitTargetBufferAmount { get; set; } = 100m;
    public decimal DailyLossSafetyBufferPercent { get; set; } = 0m;
    public decimal TotalLossSafetyBufferPercent { get; set; } = 0m;
    public decimal StopTradingAtProfitTargetBufferPercent { get; set; } = 0m;
}

public sealed class LowRiskRolloutOptions
{
    public bool Enabled { get; set; } = true;
    public decimal RiskPercentPerTrade { get; set; } = 0.25m;
    public decimal MaxRiskPercentPerTrade { get; set; } = 0.5m;
    public int MaxActiveTrades { get; set; } = 1;
}

public sealed class MT5Options
{
    public string BridgeBaseUrl { get; set; } = "http://localhost:5010";
    public long AccountId { get; set; }
    public string Symbol { get; set; } = "EURUSD";
    public long MagicNumber { get; set; } = 20260520;
    public int MaxActiveTrades { get; set; } = 1;
    public int TimeoutSeconds { get; set; } = 10;
    public bool AllowLiveOrderCreation { get; set; }
    public int UnitsPerLot { get; set; } = 100000;
    public int PendingOrderExpirationMinutes { get; set; } = 30;
    public int MaxSlippagePoints { get; set; } = 20;
    public bool CancelStalePendingOrders { get; set; } = true;
}

public sealed class TradingViewOptions
{
    public string Symbol { get; set; } = "FX:EURUSD";
    public string Exchange { get; set; } = "FX";
    public string Interval { get; set; } = "5";
    public string ScreenshotDirectory { get; set; } = "MarketScreenshots";
    public string ScreenshotExtension { get; set; } = "png";
    public string BrowserPath { get; set; } = "";
    public int ScreenshotWidth { get; set; } = 1440;
    public int ScreenshotHeight { get; set; } = 900;
}
