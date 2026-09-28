using System.Net.WebSockets;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TradingBot.Application;
using TradingBot.Domain;
using TradingBot.Shared;

namespace TradingBot.Infrastructure.Broker;

public sealed class CTraderJsonApiClient(TradingBotOptions options)
{
    private const int ApplicationAuthReq = 2100;
    private const int ApplicationAuthRes = 2101;
    private const int AccountAuthReq = 2102;
    private const int AccountAuthRes = 2103;
    private const int NewOrderReq = 2106;
    private const int TraderReq = 2121;
    private const int TraderRes = 2122;
    private const int ReconcileReq = 2124;
    private const int ReconcileRes = 2125;
    private const int ExecutionEvent = 2126;
    private const int ErrorRes = 2142;
    private const int GetAccountsByAccessTokenReq = 2149;
    private const int GetAccountsByAccessTokenRes = 2150;
    private const int SymbolsListReq = 2114;
    private const int SymbolsListRes = 2115;
    private const int GetTrendbarsReq = 2137;
    private const int GetTrendbarsRes = 2138;

    public async Task<Result<CTraderAccountTestResult>> TestAccountAsync(string accessToken, CancellationToken cancellationToken)
    {
        var settingsValidation = ValidateSettings(accessToken);
        if (!settingsValidation.IsSuccess)
        {
            return Result<CTraderAccountTestResult>.Failure(settingsValidation.Error!);
        }

        CTraderApiException? lastRouteException = null;
        foreach (var host in GetHostCandidates())
        {
            var uri = new Uri($"wss://{host}:{options.CTrader.JsonPort}");
            using var socket = new ClientWebSocket();
            try
            {
                await socket.ConnectAsync(uri, cancellationToken);

                await SendAsync(socket, ApplicationAuthReq, new
                {
                    clientId = options.CTrader.ClientId,
                    clientSecret = options.CTrader.ClientSecret
                }, cancellationToken);
                await ReadExpectedAsync(socket, ApplicationAuthRes, "application authentication", cancellationToken);

                await SendAsync(socket, GetAccountsByAccessTokenReq, new
                {
                    accessToken
                }, cancellationToken);
                var accountsResponse = await ReadExpectedAsync(socket, GetAccountsByAccessTokenRes, "granted account list", cancellationToken);
                var grantedAccounts = ExtractGrantedAccounts(accountsResponse.RootElement);
                var grantedAccountIds = grantedAccounts.Select(account => account.CtidTraderAccountId).ToArray();

                if (!grantedAccountIds.Contains(options.CTrader.CtidTraderAccountId))
                {
                    return Result<CTraderAccountTestResult>.Failure($"Configured CtidTraderAccountId {options.CTrader.CtidTraderAccountId} is not included in the accounts granted to this access token.");
                }

                var configuredAccount = grantedAccounts.First(account => account.CtidTraderAccountId == options.CTrader.CtidTraderAccountId);
                if (configuredAccount.IsLive == options.CTrader.IsDemo)
                {
                    return Result<CTraderAccountTestResult>.Failure($"Configured CtidTraderAccountId {options.CTrader.CtidTraderAccountId} is {(configuredAccount.IsLive ? "Live" : "Demo")}, but CTrader:IsDemo is set to {options.CTrader.IsDemo}.");
                }

                await SendAsync(socket, AccountAuthReq, new
                {
                    ctidTraderAccountId = options.CTrader.CtidTraderAccountId,
                    accessToken
                }, cancellationToken);
                await ReadExpectedAsync(socket, AccountAuthRes, "account authentication", cancellationToken);

                await SendAsync(socket, TraderReq, new
                {
                    ctidTraderAccountId = options.CTrader.CtidTraderAccountId
                }, cancellationToken);
                var traderResponse = await ReadExpectedAsync(socket, TraderRes, "trader account data", cancellationToken);

                return Result<CTraderAccountTestResult>.Success(new CTraderAccountTestResult(
                    true,
                    host,
                    options.CTrader.JsonPort,
                    options.CTrader.CtidTraderAccountId,
                    grantedAccountIds,
                    ExtractTraderPayload(traderResponse.RootElement)));
            }
            catch (WebSocketException exception)
            {
                return Result<CTraderAccountTestResult>.Failure($"Unable to connect to cTrader JSON WebSocket endpoint {host}: {exception.Message}");
            }
            catch (TimeoutException exception)
            {
                return Result<CTraderAccountTestResult>.Failure(exception.Message);
            }
            catch (CTraderApiException exception) when (CanRetryAlternateHost(exception))
            {
                lastRouteException = exception;
            }
            catch (CTraderApiException exception)
            {
                return Result<CTraderAccountTestResult>.Failure(exception.Message);
            }
            catch (JsonException exception)
            {
                return Result<CTraderAccountTestResult>.Failure($"Invalid JSON response from cTrader: {exception.Message}");
            }
        }

        return Result<CTraderAccountTestResult>.Failure(lastRouteException?.Message ?? "Unable to authenticate against any configured cTrader endpoint.");
    }

