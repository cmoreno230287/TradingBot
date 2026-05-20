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

        if (options.MaxActiveTrades <= 0)
        {
            return Result.Failure("MaxActiveTrades must be greater than zero.");
        }

        if (!string.Equals(options.Backtesting.DataSource, "cTrader", StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure("Backtesting:DataSource must be 'cTrader'. Sample market data is not supported.");
        }

        return Result.Success();
    }
}
