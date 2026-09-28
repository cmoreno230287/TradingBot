using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TradingBot.Application;

public static class LiveStrategyIdentity
{
    public static string Version(TradingBotOptions options)
    {
        var parameters = JsonSerializer.Serialize(new { strategy = options.ActiveStrategy, options.FtmoSmc,
            options.FtmoPullback, options.AllowedSessions, options.FtmoProtection.RiskPercent,
            options.FtmoProtection.CommissionPerLot, options.FtmoProtection.SlippageReservePips,
            options.FtmoProtection.EntryAnalysisIntervalSeconds, options.MaxSpreadPips,
            options.UseNewsFilter, options.MinutesBeforeHighImpactNews, options.MinutesAfterHighImpactNews,
            options.LondonKillZoneNYTime, options.NewYorkKillZoneNYTime, options.LondonNewYorkOverlapNYTime,
            options.ForexMarketSessions.TradingDays });
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(parameters)))[..12];
        return $"{options.ActiveStrategy.Id}|{options.ActiveStrategy.Engine}|live-v2|{hash}";
    }
}