    public async Task<Result<CTraderGrantedAccountsResult>> GetGrantedAccountsAsync(string accessToken, CancellationToken cancellationToken)
    {
        var settingsValidation = ValidateSettingsForAccountList(accessToken);
        if (!settingsValidation.IsSuccess)
        {
            return Result<CTraderGrantedAccountsResult>.Failure(settingsValidation.Error!);
        }

        CTraderApiException? lastRouteException = null;
        foreach (var host in GetHostCandidates())
        {
            var uri = new Uri($"wss://{host}:{options.CTrader.JsonPort}");
            using var socket = new ClientWebSocket();
            try
            {
                await socket.ConnectAsync(uri, cancellationToken);

                await SendAsync(socket, ApplicationAuthReq, new
                {
                    clientId = options.CTrader.ClientId,
                    clientSecret = options.CTrader.ClientSecret
                }, cancellationToken);
                await ReadExpectedAsync(socket, ApplicationAuthRes, "application authentication", cancellationToken);

                await SendAsync(socket, GetAccountsByAccessTokenReq, new
                {
                    accessToken
                }, cancellationToken);
                var accountsResponse = await ReadExpectedAsync(socket, GetAccountsByAccessTokenRes, "granted account list", cancellationToken);

                return Result<CTraderGrantedAccountsResult>.Success(new CTraderGrantedAccountsResult(
                    host,
                    options.CTrader.JsonPort,
                    ExtractGrantedAccounts(accountsResponse.RootElement)));
            }
            catch (WebSocketException exception)
            {
                return Result<CTraderGrantedAccountsResult>.Failure($"Unable to connect to cTrader JSON WebSocket endpoint {host}: {exception.Message}");
            }
            catch (CTraderApiException exception) when (CanRetryAlternateHost(exception))
            {
                lastRouteException = exception;
            }
            catch (CTraderApiException exception)
            {
                return Result<CTraderGrantedAccountsResult>.Failure(exception.Message);
            }
        }

        return Result<CTraderGrantedAccountsResult>.Failure(lastRouteException?.Message ?? "Unable to authenticate against any configured cTrader endpoint.");
    }

    public async Task<Result<CTraderCreateOrderResult>> CreateLimitOrderAsync(decimal entryPoint, decimal quantityLots, string accessToken, CancellationToken cancellationToken)
    {
        var tradeSide = ParseTradeSide(options.CTrader.DefaultTradeSide);
        var protection = CalculateProtectionPrices(entryPoint, tradeSide);

        return await CreateLimitOrderAsync(
            entryPoint,
            quantityLots,
            ParseDirection(options.CTrader.DefaultTradeSide),
            protection.StopLoss,
            protection.TakeProfit,
            accessToken,
            cancellationToken);
    }

