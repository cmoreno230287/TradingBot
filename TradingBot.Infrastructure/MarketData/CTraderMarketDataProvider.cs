using TradingBot.Application;
using TradingBot.Domain;
using TradingBot.Infrastructure.Broker;

namespace TradingBot.Infrastructure.MarketData;

public sealed class CTraderMarketDataProvider(
    CTraderJsonApiClient cTraderClient,
    TradingBotOptions options) : IMarketDataProvider
{
    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(
        string symbol,
        Timeframe timeframe,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.CTrader.AccessToken))
        {
            throw new InvalidOperationException("cTrader access token is required for market data. Run 'dotnet run --project TradingBot.CLI -- ctrader-connect' first.");
        }

        var result = await cTraderClient.GetTrendbarsAsync(
            options.CTrader.AccessToken,
            symbol,
            timeframe,
            from,
            to,
            cancellationToken);

        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(result.Error);
        }

        return result.Value!;
    }
}
