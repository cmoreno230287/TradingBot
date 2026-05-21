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

        if (options.FTMOChallenge.Enabled)
        {
            if (options.FTMOChallenge.InitialBalance <= 0)
            {
                return Result.Failure("FTMOChallenge:InitialBalance must be greater than zero.");
            }

            if (options.FTMOChallenge.MinimumTradingDays < 0)
            {
                return Result.Failure("FTMOChallenge:MinimumTradingDays cannot be negative.");
            }

            if (options.FTMOChallenge.MaxDailyLossAmount <= 0 && options.FTMOChallenge.MaxDailyLossPercent <= 0)
            {
                return Result.Failure("FTMOChallenge must define MaxDailyLossAmount or MaxDailyLossPercent.");
            }

            if (options.FTMOChallenge.MaxTotalLossAmount <= 0 && options.FTMOChallenge.MaxTotalLossPercent <= 0)
            {
                return Result.Failure("FTMOChallenge must define MaxTotalLossAmount or MaxTotalLossPercent.");
            }

            if (options.FTMOChallenge.ProfitTargetAmount <= 0 && options.FTMOChallenge.ProfitTargetPercent <= 0)
            {
                return Result.Failure("FTMOChallenge must define ProfitTargetAmount or ProfitTargetPercent.");
            }

            if (options.FTMOChallenge.DailyLossSafetyBufferAmount < 0
                || options.FTMOChallenge.TotalLossSafetyBufferAmount < 0
                || options.FTMOChallenge.StopTradingAtProfitTargetBufferAmount < 0
                || options.FTMOChallenge.DailyLossSafetyBufferPercent < 0
                || options.FTMOChallenge.TotalLossSafetyBufferPercent < 0
                || options.FTMOChallenge.StopTradingAtProfitTargetBufferPercent < 0)
            {
                return Result.Failure("FTMOChallenge safety buffers cannot be negative.");
            }

            if (options.FTMOChallenge.MaxDailyLossAmount > 0
                && options.FTMOChallenge.DailyLossSafetyBufferAmount >= options.FTMOChallenge.MaxDailyLossAmount)
            {
                return Result.Failure("FTMOChallenge:DailyLossSafetyBufferAmount must be lower than MaxDailyLossAmount.");
            }

            if (options.FTMOChallenge.MaxTotalLossAmount > 0
                && options.FTMOChallenge.TotalLossSafetyBufferAmount >= options.FTMOChallenge.MaxTotalLossAmount)
            {
                return Result.Failure("FTMOChallenge:TotalLossSafetyBufferAmount must be lower than MaxTotalLossAmount.");
            }
        }

        if (options.AnalysisExecutionIntervalSeconds <= 0)
        {
            return Result.Failure("AnalysisExecutionIntervalSeconds must be greater than zero.");
        }

        if (options.BiasSwingStrength <= 0 || options.SetupLookbackCandlesM5 <= 0 || options.LiquiditySweepLookbackCandles <= 0 || options.MaxSetupAgeCandlesM5 <= 0)
        {
            return Result.Failure("Strategy lookback and swing settings must be greater than zero.");
        }

        if (options.MinFvgSizePips < 0)
        {
            return Result.Failure("MinFvgSizePips cannot be negative.");
        }

        if (options.TradeTracking.MaxRowsPerFile <= 0 || options.TradeTracking.LookbackDays <= 0)
        {
            return Result.Failure("TradeTracking:MaxRowsPerFile and TradeTracking:LookbackDays must be greater than zero.");
        }

        if (options.SignalTracking.MaxRowsPerFile <= 0)
        {
            return Result.Failure("SignalTracking:MaxRowsPerFile must be greater than zero.");
        }

        if (options.TradingView.ScreenshotWidth <= 0 || options.TradingView.ScreenshotHeight <= 0)
        {
            return Result.Failure("TradingView screenshot dimensions must be greater than zero.");
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

        if (!string.Equals(options.Backtesting.DataSource, "cTrader", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.Backtesting.DataSource, "MT5", StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure("Backtesting:DataSource must be 'cTrader' or 'MT5'. Sample market data is not supported.");
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

            if (options.MT5.PendingOrderExpirationMinutes <= 0)
            {
                return Result.Failure("MT5:PendingOrderExpirationMinutes must be greater than zero.");
            }

            if (options.MT5.MaxSlippagePoints < 0)
            {
                return Result.Failure("MT5:MaxSlippagePoints cannot be negative.");
            }
        }

        return Result.Success();
    }
}
