namespace TradingBot.Domain;

public enum OrderType
{
    Market,
    Limit
}

public enum TradeOutcomeStatus
{
    Open,
    Win,
    Loss,
    BreakEven,
    Cancelled
}

public sealed record OrderRequest(
    string Symbol,
    TradeDirection Direction,
    OrderType OrderType,
    decimal EntryPrice,
    decimal StopLoss,
    decimal TakeProfit,
    decimal LotSize,
    decimal RiskPercent);

public sealed record OrderResult(
    string BrokerOrderId,
    bool Accepted,
    string Message);

public sealed record AccountSnapshot(
    decimal Balance,
    decimal Equity,
    decimal DailyRealizedProfitLoss,
    decimal WeeklyRealizedProfitLoss,
    int ConsecutiveLosses,
    int ConsecutiveLosingDays,
    decimal DailyStartingBalance = 0m,
    decimal InitialBalance = 0m,
    int TradingDays = 0);

public sealed record RiskDecision(
    bool IsAllowed,
    decimal PositionSize,
    string Reason);

public sealed record TradeJournalEntry
{
    public string StrategyId { get; init; } = "SMC-EURUSD-V3";
    public string TradeId { get; init; } = Guid.NewGuid().ToString("N");
    public string? BrokerOrderId { get; init; }
    public string Symbol { get; init; } = "EURUSD";
    public SessionName Session { get; init; }
    public TradeDirection Direction { get; init; }
    public decimal EntryPrice { get; init; }
    public decimal StopLossPrice { get; init; }
    public decimal TakeProfitPrice { get; init; }
    public decimal RiskRewardRatio { get; init; }
    public DateTimeOffset OpenedAt { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }
    public TradeOutcomeStatus OutcomeStatus { get; init; }
    public decimal LotSize { get; init; }
    public decimal RiskPercent { get; init; }
    public decimal ProfitLossAmount { get; init; }
    public decimal Spread { get; init; }
    public decimal Slippage { get; init; }
    public string LiquiditySwept { get; init; } = "";
    public string LiquidityTarget { get; init; } = "";
    public string EntryMode { get; init; } = "Midpoint";
    public string DailyBias { get; init; } = "";
    public string H1Bias { get; init; } = "";
    public string NewsFilterStatus { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record BacktestMetrics(
    int TotalTrades,
    decimal WinRate,
    decimal ProfitFactor,
    decimal MaxDrawdown,
    decimal AverageRiskReward,
    int ConsecutiveLosses,
    int ConsecutiveWins,
    decimal Expectancy);

public sealed record BacktestResult(
    IReadOnlyList<TradeJournalEntry> Trades,
    BacktestMetrics Metrics);
