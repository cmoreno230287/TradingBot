using TradingBot.Domain;

namespace TradingBot.Application;

public sealed class RiskManager(TradingBotOptions options) : IRiskManager
{
    public RiskDecision Evaluate(OrderRequest request, AccountSnapshot account)
    {
        if (request.RiskPercent <= 0 || request.RiskPercent > options.MaxRiskPercentPerTrade)
        {
            return new RiskDecision(false, 0m, "Trade risk exceeds configured maximum.");
        }

        if (account.ConsecutiveLosses >= options.MaxConsecutiveLosses)
        {
            return new RiskDecision(false, 0m, "Max consecutive losses reached.");
        }

        if (account.ConsecutiveLosingDays >= options.MaxConsecutiveLosingDays)
        {
            return new RiskDecision(false, 0m, "Weekly losing-day limit reached.");
        }

        var dailyDrawdownPercent = account.Balance == 0 ? 0 : Math.Abs(Math.Min(account.DailyRealizedProfitLoss, 0m)) / account.Balance * 100m;
        if (dailyDrawdownPercent >= options.DailyDrawdownLimitPercent)
        {
            return new RiskDecision(false, 0m, "Daily drawdown limit reached.");
        }

        var weeklyDrawdownPercent = account.Balance == 0 ? 0 : Math.Abs(Math.Min(account.WeeklyRealizedProfitLoss, 0m)) / account.Balance * 100m;
        if (weeklyDrawdownPercent >= options.WeeklyDrawdownLimitPercent)
        {
            return new RiskDecision(false, 0m, "Weekly drawdown limit reached.");
        }

        var stopDistancePips = Math.Abs(request.EntryPrice - request.StopLoss) / options.PipSize;
        if (stopDistancePips <= 0)
        {
            return new RiskDecision(false, 0m, "Stop loss distance is invalid.");
        }

        var riskAmount = account.Balance * (request.RiskPercent / 100m);
        var lots = riskAmount / (stopDistancePips * options.PipValuePerLot);

        return new RiskDecision(true, decimal.Round(lots, 2), "Risk accepted.");
    }
}
