using TradingBot.Application;
using TradingBot.Domain;

namespace TradingBot.Infrastructure.MetaTrader;

public sealed class MT5MarketDataProvider(MT5BridgeClient bridgeClient) : IMarketDataProvider
{
    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(
        string symbol,
        Timeframe timeframe,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var result = await bridgeClient.GetCandlesAsync(symbol, timeframe, from, to, cancellationToken);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(result.Error);
        }

        return result.Value!;
    }
}
