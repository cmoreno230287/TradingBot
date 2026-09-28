namespace TradingBot.Application;

public sealed class NewsCalendarOptions
{
    public bool Enabled { get; set; }
    public string FeedUrl { get; set; } = "";
    public int RefreshSeconds { get; set; } = 300;
    public int MaximumAgeMinutes { get; set; } = 60;
}

public sealed record NewsCalendarSnapshot(DateTimeOffset GeneratedAtUtc, DateTimeOffset CoverageFromUtc,
    DateTimeOffset CoverageUntilUtc, string[] BlackoutWindowsUtc, string Source);

public static class NewsCalendarState
{
    public static NewsCalendarSnapshot? Current(TradingBotOptions options, DateTimeOffset now)
    {
        if (!options.NewsCalendar.Enabled)
            return options.NewsCoverageFromUtc is { } from && options.NewsCoverageUntilUtc is { } until
                ? new(now, from, until, options.NewsBlackoutWindowsUtc, "configured") : null;
        var snapshot = options.LiveNewsCalendar;
        return snapshot is not null && snapshot.GeneratedAtUtc <= now.AddMinutes(1)
            && now - snapshot.GeneratedAtUtc <= TimeSpan.FromMinutes(options.NewsCalendar.MaximumAgeMinutes)
            ? snapshot : null;
    }
}
