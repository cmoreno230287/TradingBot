namespace TradingBot.Application;

/// <summary>Forex weekly closure in New York time plus explicitly declared broker closures.</summary>
public static class ForexDataCalendar
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    public static bool IsExpectedOpen(DateTimeOffset utc, IReadOnlyList<string> closures)
    {
        var local = TimeZoneInfo.ConvertTime(utc, NewYork);
        if (local.DayOfWeek == DayOfWeek.Saturday
            || local.DayOfWeek == DayOfWeek.Friday && local.Hour >= 17
            || local.DayOfWeek == DayOfWeek.Sunday && local.Hour < 17) return false;
        foreach (var closure in closures)
        {
            var parts = closure.Split('/');
            if (parts.Length == 2 && DateTimeOffset.TryParse(parts[0], out var start)
                && DateTimeOffset.TryParse(parts[1], out var end) && utc >= start && utc < end) return false;
        }
        return true;
    }

    public static bool HasUnexpectedHourlyGap(IReadOnlyList<TradingBot.Domain.Candle> candles, IReadOnlyList<string> closures)
    {
        for (var i = 1; i < candles.Count; i++)
        {
            if (candles[i].OpenedAt <= candles[i - 1].OpenedAt
                || (candles[i].OpenedAt - candles[i - 1].OpenedAt).Ticks % TimeSpan.TicksPerHour != 0) return true;
            for (var at = candles[i - 1].OpenedAt.AddHours(1); at < candles[i].OpenedAt; at = at.AddHours(1))
                if (IsExpectedOpen(at, closures)) return true;
        }
        return false;
    }
}
