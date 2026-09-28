using System.Text.Json;
using System.Text.Json.Nodes;
using TradingBot.Application;
using TradingBot.Domain;
using TradingBot.Infrastructure.Broker;
using TradingBot.Infrastructure.Configuration;
using TradingBot.Infrastructure.Filters;
using TradingBot.Infrastructure.MarketData;
using TradingBot.Infrastructure.MetaTrader;
using TradingBot.Infrastructure.Sessions;
using TradingBot.Reporting;
using TradingBot.Shared;
using TradingBot.Strategies;


internal static partial class CliCommands
{
    internal static async Task<int> ConnectConfiguredBrokerAsync(ServiceRegistry services, CancellationToken cancellationToken)
    {
        if (IsBroker(services.Options, "MT5"))
        {
            var result = await services.Broker.ValidateConnectionAsync(cancellationToken);
            if (!result.IsSuccess)
            {
                Console.Error.WriteLine(result.Error);
                return 3;
            }

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                isConnected = true,
                broker = "MT5",
                bridgeBaseUrl = services.Options.MT5.BridgeBaseUrl
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        return await CTraderConnectAsync(services, cancellationToken);
    }

    internal static async Task<int> CTraderRequestTokenAsync(ServiceRegistry services, CancellationToken cancellationToken)
    {
        var result = await RefreshCTraderTokenAsync(services, cancellationToken);
        if (!result.IsSuccess)
        {
            Console.Error.WriteLine(result.Error);
            return 3;
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            isConnected = result.Value!.IsConnected,
            message = result.Value.Message,
            hasAccessToken = result.Value.HasAccessToken,
            hasRefreshToken = result.Value.HasRefreshToken,
            expiresInSeconds = result.Value.ExpiresInSeconds,
            ctidTraderAccountIdConfigured = services.Options.CTrader.CtidTraderAccountId > 0,
            accountMode = services.Options.CTrader.IsDemo ? "Demo" : "Live"
        }, new JsonSerializerOptions { WriteIndented = true }));

