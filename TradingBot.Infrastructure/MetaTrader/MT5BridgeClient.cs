using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TradingBot.Application;
using TradingBot.Domain;
using TradingBot.Shared;

namespace TradingBot.Infrastructure.MetaTrader;

public sealed class MT5BridgeClient
{
    private readonly TradingBotOptions _options;
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public MT5BridgeClient(TradingBotOptions options)
    {
        _options = options;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(options.MT5.BridgeBaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(options.MT5.TimeoutSeconds)
        };
    }

    public async Task<Result<JsonElement>> GetHealthAsync(CancellationToken cancellationToken) =>
        await GetJsonAsync("health", "MT5 bridge health", cancellationToken);

    public async Task<Result<JsonElement>> GetAccountAsync(CancellationToken cancellationToken) =>
        await GetJsonAsync("account", "MT5 account details", cancellationToken);

    public async Task<Result<AccountSnapshot>> GetAccountSnapshotAsync(CancellationToken cancellationToken)
    {
        var result = await GetJsonAsync("account-risk", "MT5 account risk state", cancellationToken);
        if (!result.IsSuccess)
        {
            return Result<AccountSnapshot>.Failure(result.Error!);
        }

        var root = result.Value;
        return Result<AccountSnapshot>.Success(new AccountSnapshot(
            ReadDecimal(root, "balance"),
            ReadDecimal(root, "equity"),
            ReadDecimal(root, "dailyRealizedProfitLoss"),
            ReadDecimal(root, "weeklyRealizedProfitLoss"),
            (int)(ReadLong(root, "consecutiveLosses") ?? 0),
            (int)(ReadLong(root, "consecutiveLosingDays") ?? 0),
            ReadDecimal(root, "dailyStartingBalance"),
            ReadDecimal(root, "initialBalance"),
            (int)(ReadLong(root, "tradingDays") ?? 0)));
    }

    public async Task<Result<JsonElement>> GetSymbolAsync(string symbol, CancellationToken cancellationToken) =>
        await GetJsonAsync($"symbols/{Uri.EscapeDataString(symbol)}", "MT5 symbol metadata", cancellationToken);

    public async Task<Result<MarketExecutionSnapshot>> GetMarketExecutionSnapshotAsync(string symbol, CancellationToken cancellationToken)
    {
        var result = await GetJsonAsync($"quote/{Uri.EscapeDataString(symbol)}", "MT5 quote", cancellationToken);
        if (!result.IsSuccess)
        {
            return Result<MarketExecutionSnapshot>.Failure(result.Error!);
        }

        var root = result.Value;
        return Result<MarketExecutionSnapshot>.Success(new MarketExecutionSnapshot(
            ReadString(root, "symbol") ?? symbol,
            ReadDecimal(root, "bid"),
            ReadDecimal(root, "ask"),
            ReadDecimal(root, "spreadPips"),
            ReadDateTime(root, "time") ?? DateTimeOffset.UtcNow));
    }

