using System.Globalization;
using System.Text;
using TradingBot.Application;
using TradingBot.Domain;

namespace TradingBot.Reporting;

public sealed class CsvJournalWriter(TradingBotOptions options) : IJournalWriter
{
    public async Task<string> WriteTradesAsync(IEnumerable<TradeJournalEntry> trades, string reportName, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.ReportsDirectory);
        var path = Path.Combine(options.ReportsDirectory, $"{reportName}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.csv");

        var builder = new StringBuilder();
        builder.AppendLine("StrategyId,Symbol,Session,Direction,EntryPrice,StopLossPrice,TakeProfitPrice,RiskRewardRatio,OpenedAt,ClosedAt,OutcomeStatus,TradeId,BrokerOrderId,LotSize,RiskPercent,ProfitLossAmount,Spread,Slippage,LiquiditySwept,LiquidityTarget,EntryMode,DailyBias,H1Bias,NewsFilterStatus,CreatedAt");

        foreach (var trade in trades)
        {
            builder.AppendLine(string.Join(',',
                Csv(trade.StrategyId),
                Csv(trade.Symbol),
                Csv(trade.Session.ToString()),
                Csv(trade.Direction.ToString().ToUpperInvariant()),
                Number(trade.EntryPrice),
                Number(trade.StopLossPrice),
                Number(trade.TakeProfitPrice),
                Number(trade.RiskRewardRatio),
                Csv(trade.OpenedAt.ToString("O")),
                Csv(trade.ClosedAt?.ToString("O") ?? ""),
                Csv(trade.OutcomeStatus.ToString()),
                Csv(trade.TradeId),
                Csv(trade.BrokerOrderId ?? ""),
                Number(trade.LotSize),
                Number(trade.RiskPercent),
                Number(trade.ProfitLossAmount),
                Number(trade.Spread),
                Number(trade.Slippage),
                Csv(trade.LiquiditySwept),
                Csv(trade.LiquidityTarget),
                Csv(trade.EntryMode),
                Csv(trade.DailyBias),
                Csv(trade.H1Bias),
                Csv(trade.NewsFilterStatus),
                Csv(trade.CreatedAt.ToString("O"))));
        }

        await File.WriteAllTextAsync(path, builder.ToString(), cancellationToken);
        return Path.GetFullPath(path);
    }

    private static string Number(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