    public async Task<Result<CTraderCreateOrderResult>> CreateLimitOrderAsync(
        decimal entryPoint,
        decimal quantityLots,
        TradeDirection direction,
        decimal stopLoss,
        decimal takeProfit,
        string accessToken,
        CancellationToken cancellationToken)
    {
        if (!options.LiveTradingEnabled)
            return Result<CTraderCreateOrderResult>.Failure("LiveTradingEnabled=false blocks new orders.");
        var validation = ValidateCreateOrderSettings(entryPoint, quantityLots, accessToken);
        if (!validation.IsSuccess)
        {
            return Result<CTraderCreateOrderResult>.Failure(validation.Error!);
        }

        var protectionValidation = ValidateProtectionPrices(entryPoint, direction, stopLoss, takeProfit);
        if (!protectionValidation.IsSuccess)
        {
            return Result<CTraderCreateOrderResult>.Failure(protectionValidation.Error!);
        }

        var tradeSide = direction == TradeDirection.Buy ? 1 : 2;
        var volume = checked((long)Math.Round(quantityLots * options.CTrader.UnitsPerLot * 100m, MidpointRounding.AwayFromZero));
        var riskReward = CalculateRiskReward(entryPoint, stopLoss, takeProfit);
        CTraderApiException? lastRouteException = null;
        foreach (var host in GetHostCandidates())
        {
            var uri = new Uri($"wss://{host}:{options.CTrader.JsonPort}");
            using var socket = new ClientWebSocket();
            try
            {
                await socket.ConnectAsync(uri, cancellationToken);
                await AuthenticateAsync(socket, accessToken, cancellationToken);

                var clientOrderId = $"TradingBot-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
                await SendAsync(socket, NewOrderReq, new
                {
                    ctidTraderAccountId = options.CTrader.CtidTraderAccountId,
                    symbolId = options.CTrader.SymbolId,
                    orderType = 2,
                    tradeSide,
                    volume,
                    limitPrice = entryPoint,
                    stopLoss,
                    takeProfit,
                    timeInForce = 2,
                    clientOrderId,
                    label = "TradingBot.CLI",
                    comment = "Created by TradingBot.CLI ctrader-createorder"
                }, cancellationToken);

                var execution = await ReadExpectedAsync(socket, ExecutionEvent, "new order execution event", cancellationToken);

                return Result<CTraderCreateOrderResult>.Success(new CTraderCreateOrderResult(
                    true,
                    clientOrderId,
                    options.CTrader.CtidTraderAccountId,
                    options.CTrader.SymbolId,
                    direction.ToString().ToUpperInvariant(),
                    volume,
                    entryPoint,
                    stopLoss,
                    takeProfit,
                    riskReward,
                    ExtractPayloadClone(execution.RootElement)));
            }
            catch (WebSocketException exception)
            {
                return Result<CTraderCreateOrderResult>.Failure($"Unable to connect to cTrader JSON WebSocket endpoint {host}: {exception.Message}");
            }
            catch (CTraderApiException exception) when (CanRetryAlternateHost(exception))
            {
                lastRouteException = exception;
            }
            catch (CTraderApiException exception)
            {
                return Result<CTraderCreateOrderResult>.Failure(exception.Message);
            }
            catch (OverflowException)
            {
                return Result<CTraderCreateOrderResult>.Failure("Quantity is too large to convert to cTrader volume.");
            }
        }

        return Result<CTraderCreateOrderResult>.Failure(lastRouteException?.Message ?? "Unable to authenticate against any configured cTrader endpoint.");
    }