    public async Task<Result<StaleOrderCleanupResult>> CancelStalePendingOrdersAsync(CancellationToken cancellationToken)
    {
        if (!_options.MT5.CancelStalePendingOrders)
        {
            return Result<StaleOrderCleanupResult>.Success(new StaleOrderCleanupResult(0, 0, []));
        }

        var expirationMinutes = _options.MT5.PendingOrderExpirationHours * 60;
        var path = $"orders?symbol={Uri.EscapeDataString(_options.Symbol)}&magicNumber={_options.MT5.MagicNumber}&olderThanMinutes={expirationMinutes}";
        var ordersResult = await GetJsonAsync(path, "MT5 stale pending orders", cancellationToken);
        if (!ordersResult.IsSuccess)
        {
            return Result<StaleOrderCleanupResult>.Failure(ordersResult.Error!);
        }

        var array = FindArray(ordersResult.Value, "orders") ?? (ordersResult.Value.ValueKind == JsonValueKind.Array ? ordersResult.Value : default);
        if (array.ValueKind != JsonValueKind.Array)
        {
            return Result<StaleOrderCleanupResult>.Success(new StaleOrderCleanupResult(0, 0, []));
        }

        var cancelled = new List<string>();
        foreach (var order in array.EnumerateArray())
        {
            var ticket = ReadString(order, "ticket") ?? ReadString(order, "order");
            if (string.IsNullOrWhiteSpace(ticket))
            {
                continue;
            }

            var cancelResult = await DeleteJsonAsync($"orders/{Uri.EscapeDataString(ticket)}", "MT5 stale pending order cancellation", cancellationToken);
            if (!cancelResult.IsSuccess)
            {
                return Result<StaleOrderCleanupResult>.Failure(cancelResult.Error!);
            }

            cancelled.Add(ticket);
        }

        return Result<StaleOrderCleanupResult>.Success(new StaleOrderCleanupResult(array.GetArrayLength(), cancelled.Count, cancelled));
    }

    public async Task<Result<IReadOnlyList<ClosedTradeReport>>> GetClosedTradesAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var path = $"closed-trades?symbol={Uri.EscapeDataString(_options.Symbol)}&magicNumber={_options.MT5.MagicNumber}&from={Uri.EscapeDataString(from.ToString("O", CultureInfo.InvariantCulture))}&to={Uri.EscapeDataString(to.ToString("O", CultureInfo.InvariantCulture))}";
        var result = await GetJsonAsync(path, "MT5 closed trades", cancellationToken);
        if (!result.IsSuccess)
        {
            return Result<IReadOnlyList<ClosedTradeReport>>.Failure(result.Error!);
        }

        var array = FindArray(result.Value, "trades") ?? (result.Value.ValueKind == JsonValueKind.Array ? result.Value : default);
        if (array.ValueKind != JsonValueKind.Array)
        {
            return Result<IReadOnlyList<ClosedTradeReport>>.Success([]);
        }

        var trades = array.EnumerateArray()
            .Select(item => new ClosedTradeReport(
                ReadString(item, "tradeId") ?? "",
                ReadString(item, "symbol") ?? _options.Symbol,
                ReadString(item, "session") ?? "Unknown",
                ReadString(item, "direction") ?? "",
                ReadDecimal(item, "entryPrice"),
                ReadDecimal(item, "closePrice"),
                ReadDecimal(item, "stopLossPrice"),
                ReadDecimal(item, "takeProfitPrice"),
                ReadDecimal(item, "riskRewardRatio"),
                ReadDateTime(item, "openedAt") ?? DateTimeOffset.MinValue,
                ReadDateTime(item, "closedAt") ?? DateTimeOffset.MinValue,
                ReadDecimal(item, "volume"),
                ReadDecimal(item, "profit"),
                ReadDecimal(item, "commission"),
                ReadDecimal(item, "swap"),
                ReadDecimal(item, "netProfit"),
                ReadLong(item, "magicNumber") ?? _options.MT5.MagicNumber,
                ReadString(item, "comment") ?? ""))
            .Where(trade => !string.IsNullOrWhiteSpace(trade.TradeId))
            .OrderBy(trade => trade.ClosedAt)
            .ToArray();

