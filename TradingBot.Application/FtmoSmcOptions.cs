namespace TradingBot.Application;

public sealed class FtmoSmcOptions
{
    public int SwingStrength { get; set; } = 2;
    public int H1RangeLookback { get; set; } = 48;
    public int H1HistoryDays { get; set; } = 20;
    public int M5HistoryDays { get; set; } = 3;
    public int MaximumH1DataAgeHours { get; set; } = 2;
    public int MaximumM5DataAgeMinutes { get; set; } = 6;
    public int LiquidityLookback { get; set; } = 12;
    public int MaximumSweepAgeBars { get; set; } = 6;
    public int MaximumFvgDelayAfterBreakBars { get; set; }
    public int AtrPeriod { get; set; } = 14;
    public decimal MinimumDisplacementBodyRatio { get; set; } = 0.6m;
    public decimal DisplacementAtrMultiplier { get; set; } = 1.2m;
    public decimal MinimumFvgPips { get; set; } = 0.5m;
    public bool UsePremiumDiscountFilter { get; set; } = true;
    public bool RequireLiquidityTargetRoom { get; set; } = true;
    public decimal StopBufferPips { get; set; } = 1m;
    public decimal MinimumStopPips { get; set; } = 4m;
    public decimal MaximumStopPips { get; set; } = 25m;
    public decimal RewardRisk { get; set; } = 2m;
    public decimal EntryRetracementFraction { get; set; } = 0.5m;
}

