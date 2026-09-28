using TradingBot.Domain;

namespace TradingBot.Application;

public sealed class FtmoProtectionOptions
{
    public bool Enabled { get; set; }
    public bool RulesConfirmed { get; set; }
    public string AccountVariant { get; set; } = "Unconfirmed";
    public string LossModel { get; set; } = "Static";
    public string ResetTimeZone { get; set; } = "Europe/Prague";
    public int MaximumSubmissionAttempts { get; set; } = 3;
    public int SubmissionRetryDelaySeconds { get; set; } = 15;
    public bool LocalAlertsEnabled { get; set; } = true;
    public int AlertRepeatMinutes { get; set; } = 15;
    public int MaximumProtectionAgeSeconds { get; set; } = 20;
    public int AnalysisTimeoutSeconds { get; set; } = 30;
    public int EntryAnalysisIntervalSeconds { get; set; } = 15;
    public int EvidenceTimeoutSeconds { get; set; } = 2;
    public bool KillSwitch { get; set; }
    public decimal InitialBalance { get; set; } = 100000m;
    public string Currency { get; set; } = "USD";
    public decimal ProfitTargetAmount { get; set; } = 5000m;
    public decimal MaximumDailyLossAmount { get; set; } = 5000m;
    public decimal MaximumLossAmount { get; set; } = 10000m;
    public int MinimumTradingDays { get; set; } = 2;
    public decimal DailySafetyBufferAmount { get; set; } = 500m;
    public decimal TotalSafetyBufferAmount { get; set; } = 500m;
    public decimal InternalDailyLossPercent { get; set; } = 1m;
    public decimal RiskPercent { get; set; } = 0.25m;
    public decimal MaximumAggregateRiskPercent { get; set; } = 0.5m;
    public int MaximumActiveTrades { get; set; } = 1;
    public int MaximumTradesPerDay { get; set; } = 3;
    public int MaximumConsecutiveLosses { get; set; } = 2;
    public int LossCooldownMinutes { get; set; } = 60;
    public decimal MinimumRiskReward { get; set; } = 2m;
    public decimal CommissionPerLot { get; set; } = 7m;
    public decimal SlippageReservePips { get; set; } = 1m;
    public int MaximumQuoteAgeSeconds { get; set; } = 30;
    public int MonitorIntervalSeconds { get; set; } = 5;
    public int PendingLifetimeMinutes { get; set; } = 15;
    public int MaximumHoldingMinutes { get; set; } = 240;
    public int FlattenBeforeResetMinutes { get; set; } = 15;
    public DateTimeOffset ChallengeStartUtc { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public string StateDirectory { get; set; } = "data/ftmo-state";
}

public sealed class FtmoPullbackOptions
{
    public int FastEmaPeriod { get; set; } = 20;
    public int SlowEmaPeriod { get; set; } = 50;
    public int AtrPeriod { get; set; } = 14;
    public decimal MinimumAtrPips { get; set; } = 2m;
    public decimal MaximumAtrPips { get; set; } = 20m;
    public decimal MinimumBodyRatio { get; set; } = 0.5m;
    public int StopLookback { get; set; } = 5;
    public decimal StopBufferPips { get; set; } = 1m;
    public decimal MinimumStopPips { get; set; } = 4m;
    public decimal MaximumStopPips { get; set; } = 25m;
    public decimal RewardRisk { get; set; } = 2m;
}

public static class FtmoClock
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");
    public static DateOnly TradingDay(DateTimeOffset utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, Zone).DateTime);
    public static DateTimeOffset DayStart(DateTimeOffset utc) => Start(TradingDay(utc));
    public static DateTimeOffset NextDayStart(DateTimeOffset utc) => Start(TradingDay(utc).AddDays(1));
    private static DateTimeOffset Start(DateOnly day) => new(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), Zone), TimeSpan.Zero);
}

public sealed record FtmoAccountState(
    decimal Balance, decimal Equity, decimal DailyStartingBalance,
    decimal ExistingRisk, int ActiveTrades, int TradesToday, int ConsecutiveLosses,
    DateTimeOffset? LastLossUtc, int TradingDays, bool ExposureKnown = true,
    string Currency = "USD");

public sealed record FtmoInstrument(decimal LossPerLot, decimal VolumeMinimum, decimal VolumeMaximum,
    decimal VolumeStep, decimal PipValuePerLot);

public sealed record FtmoRiskDecision(bool Allowed, string Reason, decimal Lots = 0m, decimal MonetaryRisk = 0m,
    decimal DailyHeadroom = 0m, decimal TotalHeadroom = 0m, decimal AggregateHeadroom = 0m);

public enum FtmoHaltCode { Running, KillHalt, UnknownExposure, CurrencyMismatch, TotalHalt, DailyHalt, TotalExposure }
public sealed record FtmoProtectionStatus(FtmoHaltCode Code, string? Reason);

/// <summary>Live risk policy. ExistingRisk is remaining equity-to-SL risk.</summary>
public sealed class FtmoRiskEngine(FtmoProtectionOptions options)
{
    public decimal DailyBudget => Math.Min(options.MaximumDailyLossAmount - options.DailySafetyBufferAmount,
        options.InitialBalance * options.InternalDailyLossPercent / 100m);

    public string? ProtectionReason(FtmoAccountState state)
        => ProtectionStatus(state).Reason;

