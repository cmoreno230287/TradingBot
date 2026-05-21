using TradingBot.Application;
using TradingBot.Domain;

namespace TradingBot.Infrastructure.MetaTrader;

public sealed class MT5HistoricalMarketDataProvider(
    MT5BridgeClient bridgeClient,
    TradingBotOptions options) : IHistoricalMarketDataProvider
{
    public string ProviderName => "MT5";

    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(HistoricalDataRequest request, CancellationToken cancellationToken)
    {
        var candles = new List<Candle>();
        var cursor = request.From;
        var chunk = TimeSpan.FromDays(GetChunkDays(request.Timeframe));
        while (cursor < request.To)
        {
            var chunkTo = cursor.Add(chunk);
            if (chunkTo > request.To)
            {
                chunkTo = request.To;
            }

            var result = await bridgeClient.GetCandlesAsync(
                request.Symbol,
                request.Timeframe,
                cursor,
                chunkTo,
                cancellationToken);

            if (!result.IsSuccess)
            {
                throw new InvalidOperationException(result.Error);
            }

            candles.AddRange(result.Value!);
            cursor = chunkTo;
        }

        return candles
            .GroupBy(candle => candle.OpenedAt)
            .Select(group => group.First())
            .OrderBy(candle => candle.OpenedAt)
            .ToArray();
    }

    private int GetChunkDays(Timeframe timeframe) =>
        timeframe switch
        {
            Timeframe.M1 => Math.Max(1, options.Backtesting.HistoricalDataChunkDaysM1),
            Timeframe.M5 => Math.Max(1, options.Backtesting.HistoricalDataChunkDaysM5),
            Timeframe.H1 => Math.Max(1, options.Backtesting.HistoricalDataChunkDaysH1),
            Timeframe.D1 => Math.Max(1, options.Backtesting.HistoricalDataChunkDaysD1),
            _ => 7
        };
}
