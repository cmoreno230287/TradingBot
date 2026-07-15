using TradingBot.Shared;

namespace TradingBot.Application;

public static class OptionsValidator
{
    public static Result Validate(TradingBotOptions options)
    {
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
            && !string.Equals(options.ActiveStrategy.Engine, "SmcLiquiditySweepChoch", StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure("Supported strategy engines are SmartMoney, HourlySweepM1Fvg, and SmcLiquiditySweepChoch.");
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

        var enabledBacktestingSources = options.Backtesting.DataSources
            .Where(item => item.Enabled)
            .ToArray();
        if (enabledBacktestingSources.Length == 0)
        {
            return Result.Failure("At least one backtesting data source must be enabled.");
        }

        if (!string.Equals(options.ActiveBacktestingDataSource.DataSource, "cTrader", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.ActiveBacktestingDataSource.DataSource, "MT5", StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure("Backtesting:DataSource must be 'cTrader' or 'MT5'. Sample market data is not supported.");
        }

        if (options.ActiveBacktestingDataSource.HistoricalDataChunkDaysM1 <= 0
            || options.ActiveBacktestingDataSource.HistoricalDataChunkDaysM5 <= 0
            || options.ActiveBacktestingDataSource.HistoricalDataChunkDaysH1 <= 0
            || options.ActiveBacktestingDataSource.HistoricalDataChunkDaysD1 <= 0)
        {
            return Result.Failure("Active backtesting data source historical chunk days must be greater than zero.");
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