    public FtmoProtectionStatus ProtectionStatus(FtmoAccountState state)
    {
        if (options.KillSwitch) return new(FtmoHaltCode.KillHalt, "Configured kill switch is active.");
        if (!state.ExposureKnown || state.Balance <= 0 || state.Equity <= 0 || state.DailyStartingBalance <= 0)
            return new(FtmoHaltCode.UnknownExposure, "Account state or stop-loss exposure is unknown.");
        if (state.Currency != options.Currency) return new(FtmoHaltCode.CurrencyMismatch, "Account currency does not match configured rule amounts.");
        if (state.Equity <= options.InitialBalance - options.MaximumLossAmount + options.TotalSafetyBufferAmount)
            return new(FtmoHaltCode.TotalHalt, "Total loss safety threshold reached.");
        if (state.Equity - state.ExistingRisk <= state.DailyStartingBalance - DailyBudget)
            return new(FtmoHaltCode.DailyHalt, "Daily loss/exposure safety threshold reached.");
        if (state.Equity - state.ExistingRisk <= options.InitialBalance - options.MaximumLossAmount + options.TotalSafetyBufferAmount)
            return new(FtmoHaltCode.TotalExposure, "Existing exposure exceeds total loss headroom.");
        return new(FtmoHaltCode.Running, null);
    }

    public bool TargetReached(FtmoAccountState state) => state.ActiveTrades == 0
        && state.Balance - options.InitialBalance >= options.ProfitTargetAmount
        && state.TradingDays >= options.MinimumTradingDays;

    public FtmoRiskDecision Evaluate(TradeSignal signal, FtmoAccountState state, FtmoInstrument instrument,
        DateTimeOffset now, decimal? requestedLots = null)
    {
        var dailyHeadroom = state.Equity - state.ExistingRisk - state.DailyStartingBalance + DailyBudget;
        var totalHeadroom = state.Equity - state.ExistingRisk - options.InitialBalance + options.MaximumLossAmount - options.TotalSafetyBufferAmount;
        var aggregateHeadroom = options.InitialBalance * options.MaximumAggregateRiskPercent / 100m - state.ExistingRisk;
        FtmoRiskDecision Reject(string reason) => new(false, reason, DailyHeadroom: dailyHeadroom,
            TotalHeadroom: totalHeadroom, AggregateHeadroom: aggregateHeadroom);
        if (signal.SmcContext is { } context && (now < context.FvgConfirmedAt || now >= context.SubmitBefore))
            return Reject("SMC submission window expired or confirmation is in the future.");
        var protection = ProtectionReason(state);
        if (protection is not null) return Reject(protection);
        if (state.Balance - options.InitialBalance >= options.ProfitTargetAmount)
            return Reject(TargetReached(state) ? "Challenge target and minimum days reached." : "Profit target reached; minimum days incomplete. Preserve capital; no forced trades.");
        if (state.ActiveTrades >= options.MaximumActiveTrades) return Reject("Maximum account-wide active trades reached.");
        if (state.TradesToday + state.ActiveTrades >= options.MaximumTradesPerDay) return Reject("Daily trade budget exhausted (including pending reservations).");
        if (state.ConsecutiveLosses >= options.MaximumConsecutiveLosses) return Reject("Daily consecutive loss limit reached.");
        if (state.LastLossUtc is { } loss && now < loss.AddMinutes(options.LossCooldownMinutes)) return Reject("Loss cooldown is active.");
        if (now >= FtmoClock.NextDayStart(now).AddMinutes(-options.FlattenBeforeResetMinutes)) return Reject("Daily reset flattening window.");
        if (!signal.IsValidSetup || signal.EntryPrice <= 0 || signal.StopLoss <= 0 || signal.TakeProfit <= 0)
            return Reject("Invalid signal or missing protective prices.");
        var sign = signal.Direction == TradeDirection.Buy ? 1m : -1m;
        var stop = (signal.EntryPrice - signal.StopLoss) * sign;
        var reward = (signal.TakeProfit - signal.EntryPrice) * sign;
        if (stop <= 0 || reward / stop < options.MinimumRiskReward) return Reject("Directional SL/TP or minimum reward/risk rejected.");
        if (instrument.LossPerLot <= 0 || instrument.PipValuePerLot <= 0 || instrument.VolumeMinimum <= 0
            || instrument.VolumeStep <= 0 || instrument.VolumeMaximum < instrument.VolumeMinimum)
            return Reject("Invalid broker sizing metadata.");
        var perLot = instrument.LossPerLot + options.CommissionPerLot + instrument.PipValuePerLot * options.SlippageReservePips;
        var budget = Math.Min(state.Balance, state.Equity) * options.RiskPercent / 100m;
        var lots = Math.Floor(Math.Min(instrument.VolumeMaximum, budget / perLot) / instrument.VolumeStep) * instrument.VolumeStep;
        if (requestedLots is { } requested)
        {
            if (requested <= 0 || requested > lots || requested % instrument.VolumeStep != 0)
                return Reject("Requested volume exceeds risk-derived volume or broker step.");
            lots = requested;
        }
        if (lots < instrument.VolumeMinimum) return Reject("Risk budget cannot fund broker minimum volume.");
        var risk = lots * perLot;
        if (state.ExistingRisk + risk > options.InitialBalance * options.MaximumAggregateRiskPercent / 100m)
            return Reject("Maximum aggregate risk exceeded.");
        var worstEquity = state.Equity - state.ExistingRisk - risk;
        if (worstEquity <= state.DailyStartingBalance - DailyBudget) return Reject("Proposed risk exceeds daily loss headroom.");
        if (worstEquity <= options.InitialBalance - options.MaximumLossAmount + options.TotalSafetyBufferAmount)
            return Reject("Proposed risk exceeds total loss headroom.");
        return new(true, "FTMO risk accepted.", lots, risk, dailyHeadroom - risk, totalHeadroom - risk, aggregateHeadroom - risk);
    }
}