    public async Task<Result<CTraderSymbolsResult>> GetSymbolsAsync(string accessToken, string symbolName, CancellationToken cancellationToken)
    {
        var validation = ValidateSettings(accessToken);
        if (!validation.IsSuccess)
        {
            return Result<CTraderSymbolsResult>.Failure(validation.Error!);
        }

        CTraderApiException? lastRouteException = null;
        foreach (var host in GetHostCandidates())
        {
            var uri = new Uri($"wss://{host}:{options.CTrader.JsonPort}");
            using var socket = new ClientWebSocket();
            try
            {
                await socket.ConnectAsync(uri, cancellationToken);
                await AuthenticateAsync(socket, accessToken, cancellationToken);

                await SendAsync(socket, SymbolsListReq, new
                {
                    ctidTraderAccountId = options.CTrader.CtidTraderAccountId,
                    includeArchivedSymbols = false
                }, cancellationToken);
                var symbolsResponse = await ReadExpectedAsync(socket, SymbolsListRes, "symbols list", cancellationToken);
                var symbols = ExtractSymbols(symbolsResponse.RootElement, symbolName);

                return Result<CTraderSymbolsResult>.Success(new CTraderSymbolsResult(
                    host,
                    options.CTrader.JsonPort,
                    options.CTrader.CtidTraderAccountId,
                    options.CTrader.SymbolId,
                    symbols));
            }
            catch (WebSocketException exception)
            {
                return Result<CTraderSymbolsResult>.Failure($"Unable to connect to cTrader JSON WebSocket endpoint {host}: {exception.Message}");
            }
            catch (CTraderApiException exception) when (CanRetryAlternateHost(exception))
            {
                lastRouteException = exception;
            }
            catch (CTraderApiException exception)
            {
                return Result<CTraderSymbolsResult>.Failure(exception.Message);
            }
        }

        return Result<CTraderSymbolsResult>.Failure(lastRouteException?.Message ?? "Unable to authenticate against any configured cTrader endpoint.");
    }

    public async Task<Result<CTraderActiveTradeSummary>> GetActiveTradeSummaryAsync(string accessToken, CancellationToken cancellationToken)
    {
        var validation = ValidateSettings(accessToken);
        if (!validation.IsSuccess)
        {
            return Result<CTraderActiveTradeSummary>.Failure(validation.Error!);
        }

        CTraderApiException? lastRouteException = null;
        foreach (var host in GetHostCandidates())
        {
            var uri = new Uri($"wss://{host}:{options.CTrader.JsonPort}");
            using var socket = new ClientWebSocket();
            try
            {
                await socket.ConnectAsync(uri, cancellationToken);
                await AuthenticateAsync(socket, accessToken, cancellationToken);

                await SendAsync(socket, ReconcileReq, new
                {
                    ctidTraderAccountId = options.CTrader.CtidTraderAccountId,
                    returnProtectionOrders = false
                }, cancellationToken);

                var response = await ReadExpectedAsync(socket, ReconcileRes, "active trade reconciliation", cancellationToken);
                return Result<CTraderActiveTradeSummary>.Success(ExtractActiveTradeSummary(response.RootElement));
            }
            catch (WebSocketException exception)
            {
                return Result<CTraderActiveTradeSummary>.Failure($"Unable to connect to cTrader JSON WebSocket endpoint {host}: {exception.Message}");
            }
            catch (CTraderApiException exception) when (CanRetryAlternateHost(exception))
            {
                lastRouteException = exception;
            }
            catch (CTraderApiException exception)
            {
                return Result<CTraderActiveTradeSummary>.Failure(exception.Message);
            }
        }

        return Result<CTraderActiveTradeSummary>.Failure(lastRouteException?.Message ?? "Unable to authenticate against any configured cTrader endpoint.");
    }

    public async Task<Result<IReadOnlyList<Candle>>> GetTrendbarsAsync(
        string accessToken,
        string symbol,
        Timeframe timeframe,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var validation = ValidateSettings(accessToken);
        if (!validation.IsSuccess)
        {
            return Result<IReadOnlyList<Candle>>.Failure(validation.Error!);
        }

        CTraderApiException? lastRouteException = null;
        foreach (var host in GetHostCandidates())
        {
            var uri = new Uri($"wss://{host}:{options.CTrader.JsonPort}");
            using var socket = new ClientWebSocket();
            try
            {
                await socket.ConnectAsync(uri, cancellationToken);
                await AuthenticateAsync(socket, accessToken, cancellationToken);

                await SendAsync(socket, GetTrendbarsReq, new
                {
                    ctidTraderAccountId = options.CTrader.CtidTraderAccountId,
                    symbolId = options.CTrader.SymbolId,
                    period = MapTrendbarPeriod(timeframe),
                    fromTimestamp = from.ToUnixTimeMilliseconds(),
                    toTimestamp = to.ToUnixTimeMilliseconds()
                }, cancellationToken);

                var response = await ReadExpectedAsync(socket, GetTrendbarsRes, "historical trendbars", cancellationToken);
                var candles = ExtractTrendbars(response.RootElement, symbol, timeframe);
                return Result<IReadOnlyList<Candle>>.Success(candles);
            }
            catch (WebSocketException exception)
            {
                return Result<IReadOnlyList<Candle>>.Failure($"Unable to connect to cTrader JSON WebSocket endpoint {host}: {exception.Message}");
            }
            catch (CTraderApiException exception) when (CanRetryAlternateHost(exception))
            {
                lastRouteException = exception;
            }
            catch (CTraderApiException exception)
            {
                return Result<IReadOnlyList<Candle>>.Failure(exception.Message);
            }
        }

        return Result<IReadOnlyList<Candle>>.Failure(lastRouteException?.Message ?? "Unable to authenticate against any configured cTrader endpoint.");
    }

