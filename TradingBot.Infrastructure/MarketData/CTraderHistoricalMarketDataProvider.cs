using TradingBot.Application;
using TradingBot.Domain;
using TradingBot.Infrastructure.Broker;

namespace TradingBot.Infrastructure.MarketData;

public sealed class CTraderHistoricalMarketDataProvider(
    CTraderJsonApiClient cTraderClient,
    TradingBotOptions options) : IHistoricalMarketDataProvider
{
    public string ProviderName => "cTrader";

    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(HistoricalDataRequest request, CancellationToken cancellationToken)
    {
        var accessToken = options.CTrader.AccessToken;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new InvalidOperationException("cTrader access token is required for historical data. Run 'dotnet run --project TradingBot.CLI -- ctrader-connect' first.");
        }

        var candles = new List<Candle>();
        foreach (var (from, to) in GetChunks(request.From, request.To, request.Timeframe))
        {
            var result = await cTraderClient.GetTrendbarsAsync(
                accessToken,
                request.Symbol,
                request.Timeframe,
                from,
                to,
                cancellationToken);

            if (!result.IsSuccess)
            {
                throw new InvalidOperationException(result.Error);
            }

            candles.AddRange(result.Value!);
        }

        return candles
            .GroupBy(candle => candle.OpenedAt)
            .Select(group => group.First())
            .OrderBy(candle => candle.OpenedAt)
            .ToArray();
    }

    private IEnumerable<(DateTimeOffset From, DateTimeOffset To)> GetChunks(DateTimeOffset from, DateTimeOffset to, Timeframe timeframe)
    {
        var chunkDays = timeframe switch
        {
            Timeframe.M1 => options.Backtesting.HistoricalDataChunkDaysM1,
            Timeframe.M5 => options.Backtesting.HistoricalDataChunkDaysM5,
            Timeframe.H1 => options.Backtesting.HistoricalDataChunkDaysH1,
            Timeframe.D1 => options.Backtesting.HistoricalDataChunkDaysD1,
            _ => 30
        };

        var cursor = from;
        while (cursor < to)
        {
            var chunkTo = cursor.AddDays(chunkDays);
            if (chunkTo > to)
            {
                chunkTo = to;
            }

            yield return (cursor, chunkTo);
            cursor = chunkTo;
        }
    }
}
