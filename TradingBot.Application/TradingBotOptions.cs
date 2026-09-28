using System.Text.Json.Serialization;

namespace TradingBot.Application;

public sealed class TradingBotOptions
{
    public const string DefaultStrategyId = "EURUSD_Scalping_Forex_V1";

    [JsonIgnore]
    public string Symbol { get; set; } = "EURUSD";
    public string Broker { get; set; } = "cTrader";
    public bool LiveTradingEnabled { get; set; } = true;
    public FtmoProtectionOptions FtmoProtection { get; set; } = new();
    public FtmoPullbackOptions FtmoPullback { get; set; } = new();
    public FtmoSmcOptions FtmoSmc { get; set; } = new();
    public int AnalysisExecutionIntervalSeconds { get; set; } = 60;
    public StrategySelectionOptions Strategies { get; set; } = StrategySelectionOptions.CreateDefault();
    [JsonIgnore]
    public FTMOChallengeOptions FTMOChallenge { get; set; } = new();
    public FundedAccountChallengesOptions FundedAccountChallenges { get; set; } = new();
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

    [JsonIgnore]
    public string MacroBiasTimeframe { get; set; } = "D1";
    [JsonIgnore]
    public string BiasTimeframe { get; set; } = "H1";
    [JsonIgnore]
    public string ExecutionTimeframe { get; set; } = "M5";
    [JsonIgnore]
    public string EntryTimeframe { get; set; } = "M1";
    public bool UsePreviousDayHighLow { get; set; } = true;
    public bool UsePreviousWeekHighLow { get; set; } = true;
    public bool UseSessionHighLow { get; set; } = true;
    [JsonIgnore]
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
    [JsonIgnore]
    public decimal MinRiskReward { get; set; } = 2.0m;
    [JsonIgnore]
    public decimal PreferredRiskReward { get; set; } = 3.0m;
    public decimal MaxSpreadPips { get; set; } = 1.5m;
    public decimal PipValuePerLot { get; set; } = 10m;
    public decimal PipSize { get; set; } = 0.0001m;
    public int SwingStrength { get; set; } = 2;
    public bool RequireCandleCloseForBos { get; set; } = true;
    public bool RequireMarketStructureShift { get; set; } = true;
    [JsonIgnore]
    public bool RequireDisplacement { get; set; } = true;
    [JsonIgnore]
    public decimal DisplacementMinBodyToRangeRatio { get; set; } = 0.60m;
    [JsonIgnore]
    public decimal DisplacementAtrMultiplier { get; set; } = 1.20m;
    [JsonIgnore]
    public bool UseDailyBiasFilter { get; set; } = true;
    [JsonIgnore]
    public int BiasSwingStrength { get; set; } = 2;
    [JsonIgnore]
    public int SetupLookbackCandlesM5 { get; set; } = 96;
    [JsonIgnore]
    public int LiquiditySweepLookbackCandles { get; set; } = 24;
    [JsonIgnore]
    public int MaxSetupAgeCandlesM5 { get; set; } = 12;
    [JsonIgnore]
    public decimal MinFvgSizePips { get; set; } = 1.0m;
    [JsonIgnore]
    public string FvgEntryMode { get; set; } = "Dynamic";
    [JsonIgnore]
    public decimal FVGPercentBoundary { get; set; } = 5m;
    [JsonIgnore]
    public bool AllowOrderBlockEntry { get; set; } = true;
    public string DefaultFvgEntryMode { get; set; } = "Midpoint";
    public bool AllowBoundaryEntryOnStrongDisplacement { get; set; } = true;
    public bool AllowM1ConfirmationEntry { get; set; } = true;
    public decimal BreakEvenAtRR { get; set; } = 1.0m;
    public bool PartialCloseEnabled { get; set; } = true;
    public decimal PartialClosePercent { get; set; } = 50m;
    public decimal PartialCloseAtRR { get; set; } = 1.0m;
    public bool UseNewsFilter { get; set; } = true;
    public NewsCalendarOptions NewsCalendar { get; set; } = new();
    private NewsCalendarSnapshot? liveNewsCalendar;
    [System.Text.Json.Serialization.JsonIgnore]
    public NewsCalendarSnapshot? LiveNewsCalendar
    {
        get => Volatile.Read(ref liveNewsCalendar);
        set => Volatile.Write(ref liveNewsCalendar, value);
    }
    public string[] NewsBlackoutWindowsUtc { get; set; } = [];
    public DateTimeOffset? NewsCoverageFromUtc { get; set; }
    public DateTimeOffset? NewsCoverageUntilUtc { get; set; }
    public string[] BrokerMarketClosuresUtc { get; set; } = [];
    public int MinutesBeforeHighImpactNews { get; set; } = 15;
    public int MinutesAfterHighImpactNews { get; set; } = 30;
    public decimal NormalStopLossBufferPips { get; set; } = 2.0m;
    public decimal HighVolatilityStopLossBufferPips { get; set; } = 5.0m;
    public bool UseAtrBasedBuffer { get; set; } = true;
    public int AtrPeriod { get; set; } = 14;
    public decimal AtrBufferMultiplier { get; set; } = 0.10m;
    [JsonIgnore]
    public bool TradeOutKillZoneTime { get; set; }
    [JsonIgnore]
    public string[] AllowedSessions { get; set; } = ["London", "NewYork", "LondonNewYorkOverlap"];
    [JsonIgnore]
    public string LondonKillZoneNYTime { get; set; } = "02:00-05:00";
    [JsonIgnore]
    public string NewYorkKillZoneNYTime { get; set; } = "08:30-11:00";
    [JsonIgnore]
    public string LondonNewYorkOverlapNYTime { get; set; } = "08:00-11:00";
    public int PendingOrderExpirationCandlesM5 { get; set; } = 6;
    public int PendingOrderExpirationCandlesM1 { get; set; } = 10;
    public string ReportsDirectory { get; set; } = "reports";
    public OperationalMonitoringOptions OperationalMonitoring { get; set; } = new();
    public TradeTrackingOptions TradeTracking { get; set; } = new();
    public DailyTradingStopOptions DailyTradingStop { get; set; } = new();
    public ForexMarketSessionOptions ForexMarketSessions { get; set; } = new();
    public TradingSessionOptions TradingSessions { get; set; } = new();

