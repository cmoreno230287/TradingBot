using TradingBot.Shared;

namespace TradingBot.Application;

public static class OptionsValidator
{
    public static Result Validate(TradingBotOptions options)
    {
        var ftmo = ValidateFtmo(options);
        if (!ftmo.IsSuccess) return ftmo;
        if (string.IsNullOrWhiteSpace(options.Symbol))
        {
            return Result.Failure("Symbol is required.");
        }

        if (options.RiskPercentPerTrade <= 0 || options.RiskPercentPerTrade > options.MaxRiskPercentPerTrade)
        {
            return Result.Failure("RiskPercentPerTrade must be greater than zero and not exceed MaxRiskPercentPerTrade.");
        }

        if (options.LowRiskRollout.Enabled)
        {
            if (options.LowRiskRollout.RiskPercentPerTrade <= 0
                || options.LowRiskRollout.MaxRiskPercentPerTrade <= 0
                || options.LowRiskRollout.MaxActiveTrades <= 0)
            {
                return Result.Failure("LowRiskRollout risk and active trade settings must be greater than zero.");
            }

            if (options.LowRiskRollout.RiskPercentPerTrade > options.LowRiskRollout.MaxRiskPercentPerTrade)
            {
                return Result.Failure("LowRiskRollout:RiskPercentPerTrade cannot exceed LowRiskRollout:MaxRiskPercentPerTrade.");
            }

            if (options.RiskPercentPerTrade > options.LowRiskRollout.RiskPercentPerTrade)
            {
                return Result.Failure("Low-risk rollout is enabled. RiskPercentPerTrade must not exceed LowRiskRollout:RiskPercentPerTrade.");
            }

            if (options.MaxRiskPercentPerTrade > options.LowRiskRollout.MaxRiskPercentPerTrade)
            {
                return Result.Failure("Low-risk rollout is enabled. MaxRiskPercentPerTrade must not exceed LowRiskRollout:MaxRiskPercentPerTrade.");
            }
        }

        if (options.MaxRiskPercentPerTrade > 1.0m)
        {
            return Result.Failure("MaxRiskPercentPerTrade cannot exceed 1.0%.");
        }

        if (options.MinRiskReward < 2.0m)
        {
            return Result.Failure("MinRiskReward must be at least 2.0.");
        }

        if (options.DailyDrawdownLimitPercent <= 0 || options.WeeklyDrawdownLimitPercent <= 0)
        {
            return Result.Failure("Drawdown limits must be configured.");
        }

        if (options.AnalysisExecutionIntervalSeconds <= 0)
        {
            return Result.Failure("AnalysisExecutionIntervalSeconds must be greater than zero.");
        }

        var enabledStrategies = options.Strategies.Items.Where(item => item.Enabled).ToArray();
        if (enabledStrategies.Length == 0)
        {
            return Result.Failure("At least one strategy must be enabled.");
        }

        if (!string.IsNullOrWhiteSpace(options.Strategies.ActiveStrategyId)
            && !enabledStrategies.Any(item => string.Equals(item.Id, options.Strategies.ActiveStrategyId, StringComparison.OrdinalIgnoreCase)))
        {
            return Result.Failure("Strategies:ActiveStrategyId must reference an enabled strategy.");
        }

        if (!string.Equals(options.ActiveStrategy.Engine, "SmartMoney", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.ActiveStrategy.Engine, "HourlySweepM1Fvg", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.ActiveStrategy.Engine, "SmcLiquiditySweepChoch", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.ActiveStrategy.Engine, "FtmoPullback", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.ActiveStrategy.Engine, "FtmoSmc", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.ActiveStrategy.Engine, "FtmoSmcContinuation", StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure("Supported strategy engines are SmartMoney, HourlySweepM1Fvg, SmcLiquiditySweepChoch, FtmoPullback, FtmoSmc, and FtmoSmcContinuation.");
        }

        if (options.BiasSwingStrength <= 0 || options.SetupLookbackCandlesM5 <= 0 || options.LiquiditySweepLookbackCandles <= 0 || options.MaxSetupAgeCandlesM5 <= 0)
        {
            return Result.Failure("Strategy lookback and swing settings must be greater than zero.");
        }

        if (options.MinFvgSizePips < 0)
        {
            return Result.Failure("MinFvgSizePips cannot be negative.");
        }

        if (options.FVGPercentBoundary <= 0m || options.FVGPercentBoundary >= 100m)
        {
            return Result.Failure("FVGPercentBoundary must be greater than 0 and less than 100.");
        }

        if (options.TradeTracking.MaxRowsPerFile <= 0 || options.TradeTracking.LookbackDays <= 0)
        {
            return Result.Failure("TradeTracking:MaxRowsPerFile and TradeTracking:LookbackDays must be greater than zero.");
        }

        if (options.DailyTradingStop.Enabled
            && (options.DailyTradingStop.MaxWinningTradesPerDay <= 0 || options.DailyTradingStop.MaxLosingTradesPerDay <= 0))
        {
            return Result.Failure("DailyTradingStop winning and losing trade limits must be greater than zero.");
        }

        if (options.ForexMarketSessions.Enabled)
        {
            if (options.ForexMarketSessions.TradingDays.Length == 0)
            {
                return Result.Failure("ForexMarketSessions:TradingDays must contain at least one day.");
            }

            foreach (var day in options.ForexMarketSessions.TradingDays)
            {
                if (!Enum.TryParse<DayOfWeek>(day, ignoreCase: true, out _))
                {
                    return Result.Failure($"ForexMarketSessions:TradingDays contains an invalid day '{day}'.");
                }
            }

            if (!IsTimeRange(options.ForexMarketSessions.LondonSessionNYTime))
            {
                return Result.Failure("ForexMarketSessions:LondonSessionNYTime must use HH:mm-HH:mm format.");
            }

            if (!IsTimeRange(options.ForexMarketSessions.NewYorkSessionNYTime))
            {
                return Result.Failure("ForexMarketSessions:NewYorkSessionNYTime must use HH:mm-HH:mm format.");
            }
        }

        if (options.MaxActiveTrades <= 0)
        {
            return Result.Failure("MaxActiveTrades must be greater than zero.");
        }

        if (!string.Equals(options.Broker, "cTrader", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.Broker, "MT5", StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure("Broker must be 'cTrader' or 'MT5'.");
        }

        var activeChallenge = options.ActiveFundedAccountChallenge;
        if (activeChallenge is not null)
        {
            var validation = ValidateFundedChallenge(activeChallenge);
            if (!validation.IsSuccess)
            {
                return validation;
            }
        }

        if (string.Equals(options.Broker, "MT5", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(options.MT5.BridgeBaseUrl))
            {
                return Result.Failure("MT5:BridgeBaseUrl is required when Broker is MT5.");
            }

            if (options.MT5.TimeoutSeconds <= 0)
            {
                return Result.Failure("MT5:TimeoutSeconds must be greater than zero.");
            }

            if (options.MT5.MagicNumber <= 0)
            {
                return Result.Failure("MT5:MagicNumber must be greater than zero.");
            }

            if (options.MT5.MaxActiveTrades <= 0)
            {
                return Result.Failure("MT5:MaxActiveTrades must be greater than zero.");
            }

            if (options.MT5.PendingOrderExpirationHours <= 0)
            {
                return Result.Failure("MT5:PendingOrderExpirationHours must be greater than zero.");
            }

            if (options.MT5.MaxSlippagePoints < 0)
            {
                return Result.Failure("MT5:MaxSlippagePoints cannot be negative.");
            }

            if (options.MT5.MaxEntryDistancePips <= 0)
            {
                return Result.Failure("MT5:MaxEntryDistancePips must be greater than zero.");
            }

            if (options.MT5.CancelPendingOrderWhenEntryDistanceExceedsPips <= 0)
            {
                return Result.Failure("MT5:CancelPendingOrderWhenEntryDistanceExceedsPips must be greater than zero.");
            }
        }

        return Result.Success();
    }

    private static bool IsTimeRange(string value)
    {
        var parts = value.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2
            && TimeSpan.TryParse(parts[0], out _)
            && TimeSpan.TryParse(parts[1], out _);
    }

    private static Result ValidateFtmo(TradingBotOptions options)
    {
        var p = options.FtmoProtection;
        if (p.EvidenceTimeoutSeconds is < 1 or > 10) return Result.Failure("Evidence timeout must be between 1 and 10 seconds.");
        if (options.NewsCalendar.Enabled && (!options.UseNewsFilter
            || !Uri.TryCreate(options.NewsCalendar.FeedUrl, UriKind.Absolute, out var feed) || feed.Scheme != "https"
            || !string.IsNullOrEmpty(feed.UserInfo) || options.NewsCalendar.RefreshSeconds is < 30 or > 3600
            || options.NewsCalendar.MaximumAgeMinutes is < 1 or > 1440))
            return Result.Failure("Automatic news requires filtering, an HTTPS feed, and bounded refresh/age limits.");
        if (p.MaximumSubmissionAttempts is < 1 or > 5 || p.SubmissionRetryDelaySeconds is < 1 or > 300
            || p.AlertRepeatMinutes is < 1 or > 1440)
            return Result.Failure("Invalid bounded submission retry policy.");
        if (p.Enabled && (p.LossModel != "Static" || p.ResetTimeZone != "Europe/Prague"))
            return Result.Failure("Only static loss limits with Europe/Prague resets are supported.");
        if (p.MaximumProtectionAgeSeconds < p.MonitorIntervalSeconds || p.MaximumProtectionAgeSeconds > 120
            || p.AnalysisTimeoutSeconds < 1 || p.AnalysisTimeoutSeconds > 120
            || p.EntryAnalysisIntervalSeconds is < 1 or > 60)
            return Result.Failure("Invalid live protection age or analysis timeout.");
        if (options.NewsCoverageFromUtc is { } coverageStart && options.NewsCoverageUntilUtc is { } coverageEnd
            && coverageEnd <= coverageStart) return Result.Failure("News coverage end must follow its start.");
        var s = options.FtmoPullback;
        var strategy = string.Equals(options.ActiveStrategy.Engine, "FtmoPullback", StringComparison.OrdinalIgnoreCase)
            || string.Equals(options.ActiveStrategy.Engine, "FtmoSmc", StringComparison.OrdinalIgnoreCase)
            || string.Equals(options.ActiveStrategy.Engine, "FtmoSmcContinuation", StringComparison.OrdinalIgnoreCase);
        if (strategy && !p.Enabled) return Result.Failure("FTMO strategies require FtmoProtection.Enabled.");
        if (!p.Enabled) return Result.Success();
        if (options.Broker != "MT5" || options.Symbol != "EURUSD" || p.Currency != "USD")
            return Result.Failure("FTMO protection currently supports MT5 EURUSD accounts denominated in USD only.");
        if (options.AllowedSessions.Length == 0 || options.AllowedSessions.Any(s => !Enum.TryParse<TradingBot.Domain.SessionName>(s, out var session) || session == TradingBot.Domain.SessionName.Closed)
            || !IsTimeRange(options.LondonKillZoneNYTime) || !IsTimeRange(options.NewYorkKillZoneNYTime) || !IsTimeRange(options.LondonNewYorkOverlapNYTime))
            return Result.Failure("FTMO entry sessions must be explicitly valid.");
        foreach (var window in options.NewsBlackoutWindowsUtc.Concat(options.BrokerMarketClosuresUtc))
        {
            var parts = window.Split('/');
            if (parts.Length != 2 || !DateTimeOffset.TryParse(parts[0], out var start) || !DateTimeOffset.TryParse(parts[1], out var end) || end < start)
                return Result.Failure("FTMO news windows must be valid ISO timestamp intervals.");
        }
        if (p.InitialBalance <= 0 || p.ProfitTargetAmount <= 0 || p.MaximumDailyLossAmount <= 0
            || p.MaximumLossAmount <= 0 || p.MaximumLossAmount >= p.InitialBalance || p.MinimumTradingDays < 0
            || p.DailySafetyBufferAmount < 0 || p.DailySafetyBufferAmount >= p.MaximumDailyLossAmount
            || p.TotalSafetyBufferAmount < 0 || p.TotalSafetyBufferAmount >= p.MaximumLossAmount
            || p.InternalDailyLossPercent <= 0 || p.RiskPercent <= 0 || p.RiskPercent > 1
            || p.MaximumAggregateRiskPercent < p.RiskPercent || p.MaximumActiveTrades != 1
            || p.MaximumTradesPerDay <= 0 || p.MaximumConsecutiveLosses <= 0 || p.LossCooldownMinutes < 0
            || p.MinimumRiskReward < 2 || p.CommissionPerLot < 0 || p.SlippageReservePips < 0
            || p.MaximumQuoteAgeSeconds <= 0 || p.MonitorIntervalSeconds <= 0 || p.MonitorIntervalSeconds > 60
            || p.PendingLifetimeMinutes <= 0 || p.MaximumHoldingMinutes <= 0 || p.FlattenBeforeResetMinutes < 1
            || p.FlattenBeforeResetMinutes >= 1440 || string.IsNullOrWhiteSpace(p.StateDirectory))
            return Result.Failure("Invalid FTMO risk policy; single account-wide exposure is required by this implementation.");
        if (!string.Equals(options.ActiveStrategy.Engine, "FtmoSmc", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.ActiveStrategy.Engine, "FtmoSmcContinuation", StringComparison.OrdinalIgnoreCase)
            && (s.FastEmaPeriod < 2 || s.SlowEmaPeriod <= s.FastEmaPeriod || s.SlowEmaPeriod > 100
            || s.AtrPeriod < 2 || s.StopLookback < 2 || s.MinimumAtrPips <= 0 || s.MaximumAtrPips < s.MinimumAtrPips
            || s.MinimumBodyRatio <= 0 || s.MinimumBodyRatio > 1 || s.StopBufferPips < 0
            || s.MinimumStopPips <= 0 || s.MaximumStopPips < s.MinimumStopPips
            || s.RewardRisk < p.MinimumRiskReward || options.PipSize <= 0))
            return Result.Failure("Invalid FTMO pullback parameters.");
        if (string.Equals(options.ActiveStrategy.Engine, "FtmoSmc", StringComparison.OrdinalIgnoreCase)
            || string.Equals(options.ActiveStrategy.Engine, "FtmoSmcContinuation", StringComparison.OrdinalIgnoreCase))
        {
            var smc = options.FtmoSmc;
            var active = options.ActiveStrategy;
            if (!string.Equals(active.BiasTimeframe, "H1", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(active.ExecutionTimeframe, "M5", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(active.EntryTimeframe, "M5", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(active.MacroBiasTimeframe, "H1", StringComparison.OrdinalIgnoreCase)
                || active.UseDailyBiasFilter)
                return Result.Failure("Protected SMC requires H1 macro/bias, M5 execution/entry and no legacy daily-bias filter.");
            if (smc.EntryRetracementFraction <= 0 || smc.EntryRetracementFraction >= 1)
                return Result.Failure("SMC entry retracement fraction must be strictly between zero and one.");
            if (smc.SwingStrength < 1 || smc.SwingStrength > 10 || smc.H1RangeLookback < 4 * smc.SwingStrength + 5
                || smc.H1RangeLookback > 240 || smc.H1HistoryDays < 1 || smc.H1HistoryDays > 90 || smc.M5HistoryDays < 1 || smc.M5HistoryDays > 30 || smc.MaximumH1DataAgeHours < 1 || smc.MaximumH1DataAgeHours > 48 || smc.MaximumM5DataAgeMinutes < 1 || smc.MaximumM5DataAgeMinutes > 120 || smc.LiquidityLookback < 2 * smc.SwingStrength + 1 || smc.LiquidityLookback > 100
                || smc.MaximumSweepAgeBars < 1 || smc.MaximumSweepAgeBars > 48 || smc.AtrPeriod < 2 || smc.AtrPeriod > 100
                || smc.MaximumFvgDelayAfterBreakBars < 0 || smc.MaximumFvgDelayAfterBreakBars > smc.MaximumSweepAgeBars
                || smc.MinimumDisplacementBodyRatio <= 0 || smc.MinimumDisplacementBodyRatio > 1
                || smc.DisplacementAtrMultiplier <= 0 || smc.MinimumFvgPips <= 0 || smc.StopBufferPips < 0
                || smc.MinimumStopPips <= 0 || smc.MaximumStopPips < smc.MinimumStopPips
                || smc.RewardRisk < p.MinimumRiskReward || options.PipSize <= 0)
                return Result.Failure("Invalid FTMO SMC parameters.");
        }
        return Result.Success();
    }

    private static Result ValidateFundedChallenge(FundedAccountChallengeOptions challenge)
    {
        if (challenge.InitialBalance <= 0)
        {
            return Result.Failure("Funded account challenge InitialBalance must be greater than zero.");
        }

        if (challenge.MinimumTradingDays < 0)
        {
            return Result.Failure("Funded account challenge MinimumTradingDays cannot be negative.");
        }

        if (challenge.MaxDailyLossAmount <= 0 && challenge.MaxDailyLossPercent <= 0)
        {
            return Result.Failure("Funded account challenge must define MaxDailyLossAmount or MaxDailyLossPercent.");
        }

        if (challenge.MaxTotalLossAmount <= 0 && challenge.MaxTotalLossPercent <= 0)
        {
            return Result.Failure("Funded account challenge must define MaxTotalLossAmount or MaxTotalLossPercent.");
        }

        if (challenge.ProfitTargetAmount <= 0 && challenge.ProfitTargetPercent <= 0)
        {
            return Result.Failure("Funded account challenge must define ProfitTargetAmount or ProfitTargetPercent.");
        }

        if (challenge.DailyLossSafetyBufferAmount < 0
            || challenge.TotalLossSafetyBufferAmount < 0
            || challenge.StopTradingAtProfitTargetBufferAmount < 0
            || challenge.DailyLossSafetyBufferPercent < 0
            || challenge.TotalLossSafetyBufferPercent < 0
            || challenge.StopTradingAtProfitTargetBufferPercent < 0)
        {
            return Result.Failure("Funded account challenge safety buffers cannot be negative.");
        }

        return Result.Success();
    }
}

