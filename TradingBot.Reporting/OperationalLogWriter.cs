using System.Text.Json;
using TradingBot.Application;

namespace TradingBot.Reporting;

public sealed class OperationalLogWriter(TradingBotOptions options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task WriteAsync(
        string cycleId,
        string eventName,
        object payload,
        CancellationToken cancellationToken)
    {
        if (!options.OperationalMonitoring.Enabled)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(options.OperationalMonitoring.Directory);
            var path = Path.Combine(
                options.OperationalMonitoring.Directory,
                $"operations-{DateTimeOffset.UtcNow:yyyyMMdd}.jsonl");

            var record = new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                cycleId,
                eventName,
                broker = options.Broker,
                symbol = options.Symbol,
                payload
            };

            await File.AppendAllTextAsync(
                path,
                JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine,
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"Operational monitoring write failed: {exception.Message}");
        }
    }
}
