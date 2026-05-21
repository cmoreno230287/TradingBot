using TradingBot.Application;
using TradingBot.Domain;

namespace TradingBot.Infrastructure.MetaTrader;

public sealed class MT5HistoricalTickDataProvider(MT5BridgeClient bridgeClient) : IHistoricalTickDataProvider
{
    public string ProviderName => "MT5";

    public async Task<IReadOnlyList<MarketTick>> GetTicksAsync(
        HistoricalTickDataRequest request,
        CancellationToken cancellationToken)
    {
        var result = await bridgeClient.GetTicksAsync(
            request.Symbol,
            request.From,
            request.To,
            cancellationToken);

        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(result.Error);
        }

        return result.Value!;
    }
}
