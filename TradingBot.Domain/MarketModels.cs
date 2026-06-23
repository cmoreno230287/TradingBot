namespace TradingBot.Domain;

public enum TradeDirection
{
    Buy,
    Sell
}

public enum Timeframe
{
    M1,
    M5,
    H1,
    D1
}

public enum MarketBias
{
    Bullish,
    Bearish,
    Neutral
}

public enum SessionName
{
    London,
    NewYork,
    LondonNewYorkOverlap,
    Closed
}

public sealed record Candle(
    string Symbol,
    Timeframe Timeframe,
    DateTimeOffset OpenedAt,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume)
{
    public decimal Range => High - Low;

    public decimal Body => Math.Abs(Close - Open);

    public bool IsBullish => Close > Open;

    public bool IsBearish => Close < Open;
}

public sealed record MarketTick(
    string Symbol,
    DateTimeOffset Timestamp,
    decimal Bid,
    decimal Ask,
    decimal Last,
    decimal Volume)
{
    public decimal Mid => Last > 0m ? Last : (Bid + Ask) / 2m;
}

public sealed record FairValueGap(
    TradeDirection Direction,
    decimal LowerPrice,
    decimal UpperPrice,
    DateTimeOffset CreatedAt)
{
    public decimal Midpoint => (LowerPrice + UpperPrice) / 2m;

    public decimal Size => UpperPrice - LowerPrice;
}

public sealed record LiquiditySweep(
    TradeDirection ExpectedDirection,
    string LevelName,
    decimal SweptPrice,
    DateTimeOffset OccurredAt);

public sealed record TradeSignal(
    string Symbol,
    TradeDirection Direction,
    decimal EntryPrice,
    decimal StopLoss,
    decimal TakeProfit,
    decimal RiskReward,
    SessionName Session,
    bool IsValidSetup,
    string SetupReason,
    FairValueGap? FairValueGap = null,
    string? SetupId = null);