    [JsonIgnore]
    public StrategyDefinitionOptions ActiveStrategy { get; private set; } = StrategyDefinitionOptions.CreateDefault();


    [JsonIgnore]
    public FundedAccountChallengeOptions? ActiveFundedAccountChallenge { get; private set; }

    public void Normalize()
    {
        ActiveStrategy = Strategies.ResolveActive();
        ApplyStrategy(ActiveStrategy);

        ApplyTradingSessions();


        ActiveFundedAccountChallenge = FundedAccountChallenges.ResolveActive();
        if (ActiveFundedAccountChallenge is not null)
        {
            FTMOChallenge = ActiveFundedAccountChallenge.ToFtmoOptions();
        }
    }

    private void ApplyStrategy(StrategyDefinitionOptions strategy)
    {
        Symbol = string.IsNullOrWhiteSpace(strategy.Symbol) ? Symbol : strategy.Symbol;
        MacroBiasTimeframe = strategy.MacroBiasTimeframe;
        BiasTimeframe = strategy.BiasTimeframe;
        ExecutionTimeframe = strategy.ExecutionTimeframe;
        EntryTimeframe = strategy.EntryTimeframe;
        UseDailyBiasFilter = strategy.UseDailyBiasFilter;
        UsePremiumDiscountFilter = strategy.UsePremiumDiscountFilter;
        MinRiskReward = strategy.MinRiskReward;
        PreferredRiskReward = strategy.PreferredRiskReward;
        BiasSwingStrength = strategy.BiasSwingStrength;
        SetupLookbackCandlesM5 = strategy.SetupLookbackCandlesM5;
        LiquiditySweepLookbackCandles = strategy.LiquiditySweepLookbackCandles;
        MaxSetupAgeCandlesM5 = strategy.MaxSetupAgeCandlesM5;
        MinFvgSizePips = strategy.MinFvgSizePips;
        FvgEntryMode = strategy.FvgEntryMode;
        FVGPercentBoundary = strategy.FVGPercentBoundary;
        AllowOrderBlockEntry = strategy.AllowOrderBlockEntry;
        RequireDisplacement = strategy.RequireDisplacement;
        DisplacementMinBodyToRangeRatio = strategy.DisplacementMinBodyToRangeRatio;
        DisplacementAtrMultiplier = strategy.DisplacementAtrMultiplier;
    }

    private void ApplyTradingSessions()
    {
        AllowedSessions = TradingSessions.AllowedSessions;
        LondonKillZoneNYTime = TradingSessions.LondonKillZoneNYTime;
        NewYorkKillZoneNYTime = TradingSessions.NewYorkKillZoneNYTime;
        LondonNewYorkOverlapNYTime = TradingSessions.LondonNewYorkOverlapNYTime;
        TradeOutKillZoneTime = TradingSessions.TradeOutKillZoneTime;
    }
}

public sealed class StrategySelectionOptions
{
    public string ActiveStrategyId { get; set; } = TradingBotOptions.DefaultStrategyId;
    public List<StrategyDefinitionOptions> Items { get; set; } = [StrategyDefinitionOptions.CreateDefault()];

    public static StrategySelectionOptions CreateDefault() => new();

    public StrategyDefinitionOptions ResolveActive()
    {
        if (Items.Count == 0)
        {
            Items.Add(StrategyDefinitionOptions.CreateDefault());
        }

        var enabled = Items.Where(item => item.Enabled).ToArray();
        if (enabled.Length == 0)
        {
            return Items[0];
        }

        var selected = enabled.FirstOrDefault(item => string.Equals(item.Id, ActiveStrategyId, StringComparison.OrdinalIgnoreCase));
        return selected ?? enabled[0];
    }
}

