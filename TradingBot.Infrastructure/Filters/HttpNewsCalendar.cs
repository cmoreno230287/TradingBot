using System.Net.Http.Json;
using TradingBot.Application;

namespace TradingBot.Infrastructure.Filters;

/// <summary>Explicit coverage contract; a missing event list is not an empty, reviewed calendar.</summary>
public sealed class HttpNewsCalendar(TradingBotOptions options, HttpClient client)
{
    private DateTimeOffset nextRefresh;
    public async Task RefreshAsync(DateTimeOffset now, CancellationToken token)
    {
        if (!options.NewsCalendar.Enabled || now < nextRefresh) return;
        // Failed refreshes retry on the next maintenance cycle; previous data retains its original expiry.
        var snapshot = await client.GetFromJsonAsync<NewsCalendarSnapshot>(options.NewsCalendar.FeedUrl, token)
            ?? throw new InvalidOperationException("News calendar returned no coverage.");
        Validate(snapshot, now, options.NewsCalendar.MaximumAgeMinutes);
        if (options.LiveNewsCalendar is { } previous && snapshot.GeneratedAtUtc < previous.GeneratedAtUtc)
            throw new InvalidOperationException("News calendar generation time moved backwards.");
        options.LiveNewsCalendar = snapshot;
        nextRefresh = now.AddSeconds(options.NewsCalendar.RefreshSeconds);
    }

    public static void Validate(NewsCalendarSnapshot snapshot, DateTimeOffset now, int maximumAgeMinutes)
    {
        if (string.IsNullOrWhiteSpace(snapshot.Source) || snapshot.BlackoutWindowsUtc is null
            || snapshot.GeneratedAtUtc > now.AddMinutes(1) || now - snapshot.GeneratedAtUtc > TimeSpan.FromMinutes(maximumAgeMinutes)
            || snapshot.CoverageFromUtc > now || snapshot.CoverageUntilUtc <= now
            || snapshot.CoverageUntilUtc <= snapshot.CoverageFromUtc)
            throw new InvalidOperationException("News calendar is stale, incomplete, or outside its coverage interval.");
        foreach (var window in snapshot.BlackoutWindowsUtc)
        {
            var parts = window.Split('/');
            if (parts.Length != 2 || !DateTimeOffset.TryParse(parts[0], out var from)
                || !DateTimeOffset.TryParse(parts[1], out var to) || to < from)
                throw new InvalidOperationException("News calendar contains an invalid blackout interval.");
        }
    }
}