        return Result<IReadOnlyList<ClosedTradeReport>>.Success(trades);
    }

    public async Task<Result<IReadOnlyList<Candle>>> GetCandlesAsync(
        string symbol,
        Timeframe timeframe,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var path = $"candles?symbol={Uri.EscapeDataString(symbol)}&timeframe={timeframe}&from={Uri.EscapeDataString(from.ToString("O", CultureInfo.InvariantCulture))}&to={Uri.EscapeDataString(to.ToString("O", CultureInfo.InvariantCulture))}";
        var result = await GetJsonAsync(path, "MT5 candles", cancellationToken);
        if (!result.IsSuccess)
        {
            return Result<IReadOnlyList<Candle>>.Failure(result.Error!);
        }

        return Result<IReadOnlyList<Candle>>.Success(ParseCandles(result.Value, symbol, timeframe));
    }

    public async Task<Result<IReadOnlyList<MarketTick>>> GetTicksAsync(
        string symbol,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var path = $"ticks?symbol={Uri.EscapeDataString(symbol)}&from={Uri.EscapeDataString(from.ToString("O", CultureInfo.InvariantCulture))}&to={Uri.EscapeDataString(to.ToString("O", CultureInfo.InvariantCulture))}";
        var result = await GetJsonAsync(path, "MT5 ticks", cancellationToken);
        if (!result.IsSuccess)
        {
            return Result<IReadOnlyList<MarketTick>>.Failure(result.Error!);
        }

        return Result<IReadOnlyList<MarketTick>>.Success(ParseTicks(result.Value, symbol));
    }

    public async Task<Result<ActiveTradeSummary>> GetActiveTradesAsync(CancellationToken cancellationToken)
    {
        var positionsResult = await GetJsonAsync($"positions?symbol={Uri.EscapeDataString(_options.Symbol)}&magicNumber={_options.MT5.MagicNumber}", "MT5 positions", cancellationToken);
        if (!positionsResult.IsSuccess)
        {
            return Result<ActiveTradeSummary>.Failure(positionsResult.Error!);
        }

        var ordersResult = await GetJsonAsync($"orders?symbol={Uri.EscapeDataString(_options.Symbol)}&magicNumber={_options.MT5.MagicNumber}", "MT5 orders", cancellationToken);
        if (!ordersResult.IsSuccess)
        {
            return Result<ActiveTradeSummary>.Failure(ordersResult.Error!);
        }

        return Result<ActiveTradeSummary>.Success(new ActiveTradeSummary(
            CountArrayPayload(positionsResult.Value, "positions"),
            CountArrayPayload(ordersResult.Value, "orders")));
    }

    public async Task<Result<BrokerOrderCreationResult>> CreateLimitOrderAsync(
        TradeSignal signal,
        decimal lots,
        CancellationToken cancellationToken)
    {
        var clientOrderId = $"tradingbot-mt5-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
        var payload = new
        {
            clientOrderId,
            accountId = _options.MT5.AccountId,
            symbol = signal.Symbol,
            side = signal.Direction.ToString().ToUpperInvariant(),
            orderType = "LIMIT",
            lots,
            volume = (long)Math.Round(lots * _options.MT5.UnitsPerLot, MidpointRounding.AwayFromZero),
            price = signal.EntryPrice,
            stopLoss = signal.StopLoss,
            takeProfit = signal.TakeProfit,
            riskReward = signal.RiskReward,
            magicNumber = _options.MT5.MagicNumber,
            expirationMinutes = _options.MT5.PendingOrderExpirationHours * 60,
            maxSlippagePoints = _options.MT5.MaxSlippagePoints
        };

        try
        {
            var json = JsonSerializer.Serialize(payload, _jsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync("orders", content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Result<BrokerOrderCreationResult>.Failure($"MT5 bridge rejected order creation: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
            }

            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var root = document.RootElement;
            return Result<BrokerOrderCreationResult>.Success(new BrokerOrderCreationResult(
                ReadString(root, "clientOrderId") ?? clientOrderId,
                ReadString(root, "brokerOrderId") ?? ReadString(root, "orderId") ?? "",
                ReadLong(root, "accountId") ?? _options.MT5.AccountId,
                ReadLong(root, "symbolId"),
                signal.Direction.ToString().ToUpperInvariant(),
                lots,
                (long)Math.Round(lots * _options.MT5.UnitsPerLot, MidpointRounding.AwayFromZero),
                signal.EntryPrice,
                signal.StopLoss,
                signal.TakeProfit,
                signal.RiskReward));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return Result<BrokerOrderCreationResult>.Failure($"Unable to create MT5 order through bridge '{_options.MT5.BridgeBaseUrl}': {exception.Message}");
        }
    }

    private async Task<Result<JsonElement>> DeleteJsonAsync(string path, string area, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.DeleteAsync(path, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Result<JsonElement>.Failure($"Unable to execute {area}: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
            }

            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            return Result<JsonElement>.Success(document.RootElement.Clone());
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return Result<JsonElement>.Failure($"Unable to connect to MT5 bridge '{_options.MT5.BridgeBaseUrl}' for {area}: {exception.Message}");
        }
    }

    private async Task<Result<JsonElement>> GetJsonAsync(string path, string area, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(path, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Result<JsonElement>.Failure($"Unable to fetch {area}: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
            }

            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            return Result<JsonElement>.Success(document.RootElement.Clone());
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return Result<JsonElement>.Failure($"Unable to connect to MT5 bridge '{_options.MT5.BridgeBaseUrl}' for {area}: {exception.Message}");
        }
    }

    private static IReadOnlyList<Candle> ParseCandles(JsonElement root, string defaultSymbol, Timeframe defaultTimeframe)
    {
        var array = FindArray(root, "candles") ?? (root.ValueKind == JsonValueKind.Array ? root : default);
        if (array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return array.EnumerateArray()
            .Select(item => new Candle(
                ReadString(item, "symbol") ?? defaultSymbol,
                Enum.TryParse<Timeframe>(ReadString(item, "timeframe"), ignoreCase: true, out var timeframe) ? timeframe : defaultTimeframe,
                ReadDateTime(item, "openedAt") ?? ReadDateTime(item, "time") ?? ReadDateTime(item, "timestamp") ?? DateTimeOffset.UtcNow,
                ReadDecimal(item, "open"),
                ReadDecimal(item, "high"),
                ReadDecimal(item, "low"),
                ReadDecimal(item, "close"),
                ReadDecimal(item, "volume")))
            .OrderBy(candle => candle.OpenedAt)
            .ToArray();
    }

    private static IReadOnlyList<MarketTick> ParseTicks(JsonElement root, string defaultSymbol)
    {
        var array = FindArray(root, "ticks") ?? (root.ValueKind == JsonValueKind.Array ? root : default);
        if (array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return array.EnumerateArray()
            .Select(item => new MarketTick(
                ReadString(item, "symbol") ?? defaultSymbol,
                ReadDateTime(item, "timestamp") ?? ReadDateTime(item, "time") ?? DateTimeOffset.UtcNow,
                ReadDecimal(item, "bid"),
                ReadDecimal(item, "ask"),
                ReadDecimal(item, "last"),
                ReadDecimal(item, "volume")))
            .OrderBy(tick => tick.Timestamp)
            .ToArray();
    }

    private static int CountArrayPayload(JsonElement root, string propertyName)
    {
        var array = FindArray(root, propertyName) ?? (root.ValueKind == JsonValueKind.Array ? root : default);
        return array.ValueKind == JsonValueKind.Array ? array.GetArrayLength() : 0;
    }

    private static JsonElement? FindArray(JsonElement root, string propertyName)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty(propertyName, out var direct) && direct.ValueKind == JsonValueKind.Array)
            {
                return direct;
            }

            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                return data;
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var property)
            ? property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString()
            : null;

    private static long? ReadLong(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String && long.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var value) ? value : null;
    }

    private static decimal ReadDecimal(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            return 0m;
        }

        return property.ValueKind == JsonValueKind.String
            ? decimal.Parse(property.GetString()!, CultureInfo.InvariantCulture)
            : property.GetDecimal();
    }

    private static DateTimeOffset? ReadDateTime(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(property.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed;
        }

        return property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var epoch)
            ? epoch > 10_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(epoch) : DateTimeOffset.FromUnixTimeSeconds(epoch)
            : null;
    }
}
