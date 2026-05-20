using TradingBot.Application;
using TradingBot.Domain;

namespace TradingBot.Infrastructure.Sessions;

public sealed class NewYorkSessionClock : ISessionClock
{
    public SessionName GetCurrentSession(DateTimeOffset timestamp)
    {
        var nyZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var ny = TimeZoneInfo.ConvertTime(timestamp, nyZone).TimeOfDay;

        if (ny >= new TimeSpan(8, 0, 0) && ny <= new TimeSpan(11, 0, 0))
        {
            return SessionName.LondonNewYorkOverlap;
        }

        if (ny >= new TimeSpan(2, 0, 0) && ny <= new TimeSpan(5, 0, 0))
        {
            return SessionName.London;
        }

        if (ny >= new TimeSpan(8, 30, 0) && ny <= new TimeSpan(11, 0, 0))
        {
            return SessionName.NewYork;
        }

        return SessionName.Closed;
    }
}
