using System.Text.Json;
using TradingBot.Application;
using TradingBot.Domain;
using TradingBot.Infrastructure.Filters;

internal static class LiveTradingTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-02-03T10:00:00Z");
    private static readonly TradeSignal Signal = new("EURUSD", TradeDirection.Buy, 1.1m, 1.099m, 1.102m, 2,
        SessionName.London, true, "test", SetupId: "live-test");
    public static void Run()
    {
        Test("Automatic calendar refresh retains only unexpired coverage after HTTP failure", async () => {
            var o = Options(); o.UseNewsFilter = true; o.NewsCalendar.Enabled = true; o.NewsCalendar.FeedUrl = "https://calendar.test/feed";
            var snapshot = new NewsCalendarSnapshot(Now, Now.AddHours(-1), Now.AddDays(1), [], "test provider");
            var handler = new CalendarHandler(snapshot);
            using var client = new HttpClient(handler);
            var calendar = new HttpNewsCalendar(o, client);
            await calendar.RefreshAsync(Now, default);
            await calendar.RefreshAsync(Now.AddSeconds(10), default);
            Check(handler.Calls == 1, "Refresh interval ignored.");
            handler.Fail = true;
            try { await calendar.RefreshAsync(Now.AddMinutes(10), default); throw new Exception("HTTP failure ignored."); }
            catch (HttpRequestException) { }
            Check(NewsCalendarState.Current(o, Now.AddMinutes(10)) is not null, "Valid previous coverage discarded.");
            Check(NewsCalendarState.Current(o, Now.AddMinutes(61)) is null, "Failed refresh extended data lifetime.");
        });
        Test("Event sink failure pauses entries while protection continues", async () => {
            var o = Options(); o.FtmoProtection.EntryAnalysisIntervalSeconds = 1; o.FtmoProtection.MonitorIntervalSeconds = 1;
            var broker = new Broker();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2.5));
            try { await new LiveTradingCoordinator(o, new Strategy(), broker, new FailingEvents()).RunAsync(false, stop.Token); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            Check(broker.Protections >= 2 && broker.Submissions == 0, "Event failure stopped protection or allowed entry.");
        });
        Test("Persistence recovery requires a subsequent protection cycle", async () => {
            var sink = new ControlledEvents { Fail = true }; var broker = new Broker();
            var runner = new LiveTradingCoordinator(Options(), new Strategy(), broker, sink, new Clock());
            await runner.RunAsync(true, default);
            sink.Fail = false;
            await runner.RunAsync(true, default);
            Check(broker.Submissions == 0, "Recovered persistence reused old protection.");
            await runner.RunAsync(true, default);
            Check(broker.Submissions == 1, "Fresh reconciled recovery did not resume.");
        });
        Test("Stalled persistence is bounded without overlapping writes", async () => {
            var sink = new ControlledEvents { Stall = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            var writer = new ResilientLiveEvents(sink, TimeSpan.FromMilliseconds(50));
            try {
                Check(!await writer.WriteAsync("test", new {}, default), "Stalled write did not time out.");
                Check(!await writer.RecoverAsync(default) && !writer.IsHealthy && sink.Calls == 1, "Overlapping stalled writes.");
            } finally { sink.Stall.SetResult(); }
        });
        Test("Accepted order is not duplicated after evidence failure and recovery", async () => {
            var sink = new ControlledEvents { FailName = "order_created" }; var broker = new Broker();
            var runner = new LiveTradingCoordinator(Options(), new Strategy(), broker, sink, new Clock());
            await runner.RunAsync(true, default);
            sink.FailName = null;
            await runner.RunAsync(true, default);
            await runner.RunAsync(true, default);
            Check(broker.Submissions == 1, "Accepted order duplicated after persistence recovery.");
        });
        Test("Unavailable accounting blocks entries until fresh accounting returns", async () => {
            var broker = new Broker { AccountingAvailable = false };
            var runner = new LiveTradingCoordinator(Options(), new Strategy(), broker, new Events(), new Clock());
            await runner.RunAsync(true, default);
            Check(broker.Submissions == 0 && broker.Protections == 1, "Unavailable accounting accepted.");
            broker.AccountingAvailable = true;
            await runner.RunAsync(true, default);
            Check(broker.Submissions == 1 && broker.Protections == 2, "Accounting recovery failed.");
        });
        Test("Safe submission retries are delayed and bounded", async () => {
            var o = Options(); o.FtmoProtection.MaximumSubmissionAttempts = 2;
            var clock = new Clock();
            var broker = new Broker { Submission = new(false, null, "preflight unavailable", true, "rejected") };
            var runner = new LiveTradingCoordinator(o, new Strategy(), broker, new Events(), clock);
            await runner.RunAsync(true, default);
            await runner.RunAsync(true, default);
            Check(broker.Submissions == 1, "Retry delay bypassed.");
            clock.Now = clock.Now.AddSeconds(20);
            await runner.RunAsync(true, default);
            clock.Now = clock.Now.AddSeconds(20);
            await runner.RunAsync(true, default);
            Check(broker.Submissions == 2, "Retry cap bypassed.");
        });
        Test("Uncertain submissions are never retried", async () => {
            var broker = new Broker { Submission = new(false, null, "timeout") };
            var runner = new LiveTradingCoordinator(Options(), new Strategy(), broker, new Events(), new Clock());
            await runner.RunAsync(true, default); await runner.RunAsync(true, default);
            Check(broker.Submissions == 1, "Uncertain execution retried.");
        });
        Test("Noncooperative timed-out analysis cannot overlap", async () => {
            var o = Options(); o.FtmoProtection.AnalysisTimeoutSeconds = 1;
            var completion = new TaskCompletionSource<TradeSignal>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var strategy = new Strategy { Action = _ => { Interlocked.Increment(ref calls); return completion.Task; } };
            var broker = new Broker();
            var runner = new LiveTradingCoordinator(o, strategy, broker, new Events(), new Clock());
            try
            {
                try { await runner.RunAsync(true, default); throw new InvalidOperationException("Analysis did not time out."); }
                catch (OperationCanceledException) { }
                await runner.RunAsync(true, default);
                Check(calls == 1 && broker.Protections == 2 && broker.Submissions == 0, "Overlapping analysis or unprotected execution.");
            }
            finally { completion.TrySetResult(Signal); }
        });
        Test("Unsupported challenge models block entry", () => {
            var o = Options(); o.FtmoProtection.LossModel = "Trailing";
            Check(LiveEntryPolicy.BlockReason(o) is not null, "Unsupported trailing model accepted.");
            o.FtmoProtection.LossModel = "Static"; o.FtmoProtection.ResetTimeZone = "UTC";
            Check(LiveEntryPolicy.BlockReason(o) is not null, "Unsupported reset accepted.");
            return Task.CompletedTask;
        });
        Test("Calendar feed freshness cannot be replaced by static fallback", () => {
            var o = Options(); o.UseNewsFilter = true; o.NewsCalendar.Enabled = true;
            o.NewsCoverageFromUtc = Now.AddDays(-1); o.NewsCoverageUntilUtc = Now.AddDays(1);
            var filter = new NewsFilter(o);
            Check(filter.IsBlocked(Now, "EURUSD", out _), "Automatic feed silently used static coverage.");
            var snapshot = new NewsCalendarSnapshot(Now, Now.AddHours(-1), Now.AddDays(1), [], "test feed");
            HttpNewsCalendar.Validate(snapshot, Now, 60); o.LiveNewsCalendar = snapshot;
            Check(!filter.IsBlocked(Now, "EURUSD", out _), "Fresh reviewed coverage blocked.");
            Check(filter.IsBlocked(Now.AddHours(2), "EURUSD", out _), "Stale feed accepted despite long coverage.");
            try { HttpNewsCalendar.Validate(snapshot with { BlackoutWindowsUtc = ["invalid"] }, Now, 60); throw new Exception("Malformed event accepted."); }
            catch (InvalidOperationException) { }
            return Task.CompletedTask;
        });
        Test("Strategy parameter changes receive distinct evidence versions", () => {
            var o = Options(); var first = LiveStrategyIdentity.Version(o);
            Check(first == LiveStrategyIdentity.Version(o), "Version is nondeterministic.");
            o.FtmoSmc.EntryRetracementFraction = .7m;
            Check(first != LiveStrategyIdentity.Version(o), "Changed parameters reused evidence version.");
            return Task.CompletedTask;
        });
        Test("Disabled legacy broker cannot fabricate paper orders", async () => {
            var broker = new TradingBot.Infrastructure.Broker.CTraderBrokerClient(new TradingBotOptions { LiveTradingEnabled = false });
            var result = await broker.PlaceOrderAsync(new OrderRequest("EURUSD", TradeDirection.Buy, OrderType.Limit,
                1.1m, 1.099m, 1.102m, .01m, .25m), default);
            Check(!result.IsSuccess, "Fabricated order accepted.");
        });
        Test("Live defaults enabled and explicit pause blocks entries", async () => {
            var o = Options(); Check(o.LiveTradingEnabled && o.MT5.AllowLiveOrderCreation, "Live defaults missing.");
            o.LiveTradingEnabled = false; var broker = new Broker();
            await new LiveTradingCoordinator(o, new Strategy(), broker, new Events(), new Clock()).RunAsync(true, default);
            Check(broker.Protections == 1 && broker.Submissions == 0, "Paused entries disabled protection or submitted.");
        });
        Test("Live rule and account permission matrix", async () => {
            foreach (var mutation in new Action<TradingBotOptions>[] { o => o.MT5.AllowLiveOrderCreation = false,
                o => o.MT5.AccountId = 0, o => o.FtmoProtection.RulesConfirmed = false,
                o => o.FtmoProtection.AccountVariant = "Unconfirmed", o => o.FtmoProtection.Enabled = false })
            {
                var o = Options(); mutation(o); var broker = new Broker();
                await new LiveTradingCoordinator(o, new Strategy(), broker, new Events(), new Clock()).RunAsync(true, default);
                Check(broker.Submissions == 0, "Permission bypass.");
            }
        });
        Test("Live accepted setup is submitted once", async () => {
            var b = new Broker(); var runner = new LiveTradingCoordinator(Options(), new Strategy(), b, new Events(), new Clock());
            await runner.RunAsync(true, default); await runner.RunAsync(true, default);
            Check(b.Submissions == 1, "Duplicate or missing submission.");
        });
        Test("Live stale protection after analysis prevents submission", async () => {
            var c = new Clock(); var b = new Broker(); var s = new Strategy { Action = _ => { c.Now = c.Now.AddMinutes(1); return Task.FromResult(Signal); } };
            await new LiveTradingCoordinator(Options(), s, b, new Events(), c).RunAsync(true, default);
            Check(b.Submissions == 0 && b.Snapshots == 0, "Stale heartbeat accepted.");
        });
        Test("Protection continues during stalled strategy analysis", async () => {
            var o = Options(); o.FtmoProtection.MonitorIntervalSeconds = 1; o.FtmoProtection.EntryAnalysisIntervalSeconds = 1;
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var b = new Broker();
            var s = new Strategy { Action = async token => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return Signal; } };
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var run = new LiveTradingCoordinator(o, s, b, new Events(), new Clock()).RunAsync(false, stop.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(4));
            var initial = b.Protections;
            await Task.Delay(1600, stop.Token);
            Check(b.Protections > initial, "Strategy stalled protection scheduler.");
            stop.Cancel(); try { await run; } catch (OperationCanceledException) { }
        });
        Test("Live news requires declared current coverage", () => {
            var o = Options(); o.UseNewsFilter = true; var filter = new NewsFilter(o);
            Check(filter.IsBlocked(Now, "EURUSD", out _), "Missing news coverage accepted.");
            o.NewsCoverageFromUtc = Now.AddHours(-1); o.NewsCoverageUntilUtc = Now.AddHours(1);
            Check(!filter.IsBlocked(Now, "EURUSD", out _), "Reviewed interval without events rejected.");
            o.NewsBlackoutWindowsUtc = [$"{Now:O}/{Now:O}"];
            Check(filter.IsBlocked(Now, "EURUSD", out _), "News blackout ignored.");
            return Task.CompletedTask;
        });
        Test("Forex calendar distinguishes closure from missing weekday bars", () => {
            Check(!ForexDataCalendar.IsExpectedOpen(DateTimeOffset.Parse("2026-02-07T12:00:00Z"), []), "Saturday considered open.");
            Check(ForexDataCalendar.IsExpectedOpen(Now, []), "Tuesday considered closed.");
            Check(!ForexDataCalendar.IsExpectedOpen(Now, [$"{Now:O}/{Now.AddHours(1):O}"]), "Broker closure ignored.");
            return Task.CompletedTask;
        });
        Test("Shared CSharp Python protection boundary cases", () => {
            var cases = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "risk-cases.json")));
            foreach (var c in cases.RootElement.EnumerateArray())
            {
                var a = Account with { Equity = c.GetProperty("equity").GetDecimal(), ExistingRisk = c.GetProperty("existingRisk").GetDecimal() };
                if (c.TryGetProperty("dailyStartingBalance", out var balance)) a = a with { DailyStartingBalance = balance.GetDecimal() };
                if (c.TryGetProperty("exposureKnown", out var known)) a = a with { ExposureKnown = known.GetBoolean() };
                if (c.TryGetProperty("currency", out var currency)) a = a with { Currency = currency.GetString()! };
                var policy = new FtmoProtectionOptions { KillSwitch = c.TryGetProperty("killSwitch", out var kill) && kill.GetBoolean() };
                Check(new FtmoRiskEngine(policy).ProtectionStatus(a).Code.ToString() == c.GetProperty("code").GetString(), "Shared risk case mismatch.");
            }
            return Task.CompletedTask;
        });
    }
    private static void Test(string name, Func<Task> action) { action().GetAwaiter().GetResult(); Console.WriteLine("PASS " + name); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static TradingBotOptions Options() => new() { Broker = "MT5", UseNewsFilter = false,
        FtmoProtection = new() { Enabled = true, RulesConfirmed = true, AccountVariant = "Challenge" }, MT5 = new() { AccountId = 123 } };
    private static readonly FtmoAccountState Account = new(100000, 100000, 100000, 0, 0, 0, 0, null, 0);
    private sealed class Clock : TimeProvider { public DateTimeOffset Now = LiveTradingTests.Now; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Strategy : IStrategyEngine
    {
        public Func<CancellationToken, Task<TradeSignal>> Action = _ => Task.FromResult(Signal);
        public Task<TradeSignal> AnalyzeAsync(string symbol, DateTimeOffset now, CancellationToken token) => Action(token);
    }
    private sealed class Broker : ILiveTradingBroker
    {
        public int Protections, Snapshots, Submissions;
        public bool AccountingAvailable = true;
        public LiveSubmission Submission = new(true, "1", null, false, "accepted");
        public Task<LiveProtectionSnapshot> ProtectAsync(CancellationToken token) { Interlocked.Increment(ref Protections); return Task.FromResult(new LiveProtectionSnapshot(Account, false, null, AccountingAvailable: AccountingAvailable)); }
        public Task<LiveOrderSnapshot> SnapshotAsync(TradeSignal signal, CancellationToken token) { Snapshots++; return Task.FromResult(new LiveOrderSnapshot(Account, new(100, .01m, 100, .01m, 10))); }
        public Task<LiveSubmission> SubmitAsync(TradeSignal signal, decimal lots, CancellationToken token) { Submissions++; return Task.FromResult(Submission); }
    }
    private sealed class Events : ILiveTradingEvents
    {
        public Task WriteAsync(string name, object value, CancellationToken token) => Task.CompletedTask;
        public Task TrackAsync(CancellationToken token) => Task.CompletedTask;
    }
    private sealed class FailingEvents : ILiveTradingEvents
    {
        public Task WriteAsync(string name, object value, CancellationToken token) => throw new IOException("test sink failure");
        public Task TrackAsync(CancellationToken token) => Task.CompletedTask;
    }
    private sealed class ControlledEvents : ILiveTradingEvents
    {
        public bool Fail;
        public string? FailName;
        public int Calls;
        public TaskCompletionSource? Stall;
        public Task WriteAsync(string name, object value, CancellationToken token) {
            Interlocked.Increment(ref Calls);
            if (Fail || FailName == name) throw new UnauthorizedAccessException("test denied");
            return Stall?.Task ?? Task.CompletedTask;
        }
        public Task TrackAsync(CancellationToken token) => Task.CompletedTask;
    }
    private sealed class CalendarHandler(NewsCalendarSnapshot snapshot) : HttpMessageHandler
    {
        public int Calls;
        public bool Fail;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            if (Fail) throw new HttpRequestException("test feed unavailable");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = System.Net.Http.Json.JsonContent.Create(snapshot) });
        }
    }
}