    private Result ValidateSettings(string accessToken)
    {
        if (string.IsNullOrWhiteSpace(options.CTrader.ClientId))
        {
            return Result.Failure("CTrader:ClientId is required.");
        }

        if (string.IsNullOrWhiteSpace(options.CTrader.ClientSecret))
        {
            return Result.Failure("CTrader:ClientSecret is required.");
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return Result.Failure("A valid cTrader access token is required.");
        }

        if (options.CTrader.CtidTraderAccountId <= 0)
        {
            return Result.Failure("CTrader:CtidTraderAccountId must be configured.");
        }

        return Result.Success();
    }

    private Result ValidateSettingsForAccountList(string accessToken)
    {
        if (string.IsNullOrWhiteSpace(options.CTrader.ClientId))
        {
            return Result.Failure("CTrader:ClientId is required.");
        }

        if (string.IsNullOrWhiteSpace(options.CTrader.ClientSecret))
        {
            return Result.Failure("CTrader:ClientSecret is required.");
        }

        return string.IsNullOrWhiteSpace(accessToken)
            ? Result.Failure("A valid cTrader access token is required.")
            : Result.Success();
    }

    private Result ValidateCreateOrderSettings(decimal entryPoint, decimal quantityLots, string accessToken)
    {
        var accountValidation = ValidateSettings(accessToken);
        if (!accountValidation.IsSuccess)
        {
            return accountValidation;
        }

        if (!string.Equals(options.CTrader.Scope, "trading", StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure("Order creation requires CTrader:Scope to be \"trading\". Set Scope to trading and run ctrader-authorize again.");
        }

        if (!options.CTrader.IsDemo && !options.CTrader.AllowLiveOrderCreation)
        {
            return Result.Failure("Order creation is blocked for live-environment accounts. To override, set CTrader:AllowLiveOrderCreation to true and pass confirm_live_order=true.");
        }

        if (options.CTrader.SymbolId <= 0)
        {
            return Result.Failure("CTrader:SymbolId must be configured before creating an order.");
        }

        if (entryPoint <= 0)
        {
            return Result.Failure("entry_point must be greater than zero.");
        }

        if (quantityLots <= 0)
        {
            return Result.Failure("quantity must be greater than zero.");
        }

        if (options.NormalStopLossBufferPips <= 0)
        {
            return Result.Failure("NormalStopLossBufferPips must be greater than zero to calculate stop loss.");
        }

        if (options.PreferredRiskReward < options.MinRiskReward)
        {
            return Result.Failure("PreferredRiskReward must be greater than or equal to MinRiskReward to calculate take profit.");
        }

        return ParseTradeSide(options.CTrader.DefaultTradeSide) == 0
            ? Result.Failure("CTrader:DefaultTradeSide must be BUY or SELL.")
            : Result.Success();
    }

    private async Task AuthenticateAsync(ClientWebSocket socket, string accessToken, CancellationToken cancellationToken)
    {
        await SendAsync(socket, ApplicationAuthReq, new
        {
            clientId = options.CTrader.ClientId,
            clientSecret = options.CTrader.ClientSecret
        }, cancellationToken);
        await ReadExpectedAsync(socket, ApplicationAuthRes, "application authentication", cancellationToken);

        await SendAsync(socket, AccountAuthReq, new
        {
            ctidTraderAccountId = options.CTrader.CtidTraderAccountId,
            accessToken
        }, cancellationToken);
        await ReadExpectedAsync(socket, AccountAuthRes, "account authentication", cancellationToken);
    }

    private static int ParseTradeSide(string value) => value.Trim().ToUpperInvariant() switch
    {
        "BUY" => 1,
        "SELL" => 2,
        _ => 0
    };

    private static TradeDirection ParseDirection(string value) =>
        string.Equals(value, "SELL", StringComparison.OrdinalIgnoreCase) ? TradeDirection.Sell : TradeDirection.Buy;

    private OrderProtectionPrices CalculateProtectionPrices(decimal entryPoint, int tradeSide)
    {
        var stopDistance = options.NormalStopLossBufferPips * options.PipSize;
        var takeProfitDistance = stopDistance * options.PreferredRiskReward;

        var stopLoss = tradeSide == 1
            ? entryPoint - stopDistance
            : entryPoint + stopDistance;
        var takeProfit = tradeSide == 1
            ? entryPoint + takeProfitDistance
            : entryPoint - takeProfitDistance;

        return new OrderProtectionPrices(
            decimal.Round(stopLoss, 5),
            decimal.Round(takeProfit, 5),
            options.PreferredRiskReward);
    }

    private static Result ValidateProtectionPrices(decimal entryPoint, TradeDirection direction, decimal stopLoss, decimal takeProfit)
    {
        if (stopLoss <= 0 || takeProfit <= 0)
        {
            return Result.Failure("Strategy stop loss and take profit must be greater than zero.");
        }

        if (direction == TradeDirection.Buy && (stopLoss >= entryPoint || takeProfit <= entryPoint))
        {
            return Result.Failure("BUY orders require stopLoss below entry and takeProfit above entry.");
        }

        if (direction == TradeDirection.Sell && (stopLoss <= entryPoint || takeProfit >= entryPoint))
        {
            return Result.Failure("SELL orders require stopLoss above entry and takeProfit below entry.");
        }

        return Result.Success();
    }

    private static decimal CalculateRiskReward(decimal entryPoint, decimal stopLoss, decimal takeProfit)
    {
        var risk = Math.Abs(entryPoint - stopLoss);
        return risk == 0m ? 0m : decimal.Round(Math.Abs(takeProfit - entryPoint) / risk, 2);
    }

    private IReadOnlyList<string> GetHostCandidates()
    {
        var preferred = options.CTrader.IsDemo ? options.CTrader.JsonDemoHost : options.CTrader.JsonLiveHost;
        var alternate = options.CTrader.IsDemo ? options.CTrader.JsonLiveHost : options.CTrader.JsonDemoHost;

        return string.Equals(preferred, alternate, StringComparison.OrdinalIgnoreCase)
            ? [preferred]
            : [preferred, alternate];
    }

    private static bool CanRetryAlternateHost(CTraderApiException exception) =>
        exception.Message.Contains("application authentication", StringComparison.OrdinalIgnoreCase)
        && exception.Message.Contains("CANT_ROUTE_REQUEST", StringComparison.OrdinalIgnoreCase);

    private static async Task SendAsync(ClientWebSocket socket, int payloadType, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(new
        {
            clientMsgId = Guid.NewGuid().ToString("N"),
            payloadType,
            payload
        });

        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<JsonDocument> ReadExpectedAsync(ClientWebSocket socket, int expectedPayloadType, string operationName, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        while (!timeout.IsCancellationRequested)
        {
            var document = await ReadJsonMessageAsync(socket, timeout.Token);
            var payloadType = document.RootElement.GetProperty("payloadType").GetInt32();

            if (payloadType == ErrorRes)
            {
                var message = document.RootElement.TryGetProperty("payload", out var payload)
                    ? payload.ToString()
                    : document.RootElement.ToString();
                document.Dispose();
                throw new CTraderApiException($"cTrader returned an error during {operationName}: {message}");
            }

            if (payloadType == expectedPayloadType)
            {
                return document;
            }

            document.Dispose();
        }

        throw new TimeoutException($"Timed out waiting for cTrader payload type {expectedPayloadType} during {operationName}.");
    }

    private static async Task<JsonDocument> ReadJsonMessageAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var memory = new MemoryStream();

        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new CTraderApiException("cTrader closed the WebSocket connection.");
            }

            memory.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return JsonDocument.Parse(memory.ToArray());
    }

