using System.Text.Json;
using TradingBot.Application;
using TradingBot.Domain;

namespace TradingBot.Infrastructure.MetaTrader;

public sealed class MT5LiveTradingBroker(MT5BridgeClient client) : ILiveTradingBroker
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static FtmoAccountState Account(JsonElement root)
    {
        var state = root.GetProperty("account").Deserialize<FtmoAccountState>(Json)
            ?? throw new InvalidOperationException("Bridge account snapshot is missing.");
        var times = root.GetProperty("entryTimes").Deserialize<DateTimeOffset[]>(Json) ?? [];
        return state with { TradingDays = times.Select(FtmoClock.TradingDay).Distinct().Count() };
    }
    private async Task<JsonElement> Request(string operation, TradeSignal? signal, decimal lots, CancellationToken token)
    {
        var response = await client.FtmoRequestAsync(operation, signal, lots, token);
        if (!response.IsSuccess) throw new InvalidOperationException(response.Error);
        return response.Value;
    }
    public async Task<LiveProtectionSnapshot> ProtectAsync(CancellationToken token)
    {
        var root = await Request("protect", null, 0, token);
        return new(Account(root), root.GetProperty("halted").GetBoolean(),
            root.TryGetProperty("reason", out var reason) ? reason.GetString() : null,
            root.GetProperty("haltCode").GetString() ?? "Unknown",
            root.GetProperty("actions").Deserialize<string[]>(Json),
            root.GetProperty("errors").Deserialize<string[]>(Json),
            root.GetProperty("targetReached").GetBoolean(),
            root.GetProperty("accountingAvailable").GetBoolean(),
            root.TryGetProperty("accountingError", out var accountingError) ? accountingError.GetString() : null);
    }
    public async Task<LiveOrderSnapshot> SnapshotAsync(TradeSignal signal, CancellationToken token)
    {
        var root = await Request("snapshot", signal, 0, token);
        return new(Account(root), root.GetProperty("instrument").Deserialize<FtmoInstrument>(Json)
            ?? throw new InvalidOperationException("Bridge instrument snapshot is missing."));
    }
    public async Task<LiveSubmission> SubmitAsync(TradeSignal signal, decimal lots, CancellationToken token)
    {
        var result = await client.FtmoRequestAsync("orders", signal, lots, token);
        if (!result.IsSuccess) return new(false, null, result.Error);
        var root = result.Value;
        return new(root.GetProperty("accepted").GetBoolean(), root.TryGetProperty("brokerOrderId", out var id) ? id.GetString() : null,
            root.TryGetProperty("error", out var error) ? error.GetString() : null,
            root.TryGetProperty("retryable", out var retry) && retry.GetBoolean(),
            root.TryGetProperty("executionState", out var state) ? state.GetString() ?? "unknown" : "unknown");
    }
}
