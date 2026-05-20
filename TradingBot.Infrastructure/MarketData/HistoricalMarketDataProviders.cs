using System.Globalization;
using TradingBot.Application;
using TradingBot.Domain;

namespace TradingBot.Infrastructure.MarketData;

public sealed class HistoricalMarketDataProviderFactory(IEnumerable<IHistoricalMarketDataProvider> providers) : IHistoricalMarketDataProviderFactory
{
    private readonly IReadOnlyDictionary<string, IHistoricalMarketDataProvider> _providers = providers.ToDictionary(
        provider => provider.ProviderName,
        StringComparer.OrdinalIgnoreCase);

    public IHistoricalMarketDataProvider Resolve(string providerName)
    {
        if (_providers.TryGetValue(providerName, out var provider))
        {
            return provider;
        }

        var supported = string.Join(", ", _providers.Keys.Order(StringComparer.OrdinalIgnoreCase));
        throw new InvalidOperationException($"Unsupported historical data provider '{providerName}'. Supported providers: {supported}.");
    }
}

public sealed class CachedHistoricalMarketDataProvider(
    IHistoricalMarketDataProvider innerProvider,
    TradingBotOptions options) : IHistoricalMarketDataProvider
{
    public string ProviderName => innerProvider.ProviderName;

    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(HistoricalDataRequest request, CancellationToken cancellationToken)
    {
        if (!options.Backtesting.CacheEnabled)
        {
            return await innerProvider.GetCandlesAsync(request, cancellationToken);
        }

        var path = GetCachePath(request);
        if (File.Exists(path))
        {
            return await ReadCacheAsync(path, cancellationToken);
        }

        var candles = await innerProvider.GetCandlesAsync(request, cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await WriteCacheAsync(path, candles, cancellationToken);
        return candles;
    }

    private string GetCachePath(HistoricalDataRequest request)
    {
        var fileName = $"{request.From:yyyyMMddHHmmss}_{request.To:yyyyMMddHHmmss}.csv";
        return Path.Combine(
            options.Backtesting.CacheDirectory,
            ProviderName,
            request.Symbol,
            request.Timeframe.ToString(),
            fileName);
    }

    private static async Task<IReadOnlyList<Candle>> ReadCacheAsync(string path, CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        return lines
            .Skip(1)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(Parse)
            .OrderBy(candle => candle.OpenedAt)
            .ToArray();
    }

    private static async Task WriteCacheAsync(string path, IReadOnlyList<Candle> candles, CancellationToken cancellationToken)
    {
        var lines = new List<string>
        {
            "Symbol,Timeframe,OpenedAt,Open,High,Low,Close,Volume"
        };

        lines.AddRange(candles
            .OrderBy(candle => candle.OpenedAt)
            .Select(candle => string.Join(',',
                candle.Symbol,
                candle.Timeframe,
                candle.OpenedAt.ToString("O", CultureInfo.InvariantCulture),
                Number(candle.Open),
                Number(candle.High),
                Number(candle.Low),
                Number(candle.Close),
                Number(candle.Volume))));

        await File.WriteAllLinesAsync(path, lines, cancellationToken);
    }

    private static Candle Parse(string line)
    {
        var parts = line.Split(',');
        return new Candle(
            parts[0],
            Enum.Parse<Timeframe>(parts[1]),
            DateTimeOffset.Parse(parts[2], CultureInfo.InvariantCulture),
            decimal.Parse(parts[3], CultureInfo.InvariantCulture),
            decimal.Parse(parts[4], CultureInfo.InvariantCulture),
            decimal.Parse(parts[5], CultureInfo.InvariantCulture),
            decimal.Parse(parts[6], CultureInfo.InvariantCulture),
            decimal.Parse(parts[7], CultureInfo.InvariantCulture));
    }

    private static string Number(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}
