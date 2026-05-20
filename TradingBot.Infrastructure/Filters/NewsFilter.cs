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

        var events = Array.Empty<DateTimeOffset>();
        var blocked = events.Any(e => timestamp >= e.AddMinutes(-options.MinutesBeforeHighImpactNews) && timestamp <= e.AddMinutes(options.MinutesAfterHighImpactNews));
        if (!blocked)
        {
            return false;
        }

        reason = "High-impact news window is active.";
        return true;
    }
}
