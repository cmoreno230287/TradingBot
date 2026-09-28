using TradingBot.Application;
using TradingBot.Domain;
using TradingBot.Infrastructure.Filters;
using TradingBot.Strategies;

internal static class FtmoSmcTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-02-03T10:00:00Z");
    public static void Run()
    {
        var tests = new (string, Action)[]
        {
            ("Protected SMC rejects structure broken before sweep", () => {
                var f = Fixture(); f.M5[21] = f.M5[21] with { High = 1.1007m, Close = 1.1006m };
                Check(!Analyze(f).IsValidSetup, "Previously broken level accepted."); }),
            ("Protected SMC records ordered timing and submission expiry", () => {
                var f = Fixture(); var s = Analyze(f); var c = s.SmcContext!;
                Check(s.IsValidSetup && c.SweepOpenedAt < c.BreakOpenedAt && c.BreakOpenedAt < c.FvgConfirmedAt,
                    "SMC timeline missing or unordered.");
                var account = new FtmoAccountState(100000, 100000, 100000, 0, 0, 0, 0, null, 0);
                var decision = new FtmoRiskEngine(f.Options.FtmoProtection).Evaluate(s, account,
                    new FtmoInstrument(110, .01m, 100, .01m, 10), c.SubmitBefore);
                Check(!decision.Allowed && decision.Reason.Contains("submission"), "Expired signal accepted."); }),
            ("Protected SMC honors retracement on buys and sells", () => {
                var f = Fixture(); f.Options.FtmoSmc.EntryRetracementFraction = .25m;
                var buy = Analyze(f); Check(buy.IsValidSetup && buy.EntryPrice == 1.10065m, buy.SetupReason);
                f.H1 = Mirror(f.H1); f.M5 = Mirror(f.M5);
                var sell = Analyze(f); Check(sell.IsValidSetup && sell.EntryPrice == 1.09935m, sell.SetupReason); }),
            ("Protected SMC rejects sweep invalidation and duplicate H1 bars", () => {
                var f = Fixture(); f.M5[23] = f.M5[23] with { Low = 1.0994m };
                Check(!Analyze(f).IsValidSetup, "Invalidated sweep accepted.");
                f = Fixture(); f.H1[21] = f.H1[21] with { OpenedAt = f.H1[20].OpenedAt };
                Check(!Analyze(f).IsValidSetup, "Duplicate H1 accepted."); }),
            ("Protected SMC rejects unsupported timeframes and boundary entries", () => {
                var f = Fixture(); f.Options.Strategies.Items[0].EntryTimeframe = "M1";
                Check(!OptionsValidator.Validate(f.Options).IsSuccess, "Unsupported timeframe accepted.");
                f = Fixture(); f.Options.FtmoSmc.EntryRetracementFraction = 0;
                Check(!OptionsValidator.Validate(f.Options).IsSuccess, "Boundary entry accepted."); }),
            ("SMC continuation accepts BOS without mandatory sweep", () => {
                var f = ContinuationFixture(); f.M5[22] = f.M5[22] with { Low = 1.0998m };
                var s = AnalyzeContinuation(f); Check(s.IsValidSetup && s.Direction == TradeDirection.Buy, s.SetupReason);
                Check(s.SetupId!.StartsWith("FTMO-SMC-CONT|"), "Continuation ID namespace missing."); }),
            ("SMC continuation accepts mirrored sell", () => {
                var f = ContinuationFixture(); f.H1 = Mirror(f.H1); f.M5 = Mirror(f.M5);
                var s = AnalyzeContinuation(f); Check(s.IsValidSetup && s.Direction == TradeDirection.Sell, s.SetupReason); }),
            ("SMC continuation requires H1 break", () => {
                var f = ContinuationFixture(); f.H1 = Fixture().H1;
                Check(!AnalyzeContinuation(f).IsValidSetup, "Missing H1 break accepted."); }),
            ("SMC continuation requires FVG and bounded stop", () => {
                var f = ContinuationFixture(); f.M5[24] = f.M5[24] with { Low = 1.1002m };
                Check(!AnalyzeContinuation(f).IsValidSetup, "Missing FVG accepted.");
                f = ContinuationFixture(); f.Options.FtmoSmc.MaximumStopPips = 5;
                Check(!AnalyzeContinuation(f).IsValidSetup, "Oversized continuation stop accepted."); }),
            ("SMC continuation H1 bias switches on opposing break", () => {
                var f = ContinuationFixture();
                Check(FtmoSmcContinuationStrategyEngine.LastStructureBreak(f.H1, 2) == TradeDirection.Buy, "Bullish H1 break missed.");
                f.H1.Add(new("EURUSD", Timeframe.H1, Now, 1.10m, 1.11m, 1.08m, 1.085m, 1));
                Check(FtmoSmcContinuationStrategyEngine.LastStructureBreak(f.H1, 2) == TradeDirection.Sell, "Latest bearish break ignored."); }),
            ("SMC continuation rejects weak displacement and repeated break", () => {
                var f = ContinuationFixture(); f.M5[23] = f.M5[23] with { Open = 1.1011m };
                Check(!AnalyzeContinuation(f).IsValidSetup, "Weak displacement accepted.");
                f = ContinuationFixture(); f.M5[21] = f.M5[21] with { High = 1.1007m, Close = 1.1006m };
                Check(!AnalyzeContinuation(f).IsValidSetup, "Repeated break accepted."); }),
            ("SMC continuation ignores forming bars and preserves ID", () => {
                var f = ContinuationFixture(); var first = AnalyzeContinuation(f);
                f.M5.Add(new("EURUSD", Timeframe.M5, Now, 1.1m, 1.3m, .8m, .9m, 1));
                f.H1.Add(new("EURUSD", Timeframe.H1, Now, 1.1m, 1.3m, .8m, .9m, 1));
                var next = AnalyzeContinuation(f);
                Check(first.IsValidSetup && next.IsValidSetup && first.SetupId == next.SetupId, "Forming data affected continuation."); }),
            ("SMC continuation rejects missing bars and disabled protection", () => {
                var f = ContinuationFixture(); f.M5.RemoveAt(20);
                Check(!AnalyzeContinuation(f).IsValidSetup, "Missing bar accepted.");
                Check(OptionsValidator.Validate(f.Options).IsSuccess, "Continuation registration invalid.");
                f.Options.FtmoProtection.Enabled = false;
                Check(!OptionsValidator.Validate(f.Options).IsSuccess, "Continuation protection bypass."); }),
            ("FTMO SMC accepts bullish structure sweep displacement FVG", () => {
                var f = Fixture(); var s = Analyze(f);
                Check(s.IsValidSetup && s.Direction == TradeDirection.Buy && s.FairValueGap is not null, s.SetupReason);
                Check(s.EntryPrice == 1.1005m && s.StopLoss == 1.0994m && s.TakeProfit == 1.1027m, "Unexpected SMC prices."); }),
            ("FTMO SMC accepts mirrored bearish setup", () => {
                var f = Fixture(); f.H1 = Mirror(f.H1); f.M5 = Mirror(f.M5);
                var s = Analyze(f);
                Check(s.IsValidSetup && s.Direction == TradeDirection.Sell, s.SetupReason);
                Check(s.EntryPrice == 1.0995m && s.StopLoss == 1.1006m && s.TakeProfit == 1.0973m, "Unexpected sell prices."); }),
            ("FTMO SMC requires liquidity sweep", () => {
                var f = Fixture(); f.M5[22] = f.M5[22] with { Low = 1.0998m };
                Check(!Analyze(f).IsValidSetup, "No sweep must reject."); }),
            ("FTMO SMC requires CHOCH close beyond confirmed swing", () => {
                var f = Fixture(); f.M5[18] = f.M5[18] with { High = 1.1015m };
                Check(!Analyze(f).IsValidSetup, "No CHOCH must reject."); }),
            ("FTMO SMC requires displacement", () => {
                var f = Fixture(); f.M5[23] = f.M5[23] with { Open = 1.1011m };
                Check(!Analyze(f).IsValidSetup, "Weak displacement must reject."); }),
            ("FTMO SMC requires minimum FVG size", () => {
                var f = Fixture(); f.M5[24] = f.M5[24] with { Low = 1.10022m };
                Check(!Analyze(f).IsValidSetup, "Tiny FVG must reject."); }),
            ("FTMO SMC premium discount filter is configurable", () => {
                var f = Fixture(); f.H1[10] = f.H1[10] with { High = 1.104m }; f.H1[30] = f.H1[30] with { High = 1.105m };
                Check(!Analyze(f).IsValidSetup, "Premium buy must reject.");
                f.Options.FtmoSmc.UsePremiumDiscountFilter = false;
                Check(Analyze(f).IsValidSetup, "Premium discount toggle ignored."); }),
            ("FTMO SMC target room filter is configurable", () => {
                var f = Fixture(); f.Options.FtmoSmc.RewardRisk = 12;
                Check(!Analyze(f).IsValidSetup, "Target beyond opposing liquidity must reject.");
                f.Options.FtmoSmc.RequireLiquidityTargetRoom = false;
                Check(Analyze(f).IsValidSetup, "Target room toggle ignored."); }),
            ("FTMO SMC ignores forming candles and preserves setup ID", () => {
                var f = Fixture(); var first = Analyze(f);
                f.M5.Add(new("EURUSD", Timeframe.M5, Now, 1.1m, 1.3m, 0.8m, 0.9m, 1));
                f.H1.Add(new("EURUSD", Timeframe.H1, Now, 1.1m, 1.3m, 0.8m, 0.9m, 1));
                var next = Analyze(f, Now.AddSeconds(20));
                Check(next.IsValidSetup && next.SetupId == first.SetupId && next.EntryPrice == first.EntryPrice, "Forming data leaked into signal."); }),
            ("FTMO SMC rejects incomplete pivot confirmations", () => {
                var f = Fixture(); var bars = f.H1.Take(31).ToArray();
                Check(FtmoSmcStrategyEngine.ConfirmedSwings(bars, 2, true).Count == 1, "Unconfirmed right wing counted.");
                f.H1 = f.H1.Select(c => c with { High = 1.103m, Low = 1.099m }).ToList();
                Check(!Analyze(f).IsValidSetup, "Neutral/equal swings accepted as trend."); }),
            ("FTMO SMC rejects gaps and stale data", () => {
                var f = Fixture(); f.M5[10] = f.M5[10] with { OpenedAt = f.M5[10].OpenedAt.AddMinutes(1) };
                Check(!Analyze(f).IsValidSetup, "Gapped candles accepted.");
                Check(!Analyze(Fixture(), Now.AddMinutes(10)).IsValidSetup, "Stale setup accepted."); }),
            ("FTMO SMC accepts a bounded delayed post-break FVG", () => {
                var f = Fixture(); f.Options.FtmoSmc.UsePremiumDiscountFilter = false;
                var original = Analyze(f);
                f.M5.Add(new("EURUSD", Timeframe.M5, Now, 1.1013m, 1.1019m, 1.1012m, 1.1018m, 1));
                f.M5.Add(new("EURUSD", Timeframe.M5, Now.AddMinutes(5), 1.1018m, 1.1020m, 1.1016m, 1.1019m, 1));
                Check(!Analyze(f, Now.AddMinutes(10)).IsValidSetup, "Strict profile accepted delayed FVG.");
                f.Options.FtmoSmc.MaximumFvgDelayAfterBreakBars = 3;
                var delayed = Analyze(f, Now.AddMinutes(10));
                Check(delayed.IsValidSetup, delayed.SetupReason);
                Check(delayed.SetupId == original.SetupId, "A later FVG must not create a duplicate tradable setup ID.");
                f.Options.FtmoSmc.MaximumFvgDelayAfterBreakBars = 1;
                Check(!Analyze(f, Now.AddMinutes(10)).IsValidSetup, "Post-break window cap ignored."); }),
            ("FTMO SMC explains premium-discount rejection", () => {
                var f = Fixture(); f.H1[10] = f.H1[10] with { High = 1.104m }; f.H1[30] = f.H1[30] with { High = 1.105m };
                Check(Analyze(f).SetupReason.Contains("premium/discount"), "Specific rejection reason missing."); }),
            ("FTMO SMC enforces protected profile and parameters", () => {
                var f = Fixture(); Check(OptionsValidator.Validate(f.Options).IsSuccess, "Valid SMC configuration rejected.");
                f.Options.FtmoSmc.MinimumFvgPips = 0;
                Check(!OptionsValidator.Validate(f.Options).IsSuccess, "Invalid SMC setting accepted.");
                f.Options.FtmoSmc.MinimumFvgPips = .5m; f.Options.FtmoProtection.Enabled = false;
                Check(!OptionsValidator.Validate(f.Options).IsSuccess, "SMC risk protection bypass."); })
        };
        foreach (var (name, action) in tests) { action(); Console.WriteLine("PASS " + name); }
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static Feed ContinuationFixture()
    {
        var f = Fixture();
        f.Options.Strategies.Items[0].Engine = "FtmoSmcContinuation";
        f.Options.FtmoSmc.UsePremiumDiscountFilter = false;
        f.Options.FtmoSmc.RequireLiquidityTargetRoom = false;
        f.H1[^1] = f.H1[^1] with { Open = 1.111m, High = 1.114m, Low = 1.110m, Close = 1.113m };
        return f;
    }
    private static TradeSignal AnalyzeContinuation(Feed f) =>
        new FtmoSmcContinuationStrategyEngine(f, new NewsFilter(f.Options), new Session(), f.Options)
            .AnalyzeAsync("EURUSD", Now, default).GetAwaiter().GetResult();
    private static TradeSignal Analyze(Feed feed, DateTimeOffset? now = null) =>
        new FtmoSmcStrategyEngine(feed, new NewsFilter(feed.Options), new Session(), feed.Options)
            .AnalyzeAsync("EURUSD", now ?? Now, default).GetAwaiter().GetResult();
    private static List<Candle> Mirror(IEnumerable<Candle> bars) => bars.Select(c => c with
        { Open = 2.2m - c.Open, Close = 2.2m - c.Close, High = 2.2m - c.Low, Low = 2.2m - c.High }).ToList();

    private static Feed Fixture()
    {
        var options = new TradingBotOptions { Broker = "MT5", RiskPercentPerTrade = .25m, MaxRiskPercentPerTrade = .5m,
            FtmoProtection = new() { Enabled = true }, UseNewsFilter = false };
        options.Strategies.ActiveStrategyId = "smc-test";
        options.Strategies.Items = [new() { Id = "smc-test", Engine = "FtmoSmc", Symbol = "EURUSD",
            MacroBiasTimeframe = "H1", EntryTimeframe = "M5" }];
        options.Normalize();
        var h1 = Enumerable.Range(0, 48).Select(i => new Candle("EURUSD", Timeframe.H1, Now.AddHours(i - 48),
            1.101m, 1.103m, 1.099m, 1.101m, 1)).ToList();
        h1[10] = h1[10] with { High = 1.110m }; h1[30] = h1[30] with { High = 1.112m };
        h1[20] = h1[20] with { Low = 1.090m }; h1[40] = h1[40] with { Low = 1.092m };
        var m5 = Enumerable.Range(0, 25).Select(i => new Candle("EURUSD", Timeframe.M5, Now.AddMinutes((i - 25) * 5),
            1.1000m, 1.1002m, 1.0998m, 1.1000m, 1)).ToList();
        m5[18] = m5[18] with { High = 1.1005m };
        m5[22] = m5[22] with { Low = 1.0995m };
        m5[23] = m5[23] with { Open = 1.1000m, Close = 1.1012m, High = 1.1013m, Low = 1.0999m };
        m5[24] = m5[24] with { Open = 1.1012m, Close = 1.1013m, High = 1.1014m, Low = 1.1008m };
        return new Feed(options, h1, m5);
    }

    private sealed class Session : ISessionClock { public SessionName GetCurrentSession(DateTimeOffset at) => SessionName.London; }
    private sealed class Feed(TradingBotOptions options, List<Candle> h1, List<Candle> m5) : IMarketDataProvider
    {
        public TradingBotOptions Options { get; } = options;
        public List<Candle> H1 { get; set; } = h1;
        public List<Candle> M5 { get; set; } = m5;
        public Task<IReadOnlyList<Candle>> GetCandlesAsync(string symbol, Timeframe timeframe, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Candle>>((timeframe == Timeframe.H1 ? H1 : M5).Where(c => c.OpenedAt >= from && c.OpenedAt <= to).ToArray());
    }
}
