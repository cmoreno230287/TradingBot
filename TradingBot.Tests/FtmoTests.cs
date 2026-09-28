using TradingBot.Application;
using TradingBot.Domain;
using TradingBot.Infrastructure.Filters;
using TradingBot.Strategies;

internal static class FtmoTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-02-03T10:00:00Z");
    private static TradeSignal Signal => new("EURUSD", TradeDirection.Buy, 1.1000m, 1.0990m, 1.1020m,
        2, SessionName.London, true, "test", SetupId: "stable-setup");
    private static FtmoAccountState Account => new(100000, 100000, 100000, 0, 0, 0, 0, null, 0);
    private static FtmoInstrument Instrument => new(100, 0.01m, 100, 0.01m, 10);

    public static void Run()
    {
        var tests = new (string, Action)[]
        {
            ("FTMO floors volume including costs", () => {
                var decision = new FtmoRiskEngine(new()).Evaluate(Signal, Account, Instrument, Now);
                Check(decision.Allowed && decision.Lots == 2.13m && decision.MonetaryRisk <= 250, "Sizing must floor and include costs."); }),
            ("FTMO rejects proposed daily boundary", () => {
                var d = new FtmoRiskEngine(new()).Evaluate(Signal, Account with { Equity = 99150 }, Instrument, Now);
                Check(!d.Allowed && d.Reason.Contains("daily"), "Current loss plus proposed risk must be rejected."); }),
            ("FTMO rejects proposed total boundary", () => {
                var d = new FtmoRiskEngine(new()).Evaluate(Signal, Account with { Balance = 90600, Equity = 90600, DailyStartingBalance = 90600 }, Instrument, Now);
                Check(!d.Allowed && d.Reason.Contains("total"), "Total remaining headroom must reserve the new trade."); }),
            ("FTMO rejects aggregate exposure", () => {
                var d = new FtmoRiskEngine(new()).Evaluate(Signal, Account with { ExistingRisk = 300 }, Instrument, Now);
                Check(!d.Allowed && d.Reason.Contains("aggregate"), "Aggregate exposure must count existing risk."); }),
            ("FTMO rejects foreign/unknown exposure", () => {
                Check(!new FtmoRiskEngine(new()).Evaluate(Signal, Account with { ExposureKnown = false }, Instrument, Now).Allowed, "Unknown exposure allowed."); }),
            ("FTMO rejects missing stop and wrong-side target", () => {
                var e = new FtmoRiskEngine(new());
                Check(!e.Evaluate(Signal with { StopLoss = 0 }, Account, Instrument, Now).Allowed, "No SL allowed.");
                Check(!e.Evaluate(Signal with { TakeProfit = 1.0980m }, Account, Instrument, Now).Allowed, "Wrong TP allowed."); }),
            ("FTMO rejects below-minimum lots and manual oversizing", () => {
                var e = new FtmoRiskEngine(new());
                Check(!e.Evaluate(Signal, Account, Instrument with { VolumeMinimum = 3 }, Now).Allowed, "Minimum should not be rounded up.");
                Check(!e.Evaluate(Signal, Account, Instrument, Now, 2.14m).Allowed, "Manual oversizing allowed."); }),
            ("FTMO rejects duplicate simultaneous exposure", () => {
                Check(!new FtmoRiskEngine(new()).Evaluate(Signal, Account with { ActiveTrades = 1 }, Instrument, Now).Allowed, "Concurrent exposure allowed."); }),
            ("FTMO daily trades and loss streak", () => {
                var e = new FtmoRiskEngine(new());
                Check(!e.Evaluate(Signal, Account with { TradesToday = 3 }, Instrument, Now).Allowed, "Daily cap bypass.");
                Check(!e.Evaluate(Signal, Account with { ConsecutiveLosses = 2 }, Instrument, Now).Allowed, "Streak cap bypass."); }),
            ("FTMO cooldown expires deterministically", () => {
                var e = new FtmoRiskEngine(new());
                Check(!e.Evaluate(Signal, Account with { LastLossUtc = Now.AddMinutes(-59) }, Instrument, Now).Allowed, "Cooldown bypass.");
                Check(e.Evaluate(Signal, Account with { LastLossUtc = Now.AddMinutes(-60) }, Instrument, Now).Allowed, "Cooldown never resets."); }),
            ("FTMO target requires closed balance, flat account and days", () => {
                var e = new FtmoRiskEngine(new());
                Check(!e.TargetReached(Account with { Equity = 106000, TradingDays = 2 }), "Floating target counted.");
                Check(!e.TargetReached(Account with { Balance = 105000, TradingDays = 1 }), "Minimum days ignored.");
                Check(!e.TargetReached(Account with { Balance = 105000, TradingDays = 2, ActiveTrades = 1 }), "Exposure ignored.");
                Check(e.TargetReached(Account with { Balance = 105000, TradingDays = 2 }), "Valid target missed."); }),
            ("FTMO DST reset uses 23/25 hour days", () => {
                var spring = DateTimeOffset.Parse("2026-03-29T12:00:00Z");
                var autumn = DateTimeOffset.Parse("2026-10-25T12:00:00Z");
                Check((FtmoClock.NextDayStart(spring) - FtmoClock.DayStart(spring)).TotalHours == 23, "Spring DST wrong.");
                Check((FtmoClock.NextDayStart(autumn) - FtmoClock.DayStart(autumn)).TotalHours == 25, "Autumn DST wrong.");
                Check(FtmoClock.TradingDay(DateTimeOffset.Parse("2026-07-01T22:05:00Z")) == new DateOnly(2026, 7, 2), "Prague reset wrong."); }),
            ("FTMO kill switch and pre-reset window reject entries", () => {
                Check(!new FtmoRiskEngine(new() { KillSwitch = true }).Evaluate(Signal, Account, Instrument, Now).Allowed, "Kill switch bypass.");
                Check(!new FtmoRiskEngine(new()).Evaluate(Signal, Account, Instrument, FtmoClock.NextDayStart(Now).AddMinutes(-10)).Allowed, "Reset entry bypass."); }),
            ("FTMO invalid protection configuration rejected", () => {
                var o = Options(); o.FtmoProtection.DailySafetyBufferAmount = 5000;
                Check(!OptionsValidator.Validate(o).IsSuccess, "Zero loss budget accepted."); }),
            ("FTMO strategy rejects missing and high-volatility data", StrategyDataGuards),
            ("FTMO strategy uses completed bars and stable IDs", StrategySignal),
        };
        foreach (var (name, test) in tests) { test(); Console.WriteLine("PASS " + name); }
    }

    private static TradingBotOptions Options()
    {
        var o = new TradingBotOptions { Broker = "MT5", RiskPercentPerTrade = 0.25m, MaxRiskPercentPerTrade = 0.5m,
            FtmoProtection = new() { Enabled = true }, UseNewsFilter = false };
        o.Strategies.Items = [new() { Id = "test-ftmo", Engine = "FtmoPullback", Enabled = true, Symbol = "EURUSD" }];
        o.Strategies.ActiveStrategyId = "test-ftmo";
        o.Normalize();
        return o;
    }

    private static List<Candle> Hourly() => Enumerable.Range(0, 180).Select(i =>
        new Candle("EURUSD", Timeframe.H1, Now.AddHours(i - 180), 1.08m + i * 0.0001m,
            1.0802m + i * 0.0001m, 1.0798m + i * 0.0001m, 1.0801m + i * 0.0001m, 100)).ToList();

    private static List<Candle> Execution()
    {
        var bars = Enumerable.Range(0, 160).Select(i => new Candle("EURUSD", Timeframe.M5, Now.AddMinutes((i - 160) * 5),
            1.1m, 1.1002m, 1.0998m, 1.1m, 100)).ToList();
        bars[^2] = bars[^2] with { Open = 1.1001m, High = 1.1002m, Low = 1.0998m, Close = 1.1m };
        bars[^1] = bars[^1] with { Open = 1.1m, High = 1.1009m, Low = 1.0999m, Close = 1.1008m };
        return bars;
    }

    private static void StrategySignal()
    {
        var o = Options(); var bars = Execution();
        var provider = new Data(Hourly(), bars);
        var engine = new FtmoPullbackStrategyEngine(provider, new NewsFilter(o), new Session(), o);
        var first = engine.AnalyzeAsync("EURUSD", Now, default).GetAwaiter().GetResult();
        Check(first.IsValidSetup && first.Direction == TradeDirection.Buy, "Expected deterministic pullback buy.");
        bars.Add(new("EURUSD", Timeframe.M5, Now, 1.1m, 2m, 0.5m, 0.6m, 100));
        var second = engine.AnalyzeAsync("EURUSD", Now.AddSeconds(20), default).GetAwaiter().GetResult();
        Check(second.IsValidSetup && first.SetupId == second.SetupId && first.EntryPrice == second.EntryPrice, "Unfinished candle changed signal.");
    }

    private static void StrategyDataGuards()
    {
        var o = Options();
        var engine = new FtmoPullbackStrategyEngine(new Data([], []), new NewsFilter(o), new Session(), o);
        Check(!engine.AnalyzeAsync("EURUSD", Now, default).Result.IsValidSetup, "Missing data allowed.");
        var bars = Execution(); o.FtmoPullback.MaximumAtrPips = 1;
        engine = new(new Data(Hourly(), bars), new NewsFilter(o), new Session(), o);
        Check(!engine.AnalyzeAsync("EURUSD", Now, default).Result.IsValidSetup, "High volatility allowed.");
        o.FtmoPullback.MaximumAtrPips = 20; bars.RemoveAt(bars.Count - 3);
        Check(!engine.AnalyzeAsync("EURUSD", Now, default).Result.IsValidSetup, "Gap allowed.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Session : ISessionClock { public SessionName GetCurrentSession(DateTimeOffset at) => SessionName.London; }
    private sealed class FixedSignal(TradeSignal signal) : IStrategyEngine
    { public Task<TradeSignal> AnalyzeAsync(string symbol, DateTimeOffset now, CancellationToken ct) => Task.FromResult(signal); }
    private sealed class Data(List<Candle> h1, List<Candle> m5) : IMarketDataProvider
    {
        public Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Candle>>((timeframe == Timeframe.H1 ? h1 : m5).Where(c => c.OpenedAt >= from && c.OpenedAt < to).ToArray());
    }
}
