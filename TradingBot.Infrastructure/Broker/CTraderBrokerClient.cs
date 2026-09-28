using TradingBot.Application;
using TradingBot.Domain;
using TradingBot.Shared;

namespace TradingBot.Infrastructure.Broker;

public sealed class CTraderBrokerClient(TradingBotOptions options) : IBrokerClient
{
    public Task<Result> ValidateConnectionAsync(CancellationToken cancellationToken)
    {
        var clientId = options.CTrader.ClientId;
        var clientSecret = options.CTrader.ClientSecret;

        return string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret)
            ? Task.FromResult(Result.Failure("Live cTrader mode requires CTRADER_CLIENT_ID and CTRADER_CLIENT_SECRET."))
            : Task.FromResult(Result.Success());
    }

    public Task<Result<OrderResult>> PlaceOrderAsync(OrderRequest request, CancellationToken cancellationToken)
    {
        if (!options.LiveTradingEnabled)
        {
            return Task.FromResult(Result<OrderResult>.Failure("Live trading is disabled; no order was created."));
        }

        return Task.FromResult(Result<OrderResult>.Failure("Real cTrader order execution is not configured yet. Add the cTrader Open API adapter behind IBrokerClient."));
    }
}
