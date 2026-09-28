using TradingBot.Domain;
namespace TradingBot.Strategies;

/// <summary>Shared, closed-bar SMC structure primitives.</summary>
public static class SmcDetectors
{
    public static IReadOnlyList<(int Index, decimal Price)> ConfirmedSwings(IReadOnlyList<Candle> candles, int strength, bool highs)
    {
        if (strength < 1 || candles.Count < strength * 2 + 1) return [];
        var result = new List<(int, decimal)>();
        for (var i = strength; i < candles.Count - strength; i++)
        {
            var price = highs ? candles[i].High : candles[i].Low;
            var confirmed = true;
            for (var wing = 1; wing <= strength; wing++)
            {
                if (highs ? price <= candles[i - wing].High || price <= candles[i + wing].High
                    : price >= candles[i - wing].Low || price >= candles[i + wing].Low) { confirmed = false; break; }
            }
            if (confirmed) result.Add((i, price));
        }
        return result;
    }
}
