using TradingBot.Application;
using TradingBot.Domain;

namespace TradingBot.Infrastructure.Sessions;

public sealed class NewYorkSessionClock(TradingBotOptions options) : ISessionClock
{
    private readonly SessionWindow _london = ParseSessionWindow(options.LondonKillZoneNYTime, nameof(options.LondonKillZoneNYTime));
    private readonly SessionWindow _newYork = ParseSessionWindow(options.NewYorkKillZoneNYTime, nameof(options.NewYorkKillZoneNYTime));
    private readonly SessionWindow _overlap = ParseSessionWindow(options.LondonNewYorkOverlapNYTime, nameof(options.LondonNewYorkOverlapNYTime));

    public SessionName GetCurrentSession(DateTimeOffset timestamp)
    {
        var nyZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var ny = TimeZoneInfo.ConvertTime(timestamp, nyZone).TimeOfDay;

        if (_overlap.Contains(ny))
        {
            return SessionName.LondonNewYorkOverlap;
        }

        if (_london.Contains(ny))
        {
            return SessionName.London;
        }

        if (_newYork.Contains(ny))
        {
            return SessionName.NewYork;
        }

        return SessionName.Closed;
    }

    private static SessionWindow ParseSessionWindow(string value, string optionName)
    {
        var parts = value.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2
            || !TimeSpan.TryParse(parts[0], out var start)
            || !TimeSpan.TryParse(parts[1], out var end))
        {
            throw new InvalidOperationException($"{optionName} must use HH:mm-HH:mm format.");
        }

        return new SessionWindow(start, end);
    }

    private readonly record struct SessionWindow(TimeSpan Start, TimeSpan End)
    {
        public bool Contains(TimeSpan value) =>
            Start <= End
                ? value >= Start && value <= End
                : value >= Start || value <= End;
    }
}