public sealed class StrategyDefinitionOptions
{
    public string Id { get; set; } = TradingBotOptions.DefaultStrategyId;
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "EURUSD SMC Scalping Forex V1";
    public string Engine { get; set; } = "SmartMoney";
    public string Symbol { get; set; } = "EURUSD";
    public string MacroBiasTimeframe { get; set; } = "D1";
    public string BiasTimeframe { get; set; } = "H1";
    public string ExecutionTimeframe { get; set; } = "M5";
    public string EntryTimeframe { get; set; } = "M1";
    public bool UseDailyBiasFilter { get; set; }
    public bool UsePremiumDiscountFilter { get; set; } = true;
    public decimal MinRiskReward { get; set; } = 2.0m;
    public decimal PreferredRiskReward { get; set; } = 3.0m;
    public int BiasSwingStrength { get; set; } = 2;
    public int SetupLookbackCandlesM5 { get; set; } = 96;
    public int LiquiditySweepLookbackCandles { get; set; } = 24;
    public int MaxSetupAgeCandlesM5 { get; set; } = 12;
    public decimal MinFvgSizePips { get; set; } = 1.0m;
    public string FvgEntryMode { get; set; } = "Dynamic";
    public decimal FVGPercentBoundary { get; set; } = 5m;
    public bool AllowOrderBlockEntry { get; set; } = true;
    public bool RequireDisplacement { get; set; } = true;
    public decimal DisplacementMinBodyToRangeRatio { get; set; } = 0.60m;
    public decimal DisplacementAtrMultiplier { get; set; } = 1.20m;

    public static StrategyDefinitionOptions CreateDefault() => new();
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

public sealed class DailyTradingStopOptions
{
    public bool Enabled { get; set; } = true;
    public int MaxWinningTradesPerDay { get; set; } = 1;
    public int MaxLosingTradesPerDay { get; set; } = 2;
}

public sealed class ForexMarketSessionOptions
{
    public bool Enabled { get; set; } = true;
    public string[] TradingDays { get; set; } = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"];
    public string LondonSessionNYTime { get; set; } = "03:00-12:00";
    public string NewYorkSessionNYTime { get; set; } = "08:00-17:00";
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

public sealed class TradingSessionOptions
{
    public string[] AllowedSessions { get; set; } = ["London", "NewYork", "LondonNewYorkOverlap"];
    public string LondonKillZoneNYTime { get; set; } = "02:00-05:00";
    public string NewYorkKillZoneNYTime { get; set; } = "08:30-11:00";
    public string LondonNewYorkOverlapNYTime { get; set; } = "08:00-11:00";
    public bool TradeOutKillZoneTime { get; set; }
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

public sealed class FundedAccountChallengesOptions
{
    public List<FundedAccountChallengeOptions> Items { get; set; } = [FundedAccountChallengeOptions.CreateFtmoDefault()];
    [JsonIgnore]
    public FundedAccountChallengeOptions FTMO { get; set; } = FundedAccountChallengeOptions.CreateDisabledFtmoDefault();

    public FundedAccountChallengeOptions? ResolveActive()
    {
        var candidates = Items
            .Concat([FTMO])
            .Where(item => item.Enabled)
            .ToArray();

        return candidates.FirstOrDefault();
    }
}

public sealed class FundedAccountChallengeOptions
{
    public string Name { get; set; } = "FTMO";
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

    public static FundedAccountChallengeOptions CreateFtmoDefault() => new();

    public static FundedAccountChallengeOptions CreateDisabledFtmoDefault() => new() { Enabled = false };

    public FTMOChallengeOptions ToFtmoOptions() => new()
    {
        Enabled = Enabled,
        InitialBalance = InitialBalance,
        MinimumTradingDays = MinimumTradingDays,
        ProfitTargetAmount = ProfitTargetAmount,
        MaxDailyLossAmount = MaxDailyLossAmount,
        MaxTotalLossAmount = MaxTotalLossAmount,
        ProfitTargetPercent = ProfitTargetPercent,
        MaxDailyLossPercent = MaxDailyLossPercent,
        MaxTotalLossPercent = MaxTotalLossPercent,
        DailyLossSafetyBufferAmount = DailyLossSafetyBufferAmount,
        TotalLossSafetyBufferAmount = TotalLossSafetyBufferAmount,
        StopTradingAtProfitTargetBufferAmount = StopTradingAtProfitTargetBufferAmount,
        DailyLossSafetyBufferPercent = DailyLossSafetyBufferPercent,
        TotalLossSafetyBufferPercent = TotalLossSafetyBufferPercent,
        StopTradingAtProfitTargetBufferPercent = StopTradingAtProfitTargetBufferPercent
    };
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
    public bool AllowLiveOrderCreation { get; set; } = true;
    public int UnitsPerLot { get; set; } = 100000;
    public int PendingOrderExpirationHours { get; set; } = 12;
    public int MaxSlippagePoints { get; set; } = 20;
    public bool CancelStalePendingOrders { get; set; } = true;
    public decimal MaxEntryDistancePips { get; set; } = 8m;
    public bool CancelPendingOrderWhenSetupInvalid { get; set; } = true;
    public bool CancelPendingOrderWhenDirectionChanges { get; set; } = true;
    public decimal CancelPendingOrderWhenEntryDistanceExceedsPips { get; set; } = 12m;
}



