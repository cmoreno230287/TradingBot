using TradingBot.Application;
using TradingBot.Domain;
using TradingBot.Shared;

namespace TradingBot.Infrastructure.Broker;

public sealed class CTraderBrokerClient(TradingBotOptions options) : IBrokerClient
{
    public Task<Result> ValidateConnectionAsync(CancellationToken cancellationToken)
    {
        if (!options.LiveTradingEnabled)
        {
            return Task.FromResult(Result.Success());
        }

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
            var paperId = $"PAPER-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            return Task.FromResult(Result<OrderResult>.Success(new OrderResult(paperId, true, "Paper order accepted.")));
        }

        return Task.FromResult(Result<OrderResult>.Failure("Real cTrader order execution is not configured yet. Add the cTrader Open API adapter behind IBrokerClient."));
    }
}
