using TradingBot.Domain;

namespace TradingBot.Application;

public sealed class RiskManager(TradingBotOptions options) : IRiskManager
{
    public RiskDecision Evaluate(OrderRequest request, AccountSnapshot account)
    {
        var fundedAccountDecision = EvaluateFundedAccountChallengeRules(account);
        if (!fundedAccountDecision.IsAllowed)
        {
            return fundedAccountDecision;
        }

        if (request.RiskPercent <= 0 || request.RiskPercent > options.MaxRiskPercentPerTrade)
        {
            return new RiskDecision(false, 0m, "Trade risk exceeds configured maximum.");
        }

        if (account.ConsecutiveLosses >= options.MaxConsecutiveLosses)
        {
            return new RiskDecision(false, 0m, "Max consecutive losses reached for the current trading day.");
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

    private RiskDecision EvaluateFundedAccountChallengeRules(AccountSnapshot account)
    {
        var challenge = options.ActiveFundedAccountChallenge;
        if (challenge is null || !challenge.Enabled)
        {
            return new RiskDecision(true, 0m, "Funded account challenge guard is disabled.");
        }

        var initialBalance = account.InitialBalance > 0m
            ? account.InitialBalance
            : challenge.InitialBalance > 0m ? challenge.InitialBalance : account.Balance;
        if (initialBalance <= 0m)
        {
            return new RiskDecision(false, 0m, "Funded account challenge initial balance is invalid.");
        }

        var dailyStartingBalance = account.DailyStartingBalance > 0m
            ? account.DailyStartingBalance
            : account.Balance - account.DailyRealizedProfitLoss;
        var dailyLossAmount = Math.Max(0m, dailyStartingBalance - account.Equity);
        var maxDailyLossAmount = ResolveAmountLimit(
            challenge.MaxDailyLossAmount,
            challenge.MaxDailyLossPercent,
            challenge.DailyLossSafetyBufferAmount,
            challenge.DailyLossSafetyBufferPercent,
            initialBalance);
        if (maxDailyLossAmount > 0m && dailyLossAmount >= maxDailyLossAmount)
        {
            return new RiskDecision(false, 0m, $"{challenge.Name} daily loss guard reached. DailyLoss={decimal.Round(dailyLossAmount, 2)}, Limit={decimal.Round(maxDailyLossAmount, 2)}.");
        }

        var totalLossAmount = Math.Max(0m, initialBalance - account.Equity);
        var maxTotalLossAmount = ResolveAmountLimit(
            challenge.MaxTotalLossAmount,
            challenge.MaxTotalLossPercent,
            challenge.TotalLossSafetyBufferAmount,
            challenge.TotalLossSafetyBufferPercent,
            initialBalance);
        if (maxTotalLossAmount > 0m && totalLossAmount >= maxTotalLossAmount)
        {
            return new RiskDecision(false, 0m, $"{challenge.Name} total loss guard reached. TotalLoss={decimal.Round(totalLossAmount, 2)}, Limit={decimal.Round(maxTotalLossAmount, 2)}.");
        }

        var profitTargetAmount = ResolveAmountLimit(
            challenge.ProfitTargetAmount,
            challenge.ProfitTargetPercent,
            challenge.StopTradingAtProfitTargetBufferAmount,
            challenge.StopTradingAtProfitTargetBufferPercent,
            initialBalance);
        var currentProfit = account.Equity - initialBalance;
        if (profitTargetAmount > 0m && currentProfit >= profitTargetAmount)
        {
            return new RiskDecision(false, 0m, $"{challenge.Name} profit target guard reached. Profit={decimal.Round(currentProfit, 2)}, Target={decimal.Round(profitTargetAmount, 2)}.");
        }

        return new RiskDecision(true, 0m, $"{challenge.Name} challenge guard accepted.");
    }

    private static decimal ResolveAmountLimit(
        decimal amount,
        decimal percent,
        decimal safetyBufferAmount,
        decimal safetyBufferPercent,
        decimal initialBalance)
    {
        var rawLimit = amount > 0m ? amount : initialBalance * (percent / 100m);
        var buffer = safetyBufferAmount > 0m ? safetyBufferAmount : initialBalance * (safetyBufferPercent / 100m);
        return Math.Max(0m, rawLimit - buffer);
    }
}
