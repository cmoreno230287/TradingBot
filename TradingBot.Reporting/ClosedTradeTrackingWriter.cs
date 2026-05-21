using System.Globalization;
using System.Text;
using TradingBot.Application;

namespace TradingBot.Reporting;

public sealed class ClosedTradeTrackingWriter(TradingBotOptions options)
{
    private const string Header = "TradeId,Symbol,Session,Direction,EntryPrice,ClosePrice,StopLossPrice,TakeProfitPrice,RiskRewardRatio,OpenedAt,ClosedAt,Volume,Profit,Commission,Swap,NetProfit,MagicNumber,Comment";

    public async Task<int> AppendAsync(IReadOnlyList<ClosedTradeReport> trades, CancellationToken cancellationToken)
    {
        if (!options.TradeTracking.Enabled || trades.Count == 0)
        {
            return 0;
        }

        Directory.CreateDirectory(options.TradeTracking.Directory);
        var existingIds = LoadExistingTradeIds();
        var newTrades = trades
            .Where(trade => !existingIds.Contains(trade.TradeId, StringComparer.OrdinalIgnoreCase))
            .OrderBy(trade => trade.ClosedAt)
            .ToArray();
        if (newTrades.Length == 0)
        {
            return 0;
        }

        var written = 0;
        foreach (var trade in newTrades)
        {
            var path = ResolveWritableFile();
            if (!File.Exists(path))
            {
                await File.WriteAllTextAsync(path, Header + Environment.NewLine, cancellationToken);
            }

            await File.AppendAllTextAsync(path, FormatTrade(trade) + Environment.NewLine, cancellationToken);
            existingIds.Add(trade.TradeId);
            written++;
        }

        return written;
    }

    private HashSet<string> LoadExistingTradeIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(options.TradeTracking.Directory))
        {
            return ids;
        }

        foreach (var file in Directory.EnumerateFiles(options.TradeTracking.Directory, "TradesReported_*.csv"))
        {
            foreach (var line in File.ReadLines(file).Skip(1))
            {
                var id = ReadFirstCsvColumn(line);
                if (!string.IsNullOrWhiteSpace(id))
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }

    private string ResolveWritableFile()
    {
        var index = 1;
        while (true)
        {
            var path = Path.Combine(options.TradeTracking.Directory, $"TradesReported_{index}.csv");
            if (!File.Exists(path) || CountDataRows(path) < options.TradeTracking.MaxRowsPerFile)
            {
                return path;
            }

            index++;
        }
    }

    private static int CountDataRows(string path) =>
        Math.Max(0, File.ReadLines(path).Count() - 1);

    private static string FormatTrade(ClosedTradeReport trade)
    {
        return string.Join(',',
            Csv(trade.TradeId),
            Csv(trade.Symbol),
            Csv(trade.Session),
            Csv(trade.Direction),
            Number(trade.EntryPrice),
            Number(trade.ClosePrice),
            Number(trade.StopLossPrice),
            Number(trade.TakeProfitPrice),
            Number(trade.RiskRewardRatio),
            Csv(trade.OpenedAt.ToString("O")),
            Csv(trade.ClosedAt.ToString("O")),
            Number(trade.Volume),
            Number(trade.Profit),
            Number(trade.Commission),
            Number(trade.Swap),
            Number(trade.NetProfit),
            trade.MagicNumber.ToString(CultureInfo.InvariantCulture),
            Csv(trade.Comment));
    }

    private static string Number(decimal value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static string Csv(string value) =>
        $"\"{value.Replace("\"", "\"\"")}\"";

    private static string ReadFirstCsvColumn(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return "";
        }

        if (line[0] != '"')
        {
            var commaIndex = line.IndexOf(',');
            return commaIndex >= 0 ? line[..commaIndex] : line;
        }

        var builder = new StringBuilder();
        for (var i = 1; i < line.Length; i++)
        {
            if (line[i] == '"' && i + 1 < line.Length && line[i + 1] == '"')
            {
                builder.Append('"');
                i++;
                continue;
            }

            if (line[i] == '"')
            {
                break;
            }

            builder.Append(line[i]);
        }

        return builder.ToString();
    }
}