        return result.Value.IsConnected ? 0 : 4;
    }

    internal static async Task<int> CTraderRefreshTokenAsync(ServiceRegistry services, CancellationToken cancellationToken)
    {
        var result = await RefreshCTraderTokenAsync(services, cancellationToken);
        if (!result.IsSuccess)
        {
            Console.Error.WriteLine(result.Error);
            return 3;
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            isRefreshed = true,
            message = "cTrader token refreshed and saved to appsettings.json.",
            hasAccessToken = result.Value!.HasAccessToken,
            hasRefreshToken = result.Value.HasRefreshToken,
            expiresInSeconds = result.Value.ExpiresInSeconds,
            refreshedAt = DateTimeOffset.Now
        }, new JsonSerializerOptions { WriteIndented = true }));

        return 0;
    }

    internal static async Task<Result<CTraderTokenTestResult>> RefreshCTraderTokenAsync(ServiceRegistry services, CancellationToken cancellationToken)
    {
        var result = await services.CTraderTester.TestTokenEndpointAsync(cancellationToken);
        if (!result.IsSuccess)
        {
            return result;
        }

        if (!string.IsNullOrWhiteSpace(result.Value!.AccessToken) && !string.IsNullOrWhiteSpace(result.Value.RefreshToken))
        {
            await SaveCTraderTokensAsync(services.AppsettingsPath, result.Value.AccessToken, result.Value.RefreshToken, cancellationToken);
            services.Options.CTrader.AccessToken = result.Value.AccessToken;
            services.Options.CTrader.RefreshToken = result.Value.RefreshToken;
        }

        return result;
    }

    internal static async Task<int> CTraderConnectAsync(ServiceRegistry services, CancellationToken cancellationToken)
    {
        var authorizeExitCode = await CTraderAuthorizeAsync(services, cancellationToken);
        if (authorizeExitCode != 0)
        {
            return authorizeExitCode;
        }

        var refreshedOptions = JsonOptionsLoader.Load(services.AppsettingsPath);
        var refreshedServices = BuildServices(services.AppsettingsPath, refreshedOptions);
        return await CTraderRequestTokenAsync(refreshedServices, cancellationToken);
    }

    internal static async Task<Result> PrepareConfiguredBrokerForMarketDataAsync(ServiceRegistry services, CancellationToken cancellationToken)
    {
        if (IsBroker(services.Options, "MT5"))
        {
            return await services.Broker.ValidateConnectionAsync(cancellationToken);
        }

        var tokenResult = await RefreshCTraderTokenAsync(services, cancellationToken);
        return tokenResult.IsSuccess ? Result.Success() : Result.Failure(tokenResult.Error!);
    }

    internal static async Task<int> CTraderAccountDetailsAsync(ServiceRegistry services, CancellationToken cancellationToken)
    {
        var tokenResult = await services.CTraderTester.TestTokenEndpointAsync(cancellationToken);
        if (!tokenResult.IsSuccess)
        {
            Console.Error.WriteLine(tokenResult.Error);
            return 3;
        }

        if (!string.IsNullOrWhiteSpace(tokenResult.Value!.AccessToken) && !string.IsNullOrWhiteSpace(tokenResult.Value.RefreshToken))
        {
            await SaveCTraderTokensAsync(services.AppsettingsPath, tokenResult.Value.AccessToken, tokenResult.Value.RefreshToken, cancellationToken);
            services.Options.CTrader.AccessToken = tokenResult.Value.AccessToken;
            services.Options.CTrader.RefreshToken = tokenResult.Value.RefreshToken;
        }

        var accountResult = await services.CTraderJsonApi.TestAccountAsync(tokenResult.Value.AccessToken ?? services.Options.CTrader.AccessToken, cancellationToken);
        if (!accountResult.IsSuccess)
        {
            Console.Error.WriteLine(accountResult.Error);
            return 4;
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            isConnected = accountResult.Value!.IsConnected,
            host = accountResult.Value.Host,
            port = accountResult.Value.Port,
            ctidTraderAccountId = accountResult.Value.CtidTraderAccountId,
            grantedAccountIds = accountResult.Value.GrantedAccountIds,
            accountMode = services.Options.CTrader.IsDemo ? "Demo" : "Live",
            trader = accountResult.Value.TraderPayload
        }, new JsonSerializerOptions { WriteIndented = true }));

        return 0;
    }

    internal static async Task<int> CTraderAccountsListAsync(ServiceRegistry services, CancellationToken cancellationToken)
    {
        var tokenResult = await services.CTraderTester.TestTokenEndpointAsync(cancellationToken);
        if (!tokenResult.IsSuccess)
        {
            Console.Error.WriteLine(tokenResult.Error);
            return 3;
        }

        if (!string.IsNullOrWhiteSpace(tokenResult.Value!.AccessToken) && !string.IsNullOrWhiteSpace(tokenResult.Value.RefreshToken))
        {
            await SaveCTraderTokensAsync(services.AppsettingsPath, tokenResult.Value.AccessToken, tokenResult.Value.RefreshToken, cancellationToken);
        }

        var accountsResult = await services.CTraderJsonApi.GetGrantedAccountsAsync(tokenResult.Value.AccessToken ?? services.Options.CTrader.AccessToken, cancellationToken);
        if (!accountsResult.IsSuccess)
        {
            Console.Error.WriteLine(accountsResult.Error);
            return 4;
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            host = accountsResult.Value!.Host,
            port = accountsResult.Value.Port,
            configuredCtidTraderAccountId = services.Options.CTrader.CtidTraderAccountId,
            configuredMode = services.Options.CTrader.IsDemo ? "Demo" : "Live",
            accounts = accountsResult.Value.Accounts.Select(account => new
            {
                account.CtidTraderAccountId,
                mode = account.IsLive ? "Live" : "Demo",
                account.TraderLogin,
                account.BrokerTitleShort,
                isConfiguredAccount = account.CtidTraderAccountId == services.Options.CTrader.CtidTraderAccountId
            })
        }, new JsonSerializerOptions { WriteIndented = true }));

        return 0;
    }

    internal static async Task<int> CTraderCreateOrderAsync(ServiceRegistry services, string[] args, CancellationToken cancellationToken)
    {
        if (services.Options.FtmoProtection.Enabled) { Console.Error.WriteLine("Manual cTrader execution is unavailable under FTMO protection."); return 2; }
        if (!TryReadDecimalArg(args, "entry_point", out var entryPoint) || !TryReadDecimalArg(args, "quantity", out var quantity))
        {
            Console.Error.WriteLine("Usage: TradingBot.CLI ctrader-createorder entry_point=[value] quantity=[value] [confirm_live_order=true]");
            return 2;
        }

        if (!services.Options.CTrader.IsDemo && !HasFlagArg(args, "confirm_live_order", "true"))
        {
            Console.Error.WriteLine("Live-environment order creation requires confirm_live_order=true.");
            return 2;
        }

        var tokenResult = await services.CTraderTester.TestTokenEndpointAsync(cancellationToken);
        if (!tokenResult.IsSuccess)
        {
            Console.Error.WriteLine(tokenResult.Error);
            return 3;
        }

        if (!string.IsNullOrWhiteSpace(tokenResult.Value!.AccessToken) && !string.IsNullOrWhiteSpace(tokenResult.Value.RefreshToken))
        {
            await SaveCTraderTokensAsync(services.AppsettingsPath, tokenResult.Value.AccessToken, tokenResult.Value.RefreshToken, cancellationToken);
            services.Options.CTrader.AccessToken = tokenResult.Value.AccessToken;
            services.Options.CTrader.RefreshToken = tokenResult.Value.RefreshToken;
        }

        var activeTradeValidation = await ValidateActiveTradeLimitAsync(
            services,
            Guid.NewGuid().ToString("N"),
            formattedOutput: false,
            cancellationToken);
        if (!activeTradeValidation.IsSuccess)
        {
            Console.Error.WriteLine(activeTradeValidation.Error);
            return 4;
        }

        var orderResult = await services.CTraderJsonApi.CreateLimitOrderAsync(
            entryPoint,
            quantity,
            tokenResult.Value.AccessToken ?? services.Options.CTrader.AccessToken,
            cancellationToken);

        if (!orderResult.IsSuccess)
        {
            Console.Error.WriteLine(orderResult.Error);
            return 4;
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            isAccepted = orderResult.Value!.IsAccepted,
            orderType = services.Options.CTrader.DefaultOrderType,
            tradeSide = orderResult.Value.TradeSide,
            ctidTraderAccountId = orderResult.Value.CtidTraderAccountId,
            symbolId = orderResult.Value.SymbolId,
            quantityLots = quantity,
            volume = orderResult.Value.Volume,
            limitPrice = orderResult.Value.LimitPrice,
            stopLoss = orderResult.Value.StopLoss,
            takeProfit = orderResult.Value.TakeProfit,
            riskReward = orderResult.Value.RiskReward,
            clientOrderId = orderResult.Value.ClientOrderId,
            execution = orderResult.Value.ExecutionPayload
        }, new JsonSerializerOptions { WriteIndented = true }));

        return 0;
    }

    internal static async Task<int> CTraderSymbolsAsync(ServiceRegistry services, string[] args, CancellationToken cancellationToken)
    {
        var symbolName = ReadKeyValueArg(args, "symbol") ?? services.Options.Symbol;
        var tokenResult = await services.CTraderTester.TestTokenEndpointAsync(cancellationToken);
        if (!tokenResult.IsSuccess)
        {
            Console.Error.WriteLine(tokenResult.Error);
            return 3;
        }

        if (!string.IsNullOrWhiteSpace(tokenResult.Value!.AccessToken) && !string.IsNullOrWhiteSpace(tokenResult.Value.RefreshToken))
        {
            await SaveCTraderTokensAsync(services.AppsettingsPath, tokenResult.Value.AccessToken, tokenResult.Value.RefreshToken, cancellationToken);
        }

        var symbolsResult = await services.CTraderJsonApi.GetSymbolsAsync(
            tokenResult.Value.AccessToken ?? services.Options.CTrader.AccessToken,
            symbolName,
            cancellationToken);

        if (!symbolsResult.IsSuccess)
        {
            Console.Error.WriteLine(symbolsResult.Error);
            return 4;
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            host = symbolsResult.Value!.Host,
            port = symbolsResult.Value.Port,
            ctidTraderAccountId = symbolsResult.Value.CtidTraderAccountId,
            searchedSymbol = symbolName,
            configuredSymbolId = symbolsResult.Value.ConfiguredSymbolId,
            configuredSymbolIdFound = symbolsResult.Value.Symbols.Any(symbol => symbol.SymbolId == symbolsResult.Value.ConfiguredSymbolId),
            symbols = symbolsResult.Value.Symbols.Select(symbol => new
            {
                symbol.SymbolId,
                symbol.SymbolName,
                symbol.Enabled,
                symbol.BaseAssetId,
                symbol.QuoteAssetId,
                isConfiguredSymbol = symbol.SymbolId == symbolsResult.Value.ConfiguredSymbolId
            })
        }, new JsonSerializerOptions { WriteIndented = true }));

        return 0;
    }

    internal static async Task<int> MT5TestConnectionAsync(ServiceRegistry services, CancellationToken cancellationToken)
    {
        var result = await services.MT5Bridge.GetHealthAsync(cancellationToken);
        if (!result.IsSuccess)
        {
            Console.Error.WriteLine(result.Error);
            return 3;
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            isConnected = true,
            broker = "MT5",
            bridgeBaseUrl = services.Options.MT5.BridgeBaseUrl,
            health = result.Value
        }, new JsonSerializerOptions { WriteIndented = true }));

        return 0;
    }

    internal static async Task<int> MT5AccountDetailsAsync(ServiceRegistry services, CancellationToken cancellationToken)
    {
        var result = await services.MT5Bridge.GetAccountAsync(cancellationToken);
        if (!result.IsSuccess)
        {
            Console.Error.WriteLine(result.Error);
            return 3;
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            broker = "MT5",
            configuredAccountId = services.Options.MT5.AccountId,
            account = result.Value
        }, new JsonSerializerOptions { WriteIndented = true }));

        return 0;
    }

    internal static async Task<int> MT5SymbolsAsync(ServiceRegistry services, string[] args, CancellationToken cancellationToken)
    {
        var symbolName = ReadKeyValueArg(args, "symbol") ?? services.Options.Symbol;
        var result = await services.MT5Bridge.GetSymbolAsync(symbolName, cancellationToken);
        if (!result.IsSuccess)
        {
            Console.Error.WriteLine(result.Error);
            return 3;
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            broker = "MT5",
            searchedSymbol = symbolName,
            symbol = result.Value
        }, new JsonSerializerOptions { WriteIndented = true }));

        return 0;
    }

    internal static async Task<int> MT5CreateOrderAsync(ServiceRegistry services, string[] args, CancellationToken cancellationToken)
    {
        if (services.Options.FtmoProtection.Enabled) { Console.Error.WriteLine("Manual orders are disabled under FTMO protection; use guarded strategy execution."); return 2; }
        if (!TryReadDecimalArg(args, "entry_point", out var entryPoint) || !TryReadDecimalArg(args, "quantity", out var quantity))
        {
            Console.Error.WriteLine("Usage: TradingBot.CLI mt5-createorder entry_point=[value] quantity=[value] [confirm_live_order=true]");
            return 2;
        }

        if (!services.Options.MT5.AllowLiveOrderCreation && !HasFlagArg(args, "confirm_live_order", "true"))
        {
            Console.Error.WriteLine("MT5 order creation requires MT5:AllowLiveOrderCreation=true or confirm_live_order=true.");
            return 2;
        }

        var signal = new TradeSignal(
            services.Options.Symbol,
            TradeDirection.Buy,
            entryPoint,
            entryPoint - (services.Options.PipSize * 20m),
            entryPoint + (services.Options.PipSize * 60m),
            3m,
            SessionName.Closed,
            true,
            "Manual MT5 order test.");

        var cycleId = Guid.NewGuid().ToString("N");
        await services.Monitor.WriteAsync(cycleId, "manual_mt5_order_started", new
        {
            entryPoint,
            quantity
        }, cancellationToken);

        var marketGuard = await ValidateMarketExecutionGuardAsync(services, cycleId, formattedOutput: false, cancellationToken);
        if (!marketGuard.IsSuccess)
        {
            Console.Error.WriteLine(marketGuard.Error);
            return 4;
        }

        var staleCleanup = await CleanupStalePendingOrdersAsync(services, cycleId, formattedOutput: false, cancellationToken);
        if (!staleCleanup.IsSuccess)
        {
            Console.Error.WriteLine(staleCleanup.Error);
            return 4;
        }

        var activeTradeValidation = await ValidateActiveTradeLimitAsync(
            services,
            cycleId,
            formattedOutput: false,
            cancellationToken);
        if (!activeTradeValidation.IsSuccess)
        {
            Console.Error.WriteLine(activeTradeValidation.Error);
            return 4;
        }

        var result = await services.MT5Bridge.CreateLimitOrderAsync(signal, quantity, cancellationToken);
        if (!result.IsSuccess)
        {
            Console.Error.WriteLine(result.Error);
            return 4;
        }

        Console.WriteLine(JsonSerializer.Serialize(result.Value, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    internal static async Task<int> CTraderAuthorizeAsync(ServiceRegistry services, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(services.Options.CTrader.RedirectUri, UriKind.Absolute, out var redirectUri))
        {
            Console.Error.WriteLine("CTrader:RedirectUri must be an absolute URI.");
            return 2;
        }

        if (!redirectUri.IsLoopback)
        {
            Console.Error.WriteLine("ctrader-authorize only supports localhost redirect URIs.");
            return 2;
        }

        using var listener = new System.Net.HttpListener();
        var prefix = $"{redirectUri.Scheme}://{redirectUri.Authority}{redirectUri.AbsolutePath.TrimEnd('/')}/";
        listener.Prefixes.Add(prefix);
        listener.Start();

        var authUrl = services.CTraderTester.BuildAuthorizationUrl();
        Console.WriteLine("Open this URL, click Allow Access, and keep this command running:");
        Console.WriteLine(authUrl);

        TryOpenBrowser(authUrl);

        var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromMinutes(2), cancellationToken);
        var code = context.Request.QueryString["code"];
        var error = context.Request.QueryString["error"];

        await WriteBrowserResponseAsync(context, string.IsNullOrWhiteSpace(code) ? "Authorization failed. You can close this tab." : "Authorization received. You can close this tab.", cancellationToken);

        if (!string.IsNullOrWhiteSpace(error))
        {
            Console.Error.WriteLine($"cTrader authorization failed: {error}");
            return 3;
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            Console.Error.WriteLine("cTrader redirect did not include a code.");
            return 3;
        }

        var exchange = await services.CTraderTester.ExchangeAuthorizationCodeAsync(code, cancellationToken);
        if (!exchange.IsSuccess)
        {
            Console.Error.WriteLine(exchange.Error);
            return 4;
        }

        await SaveCTraderTokensAsync(services.AppsettingsPath, exchange.Value!.AccessToken, exchange.Value.RefreshToken, cancellationToken);

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            isConnected = true,
            message = "cTrader authorization succeeded. AccessToken and RefreshToken were saved to appsettings.json.",
            hasAccessToken = true,
            hasRefreshToken = true,
            expiresInSeconds = exchange.Value.ExpiresInSeconds
        }, new JsonSerializerOptions { WriteIndented = true }));

        return 0;
    }

    internal static void TryOpenBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            // The printed URL is the fallback in headless shells.
        }
    }

    internal static async Task WriteBrowserResponseAsync(System.Net.HttpListenerContext context, string message, CancellationToken cancellationToken)
    {
        var html = System.Text.Encoding.UTF8.GetBytes($"<html><body><h1>{System.Net.WebUtility.HtmlEncode(message)}</h1></body></html>");
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = html.Length;
        await context.Response.OutputStream.WriteAsync(html, cancellationToken);
        context.Response.Close();
    }

    internal static async Task SaveCTraderTokensAsync(string appsettingsPath, string accessToken, string refreshToken, CancellationToken cancellationToken)
    {
        var json = await File.ReadAllTextAsync(appsettingsPath, cancellationToken);
        var root = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidOperationException("Invalid appsettings.json.");
        var brokers = root["Brokers"]?.AsObject();
        var cTrader = brokers?["CTrader"]?.AsObject()
            ?? root["CTrader"]?.AsObject()
            ?? throw new InvalidOperationException("Missing Brokers:CTrader section.");

        cTrader["AuthorizationCode"] = "";
        cTrader["AccessToken"] = accessToken;
        cTrader["RefreshToken"] = refreshToken;

        await File.WriteAllTextAsync(appsettingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
    }

}
