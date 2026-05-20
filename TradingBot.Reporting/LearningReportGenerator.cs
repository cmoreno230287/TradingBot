using System.Globalization;
using System.Text;
using TradingBot.Application;
using TradingBot.Domain;

namespace TradingBot.Reporting;

public sealed class LearningReportGenerator(TradingBotOptions options)
{
    public async Task<LearningReportResult> WriteAsync(
        TradeSignal signal,
        RiskDecision riskDecision,
        IReadOnlyList<Candle> candles,
        DateTimeOffset analyzedAt,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.ReportsDirectory);

        var stamp = analyzedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var baseName = $"backtest-learning-{signal.Symbol}-{stamp}";
        var imagePath = Path.Combine(options.ReportsDirectory, $"{baseName}.svg");
        var pdfPath = Path.Combine(options.ReportsDirectory, $"{baseName}.pdf");

        await File.WriteAllTextAsync(imagePath, BuildSvg(signal, candles, analyzedAt), cancellationToken);
        await File.WriteAllBytesAsync(pdfPath, BuildPdf(signal, riskDecision, analyzedAt, imagePath), cancellationToken);

        return new LearningReportResult(imagePath, pdfPath);
    }

    private static string BuildSvg(TradeSignal signal, IReadOnlyList<Candle> candles, DateTimeOffset analyzedAt)
    {
        const int width = 1400;
        const int height = 850;
        const int left = 80;
        const int right = 1280;
        const int top = 70;
        const int bottom = 720;

        if (candles.Count == 0)
        {
            return EmptySvg(width, height, "No candles available for chart rendering.");
        }

        var high = candles.Max(c => c.High);
        var low = candles.Min(c => c.Low);
        high = Math.Max(high, Math.Max(signal.TakeProfit, Math.Max(signal.EntryPrice, signal.StopLoss)));
        low = Math.Min(low, Math.Min(signal.TakeProfit, Math.Min(signal.EntryPrice, signal.StopLoss)));
        var padding = Math.Max((high - low) * 0.08m, 0.0005m);
        high += padding;
        low -= padding;

        decimal PriceToY(decimal price) => bottom - ((price - low) / (high - low) * (bottom - top));
        decimal IndexToX(int index) => left + index * ((right - left) / Math.Max(candles.Count - 1m, 1m));
        static string N(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
        static string P(decimal value) => value.ToString("0.#####", CultureInfo.InvariantCulture);

        var svg = new StringBuilder();
        svg.AppendLine($"""<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}">""");
        svg.AppendLine("""<rect width="100%" height="100%" fill="#0f131a"/>""");
        svg.AppendLine("""<rect x="55" y="45" width="1265" height="710" fill="#131923" stroke="#2a3442"/>""");
        svg.AppendLine("""<text x="80" y="35" fill="#e6edf3" font-family="Segoe UI, Arial" font-size="24" font-weight="700">EURUSD Smart Money Valid Setup</text>""");
        svg.AppendLine($"""<text x="1030" y="35" fill="#8b949e" font-family="Segoe UI, Arial" font-size="15">{analyzedAt:yyyy-MM-dd HH:mm zzz}</text>""");

        for (var i = 0; i <= 6; i++)
        {
            var y = top + i * ((bottom - top) / 6m);
            var price = high - i * ((high - low) / 6m);
            svg.AppendLine($"""<line x1="{left}" y1="{N(y)}" x2="{right}" y2="{N(y)}" stroke="#243041" stroke-width="1"/>""");
            svg.AppendLine($"""<text x="1290" y="{N(y + 5)}" fill="#8b949e" font-family="Consolas, monospace" font-size="13">{P(price)}</text>""");
        }

        if (signal.FairValueGap is not null)
        {
            var fvgTop = PriceToY(signal.FairValueGap.UpperPrice);
            var fvgBottom = PriceToY(signal.FairValueGap.LowerPrice);
            svg.AppendLine($"""<rect x="{left}" y="{N(fvgTop)}" width="{right - left}" height="{N(fvgBottom - fvgTop)}" fill="#f2cc60" opacity="0.20" stroke="#f2cc60" stroke-dasharray="8 6"/>""");
            svg.AppendLine($"""<text x="95" y="{N(fvgTop - 8)}" fill="#f2cc60" font-family="Segoe UI, Arial" font-size="15">Bullish Fair Value Gap</text>""");
        }

        var candleWidth = Math.Max(5m, (right - left) / Math.Max(candles.Count, 1m) * 0.55m);
        for (var i = 0; i < candles.Count; i++)
        {
            var candle = candles[i];
            var x = IndexToX(i);
            var openY = PriceToY(candle.Open);
            var closeY = PriceToY(candle.Close);
            var highY = PriceToY(candle.High);
            var lowY = PriceToY(candle.Low);
            var color = candle.IsBullish ? "#26a69a" : "#ef5350";
            var bodyTop = Math.Min(openY, closeY);
            var bodyHeight = Math.Max(Math.Abs(openY - closeY), 2m);

            svg.AppendLine($"""<line x1="{N(x)}" y1="{N(highY)}" x2="{N(x)}" y2="{N(lowY)}" stroke="{color}" stroke-width="2"/>""");
            svg.AppendLine($"""<rect x="{N(x - candleWidth / 2)}" y="{N(bodyTop)}" width="{N(candleWidth)}" height="{N(bodyHeight)}" fill="{color}" rx="1"/>""");
        }

        AddLevel(svg, signal.EntryPrice, "#f2cc60", "ENTRY", left, right, PriceToY, P, N);
        AddLevel(svg, signal.StopLoss, "#ff6b6b", "SL", left, right, PriceToY, P, N);
        AddLevel(svg, signal.TakeProfit, "#2ecc71", "TP", left, right, PriceToY, P, N);

        svg.AppendLine($"""<line x1="{left}" y1="{N(PriceToY(signal.StopLoss))}" x2="{left}" y2="{N(PriceToY(signal.TakeProfit))}" stroke="#58a6ff" stroke-width="3" opacity="0.8"/>""");
        svg.AppendLine($"""<text x="92" y="{N(PriceToY(signal.TakeProfit) - 12)}" fill="#58a6ff" font-family="Segoe UI, Arial" font-size="15">Risk / Reward {signal.RiskReward:0.##}R</text>""");
        svg.AppendLine("""<text x="80" y="790" fill="#e6edf3" font-family="Segoe UI, Arial" font-size="18">Setup: liquidity sweep + MSS/BOS + FVG midpoint entry</text>""");
        svg.AppendLine("""<text x="80" y="820" fill="#8b949e" font-family="Segoe UI, Arial" font-size="14">Educational chart generated from cTrader historical candles by TradingBot.</text>""");
        svg.AppendLine("</svg>");
        return svg.ToString();
    }

    private static void AddLevel(
        StringBuilder svg,
        decimal price,
        string color,
        string label,
        int left,
        int right,
        Func<decimal, decimal> priceToY,
        Func<decimal, string> priceFormat,
        Func<decimal, string> numberFormat)
    {
        var y = priceToY(price);
        svg.AppendLine($"""<line x1="{left}" y1="{numberFormat(y)}" x2="{right}" y2="{numberFormat(y)}" stroke="{color}" stroke-width="2"/>""");
        svg.AppendLine($"""<rect x="{right - 8}" y="{numberFormat(y - 15)}" width="112" height="30" fill="{color}" rx="4"/>""");
        svg.AppendLine($"""<text x="{right}" y="{numberFormat(y + 5)}" fill="#0f131a" font-family="Consolas, monospace" font-size="15" font-weight="700">{label} {priceFormat(price)}</text>""");
    }

    private static string EmptySvg(int width, int height, string message) =>
        $"""<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}"><rect width="100%" height="100%" fill="#0f131a"/><text x="80" y="120" fill="#e6edf3" font-family="Segoe UI, Arial" font-size="24">{EscapeXml(message)}</text></svg>""";

    private static byte[] BuildPdf(TradeSignal signal, RiskDecision riskDecision, DateTimeOffset analyzedAt, string imagePath)
    {
        var lines = new[]
        {
            "TradingBot Backtest Learning Report",
            $"Symbol: {signal.Symbol}",
            $"Analyzed At: {analyzedAt:yyyy-MM-dd HH:mm:ss zzz}",
            $"Session: {signal.Session}",
            $"Setup Status: VALID",
            $"Reason: {signal.SetupReason}",
            $"Direction: {signal.Direction.ToString().ToUpperInvariant()}",
            $"Entry: {signal.EntryPrice}",
            $"Stop Loss: {signal.StopLoss}",
            $"Take Profit: {signal.TakeProfit}",
            $"Risk Reward: {signal.RiskReward}",
            $"Risk Status: {(riskDecision.IsAllowed ? "ACCEPTED" : "REJECTED")}",
            $"Lot Size: {riskDecision.PositionSize}",
            $"Risk Reason: {riskDecision.Reason}",
            $"Chart Image: {Path.GetFileName(imagePath)}",
            "",
            "Learning Notes:",
            "1. The setup is valid only after the strategy detects aligned higher timeframe bias.",
            "2. The entry is generated from the detected fair value gap.",
            "3. The stop loss is placed beyond the swept liquidity with the configured buffer.",
            "4. The take profit is calculated from the configured preferred risk/reward.",
            "5. This report is educational and was generated from historical cTrader candles."
        };

        return SimplePdf.Write(lines);
    }

    private static string EscapeXml(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
}

public sealed record LearningReportResult(string ImagePath, string PdfPath);
