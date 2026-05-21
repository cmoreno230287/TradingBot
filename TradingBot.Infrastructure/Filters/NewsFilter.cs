using TradingBot.Application;

namespace TradingBot.Infrastructure.Filters;

public sealed class NewsFilter(TradingBotOptions options) : INewsFilter
{
    public bool IsBlocked(DateTimeOffset timestamp, string symbol, out string reason)
    {
        reason = "Clear";

        if (!options.UseNewsFilter)
        {
            return false;
        }

        foreach (var window in options.NewsBlackoutWindowsUtc)
        {
            if (!TryParseWindow(window, out var from, out var to))
            {
                continue;
            }

            var blockedFrom = from.AddMinutes(-options.MinutesBeforeHighImpactNews);
            var blockedTo = to.AddMinutes(options.MinutesAfterHighImpactNews);
            if (timestamp >= blockedFrom && timestamp <= blockedTo)
            {
                reason = $"Configured news blackout window is active: {from:O}/{to:O}.";
                return true;
            }
        }

        return false;
    }

    private static bool TryParseWindow(string raw, out DateTimeOffset from, out DateTimeOffset to)
    {
        from = default;
        to = default;
        var parts = raw.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2
            && DateTimeOffset.TryParse(parts[0], out from)
            && DateTimeOffset.TryParse(parts[1], out to)
            && to >= from;
    }
}
