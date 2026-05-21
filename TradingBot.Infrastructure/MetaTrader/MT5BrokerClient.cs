using TradingBot.Application;
using TradingBot.Domain;
using TradingBot.Shared;

namespace TradingBot.Infrastructure.MetaTrader;

public sealed class MT5BrokerClient(MT5BridgeClient bridgeClient, TradingBotOptions options) : IBrokerClient
{
    public async Task<Result> ValidateConnectionAsync(CancellationToken cancellationToken)
    {
        var health = await bridgeClient.GetHealthAsync(cancellationToken);
        if (!health.IsSuccess)
        {
            return Result.Failure(health.Error!);
        }

        return Result.Success();
    }

    public async Task<Result<OrderResult>> PlaceOrderAsync(OrderRequest request, CancellationToken cancellationToken)
    {
        if (!options.MT5.AllowLiveOrderCreation)
        {
            return Result<OrderResult>.Failure("MT5 order creation is blocked. Set MT5:AllowLiveOrderCreation to true for live/demo bridge execution.");
        }

        var signal = new TradeSignal(
            request.Symbol,
            request.Direction,
            request.EntryPrice,
            request.StopLoss,
            request.TakeProfit,
            0m,
            SessionName.Closed,
            true,
            "Manual MT5 order request.");

        var result = await bridgeClient.CreateLimitOrderAsync(signal, request.LotSize, cancellationToken);
        return result.IsSuccess
            ? Result<OrderResult>.Success(new OrderResult(result.Value!.BrokerOrderId, true, "MT5 bridge order accepted."))
            : Result<OrderResult>.Failure(result.Error!);
    }
}