    private static IReadOnlyList<CTraderGrantedAccount> ExtractGrantedAccounts(JsonElement root)
    {
        if (!root.TryGetProperty("payload", out var payload))
        {
            return [];
        }

        if (!payload.TryGetProperty("ctidTraderAccount", out var accountArray) || accountArray.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return accountArray
            .EnumerateArray()
            .Where(account => account.TryGetProperty("ctidTraderAccountId", out _))
            .Select(account => new CTraderGrantedAccount(
                account.GetProperty("ctidTraderAccountId").GetInt64(),
                account.TryGetProperty("isLive", out var isLive) && isLive.GetBoolean(),
                account.TryGetProperty("traderLogin", out var traderLogin) ? traderLogin.GetInt64() : null,
                account.TryGetProperty("brokerTitleShort", out var broker) ? broker.GetString() : null))
            .ToArray();
    }

    private static JsonElement ExtractTraderPayload(JsonElement root)
    {
        if (!root.TryGetProperty("payload", out var payload))
        {
            return root.Clone();
        }

        return payload.Clone();
    }

    private static JsonElement ExtractPayloadClone(JsonElement root)
    {
        if (!root.TryGetProperty("payload", out var payload))
        {
            return root.Clone();
        }

        return payload.Clone();
    }

    private static CTraderActiveTradeSummary ExtractActiveTradeSummary(JsonElement root)
    {
        if (!root.TryGetProperty("payload", out var payload))
        {
            return new CTraderActiveTradeSummary(0, 0);
        }

        var positions = payload.TryGetProperty("position", out var positionArray) && positionArray.ValueKind == JsonValueKind.Array
            ? positionArray.GetArrayLength()
            : 0;
        var pendingOrders = payload.TryGetProperty("order", out var orderArray) && orderArray.ValueKind == JsonValueKind.Array
            ? orderArray.GetArrayLength()
            : 0;

        return new CTraderActiveTradeSummary(positions, pendingOrders);
    }

    private static IReadOnlyList<CTraderSymbolInfo> ExtractSymbols(JsonElement root, string symbolName)
    {
        if (!root.TryGetProperty("payload", out var payload))
        {
            return [];
        }

        if (!payload.TryGetProperty("symbol", out var symbolArray) || symbolArray.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var normalized = NormalizeSymbolName(symbolName);
        return symbolArray
            .EnumerateArray()
            .Where(symbol => MatchesSymbol(symbol, normalized))
            .Select(symbol => new CTraderSymbolInfo(
                symbol.TryGetProperty("symbolId", out var symbolId) ? symbolId.GetInt64() : 0,
                symbol.TryGetProperty("symbolName", out var name) ? name.GetString() : null,
                symbol.TryGetProperty("enabled", out var enabled) && enabled.GetBoolean(),
                symbol.TryGetProperty("baseAssetId", out var baseAssetId) ? baseAssetId.GetInt64() : null,
                symbol.TryGetProperty("quoteAssetId", out var quoteAssetId) ? quoteAssetId.GetInt64() : null))
            .ToArray();
    }

    private static bool MatchesSymbol(JsonElement symbol, string normalized)
    {
        if (!symbol.TryGetProperty("symbolName", out var nameProperty))
        {
            return false;
        }

        var name = nameProperty.GetString();
        return NormalizeSymbolName(name ?? "").Contains(normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSymbolName(string value) =>
        value.Replace("/", "", StringComparison.OrdinalIgnoreCase)
            .Replace("\\", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("_", "", StringComparison.OrdinalIgnoreCase)
            .Replace("-", "", StringComparison.OrdinalIgnoreCase)
            .ToUpperInvariant();

    private static int MapTrendbarPeriod(Timeframe timeframe) => timeframe switch
    {
        Timeframe.M1 => 1,
        Timeframe.M5 => 2,
        Timeframe.H1 => 8,
        Timeframe.D1 => 14,
        _ => throw new NotSupportedException($"Unsupported cTrader trendbar timeframe '{timeframe}'.")
    };

    private static IReadOnlyList<Candle> ExtractTrendbars(JsonElement root, string symbol, Timeframe timeframe)
    {
        if (!root.TryGetProperty("payload", out var payload))
        {
            return [];
        }

        if (!payload.TryGetProperty("trendbar", out var trendbars) || trendbars.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return trendbars
            .EnumerateArray()
            .Select(trendbar => DecodeTrendbar(trendbar, symbol, timeframe))
            .OrderBy(candle => candle.OpenedAt)
            .ToArray();
    }

    private static Candle DecodeTrendbar(JsonElement trendbar, string symbol, Timeframe timeframe)
    {
        var low = ReadDecimal(trendbar, "low");
        var open = low + ReadDecimal(trendbar, "deltaOpen");
        var high = low + ReadDecimal(trendbar, "deltaHigh");
        var close = low + ReadDecimal(trendbar, "deltaClose");
        var timestamp = trendbar.TryGetProperty("utcTimestampInMinutes", out var minutes)
            ? DateTimeOffset.FromUnixTimeSeconds(minutes.GetInt64() * 60)
            : DateTimeOffset.FromUnixTimeMilliseconds(trendbar.GetProperty("timestamp").GetInt64());

        return new Candle(
            symbol,
            timeframe,
            timestamp,
            NormalizePrice(open),
            NormalizePrice(high),
            NormalizePrice(low),
            NormalizePrice(close),
            ReadDecimal(trendbar, "volume"));
    }

    private static decimal ReadDecimal(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return 0m;
        }

        return property.ValueKind == JsonValueKind.String
            ? decimal.Parse(property.GetString()!, CultureInfo.InvariantCulture)
            : property.GetDecimal();
    }

    private static decimal NormalizePrice(decimal rawPrice) => rawPrice > 1000m
        ? rawPrice / 100000m
        : rawPrice;
}

public sealed record CTraderAccountTestResult(
    bool IsConnected,
    string Host,
    int Port,
    long CtidTraderAccountId,
    IReadOnlyList<long> GrantedAccountIds,
    JsonElement TraderPayload);

public sealed record CTraderGrantedAccountsResult(
    string Host,
    int Port,
    IReadOnlyList<CTraderGrantedAccount> Accounts);

public sealed record CTraderGrantedAccount(
    long CtidTraderAccountId,
    bool IsLive,
    long? TraderLogin,
    string? BrokerTitleShort);

public sealed record CTraderCreateOrderResult(
    bool IsAccepted,
    string ClientOrderId,
    long CtidTraderAccountId,
    long SymbolId,
    string TradeSide,
    long Volume,
    decimal LimitPrice,
    decimal StopLoss,
    decimal TakeProfit,
    decimal RiskReward,
    JsonElement ExecutionPayload);

internal sealed record OrderProtectionPrices(
    decimal StopLoss,
    decimal TakeProfit,
    decimal RiskReward);

public sealed record CTraderSymbolsResult(
    string Host,
    int Port,
    long CtidTraderAccountId,
    long ConfiguredSymbolId,
    IReadOnlyList<CTraderSymbolInfo> Symbols);

public sealed record CTraderSymbolInfo(
    long SymbolId,
    string? SymbolName,
    bool Enabled,
    long? BaseAssetId,
    long? QuoteAssetId);

public sealed record CTraderActiveTradeSummary(
    int OpenPositions,
    int PendingOrders)
{
    public int TotalActiveTrades => OpenPositions + PendingOrders;
}

internal sealed class CTraderApiException(string message) : Exception(message);
