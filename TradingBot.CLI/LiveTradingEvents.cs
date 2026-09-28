using System.Text.Json;
using TradingBot.Application;
using TradingBot.Infrastructure.Filters;
using TradingBot.Infrastructure.MetaTrader;
using TradingBot.Reporting;

internal sealed class LiveTradingEvents : ILiveTradingEvents, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TradingBotOptions options;
    private readonly MT5BridgeClient bridge;
    private readonly OperationalLogWriter log;
    private readonly HttpClient calendarClient = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
    private readonly HttpNewsCalendar calendar;
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly string runId = Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, DateTimeOffset> alertTimes = new();

    public LiveTradingEvents(TradingBotOptions options, MT5BridgeClient bridge)
    {
        this.options = options;
        this.bridge = bridge;
        log = new(options);
        calendar = new(options, calendarClient);
    }

    public async Task WriteAsync(string name, object value, CancellationToken token)
    {
        await writes.WaitAsync(token);
        try
        {
            await log.WriteAsync(runId, name, new { strategyId = options.ActiveStrategy.Id,
                strategyVersion = LiveStrategyIdentity.Version(options), engine = options.ActiveStrategy.Engine, data = value }, token);
            var data = JsonSerializer.SerializeToElement(value, Json);
            if (name is "live_signal" or "live_risk" or "order_created" or "order_rejected" or "live_evidence_probe")
            {
                Directory.CreateDirectory(options.FtmoProtection.StateDirectory);
                var evidence = JsonSerializer.Serialize(new { timestampUtc = DateTimeOffset.UtcNow, runId,
                    strategyVersion = LiveStrategyIdentity.Version(options), eventName = name, data }, Json);
                await File.AppendAllTextAsync(Path.Combine(options.FtmoProtection.StateDirectory,
                    $"execution-evidence-{DateTimeOffset.UtcNow:yyyyMMdd}.jsonl"), evidence + Environment.NewLine, token);
            }
            var unhealthy = name == "live_cycle_failed" || name == "live_protection" &&
                data.GetProperty("snapshot").GetProperty("halted").GetBoolean();
            if (!options.FtmoProtection.LocalAlertsEnabled || !unhealthy) return;
            var key = name == "live_protection" ? data.GetProperty("snapshot").GetProperty("haltCode").GetString() ?? name
                : data.TryGetProperty("action", out var action) ? action.GetString() ?? name : name;
            var now = DateTimeOffset.UtcNow;
            if (alertTimes.TryGetValue(key, out var last) && now - last < TimeSpan.FromMinutes(options.FtmoProtection.AlertRepeatMinutes)) return;
            Directory.CreateDirectory(options.FtmoProtection.StateDirectory);
            var alert = JsonSerializer.Serialize(new { timestampUtc = now, runId, code = key, eventName = name, data }, Json);
            await File.AppendAllTextAsync(Path.Combine(options.FtmoProtection.StateDirectory, "alerts.jsonl"), alert + Environment.NewLine, token);
            Console.Error.WriteLine("LIVE ALERT " + alert);
            alertTimes[key] = now;
        }
        finally { writes.Release(); }
    }

    public async Task RefreshAsync(CancellationToken token)
    {
        await calendar.RefreshAsync(DateTimeOffset.UtcNow, token);
    }

    public async Task TrackAsync(CancellationToken token)
    {
        // Independent of CSV tracking: every unattended run publishes current health and evidence.
        var health = await bridge.FtmoRequestAsync("status", null, 0, token);
        if (!health.IsSuccess) throw new InvalidOperationException(health.Error);
        Directory.CreateDirectory(options.FtmoProtection.StateDirectory);
        var path = Path.Combine(options.FtmoProtection.StateDirectory, $"health-{options.MT5.AccountId}.json");
        await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(new { checkedAtUtc = DateTimeOffset.UtcNow,
            bridge = health.Value, news = NewsCalendarState.Current(options, DateTimeOffset.UtcNow) }, Json), token);
        File.Move(path + ".tmp", path, true);
        if (!options.TradeTracking.Enabled) return;
        var result = await bridge.GetClosedTradesAsync(DateTimeOffset.UtcNow.AddDays(-options.TradeTracking.LookbackDays), DateTimeOffset.UtcNow, token);
        if (!result.IsSuccess) throw new InvalidOperationException(result.Error);
        var written = await new ClosedTradeTrackingWriter(options).AppendAsync(result.Value!, token);
        LiveConsole.WriteTracking(result.Value!.Count, written, options.TradeTracking.Directory);
        await WriteAsync("live_reconciled_history", new { fetched = result.Value!.Count, written }, token);
    }

    // A timed-out filesystem call may still own the write gate; let it release before GC.
    public void Dispose() { calendarClient.Dispose(); }
}

