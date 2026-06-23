using System.Globalization;
using System.Text;
using TradingBot.Application;

namespace TradingBot.Reporting;

public sealed class RecentValidSetupCsvWriter(TradingBotOptions options)
{
    public const int MaxRows = 2000;

    public async Task<string> WriteAsync(IEnumerable<RecentValidSetupRow> setups, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(options.ReportsDirectory, "recent-setups");
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"recent-valid-setups-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.csv");
        var builder = new StringBuilder();
        builder.AppendLine("StrategyId,StrategyName,Symbol,SetupTimeUtc,SetupTimeLocal,Session,Direction,IsIntoKillZone,EntryPrice,StopLoss,TakeProfit,RiskReward,Reason,HistoricalSource");

        foreach (var setup in setups.Take(MaxRows))
        {
            builder.AppendLine(string.Join(',',
                Csv(setup.StrategyId),
                Csv(setup.StrategyName),
                Csv(setup.Symbol),
                Csv(setup.SetupTimeUtc.ToString("O")),
                Csv(setup.SetupTimeLocal.ToString("O")),
                Csv(setup.Session),
                Csv(setup.Direction),
                Csv(setup.IsIntoKillZone ? "YES" : "NO"),
                Number(setup.EntryPrice),
                Number(setup.StopLoss),
                Number(setup.TakeProfit),
                Number(setup.RiskReward),
                Csv(setup.Reason),
                Csv(setup.HistoricalSource)));
        }

        await File.WriteAllTextAsync(path, builder.ToString(), cancellationToken);
        return Path.GetFullPath(path);
    }

    private static string Number(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}

public sealed record RecentValidSetupRow(
    string StrategyId,
    string StrategyName,
    string Symbol,
    DateTimeOffset SetupTimeUtc,
    DateTimeOffset SetupTimeLocal,
    string Session,
    string Direction,
    bool IsIntoKillZone,
    decimal EntryPrice,
    decimal StopLoss,
    decimal TakeProfit,
    decimal RiskReward,
    string Reason,
    string HistoricalSource);
