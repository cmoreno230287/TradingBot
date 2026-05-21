using System.Globalization;
using System.Text;
using TradingBot.Application;

namespace TradingBot.Reporting;

public sealed class SignalTrackingWriter(TradingBotOptions options)
{
    private const string Header = "SignalId,Symbol,Session,Direction,EntryPrice,StopLossPrice,TakeProfitPrice,RiskRewardRatio,CreatedAt,SetupReason,ScreenshotH1,ScreenshotM5,ScreenshotM1";

    public async Task<int> AppendAsync(IReadOnlyList<SignalReport> signals, CancellationToken cancellationToken)
    {
        if (!options.SignalTracking.Enabled || signals.Count == 0)
        {
            return 0;
        }

        Directory.CreateDirectory(options.SignalTracking.Directory);
        var existingIds = LoadExistingIds();
        var newSignals = signals
            .Where(signal => !existingIds.Contains(signal.SignalId, StringComparer.OrdinalIgnoreCase))
            .OrderBy(signal => signal.CreatedAt)
            .ToArray();

        var written = 0;
        foreach (var signal in newSignals)
        {
            var path = ResolveWritableFile();
            if (!File.Exists(path))
            {
                await File.WriteAllTextAsync(path, Header + Environment.NewLine, cancellationToken);
            }

            await File.AppendAllTextAsync(path, FormatSignal(signal) + Environment.NewLine, cancellationToken);
            existingIds.Add(signal.SignalId);
            written++;
        }

        return written;
    }

    private HashSet<string> LoadExistingIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(options.SignalTracking.Directory))
        {
            return ids;
        }

        foreach (var file in Directory.EnumerateFiles(options.SignalTracking.Directory, "SignalsReported_*.csv"))
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
            var path = Path.Combine(options.SignalTracking.Directory, $"SignalsReported_{index}.csv");
            if (!File.Exists(path) || Math.Max(0, File.ReadLines(path).Count() - 1) < options.SignalTracking.MaxRowsPerFile)
            {
                return path;
            }

            index++;
        }
    }

    private static string FormatSignal(SignalReport signal) =>
        string.Join(',',
            Csv(signal.SignalId),
            Csv(signal.Symbol),
            Csv(signal.Session),
            Csv(signal.Direction),
            Number(signal.EntryPrice),
            Number(signal.StopLossPrice),
            Number(signal.TakeProfitPrice),
            Number(signal.RiskRewardRatio),
            Csv(signal.CreatedAt.ToString("O")),
            Csv(signal.SetupReason),
            Csv(signal.ScreenshotH1),
            Csv(signal.ScreenshotM5),
            Csv(signal.ScreenshotM1));

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
